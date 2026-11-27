using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>Plumbing shared by the Word and Outlook tools: one STA thread per call with a timeout, error text, late-bound COM helpers. No Interop assemblies: everything goes through <c>dynamic</c> over IDispatch.</summary>
public static class OfficeCom
{
    public const string WindowsOnly = "error: Windows only";
    /// <summary>A hung Office (modal dialog, security prompt) must not hang a turn.</summary>
    public static int TimeoutMs { get; set; } = 25_000;

    /// <summary>Runs <paramref name="f"/> on a fresh STA thread (plain thread off Windows). Throws TimeoutException if it does not finish; the stuck thread is abandoned (background).</summary>
    public static T RunSta<T>(Func<T> f, int? timeoutMs = null)
    {
        T? result = default;
        Exception? error = null;
        var t = new Thread(() => { try { result = f(); } catch (Exception e) { error = e; } }) { IsBackground = true, Name = "office-com" };
        if (OperatingSystem.IsWindows()) t.SetApartmentState(ApartmentState.STA);
        t.Start();
        var ms = timeoutMs ?? TimeoutMs;
        if (!t.Join(ms)) throw new TimeoutException($"Office did not answer within {ms / 1000} s (a dialog may be waiting on screen)");
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
        return result!;
    }

    /// <summary>"error: ..." text for an exception from a COM call; HRESULT in hex for COMException.</summary>
    public static string Describe(Exception e) => e switch
    {
        TimeoutException t => "error: " + t.Message,
        COMException c when (uint)c.HResult == 0x80040154 => "error: the Office application is not installed (0x80040154)",
        COMException c => $"error: Office call failed (0x{(uint)c.HResult:X8}): {FirstLine(c.Message)}",
        Microsoft.CSharp.RuntimeBinder.RuntimeBinderException b => $"error: Office call failed: {FirstLine(b.Message)}",
        _ => $"error: {e.GetType().Name}: {FirstLine(e.Message)}",
    };

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    /// <summary>Runs a Windows-only COM action behind the guard and the timeout; every failure becomes "error: ...".</summary>
    public static string Guarded(Func<string> action)
    {
        if (!OperatingSystem.IsWindows()) return WindowsOnly;
        try { return RunSta(action); }
        catch (Exception e) when (e is not OperationCanceledException) { return Describe(e); }
    }
}

/// <summary>Late-bound COM: attach to a running instance or create one, release RCWs.</summary>
[SupportedOSPlatform("windows")]
public static class ComInterop
{
    [DllImport("ole32.dll", CharSet = CharSet.Unicode)] private static extern int CLSIDFromProgID(string progId, out Guid clsid);
    [DllImport("oleaut32.dll")] private static extern int GetActiveObject(ref Guid clsid, nint reserved, [MarshalAs(UnmanagedType.IUnknown)] out object? obj);

    /// <summary>The running instance (Running Object Table) or null.</summary>
    public static object? GetActive(string progId)
    {
        if (CLSIDFromProgID(progId, out var clsid) != 0) return null;
        return GetActiveObject(ref clsid, 0, out var obj) == 0 ? obj : null;
    }

    public static object Create(string progId)
    {
        var type = Type.GetTypeFromProgID(progId, throwOnError: false) ?? throw new COMException($"{progId} is not registered", unchecked((int)0x80040154));
        return Activator.CreateInstance(type) ?? throw new COMException($"{progId} could not be created", unchecked((int)0x80080005));
    }

    public static void Release(params object?[] objs)
    {
        foreach (var o in objs)
        {
            try { if (o is not null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); }
            catch (Exception e) when (e is ArgumentException or InvalidComObjectException) { }
        }
    }
}

/// <summary>Word through COM: read text of .doc/.rtf, close a document by path.</summary>
[SupportedOSPlatform("windows")]
public static class WordAutomation
{
    private const string ProgId = "Word.Application";

    /// <summary>Text of a document via Word: reads it from the running Word when already open there, else opens it read-only and hidden (in a hidden Word of our own if none runs), then closes it without saving.</summary>
    public static string ReadText(string path)
    {
        dynamic? app = ComInterop.GetActive(ProgId);
        var created = false;
        dynamic? docs = null, doc = null, content = null;
        var openedHere = false;
        try
        {
            if (app is null)
            {
                app = ComInterop.Create(ProgId);
                created = true;
                app.Visible = false;
                app.DisplayAlerts = 0;
            }
            docs = app.Documents;
            doc = FindOpen(docs, path);
            if (doc is null)
            {
                doc = docs.Open(FileName: path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, NoEncodingDialog: true, Visible: false);
                openedHere = true;
            }
            content = doc.Content;
            string text = content.Text;
            return LegacyDocReader.CleanWordText(text);
        }
        finally
        {
            try { if (openedHere && doc is not null) doc.Close(SaveChanges: 0); } catch (Exception) { }
            try { if (created && app is not null) app.Quit(SaveChanges: 0); } catch (Exception) { }
            ComInterop.Release(content, doc, docs, app);
        }
    }

    /// <summary>The open Document with this full path (caller releases it) or null.</summary>
    private static object? FindOpen(dynamic docs, string path)
    {
        var full = Path.GetFullPath(path);
        int count = docs.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic d = docs.Item(i);
            string name = d.FullName;
            if (string.Equals(name, full, StringComparison.OrdinalIgnoreCase)) return d;
            ComInterop.Release(d);
        }
        return null;
    }

    /// <summary>Closes the document if Word has it open. Null when Word is not running or does not have it (the caller falls back to the window). save: ask, yes or no.</summary>
    public static string? Close(string path, string save)
    {
        dynamic? app = ComInterop.GetActive(ProgId);
        if (app is null) return null;
        dynamic? docs = null, doc = null;
        try
        {
            docs = app.Documents;
            doc = FindOpen(docs, path);
            if (doc is null) return null;
            bool saved = doc.Saved;
            if (saved) { doc.Close(SaveChanges: 0); return "closed (there were no unsaved changes)"; }
            switch (save)
            {
                case "no": doc.Close(SaveChanges: 0); return "closed without saving, the unsaved changes were discarded";
                case "yes":
                    if ((bool)doc.ReadOnly) return "error: the document is read-only, so it cannot be saved; not closed";
                    doc.Close(SaveChanges: -1);
                    return "saved and closed";
                default: return "unsaved changes; ask the user whether to save (not closed)";
            }
        }
        finally { ComInterop.Release(doc, docs, app); }
    }
}

public sealed class CloseDocumentTool(IReadOnlyList<string> roots) : IAgentTool
{
    public string Name => "close_document";
    public string Description =>
        "Closes an open document. In Word it checks for unsaved changes first: with save=ask (default) it does NOT close and says 'unsaved changes' so you can ask the user aloud whether to save, then call again with save=yes or no. " +
        "Other programs get a close request to their window and may show their own save prompt on screen. Never discards changes unless save=no.";
    public string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"Full path, or folder/name as listed by search_documents"},"save":{"type":"string","enum":["ask","yes","no"],"description":"What to do with unsaved changes, default ask"}},"required":["path"]}
        """;

    public static string? NormalizeSave(string? s) => string.IsNullOrWhiteSpace(s) ? "ask" : s.Trim().ToLowerInvariant() is "ask" or "yes" or "no" ? s.Trim().ToLowerInvariant() : null;

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var path = ToolText.GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return "error: path is required";
        var save = NormalizeSave(ToolText.GetString(input, "save"));
        if (save is null) return "error: save must be ask, yes or no";
        var full = ReadDocumentTool.Resolve(path, roots);
        if (full is null) return "error: path not under a document root";
        if (!File.Exists(full)) return "error: file not found";
        if (!OperatingSystem.IsWindows()) return OfficeCom.WindowsOnly;
        return CloseWindows(full, save);
    }, ct);

    [SupportedOSPlatform("windows")]
    private static string CloseWindows(string full, string save)
    {
        string? viaWord = null;
        var wordTried = false;
        try
        {
            viaWord = OfficeCom.RunSta(() => WordAutomation.Close(full, save));
            wordTried = true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Word missing or hung: say so only when no window fallback applies.
            var w = FindWindow(full);
            if (w is null) return OfficeCom.Describe(e);
        }
        if (viaWord is not null) return viaWord;
        var win = FindWindow(full);
        if (win is null) return wordTried ? "not open" : "error: Word did not answer and no window of the document was found";
        try
        {
            return WindowNative.RequestClose(win.Handle)
                ? $"sent a close request to the window \"{win.Title}\"; the program may show its own save prompt, so ask the user to answer on the screen"
                : "error: could not send the close request to the window";
        }
        catch (Exception e) when (e is not OperationCanceledException) { return $"error: {e.GetType().Name}: {e.Message}"; }
    }

    [SupportedOSPlatform("windows")]
    private static WindowInfo? FindWindow(string full) =>
        WindowNative.VisibleWindows().FirstOrDefault(w => WindowMatcher.TitleMatches(w.Title, full));
}
