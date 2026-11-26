using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class TranslateTests
{
    private sealed class FakeTranslator : ITranslator
    {
        public readonly List<int> BatchSizes = [];
        public int FailOnBatch = -1;
        public Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string language, CancellationToken ct)
        {
            if (BatchSizes.Count == FailOnBatch) throw new InvalidOperationException("boom");
            BatchSizes.Add(texts.Count);
            return Task.FromResult<IReadOnlyList<string>>(texts.Select(t => t.ToUpperInvariant() + " [" + language + "]").ToList());
        }
        public Task<string> SummarizeAsync(string text, string language, CancellationToken ct) => Task.FromResult($"SUMMARY {language}: {text.Length}");
    }

    private static JsonElement In(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Q(string s) => JsonSerializer.Serialize(s);
    private static string Sha(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    private static void MakeDocx(string path, bool hebrewDoc = false)
    {
        using var doc = WordprocessingDocument.Create(path, DocumentFormat.OpenXml.WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var heading = new Paragraph(
            new ParagraphProperties(new ParagraphStyleId { Val = "Heading1" }),
            new Run(new RunProperties(new Bold()), new Text("Chapter one")),
            new Run(new Text(" tail")));
        var body1 = new Paragraph(
            new ParagraphProperties(new Justification { Val = JustificationValues.Left }),
            new Run(new Text("Hello ") { Space = DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve }),
            new Run(new RunProperties(new Bold()), new Text("bold word")));
        var heb = new Paragraph(new ParagraphProperties(new BiDi()), new Run(new Text("שלום עולם")));
        var empty = new Paragraph();
        var table = new Table(
            new TableRow(new TableCell(new Paragraph(new Run(new Text("cell a")))), new TableCell(new Paragraph(new Run(new Text("cell b"))))),
            new TableRow(new TableCell(new Paragraph(new Run(new Text("cell c")))), new TableCell(new Paragraph())));
        main.Document = new Document(new Body(heading, body1, empty, table, heb));
        main.Document.Save();
    }

    private static List<Paragraph> Paras(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        return doc.MainDocumentPart!.Document!.Body!.Descendants<Paragraph>().Select(p => (Paragraph)p.CloneNode(true)).ToList();
    }

    [Fact]
    public async Task Docx_TranslatedToNewFile_StructureAndFormattingKept_OriginalUntouched()
    {
        using var d = new TempDir();
        var src = d.File("papers/Article.docx");
        MakeDocx(src);
        var before = Sha(src);
        var tr = new FakeTranslator();
        var tool = new TranslateDocumentTool([d.Path], tr);

        var res = await tool.RunAsync(In($$"""{"path":{{Q("papers/Article.docx")}},"target_language":"he"}"""), default);

        Assert.StartsWith("ok: saved as papers/Article (עברית).docx", res);
        Assert.Contains("paragraphs: 6", res);
        Assert.Contains("batches: 1", res);
        Assert.Equal(before, Sha(src));
        var outPath = d.File("papers/Article (עברית).docx");
        Assert.True(File.Exists(outPath));

        var orig = Paras(src);
        var outp = Paras(outPath);
        Assert.Equal(orig.Count, outp.Count);
        // heading: translated into the first (bold) run, rPr kept, style kept, tail run dropped
        Assert.Equal("CHAPTER ONE TAIL [Hebrew]", TranslateDocumentTool.ParagraphText(outp[0]));
        Assert.NotNull(outp[0].Descendants<Run>().First().RunProperties?.GetFirstChild<Bold>());
        Assert.Equal("Heading1", outp[0].ParagraphProperties!.ParagraphStyleId!.Val!.Value);
        Assert.Equal(DocumentFormat.OpenXml.SpaceProcessingModeValues.Preserve, outp[0].Descendants<Text>().First().Space!.Value);
        // table cells
        var texts = outp.Select(TranslateDocumentTool.ParagraphText).ToList();
        Assert.Contains("CELL A [Hebrew]", texts);
        Assert.Contains("CELL B [Hebrew]", texts);
        Assert.Contains("CELL C [Hebrew]", texts);
        // direction: bidi added, left flipped to right and bidi placed before jc
        Assert.NotNull(outp[1].ParagraphProperties!.GetFirstChild<BiDi>());
        Assert.Equal(JustificationValues.Right, outp[1].ParagraphProperties!.GetFirstChild<Justification>()!.Val!.Value);
        var kids = outp[1].ParagraphProperties!.ChildElements.Select(c => c.GetType()).ToList();
        Assert.True(kids.IndexOf(typeof(BiDi)) < kids.IndexOf(typeof(Justification)));
        // empty paragraphs untouched (no bidi added)
        Assert.Null(outp[2].ParagraphProperties?.GetFirstChild<BiDi>());
    }

    [Fact]
    public async Task Docx_ToEnglish_RemovesBidi()
    {
        using var d = new TempDir();
        var src = d.File("a.docx");
        MakeDocx(src);
        var tool = new TranslateDocumentTool([d.Path], new FakeTranslator());
        var res = await tool.RunAsync(In($$"""{"path":{{Q(src)}},"target_language":"en"}"""), default);
        Assert.Contains("a (English).docx", res);
        var outp = Paras(d.File("a (English).docx"));
        Assert.Null(outp.Last().ParagraphProperties!.GetFirstChild<BiDi>());
        Assert.Contains("שלום עולם [English]", TranslateDocumentTool.ParagraphText(outp.Last()));
    }

    [Fact]
    public async Task NameCollision_AppendsCounter_NeverOverwrites()
    {
        using var d = new TempDir();
        var src = d.File("a.docx");
        MakeDocx(src);
        var tool = new TranslateDocumentTool([d.Path], new FakeTranslator());
        var input = In($$"""{"path":{{Q(src)}},"target_language":"en"}""");
        await tool.RunAsync(input, default);
        var first = Sha(d.File("a (English).docx"));
        var res = await tool.RunAsync(input, default);
        Assert.Contains("a (English) (2).docx", res);
        Assert.True(File.Exists(d.File("a (English) (2).docx")));
        Assert.Equal(first, Sha(d.File("a (English).docx")));
    }

    [Fact]
    public async Task Batching_SplitsAt40_AndPartialFailureWritesNothing()
    {
        using var d = new TempDir();
        var src = d.File("big.docx");
        using (var doc = WordprocessingDocument.Create(src, DocumentFormat.OpenXml.WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            main.Document = new Document(new Body(Enumerable.Range(0, 85).Select(i => new Paragraph(new Run(new Text("p" + i))))));
            main.Document.Save();
        }
        var tr = new FakeTranslator();
        var res = await new TranslateDocumentTool([d.Path], tr).RunAsync(In($$"""{"path":{{Q(src)}},"target_language":"en"}"""), default);
        Assert.Equal([40, 40, 5], tr.BatchSizes);
        Assert.Contains("batches: 3", res);

        File.Delete(d.File("big (English).docx"));
        var failing = new FakeTranslator { FailOnBatch = 1 };
        var res2 = await new TranslateDocumentTool([d.Path], failing).RunAsync(In($$"""{"path":{{Q(src)}},"target_language":"en"}"""), default);
        Assert.StartsWith("error:", res2);
        Assert.False(File.Exists(d.File("big (English).docx")));
    }

    [Fact]
    public async Task PathGuard_AndArguments()
    {
        using var d = new TempDir();
        using var outside = new TempDir();
        var o = outside.File("x.docx");
        MakeDocx(o);
        var tool = new TranslateDocumentTool([d.Path], new FakeTranslator());
        Assert.Equal("error: path not under a document root", await tool.RunAsync(In($$"""{"path":{{Q(o)}},"target_language":"en"}"""), default));
        Assert.Equal("error: path not under a document root", await tool.RunAsync(In("""{"path":"../x.docx","target_language":"en"}"""), default));
        Assert.StartsWith("error: target_language", await tool.RunAsync(In("""{"path":"a.docx","target_language":"fr"}"""), default));
        Assert.StartsWith("error: path is required", await tool.RunAsync(In("""{"target_language":"en"}"""), default));
        var xl = d.File("a.xlsx"); File.WriteAllText(xl, "x");
        Assert.StartsWith("error: unsupported", await tool.RunAsync(In($$"""{"path":{{Q(xl)}},"target_language":"en"}"""), default));
        Assert.Empty(Directory.GetFiles(outside.Path, "*(English)*"));
    }

    [Fact]
    public async Task Txt_SplitsOnBlankLines_WritesTxt()
    {
        using var d = new TempDir();
        var src = d.File("note.txt");
        File.WriteAllText(src, "first para\nsecond line\n\nsecond block\r\n\r\n\r\nthird", new UTF8Encoding(false));
        var tr = new FakeTranslator();
        var res = await new TranslateDocumentTool([d.Path], tr).RunAsync(In($$"""{"path":{{Q(src)}},"target_language":"he"}"""), default);
        Assert.Contains("note (עברית).txt", res);
        Assert.Contains("paragraphs: 3", res);
        var text = DocumentReader.ReadText(d.File("note (עברית).txt"));
        Assert.Equal("FIRST PARA\nSECOND LINE [HEBREW]\n\nSECOND BLOCK [HEBREW]\n\nTHIRD [HEBREW]", text.ToUpperInvariant());
    }

    [Fact]
    public async Task Pdf_BecomesDocx()
    {
        using var d = new TempDir();
        var src = d.File("paper.pdf");
        File.WriteAllBytes(src, TinyPdf("Hello PDF world"));
        Assert.Contains("Hello PDF world", DocumentReader.ReadText(src));
        var res = await new TranslateDocumentTool([d.Path], new FakeTranslator()).RunAsync(In($$"""{"path":{{Q(src)}},"target_language":"he"}"""), default);
        Assert.Contains("paper (עברית).docx", res);
        var ps = Paras(d.File("paper (עברית).docx"));
        var p = Assert.Single(ps);
        Assert.Equal("HELLO PDF WORLD [Hebrew]", TranslateDocumentTool.ParagraphText(p));
        Assert.NotNull(p.ParagraphProperties!.GetFirstChild<BiDi>());
    }

    [Fact]
    public async Task OnlySummary_WritesNoFile()
    {
        using var d = new TempDir();
        var src = d.File("note.txt");
        File.WriteAllText(src, "some text");
        var res = await new TranslateDocumentTool([d.Path], new FakeTranslator()).RunAsync(In($$"""{"path":{{Q(src)}},"target_language":"en","only_summary":true}"""), default);
        Assert.StartsWith("summary (English): SUMMARY English: 9", res);
        Assert.Single(Directory.GetFiles(d.Path));
    }

    [Fact]
    public void TranslationJson_Strict()
    {
        Assert.Equal(["a", "b"], TranslationJson.Parse("""[{"id":0,"text":"a"},{"id":1,"text":"b"}]""", 2));
        Assert.Equal(["a"], TranslationJson.Parse("```json\n[{\"id\":0,\"text\":\"a\"}]\n```", 1));
        Assert.Throws<FormatException>(() => TranslationJson.Parse("""[{"id":1,"text":"a"}]""", 1));
        Assert.Throws<FormatException>(() => TranslationJson.Parse("""[{"id":0,"text":"a"}]""", 2));
        Assert.Throws<FormatException>(() => TranslationJson.Parse("Here you go: []", 0));
        Assert.Throws<FormatException>(() => TranslationJson.Parse("""[{"id":0,"text":" "}]""", 1));
        Assert.Contains("שלום", TranslationJson.BuildRequest(["שלום"]));
    }

    [Fact]
    public async Task ClaudeTranslator_RetriesOnceOnMalformed_ThenFails()
    {
        var calls = 0;
        var t = new ClaudeTranslator(null, "m", (_, user, _) =>
        {
            calls++;
            return Task.FromResult(calls == 1 ? "oops" : """[{"id":0,"text":"X"}]""");
        });
        Assert.Equal(["X"], await t.TranslateBatchAsync(["a"], "English", default));
        Assert.Equal(2, calls);

        calls = 0;
        var bad = new ClaudeTranslator(null, "m", (_, _, _) => { calls++; return Task.FromResult("nope"); });
        await Assert.ThrowsAsync<InvalidOperationException>(() => bad.TranslateBatchAsync(["a"], "English", default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Settings_TranslateModel_FromEnv_AndRegisteredInBuildTools()
    {
        var s = ShamashSettings.Load(k => k == "SHAMASH_TRANSLATE_MODEL" ? "claude-sonnet-5-5" : null, []);
        Assert.Equal("claude-sonnet-5-5", s.TranslateModel);
        Assert.Contains(TurnPipeline.BuildTools(s), t => t.Name == "translate_document");
    }

    /// <summary>A one-page PDF with Helvetica text and a correct xref table.</summary>
    private static byte[] TinyPdf(string text)
    {
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        void Obj(string body) { offsets.Add(sb.Length); sb.Append($"{offsets.Count} 0 obj\n{body}\nendobj\n"); }
        var content = $"BT /F1 18 Tf 72 700 Td ({text}) Tj ET";
        Obj("<< /Type /Catalog /Pages 2 0 R >>");
        Obj("<< /Type /Pages /Kids [3 0 R] /Count 1 >>");
        Obj("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>");
        Obj($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        Obj("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(sb.ToString());
    }
}
