using System.Collections.Concurrent;
using Noticker.Data;
using Noticker.Models;

namespace Noticker.Sync;

public class SyncQueue
{
    // (stickerId, errorMessage) — subscribe in App.xaml.cs to show tray balloon
    public event Action<string, string>? SyncError;

    // (stickerId, title, alreadyPushed) — 충돌 발생. App이 풍선 표시.
    // alreadyPushed=true는 사후 검출(TOCTOU) — push가 이미 반영된 뒤라 "보류" 문구는 거짓
    public event Action<string, string, bool>? SyncConflict;

    // (stickerId) — push 성공. 백그라운드 전이라 App이 표시등 갱신을 중계해야 한다
    public event Action<string>? SyncSucceeded;

    private readonly ConcurrentQueue<Sticker> _queue = new();
    private readonly StickerRepository _repo;
    private readonly NotionClient _client;
    private readonly AppSettings _settings;
    private readonly SemaphoreSlim _signal = new(0);
    private int _inFlight;

    public SyncQueue(StickerRepository repo, NotionClient client, AppSettings settings)
    {
        _repo = repo;
        _client = client;
        _settings = settings;
    }

    public void Enqueue(Sticker s)
    {
        if (_settings.IsSyncPaused) return;
        _queue.Enqueue(s);
        _signal.Release();
    }

    // 창이 들고 있는 live Sticker 조회 — UI 스레드에서만 호출됨 (RetryPendingAsync는
    // DispatcherTimer/메뉴에서 시작되고 await 재개도 dispatcher 컨텍스트)
    public Func<string, Sticker?>? LiveStickerLookup { get; set; }

    public async Task RetryPendingAsync(CancellationToken ct)
    {
        var pending = _repo.GetPendingForRetry();
        foreach (var s in pending)
        {
            if (ct.IsCancellationRequested) break;
            // DB 사본 대신 live 인스턴스로 교체 — 사본에 ack/synced를 쓰면 창의
            // 전체 행 저장(SavePosition 등)이 스테일 값으로 되돌린다 (검증 리뷰 F4)
            var live = LiveStickerLookup?.Invoke(s.Id);
            _queue.Enqueue(live ?? s);
            _signal.Release();
            await Task.Delay(200, ct); // stagger to avoid sync storm
        }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await _signal.WaitAsync(ct);
            if (!_queue.TryDequeue(out var sticker)) continue;

            // max 1 in-flight enforced by the single RunAsync loop
            Interlocked.Exchange(ref _inFlight, 1);
            try { await ProcessAsync(sticker, ct); }
            finally { Interlocked.Exchange(ref _inFlight, 0); }
        }
    }

    private void RaiseSyncError(string stickerId, string message) =>
        SyncError?.Invoke(stickerId, message);

    // 충돌 진입 + ack: 저장 쌍을 원격 값으로 갱신해야 다음 사용자 push가 재충돌 없이 이긴다.
    // 'conflict'는 GetPendingForRetry에서 제외됨 — 재시도 폭주/풍선 스팸 없음.
    // 다음 키 입력 시 SaveContent가 'pending'으로 덮어 해소
    private void MarkConflict(Sticker s, string remoteTime, string remoteBy, bool alreadyPushed)
    {
        // ack를 먼저 — 중간에 죽어도 "conflict인데 ack 없음"(재충돌 루프)이 아니라
        // "ack만 됨"(무해)으로 남는다
        s.NotionLastEdit = remoteTime;
        s.NotionLastEditBy = remoteBy;
        _repo.UpdateNotionLastEdit(s.Id, remoteTime, remoteBy);
        s.SyncState = "conflict";
        _repo.UpdateSyncState(s.Id, "conflict", s.NotionPageId, s.RetryCount);
        SyncConflict?.Invoke(s.Id, s.Title, alreadyPushed);
    }

    private async Task ProcessAsync(Sticker s, CancellationToken ct)
    {
        if (_settings.IsSyncPaused) return;
        if (string.IsNullOrEmpty(s.Title) && string.IsNullOrEmpty(s.Body)) return;

        try
        {
            if (s.NotionPageId is null)
            {
                var pageId = await _client.CreatePageAsync(s, ct);
                s.NotionPageId = pageId;
            }
            else
            {
                // 0단계 덮어쓰기 보호: children 전체 교체 전에 외부(사람) 수정 검출.
                // baseline(NotionLastEdit)이 없으면 비교 불가 — 이번 push의 사후 GET이 baseline을 만든다
                var botId = _settings.NotionBotUserId;
                if (botId is not null && s.NotionLastEdit is not null)
                {
                    var pre = await _client.GetPageMetaAsync(s.NotionPageId, ct);
                    if (pre is null)
                    {
                        // 404 — 기존 재생성 정책과 동일 경로
                        s.NotionPageId = null;
                        _queue.Enqueue(s);
                        _signal.Release();
                        return;
                    }
                    if (PullDecision.IsPushConflict(pre.Value.LastEditedTime, pre.Value.LastEditedById,
                            s.NotionLastEdit, s.NotionLastEditBy, botId))
                    {
                        MarkConflict(s, pre.Value.LastEditedTime, pre.Value.LastEditedById, alreadyPushed: false);
                        return;
                    }
                }

                await _client.UpdatePageAsync(s, ct);
            }

            // 사후 baseline 저장 — children PATCH 응답엔 페이지 last_edited_time이 없음.
            // push 도중 사람 수정이 끼어든 경우(TOCTOU)도 여기서 검출
            if (s.NotionPageId is not null && _settings.NotionBotUserId is string bot)
            {
                var post = await _client.GetPageMetaAsync(s.NotionPageId, ct);
                if (post is not null)
                {
                    if (post.Value.LastEditedById != bot)
                    {
                        MarkConflict(s, post.Value.LastEditedTime, post.Value.LastEditedById, alreadyPushed: true);
                        return;
                    }
                    s.NotionLastEdit = post.Value.LastEditedTime;
                    s.NotionLastEditBy = post.Value.LastEditedById;
                    _repo.UpdateNotionLastEdit(s.Id, post.Value.LastEditedTime, post.Value.LastEditedById);
                }
            }

            s.SyncState = "synced";
            s.RetryCount = 0;
            _repo.UpdateSyncState(s.Id, "synced", s.NotionPageId, 0);
            SyncSucceeded?.Invoke(s.Id);
        }
        catch (NotionPageNotFoundException)
        {
            // Page was archived/deleted — re-create it
            s.NotionPageId = null;
            _queue.Enqueue(s);
            _signal.Release();
        }
        catch (NotionUnauthorizedException ex)
        {
            _settings.IsSyncPaused = true;
            s.SyncState = "failed";
            _repo.UpdateSyncState(s.Id, "failed", s.NotionPageId, s.RetryCount);
            RaiseSyncError(s.Id, ex.Message);
        }
        catch (NotionRateLimitException)
        {
            await Task.Delay(5000, ct);
            _queue.Enqueue(s); // retry
            _signal.Release();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Infrastructure.SyncLog.Write($"push: EXCEPTION {ex.GetType().Name}: {ex.Message}");
            s.RetryCount++;
            var newState = s.RetryCount >= 3 ? "failed" : "pending";
            s.SyncState = newState;
            _repo.UpdateSyncState(s.Id, newState, s.NotionPageId, s.RetryCount);
            if (newState == "failed")
                RaiseSyncError(s.Id, ex.Message);
        }
    }
}
