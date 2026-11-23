using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using Microsoft.Data.Sqlite;
using Noticker.Data;
using Noticker.Infrastructure;
using Noticker.Models;
using Noticker.Services;
using Noticker.Sync;
using Noticker.Windows;

namespace Noticker;

// App partial — 포모도로 서비스/타이머/위젯 소유와 세션 종료 알림. (App.xaml.cs에서 분리, 동작 변경 없음)
public partial class App
{
    // 포모도로 — App이 composition root (서비스/타이머 소유, 알림 side-effect 수행)
    private PomodoroService? _pomodoro;
    private System.Windows.Threading.DispatcherTimer? _pomodoroTimer;
    private PomodoroWindow? _pomodoroWindow;
    private string? _pomodoroWedgeColorKey;            // 노션 색 이름 — Idle일 때만 갱신 (색 잠금)

    private void InitPomodoro()
    {
        // 시간 소스는 UtcNow — Now 금지 (DST/시계 변경 시 타이머 동결 방지)
        _pomodoro = new PomodoroService(() => DateTime.UtcNow);
        // 프로퍼티 set — SetCustomDuration의 Custom+Idle 가드를 우회하는 초기 로드 경로
        _pomodoro.CustomMinutes = AppSettings.Instance.PomodoroCustomMinutes;
        RefreshPomodoroSettings();
        _pomodoro.Changed += OnPomodoroChanged;
        _pomodoro.SessionEnded += OnPomodoroSessionEnded;

        // State == Running일 때만 구동 (OnPomodoroChanged가 start/stop 동기화)
        _pomodoroTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _pomodoroTimer.Tick += (_, _) =>
        {
            if (IsShuttingDown) return;
            _pomodoro!.Tick();
        };
    }

    // SettingsWindow 저장 후 호출 — AppSettings → 서비스로 복사 (세션 중 변경은 다음 세션부터)
    // 주의: CustomMinutes는 여기서 건드리지 않음 — 다이얼 입력만이 변경 (설계 불간섭 invariant)
    public void RefreshPomodoroSettings()
    {
        if (_pomodoro is null) return;
        var s = AppSettings.Instance;
        _pomodoro.FocusMinutes = s.PomodoroFocusMinutes;
        _pomodoro.ShortBreakMinutes = s.PomodoroShortBreakMinutes;
        _pomodoro.LongBreakMinutes = s.PomodoroLongBreakMinutes;
        _pomodoro.LongBreakInterval = s.PomodoroLongBreakInterval;
        _pomodoro.AutoStart = s.PomodoroAutoStart;
    }

    public void OpenPomodoro(string? colorKey = null)
    {
        if (_pomodoro is null) return;
        // 색 잠금: Idle일 때만 키 갱신. null(트레이/무카테고리) = 테마 바색(흑/백) 웨지
        if (_pomodoro.State == PomodoroState.Idle)
            _pomodoroWedgeColorKey = colorKey;
        _pomodoroWindow ??= new PomodoroWindow(_pomodoro, SettingsRepo!);
        // ??= 캐시 창에는 매 호출 push — 생성자 전달만으론 색이 첫 오픈에 고정됨
        _pomodoroWindow.SetWedgeColorKey(_pomodoroWedgeColorKey);
        _pomodoroWindow.Show();      // hide 패턴이라 OfType+Activate만으로는 안 보임
        _pomodoroWindow.Activate();  // 사용자가 직접 연 경우 — 활성화가 맞음
    }

    public void ShowPomodoroHideNotice()
    {
        if (IsShuttingDown) return;
        ShowBalloon(4000, "Noticker",
            "타이머는 백그라운드에서 계속 실행 중입니다", ToolTipIcon.None);
    }

    private void OnPomodoroChanged(object? sender, EventArgs e)
    {
        if (IsShuttingDown) return;
        bool running = _pomodoro!.State == PomodoroState.Running;
        if (running && !_pomodoroTimer!.IsEnabled) _pomodoroTimer.Start();
        else if (!running && _pomodoroTimer!.IsEnabled) _pomodoroTimer.Stop();
        UpdateTrayTooltip();
        UpdateTrayWedgeIcon();
    }

    private void OnPomodoroSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        if (IsShuttingDown) return;

        // 1순위: 위젯 자동 표시 (보장 채널 — ShowActivated=False라 포커스 안 뺏음)
        if (_pomodoroWindow is not null && !_pomodoroWindow.IsVisible)
            _pomodoroWindow.Show();

        // 2순위: 사운드 (Asterisk — 보상의 순간에 경고음은 부적합)
        if (AppSettings.Instance.PomodoroSound)
        {
            try { System.Media.SystemSounds.Asterisk.Play(); }
            catch { /* 장치 없음 등 — 무시 */ }
        }

        // 3순위: 트레이 풍선 (Win11 집중 지원이 억제할 수 있는 보조 채널)
        // Kind 최우선 분기 — 커스텀 종료에 포모도로 문구 금지 (EndedMode는 Custom에서 의미 없음)
        var msg = e.Kind == TimerKind.Custom
            ? $"타이머 끝 — {e.EndedMinutes}분 경과"
            : e.EndedMode == PomodoroMode.Focus
                ? $"집중 끝 — {(e.NextMode == PomodoroMode.LongBreak ? "긴" : "짧은")} 휴식하세요"
                : "휴식 끝 — 다시 집중할 시간";
        ShowBalloon(5000, "Noticker — 포모도로", msg, ToolTipIcon.None);
    }
}
