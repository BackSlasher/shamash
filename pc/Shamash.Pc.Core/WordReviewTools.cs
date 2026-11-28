using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shamash.Pc;

/// <summary>One tracked change as Word reports it: Range.Start/End, WdRevisionType, author, date, the text it covers.</summary>
public sealed record RevInfo(int Start, int End, int Type, string Author, DateTime Date, string Text);

/// <summary>The seam between the review sitting and Word: a document with revisions. The COM version is <see cref="ComRevisionDocument"/>; tests use a fake.</summary>
public interface IRevisionDocument
{
    string Name { get; }
    string FullPath { get; }
    /// <summary>Every revision of the document, in document order. Indices are 0-based and valid until the next Accept/Reject.</summary>
    IReadOnlyList<RevInfo> Snapshot();
    /// <summary>Selects the revision's range and scrolls it into view.</summary>
    void Select(int index);
    /// <summary>The text just before and just after the revision's range (a few hundred characters each).</summary>
    (string Before, string After) Around(RevInfo r);
    void Accept(int index);
    void Reject(int index);
    void AcceptAll();
    void RejectAll();
    /// <summary>Backup before the first mutation (see <see cref="WordBackups"/>); the note for the result, "" when nothing new.</summary>
    string Backup();
}

public sealed class ReviewItem
{
    /// <summary>"start|type|author" when the item was created; informational, the fields below are what gets resolved.</summary>
    public string Key { get; set; } = "";
    public int Start { get; set; }
    public int Type { get; set; }
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>pending, skipped (waiting for the revisit), left (skipped in the revisit too), accepted or rejected.</summary>
    public string Status { get; set; } = ReviewLogic.Pending;
}

public sealed class ReviewSession
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Started { get; set; } = "";
    public List<ReviewItem> Items { get; set; } = [];
    /// <summary>Index of the item last shown and not yet answered; -1 when none.</summary>
    public int Position { get; set; } = -1;
    /// <summary>True once the skipped ones were put back in the queue (the pointer wraps once).</summary>
    public bool Revisit { get; set; }
}

public sealed class ReviewFile
{
    public int Version { get; set; } = 1;
    public List<ReviewSession> Sessions { get; set; } = [];
}

/// <summary>Review sessions in <c>%LocalAppData%\Shamash\review-sessions.json</c>, one per document path, so a sitting resumes after a restart or on another day.</summary>
public sealed class ReviewStore(string path)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _lock = new();
    private ReviewFile _data = Load(path);

    public string FilePath { get; } = path;

    public static string DefaultPath()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return System.IO.Path.Combine(string.IsNullOrEmpty(local) ? "." : local, "Shamash", "review-sessions.json");
    }

    private static ReviewFile Load(string path)
    {
        try
        {
            if (File.Exists(path)) return JsonSerializer.Deserialize<ReviewFile>(File.ReadAllText(path, Encoding.UTF8), Json) ?? new ReviewFile();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
        return new ReviewFile();
    }

    public static string KeyOf(string fullPath) => DocumentIdentity.Normalize(fullPath);

    public ReviewSession? Get(string fullPath)
    {
        lock (_lock) return _data.Sessions.FirstOrDefault(s => string.Equals(KeyOf(s.Path), KeyOf(fullPath), StringComparison.OrdinalIgnoreCase));
    }

    public void Put(ReviewSession s)
    {
        lock (_lock)
        {
            _data.Sessions.RemoveAll(x => string.Equals(KeyOf(x.Path), KeyOf(s.Path), StringComparison.OrdinalIgnoreCase));
            _data.Sessions.Add(s);
            Save();
        }
    }

    public void Remove(string fullPath)
    {
        lock (_lock)
        {
            if (_data.Sessions.RemoveAll(x => string.Equals(KeyOf(x.Path), KeyOf(fullPath), StringComparison.OrdinalIgnoreCase)) > 0) Save();
        }
    }

    /// <summary>Saves after a session was changed in place.</summary>
    public void Touch() { lock (_lock) Save(); }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(FilePath))!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_data, Json), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Pure decisions and result text of the review sitting: revision names and keys, resolving stored items against Word's current list, the pointer, the grouped-action guard.</summary>
public static class ReviewLogic
{
    public const string Pending = "pending", Skipped = "skipped", Left = "left", Accepted = "accepted", Rejected = "rejected";
    public const int ContextWords = 10, TextWords = 30, MaxAnswers = 30, MaxStoredText = 80;
    public const string NoSession = "error: no review session for this document; call review_changes_start";
    public const string AllDone = "all answered or skipped";

    public static string TypeName(int wdRevisionType) => wdRevisionType switch
    {
        1 => "insertion",
        2 => "deletion",
        3 or 8 or 10 => "formatting",   // wdRevisionProperty, wdRevisionStyle, wdRevisionParagraphProperty
        4 => "paragraph number",
        5 => "field display",
        6 => "reconcile",
        7 => "conflict",
        9 => "replacement",
        11 => "table property",
        12 => "section property",
        13 => "style definition",
        14 => "move from",
        15 => "move to",
        16 => "cell insertion",
        17 => "cell deletion",
        18 => "cell merge",
        _ => "change",
    };

    public static string Short(string? text)
    {
        var t = (text ?? "").Trim();
        return t.Length <= MaxStoredText ? t : t[..MaxStoredText];
    }

    public static string Key(RevInfo r) => $"{r.Start}|{r.Type}|{r.Author}";

    public static string DateText(DateTime d) => d.Year < 1990 ? "" : d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    public static ReviewItem ItemOf(RevInfo r) => new()
    {
        Key = Key(r), Start = r.Start, Type = r.Type, Author = r.Author, Date = DateText(r.Date), Text = Short(r.Text),
    };

    /// <summary>Not accepted or rejected: the revision is still in the document (skipped for good included).</summary>
    public static bool IsUnanswered(ReviewItem i) => i.Status is Pending or Skipped or Left;

    /// <summary>
    /// Index in <paramref name="snapshot"/> of the revision this item stands for, or -1. The revision must match type, author and text;
    /// among several (Word merges nothing, but two identical edits are possible) the one at the stored start wins, else the closest
    /// start. Revisions in <paramref name="claimed"/> belong to another item already.
    /// </summary>
    public static int Resolve(ReviewItem item, IReadOnlyList<RevInfo> snapshot, ISet<int> claimed)
    {
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < snapshot.Count; i++)
        {
            var r = snapshot[i];
            if (claimed.Contains(i) || r.Type != item.Type || r.Author != item.Author || Short(r.Text) != item.Text) continue;
            var d = Math.Abs(r.Start - item.Start);
            if (d < bestDistance) { best = i; bestDistance = d; }
        }
        return best;
    }

    /// <summary>
    /// Brings the stored items in line with Word: unanswered items get their current start (or are dropped, or marked
    /// <paramref name="goneStatus"/>, when their revision no longer exists); revisions nobody knows are appended as pending.
    /// Returns the index in <paramref name="snapshot"/> of every kept unanswered item (by item index).
    /// </summary>
    public static Dictionary<int, int> Reconcile(ReviewSession s, IReadOnlyList<RevInfo> snapshot, string? goneStatus = null)
    {
        var claimed = new HashSet<int>();
        var map = new Dictionary<int, int>();
        var drop = new List<ReviewItem>();
        for (var k = 0; k < s.Items.Count; k++)
        {
            var item = s.Items[k];
            if (!IsUnanswered(item)) continue;
            var idx = Resolve(item, snapshot, claimed);
            if (idx < 0)
            {
                if (goneStatus is null) drop.Add(item); else item.Status = goneStatus;
                continue;
            }
            claimed.Add(idx);
            item.Start = snapshot[idx].Start;
            item.Key = Key(snapshot[idx]);
            map[k] = idx;
        }
        // Dropping shifts indices: remember the current item by reference.
        var current = s.Position >= 0 && s.Position < s.Items.Count ? s.Items[s.Position] : null;
        var oldIndex = map.ToDictionary(kv => s.Items[kv.Key], kv => kv.Value);
        foreach (var d in drop) s.Items.Remove(d);
        for (var i = 0; i < snapshot.Count; i++)
        {
            if (claimed.Contains(i)) continue;
            // An answered item may stand for a revision still in the list only if Word failed to apply it; treat everything unclaimed as new.
            var item = ItemOf(snapshot[i]);
            s.Items.Add(item);
            oldIndex[item] = i;
        }
        s.Position = current is not null && s.Items.Contains(current) ? s.Items.IndexOf(current) : -1;
        return oldIndex.ToDictionary(kv => s.Items.IndexOf(kv.Key), kv => kv.Value);
    }

    public static int Remaining(ReviewSession s) => s.Items.Count(i => i.Status is Pending or Skipped);
    public static int Count(ReviewSession s, params string[] statuses) => s.Items.Count(i => statuses.Contains(i.Status));

    /// <summary>
    /// Makes <see cref="ReviewSession.Position"/> point at the next pending item (after the current position, else from the top).
    /// When none is pending and the skipped ones have not been revisited, they go back in the queue once. False when nothing is left.
    /// </summary>
    public static bool Advance(ReviewSession s)
    {
        int Find()
        {
            for (var i = s.Position + 1; i < s.Items.Count; i++) if (s.Items[i].Status == Pending) return i;
            for (var i = 0; i < s.Items.Count; i++) if (s.Items[i].Status == Pending) return i;
            return -1;
        }
        var idx = Find();
        if (idx < 0 && !s.Revisit && s.Items.Any(i => i.Status == Skipped))
        {
            s.Revisit = true;
            foreach (var i in s.Items.Where(i => i.Status == Skipped)) i.Status = Pending;
            s.Position = -1;
            idx = Find();
        }
        s.Position = idx;
        return idx >= 0;
    }

    /// <summary>Keeps the pointer when it still points at a pending item, else advances.</summary>
    public static bool Ensure(ReviewSession s) =>
        s.Position >= 0 && s.Position < s.Items.Count && s.Items[s.Position].Status == Pending || Advance(s);

    /// <summary>Applies a skip to the current item: it waits for the revisit, or is left for good when it already was revisited.</summary>
    public static void Skip(ReviewSession s, ReviewItem item) => item.Status = s.Revisit ? Left : Skipped;

    public static string Context(string before, string after, string text)
    {
        var b = WordEditText.FirstWords(before, int.MaxValue).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var a = WordEditText.FirstWords(after, int.MaxValue).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var mid = WordEditText.FirstWords(text, TextWords);
        var left = string.Join(' ', b.TakeLast(ContextWords));
        var right = string.Join(' ', a.Take(ContextWords));
        var sb = new StringBuilder("…");
        if (left.Length > 0) sb.Append(left).Append(' ');
        sb.Append('[').Append(mid.Length > 0 ? mid : "¶").Append(']');
        if (right.Length > 0) sb.Append(' ').Append(right);
        return sb.Append('…').ToString();
    }

    public static string AuthorText(string author) => string.Join(' ', (author ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>"revision 3 of 8: insertion by Dani (2026-11-20): "…context [inserted text] context…"</summary>
    public static string Line(int number, int total, ReviewItem item, string context)
    {
        var date = item.Date.Length > 0 ? $" ({item.Date})" : "";
        return $"revision {number} of {total}: {TypeName(item.Type)} by {AuthorText(item.Author)}{date}: \"{context}\"";
    }

    public static string Tail(ReviewSession s) => s.Revisit && Remaining(s) > 0 ? "; revisiting the skipped ones" : "";

    public static string Counts(ReviewSession s) =>
        $"accepted {Count(s, Accepted)}, rejected {Count(s, Rejected)}, skipped {Count(s, Skipped, Left)}, remaining {Remaining(s)}";

    // ---- answers

    /// <summary>"accept", "reject" or "skip" from the model's word or the user's Hebrew; null when it is none of them.</summary>
    public static string? NormalizeAnswer(string? a)
    {
        var t = (a ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "accept" or "accepted" or "yes" or "קבל" or "קבלתי" or "אשר" => "accept",
            "reject" or "rejected" or "no" or "דחה" or "דחייה" => "reject",
            "skip" or "skipped" or "דלג" or "דלגי" => "skip",
            _ => null,
        };
    }

    public static (List<string> Answers, string? Error) ParseAnswers(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object || !input.TryGetProperty("answers", out var arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
            return ([], "error: answers is required (a list of accept, reject or skip)");
        if (arr.GetArrayLength() > MaxAnswers) return ([], $"error: at most {MaxAnswers} answers per call");
        var list = new List<string>();
        foreach (var e in arr.EnumerateArray())
        {
            var n = e.ValueKind == JsonValueKind.String ? NormalizeAnswer(e.GetString()) : null;
            if (n is null) return ([], $"error: answers must be accept, reject or skip (got {e.GetRawText().Replace('"', '\'')})");
            list.Add(n);
        }
        return (list, null);
    }

    // ---- grouped accept/reject

    public sealed record BulkRequest(string? Author, string? Type, int? Confirmed);

    public static string? CanonicalType(string? t) => t?.Trim().ToLowerInvariant() switch
    {
        null or "" => null,
        "insertion" or "insert" or "inserted" or "insertions" => "insertion",
        "deletion" or "delete" or "deleted" or "deletions" => "deletion",
        "formatting" or "format" or "formats" => "formatting",
        _ => "?",
    };

    public static (BulkRequest? Request, string? Error) ParseBulk(JsonElement input)
    {
        var author = ToolText.GetString(input, "author");
        if (string.IsNullOrWhiteSpace(author)) author = null;
        var type = CanonicalType(ToolText.GetString(input, "type"));
        if (type == "?") return (null, "error: type must be insertion, deletion or formatting");
        int? confirmed = null;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("confirmed_count", out var c) && c.ValueKind != JsonValueKind.Null)
        {
            if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out var n) || n < 0) return (null, "error: confirmed_count must be a whole number");
            confirmed = n;
        }
        return (new BulkRequest(author?.Trim(), type, confirmed), null);
    }

    public static bool Matches(RevInfo r, BulkRequest q) =>
        (q.Author is null || r.Author.Contains(q.Author, StringComparison.OrdinalIgnoreCase)) && (q.Type is null || TypeName(r.Type) == q.Type);

    public static string FilterText(BulkRequest q) =>
        (q.Type is not null ? $" of type {q.Type}" : "") + (q.Author is not null ? $" by {AuthorText(q.Author)}" : "");

    /// <summary>Null when the count was confirmed, else the error that tells the model to say the number and ask.</summary>
    public static string? GuardCount(int matching, int? confirmed, IReadOnlyList<RevInfo> all, BulkRequest q)
    {
        if (matching == 0)
        {
            var authors = all.Select(r => AuthorText(r.Author)).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();
            return $"error: 0 changes match{FilterText(q)}; authors in the document: {(authors.Count == 0 ? "none" : string.Join(", ", authors))}";
        }
        return confirmed == matching ? null : $"error: {matching} changes match{FilterText(q)}, confirm with confirmed_count {matching}";
    }
}

/// <summary>The review sitting itself, over <see cref="IRevisionDocument"/> so it is testable without Word.</summary>
public sealed class ReviewEngine(ReviewStore store)
{
    private static string Tag(IRevisionDocument d) => WordEditText.DocumentTag(d.Name);
    private static string PathOf(IRevisionDocument d) => string.IsNullOrEmpty(d.FullPath) ? d.Name : d.FullPath;

    public string Start(IRevisionDocument doc)
    {
        var snap = doc.Snapshot();
        if (snap.Count == 0)
        {
            store.Remove(PathOf(doc));
            return $"no tracked changes in this document; {Tag(doc)}";
        }
        var s = store.Get(PathOf(doc));
        if (s is null)
        {
            s = new ReviewSession { Path = PathOf(doc), Name = doc.Name, Started = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), Items = snap.Select(ReviewLogic.ItemOf).ToList() };
            ReviewLogic.Advance(s);
            store.Put(s);
            var kinds = string.Join(", ", s.Items.GroupBy(i => ReviewLogic.TypeName(i.Type)).Select(g => $"{g.Key} {g.Count()}"));
            return $"review started: {s.Items.Count} changes ({kinds}); {Tag(doc)}; call review_changes_next";
        }
        ReviewLogic.Reconcile(s, snap);
        // Everything answered except revisits already done: put the left ones back, so a new day starts at them.
        if (ReviewLogic.Remaining(s) == 0 && s.Items.Any(i => i.Status == ReviewLogic.Left))
        {
            foreach (var i in s.Items.Where(i => i.Status == ReviewLogic.Left)) i.Status = ReviewLogic.Pending;
            s.Revisit = true;
            s.Position = -1;
        }
        ReviewLogic.Ensure(s);
        store.Touch();
        var started = s.Started.Length > 0 ? $" (started {s.Started})" : "";
        return ReviewLogic.Remaining(s) == 0
            ? $"review resumed{started}: {ReviewLogic.Counts(s)}; {ReviewLogic.AllDone}; {Tag(doc)}"
            : $"review resumed{started} at the next unanswered change: {ReviewLogic.Counts(s)}; {Tag(doc)}; call review_changes_next";
    }

    /// <summary>The text of the current item and the selection of its range; null item when nothing is left.</summary>
    private string? Show(IRevisionDocument doc, ReviewSession s, bool withHint)
    {
        var snap = doc.Snapshot();
        var map = ReviewLogic.Reconcile(s, snap);
        if (!ReviewLogic.Ensure(s)) { store.Touch(); return null; }
        var item = s.Items[s.Position];
        var idx = map[s.Position];
        doc.Select(idx);
        var (before, after) = doc.Around(snap[idx]);
        store.Touch();
        return ReviewLogic.Line(s.Position + 1, s.Items.Count, item, ReviewLogic.Context(before, after, snap[idx].Text)) + ReviewLogic.Tail(s) + (withHint ? "; answer with review_changes_answer" : "");
    }

    public string Next(IRevisionDocument doc)
    {
        if (store.Get(PathOf(doc)) is not { } s) return ReviewLogic.NoSession;
        var line = Show(doc, s, withHint: true);
        return line is null ? $"{ReviewLogic.AllDone}: {ReviewLogic.Counts(s)}; {Tag(doc)}" : $"{line}; {Tag(doc)}";
    }

    public string Status(IRevisionDocument doc)
    {
        var snap = doc.Snapshot();
        if (store.Get(PathOf(doc)) is not { } s) return $"review: no session; revisions in the document: {snap.Count}; {Tag(doc)}";
        return $"review: session since {s.Started}; revisions in the document: {snap.Count}; {ReviewLogic.Counts(s)}; {Tag(doc)}";
    }

    public string Answer(IReadOnlyList<string> answers, IRevisionDocument doc)
    {
        if (store.Get(PathOf(doc)) is not { } s) return ReviewLogic.NoSession;
        var outcomes = new List<string>();
        var backup = "";
        var mutated = false;
        foreach (var answer in answers)
        {
            var snap = doc.Snapshot();
            var map = ReviewLogic.Reconcile(s, snap);
            if (!ReviewLogic.Ensure(s)) { outcomes.Add("no revision left"); break; }
            var item = s.Items[s.Position];
            var idx = map[s.Position];
            var acted = snap[idx];
            if (answer == "skip")
            {
                ReviewLogic.Skip(s, item);
                outcomes.Add("skipped");
            }
            else
            {
                if (!mutated) { backup = doc.Backup(); }
                mutated = true;
                if (answer == "accept") doc.Accept(idx); else doc.Reject(idx);
                item.Status = answer == "accept" ? ReviewLogic.Accepted : ReviewLogic.Rejected;
                outcomes.Add(item.Status);
                // Text that left the document moves everything after it up.
                var removed = (answer == "accept" && acted.Type == 2) || (answer == "reject" && acted.Type == 1) ? acted.End - acted.Start : 0;
                if (removed > 0) foreach (var o in s.Items.Where(o => ReviewLogic.IsUnanswered(o) && o.Start > acted.Start)) o.Start -= removed;
            }
            ReviewLogic.Advance(s);
            store.Touch();
        }
        var next = Show(doc, s, withHint: false);
        outcomes.Add(next is null ? ReviewLogic.AllDone : $"remaining {ReviewLogic.Remaining(s)}, next: {next}");
        var body = string.Join("; ", outcomes);
        return mutated ? WordEditText.Mutation(body, doc.Name, backup) : $"{body}; {Tag(doc)}";
    }

    /// <summary>accept_all_changes / reject_all_changes: acts only when confirmed_count equals the number of matching revisions.</summary>
    public string Bulk(bool accept, ReviewLogic.BulkRequest q, IRevisionDocument doc)
    {
        var snap = doc.Snapshot();
        var hits = Enumerable.Range(0, snap.Count).Where(i => ReviewLogic.Matches(snap[i], q)).ToList();
        if (ReviewLogic.GuardCount(hits.Count, q.Confirmed, snap, q) is { } error) return $"{error}; {Tag(doc)}";
        var backup = doc.Backup();
        if (hits.Count == snap.Count)
        {
            if (accept) doc.AcceptAll(); else doc.RejectAll();
        }
        else
        {
            // From the end, so the indices of the ones still to do do not move.
            foreach (var i in Enumerable.Reverse(hits)) { if (accept) doc.Accept(i); else doc.Reject(i); }
        }
        var after = doc.Snapshot();
        if (store.Get(PathOf(doc)) is { } s)
        {
            ReviewLogic.Reconcile(s, after, accept ? ReviewLogic.Accepted : ReviewLogic.Rejected);
            if (s.Position >= 0 && s.Position < s.Items.Count && s.Items[s.Position].Status != ReviewLogic.Pending) ReviewLogic.Advance(s);
            store.Touch();
        }
        return WordEditText.Mutation($"{(accept ? "accepted" : "rejected")} {hits.Count} {(hits.Count == 1 ? "change" : "changes")}{ReviewLogic.FilterText(q)}; {after.Count} left in the document", doc.Name, backup);
    }
}

/// <summary>The COM side of <see cref="IRevisionDocument"/>: late-bound calls on the active document.</summary>
[SupportedOSPlatform("windows")]
internal sealed class ComRevisionDocument(object app, object doc) : IRevisionDocument
{
    private readonly dynamic _app = app, _doc = doc;
    private const int ContextChars = 400;

    public string Name => (string)_doc.Name;
    public string FullPath => (string)_doc.FullName;

    public IReadOnlyList<RevInfo> Snapshot()
    {
        dynamic? revs = null;
        try
        {
            revs = _doc.Revisions;
            int n = revs.Count;
            var list = new List<RevInfo>(n);
            for (var i = 1; i <= n; i++)
            {
                dynamic? r = null, range = null;
                try
                {
                    r = revs.Item(i);
                    range = r.Range;
                    DateTime date;
                    try { date = (DateTime)r.Date; } catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or InvalidCastException or System.Runtime.InteropServices.COMException) { date = default; }
                    list.Add(new RevInfo((int)range.Start, (int)range.End, (int)r.Type, (string)r.Author ?? "", date, (string)range.Text ?? ""));
                }
                finally { ComInterop.Release(range, r); }
            }
            return list;
        }
        finally { ComInterop.Release(revs); }
    }

    private T WithRevision<T>(int index, Func<dynamic, T> f)
    {
        dynamic? revs = null, r = null;
        try { revs = _doc.Revisions; r = revs.Item(index + 1); return f(r); }
        finally { ComInterop.Release(r, revs); }
    }

    public void Select(int index) => WithRevision(index, r =>
    {
        dynamic? range = null, win = null;
        try
        {
            range = r.Range;
            range.Select();
            try { win = _app.ActiveWindow; win.ScrollIntoView(range, true); }
            catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException) { }
        }
        finally { ComInterop.Release(win, range); }
        return 0;
    });

    public (string Before, string After) Around(RevInfo r)
    {
        dynamic? content = null, b = null, a = null;
        try
        {
            content = _doc.Content;
            int end = content.End;
            b = _doc.Range(Math.Max(0, r.Start - ContextChars), r.Start);
            a = _doc.Range(Math.Min(end, r.End), Math.Min(end, r.End + ContextChars));
            return ((string)b.Text ?? "", (string)a.Text ?? "");
        }
        finally { ComInterop.Release(a, b, content); }
    }

    public void Accept(int index) => WithRevision(index, r => { r.Accept(); return 0; });
    public void Reject(int index) => WithRevision(index, r => { r.Reject(); return 0; });

    public void AcceptAll() { dynamic? revs = null; try { revs = _doc.Revisions; revs.AcceptAll(); } finally { ComInterop.Release(revs); } }
    public void RejectAll() { dynamic? revs = null; try { revs = _doc.Revisions; revs.RejectAll(); } finally { ComInterop.Release(revs); } }

    public string Backup() => WordBackups.EnsureBackup(FullPath);
}

/// <summary>A review tool: input validation first (no COM), then the engine over the active document.</summary>
public sealed class ReviewTool(string name, string description, string inputSchema, Func<JsonElement, string?> validate, Func<JsonElement, IRevisionDocument, string> run) : WordEditTool
{
    public override string Name => name;
    public override string Description => description;
    public override string InputSchema => inputSchema;
    protected override string? Validate(JsonElement input) => validate(input);

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc) => run(input, new ComRevisionDocument(app, doc));
}

public static class WordReviewTools
{
    public static IEnumerable<IAgentTool> Create(ReviewStore? store = null)
    {
        var engine = new ReviewEngine(store ?? new ReviewStore(ReviewStore.DefaultPath()));
        return
        [
            new ReviewTool("review_changes_start",
                "Starts the review of the tracked changes in the document open in Word, or resumes the sitting of an earlier day at the next unanswered change (it says which). Then call review_changes_next.",
                """{"type":"object","properties":{}}""", _ => null, (_, d) => engine.Start(d)),
            new ReviewTool("review_changes_next",
                "Selects the current tracked change on screen (highlighted) and returns it: number, type (insertion, deletion, formatting), author, date and the changed text in brackets with ~10 words of context. Read it aloud as 'הוספה של דני: ..., לקבל?'. Calling it again without an answer returns the same change.",
                """{"type":"object","properties":{}}""", _ => null, (_, d) => engine.Next(d)),
            new ReviewTool("review_changes_answer",
                "Applies the user's answers, in order, to the current change and the following ones: each is accept, reject or skip. Call it only with words he said for these changes ('קבל' always means the one highlighted on screen). Several answers in one call are applied in order. Returns each outcome, how many remain and the next change (selected on screen). Skipped changes come back after the others. Accept and reject can be undone with undo_last_edit.",
                """{"type":"object","properties":{"answers":{"type":"array","items":{"type":"string","enum":["accept","reject","skip"]},"description":"One answer per change, starting at the current one"}},"required":["answers"]}""",
                i => ReviewLogic.ParseAnswers(i).Error, (i, d) => engine.Answer(ReviewLogic.ParseAnswers(i).Answers, d)),
            new ReviewTool("review_changes_status",
                "Counts of the review sitting of the open document (accepted, rejected, skipped, remaining) and whether a session exists. Changes nothing.",
                """{"type":"object","properties":{}}""", _ => null, (_, d) => engine.Status(d)),
            BulkTool(engine, "accept_all_changes", true,
                "Accepts every tracked change in the open document that matches the optional author and type. Say the count to the user first and call this only after his clear yes, with confirmed_count equal to that count; otherwise it refuses and tells the count."),
            BulkTool(engine, "reject_all_changes", false,
                "Rejects every tracked change in the open document that matches the optional author and type. Say the count to the user first and call this only after his clear yes, with confirmed_count equal to that count; otherwise it refuses and tells the count."),
        ];
    }

    private static ReviewTool BulkTool(ReviewEngine engine, string name, bool accept, string description) =>
        new(name, description,
            """{"type":"object","properties":{"author":{"type":"string","description":"Optional: only changes by this author (part of the name is enough)"},"type":{"type":"string","enum":["insertion","deletion","formatting"],"description":"Optional: only this kind of change"},"confirmed_count":{"type":"integer","description":"The number of matching changes the user agreed to"}},"required":["confirmed_count"]}""",
            i => ReviewLogic.ParseBulk(i).Error, (i, d) => engine.Bulk(accept, ReviewLogic.ParseBulk(i).Request!, d));
}
