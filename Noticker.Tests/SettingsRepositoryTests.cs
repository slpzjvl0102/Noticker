using System.IO;
using Noticker.Data;
using Noticker.Models;

namespace Noticker.Tests;

public class SettingsRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SettingsRepository _repo;

    public SettingsRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"noticker_settings_test_{Guid.NewGuid()}.db");
        _repo = new SettingsRepository(_dbPath);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(_dbPath);
    }

    // ── Get / Set ──────────────────────────────────────────────────────────────

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        _repo.Set("my_key", "my_value");
        Assert.Equal("my_value", _repo.Get("my_key"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        Assert.Null(_repo.Get("does_not_exist"));
    }

    [Fact]
    public void Set_Upserts_ExistingKey()
    {
        _repo.Set("key", "first");
        _repo.Set("key", "second");
        Assert.Equal("second", _repo.Get("key"));
    }

    // ── Delete ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Delete_RemovesKey()
    {
        _repo.Set("key", "value");
        _repo.Delete("key");
        Assert.Null(_repo.Get("key"));
    }

    [Fact]
    public void Delete_NonExistentKey_DoesNotThrow()
    {
        var ex = Record.Exception(() => _repo.Delete("ghost"));
        Assert.Null(ex);
    }

    // ── LoadInto ───────────────────────────────────────────────────────────────
    // AppSettings is a singleton, so we use Instance and verify state after LoadInto.

    [Fact]
    public void LoadInto_ColorSwapped_True()
    {
        _repo.Set("color_swapped", "true");
        _repo.LoadInto(AppSettings.Instance);
        Assert.True(AppSettings.Instance.ColorSwapped);
    }

    [Fact]
    public void LoadInto_TargetDbId_Loaded()
    {
        _repo.Set("target_db_id", "db-xyz");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("db-xyz", AppSettings.Instance.TargetDbId);
    }

    [Fact]
    public void LoadInto_NotionDbTitle_Loaded()
    {
        _repo.Set("notion_db_title", "업무 노트");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("업무 노트", AppSettings.Instance.NotionDbTitle);
    }

    [Fact]
    public void LoadInto_MissingNotionDbTitle_Null()
    {
        // singleton 오염 방지: 먼저 잡값을 넣고 LoadInto가 null로 덮는지 확인
        AppSettings.Instance.NotionDbTitle = "stale";
        _repo.LoadInto(AppSettings.Instance);
        Assert.Null(AppSettings.Instance.NotionDbTitle);
    }

    [Fact]
    public void LoadInto_MissingAutostart_DefaultsTrue()
    {
        AppSettings.Instance.AutostartEnabled = false;   // 잡값
        _repo.LoadInto(AppSettings.Instance);
        Assert.True(AppSettings.Instance.AutostartEnabled);
    }

    [Fact]
    public void LoadInto_AutostartFalse_Loaded()
    {
        _repo.Set("autostart_enabled", "false");
        _repo.LoadInto(AppSettings.Instance);
        Assert.False(AppSettings.Instance.AutostartEnabled);
    }

    [Fact]
    public void LoadInto_MissingHotkeyPreset_DefaultsCtrlAltN()
    {
        AppSettings.Instance.HotkeyPreset = "stale";   // 잡값
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("ctrl_alt_n", AppSettings.Instance.HotkeyPreset);
    }

    [Fact]
    public void LoadInto_HotkeyPreset_Loaded()
    {
        _repo.Set("hotkey_preset", "win_shift_n");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("win_shift_n", AppSettings.Instance.HotkeyPreset);
    }

    [Fact]
    public void LoadInto_CategoryPropertyName_DefaultsToCategory()
    {
        // fresh DB — no category_property_name stored
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("Category", AppSettings.Instance.CategoryPropertyName);
    }

    [Fact]
    public void LoadInto_CategoryOptions_ParsedFromJson()
    {
        _repo.Set("category_options_cache", "[\"Work\",\"Personal\"]");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Contains("Work", AppSettings.Instance.CategoryOptions);
        Assert.Contains("Personal", AppSettings.Instance.CategoryOptions);
    }

    [Fact]
    public void LoadInto_MalformedCategoryOptions_FallsBackToEmpty()
    {
        AppSettings.Instance.CategoryOptions = [];
        _repo.Set("category_options_cache", "not-json");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Empty(AppSettings.Instance.CategoryOptions);
    }

    [Fact]
    public void LoadInto_CategoryColors_ParsedFromJson()
    {
        _repo.Set("category_colors_cache", "{\"Work\":\"blue\"}");
        _repo.LoadInto(AppSettings.Instance);
        Assert.True(AppSettings.Instance.CategoryColors.ContainsKey("Work"));
        Assert.Equal("blue", AppSettings.Instance.CategoryColors["Work"]);
    }

    // ── 포모도로 설정 ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null, 25)]
    [InlineData("abc", 25)]
    [InlineData("", 25)]
    [InlineData("0", 1)]
    [InlineData("-5", 1)]
    [InlineData("99999", 120)]
    [InlineData("50", 50)]
    public void ParseClamped_ZeroNegativeTextHuge_ClampOrDefault(string? raw, int expected)
    {
        Assert.Equal(expected, AppSettings.ParseClamped(raw, 25, 1, 120));
    }

    [Fact]
    public void LoadInto_MissingPomodoroKeys_AppliesDefaults()
    {
        // singleton 오염 방지: 먼저 잡값을 넣고 LoadInto가 기본값으로 덮는지 확인
        AppSettings.Instance.PomodoroFocusMinutes = 99;
        AppSettings.Instance.PomodoroAutoStart = true;
        AppSettings.Instance.PomodoroSound = false;
        AppSettings.Instance.PomodoroAlwaysOnTop = false;

        _repo.LoadInto(AppSettings.Instance);

        Assert.Equal(25, AppSettings.Instance.PomodoroFocusMinutes);
        Assert.Equal(5, AppSettings.Instance.PomodoroShortBreakMinutes);
        Assert.Equal(15, AppSettings.Instance.PomodoroLongBreakMinutes);
        Assert.Equal(4, AppSettings.Instance.PomodoroLongBreakInterval);
        Assert.False(AppSettings.Instance.PomodoroAutoStart);
        Assert.True(AppSettings.Instance.PomodoroSound);
        Assert.True(AppSettings.Instance.PomodoroAlwaysOnTop);
    }

    [Fact]
    public void LoadInto_StoredPomodoroValues_Loaded()
    {
        _repo.Set("pomodoro_focus_min", "50");
        _repo.Set("pomodoro_short_break_min", "10");
        _repo.Set("pomodoro_long_break_min", "30");
        _repo.Set("pomodoro_long_break_interval", "2");
        _repo.Set("pomodoro_auto_start", "true");
        _repo.Set("pomodoro_sound", "false");
        _repo.Set("pomodoro_always_on_top", "false");

        _repo.LoadInto(AppSettings.Instance);

        Assert.Equal(50, AppSettings.Instance.PomodoroFocusMinutes);
        Assert.Equal(10, AppSettings.Instance.PomodoroShortBreakMinutes);
        Assert.Equal(30, AppSettings.Instance.PomodoroLongBreakMinutes);
        Assert.Equal(2, AppSettings.Instance.PomodoroLongBreakInterval);
        Assert.True(AppSettings.Instance.PomodoroAutoStart);
        Assert.False(AppSettings.Instance.PomodoroSound);
        Assert.False(AppSettings.Instance.PomodoroAlwaysOnTop);
    }

    [Fact]
    public void LoadInto_MalformedPomodoroValues_ClampedOrDefault()
    {
        _repo.Set("pomodoro_focus_min", "abc");      // → 기본 25
        _repo.Set("pomodoro_short_break_min", "999"); // → max 60
        _repo.Set("pomodoro_long_break_interval", "0"); // → min 1

        _repo.LoadInto(AppSettings.Instance);

        Assert.Equal(25, AppSettings.Instance.PomodoroFocusMinutes);
        Assert.Equal(60, AppSettings.Instance.PomodoroShortBreakMinutes);
        Assert.Equal(1, AppSettings.Instance.PomodoroLongBreakInterval);
    }

    [Fact]
    public void CustomMin_RoundTrip()
    {
        _repo.Set("pomodoro_custom_min", "37");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal(37, AppSettings.Instance.PomodoroCustomMinutes);
    }

    [Theory]
    [InlineData("abc", 30)]  // → 기본 30
    [InlineData("0", 1)]     // → min 1
    [InlineData("999", 60)]  // → max 60
    [InlineData(null, 30)]   // 미저장 → 기본 30
    public void CustomMin_CorruptedOrOutOfRange_ParseClamped(string? raw, int expected)
    {
        // singleton 오염 방지: 먼저 잡값을 넣고 LoadInto가 덮는지 확인
        AppSettings.Instance.PomodoroCustomMinutes = 99;
        if (raw is not null)
            _repo.Set("pomodoro_custom_min", raw);

        _repo.LoadInto(AppSettings.Instance);

        Assert.Equal(expected, AppSettings.Instance.PomodoroCustomMinutes);
    }
}
