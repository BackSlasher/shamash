using System.Text.Json;
using Xunit;

namespace Shamash.Pc.Tests;

public class DesktopToolTests
{
    private sealed class FakeDesktop : IDesktopAutomation
    {
        public List<WindowInfo> All = [];
        public Dictionary<int, string> Processes = [];
        public HashSet<nint> Minimized = [];
        public Dictionary<nint, WindowDescription> Described = [];
        public nint Front;
        public string? ClickProblem;
        public readonly List<(nint, string, string?)> Clicks = [];
        public readonly List<nint> Closed = [];
        public readonly List<string> Keys = [];
        public Action<nint>? OnClose;
        public Action<string, string?>? OnClick;

        public IReadOnlyList<WindowInfo> Windows() => All;
        public WindowInfo? Foreground() => All.FirstOrDefault(w => w.Handle == Front);
        public string ProcessName(int processId) => Processes.GetValueOrDefault(processId, $"pid {processId}");
        public bool IsMinimized(nint hwnd) => Minimized.Contains(hwnd);
        public WindowDescription? Describe(nint hwnd, int maxElements) =>
            Described.TryGetValue(hwnd, out var d) ? d with { Elements = d.Elements.Take(maxElements).ToList(), Truncated = d.Truncated || d.Elements.Count > maxElements } : null;
        public string? Click(nint hwnd, string name, string? kind) { Clicks.Add((hwnd, name, kind)); OnClick?.Invoke(name, kind); return ClickProblem; }
        public bool Close(nint hwnd) { Closed.Add(hwnd); OnClose?.Invoke(hwnd); return true; }
        public string? BringToFront(nint hwnd) { Front = hwnd; return "fake"; }
        public string? PressKey(string key) { Keys.Add(key); return null; }
    }

    private static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
    private static Task NoDelay(TimeSpan _) => Task.CompletedTask;

    private static UiElement Btn(string name, bool enabled = true) => new("button", name, enabled, null, false, null);

    /// <summary>Notepad in front of Word, a minimized Chrome, and the desktop shell process explorer.</summary>
    private static FakeDesktop Desk()
    {
        var d = new FakeDesktop
        {
            All =
            [
                new WindowInfo(1, "ללא שם - פנקס רשימות", 10),
                new WindowInfo(2, "מכתב לעירייה - Word", 20),
                new WindowInfo(3, "Inbox - Chrome", 30),
                new WindowInfo(4, "Documents", 40),
            ],
            Processes = new() { [10] = "notepad", [20] = "WINWORD", [30] = "chrome", [40] = "explorer" },
            Front = 1,
        };
        d.Minimized.Add(3);
        d.Described[1] = new("ללא שם - פנקס רשימות", "notepad", [new("text", "שלום", true, null, false, null), new("text", "עולם", true, null, false, null), Btn("שמור"), Btn("בטל")], false);
        d.Described[2] = new("מכתב לעירייה - Word", "WINWORD", [Btn("Save"), Btn("Save As"), Btn("Cancel")], false);
        return d;
    }

    [Fact]
    public async Task ListWindows_FrontFirst_WithFrontAndMinimizedMarks()
    {
        var text = await new ListWindowsTool(Desk()).RunAsync(J("{}"), default);
        var lines = text.Split('\n');
        Assert.Equal("1. \"ללא שם - פנקס רשימות\" (notepad) [front]", lines[0]);
        Assert.Equal("2. \"מכתב לעירייה - Word\" (WINWORD)", lines[1]);
        Assert.Equal("3. \"Inbox - Chrome\" (chrome) [minimized]", lines[2]);
    }

    [Fact]
    public async Task ListWindows_CapsAtFifteen_AndSaysHowManyMore()
    {
        var d = new FakeDesktop { All = Enumerable.Range(1, 20).Select(i => new WindowInfo(i, $"w{i}", 5)).ToList() };
        var text = await new ListWindowsTool(d).RunAsync(J("{}"), default);
        Assert.Equal(16, text.Split('\n').Length);
        Assert.EndsWith("... and 5 more", text);
        Assert.Equal("no visible windows", await new ListWindowsTool(new FakeDesktop()).RunAsync(J("{}"), default));
    }

    [Fact]
    public async Task Describe_DefaultsToForeground_JoinsTextRuns()
    {
        var text = await new DescribeWindowTool(Desk()).RunAsync(J("{}"), default);
        Assert.Equal("window \"ללא שם - פנקס רשימות\" (notepad)\n- text: שלום עולם\n- button: שמור\n- button: בטל", text);
    }

    [Fact]
    public async Task Describe_ShowsStates()
    {
        var d = Desk();
        d.Described[1] = new("t", "notepad",
        [
            new("checkbox", "זכור אותי", true, true, false, null),
            new("checkbox", "Other", false, false, false, null),
            new("listitem", "Row", true, null, true, null),
            new("edit", "Name", true, null, false, "דוד"),
        ], false);
        var lines = (await new DescribeWindowTool(d).RunAsync(J("{}"), default)).Split('\n');
        Assert.Equal("- checkbox: זכור אותי [checked]", lines[1]);
        Assert.Equal("- checkbox: Other [disabled] [unchecked]", lines[2]);
        Assert.Equal("- listitem: Row [selected]", lines[3]);
        Assert.Equal("- edit: Name (value: דוד)", lines[4]);
    }

    [Fact]
    public async Task Describe_CapsElementsAndSaysMore()
    {
        var d = Desk();
        d.Described[1] = new("big", "notepad", Enumerable.Range(1, 100).Select(i => Btn($"b{i}")).ToList(), false);
        var lines = (await new DescribeWindowTool(d).RunAsync(J("{}"), default)).Split('\n');
        Assert.Equal(1 + 60 + 1, lines.Length);
        Assert.Equal("... (more)", lines[^1]);
    }

    [Fact]
    public async Task Describe_CapsCharacters()
    {
        var d = Desk();
        d.Described[1] = new("big", "notepad", Enumerable.Range(1, 40).Select(i => Btn(new string('x', 100) + i)).ToList(), false);
        var text = await new DescribeWindowTool(d).RunAsync(J("{}"), default);
        Assert.True(text.Length < DesktopText.MaxDescribeChars + 200);
        Assert.EndsWith("... (more)", text);
    }

    [Theory]
    [InlineData("Word", "מכתב לעירייה - Word")]          // title substring
    [InlineData("מכתב", "מכתב לעירייה - Word")]           // Hebrew title substring
    [InlineData("winword", "מכתב לעירייה - Word")]        // process name, case-insensitive
    [InlineData("notepad.exe", "ללא שם - פנקס רשימות")]    // .exe ignored
    public async Task Describe_MatchesTitleSubstringOrProcess(string query, string title)
    {
        var text = await new DescribeWindowTool(Desk()).RunAsync(J(JsonSerializer.Serialize(new { window = query })), default);
        Assert.StartsWith($"window \"{title}\"", text);
    }

    [Fact]
    public async Task Describe_Miss_ListsVisibleWindows()
    {
        var text = await new DescribeWindowTool(Desk()).RunAsync(J("""{"window":"photoshop"}"""), default);
        Assert.StartsWith("error: no window matches \"photoshop\"; visible windows: ", text);
        Assert.Contains("\"מכתב לעירייה - Word\" (WINWORD)", text);
    }

    [Fact]
    public async Task Click_ExactBeatsContains_BringsToFront_ReportsFront()
    {
        var d = Desk();
        var text = await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"window":"Word","name":"Save"}"""), default);
        Assert.Equal((2, "Save", "button"), Assert.Single(d.Clicks));
        Assert.Equal("clicked button \"Save\"; now in front: \"מכתב לעירייה - Word\" (WINWORD)", text);
    }

    [Fact]
    public async Task Click_ContainsIsCaseInsensitive_AndHebrew()
    {
        var d = Desk();
        var text = await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"name":"שמ"}"""), default);
        Assert.StartsWith("clicked button \"שמור\"", text);
        d.Described[2] = new("t", "WINWORD", [Btn("Cancel")], false);
        Assert.StartsWith("clicked button \"Cancel\"", await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"window":"word","name":"cANC"}"""), default));
    }

    [Fact]
    public async Task Click_AmbiguousExact_IsAnError_AndKindResolvesIt()
    {
        var d = Desk();
        d.Described[1] = new("t", "notepad", [Btn("OK"), new("link", "OK", true, null, false, null), new("button", "OK", true, null, false, null)], false);
        var tool = new ClickControlTool(d, null, NoDelay);
        Assert.Equal("error: 3 controls named \"OK\"; be more specific (kinds: button, link)", await tool.RunAsync(J("""{"name":"OK"}"""), default));
        Assert.Empty(d.Clicks);
        Assert.StartsWith("error: 2 controls", await tool.RunAsync(J("""{"name":"OK","kind":"button"}"""), default));
        Assert.StartsWith("clicked link", await tool.RunAsync(J("""{"name":"OK","kind":"link"}"""), default));
    }

    [Fact]
    public async Task Click_AmbiguousContains_IsAnError()
    {
        var d = Desk();
        var text = await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"window":"Word","name":"Sav"}"""), default);
        Assert.StartsWith("error: 2 controls named \"Sav\"", text);
        Assert.Empty(d.Clicks);
    }

    [Fact]
    public async Task Click_NoMatchDisabledAndMissingWindow()
    {
        var d = Desk();
        d.Described[2] = new("t", "WINWORD", [Btn("Print", enabled: false)], false);
        var tool = new ClickControlTool(d, null, NoDelay);
        Assert.StartsWith("error: no control named \"Zzz\" in", await tool.RunAsync(J("""{"window":"Word","name":"Zzz"}"""), default));
        Assert.Equal("error: the button \"Print\" is disabled", await tool.RunAsync(J("""{"window":"Word","name":"Print"}"""), default));
        Assert.StartsWith("error: no window matches", await tool.RunAsync(J("""{"window":"nope","name":"x"}"""), default));
        Assert.Equal("error: name is required", await tool.RunAsync(J("{}"), default));
        Assert.Empty(d.Clicks);
    }

    [Fact]
    public async Task Click_StaticTextIsNotClickable()
    {
        var d = Desk();
        var text = await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"name":"שלום"}"""), default);
        Assert.StartsWith("error: no control named", text);
    }

    [Fact]
    public async Task Click_ClickProblemIsReturned()
    {
        var d = Desk();
        d.ClickProblem = "control gone";
        Assert.Equal("error: control gone", await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"name":"שמור"}"""), default));
    }

    [Fact]
    public async Task Click_DialogAppears_IsDescribed()
    {
        var d = Desk();
        d.OnClick = (_, _) =>
        {
            d.All.Insert(0, new WindowInfo(9, "Save changes?", 10));
            d.Front = 9;
        };
        d.Described[9] = new("Save changes?", "notepad", [new("text", "Do you want to save?", true, null, false, null), Btn("Save"), Btn("Don't save"), Btn("Cancel")], false);
        var text = await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"name":"בטל"}"""), default);
        Assert.Equal("clicked button \"בטל\"; now in front: \"Save changes?\" (notepad); a dialog appeared: \"Save changes?\": text: Do you want to save?; button: Save; button: Don't save; button: Cancel", text);
    }

    [Fact]
    public async Task Click_OfficeDialog_UsesTheOfficeProbe()
    {
        var d = Desk();
        d.OnClick = (_, _) => { d.All.Insert(0, new WindowInfo(9, "Microsoft Word", 20)); d.Front = 9; };
        IReadOnlyList<DialogInfo> probe() => [new DialogInfo("Word", "Microsoft Word", ["Save changes?"], ["Save", "Cancel"])];
        var text = await new ClickControlTool(d, probe, NoDelay).RunAsync(J("""{"window":"Word","name":"Save"}"""), default);
        Assert.Contains("a dialog appeared: Word is showing a dialog \"Microsoft Word\": Save changes? [buttons: Save, Cancel]", text);
    }

    [Fact]
    public async Task Click_WindowClosedByTheClick_IsSaid()
    {
        var d = Desk();
        d.OnClick = (_, _) => { d.All.RemoveAll(w => w.Handle == 1); d.Front = 2; };
        var text = await new ClickControlTool(d, null, NoDelay).RunAsync(J("""{"name":"בטל"}"""), default);
        Assert.EndsWith("; the window closed", text);
    }

    [Fact]
    public async Task Close_ClosedWindow_IsReported()
    {
        var d = Desk();
        d.OnClose = h => d.All.RemoveAll(w => w.Handle == h);
        var text = await new CloseWindowTool(d, NoDelay).RunAsync(J("""{"window":"chrome"}"""), default);
        Assert.Equal("closed \"Inbox - Chrome\"", text);
        Assert.Equal([(nint)3], d.Closed);
    }

    [Fact]
    public async Task Close_StillOpen_ShowsThePrompt()
    {
        var d = Desk();
        d.OnClose = _ =>
        {
            d.All.Insert(0, new WindowInfo(9, "Save changes?", 10));
            d.Front = 9;
            d.Described[9] = new("Save changes?", "notepad", [Btn("Save"), Btn("Don't save"), Btn("Cancel")], false);
        };
        var text = await new CloseWindowTool(d, NoDelay).RunAsync(J("""{"window":"פנקס"}"""), default);
        Assert.Equal("\"ללא שם - פנקס רשימות\" is still open; it shows: \"Save changes?\": button: Save; button: Don't save; button: Cancel", text);
    }

    [Fact]
    public async Task Close_RefusesExplorer_AndOwnProcess()
    {
        var d = Desk();
        var tool = new CloseWindowTool(d, NoDelay);
        Assert.StartsWith("error: refusing to close \"Documents\"", await tool.RunAsync(J("""{"window":"explorer"}"""), default));
        d.All.Add(new WindowInfo(7, "Shamash status", Environment.ProcessId));
        Assert.StartsWith("error: refusing to close the assistant", await tool.RunAsync(J("""{"window":"Shamash status"}"""), default));
        Assert.Empty(d.Closed);
        Assert.Equal("error: window is required", await tool.RunAsync(J("{}"), default));
    }

    [Theory]
    [InlineData("escape", "escape")]
    [InlineData("Esc", "escape")]
    [InlineData("Return", "enter")]
    [InlineData("ALT + F4", "alt+f4")]
    [InlineData("Right", "right")]
    public async Task PressKey_AllowsTheWhitelist(string key, string sent)
    {
        var d = Desk();
        var text = await new PressKeyTool(d, NoDelay).RunAsync(J(JsonSerializer.Serialize(new { key })), default);
        Assert.Equal([sent], d.Keys);
        Assert.Equal($"pressed {sent}; now in front: \"ללא שם - פנקס רשימות\" (notepad)", text);
    }

    [Theory]
    [InlineData("ctrl+c")]
    [InlineData("a")]
    [InlineData("delete")]
    [InlineData("win")]
    [InlineData("f4")]
    public async Task PressKey_RefusesEverythingElse(string key)
    {
        var d = Desk();
        var text = await new PressKeyTool(d, NoDelay).RunAsync(J(JsonSerializer.Serialize(new { key })), default);
        Assert.StartsWith("error: key", text);
        Assert.Empty(d.Keys);
    }

    [Fact]
    public async Task PressKey_AltF4_NotOnTheDesktopOrOurselves()
    {
        var d = Desk();
        d.Front = 4; // Documents, explorer
        var text = await new PressKeyTool(d, NoDelay).RunAsync(J("""{"key":"alt+f4"}"""), default);
        Assert.StartsWith("error: refusing", text);
        Assert.Empty(d.Keys);
    }

    [Fact]
    public void Schemas_AreValidJson_AndNamesAreTheDocumentedOnes()
    {
        var d = Desk();
        IAgentTool[] tools = [new ListWindowsTool(d), new DescribeWindowTool(d), new ClickControlTool(d), new CloseWindowTool(d), new PressKeyTool(d)];
        Assert.Equal(["list_windows", "describe_window", "click_control", "close_window", "press_key"], tools.Select(t => t.Name));
        foreach (var t in tools)
        {
            using var doc = JsonDocument.Parse(t.InputSchema);
            Assert.Equal("object", doc.RootElement.GetProperty("type").GetString());
            Assert.Contains("unexpected situations", t.Description);
        }
    }

    [Fact]
    public void BuildTools_WithoutDesktop_OmitsScreenAndWindowTools_WithDesktopAddsThem()
    {
        var s = ShamashSettings.Load(_ => null, []);
        var without = TurnPipeline.BuildTools(s).Select(t => t.Name).ToList();
        Assert.Contains("report_missing_feature", without);
        Assert.DoesNotContain("look_at_screen", without);
        Assert.DoesNotContain("click_control", without);
        using var dir = new TempDir();
        var with = TurnPipeline.BuildTools(s, new DesktopServices(new FakeScreen(null), Desk(), dir.Path)).Select(t => t.Name).ToList();
        Assert.All(new[] { "look_at_screen", "list_windows", "describe_window", "click_control", "close_window", "press_key" }, n => Assert.Contains(n, with));
    }

    private sealed class FakeScreen(ToolImage? image) : IScreenCapture
    {
        public ToolImage? Capture() => image;
    }

    [Fact]
    public async Task LookAtScreen_ReturnsImageAndWindowText_AndSavesTheFile()
    {
        using var dir = new TempDir();
        var image = new ToolImage([1, 2, 3], "image/png", 1280, 720);
        var tool = new LookAtScreenTool(new FakeScreen(image), Desk(), dir.Path, null, () => new DateTime(2026, 11, 28, 9, 5, 7));
        var r = await tool.RunRichAsync(J("{}"), default);
        Assert.Same(image, r.Image);
        Assert.Equal("screenshot 1280x720 of the whole screen taken at 09:05; foreground window: \"ללא שם - פנקס רשימות\" (notepad); " +
            "other visible windows: \"מכתב לעירייה - Word\" (WINWORD), \"Inbox - Chrome\" (chrome), \"Documents\" (explorer)", r.Text);
        Assert.Equal([1, 2, 3], await File.ReadAllBytesAsync(Path.Combine(dir.Path, "pclink", "screens", "20261128-090507.png")));
        Assert.Equal(r.Text, await tool.RunAsync(J("{}"), default));
    }

    [Fact]
    public async Task LookAtScreen_KeepsTheLastFiftyScreens()
    {
        using var dir = new TempDir();
        var screens = Path.Combine(dir.Path, "pclink", "screens");
        Directory.CreateDirectory(screens);
        for (var i = 0; i < 60; i++) File.WriteAllBytes(Path.Combine(screens, $"20260101-0000{i:00}.png"), [0]);
        var tool = new LookAtScreenTool(new FakeScreen(new ToolImage([1], "image/png", 1, 1)), Desk(), dir.Path, null, () => new DateTime(2026, 11, 28, 9, 5, 7));
        await tool.RunRichAsync(J("{}"), default);
        var files = Directory.GetFiles(screens).Select(Path.GetFileName).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(LookAtScreenTool.KeepScreens, files.Count);
        Assert.Contains("20261128-090507.png", files);
        Assert.DoesNotContain("20260101-000000.png", files);
    }

    [Fact]
    public async Task LookAtScreen_NoCapture_IsAnError()
    {
        using var dir = new TempDir();
        var r = await new LookAtScreenTool(new FakeScreen(null), Desk(), dir.Path).RunRichAsync(J("{}"), default);
        Assert.Equal("error: screen capture not available", r.Text);
        Assert.Null(r.Image);
    }

    [Fact]
    public async Task ReportMissingFeature_AppendsAJsonLine_AndReturnsTheRecordedText()
    {
        using var dir = new TempDir();
        var tool = new ReportMissingFeatureTool(Path.Combine(dir.Path, "data"));
        var first = await tool.RunAsync(J("""{"request":"תזמן לי תזכורת למחר","details":"no calendar tool"}"""), default);
        var second = await tool.RunAsync(J("""{"request":"שלח וואטסאפ"}"""), default);
        Assert.Equal(ReportMissingFeatureTool.Recorded, first);
        Assert.Equal(ReportMissingFeatureTool.Recorded, second);
        var lines = await File.ReadAllLinesAsync(Path.Combine(dir.Path, "data", "missing-features.jsonl"));
        Assert.Equal(2, lines.Length);
        using var doc = JsonDocument.Parse(lines[0]);
        Assert.Equal("תזמן לי תזכורת למחר", doc.RootElement.GetProperty("request").GetString());
        Assert.Equal("no calendar tool", doc.RootElement.GetProperty("details").GetString());
        Assert.True(DateTimeOffset.TryParse(doc.RootElement.GetProperty("time").GetString(), out _));
        Assert.Contains("תזמן לי", lines[0]); // Hebrew stays readable in the file
        using var doc2 = JsonDocument.Parse(lines[1]);
        Assert.Equal(JsonValueKind.Null, doc2.RootElement.GetProperty("details").ValueKind);
    }

    [Fact]
    public async Task ReportMissingFeature_RequiresRequest_AndReportsWriteErrors()
    {
        using var dir = new TempDir();
        Assert.Equal("error: request is required", await new ReportMissingFeatureTool(dir.Path).RunAsync(J("{}"), default));
        var blocker = Path.Combine(dir.Path, "file");
        File.WriteAllText(blocker, "x");
        var text = await new ReportMissingFeatureTool(Path.Combine(blocker, "sub")).RunAsync(J("""{"request":"x"}"""), default);
        Assert.StartsWith("error:", text);
    }

    [Fact]
    public void SystemPrompt_MentionsTheEscapeHatches()
    {
        Assert.Contains("report_missing_feature", ClaudeReplyModel.SystemPrompt);
        Assert.Contains("look_at_screen", ClaudeReplyModel.SystemPrompt);
        Assert.Contains("close_window", ClaudeReplyModel.SystemPrompt);
        Assert.DoesNotContain("אף כלי עדיין לא יודע", ClaudeReplyModel.SystemPrompt);
    }
}
