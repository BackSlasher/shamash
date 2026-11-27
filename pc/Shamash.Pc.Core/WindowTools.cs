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

/// <summary>Shared by the four window tools: argument resolution and the Windows-only guard.</summary>
public abstract class WindowToolBase(IReadOnlyList<string> roots) : IAgentTool
{
    public const string WindowsOnly = "error: Windows only";
    public abstract string Name { get; }
    public abstract string Description { get; }
    public string InputSchema => """
        {"type":"object","properties":{"path":{"type":"string","description":"Full path, or folder/name as listed by search_documents"}},"required":["path"]}
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
    protected static WindowInfo? FindWindow(string full) =>
        WindowNative.VisibleWindows().FirstOrDefault(w => WindowMatcher.TitleMatches(w.Title, full));

    [SupportedOSPlatform("windows")]
    protected static string Describe(string full)
    {
        if (FindWindow(full) is { } w) return $"open: window \"{w.Title}\" (process {WindowNative.ProcessName(w.ProcessId)})";
        var held = WindowNative.HoldingProcesses(full);
        return held.Count > 0
            ? $"held by process {string.Join(", ", held.Select(WindowNative.ProcessName).Distinct())}, no window found"
            : "not open";
    }

    [SupportedOSPlatform("windows")]
    protected static string Focus(string full)
    {
        if (FindWindow(full) is not { } w) return "not open";
        return WindowNative.BringToFront(w.Handle) is { } step
            ? $"brought to front via {step}: window \"{w.Title}\""
            : "error: window found but Windows would not let it take the foreground";
    }
}

public sealed class ShowInExplorerTool(IReadOnlyList<string> roots) : WindowToolBase(roots)
{
    public override string Name => "show_in_explorer";
    public override string Description => "Opens the file manager (Explorer) at the folder of a document with the file selected. Returns the folder name.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full)
    {
        Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = $"/select,\"{full}\"" });
        return $"opened folder {Path.GetFileName(Path.GetDirectoryName(full))} with the file selected";
    }
}

public sealed class OpenFileTool(IReadOnlyList<string> roots) : WindowToolBase(roots)
{
    public override string Name => "open_file";
    public override string Description => "Opens a document in its default program. If it is already open, brings that window to the front instead.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full)
    {
        if (Describe(full).StartsWith("open:", StringComparison.Ordinal)) return "already open, " + Focus(full);
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return $"error: could not open: {e.Message}"; }
        return $"opened {Path.GetFileName(full)} with the default app";
    }
}

public sealed class IsDocumentOpenTool(IReadOnlyList<string> roots) : WindowToolBase(roots)
{
    public override string Name => "is_document_open";
    public override string Description => "Tells whether a document is open right now: matches window titles, then asks Windows which process holds the file.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full) => Describe(full);
}

public sealed class FocusDocumentTool(IReadOnlyList<string> roots) : WindowToolBase(roots)
{
    public override string Name => "focus_document";
    public override string Description => "Brings the window of an already open document to the front (restores it if minimized). Never opens a second copy.";

    [SupportedOSPlatform("windows")]
    protected override string RunWindows(string full) => Focus(full);
}
