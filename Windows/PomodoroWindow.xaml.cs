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
    private string? _wedgeColorKey;
    private Brush _wedgeBrush = MakeFrozenBrush(NotionColorPalette.FallbackWedge);
    private readonly System.Windows.Threading.DispatcherTimer _persistDebounce;

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

        Dial.SetMinutesRequested += (_, m) => _service.SetCustomDuration(m);
        Dial.WheelDeltaRequested += (_, d) => ApplyCustomDelta(d);
        Dial.DragCompleted += (_, _) => PersistCustomMinutes();

        // 휠/키보드 연타가 노치마다 DB를 치지 않도록 디바운스 (SavePosition 어법)
        _persistDebounce = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(600)
        };
        _persistDebounce.Tick += (_, _) => { _persistDebounce.Stop(); PersistCustomMinutes(); };

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

    private static SolidColorBrush MakeFrozenBrush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ── 웨지 색 (스티커 카테고리 → 팔레트, 세션 중 잠금은 App의 Idle 가드가 보장) ──

    public Brush WedgeBrush => _wedgeBrush;

    // 휴식 아웃라인 웨지 stroke — BodyForeground 0.55 (보조 텍스트 토큰과 동일 위계)
    public Brush WedgeStrokeBrush
    {
        get
        {
            var c = ((SolidColorBrush)BodyForeground).Color;
            return MakeFrozenBrush(Color.FromArgb(0x8C, c.R, c.G, c.B)); // 0.55 ≈ 0x8C
        }
    }

    public void SetWedgeColorKey(string? key)
    {
        if (key == _wedgeColorKey) return;
        _wedgeColorKey = key;
        _wedgeBrush = MakeFrozenBrush(NotionColorPalette.Wedge(key));
        Notify(nameof(WedgeBrush));
    }

    // ── 다이얼 바인딩 ──────────────────────────────────────────────────────────

    public double DialFraction => _service.WedgeFraction;
    public bool DialOutlineOnly =>
        _service.Kind == TimerKind.Pomodoro && _service.Mode != PomodoroMode.Focus;
    public bool DialSettable =>
        _service.Kind == TimerKind.Custom && _service.State == PomodoroState.Idle;
    public double DialWedgeOpacity =>
        _service.State == PomodoroState.Paused && !DialOutlineOnly ? 0.45 : 1.0;

    // ── 표시 바인딩 ────────────────────────────────────────────────────────────

    public string TimeText => PomodoroService.FormatRemaining(_service.Remaining);
    public string ModeText => _service.State == PomodoroState.Paused
        ? $"{_service.ModeLabel} · 일시정지"
        : _service.ModeLabel;
    public double TimeOpacity => _service.State == PomodoroState.Paused ? 0.45 : 1.0;
    public bool ResetEnabled => _service.State != PomodoroState.Idle;
    public Visibility PlayGlyphVisibility =>
        _service.State == PomodoroState.Running ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PauseGlyphVisibility =>
        _service.State == PomodoroState.Running ? Visibility.Visible : Visibility.Collapsed;
    public string StartPauseTooltip =>
        _service.State == PomodoroState.Running ? "일시정지 (Space)" : "시작 (Space)";
    public string TimeAutomationName => $"{_service.ModeLabel} 남은 시간 {TimeText}";
    public Visibility DotsVisibility =>
        _service.Kind == TimerKind.Pomodoro ? Visibility.Visible : Visibility.Hidden;
    public Visibility SkipVisibility =>
        _service.Kind == TimerKind.Pomodoro ? Visibility.Visible : Visibility.Hidden;
    public string OverflowText => $"+{_service.OverflowMinutes}분";
    public Visibility OverflowVisibility =>
        _service.OverflowMinutes > 0 ? Visibility.Visible : Visibility.Collapsed;

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
        Notify(nameof(DialFraction));
        Notify(nameof(DialOutlineOnly));
        Notify(nameof(DialSettable));
        Notify(nameof(DialWedgeOpacity));
        Notify(nameof(DotsVisibility));
        Notify(nameof(SkipVisibility));
        Notify(nameof(OverflowText));
        Notify(nameof(OverflowVisibility));
        SyncKindToggle();
        RebuildDots();
    }

    private void SyncKindToggle()
    {
        // one-way from service — ToggleButton 자가 토글로 인한 상태 desync 방지
        KindToggle.IsChecked = _service.Kind == TimerKind.Custom;
        KindToggle.IsEnabled = _service.State == PomodoroState.Idle;
    }

    private void RebuildDots()
    {
        int interval = _service.LongBreakInterval;
        int completed = Math.Min(_service.CompletedFocusCount, interval);
        var key = (completed, interval, Swapped);
        if (key == _dotsKey) return;                   // 매초 재생성 방지
        _dotsKey = key;

        // 간격 1-6 → 8px 도트/6px 간격, 7-12 → 6px/4px
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

    private void KindToggle_Click(object sender, RoutedEventArgs e)
    {
        var target = _service.Kind == TimerKind.Pomodoro ? TimerKind.Custom : TimerKind.Pomodoro;
        _service.SwitchKind(target);                   // Idle 가드 — no-op일 수 있음
        SyncKindToggle();                              // 자가 토글 복원 (실제 상태 기준)
    }

    private void ApplyCustomDelta(int delta)
    {
        _service.SetCustomDuration(DialMath.ClampMinutes(_service.CustomMinutes + delta));
        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    private void PersistCustomMinutes()
    {
        AppSettings.Instance.PomodoroCustomMinutes = _service.CustomMinutes;
        try { _settingsRepo.Set("pomodoro_custom_min", _service.CustomMinutes.ToString()); }
        catch { }
    }

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
        // 커스텀 + Idle: 방향키/페이지키로 시간 조정 (방향키 포커스 내비게이션 충돌 방지)
        if (DialSettable)
        {
            int delta = e.Key switch
            {
                Key.Right or Key.Up => 1,
                Key.Left or Key.Down => -1,
                Key.PageUp => 5,
                Key.PageDown => -5,
                _ => 0,
            };
            if (delta != 0)
            {
                ApplyCustomDelta(delta);
                e.Handled = true;
                return;
            }
        }

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
            Notify(nameof(WedgeStrokeBrush));
            _dotsKey = (-1, -1, false);                // 테마 전환 시 도트 브러시 재생성 강제
            RebuildDots();
        }
    }
}
