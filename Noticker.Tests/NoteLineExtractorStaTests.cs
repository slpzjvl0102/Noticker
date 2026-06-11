using System.Text;
using System.Windows;
using System.Windows.Documents;
using Noticker.Sync;
using Noticker.Windows;
using DataFormats = System.Windows.DataFormats;
using RichTextBox = System.Windows.Controls.RichTextBox;

namespace Noticker.Tests;

// Compose → RichTextBox 로드 → Extract가 원본 NoteLine을 복원하는지 — 운영 경로 그대로의 왕복.
// FlowDocument는 STA 필수라 STA 스레드로 감싼다
public class NoteLineExtractorStaTests
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
            $"STA 실패: {error.GetType().Name}: {error.Message}\n{error.StackTrace}");
    }

    private static List<NoteLine> LoadAndExtract(List<NoteLine> source)
    {
        var rtf = RtfComposer.Compose(source, null);
        var box = new RichTextBox();
        using var ms = new System.IO.MemoryStream(Encoding.Latin1.GetBytes(rtf));
        new TextRange(box.Document.ContentStart, box.Document.ContentEnd).Load(ms, DataFormats.Rtf);
        return NoteLineExtractor.Extract(box.Document);
    }

    [Fact]
    public void RoundTrip_PreservesTextAndKinds()
    {
        RunSta(() =>
        {
            List<NoteLine> source =
            [
                new(NoteLineKind.Paragraph, [new NoteRun("문단 ", false, false), new NoteRun("굵게", true, false)]),
                new(NoteLineKind.Bullet, [new NoteRun("불릿 밑줄", false, true)]),
                new(NoteLineKind.Number, [new NoteRun("번호 둘다", true, true)]),
            ];

            var extracted = LoadAndExtract(source);

            // plain 본문 동등성 — 마커가 run 텍스트에 새지 않았는지까지 포함해 검증
            Assert.Equal(NotionBlockConverter.ToPlainText(source),
                         NotionBlockConverter.ToPlainText(extracted));

            // Kind 순서 보존 (RTF 왕복이 빈 trailing 문단을 덧붙일 수 있어 prefix 비교)
            var kinds = extracted.Select(l => l.Kind).Take(3).ToList();
            Assert.Equal([NoteLineKind.Paragraph, NoteLineKind.Bullet, NoteLineKind.Number], kinds);
        });
    }

    [Fact]
    public void RoundTrip_PreservesBoldAndUnderlineFlags()
    {
        RunSta(() =>
        {
            List<NoteLine> source =
            [
                new(NoteLineKind.Paragraph, [new NoteRun("plain ", false, false), new NoteRun("bold", true, false)]),
                new(NoteLineKind.Bullet, [new NoteRun("under", false, true)]),
            ];

            var extracted = LoadAndExtract(source);

            var para = extracted[0];
            Assert.Contains(para.Runs, r => r.Text.Contains("bold") && r.Bold && !r.Underline);
            Assert.Contains(para.Runs, r => r.Text.Contains("plain") && !r.Bold);

            var bullet = extracted[1];
            Assert.Contains(bullet.Runs, r => r.Text.Contains("under") && r.Underline);
            // 마커 문자(•)가 run 텍스트에 남으면 안 됨
            Assert.DoesNotContain(bullet.Runs, r => r.Text.Contains('•'));
        });
    }

    [Fact]
    public void AdjacentSameFormatRuns_AreMerged()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("하나"));
            p.Inlines.Add(new Run("둘"));
            var bold = new Run("셋") { FontWeight = FontWeights.Bold };
            p.Inlines.Add(bold);
            doc.Blocks.Add(p);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Single(lines);
            Assert.Equal(2, lines[0].Runs.Count);
            Assert.Equal(new NoteRun("하나둘", false, false), lines[0].Runs[0]);
            Assert.Equal(new NoteRun("셋", true, false), lines[0].Runs[1]);
        });
    }

    [Fact]
    public void EmptyDocument_ExtractsToZeroLines()
    {
        RunSta(() =>
        {
            // 빈 RichTextBox 문서 = 빈 paragraph 1개 — plain 경로(Body="" → 블록 0개)와
            // 패리티가 맞으려면 0줄로 추출돼야 한다
            var box = new RichTextBox();
            Assert.Empty(NoteLineExtractor.Extract(box.Document));

            // 내용 뒤 trailing 빈 줄도 제거되는지
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph(new Run("내용")) { Margin = new Thickness(0) });
            doc.Blocks.Add(new Paragraph { Margin = new Thickness(0) });
            var lines = NoteLineExtractor.Extract(doc);
            Assert.Single(lines);
            Assert.Equal("내용", lines[0].Runs[0].Text);
        });
    }
}
