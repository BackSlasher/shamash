using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>Phone-link probe server (port of tools/pclink/server.py): Kestrel + WebSocket at "/".</summary>
public sealed class LinkServer(ILoggerFactory loggerFactory, string url, string outDir, TurnPipeline? pipeline = null) : IAsyncDisposable
{
    public const int DefaultPort = 8777;
    public const int MaxMessageBytes = 1 << 20;

    private readonly ILogger _log = loggerFactory.CreateLogger<LinkServer>();
    private WebApplication? _app;
    private readonly Lazy<TurnPipeline> _pipeline = new(() => pipeline ?? TurnPipeline.Create(
        ShamashSettings.LoadDefault(loggerFactory.CreateLogger<ShamashSettings>()), SharedHttp.Client, loggerFactory));

    public static int PortFromEnvironment() =>
        int.TryParse(Environment.GetEnvironmentVariable("SHAMASH_LINK_PORT"), out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    public static string DefaultOutDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(baseDir) ? Path.GetFullPath("pclink") : Path.Combine(baseDir, "Shamash", "pclink");
    }

    /// <summary>First bound address after start (useful when listening on port 0).</summary>
    public string? BoundUrl { get; private set; }

    public async Task StartAsync(CancellationToken ct = default)
    {
        var b = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        b.Logging.ClearProviders();
        b.Services.AddSingleton(loggerFactory);
        b.WebHost.UseUrls(url);
        var app = b.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.Map("/", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsync("WebSocket endpoint", ctx.RequestAborted);
                return;
            }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            var peer = $"{ctx.Connection.RemoteIpAddress}:{ctx.Connection.RemotePort}";
            await HandleAsync(ws, peer, ctx.RequestAborted);
        });
        await app.StartAsync(ct);
        _app = app;
        BoundUrl = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        _log.LogInformation("link: listening on {Url}", url);
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        if (_app is { } a) await a.StopAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is { } a) await a.DisposeAsync();
    }

    private async Task HandleAsync(WebSocket ws, string peer, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var rate = 16000;
        var pcm = new MemoryStream();
        var stats = new LinkSessionStats();
        var buf = new byte[64 * 1024];
        var conversation = false;
        var stamp = DateTime.Now;
        using var turnCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<TurnResult>? turn = null;
        TurnResult? turnResult = null;
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = new MemoryStream();
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf.AsMemory(), loopCts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) goto done;
                    msg.Write(buf, 0, r.Count);
                    if (msg.Length > MaxMessageBytes)
                    {
                        await ws.CloseAsync(WebSocketCloseStatus.MessageTooBig, "too big", CancellationToken.None);
                        goto done;
                    }
                } while (!r.EndOfMessage);

                if (r.MessageType == WebSocketMessageType.Text)
                {
                    if (!TryParseJson(msg.GetBuffer().AsSpan(0, (int)msg.Length), out var type, out var root)) continue;
                    using (root)
                    {
                        if (type == "hello")
                        {
                            if (root!.RootElement.TryGetProperty("sampleRate", out var sr) && sr.TryGetInt32(out var v) && v > 0) rate = v;
                            var wasConversation = conversation;
                            conversation = root.RootElement.TryGetProperty("mode", out var mode)
                                && mode.ValueKind == JsonValueKind.String && mode.GetString() == "conversation";
                            if (conversation && !wasConversation) ServiceStatus.Instance.TurnStarted();
                            _log.LogInformation("link: hello from {Peer}: {Hello}", peer, Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
                        }
                        else if (type == "cancel" && conversation)
                        {
                            turnCts.Cancel();
                            if (turn is null) goto done;
                        }
                        else if (type == "end")
                        {
                            if (!conversation) goto done;
                            if (turn is null)
                            {
                                var sender = new WebSocketSender(ws);
                                var audio = pcm.ToArray();
                                SaveWav(audio, rate, stamp);
                                turn = RunTurnAsync(audio, rate, sender, turnCts.Token, ws, loopCts);
                            }
                        }
                    }
                    continue;
                }

                if (conversation)
                {
                    // Conversation mode: no acks. Audio after `end` is ignored.
                    if (turn is null && LinkFrame.TryParse(msg.GetBuffer().AsMemory(0, (int)msg.Length), out var cf))
                    {
                        stats.Add(cf.Seq, cf.Pcm.Length, sw.Elapsed.TotalMilliseconds);
                        pcm.Write(cf.Pcm.Span);
                    }
                    continue;
                }

                var now = sw.Elapsed.TotalMilliseconds;
                if (!LinkFrame.TryParse(msg.GetBuffer().AsMemory(0, (int)msg.Length), out var f)) continue;
                stats.Add(f.Seq, f.Pcm.Length, now);
                pcm.Write(f.Pcm.Span);
                var ack = string.Create(CultureInfo.InvariantCulture,
                    $"{{\"type\":\"ack\",\"seq\":{f.Seq},\"tPhone\":{f.TPhoneMs},\"tServerMs\":{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}}}");
                await ws.SendAsync(Encoding.UTF8.GetBytes(ack), WebSocketMessageType.Text, true, ct);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        done:

        if (conversation)
        {
            // Socket closed or cancelled: stop the pipeline and wait for it to unwind.
            if (ws.State is not (WebSocketState.Open or WebSocketState.CloseSent)) turnCts.Cancel();
            else if (turn is null) turnCts.Cancel();
            if (turn is not null)
            {
                try { turnResult = await turn; } catch (Exception e) { _log.LogWarning(e, "turn: pipeline faulted"); }
            }
            else
            {
                // Closed before `end`: nothing was run; keep whatever audio arrived.
                SaveWav(pcm.ToArray(), rate, stamp);
            }
            try
            {
                if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
            if (turnResult is not null) SaveTurnJson(turnResult, stamp);
            ServiceStatus.Instance.TurnEnded(turnResult);
            _log.LogInformation("link: conversation peer={Peer} frames={Frames} bytes={Bytes} audio={Audio}s wall={Wall}s",
                peer, stats.Frames, stats.Bytes, stats.AudioSeconds(rate).ToString("F2", CultureInfo.InvariantCulture),
                sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture));
            return;
        }

        var secs = stats.AudioSeconds(rate);
        try
        {
            var summary = string.Create(CultureInfo.InvariantCulture,
                $"{{\"type\":\"summary\",\"frames\":{stats.Frames},\"bytes\":{stats.Bytes},\"seconds\":{Math.Round(secs, 3):0.###},\"gaps\":{stats.Gaps}}}");
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await ws.SendAsync(Encoding.UTF8.GetBytes(summary), WebSocketMessageType.Text, true, CancellationToken.None);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }

        if (pcm.Length > 0)
        {
            try
            {
                Directory.CreateDirectory(outDir);
                var path = Path.Combine(outDir, $"pclink-{DateTime.Now:yyyyMMdd-HHmmss}.wav");
                await using var fs = File.Create(path);
                WavWriter.Write(fs, pcm.GetBuffer().AsSpan(0, (int)pcm.Length), rate);
            }
            catch (Exception e) { _log.LogWarning(e, "link: could not write WAV to {Dir}", outDir); }
        }

        var jit = stats.InterArrival() is { } j
            ? string.Create(CultureInfo.InvariantCulture, $"{j.Min:F1}/{j.Median:F1}/{j.Max:F1}")
            : "n/a";
        _log.LogInformation("link: peer={Peer} frames={Frames} bytes={Bytes} audio={Audio}s wall={Wall}s gaps={Gaps} interarrival_ms(min/med/max)={Jit}",
            peer, stats.Frames, stats.Bytes, secs.ToString("F2", CultureInfo.InvariantCulture),
            sw.Elapsed.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture), stats.Gaps, jit);
    }

    private async Task<TurnResult> RunTurnAsync(byte[] pcm, int rate, ITurnSender sender, CancellationToken ct, WebSocket ws, CancellationTokenSource loopCts)
    {
        TurnResult r;
        try { r = await _pipeline.Value.RunAsync(pcm, rate, sender, ct); }
        catch (Exception e)
        {
            _log.LogError(e, "turn: internal failure");
            r = new TurnResult { ErrorStage = "internal", ErrorMessage = e.Message };
            try { await sender.SendTextAsync(TurnJson.Message("error", ("stage", "internal"), ("message", e.Message)), CancellationToken.None); }
            catch (Exception) { }
        }
        // Send our close frame; the receive loop ends when the phone answers (or after a grace period).
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
        loopCts.CancelAfter(TimeSpan.FromSeconds(5));
        return r;
    }

    private string TurnBaseName(DateTime stamp) => Path.Combine(outDir, $"pclink-{stamp:yyyyMMdd-HHmmss}");

    private void SaveWav(byte[] pcm, int rate, DateTime stamp)
    {
        if (pcm.Length == 0) return;
        try
        {
            Directory.CreateDirectory(outDir);
            using var fs = File.Create(TurnBaseName(stamp) + ".wav");
            WavWriter.Write(fs, pcm, rate);
        }
        catch (Exception e) { _log.LogWarning(e, "link: could not write WAV to {Dir}", outDir); }
    }

    private void SaveTurnJson(TurnResult r, DateTime stamp)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            File.WriteAllText(TurnBaseName(stamp) + ".json", r.ToJson(), new UTF8Encoding(false));
        }
        catch (Exception e) { _log.LogWarning(e, "link: could not write turn JSON to {Dir}", outDir); }
    }

    private static bool TryParseJson(ReadOnlySpan<byte> utf8, out string? type, out JsonDocument? doc)
    {
        type = null; doc = null;
        try
        {
            doc = JsonDocument.Parse(utf8.ToArray());
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("type", out var t))
                type = t.GetString();
            return true;
        }
        catch (JsonException) { doc?.Dispose(); doc = null; return false; }
    }
}

/// <summary>Runs <see cref="LinkServer"/> inside the Generic Host.</summary>
public sealed class LinkHostedService(ILoggerFactory loggerFactory) : IHostedService
{
    private LinkServer? _server;

    public async Task StartAsync(CancellationToken ct)
    {
        var log = loggerFactory.CreateLogger<ShamashSettings>();
        var settings = ShamashSettings.LoadDefault(log);
        settings.LogPresence(log);
        ServiceStatus.Instance.SetConfig(settings.Describe());
        var pipeline = TurnPipeline.Create(settings, SharedHttp.Client, loggerFactory);
        var url = $"http://0.0.0.0:{LinkServer.PortFromEnvironment()}";
        _server = new LinkServer(loggerFactory, url, LinkServer.DefaultOutDir(), pipeline);
        try
        {
            await _server.StartAsync(ct);
            ServiceStatus.Instance.SetLink(url, null);
        }
        catch (Exception e)
        {
            // A busy port must not take down updates and heartbeat.
            loggerFactory.CreateLogger<LinkHostedService>().LogError(e, "link: failed to start");
            ServiceStatus.Instance.SetLink(null, e.Message);
            _server = null;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is { } s) { await s.StopAsync(ct); await s.DisposeAsync(); }
    }
}

/// <summary>One HttpClient for the life of the process (timeouts are per call).</summary>
public static class SharedHttp
{
    public static readonly HttpClient Client = new() { Timeout = Timeout.InfiniteTimeSpan };
}

/// <summary>Sends pipeline output over the phone's WebSocket.</summary>
public sealed class WebSocketSender(WebSocket ws) : ITurnSender
{
    public Task SendTextAsync(string json, CancellationToken ct) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) =>
        ws.SendAsync(data, WebSocketMessageType.Binary, true, ct).AsTask();
}
