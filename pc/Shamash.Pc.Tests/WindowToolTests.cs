using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class WindowToolTests
{
    private static JsonElement Args(string path) => JsonDocument.Parse(JsonSerializer.Serialize(new { path })).RootElement.Clone();

    private static IAgentTool[] All(string root) =>
        [new ShowInExplorerTool([root]), new OpenFileTool([root]), new IsDocumentOpenTool([root]), new FocusDocumentTool([root])];

    [Fact]
    public void BuildTools_RegistersTheFourWindowTools()
    {
        var names = TurnPipeline.BuildTools(ShamashSettings.Load(_ => null, [])).Select(t => t.Name).ToList();
        Assert.All(new[] { "show_in_explorer", "open_file", "is_document_open", "focus_document" }, n => Assert.Contains(n, names));
    }

    [Fact]
    public async Task Guards_MissingOutsideAndNotFound()
    {
        using var d = new TempDir();
        using var other = new TempDir();
        File.WriteAllText(other.File("x.txt"), "x");
        File.WriteAllText(d.File("a/b.txt"), "b");
        foreach (var t in All(d.Path))
        {
            Assert.Equal("error: path is required", await t.RunAsync(JsonDocument.Parse("{}").RootElement, default));
            Assert.Equal("error: path not under a document root", await t.RunAsync(Args(Path.Combine(other.Path, "x.txt")), default));
            Assert.Equal("error: path not under a document root", await t.RunAsync(Args("../x.txt"), default));
            Assert.Equal("error: file not found", await t.RunAsync(Args(Path.Combine(d.Path, "nope.txt")), default));
        }
    }

    [Fact]
    public async Task ValidPath_OffWindows_IsWindowsOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var d = new TempDir();
        File.WriteAllText(d.File("a/b.txt"), "b");
        foreach (var t in All(d.Path))
        {
            Assert.Equal("error: Windows only", await t.RunAsync(Args("a/b.txt"), default));
            Assert.Equal("error: Windows only", await t.RunAsync(Args(Path.Combine(d.Path, "a", "b.txt")), default));
        }
    }

    [Theory]
    [InlineData("מכתב לעירייה - Word", "/docs/מכתב לעירייה.docx", true)]
    [InlineData("Report.PDF - Adobe Reader", "C:/x/report.pdf", true)]
    [InlineData("report - Notepad", "C:/x/Report.txt", true)]
    [InlineData("Untitled - Notepad", "C:/x/report.txt", false)]
    [InlineData("", "C:/x/report.txt", false)]
    public void TitleMatches_NameWithAndWithoutExtension(string title, string path, bool expected) =>
        Assert.Equal(expected, WindowMatcher.TitleMatches(title, path));

    [Fact]
    public void Settings_AllowTextTurns_EnvAndFile()
    {
        Assert.False(ShamashSettings.Load(_ => null, []).AllowTextTurns);
        Assert.True(ShamashSettings.Load(k => k == "SHAMASH_ALLOW_TEXT_TURNS" ? "1" : null, []).AllowTextTurns);
        using var d = new TempDir();
        var cfg = d.File("c.json");
        File.WriteAllText(cfg, """{"allowTextTurns":true}""");
        Assert.True(ShamashSettings.Load(_ => null, [cfg]).AllowTextTurns);
    }

    private sealed class CountingStt : ISpeechToText
    {
        public int Calls;
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) { Calls++; return Task.FromResult("from-stt"); }
    }
    private sealed class Echo : IReplyModel
    {
        public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> h, string u, CancellationToken ct) => Task.FromResult("re:" + u);
    }
    private sealed class OneChunkTts : ITextToSpeech
    {
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { await Task.Yield(); yield return new byte[640]; }
    }
    private sealed class Rec : ITurnSender
    {
        public readonly List<string> Texts = [];
        public Task SendTextAsync(string json, CancellationToken ct) { Texts.Add(json); return Task.CompletedTask; }
        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
    }

    [Fact]
    public async Task TextTurn_BypassesStt_WhenAllowed()
    {
        var stt = new CountingStt();
        var p = new TurnPipeline(stt, new Echo(), new OneChunkTts(), NullLogger.Instance, allowTextTurns: true);
        var s = new Rec();
        var r = await p.RunAsync(new byte[32000], 16000, s, default, "שלום עולם");
        Assert.Equal(0, stt.Calls);
        Assert.True(r.Done);
        Assert.Equal("שלום עולם", r.Transcript);
        Assert.Contains("\"type\":\"transcript\"", s.Texts[0]);
        Assert.Contains("\"text\":\"שלום עולם\"", s.Texts[0]);
        Assert.Contains("re:שלום עולם", s.Texts[1]);
    }

    [Fact]
    public async Task TextTurn_Ignored_WhenNotAllowed()
    {
        var stt = new CountingStt();
        var p = new TurnPipeline(stt, new Echo(), new OneChunkTts(), NullLogger.Instance);
        var r = await p.RunAsync(new byte[32000], 16000, new Rec(), default, "שלום");
        Assert.Equal(1, stt.Calls);
        Assert.Equal("from-stt", r.Transcript);
    }
}
