using System.Windows;
using System.Windows.Documents;
using DocList = System.Windows.Documents.List;
using Noticker.Sync;

namespace Noticker.Windows;

// NoteLine(+Depth) → FlowDocument 재구성 — 중첩 리스트의 '정본' 빌더.
// RTF는 중첩 List 구조·MarkerStyle을 신뢰성 있게 왕복하지 못하므로(MS 재현·미해결 버그),
// 로드(StickerWindow.LoadBody)와 pull 합성(RtfComposer) 모두 이 빌더를 거쳐 문서를 만든다.
// UI 스레드 전용(FlowDocument는 STA). 마커 사이클은 StickerWindow.FixListMarker와 공유.
public static class NoteLineDocumentBuilder
{
    private const double BodyFontSize = 13;   // 항목 텍스트 크기 (마커와 분리)

    private static readonly TextMarkerStyle[] _bulletCycle =
        [TextMarkerStyle.Disc, TextMarkerStyle.Circle, TextMarkerStyle.Box];   // ●→○→■
    private static readonly TextMarkerStyle[] _numberCycle =
        [TextMarkerStyle.Decimal, TextMarkerStyle.LowerLatin, TextMarkerStyle.LowerRoman]; // 1.→a.→i.

    public static TextMarkerStyle MarkerForDepth(bool numbered, int depth)
    {
        var cycle = numbered ? _numberCycle : _bulletCycle;
        return cycle[Math.Max(0, depth) % cycle.Length];
    }

    public static bool IsNumberedStyle(TextMarkerStyle? m) => m is
        TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
        or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman;

    // 마커 글리프별 시각 크기 보정 — 같은 폰트에서 ○(속 빈 원)·■(꽉 찬 사각)이 ●(점)보다 커 보여
    // List 폰트를 글리프별로 낮춰 균형을 맞춘다. 텍스트는 항목 Paragraph에서 본문(13)로 고정하므로
    // 이 값은 마커 크기에만 영향. (값은 시각 튜닝 — 필요하면 여기만 조정)
    public static double MarkerFontSize(TextMarkerStyle m) => m switch
    {
        TextMarkerStyle.Circle => 7,
        TextMarkerStyle.Box or TextMarkerStyle.Square => 13,
        _ => 13,
    };

    public const double IndentPerLevel = 20;   // 레벨당 들여쓰기(부모 텍스트 기준)
    public const double MarkerGap = 6;          // 마커-텍스트 간격 고정값

    // List의 시각 속성을 한 곳에서 적용 — 마커 종류/크기·들여쓰기·마커 간격.
    // MarkerOffset을 고정하면 마커 글리프 폰트(●13/○7/■13)가 달라도 마커-텍스트 간격이
    // 일정해진다(기본 NaN은 폰트에서 자동 계산돼 레벨마다 간격이 달라짐 → 공백 불균일 원인).
    public static void ApplyListStyle(DocList list, int depth, bool numbered)
    {
        var marker = MarkerForDepth(numbered, depth);
        list.MarkerStyle = marker;
        list.FontSize = MarkerFontSize(marker);
        list.Margin = new Thickness(IndentPerLevel, 0, 0, 0);
        list.Padding = new Thickness(0);
        list.MarkerOffset = MarkerGap;
    }

    // 기존 doc.Blocks를 비우고 lines로 채운다(중첩 포함). doc의 폰트/크기는 호출자가 설정.
    // 평면 depth 태그 시퀀스 → 중첩 List 트리: 깊이별로 열린 List를 스택처럼 추적.
    public static void Populate(FlowDocument doc, IReadOnlyList<NoteLine> lines)
    {
        doc.Blocks.Clear();
        var open = new List<DocList?>();   // index = depth, 현재 열려 있는 List

        foreach (var line in lines)
        {
            if (line.Kind == NoteLineKind.Paragraph)
            {
                open.Clear();
                doc.Blocks.Add(MakeParagraph(line));
                continue;
            }

            int d = line.Depth < 0 ? 0 : line.Depth;
            bool numbered = line.Kind == NoteLineKind.Number;

            // 현재 줄보다 깊은 열린 List는 닫는다
            if (open.Count > d + 1) open.RemoveRange(d + 1, open.Count - (d + 1));

            var list = d < open.Count ? open[d] : null;
            if (list is null || IsNumberedStyle(list.MarkerStyle) != numbered)
            {
                // depth d에 새 List(또는 종류 바뀐 형제 List) 시작 — 시각 속성은 ApplyListStyle 공용.
                list = new DocList();
                ApplyListStyle(list, d, numbered);
                AttachList(doc, open, list, d);
                while (open.Count <= d) open.Add(null);
                open[d] = list;
                if (open.Count > d + 1) open.RemoveRange(d + 1, open.Count - (d + 1));
            }
            var p = MakeParagraph(line);
            p.FontSize = BodyFontSize;            // 텍스트는 본문 크기(마커 폰트와 분리)
            list.ListItems.Add(new ListItem(p));
        }

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
    }

    // depth 0은 문서에, depth>0은 부모(depth-1)의 마지막 ListItem 안에 중첩.
    // 부모 항목이 없으면(깊이 점프 등 비정상 입력) 최상위로 강등.
    private static void AttachList(FlowDocument doc, List<DocList?> open, DocList list, int d)
    {
        if (d == 0) { doc.Blocks.Add(list); return; }
        var parent = d - 1 < open.Count ? open[d - 1] : null;
        if (parent is { ListItems.Count: > 0 })
            parent.ListItems.LastListItem!.Blocks.Add(list);
        else
            doc.Blocks.Add(list);
    }

    public static Paragraph MakeParagraph(NoteLine line)
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        foreach (var run in line.Runs)
        {
            var r = new Run(run.Text);
            if (run.Bold) r.FontWeight = FontWeights.Bold;
            if (run.Underline) r.TextDecorations = TextDecorations.Underline;
            p.Inlines.Add(r);
        }
        return p;
    }
}
