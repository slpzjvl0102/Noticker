# 글로벌 hotkey로 스티커 생성 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 시스템 전역 단축키(기본 Ctrl+Alt+N, 프리셋 4종 설정 가능)로 어디서든 새 스티커를 만들고 즉시 타이핑할 수 있게 한다.

**Architecture:** `Infrastructure/HotkeyPresets`(순수 매핑, 단위 테스트)와 `Infrastructure/HotkeyManager`(메시지 전용 HwndSource + RegisterHotKey, IDisposable)를 신규 작성하고, App.xaml.cs가 `ApplyHotkey()`로 시작·설정 변경 양쪽에서 등록을 적용한다. 설정은 기존 `autostart_enabled` 패턴(`AppSettings` 프로퍼티 + `SettingsRepository` key + SettingsWindow 위젯) 그대로.

**Tech Stack:** WPF (net8.0-windows), P/Invoke user32!RegisterHotKey, SQLite settings, xUnit.

**스펙:** `docs/superpowers/specs/2026-06-11-global-hotkey-design.md` (승인됨, D1–D5)

**빌드/테스트 명령** (모든 태스크 공통):
- 빌드: `taskkill //IM Noticker.exe //F` (실행 중일 때만 필요) 후 `dotnet build Noticker.csproj`
- 테스트: `dotnet test Noticker.Tests/Noticker.Tests.csproj` (시작 시점 257개 통과)

---

### Task 1: HotkeyPresets — 프리셋 매핑 (TDD)

**Files:**
- Create: `Infrastructure/HotkeyPresets.cs`
- Test: `Noticker.Tests/HotkeyPresetsTests.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`Noticker.Tests/HotkeyPresetsTests.cs` 신규:

```csharp
using Noticker.Infrastructure;

namespace Noticker.Tests;

public class HotkeyPresetsTests
{
    [Fact]
    public void Resolve_CtrlAltN_MapsToControlAltN()
    {
        var r = HotkeyPresets.Resolve("ctrl_alt_n");
        Assert.NotNull(r);
        Assert.Equal(
            HotkeyPresets.ModControl | HotkeyPresets.ModAlt | HotkeyPresets.ModNoRepeat,
            r.Value.Modifiers);
        Assert.Equal(0x4Eu, r.Value.Vk);   // 'N'
    }

    [Fact]
    public void Resolve_WinShiftN_MapsToWinShiftN()
    {
        var r = HotkeyPresets.Resolve("win_shift_n");
        Assert.NotNull(r);
        Assert.Equal(
            HotkeyPresets.ModWin | HotkeyPresets.ModShift | HotkeyPresets.ModNoRepeat,
            r.Value.Modifiers);
        Assert.Equal(0x4Eu, r.Value.Vk);
    }

    [Fact]
    public void Resolve_CtrlAltSpace_MapsToControlAltSpace()
    {
        var r = HotkeyPresets.Resolve("ctrl_alt_space");
        Assert.NotNull(r);
        Assert.Equal(
            HotkeyPresets.ModControl | HotkeyPresets.ModAlt | HotkeyPresets.ModNoRepeat,
            r.Value.Modifiers);
        Assert.Equal(0x20u, r.Value.Vk);   // Space
    }

    [Fact]
    public void Resolve_None_ReturnsNull()
    {
        Assert.Null(HotkeyPresets.Resolve("none"));
    }

    [Fact]
    public void Resolve_UnknownKey_FallsBackToDefault()
    {
        // DB에 잡값이 남아도 안전 — 기본 조합으로 폴백 (스펙 §1)
        Assert.Equal(HotkeyPresets.Resolve(HotkeyPresets.DefaultKey),
            HotkeyPresets.Resolve("garbage_value"));
    }

    [Theory]
    [InlineData("ctrl_alt_n")]
    [InlineData("win_shift_n")]
    [InlineData("ctrl_alt_space")]
    public void Resolve_AllCombos_IncludeNoRepeat(string key)
    {
        // 키를 누르고 있을 때 스티커 연속 생성 방지 (스펙 §1)
        var r = HotkeyPresets.Resolve(key);
        Assert.NotNull(r);
        Assert.Equal(HotkeyPresets.ModNoRepeat, r.Value.Modifiers & HotkeyPresets.ModNoRepeat);
    }

    [Theory]
    [InlineData("ctrl_alt_n", "Ctrl+Alt+N")]
    [InlineData("win_shift_n", "Win+Shift+N")]
    [InlineData("ctrl_alt_space", "Ctrl+Alt+Space")]
    [InlineData("none", "사용 안 함")]
    public void DisplayName_KnownKeys(string key, string expected)
    {
        Assert.Equal(expected, HotkeyPresets.DisplayName(key));
    }

    [Fact]
    public void DisplayName_UnknownKey_FallsBackToDefault()
    {
        Assert.Equal("Ctrl+Alt+N", HotkeyPresets.DisplayName("garbage_value"));
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 컴파일 에러 — `HotkeyPresets`가 존재하지 않음 (CS0103/CS0246)

- [ ] **Step 3: 최소 구현**

`Infrastructure/HotkeyPresets.cs` 신규:

```csharp
namespace Noticker.Infrastructure;

// 전역 단축키 프리셋 키 → Win32 (modifiers, vk) 매핑 — 순수 로직, Win32 호출 없음.
// 자유 입력 없이 검증된 조합만 허용 (스펙 D2). Ctrl+Shift+N은 Chrome 시크릿 창과
// 충돌해 후보에서 제외됐다 (스펙 D1)
public static class HotkeyPresets
{
    public const string DefaultKey = "ctrl_alt_n";

    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModWin = 0x0008;
    // 키를 누르고 있을 때 WM_HOTKEY 반복 발생 방지 — 스티커 연속 생성 차단
    public const uint ModNoRepeat = 0x4000;

    private const uint VkN = 0x4E;
    private const uint VkSpace = 0x20;

    // 'none' → null (등록 안 함). 알 수 없는 키 → 기본 조합 폴백 (DB 잡값 안전)
    public static (uint Modifiers, uint Vk)? Resolve(string presetKey) => presetKey switch
    {
        "none" => null,
        "win_shift_n" => (ModWin | ModShift | ModNoRepeat, VkN),
        "ctrl_alt_space" => (ModControl | ModAlt | ModNoRepeat, VkSpace),
        _ => (ModControl | ModAlt | ModNoRepeat, VkN),   // ctrl_alt_n + 잡값 폴백
    };

    // 풍선/설정 콤보 표시용
    public static string DisplayName(string presetKey) => presetKey switch
    {
        "none" => "사용 안 함",
        "win_shift_n" => "Win+Shift+N",
        "ctrl_alt_space" => "Ctrl+Alt+Space",
        _ => "Ctrl+Alt+N",
    };
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS — 257 + 13 = 270개 전부 통과 (Fact 6 + Theory 케이스 7 추가)

- [ ] **Step 5: 커밋**

```bash
git add Infrastructure/HotkeyPresets.cs Noticker.Tests/HotkeyPresetsTests.cs
git commit -m "feat: hotkey preset mapping (pure logic, TDD)"
```

---

### Task 2: 설정 영속 — AppSettings.HotkeyPreset + LoadInto (TDD)

**Files:**
- Modify: `Models/AppSettings.cs:35` (AutostartEnabled 아래)
- Modify: `Data/SettingsRepository.cs:76` (LoadInto의 autostart 줄 아래)
- Test: `Noticker.Tests/SettingsRepositoryTests.cs` (LoadInto 섹션에 추가)

- [ ] **Step 1: 실패하는 테스트 작성**

`Noticker.Tests/SettingsRepositoryTests.cs`의 `LoadInto_AutostartFalse_Loaded` 테스트(108–114행) 뒤에 추가:

```csharp
    [Fact]
    public void LoadInto_MissingHotkeyPreset_DefaultsCtrlAltN()
    {
        AppSettings.Instance.HotkeyPreset = "stale";   // 잡값
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("ctrl_alt_n", AppSettings.Instance.HotkeyPreset);
    }

    [Fact]
    public void LoadInto_HotkeyPreset_Loaded()
    {
        _repo.Set("hotkey_preset", "win_shift_n");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("win_shift_n", AppSettings.Instance.HotkeyPreset);
    }
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 컴파일 에러 — `AppSettings`에 `HotkeyPreset` 없음 (CS1061)

- [ ] **Step 3: 최소 구현**

`Models/AppSettings.cs` — `AutostartEnabled`(35행) 아래에 추가:

```csharp
    public string HotkeyPreset { get; set; } = "ctrl_alt_n";   // HotkeyPresets 프리셋 키
```

`Data/SettingsRepository.cs` — `LoadInto`의 `settings.AutostartEnabled = ...`(76행) 아래에 추가:

```csharp
        settings.HotkeyPreset = Get("hotkey_preset") ?? "ctrl_alt_n";
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS — 270 + 2 = 272개 전부 통과

- [ ] **Step 5: 커밋**

```bash
git add Models/AppSettings.cs Data/SettingsRepository.cs Noticker.Tests/SettingsRepositoryTests.cs
git commit -m "feat: persist hotkey preset setting (default ctrl_alt_n)"
```

---

### Task 3: HotkeyManager — Win32 래퍼

Win32(HwndSource/RegisterHotKey)는 STA·메시지 펌프 의존이라 단위 테스트 부적합 (스펙 §6) — 빌드로 검증하고 수동 QA(Task 6)로 확인한다.

**Files:**
- Create: `Infrastructure/HotkeyManager.cs`

- [ ] **Step 1: 구현**

`Infrastructure/HotkeyManager.cs` 신규:

```csharp
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Noticker.Infrastructure;

// RegisterHotKey 래퍼 — 메시지 전용 HwndSource로 WM_HOTKEY를 수신한다.
// 트레이 앱이라 항상 떠 있는 창이 없어 수신 전용 HWND가 필요 (스펙 D4).
// Win32 의존을 이 클래스에 격리 — 단위 테스트 대상 아님 (수동 QA, 스펙 §6)
public sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;
    private const int HotkeyId = 1;
    private static readonly IntPtr HwndMessage = new(-3);   // HWND_MESSAGE

    private readonly HwndSource _source;
    private bool _registered;
    private bool _disposed;

    public event Action? Pressed;

    public HotkeyManager()
    {
        // UI 스레드에서 생성할 것 — AddHook 콜백이 생성 스레드로 들어온다
        _source = new HwndSource(new HwndSourceParameters("NotickerHotkey")
        {
            ParentWindow = HwndMessage,
        });
        _source.AddHook(WndProc);
    }

    // 기존 등록 해제 후 새 조합 등록. 실패(타 앱 점유) 시 false — 등록 없음 상태
    public bool Register(uint modifiers, uint vk)
    {
        Unregister();
        _registered = RegisterHotKey(_source.Handle, HotkeyId, modifiers, vk);
        return _registered;
    }

    public void Unregister()
    {
        if (!_registered) return;
        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && wParam.ToInt32() == HotkeyId)
        {
            Pressed?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    // 프로세스 종료 시 OS가 자동 해제하지만 명시적으로 정리 — ExitApp 관례와 동일.
    // ExitApp과 OnExit 양쪽에서 불려도 안전하게 멱등
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Unregister();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
```

- [ ] **Step 2: 빌드 확인**

Run: `dotnet build Noticker.csproj` (앱 실행 중이면 먼저 `taskkill //IM Noticker.exe //F`)
Expected: Build succeeded, 0 errors

- [ ] **Step 3: 기존 테스트 회귀 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS — 272개 전부 통과 (신규 테스트 없음)

- [ ] **Step 4: 커밋**

```bash
git add Infrastructure/HotkeyManager.cs
git commit -m "feat: HotkeyManager — message-only HwndSource + RegisterHotKey wrapper"
```

---

### Task 4: App 통합 — 시작 등록·핸들러·정리

**Files:**
- Modify: `App.xaml.cs` (필드 ~18행, OnStartup 81행, CreateSticker 194행, ExitApp 608행, OnExit 627행)
- Modify: `Windows/StickerWindow.xaml.cs` (public 메서드 1개 추가)

- [ ] **Step 1: StickerWindow.FocusBody 추가**

`Windows/StickerWindow.xaml.cs` — `BodyBox_SelectionChanged`(262–263행) 위에 추가:

```csharp
    // 전역 단축키 생성 경로 — 즉시 타이핑 가능하게 본문에 포커스 (App.OnHotkeyPressed)
    public void FocusBody() => BodyBox.Focus();
```

- [ ] **Step 2: CreateSticker 반환 타입 변경**

`App.xaml.cs:194` — `public void CreateSticker()`를 다음으로 교체 (기존 호출자 2곳 — 트레이 메뉴 람다 166행, ShowPostOnboardingUi 487행 — 은 반환값을 무시하므로 동작 변경 없음):

```csharp
    public StickerWindow CreateSticker()
    {
        var screen = GetActiveScreen();
        var wa = screen.WorkingArea;
        int x = wa.Left + (wa.Width - 250) / 2;
        int y = wa.Top + (wa.Height - 300) / 2;

        var s = new Sticker
        {
            MonitorDeviceName = screen.DeviceName,
            PositionX = x - wa.Left,
            PositionY = y - wa.Top,
        };
        StickerRepo!.Insert(s);
        OpenStickerWindow(s, x, y);
        return _stickerWindows[s.Id];
    }
```

- [ ] **Step 3: hotkey 필드·초기화·핸들러 추가**

`App.xaml.cs` — `private NotifyIcon? _trayIcon;`(18행) 아래에 필드 추가:

```csharp
    private HotkeyManager? _hotkey;
```

`OnStartup`의 `InitTray();`(81행) 바로 아래에 호출 추가 (풍선은 트레이 생성 이후여야 보인다):

```csharp
        InitTray();
        InitHotkey();
```

`InitTray()` 메서드(148–175행) 끝 뒤에 메서드 3개 추가:

```csharp
    private void InitHotkey()
    {
        _hotkey = new HotkeyManager();
        _hotkey.Pressed += OnHotkeyPressed;
        if (!ApplyHotkey())
        {
            // 시작 시 실패(타 앱 점유) — 풍선 1회, 앱은 정상 동작 (스펙 §5)
            var name = HotkeyPresets.DisplayName(AppSettings.Instance.HotkeyPreset);
            _trayIcon?.ShowBalloonTip(6000, "Noticker",
                $"단축키 등록 실패 — {name}을(를) 다른 앱이 사용 중입니다. 설정에서 변경하세요.",
                ToolTipIcon.Warning);
        }
    }

    // 시작·설정 변경 공용 — AppSettings의 프리셋을 실제 등록에 반영. 성공 여부 반환.
    // SettingsWindow가 변경 적용 시 호출
    public bool ApplyHotkey()
    {
        if (_hotkey is null) return false;
        var combo = HotkeyPresets.Resolve(AppSettings.Instance.HotkeyPreset);
        if (combo is null) { _hotkey.Unregister(); return true; }   // 사용 안 함
        return _hotkey.Register(combo.Value.Modifiers, combo.Value.Vk);
    }

    // 단축키 = 즉시 캡처 — 생성 후 창 활성화 + 본문 포커스 (바로 타이핑).
    // 사용자가 등록된 hotkey를 누른 직후라 OS가 포그라운드 전환을 허용한다
    private void OnHotkeyPressed()
    {
        if (IsShuttingDown) return;
        var win = CreateSticker();
        win.Activate();
        win.FocusBody();
    }
```

- [ ] **Step 4: 종료 정리**

`ExitApp()`(608행) — `_trayIcon?.Dispose();` 위에 추가:

```csharp
        _hotkey?.Dispose();
```

`OnExit()`(627행) — `_trayIcon?.Dispose();` 위에 추가 (Dispose는 멱등):

```csharp
        _hotkey?.Dispose();
```

- [ ] **Step 5: 빌드 + 테스트 확인**

Run: `taskkill //IM Noticker.exe //F` (실행 중이면) 후 `dotnet build Noticker.csproj`, 이어서 `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: Build succeeded · 272개 전부 통과

- [ ] **Step 6: 커밋**

```bash
git add App.xaml.cs Windows/StickerWindow.xaml.cs
git commit -m "feat: register global hotkey on startup — create + focus sticker"
```

---

### Task 5: SettingsWindow — 프리셋 콤보 + 실패 시 복원

**Files:**
- Modify: `Windows/SettingsWindow.xaml` (옵션 섹션, AutostartCheck 175행 아래)
- Modify: `Windows/SettingsWindow.xaml.cs` (LoadCurrentValues, SaveButton_Click, PersistSettings)

- [ ] **Step 1: XAML — 콤보 + 인라인 에러 텍스트**

`Windows/SettingsWindow.xaml` — `<CheckBox x:Name="AutostartCheck" .../>`(175행) 바로 아래에 추가:

```xml
                <TextBlock Text="새 스티커 단축키 (전역)" Style="{StaticResource FieldLabelStyle}"
                           Margin="0,8,0,4"/>
                <ComboBox x:Name="HotkeyCombo" Width="180" HorizontalAlignment="Left"
                          FontSize="13" Margin="0,0,0,4">
                    <ComboBoxItem Content="Ctrl+Alt+N" Tag="ctrl_alt_n"/>
                    <ComboBoxItem Content="Win+Shift+N" Tag="win_shift_n"/>
                    <ComboBoxItem Content="Ctrl+Alt+Space" Tag="ctrl_alt_space"/>
                    <ComboBoxItem Content="사용 안 함" Tag="none"/>
                </ComboBox>
                <TextBlock x:Name="HotkeyStatusText" FontSize="12" Foreground="#C0392B"
                           Margin="0,0,0,8" Visibility="Collapsed"/>
```

- [ ] **Step 2: code-behind — 로드·적용·복원·영속**

`Windows/SettingsWindow.xaml.cs`:

(a) `LoadCurrentValues()`의 `AutostartCheck.IsChecked = app.AutostartEnabled;`(34행) 아래에 추가:

```csharp
        SelectHotkeyItem(app.HotkeyPreset);
```

(b) `LoadCurrentValues()` 메서드 끝(47행 `}`) 뒤에 헬퍼 추가:

```csharp
    private void SelectHotkeyItem(string presetKey)
    {
        foreach (System.Windows.Controls.ComboBoxItem item in HotkeyCombo.Items)
            if ((string)item.Tag == presetKey) { HotkeyCombo.SelectedItem = item; return; }
        HotkeyCombo.SelectedIndex = 0;   // 잡값 — 기본 Ctrl+Alt+N (HotkeyPresets.Resolve 폴백과 일치)
    }
```

(c) `SaveButton_Click`(81–86행)을 다음으로 교체 — hotkey 적용 실패 시 창을 닫지 않아야 인라인 에러가 보인다:

```csharp
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (!ApplyHotkeySetting()) return;   // 실패 — 창 유지, 인라인 에러 표시됨
        ApplyToAppSettings();
        PersistSettings();
        Close();
    }

    // 단축키 적용 + 실패 시 이전 값 복원 (스펙 §4 저장 흐름). 성공 여부 반환.
    // 실패 시 다른 설정도 저장되지 않는다 — 사용자가 조합을 고치고 다시 저장
    private bool ApplyHotkeySetting()
    {
        var app = AppSettings.Instance;
        var old = app.HotkeyPreset;
        var selected = (string)((System.Windows.Controls.ComboBoxItem)HotkeyCombo.SelectedItem).Tag;
        if (selected == old) return true;    // 변경 없음 — 재등록 불필요

        app.HotkeyPreset = selected;
        if (App.Current.ApplyHotkey())
        {
            HotkeyStatusText.Visibility = Visibility.Collapsed;
            return true;
        }

        app.HotkeyPreset = old;
        App.Current.ApplyHotkey();           // 직전까지 들고 있던 조합 재등록 — best effort
        SelectHotkeyItem(old);
        HotkeyStatusText.Text = "이 조합은 다른 앱이 사용 중입니다.";
        HotkeyStatusText.Visibility = Visibility.Visible;
        return false;
    }
```

(d) `PersistSettings()`의 `_settings.Set("autostart_enabled", ...)`(113행) 아래에 추가 — 복원 후의 최종 값만 영속되므로 실패한 새 값이 DB에 남지 않는다 (스펙 §4):

```csharp
        _settings.Set("hotkey_preset", app.HotkeyPreset);
```

- [ ] **Step 3: 빌드 + 테스트 확인**

Run: `taskkill //IM Noticker.exe //F` (실행 중이면) 후 `dotnet build Noticker.csproj`, 이어서 `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: Build succeeded · 272개 전부 통과

- [ ] **Step 4: 커밋**

```bash
git add Windows/SettingsWindow.xaml Windows/SettingsWindow.xaml.cs
git commit -m "feat: hotkey preset combo in settings — inline error + revert on conflict"
```

---

### Task 6: 전체 검증 + 수동 QA 준비

**Files:** 없음 (검증만)

- [ ] **Step 1: 전체 테스트**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 272개 전부 통과

- [ ] **Step 2: 앱 실행**

Run: `taskkill //IM Noticker.exe //F` 후 `dotnet build Noticker.csproj`, 이어서 `start "" "bin/Debug/net8.0-windows/Noticker.exe"`
Expected: 트레이 아이콘 정상, 에러 풍선 없음 (Ctrl+Alt+N이 점유되지 않은 환경 기준)

- [ ] **Step 3: 사용자 수동 QA 안내** (스펙 §6 시나리오 — 사용자가 직접 수행, push는 QA 확인 후)

1. 타 앱 포커스 상태에서 Ctrl+Alt+N → 새 스티커 생성 + 즉시 타이핑 가능
2. 키를 누르고 있어도 스티커 1개만 생성 (MOD_NOREPEAT)
3. 설정에서 Win+Shift+N으로 변경 → 새 조합 즉시 작동, 옛 조합 무반응, 재시작 후 유지
4. 타 앱이 점유한 조합으로 변경 시도 → 인라인 에러 + 이전 조합 유지 + 창 안 닫힘
5. 점유된 기본 조합으로 앱 시작 → 트레이 풍선, 앱 정상 동작
6. "사용 안 함" → 무반응, 재시작 후에도 무반응
