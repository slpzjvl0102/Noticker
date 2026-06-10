using System.Windows.Threading;
using Noticker.Models;

namespace Noticker.Sync;

public class DebouncedSyncService
{
    private readonly SyncQueue _queue;
    private DispatcherTimer? _timer;
    private Sticker? _pending;
    private bool _cancelled;

    public DebouncedSyncService(SyncQueue queue)
    {
        _queue = queue;
    }

    // pull의 dirty 가드용 — _pending은 enqueue 후 null로 돌아가지 않으므로 타이머 상태가 기준
    public bool IsPending => _timer?.IsEnabled == true;

    // Called by StickerWindow whenever title or body changes.
    public void OnChanged(Sticker sticker)
    {
        if (_cancelled) return;
        _pending = sticker;

        if (_timer == null)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _timer.Tick += OnTick;
        }

        _timer.Stop();
        _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _timer!.Stop();
        if (_cancelled || _pending is null) return;
        _queue.Enqueue(_pending);
    }

    // Call on StickerWindow.Closed to avoid timer firing after window is gone.
    public void Cancel()
    {
        _cancelled = true;
        _timer?.Stop();
    }
}
