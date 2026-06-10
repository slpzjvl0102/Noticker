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
