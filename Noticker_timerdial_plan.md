<!-- /autoplan restore point: /c/Users/jungw/.gstack/projects/slpzjvl0102-Noticker/main-autoplan-restore-20260610-174156.md -->
# Noticker Time Timer 다이얼 + 커스텀 모드 계획

설계 출처: `~/.gstack/projects/slpzjvl0102-Noticker/jungw-main-design-20260610-timerdial.md`
(APPROVED — /office-hours, 적대적 리뷰 2라운드 통과). 초안 v1 — /autoplan 리뷰 대상.

## 1. 배경

출시된 포모도로 위젯은 MM:SS 텍스트 중심이라 흘끗 봐서는 남은 시간이 느껴지지 않는다.
실물 Time Timer처럼 줄어드는 파이 웨지(면적)로 바꾸고, 25/5 사이클 외에 유저가 다이얼을
끌어 직접 시간을 정하는 단발 카운트다운 모드를 추가한다.

핵심 차별점 (EUREKA): **웨지 색 = 타이머를 연 스티커의 카테고리 색** — 타이머가 "어떤 일을
위한 시간인지"를 색으로 기억. E6(태스크 연결)의 첫 조각.

## 2. 확정 프리미스 (사용자 승인 + 2차 의견/스펙 리뷰 반영, 2026-06-10)

1. 다이얼이 주 표시, MM:SS는 다이얼 중앙 12-14px (대비 플레이트)
2. 웨지 색 = 마지막으로 연 스티커의 카테고리 색, fallback `#E84033` (선명한 Time Timer 빨강).
   색 키는 App 보유, **Idle일 때만 갱신** (= 세션 중 색 잠금)
3. 60분 시계면 고정, 12시 기준 **반시계** 웨지·눈금 (25분=150°). >60분 세션은 꽉 찬 원 +
   중앙 "+N분" 오버플로
4. 커스텀 = 단발 카운트다운: `TimerKind { Pomodoro, Custom }` 별도 축, AutoStart 무시,
   완료 → Idle, ModeLabel "타이머", 종료 풍선 "타이머 끝 — N분 경과",
   `SessionEndedEventArgs`에 `TimerKind Kind` 추가. 모드 토글은 Idle에서만
5. 커스텀 설정: 다이얼 면 전체 클릭/드래그 (5분 스냅, 0분은 5분으로 클램프) + 휠 ±1분
   (휠 값은 다음 드래그까지 재스냅 안 함) + 키보드 ←→ ±1 / PgUp·PgDn ±5, 범위 1-60.
   마지막 값 `pomodoro_custom_min` 영속, `RefreshPomodoroSettings()`는 불간섭
6. 휴식 웨지 = 무채색 아웃라인 (WedgeStrokeOnly) — 색은 집중 전용
7. 1Hz tick에 애니메이션 불필요 (골드플레이팅 금지)

## 3. 구현 (Approach B — TimerDial UserControl, derisk 순서)

### 3.1 TimerDial UserControl (읽기 전용 먼저)

- DP: `Fraction`(0-1+), `WedgeBrush`, `FaceBrush`, `TickBrush`, `WedgeStroke`, `WedgeStrokeOnly`
- StreamGeometry: BeginFigure(중심) → LineTo(12시) → ArcTo(반시계, IsLargeArc=θ>180°) → 닫기
- **Fraction ≥ 1.0: EllipseGeometry 꽉 찬 원 특수 처리** (ArcTo 시작점=끝점 퇴화 회피)
- 눈금 60개 (RotateTransform), 5분 단위 major + 반시계 숫자 0·5·…·55 (FontSize 9, Opacity 0.55)
- `UseLayoutRounding` + `SnapsToDevicePixels`
- 더미 Fraction으로 두 테마(흑/백 스왑) 시각 검증 먼저 — 상태 연결 전

### 3.2 PomodoroService 확장 (TDD)

- `TimerKind { Pomodoro, Custom }` — 기존 `PomodoroMode` enum 불변, Kind 가드가 앞단 분기
- `SwitchKind(TimerKind)`: Idle에서만, no-op 가드 + Changed 발화
- `SetCustomDuration(int minutes)`: Custom+Idle에서만, 1-60 클램프
- `CustomMinutes` 프로퍼티 (설정 창 아닌 다이얼 입력만 변경)
- Custom: Start는 CustomMinutes 스냅샷, 종료 → Idle (AutoStart 무시), 카운트/사이클 무관,
  ModeLabel "타이머", TrayTooltip "Noticker — 타이머 12:34"
- `SessionEndedEventArgs`에 `TimerKind Kind` 추가 (기존 생성자 호출부 갱신)
- `WedgeFraction` 계산 프로퍼티: `min(Remaining.TotalMinutes, 60) / 60` + `OverflowMinutes`
  (>60 잔여 시 초과분, 아니면 0) — 다이얼/오버플로 표시는 서비스 계산을 바인딩만

### 3.3 PomodoroWindow 레이아웃 개편

- 콘텐츠 200×220 (창 220×240, 그림자 여백 10px): Grid `28 / * / 28`
- Row 1: TimerDial (≈150×150 중앙) — 중앙에 MM:SS 12-14px (FaceBrush 배경 플레이트, 대비 확보),
  바로 위 모드 라벨 11px, 다이얼 아래 도트(포모도로 kind일 때만) + "+N분" 오버플로(>60분일 때)
- 타이틀바 또는 컨트롤 바에 kind 토글 버튼 (Path 글리프, Idle에서만 활성)
- Paused: 다이얼 웨지 Opacity 0.45 (기존 시간 텍스트 dim과 동일 문법)
- 기존 컨트롤·핀·알림·위치 영속 유지. 커스텀 kind에서 건너뛰기 버튼 숨김 (Open Question 해소: 권장안 채택)

### 3.4 입력 레이어 (커스텀 + Idle에서만)

- 다이얼 위 투명 히트테스트 Ellipse, `Atan2` 각도→분 (반시계), CaptureMouse 드래그
- 5분 스냅 (0 → 5 클램프), 휠 ±1 (재스냅 안 함), ←→ ±1 / PgUp·PgDn ±5
- 다이얼 영역은 창 드래그(DragMove) 트리거 아님 (현재도 타이틀바만 DragMove — 유지)
- 접근성: 다이얼 `AutomationProperties.Name` = "타이머 설정 {N}분", 키보드 경로가 동등 기능 보장

### 3.5 색 배선

- `Infrastructure/PomodoroWedgePalette.cs` (또는 TimerDial 동반 static): `_notionColors` 키별
  밝은 변형 — 흰 바탕·#333 바탕 양쪽 대비 확보 (예: red `#E84033`, green `#2E9E5B` 등 10색),
  fallback `#E84033`
- App `_pomodoroWedgeColorKey` (string?): `OpenPomodoro(string? colorKey)` — `service.State ==
  Idle`일 때만 갱신. StickerWindow는 자기 카테고리의 노션 색 이름 전달, 트레이는 null
- 창은 키→팔레트 해석해 WedgeBrush 설정. 휴식이면 WedgeStrokeOnly

## 4. 데이터 / 영속성

신규 키: `pomodoro_custom_min` (기본 30, 범위 1-60, ParseClamped), `pomodoro_kind`는 영속 안 함
(재시작 시 Pomodoro로 — 타이머 상태 미보존 프리미스와 일관). 마이그레이션 불필요.

## 5. 변경 파일

| 파일 | 변경 |
|------|------|
| `Windows/Controls/TimerDial.xaml(.cs)` | 신규 — 다이얼 UserControl |
| `Infrastructure/PomodoroWedgePalette.cs` | 신규 — 밝은 웨지 팔레트 + fallback |
| `Services/PomodoroService.cs` | TimerKind 축, SetCustomDuration, WedgeFraction, EventArgs.Kind |
| `Windows/PomodoroWindow.xaml(.cs)` | 레이아웃 개편 + 입력 레이어 + kind 토글 |
| `Windows/StickerWindow.xaml.cs` | PomodoroButton_Click이 카테고리 색 이름 전달 |
| `App.xaml.cs` | OpenPomodoro(colorKey), _pomodoroWedgeColorKey, custom_min 로드/저장 배선 |
| `Data/SettingsRepository.cs` | pomodoro_custom_min 로드 |
| `Models/AppSettings.cs` | PomodoroCustomMinutes + 상수 |
| `Noticker.Tests/PomodoroServiceTests.cs` | Kind/Custom/WedgeFraction 케이스 추가 |
| `Noticker.Tests/PomodoroWedgePaletteTests.cs` | 신규 — 키 매핑/fallback/전체 키 커버 |

## 6. 엣지 케이스

| 시나리오 | 처리 |
|----------|------|
| Running 중 SwitchKind/SetCustomDuration | no-op (Idle 가드) |
| Running 중 다른 스티커에서 열기 | 색·세션 불변 (Idle-only 키 갱신), 창만 Show+Activate |
| 커스텀 완료 + AutoStart ON | 무시 — Idle 유지 |
| Fraction 정확히 1.0 / >1.0 | EllipseGeometry 특수 처리 |
| 드래그가 12시 근처(스냅 0분) | 5분으로 클램프 |
| 저장된 custom_min 손상 | ParseClamped → 기본 30 |
| ColorSwapped 토글 중 웨지 | 팔레트는 불변, Face/Tick/아웃라인만 테마 따름 — 대비는 팔레트가 양 테마 보장 |
| 포모도로 휴식 중 다이얼 | 아웃라인 웨지 + 도트 유지 |
| >60분 집중 (예: 90분) | 꽉 찬 원 + "+30분" 표시, 잔여 60분부터 웨지 감소 |

## 7. 테스트 추가 목록

서비스: `SwitchKind_OnlyWhenIdle`, `SwitchKind_FiresChanged`, `SetCustomDuration_ClampsRange`,
`SetCustomDuration_OnlyCustomIdle`, `CustomSession_EndsToIdle_IgnoresAutoStart`,
`CustomSession_DoesNotAffectFocusCount`, `Custom_ModeLabel_IsTimer`,
`Custom_TrayTooltip_Format`, `SessionEnded_CarriesKind`, `WedgeFraction_60MinFace`
(25분→0.4167, 90분 잔여→1.0+overflow 30, 0→0), `CustomMinutes_PersistKey_RoundTrip`(repo 테스트)

팔레트: `EveryNotionColorKey_HasWedgeVariant`, `UnknownOrNullKey_FallsBackToRed`

수동 QA: 드래그/휠/키보드 설정, 색 잠금(세션 중 다른 스티커), 두 테마 대비, >60분 오버플로,
반시계 방향 확인
