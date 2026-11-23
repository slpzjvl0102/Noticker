using System.Drawing;
using System.Runtime.InteropServices;
using Screen = System.Windows.Forms.Screen;

namespace Noticker.Infrastructure;

// WinForms Screen(물리 픽셀) → WPF 좌표(DIP) 변환. PerMonitorV2 앱에서 Screen.WorkingArea는
// 물리 픽셀이고 Window.Left/Top/Width/Height는 DIP(1/96인치)라, 배율 125%·150% 노트북에서
// 그대로 섞으면 중앙 배치·화면 밖 clamp가 어긋난다. 모니터별 배율로 나눠 DIP로 맞춘다.
public static class ScreenDip
{
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    // 모니터 배율 (1.0 = 100%). 조회 실패(구버전 OS 등) 시 1.0 — 기존 동작과 동일
    public static double ScaleOf(Screen screen)
    {
        try
        {
            var b = screen.Bounds;
            var hmon = MonitorFromPoint(new POINT { X = b.Left + b.Width / 2, Y = b.Top + b.Height / 2 },
                MONITOR_DEFAULTTONEAREST);
            if (hmon != IntPtr.Zero && GetDpiForMonitor(hmon, MDT_EFFECTIVE_DPI, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX / 96.0;
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
        return 1.0;
    }

    // 순수 함수 — 픽셀 사각형을 배율로 나눠 DIP 사각형으로 (테스트 대상)
    public static Rectangle ToDip(Rectangle px, double scale)
    {
        if (scale <= 0 || double.IsNaN(scale)) scale = 1.0;
        int left = (int)Math.Round(px.Left / scale);
        int top = (int)Math.Round(px.Top / scale);
        int right = (int)Math.Round(px.Right / scale);
        int bottom = (int)Math.Round(px.Bottom / scale);
        return Rectangle.FromLTRB(left, top, right, bottom);
    }

    public static Rectangle WorkingArea(Screen screen) => ToDip(screen.WorkingArea, ScaleOf(screen));

    // 스티커/포모도로 위치 복원용 — (모니터 이름, DIP 작업 영역) 목록
    public static IReadOnlyList<(string DeviceName, Rectangle Area)> AllWorkingAreas() =>
        Screen.AllScreens.Select(sc => (sc.DeviceName, WorkingArea(sc))).ToList();

    public static Rectangle PrimaryWorkingArea() => WorkingArea(Screen.PrimaryScreen!);
}
