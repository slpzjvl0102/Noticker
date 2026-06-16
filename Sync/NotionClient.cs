using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Noticker.Infrastructure;
using Noticker.Models;

namespace Noticker.Sync;

public class NotionPageNotFoundException : Exception
{
    public NotionPageNotFoundException(string pageId) : base($"Notion page {pageId} not found.") { }
}

public class NotionUnauthorizedException : Exception
{
    public NotionUnauthorizedException() : base("Notion API returned 401. Check your token.") { }
}

public class NotionRateLimitException : Exception
{
    public NotionRateLimitException() : base("Notion API rate limit hit (429).") { }
}

public class NotionClient
{
    private const string BaseUrl = "https://api.notion.com/v1";
    private const string NotionVersion = "2022-06-28";
    private const int ChunkSize = 2000;

    private readonly HttpClient _http;
    private readonly AppSettings _settings;

    public NotionClient(AppSettings settings)
    {
        _settings = settings;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Add("Notion-Version", NotionVersion);
    }

    private void SetAuth()
    {
        // 토큰이 안 바뀌었으면 쓰지 않는다 — 온보딩 프로브의 per-request 전송이
        // DefaultRequestHeaders를 읽는 동안 동기화 루프가 같은 컬렉션을 변이하는
        // 잔여 경쟁을 닫는다 (토큰 변경은 프로브가 안 도는 온보딩 완료 시점뿐)
        if (_http.DefaultRequestHeaders.Authorization?.Parameter == _settings.NotionToken)
            return;
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.NotionToken);
    }

    // Creates an EMPTY Notion page (properties only) and returns its id.
    // 본문은 호출자가 id를 영속한 뒤 SyncBodyAsync로 채운다 — 생성과 본문을 분리해야
    // 본문 append 실패 시 재시도가 UpdatePage 경로를 타 빈 페이지가 중복 생성되지 않는다.
    public async Task<string> CreatePageAsync(Sticker s, CancellationToken ct)
    {
        SetAuth();
        var payload = new { parent = new { database_id = _settings.TargetDbId }, properties = BuildProperties(s) };
        var response = await PostAsync("/pages", payload, ct);
        return response.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Notion response missing 'id' field.");
    }

    // Updates title/category and replaces all page body blocks with current body (중첩 포함).
    public async Task UpdatePageAsync(Sticker s, CancellationToken ct)
    {
        SetAuth();
        // 1. 제목/카테고리 속성 갱신
        await PatchAsync($"/pages/{s.NotionPageId}", new { properties = BuildProperties(s) }, ct);
        // 2. 기존 본문 블록 전체 삭제
        var blockIds = await GetChildBlockIdsAsync(s.NotionPageId!, ct);
        foreach (var blockId in blockIds)
            await DeleteBlockAsync(blockId, ct);
        // 3. 현재 본문을 중첩 트리로 재구성해 append
        await SyncBodyAsync(s, ct);
    }

    // 본문(BodyRuns/평면 폴백)을 중첩 트리로 만들어 페이지 아래에 재귀 append.
    public async Task SyncBodyAsync(Sticker s, CancellationToken ct)
    {
        SetAuth();
        await AppendForestAsync(s.NotionPageId!, BuildBodyForest(s), ct);
    }

    // Fetches options for the configured Category property (select or multi_select).
    // Returns (Name, NotionColor) pairs — color is one of: default, gray, brown, orange, yellow,
    // green, blue, purple, pink, red.
    public async Task<List<(string Name, string Color)>> FetchCategoryOptionsAsync(CancellationToken ct)
    {
        SetAuth();
        var response = await GetAsync($"/databases/{_settings.TargetDbId}", ct);
        var props = response.RootElement.GetProperty("properties");
        var propName = _settings.CategoryPropertyName;

        if (!props.TryGetProperty(propName, out var prop))
        {
            var available = string.Join(", ", props.EnumerateObject().Select(p => p.Name));
            throw new InvalidOperationException(
                $"프로퍼티 '{propName}'을(를) 찾을 수 없습니다.\n" +
                $"DB에 있는 프로퍼티: {available}");
        }

        var options = new List<(string Name, string Color)>();
        foreach (var typeName in new[] { "select", "multi_select" })
        {
            if (prop.TryGetProperty(typeName, out var sel) &&
                sel.TryGetProperty("options", out var opts))
            {
                foreach (var opt in opts.EnumerateArray())
                {
                    if (opt.TryGetProperty("name", out var name))
                    {
                        var color = opt.TryGetProperty("color", out var c)
                            ? (c.GetString() ?? "default") : "default";
                        options.Add((name.GetString() ?? "", color));
                    }
                }
                break;
            }
        }
        return options;
    }

    // Fetches a page's (last_edited_time, last_edited_by.id) pair for overwrite protection
    // and conflict detection. Returns null if the page no longer exists (404).
    public async Task<(string LastEditedTime, string LastEditedById)?> GetPageMetaAsync(string pageId, CancellationToken ct)
    {
        SetAuth();
        var doc = await GetOrNullOn404Async($"/pages/{pageId}", ct);
        if (doc is null) return null;

        var root = doc.RootElement;
        var time = root.GetProperty("last_edited_time").GetString() ?? "";
        var by = root.GetProperty("last_edited_by").GetProperty("id").GetString() ?? "";
        return (time, by);
    }

    // Returns the integration's bot user id (for echo filtering), or null on any failure —
    // caller treats null as "protection not yet active".
    public async Task<string?> GetBotUserIdAsync(CancellationToken ct)
    {
        SetAuth();
        try
        {
            var doc = await GetAsync("/users/me", ct);
            return doc.RootElement.GetProperty("id").GetString();
        }
        catch
        {
            return null;
        }
    }

    // Queries the target DB for pages sorted by last_edited_time.
    // cursorIso null = 필터 없음 + 내림차순 (가져오기 목록 — 300건 캡이 최신부터 남도록);
    // cursorIso 지정 = on_or_after 필터 + 오름차순 (폴링 — 커서 전진 보장).
    public async Task<List<(string PageId, string LastEditedTime, string LastEditedById, string Title)>> QueryUpdatedPagesAsync(
        string? cursorIso, CancellationToken ct)
    {
        SetAuth();

        var results = new List<(string PageId, string LastEditedTime, string LastEditedById, string Title)>();
        string? startCursor = null;

        // 페이지네이션 하드 캡 3페이지(300건) — 오프라인 복귀 burst 방어.
        // 초과분은 폴링 커서(on_or_after + 오름차순 정렬)가 다음 사이클에 이어받는다.
        for (int page = 0; page < 3; page++)
        {
            var body = new Dictionary<string, object>
            {
                ["page_size"] = 100,
                ["sorts"] = new[] { new { timestamp = "last_edited_time",
                    direction = cursorIso is null ? "descending" : "ascending" } }
            };
            if (cursorIso is not null)
                body["filter"] = new { timestamp = "last_edited_time", last_edited_time = new { on_or_after = cursorIso } };
            if (startCursor is not null)
                body["start_cursor"] = startCursor;

            var doc = await PostAsync($"/databases/{_settings.TargetDbId}/query", body, ct);
            var root = doc.RootElement;

            foreach (var p in root.GetProperty("results").EnumerateArray())
            {
                var id = p.GetProperty("id").GetString() ?? "";
                var time = p.GetProperty("last_edited_time").GetString() ?? "";
                var by = p.GetProperty("last_edited_by").GetProperty("id").GetString() ?? "";
                results.Add((id, time, by, ExtractTitle(p)));
            }

            if (!root.GetProperty("has_more").GetBoolean()) break;
            startCursor = root.GetProperty("next_cursor").GetString();
        }

        return results;
    }

    // Returns the page's child block array (cloned so it outlives the response document)
    // plus has_more, or null if the page no longer exists (404).
    // 단일 페이지(100블록)만 읽는다. HasMore=true는 호출자가 "범위 밖"으로 취급해야 한다 —
    // 잘린 본문을 가져온 뒤 push하면 기존 삭제 경로(전체 페이지네이션)가 101번째 이후
    // 블록을 Notion에서 파괴하는 비대칭이 생기기 때문 (검증 리뷰 발견).
    public async Task<(JsonElement Blocks, bool HasMore)?> GetPageBlocksAsync(string pageId, CancellationToken ct)
    {
        SetAuth();
        var doc = await GetOrNullOn404Async($"/blocks/{pageId}/children?page_size=100", ct);
        if (doc is null) return null;
        return (doc.RootElement.GetProperty("results").Clone(),
                doc.RootElement.GetProperty("has_more").GetBoolean());
    }

    // 재귀 pull이 누적할 수 있는 최대 블록 수 — 초과하면 pull 중단(UI 루프 점유 방지).
    private const int MaxPulledBlocks = 400;

    // 페이지 본문을 재귀로 전부 가져온다 — 리스트 항목의 has_children를 따라 내려가며 depth를 매기고,
    // 각 레벨은 cursor로 페이지네이션. paginated이므로 100블록 초과여도 잘림이 없다 → HasMore는 더 이상
    // pull 중단 사유가 아니다. 미지원 타입/리스트 외 중첩(toggle 등)을 만나면 Supported=false로 보고.
    // 404면 null. UI 스레드와 무관(순수 HTTP).
    public async Task<(List<NotionBlockConverter.PulledBlock> Blocks, bool Supported, string? UnsupportedType)?>
        GetPageBlockTreeAsync(string pageId, CancellationToken ct)
    {
        SetAuth();
        var probe = await GetOrNullOn404Async($"/blocks/{pageId}/children?page_size=100", ct);
        if (probe is null) return null;

        var acc = new List<NotionBlockConverter.PulledBlock>();
        var (ok, unsupported) = await CollectLevelAsync(pageId, 0, acc, firstDoc: probe, ct);
        return (acc, ok, unsupported);
    }

    // 한 블록의 자식 레벨을 전위순회로 acc에 누적. 리스트 항목의 has_children만 재귀(그 외 중첩은 미지원).
    // firstDoc: depth 0 첫 페이지는 404 프로브 결과를 재사용(요청 1회 절약).
    private async Task<(bool ok, string? unsupported)> CollectLevelAsync(
        string blockId, int depth, List<NotionBlockConverter.PulledBlock> acc,
        JsonDocument? firstDoc, CancellationToken ct)
    {
        string? cursor = null;
        var doc = firstDoc;
        while (true)
        {
            if (doc is null)
            {
                await Task.Delay(350, ct);   // rate limit 페이싱
                var path = $"/blocks/{blockId}/children?page_size=100" +
                           (cursor is not null ? $"&start_cursor={cursor}" : "");
                doc = await GetAsync(path, ct);
            }
            var root = doc.RootElement;
            foreach (var block in root.GetProperty("results").EnumerateArray())
            {
                var type = block.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : null;
                bool isList = type is "bulleted_list_item" or "numbered_list_item";
                bool hasChildren = block.TryGetProperty("has_children", out var hc) &&
                                   hc.ValueKind == JsonValueKind.True;

                if (type is not ("paragraph" or "bulleted_list_item" or "numbered_list_item"))
                    return (false, type ?? "unknown");
                if (hasChildren && !isList)
                    return (false, "nested " + type);

                acc.Add(new NotionBlockConverter.PulledBlock(block.Clone(), depth));
                // 총 블록 상한 — 병적으로 큰 페이지가 재귀 fetch로 UI 루프를 수 분 점유하는 것 방지.
                // 초과 시 pull 중단(옛 100블록 천장의 대체). 초과 본문은 push 쪽도 안전.
                if (acc.Count > MaxPulledBlocks)
                    return (false, $"too many blocks (>{MaxPulledBlocks})");

                if (hasChildren && isList)
                {
                    var childId = block.GetProperty("id").GetString()!;
                    var (cok, cuns) = await CollectLevelAsync(childId, depth + 1, acc, firstDoc: null, ct);
                    if (!cok) return (false, cuns);
                }
            }
            if (!root.GetProperty("has_more").GetBoolean()) break;
            cursor = root.GetProperty("next_cursor").GetString();
            if (cursor is null) break;
            doc = null;
        }
        return (true, null);
    }

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
            if (cursor is null) break;   // 방어: has_more=true + null cursor → 무한 루프 차단
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

    // Finds the title-type property on a page object and concatenates its plain_text runs.
    // Returns "" if the page is untitled.
    private static string ExtractTitle(JsonElement page)
    {
        if (!page.TryGetProperty("properties", out var props)) return "";

        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Value.TryGetProperty("type", out var type) && type.GetString() == "title" &&
                prop.Value.TryGetProperty("title", out var runs))
            {
                var sb = new StringBuilder();
                foreach (var run in runs.EnumerateArray())
                {
                    if (run.TryGetProperty("plain_text", out var pt))
                        sb.Append(pt.GetString());
                }
                return sb.ToString();
            }
        }
        return "";
    }

    // Returns IDs of all direct child blocks of a page (for clearing before re-write).
    private async Task<List<string>> GetChildBlockIdsAsync(string pageId, CancellationToken ct)
    {
        var ids = new List<string>();
        string? cursor = null;

        while (true)
        {
            var path = $"/blocks/{pageId}/children?page_size=100" +
                       (cursor is not null ? $"&start_cursor={cursor}" : "");
            var doc = await GetAsync(path, ct);
            var root = doc.RootElement;

            foreach (var block in root.GetProperty("results").EnumerateArray())
                ids.Add(block.GetProperty("id").GetString()!);

            if (!root.GetProperty("has_more").GetBoolean()) break;
            cursor = root.GetProperty("next_cursor").GetString();
        }

        return ids;
    }

    private async Task DeleteBlockAsync(string blockId, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, BaseUrl + $"/blocks/{blockId}");
        var response = await _http.SendAsync(request, ct);
        // 404 means block already gone — treat as success
        if (response.StatusCode != HttpStatusCode.NotFound)
            await HandleResponseAsync(response, pageId: null);
    }

    private Dictionary<string, object> BuildProperties(Sticker s)
    {
        var props = new Dictionary<string, object>
        {
            ["title"] = new
            {
                title = new[] { new { text = new { content = string.IsNullOrEmpty(s.Title) ? "(untitled)" : s.Title } } }
            }
        };

        if (s.Category is not null)
            props[_settings.CategoryPropertyName] = new { select = new { name = s.Category } };

        return props;
    }

    // 본문(BodyRuns/평면 폴백)을 depth 태그 블록 트리(forest)로 — push가 중첩 List를 Notion
    // 네이티브 중첩으로 보내도록. BodyRuns 없거나 깨지면 plain 평면 폴백.
    private static List<BlockNode> BuildBodyForest(Sticker s)
    {
        var lines = NoteLineSerializer.Deserialize(s.BodyRuns);
        if (lines is not null) return BuildForest(lines);
        if (!string.IsNullOrEmpty(s.BodyRuns))
            SyncLog.Write($"push: body_runs 역직렬화 실패 — plain 폴백 (sticker={s.Id[..Math.Min(8, s.Id.Length)]})");
        return BuildParagraphBlocks(s.Body).Select(b => new BlockNode(b)).ToList();   // 평면(depth 0)
    }

    // 블록 트리 노드 — Notion 블록(children 키 없음) + 자식 노드.
    public sealed class BlockNode(object block)
    {
        public object Block { get; } = block;
        public List<BlockNode> Children { get; } = [];
    }

    // depth 태그 NoteLine 시퀀스 → 블록 forest. depth d 줄은 직전 depth d-1 노드의 자식.
    // 깊이 점프(d > 스택 깊이)는 가능한 가장 깊은 곳에 붙여 방어.
    public static List<BlockNode> BuildForest(IReadOnlyList<NoteLine> lines)
    {
        var roots = new List<BlockNode>();
        var stack = new List<BlockNode>();   // stack[i] = 현재 열린 depth-i 노드
        foreach (var line in lines)
        {
            int d = line.Depth < 0 ? 0 : line.Depth;
            if (d > stack.Count) d = stack.Count;
            var node = new BlockNode(MakeBlock(line));
            if (d == 0) roots.Add(node);
            else stack[d - 1].Children.Add(node);
            if (stack.Count > d) stack.RemoveRange(d, stack.Count - d);
            stack.Add(node);   // stack[d] = node
        }
        return roots;
    }

    // forest를 부모 아래에 한 레벨씩 append하고, 응답의 block id로 각 노드의 자식을 재귀 append.
    // Notion은 요청당 children 100개·중첩 2레벨 한도라, 한 레벨씩 올리면 임의 깊이를 안전히 처리하고
    // 각 블록 id를 응답에서 받아 자식의 부모로 쓸 수 있다.
    // 페이싱(Task.Delay)을 두지 않는다 — UpdatePage의 삭제+다중 append가 페이싱으로 수 초 걸리면
    // 그동안 사람이 Notion에서 끼어든 편집을 봇의 마지막 쓰기가 조용히 덮는 TOCTOU 창이 넓어진다.
    // 빠르게 몰아쳐 창을 단일요청 수준으로 좁히고, 429는 SyncQueue가 백오프·재시도로 처리한다.
    private async Task AppendForestAsync(string parentId, IReadOnlyList<BlockNode> nodes, CancellationToken ct)
    {
        if (nodes.Count == 0) return;

        var ids = new List<string>(nodes.Count);
        for (int i = 0; i < nodes.Count; i += 100)
        {
            var chunk = nodes.Skip(i).Take(100).Select(n => n.Block).ToArray();
            var resp = await PatchAsync($"/blocks/{parentId}/children", new { children = chunk }, ct);
            foreach (var b in resp.RootElement.GetProperty("results").EnumerateArray())
                ids.Add(b.GetProperty("id").GetString()!);
        }

        for (int i = 0; i < nodes.Count; i++)
            if (nodes[i].Children.Count > 0)
                await AppendForestAsync(ids[i], nodes[i].Children, ct);
    }

    // run 단위 rich_text 블록 — annotations는 굵게/밑줄 중 하나라도 있을 때만 포함
    // (Notion 기본값이 모두 false라 생략 가능, payload 절약). 2000자 청크는 run 단위 분할.
    public static object[] BuildAnnotatedBlocks(IReadOnlyList<NoteLine> lines)
        => lines.Select(MakeBlock).ToArray();

    // 단일 NoteLine → Notion 블록(children 키 없음 — 중첩은 BuildForest/AppendForest가 처리).
    private static object MakeBlock(NoteLine line)
    {
        var richText = line.Runs
            .Where(r => !string.IsNullOrEmpty(r.Text))
            .SelectMany(r => SplitIntoChunks(r.Text, ChunkSize)
                .Select(c => MakeRichText(c, r.Bold, r.Underline)))
            .ToArray();

        // Notion 한도: 블록당 rich_text 100개 — 초과 시 그 줄만 평문 강등 (400 방지)
        if (richText.Length > 100)
        {
            var flat = string.Concat(line.Runs.Select(r => r.Text));
            richText = SplitIntoChunks(flat, ChunkSize)
                .Select(c => MakeRichText(c, bold: false, underline: false))
                .ToArray();
        }

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
    }

    private static object MakeRichText(string content, bool bold, bool underline) =>
        bold || underline
            ? new { text = new { content }, annotations = new { bold, underline } }
            : new { text = new { content } };

    // Converts body text to Notion block array.
    // Lines starting with "• " become bulleted_list_item blocks.
    // Lines matching "N. " become numbered_list_item blocks.
    // All other lines become paragraph blocks.
    private static object[] BuildParagraphBlocks(string body)
    {
        if (string.IsNullOrEmpty(body)) return [];

        return body.Split('\n').Select(line =>
        {
            if (line.StartsWith("• "))
            {
                var content = line[2..];
                return (object)new
                {
                    type = "bulleted_list_item",
                    bulleted_list_item = new
                    {
                        rich_text = string.IsNullOrEmpty(content)
                            ? Array.Empty<object>()
                            : (object)SplitIntoChunks(content, ChunkSize)
                                .Select(c => new { text = new { content = c } })
                                .ToArray()
                    }
                };
            }

            var m = System.Text.RegularExpressions.Regex.Match(line, @"^\d+\. (.*)$");
            if (m.Success)
            {
                var content = m.Groups[1].Value;
                return (object)new
                {
                    type = "numbered_list_item",
                    numbered_list_item = new
                    {
                        rich_text = string.IsNullOrEmpty(content)
                            ? Array.Empty<object>()
                            : (object)SplitIntoChunks(content, ChunkSize)
                                .Select(c => new { text = new { content = c } })
                                .ToArray()
                    }
                };
            }

            return (object)new
            {
                type = "paragraph",
                paragraph = new
                {
                    rich_text = string.IsNullOrEmpty(line)
                        ? Array.Empty<object>()
                        : (object)SplitIntoChunks(line, ChunkSize)
                            .Select(c => new { text = new { content = c } })
                            .ToArray()
                }
            };
        }).ToArray();
    }

    private async Task<JsonDocument> PostAsync(string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await _http.PostAsync(BaseUrl + path, content, ct);
        return await HandleResponseAsync(response, pageId: null);
    }

    private async Task<JsonDocument> PatchAsync(string path, object body, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(body);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), BaseUrl + path)
        {
            Content = content
        };
        var response = await _http.SendAsync(request, ct);
        var pageId = path.StartsWith("/pages/") ? path["/pages/".Length..] : null;
        return await HandleResponseAsync(response, pageId);
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var response = await _http.GetAsync(BaseUrl + path, ct);
        return await HandleResponseAsync(response, pageId: null);
    }

    // GET that yields null on 404 instead of throwing — pull/protection probes treat
    // a deleted page as absence, not an error.
    private async Task<JsonDocument?> GetOrNullOn404Async(string path, CancellationToken ct)
    {
        var response = await _http.GetAsync(BaseUrl + path, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        return await HandleResponseAsync(response, pageId: null);
    }

    private static async Task<JsonDocument> HandleResponseAsync(HttpResponseMessage response, string? pageId)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new NotionUnauthorizedException();
        if (response.StatusCode == HttpStatusCode.NotFound && pageId is not null)
            throw new NotionPageNotFoundException(pageId);
        if (response.StatusCode == (HttpStatusCode)429)
            throw new NotionRateLimitException();

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            string? notionMsg = null;
            try
            {
                var errDoc = JsonDocument.Parse(body);
                notionMsg = errDoc.RootElement.TryGetProperty("message", out var m)
                    ? m.GetString() : null;
            }
            catch { /* not JSON */ }
            throw new HttpRequestException($"Notion API {(int)response.StatusCode}: {notionMsg ?? body}");
        }

        var json = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(json);
    }

    // Splits text into chunks of at most maxLen Unicode code points (StringInfo-safe).
    public static List<string> SplitIntoChunks(string text, int maxLen)
    {
        if (string.IsNullOrEmpty(text)) return [text];

        var chunks = new List<string>();
        var sb = new StringBuilder();
        int count = 0;

        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var element = enumerator.GetTextElement();
            sb.Append(element);
            count++;
            if (count == maxLen)
            {
                chunks.Add(sb.ToString());
                sb.Clear();
                count = 0;
            }
        }

        if (sb.Length > 0) chunks.Add(sb.ToString());
        return chunks;
    }
}
