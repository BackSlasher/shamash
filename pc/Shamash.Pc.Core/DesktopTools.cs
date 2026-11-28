using System.Text;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>One thing on a window UI Automation reports. Kind: button, checkbox, radio, link, combobox, edit, tab, menuitem, listitem or text.</summary>
public sealed record UiElement(string Kind, string Name, bool Enabled, bool? Toggled, bool Selected, string? Value);

/// <summary>A window and what is on it, in reading order. Truncated: the walk stopped at the element limit.</summary>
public sealed record WindowDescription(string Title, string Process, IReadOnlyList<UiElement> Elements, bool Truncated);

/// <summary>
/// The Windows-only half of the generic window tools (UI Automation, Win32). The tools in this file own all matching,
/// formatting and safety decisions and only call this seam, so they run in the Linux test container against a fake.
/// </summary>
public interface IDesktopAutomation
{
    /// <summary>Visible top-level windows, front first; without our own process, windows with no title and shell windows (Progman, WorkerW, Windows.UI.Core.CoreWindow).</summary>
    IReadOnlyList<WindowInfo> Windows();
    WindowInfo? Foreground();
    string ProcessName(int processId);
    bool IsMinimized(nint hwnd);
    /// <summary>Interactive elements and visible static text, in reading order; null when the window is gone or cannot be read.</summary>
    WindowDescription? Describe(nint hwnd, int maxElements);
    /// <summary>Invokes the element by its pattern (Invoke, Toggle, SelectionItem, ExpandCollapse); falls back to focusing the window and clicking the centre. Null on success, else the problem.</summary>
    string? Click(nint hwnd, string name, string? kind);
    /// <summary>Posts WM_CLOSE; the program decides and may ask about unsaved work.</summary>
    bool Close(nint hwnd);
    /// <summary>The step that worked, or null when Windows would not let the window take the foreground.</summary>
    string? BringToFront(nint hwnd);
    /// <summary>Sends one key from <see cref="DesktopText.Keys"/> to the foreground window. Null on success, else the problem.</summary>
    string? PressKey(string key);
}

/// <summary>Screenshot of the whole virtual screen, already scaled and PNG-encoded; null when capture is not possible.</summary>
public interface IScreenCapture
{
    ToolImage? Capture();
}

/// <summary>What the Windows host supplies to <see cref="TurnPipeline.BuildTools"/>; without it the screen and window tools are left out.</summary>
public sealed record DesktopServices(IScreenCapture Screen, IDesktopAutomation Automation, string DataDir);

/// <summary>Matching and formatting shared by the window tools, pure so it is testable everywhere.</summary>
public static class DesktopText
{
    public const int MaxListed = 15;
    public const int MaxDescribeElements = 60;
    public const int MaxDescribeChars = 2500;
    public const int MaxDialogElements = 15;
    public const int MaxStillOpenElements = 10;
    public const int MaxTitleChars = 120;

    /// <summary>The only keys press_key sends; "alt+f4" is the one way to close a window that has no close button we can reach.</summary>
    public static readonly IReadOnlyList<string> Keys = ["escape", "enter", "tab", "space", "up", "down", "left", "right", "alt+f4"];

    /// <summary>Window titles and control names come from other programs: one line, no accelerator ampersands, bounded.</summary>
    public static string Clean(string? s, bool dropAmpersand = false)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (dropAmpersand) s = s.Replace("&", "");
        var t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= MaxTitleChars ? t : t[..MaxTitleChars] + "...";
    }

    public static string Label(WindowInfo w, IDesktopAutomation auto) => $"\"{Clean(w.Title)}\" ({auto.ProcessName(w.ProcessId)})";

    /// <summary>"escape", "Esc", "ALT + F4", "Return" and the like to the whitelist spelling; null when the key is not allowed.</summary>
    public static string? NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        var k = key.Trim().ToLowerInvariant().Replace(" ", "");
        k = k switch { "esc" => "escape", "return" => "enter", "spacebar" => "space", "uparrow" => "up", "downarrow" => "down", "leftarrow" => "left", "rightarrow" => "right", "alt+f4" or "alt-f4" => "alt+f4", _ => k };
        return Keys.Contains(k) ? k : null;
    }

    /// <summary>
    /// The window the user means: exact title or process name first, then a title substring, then a process-name substring
    /// (case-insensitive; ".exe" ignored). Null query means the foreground window.
    /// </summary>
    public static WindowInfo? Find(IDesktopAutomation auto, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return auto.Foreground();
        var q = query.Trim();
        if (q.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) q = q[..^4];
        var all = auto.Windows();
        return all.FirstOrDefault(w => string.Equals(Clean(w.Title), q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => string.Equals(auto.ProcessName(w.ProcessId), q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => w.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(w => auto.ProcessName(w.ProcessId).Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public static string NoMatch(IDesktopAutomation auto, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "error: no window is in front";
        var all = auto.Windows();
        var listed = all.Count == 0 ? "none" : string.Join(", ", all.Take(MaxListed).Select(w => Label(w, auto)));
        return $"error: no window matches \"{Clean(query)}\"; visible windows: {listed}";
    }

    public static string ElementLine(UiElement e)
    {
        var sb = new StringBuilder("- ").Append(e.Kind).Append(": ").Append(Clean(e.Name));
        if (!e.Enabled) sb.Append(" [disabled]");
        if (e.Toggled is { } on) sb.Append(on ? " [checked]" : " [unchecked]");
        if (e.Selected) sb.Append(" [selected]");
        if (!string.IsNullOrWhiteSpace(e.Value)) sb.Append(" (value: ").Append(Clean(e.Value)).Append(')');
        return sb.ToString();
    }

    /// <summary>Element lines with each run of consecutive static text joined into one "- text: ..." line, capped by lines and characters.</summary>
    public static IReadOnlyList<string> Lines(WindowDescription d, int maxLines, int maxChars, out bool more)
    {
        var lines = new List<string>();
        var run = new List<string>();
        void Flush()
        {
            if (run.Count > 0) lines.Add("- text: " + Clean(string.Join(' ', run)));
            run.Clear();
        }
        foreach (var e in d.Elements)
        {
            if (e.Kind == "text") { if (!string.IsNullOrWhiteSpace(e.Name)) run.Add(e.Name); continue; }
            Flush();
            lines.Add(ElementLine(e));
        }
        Flush();

        more = d.Truncated;
        var kept = new List<string>();
        var chars = 0;
        foreach (var l in lines)
        {
            if (kept.Count >= maxLines || chars + l.Length > maxChars) { more = true; break; }
            kept.Add(l);
            chars += l.Length + 1;
        }
        return kept;
    }

    public static string Format(WindowDescription d, int maxLines = MaxDescribeElements, int maxChars = MaxDescribeChars)
    {
        var lines = Lines(d, maxLines, maxChars, out var more);
        var sb = new StringBuilder($"window \"{Clean(d.Title)}\" ({d.Process})");
        foreach (var l in lines) sb.Append('\n').Append(l);
        if (more) sb.Append("\n... (more)");
        return sb.ToString();
    }

    /// <summary>One line for results that append what a window shows: "kind: name, kind: name", without the leading dashes.</summary>
    public static string Inline(WindowDescription d, int maxElements)
    {
        var lines = Lines(d, maxElements, MaxDescribeChars, out var more);
        var s = string.Join("; ", lines.Select(l => l[2..]));
        if (s.Length == 0) s = "nothing readable";
        return more ? s + "; ... (more)" : s;
    }

    public static string NowInFront(IDesktopAutomation auto) =>
        auto.Foreground() is { } f ? $"now in front: {Label(f, auto)}" : "now in front: nothing";
}

/// <summary>Shared by the generic window tools: the guard text every description repeats and the delay seam.</summary>
public abstract class DesktopToolBase(IDesktopAutomation auto, Func<TimeSpan, Task>? delay = null) : IAgentTool
{
    /// <summary>Said in every description so the model keeps these for surprises and leaves Word, Excel and Outlook to their own tools.</summary>
    protected const string Scope =
        " Only for getting out of unexpected situations (a dialog, a program opened by accident, a question the computer is asking); " +
        "do not use it to operate Word, Excel or Outlook, which have their own tools. Never types text and never starts programs.";

    protected IDesktopAutomation Auto { get; } = auto;
    protected Task Delay(TimeSpan t) => delay is null ? Task.Delay(t) : delay(t);

    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract string InputSchema { get; }
    protected abstract Task<string> RunCoreAsync(JsonElement input, CancellationToken ct);

    public async Task<string> RunAsync(JsonElement input, CancellationToken ct)
    {
        try { return await RunCoreAsync(input, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { return $"error: {e.GetType().Name}: {e.Message}"; }
    }
}

public sealed class ListWindowsTool(IDesktopAutomation auto, Func<TimeSpan, Task>? delay = null) : DesktopToolBase(auto, delay)
{
    public override string Name => "list_windows";
    public override string Description => "Lists the windows open on the screen, front first, marking the one in front and minimized ones." + Scope;
    public override string InputSchema => """{"type":"object","properties":{}}""";

    protected override Task<string> RunCoreAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var all = Auto.Windows();
        if (all.Count == 0) return "no visible windows";
        var front = Auto.Foreground();
        var sb = new StringBuilder();
        var i = 0;
        foreach (var w in all.Take(DesktopText.MaxListed))
        {
            if (i > 0) sb.Append('\n');
            sb.Append(++i).Append(". ").Append(DesktopText.Label(w, Auto));
            if (front is not null && front.Handle == w.Handle) sb.Append(" [front]");
            if (Auto.IsMinimized(w.Handle)) sb.Append(" [minimized]");
        }
        if (all.Count > DesktopText.MaxListed) sb.Append($"\n... and {all.Count - DesktopText.MaxListed} more");
        return sb.ToString();
    }, ct);
}

public sealed class DescribeWindowTool(IDesktopAutomation auto, Func<TimeSpan, Task>? delay = null) : DesktopToolBase(auto, delay)
{
    public override string Name => "describe_window";
    public override string Description =>
        "Reads what is on a window: its buttons, checkboxes, links, fields and visible text, without touching anything. " +
        "window is a part of the title or the program name; omit it for the window in front." + Scope;
    public override string InputSchema => """{"type":"object","properties":{"window":{"type":"string","description":"Part of the window title, or the program name (default: the window in front)"}}}""";

    protected override Task<string> RunCoreAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var query = ToolText.GetString(input, "window");
        if (DesktopText.Find(Auto, query) is not { } w) return DesktopText.NoMatch(Auto, query);
        var d = Auto.Describe(w.Handle, DesktopText.MaxDescribeElements + 1);
        if (d is null) return $"error: could not read the window {DesktopText.Label(w, Auto)}";
        return DesktopText.Format(d);
    }, ct);
}

public sealed class ClickControlTool(IDesktopAutomation auto, Func<IReadOnlyList<DialogInfo>>? officeProbe = null, Func<TimeSpan, Task>? delay = null) : DesktopToolBase(auto, delay)
{
    public const int SettleMs = 700;
    private const int ScanElements = 200;
    private static readonly string[] OfficeProcesses = ["WINWORD", "EXCEL", "OUTLOOK"];

    public override string Name => "click_control";
    public override string Description =>
        "Clicks one button, checkbox, link, tab or menu item of a window, found by its name as describe_window shows it (Hebrew or English). " +
        "Brings the window to the front first and reports what is in front afterwards, including a dialog that appeared. " +
        "Prefer Cancel or No over OK, and never click anything that confirms, agrees, buys, deletes or sends unless the user clearly said yes. " +
        "window is a part of the title or the program name (default: the window in front); kind (optional) narrows the match: button, checkbox, radio, link, combobox, tab, menuitem, listitem." + Scope;
    public override string InputSchema => """
        {"type":"object","properties":{"window":{"type":"string","description":"Part of the window title, or the program name (default: the window in front)"},"name":{"type":"string","description":"The control's name as describe_window shows it"},"kind":{"type":"string","description":"Optional: button, checkbox, radio, link, combobox, tab, menuitem, listitem"}},"required":["name"]}
        """;

    protected override async Task<string> RunCoreAsync(JsonElement input, CancellationToken ct)
    {
        var name = ToolText.GetString(input, "name");
        if (string.IsNullOrWhiteSpace(name)) return "error: name is required";
        var kind = ToolText.GetString(input, "kind")?.Trim().ToLowerInvariant();
        if (kind is { Length: 0 }) kind = null;
        var query = ToolText.GetString(input, "window");
        if (DesktopText.Find(Auto, query) is not { } w) return DesktopText.NoMatch(Auto, query);
        var d = Auto.Describe(w.Handle, ScanElements);
        if (d is null) return $"error: could not read the window {DesktopText.Label(w, Auto)}";

        var pick = Pick(d, name, kind, out var error);
        if (pick is null) return error!;
        if (!pick.Enabled) return $"error: the {pick.Kind} \"{DesktopText.Clean(pick.Name, true)}\" is disabled";

        Auto.BringToFront(w.Handle);
        if (Auto.Click(w.Handle, pick.Name, pick.Kind) is { } problem) return $"error: {problem}";
        await Delay(TimeSpan.FromMilliseconds(SettleMs));
        ct.ThrowIfCancellationRequested();

        var result = $"clicked {pick.Kind} \"{DesktopText.Clean(pick.Name, true)}\"; {DesktopText.NowInFront(Auto)}";
        var front = Auto.Foreground();
        if (!Auto.Windows().Any(x => x.Handle == w.Handle)) return result + "; the window closed";
        if (front is not null && front.Handle != w.Handle) result += "; a dialog appeared: " + DescribeDialog(front);
        return result;
    }

    /// <summary>Exact name first (accelerator ampersands and spacing ignored), then case-insensitive contains. More than one candidate at the winning tier is refused rather than guessed.</summary>
    public static UiElement? Pick(WindowDescription d, string name, string? kind, out string? error)
    {
        error = null;
        var wanted = DesktopText.Clean(name, true);
        var candidates = d.Elements.Where(e => e.Kind != "text" && (kind is null || e.Kind == kind)).ToList();
        var exact = candidates.Where(e => string.Equals(DesktopText.Clean(e.Name, true), wanted, StringComparison.Ordinal)).ToList();
        var tier = exact.Count > 0 ? exact : candidates.Where(e => DesktopText.Clean(e.Name, true).Contains(wanted, StringComparison.OrdinalIgnoreCase)).ToList();
        if (tier.Count == 1) return tier[0];
        if (tier.Count == 0)
        {
            var names = candidates.Select(e => DesktopText.Clean(e.Name, true)).Where(n => n.Length > 0).Distinct().Take(DesktopText.MaxListed);
            error = $"error: no control named \"{wanted}\" in \"{DesktopText.Clean(d.Title)}\"; controls: {string.Join(", ", names)}";
            return null;
        }
        error = $"error: {tier.Count} controls named \"{wanted}\"; be more specific (kinds: {string.Join(", ", tier.Select(e => e.Kind).Distinct())})";
        return null;
    }

    /// <summary>Office dialogs have their own reader (title, text, buttons); anything else is read through UI Automation.</summary>
    private string DescribeDialog(WindowInfo front)
    {
        if (OfficeProcesses.Contains(Auto.ProcessName(front.ProcessId), StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var text = DialogText.Format(OfficeCom.ProbeDialogs(officeProbe));
                if (text.Length > 0) return text;
            }
            catch (Exception e) when (e is not OperationCanceledException) { }
        }
        var d = Auto.Describe(front.Handle, DesktopText.MaxDialogElements);
        return d is null ? DesktopText.Label(front, Auto) : $"\"{DesktopText.Clean(d.Title)}\": " + DesktopText.Inline(d, DesktopText.MaxDialogElements);
    }
}

public sealed class CloseWindowTool(IDesktopAutomation auto, Func<TimeSpan, Task>? delay = null) : DesktopToolBase(auto, delay)
{
    public const int WaitMs = 1000;
    public override string Name => "close_window";
    public override string Description =>
        "Asks one window to close (like its X button). If the program asks something first, for example about unsaved changes, the result says so and shows what the window offers. " +
        "window is a part of the title or the program name. Will not close the desktop or taskbar (explorer) or this assistant." + Scope;
    public override string InputSchema => """{"type":"object","properties":{"window":{"type":"string","description":"Part of the window title, or the program name"}},"required":["window"]}""";

    protected override async Task<string> RunCoreAsync(JsonElement input, CancellationToken ct)
    {
        var query = ToolText.GetString(input, "window");
        if (string.IsNullOrWhiteSpace(query)) return "error: window is required";
        if (DesktopText.Find(Auto, query) is not { } w) return DesktopText.NoMatch(Auto, query);
        if (Refusal(Auto, w) is { } refusal) return refusal;
        if (!Auto.Close(w.Handle)) return $"error: could not ask {DesktopText.Label(w, Auto)} to close";
        await Delay(TimeSpan.FromMilliseconds(WaitMs));
        ct.ThrowIfCancellationRequested();

        if (!Auto.Windows().Any(x => x.Handle == w.Handle)) return $"closed \"{DesktopText.Clean(w.Title)}\"";
        // An unsaved-changes prompt is usually its own window of the same program, now in front; otherwise the window itself shows it.
        var front = Auto.Foreground();
        var target = front is not null && front.Handle != w.Handle && front.ProcessId == w.ProcessId ? front : w;
        var d = Auto.Describe(target.Handle, DesktopText.MaxStillOpenElements);
        var shows = d is null ? "nothing readable" : $"\"{DesktopText.Clean(d.Title)}\": " + DesktopText.Inline(d, DesktopText.MaxStillOpenElements);
        return $"\"{DesktopText.Clean(w.Title)}\" is still open; it shows: {shows}";
    }

    /// <summary>The desktop and taskbar are windows of explorer, and closing our own process would end the assistant.</summary>
    public static string? Refusal(IDesktopAutomation auto, WindowInfo w)
    {
        if (w.ProcessId == Environment.ProcessId) return "error: refusing to close the assistant's own window";
        if (string.Equals(auto.ProcessName(w.ProcessId), "explorer", StringComparison.OrdinalIgnoreCase))
            return $"error: refusing to close \"{DesktopText.Clean(w.Title)}\"; it belongs to explorer (the desktop and taskbar)";
        return null;
    }
}

public sealed class PressKeyTool(IDesktopAutomation auto, Func<TimeSpan, Task>? delay = null) : DesktopToolBase(auto, delay)
{
    public const int SettleMs = 500;
    public override string Name => "press_key";
    public override string Description =>
        "Presses one key in the window in front. Allowed keys: " + string.Join(", ", DesktopText.Keys) + ". Escape cancels a dialog; Enter confirms whatever button is highlighted, so use it only when the user clearly said yes." + Scope;
    public override string InputSchema => """{"type":"object","properties":{"key":{"type":"string","description":"escape, enter, tab, space, up, down, left, right or alt+f4"}},"required":["key"]}""";

    protected override async Task<string> RunCoreAsync(JsonElement input, CancellationToken ct)
    {
        var raw = ToolText.GetString(input, "key");
        if (string.IsNullOrWhiteSpace(raw)) return "error: key is required";
        if (DesktopText.NormalizeKey(raw) is not { } key)
            return $"error: key \"{DesktopText.Clean(raw)}\" is not allowed; allowed: {string.Join(", ", DesktopText.Keys)}";
        // Alt+F4 on the desktop opens the shutdown dialog, and on our own window it would end the assistant.
        if (key == "alt+f4" && Auto.Foreground() is { } f && CloseWindowTool.Refusal(Auto, f) is { } refusal)
            return refusal.Replace("close", "send alt+f4 to");
        if (Auto.PressKey(key) is { } problem) return $"error: {problem}";
        await Delay(TimeSpan.FromMilliseconds(SettleMs));
        ct.ThrowIfCancellationRequested();
        return $"pressed {key}; {DesktopText.NowInFront(Auto)}";
    }
}
