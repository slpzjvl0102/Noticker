using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Documents;
using DataFormats = System.Windows.DataFormats;
using DocList = System.Windows.Documents.List;
using Noticker.Sync;

namespace Noticker.Windows;

// 중간 모델(NoteLine) → RTF — pull 적용/가져오기용.
// UI 스레드 전용 (FlowDocument는 STA 필요 — MTA 테스트 불가, 수동 QA 영역).
// 인코딩/스타일은 StickerWindow.LoadBody가 읽는 형식과 정확히 대칭 (Latin1, 마진 0, 리스트 들여쓰기 20)
public static class RtfComposer
{
    public static string Compose(IReadOnlyList<NoteLine> lines)
    {
        var doc = new FlowDocument();

        DocList? currentList = null;
        NoteLineKind? listKind = null;

        foreach (var line in lines)
        {
            if (line.Kind == NoteLineKind.Paragraph)
            {
                currentList = null;
                listKind = null;
                doc.Blocks.Add(MakeParagraph(line));
            }
            else
            {
                // 연속된 같은 종류 항목은 하나의 List로 묶음 (LoadBody의 리스트 어법과 일치)
                if (currentList is null || listKind != line.Kind)
                {
                    currentList = new DocList
                    {
                        MarkerStyle = line.Kind == NoteLineKind.Bullet
                            ? TextMarkerStyle.Disc
                            : TextMarkerStyle.Decimal,
                        Margin = new Thickness(20, 0, 0, 0),
                        Padding = new Thickness(0),
                    };
                    listKind = line.Kind;
                    doc.Blocks.Add(currentList);
                }
                currentList.ListItems.Add(new ListItem(MakeParagraph(line)));
            }
        }

        if (doc.Blocks.Count == 0)
            doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });

        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Rtf);
        return Encoding.Latin1.GetString(ms.ToArray());
    }

    private static Paragraph MakeParagraph(NoteLine line)
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
