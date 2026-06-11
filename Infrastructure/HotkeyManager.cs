using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Noticker.Infrastructure;

// RegisterHotKey 래퍼 — 메시지 전용 HwndSource로 WM_HOTKEY를 수신한다.
// 트레이 앱이라 항상 떠 있는 창이 없어 수신 전용 HWND가 필요 (스펙 D4).
// Win32 의존을 이 클래스에 격리 — 단위 테스트 대상 아님 (수동 QA, 스펙 §6)
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;
    private static readonly IntPtr HwndMessage = new(-3);   // HWND_MESSAGE

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public event Action? Pressed;

    public HotkeyManager()
    {
        // UI 스레드에서 생성할 것 — AddHook 콜백이 생성 스레드로 들어온다
        _source = new HwndSource(new HwndSourceParameters("NotickerHotkey")
        {
            ParentWindow = HwndMessage,
        });
        _source.AddHook(WndProc);
    }

    // 기존 등록 해제 후 새 조합 등록. 실패(타 앱 점유) 시 false — 등록 없음 상태
    public bool Register(uint modifiers, uint vk)
    {
        if (_disposed) return false;
        Unregister();
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_disposed) return IntPtr.Zero;   // 큐에 남은 WM_HOTKEY가 Dispose 후 도착하는 레이스 차단
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // 프로세스 종료 시 OS가 자동 해제하지만 명시적으로 정리 — ExitApp 관례와 동일.
    // ExitApp과 OnExit 양쪽에서 불려도 안전하게 멱등
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
