using System.Text.Json;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class WordCopyPictureTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // Source paragraphs as Word's Content.Text gives them: numbered, one empty, two that mention the sultan.
    private static readonly string[] Paras =
    [
        "1. ירושלים בתקופה העות'מאנית עברה שינויים עמוקים.",
        "2. הסולטאן סולימאן המפואר בנה מחדש את החומות.",
        "",
        "4. הקונסוליות הזרות שינו את מאזן הכוחות.",
        "5. בימי הסולטאן עבדול חמיד נבנה הרכב הרכבת.",
        "6. מחקר חדש בארכיונים באיסטנבול.",
    ];

    // ---- paragraph texts

    [Fact]
    public void ParagraphTexts_SplitsOnParagraphMarks_AndCountsCellAndRowEndsOnce()
    {
        Assert.Equal(["a", "", "b"], WordCopyText.ParagraphTexts("a\r\rb\r"));
        // one row, two cells: cell\r\a cell\r\a row-end\r\a, then a closing paragraph
        Assert.Equal(["x", "y", "", "after"], WordCopyText.ParagraphTexts("x\r\ay\r\a\r\aafter\r"));
        Assert.Empty(WordCopyText.ParagraphTexts(""));
        Assert.Empty(WordCopyText.ParagraphTexts(null));
    }

    [Fact]
    public void IsBlank_TreatsParagraphAndCellMarksAsEmpty()
    {
        Assert.True(WordCopyText.IsBlank(""));
        Assert.True(WordCopyText.IsBlank(" \a\f"));
        Assert.False(WordCopyText.IsBlank(" א "));
    }

    // ---- normalising and matching

    [Fact]
    public void Normalize_IsCaseInsensitive_DropsNiqqudAndQuotes()
    {
        Assert.Equal("jerusalem ottoman", WordCopyText.Normalize("  JERUSALEM-Ottoman "));
        Assert.Equal(WordCopyText.Normalize("העות'מאנית"), WordCopyText.Normalize("העות\u05F3מאנית"));
        Assert.Equal(WordCopyText.Normalize("העות'מאנית"), WordCopyText.Normalize("העותמאנית"));
        Assert.Equal("שלום", WordCopyText.Normalize("שָׁלוֹם"));
    }

    [Fact]
    public void Matches_NeedsEveryToken_InAnyOrder_AndIsHebrewSafe()
    {
        var t = WordCopyText.Tokens("עבדול סולטאן");
        Assert.True(WordCopyText.Matches(Paras[4], t));
        Assert.False(WordCopyText.Matches(Paras[1], t));
        Assert.True(WordCopyText.Matches("The Sultan Rebuilt", WordCopyText.Tokens("sultan REBUILT")));
        Assert.True(WordCopyText.Matches(Paras[0], WordCopyText.Tokens("העותמאנית")));
        Assert.False(WordCopyText.Matches("anything", []));
    }

    // ---- candidates

    [Fact]
    public void FindCandidates_ByNumber_ByWords_OrAllNonEmpty()
    {
        Assert.Equal([2], WordCopyText.FindCandidates(Paras, 2, "ignored").Candidates);
        Assert.Equal([2, 5], WordCopyText.FindCandidates(Paras, null, "סולטאן").Candidates);
        Assert.Equal([1, 2, 4, 5, 6], WordCopyText.FindCandidates(Paras, null, null).Candidates);
        Assert.Equal([1, 2, 4, 5, 6], WordCopyText.FindCandidates(Paras, null, "  ").Candidates);
    }

    [Fact]
    public void FindCandidates_Errors()
    {
        Assert.Equal("error: the document has 6 paragraphs; there is no paragraph 9", WordCopyText.FindCandidates(Paras, 9, null).Error);
        Assert.Equal("error: paragraph 3 is empty", WordCopyText.FindCandidates(Paras, 3, null).Error);
        Assert.Equal("error: no paragraph contains \"פיל\"", WordCopyText.FindCandidates(Paras, null, "פיל").Error);
        Assert.Equal("error: the document has no text paragraphs", WordCopyText.FindCandidates(["", " "], null, null).Error);
    }

    // ---- hints and navigation

    [Theory]
    [InlineData(null, WordCopyText.Move.Forward)]
    [InlineData("", WordCopyText.Move.Forward)]
    [InlineData("יותר למטה", WordCopyText.Move.Forward)]
    [InlineData("הבאה", WordCopyText.Move.Forward)]
    [InlineData("יותר למעלה", WordCopyText.Move.Back)]
    [InlineData("the previous one", WordCopyText.Move.Back)]
    [InlineData("הפסקה", WordCopyText.Move.Forward)]
    [InlineData("הפסקה עם הקונסוליות", WordCopyText.Move.Narrow)]
    public void ParseHint_Direction(string? hint, WordCopyText.Move move) => Assert.Equal(move, WordCopyText.ParseHint(hint).Move);

    [Fact]
    public void ParseHint_NarrowDropsFillerWords() =>
        Assert.Equal(["הקונסוליות"], WordCopyText.ParseHint("הפסקה עם הקונסוליות").Words);

    [Fact]
    public void Navigate_ForwardAndBack_StopAtTheEnds_WithoutMoving()
    {
        int[] c = [2, 5];
        var fwd = WordCopyText.Navigate(Paras, c, 0, "יותר למטה");
        Assert.Equal((1, null), (fwd.Index, fwd.Message));
        var end = WordCopyText.Navigate(Paras, c, 1, null);
        Assert.Equal((1, WordCopyText.Exhausted), (end.Index, end.Message));
        Assert.Equal("no more candidates; ask him for other words or the paragraph number", end.Message);
        var back = WordCopyText.Navigate(Paras, c, 1, "יותר למעלה");
        Assert.Equal(0, back.Index);
        Assert.Equal(WordCopyText.Exhausted, WordCopyText.Navigate(Paras, c, 0, "למעלה").Message);
    }

    [Fact]
    public void Navigate_NarrowingWords_ReplaceTheList_AndSkipTheRejectedParagraph()
    {
        int[] all = [1, 2, 4, 5, 6];
        var n = WordCopyText.Navigate(Paras, all, 1, "הפסקה עם הקונסוליות");
        Assert.Equal([4], n.Candidates);
        Assert.Equal(0, n.Index);
        Assert.Null(n.Message);
        // the rejected paragraph itself is not offered again, but words outside the candidate list widen the search
        var again = WordCopyText.Navigate(Paras, [2, 5], 0, "סולטאן");
        Assert.Equal([5], again.Candidates);
        var wide = WordCopyText.Navigate(Paras, [2, 5], 0, "ארכיונים");
        Assert.Equal([6], wide.Candidates);
        var none = WordCopyText.Navigate(Paras, [2, 5], 0, "פיל");
        Assert.StartsWith("error: no other paragraph contains \"פיל\"", none.Message);
        Assert.Equal(0, none.Index);
    }

    // ---- text

    [Fact]
    public void Describe_NamesTheCandidate_AndAsksZot()
    {
        var text = WordCopyText.Describe(0, 2, "ירושלים.docx", 2, string.Join(' ', Enumerable.Range(1, 20).Select(i => "מ" + i)));
        Assert.Equal("candidate 1 of 2 in \"ירושלים.docx\": paragraph 2 starts \"" + string.Join(' ', Enumerable.Range(1, 12).Select(i => "מ" + i)) + "…\"; ask him \"זאת?\"", text);
    }

    [Fact]
    public void Pasted_AndMutation_MatchTheContract()
    {
        var body = WordCopyText.Pasted(3, "מקור.docx", "יעד.docx", 4, plain: false);
        Assert.Equal("pasted paragraph 3 of \"מקור.docx\" into \"יעד.docx\" at paragraph 4", body);
        Assert.Equal(body + " without formatting", WordCopyText.Pasted(3, "מקור.docx", "יעד.docx", 4, true));
        Assert.EndsWith("; undo available", WordEditText.Mutation(body, "יעד.docx", ""));
    }

    [Fact]
    public void ClampCursor_PlainText_CursorAfter()
    {
        Assert.Equal(99, WordCopyText.ClampCursor(500, 100));
        Assert.Equal(0, WordCopyText.ClampCursor(-4, 100));
        Assert.Equal(40, WordCopyText.ClampCursor(40, 100));
        Assert.Equal(0, WordCopyText.ClampCursor(5, 0));
        Assert.Equal("a\rb", WordCopyText.PlainText("a\r\ab\a"));
        Assert.Equal(40 + 57, WordCopyText.CursorAfter(40, 100, 157));
        Assert.Equal(40, WordCopyText.CursorAfter(40, 100, 100));
    }

    // ---- input validation

    [Fact]
    public void ParseStart_ValidatesAndLetsNumberWin()
    {
        Assert.Equal("error: source is required", WordCopyText.ParseStart(J("{}")).Error);
        Assert.Equal("error: paragraph_number must be a whole number from 1", WordCopyText.ParseStart(J("""{"source":"a.docx","paragraph_number":0}""")).Error);
        Assert.Equal("error: paragraph_number must be a whole number from 1", WordCopyText.ParseStart(J("""{"source":"a.docx","paragraph_number":"3"}""")).Error);
        Assert.StartsWith("error: words is too long", WordCopyText.ParseStart(J(JsonSerializer.Serialize(new { source = "a.docx", words = new string('x', 300) }))).Error);
        Assert.StartsWith("error: words must be plain", WordCopyText.ParseStart(J("""{"source":"a.docx","words":"a\nb"}""")).Error);
        var both = WordCopyText.ParseStart(J("""{"source":" a.docx ","paragraph_number":3,"words":"x"}""")).Request!;
        Assert.Equal(("a.docx", 3, (string?)null), (both.Source, both.ParagraphNumber, both.Words));
        Assert.Equal("x", WordCopyText.ParseStart(J("""{"source":"a.docx","words":"x"}""")).Request!.Words);
    }

    [Fact]
    public void ParseNextAndConfirm_Validate()
    {
        Assert.Null(WordCopyText.ParseNext(J("{}")).Error);
        Assert.Equal("יותר למטה", WordCopyText.ParseNext(J("""{"hint":"יותר למטה"}""")).Hint);
        Assert.StartsWith("error: hint is too long", WordCopyText.ParseNext(J(JsonSerializer.Serialize(new { hint = new string('x', 300) }))).Error);
        Assert.Equal((false, (string?)null), WordCopyText.ParseConfirm(J("{}")));
        Assert.Equal((true, (string?)null), WordCopyText.ParseConfirm(J("""{"plain":true}""")));
        Assert.Equal("error: plain must be true or false", WordCopyText.ParseConfirm(J("""{"plain":"yes"}""")).Error);
    }

    [Fact]
    public void ResolveSource_UnderRootsOnly_AndWordDocumentsOnly()
    {
        using var d = new TempDir();
        using var outside = new TempDir();
        File.WriteAllText(d.File("מאמרים/מקור.docx"), "x");
        File.WriteAllText(d.File("מאמרים/notes.txt"), "x");
        File.WriteAllText(Path.Combine(outside.Path, "s.docx"), "x");
        var rootName = Path.GetFileName(d.Path);
        Assert.Equal(Path.Combine(d.Path, "מאמרים", "מקור.docx"), WordCopyText.ResolveSource($"{rootName}/מאמרים/מקור.docx", [d.Path]).Path);
        Assert.Equal("error: path not under a document root", WordCopyText.ResolveSource(Path.Combine(outside.Path, "s.docx"), [d.Path]).Error);
        Assert.Equal("error: file not found", WordCopyText.ResolveSource("מאמרים/אין.docx", [d.Path]).Error);
        Assert.StartsWith("error: only Word documents", WordCopyText.ResolveSource("מאמרים/notes.txt", [d.Path]).Error);
    }

    [Fact]
    public async Task CopyTools_ValidateBeforeAnyCom_AndRefuseOffWindows()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("src.docx"), "x");
        Assert.Equal("error: source is required", await new CopyParagraphStartTool([d.Path]).RunAsync(J("{}"), default));
        Assert.Equal("error: path not under a document root", await new CopyParagraphStartTool([d.Path]).RunAsync(J("""{"source":"/etc/passwd"}"""), default));
        Assert.Equal("error: plain must be true or false", await new CopyParagraphConfirmTool([d.Path]).RunAsync(J("""{"plain":1}"""), default));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OfficeCom.WindowsOnly, await new CopyParagraphStartTool([d.Path]).RunAsync(J("""{"source":"src.docx"}"""), default));
            Assert.Equal(OfficeCom.WindowsOnly, await new CopyParagraphCancelTool([d.Path]).RunAsync(J("{}"), default));
        }
    }

    // ---- state file

    [Fact]
    public void SessionFile_RoundTrips_TreatsDamageAsNoState_AndClears()
    {
        using var d = new TempDir();
        var f = new SessionFile<CopySession>(d.File("sub/copy-session.json"));
        Assert.Null(f.Load());
        var s = new CopySession(@"C:\a\יעד.docx", 120, @"C:\a\מקור.docx", true, [2, 5], 1);
        f.Save(s);
        var back = f.Load()!;
        Assert.Equal((s.Target, s.Cursor, s.Source, s.OpenedHere, s.Index), (back.Target, back.Cursor, back.Source, back.OpenedHere, back.Index));
        Assert.Equal([2, 5], back.Candidates);
        File.WriteAllText(f.FilePath, "{ not json");
        Assert.Null(f.Load());
        f.Save(s); f.Clear(); f.Clear();
        Assert.Null(f.Load());
    }

    [Fact]
    public void SessionFile_PictureSessionRoundTrips()
    {
        using var d = new TempDir();
        var f = new SessionFile<PictureSession>(d.File("picture-session.json"));
        f.Save(new PictureSession(@"C:\d.docx", "הכותל בחורף.png", true, 0, "ShamashPicture1"));
        var b = f.Load()!;
        Assert.Equal(("הכותל בחורף.png", true, "ShamashPicture1"), (b.File, b.Floating, b.ShapeName));
    }

    // ---- pictures: wrap, size, validation

    [Theory]
    [InlineData("square", 0)]
    [InlineData("tight", 1)]
    [InlineData("top_bottom", 4)]
    public void WrapType_MapsToWdWrapType(string wrap, int code) => Assert.Equal(code, WordPictureText.WrapType(wrap));

    [Fact]
    public void WrapType_InlineIsNotAShape() => Assert.Null(WordPictureText.WrapType("inline"));

    [Theory]
    [InlineData(0, "square")]
    [InlineData(1, "tight")]
    [InlineData(4, "top_bottom")]
    [InlineData(7, "inline")]
    [InlineData(3, "in front of text")]
    [InlineData(5, "behind text")]
    [InlineData(99, "other")]
    public void WrapName_ReadsBackWordValues(int code, string name) => Assert.Equal(name, WordPictureText.WrapName(code));

    [Fact]
    public void Size_IsCentimetresFromPoints_AndFitWidthCutsToTheText()
    {
        Assert.Equal(283.5, WordPictureText.ToPoints(10), 3);
        Assert.Equal("10x5 cm", WordPictureText.Size(283.5, 141.75));
        Assert.Equal("5.3x2 cm", WordPictureText.Size(150, 56.7));
        Assert.Equal((400.0, true), WordPictureText.FitWidth(2000, 400));
        Assert.Equal((300.0, false), WordPictureText.FitWidth(300, 400));
        Assert.Equal((300.0, false), WordPictureText.FitWidth(300, 0));
    }

    [Fact]
    public void ParseInsert_DefaultsToInline_AndValidates()
    {
        var r = WordPictureText.ParseInsert(J("""{"path":" x/y.png "}""")).Request!;
        Assert.Equal(("x/y.png", "inline", (double?)null), (r.Path, r.Wrap, r.WidthCm));
        var r2 = WordPictureText.ParseInsert(J("""{"path":"p.png","wrap":"Top-Bottom","width_cm":8.5}""")).Request!;
        Assert.Equal(("top_bottom", 8.5), (r2.Wrap, r2.WidthCm!.Value));
        Assert.Equal("error: path is required", WordPictureText.ParseInsert(J("{}")).Error);
        Assert.Equal("error: wrap must be inline, square, tight or top_bottom", WordPictureText.ParseInsert(J("""{"path":"p.png","wrap":"behind"}""")).Error);
        Assert.StartsWith("error: width_cm must be a number from 0.5 to 50", WordPictureText.ParseInsert(J("""{"path":"p.png","width_cm":0}""")).Error);
        Assert.StartsWith("error: width_cm must be a number", WordPictureText.ParseInsert(J("""{"path":"p.png","width_cm":"9"}""")).Error);
        Assert.StartsWith("error: width_cm must be a number", WordPictureText.ParseInsert(J("""{"path":"p.png","width_cm":500}""")).Error);
    }

    [Fact]
    public void ParseWidthAndWrap_RequiredForTheFollowUpTools()
    {
        Assert.Equal("error: width_cm is required", WordPictureText.ParseWidth(J("{}"), true).Error);
        Assert.Equal(6, WordPictureText.ParseWidth(J("""{"width_cm":6}"""), true).Cm);
        Assert.Equal("error: wrap is required (inline, square, tight or top_bottom)", WordPictureText.ParseWrap(J("{}"), true).Error);
        Assert.Equal("square", WordPictureText.ParseWrap(J("""{"wrap":"square"}"""), true).Wrap);
    }

    [Fact]
    public void ResolvePicture_UnderDocumentOrPictureRoots_ImagesOnly()
    {
        using var d = new TempDir();
        using var pics = new TempDir();
        File.WriteAllText(d.File("a.png"), "x");
        File.WriteAllText(d.File("a.docx"), "x");
        File.WriteAllText(pics.File("תמונות/הכותל בחורף.png"), "x");
        using var outside = new TempDir();
        File.WriteAllText(Path.Combine(outside.Path, "o.png"), "x");
        var roots = new[] { d.Path, pics.Path };
        var picName = Path.GetFileName(pics.Path);
        Assert.Equal(Path.Combine(pics.Path, "תמונות", "הכותל בחורף.png"), WordPictureText.ResolvePicture($"{picName}/תמונות/הכותל בחורף.png", roots).Path);
        Assert.Equal(Path.Combine(pics.Path, "תמונות", "הכותל בחורף.png"), WordPictureText.ResolvePicture("תמונות/הכותל בחורף.png", roots).Path);
        Assert.Equal("error: path not under a document or picture folder", WordPictureText.ResolvePicture(Path.Combine(outside.Path, "o.png"), roots).Error);
        Assert.Equal("error: file not found", WordPictureText.ResolvePicture("תמונות/אין.png", roots).Error);
        Assert.StartsWith("error: not a picture file", WordPictureText.ResolvePicture(Path.Combine(d.Path, "a.docx"), roots).Error);
    }

    [Fact]
    public void PathResolver_PicturesAlias_ReachesAPicturesRoot()
    {
        using var t = new TempDir();
        var pictures = Path.Combine(t.Path, "Pictures");
        var docs = Path.Combine(t.Path, "Documents");
        Directory.CreateDirectory(docs);
        File.WriteAllText(Path.Combine(t.File("Pictures/x.png")), "x");
        Assert.Equal(Path.Combine(pictures, "x.png"), PathResolver.ResolveFile("תמונות/x.png", [docs, pictures]));
        Assert.Equal(pictures, PathResolver.ResolveFolder("תמונות", [docs, pictures]));
    }

    [Fact]
    public void InsertedAndFollowUpResults_AreFormatted()
    {
        Assert.Equal("inserted picture \"הכותל בחורף.png\" (10x5 cm, wrap: square) after paragraph 3 starting \"3. שלישית, בתקופה\"",
            WordPictureText.Inserted("הכותל בחורף.png", 283.5, 141.75, "square", 3, "3. שלישית, בתקופה\r", false));
        Assert.EndsWith("; scaled down to fit the page width", WordPictureText.Inserted("a.png", 400, 200, "inline", 1, "x", true));
        Assert.Equal("picture \"a.png\" resized to 5x2.5 cm (was 10x5 cm)", WordPictureText.Resized("a.png", 283.5, 141.75, 141.75, 70.875));
        Assert.Equal("picture \"a.png\" wrap: top_bottom (was square)", WordPictureText.WrapChanged("a.png", "square", "top_bottom"));
        Assert.Equal("picture \"a.png\" already has wrap: square", WordPictureText.WrapChanged("a.png", "square", "square"));
        Assert.Equal("error: no picture selected", WordPictureText.NoPicture);
    }

    [Fact]
    public async Task PictureTools_ValidateBeforeAnyCom_AndRefuseOffWindows()
    {
        using var d = new TempDir();
        File.WriteAllText(d.File("a.png"), "x");
        var insert = new InsertPictureTool([d.Path], []);
        Assert.Equal("error: path is required", await insert.RunAsync(J("{}"), default));
        Assert.Equal("error: file not found", await insert.RunAsync(J("""{"path":"nope.png"}"""), default));
        Assert.Equal("error: wrap must be inline, square, tight or top_bottom", await insert.RunAsync(J("""{"path":"a.png","wrap":"x"}"""), default));
        Assert.Equal("error: width_cm is required", await new ResizeLastPictureTool().RunAsync(J("{}"), default));
        Assert.StartsWith("error: wrap is required", await new SetPictureWrapTool().RunAsync(J("{}"), default));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(OfficeCom.WindowsOnly, await insert.RunAsync(J("""{"path":"a.png","wrap":"square","width_cm":8}"""), default));
            Assert.Equal(OfficeCom.WindowsOnly, await new ResizeLastPictureTool().RunAsync(J("""{"width_cm":5}"""), default));
        }
    }

    // ---- search: picture kind and roots

    private sealed class RecordingIndex : IDocumentIndex
    {
        public IReadOnlyList<string>? Roots; public string? Kind;
        public IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct)
        { Roots = roots; Kind = kind; return []; }
    }

    [Fact]
    public async Task Search_PictureKind_AddsPictureRoots_OtherKindsDoNot()
    {
        var idx = new RecordingIndex();
        var tool = new SearchDocumentsTool(["/docs"], idx, ["/pics"]);
        await tool.RunAsync(J("""{"query":"כותל","kind":"picture"}"""), default);
        Assert.Equal(["/docs", "/pics"], idx.Roots);
        Assert.Equal("picture", idx.Kind);
        await tool.RunAsync(J("""{"query":"כותל"}"""), default);
        Assert.Equal(["/docs"], idx.Roots);
        Assert.Equal("any", idx.Kind);
        await tool.RunAsync(J("""{"query":"כותל","kind":"docx"}"""), default);
        Assert.Equal(["/docs"], idx.Roots);
    }

    [Fact]
    public async Task Search_PictureKind_ListsHitsUnderThePicturesRoot()
    {
        using var t = new TempDir();
        var docs = Path.Combine(t.Path, "Documents"); Directory.CreateDirectory(docs);
        var pics = Path.Combine(t.Path, "Pictures");
        var f1 = t.File("Pictures/תמונות/הכותל בחורף.png"); File.WriteAllText(f1, "x");
        var f2 = t.File("Pictures/תמונות/הכותל בקיץ.png"); File.WriteAllText(f2, "x");
        var m = new DateTime(2026, 11, 20);
        var tool = new SearchDocumentsTool([docs], new FixedIndex([new("הכותל בחורף.png", f1, m, 2048), new("הכותל בקיץ.png", f2, m, 2048)]), [pics]);
        var r = await tool.RunAsync(J("""{"query":"כותל","kind":"picture"}"""), default);
        Assert.Contains("הכותל בחורף.png | Pictures/תמונות | 2026-11-20 | 2 KB", r);
        Assert.Contains("הכותל בקיץ.png | Pictures/תמונות |", r);
    }

    private sealed class FixedIndex(IReadOnlyList<DocumentHit> hits) : IDocumentIndex
    {
        public IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct) => hits;
    }

    [Fact]
    public void Search_PictureKind_SqlFiltersImageExtensions_AndScopesBothRoots()
    {
        var sql = WindowsSearchIndex.BuildSql([@"C:\Users\a\Documents", @"C:\Users\a\Pictures"], "כותל", ["כותל"], "picture", 10);
        Assert.Contains("SCOPE='file:C:\\Users\\a\\Pictures'", sql);
        foreach (var e in new[] { ".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".bmp" })
            Assert.Contains($"System.FileExtension = '{e}'", sql);
        Assert.DoesNotContain("'.docx'", sql);
        Assert.Equal([".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".bmp"], SearchDocumentsTool.Extensions("picture")!);
        Assert.Null(SearchDocumentsTool.Extensions("any"));
    }

    [Fact]
    public void Search_SchemaListsPicture_DefaultStaysAny()
    {
        var schema = new SearchDocumentsTool(["/d"], null).InputSchema;
        Assert.Contains("\"picture\"", schema);
        Assert.Equal(["/d"], SearchDocumentsTool.RootsFor("any", ["/d"], ["/p"]));
        Assert.Equal(["/d", "/p"], SearchDocumentsTool.RootsFor("picture", ["/d"], ["/p"]));
        Assert.Equal(["/d"], SearchDocumentsTool.RootsFor("picture", ["/d"], ["/D"]));
        Assert.Equal(["/d"], SearchDocumentsTool.RootsFor("picture", ["/d"], null));
    }

    // ---- settings

    [Fact]
    public void Settings_PictureRoots_FromEnvFileAndDefault()
    {
        Assert.Equal(["/p1", "/p2"], ShamashSettings.Load(k => k == "SHAMASH_PICTURE_ROOTS" ? "/p1; /p2" : null, []).PictureRoots);
        using var d = new TempDir();
        var cfg = d.File("config.json");
        File.WriteAllText(cfg, """{"pictureRoots":["/x"],"documentRoots":["/docs"]}""");
        var s = ShamashSettings.Load(_ => null, [cfg]);
        Assert.Equal(["/x"], s.PictureRoots);
        Assert.Equal(["/docs"], s.DocumentRoots);
        if (!OperatingSystem.IsWindows()) Assert.Empty(ShamashSettings.Load(_ => null, []).PictureRoots);
        // documentRoots in the file must not leak into the picture roots
        File.WriteAllText(cfg, """{"documentRoots":["/docs"]}""");
        if (!OperatingSystem.IsWindows()) Assert.Empty(ShamashSettings.Load(_ => null, [cfg]).PictureRoots);
    }

    // ---- registration and prompt

    [Fact]
    public void BuildTools_RegistersTheCopyAndPictureTools_WithValidSchemas()
    {
        var tools = TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, []));
        var names = tools.Select(t => t.Name).ToList();
        foreach (var n in new[] { "copy_paragraph_start", "copy_paragraph_next", "copy_paragraph_confirm", "copy_paragraph_cancel", "insert_picture", "resize_last_picture", "set_picture_wrap" })
        {
            Assert.Contains(n, names);
            JsonDocument.Parse(tools.First(t => t.Name == n).InputSchema).Dispose();
        }
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void SystemPrompt_KeepsOldRules_AndAddsCopyAndPictureRules()
    {
        var p = ClaudeReplyModel.SystemPrompt;
        foreach (var kept in new[] { "undo_last_edit", "describe_active_document", "close_document", "report_missing_feature", "sort_folder_start" }) Assert.Contains(kept, p);
        foreach (var added in new[] { "copy_paragraph_start", "copy_paragraph_next", "copy_paragraph_confirm", "copy_paragraph_cancel", "בלי עיצוב", "kind=picture", "insert_picture", "set_picture_wrap", "top_bottom", "resize_last_picture" })
            Assert.Contains(added, p);
    }
}
