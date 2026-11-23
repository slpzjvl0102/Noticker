using System.Text.RegularExpressions;
using System.Windows;
using RichTextBox = System.Windows.Controls.RichTextBox;
using System.Windows.Documents;
using System.Windows.Input;
using DocList = System.Windows.Documents.List;

namespace Noticker.Windows;

// 스티커 본문(RichTextBox)의 리스트 편집 동작 — StickerWindow에서 분리 (동작 변경 없음).
// 저장/IME 상태는 호출자(StickerWindow) 책임이고, 여기는 문서 조작만 한다. UI 스레드 전용.
public static class StickerListEditing
{
    private const double TopLevelListIndent = 20;

    private static readonly Regex _numberShortcut = new(@"^\d+\.$", RegexOptions.Compiled);

    // 새로 생긴 최상위 List 블록의 들여쓰기 보정 (Document.Resources 스타일이 놓치는 경우 대비)
    public static void NormalizeTopLevelListIndent(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
            if (block is DocList lst && lst.Margin.Left != TopLevelListIndent)
            {
                lst.Margin = new Thickness(TopLevelListIndent, 0, 0, 0);
                lst.Padding = new Thickness(0);
            }
    }

    // 문단 시작의 "- " / "* " → 불릿, "1. " → 번호 리스트 (Space 키 직전 호출).
    // 변환했으면 true — 호출자가 e.Handled를 설정한다.
    public static bool TryApplyMarkdownShortcut(RichTextBox box)
    {
        var para = box.CaretPosition.Paragraph;
        if (para == null || para.Parent is ListItem) return false;

        var text = new TextRange(para.ContentStart, box.CaretPosition).Text;

        if (text is "-" or "*")
        {
            new TextRange(para.ContentStart, box.CaretPosition).Text = "";
            EditingCommands.ToggleBullets.Execute(null, box);
            return true;
        }
        if (_numberShortcut.IsMatch(text))
        {
            new TextRange(para.ContentStart, box.CaretPosition).Text = "";
            EditingCommands.ToggleNumbering.Execute(null, box);
            return true;
        }
        return false;
    }

    // Tab/Shift+Tab = 리스트 항목 강등/승격 (Notion식 아웃라이너).
    // WPF 내장 IncreaseIndentation/DecreaseIndentation이 "이전 형제 밑 중첩 / 첫 항목 no-op"을
    // 정확히 수행한다(TextRangeEditLists.IndentListItems). Tab을 기본 TabForward에 맡기면 빈
    // 셀렉션일 때 탭문자/빈 불릿이 생기므로 직접 실행한다. (AcceptsTab=True 필요)
    // 캐럿이 리스트 밖이면 false (기본 Tab 동작 유지), 처리했으면 true.
    public static bool TryIndentListItem(RichTextBox box, bool outdent)
    {
        var li = box.CaretPosition?.Paragraph?.Parent as ListItem;
        if (li == null) return false;

        bool numbered = NoteLineDocumentBuilder.IsNumberedStyle(li.List?.MarkerStyle);   // 강등 전 종류 기억
        if (outdent)
            EditingCommands.DecreaseIndentation.Execute(null, box);
        else
            EditingCommands.IncreaseIndentation.Execute(null, box);

        // 강등이 만든 새 중첩 List의 종류를 원래대로 강제 (WPF 클론 동작 불확실 대비) —
        // 종류(불릿/번호)는 보존하고 스타일만 깊이로 사이클한다.
        if (box.CaretPosition?.Paragraph?.Parent is ListItem li2 && li2.List is { } newList &&
            NoteLineDocumentBuilder.IsNumberedStyle(newList.MarkerStyle) != numbered)
            newList.MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc;

        FixListMarkersByDepth(box.Document);
        return true;
    }

    // 문서의 모든 List를 재귀로 내려가며 깊이별 마커(●→○→■ / 1.→a.→i.)·들여쓰기·마커크기 교정.
    // 종류는 List가 가진 것을 보존, 스타일만 깊이로 사이클. 마커 사이클은 NoteLineDocumentBuilder와 공유.
    public static void FixListMarkersByDepth(FlowDocument doc)
    {
        foreach (var block in doc.Blocks)
            if (block is DocList list)
                FixListMarker(list, 0);
    }

    private static void FixListMarker(DocList list, int depth)
    {
        // 마커 종류/크기·들여쓰기·마커 간격은 빌더와 동일한 ApplyListStyle로(공백 균일 + DRY).
        bool numbered = NoteLineDocumentBuilder.IsNumberedStyle(list.MarkerStyle);
        NoteLineDocumentBuilder.ApplyListStyle(list, depth, numbered);
        foreach (var item in list.ListItems)
            foreach (var inner in item.Blocks)
            {
                if (inner is Paragraph p) p.FontSize = 13;   // 텍스트는 본문 크기(마커 폰트와 분리)
                else if (inner is DocList nested) FixListMarker(nested, depth + 1);
            }
    }
}
