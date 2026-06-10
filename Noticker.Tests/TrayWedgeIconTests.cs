using Noticker.Infrastructure;

namespace Noticker.Tests;

public class TrayWedgeIconTests
{
    private static readonly System.Drawing.Color Red = System.Drawing.Color.FromArgb(0xE8, 0x40, 0x33);

    [Theory]
    [InlineData(0.0)]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(1.0)]
    public void Render_ValidFractions_Returns16x16Icon(double fraction)
    {
        using var icon = TrayWedgeIcon.Render(fraction, Red);
        Assert.NotNull(icon);
        Assert.Equal(16, icon.Width);
        Assert.Equal(16, icon.Height);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(-1.0)]
    [InlineData(2.0)]
    public void Render_OutOfRangeFractions_ClampsWithoutThrowing(double fraction)
    {
        using var icon = TrayWedgeIcon.Render(fraction, Red);
        Assert.Equal(16, icon.Width);
    }

    [Fact]
    public void Render_RepeatedCalls_DoNotLeakHandles()
    {
        // GetHicon 핸들 누수 방어 — 수백 회 반복 생성/해제가 예외 없이 동작해야 함
        for (int i = 0; i < 300; i++)
        {
            using var icon = TrayWedgeIcon.Render(i / 300.0, Red);
        }
    }
}
