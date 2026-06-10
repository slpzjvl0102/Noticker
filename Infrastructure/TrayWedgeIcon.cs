using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Bitmap = System.Drawing.Bitmap;
using Color = System.Drawing.Color;
using Graphics = System.Drawing.Graphics;
using Icon = System.Drawing.Icon;
using Pen = System.Drawing.Pen;
using Rectangle = System.Drawing.Rectangle;
using SolidBrush = System.Drawing.SolidBrush;

namespace Noticker.Infrastructure;

// 트레이용 16×16 미니 웨지 아이콘 — 위젯 다이얼과 동일하게 12시 기준 반시계.
// GetHicon의 HICON은 Icon.FromHandle이 소유하지 않으므로 DestroyIcon 필수 (GDI 핸들 누수 방지).
public static class TrayWedgeIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Color RingGray = Color.FromArgb(0x88, 0x88, 0x88);

    public static Icon Render(double fraction, Color wedgeColor)
    {
        fraction = double.IsNaN(fraction) ? 0.0 : Math.Clamp(fraction, 0.0, 1.0);

        using var bmp = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new Rectangle(1, 1, 13, 13);
            using var fill = new SolidBrush(wedgeColor);

            if (fraction >= 1.0)
            {
                g.FillEllipse(fill, rect);
            }
            else if (fraction > 0.0)
            {
                // GDI 각도: 0°=3시, 양수=시계방향 → 12시 시작 반시계 = start -90, sweep 음수
                g.FillPie(fill, rect, -90f, -(float)(fraction * 360.0));
            }

            using var ring = new Pen(RingGray, 1f);
            g.DrawEllipse(ring, rect);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var unowned = Icon.FromHandle(handle);
            return (Icon)unowned.Clone();              // 핸들 독립 사본 — 호출자가 Dispose
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
