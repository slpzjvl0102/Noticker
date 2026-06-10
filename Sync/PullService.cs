using Noticker.Data;
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
                // 첫 가동: now로 초기화 — 전체 DB를 쏟아붓지 않고 이후 수정분부터 받는다
                _settingsRepo.Set("notion_last_poll",
                    DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
                return;
            }

            var pages = (await _client.QueryUpdatedPagesAsync(cursor, ct))
                .Select(t => new PageMeta(t.PageId, t.LastEditedTime, t.LastEditedById, t.Title))
                .ToList();
            if (pages.Count == 0) return;

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
            bool deferred = false;

            foreach (var p in pages)
            {
                if (ct.IsCancellationRequested || deferred) break;

                if (!byPageId.TryGetValue(p.PageId, out var hit))
                {
                    // 미연결 페이지 — terminal (가져오기는 ImportWindow 경유로만)
                    maxProcessed = PullDecision.NextCursor(maxProcessed, [p.LastEditedTime]);
                    continue;
                }

                var (s, win) = hit;
                var action = PullDecision.Decide(p, botId, s.NotionLastEdit, s.NotionLastEditBy,
                    s.SyncState,
                    bodyEmpty: string.IsNullOrEmpty(s.Title) && string.IsNullOrEmpty(s.Body),
                    debouncePending: win.IsSyncPending,
                    hasKeyboardFocus: win.IsKeyboardFocusWithin,
                    pullDisabled: s.PullDisabled);

                switch (action)
                {
                    case PullAction.AckOnly:
                        Ack(s, p);
                        maxProcessed = PullDecision.NextCursor(maxProcessed, [p.LastEditedTime]);
                        break;

                    case PullAction.Skip:
                        // dedupe/pull_disabled는 terminal, dirty(pending/conflict/debounce/focus)는 defer
                        bool terminal = s.PullDisabled ||
                            (p.LastEditedTime == s.NotionLastEdit && p.LastEditedById == s.NotionLastEditBy);
                        if (terminal)
                            maxProcessed = PullDecision.NextCursor(maxProcessed, [p.LastEditedTime]);
                        else
                            deferred = true;            // 커서 보류 — 다음 사이클 재시도
                        break;

                    case PullAction.Apply:
                        if (applied >= MaxAppliesPerCycle) { deferred = true; break; }

                        await Task.Delay(350, ct);      // 오프라인 복귀 burst의 rate limit 페이싱
                        var blocks = await _client.GetPageBlocksAsync(p.PageId, ct);
                        if (blocks is null)
                        {
                            // 404 — push의 기존 재생성 정책에 맡김
                            maxProcessed = PullDecision.NextCursor(maxProcessed, [p.LastEditedTime]);
                            break;
                        }

                        var (supported, _) = NotionBlockConverter.CheckVocabulary(blocks.Value);
                        if (!supported)
                        {
                            // 범위 밖 — pull 영구 중단 + ack (같은 페이지 재검사 루프 방지)
                            s.PullDisabled = true;
                            _repo.SetPullDisabled(s.Id, true);
                            Ack(s, p);
                            maxProcessed = PullDecision.NextCursor(maxProcessed, [p.LastEditedTime]);
                            break;
                        }

                        var lines = NotionBlockConverter.ToLines(blocks.Value);
                        var plain = NotionBlockConverter.ToPlainText(lines);
                        var rtf = RtfComposer.Compose(lines);
                        // 쌍을 먼저 갱신 — ApplyPulledContent의 repo.Update가 함께 영속
                        s.NotionLastEdit = p.LastEditedTime;
                        s.NotionLastEditBy = p.LastEditedById;
                        win.ApplyPulledContent(p.Title, plain, rtf);
                        _notify(p.Title, "Notion에서 갱신됨");
                        applied++;
                        maxProcessed = PullDecision.NextCursor(maxProcessed, [p.LastEditedTime]);
                        break;
                }
            }

            if (maxProcessed != cursor)
                _settingsRepo.Set("notion_last_poll", maxProcessed);
        }
        catch (OperationCanceledException) { }
        catch { /* 오프라인 등 — 다음 사이클 재시도 (코드베이스의 silent-catch 어법) */ }
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
