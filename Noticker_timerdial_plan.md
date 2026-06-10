<!-- /autoplan restore point: /c/Users/jungw/.gstack/projects/slpzjvl0102-Noticker/main-autoplan-restore-20260610-174156.md -->
# Noticker Time Timer 다이얼 + 커스텀 모드 계획

설계 출처: `~/.gstack/projects/slpzjvl0102-Noticker/jungw-main-design-20260610-timerdial.md` (APPROVED)
/autoplan 리뷰 완료본 (CEO + Design×2 + Eng×2, 2026-06-10). **Status: APPROVED — 눈금 숫자 유지 확정 (사용자, D8)**

## 1. 배경

출시된 포모도로 위젯의 MM:SS 텍스트를 실물 Time Timer처럼 줄어드는 파이 웨지(면적)로 바꾸고,
유저가 다이얼을 끌어 직접 시간을 정하는 단발 카운트다운 모드(커스텀)를 추가한다.
핵심 차별점 (EUREKA): **웨지 색 = 타이머를 연 스티커의 카테고리 색** — E6(태스크 연결)의 첫 조각.

## 2. 확정 프리미스

1. 다이얼이 주 표시. MM:SS **13px 고정** (FontWeight Normal, Tabular), 모드 라벨 9px Opacity 0.55와
   함께 **중앙 플레이트 내부** 배치
2. 웨지 색 = 마지막으로 연 스티커의 카테고리 색, fallback `#E84033`. 색 키는 App 보유,
   **Idle일 때만 갱신** (= 색 잠금). **트레이(null)에서 열면 마지막 색 유지** (clear 아님 —
   "마지막으로 연 스티커의 색" 의미론). 카테고리 없는 스티커도 null 전달 → 유지
3. 60분 시계면 고정, 12시 기준 **반시계** (25분=150°). >60분 세션은 꽉 찬 원 + "+N분" 오버플로
   (N = `ceil(잔여분) - 60`, 분당 갱신 — 89:59에 "+30분", 60:00 정각엔 표기 없음)
4. 커스텀 = 단발 카운트다운: `TimerKind { Pomodoro, Custom }` 별도 축. AutoStart 무시(완료→Idle),
   ModeLabel "타이머", 토글은 Idle에서만. **Kind 전환은 Mode/완료카운트 비파괴** — Pomodoro로
   복귀하면 사이클이 멈춘 자리에서 재개
5. 커스텀 설정: 다이얼 드래그(5분 스냅 — **스냅 플로어만 5분**, 서비스 클램프는 1-60) + 휠 ±1
   (재스냅 안 함) + 키보드 ←→ ±1 / PgUp·PgDn ±5. `pomodoro_custom_min` 영속 (기본 30)
6. 휴식 웨지 = 무채색: Stroke `BodyForeground` 1.5px Opacity 0.55 + **약한 fill `#14808080`**
   (순수 아웃라인은 60개 눈금 위에서 면으로 안 읽힘) — 색은 집중 전용
7. 1Hz tick에 애니메이션 불필요. 웨지 재생성은 표시 각도 변화 ≥0.5°일 때만 (드래그 중 제외)
8. Idle 다이얼 = **다음 세션 웨지 프리뷰** (빈 원 상태 없음 — Pomodoro Idle이면 다음 세션 길이,
   Custom Idle이면 CustomMinutes)

## 3. 구현 (derisk 순서)

### 3.1 TimerDial UserControl (읽기 전용 먼저)

- DP: `Fraction`(Coerce 0-1, NaN→0 — 오버플로 텍스트는 창 소관), `WedgeBrush`, `FaceBrush`,
  `TickBrush`, `WedgeStroke`, `IsOutlineOnly`(bool — `WedgeStrokeOnly` 아님: Stroke=Brush 관례 충돌 방지)
- 웨지: StreamGeometry (BeginFigure 중심 → LineTo 12시 → ArcTo 반시계, IsLargeArc=θ>180°), Freeze
  후 `Path.Data` 스왑만. **Fraction ≥ 1.0 → EllipseGeometry** (ArcTo 퇴화 회피), **Fraction == 0 →
  빈 geometry**. 채운 웨지는 Stroke 없음 (플랫 컬러 — 외곽선+채움 조합 금지)
- **렌더 레이어 분리**: 정적 면(외곽 링 + 눈금 60 + 숫자 12)은 테마/크기 변경 시에만 재생성하는
  캐시 레이어(DrawingGroup), 웨지만 per-tick dirty. FormattedText 매 tick 재생성 금지
- 144px 기하 확정: 외곽 링 r71 Stroke `#30808080` 1px / major 눈금(5분) r69→62 두께 1.5
  `TickBrush` Opacity 0.55 / minor r69→66 두께 1 Opacity 0.25 / 숫자 12개(0·5·…·55, 반시계)
  중심 r54, FontSize 9, Opacity 0.55, **수직 유지** (눈금 회전 파이프라인에 넣지 말 것) / 노브 r60
- 중앙 플레이트: 지름 54px Ellipse, Fill=`FaceBrush`(=BodyBackground 바인딩), Opacity 0.92,
  Stroke `#30808080` 1px. 내부: 모드 라벨 9px 위 + MM:SS 13px 아래. 다이얼 면 자체는 fill 없음
  (눈금이 BodyBackground 위에 직접)
- 더미 Fraction으로 두 테마 시각 검증 먼저 — 상태 연결 전

### 3.2 DialMath (순수 함수, TDD)

`Infrastructure/DialMath.cs` (ScreenPlacement 어법):
- `MinutesFromPoint(double dx, double dy)` — Atan2, 반시계, 12시=0
- `SnapTo5(double raw)` — 가장 가까운 5의 배수, **0 → 5 클램프**
- `ClampMinutes(int)` — 1-60 (휠/키보드 경로)
- `ApplyDragSample(int prev, double raw)` — **0/360 경계 정책**: 한 캡처 내 |new−prev| > 30분이면
  가까운 경계(5 또는 60)에 핀 — 12시 부근에서 60↔5 플리커 방지
- 데드존: 중심 반경 27px(플레이트 영역) 미만 클릭/드래그 무시 — 미세 반경에서 각도 노이즈 폭주 방지

### 3.3 PomodoroService 확장 (TDD)

- `TimerKind { Pomodoro, Custom }`. Kind 가드 분기 지점 명시 (전부 — 반쪽 분기 금지):
  - `Remaining` Idle arm: Custom이면 `TimeSpan.FromMinutes(CustomMinutes)`
  - `Start()`: Custom이면 CustomMinutes 스냅샷
  - `Tick()` 완료 블록: Custom이면 카운트++/NextMode()/AutoStart 전부 스킵 → Idle
  - `Skip()`: **Custom이면 no-op** (서비스 레벨 가드 — 버튼 숨김만으론 불충분)
  - `ModeLabel`: Custom이면 "타이머" (TrayTooltip은 자동으로 따라옴)
- `SwitchKind(TimerKind)`: Idle에서만. **같은 kind 재호출은 Changed 미발화**, 실제 전환만 발화
- `CustomMinutes` 프로퍼티: **set 가능** (App 초기 로드 경로 — SetCustomDuration의 가드에 막히지
  않도록 분리). `SetCustomDuration(int)`: Custom+Idle에서만, ClampMinutes(1-60), **값이 실제로
  변하면 Changed 발화** (드래그 중 다이얼/플레이트 갱신의 전제)
- `SessionEndedEventArgs`에 `TimerKind Kind` + `int EndedMinutes` 추가 (풍선 "N분 경과"의 데이터
  소스 + 향후 E5 로깅). Custom 종료 시 EndedMode/NextMode는 보존된 Mode 그대로 (의미 없음 —
  소비자는 Kind 먼저 분기, 규약 주석)
- `WedgeFraction`: `min(Remaining.TotalMinutes, 60) / 60` (Paused면 _pausedRemaining 기준),
  `OverflowMinutes`: `max(0, ceil(Remaining.TotalMinutes) - 60)`

### 3.4 색 팔레트 단일 출처

`Infrastructure/NotionColorPalette.cs`: `(Color Bar, Color Wedge)` 튜플 dict 10키 —
StickerWindow `_notionColors`를 이쪽 참조로 교체 (Bar = 기존 어두운 값 그대로, Wedge = 밝은 변형,
흰 바탕·#333 바탕 양쪽 대비 확보). `Keys` 노출 → 완전성 테스트가 두 소비자 동시 보장.
`Wedge(string? key)` → 미존재/null이면 `#E84033`.

### 3.5 PomodoroWindow 레이아웃 개편

- 콘텐츠 200×220 (창 220×240), Grid `28 / 164 / 28`. **수납 검증**: 4(상단 마진) + 144(다이얼) +
  4 + 12(보조 행) = 164px ✓
- Row 1: TimerDial 144×144 중앙. 보조 행(12px 고정): 세션 도트(Pomodoro kind만,
  **Visibility=Hidden** — Collapsed 금지, kind 전환 시 레이아웃 점프 방지) + 오버플로 시 도트
  우측 6px "+N분" (11px, BodyForeground, Opacity 0.55)
- kind 토글: **타이틀바 좌측** (컬럼 `24 / * / 24 / 24`, Column 0), 22×22 PomodoroToggleStyle,
  **IsChecked = Custom** (one-way from `service.Kind`, Changed 시 갱신 — ToggleButton 자가 토글
  금지, Click은 명령만). 글리프: 모래시계 Path 16×16
  `M4,2 L12,2 L12,4 L9,8 L12,12 L12,14 L4,14 L4,12 L7,8 L4,4 Z` Fill=TitleForeground.
  비Idle 시 IsEnabled=False (0.35). ToolTip/AutomationProperties.Name "타이머 모드 전환"
- Paused: MM:SS dim(기존 TimeOpacity 0.45) + 모드 라벨 " · 일시정지" 접미가 1차 신호.
  채운 웨지는 Opacity 0.45, **아웃라인(휴식) 웨지는 dim 미적용** (0.45×0.55 = 비가시)
- 커스텀 kind에서 건너뛰기 버튼 **Visibility=Hidden** (자리 유지 — 컨트롤 바 중앙 정렬 불변)
- 기존 컨트롤·핀·알림·위치 영속 유지

### 3.6 입력 레이어 (Custom + Idle에서만)

- 투명 히트 Ellipse (`Fill="Transparent"` — null이면 히트 안 됨), **IsHitTestVisible을
  (Kind==Custom && State==Idle)에 바인딩** (핸들러 가드만으론 커서/클릭 흡수 문제)
- 발견성 3중 장치: ① **노브** — 웨지 끝각 r60에 지름 8px 원 (Fill=WedgeBrush,
  Stroke=FaceBrush 1.5px), Custom+Idle 상시 표시, 드래그 중 10px. 노브 밖 면 클릭도 즉시 해당
  각도로 설정 (정밀 클릭 강요 금지) ② 히트 영역 `Cursor="Hand"` ③ ToolTip "드래그하여 시간 설정 (휠 ±1분)"
- 드래그: CaptureMouse, **per-move 커밋** (SetCustomDuration 호출 — Changed가 실시간 갱신),
  플레이트 MM:SS가 스냅 값 실시간 표시. **LostMouseCapture에서 드래그 상태 해제** (Alt-Tab 누수
  방지). State가 Idle을 떠나면 드래그 자체 취소
- `pomodoro_custom_min` 저장은 **드래그 종료/휠 디바운스 시 1회 + try/catch** (SavePosition 어법
  — 휠 노치마다 DB 쓰기 금지)
- 키보드: 다이얼 Focusable(Custom+Idle), 클릭 시 Focus. 포커스 비주얼 = 외곽 링 `#50808080`
  2px 승격. ←→ ±1 / PgUp·PgDn ±5 (Window PreviewKeyDown에서 Custom+Idle 가드 — 방향키 포커스
  내비게이션 충돌 방지). `AutomationProperties.Name` "타이머 설정 {N}분" + LiveSetting=Polite

### 3.7 App / Sticker 배선

- `OpenPomodoro(string? colorKey)`: `service.State == Idle && colorKey != null`일 때만
  `_pomodoroWedgeColorKey` 갱신, **매 호출 시 `_pomodoroWindow?.SetWedgeColorKey(키)` push**
  (`??=` 캐시 창에 생성자 전달만으론 색이 첫 오픈에 고정 — 3-voice 합의 결함)
- `OnPomodoroSessionEnded`: **`e.Kind == Custom` 최우선 분기** → 풍선 "타이머 끝 —
  {e.EndedMinutes}분 경과", 포모도로 메시지 분기 진입 금지. 자동 표시/사운드는 공통
- `InitPomodoro`: `pomodoro_custom_min` 로드 → `service.CustomMinutes` (프로퍼티 set — 가드 우회).
  `RefreshPomodoroSettings`는 CustomMinutes 불간섭 (보호 주석 추가)
- StickerWindow `PomodoroButton_Click`: 자기 카테고리의 노션 색 이름 전달
  (`AppSettings.CategoryColors[category]`), 없으면 null. 트레이 메뉴는 null

## 4. 데이터 / 영속성

신규 키: `pomodoro_custom_min` (기본 30, 1-60, ParseClamped — 로드/입력 양쪽). `pomodoro_kind`
영속 안 함 (재시작 = Pomodoro). 마이그레이션 불필요.

## 5. 변경 파일

| 파일 | 변경 |
|------|------|
| `Windows/Controls/TimerDial.xaml(.cs)` | 신규 — 다이얼 (정적 면 캐시 + 웨지 스왑) |
| `Infrastructure/DialMath.cs` | 신규 — 각도/스냅/클램프/경계 순수 함수 |
| `Infrastructure/NotionColorPalette.cs` | 신규 — (Bar, Wedge) 단일 출처, Keys 노출 |
| `Services/PomodoroService.cs` | TimerKind 축 (가드 5지점), CustomMinutes, WedgeFraction, EventArgs Kind+EndedMinutes |
| `Windows/PomodoroWindow.xaml(.cs)` | 레이아웃 개편 + 입력 레이어 + kind 토글 + SetWedgeColorKey |
| `Windows/StickerWindow.xaml.cs` | _notionColors → NotionColorPalette 참조, 색 이름 전달 |
| `App.xaml.cs` | OpenPomodoro(colorKey) + push, SessionEnded Kind 분기, custom_min 배선 |
| `Data/SettingsRepository.cs` | pomodoro_custom_min 로드 |
| `Models/AppSettings.cs` | PomodoroCustomMinutes 상수 |
| `Noticker.Tests/PomodoroServiceTests.cs` | Kind/Custom/WedgeFraction 케이스 (아래 §7) |
| `Noticker.Tests/DialMathTests.cs` | 신규 — 각도/스냅/경계 전 케이스 |
| `Noticker.Tests/NotionColorPaletteTests.cs` | 신규 — 완전성/fallback |

병렬화: Track A(TimerDial 시각) ∥ Track B(서비스+DialMath TDD) ∥ Track C(팔레트+영속) →
이후 §3.5-3.7은 순차 (PomodoroWindow/App 공유 파일).

## 6. 엣지 케이스

| 시나리오 | 처리 |
|----------|------|
| Running 중 SwitchKind/SetCustomDuration/드래그 | no-op (서비스 가드 + IsHitTestVisible off) |
| Custom 중 Skip() 호출 | 서비스 no-op (사이클 비파괴) |
| Running 중 다른 스티커에서 열기 | 색·세션 불변, 창만 Show+Activate |
| Idle에서 다른 색 스티커로 다시 열기 | 즉시 새 색 (SetWedgeColorKey push) |
| 트레이/무카테고리에서 열기 | 마지막 색 유지 |
| 커스텀 완료 + AutoStart ON | 무시 — Idle |
| Kind 왕복 (집중1 완료 → Custom → Pomodoro) | Mode/카운트 보존 — 휴식부터 재개 |
| Fraction 0 / 정확히 1.0 / >1.0 / NaN | 빈 geometry / Ellipse / Ellipse+오버플로 / Coerce 0 |
| 드래그 12시 경계 (60↔5) | ApplyDragSample 경계 핀 — 플리커 없음 |
| 중심 부근 클릭 (r<27px) | 데드존 무시 |
| 드래그 중 Alt-Tab/풍선 | LostMouseCapture → 드래그 해제, 마지막 커밋 값 유지 |
| 드래그 중 Space (Start) | State 이탈 → 드래그 취소, 현재 커밋 값으로 시작 |
| 저장된 custom_min 손상 ("abc"/"0"/"999") | ParseClamped → 30/1/60 |
| 60:00 정각 | Fraction 1.0, 오버플로 표기 없음 |
| 89:59 | "+30분" (ceil) |
| Paused 휴식(아웃라인) | 웨지 dim 미적용 — 텍스트 dim + " · 일시정지"가 신호 |
| ColorSwapped 토글 | 정적 면 캐시 재생성, 팔레트는 양 테마 대비 보장 |

## 7. 테스트 목록

서비스 (기존 + 추가): `SwitchKind_OnlyWhenIdle`, `SwitchKind_SameKind_DoesNotFireChanged`,
`SwitchKind_RoundTrip_PreservesModeAndFocusCount`, `SetCustomDuration_ClampsRange1To60`,
`SetCustomDuration_OnlyCustomIdle`, `SetCustomDuration_FiresChanged_OnRealChange`,
`CustomIdle_Remaining_EqualsCustomMinutes`, `CustomSession_EndsToIdle_IgnoresAutoStart`,
`CustomSession_DoesNotAffectFocusCount`, `CustomSession_End_DoesNotMutateMode`,
`Reset_DuringCustom_RestoresCustomMinutes`, `CustomTick_JumpPastEnd_SingleSessionEnded_ToIdle`,
`Skip_CustomKind_NoOp`, `Custom_ModeLabel_IsTimer`, `Custom_TrayTooltip_Format`,
`TrayTooltip_PausedCustom_Within63Chars`, `SessionEnded_CarriesKindAndEndedMinutes` (양 kind),
`WedgeFraction_60MinFace` (25분→0.4167, 정각 60→1.0+오버플로0, 89:59→1.0+30, 0→0, Paused 기준)

DialMath: `MinutesFromPoint_CcwFrom12` (0°/90°→45/270°→15/359.9°), `Exact12OClock_ClampsToFive`,
`SnapTo5_RoundsAndClampsZero` (22→20, 23→25, 2→5, 58→60, 0→5), `ClampMinutes_Boundaries`,
`ApplyDragSample_BoundaryPin` (59→2 점프 → 60 핀, 5→58 점프 → 5 핀), `DeadZone_RejectsNearCenter`

팔레트: `PaletteKeys_ExactlyMatchBarKeys`, `EveryKey_HasWedgeVariant`, `UnknownOrNullKey_FallsBackToRed`

영속: `CustomMin_RoundTrip`, `CustomMin_CorruptedOrOutOfRange_ParseClamped` (abc/0/999/null)

수동 QA: 별도 테스트 플랜 아티팩트 참조 (드래그 방향/경계/캡처 누수, 색 잠금·push, 두 테마 대비,
오버플로, kind 왕복, 회귀).

## NOT in scope

- 눈금 숫자 제거 여부 — TASTE (최종 게이트, 기본 유지: 사용자 이미지 충실)
- Notion 세션 로깅 (E5), 트레이 미니 웨지 — TODOS 유지/추가
- 60분 초과 커스텀, kind 영속, 애니메이션, 터치 최적화

## Decision Audit Trail (run 2)

| # | Phase | Decision | Class | Principle | Rationale |
|---|-------|----------|-------|-----------|-----------|
| 21 | Gate | 프리미스 게이트 = office-hours D3+D6 승인으로 충족 | Mechanical | — | 수분 전 동일 전제 사용자 확인 — 재질문은 재론 |
| 22 | Eng | SetWedgeColorKey push 메커니즘 | Mechanical | P1 | 3-voice 합의 — ??= 캐시 결함 |
| 23 | Eng | EventArgs에 Kind+EndedMinutes | Mechanical | P2 | 풍선 데이터 소스 + E5 future-fit |
| 24 | Design | 다이얼 144px + 라벨 플레이트 내장 | Mechanical | P1 | 164px 기하 충돌 해소 (2-agent 합의) |
| 25 | Design | 눈금 숫자 유지 (9px, 0.55) | **Taste** | — | 디자인 에이전트 충돌 — 사용자 이미지에 숫자 있음 → 유지 기본 |
| 26 | Design | 휴식 웨지 = 스트로크+약한 fill | Mechanical | P1 | 순수 아웃라인은 눈금 위 비가시 |
| 27 | Design | 노브+Hand+툴팁 발견성 3중 | Mechanical | P1 | 드래그 기능 발견 실패 = 기능 없음 |
| 28 | Eng | DialMath 순수 추출 | Mechanical | P4/P5 | 시그니처 인터랙션 수학의 테스트 가능성 |
| 29 | Eng | NotionColorPalette 단일 출처 | Mechanical | P4 | 2-dict 드리프트 + 완전성 테스트 작성 가능 |
| 30 | Eng | 드래그 경계 핀 + 데드존 + LostMouseCapture | Mechanical | P1 | 핵심 인터랙션 견고성 |
| 31 | Eng | 클램프 플로어 단일화 (스냅 5 / 서비스 1) | Mechanical | P5 | 3-플로어 모순 해소 |
| 32 | Eng | Skip 서비스 가드 | Mechanical | P1 | 버튼 숨김만으론 사이클 오염 가능 |
| 33 | Design | 오버플로 = 보조 행 (플레이트 아님) | Taste→해소 | P5 | 90분 케이스 도트 공존 기하 검증됨 |
| 34 | Design | kind 토글 = 타이틀바 Col0 모래시계, checked=Custom | Mechanical | P5 | 2-agent 합의, transport 바 순수성 |

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 1 | CLEAR (via /autoplan) | 13 findings (2 high) → 전부 반영 |
| Codex Review | `/codex review` | Independent 2nd opinion | 0 | — (codex 미설치) | — |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 1 | CLEAR (PLAN via /autoplan) | 커버리지 13/41→전 케이스 반영, CRITICAL GAP 2 해소 |
| Design Review | `/plan-design-review` | UI/UX gaps | 1 | CLEAR (FULL via /autoplan) | 6.5/10 → 9/10, 12 amendments 채택 |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | — (DX scope 없음) | — |

- **VERDICT:** CEO + ENG + DESIGN CLEARED — ready to implement. Dual voices: subagent-only.
- **CROSS-MODEL:** 디자인 에이전트 2개 충돌 1건 (눈금 숫자) → 최종 게이트에서 사용자가 유지로 확정 (D8).

NO UNRESOLVED DECISIONS
