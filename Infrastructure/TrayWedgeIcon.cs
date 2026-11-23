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

// 트레이용 미니 웨지 아이콘 (기본 16×16, 고배율에선 트레이 크기로 렌더해 흐림 방지) — 위젯 다이얼과 동일하게 12시 기준 반시계.
// GetHicon의 HICON은 Icon.FromHandle이 소유하지 않으므로 DestroyIcon 필수 (GDI 핸들 누수 방지).
public static class TrayWedgeIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private static readonly Color RingGray = Color.FromArgb(0x88, 0x88, 0x88);

    public static Icon Render(double fraction, Color wedgeColor, int size = 16)
    {
        fraction = double.IsNaN(fraction) ? 0.0 : Math.Clamp(fraction, 0.0, 1.0);
        size = Math.Clamp(size, 16, 64);
        float k = size / 16f;   // 16px 기준 도형을 비례 확대

        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var rect = new Rectangle((int)Math.Round(1 * k), (int)Math.Round(1 * k),
                (int)Math.Round(13 * k), (int)Math.Round(13 * k));
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

            using var ring = new Pen(RingGray, k);
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
