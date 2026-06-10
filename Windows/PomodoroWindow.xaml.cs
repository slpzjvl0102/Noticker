using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using Noticker.Data;
using Noticker.Infrastructure;
using Noticker.Models;
using Noticker.Services;

namespace Noticker.Windows;

// 순수 뷰 — PomodoroService(App 소유)의 이벤트를 구독하고 명령만 보낸다.
// X = 숨기기 (타이머는 계속 동작), ShowActivated=False (자동 표시 시 포커스 스틸링 방지)
public partial class PomodoroWindow : Window, INotifyPropertyChanged
{
    private readonly PomodoroService _service;
    private readonly SettingsRepository _settingsRepo;
    private bool _loading = true;
    private bool _hideNoticeShown;                     // 앱 실행당 1회 안내
    private (int Completed, int Interval, bool Swapped) _dotsKey = (-1, -1, false);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public PomodoroWindow(PomodoroService service, SettingsRepository settingsRepo)
    {
        _service = service;
        _settingsRepo = settingsRepo;

        InitializeComponent();
        DataContext = this;

        AppSettings.Instance.PropertyChanged += OnAppSettingsChanged;
        _service.Changed += OnServiceChanged;
        _service.SessionEnded += OnSessionEnded;

        Topmost = AppSettings.Instance.PomodoroAlwaysOnTop;
        PinButton.IsChecked = Topmost;

        RestorePosition();
        RefreshAll();
        _loading = false;
    }

    // ── Colors (ColorSwapped 전역 스왑 — 하드코딩 hex 금지) ────────────────────

    private bool Swapped => AppSettings.Instance.ColorSwapped;
    private static readonly SolidColorBrush _darkGray = new(Color.FromRgb(0x33, 0x33, 0x33));

    public Brush TitleBackground => Swapped ? Brushes.White : _darkGray;
    public Brush TitleForeground => Swapped ? Brushes.Black : Brushes.White;
    public Brush BodyBackground => Swapped ? _darkGray : Brushes.White;
    public Brush BodyForeground => Swapped ? Brushes.White : Brushes.Black;

    // ── 표시 바인딩 ────────────────────────────────────────────────────────────

    public string TimeText => PomodoroService.FormatRemaining(_service.Remaining);
    public string ModeText => _service.ModeLabel;
    public double TimeOpacity => _service.State == PomodoroState.Paused ? 0.45 : 1.0;
    public bool ResetEnabled => _service.State != PomodoroState.Idle;
    public Visibility PlayGlyphVisibility =>
        _service.State == PomodoroState.Running ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PauseGlyphVisibility =>
        _service.State == PomodoroState.Running ? Visibility.Visible : Visibility.Collapsed;
    public string StartPauseTooltip =>
        _service.State == PomodoroState.Running ? "일시정지 (Space)" : "시작 (Space)";
    public string TimeAutomationName => $"{_service.ModeLabel} 남은 시간 {TimeText}";

    private void OnServiceChanged(object? sender, EventArgs e) => RefreshAll();

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        Notify(nameof(TimeText));
        Notify(nameof(ModeText));
        Notify(nameof(TimeOpacity));
        Notify(nameof(ResetEnabled));
        Notify(nameof(PlayGlyphVisibility));
        Notify(nameof(PauseGlyphVisibility));
        Notify(nameof(StartPauseTooltip));
        Notify(nameof(TimeAutomationName));
        RebuildDots();
    }

    private void RebuildDots()
    {
        int interval = _service.LongBreakInterval;
        int completed = Math.Min(_service.CompletedFocusCount, interval);
        var key = (completed, interval, Swapped);
        if (key == _dotsKey) return;                   // 매초 재생성 방지
        _dotsKey = key;

        // 간격 1-6 → 8px 도트/6px 간격, 7-12 → 6px/4px (180px 폭 내 수용)
        double size = interval <= 6 ? 8 : 6;
        double gap = interval <= 6 ? 6 : 4;

        DotsPanel.Children.Clear();
        for (int i = 0; i < interval; i++)
        {
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Margin = new Thickness(i == 0 ? 0 : gap, 0, 0, 0),
            };
            if (i < completed)
            {
                dot.Fill = BodyForeground;
            }
            else
            {
                dot.Stroke = BodyForeground;
                dot.StrokeThickness = 1;
                dot.Opacity = 0.3;
            }
            DotsPanel.Children.Add(dot);
        }
        System.Windows.Automation.AutomationProperties.SetName(
            DotsPanel, $"세션 {completed}/{interval} 완료");
    }

    // ── 명령 ───────────────────────────────────────────────────────────────────

    private void StartPause_Click(object sender, RoutedEventArgs e) => ToggleStartPause();

    private void ToggleStartPause()
    {
        switch (_service.State)
        {
            case PomodoroState.Running: _service.Pause(); break;
            case PomodoroState.Paused: _service.Resume(); break;
            default: _service.Start(); break;
        }
    }

    private void Reset_Click(object sender, RoutedEventArgs e) => _service.Reset();

    private void Skip_Click(object sender, RoutedEventArgs e) => _service.Skip();

    private void PinButton_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Topmost = PinButton.IsChecked == true;
        AppSettings.Instance.PomodoroAlwaysOnTop = Topmost;
        try { _settingsRepo.Set("pomodoro_always_on_top", Topmost ? "true" : "false"); }
        catch { }
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            ToggleStartPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();                                   // OnClosing이 숨기기로 전환
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        DragMove();
        SavePosition();
    }

    // ── 창 생명주기 (hide 패턴) ─────────────────────────────────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Current.IsShuttingDown)
        {
            AppSettings.Instance.PropertyChanged -= OnAppSettingsChanged;
            _service.Changed -= OnServiceChanged;
            _service.SessionEnded -= OnSessionEnded;
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        Hide();
        if (_service.State == PomodoroState.Running && !_hideNoticeShown)
        {
            _hideNoticeShown = true;
            App.Current.ShowPomodoroHideNotice();
        }
    }

    // ── 위치 영속 ──────────────────────────────────────────────────────────────

    private void RestorePosition()
    {
        var primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
        if (int.TryParse(_settingsRepo.Get("pomodoro_window_x"), out var rx) &&
            int.TryParse(_settingsRepo.Get("pomodoro_window_y"), out var ry))
        {
            var screens = System.Windows.Forms.Screen.AllScreens
                .Select(sc => (sc.DeviceName, sc.WorkingArea)).ToList();
            var wa = ScreenPlacement.SelectWorkingArea(
                screens, primary, _settingsRepo.Get("pomodoro_monitor"));
            var (x, y) = ScreenPlacement.ClampToArea(wa, rx, ry, (int)Width, (int)Height);
            Left = x;
            Top = y;
        }
        else
        {
            // 첫 실행: 작업 영역 우하단 (트레이 근처)
            Left = primary.Right - Width - 16;
            Top = primary.Bottom - Height - 16;
        }
    }

    private void SavePosition()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle)
            ?? System.Windows.Forms.Screen.PrimaryScreen!;
        try
        {
            _settingsRepo.Set("pomodoro_monitor", screen.DeviceName);
            _settingsRepo.Set("pomodoro_window_x", ((int)(Left - screen.WorkingArea.Left)).ToString());
            _settingsRepo.Set("pomodoro_window_y", ((int)(Top - screen.WorkingArea.Top)).ToString());
        }
        catch { }
    }

    private void OnAppSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.ColorSwapped))
        {
            Notify(nameof(TitleBackground));
            Notify(nameof(TitleForeground));
            Notify(nameof(BodyBackground));
            Notify(nameof(BodyForeground));
            RebuildDots();
        }
    }
}
