using System.Windows.Threading;
using Noticker.Models;

namespace Noticker.Sync;

public class DebouncedSyncService
{
    private readonly SyncQueue _queue;
    private DispatcherTimer? _timer;
    private Sticker? _pending;
    private bool _cancelled;

    // enqueue 직전 호출 — StickerWindow가 디바운스 중인 본문 직렬화를 확정해
    // push가 항상 최신 Body/BodyRtf/BodyRuns를 보내도록 보장한다
    private readonly Action? _beforeEnqueue;

    public DebouncedSyncService(SyncQueue queue, Action? beforeEnqueue = null)
    {
        _queue = queue;
        _beforeEnqueue = beforeEnqueue;
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
        _beforeEnqueue?.Invoke();
        _queue.Enqueue(_pending);
    }

    // Call on StickerWindow.Closed to avoid timer firing after window is gone.
    public void Cancel()
    {
        _cancelled = true;
        _timer?.Stop();
    }
}
