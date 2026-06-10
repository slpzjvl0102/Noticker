using Noticker.Data;
using Noticker.Infrastructure;
using Noticker.Models;
using Noticker.Windows;

namespace Noticker.Sync;

// 1.5-방향 pull 파이프라인 — App의 1분 DispatcherTimer(UI 스레드)에서 구동.
// 판정은 PullDecision(순수), 변환은 NotionBlockConverter(순수) + RtfComposer(UI 스레드).
//
// 커서 규칙: ascending 정렬 + 첫 보류(dirty defer/적용 상한) 지점에서 중단 —
// 보류 페이지가 커서 뒤에 남아 다음 사이클에 다시 온다. terminal 결과
// (미연결/dedupe/에코 ack/pull_disabled/적용 완료)만 커서를 전진시킨다.
public class PullService
{
    private readonly StickerRepository _repo;
    private readonly SettingsRepository _settingsRepo;
    private readonly NotionClient _client;
    private readonly AppSettings _settings;
    private readonly Func<string, StickerWindow?> _windowLookup;
    private readonly Action<string, string> _notify;   // (title, message)

    private bool _running;                              // 1분 tick 재진입 가드
    private const int MaxAppliesPerCycle = 20;

    public PullService(
        StickerRepository repo,
        SettingsRepository settingsRepo,
        NotionClient client,
        AppSettings settings,
        Func<string, StickerWindow?> windowLookup,
        Action<string, string> notify)
    {
        _repo = repo;
        _settingsRepo = settingsRepo;
        _client = client;
        _settings = settings;
        _windowLookup = windowLookup;
        _notify = notify;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        if (_running) return;
        if (_settings.IsSyncPaused || !_settings.IsConfigured) return;
        var botId = _settings.NotionBotUserId;
        if (botId is null) return;                      // bot id 캐시 전 — 보호/pull 비활성

        _running = true;
        try
        {
            var cursor = _settingsRepo.Get("notion_last_poll");
            if (cursor is null)
            {
                // 첫 가동: now-5분으로 초기화 — 전체 DB를 쏟아붓지 않으면서 로컬 시계가
                // 서버보다 빠른 경우의 누락 창을 마진으로 흡수 (중복 수신은 쌍 dedupe가 무해화)
                _settingsRepo.Set("notion_last_poll",
                    DateTime.UtcNow.AddMinutes(-5).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
                return;
            }

            // 오름차순을 클라이언트에서 강제 — 서버 sorts 파라미터가 실제로는 순서를
            // 보장하지 않는 것이 로그로 관찰됨 (16:08 → 16:09 → 16:08 순서로 반환).
            // 커서/defer 로직은 오름차순이 전제: 어긋나면 defer된 옛 페이지를 이미 전진한
            // 커서가 지나쳐 그 수정을 영영 못 받는다
            var pages = (await _client.QueryUpdatedPagesAsync(cursor, ct))
                .Select(t => new PageMeta(t.PageId, t.LastEditedTime, t.LastEditedById, t.Title))
                .OrderBy(p => p.LastEditedTime, StringComparer.Ordinal)
                .ToList();
            if (pages.Count == 0) return;
            SyncLog.Write($"pull: cursor={cursor} pages={pages.Count}");

            // pageId → 창이 들고 있는 live Sticker 인스턴스.
            // GetAll 사본을 갱신하면 안 됨 — SavePosition/SaveSize가 창의 인스턴스로
            // 전체 행을 다시 쓰므로 사본 갱신은 다음 드래그에 되돌려진다
            var byPageId = new Dictionary<string, (Sticker S, StickerWindow W)>();
            foreach (var row in _repo.GetAll())
            {
                if (row.NotionPageId is null) continue;
                var w = _windowLookup(row.Id);
                if (w is not null) byPageId[row.NotionPageId] = (w.Sticker, w);
            }

            int applied = 0;
            string maxProcessed = cursor;
            // 일시적 dirty(타이핑/debounce/pending)를 만나면 커서만 동결하고 루프는 계속 —
            // 한 스티커가 전체 pull을 막는 head-of-line blocking 방지 (검증 리뷰 F4).
            // 이미 적용된 뒤쪽 페이지는 다음 사이클에 (쌍 dedupe로) terminal no-op
            bool cursorFrozen = false;
            void Advance(string time)
            {
                if (!cursorFrozen)
                    maxProcessed = PullDecision.NextCursor(maxProcessed, [time]);
            }

            foreach (var p in pages)
            {
                if (ct.IsCancellationRequested) break;

                if (!byPageId.TryGetValue(p.PageId, out var hit))
                {
                    // 미연결 페이지 — terminal (가져오기는 ImportWindow 경유로만)
                    Advance(p.LastEditedTime);
                    continue;
                }

                var (s, win) = hit;
                var action = PullDecision.Decide(p, botId, s.NotionLastEdit, s.NotionLastEditBy,
                    s.SyncState,
                    bodyEmpty: string.IsNullOrEmpty(s.Title) && string.IsNullOrEmpty(s.Body),
                    debouncePending: win.IsSyncPending,
                    hasKeyboardFocus: win.IsKeyboardFocusWithin,
                    pullDisabled: s.PullDisabled);
                SyncLog.Write($"pull: page={p.PageId[..8]} time={p.LastEditedTime} by={(p.LastEditedById == botId ? "bot" : "human")} state={s.SyncState} → {action}");

                switch (action)
                {
                    case PullAction.AckOnly:
                        Ack(s, p);
                        Advance(p.LastEditedTime);
                        break;

                    case PullAction.Skip:
                        // terminal: dedupe / pull_disabled / 영구 dirty(conflict·failed —
                        //   conflict는 ack해서 다음 push가 재충돌 없이 이기고, failed는 사용자가
                        //   수정해 pending으로 돌아올 때까지 보존. defer로 두면 영원히 안 풀리는
                        //   상태가 앱 전체 커서를 정지시킴 — 검증 리뷰 F2/F1)
                        // defer: 일시적 dirty(pending/debounce/focus) — 커서 동결, 다음 사이클 재시도
                        bool pairEqual = p.LastEditedTime == s.NotionLastEdit &&
                                         p.LastEditedById == s.NotionLastEditBy;
                        if (s.SyncState == "conflict" && !pairEqual)
                            Ack(s, p);
                        bool terminal = s.PullDisabled || pairEqual ||
                            s.SyncState == "conflict" || s.SyncState == "failed";
                        if (terminal)
                            Advance(p.LastEditedTime);
                        else
                            cursorFrozen = true;
                        break;

                    case PullAction.Apply:
                        if (applied >= MaxAppliesPerCycle)
                        {
                            // 적용 상한 — 추가 blocks GET 없이 종료, 커서가 진행을 보장
                            cursorFrozen = true;
                            goto DonePolling;
                        }

                        await Task.Delay(350, ct);      // 오프라인 복귀 burst의 rate limit 페이싱
                        var result = await _client.GetPageBlocksAsync(p.PageId, ct);
                        if (result is null)
                        {
                            // 404 — push의 기존 재생성 정책에 맡김
                            Advance(p.LastEditedTime);
                            break;
                        }

                        // 가드 재검사 — 위 Decide와 여기 사이의 await 동안 메시지 펌프가
                        // 사용자 입력을 처리했을 수 있음. 스테일 가드로 적용하면 그 사이의
                        // 키 입력이 클로버됨 (검증 리뷰 critical F1)
                        if (win.IsKeyboardFocusWithin || win.IsSyncPending ||
                            s.SyncState is "pending" or "conflict")
                        {
                            cursorFrozen = true;
                            continue;
                        }

                        var (supported, _) = NotionBlockConverter.CheckVocabulary(result.Value.Blocks);
                        if (!supported || result.Value.HasMore)
                        {
                            // 범위 밖 또는 100블록 초과(잘린 본문을 push하면 Notion 쪽
                            // 초과 블록이 파괴됨) — pull 영구 중단 + ack
                            s.PullDisabled = true;
                            _repo.SetPullDisabled(s.Id, true);
                            Ack(s, p);
                            Advance(p.LastEditedTime);
                            break;
                        }

                        var lines = NotionBlockConverter.ToLines(result.Value.Blocks);
                        var plain = NotionBlockConverter.ToPlainText(lines);
                        var rtf = RtfComposer.Compose(lines, s.FontFamily);
                        // 쌍을 먼저 갱신 — ApplyPulledContent의 repo.Update가 함께 영속
                        s.NotionLastEdit = p.LastEditedTime;
                        s.NotionLastEditBy = p.LastEditedById;
                        win.ApplyPulledContent(p.Title, plain, rtf);
                        _notify(p.Title, "Notion에서 갱신됨");
                        applied++;
                        Advance(p.LastEditedTime);
                        break;
                }
            }
            DonePolling:

            if (maxProcessed != cursor)
                _settingsRepo.Set("notion_last_poll", maxProcessed);
        }
        catch (OperationCanceledException) { }
        catch (NotionUnauthorizedException)
        {
            // push 경로와 동일한 처우 — 401은 조용히 매분 재시도하면 안 됨
            _settings.IsSyncPaused = true;
            SyncLog.Write("pull: 401 — sync paused");
        }
        catch (Exception ex)
        {
            // 오프라인 등 — 다음 사이클 재시도. 단, 어디서 죽었는지는 남긴다
            SyncLog.Write($"pull: EXCEPTION {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
        }
        finally
        {
            _running = false;
        }
    }

    private void Ack(Sticker s, PageMeta p)
    {
        s.NotionLastEdit = p.LastEditedTime;
        s.NotionLastEditBy = p.LastEditedById;
        _repo.UpdateNotionLastEdit(s.Id, p.LastEditedTime, p.LastEditedById);
    }
}
