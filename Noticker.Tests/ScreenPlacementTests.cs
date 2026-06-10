using System.Drawing;
using Noticker.Infrastructure;

namespace Noticker.Tests;

public class ScreenPlacementTests
{
    private static readonly Rectangle Primary = new(0, 0, 1920, 1040);
    private static readonly Rectangle Secondary = new(1920, 0, 2560, 1400);

    private static readonly List<(string DeviceName, Rectangle Area)> Screens =
    [
        (@"\\.\DISPLAY1", Primary),
        (@"\\.\DISPLAY2", Secondary),
    ];

    // ── SelectWorkingArea ──────────────────────────────────────────────────────

    [Fact]
    public void SelectWorkingArea_MatchingMonitor_ReturnsItsArea()
    {
        var area = ScreenPlacement.SelectWorkingArea(Screens, Primary, @"\\.\DISPLAY2");
        Assert.Equal(Secondary, area);
    }

    [Fact]
    public void SelectWorkingArea_MissingMonitor_FallsBackToPrimary()
    {
        var area = ScreenPlacement.SelectWorkingArea(Screens, Primary, @"\\.\DISPLAY9");
        Assert.Equal(Primary, area);
    }

    [Fact]
    public void SelectWorkingArea_NullDeviceName_FallsBackToPrimary()
    {
        var area = ScreenPlacement.SelectWorkingArea(Screens, Primary, null);
        Assert.Equal(Primary, area);
    }

    [Fact]
    public void SelectWorkingArea_MatchIsCaseInsensitive()
    {
        var area = ScreenPlacement.SelectWorkingArea(Screens, Primary, @"\\.\display2");
        Assert.Equal(Secondary, area);
    }

    // ── ClampToArea ────────────────────────────────────────────────────────────

    [Fact]
    public void ClampToArea_InsideArea_Unchanged()
    {
        var (x, y) = ScreenPlacement.ClampToArea(Primary, 100, 100, 250, 300);
        Assert.Equal((100, 100), (x, y));
    }

    [Fact]
    public void ClampToArea_OffscreenRight_ClampedIntoArea()
    {
        var (x, y) = ScreenPlacement.ClampToArea(Primary, 5000, 100, 250, 300);
        Assert.Equal(1920 - 250, x);
        Assert.Equal(100, y);
    }

    [Fact]
    public void ClampToArea_NegativeCoords_ClampedToTopLeft()
    {
        var (x, y) = ScreenPlacement.ClampToArea(Primary, -50, -70, 250, 300);
        Assert.Equal((0, 0), (x, y));
    }

    [Fact]
    public void ClampToArea_SecondaryMonitor_AppliesAreaOrigin()
    {
        // 저장 좌표는 모니터 working area 기준 상대 좌표 (RestoreStickers 의미론)
        var (x, y) = ScreenPlacement.ClampToArea(Secondary, 10, 20, 250, 300);
        Assert.Equal(1920 + 10, x);
        Assert.Equal(20, y);
    }

    [Fact]
    public void ClampToArea_WindowWiderThanArea_ClampsToLeftWithoutThrowing()
    {
        var (x, y) = ScreenPlacement.ClampToArea(Primary, 100, 100, 3000, 300);
        Assert.Equal(0, x);
        Assert.Equal(100, y);
    }
}
