using System.Drawing;
using Noticker.Infrastructure;

namespace Noticker.Tests;

public class ScreenDipTests
{
    [Fact]
    public void ToDip_Scale100_Unchanged()
    {
        var px = new Rectangle(0, 0, 1920, 1040);
        Assert.Equal(px, ScreenDip.ToDip(px, 1.0));
    }

    [Fact]
    public void ToDip_Scale150_DividesAllEdges()
    {
        // 1920x1080 노트북 150% — 작업 표시줄 제외 1920x1032 px → 1280x688 DIP
        var dip = ScreenDip.ToDip(new Rectangle(0, 0, 1920, 1032), 1.5);
        Assert.Equal(Rectangle.FromLTRB(0, 0, 1280, 688), dip);
    }

    [Fact]
    public void ToDip_SecondaryMonitorOffset_ScalesOrigin()
    {
        var dip = ScreenDip.ToDip(Rectangle.FromLTRB(1920, 0, 3840, 1080), 1.25);
        Assert.Equal(Rectangle.FromLTRB(1536, 0, 3072, 864), dip);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    public void ToDip_InvalidScale_FallsBackTo100(double scale)
    {
        var px = new Rectangle(10, 20, 300, 400);
        Assert.Equal(px, ScreenDip.ToDip(px, scale));
    }
}
