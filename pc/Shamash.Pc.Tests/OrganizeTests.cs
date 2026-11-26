using System.Text.Json;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class OrganizeTests
{
    private sealed class Env : IDisposable
    {
        public TempDir Root = new();
        public TempDir Outside = new();
        public string Journal;
        public List<string> Opened = [];
        public OrganizeStore Store;
        public IAgentTool Find, Apply, Undo;
        public Env()
        {
            Journal = Path.Combine(Root.Path, "..", "journal-" + Guid.NewGuid().ToString("N") + ".log");
            Journal = Path.GetFullPath(Journal);
            Store = new OrganizeStore(Journal, Opened.Add);
            var tools = OrganizeStore.CreateTools([Root.Path], Store);
            Find = tools[0]; Apply = tools[1]; Undo = tools[2];
        }
        public string Write(string rel, string text, DateTime? when = null)
        {
            var p = Root.File(rel);
            File.WriteAllText(p, text);
            if (when is not null) File.SetLastWriteTimeUtc(p, when.Value);
            return p;
        }
        public static string Call(IAgentTool t, string json) => t.RunAsync(JsonDocument.Parse(json).RootElement, default).GetAwaiter().GetResult();
        public string FindIn(string folder) => Call(Find, JsonSerializer.Serialize(new { folder }));
        public void Dispose() { Root.Dispose(); Outside.Dispose(); try { File.Delete(Journal); } catch (IOException) { } }
    }

    private static string Para(string seed, int n) => string.Join(' ', Enumerable.Range(0, n).Select(i => $"{seed}{i}"));

    [Fact]
    public void ExactDuplicatesGrouped_KeeperIsNewest()
    {
        using var e = new Env();
        var old = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        e.Write("a/מאמר.txt", "זה טקסט ארוך מספיק של מאמר", old);
        var newest = e.Write("a/מאמר עותק.txt", "זה טקסט ארוך מספיק של מאמר", old.AddDays(5));
        e.Write("b/other.txt", "completely different words here for sure", old);
        var r = e.FindIn(e.Root.Path);
        Assert.Contains("groups: 1", r);
        Assert.Contains("duplicate files: 1", r);
        Assert.Contains("exact", r);
        Assert.Contains("keep: " + Path.Combine("a", "מאמר עותק.txt"), r);
        Assert.Single(e.Opened);
        Assert.StartsWith("תוכנית סידור", Path.GetFileName(e.Opened[0]));
        Assert.True(File.Exists(newest));
    }

    [Fact]
    public void KeeperTie_PrefersNameWithoutCopyMarker()
    {
        using var e = new Env();
        var t = new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        e.Write("x Copy.txt", "same content in both files here", t);
        var good = e.Write("x.txt", "same content in both files here", t);
        var r = e.FindIn(e.Root.Path);
        Assert.Contains("keep: x.txt |", r);
        Assert.True(File.Exists(good));
    }

    [Fact]
    public void NearDuplicateGrouped_UnrelatedNot()
    {
        using var e = new Env();
        var full = string.Join("\n", Enumerable.Range(0, 10).Select(i => Para("w" + i + "_", 12)));
        var missing = string.Join("\n", Enumerable.Range(0, 10).Where(i => i != 5).Select(i => Para("w" + i + "_", 12)));
        var other = string.Join("\n", Enumerable.Range(0, 10).Select(i => Para("zz" + i + "_", 12)));
        e.Write("paper.txt", full, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        e.Write("paper v2.txt", missing, new DateTime(2021, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        e.Write("unrelated.txt", other);
        var r = e.FindIn(e.Root.Path);
        Assert.Contains("groups: 1", r);
        Assert.Contains("near", r);
        Assert.Contains("keep: paper v2.txt", r);
        Assert.DoesNotContain("unrelated.txt", r);
    }

    [Fact]
    public void Apply_MovesJournals_NoOverwrite_RefusesSecondApply_AndUndoRestores()
    {
        using var e = new Env();
        var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var keep = e.Write("sub/a.txt", "alpha beta gamma delta epsilon", t.AddDays(1));
        var dup = e.Write("sub/a copy.txt", "alpha beta gamma delta epsilon", t);
        // pre-existing file in the target forces the " (2)" rename
        var clash = e.Write("כפולים/sub/a copy.txt", "something else entirely");
        var r = e.FindIn(e.Root.Path);
        var id = r.Split('\n')[0].Split(' ')[1];
        var a = Env.Call(e.Apply, $$"""{"plan_id":"{{id}}"}""");
        Assert.Contains("moved: 1", a);
        Assert.False(File.Exists(dup));
        Assert.True(File.Exists(keep));
        Assert.Equal("something else entirely", File.ReadAllText(clash));
        Assert.True(File.Exists(Path.Combine(e.Root.Path, "כפולים", "sub", "a copy (2).txt")));
        var line = File.ReadAllLines(e.Journal).Single().Split('\t');
        Assert.Equal(4, line.Length);
        Assert.Equal(id, line[1]);
        Assert.Equal(dup, line[2]);
        Assert.StartsWith("error", Env.Call(e.Apply, $$"""{"plan_id":"{{id}}"}"""));
        Assert.StartsWith("error", Env.Call(e.Apply, """{"plan_id":"p99"}"""));

        var u = Env.Call(e.Undo, $$"""{"plan_id":"{{id}}"}""");
        Assert.Contains("restored: 1", u);
        Assert.True(File.Exists(dup));
        Assert.Equal("alpha beta gamma delta epsilon", File.ReadAllText(dup));
        Assert.StartsWith("error", Env.Call(e.Undo, $$"""{"plan_id":"{{id}}"}"""));
    }

    [Fact]
    public void Undo_SkipsWhenOriginalPlaceTaken()
    {
        using var e = new Env();
        var t = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        e.Write("k.txt", "one two three four five six", t.AddDays(1));
        var dup = e.Write("d.txt", "one two three four five six", t);
        var id = e.FindIn(e.Root.Path).Split('\n')[0].Split(' ')[1];
        Env.Call(e.Apply, $$"""{"plan_id":"{{id}}"}""");
        File.WriteAllText(dup, "new file");
        var u = Env.Call(e.Undo, $$"""{"plan_id":"{{id}}"}""");
        Assert.Contains("restored: 0", u);
        Assert.Contains("original place is taken", u);
        Assert.Equal("new file", File.ReadAllText(dup));
    }

    [Fact]
    public void PathGuard_RefusesFolderOutsideRoots_AndAcceptsRelative()
    {
        using var e = new Env();
        e.Outside.File("x.txt");
        Assert.Equal("error: folder not under a document root", e.FindIn(e.Outside.Path));
        Assert.Equal("error: folder not under a document root", Env.Call(e.Find, """{"folder":"../.."}"""));
        e.Write("מאמרים/a.txt", "unique text number one here");
        Assert.Contains("groups: 0", Env.Call(e.Find, """{"folder":"מאמרים"}"""));
    }

    [Fact]
    public void SubfoldersCanBeExcluded()
    {
        using var e = new Env();
        e.Write("a.txt", "same same same same same");
        e.Write("sub/a.txt", "same same same same same");
        Assert.Contains("groups: 0", Env.Call(e.Find, JsonSerializer.Serialize(new { folder = e.Root.Path, include_subfolders = false })));
        Assert.Contains("groups: 1", e.FindIn(e.Root.Path));
    }
}
