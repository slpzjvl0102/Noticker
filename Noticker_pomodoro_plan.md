<!-- /autoplan restore point: /c/Users/jungw/.gstack/projects/slpzjvl0102-Noticker/main-autoplan-restore-20260610-141701.md -->
# Noticker 포모도로 타이머 기능 계획

Noticker(WPF + .NET 8 스티커메모 앱)에 포모도로 타이머를 추가하는 계획. 초안 v1.

## 1. 배경 / 동기

Noticker는 데스크탑 위에 떠 있는 capture 도구다. 메모를 적는 사람은 대부분 작업 중이고,
작업 중인 사람에게 가장 가까운 생산성 도구가 포모도로 타이머다. 별도 앱을 띄우는 대신
이미 떠 있는 Noticker 트레이에서 바로 타이머를 꺼내 쓸 수 있게 한다.

## 2. 목표 / 제외

### 목표

- 스티커와 같은 디자인 언어(흑/백 미니멀, WindowStyle=None)의 작은 포모도로 위젯 창
- 집중 25분 / 짧은 휴식 5분 / 긴 휴식 15분(4세션마다) 기본 사이클, 설정에서 변경 가능
- 세션 종료 시 확실한 알림 (트레이 풍선 + 사운드 + 위젯 자동 표시)
- 위젯을 숨겨도 타이머는 백그라운드에서 계속 동작
- 위젯 위치 영속 (재시작 후 같은 자리에 복원)

### 제외 (v1)

- 포모도로 기록/통계, Notion으로의 세션 로그 sync
- 태스크(스티커)와 타이머 연결
- 글로벌 hotkey
- 다중 타이머

## 3. 기능 명세

### 3.1 타이머 위젯 창 (PomodoroWindow)

- 열기: 트레이 우클릭 메뉴에 "포모도로 타이머" 항목 추가
- 표시 요소:
  - 남은 시간 `MM:SS` (큰 글씨)
  - 현재 모드 라벨: 집중 / 짧은 휴식 / 긴 휴식
  - 세션 진행 표시: ●●○○ (긴 휴식 간격 기준)
- 컨트롤: 시작/일시정지 토글 버튼, 리셋 버튼, 건너뛰기 버튼(현재 세션 종료하고 다음으로)
- 창 동작:
  - 상단 바 드래그로 이동, 고정 크기(리사이즈 없음)
  - X 버튼 = 창 숨기기 (타이머는 계속 동작) — 스티커의 hide 패턴 재사용
  - ShowInTaskbar=False, 트레이 메뉴로 다시 표시
  - 색상: AppSettings.ColorSwapped 전역 스왑 따름

### 3.2 타이머 엔진 (PomodoroService)

- DispatcherTimer 1초 tick, 종료 시각 기준 계산(절전/슬립 후 어긋남 방지: 남은 초를 빼는 게 아니라 `endTime - now`로 계산)
- 상태 모델: Mode(Focus/ShortBreak/LongBreak) × State(Idle/Running/Paused)
- 세션 종료 시:
  - 알림 발생 (3.3)
  - 자동 시작 옵션 ON이면 다음 세션 즉시 시작, OFF면 다음 세션 Idle 대기
  - Focus 완료 카운트 증가, 간격 도달 시 긴 휴식
- 앱 종료/재시작 시 타이머 진행 상태는 보존하지 않음 (Idle로 리셋)

### 3.3 알림

- 트레이 풍선 (기존 ShowBalloonTip 패턴 재사용): "집중 끝 — 5분 휴식하세요" 등
- 시스템 사운드 (SystemSounds, 설정에서 on/off)
- 위젯이 숨겨져 있으면 자동으로 Show + Activate

### 3.4 설정

SettingsWindow에 "포모도로" 섹션 추가:

- 집중 시간 (분, 기본 25, 범위 1-120)
- 짧은 휴식 (분, 기본 5, 범위 1-60)
- 긴 휴식 (분, 기본 15, 범위 1-60)
- 긴 휴식 간격 (세션 수, 기본 4, 범위 1-12)
- 다음 세션 자동 시작 (토글, 기본 OFF)
- 종료 사운드 (토글, 기본 ON)

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
| `pomodoro_window_x`, `pomodoro_window_y`, `pomodoro_monitor` | (위치 복원용) |

스키마 마이그레이션 불필요 (stickers 테이블 변경 없음).

## 5. 변경 파일

| 파일 | 변경 내용 |
|------|----------|
| `Windows/PomodoroWindow.xaml(.cs)` | 신규 — 위젯 UI |
| `Services/PomodoroService.cs` | 신규 — 타이머 상태 머신 |
| `Models/AppSettings.cs` | 포모도로 설정 프로퍼티 추가 |
| `Data/SettingsRepository.cs` | LoadInto에 신규 키 로드/저장 |
| `App.xaml.cs` | 트레이 메뉴 항목, OpenPomodoro(), 알림 연결 |
| `Windows/SettingsWindow.xaml(.cs)` | 포모도로 설정 섹션 |
| `Noticker.Tests/PomodoroServiceTests.cs` | 신규 — 상태 전이/사이클 테스트 |

## 6. 엣지 케이스

| 시나리오 | 처리 |
|----------|------|
| PC 슬립 후 복귀 | endTime 기준 재계산 — 슬립 중 시간 경과 반영, 이미 지났으면 종료 처리 |
| 타이머 동작 중 설정 변경 | 현재 세션은 기존 시간 유지, 다음 세션부터 새 설정 적용 |
| 위젯 모니터 분리 | 스티커와 동일한 clamp/primary fallback 패턴 |
| 앱 종료 시 타이머 동작 중 | 그냥 종료 (상태 미보존, v1 단순화) |
| 잘못된 설정 입력 (0, 음수, 텍스트) | 범위 클램프 + 기본값 fallback |
