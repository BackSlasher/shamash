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
        "Reads the text of one document (.docx, .pdf, .xlsx, .txt, .md, .csv) from the user's document folders. " +
        "path is the full path, or 'folder/name' exactly as search_documents showed it. Long text is cut at max_chars.";
    public string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"Full path, or folder/name as listed by search_documents"},"max_chars":{"type":"integer","description":"Maximum characters to return, default 6000"}},"required":["path"]}
        """;

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() => Run(input), ct);

    private string Run(JsonElement input)
    {
        var path = ToolText.GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return "error: path is required";
        var max = Math.Clamp(ToolText.GetInt(input, "max_chars") ?? DefaultMaxChars, 100, HardMaxChars);
        var full = Resolve(path);
        if (full is null) return "error: path not under a document root";
        if (!File.Exists(full)) return "error: file not found";
        if (!DocumentReader.IsSupported(full)) return $"error: unsupported file type {Path.GetExtension(full)}";
        try
        {
            if (new FileInfo(full).Length > MaxFileBytes) return "error: file too large";
            return ToolText.Truncate(DocumentReader.ReadText(full), max);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or OpenXmlPackageException or NotSupportedException or ArgumentException)
        {
            return $"error: could not read file ({e.GetType().Name})";
        }
        catch (Exception e) when (e.GetType().Namespace?.StartsWith("UglyToad") == true)
        {
            return $"error: could not read file ({e.GetType().Name})";
        }
    }

    /// <summary>Full path if it is (or maps to) a location under a root; null when outside every root.</summary>
    private string? Resolve(string path)
    {
        if (Path.IsPathFullyQualified(path))
            return PathGuard.IsUnderRoots(path, roots) ? Path.GetFullPath(path) : null;
        // relative form "folder/name": try under each root; the guard still applies after normalising "..".
        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (PathGuard.IsUnderRoots(candidate, roots) && File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

public sealed record DocumentHit(string Name, string FullPath, DateTime Modified, long Size, string Method = "index");

public interface IDocumentIndex
{
    /// <summary>Hits newest first; may throw when the index is unavailable (the caller falls back to a walk).</summary>
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

/// <summary>Which extensions the Windows Search index has no content filter for (no PersistentHandler), so only names are indexed.</summary>
public sealed class IndexCoverage(Func<string, bool>? hasHandler = null)
{
    public static readonly string[] Checked = [".docx", ".xlsx", ".txt", ".pdf"];

    private readonly Lazy<HashSet<string>> _nameOnly = new(() => Compute(hasHandler ?? RegistryHasHandler));

    /// <summary>Extensions (with dot) whose content the index does not cover. Computed once per process.</summary>
    public IReadOnlySet<string> NameOnly => _nameOnly.Value;

    private static HashSet<string> Compute(Func<string, bool> has)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Checked)
        {
            bool ok;
            try { ok = has(e); } catch (Exception) { ok = true; } // unknown: trust the index
            if (!ok) set.Add(e);
        }
        return set;
    }

    private static bool RegistryHasHandler(string ext)
    {
        if (!OperatingSystem.IsWindows()) return true;
        return Check(ext);

        [SupportedOSPlatform("windows")]
        static bool Check(string ext)
        {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(ext + "\\PersistentHandler");
            return key?.GetValue(null) is string v && !string.IsNullOrWhiteSpace(v);
        }
    }
}

public sealed class SearchDocumentsTool(IReadOnlyList<string> roots, IDocumentIndex? index = null, IndexCoverage? coverage = null) : IAgentTool
{
    public const int WalkMaxFiles = 2000;
    public static readonly TimeSpan WalkMaxTime = TimeSpan.FromSeconds(10);
    public const long ContentMaxBytes = 5L * 1024 * 1024;
    public const int DefaultMax = 10;

    private readonly IDocumentIndex? _index = index ?? DefaultIndex();
    private readonly IndexCoverage _coverage = coverage ?? new IndexCoverage();
    private static readonly char[] WordSeparators = [' ', '\t', '\r', '\n', '\'', '\u2019', '\u05F3', '\u05F4', '"', '-', '\u05BE', '\u2010', '\u2011', '\u2013'];

    /// <summary>Splits a query into search words: whitespace, apostrophes, geresh, gershayim, quotes and hyphens all separate words.</summary>
    public static List<string> Tokenize(string query) =>
        query.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).Take(8).ToList();

    private static IDocumentIndex? DefaultIndex() => OperatingSystem.IsWindows() ? new WindowsSearchIndex() : null;

    public string Name => "search_documents";
    public string Description =>
        "Searches the user's document folders by file name and by content. query is a few keywords (Hebrew or English), not a sentence. " +
        "Give Hebrew words without the prefix letters ה ו ב ל מ ש כ when possible (ירושלים, not בירושלים). " +
        "If a search returns nothing, retry once with a shorter or alternative form of the words before concluding the file does not exist. " +
        "Returns numbered lines: name | folder | modified date | size KB | index or walk, newest first.";
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

        List<DocumentHit> hits = [];
        string? note = null;
        var indexOk = false;
        if (_index is not null)
        {
            try { hits = [.. _index.Search(roots, query, words, kind, max, ct)]; indexOk = true; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) { note = $"index unavailable ({e.GetType().Name})"; }
        }
        else note = "no index on this system";

        var complete = true;
        if (indexOk && hits.Count > 0)
        {
            // The index answered; add content hits for the types it only knows by name. .txt without a BOM
            // is not detectable, so .txt joins the walk when the index found no .txt for these words.
            var extra = new HashSet<string>(_coverage.NameOnly, StringComparer.OrdinalIgnoreCase);
            if (!hits.Any(h => Path.GetExtension(h.FullPath).Equals(".txt", StringComparison.OrdinalIgnoreCase))) extra.Add(".txt");
            var kindExts = Extensions(kind);
            if (kindExts is not null) extra.IntersectWith(kindExts);
            if (extra.Count > 0)
            {
                var seenPaths = new HashSet<string>(hits.Select(h => h.FullPath), StringComparer.OrdinalIgnoreCase);
                var walked = Walk(words, extra, max, ct, out complete).Where(h => seenPaths.Add(h.FullPath));
                hits = [.. hits, .. walked];
                note = "index plus content walk of " + string.Join(" ", extra.Order()) + (complete ? "" : ", walk stopped at its limit");
            }
        }
        else
        {
            hits = Walk(words, Extensions(kind) is { } ke ? new HashSet<string>(ke) : null, max, ct, out complete);
            note = (note ?? (indexOk ? "index returned nothing" : null)) + "; slow folder walk" + (complete ? "" : ", stopped at its limit so it may be incomplete");
        }
        var method = hits.Select(h => h.Method).Distinct().Order().ToList();
        var methodText = method.Count == 0 ? "walk" : string.Join("+", method.OrderByDescending(m => m == "index"));
        if (hits.Count == 0) return $"no documents found (method: {methodText}{(note is null ? "" : ", " + note)})";

        var sb = new StringBuilder();
        sb.Append($"method: {methodText}").Append(note is null ? "" : $" ({note})").Append('\n');
        var n = 0;
        foreach (var h in hits.Take(max))
        {
            var root = PathGuard.FindRoot(h.FullPath, roots);
            var dir = Path.GetDirectoryName(h.FullPath) ?? "";
            var folder = root is null ? Path.GetFileName(dir) : Path.GetRelativePath(root, dir);
            sb.Append(++n).Append(". ").Append(h.Name).Append(" | ").Append(folder).Append(" | ")
              .Append(h.Modified.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(" | ")
              .Append(((h.Size + 1023) / 1024).ToString(CultureInfo.InvariantCulture)).Append(" KB | ").Append(h.Method).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private List<DocumentHit> Walk(IReadOnlyList<string> words, IReadOnlySet<string>? exts, int max, CancellationToken ct, out bool complete)
    {
        var sw = Stopwatch.StartNew();
        var seen = 0;
        complete = true;
        var hits = new List<DocumentHit>();
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*", opts))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(file);
                if (name.StartsWith("~$", StringComparison.Ordinal)) continue;
                var ext = Path.GetExtension(file);
                if (exts is not null && !exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                if (++seen > WalkMaxFiles || sw.Elapsed > WalkMaxTime) { complete = false; goto done; }
                FileInfo fi;
                try { fi = new FileInfo(file); } catch (Exception) { continue; }
                var match = words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));
                if (!match && fi.Length < ContentMaxBytes && DocumentReader.IsSupported(file))
                {
                    try
                    {
                        var text = DocumentReader.ReadText(file);
                        match = words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (Exception e) when (e is not OperationCanceledException) { /* unreadable file: not a match */ }
                }
                if (match) hits.Add(new DocumentHit(name, file, fi.LastWriteTime, fi.Length, "walk"));
            }
        }
        done:
        return hits.OrderByDescending(h => h.Modified).Take(max).ToList();
    }
}
