using Noticker.Infrastructure;

namespace Noticker.Tests;

public class NotionColorPaletteTests
{
    [Fact]
    public void EveryKey_HasWedgeVariant()
    {
        Assert.Equal(10, NotionColorPalette.Keys.Count);
        foreach (var key in NotionColorPalette.Keys)
            Assert.NotNull(NotionColorPalette.Bar(key));
    }

    [Fact]
    public void UnknownOrNullKey_FallsBackToRed()
    {
        Assert.Equal(NotionColorPalette.FallbackWedge, NotionColorPalette.Wedge(null));
        Assert.Equal(NotionColorPalette.FallbackWedge, NotionColorPalette.Wedge("nope"));
    }

    [Fact]
    public void KnownKey_Wedge_DiffersFromBar()
    {
        foreach (var key in NotionColorPalette.Keys)
            Assert.NotEqual(NotionColorPalette.Bar(key), NotionColorPalette.Wedge(key));
    }
}
