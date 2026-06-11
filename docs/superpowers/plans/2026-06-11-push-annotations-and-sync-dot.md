# push 서식 annotation + 동기화 표시등 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 스티커의 굵게/밑줄을 Notion rich_text annotation으로 push해 서식 비대칭을 해소하고, 빈 메모의 동기화 표시등을 회색으로 바꾼다.

**Architecture:** 기존 pull 중간 모델(`NoteLine`/`NoteRun`, Sync/NotionBlockConverter.cs)을 push에도 재사용한다. `SaveBodyContent`가 FlowDocument를 순회할 때 `NoteLineExtractor`(신설)로 run을 추출해 `NoteLineSerializer`(신설) JSON으로 `body_runs` 컬럼(v5 마이그레이션)에 저장하고, push는 `BodyRuns`가 있으면 run 단위 annotation 블록을, 없으면 기존 plain 경로를 쓴다. pull/가져오기도 이미 가진 NoteLine으로 BodyRuns를 쌍으로 저장한다.

**Tech Stack:** .NET 8 WPF, xUnit(STA 패턴 포함), System.Text.Json, SQLite(PRAGMA user_version 마이그레이션).

**스펙:** `docs/superpowers/specs/2026-06-11-push-annotations-and-sync-dot-design.md`

**빌드/테스트 명령:**
- 테스트: `dotnet test Noticker.Tests/Noticker.Tests.csproj` (루트 dotnet test는 no-op)
- 앱 빌드: `taskkill //IM Noticker.exe //F 2>/dev/null; dotnet build Noticker.csproj`
- 현재 테스트 수: 222

---

## File Structure

| 파일 | 작업 | 책임 |
|---|---|---|
| `Sync/NoteLineSerializer.cs` | 신설 | NoteLine ↔ JSON (순수 함수, 실패=null) |
| `Windows/NoteLineExtractor.cs` | 신설 | FlowDocument → NoteLine (STA 전용, RtfComposer의 역방향) |
| `Models/Sticker.cs` | 수정 | `BodyRuns` 속성 |
| `Data/StickerRepository.cs` | 수정 | v5 마이그레이션 + Insert/Update/Bind/Map |
| `Sync/NotionClient.cs` | 수정 | `BuildAnnotatedBlocks`(정적·테스트 대상) + `BuildBodyBlocks` 폴백 + push 배선 |
| `Windows/StickerWindow.xaml.cs` | 수정 | SaveBodyContent run 저장, ApplyPulledContent 시그니처, 표시등 회색 분기 |
| `Sync/PullService.cs` | 수정 | Apply 시 BodyRuns 전달 |
| `Sync/NotionBlockConverter.cs` | 수정 | `HasAnnotations` → `HasUnsupportedAnnotations` (bold/underline 제외) |
| `Windows/NotionImportWindow.xaml.cs` | 수정 | 경고 축소 + BodyRuns 전달 |
| `App.xaml.cs` | 수정 | `CreateImportedSticker`에 bodyRuns 파라미터 |
| `Noticker.Tests/NoteLineSerializerTests.cs` | 신설 | 직렬화 왕복/실패 |
| `Noticker.Tests/AnnotatedBlocksTests.cs` | 신설 | run→블록 JSON 형태 |
| `Noticker.Tests/NoteLineExtractorStaTests.cs` | 신설 | Compose→Load→Extract 왕복 (STA) |
| `Noticker.Tests/StickerRepositoryTests.cs` | 수정 | BodyRuns 왕복 |
| `Noticker.Tests/NotionBlockConverterTests.cs` | 수정 | HasUnsupportedAnnotations 갱신 |

---

### Task 1: NoteLineSerializer (TDD)

**Files:**
- Create: `Noticker.Tests/NoteLineSerializerTests.cs`
- Create: `Sync/NoteLineSerializer.cs`

- [ ] **Step 1: 실패하는 테스트 작성**

`Noticker.Tests/NoteLineSerializerTests.cs` 전체 내용:

```csharp
using Noticker.Sync;

namespace Noticker.Tests;

public class NoteLineSerializerTests
{
    private static List<NoteLine> Sample() =>
    [
        new(NoteLineKind.Paragraph, [new NoteRun("plain ", false, false), new NoteRun("굵게🔥", true, false)]),
        new(NoteLineKind.Bullet, [new NoteRun("밑줄", false, true)]),
        new(NoteLineKind.Number, [new NoteRun("둘 다", true, true)]),
        new(NoteLineKind.Paragraph, []),
    ];

    [Fact]
    public void RoundTrip_PreservesKindsRunsAndFlags()
    {
        var json = NoteLineSerializer.Serialize(Sample());
        var back = NoteLineSerializer.Deserialize(json);

        Assert.NotNull(back);
        Assert.Equal(Sample().Count, back!.Count);
        for (int i = 0; i < back.Count; i++)
        {
            Assert.Equal(Sample()[i].Kind, back[i].Kind);
            Assert.Equal(Sample()[i].Runs, back[i].Runs);
        }
    }

    [Fact]
    public void Serialize_UsesCompactKindAndRunKeys()
    {
        var json = NoteLineSerializer.Serialize(
            [new(NoteLineKind.Bullet, [new NoteRun("a", true, false)])]);

        Assert.Contains("\"Kind\":\"bullet\"", json);
        Assert.Contains("\"T\":\"a\"", json);
        Assert.Contains("\"B\":true", json);
        Assert.Contains("\"U\":false", json);
    }

    [Fact]
    public void RoundTrip_EmptyList()
    {
        Assert.Equal([], NoteLineSerializer.Deserialize(NoteLineSerializer.Serialize([])));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{\"oops\":1}")]
    [InlineData("[{\"Kind\":\"unknown\",\"Runs\":[]}]")]
    [InlineData("[{\"Kind\":\"paragraph\"}]")]
    public void Deserialize_InvalidInput_ReturnsNull(string? json)
    {
        Assert.Null(NoteLineSerializer.Deserialize(json));
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter NoteLineSerializerTests`
Expected: 컴파일 에러 — `NoteLineSerializer` 없음

- [ ] **Step 3: 최소 구현**

`Sync/NoteLineSerializer.cs` 전체 내용:

```csharp
using System.Text.Json;

namespace Noticker.Sync;

// NoteLine ↔ JSON (stickers.body_runs 컬럼) — HTTP/WPF 없이 단위 테스트 가능한 순수 함수.
// 역직렬화 실패는 null 반환 — 호출자(push)가 plain 폴백을 타도록 예외를 삼킨다
public static class NoteLineSerializer
{
    private sealed record RunDto(string? T, bool B, bool U);
    private sealed record LineDto(string? Kind, List<RunDto>? Runs);

    public static string Serialize(IReadOnlyList<NoteLine> lines)
    {
        var dtos = lines.Select(l => new LineDto(
            l.Kind switch
            {
                NoteLineKind.Bullet => "bullet",
                NoteLineKind.Number => "numbered",
                _ => "paragraph",
            },
            l.Runs.Select(r => new RunDto(r.Text, r.Bold, r.Underline)).ToList())).ToList();
        return JsonSerializer.Serialize(dtos);
    }

    public static List<NoteLine>? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try
        {
            var dtos = JsonSerializer.Deserialize<List<LineDto>>(json);
            if (dtos is null) return null;

            var lines = new List<NoteLine>(dtos.Count);
            foreach (var d in dtos)
            {
                NoteLineKind? kind = d.Kind switch
                {
                    "paragraph" => NoteLineKind.Paragraph,
                    "bullet" => NoteLineKind.Bullet,
                    "numbered" => NoteLineKind.Number,
                    _ => null,
                };
                // 알 수 없는 Kind/Runs 누락 — 부분 복구 대신 통째로 폴백 (본문 유실 방지)
                if (kind is null || d.Runs is null) return null;
                lines.Add(new NoteLine(kind.Value,
                    d.Runs.Select(r => new NoteRun(r.T ?? "", r.B, r.U)).ToList()));
            }
            return lines;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter NoteLineSerializerTests`
Expected: PASS (9개: Fact 3 + Theory 6)

- [ ] **Step 5: Commit**

```bash
git add Sync/NoteLineSerializer.cs Noticker.Tests/NoteLineSerializerTests.cs
git commit -m "feat: NoteLine JSON serializer for body_runs column"
```

---

### Task 2: Sticker.BodyRuns + DB v5 마이그레이션 (TDD)

**Files:**
- Modify: `Models/Sticker.cs:18` 근처 (BodyRtf 아래)
- Modify: `Data/StickerRepository.cs` (MigrateIfNeeded, Insert, Update, Bind, Map)
- Test: `Noticker.Tests/StickerRepositoryTests.cs`

- [ ] **Step 1: 실패하는 테스트 추가**

`Noticker.Tests/StickerRepositoryTests.cs`에 추가. 파일의 기존 픽스처(temp db 경로 + repo 필드)를 먼저 읽고 동일 패턴으로 — 아래 코드의 `_repo`를 실제 필드명에 맞출 것:

```csharp
    [Fact]
    public void BodyRuns_InsertUpdate_RoundTrip()
    {
        var s = new Sticker
        {
            MonitorDeviceName = "m",
            BodyRuns = """[{"Kind":"paragraph","Runs":[{"T":"굵게","B":true,"U":false}]}]""",
        };
        _repo.Insert(s);
        var loaded = _repo.GetAll().Single(x => x.Id == s.Id);
        Assert.Equal(s.BodyRuns, loaded.BodyRuns);

        loaded.BodyRuns = null;
        _repo.Update(loaded);
        Assert.Null(_repo.GetAll().Single(x => x.Id == s.Id).BodyRuns);
    }
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter StickerRepositoryTests`
Expected: 컴파일 에러 — `BodyRuns` 속성 없음

- [ ] **Step 3: 구현**

`Models/Sticker.cs` — `BodyRtf` 선언 아래에 추가:

```csharp
    public string? BodyRuns { get; set; }   // NoteLine JSON (push 서식) — null = plain 폴백
```

`Data/StickerRepository.cs`:

1. `MigrateIfNeeded`의 `if (version < 4) MigrateToV4(conn);` 아래에:
```csharp
        if (version < 5) MigrateToV5(conn);
```

2. `MigrateToV4` 메서드 아래에:
```csharp
    private static void MigrateToV5(SqliteConnection conn)
    {
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "ALTER TABLE stickers ADD COLUMN body_runs TEXT";
        cmd.ExecuteNonQuery();
        cmd.CommandText = "PRAGMA user_version = 5";
        cmd.ExecuteNonQuery();
        tx.Commit();
    }
```

3. `Insert`의 SQL: 컬럼 목록 `pull_disabled)` → `pull_disabled, body_runs)`,
   VALUES `$pull_disabled)` → `$pull_disabled, $body_runs)`

4. `Update`의 SQL: `pull_disabled       = $pull_disabled` 다음 줄에
   `,
                    body_runs           = $body_runs` 추가 (WHERE 앞)

5. `Bind` 끝에:
```csharp
        cmd.Parameters.AddWithValue("$body_runs", (object?)s.BodyRuns ?? DBNull.Value);
```

6. `Map` 끝에 (PullDisabled 줄 다음):
```csharp
        BodyRuns = r.IsDBNull(r.GetOrdinal("body_runs")) ? null : r.GetString(r.GetOrdinal("body_runs")),
```

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (223개 — 222 + 1)

- [ ] **Step 5: Commit**

```bash
git add Models/Sticker.cs Data/StickerRepository.cs Noticker.Tests/StickerRepositoryTests.cs
git commit -m "feat: body_runs column (schema v5) for formatted push"
```

---

### Task 3: run 단위 annotation 블록 + push 배선 (TDD)

**Files:**
- Create: `Noticker.Tests/AnnotatedBlocksTests.cs`
- Modify: `Sync/NotionClient.cs` (BuildParagraphBlocks 근처 + CreatePageAsync/UpdatePageAsync)

- [ ] **Step 1: 실패하는 테스트 작성**

`Noticker.Tests/AnnotatedBlocksTests.cs` 전체 내용:

```csharp
using System.Text.Json;
using Noticker.Sync;

namespace Noticker.Tests;

// BuildAnnotatedBlocks의 출력(익명 객체)을 JSON으로 직렬화해 형태를 검증
public class AnnotatedBlocksTests
{
    private static JsonElement Build(params NoteLine[] lines)
    {
        var json = JsonSerializer.Serialize(NotionClient.BuildAnnotatedBlocks(lines));
        return JsonDocument.Parse(json).RootElement;
    }

    [Fact]
    public void PlainRun_OmitsAnnotations()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph, [new NoteRun("plain", false, false)]));

        var rt = root[0].GetProperty("paragraph").GetProperty("rich_text")[0];
        Assert.Equal("plain", rt.GetProperty("text").GetProperty("content").GetString());
        Assert.False(rt.TryGetProperty("annotations", out _));
    }

    [Fact]
    public void FormattedRuns_CarryAnnotations()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun("b", true, false), new NoteRun("u", false, true), new NoteRun("bu", true, true)]));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.True(rts[0].GetProperty("annotations").GetProperty("bold").GetBoolean());
        Assert.False(rts[0].GetProperty("annotations").GetProperty("underline").GetBoolean());
        Assert.True(rts[1].GetProperty("annotations").GetProperty("underline").GetBoolean());
        Assert.True(rts[2].GetProperty("annotations").GetProperty("bold").GetBoolean());
        Assert.True(rts[2].GetProperty("annotations").GetProperty("underline").GetBoolean());
    }

    [Fact]
    public void Kinds_MapToBlockTypes()
    {
        var root = Build(
            new NoteLine(NoteLineKind.Paragraph, [new NoteRun("p", false, false)]),
            new NoteLine(NoteLineKind.Bullet, [new NoteRun("b", false, false)]),
            new NoteLine(NoteLineKind.Number, [new NoteRun("n", false, false)]));

        Assert.Equal("paragraph", root[0].GetProperty("type").GetString());
        Assert.Equal("bulleted_list_item", root[1].GetProperty("type").GetString());
        Assert.Equal("numbered_list_item", root[2].GetProperty("type").GetString());
    }

    [Fact]
    public void EmptyLine_HasEmptyRichText()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph, []));
        Assert.Equal(0, root[0].GetProperty("paragraph").GetProperty("rich_text").GetArrayLength());
    }

    [Fact]
    public void LongRun_SplitsIntoChunks_EachKeepingAnnotations()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun(new string('가', 2500), true, false)]));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.Equal(2, rts.GetArrayLength());
        Assert.Equal(2000, rts[0].GetProperty("text").GetProperty("content").GetString()!.Length);
        Assert.Equal(500, rts[1].GetProperty("text").GetProperty("content").GetString()!.Length);
        Assert.True(rts[0].GetProperty("annotations").GetProperty("bold").GetBoolean());
        Assert.True(rts[1].GetProperty("annotations").GetProperty("bold").GetBoolean());
    }

    [Fact]
    public void EmptyTextRun_IsDropped()
    {
        var root = Build(new NoteLine(NoteLineKind.Paragraph,
            [new NoteRun("", true, false), new NoteRun("a", false, false)]));

        var rts = root[0].GetProperty("paragraph").GetProperty("rich_text");
        Assert.Equal(1, rts.GetArrayLength());
        Assert.Equal("a", rts[0].GetProperty("text").GetProperty("content").GetString());
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter AnnotatedBlocksTests`
Expected: 컴파일 에러 — `BuildAnnotatedBlocks` 없음

- [ ] **Step 3: 구현**

`Sync/NotionClient.cs`의 `BuildParagraphBlocks` 메서드 바로 앞에 추가:

```csharp
    // BodyRuns(NoteLine JSON)가 있으면 서식 보존 블록, 없거나 깨졌으면 기존 plain 경로 —
    // 폴백이 push 자체를 보장한다 (기존 스티커는 다음 편집에서 BodyRuns가 생긴다)
    private static object[] BuildBodyBlocks(Sticker s)
    {
        var lines = NoteLineSerializer.Deserialize(s.BodyRuns);
        if (lines is not null) return BuildAnnotatedBlocks(lines);
        if (s.BodyRuns is not null)
            SyncLog.Write($"push: body_runs 역직렬화 실패 — plain 폴백 (sticker={s.Id[..8]})");
        return BuildParagraphBlocks(s.Body);
    }

    // run 단위 rich_text 블록 — annotations는 굵게/밑줄 중 하나라도 있을 때만 포함
    // (Notion 기본값이 모두 false라 생략 가능, payload 절약). 2000자 청크는 run 단위 분할.
    public static object[] BuildAnnotatedBlocks(IReadOnlyList<NoteLine> lines)
    {
        return lines.Select(line =>
        {
            var richText = line.Runs
                .Where(r => !string.IsNullOrEmpty(r.Text))
                .SelectMany(r => SplitIntoChunks(r.Text, ChunkSize)
                    .Select(c => MakeRichText(c, r.Bold, r.Underline)))
                .ToArray();

            return line.Kind switch
            {
                NoteLineKind.Bullet => (object)new
                {
                    type = "bulleted_list_item",
                    bulleted_list_item = new { rich_text = richText }
                },
                NoteLineKind.Number => new
                {
                    type = "numbered_list_item",
                    numbered_list_item = new { rich_text = richText }
                },
                _ => new
                {
                    type = "paragraph",
                    paragraph = new { rich_text = richText }
                },
            };
        }).ToArray();
    }

    private static object MakeRichText(string content, bool bold, bool underline) =>
        bold || underline
            ? new { text = new { content }, annotations = new { bold, underline } }
            : new { text = new { content } };
```

push 배선 2곳 교체:
- `CreatePageAsync`: `var children = BuildParagraphBlocks(s.Body);` → `var children = BuildBodyBlocks(s);`
- `UpdatePageAsync`: `var blocks = BuildParagraphBlocks(s.Body);` → `var blocks = BuildBodyBlocks(s);`

(주의: `SyncLog`는 `Noticker.Sync` 네임스페이스에 이미 존재 — PullService가 사용 중. 추가 using 불필요.)

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (229개 — 223 + 6)

- [ ] **Step 5: Commit**

```bash
git add Sync/NotionClient.cs Noticker.Tests/AnnotatedBlocksTests.cs
git commit -m "feat: push bold/underline as rich_text annotations with plain fallback"
```

---

### Task 4: NoteLineExtractor — FlowDocument → NoteLine (STA TDD)

**Files:**
- Create: `Noticker.Tests/NoteLineExtractorStaTests.cs`
- Create: `Windows/NoteLineExtractor.cs`
- Modify: `Windows/StickerWindow.xaml.cs:569` 근처 (SaveBodyContent 끝)

- [ ] **Step 1: 실패하는 테스트 작성**

`Noticker.Tests/NoteLineExtractorStaTests.cs` 전체 내용 (RunSta 패턴은 PullApplyStaRepro.cs와 동일):

```csharp
using System.Text;
using System.Windows;
using System.Windows.Documents;
using Noticker.Sync;
using Noticker.Windows;
using DataFormats = System.Windows.DataFormats;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Noticker.Tests;

// Compose → RichTextBox 로드 → Extract가 원본 NoteLine을 복원하는지 — 운영 경로 그대로의 왕복.
// FlowDocument는 STA 필수라 STA 스레드로 감싼다
public class NoteLineExtractorStaTests
{
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(
            $"STA 실패: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
    }

    private static List<NoteLine> LoadAndExtract(List<NoteLine> source)
    {
        var rtf = RtfComposer.Compose(source, null);
        var box = new RichTextBox();
        using var ms = new System.IO.MemoryStream(Encoding.Latin1.GetBytes(rtf));
        new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Load(ms, DataFormats.Rtf);
        return NoteLineExtractor.Extract(box.Document);
    }

    [Fact]
    public void RoundTrip_PreservesTextAndKinds()
    {
        RunSta(() =>
        {
            List<NoteLine> source =
            [
                new(NoteLineKind.Paragraph, [new NoteRun("문단 ", false, false), new NoteRun("굵게", true, false)]),
                new(NoteLineKind.Bullet, [new NoteRun("불릿 밑줄", false, true)]),
                new(NoteLineKind.Number, [new NoteRun("번호 둘다", true, true)]),
            ];

            var extracted = LoadAndExtract(source);

            // plain 본문 동등성 — 마커가 run 텍스트에 새지 않았는지까지 포함해 검증
            Assert.Equal(NotionBlockConverter.ToPlainText(source),
                         NotionBlockConverter.ToPlainText(extracted));

            // Kind 순서 보존 (RTF 왕복이 빈 trailing 문단을 덧붙일 수 있어 prefix 비교)
            var kinds = extracted.Select(l => l.Kind).Take(3).ToList();
            Assert.Equal([NoteLineKind.Paragraph, NoteLineKind.Bullet, NoteLineKind.Number], kinds);
        });
    }

    [Fact]
    public void RoundTrip_PreservesBoldAndUnderlineFlags()
    {
        RunSta(() =>
        {
            List<NoteLine> source =
            [
                new(NoteLineKind.Paragraph, [new NoteRun("plain ", false, false), new NoteRun("bold", true, false)]),
                new(NoteLineKind.Bullet, [new NoteRun("under", false, true)]),
            ];

            var extracted = LoadAndExtract(source);

            var para = extracted[0];
            Assert.Contains(para.Runs, r => r.Text.Contains("bold") && r.Bold && !r.Underline);
            Assert.Contains(para.Runs, r => r.Text.Contains("plain") && !r.Bold);

            var bullet = extracted[1];
            Assert.Contains(bullet.Runs, r => r.Text.Contains("under") && r.Underline);
            // 마커 문자(•)가 run 텍스트에 남으면 안 됨
            Assert.DoesNotContain(bullet.Runs, r => r.Text.Contains('•'));
        });
    }

    [Fact]
    public void AdjacentSameFormatRuns_AreMerged()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("하나"));
            p.Inlines.Add(new Run("둘"));
            var bold = new Run("셋") { FontWeight = FontWeights.Bold };
            p.Inlines.Add(bold);
            doc.Blocks.Add(p);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Single(lines);
            Assert.Equal(2, lines[0].Runs.Count);
            Assert.Equal(new NoteRun("하나둘", false, false), lines[0].Runs[0]);
            Assert.Equal(new NoteRun("셋", true, false), lines[0].Runs[1]);
        });
    }
}
```

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter NoteLineExtractorStaTests`
Expected: 컴파일 에러 — `NoteLineExtractor` 없음

- [ ] **Step 3: 구현**

`Windows/NoteLineExtractor.cs` 전체 내용:

```csharp
using System.Windows;
using System.Windows.Documents;
using Noticker.Sync;

namespace Noticker.Windows;

// FlowDocument → NoteLine — SaveBodyContent의 run 추출 (RtfComposer.Compose의 역방향).
// UI 스레드 전용 (FlowDocument는 STA 필수) — STA 테스트로 검증.
// 마커 제거 규칙은 SaveBodyContent의 plain(Body) 경로와 정확히 대칭이어야 한다
public static class NoteLineExtractor
{
    public static List<NoteLine> Extract(FlowDocument doc)
    {
        var lines = new List<NoteLine>();
        foreach (var block in doc.Blocks)
        {
            if (block is Paragraph para)
            {
                lines.Add(new NoteLine(NoteLineKind.Paragraph, ExtractRuns(para, listKind: null)));
            }
            else if (block is System.Windows.Documents.List list)
            {
                bool numbered = list.MarkerStyle == TextMarkerStyle.Decimal;
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        if (inner is Paragraph innerPara)
                        {
                            var kind = numbered ? NoteLineKind.Number : NoteLineKind.Bullet;
                            lines.Add(new NoteLine(kind, ExtractRuns(innerPara, kind)));
                        }
            }
        }
        return lines;
    }

    private static IReadOnlyList<NoteRun> ExtractRuns(Paragraph para, NoteLineKind? listKind)
    {
        var raw = new List<NoteRun>();
        CollectRuns(para.Inlines, raw);

        // WPF RTF 왕복이 리스트 마커를 첫 run 텍스트에 주입함 — plain(Body) 경로의
        // SaveBodyContent와 같은 규칙으로 제거
        if (listKind is not null && raw.Count > 0)
        {
            var first = raw[0].Text;
            var stripped = first;
            if (listKind == NoteLineKind.Bullet && first.Length > 0 && first[0] == '•')
                stripped = first[1..].TrimStart('\t', ' ');
            else if (listKind == NoteLineKind.Number)
            {
                var m = System.Text.RegularExpressions.Regex.Match(first, @"^\d+[.)]\s");
                if (m.Success) stripped = first[m.Length..];
            }
            if (stripped != first)
                raw[0] = raw[0] with { Text = stripped };
        }

        // 빈 run 제거 + 동일 서식 인접 run 병합 — RTF 왕복이 잘게 쪼갠 run 정리
        var merged = new List<NoteRun>();
        foreach (var r in raw)
        {
            if (r.Text.Length == 0) continue;
            if (merged.Count > 0 && merged[^1].Bold == r.Bold && merged[^1].Underline == r.Underline)
                merged[^1] = merged[^1] with { Text = merged[^1].Text + r.Text };
            else
                merged.Add(r);
        }
        return merged;
    }

    private static void CollectRuns(InlineCollection inlines, List<NoteRun> runs)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    runs.Add(new NoteRun(
                        run.Text,
                        run.FontWeight == FontWeights.Bold,   // TextElement DP 상속 — Span 굵게도 잡힘
                        run.TextDecorations is { Count: > 0 } td &&
                            td.Any(d => d.Location == TextDecorationLocation.Underline)));
                    break;
                case Span span:
                    CollectRuns(span.Inlines, runs);
                    break;
                // LineBreak 등 기타 Inline은 무시 — Noticker 본문은 줄 단위 Paragraph라
                // soft break는 입력 경로상 생기지 않는다 (pull도 '\n'을 공백으로 평탄화)
            }
        }
    }
}
```

`Windows/StickerWindow.xaml.cs`의 `SaveBodyContent` 끝, `_sticker.Body = string.Join(...)` 줄 다음에 추가:

```csharp
        // 같은 문서에서 run 단위 서식도 추출 — push가 굵게/밑줄을 annotation으로 보내도록
        _sticker.BodyRuns = NoteLineSerializer.Serialize(NoteLineExtractor.Extract(BodyBox.Document));
```

(StickerWindow.xaml.cs 상단에 `using Noticker.Sync;`가 이미 있는지 확인 — 없으면 추가.)

- [ ] **Step 4: 테스트 통과 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (232개 — 229 + 3)

- [ ] **Step 5: Commit**

```bash
git add Windows/NoteLineExtractor.cs Windows/StickerWindow.xaml.cs Noticker.Tests/NoteLineExtractorStaTests.cs
git commit -m "feat: extract bold/underline runs in SaveBodyContent for formatted push"
```

---

### Task 5: pull/가져오기 BodyRuns 저장 + 경고 축소

**Files:**
- Modify: `Sync/NotionBlockConverter.cs:45-76` (HasAnnotations)
- Modify: `Sync/PullService.cs:180-186` (Apply)
- Modify: `Windows/StickerWindow.xaml.cs:283` (ApplyPulledContent)
- Modify: `Windows/NotionImportWindow.xaml.cs:129-148`
- Modify: `App.xaml.cs:259-284` (CreateImportedSticker)
- Test: `Noticker.Tests/NotionBlockConverterTests.cs`

- [ ] **Step 1: 실패하는 테스트 갱신**

`Noticker.Tests/NotionBlockConverterTests.cs`에서 기존 `HasAnnotations` 관련 테스트를 찾아 **삭제**하고, 같은 자리에 새 의미의 테스트로 교체:

```csharp
    // ── HasUnsupportedAnnotations ──────────────────────────────────────────────
    // bold/underline은 push가 annotation으로 왕복하므로 더 이상 경고 대상이 아니다

    private static string AnnotatedBlock(string flag) =>
        $$$"""
        [{"type":"paragraph","has_children":false,
          "paragraph":{"rich_text":[{"plain_text":"x","annotations":{"{{{flag}}}":true}}]}}]
        """;

    [Theory]
    [InlineData("bold")]
    [InlineData("underline")]
    public void HasUnsupportedAnnotations_BoldUnderline_False(string flag)
    {
        Assert.False(NotionBlockConverter.HasUnsupportedAnnotations(Parse(AnnotatedBlock(flag))));
    }

    [Theory]
    [InlineData("italic")]
    [InlineData("strikethrough")]
    [InlineData("code")]
    public void HasUnsupportedAnnotations_OtherFlags_True(string flag)
    {
        Assert.True(NotionBlockConverter.HasUnsupportedAnnotations(Parse(AnnotatedBlock(flag))));
    }

    [Fact]
    public void HasUnsupportedAnnotations_NonDefaultColor_True()
    {
        var blocks = Parse("""
            [{"type":"paragraph","has_children":false,
              "paragraph":{"rich_text":[{"plain_text":"x","annotations":{"color":"red"}}]}}]
            """);
        Assert.True(NotionBlockConverter.HasUnsupportedAnnotations(blocks));
    }

    [Fact]
    public void HasUnsupportedAnnotations_NoAnnotations_False()
    {
        var blocks = Parse("""
            [{"type":"paragraph","has_children":false,
              "paragraph":{"rich_text":[{"plain_text":"x"}]}}]
            """);
        Assert.False(NotionBlockConverter.HasUnsupportedAnnotations(blocks));
    }
```

(이 파일의 기존 `Parse` 헬퍼 재사용. 기존 HasAnnotations 테스트 수가 다르면 삭제/교체 후 전체 수가 달라질 수 있음 — Step 4에서 실제 수를 보고.)

- [ ] **Step 2: 테스트 실패 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj --filter NotionBlockConverterTests`
Expected: 컴파일 에러 — `HasUnsupportedAnnotations` 없음

- [ ] **Step 3: 구현**

1. `Sync/NotionBlockConverter.cs` — `HasAnnotations`를 다음으로 교체 (이름·주석·flags 변경):

```csharp
    // 가져오기 경고용 — 스티커가 왕복하지 못하는 서식(기울임/취소선/코드/색)이 있는가.
    // bold/underline은 push가 annotation으로 보존하므로 경고 대상이 아니다
    public static bool HasUnsupportedAnnotations(JsonElement blocksArray)
    {
        if (blocksArray.ValueKind != JsonValueKind.Array) return false;

        foreach (var block in blocksArray.EnumerateArray())
        {
            var type = TryGetProp(block, "type", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            if (type is null || !TryGetProp(block, type, out var payload) ||
                !TryGetProp(payload, "rich_text", out var richText) ||
                richText.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var rt in richText.EnumerateArray())
            {
                if (!TryGetProp(rt, "annotations", out var ann)) continue;
                foreach (var flag in new[] { "italic", "strikethrough", "code" })
                {
                    if (TryGetProp(ann, flag, out var v) && v.ValueKind == JsonValueKind.True)
                        return true;
                }
                if (TryGetProp(ann, "color", out var c) && c.ValueKind == JsonValueKind.String &&
                    c.GetString() is string color && color != "default")
                    return true;
            }
        }
        return false;
    }
```

2. `Windows/NotionImportWindow.xaml.cs` — `else if` 분기와 본문 변환부 교체:

```csharp
        else if (NotionBlockConverter.HasUnsupportedAnnotations(blocks))
        {
            // 블록 종류는 지원 범위지만 왕복 불가 서식이 있음 — 굵게/밑줄은 이제
            // annotation push로 보존되므로 그 외 서식만 동의 게이트
            var result = MessageBox.Show(
                "이 페이지에는 스티커가 지원하지 않는 글자 서식(기울임/취소선/코드/색상)이 있습니다.\n" +
                "가져온 뒤 스티커를 수정하면 해당 서식이 사라질 수 있습니다. " +
                "(굵게/밑줄은 유지됩니다)\n\n계속할까요?",
                "서식 경고", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes) return;
        }
```

그리고 변환부의 `App.Current.CreateImportedSticker(...)` 호출을:

```csharp
        var lines = NotionBlockConverter.ToLines(blocks);
        var plain = NotionBlockConverter.ToPlainText(lines);
        var rtf = RtfComposer.Compose(lines);
        var runsJson = NoteLineSerializer.Serialize(lines);

        // RawTitle 사용 — DisplayTitle("(제목 없음)")을 저장하면 첫 push가 Notion 페이지
        // 제목을 placeholder로 바꿔버린다 (검증 리뷰 F4)
        App.Current.CreateImportedSticker(
            item.RawTitle, plain, rtf, runsJson, item.PageId, item.LastEditedTime, item.LastEditedById, pullDisabled);
```

3. `App.xaml.cs` — `CreateImportedSticker` 시그니처와 본문에 bodyRuns 추가:

```csharp
    public void CreateImportedSticker(string title, string plainBody, string bodyRtf, string bodyRuns,
        string pageId, string editTime, string editBy, bool pullDisabled)
```
와 Sticker 초기화에 `BodyRtf = bodyRtf,` 다음 줄로:
```csharp
            BodyRuns = bodyRuns,
```

4. `Windows/StickerWindow.xaml.cs` — `ApplyPulledContent` 시그니처/본문:

```csharp
    public void ApplyPulledContent(string title, string plainBody, string bodyRtf, string? bodyRuns)
```
본문에서 `_sticker.BodyRtf = bodyRtf;` 다음 줄로:
```csharp
            _sticker.BodyRuns = bodyRuns;
```

5. `Sync/PullService.cs` — Apply 분기에서 `var rtf = RtfComposer.Compose(lines, s.FontFamily);` 다음 줄에:

```csharp
                        var runsJson = NoteLineSerializer.Serialize(lines);
```
그리고 `win.ApplyPulledContent(p.Title, plain, rtf);` → `win.ApplyPulledContent(p.Title, plain, rtf, runsJson);`

- [ ] **Step 4: 빌드 + 전체 테스트**

Run: `taskkill //IM Noticker.exe //F 2>/dev/null; dotnet build Noticker.csproj`
Expected: Build succeeded

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS, 실패 0 (전체 수는 기존 HasAnnotations 테스트 교체분에 따라 232±α — 실제 수 보고)

- [ ] **Step 5: Commit**

```bash
git add Sync/NotionBlockConverter.cs Sync/PullService.cs Windows/StickerWindow.xaml.cs Windows/NotionImportWindow.xaml.cs App.xaml.cs Noticker.Tests/NotionBlockConverterTests.cs
git commit -m "feat: persist body_runs on pull/import, narrow import warning to unsupported formats"
```

---

### Task 6: 빈 메모 표시등 회색

**Files:**
- Modify: `Windows/StickerWindow.xaml.cs:171-186` (SyncDotColor/SyncTooltip)

- [ ] **Step 1: 구현** (표시 로직 — STA 창 필요라 단위 테스트 없음, Task 7 수동 QA로 검증)

`SyncDotColor`/`SyncTooltip`을 다음으로 교체:

```csharp
    // 빈 메모는 push 대상이 아니라(SyncQueue.ProcessAsync skip 조건과 동일 기준)
    // 'pending' 주황이 영원히 남는다 — 회색으로 사실을 표시 (D9)
    private bool IsEmptyUnsynced =>
        _sticker.NotionPageId is null &&
        string.IsNullOrEmpty(_sticker.Title) && string.IsNullOrEmpty(_sticker.Body);

    public Brush SyncDotColor => IsEmptyUnsynced
        ? new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF))
        : _sticker.SyncState switch
        {
            "synced"   => new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
            "failed"   => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            "conflict" => new SolidColorBrush(Color.FromRgb(0xFF, 0x8C, 0x00)),
            _          => new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B)),
        };

    public string SyncTooltip => IsEmptyUnsynced
        ? "빈 메모 — 동기화 대상 아님"
        : _sticker.SyncState switch
        {
            "synced"   => $"동기화됨 ({_sticker.LastSyncedAt?[..10] ?? ""})"
                          + (_sticker.PullDisabled ? "\nNotion 서식 미지원 — 가져오기 중단됨" : ""),
            "failed"   => "동기화 실패 (수동 Sync로 재시도)",
            "conflict" => "Notion과 충돌 — 수정하면 스티커 버전이 push됩니다",
            _          => "동기화 대기 중…",
        };
```

(주의: SaveContent → UpdateSyncIndicator 경로가 내용 입력 시 이미 재평가하므로 추가 배선 불필요.)

- [ ] **Step 2: 빌드 + 전체 테스트**

Run: `taskkill //IM Noticker.exe //F 2>/dev/null; dotnet build Noticker.csproj`
Expected: Build succeeded

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS (Task 5와 동일 수)

- [ ] **Step 3: Commit**

```bash
git add Windows/StickerWindow.xaml.cs
git commit -m "feat: gray sync dot for empty stickers that are not sync targets"
```

---

### Task 7: 통합 검증 + 수동 QA

- [ ] **Step 1: 전체 테스트 최종 확인**

Run: `dotnet test Noticker.Tests/Noticker.Tests.csproj`
Expected: PASS, 실패 0

- [ ] **Step 2: 앱 실행**

```bash
taskkill //IM Noticker.exe //F 2>/dev/null
dotnet build Noticker.csproj
start "" "bin/Debug/net8.0-windows/Noticker.exe"
```

- [ ] **Step 3: 사용자 수동 QA 체크리스트 전달** (사용자 직접 QA)

```
[push 서식]
1. 스티커에서 일부 텍스트 굵게 + 다른 부분 밑줄 → 1분 내 Notion에서 같은 서식 확인
2. Notion(폰/웹)에서 굵게 문구 수정 → 스티커 pull 반영 + 서식 유지 확인
3. pull 받은 직후 스티커에서 한 글자 수정 → push 후 Notion 굵게/밑줄 유지 (이전엔 벗겨짐)
4. 리스트(불릿/번호) 안의 굵게 → Notion 반영 확인
5. 가져오기: 굵게/밑줄만 있는 페이지 → 경고 없이 가져와짐 / 기울임 있는 페이지 → 경고 표시
6. 기존(이번 업데이트 전) 스티커 → 편집 없이도 push 정상 (plain 폴백), 편집 후부터 서식 push

[표시등]
7. 트레이 → 새 스티커: 좌상단 점이 회색 + 툴팁 "빈 메모 — 동기화 대상 아님"
8. 내용 입력 → 주황(대기 중) → 1분 내 초록(동기화됨) 전환
9. sync.log(%APPDATA%\Noticker\sync.log)에 body_runs 폴백 로그가 없는지 (깨진 JSON 없음)
```

- [ ] **Step 4: 사용자 확인 후 push** (확인 전 push 금지)

```bash
git push
```

---

## Self-Review 결과

- **스펙 커버리지**: NoteLineSerializer(§1 데이터) ✓ T1 / body_runs v5(§1 데이터) ✓ T2 /
  run 단위 블록 + annotations 생략 + 청크 + 폴백 + SyncLog(§1 push·오류) ✓ T3 /
  SaveBodyContent 추출·병합·마커 제거(§1 쓰기) ✓ T4 / pull·가져오기 쌍 저장 +
  경고 축소(§1 일관성·경고) ✓ T5 / 표시등 회색(§2) ✓ T6 / 수동 QA(§테스트 6) ✓ T7. 누락 없음.
- **플레이스홀더**: 없음 — 모든 코드 스텝 전체 코드 포함. (T2 Step 1과 T5 Step 1의
  "기존 픽스처/테스트에 맞춰 조정"은 대상 파일을 먼저 읽으라는 지시이며 코드는 완전함.)
- **타입 일관성**: `NoteLineSerializer.Serialize(IReadOnlyList<NoteLine>)`/`Deserialize(string?)`
  시그니처가 T3 `BuildBodyBlocks`·T4 SaveBodyContent·T5 PullService/Import 호출과 일치.
  `BuildAnnotatedBlocks(IReadOnlyList<NoteLine>)` 가 T3 테스트의 `NoteLine[]` 인자와 호환
  (배열은 IReadOnlyList 구현). `ApplyPulledContent` 4-인자/`CreateImportedSticker` 8-인자
  시그니처가 호출부와 일치. `NoteRun`/`NoteLine`/`NoteLineKind`는 기존 타입 재사용.
