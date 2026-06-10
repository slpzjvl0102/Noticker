namespace Noticker.Infrastructure;

// 다이얼 인터랙션 수학 — 순수 함수, WPF 참조 금지 (테스트 가능성의 핵심).
// 좌표계: 화면 좌표 (y 아래로 증가), 다이얼 중심 기준 오프셋 (dx, dy).
// 각도: 12시 = 0분, 반시계 방향으로 증가 (Time Timer 어법 — 9시 = 15분).
public static class DialMath
{
    // 중앙 플레이트 영역 — 미세 반경에서 각도 노이즈 폭주 방지
    public const double DeadZoneRadius = 27.0;

    public static double? MinutesFromPoint(double dx, double dy)
    {
        if (Math.Sqrt(dx * dx + dy * dy) < DeadZoneRadius) return null;
        // 12시 기준 반시계: θ = Atan2(−dx, −dy), [0, 2π) 정규화
        var theta = Math.Atan2(-dx, -dy);
        if (theta < 0) theta += 2 * Math.PI;
        return theta / (2 * Math.PI) * 60.0;
    }

    // 드래그 스냅 플로어는 5 (정확히 12시 = 0 → 5). raw는 반시계 쪽에서 60에 수렴 가능 → 60 유지
    public static int SnapTo5(double rawMinutes)
    {
        var snapped = (int)Math.Round(rawMinutes / 5.0, MidpointRounding.AwayFromZero) * 5;
        return snapped == 0 ? 5 : snapped;
    }

    // 휠/키보드 경로의 클램프 플로어는 1 (스냅 플로어 5와 별개 — 계획 §3.2)
    public static int ClampMinutes(int minutes) => Math.Clamp(minutes, 1, 60);

    // 0/360 경계 정책: 한 캡처 내 |candidate−prev| > 30분이면 가까운 경계에 핀
    // — 12시 부근에서 60↔5 플리커 방지
    public static int ApplyDragSample(int prevMinutes, int candidate)
    {
        if (Math.Abs(candidate - prevMinutes) > 30)
            return prevMinutes > 30 ? 60 : 5;
        return candidate;
    }
}
