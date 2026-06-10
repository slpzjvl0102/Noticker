using Noticker.Services;

namespace Noticker.Tests;

public class PomodoroServiceTests
{
    private DateTime _now = new(2026, 6, 10, 0, 0, 0, DateTimeKind.Utc);

    private PomodoroService NewService(
        int focus = 25, int shortBreak = 5, int longBreak = 15,
        int interval = 4, bool autoStart = false)
        => new(() => _now)
        {
            FocusMinutes = focus,
            ShortBreakMinutes = shortBreak,
            LongBreakMinutes = longBreak,
            LongBreakInterval = interval,
            AutoStart = autoStart,
        };

    private void Advance(TimeSpan t) => _now += t;

    // 한 세션을 완주시키는 헬퍼: Running 상태에서 종료 경계 직후로 점프 + Tick
    private void CompleteSession(PomodoroService s, double minutes)
    {
        Advance(TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(1));
        s.Tick();
    }

    // ── 상태 전이 ──────────────────────────────────────────────────────────────

    [Fact]
    public void Start_FromIdle_TransitionsToRunning()
    {
        var s = NewService();
        s.Start();
        Assert.Equal(PomodoroState.Running, s.State);
        Assert.Equal(PomodoroMode.Focus, s.Mode);
        Assert.Equal(TimeSpan.FromMinutes(25), s.Remaining);
    }

    [Fact]
    public void Pause_WhileRunning_TransitionsToPaused()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(10));
        s.Pause();
        Assert.Equal(PomodoroState.Paused, s.State);
        Assert.Equal(TimeSpan.FromMinutes(15), s.Remaining);
    }

    [Fact]
    public void Resume_FromPaused_TransitionsToRunning()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(10));
        s.Pause();
        s.Resume();
        Assert.Equal(PomodoroState.Running, s.State);
        Assert.Equal(TimeSpan.FromMinutes(15), s.Remaining);
    }

    [Fact]
    public void Reset_RestoresFullDuration_KeepsCount()
    {
        var s = NewService();
        s.Start();
        CompleteSession(s, 25);              // 집중 1회 완료 → 짧은 휴식 Idle
        s.Start();                            // 휴식 시작
        Advance(TimeSpan.FromMinutes(2));
        s.Reset();
        Assert.Equal(PomodoroState.Idle, s.State);
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Remaining);
        Assert.Equal(1, s.CompletedFocusCount);
    }

    [Fact]
    public void Reset_WhileRunning_StopsToIdle()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(10));
        s.Reset();
        Assert.Equal(PomodoroState.Idle, s.State);
        Assert.Equal(TimeSpan.FromMinutes(25), s.Remaining);
    }

    [Fact]
    public void InvalidTransitions_AreNoOps()
    {
        var s = NewService();
        int events = 0;
        s.SessionEnded += (_, _) => events++;

        s.Pause();                            // Idle에서 Pause → no-op
        Assert.Equal(PomodoroState.Idle, s.State);
        s.Resume();                           // Idle에서 Resume → no-op
        Assert.Equal(PomodoroState.Idle, s.State);

        s.Start();
        s.Start();                            // Running에서 Start → no-op
        Assert.Equal(PomodoroState.Running, s.State);
        s.Resume();                           // Running에서 Resume → no-op
        Assert.Equal(PomodoroState.Running, s.State);
        Assert.Equal(0, events);
    }

    [Fact]
    public void Tick_WhileIdleOrPaused_NoOp()
    {
        var s = NewService();
        s.Tick();                             // Idle
        Assert.Equal(PomodoroState.Idle, s.State);

        s.Start();
        s.Pause();
        Advance(TimeSpan.FromHours(2));
        s.Tick();                             // Paused — 시간 점프에도 전이 없음
        Assert.Equal(PomodoroState.Paused, s.State);
        Assert.Equal(TimeSpan.FromMinutes(25), s.Remaining);
    }

    // ── 사이클 ─────────────────────────────────────────────────────────────────

    [Fact]
    public void FocusEnd_TransitionsToShortBreak()
    {
        var s = NewService();
        s.Start();
        CompleteSession(s, 25);
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
        Assert.Equal(1, s.CompletedFocusCount);
    }

    [Fact]
    public void FourthFocusEnd_TransitionsToLongBreak()
    {
        var s = NewService();
        for (int i = 0; i < 3; i++)
        {
            s.Start(); CompleteSession(s, 25);    // 집중 완료
            s.Start(); CompleteSession(s, 5);     // 짧은 휴식 완료
        }
        s.Start(); CompleteSession(s, 25);        // 4번째 집중 완료
        Assert.Equal(PomodoroMode.LongBreak, s.Mode);
        Assert.Equal(4, s.CompletedFocusCount);
    }

    [Fact]
    public void LongBreakEnd_ResetsCompletedCount_NextIsFocus()
    {
        var s = NewService(interval: 1);
        s.Start(); CompleteSession(s, 25);        // 집중 1 → 긴 휴식 (간격 1)
        Assert.Equal(PomodoroMode.LongBreak, s.Mode);
        s.Start(); CompleteSession(s, 15);        // 긴 휴식 완료
        Assert.Equal(PomodoroMode.Focus, s.Mode);
        Assert.Equal(0, s.CompletedFocusCount);
    }

    [Fact]
    public void Skip_DuringFocus_DoesNotIncrementCount()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(5));
        s.Skip();
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
        Assert.Equal(PomodoroState.Idle, s.State);
        Assert.Equal(0, s.CompletedFocusCount);
    }

    [Fact]
    public void Skip_DuringBreak_AdvancesToFocus()
    {
        var s = NewService();
        s.Start(); CompleteSession(s, 25);
        s.Skip();                                 // 휴식 스킵 (Idle 상태에서도 동작)
        Assert.Equal(PomodoroMode.Focus, s.Mode);
        Assert.Equal(PomodoroState.Idle, s.State);
    }

    [Fact]
    public void SkippedFocus_DoesNotTriggerLongBreak()
    {
        var s = NewService();
        for (int i = 0; i < 3; i++)
        {
            s.Start(); CompleteSession(s, 25);
            s.Start(); CompleteSession(s, 5);
        }
        // 완료 3 + 4번째 집중을 스킵 → 완료 카운트 3 < 4 → 짧은 휴식
        s.Start();
        s.Skip();
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
        Assert.Equal(3, s.CompletedFocusCount);
    }

    [Fact]
    public void Skip_DuringLongBreak_ResetsCount()
    {
        var s = NewService(interval: 1);
        s.Start(); CompleteSession(s, 25);        // → 긴 휴식
        s.Skip();                                 // 긴 휴식 스킵 = 사이클 종료
        Assert.Equal(PomodoroMode.Focus, s.Mode);
        Assert.Equal(0, s.CompletedFocusCount);
    }

    [Fact]
    public void IntervalOne_EveryFocusEndsInLongBreak()
    {
        var s = NewService(interval: 1);
        s.Start(); CompleteSession(s, 25);
        Assert.Equal(PomodoroMode.LongBreak, s.Mode);
    }

    [Fact]
    public void IntervalShrunkMidCycle_NextBreakIsLong()
    {
        var s = NewService(interval: 4);
        for (int i = 0; i < 2; i++)
        {
            s.Start(); CompleteSession(s, 25);
            s.Start(); CompleteSession(s, 5);
        }
        s.LongBreakInterval = 2;                  // 완료 2 상태에서 간격 4→2
        s.Start(); CompleteSession(s, 25);        // 완료 3 ≥ 2 → 긴 휴식
        Assert.Equal(PomodoroMode.LongBreak, s.Mode);
    }

    // ── 시간 / 경계 ────────────────────────────────────────────────────────────

    [Fact]
    public void Resume_AfterLongPause_RemainingUnchanged()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(15));
        s.Pause();
        Advance(TimeSpan.FromHours(3));           // 일시정지 상태로 3시간 방치
        s.Resume();
        Assert.Equal(TimeSpan.FromMinutes(10), s.Remaining);
    }

    [Fact]
    public void Tick_PastEnd_FiresSessionEndedExactlyOnce()
    {
        var s = NewService();
        int events = 0;
        s.SessionEnded += (_, _) => events++;
        s.Start();
        Advance(TimeSpan.FromMinutes(26));
        s.Tick();
        s.Tick();                                 // 반복 tick — 추가 이벤트 없어야 함
        Advance(TimeSpan.FromSeconds(1));
        s.Tick();
        Assert.Equal(1, events);
    }

    [Fact]
    public void Tick_JumpPastMultipleBoundaries_AutoStartOn_SingleTransitionAnchoredAtWake()
    {
        var s = NewService(autoStart: true);
        int events = 0;
        s.SessionEnded += (_, _) => events++;
        s.Start();
        Advance(TimeSpan.FromHours(2));           // 슬립: 25분 집중 + 5분 휴식 경계 모두 초과
        s.Tick();
        Assert.Equal(1, events);                  // 알림 1회만
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
        Assert.Equal(PomodoroState.Running, s.State);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Remaining);  // wake 시점 기준 전체 시간
    }

    [Fact]
    public void Tick_JumpPastMultipleBoundaries_AutoStartOff_SingleTransitionToIdle()
    {
        var s = NewService(autoStart: false);
        int events = 0;
        s.SessionEnded += (_, _) => events++;
        s.Start();
        Advance(TimeSpan.FromHours(2));
        s.Tick();
        Advance(TimeSpan.FromHours(1));
        s.Tick();                                 // Idle — 추가 전이 없음
        Assert.Equal(1, events);
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
        Assert.Equal(PomodoroState.Idle, s.State);
    }

    [Fact]
    public void Remaining_ClampsAtZero_NeverNegative()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(30));        // Tick 전 — endTime 지남
        Assert.Equal(TimeSpan.Zero, s.Remaining);
    }

    // ── 자동 시작 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SessionEnd_AutoStartOn_NextRunningImmediately()
    {
        var s = NewService(autoStart: true);
        s.Start();
        CompleteSession(s, 25);
        Assert.Equal(PomodoroState.Running, s.State);
        Assert.Equal(PomodoroMode.ShortBreak, s.Mode);
    }

    [Fact]
    public void SessionEnd_AutoStartOff_NextIdleFullDuration()
    {
        var s = NewService(autoStart: false);
        s.Start();
        CompleteSession(s, 25);
        Assert.Equal(PomodoroState.Idle, s.State);
        Assert.Equal(TimeSpan.FromMinutes(5), s.Remaining);
    }

    // ── 설정 변경 ──────────────────────────────────────────────────────────────

    [Fact]
    public void SettingsChange_MidSession_AppliesNextSessionOnly()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(5));
        s.FocusMinutes = 50;                      // 세션 중 변경
        Assert.Equal(TimeSpan.FromMinutes(20), s.Remaining);  // 현재 세션 불변

        CompleteSession(s, 20);                   // 집중 완료 → 휴식
        s.Skip();                                 // 휴식 스킵 → 다음 집중
        Assert.Equal(TimeSpan.FromMinutes(50), s.Remaining);  // 새 값 적용
    }

    // ── 이벤트 페이로드 ────────────────────────────────────────────────────────

    [Fact]
    public void SessionEnded_CarriesModeNextModeAndCount()
    {
        var s = NewService();
        SessionEndedEventArgs? args = null;
        s.SessionEnded += (_, e) => args = e;
        s.Start();
        CompleteSession(s, 25);
        Assert.NotNull(args);
        Assert.Equal(PomodoroMode.Focus, args!.EndedMode);
        Assert.Equal(PomodoroMode.ShortBreak, args.NextMode);
        Assert.Equal(1, args.CompletedFocusCount);
    }

    // ── Changed 이벤트 (UI 갱신 신호) ──────────────────────────────────────────

    [Fact]
    public void Changed_FiresOnEveryCommandAndRunningTick()
    {
        var s = NewService();
        int changed = 0;
        s.Changed += (_, _) => changed++;

        s.Start();                                // 1
        Advance(TimeSpan.FromSeconds(1));
        s.Tick();                                 // 2 (Running tick)
        s.Pause();                                // 3
        s.Tick();                                 // no-op tick — 발화 안 함
        s.Resume();                               // 4
        s.Reset();                                // 5
        s.Skip();                                 // 6

        Assert.Equal(6, changed);
    }

    // ── 표시 포맷 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(599, "09:59")]
    [InlineData(7200, "120:00")]
    public void FormatRemaining_FormatsEdgeDurations(int totalSeconds, string expected)
    {
        Assert.Equal(expected, PomodoroService.FormatRemaining(TimeSpan.FromSeconds(totalSeconds)));
    }

    [Fact]
    public void TrayTooltip_Idle_IsPlainNoticker()
    {
        var s = NewService();
        Assert.Equal("Noticker", s.TrayTooltip);
    }

    [Fact]
    public void TrayTooltip_Running_ShowsModeAndTime()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromSeconds(86));        // 25:00 - 1:26 = 23:34
        Assert.Equal("Noticker — 집중 23:34", s.TrayTooltip);
    }

    [Fact]
    public void TrayTooltip_Paused_ShowsPausedLabel()
    {
        var s = NewService();
        s.Start();
        Advance(TimeSpan.FromMinutes(10));
        s.Pause();
        Assert.Equal("Noticker — 일시정지 15:00", s.TrayTooltip);
    }

    [Theory]
    [InlineData(120, 60, 60, true)]
    [InlineData(25, 5, 15, false)]
    public void TrayTooltip_AllModesMaxDuration_Within63Chars(int focus, int sb, int lb, bool paused)
    {
        var s = NewService(focus: focus, shortBreak: sb, longBreak: lb);
        s.Start();
        if (paused) s.Pause();
        Assert.True(s.TrayTooltip.Length <= 63, $"'{s.TrayTooltip}' = {s.TrayTooltip.Length} chars");

        // 긴 휴식 모드에서도 확인
        s.Reset();
        s.Skip();                                 // → 휴식
        s.Start();
        Assert.True(s.TrayTooltip.Length <= 63, $"'{s.TrayTooltip}' = {s.TrayTooltip.Length} chars");
    }
}
