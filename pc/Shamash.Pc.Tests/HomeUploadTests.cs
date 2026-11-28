using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Serilog.Events;
using Serilog.Parsing;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class HomeUploadTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shamash-home-up-" + Guid.NewGuid().ToString("N"));

    public HomeUploadTests()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_x"}""");
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, byte[] Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Calls.Add((request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ShamashSettings Settings() => new(null, null, "echo", "v", "m")
    {
        HomeUrl = "https://home.example", HomeEnrolToken = "secret-token", HomeHeartbeatMinutes = 5,
    };

    private static HomeTurn Turn(int i) =>
        new($"pclink-{i:D8}", DateTimeOffset.FromUnixTimeSeconds(1790000000 + i), $$"""{"transcript":"שלום {{i}}","toolCalls":[]}""");

    private static HttpResponseMessage Route(HttpRequestMessage r, string heartbeat) =>
        r.RequestUri!.AbsolutePath == "/api/heartbeat" ? Ok(heartbeat) : Ok("""{"accepted":1,"duplicates":0}""");

    private const string Quiet = """{"desiredVersion":null,"hold":false,"commands":[]}""";

    private HomeLinkService Service(FakeHandler h, HomeUploadQueue q, MemoryLogSink? sink = null, UpdateControl? control = null) =>
        new(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir, q, sink, control);

    private static void Log(MemoryLogSink sink, DateTimeOffset at, LogEventLevel level, string text) =>
        sink.Emit(new LogEvent(at, level, null, new MessageTemplateParser().Parse(text), []));

    [Fact]
    public void Queue_PeeksBatches_AndDropsOldestAtCapacity()
    {
        var q = new HomeUploadQueue();
        for (var i = 0; i < 520; i++) q.Enqueue(Turn(i));
        Assert.Equal(500, q.Count);
        var batch = q.Peek();
        Assert.Equal(50, batch.Count);
        Assert.Equal("pclink-00000020", batch[0].TurnId);
        Assert.Equal(500, q.Count);
        q.Remove(batch);
        Assert.Equal(450, q.Count);
        Assert.Equal("pclink-00000070", q.Peek(1)[0].TurnId);
    }

    [Fact]
    public async Task Queue_SignalsOnEnqueue()
    {
        var q = new HomeUploadQueue();
        Assert.False(await q.WaitAsync(TimeSpan.FromMilliseconds(20), default));
        q.Enqueue(Turn(1));
        Assert.True(await q.WaitAsync(TimeSpan.FromSeconds(5), default));
    }

    [Theory]
    [InlineData("Warning", "anything", true)]
    [InlineData("Error", "anything", true)]
    [InlineData("Information", "turn: heard it", true)]
    [InlineData("Information", "tool: search ok", true)]
    [InlineData("Information", "llm usage: in 10", true)]
    [InlineData("Information", "missing feature: reminders", true)]
    [InlineData("Information", "link: tool x from y", false)]
    [InlineData("Information", "home: heartbeat ok", false)]
    [InlineData("Debug", "turn: x", false)]
    public void LogFilter(string level, string message, bool expected) =>
        Assert.Equal(expected, HomeLogUpload.Wants(level, message));

    [Fact]
    public void MemoryLogSink_SinceReturnsOldestNewerLines()
    {
        var sink = new MemoryLogSink(10);
        var t0 = DateTimeOffset.FromUnixTimeSeconds(1790000000);
        for (var i = 0; i < 5; i++) Log(sink, t0.AddSeconds(i), LogEventLevel.Warning, "line " + i);
        Assert.Equal(["line 3", "line 4"], sink.Since(t0.AddSeconds(2), 10).Select(l => l.Message));
        Assert.Equal(["line 0", "line 1"], sink.Since(DateTimeOffset.MinValue, 2).Select(l => l.Message));
        Assert.Equal("Warning", sink.Since(DateTimeOffset.MinValue, 1)[0].Level);
    }

    [Fact]
    public async Task HeartbeatCommands_DriveTheUpdateControl()
    {
        var control = new UpdateControl();
        var reply = """{"hold":true,"commands":[{"id":"k1","type":"check_update","args":{}},{"id":"k2","type":"restart","args":{}},{"id":"k3","type":"bogus"}]}""";
        var h = new FakeHandler((r, b) => Ok(reply));
        var svc = Service(h, new HomeUploadQueue(), control: control);
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.True(control.Hold);
        Assert.Equal(1, control.CheckTriggers);

        reply = Quiet;
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.False(control.Hold);
        Assert.Equal(1, control.CheckTriggers);
    }

    [Fact]
    public async Task TurnUpload_SendsRecordsAsIs_InBatchesOf50_AfterHeartbeat()
    {
        var q = new HomeUploadQueue();
        for (var i = 0; i < 120; i++) q.Enqueue(Turn(i));
        var h = new FakeHandler((r, b) => Route(r, Quiet));
        Assert.True(await Service(h, q).TickAsync(Settings(), default));
        Assert.Equal(["/api/heartbeat", "/api/turns", "/api/turns", "/api/turns"], h.Calls.Select(c => c.Path));
        Assert.Equal(0, q.Count);
        var sizes = h.Calls.Skip(1).Select(c => JsonDocument.Parse(c.Body).RootElement.GetProperty("turns").GetArrayLength());
        Assert.Equal([50, 50, 20], sizes);

        var first = JsonDocument.Parse(h.Calls[1].Body).RootElement.GetProperty("turns")[0];
        Assert.Equal("pclink-00000000", first.GetProperty("turnId").GetString());
        Assert.Equal("2026-09-21T14:13:20Z", first.GetProperty("time").GetString());
        Assert.Equal("שלום 0", first.GetProperty("record").GetProperty("transcript").GetString());
    }

    [Fact]
    public async Task TurnUpload_KeepsItemsOn5xx_AndRetriesAfterNextHeartbeat()
    {
        var q = new HomeUploadQueue();
        for (var i = 0; i < 3; i++) q.Enqueue(Turn(i));
        var fail = true;
        var h = new FakeHandler((r, b) => r.RequestUri!.AbsolutePath == "/api/turns" && fail
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) : Route(r, Quiet));
        var svc = Service(h, q);
        Assert.True(await svc.TickAsync(Settings(), default));  // the heartbeat itself succeeded
        Assert.Equal(3, q.Count);
        Assert.Equal(3, ServiceStatus.Instance.HomeQueuedTurns);

        fail = false;
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.Equal(0, q.Count);
    }

    [Fact]
    public async Task LogUpload_SendsOnlyWantedLines_OnceEach()
    {
        var sink = new MemoryLogSink();
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-1);
        Log(sink, t0, LogEventLevel.Information, "home: heartbeat ok");
        Log(sink, t0.AddSeconds(1), LogEventLevel.Warning, "disk low");
        Log(sink, t0.AddSeconds(2), LogEventLevel.Information, "turn: heard something");
        var h = new FakeHandler((r, b) => Route(r, Quiet));
        var svc = Service(h, new HomeUploadQueue(), sink);
        Assert.True(await svc.TickAsync(Settings(), default));
        var logs = h.Calls.Single(c => c.Path == "/api/logs");
        var lines = JsonDocument.Parse(logs.Body).RootElement.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(["disk low", "turn: heard something"], lines.Select(l => l.GetProperty("message").GetString()));
        Assert.Equal("Warning", lines[0].GetProperty("level").GetString());

        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.Single(h.Calls, c => c.Path == "/api/logs");
    }
}
