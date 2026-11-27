using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Shamash.Pc;

public sealed class SortDecision
{
    public string Path { get; set; } = "";
    /// <summary>done, skipped (asked again next sitting) or never.</summary>
    public string Status { get; set; } = "";
    public string? Reason { get; set; }
    public string? Destination { get; set; }
    public string? Tag { get; set; }
}

public sealed class SortMove
{
    public string Time { get; set; } = "";
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    public string Tag { get; set; } = "";
}

public sealed class SortSession
{
    public string Id { get; set; } = "";
    public string Folder { get; set; } = "";
    public string TargetRoot { get; set; } = "";
    public string Created { get; set; } = "";
    /// <summary>The item last returned by sort_folder_next and not yet decided.</summary>
    public string? Current { get; set; }
    public int NextMove { get; set; } = 1;
    public List<SortDecision> Decided { get; set; } = [];
    public List<SortMove> Moves { get; set; } = [];
    /// <summary>Hashes and content profiles of files elsewhere, valid while path, size and mtime match.</summary>
    public Dictionary<string, SortCacheEntry> Cache { get; set; } = new();
}

public sealed class SortFile
{
    public int Version { get; set; } = 1;
    public int NextId { get; set; } = 1;
    public List<SortSession> Sessions { get; set; } = [];
}

public sealed class SortCacheEntry
{
    public long Size { get; set; }
    public long Mtime { get; set; }
    public string? Sha { get; set; }
    public bool Profiled { get; set; }
    /// <summary>Bottom-k sketch of hashed word 3-grams (null: unreadable or too short).</summary>
    public long[]? Sketch { get; set; }
    /// <summary>The most frequent longer words, for folder similarity.</summary>
    public string[]? Words { get; set; }
}

/// <summary>Piecemeal sorting of a messy folder, one file per decision. Sessions persist in a JSON file so a sitting can resume after a restart.</summary>
public sealed class SortStore(string path, OrganizeStore organize, IReadOnlyList<string> roots, int maxFiles = ShamashSettings.DefaultMaxOrganizeFiles)
{
    public const int ExcerptChars = 600;
    public const int MaxDestinationsListed = 80;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static StringComparer Cmper => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly object _lock = new();
    private bool _dirty;
    /// <summary>Test aids: how many files were hashed / profiled for real (not served from the cache).</summary>
    public int HashComputations { get; private set; }
    public int ProfileComputations { get; private set; }
    private SortFile _data = LoadFile(path);

    public string FilePath { get; } = path;

    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(local) ? "." : local, "Shamash", "sort-sessions.json");
    }

    public static IReadOnlyList<IAgentTool> CreateTools(IReadOnlyList<string> roots, int maxFiles = ShamashSettings.DefaultMaxOrganizeFiles, SortStore? store = null)
    {
        store ??= new SortStore(DefaultPath(), OrganizeStore.Default, roots, maxFiles);
        return
        [
            new SortTool("sort_folder_start",
                "Starts or resumes a piecemeal sorting session for a messy folder. Returns the session id, counts and the existing destination folders under target_root. " +
                "Then use sort_folder_next for one file at a time. Refuses folders with too many files.",
                """{"type":"object","properties":{"folder":{"type":"string","description":"Full path, or folder relative to a document root"},"target_root":{"type":"string","description":"Folder whose sub-folders are the destinations: a path, a document root name (Documents, Desktop) or its Hebrew name (מסמכים, שולחן העבודה); default the Desktop"}},"required":["folder"]}""",
                store.Start),
            new SortTool("sort_folder_next",
                "Returns the next unsorted file of the session: name, folder, date, size, duplicates, a short text excerpt and evidence from the other folders (identical clones, similar versions, folders with similar content). Describe it in one sentence and propose ONE destination, as a question: clone elsewhere -> כפולים, then the folder of a similar version, then the most similar folder, then a new folder. Calling it again without a decision returns the same file.",
                """{"type":"object","properties":{"session_id":{"type":"string"}},"required":["session_id"]}""",
                store.Next),
            new SortTool("sort_folder_move",
                "Moves the current file into a destination folder (name or path under target_root) and its exact duplicates into כפולים. Never deletes or overwrites. Call only after the user said yes.",
                """{"type":"object","properties":{"session_id":{"type":"string"},"destination":{"type":"string","description":"Folder name or path under target_root"},"create_if_missing":{"type":"boolean","description":"Default false"}},"required":["session_id","destination"]}""",
                store.Move),
            new SortTool("sort_folder_skip",
                "Skips the current file; it is asked about again next sitting unless reason is \"never\".",
                """{"type":"object","properties":{"session_id":{"type":"string"},"reason":{"type":"string"}},"required":["session_id"]}""",
                store.Skip),
            new SortTool("sort_folder_status",
                "Counts for a session (by session_id or folder) and its last 5 moves.",
                """{"type":"object","properties":{"session_id":{"type":"string"},"folder":{"type":"string"}}}""",
                store.Status),
            new SortTool("undo_last_move",
                "Moves the most recent file move (from any tool) back to where it was; a sorting move and its duplicates are undone together. Never overwrites.",
                """{"type":"object","properties":{}}""",
                store.UndoLast),
        ];
    }

    private static SortFile LoadFile(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<SortFile>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new SortFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new SortFile();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private SortSession? Find(string? id) =>
        string.IsNullOrWhiteSpace(id) ? null : _data.Sessions.FirstOrDefault(s => s.Id == id.Trim());

    private sealed record Entry(string Path, long Size, DateTime Modified);

    private string? Enumerate(SortSession s, out List<Entry> files)
    {
        files = [];
        if (!Directory.Exists(s.Folder)) return "error: session folder no longer exists";
        var dupDir = System.IO.Path.Combine(s.TargetRoot, OrganizeStore.DuplicatesFolder);
        try
        {
            var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (var f in Directory.EnumerateFiles(s.Folder, "*", opts))
            {
                if (FileSkip.IsIgnored(f) || System.IO.Path.GetFileName(f).StartsWith("תוכנית סידור", StringComparison.Ordinal)) continue;
                if (f.StartsWith(dupDir + System.IO.Path.DirectorySeparatorChar, Cmp)) continue;
                if (files.Count >= maxFiles) return $"error: folder has more than {maxFiles} files (limit maxOrganizeFiles); choose a smaller folder";
                var fi = new FileInfo(f);
                files.Add(new Entry(f, fi.Length, fi.LastWriteTimeUtc));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"error: could not scan folder ({e.GetType().Name})"; }
        files = [.. files.OrderByDescending(x => x.Modified).ThenBy(x => x.Path, StringComparer.Ordinal)];
        return null;
    }

    private (int Total, int Remaining, int Done, int Skipped) Counts(SortSession s, List<Entry> files)
    {
        var decided = s.Decided.ToDictionary(d => d.Path, d => d.Status, Cmper);
        var done = s.Decided.Count(d => d.Status == "done");
        var skipped = files.Count(f => decided.TryGetValue(f.Path, out var st) && st != "done");
        var remaining = files.Count(f => !decided.ContainsKey(f.Path));
        return (files.Count + done, remaining, done, skipped);
    }

    private static string CountsText((int Total, int Remaining, int Done, int Skipped) c) =>
        $"total: {c.Total}\nremaining: {c.Remaining}\ndone: {c.Done}\nskipped: {c.Skipped}";

    private List<string> Duplicates(Entry item, List<Entry> files)
    {
        var same = files.Where(f => f.Size == item.Size && !f.Path.Equals(item.Path, Cmp)).ToList();
        if (same.Count == 0 || item.Size > FindDuplicatesTool.MaxFileBytes) return [];
        var h = FindDuplicatesTool.Hash(item.Path);
        return h is null ? [] : same.Where(f => FindDuplicatesTool.Hash(f.Path) == h).Select(f => f.Path).ToList();
    }

    private string? DefaultTarget(string folder)
    {
        var desk = roots.FirstOrDefault(r => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(r)).Equals("Desktop", StringComparison.OrdinalIgnoreCase));
        if (desk is not null) return desk;
        if (roots.Count > 0)
        {
            var c = System.IO.Path.Combine(roots[0], "Desktop");
            if (Directory.Exists(c)) return c;
        }
        return System.IO.Path.GetDirectoryName(folder);
    }

    private static readonly Dictionary<string, string> RootAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["מסמכים"] = "Documents", ["שולחן העבודה"] = "Desktop", ["שולחן עבודה"] = "Desktop",
    };

    /// <summary>target_root as a path, a document root's folder name ("Documents"), its Hebrew display name, or a folder relative to a root; names compare case-insensitively.</summary>
    public string? ResolveTarget(string arg)
    {
        if (!System.IO.Path.IsPathFullyQualified(arg))
        {
            var name = RootAliases.TryGetValue(arg, out var alias) ? alias : arg;
            var root = roots.FirstOrDefault(r => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(r)).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (root is not null) return System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(root));
        }
        return FindDuplicatesTool.ResolveFolder(roots, arg);
    }

    private static string Rel(string root, string p) => System.IO.Path.GetRelativePath(root, p);

    public string Start(JsonElement input)
    {
        var folderArg = ToolText.GetString(input, "folder");
        if (string.IsNullOrWhiteSpace(folderArg)) return "error: folder is required";
        var folder = FindDuplicatesTool.ResolveFolder(roots, folderArg);
        if (folder is null) return "error: folder not under a document root";
        if (!Directory.Exists(folder)) return "error: folder not found";
        string? target;
        var targetArg = ToolText.GetString(input, "target_root");
        if (!string.IsNullOrWhiteSpace(targetArg))
        {
            target = ResolveTarget(targetArg.Trim());
            if (target is null) return "error: target_root not under a document root";
        }
        else target = DefaultTarget(folder);
        if (target is null || !PathGuard.IsUnderRoots(target, roots)) return "error: could not choose a target_root; pass target_root";
        target = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(target));
        if (!Directory.Exists(target)) return "error: target_root not found";

        lock (_lock)
        {
            var s = _data.Sessions.FirstOrDefault(x => x.Folder.Equals(folder, Cmp));
            var resumed = s is not null;
            if (s is null)
            {
                s = new SortSession { Id = "s" + (_data.NextId++).ToString(CultureInfo.InvariantCulture), Folder = folder, Created = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) };
                _data.Sessions.Add(s);
            }
            s.TargetRoot = target;
            s.Current = null;
            s.Decided.RemoveAll(d => d.Status == "skipped"); // asked again this sitting; "never" stays
            var err = Enumerate(s, out var files);
            if (err is not null) { if (!resumed) _data.Sessions.Remove(s); return err; }
            Save();

            var dupFiles = 0;
            foreach (var g in files.Where(f => f.Size <= FindDuplicatesTool.MaxFileBytes).GroupBy(f => f.Size).Where(g => g.Count() > 1))
                dupFiles += g.Select(f => FindDuplicatesTool.Hash(f.Path)).Where(h => h is not null).GroupBy(h => h).Sum(h => h.Count() - 1);

            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"session_id: {s.Id}\n{(resumed ? "resumed" : "new")}\nfolder: {System.IO.Path.GetFileName(folder)}\ntarget_root: {System.IO.Path.GetFileName(target)}\n");
            sb.Append(CountsText(Counts(s, files))).Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"exact duplicate files among them: {dupFiles}\n");
            var dest = DestinationFolders(s, folder, target);
            sb.Append("destination folders: ").Append(dest.Count == 0 ? "none yet" : string.Join("; ", dest));
            return sb.ToString();
        }
    }

    private static List<string> DestinationFolders(SortSession s, string folder, string target)
    {
        try
        {
            return Directory.EnumerateDirectories(target)
                .Where(d => !d.Equals(folder, Cmp) && (new DirectoryInfo(d).Attributes & (FileAttributes.Hidden | FileAttributes.System)) == 0)
                .Select(System.IO.Path.GetFileName).Where(n => n is not null && n != OrganizeStore.DuplicatesFolder && !n.StartsWith('.')).Select(n => n!)
                .Order(StringComparer.Ordinal).Take(MaxDestinationsListed).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public string Next(JsonElement input)
    {
        lock (_lock)
        {
            var s = Find(ToolText.GetString(input, "session_id"));
            if (s is null) return "error: unknown session; call sort_folder_start";
            var err = Enumerate(s, out var files);
            if (err is not null) return err;
            var decided = s.Decided.Select(d => d.Path).ToHashSet(Cmper);
            var pending = files.Where(f => !decided.Contains(f.Path)).ToList();
            var item = s.Current is null ? null : pending.FirstOrDefault(f => f.Path.Equals(s.Current, Cmp));
            item ??= pending.FirstOrDefault();
            s.Current = item?.Path;
            Save();
            var counts = Counts(s, files);
            if (item is null)
                return "nothing left to sort in this sitting\n" + CountsText(counts);

            var dups = Duplicates(item, files);
            var sb = new StringBuilder();
            sb.Append("name: ").Append(System.IO.Path.GetFileName(item.Path)).Append('\n');
            sb.Append("folder: ").Append(Rel(s.Folder, System.IO.Path.GetDirectoryName(item.Path)!)).Append('\n');
            sb.Append("modified: ").Append(item.Modified.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"size KB: {(item.Size + 1023) / 1024}\n");
            sb.Append("duplicates: ").Append(dups.Count == 0 ? "none" : $"{dups.Count} exact copies (" + string.Join("; ", dups.Take(5).Select(d => Rel(s.Folder, d))) + "), they go to כפולים with it").Append('\n');
            sb.Append("excerpt: ").Append(Excerpt(item)).Append('\n');
            sb.Append(Evidence(s, item));
            if (_dirty) { _dirty = false; Save(); }
            sb.Append(CultureInfo.InvariantCulture, $"remaining: {counts.Remaining} of {counts.Total}");
            return sb.ToString();
        }
    }

    // ---- evidence from the other folders (the code reports, the model decides) ----

    public static readonly TimeSpan EvidenceBudget = TimeSpan.FromSeconds(25);
    public const int SketchSize = 64;
    public const int MaxNearCandidates = 200;
    public const double NearMin = 0.6;
    public const int FolderSample = 20;
    private static readonly string[] NearExts = [".docx", ".txt", ".pdf"];

    private IEnumerable<Entry> Others(SortSession s)
    {
        var count = 0;
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var f in Directory.EnumerateFiles(root, "*", opts))
            {
                if (FileSkip.IsIgnored(f)) continue;
                if (f.StartsWith(s.Folder + System.IO.Path.DirectorySeparatorChar, Cmp)) continue;
                if (f.Contains(System.IO.Path.DirectorySeparatorChar + OrganizeStore.DuplicatesFolder + System.IO.Path.DirectorySeparatorChar, Cmp)) continue;
                if (++count > 20000) yield break;
                FileInfo fi;
                try { fi = new FileInfo(f); } catch (Exception) { continue; }
                if (fi.Length > FindDuplicatesTool.MaxFileBytes) continue;
                yield return new Entry(f, fi.Length, fi.LastWriteTimeUtc);
            }
        }
    }

    private SortCacheEntry CacheFor(SortSession s, Entry f)
    {
        if (s.Cache.TryGetValue(f.Path, out var e) && e.Size == f.Size && e.Mtime == f.Modified.Ticks) return e;
        e = new SortCacheEntry { Size = f.Size, Mtime = f.Modified.Ticks };
        s.Cache[f.Path] = e;
        _dirty = true;
        return e;
    }

    private string? Sha(SortSession s, Entry f)
    {
        var e = CacheFor(s, f);
        if (e.Sha is null)
        {
            e.Sha = FindDuplicatesTool.Hash(f.Path);
            if (e.Sha is not null) { HashComputations++; _dirty = true; }
        }
        return e.Sha;
    }

    private SortCacheEntry Profile(SortSession s, Entry f)
    {
        var e = CacheFor(s, f);
        if (e.Profiled) return e;
        ProfileComputations++;
        _dirty = true;
        e.Profiled = true;
        if (!DocumentReader.IsSupported(f.Path) || f.Size > SearchDocumentsTool.ContentMaxBytes) return e;
        string text;
        try { text = DocumentReader.ReadText(f.Path); } catch (Exception) { return e; }
        var sh = FindDuplicatesTool.ShinglesOf(text);
        if (sh is not null) e.Sketch = [.. sh.Select(Fnv).Distinct().Order().Take(SketchSize)];
        e.Words = [.. Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{4,}").Select(m => m.Value).GroupBy(w => w).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).Take(40).Select(g => g.Key)];
        return e;
    }

    private static long Fnv(string x)
    {
        var h = 1469598103934665603UL;
        foreach (var c in x) { h ^= c; h *= 1099511628211UL; }
        return (long)(h >> 1);
    }

    internal static double SketchSimilarity(long[] a, long[] b)
    {
        var sa = new HashSet<long>(a);
        var sb = new HashSet<long>(b);
        var union = a.Union(b).Order().Take(SketchSize).ToList();
        return union.Count == 0 ? 0 : (double)union.Count(x => sa.Contains(x) && sb.Contains(x)) / union.Count;
    }

    private string Shown(string path)
    {
        var root = PathGuard.FindRoot(path, roots);
        var dir = System.IO.Path.GetDirectoryName(path)!;
        var head = root is null ? "" : System.IO.Path.GetFileName(root);
        var rel = root is null ? dir : Rel(root, dir);
        return (rel == "." ? head : head + "/" + rel.Replace('\\', '/')) + "/" + System.IO.Path.GetFileName(path);
    }

    private string Evidence(SortSession s, Entry item)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var cut = false;
        var others = Others(s).ToList();
        var sb = new StringBuilder();

        // 1. exact clones
        var clones = new List<Entry>();
        var same = others.Where(o => o.Size == item.Size).ToList();
        if (same.Count > 0 && item.Size > 0 && Sha(s, item) is { } mine)
            foreach (var o in same)
            {
                if (sw.Elapsed > EvidenceBudget) { cut = true; break; }
                if (Sha(s, o) == mine) clones.Add(o);
            }
        sb.Append("identical copies elsewhere: ").Append(clones.Count == 0 ? "none" : string.Join("; ", clones.Take(5).Select(c => Shown(c.Path)))).Append('\n');

        // 2. near-duplicates elsewhere
        var ext = System.IO.Path.GetExtension(item.Path).ToLowerInvariant();
        var itemProfile = DocumentReader.IsSupported(item.Path) ? Profile(s, item) : null;
        var near = new List<(Entry E, double Sim)>();
        if (NearExts.Contains(ext) && itemProfile?.Sketch is { } isk)
        {
            var cands = others.Where(o => System.IO.Path.GetExtension(o.Path).Equals(ext, StringComparison.OrdinalIgnoreCase)
                    && !clones.Contains(o) && Math.Min(o.Size, item.Size) >= FindDuplicatesTool.SizeRatio * Math.Max(o.Size, item.Size))
                .OrderBy(o => Math.Abs(o.Size - item.Size)).Take(MaxNearCandidates);
            foreach (var o in cands)
            {
                if (sw.Elapsed > EvidenceBudget) { cut = true; break; }
                if (Profile(s, o).Sketch is not { } osk) continue;
                var sim = SketchSimilarity(isk, osk);
                if (sim >= NearMin) near.Add((o, sim));
            }
        }
        sb.Append("similar versions elsewhere: ").Append(near.Count == 0 ? "none" : string.Join("; ", near.OrderByDescending(n => n.Sim).Take(3).Select(n => $"{Shown(n.E.Path)} ({n.Sim.ToString("0.00", CultureInfo.InvariantCulture)})"))).Append('\n');

        // 3. folders with similar content
        var scored = new List<(string Name, double Score)>();
        if (itemProfile?.Words is { Length: > 0 } iw)
        {
            var iset = new HashSet<string>(iw);
            foreach (var name in DestinationFolders(s, s.Folder, s.TargetRoot))
            {
                if (sw.Elapsed > EvidenceBudget) { cut = true; break; }
                var dir = System.IO.Path.Combine(s.TargetRoot, name) + System.IO.Path.DirectorySeparatorChar;
                var sims = new List<double>();
                foreach (var o in others.Where(o => o.Path.StartsWith(dir, Cmp) && DocumentReader.IsSupported(o.Path) && o.Size <= SearchDocumentsTool.ContentMaxBytes)
                             .OrderByDescending(o => o.Modified).Take(FolderSample))
                {
                    if (Profile(s, o).Words is not { Length: > 0 } w) continue;
                    var inter = w.Count(iset.Contains);
                    sims.Add((double)inter / (iset.Count + w.Length - inter));
                }
                if (sims.Count > 0) scored.Add((name, sims.OrderByDescending(x => x).Take(3).Average()));
            }
        }
        var top = scored.Where(x => x.Score > 0).OrderByDescending(x => x.Score).Take(2).ToList();
        sb.Append("folders with similar content: ").Append(top.Count == 0 ? "none" : string.Join("; ", top.Select(t => $"{t.Name} ({t.Score.ToString("0.00", CultureInfo.InvariantCulture)})"))).Append('\n');
        if (cut) sb.Append("evidence: stopped at the time limit, may be incomplete\n");
        return sb.ToString();
    }

    private static string Excerpt(Entry item)
    {
        var ext = System.IO.Path.GetExtension(item.Path);
        if (!DocumentReader.IsSupported(item.Path)) return $"cannot read {ext} files (use the name and date)";
        if (item.Size > ReadDocumentTool.MaxFileBytes) return "file too large to read";
        try
        {
            var text = Regex.Replace(DocumentReader.ReadText(item.Path), @"\s+", " ").Trim();
            if (text.Length == 0) return "no text (maybe a scan)";
            return text.Length <= ExcerptChars ? text : text[..ExcerptChars] + "...";
        }
        catch (Exception) { return "could not read the file"; }
    }

    private string? ResolveDestination(SortSession s, string dest, out string full)
    {
        full = "";
        dest = dest.Trim().Trim('"');
        if (dest.Length == 0) return "error: destination is required";
        if (dest.IndexOfAny(System.IO.Path.GetInvalidPathChars()) >= 0) return "error: destination has invalid characters";
        var path = System.IO.Path.IsPathFullyQualified(dest)
            ? dest
            : System.IO.Path.Combine(s.TargetRoot, dest.Replace('\\', System.IO.Path.DirectorySeparatorChar).Replace('/', System.IO.Path.DirectorySeparatorChar));
        try { full = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path)); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return "error: destination is not a valid path"; }
        if (!PathGuard.IsUnderRoots(full, [s.TargetRoot])) return "error: destination must be under target_root";
        return null;
    }

    private void Journal(string tag, string from, string to)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(organize.JournalPath))!);
        string C(string x) => x.Replace('\t', ' ').Replace('\n', ' ');
        File.AppendAllText(organize.JournalPath, string.Join('\t', DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), tag, C(from), C(to)) + "\n", new UTF8Encoding(false));
    }

    public string Move(JsonElement input)
    {
        lock (_lock)
        {
            var s = Find(ToolText.GetString(input, "session_id"));
            if (s is null) return "error: unknown session; call sort_folder_start";
            var destArg = ToolText.GetString(input, "destination");
            if (string.IsNullOrWhiteSpace(destArg)) return "error: destination is required";
            var create = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("create_if_missing", out var p) && p.ValueKind == JsonValueKind.True;
            if (s.Current is null) return "error: no current file; call sort_folder_next first";
            if (!File.Exists(s.Current)) { s.Current = null; Save(); return "error: the current file is gone; call sort_folder_next"; }
            var bad = ResolveDestination(s, destArg, out var destDir);
            if (bad is not null) return bad;
            var item = new FileInfo(s.Current);
            if (destDir.Equals(item.DirectoryName, Cmp)) return "error: the file is already in that folder";
            if (!Directory.Exists(destDir))
            {
                if (!create) return $"error: destination folder does not exist; pass create_if_missing true to create it. Existing: {string.Join("; ", DestinationFolders(s, s.Folder, s.TargetRoot))}";
            }
            var err = Enumerate(s, out var files);
            if (err is not null) return err;
            var cur = new Entry(item.FullName, item.Length, item.LastWriteTimeUtc);
            var dups = Duplicates(cur, files);

            var tag = $"{s.Id}#{s.NextMove++}";
            var moved = 0;
            string finalPath;
            try
            {
                Directory.CreateDirectory(destDir);
                finalPath = ApplyPlanTool.FreeName(System.IO.Path.Combine(destDir, item.Name));
                File.Move(item.FullName, finalPath, overwrite: false);
                Journal(tag, item.FullName, finalPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"error: could not move the file ({e.GetType().Name})";
            }
            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            s.Moves.Add(new SortMove { Time = time, From = item.FullName, To = finalPath, Tag = tag });
            s.Decided.Add(new SortDecision { Path = item.FullName, Status = "done", Destination = finalPath, Tag = tag });
            s.Current = null;
            var dupDir = System.IO.Path.Combine(s.TargetRoot, OrganizeStore.DuplicatesFolder);
            foreach (var d in dups)
            {
                try
                {
                    Directory.CreateDirectory(dupDir);
                    var dp = ApplyPlanTool.FreeName(System.IO.Path.Combine(dupDir, System.IO.Path.GetFileName(d)));
                    File.Move(d, dp, overwrite: false);
                    Journal(tag, d, dp);
                    s.Moves.Add(new SortMove { Time = time, From = d, To = dp, Tag = tag });
                    moved++;
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
            if (s.Moves.Count > 100) s.Moves.RemoveRange(0, s.Moves.Count - 100);
            Save();

            _ = Enumerate(s, out files);
            var sb = new StringBuilder();
            sb.Append("moved: ").Append(item.Name).Append(" -> ").Append(Rel(s.TargetRoot, System.IO.Path.GetDirectoryName(finalPath)!)).Append('\n');
            sb.Append(CultureInfo.InvariantCulture, $"duplicates moved to {OrganizeStore.DuplicatesFolder}: {moved}\n");
            sb.Append(CountsText(Counts(s, files))).Append("\nundo with undo_last_move");
            return sb.ToString();
        }
    }

    public string Skip(JsonElement input)
    {
        lock (_lock)
        {
            var s = Find(ToolText.GetString(input, "session_id"));
            if (s is null) return "error: unknown session; call sort_folder_start";
            if (s.Current is null) return "error: no current file; call sort_folder_next first";
            var reason = ToolText.GetString(input, "reason")?.Trim();
            var never = string.Equals(reason, "never", StringComparison.OrdinalIgnoreCase);
            s.Decided.Add(new SortDecision { Path = s.Current, Status = never ? "never" : "skipped", Reason = string.IsNullOrEmpty(reason) ? null : reason });
            s.Current = null;
            Save();
            var err = Enumerate(s, out var files);
            if (err is not null) return err;
            return (never ? "skipped for good\n" : "skipped for this sitting\n") + CountsText(Counts(s, files));
        }
    }

    public string Status(JsonElement input)
    {
        lock (_lock)
        {
            var s = Find(ToolText.GetString(input, "session_id"));
            if (s is null && ToolText.GetString(input, "folder") is { Length: > 0 } fa && FindDuplicatesTool.ResolveFolder(roots, fa) is { } folder)
                s = _data.Sessions.FirstOrDefault(x => x.Folder.Equals(folder, Cmp));
            if (s is null) return "error: no such session";
            var err = Enumerate(s, out var files);
            if (err is not null) return err;
            var sb = new StringBuilder();
            sb.Append(CultureInfo.InvariantCulture, $"session_id: {s.Id}\nfolder: {System.IO.Path.GetFileName(s.Folder)}\n").Append(CountsText(Counts(s, files))).Append('\n');
            sb.Append("last moves:");
            var last = s.Moves.TakeLast(5).Reverse().ToList();
            if (last.Count == 0) sb.Append(" none");
            foreach (var m in last)
                sb.Append('\n').Append(m.Time).Append(' ').Append(System.IO.Path.GetFileName(m.From)).Append(" -> ").Append(Rel(s.TargetRoot, System.IO.Path.GetDirectoryName(m.To)!));
            return sb.ToString();
        }
    }

    private sealed record JournalLine(string Time, string Tag, string From, string To);

    public string UndoLast(JsonElement input)
    {
        lock (_lock)
        {
            if (!File.Exists(organize.JournalPath)) return "error: move journal not found";
            var lines = new List<JournalLine>();
            foreach (var l in File.ReadAllLines(organize.JournalPath, Encoding.UTF8))
            {
                var p = l.Split('\t');
                if (p.Length == 4) lines.Add(new JournalLine(p[0], p[1], p[2], p[3]));
            }
            // Undo lines ("undo:<tag>") cancel earlier lines of the same tag, newest first.
            var cancelled = new Dictionary<string, int>();
            foreach (var l in lines.Where(l => l.Tag.StartsWith("undo:", StringComparison.Ordinal)))
                cancelled[l.Tag[5..]] = cancelled.GetValueOrDefault(l.Tag[5..]) + 1;
            var live = new List<JournalLine>();
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                var l = lines[i];
                if (l.Tag.StartsWith("undo:", StringComparison.Ordinal)) continue;
                if (cancelled.GetValueOrDefault(l.Tag) > 0) { cancelled[l.Tag]--; continue; }
                if (live.Count > 0 && (!l.Tag.Contains('#') || l.Tag != live[0].Tag)) break;
                live.Add(l);
                if (!l.Tag.Contains('#')) break;
            }
            if (live.Count == 0) return "error: nothing to undo";
            foreach (var l in live)
            {
                if (File.Exists(l.From) || Directory.Exists(l.From)) return $"error: cannot undo, the original place of {System.IO.Path.GetFileName(l.From)} is taken";
                if (!File.Exists(l.To)) return $"error: cannot undo, {System.IO.Path.GetFileName(l.To)} is no longer where it was moved";
            }
            var restored = new List<string>();
            try
            {
                foreach (var l in live)
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(l.From)!);
                    File.Move(l.To, l.From, overwrite: false);
                    Journal("undo:" + l.Tag, l.To, l.From);
                    restored.Add(System.IO.Path.GetFileName(l.From) + " -> " + System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(l.From)));
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return $"error: stopped after {restored.Count} restores ({e.GetType().Name})";
            }
            var tag = live[0].Tag;
            if (tag.Contains('#') && Find(tag[..tag.IndexOf('#')]) is { } s)
            {
                s.Decided.RemoveAll(d => d.Tag == tag);
                s.Moves.RemoveAll(m => m.Tag == tag);
                s.Current = null;
                Save();
            }
            return $"restored: {restored.Count}\n" + string.Join('\n', restored);
        }
    }
}

/// <summary>An IAgentTool over a synchronous store method.</summary>
public sealed class SortTool(string name, string description, string schema, Func<JsonElement, string> run) : IAgentTool
{
    public string Name => name;
    public string Description => description;
    public string InputSchema => schema;
    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => run(input), ct);
}
