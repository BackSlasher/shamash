using System.Text.Json;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class SortTests
{
    private sealed class Env : IDisposable
    {
        public TempDir Dir = new();
        public string Desktop, Salad, Journal, SessionFile;
        public OrganizeStore Organize;
        public Env()
        {
            Desktop = Path.Combine(Dir.Path, "Desktop");
            Salad = Path.Combine(Desktop, "סלט למיון");
            Directory.CreateDirectory(Salad);
            Journal = Path.Combine(Dir.Path, "moves.log");
            SessionFile = Path.Combine(Dir.Path, "sort-sessions.json");
            Organize = new OrganizeStore(Journal);
        }
        public SortStore Store(int max = 3000) => new(SessionFile, Organize, [Desktop], max);
        public string Write(string rel, string text, DateTime? when = null)
        {
            var p = Path.Combine(Salad, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
            if (when is not null) File.SetLastWriteTimeUtc(p, when.Value);
            return p;
        }
        public void Dispose() => Dir.Dispose();
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static string Obj(params (string, object)[] kv) => JsonSerializer.Serialize(kv.ToDictionary(x => x.Item1, x => x.Item2));
    private static string StartIn(SortStore s, Env e) => s.Start(J(Obj(("folder", e.Salad))));
    private static string Line(string result, string key) => result.Split('\n').First(l => l.StartsWith(key + ": ", StringComparison.Ordinal))[(key.Length + 2)..];
    private static readonly DateTime T0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Start_CreatesSession_PersistsJson_AndResumes()
    {
        using var e = new Env();
        e.Write("a.txt", "אחד", T0);
        e.Write("b.txt", "שניים", T0.AddDays(1));
        e.Write("c.txt", "שלושה", T0.AddDays(2));
        Directory.CreateDirectory(Path.Combine(e.Desktop, "מאמרים"));
        var s1 = e.Store();
        var r = StartIn(s1, e);
        Assert.Equal("s1", Line(r, "session_id"));
        Assert.Equal("3", Line(r, "remaining"));
        Assert.Contains("מאמרים", Line(r, "destination folders"));
        s1.Next(J("""{"session_id":"s1"}"""));
        s1.Move(J(Obj(("session_id", "s1"), ("destination", "מאמרים"))));
        Assert.True(File.Exists(e.SessionFile));
        var json = JsonDocument.Parse(File.ReadAllText(e.SessionFile)).RootElement;
        Assert.Equal("s1", json.GetProperty("sessions")[0].GetProperty("id").GetString());
        Assert.Equal("done", json.GetProperty("sessions")[0].GetProperty("decided")[0].GetProperty("status").GetString());

        var s2 = e.Store(); // restart
        var r2 = StartIn(s2, e);
        Assert.Contains("resumed", r2);
        Assert.Equal("s1", Line(r2, "session_id"));
        Assert.Equal("2", Line(r2, "remaining"));
        Assert.Equal("1", Line(r2, "done"));
        Assert.Equal("3", Line(r2, "total"));
    }

    [Fact]
    public void Next_NewestFirst_WithExcerpts_AndUnreadableFormat()
    {
        using var e = new Env();
        e.Write("old.txt", "הטקסט הישן", T0);
        DocumentToolTests.MakeDocx(Path.Combine(e.Salad, "mid.docx"), "מכתב לעירייה", "שורה שנייה");
        File.SetLastWriteTimeUtc(Path.Combine(e.Salad, "mid.docx"), T0.AddDays(5));
        e.Write("new.doc", "binary", T0.AddDays(9));
        var s = e.Store();
        StartIn(s, e);
        var n1 = s.Next(J("""{"session_id":"s1"}"""));
        Assert.Equal("new.doc", Line(n1, "name"));
        Assert.Contains("cannot read .doc", Line(n1, "excerpt"));
        Assert.Equal(n1, s.Next(J("""{"session_id":"s1"}"""))); // same item until decided
        s.Skip(J("""{"session_id":"s1"}"""));
        var n2 = s.Next(J("""{"session_id":"s1"}"""));
        Assert.Equal("mid.docx", Line(n2, "name"));
        Assert.Contains("מכתב לעירייה", Line(n2, "excerpt"));
        s.Skip(J("""{"session_id":"s1"}"""));
        var n3 = s.Next(J("""{"session_id":"s1"}"""));
        Assert.Contains("הטקסט הישן", Line(n3, "excerpt"));
    }

    [Fact]
    public void Move_JournalsDuplicatesToKfulim_AndNeverOverwrites()
    {
        using var e = new Env();
        var main = e.Write("x.txt", "same content here", T0.AddDays(3));
        e.Write("sub/x copy.txt", "same content here", T0);
        Directory.CreateDirectory(Path.Combine(e.Desktop, "מאמרים"));
        File.WriteAllText(Path.Combine(e.Desktop, "מאמרים", "x.txt"), "different existing");
        var s = e.Store();
        StartIn(s, e);
        var n = s.Next(J("""{"session_id":"s1"}"""));
        Assert.Equal("x.txt", Line(n, "name"));
        Assert.Contains("1 exact copies", Line(n, "duplicates"));
        var r = s.Move(J(Obj(("session_id", "s1"), ("destination", "מאמרים"))));
        Assert.Contains("duplicates moved to כפולים: 1", r);
        Assert.False(File.Exists(main));
        Assert.Equal("different existing", File.ReadAllText(Path.Combine(e.Desktop, "מאמרים", "x.txt")));
        Assert.Equal("same content here", File.ReadAllText(Path.Combine(e.Desktop, "מאמרים", "x (2).txt")));
        Assert.True(File.Exists(Path.Combine(e.Desktop, "כפולים", "x copy.txt")));
        var journal = File.ReadAllLines(e.Journal);
        Assert.Equal(2, journal.Length);
        Assert.All(journal, l => Assert.StartsWith("s1#1", l.Split('\t')[1]));
        Assert.Equal("0", Line(r, "remaining"));
    }

    [Fact]
    public void Move_RequiresExistingOrCreate_AndStaysUnderTarget()
    {
        using var e = new Env();
        e.Write("a.txt", "a");
        var s = e.Store();
        StartIn(s, e);
        Assert.StartsWith("error: no current file", s.Move(J(Obj(("session_id", "s1"), ("destination", "x")))));
        s.Next(J("""{"session_id":"s1"}"""));
        Assert.Contains("does not exist", s.Move(J(Obj(("session_id", "s1"), ("destination", "חדש")))));
        Assert.StartsWith("error: destination must be under", s.Move(J(Obj(("session_id", "s1"), ("destination", "../../out"), ("create_if_missing", true)))));
        var r = s.Move(J(Obj(("session_id", "s1"), ("destination", "חדש"), ("create_if_missing", true))));
        Assert.StartsWith("moved:", r);
        Assert.True(File.Exists(Path.Combine(e.Desktop, "חדש", "a.txt")));
    }

    [Fact]
    public void Skip_AsksAgainNextSitting_ExceptNever()
    {
        using var e = new Env();
        e.Write("a.txt", "a", T0);
        e.Write("b.txt", "b", T0.AddDays(1));
        var s = e.Store();
        StartIn(s, e);
        s.Next(J("""{"session_id":"s1"}""")); // b
        var r = s.Skip(J("""{"session_id":"s1","reason":"later"}"""));
        Assert.Equal("1", Line(r, "skipped"));
        Assert.Equal("1", Line(r, "remaining"));
        s.Next(J("""{"session_id":"s1"}""")); // a
        s.Skip(J("""{"session_id":"s1","reason":"never"}"""));
        Assert.StartsWith("nothing left", s.Next(J("""{"session_id":"s1"}""")));
        var r2 = StartIn(e.Store(), e); // next sitting
        Assert.Equal("1", Line(r2, "remaining")); // b is back, a never
        Assert.Equal("1", Line(r2, "skipped"));
        Assert.Equal("b.txt", Line(e.Store().Next(J("""{"session_id":"s1"}""")), "name"));
    }

    [Fact]
    public void Status_ByIdAndFolder_ShowsLastMoves()
    {
        using var e = new Env();
        for (var i = 0; i < 7; i++) e.Write($"f{i}.txt", "t" + i, T0.AddDays(i));
        var s = e.Store();
        StartIn(s, e);
        for (var i = 0; i < 7; i++)
        {
            s.Next(J("""{"session_id":"s1"}"""));
            s.Move(J(Obj(("session_id", "s1"), ("destination", "ד"), ("create_if_missing", true))));
        }
        var st = s.Status(J("""{"session_id":"s1"}"""));
        Assert.Equal("7", Line(st, "done"));
        Assert.Equal(5, st.Split('\n').Count(l => l.Contains("-> ד")));
        Assert.Contains("f0.txt", st.Split("last moves:")[1].Split('\n')[1]);
        Assert.Equal(st, s.Status(J(Obj(("folder", e.Salad)))));
        Assert.StartsWith("error", s.Status(J("""{"session_id":"nope"}""")));
    }

    [Fact]
    public void UndoLastMove_RestoresFileAndDuplicates_ThenStepsBack_AndRefusesTakenPlace()
    {
        using var e = new Env();
        var a = e.Write("a.txt", "dup", T0.AddDays(2));
        var b = e.Write("b.txt", "dup", T0.AddDays(1));
        var c = e.Write("c.txt", "uniq", T0);
        var s = e.Store();
        StartIn(s, e);
        var undo = SortStore.CreateTools([e.Desktop], 3000, s).Single(t => t.Name == "undo_last_move");
        string Undo() => undo.RunAsync(J("{}"), default).GetAwaiter().GetResult();
        s.Next(J("""{"session_id":"s1"}""")); // a (dup b)
        s.Move(J(Obj(("session_id", "s1"), ("destination", "ד"), ("create_if_missing", true))));
        s.Next(J("""{"session_id":"s1"}""")); // c
        s.Move(J(Obj(("session_id", "s1"), ("destination", "ד"), ("create_if_missing", true))));
        Assert.Contains("restored: 1", Undo());
        Assert.True(File.Exists(c));
        File.WriteAllText(a, "taken"); // second undo: a's place is taken? no, a was moved; recreate a to block
        Assert.Contains("original place", Undo());
        File.Delete(a);
        Assert.Contains("restored: 2", Undo());
        Assert.True(File.Exists(a) && File.Exists(b));
        Assert.Contains("nothing to undo", Undo());
        // the session sees all three as pending again
        Assert.Equal("3", Line(s.Status(J("""{"session_id":"s1"}""")), "remaining"));
    }

    [Fact]
    public void UndoLastMove_RefusesPlanJournal_AndNoSession()
    {
        using var e = new Env();
        var x = Path.Combine(e.Salad, "x.txt"); File.WriteAllText(x, "1");
        var y = Path.Combine(e.Desktop, "y.txt"); File.WriteAllText(y, "2");
        var moved = Path.Combine(e.Desktop, "moved.txt");
        File.Move(y, moved);
        File.WriteAllText(e.Journal, $"2026-11-27 10:00:00\tp1\t{y}\t{moved}\n");
        var st = e.Store();
        Assert.Equal("error: the last moves were made by a deduplication plan; use undo_plan", st.UndoLast(J("{}")));
        Assert.True(File.Exists(moved) && !File.Exists(y));
        // a sorting-form tag with no such session
        File.AppendAllText(e.Journal, $"2026-11-27 10:01:00\ts7#1\t{y}\t{moved}\n");
        Assert.Equal("error: no sorting move to undo", st.UndoLast(J("{}")));
        Assert.True(File.Exists(moved) && !File.Exists(y));
    }

    [Fact]
    public void FileLimit_RefusesBigFolders_InSortAndFindDuplicates()
    {
        using var e = new Env();
        for (var i = 0; i < 4; i++) e.Write($"f{i}.txt", "x" + i);
        var r = StartIn(e.Store(max: 3), e);
        Assert.StartsWith("error: folder has more than 3 files", r);
        Assert.False(File.Exists(e.SessionFile) && JsonDocument.Parse(File.ReadAllText(e.SessionFile)).RootElement.GetProperty("sessions").GetArrayLength() > 0);
        Assert.StartsWith("ok", "ok" + StartIn(e.Store(max: 4), e)); // exactly at the limit is fine
        var find = OrganizeStore.CreateTools([e.Desktop], e.Organize, 3)[0];
        var fr = find.RunAsync(J(Obj(("folder", e.Salad))), default).GetAwaiter().GetResult();
        Assert.StartsWith("error: folder has more than 3 files", fr);
        Assert.DoesNotContain("error", OrganizeStore.CreateTools([e.Desktop], e.Organize, 4)[0].RunAsync(J(Obj(("folder", e.Salad))), default).GetAwaiter().GetResult());
    }

    [Fact]
    public void LockFilesAndShortcuts_AreIgnored_InSessionsDuplicatesAndSearch()
    {
        using var e = new Env();
        e.Write("real.txt", "keyword here");
        e.Write("~$real.docx", "keyword here");
        e.Write("link-keyword.lnk", "x");
        var s = e.Store();
        var r = StartIn(s, e);
        Assert.Equal("1", Line(r, "total"));
        Assert.Equal("real.txt", Line(s.Next(J("""{"session_id":"s1"}""")), "name"));
        var find = OrganizeStore.CreateTools([e.Desktop], e.Organize)[0];
        Assert.Contains("scanned: 1 files", find.RunAsync(J(Obj(("folder", e.Salad))), default).GetAwaiter().GetResult());
        string[] names = ["real.txt", "~$real.docx", "link-keyword.lnk"];
        var search = new SearchDocumentsTool([e.Desktop], new ListIndex(names.Select(n => new DocumentHit(n, Path.Combine(e.Desktop, n), T0, 10)).ToList()));
        var sr = search.RunAsync(J("""{"query":"keyword"}"""), default).GetAwaiter().GetResult();
        Assert.Contains("real.txt", sr);
        Assert.DoesNotContain("~$", sr);
        Assert.DoesNotContain(".lnk", sr);
    }

    [Fact]
    public void SearchRanking_Docx_BeforeDocRtfPdf_WhenDatesAreClose()
    {
        var d = new DateTime(2026, 11, 20);
        var hits = SearchDocumentsTool.Rank(
        [
            new DocumentHit("a.pdf", "/r/a.pdf", d.AddDays(3), 1),
            new DocumentHit("b.doc", "/r/b.doc", d.AddDays(2), 1),
            new DocumentHit("c.docx", "/r/c.docx", d, 1),
            new DocumentHit("old.docx", "/r/old.docx", d.AddDays(-400), 1),
            new DocumentHit("n.txt", "/r/n.txt", d.AddDays(1), 1),
        ]);
        Assert.Equal(["c.docx", "n.txt", "a.pdf", "b.doc", "old.docx"], hits.Select(h => h.Name));
    }

    private static string Words(string seed, int n) => string.Join(' ', Enumerable.Range(0, n).Select(i => $"{seed}{i}"));

    [Fact]
    public void Next_ReportsExactCloneElsewhere()
    {
        using var e = new Env();
        e.Write("a.txt", "the very same text of a letter", T0);
        var other = Path.Combine(e.Desktop, "מכתבים", "orig.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "the very same text of a letter");
        File.WriteAllText(Path.Combine(e.Desktop, "מכתבים", "unrelated.txt"), "something else entirely");
        var s = e.Store();
        StartIn(s, e);
        var n = s.Next(J("""{"session_id":"s1"}"""));
        Assert.Equal("Desktop/מכתבים/orig.txt", Line(n, "identical copies elsewhere"));
    }

    [Fact]
    public void Next_ReportsNearDuplicateElsewhere_NotUnrelated()
    {
        using var e = new Env();
        var text = Words("w", 200);
        e.Write("draft.txt", text + " tail one");
        var dir = Path.Combine(e.Desktop, "מאמרים");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "final.txt"), text + " tail two here");
        File.WriteAllText(Path.Combine(dir, "other.txt"), Words("q", 200));
        var s = e.Store();
        StartIn(s, e);
        var n = s.Next(J("""{"session_id":"s1"}"""));
        Assert.Equal("none", Line(n, "identical copies elsewhere"));
        var near = Line(n, "similar versions elsewhere");
        Assert.Contains("Desktop/מאמרים/final.txt", near);
        Assert.DoesNotContain("other.txt", near);
    }

    [Fact]
    public void Next_RanksFoldersBySimilarContent()
    {
        using var e = new Env();
        e.Write("new.txt", "jerusalem ottoman period taxation records jerusalem ottoman archive taxation jerusalem register ottoman");
        var a = Path.Combine(e.Desktop, "היסטוריה"); var b = Path.Combine(e.Desktop, "בישול");
        Directory.CreateDirectory(a); Directory.CreateDirectory(b);
        File.WriteAllText(Path.Combine(a, "1.txt"), "ottoman jerusalem taxation archive records of the ottoman period in jerusalem register");
        File.WriteAllText(Path.Combine(a, "2.txt"), "jerusalem ottoman history taxation");
        File.WriteAllText(Path.Combine(b, "1.txt"), "flour sugar butter recipe baking oven cookies recipe baking");
        var s = e.Store();
        StartIn(s, e);
        var folders = Line(s.Next(J("""{"session_id":"s1"}""")), "folders with similar content");
        Assert.StartsWith("היסטוריה (", folders);
        Assert.DoesNotContain("בישול", folders);
    }

    [Fact]
    public void EvidenceCache_IsPersisted_AndReusedOnResume_UntilTheFileChanges()
    {
        using var e = new Env();
        e.Write("a.txt", Words("z", 100), T0);
        var dir = Path.Combine(e.Desktop, "x"); Directory.CreateDirectory(dir);
        var clone = Path.Combine(dir, "clone.txt");
        File.WriteAllText(clone, Words("z", 100));
        var s1 = e.Store();
        StartIn(s1, e);
        Assert.Contains("clone.txt", s1.Next(J("""{"session_id":"s1"}""")));
        Assert.True(s1.HashComputations >= 2);
        Assert.True(s1.ProfileComputations >= 1);
        var s2 = e.Store(); // restart: cache comes from the JSON
        StartIn(s2, e);
        Assert.Contains("clone.txt", s2.Next(J("""{"session_id":"s1"}""")));
        Assert.Equal(0, s2.HashComputations);
        Assert.Equal(0, s2.ProfileComputations);
        File.WriteAllText(clone, Words("z", 100)); File.SetLastWriteTimeUtc(clone, T0.AddDays(40)); // changed mtime
        var s3 = e.Store();
        StartIn(s3, e);
        s3.Next(J("""{"session_id":"s1"}"""));
        Assert.Equal(1, s3.HashComputations);
    }

    [Fact]
    public void Settings_MaxOrganizeFiles_AndToolsRegistered()
    {
        var s = ShamashSettings.Load(k => k switch { "SHAMASH_MAX_ORGANIZE_FILES" => "50", _ => null }, []);
        Assert.Equal(50, s.MaxOrganizeFiles);
        Assert.Equal(ShamashSettings.DefaultMaxOrganizeFiles, ShamashSettings.Load(_ => null, []).MaxOrganizeFiles);
        var names = TurnPipeline.BuildTools(s).Select(t => t.Name).ToList();
        foreach (var n in new[] { "sort_folder_start", "sort_folder_next", "sort_folder_move", "sort_folder_skip", "sort_folder_status", "undo_last_move" })
            Assert.Contains(n, names);
        Assert.Contains("sort_folder_start", ClaudeReplyModel.SystemPrompt);
    }

    private sealed class ListIndex(IReadOnlyList<DocumentHit> hits) : IDocumentIndex
    {
        public IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct) => hits;
    }

    [Fact]
    public void Start_TwoRoots_AcceptsRootNameAndHebrewAliasForFolderAndTarget()
    {
        using var d = new TempDir();
        var desk = Path.Combine(d.Path, "Desktop");
        var docs = Path.Combine(d.Path, "Documents");
        Directory.CreateDirectory(desk);
        Directory.CreateDirectory(Path.Combine(docs, "sub"));
        File.WriteAllText(Path.Combine(docs, "sub", "x.txt"), "hello");
        foreach (var f in new[] { "Documents/sub", "מסמכים/sub" })
        {
            var store = new SortStore(Path.Combine(d.Path, "s-" + Guid.NewGuid().ToString("N") + ".json"), new OrganizeStore(Path.Combine(d.Path, "m.log")), [desk, docs], 3000);
            var r = store.Start(J(Obj(("folder", f), ("target_root", "מסמכים/sub"))));
            Assert.DoesNotContain("error:", r);
            Assert.Equal(Path.Combine(docs, "sub"), store.ResolveTarget("Documents/sub"));
            Assert.Equal(docs, store.ResolveTarget("מסמכים"));
        }
    }
}
