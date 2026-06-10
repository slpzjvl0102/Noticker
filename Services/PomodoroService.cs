namespace Noticker.Services;

// 순수 포모도로 상태머신 — WPF/WinForms 참조 금지 (테스트 가능성의 핵심).
// 시간은 Func<DateTime>로 주입 (DateTime.UtcNow — Now 금지: DST/시계 변경 내성).
//
//             Start            Pause
//   ┌──────┐ ───────▶ ┌─────────┐ ───────▶ ┌────────┐
//   │ Idle │          │ Running │          │ Paused │
//   │      │ ◀─────── │         │ ◀─────── │        │
//   └──────┘  Reset   └─────────┘  Resume  └────────┘
//
//   Running + remaining≤0 → 세션 완료(이벤트 1회) → autoStart ? Running(next) : Idle(next)
//   Skip: 모든 상태 → Idle(next), 완료 카운트 미증가 (긴 휴식은 완료로만 획득)
//   슬립 다중 경계: 첫 경계만 종료 처리, 다음 세션은 wake 시점 기준 (cascade 없음)
public enum PomodoroMode { Focus, ShortBreak, LongBreak }
public enum PomodoroState { Idle, Running, Paused }

public class SessionEndedEventArgs : EventArgs
{
    public PomodoroMode EndedMode { get; }
    public PomodoroMode NextMode { get; }
    public int CompletedFocusCount { get; }

    public SessionEndedEventArgs(PomodoroMode endedMode, PomodoroMode nextMode, int completedFocusCount)
    {
        EndedMode = endedMode;
        NextMode = nextMode;
        CompletedFocusCount = completedFocusCount;
    }
}

public class PomodoroService
{
    private readonly Func<DateTime> _now;
    private DateTime _endTime;            // Running일 때만 유효
    private TimeSpan _pausedRemaining;    // Paused일 때만 유효

    public PomodoroService(Func<DateTime> now) => _now = now;

    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int LongBreakInterval { get; set; } = 4;
    public bool AutoStart { get; set; }

    public PomodoroMode Mode { get; private set; } = PomodoroMode.Focus;
    public PomodoroState State { get; private set; } = PomodoroState.Idle;
    public int CompletedFocusCount { get; private set; }

    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public TimeSpan Remaining => State switch
    {
        PomodoroState.Running => ClampZero(_endTime - _now()),
        PomodoroState.Paused => _pausedRemaining,
        _ => CurrentDuration(Mode),
    };

    public string ModeLabel => Mode switch
    {
        PomodoroMode.Focus => "집중",
        PomodoroMode.ShortBreak => "짧은 휴식",
        _ => "긴 휴식",
    };

    public string TrayTooltip
    {
        get
        {
            var text = State switch
            {
                PomodoroState.Running => $"Noticker — {ModeLabel} {FormatRemaining(Remaining)}",
                PomodoroState.Paused => $"Noticker — 일시정지 {FormatRemaining(Remaining)}",
                _ => "Noticker",
            };
            // NotifyIcon.Text 한도 방어 (초과 시 ArgumentOutOfRangeException)
            return text.Length <= 63 ? text : text[..63];
        }
    }

    public static string FormatRemaining(TimeSpan t) =>
        $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";

    public void Start()
    {
        if (State != PomodoroState.Idle) return;
        _endTime = _now() + CurrentDuration(Mode);   // 세션 시작 시 지속시간 스냅샷
        State = PomodoroState.Running;
    }

    public void Pause()
    {
        if (State != PomodoroState.Running) return;
        _pausedRemaining = ClampZero(_endTime - _now());
        State = PomodoroState.Paused;
    }

    public void Resume()
    {
        if (State != PomodoroState.Paused) return;
        _endTime = _now() + _pausedRemaining;
        State = PomodoroState.Running;
    }

    public void Reset()
    {
        if (State == PomodoroState.Idle) return;
        State = PomodoroState.Idle;                  // 모드·완료 카운트 유지
    }

    public void Skip()
    {
        if (Mode == PomodoroMode.LongBreak)
            CompletedFocusCount = 0;                 // 긴 휴식 이탈 = 사이클 종료
        Mode = NextMode();                           // 집중 스킵은 카운트 미증가
        State = PomodoroState.Idle;
    }

    public void Tick()
    {
        if (State != PomodoroState.Running) return;
        if (_now() < _endTime) return;

        // 세션 완료 — 슬립으로 여러 경계를 넘겼어도 첫 경계만 처리 (알림 1회)
        var ended = Mode;
        if (ended == PomodoroMode.Focus) CompletedFocusCount++;
        if (ended == PomodoroMode.LongBreak) CompletedFocusCount = 0;
        Mode = NextMode();
        SessionEnded?.Invoke(this, new SessionEndedEventArgs(ended, Mode, CompletedFocusCount));

        if (AutoStart)
            _endTime = _now() + CurrentDuration(Mode);  // wake/완료 시점 기준
        else
            State = PomodoroState.Idle;
    }

    private PomodoroMode NextMode() => Mode switch
    {
        PomodoroMode.Focus => CompletedFocusCount >= LongBreakInterval
            ? PomodoroMode.LongBreak
            : PomodoroMode.ShortBreak,
        _ => PomodoroMode.Focus,
    };

    private TimeSpan CurrentDuration(PomodoroMode mode) => mode switch
    {
        PomodoroMode.Focus => TimeSpan.FromMinutes(FocusMinutes),
        PomodoroMode.ShortBreak => TimeSpan.FromMinutes(ShortBreakMinutes),
        _ => TimeSpan.FromMinutes(LongBreakMinutes),
    };

    private static TimeSpan ClampZero(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
