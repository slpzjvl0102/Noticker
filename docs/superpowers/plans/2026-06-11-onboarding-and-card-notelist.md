# 온보딩 위저드 + 카드형 노트 목록 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 첫 실행 시 2단계 위저드(토큰 검증 → DB/카테고리 드롭다운)로 노션을 연결하고, 노트 목록을 카드형으로 정리한다.

**Architecture:** NotionClient에 저장-전 후보 토큰으로 호출하는 프로브 메서드를 추가하되 JSON 파싱은 `NotionDirectory` 정적 클래스로 분리해 HTTP 없이 테스트한다. 신설 `OnboardingWindow`가 토큰/DB/카테고리 설정의 단일 소유자가 되고, SettingsWindow의 노션 입력란은 요약+[연결 다시 설정] 버튼으로 교체된다. NoteListWindow는 테마 브러시를 아이템(NoteItem)에 박아 카드 색을 바인딩으로 처리하고, hover는 코드비하인드 MouseEnter/Leave로 만든다.

**Tech Stack:** .NET 8 WPF, xUnit, Notion API 2022-06-28, SQLite 설정 저장(DPAPI 토큰 암호화).

**스펙:** `docs/superpowers/specs/2026-06-11-onboarding-and-notelist-design.md`

**빌드/테스트 명령 (중요):**
- 테스트: `dotnet test Noticker.Tests/Noticker.Tests.csproj` (루트 `dotnet test`는 no-op)
- 앱 빌드: 실행 중인 앱이 bin을 잠그므로 먼저 `taskkill //IM Noticker.exe //F` (bash) 후 `dotnet build Noticker.csproj`

---

## File Structure

| 파일 | 작업 | 책임 |
|---|---|---|
| `Sync/NotionDirectory.cs` | 신설 | search/databases 응답 JSON 파싱 (순수 함수, 테스트 대상) |
| `Sync/NotionClient.cs` | 수정 | 토큰 명시 인자 프로브 3종 + per-request 인증 헬퍼 |
| `Models/AppSettings.cs` | 수정 | `NotionDbTitle` 캐시 속성 |
| `Data/SettingsRepository.cs` | 수정 | LoadInto에 `notion_db_title` 로드 |
| `App.xaml.cs` | 수정 | `OpenOnboarding()`, 시작 게이트 교체, `RefreshCategoryOptionsAsync()` 공유화 |
| `Windows/OnboardingWindow.xaml(.cs)` | 신설 | 2단계 위저드 (라이트 고정) |
| `Windows/SettingsWindow.xaml(.cs)` | 수정 | 노션 입력란 3개 → 요약+버튼 |
| `Windows/NoteListWindow.xaml(.cs)` | 수정 | 카드형 외관 (동작 불변) |
| `Noticker.Tests/NotionDirectoryTests.cs` | 신설 | 파싱 단위 테스트 |
| `Noticker.Tests/SettingsRepositoryTests.cs` | 수정 | db title 로드 테스트 2건 |

---

### Task 1: NotionDirectory 파싱 (TDD)

**Files:**
- Create: `Noticker.Tests/NotionDirectoryTests.cs`
- Create: `Sync/NotionDirectory.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`Noticker.Tests/NotionDirectoryTests.cs` 전체 내용:

```csharp
using System.Text.Json;
using Noticker.Sync;

namespace Noticker.Tests;

public class NotionDirectoryTests
{
    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ── ParseDatabaseList ──────────────────────────────────────────────────────

    [Fact]
    public void ParseDatabaseList_TwoDatabases_ReturnsIdAndConcatenatedTitle()
    {
        var root = Parse("""
            {"results":[
              {"id":"aaa-111","title":[{"plain_text":"업무 "},{"plain_text":"노트"}]},
              {"id":"bbb-222","title":[{"plain_text":"Reading List"}]}
            ],"has_more":false}
            """);

        var list = NotionDirectory.ParseDatabaseList(root);

        Assert.Equal(2, list.Count);
        Assert.Equal(("aaa-111", "업무 노트"), list[0]);
        Assert.Equal(("bbb-222", "Reading List"), list[1]);
    }

    [Fact]
    public void ParseDatabaseList_UntitledDatabase_FallsBackToPlaceholder()
    {
        var root = Parse("""{"results":[{"id":"ccc","title":[]}],"has_more":false}""");

        var list = NotionDirectory.ParseDatabaseList(root);

        Assert.Equal("(제목 없음)", list[0].Title);
    }

    [Fact]
    public void ParseDatabaseList_MissingTitleProperty_FallsBackToPlaceholder()
    {
        var root = Parse("""{"results":[{"id":"ddd"}],"has_more":false}""");

        var list = NotionDirectory.ParseDatabaseList(root);

        Assert.Equal("(제목 없음)", list[0].Title);
    }

    [Fact]
    public void ParseDatabaseList_EmptyResults_ReturnsEmpty()
    {
        Assert.Empty(NotionDirectory.ParseDatabaseList(Parse("""{"results":[],"has_more":false}""")));
    }

    [Fact]
    public void ParseDatabaseList_MissingResults_ReturnsEmpty()
    {
        Assert.Empty(NotionDirectory.ParseDatabaseList(Parse("{}")));
    }

    // ── ParseSelectPropertyNames ───────────────────────────────────────────────

    [Fact]
    public void ParseSelectPropertyNames_MixedTypes_ReturnsSelectOnly()
    {
        // multi_select 제외 — push(BuildProperties)가 select 단일 값만 쓴다
        var root = Parse("""
            {"properties":{
              "Name":{"type":"title"},
              "Category":{"type":"select"},
              "Tags":{"type":"multi_select"},
              "Status":{"type":"select"}
            }}
            """);

        var names = NotionDirectory.ParseSelectPropertyNames(root);

        Assert.Equal(new[] { "Category", "Status" }, names);
    }

    [Fact]
    public void ParseSelectPropertyNames_NoSelectProperty_ReturnsEmpty()
    {
        var root = Parse("""{"properties":{"Name":{"type":"title"}}}""");

        Assert.Empty(NotionDirectory.ParseSelectPropertyNames(root));
    }

    [Fact]
    public void ParseSelectPropertyNames_MissingProperties_ReturnsEmpty()
    {
        Assert.Empty(NotionDirectory.ParseSelectPropertyNames(Parse("{}")));
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter NotionDirectoryTests`
Expected: 컴파일 에러 — `NotionDirectory`가 존재하지 않음 (CS0103/CS0246)

- [ ] **Step 3: 최소 구현**

`Sync/NotionDirectory.cs` 전체 내용:

```csharp
using System.Text;
using System.Text.Json;

namespace Noticker.Sync;

// /v1/search, /v1/databases/{id} 응답 파싱 — HTTP 없이 단위 테스트 가능하도록 분리
public static class NotionDirectory
{
    // search 응답 한 페이지의 results에서 (Id, Title) 목록 추출.
    // title은 rich_text run들의 plain_text 연결, 비어 있으면 "(제목 없음)" 폴백.
    public static List<(string Id, string Title)> ParseDatabaseList(JsonElement root)
    {
        var list = new List<(string Id, string Title)>();
        if (!root.TryGetProperty("results", out var results)) return list;

        foreach (var db in results.EnumerateArray())
        {
            var id = db.GetProperty("id").GetString() ?? "";
            var title = "";
            if (db.TryGetProperty("title", out var runs))
            {
                var sb = new StringBuilder();
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.TryGetProperty("plain_text", out var pt))
                        sb.Append(pt.GetString());
                }
                title = sb.ToString();
            }
            list.Add((id, string.IsNullOrWhiteSpace(title) ? "(제목 없음)" : title));
        }
        return list;
    }

    // databases/{id} 응답에서 select 타입 속성 이름만 추출.
    // multi_select 제외 — push(BuildProperties)가 select 단일 값만 쓴다.
    public static List<string> ParseSelectPropertyNames(JsonElement root)
    {
        var names = new List<string>();
        if (!root.TryGetProperty("properties", out var props)) return names;

        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("type", out var t) && t.GetString() == "select")
                names.Add(prop.Name);
        }
        return names;
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter NotionDirectoryTests`
Expected: PASS (8개)

- [ ] **Step 5: Commit**

```bash
git add Sync/NotionDirectory.cs Noticker.Tests/NotionDirectoryTests.cs
git commit -m "feat: NotionDirectory — search/DB property parsing for onboarding"
```

---

### Task 2: NotionClient 프로브 메서드

저장 전 후보 토큰으로 호출해야 하므로 토큰을 명시 인자로 받고, **`DefaultRequestHeaders`를 건드리지 않는다** — 기존 `SetAuth()`는 DefaultRequestHeaders를 변이하므로 동기화 루프가 도는 중에 온보딩 프로브가 끼어들면 헤더 경쟁이 생긴다. per-request 헤더로 회피.

**Files:**
- Modify: `Sync/NotionClient.cs` (PostAsync 정의 위쪽, GetPageBlocksAsync 아래에 추가)

- [ ] **Step 1: 프로브 메서드 4개 추가**

`Sync/NotionClient.cs`의 `ExtractTitle` 메서드 바로 앞에 삽입:

```csharp
    // ── 온보딩 프로브 ──────────────────────────────────────────────────────────
    // 저장 전 후보 토큰으로 호출 — 토큰을 명시 인자로 받고 DefaultRequestHeaders를
    // 변이하지 않는다 (동기화 루프와의 헤더 경쟁 방지).

    // 토큰 검증. 성공 시 null, 실패 시 사용자에게 보여줄 메시지.
    public async Task<string?> ValidateTokenAsync(string token, CancellationToken ct)
    {
        try
        {
            await SendWithTokenAsync(HttpMethod.Get, "/users/me", token, body: null, ct);
            return null;
        }
        catch (NotionUnauthorizedException)
        {
            return "토큰이 유효하지 않습니다 (401).";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    // 통합에 공유된 DB 전체 목록 (페이지네이션 전부 순회).
    public async Task<List<(string Id, string Title)>> SearchDatabasesAsync(string token, CancellationToken ct)
    {
        var results = new List<(string Id, string Title)>();
        string? cursor = null;

        while (true)
        {
            var body = new Dictionary<string, object>
            {
                ["filter"] = new { value = "database", property = "object" },
                ["page_size"] = 100
            };
            if (cursor is not null)
                body["start_cursor"] = cursor;

            var doc = await SendWithTokenAsync(HttpMethod.Post, "/search", token, body, ct);
            results.AddRange(NotionDirectory.ParseDatabaseList(doc.RootElement));

            if (!doc.RootElement.GetProperty("has_more").GetBoolean()) break;
            cursor = doc.RootElement.GetProperty("next_cursor").GetString();
        }
        return results;
    }

    // 선택한 DB의 select 타입 속성 이름 목록.
    public async Task<List<string>> GetSelectPropertiesAsync(string token, string dbId, CancellationToken ct)
    {
        var doc = await SendWithTokenAsync(HttpMethod.Get, $"/databases/{dbId}", token, body: null, ct);
        return NotionDirectory.ParseSelectPropertyNames(doc.RootElement);
    }

    private async Task<JsonDocument> SendWithTokenAsync(
        HttpMethod method, string path, string token, object? body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(method, BaseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null)
            request.Content = new StringContent(
                JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        var response = await _http.SendAsync(request, ct);
        return await HandleResponseAsync(response, pageId: null);
    }
```

- [ ] **Step 2: 빌드 + 전체 테스트 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (기존 210 + Task 1의 8 = 218개)

- [ ] **Step 3: Commit**

```bash
git add Sync/NotionClient.cs
git commit -m "feat: NotionClient onboarding probes — token-explicit validate/search/properties"
```

---

### Task 3: AppSettings.NotionDbTitle (TDD)

설정 창 요약("연결됨: {DB 제목}") 표시용 캐시. 온보딩 완료 시 저장.

**Files:**
- Modify: `Models/AppSettings.cs:32` 근처
- Modify: `Data/SettingsRepository.cs:72` 근처 (LoadInto)
- Test: `Noticker.Tests/SettingsRepositoryTests.cs`

- [ ] **Step 1: 실패하는 테스트 추가**

`Noticker.Tests/SettingsRepositoryTests.cs`의 `LoadInto_TargetDbId_Loaded` 테스트 뒤에 추가:

```csharp
    [Fact]
    public void LoadInto_NotionDbTitle_Loaded()
    {
        _repo.Set("notion_db_title", "업무 노트");
        _repo.LoadInto(AppSettings.Instance);
        Assert.Equal("업무 노트", AppSettings.Instance.NotionDbTitle);
    }

    [Fact]
    public void LoadInto_MissingNotionDbTitle_Null()
    {
        // singleton 오염 방지: 먼저 잡값을 넣고 LoadInto가 null로 덮는지 확인
        AppSettings.Instance.NotionDbTitle = "stale";
        _repo.LoadInto(AppSettings.Instance);
        Assert.Null(AppSettings.Instance.NotionDbTitle);
    }
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter SettingsRepositoryTests`
Expected: 컴파일 에러 — `NotionDbTitle` 속성 없음

- [ ] **Step 3: 구현**

`Models/AppSettings.cs` — `NotionBotUserId` 선언 아래에 추가:

```csharp
    public string? NotionDbTitle { get; set; }     // 설정 창 요약 표시용 캐시 (온보딩 완료 시 저장)
```

`Data/SettingsRepository.cs` `LoadInto` — `settings.NotionBotUserId = ...` 줄 아래에 추가:

```csharp
        settings.NotionDbTitle = Get("notion_db_title");
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (220개)

- [ ] **Step 5: Commit**

```bash
git add Models/AppSettings.cs Data/SettingsRepository.cs Noticker.Tests/SettingsRepositoryTests.cs
git commit -m "feat: cache Notion DB title in settings for connection summary"
```

---

### Task 4: App.RefreshCategoryOptionsAsync 공유화

카테고리 옵션 새로고침 로직이 SettingsWindow에 박혀 있다. 온보딩도 같은 일을 해야 하므로 App으로 끌어올린다 (로직 한 곳).

**Files:**
- Modify: `App.xaml.cs` (`RefreshPomodoroSettings` 메서드 아래에 추가)
- Modify: `Windows/SettingsWindow.xaml.cs:77-108` (`RefreshCatButton_Click`)

- [ ] **Step 1: App에 공유 메서드 추가**

`App.xaml.cs`의 `RefreshPomodoroSettings()` 메서드 바로 아래에 삽입:

```csharp
    // 카테고리 옵션/색 새로고침 — SettingsWindow와 OnboardingWindow가 공유 (로직 한 곳).
    // 실패 시 예외를 그대로 던진다 — 호출자가 각자 상태 표시를 책임진다.
    public async Task<int> RefreshCategoryOptionsAsync()
    {
        var options = await _notionClient!.FetchCategoryOptionsAsync(_cts.Token);
        var names = options.Select(o => o.Name).ToList();
        var colors = options.ToDictionary(o => o.Name, o => o.Color);

        AppSettings.Instance.CategoryOptions = names;
        AppSettings.Instance.CategoryColors = colors;
        SettingsRepo!.SaveCategoryOptions(names);
        SettingsRepo!.SaveCategoryColors(colors);

        foreach (var win in Windows.OfType<StickerWindow>())
            win.RefreshCategoryOptions();

        return options.Count;
    }
```

- [ ] **Step 2: SettingsWindow가 공유 메서드를 쓰도록 교체**

`Windows/SettingsWindow.xaml.cs`의 `RefreshCatButton_Click` 전체를 다음으로 교체
(주의: `ApplyToAppSettings()` 호출은 유지 — Task 6에서 토큰 입력란과 함께 제거된다):

```csharp
    private async void RefreshCatButton_Click(object sender, RoutedEventArgs e)
    {
        RefreshCatButton.IsEnabled = false;
        CatStatusText.Text = "불러오는 중…";

        ApplyToAppSettings();

        try
        {
            var count = await App.Current.RefreshCategoryOptionsAsync();
            CatStatusText.Text = $"{count}개 옵션 새로고침 완료";
        }
        catch (Exception ex)
        {
            CatStatusText.Text = $"실패: {ex.Message}";
        }
        finally
        {
            RefreshCatButton.IsEnabled = true;
        }
    }
```

- [ ] **Step 3: 빌드 + 전체 테스트**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (220개)

- [ ] **Step 4: Commit**

```bash
git add App.xaml.cs Windows/SettingsWindow.xaml.cs
git commit -m "refactor: hoist category option refresh into App for onboarding reuse"
```

---

### Task 5: OnboardingWindow 신설 + 시작 게이트 교체

**Files:**
- Create: `Windows/OnboardingWindow.xaml`
- Create: `Windows/OnboardingWindow.xaml.cs`
- Modify: `App.xaml.cs:77-78` (시작 게이트), `OpenSettings()` 아래에 `OpenOnboarding()` 추가

- [ ] **Step 1: XAML 작성**

`Windows/OnboardingWindow.xaml` 전체 내용:

```xml
<Window x:Class="Noticker.Windows.OnboardingWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Noticker 시작하기"
        Width="420" Height="400"
        WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize"
        ShowInTaskbar="False"
        Background="#FAFAFA">

    <Window.Resources>
        <!-- SettingsWindow와 같은 미니멀 스타일 사본 (공유 ResourceDictionary는 백로그) -->
        <Style x:Key="ActionButtonStyle" TargetType="Button">
            <Setter Property="Padding" Value="12,6"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="BorderBrush" Value="#CCCCCC"/>
            <Setter Property="BorderThickness" Value="1"/>
            <Setter Property="FontSize" Value="12"/>
            <Setter Property="Foreground" Value="#333333"/>
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
                                <Setter TargetName="bd" Property="Background" Value="#F0F0F0"/>
                                <Setter TargetName="bd" Property="BorderBrush" Value="#999999"/>
                            </Trigger>
                            <Trigger Property="IsPressed" Value="True">
                                <Setter TargetName="bd" Property="Background" Value="#E4E4E4"/>
                            </Trigger>
                            <Trigger Property="IsEnabled" Value="False">
                                <Setter TargetName="bd" Property="Opacity" Value="0.4"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>

        <Style x:Key="PrimaryButtonStyle" TargetType="Button">
            <Setter Property="Padding" Value="24,8"/>
            <Setter Property="Background" Value="#1A1A1A"/>
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
                                <Setter TargetName="bd" Property="Background" Value="#333333"/>
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

        <Style x:Key="FieldLabelStyle" TargetType="TextBlock">
            <Setter Property="FontSize" Value="11"/>
            <Setter Property="Foreground" Value="#666666"/>
            <Setter Property="Margin" Value="0,0,0,4"/>
        </Style>

        <Style TargetType="PasswordBox">
            <Setter Property="Padding" Value="0,6"/>
            <Setter Property="Margin" Value="0,0,0,8"/>
            <Setter Property="BorderThickness" Value="0,0,0,1"/>
            <Setter Property="BorderBrush" Value="#DDDDDD"/>
            <Setter Property="Background" Value="Transparent"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Foreground" Value="#1A1A1A"/>
        </Style>

        <Style TargetType="ComboBox">
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Margin" Value="0,0,0,16"/>
        </Style>
    </Window.Resources>

    <Grid>
        <Grid.RowDefinitions>
            <RowDefinition Height="48"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- 검정 헤더 — SettingsWindow와 동일한 룩 -->
        <Grid Grid.Row="0" Background="#1A1A1A">
            <TextBlock Text="Noticker 시작하기"
                       Foreground="White" FontSize="14" FontWeight="SemiBold"
                       VerticalAlignment="Center" Margin="20,0"/>
        </Grid>

        <Grid Grid.Row="1" Margin="24,16,24,0">
            <!-- 1단계: 토큰 -->
            <StackPanel x:Name="Step1Panel">
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,14">
                    <TextBlock Text="●" FontSize="11" Foreground="#1A1A1A" Margin="0,0,6,0"/>
                    <TextBlock Text="●" FontSize="11" Foreground="#CCCCCC"/>
                </StackPanel>
                <TextBlock Text="노션과 연결하기" FontSize="16" FontWeight="SemiBold"
                           Foreground="#1A1A1A" Margin="0,0,0,6"/>
                <TextBlock FontSize="12" Foreground="#777777" TextWrapping="Wrap" Margin="0,0,0,10"
                           Text="노션 통합(integration) 토큰을 붙여넣으세요. 아직 없다면 아래 링크로 발급 페이지를 열 수 있어요."/>
                <TextBlock FontSize="12" Margin="0,0,0,16">
                    <Hyperlink Click="TokenLink_Click">↗ 노션에서 토큰 발급받기</Hyperlink>
                </TextBlock>
                <TextBlock Text="NOTION API TOKEN" Style="{StaticResource FieldLabelStyle}"/>
                <PasswordBox x:Name="TokenBox"/>
                <TextBlock x:Name="Step1Status" FontSize="12" TextWrapping="Wrap"/>
            </StackPanel>

            <!-- 2단계: DB 선택 -->
            <StackPanel x:Name="Step2Panel" Visibility="Collapsed">
                <StackPanel Orientation="Horizontal" HorizontalAlignment="Center" Margin="0,0,0,14">
                    <TextBlock Text="●" FontSize="11" Foreground="#CCCCCC" Margin="0,0,6,0"/>
                    <TextBlock Text="●" FontSize="11" Foreground="#1A1A1A"/>
                </StackPanel>
                <TextBlock Text="데이터베이스 선택" FontSize="16" FontWeight="SemiBold"
                           Foreground="#1A1A1A" Margin="0,0,0,6"/>
                <TextBlock FontSize="12" Foreground="#777777" TextWrapping="Wrap" Margin="0,0,0,16"
                           Text="토큰이 확인됐어요. 메모를 저장할 DB를 고르세요."/>
                <TextBlock Text="데이터베이스" Style="{StaticResource FieldLabelStyle}"/>
                <ComboBox x:Name="DbCombo" SelectionChanged="DbCombo_SelectionChanged" IsEnabled="False"/>
                <TextBlock Text="카테고리 속성" Style="{StaticResource FieldLabelStyle}"/>
                <ComboBox x:Name="CatCombo" IsEnabled="False"/>
                <StackPanel Orientation="Horizontal">
                    <Button x:Name="RefreshDbButton" Content="새로고침"
                            Style="{StaticResource ActionButtonStyle}"
                            Click="RefreshDbButton_Click" Visibility="Collapsed"/>
                </StackPanel>
                <TextBlock x:Name="Step2Status" FontSize="12" TextWrapping="Wrap" Margin="0,4,0,0"/>
            </StackPanel>
        </Grid>

        <!-- 하단 버튼 바 -->
        <Grid Grid.Row="2" Margin="24,8,24,20">
            <Button x:Name="BackButton" Content="← 이전"
                    Style="{StaticResource ActionButtonStyle}"
                    HorizontalAlignment="Left" Click="BackButton_Click" Visibility="Collapsed"/>
            <Button x:Name="NextButton" Content="다음 →"
                    Style="{StaticResource PrimaryButtonStyle}"
                    HorizontalAlignment="Right" Click="NextButton_Click" IsDefault="True"/>
            <Button x:Name="FinishButton" Content="시작하기"
                    Style="{StaticResource PrimaryButtonStyle}"
                    HorizontalAlignment="Right" Click="FinishButton_Click"
                    Visibility="Collapsed" IsEnabled="False"/>
        </Grid>
    </Grid>
</Window>
```

- [ ] **Step 2: 코드비하인드 작성**

`Windows/OnboardingWindow.xaml.cs` 전체 내용:

```csharp
using System.Diagnostics;
using System.Windows;
using Brushes = System.Windows.Media.Brushes;
using Noticker.Data;
using Noticker.Models;
using Noticker.Sync;

namespace Noticker.Windows;

// 첫 실행 / [연결 다시 설정] 2단계 위저드 — 토큰/DB/카테고리 설정의 단일 소유자.
// SettingsWindow는 더 이상 이 값들을 직접 편집하지 않는다.
public partial class OnboardingWindow : Window
{
    private const string NoCategoryLabel = "(카테고리 없음)";

    private readonly SettingsRepository _settings;
    private readonly NotionClient _client;
    private string _validatedToken = "";
    private List<(string Id, string Title)> _databases = [];
    private List<string> _selectProperties = [];

    public OnboardingWindow(SettingsRepository settings, NotionClient client)
    {
        _settings = settings;
        _client = client;
        InitializeComponent();

        // 재설정 진입이면 기존 토큰 미리 채움
        if (AppSettings.Instance.NotionToken is not null)
            TokenBox.Password = AppSettings.Instance.NotionToken;
    }

    private void TokenLink_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://www.notion.so/my-integrations")
        {
            UseShellExecute = true
        });

    private async void NextButton_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        if (token.Length == 0)
        {
            SetStatus(Step1Status, "토큰을 입력하세요.", error: true);
            return;
        }

        NextButton.IsEnabled = false;
        SetStatus(Step1Status, "확인 중…");

        var error = await _client.ValidateTokenAsync(token, default);
        NextButton.IsEnabled = true;

        if (error is not null)
        {
            SetStatus(Step1Status, $"실패: {error}", error: true);
            return;
        }

        SetStatus(Step1Status, "");
        _validatedToken = token;
        Step1Panel.Visibility = Visibility.Collapsed;
        Step2Panel.Visibility = Visibility.Visible;
        NextButton.Visibility = Visibility.Collapsed;
        BackButton.Visibility = Visibility.Visible;
        FinishButton.Visibility = Visibility.Visible;
        await LoadDatabasesAsync();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        Step2Panel.Visibility = Visibility.Collapsed;
        Step1Panel.Visibility = Visibility.Visible;
        BackButton.Visibility = Visibility.Collapsed;
        FinishButton.Visibility = Visibility.Collapsed;
        NextButton.Visibility = Visibility.Visible;
    }

    private async void RefreshDbButton_Click(object sender, RoutedEventArgs e) =>
        await LoadDatabasesAsync();

    private async Task LoadDatabasesAsync()
    {
        DbCombo.IsEnabled = false;
        CatCombo.IsEnabled = false;
        FinishButton.IsEnabled = false;
        RefreshDbButton.Visibility = Visibility.Collapsed;
        SetStatus(Step2Status, "DB 목록 불러오는 중…");

        try
        {
            _databases = await _client.SearchDatabasesAsync(_validatedToken, default);
        }
        catch (Exception ex)
        {
            SetStatus(Step2Status, $"실패: {ex.Message}", error: true);
            RefreshDbButton.Visibility = Visibility.Visible;
            return;
        }

        if (_databases.Count == 0)
        {
            SetStatus(Step2Status,
                "통합에 공유된 DB가 없습니다. 노션에서 DB 페이지의 ⋯ 메뉴 → 연결에 " +
                "이 통합을 추가한 뒤 새로고침하세요.", error: true);
            RefreshDbButton.Visibility = Visibility.Visible;
            return;
        }

        DbCombo.ItemsSource = _databases.Select(d => d.Title).ToList();
        DbCombo.IsEnabled = true;

        // 재설정 진입이면 현재 DB 미리 선택 (저장 형식은 32hex 무대시 — 비교 전 정규화)
        var currentId = AppSettings.Instance.TargetDbId;
        var idx = currentId is null ? -1
            : _databases.FindIndex(d => d.Id.Replace("-", "") == currentId);
        DbCombo.SelectedIndex = idx >= 0 ? idx : 0;   // SelectionChanged가 속성 로드를 이어받음
    }

    private async void DbCombo_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DbCombo.SelectedIndex < 0) return;

        CatCombo.IsEnabled = false;
        FinishButton.IsEnabled = false;
        SetStatus(Step2Status, "속성 확인 중…");

        var dbId = _databases[DbCombo.SelectedIndex].Id;
        try
        {
            _selectProperties = await _client.GetSelectPropertiesAsync(_validatedToken, dbId, default);
        }
        catch (Exception ex)
        {
            SetStatus(Step2Status, $"실패: {ex.Message}", error: true);
            return;
        }

        var items = new List<string>(_selectProperties) { NoCategoryLabel };
        CatCombo.ItemsSource = items;
        CatCombo.SelectedItem = _selectProperties.Contains("Category") ? "Category" : items[0];
        CatCombo.IsEnabled = true;
        FinishButton.IsEnabled = true;
        SetStatus(Step2Status, "");
    }

    private async void FinishButton_Click(object sender, RoutedEventArgs e)
    {
        if (DbCombo.SelectedIndex < 0) return;
        FinishButton.IsEnabled = false;

        var app = AppSettings.Instance;
        var (dbId, dbTitle) = _databases[DbCombo.SelectedIndex];
        var category = CatCombo.SelectedItem as string;
        var hasCategory = category is not null && category != NoCategoryLabel;

        app.NotionToken = _validatedToken;
        app.TargetDbId = dbId.Replace("-", "");   // 기존 저장 형식(32hex 무대시)과 통일
        app.NotionDbTitle = dbTitle;
        if (hasCategory)
            app.CategoryPropertyName = category!;
        // 카테고리 없음이면 CategoryPropertyName 기본값("Category") 유지 — 스펙 합의:
        // 속성이 없을 때 옵션이 비는 현행 동작과 동일

        _settings.SaveToken(_validatedToken);
        _settings.Set("target_db_id", app.TargetDbId);
        _settings.Set("category_property_name", app.CategoryPropertyName);
        _settings.Set("notion_db_title", dbTitle);

        // 토큰이 다른 integration일 수 있음 — 옛 bot id가 남으면 모든 push가 충돌 처리됨
        App.Current.InvalidateBotUserId();
        app.IsSyncPaused = false;

        if (hasCategory)
        {
            try { await App.Current.RefreshCategoryOptionsAsync(); }
            catch { /* 옵션 갱신 실패는 설정 창 [옵션 새로고침]으로 복구 가능 — 온보딩은 막지 않는다 */ }
        }

        Close();
    }

    private static void SetStatus(System.Windows.Controls.TextBlock target, string text, bool error = false)
    {
        target.Text = text;
        target.Foreground = error ? Brushes.Red : Brushes.Gray;
    }
}
```

- [ ] **Step 3: App 배선 — 시작 게이트 + OpenOnboarding**

`App.xaml.cs:77-78`을 다음으로 교체:

```csharp
        if (!AppSettings.Instance.IsConfigured)
            OpenOnboarding();
```

`App.xaml.cs`의 `OpenSettings()` 메서드 바로 아래에 추가:

```csharp
    public void OpenOnboarding()
    {
        var existing = Windows.OfType<OnboardingWindow>().FirstOrDefault();
        if (existing != null) { existing.Activate(); return; }
        new OnboardingWindow(SettingsRepo!, _notionClient!).Show();
    }
```

- [ ] **Step 4: 빌드 + 전체 테스트**

```bash
taskkill //IM Noticker.exe //F 2>/dev/null; dotnet build Noticker.csproj
```
Expected: Build succeeded

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (220개)

- [ ] **Step 5: Commit**

```bash
git add Windows/OnboardingWindow.xaml Windows/OnboardingWindow.xaml.cs App.xaml.cs
git commit -m "feat: two-step onboarding wizard — token validation + DB/category dropdowns"
```

---

### Task 6: SettingsWindow 노션 섹션 → 요약 + [연결 다시 설정]

**Files:**
- Modify: `Windows/SettingsWindow.xaml:146-167` (토큰/DB/카테고리/연결 테스트 블록)
- Modify: `Windows/SettingsWindow.xaml.cs`

- [ ] **Step 1: XAML 교체**

`Windows/SettingsWindow.xaml`에서 `<!-- Notion Token -->`부터
`<TextBlock x:Name="TestResultText" .../>` 까지(146-165행 블록)를 다음으로 교체:

```xml
                <!-- 노션 연결 — 입력은 OnboardingWindow가 소유, 여기는 요약만 -->
                <TextBlock Text="노션 연결" Style="{StaticResource SectionHeadingStyle}"
                           Margin="0,0,0,8"/>
                <TextBlock x:Name="ConnectionSummary" FontSize="13" Foreground="#1A1A1A"
                           TextWrapping="Wrap" Margin="0,0,0,8"/>
                <StackPanel Orientation="Horizontal" Margin="0,0,0,12">
                    <Button x:Name="ReconnectButton" Content="연결 다시 설정"
                            Style="{StaticResource ActionButtonStyle}"
                            Click="ReconnectButton_Click"/>
                </StackPanel>
```

(다음 줄의 `<Separator .../>`와 "카테고리 옵션" 섹션은 그대로 둔다.)

- [ ] **Step 2: 코드비하인드 정리**

`Windows/SettingsWindow.xaml.cs`에서:

1. `LoadCurrentValues()`의 토큰/DB/카테고리 3줄
   (`if (app.NotionToken is not null) TokenBox.Password = ...`, `DbIdBox.Text = ...`,
   `CategoryPropertyBox.Text = ...`)을 다음 한 블록으로 교체:

```csharp
        ConnectionSummary.Text = app.IsConfigured
            ? $"연결됨: {app.NotionDbTitle ?? app.TargetDbId}"
            : "연결 안 됨";
```

2. `TestButton_Click` 메서드 전체 삭제.

3. `ReconnectButton_Click` 추가 (`LoadCurrentValues` 아래):

```csharp
    private void ReconnectButton_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new OnboardingWindow(_settings, _client) { Owner = this };
        wizard.ShowDialog();
        LoadCurrentValues();   // 연결 요약 갱신
    }
```

4. `RefreshCatButton_Click`에서 `ApplyToAppSettings();` 줄 삭제 (입력란이 없어졌으므로).

5. `ApplyToAppSettings()`에서 토큰 4줄(`var token = ...`부터 `app.NotionToken = token;`),
   `app.TargetDbId = NormalizeDbId(...)`, `app.CategoryPropertyName = ...` 블록 삭제.

6. `PersistSettings()`에서 토큰 저장 블록(`if (app.NotionToken is not null) { ... }`),
   `if (app.TargetDbId is not null) _settings.Set("target_db_id", ...)`,
   `_settings.Set("category_property_name", ...)` 삭제.
   끝의 `if (app.IsConfigured) app.IsSyncPaused = false;`는 유지.

7. 이제 미사용이 된 `NormalizeDbId`, `IsHex` 메서드 삭제.
   `using Brushes = ...`가 미사용이 되면 함께 삭제 (TestButton_Click만 쓰고 있었음).

- [ ] **Step 3: 빌드 + 전체 테스트**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (220개)

- [ ] **Step 4: Commit**

```bash
git add Windows/SettingsWindow.xaml Windows/SettingsWindow.xaml.cs
git commit -m "feat: replace settings Notion fields with connection summary + reconnect button"
```

---

### Task 7: NoteListWindow 카드형

동작(클릭→스티커, 검색, 삭제 확인, 빈 상태) 불변. 테마 브러시를 NoteItem에 박아
바인딩으로 색을 처리하고, `ApplyRowColors` 디스패처 후처리를 제거한다.
hover(테두리+그림자+✕ 노출)는 코드비하인드 MouseEnter/Leave — DataTemplate 트리거의
Setter에는 Binding을 쓸 수 없어서다.

**Files:**
- Modify: `Windows/NoteListWindow.xaml` (전체 교체)
- Modify: `Windows/NoteListWindow.xaml.cs` (전체 교체)

- [ ] **Step 1: XAML 교체**

`Windows/NoteListWindow.xaml` 전체 내용:

```xml
<Window x:Class="Noticker.Windows.NoteListWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="스티커 메모" Width="320" Height="500"
        MinWidth="240" MinHeight="300"
        WindowStyle="SingleBorderWindow"
        ShowInTaskbar="False"
        ResizeMode="CanResize">
    <DockPanel x:Name="RootPanel">
        <!-- 검색 — 밑줄(line-style) -->
        <Border x:Name="SearchBorder" DockPanel.Dock="Top" Padding="12,8"
                BorderThickness="0,0,0,1">
            <Grid>
                <TextBox x:Name="SearchBox" TextChanged="SearchBox_TextChanged"
                         Padding="2,4" BorderThickness="0,0,0,1" Background="Transparent"/>
                <TextBlock x:Name="SearchPlaceholder" Text="검색..."
                           Padding="2,4" IsHitTestVisible="False"
                           VerticalAlignment="Center">
                    <TextBlock.Style>
                        <Style TargetType="TextBlock">
                            <Setter Property="Visibility" Value="Collapsed"/>
                            <Style.Triggers>
                                <DataTrigger Binding="{Binding ElementName=SearchBox, Path=Text.Length}" Value="0">
                                    <Setter Property="Visibility" Value="Visible"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </TextBlock.Style>
                </TextBlock>
            </Grid>
        </Border>

        <!-- Notion 가져오기 진입 — 조용한 외곽선 버튼 -->
        <Border x:Name="ImportBorder" DockPanel.Dock="Top" Padding="8,6"
                BorderThickness="0,0,0,1">
            <Button x:Name="ImportButton" Content="↓ Notion에서 가져오기"
                    Click="ImportButton_Click"
                    Padding="6,4" HorizontalAlignment="Stretch"
                    FontSize="11" Cursor="Hand">
                <Button.Template>
                    <ControlTemplate TargetType="Button">
                        <Border x:Name="bd" Background="Transparent"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="1" CornerRadius="3"
                                Padding="{TemplateBinding Padding}">
                            <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
                        </Border>
                        <ControlTemplate.Triggers>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="bd" Property="Opacity" Value="0.7"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Button.Template>
            </Button>
        </Border>

        <!-- 빈 상태 메시지 -->
        <TextBlock x:Name="EmptyLabel"
                   Visibility="Collapsed"
                   HorizontalAlignment="Center"
                   VerticalAlignment="Center"
                   FontSize="12"
                   TextWrapping="Wrap"
                   Margin="24,0"
                   DockPanel.Dock="Bottom"/>

        <!-- 노트 목록 — 카드형 -->
        <ListView x:Name="NoteList"
                  SelectionChanged="NoteList_SelectionChanged"
                  BorderThickness="0"
                  Padding="0,4"
                  ScrollViewer.HorizontalScrollBarVisibility="Disabled">
            <ListView.ItemContainerStyle>
                <Style TargetType="ListViewItem">
                    <Setter Property="HorizontalContentAlignment" Value="Stretch"/>
                    <Setter Property="Padding" Value="0"/>
                    <Setter Property="Margin" Value="0"/>
                    <Setter Property="Cursor" Value="Hand"/>
                    <Setter Property="Background" Value="Transparent"/>
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
            <ListView.ItemTemplate>
                <DataTemplate>
                    <Border x:Name="CardBorder" Margin="8,3" Padding="11,9"
                            CornerRadius="5" BorderThickness="1"
                            Background="{Binding CardBg}"
                            BorderBrush="{Binding CardBorderBrush}"
                            MouseEnter="Card_MouseEnter" MouseLeave="Card_MouseLeave">
                        <Grid>
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <StackPanel Grid.Column="0">
                                <TextBlock Text="{Binding Title}"
                                           TextTrimming="CharacterEllipsis"
                                           FontSize="12"
                                           Foreground="{Binding TitleFg}"/>
                                <TextBlock Text="{Binding DateLabel}"
                                           FontSize="10" Margin="0,3,0,0"
                                           Foreground="{Binding MutedFg}"/>
                            </StackPanel>
                            <!-- 숨김 알약 배지 -->
                            <Border Grid.Column="1" Visibility="{Binding IsHiddenBadge}"
                                    Background="{Binding BadgeBg}" CornerRadius="8"
                                    Padding="7,1" Margin="5,0,0,0" VerticalAlignment="Center">
                                <TextBlock Text="숨김" FontSize="9"
                                           Foreground="{Binding BadgeFg}"/>
                            </Border>
                            <!-- hover한 카드에만 보이는 삭제 ✕ -->
                            <Button x:Name="DeleteX" Grid.Column="2" Content="✕"
                                    Tag="{Binding Id}" Click="DeleteButton_Click"
                                    Visibility="Collapsed"
                                    FontSize="12" Padding="4,0" Margin="8,0,0,0"
                                    VerticalAlignment="Center" Cursor="Hand"
                                    Foreground="{Binding MutedFg}">
                                <Button.Template>
                                    <ControlTemplate TargetType="Button">
                                        <Border Background="Transparent"
                                                Padding="{TemplateBinding Padding}">
                                            <ContentPresenter/>
                                        </Border>
                                    </ControlTemplate>
                                </Button.Template>
                            </Button>
                        </Grid>
                    </Border>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </DockPanel>
</Window>
```

- [ ] **Step 2: 코드비하인드 교체**

`Windows/NoteListWindow.xaml.cs` 전체 내용:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Noticker.Data;
using Noticker.Models;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Button = System.Windows.Controls.Button;
using MessageBox = System.Windows.MessageBox;

namespace Noticker.Windows;

public partial class NoteListWindow : Window
{
    private readonly StickerRepository _repo;
    private List<NoteItem> _allItems = [];
    private bool _needsRefresh = false;

    // 카드 색은 NoteItem에 브러시로 박아 바인딩으로 그린다 — 테마 전환 시 Refresh()로
    // 아이템을 재생성하므로 디스패처 후처리(구 ApplyRowColors)가 필요 없다.
    private static readonly ThemePalette _lightPalette = new(
        WinBg: new SolidColorBrush(Color.FromRgb(0xFA, 0xFA, 0xFA)),
        CardBg: Brushes.White,
        CardBorder: new SolidColorBrush(Color.FromRgb(0xEC, 0xEC, 0xEC)),
        HoverBorder: new SolidColorBrush(Color.FromRgb(0xD5, 0xD5, 0xD5)),
        TitleFg: new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
        MutedFg: new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
        BadgeBg: new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
        BadgeFg: new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
        LineBorder: new SolidColorBrush(Color.FromRgb(0xDD, 0xDD, 0xDD)));

    private static readonly ThemePalette _darkPalette = new(
        WinBg: new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
        CardBg: new SolidColorBrush(Color.FromRgb(0x3C, 0x3C, 0x3C)),
        CardBorder: new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)),
        HoverBorder: new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
        TitleFg: Brushes.White,
        MutedFg: new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
        BadgeBg: new SolidColorBrush(Color.FromRgb(0x4A, 0x4A, 0x4A)),
        BadgeFg: new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)),
        LineBorder: new SolidColorBrush(Color.FromRgb(0x50, 0x50, 0x50)));

    private static readonly DropShadowEffect _hoverShadow = new()
    {
        BlurRadius = 4,
        ShadowDepth = 1,
        Opacity = 0.12
    };

    private static ThemePalette Palette =>
        AppSettings.Instance.ColorSwapped ? _darkPalette : _lightPalette;

    public NoteListWindow(StickerRepository repo)
    {
        _repo = repo;
        InitializeComponent();
        ApplyTheme();
        AppSettings.Instance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.ColorSwapped))
                ApplyTheme();
        };
        Refresh();
    }

    private void ApplyTheme()
    {
        var p = Palette;

        RootPanel.Background = p.WinBg;
        NoteList.Background = p.WinBg;
        NoteList.Foreground = p.TitleFg;

        SearchBorder.BorderBrush = p.LineBorder;
        ImportBorder.BorderBrush = p.LineBorder;
        SearchBox.Foreground = p.TitleFg;
        SearchBox.BorderBrush = p.LineBorder;
        SearchBox.CaretBrush = p.TitleFg;
        SearchPlaceholder.Foreground = p.MutedFg;
        EmptyLabel.Foreground = p.MutedFg;
        ImportButton.Foreground = p.MutedFg;
        ImportButton.BorderBrush = p.CardBorder;

        // 카드 브러시는 아이템에 박혀 있어 재생성 필요
        if (_allItems.Count > 0) Refresh();
    }

    private void Refresh()
    {
        var p = Palette;
        _allItems = _repo.GetAllSummary()
            .Select(t => NoteItem.From(t.Id, t.Title, t.Body, t.UpdatedAt, t.IsHidden, p))
            .ToList();
        _needsRefresh = false;
        ApplyFilter();
    }

    protected override void OnActivated(EventArgs e)
    {
        base.OnActivated(e);
        if (_needsRefresh) Refresh();
    }

    protected override void OnClosed(EventArgs e)
    {
        AppSettings.Instance.PropertyChanged -= null;
        base.OnClosed(e);
    }

    private void ApplyFilter()
    {
        var q = SearchBox.Text.Trim();
        var filtered = string.IsNullOrEmpty(q)
            ? _allItems
            : _allItems.Where(i =>
                i.Title.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        NoteList.ItemsSource = filtered;

        if (filtered.Count == 0)
        {
            EmptyLabel.Text = q.Length > 0
                ? "검색 결과가 없습니다."
                : "메모가 없습니다. 트레이를 우클릭해 새 스티커를 만드세요.";
            EmptyLabel.Visibility = Visibility.Visible;
        }
        else
        {
            EmptyLabel.Visibility = Visibility.Collapsed;
        }
    }

    // hover 비주얼(테두리/그림자/✕)은 코드로 — DataTemplate 트리거의 Setter에는
    // Binding을 쓸 수 없어 테마별 브러시를 줄 수 없다.
    private void Card_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card || card.DataContext is not NoteItem item) return;
        card.BorderBrush = item.HoverBorderBrush;
        card.Effect = _hoverShadow;
        if (FindVisualChild<Button>(card, "DeleteX") is { } del)
            del.Visibility = Visibility.Visible;
    }

    private void Card_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card || card.DataContext is not NoteItem item) return;
        card.BorderBrush = item.CardBorderBrush;
        card.Effect = null;
        if (FindVisualChild<Button>(card, "DeleteX") is { } del)
            del.Visibility = Visibility.Collapsed;
    }

    private static T? FindVisualChild<T>(DependencyObject parent, string name) where T : FrameworkElement
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T fe && fe.Name == name) return fe;
            var found = FindVisualChild<T>(child, name);
            if (found != null) return found;
        }
        return null;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NoteList.SelectedItem is NoteItem item)
        {
            App.Current.ShowSticker(item.Id);
            _needsRefresh = true;
            NoteList.SelectedItem = null;
        }
    }

    private void ImportButton_Click(object sender, RoutedEventArgs e)
    {
        App.Current.OpenNotionImport();
        _needsRefresh = true;   // 가져온 스티커가 목록에 보이도록
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            var result = MessageBox.Show(
                "이 메모를 삭제할까요?\nNotion에 동기화된 내용은 그대로 유지됩니다.",
                "메모 삭제",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                App.Current.DeleteSticker(id);
                Refresh();
            }
        }
    }

    private record ThemePalette(
        Brush WinBg, Brush CardBg, Brush CardBorder, Brush HoverBorder,
        Brush TitleFg, Brush MutedFg, Brush BadgeBg, Brush BadgeFg, Brush LineBorder);

    private record NoteItem(
        string Id, string Title, string DateLabel, string IsHiddenBadge,
        Brush CardBg, Brush CardBorderBrush, Brush HoverBorderBrush,
        Brush TitleFg, Brush MutedFg, Brush BadgeBg, Brush BadgeFg)
    {
        public static NoteItem From(string id, string title, string body, string updatedAt,
            bool isHidden, ThemePalette p)
        {
            var displayTitle = !string.IsNullOrWhiteSpace(title) ? title : "(제목 없음)";
            var dateLabel = DateTime.TryParse(updatedAt, out var dt)
                ? dt.ToLocalTime().ToString("yyyy년 M월 d일")
                : "";
            return new NoteItem(id, displayTitle, dateLabel,
                isHidden ? "Visible" : "Collapsed",
                p.CardBg, p.CardBorder, p.HoverBorder,
                p.TitleFg, p.MutedFg, p.BadgeBg, p.BadgeFg);
        }
    }
}
```

- [ ] **Step 3: 빌드 + 전체 테스트**

```bash
taskkill //IM Noticker.exe //F 2>/dev/null; dotnet build Noticker.csproj
```
Expected: Build succeeded

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (220개)

- [ ] **Step 4: Commit**

```bash
git add Windows/NoteListWindow.xaml Windows/NoteListWindow.xaml.cs
git commit -m "feat: card-style note list — hover delete, pill badge, theme-aware cards"
```

---

### Task 8: 통합 검증 + 수동 QA

- [ ] **Step 1: 전체 테스트 최종 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (220개, 실패 0)

- [ ] **Step 2: 앱 실행**

```bash
taskkill //IM Noticker.exe //F 2>/dev/null
dotnet build Noticker.csproj
start "" "bin/Debug/net8.0-windows/Noticker.exe"
```

- [ ] **Step 3: 사용자 수동 QA 체크리스트 전달** (사용자 직접 QA — 워크플로 선호)

```
[온보딩 — 기존 연결이 있으므로 설정 창 경유]
1. 트레이 → 설정 → "연결됨: …" 요약 표시 확인
2. [연결 다시 설정] → 위저드 1단계: 기존 토큰 미리 채워짐, 점 ●○
3. 토큰을 일부러 깨뜨리고 [다음] → 빨간 "실패: 토큰이 유효하지 않습니다 (401)"
4. 올바른 토큰 → 2단계 진입(점 ○●), DB 드롭다운에 공유된 DB 이름 표시
5. DB 선택 → 카테고리 드롭다운에 select 속성 + "(카테고리 없음)", Category 자동 선택
6. [← 이전] → 1단계 복귀(토큰 보존) → [다음] → 2단계 재진입
7. [시작하기] → 창 닫힘, 설정 창 요약이 새 DB 제목으로 갱신
8. 스티커에서 메모 수정 → 1분 내 Notion 반영 (sync.log 확인 가능)

[노트 목록]
9. 트레이 → 메모 목록: 카드형, 파란 하이라이트 없음
10. 카드 hover → 테두리 진해짐 + 그림자 + ✕ 표시, 벗어나면 사라짐
11. ✕ 클릭 → 삭제 확인 다이얼로그 (동작 기존과 동일)
12. 숨김 스티커 → 제목 옆 "숨김" 알약 배지
13. 검색 입력 → 필터 동작, 플레이스홀더 표시
14. 설정 → 색상 Swap → 다크 카드 색 확인 (hover 포함)
15. "Notion에서 가져오기" 버튼 클릭 → 가져오기 창 열림

[첫 실행 시나리오 (선택)]
16. %APPDATA%\Noticker\noticker.db 백업 후 삭제 → 앱 실행 → 온보딩 자동 표시
    확인 후 백업 복원
```

- [ ] **Step 4: 사용자 확인 후 push** (사용자 확인 전 push 금지 — 워크플로 선호)

```bash
git push
```

---

## Self-Review 결과

- **스펙 커버리지**: 위저드 2단계+점/토큰 검증/DB 드롭다운/0개 안내/카테고리 select/
  "(카테고리 없음)" 동작/시작 게이트/설정 창 요약/notion_db_title 캐시/카드형 전 항목/
  다크 모드/오류 인라인 표시 — 전부 태스크에 매핑됨. ✓
- **플레이스홀더**: 없음 — 모든 코드 스텝에 전체 코드 포함. ✓
- **타입 일관성**: `NotionDirectory.ParseDatabaseList` → `List<(string Id, string Title)>`가
  `SearchDatabasesAsync` 반환 타입·`_databases` 필드와 일치. `RefreshCategoryOptionsAsync`
  시그니처(Task 4)와 호출(Task 5/6) 일치. `NoteItem` 브러시 속성명이 XAML 바인딩과 일치. ✓
