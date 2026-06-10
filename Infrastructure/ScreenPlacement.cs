using System.Drawing;

namespace Noticker.Infrastructure;

// 창 복원 시 모니터 매칭 + working area clamp (스티커·포모도로 공용 순수 함수)
public static class ScreenPlacement
{
    public static Rectangle SelectWorkingArea(
        IReadOnlyList<(string DeviceName, Rectangle Area)> screens,
        Rectangle primaryArea,
        string? deviceName)
    {
        foreach (var (name, area) in screens)
        {
            if (string.Equals(name, deviceName, StringComparison.OrdinalIgnoreCase))
                return area;
        }
        return primaryArea;
    }

    // relX/relY는 해당 모니터 working area 기준 상대 좌표. 창이 영역보다 크면 좌상단 고정.
    public static (int X, int Y) ClampToArea(Rectangle area, int relX, int relY, int width, int height)
    {
        int x = Math.Clamp(relX + area.Left, area.Left, Math.Max(area.Left, area.Right - width));
        int y = Math.Clamp(relY + area.Top, area.Top, Math.Max(area.Top, area.Bottom - height));
        return (x, y);
    }
}
