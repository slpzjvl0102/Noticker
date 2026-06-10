<!-- /autoplan restore point: /c/Users/jungw/.gstack/projects/slpzjvl0102-Noticker/main-autoplan-restore-20260610-141701.md -->
# Noticker 포모도로 타이머 기능 계획

Noticker(WPF + .NET 8 스티커메모 앱)에 포모도로 타이머를 추가하는 계획.
/autoplan 리뷰 완료본 (CEO + Design + Eng, 2026-06-10).

## 1. 배경 / 동기

Noticker는 데스크탑 위에 떠 있는 capture 도구다. Noticker가 이길 지점은 기능 수가 아니라
"이미 떠 있다는 것" — 별도 앱 설치 없이 트레이와 스티커 툴바에서 바로 타이머를 꺼내 쓴다.
통계·게임화는 capture 도구 철학과 충돌하므로 빼는 것이 차별화다.

## 2. 목표 / 제외

### 목표

- 스티커와 같은 디자인 언어(흑/백 미니멀, WindowStyle=None)의 작은 포모도로 위젯 창
- 집중 25분 / 짧은 휴식 5분 / 긴 휴식 15분(4세션마다) 기본 사이클, 설정에서 변경 가능
- 세션 종료 시 확실한 알림 (위젯 자동 표시 + 사운드 + 풍선, 포커스 스틸링 없음)
- 위젯을 숨겨도 타이머는 백그라운드에서 계속 동작 (트레이 툴팁으로 확인 가능)
- 위젯 위치 영속 (재시작 후 같은 자리에 복원)

### 제외 (v1)

- 포모도로 기록/통계, Notion으로의 세션 로그 sync
- 태스크(스티커)와 타이머 연결
- 글로벌 hotkey
- 다중 타이머
- 진행률 바/링 시각화 (E3 — 기각: 25분 = 약 164px 폭에서 초당 0.11px 이동, 1초 tick으로는
  멈춘 듯 보여 오히려 품질 저하. MM:SS(세션 내)와 도트(세션 간)가 이미 진행률 2채널 제공)
- 세션 전환 애니메이션 (앱 전체에 애니메이션이 없음 — 디자인 언어 밖)

## 3. 기능 명세

### 3.1 타이머 위젯 창 (PomodoroWindow)

- 열기 (진입점 2개):
  - 트레이 우클릭 메뉴에 "포모도로 타이머" 항목 추가
  - 스티커 활성 시 나타나는 하단 포맷팅 툴바의 `NumberButton` 우측에 포모도로 버튼 추가
    (앞에 1px `#50808080` 구분선 — 문자 서식 그룹과 시각적으로 분리)
- 창 인스턴스는 App이 `_pomodoroWindow` 필드로 보유. hide 패턴이므로
  `Windows.OfType + Activate`만으로는 숨김 창이 다시 보이지 않음 — 열기 경로는 `Show()` 후
  `Activate()` (사용자가 직접 열 때는 활성화가 맞음)
- 첫 실행 기본 위치: 작업 영역 우하단 (트레이 근처)

#### 레이아웃 (콘텐츠 180×150 고정, 그림자 여백 10px 포함 시 창 200×170)

- Grid Rows: `28 / * / 28`
- **Row 0 (타이틀 바)**: 높이 28, `TitleBackground`, CornerRadius `8 8 0 0`. 좌측 빈 영역 =
  드래그(스티커 패턴 재사용). 우측: 핀 토글(24px 컬럼) + X(24px 컬럼, DeleteButtonStyle, FontSize 16)
- **Row 1 (본문)**: `BodyBackground`, 세로 중앙 정렬 StackPanel
  - 모드 라벨: FontSize 11, `BodyForeground`, Opacity 0.55, 위 여백 10 ("집중"/"짧은 휴식"/"긴 휴식")
  - 시간: FontSize 36, FontWeight Light, `Typography.NumeralAlignment="Tabular"`
    (초마다 자리폭 흔들림 방지), `BodyForeground`. 분은 3자리까지 표시 ("120:00") — 폭 확보
  - 세션 도트: Ellipse — 간격 설정 1-6이면 8px/도트 간격 6px, 7-12이면 6px/간격 4px.
    완료 = `BodyForeground` 채움, 미완료 = Stroke 1px Opacity 0.3 빈 원. 시간 아래 여백 8
- **Row 2 (컨트롤 바)**: 높이 28, `TitleBackground`, CornerRadius `0 0 8 8`. 가운데 정렬,
  22×22 버튼 3개(FormatToggleStyle), 간격 8: 리셋 | 시작·일시정지 | 건너뛰기

#### 상태별 표시

| 상태 | 시간 | 시작/일시정지 버튼 | 리셋 버튼 |
|------|------|------|------|
| Idle | 전체 시간, Opacity 1 | 재생 글리프 | 비활성 (Opacity 0.35) |
| Running | 카운트다운 | 일시정지 글리프 | 활성 |
| Paused | 남은 시간, Opacity 0.45 (깜빡임 없음) | 재생 글리프 | 활성 |
| 세션 종료(auto-start OFF) | 다음 세션 전체 시간으로 Idle 복귀, 모드 라벨 즉시 전환 | 재생 글리프 | 비활성 |

- 도트 리셋: 긴 휴식이 **끝나는** 시점에 ○로 초기화 (긴 휴식 동안은 ●●●● 유지 — 보상의 순간)
- Skip은 Idle 포함 모든 상태에서 동작 (다음 모드의 Idle로)

#### 글리프 명세 (이모지 전면 금지 — 흑/백 디자인 언어 유지)

- 재생: Path `M 0 0 L 9 5.5 L 0 11 Z`, Fill=`TitleForeground`
- 일시정지: 3×11 Rectangle 2개, 간격 3px
- 건너뛰기: 7×10 재생 Path + 우측 1.5×10 Rectangle
- 리셋: 텍스트 `↺` (U+21BA), FontSize 14
- 핀(Always-on-top, E2): 상단 바 우측 22×22 토글 버튼 (FormatToggleStyle — ON이면 배경
  `#50808080`). 글리프는 Path 압정(16px 캔버스,
  `Data="M5,0 L11,0 L11,2 L10,2 L10,6 L13,9 L13,10 L8.7,10 L8,15 L7.3,10 L3,10 L3,9 L6,6 L6,2 L5,2 Z"`,
  Fill=`TitleForeground`). 기본 ON, `pomodoro_always_on_top` 키에 영속
- 스티커 툴바 진입 버튼: 22×22, FormatToggleStyle과 동일한 hover/pressed. 글리프 = 12×12
  Canvas에 Ellipse(11px, Stroke `TitleForeground` 1.2) + 시계바늘 Line 2개(중심→12시 4px,
  중심→3시 3px) — ResizeHandleTemplate과 같은 Path 아이콘 어법

#### 디자인 토큰

- 창 셸: StickerWindow의 이중 Border 구조 복사 — 바깥 `Margin="10"`, 안쪽
  `BorderThickness="1" BorderBrush="#30808080" CornerRadius="8"`,
  DropShadow(Blur 20, Opacity 0.45, Depth 4, Direction 270)
- 색상: `TitleBackground`/`TitleForeground`/`BodyBackground`/`BodyForeground` 바인딩만 사용,
  **하드코딩 hex 금지** (ColorSwapped 스왑 보장). PropertyChanged 구독/해제는 StickerWindow
  패턴 그대로 (real-close 경로에서 해제)

#### 키보드 / 접근성

- `Space` = 시작/일시정지, `Esc` = 숨기기 (창 PreviewKeyDown)
- 모든 버튼 ToolTip: "시작 (Space)"/"일시정지 (Space)", "리셋", "건너뛰기", "항상 위에 고정", "숨기기"
- 글리프 버튼 전부 `AutomationProperties.Name` 지정. 도트 컨테이너: `"세션 {n}/{간격} 완료"`.
  모드 라벨 Opacity 0.55 미만 금지 (대비 확보)

#### 창 동작

- 상단 바 드래그로 이동, 고정 크기(리사이즈 없음)
- X 버튼 = 창 숨기기 (타이머는 계속 동작) — 스티커의 hide 패턴 재사용.
  Running 중 숨기면 세션당 1회 풍선 안내: "타이머는 백그라운드에서 계속 실행 중입니다"
- ShowInTaskbar=False, `ShowActivated="False"` (XAML 설정 — 자동 표시 시 포커스 스틸링 방지의
  실제 메커니즘. WPF는 `Show()` 자체가 활성화하므로 Activate를 안 부르는 것만으로는 부족)
- 컨트롤 의미론:
  - 건너뛰기: 모든 상태에서 다음 모드의 Idle로. **완료 카운트에 미포함** (긴 휴식은 완료로만 획득)
  - 리셋: Running/Paused 중 누르면 Idle로 정지하고 현재 세션을 처음 시간으로 복원
    (자동 재시작 안 함). 완료 카운트 유지. 카운트 전체 리셋은 앱 재시작 시

### 3.2 타이머 엔진 (PomodoroService)

- **소유권: App이 단일 인스턴스를 생성·보유** (SyncQueue와 동일한 composition root 패턴).
  App이 DispatcherTimer로 `service.Tick(now)`을 구동하고, 알림(사운드/풍선/툴팁/위젯 자동
  표시) side-effect도 App이 수행. PomodoroWindow는 service 이벤트 구독 + Start/Pause/Reset/Skip
  호출만 하는 뷰
- **시간 소스: `DateTime.UtcNow`를 `Func<DateTime>`으로 주입.** `DateTime.Now` 금지 —
  시스템 시계 수동 변경/DST 전환 시 타이머가 멈춘 듯 보이는 문제 방지
- 종료 시각 기준 계산: `remaining = max(0, endTime - now)` (절전/슬립 후 어긋남 방지)
- 일시정지: Pause 시 남은 초 저장 + endTime 해제, Resume 시 `endTime = now + 남은 초`
- `Services/PomodoroService.cs`는 `System.Windows`/`System.Windows.Forms`를 참조하지 않음
  (순수 상태머신 — 테스트 가능성의 핵심)
- 상태 모델: `PomodoroMode { Focus, ShortBreak, LongBreak }` × `PomodoroState { Idle, Running, Paused }`
- **사이클 카운터는 하나**: `completedFocusCount`가 도트 표시와 휴식 종류 선택을 모두 결정.
  긴 휴식은 오직 `completedFocusCount == 간격`일 때만. Skip은 카운트 미증가로 다음 모드 진행.
  카운트는 긴 휴식이 끝날 때 리셋. 간격은 휴식 선택 시점에 읽음 — 사이클 중 간격이 4→2로
  줄었고 완료가 이미 2 이상이면 다음 휴식은 긴 휴식
- 세션 종료 시: `SessionEnded` 이벤트 발생 → App이 알림 수행 → auto-start ON이면 다음 세션
  즉시 Running, OFF면 다음 세션 Idle 대기
- **이벤트는 인자 객체를 가짐**: `SessionEndedEventArgs(mode, nextMode, completedFocusCount)` —
  향후 스티커-태스크 연결/세션 로그가 시그니처 변경 없이 구독만 추가하면 되도록
- **슬립이 두 세션 경계 이상을 넘긴 경우 (자동 시작 ON)**: 첫 경계만 종료 처리(알림 1회),
  다음 세션은 wake 시점(now) 기준으로 시작 — 경과한 중간 세션들을 cascade 완료 처리하지 않음
  (wake 시 알림 폭주 방지, v1 단순화)
- 세션 시작 시 지속시간 스냅샷: `Start()`에서 AppSettings 값을 복사 — tick마다 재읽기 금지
  (세션 중 설정 변경은 다음 세션부터)
- **DispatcherTimer는 State == Running일 때만 구동** (Idle/Paused면 Stop — 유휴 시 1Hz 웨이크업
  방지). `ExitApp()`에서 타이머 정지 후 트레이 아이콘 dispose (dispose된 `NotifyIcon.Text`
  갱신 크래시 방지). tick 핸들러는 `IsShuttingDown`이면 즉시 return
- 앱 종료/재시작 시 타이머 진행 상태는 보존하지 않음 (Idle로 리셋)

### 3.3 알림 (채널 우선순위 순)

1. **위젯 자동 표시 (보장 채널)**: 숨겨져 있으면 `Show()`만 호출 (`ShowActivated=False`이므로
   포커스 안 뺏음). Always-on-top 기본 ON이라 시야에 들어옴. 핀 OFF + 사운드 OFF + DND ON이면
   가려질 수 있음 — 알려진 한계로 수용 (사용자가 두 토글을 직접 껐을 때만)
2. **사운드 (1차 청각 채널)**: `SystemSounds.Asterisk` (경고 톤이 아닌 부드러운 톤 — 25분 집중의
   보상이 에러음이면 안 됨). 설정에서 on/off. 음소거/장치 없음이면 조용히 무시됨(no-throw)
3. **트레이 풍선 (보조)**: 기존 `ShowBalloonTip` 패턴. "집중 끝 — 5분 휴식하세요" /
   "휴식 끝 — 다시 집중할 시간" (집중 종료와 휴식 종료는 다른 메시지). Win11 집중 지원(DND)이
   억제할 수 있음을 전제로 한 보조 채널
4. **트레이 툴팁 (E1, 상시)**: Running 중 tick마다 `NotifyIcon.Text` 갱신 — "Noticker — 집중 12:34".
   Paused면 "Noticker — 일시정지 12:34", Idle이면 "Noticker"로 복원. 툴팁 문자열 생성은 테스트
   가능하도록 PomodoroService(또는 static formatter)에 두고 App은 결과만 대입. 길이 ≤ 63 가드
   (초과 시 잘라냄), 같은 값이면 재대입 생략

### 3.4 설정

SettingsWindow에 "포모도로" 섹션 추가:

- 집중 시간 (분, 기본 25, 범위 1-120)
- 짧은 휴식 (분, 기본 5, 범위 1-60)
- 긴 휴식 (분, 기본 15, 범위 1-60)
- 긴 휴식 간격 (세션 수, 기본 4, 범위 1-12)
- 다음 세션 자동 시작 (토글, 기본 OFF)
- 종료 사운드 (토글, 기본 ON)
- 레이아웃: 기존 SectionHeadingStyle("포모도로") + FieldLabelStyle + 밑줄형 TextBox 재사용.
  시간 입력 4개는 2×2 Grid(컬럼 간격 20). 토글 2개는 기존 CheckBox 스타일
- **정수 설정의 파싱·클램프 규칙(기본값/min/max)은 한 곳에 정의** (AppSettings의 상수 +
  `ParseClamped(string? raw, int def, int min, int max)` 헬퍼) — `SettingsRepository.LoadInto`
  (저장된 값 손상 대비)와 SettingsWindow 저장 양쪽이 같은 헬퍼 사용

## 4. 데이터 / 영속성

settings 테이블(key-value) 재사용. 신규 키:

| 키 | 기본값 |
|----|--------|
| `pomodoro_focus_min` | 25 |
| `pomodoro_short_break_min` | 5 |
| `pomodoro_long_break_min` | 15 |
| `pomodoro_long_break_interval` | 4 |
| `pomodoro_auto_start` | false |
| `pomodoro_sound` | true |
| `pomodoro_always_on_top` | true |
| `pomodoro_window_x`, `pomodoro_window_y`, `pomodoro_monitor` | (위치 복원용 — `pomodoro_monitor`는 `Sticker.MonitorDeviceName`과 동일한 device name 형식) |

스키마 마이그레이션 불필요 (stickers 테이블 변경 없음, 키는 upsert로 자연 생성).

## 5. 변경 파일

| 파일 | 변경 내용 |
|------|----------|
| `Windows/PomodoroWindow.xaml(.cs)` | 신규 — 위젯 UI. `.cs`는 StickerWindow.xaml.cs 상단과 동일한 using alias 블록 사용 (WPF/WinForms 타입 충돌 방지) |
| `Services/PomodoroService.cs` | 신규 — 순수 타이머 상태 머신 (WPF/WinForms 참조 없음) |
| `Infrastructure/ScreenPlacement.cs` | 신규 — RestoreStickers의 모니터 매칭/clamp 로직 추출 (스티커·포모도로 공용, 순수 함수로 단위 테스트 가능) |
| `Models/AppSettings.cs` | 포모도로 설정 프로퍼티 + ParseClamped 헬퍼/상수 |
| `Data/SettingsRepository.cs` | LoadInto에 신규 키 로드 (TryParse + clamp + 기본값 fallback) |
| `App.xaml.cs` | PomodoroService/DispatcherTimer 소유, 트레이 메뉴 항목, OpenPomodoro(), 알림 side-effect, ExitApp 타이머 정지 순서 |
| `Windows/SettingsWindow.xaml(.cs)` | 포모도로 설정 섹션 |
| `Windows/StickerWindow.xaml(.cs)` | 포맷팅 툴바에 구분선 + 포모도로 버튼 (NumberButton 우측) |
| `Noticker.Tests/PomodoroServiceTests.cs` | 신규 — 아래 §7 테스트 목록 전체 |
| `Noticker.Tests/ScreenPlacementTests.cs` | 신규 — clamp/primary fallback 테스트 |
| `Noticker.Tests/SettingsRepositoryTests.cs` | 추가 — 포모도로 키 로드/클램프 테스트 |

구현 순서 (단일 브랜치, 병렬화 없음): ScreenPlacement 추출 → PomodoroService + 테스트 →
AppSettings/LoadInto + 테스트 → PomodoroWindow → App 배선 → SettingsWindow 섹션 →
StickerWindow 툴바 버튼.

## 6. 엣지 케이스

| 시나리오 | 처리 |
|----------|------|
| PC 슬립 후 복귀 (한 세션 내) | endTime 기준 재계산 — 슬립 중 시간 경과 반영, 이미 지났으면 종료 처리 1회 |
| 슬립이 두 세션 경계 이상 넘김 (자동 시작 ON) | 첫 경계만 종료 처리(알림 1회), 다음 세션은 wake 시점 기준 시작 — cascade 없음 |
| 일시정지 중 슬립/시간 점프 | remaining 동결 (endTime 없음) — 영향 없음 |
| remaining==0 경계에서 Pause/Skip | tick이 종료 전이를 먼저 처리, 이후 조작은 새 상태 기준 no-op 또는 정상 동작 (`remaining = max(0, ...)` 클램프) |
| 타이머 동작 중 설정 변경 | 현재 세션은 시작 시 스냅샷 유지, 다음 세션부터 새 값. 간격은 휴식 선택 시점에 읽음 |
| 사이클 중 간격 축소 (4→2, 완료 3) | 완료 ≥ 새 간격이면 다음 휴식은 긴 휴식 |
| 위젯 모니터 분리 | ScreenPlacement clamp/primary fallback (스티커와 동일) |
| 앱 종료 시 타이머 동작 중 | ExitApp이 포모도로 타이머 먼저 정지 → 트레이 dispose → 종료 (disposed NotifyIcon 크래시 방지). 상태 미보존 |
| 잘못된 설정 입력/저장값 (0, 음수, 텍스트, 거대값) | ParseClamped — 입력과 로드 양쪽에서 클램프 + 기본값 |
| 시스템 시계 변경/DST | UtcNow 주입으로 영향 없음 |
| 진입점 연타/이중 클릭 | 단일 `_pomodoroWindow` 참조 — 두 번째 호출은 기존 창 Show+Activate |
| DND(집중 지원) ON | 풍선 억제 가능 — 위젯 자동 표시 + 사운드로 보완 (3.3 채널 랭킹) |

## 7. 테스트 목록 (PomodoroServiceTests.cs 외)

상태 전이: `Start_FromIdle_TransitionsToRunning`, `Pause_WhileRunning_TransitionsToPaused`,
`Resume_FromPaused_TransitionsToRunning`, `Reset_RestoresFullDuration_KeepsCount`,
`InvalidTransitions_AreNoOps` (Pause@Idle, Start@Running, Resume@Running),
`Reset_WhileRunning_StopsToIdle`, `Tick_WhileIdleOrPaused_NoOp`

사이클: `FocusEnd_TransitionsToShortBreak`, `FourthFocusEnd_TransitionsToLongBreak`,
`LongBreakEnd_ResetsCompletedCount_NextIsFocus`, `Skip_DuringFocus_DoesNotIncrementCount`,
`Skip_DuringBreak_AdvancesToFocus`, `SkippedFocus_DoesNotTriggerLongBreak` (완료 3 + 스킵 1 →
짧은 휴식), `IntervalOne_EveryFocusEndsInLongBreak`, `IntervalShrunkMidCycle_NextBreakIsLong`

시간/경계: `Resume_AfterLongPause_RemainingUnchanged`, `Tick_PastEnd_FiresSessionEndedExactlyOnce`,
`Tick_JumpPastMultipleBoundaries_SingleTransition_AnchoredAtWake` (auto-start ON/OFF 변형),
`Tick_WhilePaused_TimeJump_NoTransition`, `Remaining_ClampsAtZero_NeverNegative`

자동 시작: `SessionEnd_AutoStartOn_NextRunningImmediately`, `SessionEnd_AutoStartOff_NextIdleFullDuration`

설정: `ParseClamped_ZeroNegativeTextHuge_ClampOrDefault` (Theory),
`LoadInto_MissingPomodoroKeys_AppliesDefaults`, `SettingsChange_MidSession_AppliesNextSessionOnly`

표시: `RemainingDisplay_FormatsEdgeDurations` ("00:00", "09:59", "120:00"),
`TrayTooltip_AllModesMaxDuration_Within63Chars`, `TrayTooltip_Idle_IsPlainNoticker`,
`TrayTooltip_Paused_ShowsPausedLabel`

배치: `ClampToScreen_MissingMonitor_FallsBackToPrimary`, `ClampToScreen_OffscreenCoords_ClampedIntoWorkArea`

수동 QA 체크리스트는 별도 테스트 플랜 아티팩트 참조
(`~/.gstack/projects/slpzjvl0102-Noticker/jungw-main-eng-review-test-plan-*.md`).

## 확정된 프리미스 / 결정 (사용자 승인 + autoplan 자동 결정, 2026-06-10)

1. 떠 있는 위젯 창 형태, 진입점 = 트레이 + 스티커 툴바 버튼 (사용자 확정)
2. v1은 기록/통계 없는 순수 타이머 (사용자 확정)
3. 앱 재시작 시 타이머 진행 상태 리셋 (사용자 확정)
4. 승인 확장: E1 트레이 툴팁 남은 시간, E2 always-on-top 토글
5. E3 진행률 시각화 **기각 확정** (서브픽셀 이동 문제 — §2 제외 목록 참조)
6. 단일 완료 카운터 의미론 (skip 미카운트) — 자동 결정, 최종 게이트에서 변경 가능

## NOT in scope (검토 후 명시적 연기)

- E4 "오늘 N세션" 카운터 — 통계 없음 프리미스와 충돌 → TODOS
- E5 Notion 세션 로그 sync — v1 범위 밖 → TODOS
- E6 스티커-태스크 연결 — 플랫폼 잠재력, 12개월 방향 → TODOS (SessionEndedEventArgs가 길을 열어둠)
- 공유 ResourceDictionary 추출 (FormatToggleStyle 등 중복 해소) — 리팩터 부채, 별도 작업
- DESIGN.md 작성 — /design-consultation 별도 실행 권장
- 44px 터치 타겟 — 앱 전체 22px 컨벤션, 고치려면 repo 전체 단위
- 1초 미만 tick 정밀도 (250-500ms) — v1 불필요

## What already exists (재사용 맵)

| 하위 문제 | 기존 코드 |
|----------|----------|
| 프레임 없는 창 + 드래그 + 그림자 | StickerWindow 이중 Border 구조 |
| 흑/백 스왑 | TitleBackground/BodyBackground 바인딩 + ColorSwapped PropertyChanged 패턴 |
| hide-on-X | StickerWindow OnClosing cancel + Hide |
| 버튼 스타일 | FormatToggleStyle, DeleteButtonStyle |
| 알림 | App.OnSyncError ShowBalloonTip |
| 설정 영속 | SettingsRepository KV + AppSettings.Instance + LoadInto |
| 모니터 clamp | App.RestoreStickers (→ ScreenPlacement로 추출) |
| 설정 UI 스타일 | SettingsWindow SectionHeadingStyle/FieldLabelStyle |
| 테스트 인프라 | Noticker.Tests xUnit (net8.0-windows) |

## Dream state delta

이 계획 후: capture 도구 + 집중 타이머. 12개월 이상향(메모·태스크·집중 허브)까지 남은 것:
스티커-타이머 연결(E6), 세션 로그 Notion sync(E5). `SessionEndedEventArgs` 설계로 두 확장 모두
시그니처 변경 없이 구독자 추가만으로 가능 — 이 계획은 이상향으로 가는 길을 막지 않음 (future-fit 4.5/5).

## Failure Modes Registry (리뷰에서 식별, 모두 계획에 반영됨)

| CODEPATH | FAILURE MODE | 대응 (계획 반영) | TEST? |
|----------|-------------|----------------|-------|
| tick → NotifyIcon.Text | 종료 중 disposed 아이콘에 쓰기 → 크래시 | ExitApp 타이머 우선 정지 + IsShuttingDown 가드 | 수동 QA |
| 세션 종료 → 자동 Show | WPF Show()가 기본 활성화 → 포커스 강탈 | `ShowActivated="False"` XAML 명시 | 수동 QA (critical path) |
| 슬립 다중 경계 + auto-start | 알림 폭주 / 음수 remaining | 단일 전이 규칙 (wake 기준) | T: JumpPastMultipleBoundaries |
| DateTime.Now 사용 | 시계 변경/DST로 타이머 동결 | UtcNow 주입 명시 | T: 시간 주입 전반 |
| 저장값 손상 ("abc", "0") | 0분 세션/크래시 | ParseClamped (로드+입력 양쪽) | T: ParseClamped, LoadInto |
| 툴팁 > 63자 | ArgumentException in tick | formatter 길이 가드 + 테스트 | T: Within63Chars |
| 숨김 창에 OfType+Activate | 트레이 클릭해도 안 나타남 | _pomodoroWindow 필드 + Show() 먼저 | 수동 QA |
| 핀 OFF + 사운드 OFF + DND | 무알림 데드존 | 알려진 한계로 문서화 (사용자 선택 결과) | — |

## Decision Audit Trail

| # | Phase | Decision | Classification | Principle | Rationale | Rejected |
|---|-------|----------|----------------|-----------|-----------|----------|
| 1 | CEO | Approach A (위젯+분리 서비스) | Mechanical | P1+P5 | B는 가시성 훼손, C는 테스트 불가 | B, C |
| 2 | CEO | E1 트레이 툴팁 승인 | Mechanical | P2 | blast radius 내, S effort | — |
| 3 | CEO | E2 핀 토글 승인 | Mechanical | P2 | 단일 파일, S effort | — |
| 4 | CEO | E4/E5/E6 → TODOS | Mechanical | P3 | 프리미스 충돌/범위 밖 | scope 포함 |
| 5 | CEO | 진입점 2개 (툴바 버튼) | User-stated | — | 사용자 직접 지정 | — |
| 6 | CEO→Eng | App이 서비스/타이머 소유 | Mechanical | P5 | 3-voice 합의 (CEO/Eng voice/Eng review) | 창 소유 |
| 7 | Eng | UtcNow 주입 | Mechanical | P1 | DST/시계 변경 엣지 | DateTime.Now |
| 8 | Eng | 슬립 다중 경계 = 단일 전이 | Mechanical | P5 | 알림 폭주 방지, 단순 | cascade 완료 |
| 9 | Eng | ShowActivated=False 명시 | Mechanical | P1 | 요구사항의 실제 구현 메커니즘 | Activate 미호출만 |
| 10 | Eng | 단일 완료 카운터 (skip 미카운트) | **Taste** | P5 | 2-카운터는 상태 표면 2배; 완료로만 긴 휴식 획득 | 2-카운터, skip 카운트 |
| 11 | Eng | ScreenPlacement 추출 | Mechanical | P4 | 호출자 2곳 + 동일 의미론 명시 | 복붙 |
| 12 | Eng | Idle 시 타이머 정지 | Mechanical | P3 | 유휴 1Hz 웨이크업 제거 | 상시 구동 |
| 13 | Design | E3 진행률 바 기각 | **Taste→해소** | P5 | 0.11px/sec 서브픽셀 — 물리적으로 무의미 | 채택 |
| 14 | Design | 레이아웃/토큰/글리프 명세 채택 | Mechanical | P1 | 시각 명세 부재 = 구현자가 10개 결정 발명 | 텍스트만 |
| 15 | Design | 이모지 금지, Path 글리프 | Mechanical | P5 | 흑/백 언어에 컬러 이모지 불가 | 📌 이모지 |
| 16 | Design | Asterisk 사운드 (Exclamation 아님) | **Taste** | P5 | 보상의 순간에 경고음은 부적합 | Exclamation |
| 17 | Design | 알림 채널 랭킹 재정렬 (위젯 표시 1순위) | Mechanical | P1 | 사운드는 무음/장치없음 시 조용히 실패 | 사운드 1순위 |
| 18 | Design | 툴바 구분선 + 시계 글리프 | Mechanical | P5 | 서식 그룹과 런처 분리 (CEO voice F5 완화) | 구분 없음 |
| 19 | Eng | 테스트 21+ 케이스 열거 | Mechanical | P1 | 시간 주입 설계의 존재 이유 | "상태 전이 테스트" 한 줄 |
| 20 | CEO | SessionEndedEventArgs (bare Action 금지) | Mechanical | P2 | E5/E6 future-fit, 1줄 비용 | bare event |

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 1 | CLEAR (via /autoplan) | 7 proposals, 3 accepted, 3 deferred, E3 기각 |
| Codex Review | `/codex review` | Independent 2nd opinion | 0 | — (codex 미설치) | — |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 1 | CLEAR (PLAN via /autoplan) | 24 issues → 전부 계획 반영, 0 critical gaps 잔존 |
| Design Review | `/plan-design-review` | UI/UX gaps | 1 | CLEAR (FULL via /autoplan) | score: 5/10 → 9/10, 8 amendments 채택 |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | — (DX scope 없음) | — |

- **VERDICT:** CEO + ENG + DESIGN CLEARED — ready to implement. Eng review required gate satisfied. Dual voices: subagent-only (codex 바이너리 없음).

NO UNRESOLVED DECISIONS
