using System.Reflection;
using System.Windows;
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

        ConnectionSummary.Text = app.IsConfigured
            ? $"연결됨: {app.NotionDbTitle ?? app.TargetDbId}"
            : "연결 안 됨";
        ColorSwapCheck.IsChecked = app.ColorSwapped;
        AutostartCheck.IsChecked = app.AutostartEnabled;
        SelectHotkeyItem(app.HotkeyPreset);

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

    private void SelectHotkeyItem(string presetKey)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in HotkeyCombo.Items)
            if ((string)item.Tag == presetKey) { HotkeyCombo.SelectedItem = item; return; }
        HotkeyCombo.SelectedIndex = 0;   // 잡값 — 기본 Ctrl+Alt+N (HotkeyPresets.Resolve 폴백과 일치)
    }

    private void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        // 시작 시 떠 있던 비모달 위저드가 있으면 닫는다 — 위저드 2개가 서로
        // 다른 설정을 마지막-저장-승으로 덮어쓰는 혼란 방지
        foreach (var w in System.Windows.Application.Current.Windows.OfType<OnboardingWindow>().ToList())
            w.Close();

        var wizard = new OnboardingWindow(_settings, _client) { Owner = this };
        wizard.ShowDialog();
        LoadCurrentValues();   // 연결 요약 갱신
    }

    private async void RefreshCatButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCatButton.IsEnabled = false;
        CatStatusText.Text = "불러오는 중…";

        try
        {
            var count = await App.Current.RefreshCategoryOptionsAsync();
            CatStatusText.Text = $"{count}개 옵션 새로고침 완료";
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
        if (!ApplyHotkeySetting()) return;   // 실패 — 창 유지, 인라인 에러 표시됨
        ApplyToAppSettings();
        PersistSettings();
        Close();
    }

    // 단축키 적용 + 실패 시 이전 값 복원 (스펙 §4 저장 흐름). 성공 여부 반환.
    // 실패 시 다른 설정도 저장되지 않는다 — 사용자가 조합을 고치고 다시 저장
    private bool ApplyHotkeySetting()
    {
        var app = AppSettings.Instance;
        var old = app.HotkeyPreset;
        var selected = (string)((System.Windows.Controls.ComboBoxItem)HotkeyCombo.SelectedItem).Tag;
        if (selected == old) return true;    // 변경 없음 — 재등록 불필요

        app.HotkeyPreset = selected;
        if (App.Current.ApplyHotkey())
        {
            HotkeyStatusText.Visibility = Visibility.Collapsed;
            return true;
        }

        app.HotkeyPreset = old;
        App.Current.ApplyHotkey();           // 직전까지 들고 있던 조합 재등록 — best effort
        SelectHotkeyItem(old);
        HotkeyStatusText.Text = "이 조합은 다른 앱이 사용 중입니다.";
        HotkeyStatusText.Visibility = Visibility.Visible;
        return false;
    }

    private void ApplyToAppSettings()
    {
        var app = AppSettings.Instance;

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

        _settings.Set("color_swapped", app.ColorSwapped ? "true" : "false");
        _settings.Set("autostart_enabled", app.AutostartEnabled ? "true" : "false");
        _settings.Set("hotkey_preset", app.HotkeyPreset);

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

        // 저장은 명시적 사용자 행동이므로 sync 일시정지를 해제한다 — 401로 멈춘 뒤
        // 위저드로 재연결하지 않고 설정만 저장해도 재시도가 다시 돌게.
        if (app.IsConfigured)
            app.IsSyncPaused = false;
    }

}
