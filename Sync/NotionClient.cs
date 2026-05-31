using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _settings.NotionToken);
    }

    // Creates a new Notion page with title, category, and body as page content blocks.
    public async Task<string> CreatePageAsync(Sticker s, CancellationToken ct)
    {
        SetAuth();

        var props = BuildProperties(s);
        var children = BuildParagraphBlocks(s.Body);

        var payload = new { parent = new { database_id = _settings.TargetDbId }, properties = props, children };
        var response = await PostAsync("/pages", payload, ct);

        var id = response.RootElement.GetProperty("id").GetString()
            ?? throw new InvalidOperationException("Notion response missing 'id' field.");
        return id;
    }

    // Updates title/category and replaces all page body blocks with current body text.
    public async Task UpdatePageAsync(Sticker s, CancellationToken ct)
    {
        SetAuth();

        // 1. Update title and category properties
        await PatchAsync($"/pages/{s.NotionPageId}", new { properties = BuildProperties(s) }, ct);

        // 2. Delete all existing body blocks
        var blockIds = await GetChildBlockIdsAsync(s.NotionPageId!, ct);
        foreach (var blockId in blockIds)
            await DeleteBlockAsync(blockId, ct);

        // 3. Append new paragraph blocks if body is non-empty
        var blocks = BuildParagraphBlocks(s.Body);
        if (blocks.Length > 0)
            await PatchAsync($"/blocks/{s.NotionPageId}/children", new { children = blocks }, ct);
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

    // Tests DB connectivity. Returns null on success, error message on failure.
    public async Task<string?> TestConnectionAsync(CancellationToken ct)
    {
        SetAuth();
        try
        {
            await GetAsync($"/databases/{_settings.TargetDbId}", ct);
            return null;
        }
        catch (NotionUnauthorizedException)
        {
            return "Token이 유효하지 않습니다 (401).";
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
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
