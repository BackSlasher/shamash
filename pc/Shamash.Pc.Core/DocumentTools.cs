using System.Data.OleDb;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using WTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using DocumentFormat.OpenXml.Wordprocessing;
using UglyToad.PdfPig;

namespace Shamash.Pc;

/// <summary>Plain-text extraction for the formats he uses. Throws on unreadable files; the tools turn that into "error: ...".</summary>
public static class DocumentReader
{
    public const int MaxSheetRows = 200;
    public static readonly string[] Supported = [".docx", ".pdf", ".xlsx", ".txt", ".md", ".csv"];

    /// <summary>Formats read_document handles: Supported plus the legacy .doc and .rtf (Word COM first on Windows, then the managed extractor). Sorting evidence stay on Supported.</summary>
    public static bool IsReadable(string path) =>
        IsSupported(path) || Path.GetExtension(path).ToLowerInvariant() is ".doc" or ".rtf";

    /// <summary>Text of a document; .doc and .rtf go through Word on Windows when it is installed, else the managed fallback.</summary>
    public static string ReadTextPreferWord(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".doc" or ".rtf")) return ReadText(path);
        if (OperatingSystem.IsWindows())
        {
            try
            {
#pragma warning disable CA1416
                var viaWord = OfficeCom.RunSta(() => WordAutomation.ReadText(path));
#pragma warning restore CA1416
                if (!string.IsNullOrWhiteSpace(viaWord)) return viaWord;
            }
            catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException or TimeoutException) { }
        }
        return ext == ".rtf" ? LegacyDocReader.ReadRtf(path) : LegacyDocReader.ReadDoc(path);
    }

    public static bool IsSupported(string path) =>
        Supported.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static string ReadText(string path)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".docx": return ReadDocx(path);
            case ".xlsx": return ReadXlsx(path);
            case ".pdf": return ReadPdf(path);
            case ".txt" or ".md" or ".csv": return ReadPlain(path);
            default: throw new NotSupportedException($"unsupported file type {Path.GetExtension(path)}");
        }
    }

    /// <summary>UTF-8 with BOM detection (UTF-16 BOMs also handled by StreamReader).</summary>
    private static string ReadPlain(string path)
    {
        using var r = new StreamReader(path, new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true);
        return r.ReadToEnd();
    }

    private static string ReadDocx(string path)
    {
        using var doc = WordprocessingDocument.Open(path, false);
        var body = doc.MainDocumentPart?.Document?.Body;
        var sb = new StringBuilder();
        if (body is not null) AppendBlocks(body, sb);
        return sb.ToString();
    }

    private static void AppendBlocks(OpenXmlElement container, StringBuilder sb)
    {
        foreach (var child in container.ChildElements)
        {
            switch (child)
            {
                case Paragraph p:
                    sb.Append(p.InnerText).Append('\n');
                    break;
                case WTable t:
                    foreach (var row in t.Elements<TableRow>())
                        sb.AppendJoin('\t', row.Elements<TableCell>().Select(c => c.InnerText)).Append('\n');
                    break;
                case SdtBlock sdt when sdt.SdtContentBlock is { } content:
                    AppendBlocks(content, sb);
                    break;
            }
        }
    }

    private static string ReadXlsx(string path)
    {
        using var doc = SpreadsheetDocument.Open(path, false);
        var wb = doc.WorkbookPart;
        var sb = new StringBuilder();
        if (wb?.Workbook?.Sheets is null) return "";
        var shared = wb.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().Select(i => i.InnerText).ToList() ?? [];
        foreach (var sheet in wb.Workbook.Sheets.Elements<Sheet>())
        {
            sb.Append("[sheet ").Append(sheet.Name?.Value).Append("]\n");
            if (sheet.Id?.Value is not { } id || wb.GetPartById(id) is not WorksheetPart wsPart) continue;
            var rows = wsPart.Worksheet?.Descendants<Row>().Take(MaxSheetRows) ?? [];
            foreach (var row in rows)
            {
                sb.AppendJoin('\t', row.Elements<Cell>().Select(c => CellText(c, shared))).Append('\n');
            }
        }
        return sb.ToString();
    }

    private static string CellText(Cell c, List<string> shared)
    {
        var v = c.CellValue?.Text ?? "";
        var type = c.DataType?.Value;
        if (type == CellValues.SharedString)
            return int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var i) && i < shared.Count ? shared[i] : "";
        if (type == CellValues.InlineString) return c.InlineString?.InnerText ?? "";
        if (type == CellValues.Boolean) return v == "1" ? "TRUE" : "FALSE";
        return v;
    }

    private static string ReadPdf(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return string.Join("\n", pdf.GetPages().Select(p => p.Text));
    }
}

/// <summary>Which files the agent may read: only under the configured document roots.</summary>
public static class PathGuard
{
    private static StringComparison Cmp => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string Norm(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));

    public static bool IsUnderRoots(string path, IEnumerable<string> roots)
    {
        string full;
        try { full = Norm(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
        return roots.Any(r => RootOf(full, r));
    }

    /// <summary>The root that contains <paramref name="fullPath"/>, longest match, or null.</summary>
    /// <summary>Display name of a root: its last path segment.</summary>
    public static string RootName(string root)
    {
        var t = root.TrimEnd('\\', '/');
        var i = t.LastIndexOfAny(['\\', '/']);
        var n = i >= 0 ? t[(i + 1)..] : t;
        return n.Length > 0 ? n : root;
    }

    public static string? FindRoot(string fullPath, IEnumerable<string> roots)
    {
        var full = Norm(fullPath);
        return roots.Where(r => RootOf(full, r)).OrderByDescending(r => r.Length).FirstOrDefault();
    }

    private static bool RootOf(string full, string root)
    {
        var r = Norm(root);
        return full.Equals(r, Cmp) || full.StartsWith(r + Path.DirectorySeparatorChar, Cmp);
    }
}

/// <summary>Files no tool should list, search, group or move: Word lock files and shortcuts.</summary>
public static class FileSkip
{
    public static bool IsIgnored(string pathOrName)
    {
        var name = Path.GetFileName(pathOrName);
        return name.StartsWith("~$", StringComparison.Ordinal) || Path.GetExtension(name).Equals(".lnk", StringComparison.OrdinalIgnoreCase);
    }
}

public static class ToolText
{
    public static string Truncate(string text, int maxChars)
    {
        if (text.Length <= maxChars) return text;
        return text[..maxChars] + $"\n[truncated, {text.Length} chars total]";
    }

    public static string? GetString(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    public static int? GetInt(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetInt32(out var i) ? i : null;
}

public sealed class ReadDocumentTool(IReadOnlyList<string> roots) : IAgentTool
{
    public const int DefaultMaxChars = 6000;
    public const int HardMaxChars = 30000;
    public const long MaxFileBytes = 50L * 1024 * 1024;

    public string Name => "read_document";
    public string Description =>
        "Reads the text of one document (.docx, .doc, .rtf, .pdf, .xlsx, .txt, .md, .csv) from the user's document folders. " +
        "path is the full path, or 'folder/name' exactly as search shows it (Desktop/..., Documents/...), or a Hebrew root name (מסמכים, שולחן העבודה). Long text is cut at max_chars.";
    public string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"As search shows it (Desktop/..., Documents/...), a Hebrew root name, or a full path"},"max_chars":{"type":"integer","description":"Maximum characters to return, default 6000"}},"required":["path"]}
        """;

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => Run(input), ct);

    private string Run(JsonElement input)
    {
        var path = ToolText.GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return "error: path is required";
        var max = Math.Clamp(ToolText.GetInt(input, "max_chars") ?? DefaultMaxChars, 100, HardMaxChars);
        var full = Resolve(path, roots);
        if (full is null) return "error: path not under a document root";
        if (!File.Exists(full)) return "error: file not found";
        if (!DocumentReader.IsReadable(full)) return $"error: unsupported file type {Path.GetExtension(full)}";
        try
        {
            if (new FileInfo(full).Length > MaxFileBytes) return "error: file too large";
            return ToolText.Truncate(DocumentReader.ReadTextPreferWord(full), max);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or OpenXmlPackageException or NotSupportedException or ArgumentException or IndexOutOfRangeException)
        {
            return $"error: could not read file ({e.GetType().Name})";
        }
        catch (Exception e) when (e.GetType().Namespace?.StartsWith("UglyToad") == true)
        {
            return $"error: could not read file ({e.GetType().Name})";
        }
    }

    /// <summary>Full path under a root (existing file preferred), or null when outside every root. See <see cref="PathResolver"/>.</summary>
    internal static string? Resolve(string path, IReadOnlyList<string> roots) => PathResolver.ResolveFile(path, roots);
}

/// <summary>
/// The one resolver for every folder or file argument. Accepts, in order: an absolute path under a root; "RootName/relative"
/// (as search_documents shows it); a Hebrew root alias (מסמכים, שולחן העבודה) optionally followed by "/relative";
/// a bare relative path tried under each root. The PathGuard root check applies to every candidate. A path that exists nowhere
/// still resolves in the file variant (so callers can say "file not found"); the folder variant returns null for a missing folder.
/// </summary>
public static class PathResolver
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["מסמכים"] = "Documents", ["שולחן העבודה"] = "Desktop", ["שולחן עבודה"] = "Desktop",
    };

    public static string? ResolveFolder(string path, IReadOnlyList<string> roots)
    {
        var r = Resolve(path, roots, Directory.Exists);
        return r is null ? null : Path.TrimEndingDirectorySeparator(r);
    }

    public static string? ResolveFile(string path, IReadOnlyList<string> roots) => Resolve(path, roots, File.Exists) ?? Resolve(path, roots, _ => true);

    private static string? Resolve(string path, IReadOnlyList<string> roots, Func<string, bool> exists)
    {
        path = path.Trim();
        if (path.Length == 0) return null;
        if (Path.IsPathFullyQualified(path))
        {
            string full;
            try { full = Path.GetFullPath(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
            return PathGuard.IsUnderRoots(full, roots) && exists(full) ? full : null;
        }
        var rel = path.Replace('\\', '/').Trim('/');
        var slash = rel.IndexOf('/');
        var head = slash > 0 ? rel[..slash] : rel;
        var rest = slash > 0 ? rel[(slash + 1)..] : "";
        // "<rootname>/rest", then the Hebrew alias form
        var names = new List<string> { head };
        if (Aliases.TryGetValue(head, out var english)) names.Add(english);
        foreach (var name in names)
            foreach (var root in roots.Where(r => string.Equals(PathGuard.RootName(r), name, StringComparison.OrdinalIgnoreCase)))
                if (TryUnder(root, rest, roots, exists) is { } hit) return hit;
        foreach (var root in roots)
            if (TryUnder(root, rel, roots, exists) is { } hit) return hit;
        return null;
    }

    private static string? TryUnder(string root, string rel, IReadOnlyList<string> roots, Func<string, bool> exists)
    {
        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        return PathGuard.IsUnderRoots(candidate, roots) && exists(candidate) ? candidate : null;
    }
}

public sealed record DocumentHit(string Name, string FullPath, DateTime Modified, long Size, string Method = "index");

public interface IDocumentIndex
{
    /// <summary>Hits newest first; may throw when the index is unavailable (the tool then reports a hard error, it never searches another way).</summary>
    IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct);
}

/// <summary>The Windows Search index through the Search.CollatorDSO OLE DB provider. Windows only.</summary>
public sealed class WindowsSearchIndex : IDocumentIndex
{
    public const string ConnectionString = "Provider=Search.CollatorDSO;Extended Properties='Application=Windows';";

    [SupportedOSPlatform("windows")]
    public IReadOnlyList<DocumentHit> Search(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max, CancellationToken ct)
    {
        var sql = BuildSql(roots, query, words, kind, max);
        var hits = new List<DocumentHit>();
        using var conn = new OleDbConnection(ConnectionString);
        conn.Open();
        using var cmd = new OleDbCommand(sql, conn) { CommandTimeout = 10 };
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            ct.ThrowIfCancellationRequested();
            var path = r.IsDBNull(0) ? null : r.GetString(0);
            if (string.IsNullOrEmpty(path) || Directory.Exists(path)) continue;
            var name = r.IsDBNull(1) ? Path.GetFileName(path) : r.GetString(1);
            var modified = r.IsDBNull(2) ? DateTime.MinValue : Convert.ToDateTime(r.GetValue(2), CultureInfo.InvariantCulture);
            var size = r.IsDBNull(3) ? 0 : Convert.ToInt64(r.GetValue(3), CultureInfo.InvariantCulture);
            hits.Add(new DocumentHit(name, path, modified, size));
        }
        return hits;
    }

    /// <summary>Pure string building (UTF-16), so it is testable everywhere.</summary>
    public static string BuildSql(IReadOnlyList<string> roots, string query, IReadOnlyList<string> words, string kind, int max)
    {
        var clean = words.Select(CleanWord).Where(w => w.Length > 0).ToList();
        var scope = string.Join(" OR ", roots.Select(r => $"SCOPE='file:{Quote(r.TrimEnd('\\', '/'))}'"));
        var contains = string.Join(" AND ", clean.Select(w => $"\"{w}*\""));
        var names = string.Join(" AND ", clean.Select(w => $"System.ItemNameDisplay LIKE '%{Quote(w)}%'"));
        var sb = new StringBuilder();
        sb.Append($"SELECT TOP {max} System.ItemPathDisplay, System.ItemNameDisplay, System.DateModified, System.Size FROM SYSTEMINDEX ");
        // FREETEXT over the whole query catches prefixed Hebrew forms (העות'מאנית is indexed as העות + מאנית).
        sb.Append($"WHERE ({scope}) AND ((CONTAINS(*, '{Quote(contains)}') OR FREETEXT(*, '{Quote(query.Replace("\"", " ").Trim())}')) OR ({names}))");
        var exts = SearchDocumentsTool.Extensions(kind);
        if (exts is not null)
            sb.Append(" AND (").AppendJoin(" OR ", exts.Select(e => $"System.FileExtension = '{e}'")).Append(')');
        sb.Append(" ORDER BY System.DateModified DESC");
        return sb.ToString();
    }

    private static string Quote(string s) => s.Replace("'", "''");

    /// <summary>Removes characters that are query syntax in CONTAINS or LIKE.</summary>
    private static string CleanWord(string w) => new(w.Where(c => c is not ('"' or '%' or '_' or '[' or ']' or '*')).ToArray());
}

public sealed class SearchDocumentsTool(IReadOnlyList<string> roots, IDocumentIndex? index = null) : IAgentTool
{
    public const long ContentMaxBytes = 5L * 1024 * 1024;
    public const int DefaultMax = 10;

    private readonly IDocumentIndex? _index = index ?? DefaultIndex();
    private static readonly char[] WordSeparators = [' ', '\t', '\r', '\n', '\'', '\u2019', '\u05F3', '\u05F4', '"', '-', '\u05BE', '\u2010', '\u2011', '\u2013'];

    /// <summary>Splits a query into search words: whitespace, apostrophes, geresh, gershayim, quotes and hyphens all separate words.</summary>
    public static List<string> Tokenize(string query) =>
        query.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Take(8).ToList();

    private static IDocumentIndex? DefaultIndex() => OperatingSystem.IsWindows() ? new WindowsSearchIndex() : null;

    public string Name => "search_documents";
    public string Description =>
        "Searches the user's document folders through the Windows Search index, by file name and by content. query is a few keywords (Hebrew or English), not a sentence. " +
        "Give Hebrew words without the prefix letters ה ו ב ל מ ש כ when possible (ירושלים, not בירושלים). " +
        "If a search returns nothing, retry once with a shorter or alternative form of the words before concluding the file does not exist. " +
        "Returns numbered lines: name | folder | modified date | size KB, newest first. The folder column starts with the root folder name (Desktop or Documents), then the subfolders; pass it as folder/name to the other tools. " +
        "If it returns an error saying the search index is not available, tell the user so plainly and ask them to try again in a minute; do not guess file names.";
    public string InputSchema => """
        {"type":"object","properties":{"query":{"type":"string","description":"Keywords to look for in names and content"},"kind":{"type":"string","enum":["any","docx","pdf","xlsx","txt"],"description":"File type filter, default any"},"max":{"type":"integer","description":"Number of results, 1 to 20, default 10"}},"required":["query"]}
        """;

    /// <summary>Extensions (with dot) for a kind, null for any.</summary>
    public static string[]? Extensions(string kind) => kind switch
    {
        "docx" => [".docx"],
        "pdf" => [".pdf"],
        "xlsx" => [".xlsx"],
        "txt" => [".txt", ".md", ".csv"],
        _ => null,
    };

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => Run(input, ct), ct);

    private string Run(JsonElement input, CancellationToken ct)
    {
        var query = ToolText.GetString(input, "query") ?? "";
        var words = Tokenize(query);
        if (words.Count == 0) return "error: query is required";
        var kind = (ToolText.GetString(input, "kind") ?? "any").ToLowerInvariant();
        if (Extensions(kind) is null) kind = "any";
        var max = Math.Clamp(ToolText.GetInt(input, "max") ?? DefaultMax, 1, 20);
        if (roots.Count == 0) return "error: no document folders configured";

        const string Unavailable = "error: the Windows Search index is not available right now. Tell the user plainly that document search is unavailable and ask them to try again in a minute. Do not guess file names.";
        if (_index is null) return Unavailable;
        List<DocumentHit> hits;
        try { hits = [.. _index.Search(roots, query, words, kind, max, ct).Where(h => !FileSkip.IsIgnored(h.FullPath))]; }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return Unavailable; }
        hits = Rank(hits);
        if (hits.Count == 0) return "no documents found";

        var sb = new StringBuilder();
        var n = 0;
        foreach (var h in hits.Take(max))
        {
            var root = PathGuard.FindRoot(h.FullPath, roots);
            var dir = Path.GetDirectoryName(h.FullPath) ?? "";
            string folder;
            if (root is null) folder = Path.GetFileName(dir);
            else
            {
                var rel = Path.GetRelativePath(root, dir).Replace(Path.DirectorySeparatorChar, '/');
                folder = rel == "." ? PathGuard.RootName(root) : PathGuard.RootName(root) + "/" + rel;
            }
            sb.Append(++n).Append(". ").Append(h.Name).Append(" | ").Append(folder).Append(" | ")
              .Append(h.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(((h.Size + 1023) / 1024).ToString(CultureInfo.InvariantCulture)).Append(" KB\n");
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>Within a 30-day window .docx first, then other text formats, then .doc/.rtf/.pdf; newest first inside each step. Stable.</summary>
    public static List<DocumentHit> Rank(IEnumerable<DocumentHit> hits) =>
        hits.OrderByDescending(h => (int)(h.Modified.Date - DateTime.MinValue).TotalDays / 30)
            .ThenBy(h => FormatPriority(h.FullPath))
            .ThenByDescending(h => h.Modified).ToList();

    private static int FormatPriority(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".docx" => 0,
        ".doc" or ".rtf" or ".pdf" => 2,
        _ => 1,
    };
}
