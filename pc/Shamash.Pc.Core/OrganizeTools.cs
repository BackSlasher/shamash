using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Shamash.Pc;

public sealed record DupGroup(string Keeper, IReadOnlyList<string> Duplicates, string Kind, double Similarity);

public sealed class OrganizePlan(string id, string folder, IReadOnlyList<DupGroup> groups)
{
    public string Id { get; } = id;
    public string Folder { get; } = folder;
    public IReadOnlyList<DupGroup> Groups { get; } = groups;
    public bool Applied { get; set; }
    public bool Undone { get; set; }
    public int DuplicateCount => Groups.Sum(g => g.Duplicates.Count);
}

/// <summary>Plans, the move journal and the plan-file opener, shared by the three organise tools.</summary>
public sealed class OrganizeStore(string journalPath, Action<string>? openFile = null)
{
    public const string DuplicatesFolder = "כפולים";

    /// <summary>The store the running service uses: process-wide plans, journal under LocalAppData.</summary>
    public static OrganizeStore Default { get; } = new(DefaultJournalPath(), DefaultOpen);

    private readonly object _lock = new();
    private readonly Dictionary<string, OrganizePlan> _plans = new();
    private int _counter;
    public string JournalPath { get; } = journalPath;
    public Action<string>? OpenFile { get; } = openFile;

    public OrganizePlan Add(string folder, IReadOnlyList<DupGroup> groups)
    {
        lock (_lock)
        {
            var plan = new OrganizePlan("p" + (++_counter).ToString(CultureInfo.InvariantCulture), folder, groups);
            _plans[plan.Id] = plan;
            return plan;
        }
    }

    public OrganizePlan? Get(string id) { lock (_lock) return _plans.GetValueOrDefault(id.Trim()); }

    public static string DefaultJournalPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(local) ? "." : local, "Shamash", "moves.log");
    }

    private static void DefaultOpen(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
    }

    public static IReadOnlyList<IAgentTool> CreateTools(IReadOnlyList<string> roots, OrganizeStore? store = null)
    {
        store ??= Default;
        return [new FindDuplicatesTool(roots, store), new ApplyPlanTool(store), new UndoPlanTool(store)];
    }
}

public sealed class FindDuplicatesTool(IReadOnlyList<string> roots, OrganizeStore store) : IAgentTool
{
    public const long MaxFileBytes = 50L * 1024 * 1024;
    public const double NearThreshold = 0.8;
    public const double SizeRatio = 0.7;
    public const int MaxGroupsReturned = 30;
    private static readonly string[] NearExtensions = [".docx", ".txt", ".pdf"];
    private static readonly string[] Markers = ["עותק", "copy", "(1)"];

    public string Name => "find_duplicates";
    public string Description =>
        "Scans a folder for exact and near-duplicate files and makes a PLAN to move the extra copies into a subfolder named כפולים. Nothing is moved. " +
        "The plan is also written as a text file in the folder and opened on the screen. Describe the plan to the user and ask for confirmation before apply_plan. " +
        "folder is a full path or a folder relative to a document root.";
    public string InputSchema => """
        {"type":"object","properties":{"folder":{"type":"string","description":"Full path, or folder relative to a document root"},"include_subfolders":{"type":"boolean","description":"Default true"}},"required":["folder"]}
        """;

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => Run(input, ct), ct);

    private sealed record Item(string Path, string Rel, long Size, DateTime Modified);

    private string Run(JsonElement input, CancellationToken ct)
    {
        var folderArg = ToolText.GetString(input, "folder");
        if (string.IsNullOrWhiteSpace(folderArg)) return "error: folder is required";
        var recurse = !(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("include_subfolders", out var p) && p.ValueKind == JsonValueKind.False);
        var folder = ResolveFolder(roots, folderArg);
        if (folder is null) return "error: folder not under a document root";
        if (!Directory.Exists(folder)) return "error: folder not found";

        var items = new List<Item>();
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in Directory.EnumerateFiles(folder, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                var rel = Path.GetRelativePath(folder, f);
                if (rel.StartsWith(OrganizeStore.DuplicatesFolder + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                if (Path.GetFileName(f).StartsWith("תוכנית סידור", StringComparison.Ordinal)) continue;
                var fi = new FileInfo(f);
                if (fi.Length > MaxFileBytes) continue;
                items.Add(new Item(f, rel, fi.Length, fi.LastWriteTimeUtc));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"error: could not scan folder ({e.GetType().Name})"; }

        // Exact duplicates: group by size, then SHA-256.
        var clusters = new List<(List<Item> Members, string Kind, double Sim)>();
        var singles = new List<List<Item>>(); // each entry is a cluster of identical files (size >= 1)
        foreach (var sizeGroup in items.GroupBy(i => i.Size))
        {
            ct.ThrowIfCancellationRequested();
            if (sizeGroup.Count() == 1) { singles.Add([sizeGroup.First()]); continue; }
            foreach (var h in sizeGroup.Select(i => (Item: i, Hash: Hash(i.Path))).Where(x => x.Hash is not null).GroupBy(x => x.Hash!))
                singles.Add(h.Select(x => x.Item).ToList());
        }
        var exact = singles.Where(s => s.Count > 1).ToList();
        foreach (var e in exact) clusters.Add((e, "exact", 1.0));

        // Near duplicates among cluster representatives of docx/txt/pdf.
        var reps = singles.Select(s => (Cluster: s, Keeper: PickKeeper(s))).Where(x => NearExtensions.Contains(Path.GetExtension(x.Keeper.Path), StringComparer.OrdinalIgnoreCase)).ToList();
        var shingles = new List<HashSet<string>?>();
        foreach (var r in reps)
        {
            ct.ThrowIfCancellationRequested();
            shingles.Add(Shingles(r.Keeper.Path));
        }
        var parent = Enumerable.Range(0, reps.Count).ToArray();
        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        var minSim = new Dictionary<int, double>();
        for (var i = 0; i < reps.Count; i++)
        {
            if (shingles[i] is null) continue;
            for (var j = i + 1; j < reps.Count; j++)
            {
                if (shingles[j] is null) continue;
                long a = reps[i].Keeper.Size, b = reps[j].Keeper.Size;
                if (Math.Min(a, b) < SizeRatio * Math.Max(a, b)) continue;
                var sim = Jaccard(shingles[i]!, shingles[j]!);
                if (sim < NearThreshold) continue;
                int ri = Find(i), rj = Find(j);
                var cur = Math.Min(sim, Math.Min(minSim.GetValueOrDefault(ri, 1.0), minSim.GetValueOrDefault(rj, 1.0)));
                if (ri != rj) parent[rj] = ri;
                minSim[Find(i)] = cur;
            }
        }
        var inNear = new HashSet<int>();
        foreach (var grp in Enumerable.Range(0, reps.Count).GroupBy(Find).Where(g => g.Count() > 1))
        {
            var members = grp.SelectMany(ix => reps[ix].Cluster).ToList();
            foreach (var ix in grp) inNear.Add(ix);
            clusters.Add((members, "near", minSim.GetValueOrDefault(grp.Key, NearThreshold)));
            // the exact clusters swallowed by this near group are no longer separate groups
            foreach (var ix in grp) clusters.RemoveAll(c => c.Kind == "exact" && ReferenceEquals(c.Members, reps[ix].Cluster));
        }

        var groups = new List<DupGroup>();
        foreach (var c in clusters.OrderBy(c => PickKeeper(c.Members).Rel, StringComparer.Ordinal))
        {
            var keeper = PickKeeper(c.Members);
            var dups = c.Members.Where(m => m != keeper).OrderBy(m => m.Rel, StringComparer.Ordinal).Select(m => m.Path).ToList();
            groups.Add(new DupGroup(keeper.Path, dups, c.Kind, Math.Round(c.Sim, 2)));
        }

        var plan = store.Add(folder, groups);
        var bytes = groups.Sum(g => g.Duplicates.Sum(d => new FileInfo(d).Length));
        var planFile = WritePlanFile(plan, folder);
        if (planFile is not null)
        {
            try { store.OpenFile?.Invoke(planFile); } catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { }
        }

        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"plan_id: {plan.Id}\nfolder: {Path.GetFileName(folder)}\nscanned: {items.Count} files\ngroups: {groups.Count}\nduplicate files: {plan.DuplicateCount}\nbytes: {bytes}\n");
        sb.Append(planFile is null ? "plan file: not written\n" : "plan file: written and opened on screen\n");
        var n = 0;
        foreach (var g in groups.Take(MaxGroupsReturned))
        {
            n++;
            sb.Append(CultureInfo.InvariantCulture, $"{n}. {g.Kind} {g.Similarity:0.00} | keep: {Path.GetRelativePath(folder, g.Keeper)} | extra: {string.Join("; ", g.Duplicates.Select(d => Path.GetRelativePath(folder, d)))}\n");
        }
        if (groups.Count > MaxGroupsReturned) sb.Append(CultureInfo.InvariantCulture, $"[truncated: showing {MaxGroupsReturned} of {groups.Count} groups; the plan file has all of them]\n");
        if (groups.Count == 0) sb.Append("no duplicates found\n");
        return sb.ToString().TrimEnd();
    }

    internal static string? ResolveFolder(IReadOnlyList<string> roots, string path)
    {
        if (Path.IsPathFullyQualified(path))
            return PathGuard.IsUnderRoots(path, roots) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : null;
        var rel = path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        foreach (var root in roots)
        {
            var c = Path.GetFullPath(Path.Combine(root, rel));
            if (PathGuard.IsUnderRoots(c, roots) && Directory.Exists(c)) return Path.TrimEndingDirectorySeparator(c);
        }
        return null;
    }

    private static string? Hash(string path)
    {
        try { using var s = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(s)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static HashSet<string>? Shingles(string path)
    {
        string text;
        try { text = DocumentReader.ReadText(path); } catch (Exception) { return null; }
        var words = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 3) return null;
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i + 2 < words.Length; i++) set.Add(words[i] + " " + words[i + 1] + " " + words[i + 2]);
        return set;
    }

    private static double Jaccard(HashSet<string> a, HashSet<string> b)
    {
        var (small, big) = a.Count <= b.Count ? (a, b) : (b, a);
        var inter = small.Count(big.Contains);
        return (double)inter / (a.Count + b.Count - inter);
    }

    private static bool HasMarker(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path);
        return Markers.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    private static Item PickKeeper(List<Item> members) =>
        members.OrderByDescending(m => m.Modified)
            .ThenBy(m => HasMarker(m.Path))
            .ThenBy(m => Path.GetFileName(m.Path).Length)
            .ThenBy(m => m.Path, StringComparer.Ordinal).First();

    private static string? WritePlanFile(OrganizePlan plan, string folder)
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"תוכנית סידור {plan.Id}: {plan.Groups.Count} קבוצות, {plan.DuplicateCount} קבצים כפולים\n");
            sb.Append("שום דבר עוד לא הועבר. הקבצים לא יימחקו, רק יועברו.\n\n");
            var i = 0;
            foreach (var g in plan.Groups)
            {
                i++;
                sb.Append(CultureInfo.InvariantCulture, $"קבוצה {i} ({(g.Kind == "exact" ? "זהים" : "דומים מאוד " + g.Similarity.ToString("0.00", CultureInfo.InvariantCulture))})\n");
                sb.Append("  נשאר: ").Append(Path.GetRelativePath(folder, g.Keeper)).Append('\n');
                foreach (var d in g.Duplicates)
                {
                    var rel = Path.GetRelativePath(folder, d);
                    sb.Append("  ").Append(rel).Append("  → יועבר אל: ").Append(OrganizeStore.DuplicatesFolder).Append('\\').Append(rel).Append('\n');
                }
                sb.Append('\n');
            }
            var path = Path.Combine(folder, "תוכנית סידור " + DateTime.Now.ToString("yyyy-MM-dd HHmm", CultureInfo.InvariantCulture) + ".txt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}

public sealed class ApplyPlanTool(OrganizeStore store) : IAgentTool
{
    public string Name => "apply_plan";
    public string Description =>
        "Carries out a plan from find_duplicates: moves the extra copies into the כפולים subfolder. Never deletes, never overwrites. " +
        "Call only after the user clearly said yes to that plan in a later turn.";
    public string InputSchema => """{"type":"object","properties":{"plan_id":{"type":"string"}},"required":["plan_id"]}""";

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => Run(input, ct), ct);

    private string Run(JsonElement input, CancellationToken ct)
    {
        var id = ToolText.GetString(input, "plan_id");
        if (string.IsNullOrWhiteSpace(id)) return "error: plan_id is required";
        var plan = store.Get(id);
        if (plan is null) return "error: unknown plan";
        if (plan.Applied) return plan.Undone ? "error: plan was already applied and undone; run find_duplicates again" : "error: plan already applied";
        plan.Applied = true;

        var target = Path.Combine(plan.Folder, OrganizeStore.DuplicatesFolder);
        var moved = new List<(string From, string To)>();
        var skipped = 0;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(store.JournalPath)!);
            foreach (var d in plan.Groups.SelectMany(g => g.Duplicates))
            {
                ct.ThrowIfCancellationRequested();
                if (!File.Exists(d)) { skipped++; continue; }
                var dest = FreeName(Path.Combine(target, Path.GetRelativePath(plan.Folder, d)));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Move(d, dest, overwrite: false);
                moved.Add((d, dest));
                File.AppendAllText(store.JournalPath, string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), plan.Id, Clean(d), Clean(dest)) + "\n", new UTF8Encoding(false));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"error: stopped after {moved.Count} moves ({e.GetType().Name}); undo_plan can restore them";
        }
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"moved: {moved.Count} files into {OrganizeStore.DuplicatesFolder}\nskipped (missing): {skipped}\n");
        foreach (var (f, t) in moved.Take(5)) sb.Append(Path.GetRelativePath(plan.Folder, f)).Append(" -> ").Append(Path.GetRelativePath(plan.Folder, t)).Append('\n');
        if (moved.Count > 5) sb.Append("...\n");
        sb.Append("undo is possible with undo_plan");
        return sb.ToString();
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\n', ' ');

    internal static string FreeName(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var n = 2; ; n++)
        {
            var c = Path.Combine(dir, $"{name} ({n}){ext}");
            if (!File.Exists(c) && !Directory.Exists(c)) return c;
        }
    }
}

public sealed class UndoPlanTool(OrganizeStore store) : IAgentTool
{
    public string Name => "undo_plan";
    public string Description => "Moves the files of an applied plan back to where they were, using the move journal. Never overwrites.";
    public string InputSchema => """{"type":"object","properties":{"plan_id":{"type":"string"}},"required":["plan_id"]}""";

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => Run(input, ct), ct);

    private string Run(JsonElement input, CancellationToken ct)
    {
        var id = ToolText.GetString(input, "plan_id");
        if (string.IsNullOrWhiteSpace(id)) return "error: plan_id is required";
        var plan = store.Get(id);
        if (plan is null) return "error: unknown plan";
        if (!plan.Applied) return "error: plan was never applied";
        if (plan.Undone) return "error: plan already undone";
        if (!File.Exists(store.JournalPath)) return "error: move journal not found";

        var entries = new List<(string From, string To)>();
        foreach (var line in File.ReadAllLines(store.JournalPath, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length == 4 && parts[1] == plan.Id) entries.Add((parts[2], parts[3]));
        }
        var restored = 0;
        var problems = new List<string>();
        try
        {
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                var (from, to) = entries[i];
                if (File.Exists(from)) { problems.Add($"{Path.GetFileName(from)}: original place is taken"); continue; }
                if (!File.Exists(to)) { problems.Add($"{Path.GetFileName(to)}: not found in {OrganizeStore.DuplicatesFolder}"); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(from)!);
                File.Move(to, from, overwrite: false);
                restored++;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"error: stopped after {restored} restores ({e.GetType().Name})";
        }
        plan.Undone = true;
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"restored: {restored} files\nnot restored: {problems.Count}\n");
        foreach (var p in problems.Take(5)) sb.Append(p).Append('\n');
        return sb.ToString().TrimEnd();
    }
}
