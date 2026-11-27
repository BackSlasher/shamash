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

    /// <summary>Dialogs of Word, Excel and Outlook on screen: the given probe, else the Win32 one (none off Windows).</summary>
    public static IReadOnlyList<DialogInfo> ProbeDialogs(Func<IReadOnlyList<DialogInfo>>? probe = null)
    {
        if (probe is not null) return probe();
        if (!OperatingSystem.IsWindows()) return [];
#pragma warning disable CA1416
        return DialogProbe.Enumerate();
#pragma warning restore CA1416
    }

    /// <summary>The error with the Office dialog(s) now on screen appended, so the model can say what to press. A failing probe leaves the error as it was.</summary>
    public static string AppendDialog(string error, Func<IReadOnlyList<DialogInfo>>? probe = null)
    {
        try
        {
            var text = DialogText.Format(ProbeDialogs(probe));
            return text.Length == 0 ? error : $"{error}; {text}";
        }
        catch (Exception e) when (e is not OperationCanceledException) { return error; }
    }

    /// <summary><see cref="Describe"/> plus the dialog report.</summary>
    public static string DescribeWithDialogs(Exception e, Func<IReadOnlyList<DialogInfo>>? probe = null) => AppendDialog(Describe(e), probe);

    private static string FirstLine(string s) => s.Split('\n')[0].Trim();

    /// <summary>Runs a Windows-only COM action behind the guard and the timeout; every failure becomes "error: ...".</summary>
    public static string Guarded(Func<string> action)
    {
        if (!OperatingSystem.IsWindows()) return WindowsOnly;
        try { return RunSta(action); }
        catch (Exception e) when (e is not OperationCanceledException) { return DescribeWithDialogs(e); }
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
        dynamic? docs = null, doc = null, content = null, options = null;
        var openedHere = false;
        var restoreAlerts = false;
        int previousAlerts = 0;
        try
        {
            if (app is null)
            {
                // Our own instance: free to switch off everything that could ask a question.
                app = ComInterop.Create(ProgId);
                created = true;
                app.Visible = false;
                app.DisplayAlerts = 0;                // wdAlertsNone
                app.AutomationSecurity = 3;           // msoAutomationSecurityForceDisable: no macro prompts
                options = app.Options;
                options.ConfirmConversions = false;
            }
            else
            {
                // The user's Word: change no persistent option, only silence alerts around our call.
                previousAlerts = (int)app.DisplayAlerts;
                app.DisplayAlerts = 0;
                restoreAlerts = true;
            }
            docs = app.Documents;
            doc = FindOpen(docs, path);
            if (doc is null)
            {
                doc = docs.Open(FileName: path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false, OpenAndRepair: false, NoEncodingDialog: true, Visible: false);
                openedHere = true;
            }
            content = doc.Content;
            string text = content.Text;
            return LegacyDocReader.CleanWordText(text);
        }
        finally
        {
            try { if (openedHere && doc is not null) doc.Close(SaveChanges: 0); } catch (Exception) { }
            try { if (restoreAlerts && app is not null) app.DisplayAlerts = previousAlerts; } catch (Exception) { }
            try { if (created && app is not null) app.Quit(SaveChanges: 0); } catch (Exception) { }
            ComInterop.Release(content, options, doc, docs, app);
        }
    }

    /// <summary>The open Document with this full path (caller releases it) or null.</summary>
    private static object? FindOpen(dynamic docs, string path)
    {
        int count = docs.Count;
        for (var i = 1; i <= count; i++)
        {
            dynamic d = docs.Item(i);
            string name = d.FullName;
            if (DocumentIdentity.SamePath(name, path)) return d;
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

/// <summary>The documents open in running Word and Excel instances (never starts either).</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeOpenDocuments : IOpenDocuments
{
    private static readonly (string ProgId, string App, string Collection)[] Apps =
        [("Word.Application", "Word", "Documents"), ("Excel.Application", "Excel", "Workbooks")];

    public IReadOnlyList<OpenDocument> List()
    {
        try { return OfficeCom.RunSta(Collect); }
        catch (Exception e) when (e is not OperationCanceledException) { return []; }
    }

    private static List<OpenDocument> Collect()
    {
        var result = new List<OpenDocument>();
        foreach (var (progId, appName, collection) in Apps)
        {
            dynamic? app = null, items = null;
            try
            {
                app = ComInterop.GetActive(progId);
                if (app is null) continue;
                items = collection == "Documents" ? app.Documents : app.Workbooks;
                int count = items.Count;
                for (var i = 1; i <= count; i++)
                {
                    dynamic? item = null;
                    try
                    {
                        item = items.Item(i);
                        string full = item.FullName;
                        bool saved = item.Saved;
                        result.Add(new OpenDocument(full, appName, saved, Hwnd(app, item, appName)));
                    }
                    catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
                    finally { ComInterop.Release(item); }
                }
            }
            catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            finally { ComInterop.Release(items, app); }
        }
        return result;
    }

    /// <summary>Word: the document's own window (a hidden document has none); Excel: the application window.</summary>
    private static nint? Hwnd(dynamic app, dynamic item, string appName)
    {
        try
        {
            if (appName == "Word")
            {
                dynamic w = item.ActiveWindow;
                try { return (nint)(int)w.Hwnd; } finally { ComInterop.Release(w); }
            }
            return (nint)(int)app.Hwnd;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
    }

    public nint? Activate(OpenDocument doc)
    {
        try { return OfficeCom.RunSta(() => ActivateCore(doc)); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static nint? ActivateCore(OpenDocument doc)
    {
        var (progId, appName, collection) = Apps.First(a => a.App == doc.AppName);
        dynamic? app = ComInterop.GetActive(progId), items = null, item = null;
        if (app is null) return null;
        try
        {
            items = collection == "Documents" ? app.Documents : app.Workbooks;
            int count = items.Count;
            for (var i = 1; i <= count && item is null; i++)
            {
                dynamic d = items.Item(i);
                string name = d.FullName;
                if (DocumentIdentity.SamePath(name, doc.FullPath)) item = d; else ComInterop.Release(d);
            }
            if (item is null) return null;
            item.Activate();
            try { app.Visible = true; } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }
            try { app.Activate(); } catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { }   // Word has it; Excel may not
            if (appName == "Word")
            {
                dynamic w = app.ActiveWindow;
                try { return (nint)(int)w.Hwnd; } finally { ComInterop.Release(w); }
            }
            return (nint)(int)app.Hwnd;
        }
        catch (Exception e) when (e is COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return null; }
        finally { ComInterop.Release(item, items, app); }
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
            if (w is null) return OfficeCom.DescribeWithDialogs(e);
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
