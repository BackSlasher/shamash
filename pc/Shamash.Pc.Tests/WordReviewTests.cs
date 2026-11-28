using System.Text.Json;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class WordReviewTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// <summary>A document whose revisions behave like Word's: accepting a deletion or rejecting an insertion removes text, so everything after it moves up.</summary>
    private sealed class FakeDoc(string path = @"C:\Docs\מאמר עם הערות עורך.docx") : IRevisionDocument
    {
        public List<RevInfo> Revs = [];
        public int Selected = -1;
        public int Backups;
        public int AcceptAlls, RejectAlls;
        public List<string> Actions = [];
        public string Name => System.IO.Path.GetFileName(path.Replace('\\', '/'));
        public string FullPath => path;
        public IReadOnlyList<RevInfo> Snapshot() => Revs.ToList();
        public void Select(int index) => Selected = index;
        public (string Before, string After) Around(RevInfo r) => ("w1 w2 w3 w4 w5 w6 w7 w8 w9 w10 w11 w12", "x1 x2 x3 x4 x5 x6 x7 x8 x9 x10 x11 x12");
        public void Accept(int index) { Actions.Add("accept " + Revs[index].Text); Remove(index, accept: true); }
        public void Reject(int index) { Actions.Add("reject " + Revs[index].Text); Remove(index, accept: false); }
        public void AcceptAll() { AcceptAlls++; Revs.Clear(); }
        public void RejectAll() { RejectAlls++; Revs.Clear(); }
        public string Backup() => ++Backups == 1 ? "backup: C:\\b\\x.docx" : "";

        private void Remove(int index, bool accept)
        {
            var r = Revs[index];
            Revs.RemoveAt(index);
            var removed = (accept && r.Type == 2) || (!accept && r.Type == 1) ? r.End - r.Start : 0;
            if (removed > 0) for (var i = 0; i < Revs.Count; i++) if (Revs[i].Start > r.Start) Revs[i] = Revs[i] with { Start = Revs[i].Start - removed, End = Revs[i].End - removed };
        }
    }

    private static RevInfo Rev(int start, int type, string author, string text) => new(start, start + text.Length, type, author, new DateTime(2026, 11, 20), text);

    private static FakeDoc Five() => new()
    {
        Revs =
        [
            Rev(10, 1, "דני לוי", "הוספה א"),
            Rev(30, 2, "דני לוי", "מחיקה ב"),
            Rev(50, 1, "רינה כהן", "הוספה ג"),
            Rev(70, 3, "רינה כהן", "עיצוב ד"),
            Rev(90, 1, "דני לוי", "הוספה ה"),
        ],
    };

    private sealed class Sitting : IDisposable
    {
        public readonly TempDir Dir = new();
        public string File => Dir.File("review.json");
        public ReviewEngine Engine() => new(new ReviewStore(File));
        public void Dispose() => Dir.Dispose();
    }

    // ---- names, keys, answers

    [Theory]
    [InlineData(1, "insertion")]
    [InlineData(2, "deletion")]
    [InlineData(3, "formatting")]
    [InlineData(8, "formatting")]
    [InlineData(10, "formatting")]
    [InlineData(4, "paragraph number")]
    [InlineData(14, "move from")]
    [InlineData(99, "change")]
    public void TypeName_FormattingCoversPropertyParagraphPropertyAndStyle(int wd, string expected) => Assert.Equal(expected, ReviewLogic.TypeName(wd));

    [Fact]
    public void Key_IsStartTypeAuthor() => Assert.Equal("10|1|דני לוי", ReviewLogic.Key(Rev(10, 1, "דני לוי", "x")));

    [Fact]
    public void ParseAnswers_AcceptsEnglishAndHebrewWords_RejectsTheRest()
    {
        var (list, err) = ReviewLogic.ParseAnswers(J("""{"answers":["accept","דחה","Skip","קבל"]}"""));
        Assert.Null(err);
        Assert.Equal(["accept", "reject", "skip", "accept"], list);
        Assert.Contains("got 'maybe'", ReviewLogic.ParseAnswers(J("""{"answers":["accept","maybe"]}""")).Error);
        Assert.StartsWith("error: answers is required", ReviewLogic.ParseAnswers(J("{}")).Error);
        Assert.StartsWith("error: answers is required", ReviewLogic.ParseAnswers(J("""{"answers":[]}""")).Error);
        Assert.Contains("at most", ReviewLogic.ParseAnswers(J("{\"answers\":[" + string.Join(",", Enumerable.Repeat("\"skip\"", 31)) + "]}")).Error);
        Assert.NotNull(ReviewLogic.ParseAnswers(J("""{"answers":[1]}""")).Error);
    }

    [Fact]
    public void Context_TakesTenWordsEachSide_AndBracketsTheText()
    {
        var c = ReviewLogic.Context("w1 w2 w3 w4 w5 w6 w7 w8 w9 w10 w11 w12", "x1 x2 x3 x4 x5 x6 x7 x8 x9 x10 x11 x12", "הוספה \"חדשה\"\r");
        Assert.Equal("…w3 w4 w5 w6 w7 w8 w9 w10 w11 w12 [הוספה 'חדשה'] x1 x2 x3 x4 x5 x6 x7 x8 x9 x10…", c);
        Assert.Equal("…[¶]…", ReviewLogic.Context("", "", "\r"));
    }

    [Fact]
    public void Line_HasNumberTypeAuthorDateAndContext()
    {
        var item = ReviewLogic.ItemOf(Rev(10, 1, "דני  לוי", "א"));
        Assert.Equal("revision 3 of 8: insertion by דני לוי (2026-11-20): \"ctx\"", ReviewLogic.Line(3, 8, item, "ctx"));
        item.Date = "";
        Assert.Equal("revision 1 of 2: insertion by דני לוי: \"c\"", ReviewLogic.Line(1, 2, item, "c"));
    }

    // ---- the sitting

    [Fact]
    public void Start_NewSession_CountsKinds_ThenNextSelectsTheFirstRevision()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        Assert.Equal("review started: 5 changes (insertion 3, deletion 1, formatting 1); document: מאמר עם הערות עורך.docx; call review_changes_next", e.Start(doc));
        var next = e.Next(doc);
        Assert.Equal("revision 1 of 5: insertion by דני לוי (2026-11-20): \"…w3 w4 w5 w6 w7 w8 w9 w10 w11 w12 [הוספה א] x1 x2 x3 x4 x5 x6 x7 x8 x9 x10…\"; answer with review_changes_answer; document: מאמר עם הערות עורך.docx", next);
        Assert.Equal(0, doc.Selected);
        Assert.Equal(next, e.Next(doc));   // no answer: the same one again
    }

    [Fact]
    public void Start_WithoutRevisions_SaysSoAndKeepsNoSession()
    {
        using var s = new Sitting();
        var e = s.Engine();
        Assert.StartsWith("no tracked changes in this document", e.Start(new FakeDoc()));
        Assert.StartsWith("error: no review session", e.Next(new FakeDoc()));
    }

    [Fact]
    public void NoSession_NextAnswerAreErrors_StatusSaysNoSession()
    {
        using var s = new Sitting();
        var e = s.Engine();
        var doc = Five();
        Assert.Equal(ReviewLogic.NoSession, e.Next(doc));
        Assert.Equal(ReviewLogic.NoSession, e.Answer(["accept"], doc));
        Assert.StartsWith("review: no session; revisions in the document: 5", e.Status(doc));
        Assert.Empty(doc.Actions);
    }

    [Fact]
    public void Answer_AppliesInOrder_ReportsOutcomesRemainingAndNext_WithBackupAndUndo()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        e.Start(doc);
        var result = e.Answer(["accept", "reject", "accept"], doc);
        Assert.Equal(["accept הוספה א", "reject מחיקה ב", "accept הוספה ג"], doc.Actions);
        Assert.StartsWith("accepted; rejected; accepted; remaining 2, next: revision 4 of 5: formatting by רינה כהן (2026-11-20): \"…", result);
        Assert.EndsWith("; document: מאמר עם הערות עורך.docx; backup: C:\\b\\x.docx; undo available", result);
        Assert.Equal(1, doc.Backups);
        Assert.Equal(0, doc.Selected);   // next is formatting ד, now the first revision left
    }

    [Fact]
    public void Answer_FindsLaterRevisionsAgainAfterAcceptedDeletionShiftedThem()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        e.Start(doc);
        var r = e.Answer(["skip", "accept"], doc);   // accepting the deletion moves everything after it up by 7
        Assert.StartsWith("skipped; accepted; remaining 4, next: revision 3 of 5: insertion by רינה כהן", r);
        Assert.Equal(43, doc.Revs[1].Start);
        Assert.Equal(1, doc.Selected);
        Assert.Contains("[הוספה ג]", e.Next(doc));
        Assert.Equal(["accept מחיקה ב"], doc.Actions);
    }

    [Fact]
    public void Skip_WrapsOnce_ThenSaysAllAnsweredOrSkipped()
    {
        using var s = new Sitting();
        var doc = new FakeDoc { Revs = [Rev(10, 1, "דני", "אחת"), Rev(30, 1, "דני", "שתיים"), Rev(50, 1, "דני", "שלוש")] };
        var e = s.Engine();
        e.Start(doc);
        var r = e.Answer(["skip", "accept", "accept"], doc);
        Assert.StartsWith("skipped; accepted; accepted; remaining 1, next: revision 1 of 3: insertion by דני", r);
        Assert.Contains("; revisiting the skipped ones", e.Next(doc));
        var r2 = e.Answer(["skip"], doc);
        Assert.StartsWith("skipped; all answered or skipped; document:", r2);
        Assert.DoesNotContain("undo available", r2);   // nothing was accepted or rejected in that call
        Assert.Contains("accepted 2, rejected 0, skipped 1, remaining 0", e.Status(doc));
        Assert.StartsWith("all answered or skipped: accepted 2", e.Next(doc));
    }

    [Fact]
    public void Answer_BeyondTheLastRevision_SaysSo()
    {
        using var s = new Sitting();
        var doc = new FakeDoc { Revs = [Rev(10, 1, "דני", "אחת")] };
        var e = s.Engine();
        e.Start(doc);
        var r = e.Answer(["accept", "accept"], doc);
        Assert.StartsWith("accepted; no revision left; all answered or skipped; document:", r);
        Assert.Single(doc.Actions);
    }

    [Fact]
    public void Resume_AfterRestart_ContinuesAtTheNextUnansweredRevision_AndSaysSo()
    {
        using var s = new Sitting();
        var doc = Five();
        var first = s.Engine();
        first.Start(doc);
        first.Answer(["accept", "reject", "skip"], doc);   // 3 answered, one skipped: next is revision 4
        // new process: a new store reads the same file; Word still holds the revisions that were not answered
        var second = s.Engine();
        var start = second.Start(doc);
        Assert.StartsWith("review resumed (started ", start);
        Assert.Contains("at the next unanswered change: accepted 1, rejected 1, skipped 1, remaining 3", start);
        Assert.Contains("[עיצוב ד]", second.Next(doc));
        Assert.StartsWith("revision 4 of 5", second.Next(doc));
    }

    [Fact]
    public void Resume_AfterTheUserEditedTheDocument_DropsGoneRevisionsAndAppendsNewOnes()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        e.Start(doc);
        doc.Revs.RemoveAt(2);                                      // he handled "הוספה ג" himself in Word
        doc.Revs.Add(Rev(120, 1, "רינה כהן", "הוספה חדשה"));       // and a new one arrived
        var again = s.Engine().Start(doc);
        Assert.Contains("remaining 5", again);                     // 5 - 1 + 1
        var all = new List<string>();
        var eng = s.Engine();
        for (var i = 0; i < 5; i++) { all.Add(eng.Next(doc)); eng.Answer(["accept"], doc); }
        Assert.DoesNotContain(all, l => l.Contains("הוספה ג"));
        Assert.Contains(all, l => l.Contains("הוספה חדשה"));
    }

    [Fact]
    public void AnAnsweredRevisionThatComesBack_BecomesPendingAgain_InsteadOfDuplicating()
    {
        using var s = new Sitting();
        var doc = Five();
        var original = doc.Revs.ToList();
        var e = s.Engine();
        e.Start(doc);
        e.Answer(["accept", "reject"], doc);
        doc.Revs = original.ToList();               // the document was closed without saving: all five are back
        var again = s.Engine().Start(doc);
        Assert.Contains("remaining 5", again);
        Assert.StartsWith("revision 3 of 5", s.Engine().Next(doc));   // the pointer stays where it was; the two revived ones wait before it
        Assert.StartsWith("revision 1 of 5", s.Engine().Answer(["accept", "accept", "accept"], doc).Split("next: ")[1]);
    }

    [Fact]
    public void ReviewedDocumentsAreKeptApart_ByFullPath()
    {
        using var s = new Sitting();
        var a = Five();
        var b = new FakeDoc(@"C:\Other\second.docx") { Revs = [Rev(5, 1, "דני", "בלבד")] };
        var e = s.Engine();
        e.Start(a); e.Start(b);
        e.Answer(["accept"], a);
        Assert.Contains("remaining 1", e.Status(b));
        Assert.Contains("remaining 4", e.Status(a));
    }

    // ---- grouped

    [Fact]
    public void Bulk_RefusesWrongCount_ActsOnTheRightOne()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        var q = new ReviewLogic.BulkRequest("דני", null, 2);
        Assert.Equal("error: 3 changes match by דני, confirm with confirmed_count 3; document: מאמר עם הערות עורך.docx", e.Bulk(true, q, doc));
        Assert.Empty(doc.Actions);
        Assert.Equal(0, doc.Backups);
        var noCount = e.Bulk(true, q with { Confirmed = null }, doc);
        Assert.Contains("confirm with confirmed_count 3", noCount);
        var done = e.Bulk(true, q with { Confirmed = 3 }, doc);
        Assert.StartsWith("accepted 3 changes by דני; 2 left in the document; document: מאמר עם הערות עורך.docx; backup: C:\\b\\x.docx; undo available", done);
        Assert.Equal(["accept הוספה ה", "accept מחיקה ב", "accept הוספה א"], doc.Actions);   // last to first
        Assert.Equal(0, doc.AcceptAlls);
    }

    [Fact]
    public void Bulk_ByType_AndNoFilterUsesAcceptAllOrRejectAll()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        Assert.Contains("1 changes match of type formatting", e.Bulk(false, new(null, "formatting", 7), doc));
        Assert.StartsWith("rejected 1 change of type formatting;", e.Bulk(false, new(null, "formatting", 1), doc));
        Assert.StartsWith("rejected 4 changes;", e.Bulk(false, new(null, null, 4), doc));
        Assert.Equal(1, doc.RejectAlls);
    }

    [Fact]
    public void Bulk_NoMatch_ListsTheAuthorsPresent()
    {
        using var s = new Sitting();
        var r = s.Engine().Bulk(true, new("יוסי", null, 0), Five());
        Assert.Equal("error: 0 changes match by יוסי; authors in the document: דני לוי, רינה כהן; document: מאמר עם הערות עורך.docx", r);
    }

    [Fact]
    public void Bulk_UpdatesAnOpenSession_SoTheRestStaysPending()
    {
        using var s = new Sitting();
        var doc = Five();
        var e = s.Engine();
        e.Start(doc);
        e.Bulk(true, new("רינה", null, 2), doc);
        var st = e.Status(doc);
        Assert.Contains("accepted 2, rejected 0, skipped 0, remaining 3", st);
        Assert.StartsWith("revision 1 of 5", e.Next(doc));
    }

    [Fact]
    public void ParseBulk_ValidatesTypeAndCount()
    {
        var (q, err) = ReviewLogic.ParseBulk(J("""{"author":" דני ","type":"Format","confirmed_count":4}"""));
        Assert.Null(err);
        Assert.Equal(new ReviewLogic.BulkRequest("דני", "formatting", 4), q);
        Assert.StartsWith("error: type must be", ReviewLogic.ParseBulk(J("""{"type":"colour","confirmed_count":1}""")).Error);
        Assert.StartsWith("error: confirmed_count must be", ReviewLogic.ParseBulk(J("""{"confirmed_count":"3"}""")).Error);
        Assert.Null(ReviewLogic.ParseBulk(J("{}")).Request!.Confirmed);
    }

    // ---- registration

    [Fact]
    public void BuildTools_RegistersReviewAndRecoveryTools_WithValidSchemas()
    {
        var tools = TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, [])).ToDictionary(t => t.Name);
        foreach (var n in new[] { "review_changes_start", "review_changes_next", "review_changes_answer", "review_changes_status", "accept_all_changes", "reject_all_changes", "word_recovery" })
        {
            Assert.True(tools.ContainsKey(n), n);
            Assert.Equal("object", JsonDocument.Parse(tools[n].InputSchema).RootElement.GetProperty("type").GetString());
        }
        Assert.Equal(tools.Count, TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, [])).Select(t => t.Name).Distinct().Count());
    }

    [Fact]
    public async Task ReviewTools_RejectBadInputBeforeAnyWordCall()
    {
        var tools = TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, [])).ToDictionary(t => t.Name);
        Assert.StartsWith("error: answers is required", await tools["review_changes_answer"].RunAsync(J("{}"), default));
        Assert.StartsWith("error: type must be", await tools["accept_all_changes"].RunAsync(J("""{"type":"x"}"""), default));
    }

    [Fact]
    public void SystemPrompt_MentionsTheReviewAndRecoveryTools_AndKeepsTheSortingSentence()
    {
        foreach (var n in new[] { "review_changes_start", "review_changes_next", "review_changes_answer", "accept_all_changes", "reject_all_changes", "confirmed_count", "word_recovery", "recovery pane shown", "sort_folder_start", "undo_last_edit" })
            Assert.Contains(n, ClaudeReplyModel.SystemPrompt);
        Assert.Contains("מספיק להיום", ClaudeReplyModel.SystemPrompt);
    }
}

public class WordRecoveryTests
{
    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static UiElement El(string kind, string name, bool enabled = true) => new(kind, name, enabled, null, false, null);

    private sealed class FakeDesk : IDesktopAutomation
    {
        public bool Shown = true;
        public bool TitleBarCloseFirst;
            public bool ClickClosesPane = true;
        public string? ClickProblem;
        public bool WordWindow = true;
        public bool Unreadable;
        public List<(nint, string, string?)> Clicks = [];

        public IReadOnlyList<WindowInfo> Windows() => WordWindow ? [new WindowInfo(1, "מסמך1 - Word", 20), new WindowInfo(2, "x", 30)] : [new WindowInfo(2, "x", 30)];
        public WindowInfo? Foreground() => null;
        public string ProcessName(int pid) => pid == 20 ? "WINWORD" : "notepad";
        public bool IsMinimized(nint hwnd) => false;
        public WindowDescription? Describe(nint hwnd, int max)
        {
            if (hwnd != 1) return new("x", "notepad", [El("button", "Close")], false);
            if (Unreadable) return null;
            var els = new List<UiElement>();
            if (TitleBarCloseFirst) els.Add(El("button", "Close"));
            els.Add(El("button", "Save"));
            if (Shown) { els.Add(El("text", "Document Recovery")); els.Add(El("listitem", "דוח [Recovered]")); els.Add(El("button", "Close")); }
            return new("מסמך1 - Word", "WINWORD", els, false);
        }
        public string? Click(nint hwnd, string name, string? kind) { Clicks.Add((hwnd, name, kind)); if (ClickProblem is null && ClickClosesPane) { if (ModalAfterClick) Unreadable = true; Shown = false; } return ClickProblem; }
        public bool ModalAfterClick;
        public bool Close(nint hwnd) => true;
        public string? BringToFront(nint hwnd) => null;
        public string? PressKey(string key) => null;
    }

    private sealed class FakeHost(string? dir) : IWordRecoveryHost
    {
        public bool Running = true;
        public bool MsoWorks;
        public Action? OnMso;
        public List<string> Opened = [];
        public List<string> Mso = [];
        public (bool Running, string? Dir) Probe() => (Running, dir);
        public string OpenAutosaved(string path) { Opened.Add(path); return "AutoRecovery save of דוח.asd"; }
        public bool TryExecuteMso(string id) { Mso.Add(id); OnMso?.Invoke(); return MsoWorks; }
    }

    private static string AsdDir(TempDir d, params (string name, DateTime when)[] files)
    {
        foreach (var (name, when) in files)
        {
            var p = d.File(name);
            File.WriteAllText(p, "x");
            File.SetLastWriteTime(p, when);
        }
        return d.Path;
    }

    [Fact]
    public void ListAutosaved_FindsAsdFilesNewestFirst_IgnoresOthers()
    {
        using var d = new TempDir();
        var dir = AsdDir(d, ("old.asd", new DateTime(2026, 11, 20, 8, 0, 0)), ("sub/new.asd", new DateTime(2026, 11, 27, 21, 5, 0)), ("note.txt", new DateTime(2026, 11, 28)));
        var list = WordRecovery.ListAutosaved(dir);
        Assert.Equal(["new.asd", "old.asd"], list.Select(f => Path.GetFileName(f.Path)));
        Assert.Empty(WordRecovery.ListAutosaved(null));
        Assert.Empty(WordRecovery.ListAutosaved(Path.Combine(dir, "missing")));
    }

    [Theory]
    [InlineData("text", "Document Recovery", true)]
    [InlineData("text", "שחזור מסמכים", true)]
    [InlineData("text", "Word has recovered the following files. Save the ones you wish to keep. Available Files", true)]
    [InlineData("text", "Recovered Word has recovered files that you might want to keep.", false)]   // the Start screen's offer is not the pane
    [InlineData("button", "document recovery options", true)]
    [InlineData("listitem", "ספר [Recovered]", true)]
    [InlineData("listitem", "ספר", false)]
    [InlineData("text", "Recovery of documents", false)]
    [InlineData("text", "", false)]
    public void IsPaneMarker_RecognisesTheTitleAndTheListEntries(string kind, string name, bool expected) =>
        Assert.Equal(expected, WordRecovery.IsPaneMarker(El(kind, name)));

    [Fact]
    public void Detect_CombinesTheTwoSignals_AndFormatsTheCheck()
    {
        var files = new List<AutosaveFile> { new("/r/a.asd", new DateTime(2026, 11, 26, 9, 0, 0)), new("/r/b.asd", new DateTime(2026, 11, 27, 21, 5, 0)) };
        var shown = WordRecovery.Detect("x", new FakeDesk(), _ => files);
        Assert.Equal(true, shown.PaneShown);
        Assert.Equal("recovery pane: shown; autosaved files: 2 (newest \"b.asd\", 2026-11-27 21:05)", WordRecovery.Format(shown));
        Assert.Equal("recovery pane: not shown; autosaved files: 0", WordRecovery.Format(WordRecovery.Detect("x", new FakeDesk { Shown = false }, _ => [])));
        Assert.Null(WordRecovery.Detect("x", null, _ => []).PaneShown);
        Assert.StartsWith("recovery pane: unknown", WordRecovery.Format(WordRecovery.Detect("x", null, _ => [])));
        Assert.Null(WordRecovery.PaneVisible(new FakeDesk { WordWindow = false }));   // no Word window: nothing to look at
    }

    [Fact]
    public void Note_AddsTheSentenceOnlyWhenTheProbeSaysShown_AndSurvivesAFailingProbe()
    {
        Assert.Equal("; recovery pane shown", WordRecovery.Note(() => true));
        Assert.Equal("", WordRecovery.Note(() => false));
        Assert.Equal("", WordRecovery.Note(null));
        Assert.Equal("", WordRecovery.Note(() => throw new InvalidOperationException()));
        Assert.Null(WordRecovery.PaneProbe(null));
        Assert.True(WordRecovery.PaneProbe(new FakeDesk())!());
    }

    [Fact]
    public async Task DescribeOfficeDialogs_MentionsTheRecoveryPane()
    {
        var tool = new DescribeOfficeDialogsTool(() => [], () => true);
        Assert.Equal("no Office dialogs are open; recovery pane shown", await tool.RunAsync(J("{}"), default));
        Assert.Equal("no Office dialogs are open", await new DescribeOfficeDialogsTool(() => [], () => false).RunAsync(J("{}"), default));
    }

    [Fact]
    public void KeepFilesAnswer_FindsTheYesOptionAndOkOfWordsQuestion()
    {
        var question = new WindowDescription("Microsoft Word", "WINWORD",
            [El("text", "The Document Recovery task pane contains some recovered files that have not been opened. Would you like to view these files the next time you start Word?"),
             El("radio", "&Yes, I want to view these files later"), El("radio", "&No, remove the files"), El("button", "OK"), El("button", "Cancel")], false);
        Assert.Equal(("Yes, I want to view these files later", "OK"), WordRecovery.KeepFilesAnswer(question));
        Assert.Null(WordRecovery.KeepFilesAnswer(new("Document1 - Word", "WINWORD", [El("text", "Word has recovered the following files"), El("button", "Close")], false)));
        Assert.Null(WordRecovery.KeepFilesAnswer(question with { Elements = question.Elements.Where(e => e.Kind != "radio").ToList() }));
    }

    [Fact]
    public void LooksLikeKeepQuestion_ByTitleOwnerAndReadableText()
    {
        var q = new WindowInfo(1, "Microsoft Word", 7);
        Assert.True(WordRecovery.LooksLikeKeepQuestion(q, true, []));                       // NUIDialog: nothing readable, title and owner decide
        Assert.True(WordRecovery.LooksLikeKeepQuestion(q, true, [("Static", "The Document Recovery task pane contains some recovered files")]));
        Assert.False(WordRecovery.LooksLikeKeepQuestion(q, true, [("Static", "Do you want to save changes?")]));
        Assert.False(WordRecovery.LooksLikeKeepQuestion(q, false, []));
        Assert.False(WordRecovery.LooksLikeKeepQuestion(new WindowInfo(1, "Document1 - Word", 7), true, []));
        Assert.False(WordRecovery.LooksLikeKeepQuestion(null, true, []));
    }

    [Fact]
    public void CloseButton_RefusesTheTitleBarCloseThatComesBeforeThePane()
    {
        var ok = new WindowDescription("w", "WINWORD", [El("button", "Save"), El("text", "Document Recovery"), El("button", "סגור")], false);
        Assert.Equal(("סגור", null), WordRecovery.CloseButton(ok));
        var titleBar = ok with { Elements = [El("button", "Close"), .. ok.Elements] };
        Assert.Contains("Word's own window", WordRecovery.CloseButton(titleBar).Problem);
        Assert.Contains("no Close button was found", WordRecovery.CloseButton(ok with { Elements = ok.Elements.Take(2).ToList() }).Problem);
        Assert.Contains("cut short", WordRecovery.CloseButton(ok with { Elements = ok.Elements.Take(2).ToList(), Truncated = true }).Problem);
        Assert.Contains("not found", WordRecovery.CloseButton(new("w", "WINWORD", [El("button", "Close")], false)).Problem);
        Assert.Contains("no Close", WordRecovery.CloseButton(ok with { Elements = [.. ok.Elements.Take(2), El("button", "Close", enabled: false)] }).Problem);
    }

    [Fact]
    public void CloseButton_OnTheVmsRealOrder_ClicksThePanesCloseNotTheTitleBars()
    {
        var real = new WindowDescription("Document1 - Word", "WINWORD",
            [El("text", "Word has recovered the following files. Save the ones you wish to keep. Available Files"), El("link", "Which file do I want to save?"), El("button", "Close"),
             El("listitem", "x.docx"), El("button", "Minimize"), El("button", "Close")], false);
        Assert.Equal(("Close", null), WordRecovery.CloseButton(real));
        Assert.True(WordRecovery.PaneShown(real));
    }

    [Fact]
    public void Run_Check_ReportsPaneAndFiles_NotRunningIsAnError()
    {
        using var d = new TempDir();
        var host = new FakeHost(AsdDir(d, ("a.asd", new DateTime(2026, 11, 27, 21, 5, 0))));
        Assert.Equal("recovery pane: shown; autosaved files: 1 (newest \"a.asd\", 2026-11-27 21:05)", WordRecovery.Run("check", host, new FakeDesk()));
        Assert.Equal("error: Word is not running", WordRecovery.Run("check", new FakeHost(null) { Running = false }, new FakeDesk()));
    }

    [Fact]
    public void Run_Open_OpensTheNewestAutosavedFileAsANewDocument()
    {
        using var d = new TempDir();
        var host = new FakeHost(AsdDir(d, ("old.asd", new DateTime(2026, 11, 20)), ("new.asd", new DateTime(2026, 11, 27, 21, 5, 0))));
        var r = WordRecovery.Run("open", host, new FakeDesk());
        Assert.Equal([Path.Combine(d.Path, "new.asd")], host.Opened);
        Assert.Equal("opened the autosaved file \"new.asd\" (2026-11-27 21:05) as a new document \"AutoRecovery save of דוח.asd\"; it is a separate read-only document; the original file and the autosaved file were not changed", r);
        Assert.StartsWith("error: no autosaved files found", WordRecovery.Run("open", new FakeHost(d.Path + "/nothing"), new FakeDesk()));
    }

    [Fact]
    public void Run_Close_ClicksThePanesCloseButton_VerifiesAndLeavesAsdFilesAlone()
    {
        using var d = new TempDir();
        var dir = AsdDir(d, ("a.asd", new DateTime(2026, 11, 27)));
        var desk = new FakeDesk();
        var r = WordRecovery.Run("close", new FakeHost(dir), desk, _ => { });
        Assert.Equal([(1, "Close", "button")], desk.Clicks.Select(c => ((int)c.Item1, c.Item2, c.Item3)));
        Assert.Equal("recovery pane closed (UI Automation); verified: not shown; autosaved files left untouched: 1", r);
        Assert.True(File.Exists(Path.Combine(dir, "a.asd")));
        Assert.StartsWith("recovery pane: not shown; nothing to close", WordRecovery.Run("close", new FakeHost(dir), new FakeDesk { Shown = false }, _ => { }));
    }

    [Fact]
    public void Run_Close_FallsBackToExecuteMso_WhenTheClickDoesNotWork_AndFailsHonestlyOtherwise()
    {
        using var d = new TempDir();
        var dir = AsdDir(d, ("a.asd", new DateTime(2026, 11, 27)));
        var desk = new FakeDesk { ClickClosesPane = false };
        var host = new FakeHost(dir) { MsoWorks = true, OnMso = () => desk.Shown = false };
        var r = WordRecovery.Run("close", host, desk, _ => { });
        Assert.Equal(["DocumentRecoveryClose"], host.Mso);
        Assert.StartsWith("recovery pane closed (CommandBars)", r);

        var stuck = new FakeDesk { ClickProblem = "control \"Close\" not found" };
        var h2 = new FakeHost(dir);
        var r2 = WordRecovery.Run("close", h2, stuck, _ => { });
        Assert.StartsWith("error: the recovery pane is still shown or unconfirmed (control \"Close\" not found)", r2);
        Assert.Equal(["DocumentRecoveryClose"], h2.Mso);
    }

    [Fact]
    public void Run_Close_DoesNotClaimSuccess_WhenWordsWindowGoesUnreadable_AfterTheClick()
    {
        using var d = new TempDir();
        var dir = AsdDir(d, ("a.asd", new DateTime(2026, 11, 27)));
        var desk = new FakeDesk { ModalAfterClick = true };   // Word's "keep the recovered files?" question is up
        var r = WordRecovery.Run("close", new FakeHost(dir), desk, _ => { });
        Assert.StartsWith("error: the recovery pane is still shown or unconfirmed", r);
        Assert.Contains("nothing was pressed", r);
        Assert.Null(WordRecovery.PaneVisible(desk));
        Assert.True(File.Exists(Path.Combine(dir, "a.asd")));
    }

    [Fact]
    public void Run_Close_NeverClicksWhenTheFirstCloseButtonIsWordsOwn()
    {
        using var d = new TempDir();
        var desk = new FakeDesk { TitleBarCloseFirst = true };
        var r = WordRecovery.Run("close", new FakeHost(AsdDir(d)), desk, _ => { });
        Assert.Empty(desk.Clicks);
        Assert.Contains("Word's own window", r);
        Assert.StartsWith("error: the recovery pane is still shown or unconfirmed", r);
    }

    [Fact]
    public void Run_Close_WithoutAutomation_CannotInspect()
    {
        var r = WordRecovery.Run("close", new FakeHost(null), null, _ => { });
        Assert.StartsWith("error: cannot inspect Word's windows", r);
    }

    [Fact]
    public async Task Tool_ValidatesTheAction_AndRunsThroughTheHost()
    {
        var tool = new WordRecoveryTool(new FakeDesk(), new FakeHost("/nonexistent"));
        Assert.StartsWith("error: action must be", await tool.RunAsync(J("""{"action":"delete"}"""), default));
        Assert.Equal("recovery pane: shown; autosaved files: 0", await tool.RunAsync(J("""{"action":"check"}"""), default));
        Assert.Equal(WordRecovery.WdAutoRecoverPath, 5);
    }
}
