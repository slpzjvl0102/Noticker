using System.Collections.Concurrent;
using Noticker.Data;
using Noticker.Models;

namespace Noticker.Sync;

public class SyncQueue
{
    // (stickerId, errorMessage) — subscribe in App.xaml.cs to show tray balloon
    public event Action<string, string>? SyncError;

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

    public async Task RetryPendingAsync(CancellationToken ct)
    {
        var pending = _repo.GetPendingForRetry();
        foreach (var s in pending)
        {
            if (ct.IsCancellationRequested) break;
            _queue.Enqueue(s);
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
                await _client.UpdatePageAsync(s, ct);
            }

            s.SyncState = "synced";
            s.RetryCount = 0;
            _repo.UpdateSyncState(s.Id, "synced", s.NotionPageId, 0);
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
            s.RetryCount++;
            var newState = s.RetryCount >= 3 ? "failed" : "pending";
            s.SyncState = newState;
            _repo.UpdateSyncState(s.Id, newState, s.NotionPageId, s.RetryCount);
            if (newState == "failed")
                RaiseSyncError(s.Id, ex.Message);
        }
    }
}
