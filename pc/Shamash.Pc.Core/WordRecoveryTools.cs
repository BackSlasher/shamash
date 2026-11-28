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
            var task = Task.Run(() => LookForPane(auto));
            return task.Wait(timeoutMs) ? task.Result : null;
        }
        catch (Exception e) when (e is AggregateException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// True when a Word window shows the pane, false when every Word window was read and none does, null when there is no Word window or one could not be read.
    /// A Word window that cannot be read is NOT "no pane": while Word's "keep the recovered files?" question (opened by the pane's Close button) is up, its main window
    /// answers nothing to UI Automation, and counting that as "pane gone" made <c>close</c> report success with the question still on screen (VM, 2026-11-28).
    /// </summary>
    private static bool? LookForPane(IDesktopAutomation auto)
    {
        var words = auto.Windows().Where(w => IsWordWindow(w, auto)).ToList();
        if (words.Count == 0) return null;
        var unreadable = false;
        foreach (var w in words)
        {
            if (auto.Describe(w.Handle, DescribeElements) is not { } d) { unreadable = true; continue; }
            if (PaneShown(d)) return true;
        }
        return unreadable ? null : false;
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

    /// <summary>
    /// Closing the pane while it still lists unopened files makes Word ask "would you like to view these files the next time you start Word?"
    /// with "No, remove the files" preselected (VM, 2026-11-28). The safe answer is the "Yes" option: the autosaved files stay. This finds that
    /// question in a window description: the "Yes" radio and the OK button to press, or null when the window is something else.
    /// </summary>
    public static (string Yes, string Ok)? KeepFilesAnswer(WindowDescription d)
    {
        var isQuestion = d.Elements.Any(e => e.Kind == "text" && KeepQuestionMarkers.Any(m => Clean(e.Name).Contains(m, StringComparison.OrdinalIgnoreCase)));
        if (!isQuestion) return null;
        var yes = d.Elements.FirstOrDefault(e => e.Kind == "radio" && YesPrefixes.Any(p => Clean(e.Name).StartsWith(p, StringComparison.OrdinalIgnoreCase)));
        var ok = d.Elements.FirstOrDefault(e => e.Kind == "button" && e.Enabled && OkNames.Any(o => string.Equals(Clean(e.Name), o, StringComparison.OrdinalIgnoreCase)));
        return yes is null || ok is null ? null : (Clean(yes.Name), Clean(ok.Name));
    }

    private static readonly string[] KeepQuestionMarkers = ["recovered files", "קבצים משוחזרים"];
    private static readonly string[] YesPrefixes = ["Yes", "כן"];
    private static readonly string[] OkNames = ["OK", "אישור"];

    /// <summary>
    /// The question's window, seen on the VM (2026-11-28): a separate top-level window of WINWORD titled just "Microsoft Word" (a document window
    /// is "name - Word") that UI Automation cannot read (ElementFromHandle times out). So it is recognised by the title, the owner and, when the
    /// Win32 child controls are readable, their text; answering goes through the keyboard: Alt+Y selects the "Yes, I want to view these files
    /// later" option by its mnemonic and Enter presses OK.
    /// </summary>
    public static bool LooksLikeKeepQuestion(WindowInfo? front, bool ownedByWord, IReadOnlyList<(string Class, string Text)> controls)
    {
        if (front is null || !ownedByWord) return false;
        var title = front.Title.Trim();
        if (!QuestionTitles.Any(t => string.Equals(title, t, StringComparison.OrdinalIgnoreCase))) return false;
        return controls.Count == 0 || controls.Any(c => KeepQuestionMarkers.Any(m => c.Text.Contains(m, StringComparison.OrdinalIgnoreCase)));
    }

    private static readonly string[] QuestionTitles = ["Microsoft Word", "Microsoft Office Word"];

    /// <summary>Answers Word's keep-the-files question with "Yes" when it is on screen; returns what was pressed, or null when no such question was found.</summary>
    private static string? AnswerKeepFiles(IDesktopAutomation auto, Action<int> sleep)
    {
        foreach (var w in auto.Windows().Where(w => IsWordWindow(w, auto)))
        {
            if (auto.Describe(w.Handle, 60) is not { } d || KeepFilesAnswer(d) is not { } answer) continue;
            if (auto.Click(w.Handle, answer.Yes, "radio") is { } p1) return $"could not choose \"{answer.Yes}\": {p1}";
            if (auto.Click(w.Handle, answer.Ok, "button") is { } p2) return $"chose \"{answer.Yes}\" but could not press {answer.Ok}: {p2}";
            return $"Word asked whether to keep the recovered files; answered \"{answer.Yes}\"";
        }
        // UI Automation could not read it (the usual case): the modal question is the foreground window of WINWORD.
        var front = auto.Foreground();
        var controls = front is not null && OperatingSystem.IsWindows() ? WindowNative.ChildControls(front.Handle) : [];
        if (!LooksLikeKeepQuestion(front, front is not null && IsWordWindow(front, auto), controls)) return null;
        if (auto.PressAltLetter('Y') is { } p3) return $"Word asked whether to keep the recovered files but Alt+Y failed: {p3}";
        sleep(300);
        if (auto.PressKey("enter") is { } p4) return $"Word asked whether to keep the recovered files; chose Yes but Enter failed: {p4}";
        return "Word asked whether to keep the recovered files; answered Yes (keyboard: Alt+Y, Enter)";
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
            else
            {
                sleep(700);
                var answered = AnswerKeepFiles(auto, sleep);
                if (answered is not null) notes.Add(answered);
                if (Gone(auto, sleep)) return Closed("UI Automation" + (answered is null ? "" : "; " + answered), dir, state);
                notes.Add("the pane was still shown after clicking Close");
            }
        }
        try
        {
            if (host.TryExecuteMso(CloseMso) && Gone(auto, sleep)) return Closed("CommandBars", dir, state);
        }
        catch (Exception e) when (e is not OperationCanceledException) { notes.Add($"CommandBars: {e.GetType().Name}"); }
        if (PaneVisible(auto) is null) notes.Add("Word's window cannot be read now, which usually means Word is asking whether to keep the recovered files; its default answer, \"No, remove the files\", would delete them, so nothing was pressed");
        return $"error: the recovery pane is still shown or unconfirmed ({string.Join("; ", notes)}); ask the user to press the Close button of the pane on screen; autosaved files: {state.Files.Count}";
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
