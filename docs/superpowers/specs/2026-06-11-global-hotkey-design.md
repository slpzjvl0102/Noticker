# 글로벌 hotkey로 스티커 생성 — 설계

날짜: 2026-06-11 · 브랜치: main · 상태: 승인됨 (사용자 D1–D5 결정 반영)

## 배경

- TODOS.md (M, P2): 시스템 전역 단축키 → 새 스티커. CEO 리뷰가 두 계획 연속
  지적 — capture 도구의 최고 레버리지 기능(캡처 마찰 제거)이 계속 제외됨.
- 코드베이스에 Win32 hotkey 인프라 0건 (RegisterHotKey/HwndSource/WndProc/AddHook
  참조 없음) — 신규 작성. `Infrastructure/`에 StartupManager·ScreenPlacement 등
  순수 헬퍼 패턴이 확립돼 있어 같은 위치에 추가한다.
- Noticker는 트레이 앱이라 항상 떠 있는 창이 없음 → WM_HOTKEY를 받을 메시지 전용
  창이 필요하다.

## 범위 (사용자 결정)

| 결정 | 선택 |
|---|---|
| D1 기본 키 조합 | Ctrl+Alt+N (Ctrl+Shift+N은 Chrome 시크릿 창·탐색기와 충돌해 제외) |
| D2 설정 변경 | 프리셋 콤보 4종 (자유 입력/키 녹화 없음) |
| D3 등록 실패 처리 | 시작 시=트레이 풍선, 설정 변경 시=인라인 에러+이전 값 복원 |
| D4 구현 접근 | RegisterHotKey + 메시지 전용 HwndSource (저수준 키보드 훅 제외) |

비범위: 자유 키 조합 입력 UI, 단축키 여러 개(노트 목록 열기 등), Linux/macOS.

## 1. Infrastructure/HotkeyPresets.cs (신규, 정적·순수)

프리셋 키 문자열 → Win32 (modifiers, vk) 매핑. HTTP·WPF·Win32 호출 없이
단위 테스트 가능.

| 프리셋 키 | 표시명 | Modifiers | VK |
|---|---|---|---|
| `ctrl_alt_n` (기본) | Ctrl+Alt+N | MOD_CONTROL\|MOD_ALT | 0x4E (N) |
| `win_shift_n` | Win+Shift+N | MOD_WIN\|MOD_SHIFT | 0x4E (N) |
| `ctrl_alt_space` | Ctrl+Alt+Space | MOD_CONTROL\|MOD_ALT | 0x20 (Space) |
| `none` | 사용 안 함 | — (null 반환) | — |

- `static (uint Modifiers, uint Vk)? Resolve(string presetKey)`:
  `none` → null (등록 안 함), 알 수 없는 문자열 → `ctrl_alt_n` 매핑으로 폴백
  (DB에 잡값이 남아도 안전 — SettingsRepository의 관용 파싱 관례와 동일).
- 모든 조합에 `MOD_NOREPEAT`(0x4000)를 OR — 키를 누르고 있을 때 스티커가
  연속 생성되는 것을 방지.
- `static string DisplayName(string presetKey)` — 풍선/콤보 표시용.

## 2. Infrastructure/HotkeyManager.cs (신규, IDisposable)

Win32 전부를 이 클래스에 격리. 얇은 래퍼로 유지하고 자동 테스트 대상에서 제외
(수동 QA — §6).

- 생성자: 메시지 전용 `HwndSource` 생성
  (`HwndSourceParameters` + `ParentWindow = HWND_MESSAGE(-3)`), `AddHook`으로
  `WM_HOTKEY`(0x0312) 수신. UI 스레드에서 생성할 것 (App.OnStartup).
- `bool Register(uint modifiers, uint vk)`: 현재 등록이 있으면
  `UnregisterHotKey` 후 새로 `RegisterHotKey`(id는 고정 상수 1). 성공 여부 반환.
- `void Unregister()`: 등록 해제 (none 선택·Dispose 경로).
- `event Action? Pressed`: WM_HOTKEY 수신 시 발생. AddHook 콜백은 UI 스레드라
  Dispatcher 마샬링 불필요.
- `Dispose()`: Unregister + `HwndSource.Dispose()`. (프로세스 종료 시 OS가
  자동 해제하지만 명시적으로 정리 — ExitApp 관례와 동일.)
- P/Invoke: `user32!RegisterHotKey`, `user32!UnregisterHotKey`.

## 3. App.xaml.cs 통합

- 필드 `private HotkeyManager? _hotkey;`
- `OnStartup`의 `InitTray()` 직후:
  1. `_hotkey = new HotkeyManager()`, `Pressed` 구독.
  2. `ApplyHotkey()` 호출 — 실패 시(타 앱 점유) 트레이 풍선 1회:
     "단축키 등록 실패 — {표시명}을(를) 다른 앱이 사용 중입니다. 설정에서 변경하세요."
     앱은 정상 동작 계속.
- `Pressed` 핸들러: `CreateSticker()`가 만든 창을 `Activate()` + 본문 포커스 —
  단축키 목적이 즉시 타이핑이므로. 사용자가 우리 hotkey를 눌렀을 때는 OS가
  포그라운드 전환 권한을 부여하므로 Activate가 동작한다.
  - `CreateSticker()`의 반환 타입을 `void` → `StickerWindow`로 변경 (기존
    호출자 2곳 — 트레이 메뉴 람다, ShowPostOnboardingUi — 은 반환값 무시,
    동작 변경 없음).
  - `StickerWindow`에 `public void FocusBody() => BodyBox.Focus();` 한 줄 추가.
- `public bool ApplyHotkey()`: `AppSettings.Instance.HotkeyPreset`을
  `HotkeyPresets.Resolve`로 풀어 `_hotkey.Register`/`Unregister` 적용, 성공 여부
  반환. `none`(null)은 Unregister 후 true. 시작 경로와 설정 변경 경로가 공유.
- `ExitApp`·`OnExit`에서 `_hotkey?.Dispose()`.

## 4. 설정

기존 `autostart_enabled` 패턴 그대로:

- `AppSettings.HotkeyPreset` (string, 기본 `"ctrl_alt_n"`).
- `SettingsRepository.LoadInto`:
  `settings.HotkeyPreset = Get("hotkey_preset") ?? "ctrl_alt_n"`.
- SettingsWindow 옵션 섹션에 ComboBox 4항목 (Ctrl+Alt+N / Win+Shift+N /
  Ctrl+Alt+Space / 사용 안 함). 항목 Tag에 프리셋 키 저장.
- 저장 흐름 (Save 핸들러):
  1. `old = AppSettings.HotkeyPreset`; 새 값 대입.
  2. `App.Current.ApplyHotkey()` 실패 시: `AppSettings.HotkeyPreset = old` 복원,
     `ApplyHotkey()` 재호출(직전까지 들고 있던 조합이라 사실상 성공 — 실패해도
     무시, best-effort), 콤보 선택을 old로 되돌리고 인라인 에러 표시:
     "이 조합은 다른 앱이 사용 중입니다." (기존 SettingsWindow 상태 텍스트 패턴).
  3. 성공 시에만 저장이 계속 진행돼 `AppSettings.HotkeyPreset` 값이
     `_settings.Set("hotkey_preset", …)`로 영속된다. 실패 시에는 저장 전체가
     중단된다(창 유지 — 인라인 에러가 실제로 보이도록) — 실패한 새 값이 DB에
     남지 않는다.

## 5. 에러 처리 요약

| 상황 | 처리 |
|---|---|
| 시작 시 등록 실패 (타 앱 점유) | 트레이 풍선 1회, 앱 정상 동작 |
| 설정 변경 시 등록 실패 | 인라인 에러 + 설정·콤보 이전 값 복원, DB에 실패 값 미영속 |
| `사용 안 함` | Unregister만, 에러 아님 |
| DB의 잡값 프리셋 키 | `ctrl_alt_n`으로 폴백 (Resolve) |
| 종료 | Dispose에서 Unregister + HwndSource 정리 |

## 6. 테스트

자동 (xUnit, 기존 Noticker.Tests):
- `HotkeyPresets.Resolve` 전수: 4 프리셋 매핑값, `none` → null, 잡값 →
  ctrl_alt_n 폴백, MOD_NOREPEAT 포함 여부.
- `SettingsRepository` 왕복: `hotkey_preset` Set/Get, 미설정 시 기본
  `ctrl_alt_n` (기존 SettingsRepositoryTests 패턴).

수동 QA (Win32는 STA·메시지 펌프 의존이라 단위 테스트 부적합):
1. 타 앱 포커스 상태에서 Ctrl+Alt+N → 새 스티커 생성 + 포커스(즉시 타이핑 가능).
2. 키를 누르고 있어도 스티커 1개만 생성 (MOD_NOREPEAT).
3. 설정에서 Win+Shift+N으로 변경 → 새 조합 즉시 작동, 옛 조합 무반응, 재시작 후 유지.
4. 타 앱(예: AutoHotkey)이 점유한 조합으로 변경 시도 → 인라인 에러 + 이전 조합 유지.
5. 점유된 기본 조합으로 앱 시작 → 트레이 풍선, 앱 정상 동작.
6. "사용 안 함" → 무반응, 재시작 후에도 무반응.
