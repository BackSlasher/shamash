using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Shamash.Pc;

/// <summary>
/// Pure decisions and result text of the Word editing tools, kept apart from the COM code so they are testable.
/// Every result names the document it acted on; mutations end with "; undo available".
/// </summary>
public static class WordEditText
{
    public const string NoDocument = "error: no document is open in Word";
    public const int MaxInsertChars = 200;
    public const int MaxFootnoteChars = 1000;
    public const int MaxFindChars = 255;   // Word's Find.Text limit
    public const int MaxUndoSteps = 50;
    public const int DescribeWords = 15;
    public const int SelectionWords = 30;

    /// <summary>The words of a Word range text (paragraph marks, cell marks, vertical tabs and blanks all separate words), cut at <paramref name="max"/> with an ellipsis; double quotes become single so the result can sit inside quotes.</summary>
    public static string FirstWords(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var words = text.Replace('"', '\'').Split([' ', '\r', '\n', '\t', '\v', '\a', '\f', ' '], StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= max ? string.Join(' ', words) : string.Join(' ', words.Take(max)) + "…";
    }

    /// <summary>1-based number of the paragraph that starts at <paramref name="paragraphStart"/>, given how many whole paragraphs lie in the range 0..paragraphStart.</summary>
    public static int ParagraphNumber(int paragraphStart, int paragraphsBefore) => paragraphStart <= 0 ? 1 : paragraphsBefore + 1;

    public static string YesNo(bool b) => b ? "yes" : "no";
    public static string OnOff(bool b) => b ? "on" : "off";
    public static string Num(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    public static string DocumentTag(string name) => $"document: {name}";

    /// <summary>The closing part of every mutation result: the document, the backup note when this call took one (or could not), and the undo hint.</summary>
    public static string Mutation(string body, string documentName, string backupNote, bool undoable = true) =>
        $"{body}; {DocumentTag(documentName)}" + (backupNote.Length > 0 ? $"; {backupNote}" : "") + (undoable ? "; undo available" : "; not an undo step (set it back with set_track_changes)");

    public static string Describe(DocFacts f) =>
        $"{DocumentTag(f.Name)}; path: {f.FullPath}; saved: {YesNo(f.Saved)}; track changes: {OnOff(f.TrackChanges)}; revisions: {f.Revisions}; paragraphs: {f.Paragraphs}; " +
        $"cursor: paragraph {f.CursorParagraph} of {f.Paragraphs}, starts \"{FirstWords(f.CursorParagraphText, DescribeWords)}\"; " +
        $"selection: {(FirstWords(f.SelectionText, SelectionWords) is { Length: > 0 } s ? $"\"{s}\"" : "none")}; footnotes: {f.Footnotes}";

    // ---- undo

    public static (int Count, string? Error) ParseUndoCount(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("count", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var n)) return (0, "error: count must be a whole number");
            if (n < 1 || n > MaxUndoSteps) return (0, $"error: count must be between 1 and {MaxUndoSteps}");
            return (n, null);
        }
        return (1, null);
    }

    public static string UndoResult(int count, string documentName) =>
        $"undone: {count} {(count == 1 ? "step" : "steps")}; {DocumentTag(documentName)}";

    public const string NothingToUndo = "error: nothing to undo";

    // ---- track changes

    public static (bool On, string? Error) ParseTrack(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("on", out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return (p.GetBoolean(), null);
        return (false, "error: on is required (true or false)");
    }

    public static string TrackResult(bool was, bool now) => $"track changes: was {OnOff(was)}, now {OnOff(now)}";

    // ---- paragraph spacing

    public const int LineSingle = 0, LineOnePointFive = 1, LineDouble = 2;   // WdLineSpacing: 3 = at least, 4 = exactly, 5 = multiple
    public const int LineMixed = 9999999;                                       // wdUndefined

    public sealed record SpacingRequest(string Scope, string? LineSpacing, double? SpaceAfter)
    {
        public int? Rule => LineSpacing switch { "single" => LineSingle, "1.5" => LineOnePointFive, "double" => LineDouble, _ => null };
    }

    public static (SpacingRequest? Request, string? Error) ParseSpacing(JsonElement input)
    {
        var scope = ToolText.GetString(input, "scope")?.Trim().ToLowerInvariant();
        if (scope is not ("selection" or "paragraph" or "document")) return (null, "error: scope must be selection, paragraph or document");
        string? line = null;
        if (input.TryGetProperty("line_spacing", out var l) && l.ValueKind != JsonValueKind.Null)
        {
            line = l.ValueKind == JsonValueKind.String ? l.GetString()?.Trim().ToLowerInvariant() : null;
            if (line is not ("single" or "1.5" or "double")) return (null, "error: line_spacing must be single, 1.5 or double");
        }
        double? after = null;
        if (input.TryGetProperty("space_after_points", out var a) && a.ValueKind != JsonValueKind.Null)
        {
            if (a.ValueKind != JsonValueKind.Number || !a.TryGetDouble(out var d) || double.IsNaN(d) || d < 0 || d > 1584)
                return (null, "error: space_after_points must be a number from 0 to 1584");
            after = d;
        }
        if (line is null && after is null) return (null, "error: give line_spacing, space_after_points or both");
        return (new SpacingRequest(scope, line, after), null);
    }

    public static string LineRuleName(int rule) => rule switch
    {
        LineSingle => "single",
        LineOnePointFive => "1.5",
        LineDouble => "double",
        3 => "at least",
        4 => "exactly",
        5 => "multiple",
        _ => "mixed",
    };

    /// <summary>"paragraph 3", "paragraphs 2 to 4" or "the whole document (6 paragraphs)".</summary>
    public static string ScopeLabel(string scope, int first, int last, int total) =>
        scope == "document" ? $"the whole document ({total} {(total == 1 ? "paragraph" : "paragraphs")})"
        : first == last ? $"paragraph {first}" : $"paragraphs {first} to {last}";

    public static string SpacingResult(SpacingRequest req, int ruleBefore, int ruleAfter, double afterBefore, double afterAfter, string scopeLabel)
    {
        var parts = new List<string>();
        if (req.Rule is not null) parts.Add($"line spacing: {LineRuleName(ruleAfter)} (was {LineRuleName(ruleBefore)})");
        if (req.SpaceAfter is not null) parts.Add($"space after: {Num(afterAfter)} pt (was {Num(afterBefore)} pt)");
        return $"{string.Join(", ", parts)} for {scopeLabel}";
    }

    public static bool SameSpace(double a, double b) => Math.Abs(a - b) < 0.1;

    // ---- text insertion

    /// <summary>Validates text for Selection.TypeText: at most 200 characters, one paragraph break at most, no other control characters. Returns the text with the break as a Word paragraph mark.</summary>
    public static (string? Text, string? Error) ValidateInsert(string? text)
    {
        if (string.IsNullOrEmpty(text)) return (null, "error: text is required");
        if (text.Length > MaxInsertChars) return (null, $"error: text is too long ({text.Length} characters, the limit is {MaxInsertChars})");
        var t = text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (t.Count(c => c == '\n') > 1) return (null, "error: more than one paragraph break; insert one paragraph at a time");
        if (t.Any(c => char.IsControl(c) && c is not ('\n' or '\t'))) return (null, "error: text contains control characters");
        return (t.Replace('\n', '\r'), null);
    }

    public static string InsertResult(string text, int paragraph) =>
        $"inserted \"{text.Replace("\r", "⏎").Replace('"', '\'')}\" at paragraph {paragraph}";

    public const string SelectionNotEmpty = "error: text is selected, so typing would replace it; ask the user to click where the text should go";

    // ---- footnote

    public static (string? Text, string? AfterWords, string? Error) ValidateFootnote(string? text, string? afterWords)
    {
        if (string.IsNullOrWhiteSpace(text)) return (null, null, "error: text is required");
        text = text.Trim();
        if (text.Length > MaxFootnoteChars) return (null, null, $"error: text is too long ({text.Length} characters, the limit is {MaxFootnoteChars})");
        if (text.Any(c => char.IsControl(c) && c != '\t')) return (null, null, "error: the footnote text must be one line without control characters");
        var after = string.IsNullOrWhiteSpace(afterWords) ? null : afterWords.Trim();
        if (after is { Length: > MaxFindChars }) return (null, null, $"error: after_words is too long (the limit is {MaxFindChars} characters)");
        if (after is not null && after.Any(char.IsControl)) return (null, null, "error: after_words must be plain words on one line");
        return (text, after, null);
    }

    /// <summary>Word's Find treats ^ as the start of a special code; ^^ is a literal caret.</summary>
    public static string EscapeFind(string words) => words.Replace("^", "^^");

    public static string FootnoteNotFound(string words) => $"error: \"{words}\" not found in the current paragraph";

    public static string FootnoteResult(int index, string? afterWords, int paragraph) =>
        $"footnote {index} added " + (afterWords is null ? "at the cursor" : $"after \"{afterWords.Replace('"', '\'')}\"") + $" in paragraph {paragraph}";
}

/// <summary>What describe_active_document reads from Word.</summary>
public sealed record DocFacts(string Name, string FullPath, bool Saved, bool TrackChanges, int Revisions, int Paragraphs, int CursorParagraph, string CursorParagraphText, string? SelectionText, int Footnotes);

/// <summary>
/// Copy of a document taken before the first edit of it in this process, under <c>%LocalAppData%\Shamash\backups</c> as
/// <c>yyyyMMdd-HHmmss name</c>, and the restore side: <c>name (לפני שמש).ext</c> next to the original, never overwriting.
/// Nothing here deletes.
/// </summary>
public static class WordBackups
{
    public const string RestoreSuffix = " (לפני שמש)";
    public const string NeverSaved = "backup: none (document never saved)";

    private static readonly HashSet<string> Done = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Lock = new();

    public static string DefaultDir()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(local) ? "." : local, "Shamash", "backups");
    }

    public static string BackupName(DateTime now, string fileName) => $"{now:yyyyMMdd-HHmmss} {fileName}";

    /// <summary>Forgets which documents were backed up (tests).</summary>
    public static void Reset() { lock (Lock) Done.Clear(); }

    /// <summary>
    /// Takes the backup unless this document already got one in this process. Returns the note for the result: "" when nothing new
    /// happened, "backup: &lt;path&gt;" when a copy was made, <see cref="NeverSaved"/> when there is no file on disk. Throws IOException
    /// when the copy fails, so the caller does not edit.
    /// </summary>
    public static string EnsureBackup(string fullPath, string? dir = null, DateTime? now = null)
    {
        var key = DocumentIdentity.Normalize(fullPath);
        lock (Lock)
        {
            if (Done.Contains(key)) return "";
            if (!Path.IsPathRooted(fullPath) || !File.Exists(fullPath)) return NeverSaved;
            dir ??= DefaultDir();
            Directory.CreateDirectory(dir);
            var target = Path.Combine(dir, BackupName(now ?? DateTime.Now, Path.GetFileName(fullPath)));
            if (!File.Exists(target)) File.Copy(fullPath, target, overwrite: false);
            Done.Add(key);
            return $"backup: {target}";
        }
    }

    /// <summary>The newest backup of a file with this name, or null.</summary>
    public static string? Newest(string fileName, string? dir = null)
    {
        dir ??= DefaultDir();
        if (!Directory.Exists(dir)) return null;
        var re = new Regex(@"^\d{8}-\d{6} " + Regex.Escape(fileName) + "$", RegexOptions.IgnoreCase);
        return Directory.EnumerateFiles(dir).Where(f => re.IsMatch(Path.GetFileName(f))).OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
    }

    /// <summary>"dir\stem (לפני שמש).ext", then "... 2.ext", "... 3.ext" while the name exists.</summary>
    public static string RestoreTarget(string originalPath, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var dir = Path.GetDirectoryName(originalPath) ?? "";
        var stem = Path.GetFileNameWithoutExtension(originalPath);
        var ext = Path.GetExtension(originalPath);
        var candidate = Path.Combine(dir, stem + RestoreSuffix + ext);
        for (var n = 2; exists(candidate); n++) candidate = Path.Combine(dir, $"{stem}{RestoreSuffix} {n}{ext}");
        return candidate;
    }

    /// <summary>Copies the newest backup next to the original. Error text when there is none.</summary>
    public static string Restore(string originalPath, string? dir = null)
    {
        var name = Path.GetFileName(originalPath);
        var backup = Newest(name, dir);
        if (backup is null) return $"error: no backup of \"{name}\" was taken by Shamash";
        var target = RestoreTarget(originalPath);
        try { File.Copy(backup, target, overwrite: false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"error: could not write the restored copy: {e.Message}"; }
        return $"restored the backup of \"{name}\" to {target}; the original was not touched";
    }
}

/// <summary>Attaches to the running Word (never starts one) and hands over the active document.</summary>
[SupportedOSPlatform("windows")]
internal static class ActiveWord
{
    /// <summary>Runs <paramref name="f"/>(app, ActiveDocument), or says there is no document.</summary>
    public static string With(Func<dynamic, dynamic, string> f)
    {
        dynamic? app = ComInterop.GetActive("Word.Application");
        if (app is null) return WordEditText.NoDocument;
        dynamic? docs = null, doc = null;
        try
        {
            docs = app.Documents;
            if ((int)docs.Count == 0) return WordEditText.NoDocument;
            doc = app.ActiveDocument;
            return f(app, doc);
        }
        finally { ComInterop.Release(doc, docs, app); }
    }

    /// <summary>Runs <paramref name="f"/> as ONE Word undo step (Application.UndoRecord, Word 2010+): without it Footnotes.Add and two ParagraphFormat assignments are several steps each and "undo" took back half an edit. Falls back to no record when Word refuses.</summary>
    public static string InUndoRecord(dynamic app, string name, Func<string> f)
    {
        dynamic? rec = null;
        bool started = false;
        try { rec = app.UndoRecord; rec!.StartCustomRecord(name); started = true; } catch { }
        try { return f(); }
        finally
        {
            if (started) { try { rec!.EndCustomRecord(); } catch { } }
            ComInterop.Release(rec);
        }
    }

    /// <summary>1-based number of the paragraph holding position <paramref name="pos"/>.</summary>
    public static int ParagraphAt(dynamic doc, int pos)
    {
        dynamic? r = null, ps = null, p = null, pr = null, before = null, bps = null;
        try
        {
            r = doc.Range(pos, pos);
            ps = r.Paragraphs;
            p = ps.Item(1);
            pr = p.Range;
            int start = pr.Start;
            if (start <= 0) return 1;
            before = doc.Range(0, start);
            bps = before.Paragraphs;
            return WordEditText.ParagraphNumber(start, (int)bps.Count);
        }
        finally { ComInterop.Release(bps, before, pr, p, ps, r); }
    }

    public static string Name(dynamic doc) => (string)doc.Name;

    /// <summary>Backup before the first edit; the note for the result.</summary>
    public static string Backup(dynamic doc) => WordBackups.EnsureBackup((string)doc.FullName);
}

/// <summary>Shared shape of the editing tools: validation first (no COM), then the guarded COM call.</summary>
public abstract class WordEditTool : IAgentTool
{
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string InputSchema { get; }

    /// <summary>Error text for bad input, null when the input is fine. Runs before any COM call.</summary>
    protected virtual string? Validate(JsonElement input) => null;

    /// <summary>True for tools that change the document through several object-model calls: they run inside one undo record so undo_last_edit steps match tool calls.</summary>
    protected virtual bool OneUndoStep => false;

    [SupportedOSPlatform("windows")]
    protected abstract string Execute(JsonElement input, dynamic app, dynamic doc);

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        if (Validate(input) is { } bad) return bad;
        if (!OperatingSystem.IsWindows()) return OfficeCom.WindowsOnly;
        return Attach(input);
    }, ct);

    [SupportedOSPlatform("windows")]
    private string Attach(JsonElement input) => OfficeCom.Guarded(() => ActiveWord.With((app, doc) =>
        OneUndoStep ? ActiveWord.InUndoRecord(app, "Shamash", (Func<string>)(() => Execute(input, app, doc))) : Execute(input, app, doc)));
}

public sealed class DescribeActiveDocumentTool(Func<bool>? recoveryPane = null) : WordEditTool
{
    public override string Name => "describe_active_document";
    public override string Description =>
        "Reads the document open in the running Word (never starts Word, changes nothing): name, path, saved, track changes, revisions, paragraphs, the paragraph under the cursor (first words) and the selected text. " +
        "This is the 'here' that all editing tools act on. Use it for 'what is open now', 'where am I in the document'.";
    public override string InputSchema => """{"type":"object","properties":{}}""";

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        dynamic? sel = null, paras = null, p = null, pr = null, revs = null, notes = null, sr = null;
        try
        {
            sel = app.Selection;
            int start = sel.Start, end = sel.End;
            string? selected = null;
            if (end > start) { sr = sel.Range; selected = sr.Text; }
            paras = sel.Paragraphs;
            p = paras.Item(1);
            pr = p.Range;
            revs = doc.Revisions;
            notes = doc.Footnotes;
            dynamic all = doc.Paragraphs;
            int total = all.Count;
            ComInterop.Release(all);
            var facts = new DocFacts((string)doc.Name, (string)doc.FullName, (bool)doc.Saved, (bool)doc.TrackRevisions, (int)revs.Count, total,
                ActiveWord.ParagraphAt(doc, start), (string)pr.Text, selected, (int)notes.Count);
            return WordEditText.Describe(facts) + WordRecovery.Note(recoveryPane);
        }
        finally { ComInterop.Release(sr, notes, revs, pr, p, paras, sel); }
    }
}

public sealed class UndoLastEditTool : WordEditTool
{
    public override string Name => "undo_last_edit";
    public override string Description =>
        "Undoes the last edit(s) in the document open in Word (ActiveDocument.Undo), like Ctrl+Z. count defaults to 1. Use for 'תבטל', 'תחזיר את זה כמו שהיה'.";
    public override string InputSchema => """{"type":"object","properties":{"count":{"type":"integer","description":"How many steps to undo, default 1"}}}""";

    protected override string? Validate(JsonElement input) => WordEditText.ParseUndoCount(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var count = WordEditText.ParseUndoCount(input).Count;
        bool ok = doc.Undo(count);
        return ok ? WordEditText.UndoResult(count, ActiveWord.Name(doc)) : WordEditText.NothingToUndo;
    }
}

public sealed class RestoreBackupTool(IReadOnlyList<string> roots) : IAgentTool
{
    public string Name => "restore_backup";
    public string Description =>
        "Writes the newest Shamash backup of the open Word document (or of the document in path) next to the original as '<name> (לפני שמש).<ext>'. Never overwrites or deletes anything; the original is not touched. Returns the new path.";
    public string InputSchema => """{"type":"object","properties":{"path":{"type":"string","description":"Optional. Full path or folder/name of the document; default is the document open in Word"}}}""";

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var path = ToolText.GetString(input, "path");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var full = ReadDocumentTool.Resolve(path, roots);
            return full is null ? "error: path not under a document root" : WordBackups.Restore(full);
        }
        if (!OperatingSystem.IsWindows()) return OfficeCom.WindowsOnly;
        return Active();
    }, ct);

    [SupportedOSPlatform("windows")]
    private static string Active() => OfficeCom.Guarded(() => ActiveWord.With((app, doc) =>
    {
        var full = (string)doc.FullName;
        if (!Path.IsPathRooted(full)) return "error: the document was never saved, so there is no backup of it";
        return WordBackups.Restore(full);
    }));
}

public sealed class SetTrackChangesTool : WordEditTool
{
    public override string Name => "set_track_changes";
    public override string Description =>
        "Turns track changes on or off in the document open in Word (ActiveDocument.TrackRevisions). Existing revisions are never accepted or rejected. Say the state before and after, and that it can be undone.";
    public override string InputSchema => """{"type":"object","properties":{"on":{"type":"boolean","description":"true to turn tracking on, false to turn it off"}},"required":["on"]}""";

    protected override string? Validate(JsonElement input) => WordEditText.ParseTrack(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var want = WordEditText.ParseTrack(input).On;
        var backup = ActiveWord.Backup(doc);
        bool was = doc.TrackRevisions;
        doc.TrackRevisions = want;
        bool now = doc.TrackRevisions;
        if (now != want) return $"error: track changes stayed {WordEditText.OnOff(now)} (is the document protected?); document: {ActiveWord.Name(doc)}";
        // Verified on the VM (Word 2016): the toggle is not on Word's undo stack, so undo_last_edit would undo something else or answer "nothing to undo".
        return WordEditText.Mutation(WordEditText.TrackResult(was, now), ActiveWord.Name(doc), backup, undoable: false);
    }
}

public sealed class SetParagraphSpacingTool : WordEditTool
{
    public override string Name => "set_paragraph_spacing";
    public override string Description =>
        "Sets line spacing (single, 1.5, double) and/or space after (points) of the selection, the paragraph under the cursor, or the whole document open in Word. Touches nothing else. " +
        "For scope document ask the user for a clear yes first. Reads the value back and reports it with the previous value.";
    public override string InputSchema => """
        {"type":"object","properties":{"scope":{"type":"string","enum":["selection","paragraph","document"]},"line_spacing":{"type":"string","enum":["single","1.5","double"]},"space_after_points":{"type":"number","description":"Space after each paragraph in points"}},"required":["scope"]}
        """;

    protected override bool OneUndoStep => true;

    protected override string? Validate(JsonElement input) => WordEditText.ParseSpacing(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var req = WordEditText.ParseSpacing(input).Request!;
        dynamic? sel = null, range = null, pf = null, pf2 = null;
        try
        {
            sel = app.Selection;
            int selStart = sel.Start, selEnd = sel.End;
            switch (req.Scope)
            {
                case "selection":
                    if (selEnd <= selStart) return "error: nothing is selected; use scope paragraph or document";
                    range = sel.Range;
                    break;
                case "paragraph":
                    dynamic sp = sel.Paragraphs; dynamic first = sp.Item(1);
                    range = first.Range;
                    ComInterop.Release(first, sp);
                    break;
                default:
                    range = doc.Content;
                    break;
            }
            int rStart = range.Start, rEnd = range.End;
            int firstPara = ActiveWord.ParagraphAt(doc, rStart), lastPara = ActiveWord.ParagraphAt(doc, Math.Max(rStart, rEnd - 1));
            dynamic all = doc.Paragraphs; int total = all.Count; ComInterop.Release(all);
            var label = WordEditText.ScopeLabel(req.Scope, firstPara, lastPara, total);

            var backup = ActiveWord.Backup(doc);
            pf = range.ParagraphFormat;
            int ruleBefore = (int)pf.LineSpacingRule;
            double afterBefore = (double)pf.SpaceAfter;
            if (req.Rule is { } rule) pf.LineSpacingRule = rule;
            if (req.SpaceAfter is { } sa) pf.SpaceAfter = (float)sa;
            pf2 = range.ParagraphFormat;   // read back
            int ruleAfter = (int)pf2.LineSpacingRule;
            double afterAfter = (double)pf2.SpaceAfter;
            if (req.Rule is { } r2 && ruleAfter != r2) return $"error: line spacing reads back as {WordEditText.LineRuleName(ruleAfter)}, not {req.LineSpacing}; {WordEditText.DocumentTag(ActiveWord.Name(doc))}";
            if (req.SpaceAfter is { } s2 && !WordEditText.SameSpace(afterAfter, s2)) return $"error: space after reads back as {WordEditText.Num(afterAfter)} pt, not {WordEditText.Num(s2)}; {WordEditText.DocumentTag(ActiveWord.Name(doc))}";
            return WordEditText.Mutation(WordEditText.SpacingResult(req, ruleBefore, ruleAfter, afterBefore, afterAfter, label), ActiveWord.Name(doc), backup);
        }
        finally { ComInterop.Release(pf2, pf, range, sel); }
    }
}

public sealed class InsertTextAtCursorTool : WordEditTool
{
    public override string Name => "insert_text_at_cursor";
    public override string Description =>
        "Types text (up to 200 characters, at most one paragraph break) at the cursor of the document open in Word, e.g. a special character such as ©, é, ∑, → or a short phrase. " +
        "Name the character to the user first and call this only after he said yes. Refuses when text is selected (typing would replace it).";
    public override string InputSchema => """{"type":"object","properties":{"text":{"type":"string","description":"The exact text to type, e.g. the character itself"}},"required":["text"]}""";

    protected override bool OneUndoStep => true;

    protected override string? Validate(JsonElement input) => WordEditText.ValidateInsert(ToolText.GetString(input, "text")).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var text = WordEditText.ValidateInsert(ToolText.GetString(input, "text")).Text!;
        dynamic? sel = null;
        try
        {
            sel = app.Selection;
            int start = sel.Start, end = sel.End;
            if (end > start) return WordEditText.SelectionNotEmpty;
            int paragraph = ActiveWord.ParagraphAt(doc, start);
            var backup = ActiveWord.Backup(doc);
            sel.TypeText(text);
            return WordEditText.Mutation(WordEditText.InsertResult(text, paragraph), ActiveWord.Name(doc), backup);
        }
        finally { ComInterop.Release(sel); }
    }
}

public sealed class AddFootnoteTool : WordEditTool
{
    public override string Name => "add_footnote";
    public override string Description =>
        "Adds a footnote to the document open in Word. With after_words the reference goes right after those words inside the paragraph under the cursor (case-insensitive); otherwise at the cursor (after the selection if any). " +
        "Read the footnote text back to the user and call this only after he said yes.";
    public override string InputSchema => """
        {"type":"object","properties":{"text":{"type":"string","description":"The footnote text"},"after_words":{"type":"string","description":"Optional: put the footnote reference after these words in the current paragraph"}},"required":["text"]}
        """;

    protected override bool OneUndoStep => true;

    protected override string? Validate(JsonElement input) => WordEditText.ValidateFootnote(ToolText.GetString(input, "text"), ToolText.GetString(input, "after_words")).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var (text, after, _) = WordEditText.ValidateFootnote(ToolText.GetString(input, "text"), ToolText.GetString(input, "after_words"));
        dynamic? sel = null, rng = null, find = null, notes = null, note = null;
        try
        {
            sel = app.Selection;
            if (after is not null)
            {
                dynamic paras = sel.Paragraphs, p = paras.Item(1), pr = p.Range;
                int ps = pr.Start, pe = pr.End;
                ComInterop.Release(pr, p, paras);
                rng = doc.Range(ps, pe);
                find = rng.Find;
                find.ClearFormatting();
                bool found = find.Execute(FindText: WordEditText.EscapeFind(after), MatchCase: false, MatchWholeWord: false, MatchWildcards: false, Forward: true, Wrap: 0, Format: false);
                if (!found) return WordEditText.FootnoteNotFound(after);
                rng.Collapse(0);   // wdCollapseEnd: the reference goes after the found words
            }
            else
            {
                rng = sel.Range;
                rng.Collapse(0);
            }
            int paragraph = ActiveWord.ParagraphAt(doc, (int)rng.Start);
            var backup = ActiveWord.Backup(doc);
            notes = doc.Footnotes;
            note = notes.Add(Range: rng, Text: text);
            int index = note.Index;
            return WordEditText.Mutation(WordEditText.FootnoteResult(index, after, paragraph), ActiveWord.Name(doc), backup);
        }
        finally { ComInterop.Release(note, notes, find, rng, sel); }
    }
}

public static class WordEditTools
{
    public static IEnumerable<IAgentTool> Create(IReadOnlyList<string> roots, Func<bool>? recoveryPane = null) =>
    [
        new DescribeActiveDocumentTool(recoveryPane),
        new UndoLastEditTool(),
        new RestoreBackupTool(roots),
        new SetTrackChangesTool(),
        new SetParagraphSpacingTool(),
        new InsertTextAtCursorTool(),
        new AddFootnoteTool(),
    ];
}
