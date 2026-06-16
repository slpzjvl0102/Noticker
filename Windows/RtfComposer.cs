using System.IO;
using System.Text;
using System.Windows.Documents;
using DataFormats = System.Windows.DataFormats;
using Noticker.Sync;

namespace Noticker.Windows;

// 중간 모델(NoteLine) → RTF — BodyRtf(레거시/폴백) 생성용.
// 문서 구성은 NoteLineDocumentBuilder(중첩 정본)에 위임하고 여기선 RTF 직렬화만 한다.
// RTF는 중첩 List를 신뢰성 있게 왕복 못 하므로(MS 재현·미해결 버그) 로드 정본은 BodyRuns이며,
// 이 RTF는 BodyRuns 손상 시 폴백/구버전 호환 용도. UI 스레드 전용(FlowDocument는 STA).
public static class RtfComposer
{
    public static string Compose(IReadOnlyList<NoteLine> lines, string? fontFamily = null)
    {
        var doc = new FlowDocument { FontSize = 13 };
        if (!string.IsNullOrEmpty(fontFamily))
            doc.FontFamily = new System.Windows.Media.FontFamily(fontFamily);
        NoteLineDocumentBuilder.Populate(doc, lines);

        var range = new TextRange(doc.ContentStart, doc.ContentEnd);
        using var ms = new MemoryStream();
        range.Save(ms, DataFormats.Rtf);
        return Encoding.Latin1.GetString(ms.ToArray());
    }
}
