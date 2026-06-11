using Noticker.Infrastructure;

namespace Noticker.Tests;

public class HotkeyPresetsTests
{
    [Fact]
    public void Resolve_CtrlAltN_MapsToControlAltN()
    {
        var r = HotkeyPresets.Resolve("ctrl_alt_n");
        Assert.NotNull(r);
        Assert.Equal(
            HotkeyPresets.ModControl | HotkeyPresets.ModAlt | HotkeyPresets.ModNoRepeat,
            r.Value.Modifiers);
        Assert.Equal(0x4Eu, r.Value.Vk);   // 'N'
    }

    [Fact]
    public void Resolve_WinShiftN_MapsToWinShiftN()
    {
        var r = HotkeyPresets.Resolve("win_shift_n");
        Assert.NotNull(r);
        Assert.Equal(
            HotkeyPresets.ModWin | HotkeyPresets.ModShift | HotkeyPresets.ModNoRepeat,
            r.Value.Modifiers);
        Assert.Equal(0x4Eu, r.Value.Vk);
    }

    [Fact]
    public void Resolve_CtrlAltSpace_MapsToControlAltSpace()
    {
        var r = HotkeyPresets.Resolve("ctrl_alt_space");
        Assert.NotNull(r);
        Assert.Equal(
            HotkeyPresets.ModControl | HotkeyPresets.ModAlt | HotkeyPresets.ModNoRepeat,
            r.Value.Modifiers);
        Assert.Equal(0x20u, r.Value.Vk);   // Space
    }

    [Fact]
    public void Resolve_None_ReturnsNull()
    {
        Assert.Null(HotkeyPresets.Resolve("none"));
    }

    [Fact]
    public void Resolve_UnknownKey_FallsBackToDefault()
    {
        // DB에 잡값이 남아도 안전 — 기본 조합으로 폴백 (스펙 §1).
        // Resolve(DefaultKey)와 비교하면 양쪽이 같이 망가져도 통과 — 기대값을 직접 고정
        var fallback = HotkeyPresets.Resolve("garbage_value");
        Assert.NotNull(fallback);
        Assert.Equal(
            HotkeyPresets.ModControl | HotkeyPresets.ModAlt | HotkeyPresets.ModNoRepeat,
            fallback.Value.Modifiers);
        Assert.Equal(0x4Eu, fallback.Value.Vk);
    }

    [Theory]
    [InlineData("ctrl_alt_n")]
    [InlineData("win_shift_n")]
    [InlineData("ctrl_alt_space")]
    public void Resolve_AllCombos_IncludeNoRepeat(string key)
    {
        // 키를 누르고 있을 때 스티커 연속 생성 방지 (스펙 §1)
        var r = HotkeyPresets.Resolve(key);
        Assert.NotNull(r);
        Assert.Equal(HotkeyPresets.ModNoRepeat, r.Value.Modifiers & HotkeyPresets.ModNoRepeat);
    }

    [Theory]
    [InlineData("ctrl_alt_n", "Ctrl+Alt+N")]
    [InlineData("win_shift_n", "Win+Shift+N")]
    [InlineData("ctrl_alt_space", "Ctrl+Alt+Space")]
    [InlineData("none", "사용 안 함")]
    public void DisplayName_KnownKeys(string key, string expected)
    {
        Assert.Equal(expected, HotkeyPresets.DisplayName(key));
    }

    [Fact]
    public void DisplayName_UnknownKey_FallsBackToDefault()
    {
        Assert.Equal("Ctrl+Alt+N", HotkeyPresets.DisplayName("garbage_value"));
    }
}
