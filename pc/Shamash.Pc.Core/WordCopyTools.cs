using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>
/// Pure decisions and result text of "copy a paragraph from another document, spotted together on screen": which paragraphs are
/// candidates, how a hint moves the highlight, what is said. No COM here, so it is all testable off Windows.
/// </summary>
public static class WordCopyText
{
    public const int DescribeWords = 12;
    public const string NoSession = "error: no paragraph copy is in progress; call copy_paragraph_start first";
    public const string Exhausted = "no more candidates; ask him for other words or the paragraph number";
    public const string SourceIsTarget = "error: the source is the document being edited; name another document";
    public const string SourceClosed = "error: the source document was closed; start again with copy_paragraph_start";
    public static readonly string[] SourceExtensions = [".docx", ".doc", ".docm", ".rtf"];

    public sealed record StartRequest(string Source, int? ParagraphNumber, string? Words);

    /// <summary>source is required; paragraph_number (a whole number from 1) wins over words when both are given.</summary>
    public static (StartRequest? Request, string? Error) ParseStart(JsonElement input)
    {
        var source = ToolText.GetString(input, "source");
        if (string.IsNullOrWhiteSpace(source)) return (null, "error: source is required");
        int? number = null;
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("paragraph_number", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetInt32(out var n) || n < 1) return (null, "error: paragraph_number must be a whole number from 1");
            number = n;
        }
        var (words, bad) = CleanWords(ToolText.GetString(input, "words"), "words");
        if (bad is not null) return (null, bad);
        return (new StartRequest(source.Trim(), number, number is null ? words : null), null);
    }

    private static (string? Words, string? Error) CleanWords(string? raw, string field)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (null, null);
        var w = raw.Trim();
        if (w.Length > WordEditText.MaxFindChars) return (null, $"error: {field} is too long (the limit is {WordEditText.MaxFindChars} characters)");
        if (w.Any(char.IsControl)) return (null, $"error: {field} must be plain words on one line");
        return (w, null);
    }

    public static (string? Hint, string? Error) ParseNext(JsonElement input)
    {
        var (hint, bad) = CleanWords(ToolText.GetString(input, "hint"), "hint");
        return bad is null ? (hint, null) : (null, bad);
    }

    public static (bool Plain, string? Error) ParseConfirm(JsonElement input)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("plain", out var p) && p.ValueKind != JsonValueKind.Null)
            return p.ValueKind is JsonValueKind.True or JsonValueKind.False ? (p.GetBoolean(), null) : (false, "error: plain must be true or false");
        return (false, null);
    }

    /// <summary>The full path of the source under the document roots, or an error. Only Word documents can be a source.</summary>
    public static (string? Path, string? Error) ResolveSource(string source, IReadOnlyList<string> roots)
    {
        var full = PathResolver.ResolveFile(source, roots);
        if (full is null) return (null, "error: path not under a document root");
        if (!File.Exists(full)) return (null, "error: file not found");
        if (!SourceExtensions.Contains(System.IO.Path.GetExtension(full), StringComparer.OrdinalIgnoreCase))
            return (null, "error: only Word documents (.docx, .doc, .rtf) can be a copy source");
        return (full, null);
    }

    // ---- paragraph texts and matching

    /// <summary>Paragraph texts of a Word story text (Content.Text): paragraphs end with \r, cell and row ends with \r\a, which still count as one paragraph each.</summary>
    public static List<string> ParagraphTexts(string? contentText)
    {
        if (string.IsNullOrEmpty(contentText)) return [];
        var parts = contentText.Replace("\r\a", "\r").Split('\r').ToList();
        if (parts.Count > 0 && parts[^1].Length == 0) parts.RemoveAt(parts.Count - 1);
        return parts;
    }

    public static bool IsBlank(string? text) => string.IsNullOrEmpty(text) || text.All(c => char.IsWhiteSpace(c) || c is '\a' or '\f' or ' ');

    /// <summary>Case-insensitive, Hebrew-safe form: niqqud and cantillation dropped, quotes, geresh and gershayim dropped (so העות'מאנית matches העותמאנית), hyphens and maqaf are blanks.</summary>
    public static string Normalize(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '"' or '’' or '‘' or '׳' or '״' or '“' or '”' or '„') continue;
            sb.Append(c is '-' or '־' or '‐' or '‑' or '–' or '—' || char.IsWhiteSpace(c) || c is '\a' or '\v' ? ' ' : char.ToLowerInvariant(c));
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    public static string[] Tokens(string? words) => Normalize(words).Split(' ', StringSplitOptions.RemoveEmptyEntries);

    /// <summary>True when every token occurs in the paragraph (any order).</summary>
    public static bool Matches(string paragraph, IReadOnlyList<string> tokens)
    {
        if (tokens.Count == 0) return false;
        var n = Normalize(paragraph);
        return tokens.All(t => n.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>1-based paragraph numbers in document order: that one paragraph by number, the paragraphs matching the words, or every non-empty paragraph.</summary>
    public static (int[] Candidates, string? Error) FindCandidates(IReadOnlyList<string> texts, int? number, string? words)
    {
        if (number is { } n)
        {
            if (n > texts.Count) return ([], $"error: the document has {texts.Count} paragraphs; there is no paragraph {n}");
            return IsBlank(texts[n - 1]) ? ([], $"error: paragraph {n} is empty") : ([n], null);
        }
        var all = Enumerable.Range(1, texts.Count).Where(i => !IsBlank(texts[i - 1])).ToArray();
        if (!string.IsNullOrWhiteSpace(words))
        {
            var tokens = Tokens(words);
            var hits = all.Where(i => Matches(texts[i - 1], tokens)).ToArray();
            return hits.Length == 0 ? ([], $"error: no paragraph contains \"{words.Replace('"', '\'')}\"") : (hits, null);
        }
        return all.Length == 0 ? ([], "error: the document has no text paragraphs") : (all, null);
    }

    // ---- hints

    public enum Move { Forward, Back, Narrow }
    public sealed record Hint(Move Move, string[] Words);

    private static readonly HashSet<string> DownWords = new(StringComparer.Ordinal) { "למטה", "מטה", "הבאה", "הבא", "down", "below", "next", "lower" };
    private static readonly HashSet<string> UpWords = new(StringComparer.Ordinal) { "למעלה", "מעלה", "הקודמת", "קודמת", "הקודם", "up", "above", "previous", "back", "higher" };
    private static readonly HashSet<string> FillerWords = new(StringComparer.Ordinal) { "הפסקה", "פסקה", "עם", "שיש", "בה", "שמופיע", "שמופיעה", "יותר", "paragraph", "the", "with" };

    /// <summary>No hint or a bare "next" moves forward; "יותר למטה" forward, "יותר למעלה" back; anything else is words that narrow the list.</summary>
    public static Hint ParseHint(string? hint)
    {
        var tokens = Tokens(hint);
        if (tokens.Any(UpWords.Contains)) return new Hint(Move.Back, []);
        if (tokens.Length == 0 || tokens.Any(DownWords.Contains)) return new Hint(Move.Forward, []);
        var content = tokens.Where(t => !FillerWords.Contains(t)).ToArray();
        return content.Length == 0 ? new Hint(Move.Forward, []) : new Hint(Move.Narrow, content);
    }

    /// <summary>Candidates after the move, the new index, and a message when there is nowhere to go (then nothing changes).</summary>
    public sealed record NavResult(int[] Candidates, int Index, string? Message);

    public static NavResult Navigate(IReadOnlyList<string> texts, int[] candidates, int index, string? hint)
    {
        var h = ParseHint(hint);
        switch (h.Move)
        {
            case Move.Forward:
                return index + 1 < candidates.Length ? new NavResult(candidates, index + 1, null) : new NavResult(candidates, index, Exhausted);
            case Move.Back:
                return index - 1 >= 0 ? new NavResult(candidates, index - 1, null) : new NavResult(candidates, index, Exhausted);
            default:
                var current = candidates[index];
                var narrowed = candidates.Where(c => c != current && c <= texts.Count && Matches(texts[c - 1], h.Words)).ToArray();
                if (narrowed.Length == 0)
                    narrowed = Enumerable.Range(1, texts.Count).Where(i => i != current && !IsBlank(texts[i - 1]) && Matches(texts[i - 1], h.Words)).ToArray();
                return narrowed.Length == 0
                    ? new NavResult(candidates, index, $"error: no other paragraph contains \"{string.Join(' ', h.Words).Replace('"', '\'')}\"; {Exhausted}")
                    : new NavResult(narrowed, 0, null);
        }
    }

    // ---- result text

    public static string Describe(int index, int count, string sourceName, int paragraph, string paragraphText) =>
        $"candidate {index + 1} of {count} in \"{sourceName}\": paragraph {paragraph} starts \"{WordEditText.FirstWords(paragraphText, DescribeWords)}\"; ask him \"זאת?\"";

    public static string Pasted(int paragraph, string sourceName, string targetName, int at, bool plain) =>
        $"pasted paragraph {paragraph} of \"{sourceName}\" into \"{targetName}\" at paragraph {at}" + (plain ? " without formatting" : "");

    public static string SourceDirtyWarning(string sourceName) => $"warning: \"{sourceName}\" reports unsaved changes; it was not saved";

    public static string Cancelled(string targetName, bool targetOpen) =>
        targetOpen ? $"cancelled; back in \"{targetName}\", nothing was pasted" : $"cancelled; \"{targetName}\" is no longer open, nothing was pasted";

    public const string NothingToCancel = "no paragraph copy was in progress";

    /// <summary>The cursor remembered at the start, kept inside the document (the last position is before the final paragraph mark).</summary>
    public static int ClampCursor(int cursor, int contentEnd) => Math.Clamp(cursor, 0, Math.Max(0, contentEnd - 1));

    /// <summary>Text for Range.InsertAfter: paragraph, cell and row marks become plain paragraph marks.</summary>
    public static string PlainText(string? raw) => (raw ?? "").Replace("\r\a", "\r").Replace("\a", "");

    /// <summary>Where the cursor goes after the paste: just after what was added.</summary>
    public static int CursorAfter(int position, int contentEndBefore, int contentEndAfter) => position + Math.Max(0, contentEndAfter - contentEndBefore);
}

/// <summary>What copy_paragraph_start remembers between calls (the model forgets between turns, the file does not).</summary>
public sealed record CopySession(string Target, int Cursor, string Source, bool OpenedHere, int[] Candidates, int Index);

/// <summary>One small JSON state file under %LocalAppData%\Shamash. A missing or damaged file reads as no state.</summary>
public sealed class SessionFile<T>(string path) where T : class
{
    public static string DefaultPath(string fileName)
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrEmpty(local) ? "." : local, "Shamash", fileName);
    }

    public string FilePath => path;

    public T? Load()
    {
        try { return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null; }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    public void Save(T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    public void Clear()
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

/// <summary>Word COM pieces shared by the copy and picture tools.</summary>
[SupportedOSPlatform("windows")]
internal static class WordDocs
{
    /// <summary>The open Document with this full path (caller releases it) or null.</summary>
    public static dynamic? Find(dynamic docs, string path)
    {
        int count = docs.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic d = docs.Item(i);
            string name = d.FullName;
            if (DocumentIdentity.SamePath(name, path)) return d;
            // not released: Word hands out one RCW per document, and a non-matching one may be the ActiveDocument that ActiveWord.With still holds
        }
        return null;
    }

    /// <summary>Brings the document's window to the front. Never throws: a failed raise only means the window stays behind.</summary>
    public static void Raise(dynamic app, dynamic doc)
    {
        try { doc.Activate(); } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        try { app.Visible = true; } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        try { app.Activate(); } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
    }

    public static void ScrollTo(dynamic app, dynamic range)
    {
        dynamic? w = null;
        try { w = app.ActiveWindow; w.ScrollIntoView(range, true); }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { ComInterop.Release(w); }
    }

    /// <summary>Runs <paramref name="f"/> as one undo step when Word has Application.UndoRecord (2010 and later); otherwise plainly.</summary>
    public static T OneUndoStep<T>(object app, string name, Func<T> f)
    {
        dynamic a = app;
        dynamic? ur = null;
        var started = false;
        try { ur = a.UndoRecord; ur.StartCustomRecord(name); started = true; }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        try { return f(); }
        finally
        {
            if (started) { try { ur!.EndCustomRecord(); } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { } }
            ComInterop.Release(ur);
        }
    }

    [SupportedOSPlatform("windows")]
    public static List<string> ReadTexts(dynamic src)
    {
        dynamic? content = null, ps = null;
        try
        {
            content = src.Content;
            string text = content.Text;
            var texts = WordCopyText.ParagraphTexts(text);
            ps = src.Paragraphs;
            int count = ps.Count;
            if (texts.Count == count) return texts;
            // Word counts paragraphs in a way the text split did not reproduce (rare): read them one by one.
            texts = [];
            for (var i = 1; i <= count; i++)
            {
                dynamic p = ps.Item(i), r = p.Range;
                texts.Add(((string)r.Text).TrimEnd('\r', '\a'));
                ComInterop.Release(r, p);
            }
            return texts;
        }
        finally { ComInterop.Release(ps, content); }
    }

    /// <summary>Selects the paragraph in the source, brings the source to the front and scrolls to it. Selecting after raising keeps the highlight.</summary>
    [SupportedOSPlatform("windows")]
    public static void Highlight(dynamic app, dynamic src, int paragraph)
    {
        dynamic? ps = null, p = null, r = null;
        try
        {
            WordDocs.Raise(app, src);
            ps = src.Paragraphs; p = ps.Item(paragraph); r = p.Range;
            r.Select();
            WordDocs.ScrollTo(app, r);
        }
        finally { ComInterop.Release(r, p, ps); }
    }

    [SupportedOSPlatform("windows")]
    public static void CloseQuietly(dynamic? doc)
    {
        if (doc is null) return;
        try { doc.Close(SaveChanges: 0); } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
    }
}

public abstract class CopyParagraphToolBase(IReadOnlyList<string> roots, string? statePath) : WordEditTool
{
    protected IReadOnlyList<string> Roots => roots;
    protected SessionFile<CopySession> Store => new(statePath ?? SessionFile<CopySession>.DefaultPath("copy-session.json"));
}

public sealed class CopyParagraphStartTool(IReadOnlyList<string> roots, string? statePath = null) : CopyParagraphToolBase(roots, statePath)
{
    public override string Name => "copy_paragraph_start";
    public override string Description =>
        "Starts copying a paragraph from another Word document into the document open now, at the cursor, by spotting the paragraph together on screen. Remembers the cursor of the open document, opens source read-only and visible in the same Word, brings it to the front and highlights the first candidate paragraph. " +
        "Give paragraph_number (that paragraph) or words (paragraphs containing them); with neither, every non-empty paragraph is a candidate in order. The result names the candidate; the highlight on screen is the read-back, so ask only \"זאת?\". The source file is never modified.";
    public override string InputSchema => """
        {"type":"object","properties":{"source":{"type":"string","description":"The source document, as search_documents shows it (folder/name) or a full path"},"paragraph_number":{"type":"integer","description":"Optional: the paragraph number in the source, from 1 (wins over words)"},"words":{"type":"string","description":"Optional: words the paragraph contains, in any order"}},"required":["source"]}
        """;

    protected override string? Validate(JsonElement input) =>
        WordCopyText.ParseStart(input) is { Request: { } r } ? WordCopyText.ResolveSource(r.Source, Roots).Error : WordCopyText.ParseStart(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var req = WordCopyText.ParseStart(input).Request!;
        var source = WordCopyText.ResolveSource(req.Source, Roots).Path!;
        string target = doc.FullName;
        if (DocumentIdentity.SamePath(source, target)) return WordCopyText.SourceIsTarget;
        dynamic? sel = null, docs = null, src = null, old = null;
        var openedNow = false;
        try
        {
            sel = app.Selection;
            int cursor = sel.Start;
            docs = app.Documents;
            var store = Store;
            var prev = store.Load();
            // a source this tool opened for an earlier, abandoned copy goes away first
            if (prev is { OpenedHere: true } && !DocumentIdentity.SamePath(prev.Source, source))
            {
                old = WordDocs.Find(docs, prev.Source);
                WordDocs.CloseQuietly(old);
            }
            src = WordDocs.Find(docs, source);
            var openedHere = src is null || (prev is { OpenedHere: true } && DocumentIdentity.SamePath(prev.Source, source));
            if (src is null)
            {
                src = docs.Open(FileName: source, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, Visible: true, NoEncodingDialog: true);
                openedNow = true;
            }
            List<string> texts = WordDocs.ReadTexts(src);
            var (cands, error) = WordCopyText.FindCandidates(texts, req.ParagraphNumber, req.Words);
            if (error is not null)
            {
                if (openedNow) { WordDocs.CloseQuietly(src); WordDocs.Raise(app, doc); }
                return error;
            }
            WordDocs.Highlight(app, src, cands[0]);
            store.Save(new CopySession(target, cursor, source, openedHere, cands, 0));
            return WordCopyText.Describe(0, cands.Length, Path.GetFileName(source), cands[0], texts[cands[0] - 1]);
        }
        finally { ComInterop.Release(old, src, sel); }   // docs belongs to ActiveWord.With (the same RCW), which releases it
    }
}

public sealed class CopyParagraphNextTool(IReadOnlyList<string> roots, string? statePath = null) : CopyParagraphToolBase(roots, statePath)
{
    public override string Name => "copy_paragraph_next";
    public override string Description =>
        "Moves the highlight in the source document to the next candidate paragraph, after he said the highlighted one is not it. hint is what he added: \"יותר למטה\" moves forward, \"יותר למעלה\" back, other words narrow the candidates to paragraphs containing them. " +
        "When there is nowhere to go it says so; then ask him for other words or the paragraph number and call copy_paragraph_start again.";
    public override string InputSchema => """{"type":"object","properties":{"hint":{"type":"string","description":"Optional: his words after 'no', e.g. 'יותר למטה' or words from the right paragraph"}}}""";

    protected override string? Validate(JsonElement input) => WordCopyText.ParseNext(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var hint = WordCopyText.ParseNext(input).Hint;
        var store = Store;
        var prev = store.Load();
        if (prev is null) return WordCopyText.NoSession;
        dynamic? docs = null, src = null;
        try
        {
            docs = app.Documents;
            src = WordDocs.Find(docs, prev.Source);
            if (src is null) { store.Clear(); return WordCopyText.SourceClosed; }
            List<string> texts = WordDocs.ReadTexts(src);
            var nav = WordCopyText.Navigate(texts, prev.Candidates, prev.Index, hint);
            if (nav.Message is not null) return nav.Message;
            var paragraph = nav.Candidates[nav.Index];
            WordDocs.Highlight(app, src, paragraph);
            store.Save(prev with { Candidates = nav.Candidates, Index = nav.Index });
            return WordCopyText.Describe(nav.Index, nav.Candidates.Length, Path.GetFileName(prev.Source), paragraph, texts[paragraph - 1]);
        }
        finally { ComInterop.Release(src); }
    }
}

public sealed class CopyParagraphConfirmTool(IReadOnlyList<string> roots, string? statePath = null) : CopyParagraphToolBase(roots, statePath)
{
    public override string Name => "copy_paragraph_confirm";
    public override string Description =>
        "Copies the highlighted paragraph of the source into the first document, at the cursor position remembered by copy_paragraph_start, after he said yes. Keeps the formatting; plain=true pastes the text only (\"בלי עיצוב\"). " +
        "Takes the backup, closes the source if this copy opened it (nothing is saved or changed there), brings the first document back to the front and puts the cursor after the pasted paragraph. Say where it went and that it can be undone.";
    public override string InputSchema => """{"type":"object","properties":{"plain":{"type":"boolean","description":"true to paste text without formatting"}}}""";

    protected override string? Validate(JsonElement input) => WordCopyText.ParseConfirm(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var plain = WordCopyText.ParseConfirm(input).Plain;
        var store = Store;
        var prev = store.Load();
        if (prev is null) return WordCopyText.NoSession;
        dynamic? docs = null, src = null, tdoc = null, ps = null, p = null, pr = null, content = null, trange = null, sel = null;
        try
        {
            docs = app.Documents;
            src = WordDocs.Find(docs, prev.Source);
            if (src is null) { store.Clear(); return WordCopyText.SourceClosed; }
            tdoc = WordDocs.Find(docs, prev.Target);
            if (tdoc is null) return $"error: the document \"{Path.GetFileName(prev.Target)}\" is no longer open; cancel with copy_paragraph_cancel";
            var paragraph = prev.Candidates[prev.Index];
            ps = src.Paragraphs;
            int count = ps.Count;
            if (paragraph > count) return $"error: the source now has only {count} paragraphs; start again with copy_paragraph_start";
            bool savedBefore = src.Saved;
            p = ps.Item(paragraph); pr = p.Range;
            var sourceName = Path.GetFileName(prev.Source);
            var targetName = (string)tdoc.Name;
            string backup = ActiveWord.Backup(tdoc);

            content = tdoc.Content;
            int endBefore = content.End;
            var pos = WordCopyText.ClampCursor(prev.Cursor, endBefore);
            string? plainText = plain ? WordCopyText.PlainText((string)pr.Text) : null;
            WordDocs.OneUndoStep<int>((object)app, "Shamash: copy paragraph", () =>
            {
                dynamic r = tdoc.Range(pos, pos);
                try
                {
                    if (plainText is not null) r.InsertAfter(plainText);
                    else r.FormattedText = pr.FormattedText;
                }
                finally { ComInterop.Release(r); }
                return 0;
            });
            ComInterop.Release(content);
            content = tdoc.Content;
            int endAfter = content.End;
            int at = ActiveWord.ParagraphAt(tdoc, pos);
            var newCursor = WordCopyText.CursorAfter(pos, endBefore, endAfter);

            bool savedAfter = src.Saved;
            var warning = savedBefore && !savedAfter ? "; " + WordCopyText.SourceDirtyWarning(sourceName) : "";
            if (prev.OpenedHere) { WordDocs.CloseQuietly(src); ComInterop.Release(src); src = null; }
            WordDocs.Raise(app, tdoc);
            sel = app.Selection;
            sel.SetRange(newCursor, newCursor);
            WordDocs.ScrollTo(app, sel.Range);
            store.Clear();
            return WordEditText.Mutation(WordCopyText.Pasted(paragraph, sourceName, targetName, at, plain), targetName, backup) + warning;
        }
        finally { ComInterop.Release(sel, trange, content, pr, p, ps, tdoc, src); }
    }
}

public sealed class CopyParagraphCancelTool(IReadOnlyList<string> roots, string? statePath = null) : CopyParagraphToolBase(roots, statePath)
{
    public override string Name => "copy_paragraph_cancel";
    public override string Description =>
        "Gives up the paragraph copy: closes the source if this copy opened it (without saving), brings the first document back to the front and forgets the copy. Nothing is pasted.";
    public override string InputSchema => """{"type":"object","properties":{}}""";

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var store = Store;
        var prev = store.Load();
        if (prev is null) return WordCopyText.NothingToCancel;
        dynamic? docs = null, src = null, tdoc = null;
        try
        {
            docs = app.Documents;
            if (prev.OpenedHere) { src = WordDocs.Find(docs, prev.Source); WordDocs.CloseQuietly(src); }
            tdoc = WordDocs.Find(docs, prev.Target);
            if (tdoc is not null) WordDocs.Raise(app, tdoc);
            store.Clear();
            return WordCopyText.Cancelled(Path.GetFileName(prev.Target), tdoc is not null);
        }
        finally { ComInterop.Release(tdoc, src); }
    }
}

public static class WordCopyPictureTools
{
    public static IEnumerable<IAgentTool> Create(ShamashSettings settings) =>
    [
        new CopyParagraphStartTool(settings.DocumentRoots),
        new CopyParagraphNextTool(settings.DocumentRoots),
        new CopyParagraphConfirmTool(settings.DocumentRoots),
        new CopyParagraphCancelTool(settings.DocumentRoots),
        .. WordPictureTools.Create(settings.DocumentRoots, settings.PictureRoots),
    ];
}
