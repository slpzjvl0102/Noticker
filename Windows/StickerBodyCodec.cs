using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using DataFormats = System.Windows.DataFormats;
using DocList = System.Windows.Documents.List;
using Noticker.Sync;

namespace Noticker.Windows;

// 스티커 본문 FlowDocument ↔ 저장 형식(RTF·plain Body·BodyRuns) 변환 — StickerWindow에서 분리.
// 동작 변경 없음. UI 스레드 전용(FlowDocument는 STA). IME 조합 보호는 호출자 책임:
// Serialize는 문서 전체를 읽으므로 조합 중 호출하면 한글 복합 음절이 깨진다.
public static class StickerBodyCodec
{
    private static readonly Regex _leakedNumberMarker = new(@"^\d+[.)]\s", RegexOptions.Compiled);

    public readonly record struct SerializedBody(string Rtf, string Plain, string Runs);

    public static string DocumentText(FlowDocument doc) =>
        new TextRange(doc.ContentStart, doc.ContentEnd).Text;

    // 문서 → RTF(Latin1) + plain Body + BodyRuns(run 단위 서식·depth). push가 굵게/밑줄·중첩을 보낸다.
    public static SerializedBody Serialize(FlowDocument doc)
    {
        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var rtfMs = new MemoryStream();
        range.Save(rtfMs, DataFormats.Rtf);
        var rtf = Encoding.Latin1.GetString(rtfMs.ToArray());

        var lines = new List<string>();
        EmitPlainBlocks(doc.Blocks, lines);
        var plain = string.Join("\n", lines).TrimEnd('\n');

        var runs = NoteLineSerializer.Serialize(NoteLineExtractor.Extract(doc));
        return new SerializedBody(rtf, plain, runs);
    }

    // BodyRuns로 만든 문서의 실제 텍스트가 기대치(run 텍스트 글자수 합)의 절반 미만이면 손상 의심.
    // 마커는 양쪽 다 텍스트에 없어 공정한 비교. 공백/개행 제외. (corpus 게이트의 런타임 백업)
    public static bool RebuildLooksDegraded(FlowDocument doc, IReadOnlyList<NoteLine> lines)
    {
        int expected = lines.Sum(l => l.Runs.Sum(r => r.Text.Count(c => !char.IsWhiteSpace(c))));
        if (expected == 0) return false;
        int actual = DocumentText(doc).Count(c => !char.IsWhiteSpace(c));
        return actual < expected / 2;
    }

    // BodyRuns 폴백/구버전 호환: RTF(있으면) → 실패 시 plain Body → 둘 다 없으면 빈 문서.
    public static void LoadFromRtfOrPlain(FlowDocument doc, string? rtf, string? plain)
    {
        if (!string.IsNullOrEmpty(rtf))
        {
            try
            {
                using var ms = new MemoryStream(Encoding.Latin1.GetBytes(rtf));
                new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, DataFormats.Rtf);
                return;
            }
            catch { }
        }
        doc.Blocks.Clear();
        if (!string.IsNullOrEmpty(plain))
            doc.Blocks.Add(new Paragraph(new Run(plain)));
    }

    // RTF 로드 후 블록/리스트 마진 정규화
    public static void NormalizeDocumentMargins(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
        {
            block.ClearValue(Block.MarginProperty);
            if (block is DocList list)
            {
                list.Margin = new Thickness(20, 0, 0, 0);
                list.Padding = new Thickness(0);
                foreach (var item in list.ListItems)
                {
                    item.ClearValue(ListItem.MarginProperty);
                    foreach (var inner in item.Blocks)
                        inner.ClearValue(Block.MarginProperty);
                }
            }
        }
    }

    // 색/글꼴/크기의 명시값 제거 — Bold(FontWeight)/Underline(TextDecorations)은 보존
    public static void NormalizeInheritedFormatting(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
            NormalizeElement(block);
    }

    private static void NormalizeElement(TextElement el)
    {
        el.ClearValue(TextElement.ForegroundProperty);
        el.ClearValue(TextElement.FontFamilyProperty);
        el.ClearValue(TextElement.FontSizeProperty);

        switch (el)
        {
            case Paragraph p:
                foreach (var inline in p.Inlines) NormalizeElement(inline);
                break;
            case Span s:
                foreach (var inline in s.Inlines) NormalizeElement(inline);
                break;
            case DocList list:
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        NormalizeElement(inner);
                break;
        }
    }

    // plain Body 추출 — 중첩 포함, 깊이는 평탄(설계상 plain은 평면, 깊이는 BodyRuns가 정본).
    private static void EmitPlainBlocks(BlockCollection blocks, List<string> lines)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph para)
                lines.Add(new TextRange(para.ContentStart, para.ContentEnd).Text);
            else if (block is DocList list)
                EmitPlainList(list, lines);
        }
    }

    // 각 항목은 "• "/"N. " 접두. 마커가 텍스트에 샌 경우(RTF 왕복) 제거. 중첩 List는 재귀(평탄).
    private static void EmitPlainList(DocList list, List<string> lines)
    {
        bool numbered = NoteLineDocumentBuilder.IsNumberedStyle(list.MarkerStyle);
        int n = 1;
        foreach (var item in list.ListItems)
            foreach (var inner in item.Blocks)
            {
                if (inner is Paragraph innerPara)
                {
                    var text = new TextRange(innerPara.ContentStart, innerPara.ContentEnd).Text;
                    if (!numbered && text.Length > 0 && text[0] == '•')
                        text = text[1..].TrimStart('\t', ' ');
                    else if (numbered)
                    {
                        var m2 = _leakedNumberMarker.Match(text);
                        if (m2.Success) text = text[m2.Length..];
                    }
                    lines.Add(numbered ? $"{n++}. {text}" : $"• {text}");
                }
                else if (inner is DocList nested)
                    EmitPlainList(nested, lines);
            }
    }
}
