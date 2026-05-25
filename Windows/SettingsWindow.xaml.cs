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
    }

    private void PersistSettings()
    {
        var app = AppSettings.Instance;

        if (app.NotionToken is not null)
            _settings.SaveToken(app.NotionToken);

        if (app.TargetDbId is not null)
            _settings.Set("target_db_id", app.TargetDbId);

        _settings.Set("category_property_name", app.CategoryPropertyName);
        _settings.Set("color_swapped", app.ColorSwapped ? "true" : "false");
        _settings.Set("autostart_enabled", app.AutostartEnabled ? "true" : "false");

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
