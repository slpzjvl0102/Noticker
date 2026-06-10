using Color = System.Windows.Media.Color;

namespace Noticker.Infrastructure;

// 노션 색 키 → (Bar, Wedge) 단일 출처.
// Bar = 스티커 타이틀바용 어두운 값 (StickerWindow 기존 _notionColors 그대로),
// Wedge = 타이머 웨지용 밝은 변형 (흰 바탕·#333 바탕 양쪽 대비 확보).
public static class NotionColorPalette
{
    private static readonly Dictionary<string, (Color Bar, Color Wedge)> _colors = new()
    {
        ["default"] = (Color.FromRgb(0x33, 0x33, 0x33), Color.FromRgb(0x8A, 0x8A, 0x8A)),
        ["gray"]    = (Color.FromRgb(0x4A, 0x4A, 0x4A), Color.FromRgb(0x9A, 0xA0, 0xA6)),
        ["brown"]   = (Color.FromRgb(0x60, 0x36, 0x1A), Color.FromRgb(0xB5, 0x65, 0x1D)),
        ["orange"]  = (Color.FromRgb(0x99, 0x4A, 0x00), Color.FromRgb(0xF2, 0x99, 0x4A)),
        ["yellow"]  = (Color.FromRgb(0x7B, 0x56, 0x0E), Color.FromRgb(0xE6, 0xB8, 0x4C)),
        ["green"]   = (Color.FromRgb(0x1A, 0x5C, 0x3A), Color.FromRgb(0x2E, 0x9E, 0x5B)),
        ["blue"]    = (Color.FromRgb(0x1A, 0x44, 0x80), Color.FromRgb(0x3B, 0x82, 0xE0)),
        ["purple"]  = (Color.FromRgb(0x4D, 0x21, 0x7A), Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ["pink"]    = (Color.FromRgb(0x80, 0x1D, 0x5A), Color.FromRgb(0xE0, 0x56, 0x9E)),
        ["red"]     = (Color.FromRgb(0x80, 0x1C, 0x1C), Color.FromRgb(0xE8, 0x40, 0x33)),
    };

    public static readonly Color FallbackWedge = Color.FromRgb(0xE8, 0x40, 0x33);

    // 미존재/null 키 → fallback (#E84033)
    public static Color Wedge(string? key) =>
        key is not null && _colors.TryGetValue(key, out var c) ? c.Wedge : FallbackWedge;

    // 미존재 키 → null (StickerWindow의 "unknown → 기본 브러시" 의미론 보존)
    public static Color? Bar(string key) =>
        _colors.TryGetValue(key, out var c) ? c.Bar : null;

    public static IReadOnlyCollection<string> Keys => _colors.Keys;
}
