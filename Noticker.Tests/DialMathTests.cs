using Noticker.Infrastructure;

namespace Noticker.Tests;

public class DialMathTests
{
    // ── 각도 → 분 (12시 기준 반시계, 화면 좌표계 y-down) ──────────────────────

    [Theory]
    [InlineData(0, -100, 0)]      // 12시
    [InlineData(-100, 0, 15)]     // 9시 (반시계 90°)
    [InlineData(0, 100, 30)]      // 6시 (180°)
    [InlineData(100, 0, 45)]      // 3시 (270°)
    public void MinutesFromPoint_CcwFrom12(double dx, double dy, double expected)
    {
        var minutes = DialMath.MinutesFromPoint(dx, dy);
        Assert.NotNull(minutes);
        Assert.Equal(expected, minutes!.Value, 6);
    }

    [Fact]
    public void MinutesFromPoint_JustClockwiseOf12_ApproachesSixty()
    {
        // 12시에서 시계 방향으로 아주 살짝 (0.1° ≈ 359.9° 반시계) → 60에 수렴, 절대 60 도달 안 함
        var minutes = DialMath.MinutesFromPoint(0.1, -100);
        Assert.NotNull(minutes);
        Assert.True(minutes!.Value > 59.9 && minutes.Value < 60.0,
            $"expected (59.9, 60.0) but got {minutes.Value}");
    }

    [Fact]
    public void Exact12OClock_ClampsToFive()
    {
        // 정확히 12시 = 0분 → 스냅 경로에서 0은 5로 클램프
        var minutes = DialMath.MinutesFromPoint(0, -100);
        Assert.NotNull(minutes);
        Assert.Equal(5, DialMath.SnapTo5(minutes!.Value));
    }

    // ── 5분 스냅 ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(22, 20)]
    [InlineData(23, 25)]
    [InlineData(2, 5)]            // 0으로 내림되는 값도 5로 클램프
    [InlineData(58, 60)]
    [InlineData(0, 5)]
    [InlineData(59.9, 60)]        // 반시계 쪽에서 60 접근 → 60 유지
    public void SnapTo5_RoundsAndClampsZero(double raw, int expected)
    {
        Assert.Equal(expected, DialMath.SnapTo5(raw));
    }

    // ── 클램프 (휠/키보드 경로: 플로어 1) ─────────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(30, 30)]
    [InlineData(60, 60)]
    [InlineData(61, 60)]
    [InlineData(-5, 1)]
    public void ClampMinutes_Boundaries(int input, int expected)
    {
        Assert.Equal(expected, DialMath.ClampMinutes(input));
    }

    // ── 드래그 경계 핀 (12시 부근 60↔5 플리커 방지) ───────────────────────────

    [Theory]
    [InlineData(59, 2, 60)]       // 60 근처에서 12시 넘김 → 60 핀
    [InlineData(5, 58, 5)]        // 5 근처에서 반대로 넘김 → 5 핀
    [InlineData(55, 60, 60)]      // 정상 이동 — 그대로
    [InlineData(20, 25, 25)]      // 정상 이동 — 그대로
    [InlineData(20, 50, 50)]      // 차이 정확히 30 — 핀 아님 (초과만 핀)
    public void ApplyDragSample_BoundaryPin(int prev, int candidate, int expected)
    {
        Assert.Equal(expected, DialMath.ApplyDragSample(prev, candidate));
    }

    // ── 데드존 (중심 r<27px — 각도 노이즈 폭주 방지) ──────────────────────────

    [Theory]
    [InlineData(0, 0)]
    [InlineData(10, 10)]
    [InlineData(26.9, 0)]
    [InlineData(0, -26.9)]
    public void DeadZone_RejectsNearCenter(double dx, double dy)
    {
        Assert.Null(DialMath.MinutesFromPoint(dx, dy));
    }

    [Fact]
    public void DeadZone_BoundaryAndBeyond_Accepted()
    {
        Assert.NotNull(DialMath.MinutesFromPoint(0, -27));      // 경계 = 허용
        Assert.NotNull(DialMath.MinutesFromPoint(0, -100));
    }
}
