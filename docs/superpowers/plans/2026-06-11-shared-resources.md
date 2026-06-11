# 공유 ResourceDictionary 추출 + DESIGN.md Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 창 4개에 복제된 스타일과 매직 색 hex를 `Styles/SharedStyles.xaml` 하나로 모으고 (픽셀 변화 0), 디자인 토큰을 `DESIGN.md`로 문서화한다.

**Architecture:** SolidColorBrush 토큰 11개 + 공유 스타일 5개를 담은 ResourceDictionary를 App.xaml `MergedDictionaries`로 병합. 암시적 스타일은 App 전역에 올리지 않고 각 창에 `BasedOn` 래퍼 한 줄만 남긴다. 테마 바인딩 창(Sticker/Pomodoro + NoteList 카드 색)은 불가침.

**Tech Stack:** WPF (net8.0-windows) XAML ResourceDictionary. 코드 비하인드 변경 없음.

**스펙:** `docs/superpowers/specs/2026-06-11-shared-resources-design.md` (승인됨, D1–D4)

**빌드/테스트 명령** (모든 태스크 공통):
- 빌드: `taskkill //IM Noticker.exe //F` (실행 중일 때만) 후 `dotnet build Noticker.csproj`
- 테스트: `dotnet test Noticker.Tests/Noticker.Tests.csproj` (272개 — 이 작업으로 증감 없음)

**불변식 (모든 태스크):**
1. **픽셀 변화 0** — 색·여백·크기 값을 바꾸지 않는다. 유일한 예외: Settings 저장 버튼이 disabled 트리거를 얻음 (스펙 승인된 분기 버그 수정).
2. **StickerWindow.xaml / PomodoroWindow.xaml은 절대 건드리지 않는다** — diff에 나타나면 스펙 위반.
3. XAML은 단위 테스트가 없다 — 리소스 키 오타는 **창을 열 때** XamlParseException으로 터진다. 각 태스크의 빌드 통과는 키 해석을 보장하지 않으므로 Task 6의 실행 QA가 최종 게이트.

---

### Task 1: SharedStyles.xaml 생성 + App.xaml 병합

**Files:**
- Create: `Styles/SharedStyles.xaml`
- Modify: `App.xaml`

- [ ] **Step 1: SharedStyles.xaml 작성** — `Styles/SharedStyles.xaml` 신규 (csproj 변경 불필요 — WPF SDK가 .xaml을 Page로 자동 포함):

```xml
<ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">

    <!-- ── 팔레트 토큰 (라이트 테마 정적 창 전용) ─────────────────────────────
         불변식: 값 변경 금지 — 추출은 이름 부여만 (픽셀 변화 0).
         스티커/포모도로/노트목록 카드 색은 ViewModel 바인딩(노션 카테고리 색 등
         런타임 데이터)이라 여기 두지 않는다 (스펙 §2). 목록은 DESIGN.md와 1:1 -->
    <SolidColorBrush x:Key="SurfaceBrush" Color="#FAFAFA"/>
    <SolidColorBrush x:Key="InkBrush" Color="#1A1A1A"/>
    <SolidColorBrush x:Key="InkHoverBrush" Color="#333333"/>
    <SolidColorBrush x:Key="TextMutedBrush" Color="#666666"/>
    <SolidColorBrush x:Key="TextSectionBrush" Color="#777777"/>
    <SolidColorBrush x:Key="BorderStrongBrush" Color="#CCCCCC"/>
    <SolidColorBrush x:Key="BorderInputBrush" Color="#DDDDDD"/>
    <SolidColorBrush x:Key="BorderHoverBrush" Color="#999999"/>
    <SolidColorBrush x:Key="ControlHoverBrush" Color="#F0F0F0"/>
    <SolidColorBrush x:Key="ControlPressedBrush" Color="#E4E4E4"/>
    <SolidColorBrush x:Key="DangerBrush" Color="#C0392B"/>

    <!-- ── 공유 스타일 ───────────────────────────────────────────────────────
         Onboarding↔Settings 복제분 통합. Margin은 의도적으로 제외 — 창/인스턴스가
         보유한다 (두 창의 복제판이 Margin만 달랐음) -->

    <!-- 외곽선 보조 버튼 -->
    <Style x:Key="ActionButtonStyle" TargetType="Button">
        <Setter Property="Padding" Value="12,6"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="BorderBrush" Value="{StaticResource BorderStrongBrush}"/>
        <Setter Property="BorderThickness" Value="1"/>
        <Setter Property="FontSize" Value="12"/>
        <Setter Property="Foreground" Value="{StaticResource InkHoverBrush}"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="bd"
                            Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="3"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource ControlHoverBrush}"/>
                            <Setter TargetName="bd" Property="BorderBrush" Value="{StaticResource BorderHoverBrush}"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource ControlPressedBrush}"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="bd" Property="Opacity" Value="0.4"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- 주(Primary) 버튼 — 검정 채움. disabled 트리거 포함 판 채택 (Onboarding 판) -->
    <Style x:Key="PrimaryButtonStyle" TargetType="Button">
        <Setter Property="Padding" Value="24,8"/>
        <Setter Property="Background" Value="{StaticResource InkBrush}"/>
        <Setter Property="Foreground" Value="White"/>
        <Setter Property="BorderThickness" Value="0"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Template">
            <Setter.Value>
                <ControlTemplate TargetType="Button">
                    <Border x:Name="bd"
                            Background="{TemplateBinding Background}"
                            CornerRadius="3"
                            Padding="{TemplateBinding Padding}">
                        <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                    </Border>
                    <ControlTemplate.Triggers>
                        <Trigger Property="IsMouseOver" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="{StaticResource InkHoverBrush}"/>
                        </Trigger>
                        <Trigger Property="IsPressed" Value="True">
                            <Setter TargetName="bd" Property="Background" Value="#000000"/>
                        </Trigger>
                        <Trigger Property="IsEnabled" Value="False">
                            <Setter TargetName="bd" Property="Opacity" Value="0.4"/>
                        </Trigger>
                    </ControlTemplate.Triggers>
                </ControlTemplate>
            </Setter.Value>
        </Setter>
    </Style>

    <!-- 필드 라벨 -->
    <Style x:Key="FieldLabelStyle" TargetType="TextBlock">
        <Setter Property="FontSize" Value="11"/>
        <Setter Property="Foreground" Value="{StaticResource TextMutedBrush}"/>
        <Setter Property="Margin" Value="0,0,0,4"/>
    </Style>

    <!-- 밑줄(line-style) 입력 — Margin은 창 래퍼가 보유 (Onboarding 8 / Settings 16) -->
    <Style x:Key="LinePasswordBoxStyle" TargetType="PasswordBox">
        <Setter Property="Padding" Value="0,6"/>
        <Setter Property="BorderThickness" Value="0,0,0,1"/>
        <Setter Property="BorderBrush" Value="{StaticResource BorderInputBrush}"/>
        <Setter Property="Background" Value="Transparent"/>
        <Setter Property="FontSize" Value="13"/>
        <Setter Property="Foreground" Value="{StaticResource InkBrush}"/>
    </Style>

    <!-- 플레인 리스트 행 컨테이너 — NotionImport↔NoteList 공통분.
         NoteList는 BasedOn 래퍼에 Template 오버라이드를 추가로 보유 -->
    <Style x:Key="PlainListViewItemStyle" TargetType="ListViewItem">
        <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
        <Setter Property="Padding" Value="0"/>
        <Setter Property="Margin" Value="0"/>
        <Setter Property="Cursor" Value="Hand"/>
        <Setter Property="Background" Value="Transparent"/>
    </Style>

</ResourceDictionary>
```

- [ ] **Step 2: App.xaml 병합** — `App.xaml`의 `<Application.Resources>` 블록(6–8행)을 다음으로 교체:

```xml
    <Application.Resources>
        <ResourceDictionary>
            <ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Styles/SharedStyles.xaml"/>
            </ResourceDictionary.MergedDictionaries>
            <BooleanToVisibilityConverter x:Key="BoolToVisibilityConverter"/>
        </ResourceDictionary>
    </Application.Resources>
```

- [ ] **Step 3: 빌드 + 테스트** (이 시점에 공유 리소스는 정의만 되고 아직 미사용 — 빌드로 XAML 문법 검증)

Run: `taskkill //IM Noticker.exe //F` (실행 중이면) 후 `dotnet build Noticker.csproj`, 이어서 `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: Build succeeded 0 errors · 272개 전부 통과

- [ ] **Step 4: 실행 스모크** — 병합 구문 오류는 앱 시작 시 터지므로 즉시 확인:

Run: `start "" "bin/Debug/net8.0-windows/Noticker.exe"` 후 3초 대기, `tasklist | grep -i noticker`로 프로세스 생존 확인, 확인 후 `taskkill //IM Noticker.exe //F`
Expected: 프로세스 떠 있음 (크래시 없음)

- [ ] **Step 5: 커밋**

```bash
git add Styles/SharedStyles.xaml App.xaml
git commit -m "feat: shared resource dictionary — palette tokens + common styles"
```

---

### Task 2: OnboardingWindow 마이그레이션

**Files:**
- Modify: `Windows/OnboardingWindow.xaml` (코드 비하인드 변경 없음)

- [ ] **Step 1: 리소스 섹션 교체** — `<Window.Resources>`부터 `</Window.Resources>`까지(11–101행: 주석 + ActionButtonStyle + PrimaryButtonStyle + FieldLabelStyle + PasswordBox 암시 스타일 + ComboBox 암시 스타일)를 다음으로 교체. ComboBox 암시 스타일은 창 전용이라 유지, PasswordBox는 공유 참조 래퍼로(Margin 0,0,0,8은 이 창 고유값이라 래퍼가 보유):

```xml
    <Window.Resources>
        <!-- 버튼/라벨은 Styles/SharedStyles.xaml 공유 — 창 전용 리소스만 여기 둔다 -->
        <Style TargetType="PasswordBox" BasedOn="{StaticResource LinePasswordBoxStyle}">
            <Setter Property="Margin" Value="0,0,0,8"/>
        </Style>

        <Style TargetType="ComboBox">
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Margin" Value="0,0,0,16"/>
        </Style>
    </Window.Resources>
```

- [ ] **Step 2: 본문 hex → 토큰 교체** — 아래 속성들을 정확히 교체 (각 행 위치는 Step 1 이후 밀리므로 앵커 텍스트로 찾을 것):

| 앵커 (찾기) | 교체 |
|---|---|
| Window 선언의 `Background="#FAFAFA"` | `Background="{StaticResource SurfaceBrush}"` |
| 헤더 `<Grid Grid.Row="0" Background="#1A1A1A">` | `<Grid Grid.Row="0" Background="{StaticResource InkBrush}">` |
| Step1 점 표시 `Text="●" FontSize="11" Foreground="#1A1A1A" Margin="0,0,6,0"` | `Foreground="{StaticResource InkBrush}"` 로 hex만 교체 |
| Step1 점 표시 `Text="●" FontSize="11" Foreground="#CCCCCC"` | `Foreground="{StaticResource BorderStrongBrush}"` |
| `Text="노션과 연결하기" ... Foreground="#1A1A1A"` | `Foreground="{StaticResource InkBrush}"` |
| Step1 설명 `FontSize="12" Foreground="#777777"` | `Foreground="{StaticResource TextSectionBrush}"` |
| Step2 점 표시 `Foreground="#CCCCCC" Margin="0,0,6,0"` | `Foreground="{StaticResource BorderStrongBrush}"` |
| Step2 점 표시 둘째 `Foreground="#1A1A1A"` | `Foreground="{StaticResource InkBrush}"` |
| `Text="데이터베이스 선택" ... Foreground="#1A1A1A"` | `Foreground="{StaticResource InkBrush}"` |
| Step2 설명 `FontSize="12" Foreground="#777777"` | `Foreground="{StaticResource TextSectionBrush}"` |

교체 후 `grep -n "#1A1A1A\|#CCCCCC\|#777777\|#FAFAFA\|#666666\|#DDDDDD" Windows/OnboardingWindow.xaml` 결과가 **0건**이어야 한다.

- [ ] **Step 3: 빌드 + 테스트**

Run: `dotnet build Noticker.csproj` 후 `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 0 errors · 272 통과

- [ ] **Step 4: 커밋**

```bash
git add Windows/OnboardingWindow.xaml
git commit -m "refactor: onboarding window references shared styles + palette tokens"
```

---

### Task 3: SettingsWindow 마이그레이션

**Files:**
- Modify: `Windows/SettingsWindow.xaml` (코드 비하인드 변경 없음)

- [ ] **Step 1: 리소스 섹션 정리** — `<Window.Resources>` 안에서:

(a) `ActionButtonStyle`(13–48행), `PrimaryButtonStyle`(51–78행), `FieldLabelStyle`(81–85행) 세 스타일 블록과 각각의 직전 주석(`<!-- Minimal action button (outlined) -->`, `<!-- Primary (Save) button — black fill -->`, `<!-- Field label -->`)을 **삭제** (공유 사전이 동일 키 제공 — 참조 측 변경 불필요).

(b) `SectionHeadingStyle`은 유지하되 `<Setter Property="Foreground" Value="#777777"/>`를 `<Setter Property="Foreground" Value="{StaticResource TextSectionBrush}"/>`로 교체.

(c) 암시적 TextBox 스타일(`<!-- Line-style TextBox -->` 블록)은 유지하되 hex 두 곳 교체: `BorderBrush" Value="#DDDDDD"` → `{StaticResource BorderInputBrush}`, `Foreground" Value="#1A1A1A"` → `{StaticResource InkBrush}`.

(d) 암시적 PasswordBox 스타일(`<!-- Line-style PasswordBox -->` 블록 전체)을 다음으로 교체 (Margin 0,0,0,16은 이 창 고유값):

```xml
        <!-- Line-style PasswordBox — 공유 LinePasswordBoxStyle + 창 고유 Margin -->
        <Style TargetType="PasswordBox" BasedOn="{StaticResource LinePasswordBoxStyle}">
            <Setter Property="Margin" Value="0,0,0,16"/>
        </Style>
```

(e) 암시적 CheckBox 스타일은 유지하되 `Foreground" Value="#333333"` → `{StaticResource InkHoverBrush}`.

- [ ] **Step 2: 본문 교체**

| 앵커 (찾기) | 교체 |
|---|---|
| Window 선언의 `Background="#FAFAFA"` | `Background="{StaticResource SurfaceBrush}"` |
| `<Grid Grid.Row="0" Background="#1A1A1A">` | `Background="{StaticResource InkBrush}"` |
| `ConnectionSummary` TextBlock의 `Foreground="#1A1A1A"` | `Foreground="{StaticResource InkBrush}"` |
| `HotkeyStatusText`의 `Foreground="#C0392B"` | `Foreground="{StaticResource DangerBrush}"` |
| `ReconnectButton`에 속성 추가 | `Margin="0,0,8,0"` (옛 ActionButtonStyle의 Margin이 인스턴스로 이동) |
| `RefreshCatButton`에 속성 추가 | `Margin="0,0,8,0"` (동일) |

유지 (단일 사용 — 토큰화 대상 아님): `CatStatusText`의 `#555555`, Separator들의 `#EEEEEE`.

교체 후 `grep -n "#1A1A1A\|#FAFAFA\|#666666\|#777777\|#CCCCCC\|#DDDDDD\|#333333\|#C0392B\|#F0F0F0\|#E4E4E4\|#999999" Windows/SettingsWindow.xaml` 결과가 **0건**이어야 한다 (#555555·#EEEEEE·#000000은 남아도 됨 — #000000은 이 파일엔 없음).

- [ ] **Step 3: 빌드 + 테스트**

Run: `dotnet build Noticker.csproj` 후 `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 0 errors · 272 통과

- [ ] **Step 4: 커밋**

```bash
git add Windows/SettingsWindow.xaml
git commit -m "refactor: settings window references shared styles + palette tokens"
```

---

### Task 4: ListViewItem 공통화 (NotionImport + NoteList)

**Files:**
- Modify: `Windows/NotionImportWindow.xaml`
- Modify: `Windows/NoteListWindow.xaml`

- [ ] **Step 1: NotionImportWindow** — `<ListView.ItemContainerStyle>` 블록(33–41행)을 다음으로 교체 (#50808080·#AAAAAA는 단일 사용 + 양 테마 중간값 주석이 있어 인라인 유지):

```xml
            <ListView.ItemContainerStyle>
                <Style TargetType="ListViewItem" BasedOn="{StaticResource PlainListViewItemStyle}"/>
            </ListView.ItemContainerStyle>
```

- [ ] **Step 2: NoteListWindow** — `<ListView.ItemContainerStyle>` 블록(74–90행)을 다음으로 교체 (공통 setter 5개는 BasedOn으로, 창 고유 Template 오버라이드는 유지):

```xml
            <ListView.ItemContainerStyle>
                <Style TargetType="ListViewItem" BasedOn="{StaticResource PlainListViewItemStyle}">
                    <Setter Property="Template">
                        <Setter.Value>
                            <!-- 시스템 하이라이트/선택 비주얼 제거 — 카드가 hover를 직접 그린다 -->
                            <ControlTemplate TargetType="ListViewItem">
                                <ContentPresenter/>
                            </ControlTemplate>
                        </Setter.Value>
                    </Setter>
                </Style>
            </ListView.ItemContainerStyle>
```

- [ ] **Step 3: 빌드 + 테스트**

Run: `dotnet build Noticker.csproj` 후 `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 0 errors · 272 통과

- [ ] **Step 4: 커밋**

```bash
git add Windows/NotionImportWindow.xaml Windows/NoteListWindow.xaml
git commit -m "refactor: shared plain ListViewItem container style"
```

---

### Task 5: DESIGN.md 작성

**Files:**
- Create: `DESIGN.md` (저장소 루트)

- [ ] **Step 1: DESIGN.md 작성**:

```markdown
# Noticker 디자인 토큰

스타일 정의의 단일 소스는 `Styles/SharedStyles.xaml` (App.xaml에서 병합).
이 문서는 그 내용의 사람용 설명이다. **둘이 어긋나면 XAML이 정답** — 수정 시
이 문서도 같이 갱신할 것.

## 정적 / 동적 경계 (가장 중요한 규칙)

| 영역 | 색의 출처 | 토큰 사용 |
|---|---|---|
| 라이트 테마 창 (온보딩·설정·가져오기·노트 목록의 고정 부분) | 아래 정적 토큰 | O |
| 스티커·포모도로 창, 노트 목록 카드 색 | ViewModel 바인딩 — 노션 카테고리 색 등 **런타임 데이터** | **X — 토큰화 금지** |

스티커 계열의 `{Binding TitleBackground}` 류를 정적 토큰으로 바꾸면 테마
전환·카테고리 색이 깨진다. 이 경계를 넘는 리팩토링은 하지 않는다.

## 팔레트 토큰

모노크롬(잉크 온 페이퍼) 디자인. 검정 `InkBrush`가 헤더 띠·주 텍스트·주 버튼을
겸하고, 회색 계열이 위계를 만든다.

| 토큰 | 값 | 역할 | 주 사용처 |
|---|---|---|---|
| `SurfaceBrush` | #FAFAFA | 라이트 창 배경 | Onboarding/Settings Window |
| `InkBrush` | #1A1A1A | 잉크 — 헤더 띠, 주 텍스트, Primary 버튼 bg, 입력 글자색 | 헤더 Grid, PrimaryButtonStyle, LinePasswordBoxStyle |
| `InkHoverBrush` | #333333 | Primary 버튼 hover, 보조 본문 텍스트 | PrimaryButtonStyle 트리거, ActionButtonStyle Foreground, CheckBox |
| `TextMutedBrush` | #666666 | 필드 라벨 | FieldLabelStyle |
| `TextSectionBrush` | #777777 | 섹션 제목, 설명 문단 | SectionHeadingStyle, 온보딩 설명 |
| `BorderStrongBrush` | #CCCCCC | 외곽선 버튼 테두리, 비활성 점 표시 | ActionButtonStyle, 온보딩 단계 점 |
| `BorderInputBrush` | #DDDDDD | 입력 밑줄 | LinePasswordBoxStyle, Settings TextBox |
| `BorderHoverBrush` | #999999 | 외곽선 버튼 hover 테두리 | ActionButtonStyle 트리거 |
| `ControlHoverBrush` | #F0F0F0 | 보조 버튼 hover bg | ActionButtonStyle 트리거 |
| `ControlPressedBrush` | #E4E4E4 | 보조 버튼 pressed bg | ActionButtonStyle 트리거 |
| `DangerBrush` | #C0392B | 에러 텍스트 | Settings hotkey 인라인 에러 |

토큰화 기준: **2개 이상 파일에서 반복되는 값만.** 단일 사용 색(#555555,
#EEEEEE, #AAAAAA, #50808080, Primary pressed #000000 등)은 사용처에 인라인.

## 공유 스타일

| 키 | TargetType | 용도 | 사용 창 |
|---|---|---|---|
| `ActionButtonStyle` | Button | 외곽선 보조 버튼 (12,6 패딩, 둥근 3px) | Onboarding, Settings |
| `PrimaryButtonStyle` | Button | 검정 채움 주 버튼 (24,8 패딩) | Onboarding, Settings |
| `FieldLabelStyle` | TextBlock | 11px 회색 필드 라벨 | Onboarding, Settings |
| `LinePasswordBoxStyle` | PasswordBox | 밑줄 입력 (Margin 없음 — 창 래퍼가 보유) | Onboarding(8), Settings(16) |
| `PlainListViewItemStyle` | ListViewItem | 시스템 크롬 없는 리스트 행 | NotionImport, NoteList(+Template 오버라이드) |

Margin 규칙: 공유 스타일은 Margin을 갖지 않는다 — 배치는 창/인스턴스 책임.

## 새 창 추가 시 규칙

1. 라이트 테마 창이면 `SurfaceBrush` 배경 + `InkBrush` 헤더 띠로 시작.
2. 버튼·라벨·입력은 공유 스타일 참조 먼저 — 복사 금지.
3. 새 hex가 필요하면: 2개 이상 파일에서 쓰게 될 값인지 따져 토큰 추가
   (SharedStyles.xaml + 이 문서 동시 갱신), 단일 사용이면 인라인.
4. 암시적(TargetType-only) 스타일은 App 전역에 올리지 않는다 — 모든 창에
   적용돼 스티커 창을 오염시킨다. 창 안에서 `BasedOn` 래퍼로 적용할 것.
```

- [ ] **Step 2: 커밋**

```bash
git add DESIGN.md
git commit -m "docs: DESIGN.md — palette tokens, shared styles, static/dynamic boundary"
```

---

### Task 6: 전체 검증

**Files:** 없음 (검증만)

- [ ] **Step 1: 스티커/포모도로 불가침 확인**

Run: `_BASE=$(git log --format=%H --grep "shared resource dictionary" -1)~1 && git diff "$_BASE"..HEAD --stat -- Windows/StickerWindow.xaml Windows/PomodoroWindow.xaml` (기준 = Task 1 커밋의 부모 — 커밋 수 가정 없음)
Expected: 출력 없음 (변경 0)

- [ ] **Step 2: 중복 제거 확인** — 옛 사본이 남아 있지 않은지:

Run: `grep -rn "ActionButtonStyle\|PrimaryButtonStyle\|FieldLabelStyle" Windows/*.xaml | grep -v StaticResource`
Expected: 출력 없음 (정의는 SharedStyles.xaml에만)

- [ ] **Step 3: 빌드 + 전체 테스트**

Run: `taskkill //IM Noticker.exe //F` (실행 중이면) 후 `dotnet build Noticker.csproj`, `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: 0 errors · 272 통과

- [ ] **Step 4: 실행 + 창 4개 QA (리소스 키 해석은 런타임 검증)**

Run: `start "" "bin/Debug/net8.0-windows/Noticker.exe"`

이후 사용자(또는 컨트롤러)가 확인:
1. 트레이 → **노트 목록** 열기 — 카드 렌더·hover 삭제 ✕ 정상
2. 노트 목록 → **Notion에서 가져오기** — 목록 행 렌더 정상
3. 트레이 → **설정** — 전체 외관이 변경 전과 동일, 버튼 hover/pressed 동작, hotkey 콤보 정상
4. 설정 → **연결 다시 설정** — 온보딩 위저드 외관 동일, 단계 점·버튼 정상
5. 스티커·포모도로 창 — diff 0이므로 열어볼 필요 없음

Expected: 크래시(XamlParseException) 없음, 픽셀 변화 없음
