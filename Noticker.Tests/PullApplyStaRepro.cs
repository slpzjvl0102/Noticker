using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Noticker.Sync;
using Noticker.Windows;
using DataFormats = System.Windows.DataFormats;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Noticker.Tests;

// pull Apply 경로의 STA 재현 — Compose → RichTextBox 로드 → 정규화가 예외 없이 도는지.
// xUnit은 MTA라 STA 스레드로 감싼다 (FlowDocument/RichTextBox는 STA 필수)
public class PullApplyStaRepro
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
            $"STA 재현 실패: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
    }

    private static List<NoteLine> SampleLines() =>
    [
        new(NoteLineKind.Paragraph, [new NoteRun("폰에서 수정한 문단 ", false, false), new NoteRun("굵게", true, false)]),
        new(NoteLineKind.Bullet, [new NoteRun("불릿 항목", false, true)]),
        new(NoteLineKind.Bullet, [new NoteRun("두 번째", false, false)]),
        new(NoteLineKind.Number, [new NoteRun("번호 항목", true, true)]),
        new(NoteLineKind.Paragraph, []),
    ];

    [Fact]
    public void Compose_Load_Normalize_DoesNotThrow()
    {
        RunSta(() =>
        {
            var rtf = RtfComposer.Compose(SampleLines(), null);

            var box = new RichTextBox();
            using var ms = new System.IO.MemoryStream(Encoding.Latin1.GetBytes(rtf));
            new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Load(ms, DataFormats.Rtf);

            // StickerWindow.NormalizeElement와 동일한 로직 복제
            foreach (var block in box.Document.Blocks)
                Normalize(block);

            // 재직렬화(SaveBodyContent 경로)도 통과해야 함
            using var outMs = new System.IO.MemoryStream();
            new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Save(outMs, DataFormats.Rtf);
            Assert.True(outMs.Length > 0);
        });
    }

    [Fact]
    public void Compose_WithStickerFont_DoesNotThrow()
    {
        RunSta(() =>
        {
            _ = RtfComposer.Compose(SampleLines(), "Malgun Gothic");
            _ = RtfComposer.Compose(SampleLines(), "");
            _ = RtfComposer.Compose([], null);
        });
    }

    private static void Normalize(TextElement el)
    {
        el.ClearValue(TextElement.ForegroundProperty);
        el.ClearValue(TextElement.FontFamilyProperty);
        el.ClearValue(TextElement.FontSizeProperty);
        switch (el)
        {
            case Paragraph p:
                foreach (var inline in p.Inlines) Normalize(inline);
                break;
            case Span s:
                foreach (var inline in s.Inlines) Normalize(inline);
                break;
            case System.Windows.Documents.List list:
                foreach (var item in list.ListItems)
                    foreach (var inner in item.Blocks)
                        Normalize(inner);
                break;
        }
    }
}
