using System.Globalization;
using System.Runtime.Versioning;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>An AutoRecover file (<c>*.asd</c>) and when Word last wrote it.</summary>
public sealed record AutosaveFile(string Path, DateTime Modified);

/// <summary>What <see cref="WordRecovery.Detect"/> found. PaneShown is null when Word's windows could not be inspected (no UI Automation, no Word window).</summary>
public sealed record RecoveryState(bool? PaneShown, IReadOnlyList<AutosaveFile> Files);

/// <summary>The Word-COM half of the recovery tool, a seam so the decisions are testable without Word.</summary>
public interface IWordRecoveryHost
{
    /// <summary>Running is false when no Word is running. Dir is <c>Options.DefaultFilePath(wdAutoRecoverPath)</c>.</summary>
    (bool Running, string? Dir) Probe();
    /// <summary>Opens the autosaved file read-only as a new document and returns its name.</summary>
    string OpenAutosaved(string path);
    /// <summary>CommandBars.ExecuteMso(idMso); false when Word refuses (no such command).</summary>
    bool TryExecuteMso(string idMso);
}

/// <summary>Detection and decisions for Word's Document Recovery pane. Pure apart from the file listing and the automation seam.</summary>
public static class WordRecovery
{
    public const int WdAutoRecoverPath = 5;
    public const string CloseMso = "DocumentRecoveryClose";
    public const string PaneShownNote = "recovery pane shown";
    public const int DescribeElements = 400;
    // Word 2016 en-US names the pane by its intro text (no element is titled "Document Recovery"; the list items are plain file names), seen on the VM 2026-11-28.
    public static readonly string[] PaneNames = ["Document Recovery", "שחזור מסמכים", "Word has recovered the following files"];
    public static readonly string[] CloseNames = ["Close", "סגור"];
    private static readonly string[] ListMarkers = ["[Original]", "[Recovered]", "[מקורי]", "[משוחזר]"];

    private static string Clean(string? s) => DesktopText.Clean(s, dropAmpersand: true);

    /// <summary>The pane itself is a container UI Automation walks through, so it is recognised by its title, or by the "[Original]" / "[Recovered]" entries of its list.</summary>
    public static bool IsPaneMarker(UiElement e)
    {
        var n = Clean(e.Name);
        if (n.Length == 0) return false;
        if (PaneNames.Any(p => n.StartsWith(p, StringComparison.OrdinalIgnoreCase))) return true;
        return e.Kind == "listitem" && ListMarkers.Any(m => n.Contains(m, StringComparison.OrdinalIgnoreCase));
    }

    public static bool PaneShown(WindowDescription? d) => d is not null && d.Elements.Any(IsPaneMarker);

    public static bool IsWordWindow(WindowInfo w, IDesktopAutomation auto) =>
        string.Equals(auto.ProcessName(w.ProcessId), "WINWORD", StringComparison.OrdinalIgnoreCase);

    /// <summary>The Word window that shows the pane and its description, or null. Null Describe results (window gone) are skipped.</summary>
    public static (WindowInfo Window, WindowDescription Description)? FindPane(IDesktopAutomation auto)
    {
        foreach (var w in auto.Windows().Where(w => IsWordWindow(w, auto)))
            if (auto.Describe(w.Handle, DescribeElements) is { } d && PaneShown(d)) return (w, d);
        return null;
    }

    /// <summary>True/false for whether the pane is up; null when there is no automation or no Word window to look at. Runs the UI Automation walk off the caller's (STA) thread and gives up after <paramref name="timeoutMs"/>.</summary>
    public static bool? PaneVisible(IDesktopAutomation? auto, int timeoutMs = 8000)
    {
        if (auto is null) return null;
        try
        {
            var task = Task.Run(() => auto.Windows().Any(w => IsWordWindow(w, auto)) ? (bool?)(FindPane(auto) is not null) : null);
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch (Exception e) when (e is AggregateException or InvalidOperationException) { return null; }
    }

    /// <summary>The probe the describe tools use; null when there is no UI Automation (they then say nothing).</summary>
    public static Func<bool>? PaneProbe(IDesktopAutomation? auto) => auto is null ? null : () => PaneVisible(auto) == true;

    /// <summary>"; recovery pane shown" when the probe says so; a failing probe adds nothing.</summary>
    public static string Note(Func<bool>? probe)
    {
        try { return probe is not null && probe() ? "; " + PaneShownNote : ""; }
        catch (Exception e) when (e is not OperationCanceledException) { return ""; }
    }

    // ---- the autosave files

    public static IReadOnlyList<AutosaveFile> ListAutosaved(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return [];
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
            return Directory.EnumerateFiles(dir, "*.asd", options)
                .Select(f => new AutosaveFile(f, File.GetLastWriteTime(f)))
                .OrderByDescending(f => f.Modified).ThenBy(f => f.Path, StringComparer.Ordinal)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    public static RecoveryState Detect(string? autoRecoverDir, IDesktopAutomation? auto, Func<string?, IReadOnlyList<AutosaveFile>>? list = null, int timeoutMs = 8000) =>
        new(PaneVisible(auto, timeoutMs), (list ?? ListAutosaved)(autoRecoverDir));

    public static AutosaveFile? Newest(IReadOnlyList<AutosaveFile> files) => files.OrderByDescending(f => f.Modified).FirstOrDefault();

    public static string DateText(DateTime d) => d.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    public static string Format(RecoveryState s)
    {
        var pane = s.PaneShown switch { true => "shown", false => "not shown", null => "unknown (Word's window could not be inspected)" };
        var files = Newest(s.Files) is { } n
            ? $"{s.Files.Count} (newest \"{System.IO.Path.GetFileName(n.Path)}\", {DateText(n.Modified)})"
            : "0";
        return $"recovery pane: {pane}; autosaved files: {files}";
    }

    // ---- closing

    /// <summary>
    /// Name of the Close button to click, or the reason not to click. Click finds the FIRST control of that name in the Word window, in the
    /// order of Describe, and Word's own title bar has a "Close" button that quits Word: so the first Close must come after the pane's title.
    /// </summary>
    public static (string? Name, string? Problem) CloseButton(WindowDescription d)
    {
        var marker = d.Elements.ToList().FindIndex(IsPaneMarker);
        if (marker < 0) return (null, "the recovery pane was not found in the window");
        var close = d.Elements.ToList().FindIndex(e => e.Kind == "button" && e.Enabled && CloseNames.Any(c => string.Equals(Clean(e.Name), c, StringComparison.OrdinalIgnoreCase)));
        if (close < 0) return (null, d.Truncated ? "no Close button was reached in the window (the list of controls was cut short)" : "no Close button was found");
        if (close < marker) return (null, "the first Close button belongs to Word's own window, not to the pane, so it was not clicked");
        return (Clean(d.Elements[close].Name), null);
    }

    /// <summary>Runs one action of the word_recovery tool.</summary>
    public static string Run(string action, IWordRecoveryHost host, IDesktopAutomation? auto, Action<int>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        var (running, dir) = host.Probe();
        if (!running) return "error: Word is not running";
        var state = Detect(dir, auto);
        switch (action)
        {
            case "check":
                return Format(state);
            case "open":
            {
                if (Newest(state.Files) is not { } newest) return $"error: no autosaved files found; {Format(state)}";
                var name = host.OpenAutosaved(newest.Path);
                return $"opened the autosaved file \"{System.IO.Path.GetFileName(newest.Path)}\" ({DateText(newest.Modified)}) as a new document \"{name}\"; it is a separate read-only document; the original file and the autosaved file were not changed";
            }
            default:
                return Close(state, dir, host, auto, sleep);
        }
    }

    private static string Close(RecoveryState state, string? dir, IWordRecoveryHost host, IDesktopAutomation? auto, Action<int> sleep)
    {
        if (state.PaneShown == false) return $"recovery pane: not shown; nothing to close; autosaved files: {state.Files.Count}";
        if (state.PaneShown is null || auto is null) return $"error: cannot inspect Word's windows, so the recovery pane cannot be closed from here; {Format(state)}";

        var notes = new List<string>();
        if (FindPane(auto) is { } pane)
        {
            var (name, problem) = CloseButton(pane.Description);
            if (name is null) notes.Add(problem!);
            else if (auto.Click(pane.Window.Handle, name, "button") is { } clickProblem) notes.Add(clickProblem);
            else if (Gone(auto, sleep)) return Closed("UI Automation", dir, state);
            else notes.Add("the pane was still shown after clicking Close");
        }
        try
        {
            if (host.TryExecuteMso(CloseMso) && Gone(auto, sleep)) return Closed("CommandBars", dir, state);
        }
        catch (Exception e) when (e is not OperationCanceledException) { notes.Add($"CommandBars: {e.GetType().Name}"); }
        return $"error: the recovery pane is still shown ({string.Join("; ", notes)}); ask the user to press the Close button of the pane on screen; autosaved files: {state.Files.Count}";
    }

    /// <summary>Re-detects up to three times, half a second apart, until the pane is gone.</summary>
    private static bool Gone(IDesktopAutomation auto, Action<int> sleep)
    {
        for (var i = 0; i < 3; i++)
        {
            sleep(500);
            if (PaneVisible(auto) == false) return true;
        }
        return false;
    }

    private static string Closed(string how, string? dir, RecoveryState before) =>
        $"recovery pane closed ({how}); verified: not shown; autosaved files left untouched: {before.Files.Count}";
}

[SupportedOSPlatform("windows")]
internal sealed class ComRecoveryHost : IWordRecoveryHost
{
    public (bool Running, string? Dir) Probe() => OfficeCom.RunSta(() =>
    {
        dynamic? app = ComInterop.GetActive("Word.Application");
        if (app is null) return (false, (string?)null);
        dynamic? options = null;
        try
        {
            options = app.Options;
            string? dir;
            try { dir = (string)options.DefaultFilePath(WordRecovery.WdAutoRecoverPath); }
            catch (Exception e) when (e is Microsoft.CSharp.RuntimeBinder.RuntimeBinderException or System.Runtime.InteropServices.COMException) { dir = (string)options.DefaultFilePath[WordRecovery.WdAutoRecoverPath]; }
            return (true, dir);
        }
        finally { ComInterop.Release(options, app); }
    });

    public string OpenAutosaved(string path) => OfficeCom.RunSta(() =>
    {
        dynamic? app = ComInterop.GetActive("Word.Application"), docs = null, doc = null;
        try
        {
            if (app is null) throw new InvalidOperationException("Word is not running");
            docs = app.Documents;
            doc = docs.Open(FileName: path, ConfirmConversions: false, ReadOnly: true, AddToRecentFiles: false);
            return (string)doc.Name;
        }
        finally
        {
            // One call per object and cast to object: a dynamic argument is bound at run time, and binding an RCW a tool released already (a document it found itself) throws.
            ComInterop.Release((object?)doc); ComInterop.Release((object?)docs); ComInterop.Release((object?)app);
        }
    });

    public bool TryExecuteMso(string idMso) => OfficeCom.RunSta(() =>
    {
        dynamic? app = ComInterop.GetActive("Word.Application"), bars = null;
        try
        {
            if (app is null) return false;
            bars = app.CommandBars;
            bars.ExecuteMso(idMso);
            return true;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or Microsoft.CSharp.RuntimeBinder.RuntimeBinderException) { return false; }
        finally { ComInterop.Release(bars, app); }
    });
}

public sealed class WordRecoveryTool(IDesktopAutomation? auto, IWordRecoveryHost? host = null) : IAgentTool
{
    public string Name => "word_recovery";
    public string Description =>
        "Word's Document Recovery pane (it appears after a Word crash and lists autosaved files). action check: whether the pane is shown and how many autosaved files exist (newest name and date). " +
        "action open: opens the newest autosaved file as a NEW document next to the original (nothing is overwritten; both stay open). action close: closes the pane and checks it is gone. " +
        "Autosaved files are never deleted. Ask the user before open or close.";
    public string InputSchema => """{"type":"object","properties":{"action":{"type":"string","enum":["check","open","close"]}},"required":["action"]}""";

    public static string? ParseAction(JsonElement input)
    {
        var a = ToolText.GetString(input, "action")?.Trim().ToLowerInvariant();
        return a is "check" or "open" or "close" ? a : null;
    }

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var action = ParseAction(input);
        if (action is null) return "error: action must be check, open or close";
        var h = host;
        if (h is null)
        {
            if (!OperatingSystem.IsWindows()) return OfficeCom.WindowsOnly;
            h = new ComRecoveryHost();
        }
        try { return WordRecovery.Run(action, h, auto); }
        catch (Exception e) when (e is not OperationCanceledException) { return OfficeCom.DescribeWithDialogs(e); }
    }, ct);
}
