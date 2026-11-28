using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class PhoneBatteryTests
{
    private static readonly DateTime Now = new(2026, 11, 28, 12, 0, 0);
    private static PhoneBattery? Parse(string json) { using var d = JsonDocument.Parse(json); return PhoneBattery.FromHello(d.RootElement, Now); }

    [Fact]
    public void Hello_WithBattery_Parses()
    {
        var b = Parse("""{"type":"hello","battery":{"level":71,"charging":false}}""");
        Assert.Equal(new PhoneBattery(71, false, Now), b);
        Assert.Equal("battery 71%, not charging", b!.Describe());
        Assert.Equal("battery 100%, charging", new PhoneBattery(100, true, Now).Describe());
    }

    [Theory]
    [InlineData("""{"type":"hello"}""")]
    [InlineData("""{"type":"hello","battery":5}""")]
    [InlineData("""{"type":"hello","battery":{"level":"71","charging":true}}""")]
    [InlineData("""{"type":"hello","battery":{"level":101,"charging":true}}""")]
    [InlineData("""{"type":"hello","battery":{"level":50}}""")]
    public void Hello_WithoutValidBattery_IsNull(string json) => Assert.Null(Parse(json));

    [Fact]
    public void StatusLine_ShowsBatteryOrNothingYet()
    {
        Assert.Equal("Phone: no battery report yet", PhoneBattery.StatusLine(null));
        Assert.Equal("Phone: battery 71%, not charging (seen 12:00:00)", PhoneBattery.StatusLine(new PhoneBattery(71, false, Now)));
    }

    [Fact]
    public void PhoneReported_SaysChangedOnlyWhenLevelOrChargingDiffers()
    {
        var s = new ServiceStatus();
        Assert.True(s.PhoneReported(new PhoneBattery(71, false, Now)));
        Assert.False(s.PhoneReported(new PhoneBattery(71, false, Now.AddMinutes(5))));
        Assert.True(s.PhoneReported(new PhoneBattery(70, false, Now)));
        Assert.True(s.PhoneReported(new PhoneBattery(70, true, Now)));
    }

    private sealed class ListLoggerFactory : ILoggerFactory
    {
        public readonly List<string> Lines = [];
        public void AddProvider(ILoggerProvider p) { }
        public void Dispose() { }
        public ILogger CreateLogger(string name) => new L(Lines);
        private sealed class L(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel l) => true;
            public void Log<TState>(LogLevel l, EventId e, TState s, Exception? x, Func<TState, Exception?, string> f) { lock (lines) lines.Add(f(s, x)); }
        }
    }

    [Fact]
    public async Task Server_HelloBattery_UpdatesStatus_AndLogsOnlyOnChange_AndFeedsHeartbeat()
    {
        var dir = Path.Combine(Path.GetTempPath(), "shamash-bat-" + Guid.NewGuid().ToString("N"));
        var logs = new ListLoggerFactory();
        // 71 / not charging is the only value any test writes to the process-wide status, so parallel tests agree.
        await using var server = new LinkServer(logs, "http://127.0.0.1:0", dir);
        await server.StartAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        for (var i = 0; i < 2; i++)
        {
            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(server.BoundUrl!.Replace("http://", "ws://") + "/"), cts.Token);
            await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"hello","sampleRate":16000,"device":"test","battery":{"level":71,"charging":false}}"""), WebSocketMessageType.Text, true, cts.Token);
            await ws.SendAsync(Encoding.UTF8.GetBytes("""{"type":"end"}"""), WebSocketMessageType.Text, true, cts.Token);
            var buf = new byte[1024];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token); break; }
            }
        }
        await server.StopAsync();

        var st = ServiceStatus.Instance.PhoneBattery;
        Assert.NotNull(st);
        Assert.Equal(71, st!.Level);
        Assert.False(st.Charging);
        string[] seen; lock (logs.Lines) seen = [.. logs.Lines.Where(l => l.StartsWith("phone: "))];
        Assert.True(seen.Length <= 1); // another test may have reported the same value first
        Assert.All(seen, l => Assert.Equal("phone: battery 71%, not charging", l));

        var hb = JsonSerializer.SerializeToElement(HomeLinkService.BuildBody(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var phone = hb.GetProperty("phone");
        Assert.Equal(71, phone.GetProperty("batteryLevel").GetInt32());
        Assert.False(phone.GetProperty("charging").GetBoolean());
        Assert.True(phone.TryGetProperty("lastSeenAt", out _));
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }
}
