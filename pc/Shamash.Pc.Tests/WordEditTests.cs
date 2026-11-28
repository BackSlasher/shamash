using System.Text.Json;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class WordEditTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ---- words and paragraph numbers

    [Fact]
    public void FirstWords_CutsAtMaxWithEllipsis_AndTreatsWordMarksAsSeparators()
    {
        Assert.Equal("שלום עולם", WordEditText.FirstWords("שלום\rעולם\a", 15));
        Assert.Equal("a b c…", WordEditText.FirstWords("a b c d e", 3));
        Assert.Equal("say 'hi'", WordEditText.FirstWords("say \"hi\"", 5));
        Assert.Equal("", WordEditText.FirstWords("  \r ", 5));
        Assert.Equal("", WordEditText.FirstWords(null, 5));
    }

    [Theory]
    [InlineData(0, 0, 1)]
    [InlineData(120, 2, 3)]
    [InlineData(120, 0, 1)]
    public void ParagraphNumber_IsOneBasedFromThePrecedingParagraphs(int start, int before, int expected) =>
        Assert.Equal(expected, WordEditText.ParagraphNumber(start, before));

    // ---- describe

    [Fact]
    public void Describe_FormatsEveryFieldAndNoneForNoSelection()
    {
        var f = new DocFacts("מאמר על ירושלים.docx", @"C:\Users\x\Documents\מאמר על ירושלים.docx", false, true, 3, 6, 3, "פסקה שלישית על החומות\r", null, 2);
        Assert.Equal(
            "document: מאמר על ירושלים.docx; path: C:\\Users\\x\\Documents\\מאמר על ירושלים.docx; saved: no; track changes: on; revisions: 3; paragraphs: 6; " +
            "cursor: paragraph 3 of 6, starts \"פסקה שלישית על החומות\"; selection: none; footnotes: 2",
            WordEditText.Describe(f));
        var sel = WordEditText.Describe(f with { SelectionText = "שתי מילים\r", Saved = true, TrackChanges = false });
        Assert.Contains("selection: \"שתי מילים\"", sel);
        Assert.Contains("saved: yes; track changes: off", sel);
    }

    [Fact]
    public void Describe_LimitsCursorTo15AndSelectionTo30Words()
    {
        var words = string.Join(' ', Enumerable.Range(1, 40).Select(i => "w" + i));
        var text = WordEditText.Describe(new DocFacts("a.docx", "C:\\a.docx", true, false, 0, 1, 1, words, words, 0));
        Assert.Contains("starts \"" + string.Join(' ', Enumerable.Range(1, 15).Select(i => "w" + i)) + "…\"", text);
        Assert.Contains("selection: \"" + string.Join(' ', Enumerable.Range(1, 30).Select(i => "w" + i)) + "…\"", text);
    }

    [Fact]
    public void Mutation_NamesDocument_BackupNote_AndUndo()
    {
        Assert.Equal("x; document: a.docx; undo available", WordEditText.Mutation("x", "a.docx", ""));
        Assert.Equal("x; document: a.docx; not an undo step (set it back with set_track_changes)", WordEditText.Mutation("x", "a.docx", "", undoable: false));
        Assert.Equal("x; document: a.docx; backup: C:\\b\\1 a.docx; undo available", WordEditText.Mutation("x", "a.docx", "backup: C:\\b\\1 a.docx"));
    }

    // ---- undo

    [Fact]
    public void ParseUndoCount_DefaultsToOne_AndValidates()
    {
        Assert.Equal((1, (string?)null), WordEditText.ParseUndoCount(J("{}")));
        Assert.Equal((3, (string?)null), WordEditText.ParseUndoCount(J("""{"count":3}""")));
        Assert.StartsWith("error:", WordEditText.ParseUndoCount(J("""{"count":0}""")).Error);
        Assert.StartsWith("error:", WordEditText.ParseUndoCount(J("""{"count":51}""")).Error);
        Assert.StartsWith("error:", WordEditText.ParseUndoCount(J("""{"count":"2"}""")).Error);
        Assert.StartsWith("error:", WordEditText.ParseUndoCount(J("""{"count":1.5}""")).Error);
    }

    [Fact]
    public void UndoResult_SaysHowManySteps()
    {
        Assert.Equal("undone: 1 step; document: a.docx", WordEditText.UndoResult(1, "a.docx"));
        Assert.Equal("undone: 2 steps; document: a.docx", WordEditText.UndoResult(2, "a.docx"));
        Assert.Equal("error: nothing to undo", WordEditText.NothingToUndo);
    }

    // ---- track changes

    [Fact]
    public void ParseTrack_NeedsABoolean()
    {
        Assert.Equal((true, (string?)null), WordEditText.ParseTrack(J("""{"on":true}""")));
        Assert.Equal((false, (string?)null), WordEditText.ParseTrack(J("""{"on":false}""")));
        Assert.StartsWith("error: on is required", WordEditText.ParseTrack(J("{}")).Error);
        Assert.StartsWith("error: on is required", WordEditText.ParseTrack(J("""{"on":"yes"}""")).Error);
        Assert.Equal("track changes: was off, now on", WordEditText.TrackResult(false, true));
        Assert.Equal("track changes: was on, now off", WordEditText.TrackResult(true, false));
    }

    // ---- paragraph spacing

    [Fact]
    public void LineRules_UseWordEnumValues_OneAndAHalfIsOneNotFour()
    {
        Assert.Equal(0, WordEditText.ParseSpacing(J("""{"scope":"paragraph","line_spacing":"single"}""")).Request!.Rule);
        Assert.Equal(1, WordEditText.ParseSpacing(J("""{"scope":"paragraph","line_spacing":"1.5"}""")).Request!.Rule);
        Assert.Equal(2, WordEditText.ParseSpacing(J("""{"scope":"paragraph","line_spacing":"double"}""")).Request!.Rule);
        Assert.Equal("1.5", WordEditText.LineRuleName(1));
        Assert.Equal("exactly", WordEditText.LineRuleName(4));
        Assert.Equal("mixed", WordEditText.LineRuleName(9999999));
    }

    [Theory]
    [InlineData("{}", "scope must be")]
    [InlineData("""{"scope":"page","line_spacing":"single"}""", "scope must be")]
    [InlineData("""{"scope":"paragraph"}""", "give line_spacing")]
    [InlineData("""{"scope":"paragraph","line_spacing":"triple"}""", "line_spacing must be")]
    [InlineData("""{"scope":"paragraph","line_spacing":2}""", "line_spacing must be")]
    [InlineData("""{"scope":"paragraph","space_after_points":-1}""", "space_after_points must be")]
    [InlineData("""{"scope":"paragraph","space_after_points":"6"}""", "space_after_points must be")]
    [InlineData("""{"scope":"paragraph","space_after_points":2000}""", "space_after_points must be")]
    public void ParseSpacing_RejectsBadInput(string json, string expected)
    {
        var (req, err) = WordEditText.ParseSpacing(J(json));
        Assert.Null(req);
        Assert.StartsWith("error: " + expected, err);
    }

    [Fact]
    public void ParseSpacing_AcceptsEitherOrBothSettings()
    {
        var both = WordEditText.ParseSpacing(J("""{"scope":"Document","line_spacing":"double","space_after_points":6}""")).Request!;
        Assert.Equal("document", both.Scope);
        Assert.Equal(6, both.SpaceAfter);
        Assert.Null(WordEditText.ParseSpacing(J("""{"scope":"selection","space_after_points":0}""")).Request!.Rule);
    }

    [Fact]
    public void SpacingResult_ReadsBackWithPreviousValueAndScope()
    {
        var req = new WordEditText.SpacingRequest("paragraph", "1.5", null);
        Assert.Equal("line spacing: 1.5 (was single) for paragraph 3", WordEditText.SpacingResult(req, 0, 1, 0, 0, WordEditText.ScopeLabel("paragraph", 3, 3, 6)));
        var both = new WordEditText.SpacingRequest("document", "double", 6);
        Assert.Equal("line spacing: double (was mixed), space after: 6 pt (was 7.5 pt) for the whole document (6 paragraphs)",
            WordEditText.SpacingResult(both, 9999999, 2, 7.5, 6, WordEditText.ScopeLabel("document", 1, 6, 6)));
        Assert.Equal("paragraphs 2 to 3", WordEditText.ScopeLabel("selection", 2, 3, 6));
        Assert.True(WordEditText.SameSpace(6, 6.04));
        Assert.False(WordEditText.SameSpace(6, 7));
    }

    // ---- insert text

    [Fact]
    public void ValidateInsert_AcceptsSpecialCharacters_OneBreak_AndRejectsTheRest()
    {
        Assert.Equal("©", WordEditText.ValidateInsert("©").Text);
        Assert.Equal("é", WordEditText.ValidateInsert("é").Text);
        Assert.Equal("a\rb", WordEditText.ValidateInsert("a\r\nb").Text);
        Assert.Equal("a\tb", WordEditText.ValidateInsert("a\tb").Text);
        Assert.StartsWith("error: more than one paragraph break", WordEditText.ValidateInsert("a\nb\nc").Error);
        Assert.StartsWith("error: text is too long", WordEditText.ValidateInsert(new string('x', 201)).Error);
        Assert.Null(WordEditText.ValidateInsert(new string('x', 200)).Error);
        Assert.StartsWith("error: text is required", WordEditText.ValidateInsert("").Error);
        Assert.StartsWith("error: text is required", WordEditText.ValidateInsert(null).Error);
        Assert.StartsWith("error: text contains control", WordEditText.ValidateInsert("a\u0007b").Error);
    }

    [Fact]
    public void InsertResult_QuotesTheTextAndNamesTheParagraph()
    {
        Assert.Equal("inserted \"©\" at paragraph 3", WordEditText.InsertResult("©", 3));
        Assert.Equal("inserted \"a⏎b\" at paragraph 1", WordEditText.InsertResult("a\rb", 1));
    }

    // ---- footnote

    [Fact]
    public void ValidateFootnote_TrimsAndChecks()
    {
        var (t, a, e) = WordEditText.ValidateFootnote("  ראו שם, עמ' 12  ", " החומות ");
        Assert.Equal("ראו שם, עמ' 12", t);
        Assert.Equal("החומות", a);
        Assert.Null(e);
        Assert.Null(WordEditText.ValidateFootnote("x", "  ").AfterWords);
        Assert.StartsWith("error: text is required", WordEditText.ValidateFootnote(" ", null).Error);
        Assert.StartsWith("error: the footnote text must be one line", WordEditText.ValidateFootnote("a\nb", null).Error);
        Assert.StartsWith("error: text is too long", WordEditText.ValidateFootnote(new string('x', 1001), null).Error);
        Assert.StartsWith("error: after_words is too long", WordEditText.ValidateFootnote("x", new string('y', 256)).Error);
        Assert.StartsWith("error: after_words must be plain", WordEditText.ValidateFootnote("x", "a\rb").Error);
    }

    [Fact]
    public void Footnote_FindEscapeAndResultText()
    {
        Assert.Equal("a^^b", WordEditText.EscapeFind("a^b"));
        Assert.Equal("error: \"החומות\" not found in the current paragraph", WordEditText.FootnoteNotFound("החומות"));
        Assert.Equal("footnote 3 added after \"החומות\" in paragraph 2", WordEditText.FootnoteResult(3, "החומות", 2));
        Assert.Equal("footnote 1 added at the cursor in paragraph 4", WordEditText.FootnoteResult(1, null, 4));
    }

    // ---- backups

    [Fact]
    public void BackupName_IsTimestampSpaceName()
    {
        Assert.Equal("20261128-134502 מאמר.docx", WordBackups.BackupName(new DateTime(2026, 11, 28, 13, 45, 2), "מאמר.docx"));
    }

    [Fact]
    public void EnsureBackup_CopiesOncePerDocument_AndNeverForUnsaved()
    {
        WordBackups.Reset();
        using var d = new TempDir();
        var doc = d.File("docs/מאמר.docx");
        File.WriteAllText(doc, "original");
        var dir = Path.Combine(d.Path, "backups");
        var first = WordBackups.EnsureBackup(doc, dir, new DateTime(2026, 11, 28, 10, 0, 0));
        Assert.Equal("backup: " + Path.Combine(dir, "20261128-100000 מאמר.docx"), first);
        Assert.Equal("original", File.ReadAllText(Path.Combine(dir, "20261128-100000 מאמר.docx")));
        File.WriteAllText(doc, "edited");
        Assert.Equal("", WordBackups.EnsureBackup(doc, dir, new DateTime(2026, 11, 28, 10, 5, 0)));
        Assert.Single(Directory.GetFiles(dir));
        Assert.Equal("", WordBackups.EnsureBackup(doc.ToUpperInvariant(), dir, new DateTime(2026, 11, 28, 10, 5, 0)));   // same document by normalised path (case-insensitive set)
        Assert.Equal(WordBackups.NeverSaved, WordBackups.EnsureBackup("Document1", dir));
        Assert.Equal(WordBackups.NeverSaved, WordBackups.EnsureBackup(Path.Combine(d.Path, "missing.docx"), dir));
        WordBackups.Reset();
    }

    [Fact]
    public void Newest_PicksTheLatestTimestampOfThatName_Only()
    {
        using var d = new TempDir();
        foreach (var n in new[] { "20261128-100000 a.docx", "20261128-120000 a.docx", "20261127-235959 a.docx", "20261129-000000 other a.docx", "20261129-000000 b.docx" })
            File.WriteAllText(Path.Combine(d.Path, n), n);
        Assert.Equal(Path.Combine(d.Path, "20261128-120000 a.docx"), WordBackups.Newest("a.docx", d.Path));
        Assert.Null(WordBackups.Newest("c.docx", d.Path));
        Assert.Null(WordBackups.Newest("a.docx", Path.Combine(d.Path, "nope")));
    }

    [Fact]
    public void RestoreTarget_NumbersWhenTheNameExists()
    {
        var orig = Path.Combine("dir", "מאמר.docx");
        var taken = new HashSet<string>();
        Assert.Equal(Path.Combine("dir", "מאמר (לפני שמש).docx"), WordBackups.RestoreTarget(orig, taken.Contains));
        taken.Add(Path.Combine("dir", "מאמר (לפני שמש).docx"));
        Assert.Equal(Path.Combine("dir", "מאמר (לפני שמש) 2.docx"), WordBackups.RestoreTarget(orig, taken.Contains));
        taken.Add(Path.Combine("dir", "מאמר (לפני שמש) 2.docx"));
        Assert.Equal(Path.Combine("dir", "מאמר (לפני שמש) 3.docx"), WordBackups.RestoreTarget(orig, taken.Contains));
    }

    [Fact]
    public void Restore_WritesNextToOriginal_NeverOverwrites_NeverTouchesOriginal()
    {
        using var d = new TempDir();
        var dir = Path.Combine(d.Path, "backups"); Directory.CreateDirectory(dir);
        var orig = d.File("docs/מאמר.docx");
        File.WriteAllText(orig, "edited");
        File.WriteAllText(Path.Combine(dir, "20261128-100000 מאמר.docx"), "before");
        var r1 = WordBackups.Restore(orig, dir);
        var copy1 = Path.Combine(d.Path, "docs", "מאמר (לפני שמש).docx");
        Assert.Contains(copy1, r1);
        Assert.Equal("before", File.ReadAllText(copy1));
        Assert.Equal("edited", File.ReadAllText(orig));
        var r2 = WordBackups.Restore(orig, dir);
        Assert.Contains("מאמר (לפני שמש) 2.docx", r2);
        Assert.Equal("before", File.ReadAllText(copy1));
        Assert.Equal(2, Directory.GetFiles(Path.Combine(d.Path, "docs")).Count(f => f.Contains("לפני שמש")));
        Assert.Single(Directory.GetFiles(dir));
        Assert.StartsWith("error: no backup of \"x.docx\"", WordBackups.Restore(d.File("docs/x.docx"), dir));
    }

    // ---- tools: validation happens before any COM, schema is valid JSON

    [Fact]
    public async Task Tools_RejectBadInputBeforeTouchingWord()
    {
        var undo = new UndoLastEditTool();
        Assert.StartsWith("error: count", await undo.RunAsync(J("""{"count":0}"""), default));
        Assert.StartsWith("error: on is required", await new SetTrackChangesTool().RunAsync(J("{}"), default));
        Assert.StartsWith("error: give line_spacing", await new SetParagraphSpacingTool().RunAsync(J("""{"scope":"document"}"""), default));
        Assert.StartsWith("error: more than one paragraph break", await new InsertTextAtCursorTool().RunAsync(J("""{"text":"a\nb\nc"}"""), default));
        Assert.StartsWith("error: text is required", await new AddFootnoteTool().RunAsync(J("{}"), default));
    }

    [Fact]
    public async Task ValidTools_OffWindows_AnswerWindowsOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        Assert.Equal(OfficeCom.WindowsOnly, await new DescribeActiveDocumentTool().RunAsync(J("{}"), default));
        Assert.Equal(OfficeCom.WindowsOnly, await new SetTrackChangesTool().RunAsync(J("""{"on":true}"""), default));
        Assert.Equal(OfficeCom.WindowsOnly, await new AddFootnoteTool().RunAsync(J("""{"text":"x"}"""), default));
    }

    [Fact]
    public async Task RestoreBackupTool_NamedPathOutsideRoots_IsRefused()
    {
        using var d = new TempDir();
        var tool = new RestoreBackupTool([d.Path]);
        Assert.Equal("error: path not under a document root", await tool.RunAsync(J("""{"path":"/etc/passwd"}"""), default));
    }

    [Fact]
    public void BuildTools_RegistersTheWordEditTools_WithValidSchemas()
    {
        var tools = TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, []));
        var names = tools.Select(t => t.Name).ToList();
        foreach (var n in new[] { "describe_active_document", "undo_last_edit", "restore_backup", "set_track_changes", "set_paragraph_spacing", "insert_text_at_cursor", "add_footnote" })
        {
            Assert.Contains(n, names);
            JsonDocument.Parse(tools.First(t => t.Name == n).InputSchema).Dispose();
        }
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void SystemPrompt_MentionsTheEditingRules()
    {
        Assert.Contains("undo_last_edit", ClaudeReplyModel.SystemPrompt);
        Assert.Contains("describe_active_document", ClaudeReplyModel.SystemPrompt);
        Assert.Contains("אפשר לבטל", ClaudeReplyModel.SystemPrompt);
    }
}
