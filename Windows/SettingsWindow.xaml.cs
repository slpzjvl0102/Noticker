using System.IO;
using System.Reflection;
using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using Noticker.Data;
using Noticker.Infrastructure;
using Noticker.Models;
using Noticker.Sync;

namespace Noticker.Windows;

public partial class SettingsWindow : Window
{
    private readonly SettingsRepository _settings;
    private readonly NotionClient _client;
    private readonly StickerRepository _stickerRepo;

    public SettingsWindow(SettingsRepository settings, NotionClient client, StickerRepository stickerRepo)
    {
        _settings = settings;
        _client = client;
        _stickerRepo = stickerRepo;

        InitializeComponent();
        LoadCurrentValues();
    }

    private void LoadCurrentValues()
    {
        var app = AppSettings.Instance;

        if (app.NotionToken is not null)
            TokenBox.Password = app.NotionToken;

        DbIdBox.Text = app.TargetDbId ?? "";
        CategoryPropertyBox.Text = app.CategoryPropertyName;
        ColorSwapCheck.IsChecked = app.ColorSwapped;
        AutostartCheck.IsChecked = app.AutostartEnabled;

        PomFocusBox.Text = app.PomodoroFocusMinutes.ToString();
        PomShortBox.Text = app.PomodoroShortBreakMinutes.ToString();
        PomLongBox.Text = app.PomodoroLongBreakMinutes.ToString();
        PomIntervalBox.Text = app.PomodoroLongBreakInterval.ToString();
        PomAutoStartCheck.IsChecked = app.PomodoroAutoStart;
        PomSoundCheck.IsChecked = app.PomodoroSound;

        var cats = app.CategoryOptions;
        CatStatusText.Text = cats.Count > 0
            ? $"{cats.Count}개 옵션 캐시됨"
            : "캐시 없음 — 새로고침 버튼을 클릭하세요";
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false;
        TestResultText.Foreground = Brushes.Gray;
        TestResultText.Text = "테스트 중…";

        ApplyToAppSettings();

        var error = await _client.TestConnectionAsync(default);
        if (error is null)
        {
            TestResultText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x15, 0x80, 0x3D)); // green-700, 6.1:1 on #FAFAFA
            TestResultText.Text = "연결 성공!";
        }
        else
        {
            TestResultText.Foreground = Brushes.Red;
            TestResultText.Text = $"실패: {error}";
        }

        TestButton.IsEnabled = true;
    }

    private async void RefreshCatButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCatButton.IsEnabled = false;
        CatStatusText.Text = "불러오는 중…";

        ApplyToAppSettings();

        try
        {
            var options = await _client.FetchCategoryOptionsAsync(default);
            var names = options.Select(o => o.Name).ToList();
            var colors = options.ToDictionary(o => o.Name, o => o.Color);

            AppSettings.Instance.CategoryOptions = names;
            AppSettings.Instance.CategoryColors = colors;
            _settings.SaveCategoryOptions(names);
            _settings.SaveCategoryColors(colors);
            CatStatusText.Text = $"{options.Count}개 옵션 새로고침 완료";

            // Refresh all open sticker windows
            foreach (var win in App.Current.Windows.OfType<StickerWindow>())
                win.RefreshCategoryOptions();
        }
        catch (Exception ex)
        {
            CatStatusText.Text = $"실패: {ex.Message}";
        }
        finally
        {
            RefreshCatButton.IsEnabled = true;
        }
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyToAppSettings();
        PersistSettings();
        Close();
    }

    private void ApplyToAppSettings()
    {
        var app = AppSettings.Instance;

        var token = TokenBox.Password.Trim();
        if (!string.IsNullOrEmpty(token))
            app.NotionToken = token;

        app.TargetDbId = NormalizeDbId(DbIdBox.Text.Trim());
        app.CategoryPropertyName = string.IsNullOrWhiteSpace(CategoryPropertyBox.Text)
            ? "Category"
            : CategoryPropertyBox.Text.Trim();
        app.ColorSwapped = ColorSwapCheck.IsChecked == true;
        app.AutostartEnabled = AutostartCheck.IsChecked == true;

        // 입력 클램프 — LoadInto와 같은 ParseClamped 사용 (규칙 한 곳 정의)
        app.PomodoroFocusMinutes = AppSettings.ParseClamped(PomFocusBox.Text,
            AppSettings.PomodoroFocusDefault, AppSettings.PomodoroFocusMin, AppSettings.PomodoroFocusMax);
        app.PomodoroShortBreakMinutes = AppSettings.ParseClamped(PomShortBox.Text,
            AppSettings.PomodoroShortBreakDefault, AppSettings.PomodoroBreakMin, AppSettings.PomodoroBreakMax);
        app.PomodoroLongBreakMinutes = AppSettings.ParseClamped(PomLongBox.Text,
            AppSettings.PomodoroLongBreakDefault, AppSettings.PomodoroBreakMin, AppSettings.PomodoroBreakMax);
        app.PomodoroLongBreakInterval = AppSettings.ParseClamped(PomIntervalBox.Text,
            AppSettings.PomodoroIntervalDefault, AppSettings.PomodoroIntervalMin, AppSettings.PomodoroIntervalMax);
        app.PomodoroAutoStart = PomAutoStartCheck.IsChecked == true;
        app.PomodoroSound = PomSoundCheck.IsChecked == true;
    }

    private void PersistSettings()
    {
        var app = AppSettings.Instance;

        if (app.NotionToken is not null)
        {
            _settings.SaveToken(app.NotionToken);
            // 토큰이 다른 integration으로 바뀌었을 수 있음 — 옛 bot id가 남으면
            // 모든 push가 "남의 수정"으로 보여 매번 충돌 처리된다
            App.Current.InvalidateBotUserId();
        }

        if (app.TargetDbId is not null)
            _settings.Set("target_db_id", app.TargetDbId);

        _settings.Set("category_property_name", app.CategoryPropertyName);
        _settings.Set("color_swapped", app.ColorSwapped ? "true" : "false");
        _settings.Set("autostart_enabled", app.AutostartEnabled ? "true" : "false");

        _settings.Set("pomodoro_focus_min", app.PomodoroFocusMinutes.ToString());
        _settings.Set("pomodoro_short_break_min", app.PomodoroShortBreakMinutes.ToString());
        _settings.Set("pomodoro_long_break_min", app.PomodoroLongBreakMinutes.ToString());
        _settings.Set("pomodoro_long_break_interval", app.PomodoroLongBreakInterval.ToString());
        _settings.Set("pomodoro_auto_start", app.PomodoroAutoStart ? "true" : "false");
        _settings.Set("pomodoro_sound", app.PomodoroSound ? "true" : "false");

        // AppSettings → 서비스 복사 (현재 세션은 스냅샷 유지, 다음 세션부터 적용)
        App.Current.RefreshPomodoroSettings();

        // Autostart registry
        var exePath = Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location;
        if (app.AutostartEnabled)
            StartupManager.Enable(exePath);
        else
            StartupManager.Disable();

        // Resume sync if token was previously paused
        if (app.IsConfigured)
            app.IsSyncPaused = false;
    }

    // Accepts full Notion URL or raw UUID (with or without dashes).
    // Notion URL format: https://www.notion.so/{workspace}/{PageTitle}-{32hex}?v={viewId}
    // The database UUID is always the trailing 32 hex chars of the last path segment.
    private static string? NormalizeDbId(string input)
    {
        if (string.IsNullOrEmpty(input)) return null;

        // Strip query string and trailing slash
        var clean = input.Split('?')[0].TrimEnd('/');
        var last = clean.Split('/').LastOrDefault() ?? clean;

        // Remove all dashes to collapse any UUID-with-dashes or Title-UUID patterns
        var raw = last.Replace("-", "");

        // UUID is exactly 32 hex chars; in Title-UUID format it's the last 32 chars
        if (raw.Length >= 32)
        {
            var candidate = raw[^32..];
            if (IsHex(candidate))
                return candidate;
        }

        return input; // return as-is so the user can see what failed
    }

    private static bool IsHex(string s) =>
        s.Length > 0 && s.All(c => char.IsAsciiHexDigit(c));
}
