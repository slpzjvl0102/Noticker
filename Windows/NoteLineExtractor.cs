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

        // plain(Body) 경로의 TrimEnd('\n')와 대칭 — 뒤쪽 빈 줄은 블록으로 가지 않는다.
        // 특히 빈 문서가 [빈 paragraph 1줄]로 추출되면 plain 경로(블록 0개)와 달리
        // Notion에 빈 블록이 생긴다 (Task 3 리뷰 발견)
        while (lines.Count > 0 && lines[^1].Runs.Count == 0)
            lines.RemoveAt(lines.Count - 1);
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

    private static void CollectRuns(InlineCollection inlines, List<NoteRun> runs,
        bool inheritedBold = false, bool inheritedUnderline = false)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    // FontWeight은 WPF DP 상속으로 부모 Span의 값이 run.FontWeight에 반영됨.
                    // TextDecorations는 DP 상속이 아니라 직접 탐색 + 부모에서 내려온 플래그로 OR.
                    var bold = inheritedBold || run.FontWeight == FontWeights.Bold;
                    var underline = inheritedUnderline ||
                        (run.TextDecorations is { Count: > 0 } td &&
                            td.Any(d => d.Location == TextDecorationLocation.Underline));
                    runs.Add(new NoteRun(run.Text, bold, underline));
                    break;
                case Span span:
                    // Span 자체의 서식을 자식에게 누적해서 내려준다
                    var spanBold = inheritedBold || span.FontWeight == FontWeights.Bold;
                    var spanUnderline = inheritedUnderline ||
                        (span.TextDecorations is { Count: > 0 } std &&
                            std.Any(d => d.Location == TextDecorationLocation.Underline));
                    CollectRuns(span.Inlines, runs, spanBold, spanUnderline);
                    break;
                // LineBreak 등 기타 Inline은 무시 — Noticker 본문은 줄 단위 Paragraph라
                // soft break는 입력 경로상 생기지 않는다 (pull도 '\n'을 공백으로 평탄화)
            }
        }
    }
}
