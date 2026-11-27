using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class ConversationTests
{
    private sealed class FakeStt(Func<byte[], CancellationToken, Task<string>> f) : ISpeechToText
    {
        public int Calls;
        public byte[]? LastWav;
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) { Calls++; LastWav = wav; return f(wav, ct); }
    }

    private sealed class FakeLlm : IReplyModel
    {
        public readonly List<int> HistorySizes = [];
        public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, CancellationToken ct)
        {
            HistorySizes.Add(history.Count);
            return Task.FromResult("re:" + userText);
        }
    }

    private sealed class FakeTts(Func<CancellationToken, IAsyncEnumerable<ReadOnlyMemory<byte>>> f) : ITextToSpeech
    {
        public IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, CancellationToken ct) => f(ct);
    }

    private sealed class RecordingSender : ITurnSender
    {
        public readonly List<string> Events = [];
        public readonly List<int> BinarySizes = [];
        public Task SendTextAsync(string json, CancellationToken ct)
        {
            using var d = JsonDocument.Parse(json);
            lock (Events) Events.Add(d.RootElement.GetProperty("type").GetString()!);
            Texts.Add(json);
            return Task.CompletedTask;
        }
        public readonly List<string> Texts = [];
        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
        {
            lock (Events) Events.Add("bin");
            BinarySizes.Add(data.Length);
            return Task.CompletedTask;
        }
    }

    private static async IAsyncEnumerable<ReadOnlyMemory<byte>> Chunks(params int[] sizes)
    {
        foreach (var n in sizes) { await Task.Yield(); yield return new byte[n]; }
    }

    private static readonly byte[] OneSecond = new byte[32000];

    private static TurnPipeline Pipeline(ISpeechToText stt, IReplyModel llm, ITextToSpeech tts) =>
        new(stt, llm, tts, NullLogger.Instance);

    private static FakeStt Stt(string text) => new((_, _) => Task.FromResult(text));

    [Fact]
    public async Task Pipeline_HappyPath_OrderIsExact()
    {
        var stt = Stt("שלום");
        var p = Pipeline(stt, new FakeLlm(), new FakeTts(_ => Chunks(640, 640, 320)));
        var s = new RecordingSender();
        var r = await p.RunAsync(OneSecond, 16000, s, CancellationToken.None);
        Assert.Equal(["transcript", "reply", "audio_start", "bin", "bin", "bin", "audio_end", "done"], s.Events);
        Assert.Equal(1600, r.TtsBytes);
        Assert.True(r.Done);
        Assert.Contains("\"text\":\"שלום\"", s.Texts[0]);
        Assert.Contains("\"sampleRate\":16000", s.Texts[2]);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(stt.LastWav!, 0, 4));
        Assert.Equal(44 + OneSecond.Length, stt.LastWav!.Length);
    }

    [Fact]
    public async Task Pipeline_OddChunksAreCarried()
    {
        var p = Pipeline(Stt("x"), new FakeLlm(), new FakeTts(_ => Chunks(3, 4, 1)));
        var s = new RecordingSender();
        var r = await p.RunAsync(OneSecond, 16000, s, CancellationToken.None);
        Assert.All(s.BinarySizes, n => Assert.Equal(0, n % 2));
        Assert.Equal(8, s.BinarySizes.Sum());
        Assert.Equal(8, r.TtsBytes);
    }

    [Fact]
    public async Task Pipeline_BlankTranscript_TranscriptAndDoneOnly()
    {
        var llm = new FakeLlm();
        var p = Pipeline(Stt("   "), llm, new FakeTts(_ => Chunks(10)));
        var s = new RecordingSender();
        await p.RunAsync(OneSecond, 16000, s, CancellationToken.None);
        Assert.Equal(["transcript", "done"], s.Events);
        Assert.Empty(llm.HistorySizes);
        Assert.Empty(p.HistorySnapshot());
    }

    [Fact]
    public async Task Pipeline_ShortAudio_SkipsStt()
    {
        var stt = Stt("should not be called");
        var p = Pipeline(stt, new FakeLlm(), new FakeTts(_ => Chunks(10)));
        var s = new RecordingSender();
        await p.RunAsync(new byte[15999], 16000, s, CancellationToken.None); // just under 0.5 s
        Assert.Equal(0, stt.Calls);
        Assert.Equal(["transcript", "done"], s.Events);
    }

    [Fact]
    public async Task Pipeline_SttThrows_ErrorAndNothingAfter()
    {
        var p = Pipeline(new FakeStt((_, _) => throw new InvalidOperationException("boom")), new FakeLlm(), new FakeTts(_ => Chunks(10)));
        var s = new RecordingSender();
        var r = await p.RunAsync(OneSecond, 16000, s, CancellationToken.None);
        Assert.Equal(["error"], s.Events);
        Assert.Contains("\"stage\":\"stt\"", s.Texts[0]);
        Assert.Equal("stt", r.ErrorStage);
    }

    [Fact]
    public async Task Pipeline_StageExceptionKeepsItsStage()
    {
        var p = Pipeline(new FakeStt((_, _) => throw new StageException("internal", "no ElevenLabs key configured")), new FakeLlm(), new FakeTts(_ => Chunks(10)));
        var s = new RecordingSender();
        await p.RunAsync(OneSecond, 16000, s, CancellationToken.None);
        Assert.Contains("\"stage\":\"internal\"", s.Texts[0]);
        Assert.Contains("no ElevenLabs key configured", s.Texts[0]);
    }

    [Fact]
    public async Task Pipeline_CancelDuringTts_NoDoneNoError()
    {
        using var cts = new CancellationTokenSource();
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Slow([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new byte[640];
            cts.Cancel();
            await Task.Delay(Timeout.Infinite, ct);
            yield return new byte[640];
        }
        var p = Pipeline(Stt("x"), new FakeLlm(), new FakeTts(ct => Slow(ct)));
        var s = new RecordingSender();
        var r = await p.RunAsync(OneSecond, 16000, s, cts.Token);
        Assert.Equal(["transcript", "reply", "audio_start", "bin"], s.Events);
        Assert.Equal("tts", r.CancelledAt);
        Assert.False(r.Done);
        Assert.Null(r.ErrorStage);
    }

    [Fact]
    public async Task Pipeline_HistoryGrowsByTwoAndIsCapped()
    {
        var llm = new FakeLlm();
        var p = Pipeline(Stt("q"), llm, new FakeTts(_ => Chunks(2)));
        for (var i = 0; i < 25; i++) await p.RunAsync(OneSecond, 16000, new RecordingSender(), CancellationToken.None);
        Assert.Equal(0, llm.HistorySizes[0]);
        Assert.Equal(2, llm.HistorySizes[1]);
        Assert.Equal(4, llm.HistorySizes[2]);
        Assert.Equal(TurnPipeline.MaxHistoryTurns, llm.HistorySizes[^1]);
        Assert.Equal(TurnPipeline.MaxHistoryTurns, p.HistorySnapshot().Count);
        Assert.Equal("user", p.HistorySnapshot()[0].Role);
        Assert.Equal("assistant", p.HistorySnapshot()[1].Role);
    }

    // ---- real WebSocket ----

    private static byte[] Frame(uint seq, byte[] pcm)
    {
        var b = new byte[12 + pcm.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, seq);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(4), 1000 + seq);
        pcm.CopyTo(b, 12);
        return b;
    }

    private static async Task<(List<string> events, List<int> bin, WebSocketCloseStatus? close)> Drain(ClientWebSocket ws, CancellationToken ct, Func<string, Task>? onEvent = null)
    {
        var events = new List<string>();
        var bin = new List<int>();
        var buf = new byte[8192];
        while (true)
        {
            var r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close)
            {
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return (events, bin, r.CloseStatus);
            }
            if (r.MessageType == WebSocketMessageType.Binary) { bin.Add(r.Count); events.Add("bin"); }
            else
            {
                using var d = JsonDocument.Parse(buf.AsMemory(0, r.Count));
                var t = d.RootElement.GetProperty("type").GetString()!;
                events.Add(t);
                if (onEvent is not null) await onEvent(t);
            }
        }
    }

    [Fact]
    public async Task Server_ConversationOverWebSocket()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shamash-conv-" + Guid.NewGuid().ToString("N"));
        var p = Pipeline(Stt("שלום עולם"), new FakeLlm(), new FakeTts(_ => Chunks(640, 640, 640)));
        await using var server = new LinkServer(NullLoggerFactory.Instance, "http://127.0.0.1:0", dir, p);
        await server.StartAsync();
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await ws.ConnectAsync(new Uri(server.BoundUrl!.Replace("http://", "ws://") + "/"), cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"hello","sampleRate":16000,"mode":"conversation","device":"test"}"""), WebSocketMessageType.Text, true, cts.Token);
        // 6 frames = 0.6 s; the pipeline skips STT below 0.5 s.
        for (uint i = 0; i < 6; i++)
            await ws.SendAsync(Frame(i, new byte[3200]), WebSocketMessageType.Binary, true, cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"end"}"""), WebSocketMessageType.Text, true, cts.Token);

        var (events, bin, close) = await Drain(ws, cts.Token);
        Assert.Equal(["transcript", "reply", "audio_start", "bin", "bin", "bin", "audio_end", "done"], events);
        Assert.Equal(WebSocketCloseStatus.NormalClosure, close);
        await server.StopAsync();

        var wavs = Directory.GetFiles(dir, "pclink-*.wav");
        var jsons = Directory.GetFiles(dir, "pclink-*.json");
        Assert.Single(wavs);
        Assert.Single(jsons);
        Assert.Equal(Path.ChangeExtension(wavs[0], null), Path.ChangeExtension(jsons[0], null));
        Assert.Equal(44 + 6 * 3200, new FileInfo(wavs[0]).Length);
        using var doc = JsonDocument.Parse(File.ReadAllText(jsons[0]));
        Assert.Equal("שלום עולם", doc.RootElement.GetProperty("transcript").GetString());
        Assert.Equal("re:שלום עולם", doc.RootElement.GetProperty("reply").GetString());
        Assert.True(doc.RootElement.GetProperty("done").GetBoolean());
        Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Server_CancelDuringTts_ClosesWithoutDone()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shamash-conv-" + Guid.NewGuid().ToString("N"));
        async IAsyncEnumerable<ReadOnlyMemory<byte>> Slow([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new byte[640];
            await Task.Delay(Timeout.Infinite, ct);
            yield return new byte[640];
        }
        var p = Pipeline(Stt("x"), new FakeLlm(), new FakeTts(ct => Slow(ct)));
        await using var server = new LinkServer(NullLoggerFactory.Instance, "http://127.0.0.1:0", dir, p);
        await server.StartAsync();
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await ws.ConnectAsync(new Uri(server.BoundUrl!.Replace("http://", "ws://") + "/"), cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"hello","sampleRate":16000,"mode":"conversation"}"""), WebSocketMessageType.Text, true, cts.Token);
        for (uint i = 0; i < 6; i++)
            await ws.SendAsync(Frame(i, new byte[3200]), WebSocketMessageType.Binary, true, cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"end"}"""), WebSocketMessageType.Text, true, cts.Token);
        var sent = false;
        var (events, _, _) = await Drain(ws, cts.Token, async t =>
        {
            if (t == "audio_start" && !sent)
            {
                sent = true;
                await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"cancel"}"""), WebSocketMessageType.Text, true, cts.Token);
            }
        });
        Assert.DoesNotContain("done", events);
        Assert.DoesNotContain("error", events);
        await server.StopAsync();
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    // ---- ElevenLabs TTS chunking with a stub HTTP handler ----

    private sealed class OddStream(params byte[][] parts) : Stream
    {
        private int _i;
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_i >= parts.Length) return ValueTask.FromResult(0);
            var p = parts[_i++];
            p.CopyTo(buffer);
            return ValueTask.FromResult(p.Length);
        }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> f) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) { Last = r; return Task.FromResult(f(r)); }
    }

    [Fact]
    public async Task ElevenLabsTts_YieldsEvenChunksOnly_AndUsesKeyHeader()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new OddStream([1, 2, 3], [4], [5, 6, 7, 8, 9], [10])) });
        var tts = new ElevenLabsTextToSpeech(new HttpClient(h), "k", "voice1");
        var got = new List<byte>();
        await foreach (var c in tts.SynthesizeAsync("hi", CancellationToken.None))
        {
            Assert.Equal(0, c.Length % 2);
            got.AddRange(c.ToArray());
        }
        Assert.Equal(Enumerable.Range(1, 10).Select(i => (byte)i), got);
        Assert.Equal("k", h.Last!.Headers.GetValues("xi-api-key").Single());
        Assert.Equal("https://api.elevenlabs.io/v1/text-to-speech/voice1/stream?output_format=pcm_16000", h.Last.RequestUri!.ToString());
    }

    [Fact]
    public async Task ElevenLabsStt_ParsesTextAndMissingKeyIsInternal()
    {
        var h = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"text":"שלום","words":[]}""") });
        Assert.Equal("שלום", await new ElevenLabsSpeechToText(new HttpClient(h), "k").TranscribeAsync(new byte[50], CancellationToken.None));
        Assert.StartsWith("multipart/form-data", h.Last!.Content!.Headers.ContentType!.MediaType!);
        var ex = await Assert.ThrowsAsync<StageException>(() => new ElevenLabsSpeechToText(new HttpClient(h), null).TranscribeAsync(new byte[50], CancellationToken.None));
        Assert.Equal("internal", ex.Stage);
        Assert.Equal("no ElevenLabs key configured", ex.Message);
    }

    // ---- settings ----

    private sealed class ListLogger : ILogger
    {
        public readonly List<string> Lines = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel l) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f) => Lines.Add(f(s, x));
    }

    [Fact]
    public void Settings_EnvOverridesFile_AndDefaults()
    {
        var path = Path.Combine(Path.GetTempPath(), "shamash-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """{"elevenLabsKey":"file-el","anthropicApiKey":"file-an","llm":"echo","ttsVoiceId":"fv"}""");
        try
        {
            var env = new Dictionary<string, string> { ["ELEVENLABS_KEY"] = "env-el", ["SHAMASH_LLM"] = "claude" };
            var s = ShamashSettings.Load(k => env.GetValueOrDefault(k), [Path.Combine(Path.GetTempPath(), "nope.json"), path]);
            Assert.Equal("env-el", s.ElevenLabsKey);
            Assert.Equal("file-an", s.AnthropicApiKey);
            Assert.Equal("claude", s.Llm);
            Assert.Equal("fv", s.TtsVoiceId);
            Assert.Equal(ShamashSettings.DefaultClaudeModel, s.ClaudeModel);

            var none = ShamashSettings.Load(_ => null, []);
            Assert.Null(none.ElevenLabsKey);
            Assert.Equal("claude", none.Llm);
            Assert.Equal("JBFqnCBsd6RMkjVDRZzb", none.TtsVoiceId);
            Assert.Equal("claude-opus-5-5", none.ClaudeModel);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Settings_LoggingIsBooleansOnly()
    {
        var log = new ListLogger();
        var s = new ShamashSettings("SECRET-EL", null, "claude", "v", "m");
        s.LogPresence(log);
        Assert.Equal(["config: elevenLabsKey=true anthropicApiKey=false llm=claude"], log.Lines);
        Assert.DoesNotContain("SECRET", string.Join("\n", log.Lines));
    }

    private sealed class UsageTool : IAgentTool
    {
        public string Name => "t";
        public string Description => "d";
        public string InputSchema => """{"type":"object","properties":{}}""";
        public Task<string> RunAsync(JsonElement input, CancellationToken ct)
        {
            // Stands in for translate_document: a model call made inside a tool adds to the same turn.
            UsageScope.Record(new LlmUsage(1, 5, 0, 0, 7));
            return Task.FromResult("r");
        }
    }

    private sealed class UsageConversation : IToolConversation
    {
        private int _n;
        public Task<ModelStep> NextAsync(bool withTools, CancellationToken ct)
        {
            var step = _n++ == 0
                ? new ModelStep("", [new ToolUseRequest("a", "t", JsonDocument.Parse("{}").RootElement.Clone())], null, new LlmUsage(1, 10, 100, 0, 20))
                : new ModelStep("done", [], null, new LlmUsage(1, 3, 0, 100, 4));
            return Task.FromResult(step);
        }
        public void AddToolResults(ModelStep step, IReadOnlyList<(string Id, string Text)> results) { }
    }

    private sealed class UsageLlm : IToolReplyModel
    {
        public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, CancellationToken ct) => ReplyAsync(history, userText, [], ct);
        public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, List<ToolCallRecord> toolCalls, CancellationToken ct) =>
            ToolLoop.RunAsync(new UsageConversation(), [new UsageTool()], toolCalls, null, ct);
    }

    private sealed class ScriptConversation(params ModelStep[] steps) : IToolConversation
    {
        public int Calls;
        public List<bool> WithTools = [];
        public List<string> Nudges = [];
        public Task<ModelStep> NextAsync(bool withTools, CancellationToken ct) { WithTools.Add(withTools); return Task.FromResult(steps[Calls++]); }
        public void AddToolResults(ModelStep step, IReadOnlyList<(string Id, string Text)> results) { }
        public void AddUserText(string text) => Nudges.Add(text);
    }

    private sealed class ScriptLlm(ScriptConversation c) : IToolReplyModel
    {
        public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, CancellationToken ct) => ReplyAsync(history, userText, [], ct);
        public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, List<ToolCallRecord> toolCalls, CancellationToken ct) =>
            ToolLoop.RunAsync(c, [new UsageTool()], toolCalls, null, ct);
    }

    private static ModelStep ToolStep(LlmUsage u) =>
        new("", [new ToolUseRequest("a", "t", JsonDocument.Parse("{}").RootElement.Clone())], null, u);

    [Fact]
    public async Task EmptyFinalReply_RetriesOnceWithoutTools_AndSumsUsage()
    {
        var c = new ScriptConversation(ToolStep(new LlmUsage(1, 10, 0, 0, 20)),
            new ModelStep("  ", [], null, new LlmUsage(1, 3, 0, 0, 1)),
            new ModelStep("יש חלון", [], null, new LlmUsage(1, 4, 0, 0, 5)));
        var p = Pipeline(Stt("x"), new ScriptLlm(c), new FakeTts(_ => Chunks(10)));
        var r = await p.RunAsync(OneSecond, 16000, new RecordingSender(), CancellationToken.None);
        Assert.Equal("יש חלון", r.Reply);
        Assert.Equal(3, c.Calls);
        Assert.Equal([true, true, false], c.WithTools);
        Assert.Equal([ToolLoop.EmptyReplyNudge], c.Nudges);
        // UsageTool records 1 call (5 in, 7 out) as well.
        Assert.Equal(new LlmUsage(4, 22, 0, 0, 33), r.Usage);
    }

    [Fact]
    public async Task EmptyFirstReply_RetriesOnce()
    {
        var c = new ScriptConversation(new ModelStep("", [], null, new LlmUsage(1, 1, 0, 0, 0)),
            new ModelStep("שלום", [], null, new LlmUsage(1, 2, 0, 0, 3)));
        var p = Pipeline(Stt("x"), new ScriptLlm(c), new FakeTts(_ => Chunks(10)));
        var r = await p.RunAsync(OneSecond, 16000, new RecordingSender(), CancellationToken.None);
        Assert.Equal("שלום", r.Reply);
        Assert.Equal(2, c.Calls);
    }

    [Fact]
    public async Task EmptyTwice_FailsWithEmptyReply()
    {
        var c = new ScriptConversation(ToolStep(new LlmUsage(1, 1, 0, 0, 1)),
            new ModelStep("", [], null), new ModelStep(" ", [], null));
        var p = Pipeline(Stt("x"), new ScriptLlm(c), new FakeTts(_ => Chunks(10)));
        var r = await p.RunAsync(OneSecond, 16000, new RecordingSender(), CancellationToken.None);
        Assert.Equal(3, c.Calls);
        Assert.Equal("llm", r.ErrorStage);
        Assert.Equal("empty reply", r.ErrorMessage);
    }

    [Fact]
    public async Task NonEmptyReply_NeverRetries()
    {
        var c = new ScriptConversation(new ModelStep("hi", [], null));
        var p = Pipeline(Stt("x"), new ScriptLlm(c), new FakeTts(_ => Chunks(10)));
        await p.RunAsync(OneSecond, 16000, new RecordingSender(), CancellationToken.None);
        Assert.Equal(1, c.Calls);
        Assert.Empty(c.Nudges);
    }

    [Fact]
    public async Task Turn_SumsUsageOverModelCalls_AndWritesItToJson()
    {
        var p = Pipeline(Stt("x"), new UsageLlm(), new FakeTts(_ => Chunks(10)));
        var r = await p.RunAsync(OneSecond, 16000, new RecordingSender(), CancellationToken.None);
        Assert.Equal(new LlmUsage(3, 18, 100, 100, 31), r.Usage);
        using var d = JsonDocument.Parse(r.ToJson());
        var u = d.RootElement.GetProperty("usage");
        Assert.Equal(3, u.GetProperty("calls").GetInt32());
        Assert.Equal(18, u.GetProperty("input").GetInt64());
        Assert.Equal(100, u.GetProperty("cacheWrite").GetInt64());
        Assert.Equal(100, u.GetProperty("cacheRead").GetInt64());
        Assert.Equal(31, u.GetProperty("output").GetInt64());
    }

    [Fact]
    public void ClaudeParams_CacheSystemBlockAndLastTool()
    {
        var tools = new IAgentTool[] { new UsageTool(), new UsageTool() };
        var defs = ClaudeReplyModel.BuildToolDefs(tools);
        var pr = ClaudeReplyModel.BuildParams("m", "sys", defs, [], true);
        Assert.NotNull(pr.System);
        Assert.True(pr.System!.TryPickTextBlockParams(out var blocks));
        var sys = Assert.Single(blocks!);
        Assert.Equal("sys", sys.Text);
        Assert.NotNull(sys.CacheControl);
        Assert.True(defs[1].TryPickTool(out var last) && last!.CacheControl is not null);
        Assert.True(defs[0].TryPickTool(out var first) && first!.CacheControl is null);
    }
}
