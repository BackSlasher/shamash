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
public sealed class LinkServer(ILoggerFactory loggerFactory, string url, string outDir) : IAsyncDisposable
{
    public const int DefaultPort = 8777;
    public const int MaxMessageBytes = 1 << 20;

    private readonly ILogger _log = loggerFactory.CreateLogger<LinkServer>();
    private WebApplication? _app;

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
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var msg = new MemoryStream();
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buf.AsMemory(), ct);
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
                            _log.LogInformation("link: hello from {Peer}: {Hello}", peer, Encoding.UTF8.GetString(msg.GetBuffer(), 0, (int)msg.Length));
                        }
                        else if (type == "end") goto done;
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
        _server = new LinkServer(loggerFactory, $"http://0.0.0.0:{LinkServer.PortFromEnvironment()}", LinkServer.DefaultOutDir());
        try { await _server.StartAsync(ct); }
        catch (Exception e)
        {
            // A busy port must not take down updates and heartbeat.
            loggerFactory.CreateLogger<LinkHostedService>().LogError(e, "link: failed to start");
            _server = null;
        }
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_server is { } s) { await s.StopAsync(ct); await s.DisposeAsync(); }
    }
}
