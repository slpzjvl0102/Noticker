namespace Noticker.Infrastructure;

// 전역 단축키 프리셋 키 → Win32 (modifiers, vk) 매핑 — 순수 로직, Win32 호출 없음.
// 자유 입력 없이 검증된 조합만 허용 (스펙 D2). Ctrl+Shift+N은 Chrome 시크릿 창과
// 충돌해 후보에서 제외됐다 (스펙 D1)
public static class HotkeyPresets
{
    public const string DefaultKey = "ctrl_alt_n";

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    // 키를 누르고 있을 때 WM_HOTKEY 반복 발생 방지 — 스티커 연속 생성 차단
    public const uint ModNoRepeat = 0x4000;

    private const uint VkN = 0x4E;
    private const uint VkSpace = 0x20;

    // 'none' → null (등록 안 함). 알 수 없는 키 → 기본 조합 폴백 (DB 잡값 안전)
    public static (uint Modifiers, uint Vk)? Resolve(string presetKey) => presetKey switch
    {
        "none" => null,
        "win_shift_n" => (ModWin | ModShift | ModNoRepeat, VkN),
        "ctrl_alt_space" => (ModControl | ModAlt | ModNoRepeat, VkSpace),
        _ => (ModControl | ModAlt | ModNoRepeat, VkN),   // ctrl_alt_n + 잡값 폴백
    };

    // 풍선/설정 콤보 표시용
    public static string DisplayName(string presetKey) => presetKey switch
    {
        "none" => "사용 안 함",
        "win_shift_n" => "Win+Shift+N",
        "ctrl_alt_space" => "Ctrl+Alt+Space",
        _ => "Ctrl+Alt+N",
    };
}
