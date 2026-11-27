using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class LinkTests
{
    private static byte[] Build(uint seq, long t, byte[] pcm)
    {
        var b = new byte[12 + pcm.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, seq);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(4), t);
        pcm.CopyTo(b, 12);
        return b;
    }

    [Theory]
    [InlineData(0u, 0L)]
    [InlineData(5u, 1_700_000_000_123L)]
    [InlineData(3_000_000_000u, -42L)]
    [InlineData(uint.MaxValue, long.MaxValue)]
    public void Frame_Decodes(uint seq, long t)
    {
        var pcm = new byte[] { 1, 2, 3, 4 };
        Assert.True(LinkFrame.TryParse(Build(seq, t, pcm), out var f));
        Assert.Equal(seq, f.Seq);
        Assert.Equal(t, f.TPhoneMs);
        Assert.Equal(pcm, f.Pcm.ToArray());
    }

    [Fact]
    public void Frame_HeaderOnlyIsValidAndShortIsRejected()
    {
        Assert.True(LinkFrame.TryParse(new byte[12], out var f));
        Assert.Equal(0, f.Pcm.Length);
        Assert.False(LinkFrame.TryParse(new byte[11], out _));
        Assert.False(LinkFrame.TryParse(Array.Empty<byte>(), out _));
    }

    private static LinkSessionStats Stats(params uint[] seqs)
    {
        var s = new LinkSessionStats();
        var t = 0.0;
        foreach (var q in seqs) s.Add(q, 3200, t += 100);
        return s;
    }

    [Fact]
    public void Gaps_Counting()
    {
        Assert.Equal(0, Stats().Gaps);
        Assert.Equal(0, Stats(0, 1, 2).Gaps);
        Assert.Equal(0, Stats(0, 1, 1, 2).Gaps);          // duplicate, no hole
        Assert.Equal(2, Stats(0, 1, 4, 5).Gaps);          // holes 2,3
        Assert.Equal(3, Stats(0, 0, 2, 5).Gaps);          // duplicate plus holes 1,3,4
        Assert.Equal(1, Stats(3_000_000_000, 3_000_000_002).Gaps);
        Assert.Equal(0, Stats(7).Gaps);
    }

    [Fact]
    public void InterArrival_MinMedianMax()
    {
        Assert.Null(Stats().InterArrival());
        Assert.Null(Stats(0).InterArrival());
        var s = new LinkSessionStats();
        foreach (var (q, t) in new[] { (0u, 0.0), (1u, 100.0), (2u, 300.0), (3u, 310.0) }) s.Add(q, 1, t);
        // diffs 100, 200, 10 -> odd count: middle of sorted = 100
        Assert.Equal((10.0, 100.0, 200.0), s.InterArrival());
        s.Add(4, 1, 410); // diffs 10,100,100,200 -> even count: mean of middle two = 100
        Assert.Equal((10.0, 100.0, 200.0), s.InterArrival());
        var e = new LinkSessionStats();
        foreach (var (q, t) in new[] { (0u, 0.0), (1u, 10.0), (2u, 40.0) }) e.Add(q, 1, t); // 10, 30 -> 20
        Assert.Equal((10.0, 20.0, 30.0), e.InterArrival());
    }

    [Fact]
    public void Stats_AudioSeconds()
    {
        Assert.Equal(0.3, Stats(0, 1, 2).AudioSeconds(16000), 3); // 9600 bytes / 32000
    }

    [Fact]
    public void Wav_Header()
    {
        const int n = 9600;
        var ms = new MemoryStream();
        WavWriter.Write(ms, new byte[n], 16000);
        var b = ms.ToArray();
        Assert.Equal(44 + n, b.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(b, 0, 4));
        Assert.Equal((uint)(36 + n), BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(b, 8, 8));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(16)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(20)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(22)));
        Assert.Equal(16000, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(24)));
        Assert.Equal(32000, BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(28)));
        Assert.Equal(2, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(32)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(34)));
        Assert.Equal("data", Encoding.ASCII.GetString(b, 36, 4));
        Assert.Equal((uint)n, BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(40)));
    }

    [Fact]
    public async Task Server_AcksFramesAndSummarises()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shamash-link-" + Guid.NewGuid().ToString("N"));
        await using var server = new LinkServer(NullLoggerFactory.Instance, "http://127.0.0.1:0", dir);
        await server.StartAsync();
        var uri = new Uri(server.BoundUrl!.Replace("http://", "ws://") + "/");
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await ws.ConnectAsync(uri, cts.Token);

        async Task SendText(string s) =>
            await ws.SendAsync(Encoding.UTF8.GetBytes(s), WebSocketMessageType.Text, true, cts.Token);
        async Task<JsonDocument> Recv()
        {
            var buf = new byte[4096];
            var r = await ws.ReceiveAsync(buf, cts.Token);
            return JsonDocument.Parse(buf.AsMemory(0, r.Count));
        }

        await SendText("""{"type":"hello","sampleRate":16000,"seconds":1,"device":"test"}""");
        for (uint i = 0; i < 3; i++)
            await ws.SendAsync(Build(i, 1000 + i, new byte[3200]), WebSocketMessageType.Binary, true, cts.Token);
        for (uint i = 0; i < 3; i++)
        {
            using var ack = await Recv();
            Assert.Equal("ack", ack.RootElement.GetProperty("type").GetString());
            Assert.Equal(i, ack.RootElement.GetProperty("seq").GetUInt32());
            Assert.Equal(1000 + i, ack.RootElement.GetProperty("tPhone").GetInt64());
            Assert.True(ack.RootElement.GetProperty("tServerMs").GetInt64() > 0);
        }
        await SendText("""{"type":"end"}""");
        using var sum = await Recv();
        var root = sum.RootElement;
        Assert.Equal("summary", root.GetProperty("type").GetString());
        Assert.Equal(3, root.GetProperty("frames").GetInt32());
        Assert.Equal(3 * 3200, root.GetProperty("bytes").GetInt64());
        Assert.Equal(0, root.GetProperty("gaps").GetInt64());
        Assert.Equal(0.3, root.GetProperty("seconds").GetDouble(), 3);

        var buf2 = new byte[16];
        await ws.ReceiveAsync(buf2, cts.Token); // close frame
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token); // answer it, or the server's close handshake waits out the host shutdown timeout
        await server.StopAsync();
        var wavs = Directory.GetFiles(dir, "pclink-*.wav");
        Assert.Single(wavs);
        Assert.Equal(44 + 9600, new FileInfo(wavs[0]).Length);
        Directory.Delete(dir, true);
    }

    private sealed class FakeTool(Func<JsonElement, string> run) : IAgentTool
    {
        public string Name => "fake_tool";
        public string Description => "fake";
        public string InputSchema => """{"type":"object"}""";
        public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.FromResult(run(input));
    }

    private static Task<JsonDocument> ToolCall(bool allow, string message, IAgentTool tool) =>
        Send(new TurnPipeline(new NoStt(), new EchoReplyModel(), new NoTts(), NullLogger.Instance, allow, [tool]), message);

    private static async Task<JsonDocument> Send(TurnPipeline pipe, string message)
    {
        var dir = Path.Combine(Path.GetTempPath(), "shamash-link-" + Guid.NewGuid().ToString("N"));
        await using var server = new LinkServer(NullLoggerFactory.Instance, "http://127.0.0.1:0", dir, pipe);
        await server.StartAsync();
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await ws.ConnectAsync(new Uri(server.BoundUrl!.Replace("http://", "ws://") + "/"), cts.Token);
        await ws.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, cts.Token);
        var buf = new byte[65536];
        var r = await ws.ReceiveAsync(buf, cts.Token);
        var doc = JsonDocument.Parse(buf.AsMemory(0, r.Count));
        await ws.ReceiveAsync(buf, cts.Token); // server's close frame
        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token); // complete the handshake; otherwise StopAsync waits ~30 s
        await server.StopAsync();
        return doc;
    }

    private sealed class NoStt : ISpeechToText
    {
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) => Task.FromResult("");
    }

    private sealed class NoTts : ITextToSpeech
    {
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct) { await Task.CompletedTask; yield break; }
    }

    [Fact]
    public async Task ToolMessage_RunsToolAndReturnsResult()
    {
        using var d = await ToolCall(true, """{"type":"tool","name":"fake_tool","input":{"path":"a b"}}""",
            new FakeTool(i => "got " + i.GetProperty("path").GetString()));
        var r = d.RootElement;
        Assert.Equal("tool_result", r.GetProperty("type").GetString());
        Assert.Equal("fake_tool", r.GetProperty("name").GetString());
        Assert.Equal("got a b", r.GetProperty("result").GetString());
        Assert.True(r.GetProperty("ms").GetInt64() >= 0);
    }

    [Fact]
    public async Task ToolMessage_ThrownErrorBecomesError()
    {
        using var d = await ToolCall(true, """{"type":"tool","name":"fake_tool","input":{}}""",
            new FakeTool(_ => throw new InvalidOperationException("boom")));
        Assert.Equal("boom", d.RootElement.GetProperty("error").GetString());
        Assert.False(d.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ToolMessage_RefusedWhenTextTurnsOff()
    {
        var ran = false;
        using var d = await ToolCall(false, """{"type":"tool","name":"fake_tool","input":{}}""",
            new FakeTool(_ => { ran = true; return "x"; }));
        Assert.False(ran);
        Assert.True(d.RootElement.TryGetProperty("error", out _));
        Assert.False(d.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task ToolMessage_UnknownTool()
    {
        using var d = await ToolCall(true, """{"type":"tool","name":"nope","input":{}}""", new FakeTool(_ => "x"));
        Assert.Equal("unknown tool nope", d.RootElement.GetProperty("error").GetString());
    }

    private sealed class NullSender : ITurnSender
    {
        public Task SendTextAsync(string json, CancellationToken ct) => Task.CompletedTask;
        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class CountingTts : ITextToSpeech
    {
        public int Calls;
        public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        { Calls++; await Task.CompletedTask; yield return new byte[640]; }
    }

    private sealed class ListSender : ITurnSender
    {
        public readonly List<string> Events = [];
        public Task SendTextAsync(string json, CancellationToken ct)
        {
            using var d = JsonDocument.Parse(json);
            Events.Add(d.RootElement.GetProperty("type").GetString()!);
            return Task.CompletedTask;
        }
        public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) { Events.Add("bin"); return Task.CompletedTask; }
    }

    [Fact]
    public async Task TextTurn_NoTts_SkipsSynthesis()
    {
        var tts = new CountingTts();
        var pipe = new TurnPipeline(new NoStt(), new EchoReplyModel(), tts, NullLogger.Instance, allowTextTurns: true);
        var s = new ListSender();
        var r = await pipe.RunAsync([], 16000, s, default, "hello", noTts: true);
        Assert.Equal(["transcript", "reply", "done"], s.Events);
        Assert.Equal(0, tts.Calls);
        Assert.True(r.Done);
        Assert.Null(r.TtsFirstByteMs);
        Assert.Null(r.TtsTotalMs);
        Assert.Equal(0, r.TtsBytes);
        using var j = JsonDocument.Parse(r.ToJson());
        Assert.Equal(JsonValueKind.Null, j.RootElement.GetProperty("timingsMs").GetProperty("ttsFirstByte").ValueKind);
        Assert.Equal(JsonValueKind.Null, j.RootElement.GetProperty("timingsMs").GetProperty("ttsTotal").ValueKind);
    }

    [Fact]
    public async Task TextTurn_NoTts_IgnoredWhenTextTurnsOff()
    {
        // gate off: the hello text is ignored, so the turn is a normal audio turn and noTts must not skip synthesis
        var tts = new CountingTts();
        var pipe = new TurnPipeline(new SaysHi(), new EchoReplyModel(), tts, NullLogger.Instance, allowTextTurns: false);
        var s = new ListSender();
        await pipe.RunAsync(new byte[32000], 16000, s, default, "hello", noTts: true);
        Assert.Equal(["transcript", "reply", "audio_start", "bin", "audio_end", "done"], s.Events);
        Assert.Equal(1, tts.Calls);
    }

    private sealed class SaysHi : ISpeechToText
    {
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken ct) => Task.FromResult("hi");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResetMessage_ClearsHistoryOnlyWhenTextTurnsOn(bool allow)
    {
        var pipe = new TurnPipeline(new NoStt(), new EchoReplyModel(), new NoTts(), NullLogger.Instance, allowTextTurns: true);
        await pipe.RunAsync(new byte[32000], 16000, new NullSender(), default, "hello");
        Assert.Equal(2, pipe.HistorySnapshot().Count);
        if (!allow)
        {
            // same history, but a pipeline that refuses: build the refusing one and check it directly
            var off = new TurnPipeline(new NoStt(), new EchoReplyModel(), new NoTts(), NullLogger.Instance, allowTextTurns: false);
            using var refused = await Send(off, """{"type":"reset"}""");
            Assert.False(refused.RootElement.TryGetProperty("type", out var t) && t.GetString() == "reset_ok");
            Assert.True(refused.RootElement.TryGetProperty("error", out _));
            return;
        }
        using var d = await Send(pipe, """{"type":"reset"}""");
        Assert.Equal("reset_ok", d.RootElement.GetProperty("type").GetString());
        Assert.Empty(pipe.HistorySnapshot());
    }
}
