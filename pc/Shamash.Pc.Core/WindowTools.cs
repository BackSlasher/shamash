using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>Pure title matching, testable everywhere: the file name with or without extension, case-insensitive.</summary>
public static class WindowMatcher
{
    public static bool TitleMatches(string title, string filePath)
    {
        if (string.IsNullOrWhiteSpace(title)) return false;
        var name = Path.GetFileName(filePath);
        var stem = Path.GetFileNameWithoutExtension(filePath);
        return (name.Length > 0 && title.Contains(name, StringComparison.OrdinalIgnoreCase))
            || (stem.Length > 0 && title.Contains(stem, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed record WindowInfo(nint Handle, string Title, int ProcessId);

/// <summary>All the Win32 calls in one place. Windows only.</summary>
[SupportedOSPlatform("windows")]
public static class WindowNative
{
    private delegate bool EnumProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc cb, nint lParam);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint from, uint to, bool attach);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowTextW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    private delegate bool EnumChildProc(nint hwnd, nint lParam);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint parent, EnumChildProc cb, nint lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint msg, nint wParam, StringBuilder lParam, uint flags, uint timeoutMs, out nint result);

    private const uint WmGetText = 0x000D;
    private const uint SmtoAbortIfHung = 0x0002;

    private static string ClassName(nint hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassNameW(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Text of a control in another process: WM_GETTEXT with a short timeout (GetWindowText does not cross processes for controls, and a hung owner must not hang us).</summary>
    private static string ControlText(nint hwnd)
    {
        var sb = new StringBuilder(2048);
        return SendMessageTimeoutW(hwnd, WmGetText, sb.Capacity, sb, SmtoAbortIfHung, 250, out _) != 0 ? sb.ToString() : "";
    }

    /// <summary>Visible top-level windows of class #32770 (standard dialog) or NUIDialog (Office 2013+ alerts): handle, class, title, owning process id.</summary>
    public static List<(nint Handle, string Class, string Title, int ProcessId)> DialogWindows()
    {
        var list = new List<(nint, string, string, int)>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var cls = ClassName(h);
            if (cls is not ("#32770" or "NUIDialog")) return true;
            var sb = new StringBuilder(512);
            GetWindowTextW(h, sb, sb.Capacity);
            GetWindowThreadProcessId(h, out var pid);
            list.Add((h, cls, sb.ToString(), (int)pid));
            return true;
        }, 0);
        return list;
    }

    /// <summary>Visible child controls of a window as (class, text); only the ones with text.</summary>
    public static List<(string Class, string Text)> ChildControls(nint parent)
    {
        var list = new List<(string, string)>();
        EnumChildWindows(parent, (h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var text = ControlText(h);
            if (text.Length > 0) list.Add((ClassName(h), text));
            return true;
        }, 0);
        return list;
    }

    /// <summary>Posts WM_CLOSE: the app decides, and may show its own "save changes?" prompt.</summary>
    public static bool RequestClose(nint hwnd) => PostMessageW(hwnd, 0x0010, 0, 0);

    private const int SwRestore = 9;
    private const byte VkMenu = 0x12;
    private const uint KeyUp = 2;

    /// <summary>Visible top-level windows with a title.</summary>
    public static List<WindowInfo> VisibleWindows()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512);
            if (GetWindowTextW(h, sb, sb.Capacity) <= 0) return true;
            GetWindowThreadProcessId(h, out var pid);
            list.Add(new WindowInfo(h, sb.ToString(), (int)pid));
            return true;
        }, 0);
        return list;
    }

    public static string ProcessName(int pid)
    {
        try { return Process.GetProcessById(pid).ProcessName; } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return $"pid {pid}"; }
    }

    /// <summary>Restores if minimized, then tries three ways to take the foreground. Returns the step that worked, or null.</summary>
    public static string? BringToFront(nint hwnd)
    {
        if (IsIconic(hwnd)) ShowWindow(hwnd, SwRestore);
        SetForegroundWindow(hwnd);
        if (IsFront(hwnd)) return "SetForegroundWindow";

        var fg = GetForegroundWindow();
        var fgThread = fg == 0 ? 0 : GetWindowThreadProcessId(fg, out _);
        var me = GetCurrentThreadId();
        if (fgThread != 0 && AttachThreadInput(me, fgThread, true))
        {
            try { SetForegroundWindow(hwnd); }
            finally { AttachThreadInput(me, fgThread, false); }
            if (IsFront(hwnd)) return "AttachThreadInput";
        }

        keybd_event(VkMenu, 0, 0, 0);
        keybd_event(VkMenu, 0, KeyUp, 0);
        SetForegroundWindow(hwnd);
        return IsFront(hwnd) ? "ALT key trick" : null;
    }

    private static bool IsFront(nint hwnd) { Thread.Sleep(50); return GetForegroundWindow() == hwnd; }

    // Restart Manager: which processes hold the file open.
    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess { public int Pid; public long StartTime; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int AppType;
        public uint SessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmStartSession(out uint session, int flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)] private static extern int RmRegisterResources(uint session, uint nFiles, string[] files, uint nApps, nint apps, uint nServices, string[]? services);
    [DllImport("rstrtmgr.dll")] private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info, out uint reasons);
    [DllImport("rstrtmgr.dll")] private static extern int RmEndSession(uint session);

    /// <summary>Process ids holding the file; empty on any failure.</summary>
    public static List<int> HoldingProcesses(string path)
    {
        var pids = new List<int>();
        if (RmStartSession(out var session, 0, new StringBuilder(33)) != 0) return pids;
        try
        {
            if (RmRegisterResources(session, 1, [path], 0, 0, 0, null) != 0) return pids;
            uint needed = 0, count = 0;
            var rc = RmGetList(session, out needed, ref count, null, out _);
            if (rc != 234 /* ERROR_MORE_DATA */ || needed == 0) return pids;
            var info = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out _, ref count, info, out _) == 0)
                pids.AddRange(info.Take((int)count).Select(i => i.Process.Pid));
        }
        finally { RmEndSession(session); }
        return pids;
    }
}

/// <summary>A document open in Word or Excel, as the application itself reports it.</summary>
public sealed record OpenDocument(string FullPath, string AppName, bool Saved, nint? Hwnd);

/// <summary>Seam over the Office object models: which documents are open right now, with exact paths.</summary>
public interface IOpenDocuments
{
    /// <summary>Documents open in Word and Excel; empty when neither runs or Office does not answer.</summary>
    IReadOnlyList<OpenDocument> List();
    /// <summary>Activates the document through COM and returns its window handle, or null when that failed.</summary>
    nint? Activate(OpenDocument doc);
}

public enum DocMatch { None, Exact, SameName, Title, Held }

public sealed record DocLookup(DocMatch Match, string Text, OpenDocument? Doc = null, WindowInfo? Window = null);

/// <summary>Which window or Office document is the file: exact full path from Office first, window title only as a fallback. Everything injectable so it runs on Linux.</summary>
public static class DocumentIdentity
{
    /// <summary>Full path with separators normalised and no trailing separator; compare with OrdinalIgnoreCase.</summary>
    public static string Normalize(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return path.Trim(); }
    }

    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    public static OpenDocument? FindExact(IReadOnlyList<OpenDocument> docs, string full) => docs.FirstOrDefault(d => SamePath(d.FullPath, full));

    public static OpenDocument? FindSameName(IReadOnlyList<OpenDocument> docs, string full) =>
        docs.FirstOrDefault(d => string.Equals(Path.GetFileName(d.FullPath), Path.GetFileName(full), StringComparison.OrdinalIgnoreCase) && !SamePath(d.FullPath, full));

    private static IReadOnlyList<OpenDocument> Safe(IOpenDocuments docs)
    {
        try { return docs.List(); } catch (Exception e) when (e is not OperationCanceledException) { return []; }
    }

    public static DocLookup Lookup(string full, IOpenDocuments docs, Func<IReadOnlyList<WindowInfo>> windows, Func<string, IReadOnlyList<int>> holders, Func<int, string> processName)
    {
        var list = Safe(docs);
        if (FindExact(list, full) is { } exact)
            return new DocLookup(DocMatch.Exact, $"open in {exact.AppName}: {exact.FullPath}" + (exact.Saved ? "" : " (has unsaved changes)"), exact);
        if (FindSameName(list, full) is { } other)
            return new DocLookup(DocMatch.SameName, $"not open; a different file with the same name is open: {other.FullPath} (in {other.AppName})", other);
        if (windows().FirstOrDefault(w => WindowMatcher.TitleMatches(w.Title, full)) is { } w)
            return new DocLookup(DocMatch.Title, $"open: window \"{w.Title}\" (process {processName(w.ProcessId)}); matched by window title only, the exact path is not verified", null, w);
        var held = holders(full);
        return held.Count > 0
            ? new DocLookup(DocMatch.Held, $"held by process {string.Join(", ", held.Select(processName).Distinct())}, no window found")
            : new DocLookup(DocMatch.None, "not open");
    }

    /// <summary>Brings the document to the front: exact Office documents are activated through COM first, then their window is raised by handle; otherwise the title-matched window.</summary>
    public static string Focus(string full, IOpenDocuments docs, Func<IReadOnlyList<WindowInfo>> windows, Func<nint, string?> bringToFront, Func<int, string> processName)
    {
        var look = Lookup(full, docs, windows, _ => [], processName);
        switch (look.Match)
        {
            case DocMatch.Exact:
            {
                var d = look.Doc!;
                nint? hwnd = null;
                try { hwnd = docs.Activate(d); } catch (Exception e) when (e is not OperationCanceledException) { }
                hwnd ??= d.Hwnd;
                if (hwnd is null or 0)
                {
                    var byTitle = windows().FirstOrDefault(w => WindowMatcher.TitleMatches(w.Title, full));
                    if (byTitle is null) return $"error: {d.AppName} has the document open ({d.FullPath}) but gave no window to bring to the front";
                    hwnd = byTitle.Handle;
                }
                return bringToFront(hwnd.Value) is { } step
                    ? $"brought to front via {step}: {d.AppName} document {d.FullPath}"
                    : "error: window found but Windows would not let it take the foreground";
            }
            case DocMatch.Title:
            {
                var w = look.Window!;
                return bringToFront(w.Handle) is { } step
                    ? $"brought to front via {step}: window \"{w.Title}\" (matched by window title only)"
                    : "error: window found but Windows would not let it take the foreground";
            }
            default:
                return look.Match == DocMatch.SameName ? look.Text : "not open";
        }
    }
}

/// <summary>Shared by the four window tools: argument resolution and the Windows-only guard.</summary>
public abstract class WindowToolBase(IReadOnlyList<string> roots, IOpenDocuments? docs = null) : IAgentTool
{
    public const string WindowsOnly = "error: Windows only";
    public abstract string Name { get; }
    public abstract string Description { get; }
    public string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"As search shows it (Desktop/..., Documents/...), a Hebrew root name, or a full path"}},"required":["path"]}
        """;

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var path = ToolText.GetString(input, "path");
        if (string.IsNullOrWhiteSpace(path)) return "error: path is required";
        var full = ReadDocumentTool.Resolve(path, roots);
        if (full is null) return "error: path not under a document root";
        if (!File.Exists(full)) return "error: file not found";
        if (!OperatingSystem.IsWindows()) return WindowsOnly;
        try { return RunWindows(full); }
        catch (Exception e) when (e is not OperationCanceledException) { return $"error: {e.GetType().Name}: {e.Message}"; }
    }, ct);

    [SupportedOSPlatform("windows")]
    protected abstract string RunWindows(string full);

    [SupportedOSPlatform("windows")]
    protected IOpenDocuments Docs => docs ?? new OfficeOpenDocuments();

    [SupportedOSPlatform("windows")]
    protected DocLookup Lookup(string full) =>
        DocumentIdentity.Lookup(full, Docs, WindowNative.VisibleWindows, WindowNative.HoldingProcesses, WindowNative.ProcessName);

    [SupportedOSPlatform("windows")]
    protected string Focus(string full) =>
        DocumentIdentity.Focus(full, Docs, WindowNative.VisibleWindows, WindowNative.BringToFront, WindowNative.ProcessName);
}

public sealed class ShowInExplorerTool(IReadOnlyList<string> roots) : WindowToolBase(roots)
{
    public override string Name => "show_in_explorer";
    public override string Description => "Opens the file manager (Explorer) at the folder of a document with the file selected. Returns the folder name. path is as search shows it (Desktop/..., Documents/...), a Hebrew root name, or a full path.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full)
    {
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{full}\"" });
        return $"opened folder {Path.GetFileName(Path.GetDirectoryName(full))} with the file selected";
    }
}

public sealed class OpenFileTool(IReadOnlyList<string> roots, IOpenDocuments? docs = null) : WindowToolBase(roots, docs)
{
    public override string Name => "open_file";
    public override string Description => "Opens a document in its default program. If that exact file is already open (Word and Excel are asked for the exact path, other programs are matched by window title), brings it to the front instead.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full)
    {
        if (Lookup(full).Match is DocMatch.Exact or DocMatch.Title) return "already open, " + Focus(full);
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return $"error: could not open: {e.Message}"; }
        return $"opened {Path.GetFileName(full)} with the default app";
    }
}

public sealed class IsDocumentOpenTool(IReadOnlyList<string> roots, IOpenDocuments? docs = null) : WindowToolBase(roots, docs)
{
    public override string Name => "is_document_open";
    public override string Description => "Tells whether a document is open right now. Word and Excel are asked through COM for the exact full path (so two files with the same name are told apart); other files are matched by window title (the result says so), then Windows is asked which process holds the file.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full) => Lookup(full).Text;
}

public sealed class FocusDocumentTool(IReadOnlyList<string> roots, IOpenDocuments? docs = null) : WindowToolBase(roots, docs)
{
    public override string Name => "focus_document";
    public override string Description => "Brings the window of an already open document to the front (restores it if minimized). Word and Excel documents are matched by exact path and activated through COM. Never opens a second copy.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full) => Focus(full);
}

/// <summary>What a dialog of Word, Excel or Outlook shows: title, its text and its buttons.</summary>
public sealed record DialogInfo(string App, string Title, IReadOnlyList<string> Texts, IReadOnlyList<string> Buttons);

/// <summary>Plain formatting of dialog descriptions, testable everywhere.</summary>
public static class DialogText
{
    private const int MaxText = 500;

    private static string Clean(string s) => string.Join(' ', s.Replace("&", "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static string Format(DialogInfo d)
    {
        var texts = d.Texts.Select(Clean).Where(t => t.Length > 0).Distinct().ToList();
        var buttons = d.Buttons.Select(Clean).Where(t => t.Length > 0).Distinct().ToList();
        var body = string.Join(" ", texts);
        if (body.Length > MaxText) body = body[..MaxText] + "...";
        var title = Clean(d.Title);
        var s = $"{d.App} is showing a dialog \"{title}\"";
        if (body.Length > 0) s += ": " + body;
        if (buttons.Count > 0) s += $" [buttons: {string.Join(", ", buttons)}]";
        return s;
    }

    /// <summary>One sentence per dialog joined by "; ", or "" when there are none.</summary>
    public static string Format(IReadOnlyList<DialogInfo> dialogs) => string.Join("; ", dialogs.Select(Format));
}

/// <summary>Looks at (never clicks) the dialogs Word, Excel and Outlook have on screen.</summary>
[SupportedOSPlatform("windows")]
public static class DialogProbe
{
    private static string? AppOf(int pid)
    {
        var name = WindowNative.ProcessName(pid);
        return name.ToUpperInvariant() switch { "WINWORD" => "Word", "EXCEL" => "Excel", "OUTLOOK" => "Outlook", _ => null };
    }

    public static IReadOnlyList<DialogInfo> Enumerate()
    {
        var result = new List<DialogInfo>();
        foreach (var (h, _, title, pid) in WindowNative.DialogWindows())
        {
            if (AppOf(pid) is not { } app) continue;
            var texts = new List<string>();
            var buttons = new List<string>();
            foreach (var (cls, text) in WindowNative.ChildControls(h))
            {
                if (cls.Equals("Button", StringComparison.OrdinalIgnoreCase)) buttons.Add(text);
                else if (cls.Equals("Static", StringComparison.OrdinalIgnoreCase) || cls.StartsWith("RichEdit", StringComparison.OrdinalIgnoreCase)) texts.Add(text);
            }
            result.Add(new DialogInfo(app, title, texts, buttons));
        }
        return result;
    }
}

/// <summary>The tool form of <see cref="DialogProbe"/>: what Office dialogs are on screen right now.</summary>
public sealed class DescribeOfficeDialogsTool(Func<IReadOnlyList<DialogInfo>>? probe = null) : IAgentTool
{
    public string Name => "describe_office_dialogs";
    public string Description => "Reads (does not click) any dialog that Word, Excel or Outlook has on screen: its title, text and buttons. Use it when an Office tool stalls or reports a dialog, so you can tell the user what it says and which button to press.";
    public string InputSchema => """{"type":"object","properties":{}}""";

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        if (probe is null && !OperatingSystem.IsWindows()) return OfficeCom.WindowsOnly;
        try
        {
            var text = DialogText.Format(OfficeCom.ProbeDialogs(probe));
            return text.Length == 0 ? "no Office dialogs are open" : text;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return $"error: {e.GetType().Name}: {e.Message}"; }
    }, ct);
}
