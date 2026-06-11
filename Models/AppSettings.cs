using System.ComponentModel;

namespace Noticker.Models;

public class AppSettings : INotifyPropertyChanged
{
    public static AppSettings Instance { get; } = new();

    private AppSettings() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _colorSwapped;
    public bool ColorSwapped
    {
        get => _colorSwapped;
        set { _colorSwapped = value; Notify(nameof(ColorSwapped)); }
    }

    private bool _isSyncPaused;
    public bool IsSyncPaused
    {
        get => _isSyncPaused;
        set { _isSyncPaused = value; Notify(nameof(IsSyncPaused)); }
    }

    public string? NotionToken { get; set; }
    public string? TargetDbId { get; set; }
    public string? NotionBotUserId { get; set; }   // null = 덮어쓰기 보호/pull 미활성 (캐시 전)
    public string? NotionDbTitle { get; set; }     // 설정 창 요약 표시용 캐시 (온보딩 완료 시 저장)
    public string CategoryPropertyName { get; set; } = "Category";
    public bool AutostartEnabled { get; set; } = true;   // 기본 활성 — 명시적 해제("false")만 비활성
    public List<string> CategoryOptions { get; set; } = [];
    public Dictionary<string, string> CategoryColors { get; set; } = [];

    // ── 포모도로 ───────────────────────────────────────────────────────────────
    // 범위 상수는 LoadInto(저장값)와 SettingsWindow(입력) 양쪽에서 공유 — 한 곳 정의

    public const int PomodoroFocusDefault = 25, PomodoroFocusMin = 1, PomodoroFocusMax = 120;
    public const int PomodoroShortBreakDefault = 5, PomodoroBreakMin = 1, PomodoroBreakMax = 60;
    public const int PomodoroLongBreakDefault = 15;
    public const int PomodoroIntervalDefault = 4, PomodoroIntervalMin = 1, PomodoroIntervalMax = 12;
    public const int PomodoroCustomDefault = 30; // 범위는 PomodoroBreakMin/Max (1-60) 공유

    public int PomodoroFocusMinutes { get; set; } = PomodoroFocusDefault;
    public int PomodoroShortBreakMinutes { get; set; } = PomodoroShortBreakDefault;
    public int PomodoroLongBreakMinutes { get; set; } = PomodoroLongBreakDefault;
    public int PomodoroLongBreakInterval { get; set; } = PomodoroIntervalDefault;
    public int PomodoroCustomMinutes { get; set; } = PomodoroCustomDefault;
    public bool PomodoroAutoStart { get; set; }
    public bool PomodoroSound { get; set; } = true;
    public bool PomodoroAlwaysOnTop { get; set; } = true;

    public static int ParseClamped(string? raw, int def, int min, int max) =>
        int.TryParse(raw, out var v) ? Math.Clamp(v, min, max) : def;

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(NotionToken) &&
        !string.IsNullOrWhiteSpace(TargetDbId);
}
