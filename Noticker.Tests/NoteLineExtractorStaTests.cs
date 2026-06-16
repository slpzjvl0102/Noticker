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
    public void LineBreak_BecomesNewlineInRunText()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("abc"));
            p.Inlines.Add(new LineBreak());
            p.Inlines.Add(new Run("def"));
            doc.Blocks.Add(p);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Single(lines);
            Assert.Equal("abc\ndef", string.Concat(lines[0].Runs.Select(r => r.Text)));
        });
    }

    [Fact]
    public void SpanLevelFormatting_PropagatesToChildRuns()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            var underlineSpan = new Span(new Run("밑줄텍스트")) { TextDecorations = TextDecorations.Underline };
            var boldSpan = new Bold(new Run("굵은텍스트"));
            p.Inlines.Add(underlineSpan);
            p.Inlines.Add(boldSpan);
            doc.Blocks.Add(p);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Single(lines);
            Assert.Contains(lines[0].Runs, r => r.Text == "밑줄텍스트" && r.Underline && !r.Bold);
            Assert.Contains(lines[0].Runs, r => r.Text == "굵은텍스트" && r.Bold && !r.Underline);
        });
    }

    [Fact]
    public void UnknownInline_FallsBackToPlainText()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("앞 "));
            p.Inlines.Add(new Figure(new Paragraph(new Run("그림안텍스트"))));
            doc.Blocks.Add(p);

            var lines = NoteLineExtractor.Extract(doc);

            var all = string.Concat(lines.SelectMany(l => l.Runs).Select(r => r.Text));
            Assert.Contains("그림안텍스트", all);
        });
    }

    [Fact]
    public void HeavierThanBoldWeight_CountsAsBold()
    {
        RunSta(() =>
        {
            // 붙여넣기 유입 가능한 ExtraBold(800)는 Bold(700) 이상이라 굵게로 취급,
            // SemiBold(600)는 미만이라 일반으로 취급
            var doc = new FlowDocument();
            var p = new Paragraph { Margin = new Thickness(0) };
            p.Inlines.Add(new Run("엑스트라볼드") { FontWeight = FontWeights.ExtraBold });
            p.Inlines.Add(new Run("세미볼드") { FontWeight = FontWeights.SemiBold });
            doc.Blocks.Add(p);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Single(lines);
            Assert.Contains(lines[0].Runs, r => r.Text == "엑스트라볼드" && r.Bold);
            Assert.Contains(lines[0].Runs, r => r.Text == "세미볼드" && !r.Bold);
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

    // ── 중첩 리스트 depth (FlowDocument 직접 구성 — RTF는 중첩을 못 지키므로 우회) ──

    [Fact]
    public void NestedLists_ExtractWithDepth()
    {
        RunSta(() =>
        {
            // ● 부모
            //     ◦ 자식1
            //     ◦ 자식2
            var doc = new FlowDocument();
            var outer = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc };
            var parent = new ListItem(new Paragraph(new Run("부모")));
            var nested = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Circle };
            nested.ListItems.Add(new ListItem(new Paragraph(new Run("자식1"))));
            nested.ListItems.Add(new ListItem(new Paragraph(new Run("자식2"))));
            parent.Blocks.Add(nested);                 // 부모 항목 Blocks = [Paragraph, 중첩 List]
            outer.ListItems.Add(parent);
            doc.Blocks.Add(outer);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Equal(3, lines.Count);
            Assert.Equal(NoteLineKind.Bullet, lines[0].Kind);
            Assert.Equal(0, lines[0].Depth);
            Assert.Equal("부모", lines[0].Runs[0].Text);
            Assert.Equal(1, lines[1].Depth);
            Assert.Equal("자식1", lines[1].Runs[0].Text);
            Assert.Equal(1, lines[2].Depth);
            Assert.Equal("자식2", lines[2].Runs[0].Text);
        });
    }

    [Fact]
    public void MixedNesting_NumberUnderBullet_KindPerLevel()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var outer = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc };
            var parent = new ListItem(new Paragraph(new Run("불릿부모")));
            var nested = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal };
            nested.ListItems.Add(new ListItem(new Paragraph(new Run("번호자식"))));
            parent.Blocks.Add(nested);
            outer.ListItems.Add(parent);
            doc.Blocks.Add(outer);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Equal(2, lines.Count);
            Assert.Equal(NoteLineKind.Bullet, lines[0].Kind);
            Assert.Equal(0, lines[0].Depth);
            Assert.Equal(NoteLineKind.Number, lines[1].Kind);
            Assert.Equal(1, lines[1].Depth);
            Assert.Equal("번호자식", lines[1].Runs[0].Text);
        });
    }

    [Fact]
    public void DeeperNesting_ThreeLevels_DepthMonotonic()
    {
        RunSta(() =>
        {
            var doc = new FlowDocument();
            var l0 = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Disc };
            var i0 = new ListItem(new Paragraph(new Run("L0")));
            var l1 = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Circle };
            var i1 = new ListItem(new Paragraph(new Run("L1")));
            var l2 = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Square };
            l2.ListItems.Add(new ListItem(new Paragraph(new Run("L2"))));
            i1.Blocks.Add(l2);
            l1.ListItems.Add(i1);
            i0.Blocks.Add(l1);
            l0.ListItems.Add(i0);
            doc.Blocks.Add(l0);

            var lines = NoteLineExtractor.Extract(doc);

            Assert.Equal([0, 1, 2], lines.Select(l => l.Depth));
            Assert.Equal(["L0", "L1", "L2"], lines.Select(l => l.Runs[0].Text));
        });
    }

    // ── T3: NoteLineDocumentBuilder(로드 정본) 라운드트립 ──

    // Populate로 만든 중첩 문서를 다시 Extract하면 원본 depth/kind/runs를 복원해야 한다(RTF 우회 정본).
    [Fact]
    public void Builder_NestedRoundTrip_PreservesDepthKindRuns()
    {
        RunSta(() =>
        {
            List<NoteLine> source =
            [
                new(NoteLineKind.Bullet, [new NoteRun("부모", false, false)], 0),
                new(NoteLineKind.Bullet, [new NoteRun("자식", false, false)], 1),
                new(NoteLineKind.Bullet, [new NoteRun("손자", false, false)], 2),
                new(NoteLineKind.Bullet, [new NoteRun("다시1단계", false, false)], 0),
                new(NoteLineKind.Number, [new NoteRun("번호자식", true, false)], 1),
                new(NoteLineKind.Paragraph, [new NoteRun("문단", false, true)], 0),
            ];

            var doc = new FlowDocument();
            NoteLineDocumentBuilder.Populate(doc, source);
            var back = NoteLineExtractor.Extract(doc);

            Assert.Equal(source.Count, back.Count);
            for (int i = 0; i < source.Count; i++)
            {
                Assert.Equal(source[i].Kind, back[i].Kind);
                Assert.Equal(source[i].Depth, back[i].Depth);
                Assert.Equal(source[i].Runs, back[i].Runs);
            }
        });
    }

    // CRITICAL corpus 게이트: 기존(평면) 콘텐츠는 RTF-로드와 BodyRuns-빌더가 같은 추출 결과여야
    // 로드 경로를 BodyRuns로 안전하게 뒤집을 수 있다.
    [Fact]
    public void Builder_FlatContent_MatchesRtfLoad()
    {
        RunSta(() =>
        {
            List<NoteLine> source =
            [
                new(NoteLineKind.Paragraph, [new NoteRun("문단 ", false, false), new NoteRun("굵게", true, false)]),
                new(NoteLineKind.Bullet, [new NoteRun("불릿 밑줄", false, true)]),
                new(NoteLineKind.Number, [new NoteRun("번호 둘다", true, true)]),
                new(NoteLineKind.Number, [new NoteRun("번호2", false, false)]),
            ];

            var viaRtf = LoadAndExtract(source);                    // 경로 A: 기존 RTF 로드
            var doc = new FlowDocument();
            NoteLineDocumentBuilder.Populate(doc, source);
            var viaBuilder = NoteLineExtractor.Extract(doc);        // 경로 B: BodyRuns 빌더

            // 평면 콘텐츠는 두 경로의 plain 표현(텍스트+종류+순서)이 동일해야 한다
            Assert.Equal(NotionBlockConverter.ToPlainText(viaRtf),
                         NotionBlockConverter.ToPlainText(viaBuilder));
        });
    }
}
