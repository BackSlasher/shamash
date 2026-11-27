using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Anthropic;
using Anthropic.Models.Messages;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace Shamash.Pc;

/// <summary>The model side of translation, so the tool runs offline in tests.</summary>
public interface ITranslator
{
    /// <summary>Translates every text to <paramref name="language"/> ("English" or "Hebrew"); the result has the same count and order.</summary>
    Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string language, CancellationToken ct);

    /// <summary>A five-sentence summary of <paramref name="text"/> written in <paramref name="language"/>.</summary>
    Task<string> SummarizeAsync(string text, string language, CancellationToken ct);
}

/// <summary>Strict parser for the model's batch answer: a JSON array of {id, text}, same ids in the same order.</summary>
public static class TranslationJson
{
    public static string BuildRequest(IReadOnlyList<string> texts) =>
        JsonSerializer.Serialize(texts.Select((t, i) => new { id = i, text = t }),
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    /// <summary>Throws FormatException when the answer is not exactly the expected array. A Markdown code fence around it is tolerated.</summary>
    public static string[] Parse(string raw, int count)
    {
        var s = raw.Trim();
        if (s.StartsWith("```", StringComparison.Ordinal))
        {
            var nl = s.IndexOf('\n');
            var end = s.LastIndexOf("```", StringComparison.Ordinal);
            if (nl < 0 || end <= nl) throw new FormatException("unterminated code fence");
            s = s[(nl + 1)..end].Trim();
        }
        JsonDocument doc;
        try { doc = JsonDocument.Parse(s); }
        catch (JsonException e) { throw new FormatException("not valid JSON: " + e.Message); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array) throw new FormatException("not a JSON array");
            if (root.GetArrayLength() != count) throw new FormatException($"expected {count} items, got {root.GetArrayLength()}");
            var result = new string[count];
            var i = 0;
            foreach (var el in root.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object
                    || !el.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var idv) || idv != i
                    || !el.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String)
                    throw new FormatException($"item {i} malformed or out of order");
                var t = text.GetString()!;
                if (string.IsNullOrWhiteSpace(t)) throw new FormatException($"item {i} is empty");
                result[i++] = t;
            }
            return result;
        }
    }
}

/// <summary>Anthropic SDK implementation: one non-streaming Messages call per batch, Effort.Low as in the chat, no tools.</summary>
public sealed class ClaudeTranslator(string? apiKey, string model, Func<string, string, CancellationToken, Task<string>>? complete = null) : ITranslator
{
    public const int MaxTokens = 16000;
    private AnthropicClient? _client;

    private static string BatchSystem(string language) =>
        $"Translate each item to {language}. Keep numbering, bullets and punctuation. Do not summarise, explain or add anything. " +
        "The input is a JSON array of {\"id\": number, \"text\": string}. Return ONLY a JSON array of {\"id\", \"text\"} with the same ids in the same order, and nothing else.";

    public async Task<IReadOnlyList<string>> TranslateBatchAsync(IReadOnlyList<string> texts, string language, CancellationToken ct)
    {
        var request = TranslationJson.BuildRequest(texts);
        for (var attempt = 1; ; attempt++)
        {
            var raw = await CompleteAsync(BatchSystem(language), request, ct);
            try { return TranslationJson.Parse(raw, texts.Count); }
            catch (FormatException) when (attempt == 1) { /* retry once */ }
            catch (FormatException e) { throw new InvalidOperationException("the model returned a malformed translation twice (" + e.Message + ")"); }
        }
    }

    public Task<string> SummarizeAsync(string text, string language, CancellationToken ct) =>
        CompleteAsync($"Summarise the document in exactly five sentences, written in {language}. Plain text only, no lists or formatting.", text, ct);

    private async Task<string> CompleteAsync(string system, string user, CancellationToken ct)
    {
        if (complete is not null) return await complete(system, user, ct);
        if (string.IsNullOrEmpty(apiKey)) throw new InvalidOperationException("no Anthropic key configured");
        _client ??= new AnthropicClient { ApiKey = apiKey };
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = system,
            Messages = [new UserMessageParam { Content = user }],
            OutputConfig = new() { Effort = Effort.Low },
        }, cancellationToken: ct);
        if (response.StopReason == StopReason.Refusal) throw new InvalidOperationException("the model refused");
        var sb = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out TextBlock? t) && t is not null) sb.Append(t.Text);
        return sb.ToString();
    }
}

public sealed class TranslateDocumentTool(IReadOnlyList<string> roots, ITranslator translator) : IAgentTool
{
    public const int BatchSize = 40;
    public const int BatchMaxChars = 15000;
    public const int SummaryMaxChars = 60000;
    public const long MaxFileBytes = 50L * 1024 * 1024;

    public string Name => "translate_document";
    public string Description =>
        "Translates a document (.docx, .txt, .md, .pdf) into English or Hebrew and saves the result as a NEW file next to the original (the original is never changed). " +
        "A .docx keeps its styles, headings, tables and lists; a .pdf comes out as a .docx. path is the full path, or 'folder/name' as search_documents showed it. " +
        "With only_summary true no file is written and a five-sentence summary in the target language is returned instead. Long documents can take a minute or more.";
    public string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"Full path, or folder/name as listed by search_documents"},"target_language":{"type":"string","enum":["en","he"],"description":"en = English, he = Hebrew"},"only_summary":{"type":"boolean","description":"Return a 5-sentence summary instead of a translated file, default false"}},"required":["path","target_language"]}
        """;

    public async Task<string> RunAsync(JsonElement input, CancellationToken ct)
    {
        var path = ToolText.GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return "error: path is required";
        var lang = ToolText.GetString(input, "target_language")?.Trim().ToLowerInvariant();
        if (lang is not ("en" or "he")) return "error: target_language must be en or he";
        var summaryOnly = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("only_summary", out var os) && os.ValueKind == JsonValueKind.True;
        var full = Resolve(path);
        if (full is null) return "error: path not under a document root";
        if (!File.Exists(full)) return "error: file not found";
        var ext = Path.GetExtension(full).ToLowerInvariant();
        if (summaryOnly ? !DocumentReader.IsSupported(full) : ext is not (".docx" or ".txt" or ".md" or ".pdf"))
            return $"error: unsupported file type {ext}";

        var sw = Stopwatch.StartNew();
        try
        {
            if (new FileInfo(full).Length > MaxFileBytes) return "error: file too large";
            var language = lang == "he" ? "Hebrew" : "English";
            if (summaryOnly)
            {
                var text = await Task.Run(() => DocumentReader.ReadText(full), ct);
                if (string.IsNullOrWhiteSpace(text)) return "error: the document has no text";
                var summary = await translator.SummarizeAsync(ToolText.Truncate(text, SummaryMaxChars), language, ct);
                return "summary (" + language + "): " + summary.Trim();
            }

            byte[] output;
            int paragraphs, batches = 0;
            string outExt;
            if (ext == ".docx")
            {
                var bytes = await File.ReadAllBytesAsync(full, ct);
                (output, paragraphs, batches) = await TranslateDocxAsync(bytes, lang, language, ct);
                outExt = ".docx";
            }
            else
            {
                var text = await Task.Run(() => DocumentReader.ReadText(full), ct);
                var blocks = SplitBlocks(text, ext == ".pdf");
                if (blocks.Count == 0) return "error: the document has no text";
                (var translated, batches) = await TranslateAllAsync(blocks, language, ct);
                paragraphs = blocks.Count;
                if (ext == ".pdf") { output = BuildDocx(translated, lang == "he"); outExt = ".docx"; }
                else { output = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes(string.Join("\n\n", translated))).ToArray(); outExt = ext; }
            }

            var label = lang == "he" ? "עברית" : "English";
            var written = WriteNew(full, label, outExt, output);
            return $"ok: saved as {RelativeName(written)} | folder: {Path.GetFileName(Path.GetDirectoryName(written))} | paragraphs: {paragraphs} | batches: {batches} | {sw.ElapsedMilliseconds} ms";
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or OpenXmlPackageException or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return $"error: translation failed ({e.GetType().Name}: {e.Message})";
        }
        catch (Exception e) when (e.GetType().Namespace?.StartsWith("UglyToad") == true)
        {
            return $"error: could not read file ({e.GetType().Name})";
        }
    }

    // ---- batching ----

    private async Task<(List<string> Translated, int Batches)> TranslateAllAsync(IReadOnlyList<string> texts, string language, CancellationToken ct)
    {
        var result = new List<string>(texts.Count);
        var batches = 0;
        var i = 0;
        while (i < texts.Count)
        {
            var take = 0;
            var chars = 0;
            while (i + take < texts.Count && take < BatchSize && (take == 0 || chars + texts[i + take].Length <= BatchMaxChars))
                chars += texts[i + take++].Length;
            var batch = texts.Skip(i).Take(take).ToList();
            ct.ThrowIfCancellationRequested();
            var done = await translator.TranslateBatchAsync(batch, language, ct);
            if (done.Count != batch.Count) throw new InvalidOperationException("translator returned the wrong number of items");
            result.AddRange(done);
            batches++;
            i += take;
        }
        return (result, batches);
    }

    // ---- docx ----

    /// <summary>Builds the translated package in memory from the original bytes; nothing is written to disk here.</summary>
    private async Task<(byte[] Bytes, int Paragraphs, int Batches)> TranslateDocxAsync(byte[] original, string lang, string language, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        ms.Write(original);
        ms.Position = 0;
        int count, batches;
        using (var doc = WordprocessingDocument.Open(ms, true))
        {
            var body = doc.MainDocumentPart?.Document?.Body ?? throw new InvalidDataException("no document body");
            // Body order, tables included; headers/footers/footnotes are separate parts; text boxes are skipped.
            var paras = body.Descendants<Paragraph>()
                .Where(p => !p.Ancestors<TextBoxContent>().Any())
                .Where(p => ParagraphText(p).Trim().Length > 0)
                .ToList();
            count = paras.Count;
            if (count == 0) throw new InvalidOperationException("the document has no text");
            var (translated, b) = await TranslateAllAsync(paras.Select(ParagraphText).ToList(), language, ct);
            batches = b;
            for (var i = 0; i < paras.Count; i++) SetParagraph(paras[i], translated[i], lang == "he");
            doc.MainDocumentPart!.Document!.Save();
        }
        return (ms.ToArray(), count, batches);
    }

    public static string ParagraphText(Paragraph p) =>
        string.Concat(p.Descendants<Run>().SelectMany(r => r.Elements<Text>()).Select(t => t.Text));

    internal static void SetParagraph(Paragraph p, string text, bool hebrew)
    {
        var runs = p.Descendants<Run>().ToList();
        var first = runs.First(r => r.Elements<Text>().Any());
        foreach (var r in runs)
        {
            foreach (var t in r.Elements<Text>().ToList()) t.Remove();
            // Runs that held only text and now hold nothing (but properties) are dropped, except the first.
            if (r != first && !r.ChildElements.Any(c => c is not RunProperties)) r.Remove();
        }
        first.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        SetDirection(p, hebrew);
        SetRunDirection(p, hebrew);
    }

    private static readonly System.Type[] AfterRtl = [typeof(ComplexScript), typeof(Emphasis), typeof(Languages), typeof(EastAsianLayout), typeof(SpecVanish), typeof(OfficeMath)];

    /// <summary>Adds (Hebrew) or removes (English) w:rtl on every run and on the paragraph mark, so punctuation does not jump to the line start.</summary>
    internal static void SetRunDirection(Paragraph p, bool hebrew)
    {
        var holders = new List<OpenXmlCompositeElement>();
        foreach (var r in p.Descendants<Run>())
            holders.Add(r.RunProperties ?? r.PrependChild(new RunProperties()));
        if (p.ParagraphProperties is { } pPr)
        {
            var mark = pPr.GetFirstChild<ParagraphMarkRunProperties>();
            if (mark is null && hebrew)
            {
                mark = new ParagraphMarkRunProperties();
                var anchor = pPr.ChildElements.FirstOrDefault(c => c is SectionProperties or ParagraphPropertiesChange);
                if (anchor is not null) pPr.InsertBefore(mark, anchor); else pPr.Append(mark);
            }
            if (mark is not null) holders.Add(mark);
        }
        foreach (var rPr in holders)
        {
            var rtl = rPr.GetFirstChild<RightToLeftText>();
            if (!hebrew) { rtl?.Remove(); continue; }
            if (rtl is not null) continue;
            var anchor = rPr.ChildElements.FirstOrDefault(c => AfterRtl.Contains(c.GetType()));
            var node = new RightToLeftText();
            if (anchor is not null) rPr.InsertBefore(node, anchor); else rPr.Append(node);
        }
    }

    private static readonly System.Type[] AfterBiDi =
    [
        typeof(AdjustRightIndent), typeof(SnapToGrid), typeof(SpacingBetweenLines), typeof(Indentation), typeof(ContextualSpacing),
        typeof(MirrorIndents), typeof(SuppressOverlap), typeof(Justification), typeof(TextDirection), typeof(TextAlignment),
        typeof(TextBoxTightWrap), typeof(OutlineLevel), typeof(DivId),
        typeof(ParagraphMarkRunProperties), typeof(SectionProperties), typeof(ParagraphPropertiesChange),
    ];

    internal static void SetDirection(Paragraph p, bool hebrew)
    {
        var pPr = p.ParagraphProperties;
        if (pPr is null) { pPr = new ParagraphProperties(); p.PrependChild(pPr); }
        var bidi = pPr.GetFirstChild<BiDi>();
        if (hebrew)
        {
            if (bidi is null)
            {
                var anchor = pPr.ChildElements.FirstOrDefault(c => AfterBiDi.Contains(c.GetType()));
                var node = new BiDi();
                if (anchor is not null) pPr.InsertBefore(node, anchor); else pPr.Append(node);
            }
        }
        else bidi?.Remove();
        if (pPr.GetFirstChild<Justification>() is { } jc && jc.Val is { } v)
        {
            if (v == JustificationValues.Left) jc.Val = JustificationValues.Right;
            else if (v == JustificationValues.Right) jc.Val = JustificationValues.Left;
        }
    }

    // ---- txt / pdf ----

    /// <summary>Blocks separated by blank lines. For PDFs, which often have none, a block over 3000 chars is cut at sentence ends.</summary>
    internal static List<string> SplitBlocks(string text, bool chunkLong)
    {
        var blocks = Regex.Split(text.Replace("\r\n", "\n"), @"\n[ \t]*\n+")
            .Select(b => b.Trim()).Where(b => b.Length > 0).ToList();
        if (!chunkLong) return blocks;
        var result = new List<string>();
        foreach (var b in blocks)
        {
            if (b.Length <= 3000) { result.Add(b); continue; }
            var cur = new StringBuilder();
            foreach (var s in Regex.Split(b, @"(?<=[.!?])\s+"))
            {
                if (cur.Length > 0 && cur.Length + s.Length > 1500) { result.Add(cur.ToString()); cur.Clear(); }
                if (cur.Length > 0) cur.Append(' ');
                cur.Append(s);
            }
            if (cur.Length > 0) result.Add(cur.ToString());
        }
        return result;
    }

    /// <summary>A plain .docx: one paragraph per block, bidi for Hebrew.</summary>
    internal static byte[] BuildDocx(IReadOnlyList<string> blocks, bool hebrew)
    {
        using var ms = new MemoryStream();
        using (var doc = WordprocessingDocument.Create(ms, WordprocessingDocumentType.Document))
        {
            var main = doc.AddMainDocumentPart();
            var body = new Body();
            foreach (var b in blocks)
            {
                var p = new Paragraph(new Run(new Text(b) { Space = SpaceProcessingModeValues.Preserve }));
                SetDirection(p, hebrew);
                SetRunDirection(p, hebrew);
                body.Append(p);
            }
            main.Document = new Document(body);
            main.Document.Save();
        }
        return ms.ToArray();
    }

    // ---- files ----

    /// <summary>Writes "name (label).ext", or "name (label) (2).ext" and so on, next to the original. CreateNew never overwrites.</summary>
    internal static string WriteNew(string original, string label, string ext, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(original)!;
        var name = Path.GetFileNameWithoutExtension(original);
        for (var n = 1; ; n++)
        {
            var candidate = Path.Combine(dir, n == 1 ? $"{name} ({label}){ext}" : $"{name} ({label}) ({n}){ext}");
            try
            {
                using var fs = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write);
                fs.Write(bytes);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate)) { }
        }
    }

    private string RelativeName(string full)
    {
        var root = PathGuard.FindRoot(full, roots);
        return root is null ? Path.GetFileName(full) : Path.GetRelativePath(root, full).Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>Same rule as read_document: full path under a root, or folder/name under a root; guard applied after normalising "..".</summary>
    private string? Resolve(string path)
    {
        if (Path.IsPathFullyQualified(path))
            return PathGuard.IsUnderRoots(path, roots) ? Path.GetFullPath(path) : null;
        foreach (var root in roots)
        {
            var candidate = Path.GetFullPath(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar)));
            if (PathGuard.IsUnderRoots(candidate, roots) && File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
