using System.Runtime.Versioning;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>
/// <see cref="IDesktopAutomation"/> over Win32 (window list, close, foreground) and UI Automation through FlaUI
/// (reading and clicking controls). Built, not yet verified on a Windows machine. Never types text and never starts programs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UiaDesktopAutomation(ILogger? log = null) : IDesktopAutomation
{
    private static readonly string[] ShellClasses = ["Progman", "WorkerW", "Windows.UI.Core.CoreWindow", "Shell_TrayWnd", "Shell_SecondaryTrayWnd"];
    private const int MaxVisited = 800;
    private static readonly TimeSpan WalkBudget = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan PatternWait = TimeSpan.FromMilliseconds(1500);

    private readonly object _lock = new();
    private UIA3Automation? _automation;

    private UIA3Automation Automation { get { lock (_lock) return _automation ??= new UIA3Automation(); } }

    public IReadOnlyList<WindowInfo> Windows() =>
        WindowNative.VisibleWindows()
            .Where(w => w.ProcessId != Environment.ProcessId
                && !string.IsNullOrWhiteSpace(w.Title)
                && !ShellClasses.Contains(WindowNative.ClassName(w.Handle))
                && !WindowNative.IsCloaked(w.Handle))
            .ToList();

    public WindowInfo? Foreground()
    {
        var fg = WindowNative.ForegroundWindow();
        return fg == 0 ? null : Windows().FirstOrDefault(w => w.Handle == fg);
    }

    public string ProcessName(int processId) => WindowNative.ProcessName(processId);
    public bool IsMinimized(nint hwnd) => WindowNative.IsMinimized(hwnd);
    public bool Close(nint hwnd) => WindowNative.RequestClose(hwnd);
    public string? BringToFront(nint hwnd) => WindowNative.BringToFront(hwnd);

    public WindowDescription? Describe(nint hwnd, int maxElements)
    {
        try
        {
            var root = Automation.FromHandle(hwnd);
            var title = Safe(() => root.Properties.Name.ValueOrDefault) ?? "";
            var process = WindowNative.ProcessName(Safe(() => root.Properties.ProcessId.ValueOrDefault));
            var elements = new List<UiElement>();
            var walker = Automation.TreeWalkerFactory.GetControlViewWalker();
            var state = new Walk(maxElements, DateTime.UtcNow + WalkBudget);
            Visit(walker, root, elements, state);
            return new WindowDescription(title, process, elements, state.Truncated);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.LogWarning(e, "uia: describe failed");
            return null;
        }
    }

    public string? Click(nint hwnd, string name, string? kind)
    {
        try
        {
            var root = Automation.FromHandle(hwnd);
            var found = Find(root, name, kind);
            if (found is null) return $"control \"{name}\" not found";
            if (TryPattern(found, kind ?? KindOf(found))) return null;

            // No usable pattern: focus the window and click the middle of the control like a finger would.
            var rect = found.Properties.BoundingRectangle.ValueOrDefault;
            if (rect.Width <= 0 || rect.Height <= 0) return $"control \"{name}\" has no clickable area";
            WindowNative.BringToFront(hwnd);
            Mouse.Click(new System.Drawing.Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            log?.LogWarning(e, "uia: click failed");
            return $"could not click \"{name}\": {e.Message}";
        }
    }

    public string? PressKey(string key)
    {
        try
        {
            switch (key)
            {
                case "escape": Keyboard.Type(VirtualKeyShort.ESCAPE); break;
                case "enter": Keyboard.Type(VirtualKeyShort.ENTER); break;
                case "tab": Keyboard.Type(VirtualKeyShort.TAB); break;
                case "space": Keyboard.Type(VirtualKeyShort.SPACE); break;
                case "up": Keyboard.Type(VirtualKeyShort.UP); break;
                case "down": Keyboard.Type(VirtualKeyShort.DOWN); break;
                case "left": Keyboard.Type(VirtualKeyShort.LEFT); break;
                case "right": Keyboard.Type(VirtualKeyShort.RIGHT); break;
                case "alt+f4": Keyboard.TypeSimultaneously(VirtualKeyShort.ALT, VirtualKeyShort.F4); break;
                default: return $"key {key} is not supported";
            }
            return null;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return $"could not press {key}: {e.Message}";
        }
    }

    private sealed class Walk(int max, DateTime deadline)
    {
        public int Visited;
        public bool Truncated;
        public bool Done(int count)
        {
            if (count >= max || Visited >= MaxVisited || DateTime.UtcNow > deadline) { Truncated = true; return true; }
            return false;
        }
    }

    private static T? Safe<T>(Func<T> f)
    {
        try { return f(); } catch (Exception e) when (e is not OperationCanceledException) { return default; }
    }

    /// <summary>The Core vocabulary for a UIA control type; null for containers that are only walked through.</summary>
    private static string? KindOfType(ControlType t) => t switch
    {
        ControlType.Button or ControlType.SplitButton => "button",
        ControlType.CheckBox => "checkbox",
        ControlType.RadioButton => "radio",
        ControlType.Hyperlink => "link",
        ControlType.ComboBox => "combobox",
        ControlType.Edit => "edit",
        ControlType.TabItem => "tab",
        ControlType.MenuItem => "menuitem",
        ControlType.ListItem or ControlType.TreeItem or ControlType.DataItem => "listitem",
        ControlType.Text => "text",
        _ => null,
    };

    private static string KindOf(AutomationElement e) => KindOfType(e.Properties.ControlType.ValueOrDefault) ?? "button";

    /// <summary>Depth-first in tree order, which UIA keeps close to reading order. Controls are leaves: their inner text would only repeat the name.</summary>
    private static void Visit(ITreeWalker walker, AutomationElement el, List<UiElement> into, Walk state)
    {
        for (var child = walker.GetFirstChild(el); child is not null; child = walker.GetNextSibling(child))
        {
            state.Visited++;
            if (state.Done(into.Count)) return;
            if (Safe(() => child.Properties.IsOffscreen.ValueOrDefault)) continue;
            var kind = KindOfType(Safe(() => child.Properties.ControlType.ValueOrDefault));
            if (kind is null) { Visit(walker, child, into, state); continue; }
            var name = Safe(() => child.Properties.Name.ValueOrDefault) ?? "";
            if (kind == "text") { if (name.Trim().Length > 0) into.Add(new UiElement(kind, name, true, null, false, null)); continue; }
            into.Add(ToElement(child, kind, name));
        }
    }

    private static UiElement ToElement(AutomationElement el, string kind, string name)
    {
        var enabled = Safe(() => el.Properties.IsEnabled.ValueOrDefault);
        bool? toggled = null;
        if (kind == "checkbox" && Safe(() => el.Patterns.Toggle.PatternOrDefault) is { } toggle)
            toggled = Safe(() => toggle.ToggleState.ValueOrDefault) == ToggleState.On;
        var selected = Safe(() => el.Patterns.SelectionItem.PatternOrDefault is { } si && si.IsSelected.ValueOrDefault);
        if (kind == "radio" && toggled is null) toggled = selected;
        string? value = null;
        // Passwords are never read out.
        if (kind is "edit" or "combobox" && !Safe(() => el.Properties.IsPassword.ValueOrDefault))
            value = Safe(() => el.Patterns.Value.PatternOrDefault?.Value.ValueOrDefault);
        return new UiElement(kind, name, enabled, toggled, selected, string.IsNullOrEmpty(value) ? null : value);
    }

    /// <summary>First control with exactly this name (and kind), in the same order Describe lists them.</summary>
    private AutomationElement? Find(AutomationElement root, string name, string? kind)
    {
        var walker = Automation.TreeWalkerFactory.GetControlViewWalker();
        var state = new Walk(int.MaxValue, DateTime.UtcNow + WalkBudget);
        return FindIn(walker, root, name, kind, state);
    }

    private static AutomationElement? FindIn(ITreeWalker walker, AutomationElement el, string name, string? kind, Walk state)
    {
        for (var child = walker.GetFirstChild(el); child is not null; child = walker.GetNextSibling(child))
        {
            if (state.Done(0)) return null;
            state.Visited++;
            if (Safe(() => child.Properties.IsOffscreen.ValueOrDefault)) continue;
            var k = KindOfType(Safe(() => child.Properties.ControlType.ValueOrDefault));
            if (k is null) { if (FindIn(walker, child, name, kind, state) is { } deep) return deep; continue; }
            if (k == "text" || (kind is not null && k != kind)) continue;
            if (string.Equals(Safe(() => child.Properties.Name.ValueOrDefault), name, StringComparison.Ordinal)) return child;
        }
        return null;
    }

    /// <summary>
    /// Toggle, SelectionItem, ExpandCollapse or Invoke, whichever the control offers for its kind. The call runs on its own
    /// task and is given a moment: Invoke on a button that opens a modal dialog does not return until the dialog closes.
    /// </summary>
    private static bool TryPattern(AutomationElement el, string kind)
    {
        Action? act = null;
        if (kind == "checkbox" && el.Patterns.Toggle.PatternOrDefault is { } toggle) act = toggle.Toggle;
        else if (kind is "radio" or "tab" or "listitem" && el.Patterns.SelectionItem.PatternOrDefault is { } sel) act = sel.Select;
        else if (kind == "combobox" && el.Patterns.ExpandCollapse.PatternOrDefault is { } ec) act = ec.Expand;
        else if (el.Patterns.Invoke.PatternOrDefault is { } inv) act = inv.Invoke;
        else if (el.Patterns.SelectionItem.PatternOrDefault is { } sel2) act = sel2.Select;
        else if (el.Patterns.Toggle.PatternOrDefault is { } toggle2) act = toggle2.Toggle;
        else if (el.Patterns.ExpandCollapse.PatternOrDefault is { } ec2) act = ec2.Expand;
        if (act is null) return false;
        var task = Task.Run(act);
        try { task.Wait(PatternWait); }
        catch (AggregateException) { return false; }
        return !task.IsCompleted || task.IsCompletedSuccessfully;
    }
}
