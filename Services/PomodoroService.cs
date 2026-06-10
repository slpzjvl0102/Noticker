using Noticker.Infrastructure;

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

// Custom = 단발 카운트다운: 사이클(Mode/완료 카운트) 비파괴 — Pomodoro 복귀 시 멈춘 자리에서 재개
public enum TimerKind { Pomodoro, Custom }

public class SessionEndedEventArgs : EventArgs
{
    // Custom 종료 시 EndedMode/NextMode는 보존된 Mode 그대로 (의미 없음 — 소비자는 Kind 먼저 분기)
    public PomodoroMode EndedMode { get; }
    public PomodoroMode NextMode { get; }
    public int CompletedFocusCount { get; }
    public TimerKind Kind { get; }
    public int EndedMinutes { get; }              // 종료된 세션의 시작 시점 총 분

    public SessionEndedEventArgs(PomodoroMode endedMode, PomodoroMode nextMode, int completedFocusCount,
        TimerKind kind, int endedMinutes)
    {
        EndedMode = endedMode;
        NextMode = nextMode;
        CompletedFocusCount = completedFocusCount;
        Kind = kind;
        EndedMinutes = endedMinutes;
    }
}

public class PomodoroService
{
    private readonly Func<DateTime> _now;
    private DateTime _endTime;            // Running일 때만 유효
    private TimeSpan _pausedRemaining;    // Paused일 때만 유효
    private int _sessionMinutes;          // 세션 시작 시 총 분 스냅샷 — SessionEnded.EndedMinutes 소스

    public PomodoroService(Func<DateTime> now) => _now = now;

    public int FocusMinutes { get; set; } = 25;
    public int ShortBreakMinutes { get; set; } = 5;
    public int LongBreakMinutes { get; set; } = 15;
    public int LongBreakInterval { get; set; } = 4;
    public bool AutoStart { get; set; }

    public PomodoroMode Mode { get; private set; } = PomodoroMode.Focus;
    public PomodoroState State { get; private set; } = PomodoroState.Idle;
    public int CompletedFocusCount { get; private set; }

    public TimerKind Kind { get; private set; } = TimerKind.Pomodoro;

    // set 가능: App 초기 로드 경로 — SetCustomDuration의 Custom+Idle 가드에 막히지 않도록 분리
    public int CustomMinutes { get; set; } = 30;

    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    // 상태가 실제로 변한 모든 명령 + Running 중 매 tick에 발화 — UI/툴팁 갱신 신호
    public event EventHandler? Changed;

    public TimeSpan Remaining => State switch
    {
        PomodoroState.Running => ClampZero(_endTime - _now()),
        PomodoroState.Paused => _pausedRemaining,
        _ => Kind == TimerKind.Custom ? TimeSpan.FromMinutes(CustomMinutes) : CurrentDuration(Mode),
    };

    public string ModeLabel => Kind == TimerKind.Custom ? "타이머" : Mode switch
    {
        PomodoroMode.Focus => "집중",
        PomodoroMode.ShortBreak => "짧은 휴식",
        _ => "긴 휴식",
    };

    // 60분 시계면 웨지 — Running/Paused/Idle 전부 Remaining 경유 (Idle = 다음 세션 프리뷰)
    public double WedgeFraction => Math.Min(Remaining.TotalMinutes, 60.0) / 60.0;

    // >60분 세션의 "+N분" 표기 (89:59 → 30, 60:00 정각 → 0)
    public int OverflowMinutes => Math.Max(0, (int)Math.Ceiling(Remaining.TotalMinutes) - 60);

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
        var duration = Kind == TimerKind.Custom
            ? TimeSpan.FromMinutes(CustomMinutes)
            : CurrentDuration(Mode);
        _sessionMinutes = (int)duration.TotalMinutes;
        _endTime = _now() + duration;                // 세션 시작 시 지속시간 스냅샷
        State = PomodoroState.Running;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SwitchKind(TimerKind kind)
    {
        if (State != PomodoroState.Idle) return;
        if (kind == Kind) return;                    // 같은 kind 재호출 — Changed 미발화
        Kind = kind;                                 // Mode/완료 카운트 비파괴 — 사이클 보존
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetCustomDuration(int minutes)
    {
        if (Kind != TimerKind.Custom || State != PomodoroState.Idle) return;
        var clamped = DialMath.ClampMinutes(minutes);
        if (clamped == CustomMinutes) return;        // 값이 실제로 변할 때만 발화
        CustomMinutes = clamped;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Pause()
    {
        if (State != PomodoroState.Running) return;
        _pausedRemaining = ClampZero(_endTime - _now());
        State = PomodoroState.Paused;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Resume()
    {
        if (State != PomodoroState.Paused) return;
        _endTime = _now() + _pausedRemaining;
        State = PomodoroState.Running;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Reset()
    {
        if (State == PomodoroState.Idle) return;
        State = PomodoroState.Idle;                  // 모드·완료 카운트 유지
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Skip()
    {
        if (Kind == TimerKind.Custom) return;        // 단발 카운트다운에 스킵 없음 — 사이클 비파괴
        if (Mode == PomodoroMode.LongBreak)
            CompletedFocusCount = 0;                 // 긴 휴식 이탈 = 사이클 종료
        Mode = NextMode();                           // 집중 스킵은 카운트 미증가
        State = PomodoroState.Idle;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Tick()
    {
        if (State != PomodoroState.Running) return;
        if (_now() < _endTime)
        {
            Changed?.Invoke(this, EventArgs.Empty);  // 카운트다운 표시 갱신
            return;
        }

        // 세션 완료 — 슬립으로 여러 경계를 넘겼어도 첫 경계만 처리 (알림 1회)
        if (Kind == TimerKind.Custom)
        {
            // 단발 종료: 카운트/NextMode/AutoStart 전부 스킵 — Mode는 보존된 값 그대로 전달
            SessionEnded?.Invoke(this, new SessionEndedEventArgs(Mode, Mode, CompletedFocusCount, Kind, _sessionMinutes));
            State = PomodoroState.Idle;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        var ended = Mode;
        if (ended == PomodoroMode.Focus) CompletedFocusCount++;
        if (ended == PomodoroMode.LongBreak) CompletedFocusCount = 0;
        Mode = NextMode();
        SessionEnded?.Invoke(this, new SessionEndedEventArgs(ended, Mode, CompletedFocusCount, Kind, _sessionMinutes));

        if (AutoStart)
        {
            _sessionMinutes = (int)CurrentDuration(Mode).TotalMinutes;  // 다음 세션 스냅샷 (Start 미경유)
            _endTime = _now() + CurrentDuration(Mode);  // wake/완료 시점 기준
        }
        else
            State = PomodoroState.Idle;
        Changed?.Invoke(this, EventArgs.Empty);
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
