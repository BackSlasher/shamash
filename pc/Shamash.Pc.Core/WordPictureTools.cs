using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>Pure decisions and result text of the picture tools: wrap and size mapping, input checks, wording. No COM.</summary>
public static class WordPictureText
{
    public const double PointsPerCm = 28.35;
    public const double MinWidthCm = 0.5, MaxWidthCm = 50;
    public const int WrapSquare = 0, WrapTight = 1, WrapThrough = 2, WrapNone = 3, WrapTopBottom = 4, WrapBehind = 5, WrapInline = 7;   // WdWrapType
    public static readonly string[] Wraps = ["inline", "square", "tight", "top_bottom"];
    public static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".gif", ".bmp"];
    public const string NoPicture = "error: no picture selected";

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>WdWrapType of the Shape for a wrap name; null for inline (a picture in the text line, not a Shape).</summary>
    public static int? WrapType(string wrap) => wrap switch
    {
        "square" => WrapSquare,
        "tight" => WrapTight,
        "top_bottom" => WrapTopBottom,
        _ => null,
    };

    /// <summary>The name of a Word wrap value as read back from a Shape; inline pictures are not Shapes and are named by the caller.</summary>
    public static string WrapName(int wdWrapType) => wdWrapType switch
    {
        WrapSquare => "square",
        WrapTight => "tight",
        WrapThrough => "through",
        WrapNone => "in front of text",
        WrapTopBottom => "top_bottom",
        WrapBehind => "behind text",
        WrapInline => "inline",
        _ => "other",
    };

    public static double ToPoints(double cm) => cm * PointsPerCm;
    public static double ToCm(double points) => points / PointsPerCm;
    public static string Cm(double points) => WordEditText.Num(Math.Round(ToCm(points), 1));
    public static string Size(double widthPt, double heightPt) => $"{Cm(widthPt)}x{Cm(heightPt)} cm";

    /// <summary>Width in points for a picture with no width requested: the natural width, cut to the text width when it would not fit.</summary>
    public static (double Width, bool Fitted) FitWidth(double naturalPt, double usablePt) =>
        usablePt > 0 && naturalPt > usablePt ? (usablePt, true) : (naturalPt, false);

    public sealed record InsertRequest(string Path, string Wrap, double? WidthCm);

    public static (string? Wrap, string? Error) ParseWrap(JsonElement input, bool required)
    {
        var raw = ToolText.GetString(input, "wrap");
        if (string.IsNullOrWhiteSpace(raw)) return required ? (null, "error: wrap is required (inline, square, tight or top_bottom)") : ("inline", null);
        var w = raw.Trim().ToLowerInvariant().Replace('-', '_');
        return Wraps.Contains(w) ? (w, null) : (null, "error: wrap must be inline, square, tight or top_bottom");
    }

    public static (double? Cm, string? Error) ParseWidth(JsonElement input, bool required)
    {
        if (input.ValueKind == JsonValueKind.Object && input.TryGetProperty("width_cm", out var p) && p.ValueKind != JsonValueKind.Null)
        {
            if (p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var d) || double.IsNaN(d) || d < MinWidthCm || d > MaxWidthCm)
                return (null, $"error: width_cm must be a number from {WordEditText.Num(MinWidthCm)} to {WordEditText.Num(MaxWidthCm)}");
            return (d, null);
        }
        return required ? (null, "error: width_cm is required") : (null, null);
    }

    public static (InsertRequest? Request, string? Error) ParseInsert(JsonElement input)
    {
        var path = ToolText.GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return (null, "error: path is required");
        var (wrap, wrapError) = ParseWrap(input, required: false);
        if (wrapError is not null) return (null, wrapError);
        var (width, widthError) = ParseWidth(input, required: false);
        if (widthError is not null) return (null, widthError);
        return (new InsertRequest(path.Trim(), wrap!, width), null);
    }

    /// <summary>The picture file under the document or picture roots, or an error. The file is only ever read.</summary>
    public static (string? Path, string? Error) ResolvePicture(string path, IReadOnlyList<string> allRoots)
    {
        var full = PathResolver.ResolveFile(path, allRoots);
        if (full is null) return (null, "error: path not under a document or picture folder");
        if (!File.Exists(full)) return (null, "error: file not found");
        if (!IsImage(full)) return (null, "error: not a picture file (jpg, png, tif, gif or bmp)");
        return (full, null);
    }

    public static string Inserted(string file, double widthPt, double heightPt, string wrap, int paragraph, string paragraphText, bool fitted) =>
        $"inserted picture \"{file.Replace('"', '\'')}\" ({Size(widthPt, heightPt)}, wrap: {wrap}) after paragraph {paragraph} starting \"{WordEditText.FirstWords(paragraphText, WordEditText.DescribeWords)}\"" +
        (fitted ? "; scaled down to fit the page width" : "");

    public static string Resized(string file, double fromW, double fromH, double toW, double toH) =>
        $"picture \"{file.Replace('"', '\'')}\" resized to {Size(toW, toH)} (was {Size(fromW, fromH)})";

    public static string WrapChanged(string file, string was, string now) =>
        was == now ? $"picture \"{file.Replace('"', '\'')}\" already has wrap: {now}" : $"picture \"{file.Replace('"', '\'')}\" wrap: {now} (was {was})";
}

/// <summary>The picture the session inserted last, so "יותר קטן" and "שהטקסט יזרום מסביב" know which one. Inline pictures are found by their anchor position, floating ones by the Shape name given at conversion.</summary>
public sealed record PictureSession(string Document, string File, bool Floating, int Anchor, string? ShapeName);

public abstract class PictureToolBase(string? statePath) : WordEditTool
{
    protected SessionFile<PictureSession> Store => new(statePath ?? SessionFile<PictureSession>.DefaultPath("picture-session.json"));
}

/// <summary>A located picture: the InlineShape or Shape object (caller releases it) and which one it is.</summary>
[SupportedOSPlatform("windows")]
internal sealed class FoundPicture(dynamic obj, bool inline, string file)
{
    public dynamic Obj { get; } = obj;
    public bool Inline { get; } = inline;
    public string File { get; } = file;

    public double Width => (double)Obj.Width;
    public double Height => (double)Obj.Height;

    public string WrapName()
    {
        if (Inline) return "inline";
        dynamic wf = Obj.WrapFormat;
        try { return WordPictureText.WrapName((int)wf.Type); } finally { ComInterop.Release(wf); }
    }

    public void Release() => ComInterop.Release(Obj);

    /// <summary>The picture the session inserted last, or the selected inline shape or shape, or null.</summary>
    public static FoundPicture? Locate(object appObj, object docObj, PictureSession? last)
    {
        dynamic app = appObj, doc = docObj;
        if (last is not null && DocumentIdentity.SamePath(last.Document, (string)doc.FullName))
        {
            dynamic? shapes = null, r = null, inl = null;
            try
            {
                if (last.Floating && last.ShapeName is not null)
                {
                    shapes = doc.Shapes;
                    return new FoundPicture(shapes.Item(last.ShapeName), false, last.File);
                }
                if (!last.Floating)
                {
                    r = doc.Range(last.Anchor, last.Anchor + 1);
                    inl = r.InlineShapes;
                    if ((int)inl.Count > 0) return new FoundPicture(inl.Item(1), true, last.File);
                }
            }
            catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally { ComInterop.Release(inl, r, shapes); }
        }
        dynamic? sel = null, set = null;
        try
        {
            sel = app.Selection;
            int type = sel.Type;   // wdSelectionInlineShape 7, wdSelectionShape 8
            if (type == 7) { set = sel.InlineShapes; return new FoundPicture(set.Item(1), true, "selected picture"); }
            if (type == 8) { set = sel.ShapeRange; return new FoundPicture(set.Item(1), false, "selected picture"); }
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
        finally { ComInterop.Release(set, sel); }
        return null;
    }

    /// <summary>Makes the picture inline or floating with the given wrap and returns the object now holding it (the old one is released).</summary>
    public static FoundPicture ApplyWrap(FoundPicture p, string wrap)
    {
        var code = WordPictureText.WrapType(wrap);
        if (code is null)
        {
            if (p.Inline) return p;
            dynamic inl = p.Obj.ConvertToInlineShape();
            p.Release();
            return new FoundPicture(inl, true, p.File);
        }
        var shape = p.Inline ? p.Obj.ConvertToShape() : p.Obj;
        if (p.Inline) { p.Release(); shape.Name = "ShamashPicture" + DateTime.Now.Ticks; }
        dynamic wf = shape.WrapFormat;
        try { wf.Type = code.Value; } finally { ComInterop.Release(wf); }
        return new FoundPicture(shape, false, p.File);
    }

    public PictureSession Remember(string documentPath)
    {
        if (Inline)
        {
            dynamic r = Obj.Range;
            try { return new PictureSession(documentPath, File, false, (int)r.Start, null); } finally { ComInterop.Release(r); }
        }
        return new PictureSession(documentPath, File, true, 0, (string)Obj.Name);
    }
}

public sealed class InsertPictureTool(IReadOnlyList<string> documentRoots, IReadOnlyList<string> pictureRoots, string? statePath = null) : PictureToolBase(statePath)
{
    private IReadOnlyList<string> AllRoots => [.. documentRoots, .. pictureRoots];

    public override string Name => "insert_picture";
    public override string Description =>
        "Inserts a picture file (jpg, png, tif, gif, bmp) from the document or Pictures folders at the cursor of the document open in Word. path is as search_documents (kind picture) shows it. " +
        "wrap: inline (default, in the text line), square or tight (text flows around), top_bottom (text above and below). width_cm scales it keeping the proportions. The picture is copied into the document; the file is never changed or moved. " +
        "Say which picture and where, and that it can be undone. If several pictures matched the name, ask which one before calling.";
    public override string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"The picture file, as search_documents shows it (folder/name) or a full path"},"wrap":{"type":"string","enum":["inline","square","tight","top_bottom"],"description":"Text wrapping, default inline"},"width_cm":{"type":"number","description":"Optional width in centimetres; the height follows"}},"required":["path"]}
        """;

    protected override string? Validate(JsonElement input) =>
        WordPictureText.ParseInsert(input) is { Request: { } r } ? WordPictureText.ResolvePicture(r.Path, AllRoots).Error : WordPictureText.ParseInsert(input).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var req = WordPictureText.ParseInsert(input).Request!;
        var file = WordPictureText.ResolvePicture(req.Path, AllRoots).Path!;
        dynamic? sel = null, rng = null, pars = null, pr = null, inlines = null, setup = null;
        FoundPicture? pic = null;
        try
        {
            sel = app.Selection;
            rng = sel.Range;
            rng.Collapse(1);   // wdCollapseStart: never replace a selection
            int start = rng.Start;
            int paragraph = ActiveWord.ParagraphAt(doc, start);
            pars = rng.Paragraphs; dynamic p1 = pars.Item(1); pr = p1.Range;
            string paragraphText = pr.Text;
            ComInterop.Release(p1);
            string backup = ActiveWord.Backup(doc);
            string documentPath = doc.FullName;

            bool fitted = false;
            pic = WordDocs.OneUndoStep<FoundPicture>((object)app, "Shamash: insert picture", () =>
            {
                inlines = doc.InlineShapes;
                dynamic inl = inlines.AddPicture(FileName: file, LinkToFile: false, SaveWithDocument: true, Range: rng);
                var made = new FoundPicture(inl, true, Path.GetFileName(file));
                made.Obj.LockAspectRatio = -1;   // msoTrue
                if (req.WidthCm is { } cm) made.Obj.Width = (float)WordPictureText.ToPoints(cm);
                else
                {
                    setup = doc.PageSetup;
                    double usable = (double)setup.PageWidth - (double)setup.LeftMargin - (double)setup.RightMargin;
                    var (w, fit) = WordPictureText.FitWidth(made.Width, usable);
                    fitted = fit;
                    if (fit) made.Obj.Width = (float)w;
                }
                return WordPictureText.WrapType(req.Wrap) is null ? made : FoundPicture.ApplyWrap(made, req.Wrap);
            });
            var result = WordPictureText.Inserted(pic.File, pic.Width, pic.Height, pic.WrapName(), paragraph, paragraphText, fitted);
            Store.Save(pic.Remember(documentPath));
            return WordEditText.Mutation(result, ActiveWord.Name(doc), backup);
        }
        finally { pic?.Release(); ComInterop.Release(setup, inlines, pr, pars, rng, sel); }
    }
}

public sealed class ResizeLastPictureTool(string? statePath = null) : PictureToolBase(statePath)
{
    public override string Name => "resize_last_picture";
    public override string Description =>
        "Sets the width of the picture inserted last in this session (or, if none, the picture selected in Word) in centimetres, keeping the proportions. For 'יותר קטן' give a width clearly smaller than the current one (the insert result says the current size). Can be undone.";
    public override string InputSchema => """{"type":"object","properties":{"width_cm":{"type":"number","description":"The new width in centimetres"}},"required":["width_cm"]}""";

    protected override string? Validate(JsonElement input) => WordPictureText.ParseWidth(input, required: true).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var cm = WordPictureText.ParseWidth(input, required: true).Cm!.Value;
        FoundPicture? pic = FoundPicture.Locate((object)app, (object)doc, Store.Load());
        if (pic is null) return WordPictureText.NoPicture;
        try
        {
            double w0 = pic.Width, h0 = pic.Height;
            string backup = ActiveWord.Backup(doc);
            WordDocs.OneUndoStep<int>((object)app, "Shamash: resize picture", () =>
            {
                pic.Obj.LockAspectRatio = -1;
                pic.Obj.Width = (float)WordPictureText.ToPoints(cm);
                return 0;
            });
            double w1 = pic.Width, h1 = pic.Height;
            if (Math.Abs(w1 - WordPictureText.ToPoints(cm)) > 1) return $"error: the width reads back as {WordPictureText.Cm(w1)} cm, not {WordEditText.Num(cm)} cm; {WordEditText.DocumentTag(ActiveWord.Name(doc))}";
            Store.Save(pic.Remember((string)doc.FullName));
            return WordEditText.Mutation(WordPictureText.Resized(pic.File, w0, h0, w1, h1), ActiveWord.Name(doc), backup);
        }
        finally { pic.Release(); }
    }
}

public sealed class SetPictureWrapTool(string? statePath = null) : PictureToolBase(statePath)
{
    public override string Name => "set_picture_wrap";
    public override string Description =>
        "Changes how text flows around the picture inserted last in this session (or, if none, the picture selected in Word): square or tight (text flows around, 'שהטקסט יזרום מסביב'), top_bottom (text above and below, 'מעל ומתחת') or inline (in the text line). Can be undone.";
    public override string InputSchema => """{"type":"object","properties":{"wrap":{"type":"string","enum":["inline","square","tight","top_bottom"]}},"required":["wrap"]}""";

    protected override string? Validate(JsonElement input) => WordPictureText.ParseWrap(input, required: true).Error;

    [SupportedOSPlatform("windows")]
    protected override string Execute(JsonElement input, dynamic app, dynamic doc)
    {
        var wrap = WordPictureText.ParseWrap(input, required: true).Wrap!;
        FoundPicture? pic = FoundPicture.Locate((object)app, (object)doc, Store.Load());
        if (pic is null) return WordPictureText.NoPicture;
        try
        {
            string was = pic.WrapName();
            if (was == wrap) return $"{WordPictureText.WrapChanged(pic.File, was, wrap)}; {WordEditText.DocumentTag(ActiveWord.Name(doc))}";
            string backup = ActiveWord.Backup(doc);
            var current = pic;
            pic = WordDocs.OneUndoStep<FoundPicture>((object)app, "Shamash: picture wrap", () => FoundPicture.ApplyWrap(current, wrap));
            string now = pic.WrapName();
            if (now != wrap) return $"error: the wrap reads back as {now}, not {wrap}; {WordEditText.DocumentTag(ActiveWord.Name(doc))}";
            Store.Save(pic.Remember((string)doc.FullName));
            return WordEditText.Mutation(WordPictureText.WrapChanged(pic.File, was, now), ActiveWord.Name(doc), backup);
        }
        finally { pic.Release(); }
    }
}

public static class WordPictureTools
{
    public static IEnumerable<IAgentTool> Create(IReadOnlyList<string> documentRoots, IReadOnlyList<string> pictureRoots) =>
    [
        new InsertPictureTool(documentRoots, pictureRoots),
        new ResizeLastPictureTool(),
        new SetPictureWrapTool(),
    ];
}
