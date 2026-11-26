using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using Shamash.Pc;
using Xunit;
using W = DocumentFormat.OpenXml.Wordprocessing;

namespace Shamash.Pc.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "shamash-test-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public string File(string rel) { var p = System.IO.Path.Combine(Path, rel); Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!); return p; }
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class ToolLoopTests
{
    private sealed class FakeConversation(Func<int, bool, ModelStep> next) : IToolConversation
    {
        public int Calls;
        public readonly List<bool> WithToolsSeen = [];
        public readonly List<(string Id, string Text)> Fed = [];
        public Task<ModelStep> NextAsync(bool withTools, CancellationToken ct)
        {
            WithToolsSeen.Add(withTools);
            return Task.FromResult(next(Calls++, withTools));
        }
        public void AddToolResults(ModelStep step, IReadOnlyList<(string Id, string Text)> results) => Fed.AddRange(results);
    }

    private sealed class FuncTool(string name, Func<JsonElement, CancellationToken, Task<string>> f) : IAgentTool
    {
        public string Name => name;
        public string Description => "d";
        public string InputSchema => """{"type":"object","properties":{}}""";
        public Task<string> RunAsync(JsonElement input, CancellationToken ct) => f(input, ct);
    }

    private static ToolUseRequest Use(string id, string name, string json) => new(id, name, JsonDocument.Parse(json).RootElement.Clone());

    [Fact]
    public async Task ToolIsInvokedWithInput_AndResultFedBack()
    {
        string? seen = null;
        var tool = new FuncTool("echo", (i, _) => { seen = i.GetProperty("q").GetString(); return Task.FromResult("found:" + seen); });
        var conv = new FakeConversation((n, _) => n == 0 ? new ModelStep("", [Use("t1", "echo", """{"q":"שלום"}""")]) : new ModelStep("done", []));
        var calls = new List<ToolCallRecord>();
        var reply = await ToolLoop.RunAsync(conv, [tool], calls, null, CancellationToken.None);
        Assert.Equal("done", reply);
        Assert.Equal("שלום", seen);
        Assert.Equal([("t1", "found:שלום")], conv.Fed);
        var c = Assert.Single(calls);
        Assert.Equal("echo", c.Name);
        Assert.Contains("שלום", c.Input);
        Assert.Equal("found:שלום".Length, c.ResultChars);
    }

    [Fact]
    public async Task StopsAtEightRounds_ThenAsksWithoutTools()
    {
        var invoked = 0;
        var tool = new FuncTool("t", (_, _) => { invoked++; return Task.FromResult("r"); });
        var conv = new FakeConversation((n, withTools) => withTools ? new ModelStep("", [Use("id" + n, "t", "{}")]) : new ModelStep("final", []));
        var reply = await ToolLoop.RunAsync(conv, [tool], [], null, CancellationToken.None);
        Assert.Equal("final", reply);
        Assert.Equal(8, invoked);
        Assert.Equal(9, conv.Calls);
        Assert.False(conv.WithToolsSeen[^1]);
        Assert.All(conv.WithToolsSeen.Take(8), Assert.True);
    }

    [Fact]
    public async Task ToolException_BecomesErrorResult()
    {
        var tool = new FuncTool("bad", (_, _) => throw new InvalidOperationException("boom"));
        var conv = new FakeConversation((n, _) => n == 0 ? new ModelStep("", [Use("a", "bad", "{}"), Use("b", "nope", "{}")]) : new ModelStep("ok", []));
        var calls = new List<ToolCallRecord>();
        Assert.Equal("ok", await ToolLoop.RunAsync(conv, [tool], calls, null, CancellationToken.None));
        Assert.StartsWith("error:", conv.Fed[0].Text);
        Assert.Contains("boom", conv.Fed[0].Text);
        Assert.StartsWith("error: unknown tool", conv.Fed[1].Text);
        Assert.Equal(2, calls.Count);
    }

    [Fact]
    public async Task Cancellation_StopsTheLoop()
    {
        using var cts = new CancellationTokenSource();
        var tool = new FuncTool("t", (_, _) => { cts.Cancel(); return Task.FromResult("r"); });
        var conv = new FakeConversation((_, _) => new ModelStep("", [Use("x", "t", "{}")]));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ToolLoop.RunAsync(conv, [tool], [], null, cts.Token));
        Assert.Equal(1, conv.Calls);
    }
}

public class DocumentToolTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    internal static void MakeDocx(string path, params string[] paragraphs)
    {
        using var doc = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = doc.AddMainDocumentPart();
        var body = new Body();
        foreach (var p in paragraphs) body.Append(new W.Paragraph(new W.Run(new W.Text(p))));
        var table = new W.Table();
        var row = new W.TableRow();
        row.Append(new W.TableCell(new W.Paragraph(new W.Run(new W.Text("תא1")))), new W.TableCell(new W.Paragraph(new W.Run(new W.Text("cell2")))));
        table.Append(row);
        body.Append(table);
        main.Document = new W.Document(body);
        main.Document.Save();
    }

    internal static void MakeXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var wb = doc.AddWorkbookPart();
        wb.Workbook = new Workbook();
        var ws = wb.AddNewPart<WorksheetPart>();
        var data = new SheetData();
        var r1 = new Row();
        r1.Append(Inline("שם"), Inline("Total"));
        var r2 = new Row();
        r2.Append(Inline("דני"), new Cell { CellValue = new CellValue("42") });
        data.Append(r1, r2);
        ws.Worksheet = new Worksheet(data);
        var sheets = wb.Workbook.AppendChild(new Sheets());
        sheets.Append(new Sheet { Id = wb.GetIdOfPart(ws), SheetId = 1, Name = "Budget" });
        wb.Workbook.Save();
        static Cell Inline(string s) => new() { DataType = CellValues.InlineString, InlineString = new InlineString(new DocumentFormat.OpenXml.Spreadsheet.Text(s)) };
    }

    /// <summary>Tiny hand-written one-page PDF with Latin text; offsets computed here.</summary>
    internal static void MakePdf(string path, string text)
    {
        var objs = new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >>",
            null!, // content stream
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        var stream = $"BT /F1 18 Tf 20 100 Td ({text}) Tj ET";
        objs[3] = $"<< /Length {stream.Length} >>\nstream\n{stream}\nendstream";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objs.Length; i++) { offsets.Add(sb.Length); sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n"); }
        var xref = sb.Length;
        sb.Append($"xref\n0 {objs.Length + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes(sb.ToString()));
    }

    [Fact]
    public void Reader_Docx_ParagraphsAndTable()
    {
        using var d = new TempDir();
        var p = d.File("a.docx");
        MakeDocx(p, "מכתב לעירייה", "second");
        var t = DocumentReader.ReadText(p);
        Assert.Equal("מכתב לעירייה\nsecond\nתא1\tcell2\n", t);
    }

    [Fact]
    public void Reader_Xlsx_SheetAndRows()
    {
        using var d = new TempDir();
        var p = d.File("a.xlsx");
        MakeXlsx(p);
        Assert.Equal("[sheet Budget]\nשם\tTotal\nדני\t42\n", DocumentReader.ReadText(p));
    }

    [Fact]
    public void Reader_Pdf_LatinText()
    {
        using var d = new TempDir();
        var p = d.File("a.pdf");
        MakePdf(p, "Ottoman Jerusalem");
        Assert.Contains("Ottoman Jerusalem", DocumentReader.ReadText(p));
    }

    [Fact]
    public void Reader_Text_Utf8WithAndWithoutBom()
    {
        using var d = new TempDir();
        var a = d.File("a.txt"); var b = d.File("b.md");
        File.WriteAllText(a, "ירושלים", new UTF8Encoding(true));
        File.WriteAllText(b, "ירושלים", new UTF8Encoding(false));
        Assert.Equal("ירושלים", DocumentReader.ReadText(a));
        Assert.Equal("ירושלים", DocumentReader.ReadText(b));
    }

    [Fact]
    public async Task ReadTool_GuardsRoots_Truncates_AndResolvesRelative()
    {
        using var d = new TempDir();
        using var outside = new TempDir();
        var inside = d.File("Letters/x.txt");
        File.WriteAllText(inside, new string('א', 500));
        var secret = outside.File("s.txt");
        File.WriteAllText(secret, "no");
        var tool = new ReadDocumentTool([d.Path]);

        Assert.Equal("error: path not under a document root", await tool.RunAsync(J(JsonSerializer.Serialize(new { path = secret })), default));
        Assert.Equal("error: path not under a document root", await tool.RunAsync(J(JsonSerializer.Serialize(new { path = Path.Combine(d.Path, "..", Path.GetFileName(outside.Path), "s.txt") })), default));
        Assert.StartsWith("error: path not under", await tool.RunAsync(J("""{"path":"../../etc/passwd"}"""), default));

        var r = await tool.RunAsync(J(JsonSerializer.Serialize(new { path = inside, max_chars = 100 })), default);
        Assert.StartsWith(new string('א', 100), r);
        Assert.EndsWith("[truncated, 500 chars total]", r);

        var rel = await tool.RunAsync(J("""{"path":"Letters/x.txt","max_chars":100}"""), default);
        Assert.Equal(r, rel);
        Assert.StartsWith("error:", await tool.RunAsync(J("{}"), default));
    }

    [Fact]
    public async Task Search_WalkFallback_FindsHebrewInNameTxtAndDocx()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("notes/a.txt"), "המאמר על ירושלים בתקופה העות'מאנית");
        MakeDocx(d.File("papers/b.docx"), "ירושלים העתיקה");
        File.WriteAllText(d.File("c-ירושלים.txt"), "x");
        File.WriteAllText(d.File("other.txt"), "nothing here");
        var tool = new SearchDocumentsTool([d.Path], new ThrowingIndex());
        var r = await tool.RunAsync(J("""{"query":"ירושלים"}"""), default);
        Assert.Contains("method: walk", r);
        Assert.Contains("a.txt | notes |", r);
        Assert.Contains("b.docx | papers |", r);
        Assert.Contains("c-ירושלים.txt | . |", r);
        Assert.DoesNotContain("other.txt", r);
        var onlyDocx = await tool.RunAsync(J("""{"query":"ירושלים","kind":"docx"}"""), default);
        Assert.Contains("b.docx", onlyDocx);
        Assert.DoesNotContain("a.txt", onlyDocx);
        Assert.StartsWith("no documents found", await tool.RunAsync(J("""{"query":"zzzq"}"""), default));
    }

    [Fact]
    public async Task Search_UsesIndexWhenItAnswers()
    {
        using var d = new TempDir();
        var f = d.File("sub/x.docx");
        MakeDocx(f, "a");
        var tool = new SearchDocumentsTool([d.Path], new FakeIndex([new DocumentHit("x.docx", f, new DateTime(2026, 11, 20), 2048)]));
        var r = await tool.RunAsync(J("""{"query":"x"}"""), default);
        Assert.StartsWith("method: index", r);
        Assert.Contains("1. x.docx | sub | 2026-11-20 | 2 KB", r);
    }

    [Fact]
    public void IndexSql_Shape_AndEscaping()
    {
        var sql = WindowsSearchIndex.BuildSql([@"C:\Users\a\Documents\", @"C:\Users\a\Desktop"], "ירושלים o'brien", ["ירושלים", "o'brien"], "docx", 10);
        Assert.Equal(
            "SELECT TOP 10 System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.Size FROM SYSTEMINDEX " +
            @"WHERE (SCOPE='file:C:\Users\a\Documents' OR SCOPE='file:C:\Users\a\Desktop') AND ((CONTAINS(*, '""ירושלים*"" AND ""o''brien*""') OR FREETEXT(*, 'ירושלים o''brien')) " +
            "OR (System.ItemNameDisplay LIKE '%ירושלים%' AND System.ItemNameDisplay LIKE '%o''brien%')) AND (System.FileExtension = '.docx') ORDER BY System.DateModified DESC",
            sql);
    }

    [Fact]
    public void Tokenize_SplitsOnApostropheGereshHyphenAndQuotes()
    {
        Assert.Equal(["ירושלים", "העות", "מאנית"], SearchDocumentsTool.Tokenize("ירושלים העות'מאנית"));
        Assert.Equal(["צה", "ל", "תל", "אביב", "דו", "ח"], SearchDocumentsTool.Tokenize("צה\u05F3ל תל-אביב דו\u05BEח"));
        Assert.Equal(["מכתב", "לעירייה"], SearchDocumentsTool.Tokenize("  מכתב   \"לעירייה\"\u05F4 "));
        Assert.Empty(SearchDocumentsTool.Tokenize(" ' - "));
    }

    [Fact]
    public void Coverage_NameOnlyExtensions_FromHandlerLookup()
    {
        var c = new IndexCoverage(e => e is ".txt" or ".pdf");
        Assert.Equal(new[] { ".docx", ".xlsx" }, c.NameOnly.Order());
        Assert.Empty(new IndexCoverage(_ => true).NameOnly);
    }

    [Fact]
    public async Task Search_MergesIndexNameHitsWithContentWalk_IndexFirst()
    {
        using var d = new TempDir();
        // index knows only names for docx: it returns the file whose NAME matches
        var byName = d.File("a/ירושלים-notes.docx");
        MakeDocx(byName, "unrelated");
        // content of this docx matches only in the body: only the walk can find it
        var byBody = d.File("b/article.docx");
        MakeDocx(byBody, "ירושלים בתקופה העות'מאנית");
        // a pdf the index covers (not name-only, not walked)
        var pdf = d.File("c/other.pdf");
        MakePdf(pdf, "Jerusalem");
        File.WriteAllText(d.File("d/plain.txt"), "ירושלים", new UTF8Encoding(false)); // no BOM: index could miss it
        var idx = new FakeIndex([new DocumentHit("ירושלים-notes.docx", byName, new DateTime(2026, 1, 1), 1024), new DocumentHit("other.pdf", pdf, new DateTime(2026, 1, 2), 1024)]);
        var tool = new SearchDocumentsTool([d.Path], idx, new IndexCoverage(e => e is ".pdf"));
        var r = await tool.RunAsync(J("""{"query":"ירושלים"}"""), default);
        var lines = r.Split('\n');
        Assert.StartsWith("method: index+walk", lines[0]);
        Assert.Contains("| index", lines[1]);
        Assert.Contains("| index", lines[2]);
        Assert.Contains("article.docx | b |", r);
        Assert.Contains("plain.txt | d |", r);
        Assert.EndsWith("| walk", lines[^1]);
        Assert.Equal(1, r.Split("ירושלים-notes.docx").Length - 1); // merged by path, no duplicate
        Assert.DoesNotContain("other.pdf | c | 2026-01-02 | 1 KB | walk", r);
    }

    [Fact]
    public async Task Search_IndexTxtHit_SkipsTxtWalk()
    {
        using var d = new TempDir();
        var txt = d.File("t/a.txt");
        File.WriteAllText(txt, "ירושלים", new UTF8Encoding(true));
        var tool = new SearchDocumentsTool([d.Path], new FakeIndex([new DocumentHit("a.txt", txt, DateTime.Now, 10)]), new IndexCoverage(_ => true));
        var r = await tool.RunAsync(J("""{"query":"ירושלים"}"""), default);
        Assert.StartsWith("method: index", r);
        Assert.DoesNotContain("walk", r.Split('\n')[1]);
    }

    [Fact]
    public void Settings_DocumentRoots_FromEnvAndFile()
    {
        var env = ShamashSettings.Load(k => k == "SHAMASH_DOCUMENT_ROOTS" ? "/a; /b;;/a" : null, []);
        Assert.Equal(["/a", "/b"], env.DocumentRoots);
        using var d = new TempDir();
        var cfg = d.File("config.json");
        File.WriteAllText(cfg, """{"documentRoots":["/x","/y"]}""");
        Assert.Equal(["/x", "/y"], ShamashSettings.Load(_ => null, [cfg]).DocumentRoots);
        Assert.Empty(ShamashSettings.Load(_ => null, []).DocumentRoots); // Linux default
    }

    private sealed class ThrowingIndex : IDocumentIndex
    {
        public IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct) => throw new InvalidOperationException("no index");
    }

    private sealed class FakeIndex(IReadOnlyList<DocumentHit> hits) : IDocumentIndex
    {
        public IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct) => hits;
    }
}
