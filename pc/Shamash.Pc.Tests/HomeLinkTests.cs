using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shamash.Pc;
using Xunit;

namespace Shamash.Pc.Tests;

public class HomeLinkTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "shamash-home-" + Guid.NewGuid().ToString("N"));
    public HomeLinkTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class FakeHandler(Func<HttpRequestMessage, byte[], HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Req, byte[] Body)> Calls { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(ct);
            Calls.Add((request, body));
            return respond(request, body);
        }
    }

    private static HttpResponseMessage Ok(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static ShamashSettings Settings() => new(null, null, "echo", "v", "m")
    {
        HomeUrl = "https://home.example", HomeEnrolToken = "secret-token", HomeHeartbeatMinutes = 5,
    };

    private const string Reply = """{"serverTime":"2026-11-27T13:08:00Z","desiredVersion":"0.1.21","hold":true,"commands":[{"id":"k1","type":"noop","args":{}}]}""";

    [Fact]
    public void SignHeaders_VerifyWithPublicKey()
    {
        var key = HomeKey.LoadOrCreate(_dir);
        var body = Encoding.UTF8.GetBytes("{\"a\":1}");
        var h = key.SignHeaders("c_1", body, DateTimeOffset.FromUnixTimeSeconds(1790000000));
        Assert.Equal("c_1", h["X-Shamash-Client"]);
        Assert.Equal("1790000000", h["X-Shamash-Timestamp"]);
        var expected = "1790000000\n" + Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant();
        using var pub = ECDsa.Create();
        pub.ImportFromPem(key.PublicKeyPem());
        Assert.True(pub.VerifyData(Encoding.UTF8.GetBytes(expected), Convert.FromBase64String(h["X-Shamash-Signature"]),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.StartsWith("-----BEGIN PUBLIC KEY-----", key.PublicKeyPem());
    }

    [Fact]
    public void Key_IsPersisted()
    {
        var a = HomeKey.LoadOrCreate(_dir).PublicKeyPem();
        Assert.Equal(a, HomeKey.LoadOrCreate(_dir).PublicKeyPem());
        Assert.StartsWith("-----BEGIN PRIVATE KEY-----", File.ReadAllText(Path.Combine(_dir, "home-key.pem")));
    }

    [Fact]
    public async Task Register_StoresClientId_AndIsSkippedWhenFileExists()
    {
        var h = new FakeHandler((r, b) => r.RequestUri!.AbsolutePath == "/api/register" ? Ok("""{"clientId":"c_abc"}""") : Ok(Reply));
        var svc = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir);
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.Equal("c_abc", JsonDocument.Parse(File.ReadAllText(Path.Combine(_dir, "home-client.json"))).RootElement.GetProperty("clientId").GetString());
        var reg = JsonDocument.Parse(h.Calls[0].Body).RootElement;
        Assert.Equal("secret-token", reg.GetProperty("enrolToken").GetString());
        Assert.Equal(Environment.MachineName, reg.GetProperty("name").GetString());
        Assert.Contains("PUBLIC KEY", reg.GetProperty("publicKeyPem").GetString());

        var h2 = new FakeHandler((r, b) => Ok(Reply));
        var svc2 = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h2, _dir);
        Assert.True(await svc2.TickAsync(Settings(), default));
        Assert.All(h2.Calls, c => Assert.Equal("/api/heartbeat", c.Req.RequestUri!.AbsolutePath));
    }

    [Fact]
    public async Task Heartbeat_SendsSignedHeaders_AndParsesReply()
    {
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_x","registeredAt":"2026-11-27T00:00:00Z"}""");
        var key = HomeKey.LoadOrCreate(_dir);
        var h = new FakeHandler((r, b) => Ok(Reply));
        var client = new HomeClient(new HttpClient(h), new Uri("https://home.example/"), _dir, key);
        var reply = await client.HeartbeatAsync(HomeLinkService.BuildBody(), default);
        Assert.Equal("0.1.21", reply.DesiredVersion);
        Assert.True(reply.Hold);
        Assert.Equal("k1", Assert.Single(reply.Commands).Id);

        var (req, body) = h.Calls.Single();
        Assert.Equal("c_x", req.Headers.GetValues("X-Shamash-Client").Single());
        var ts = req.Headers.GetValues("X-Shamash-Timestamp").Single();
        var sig = Convert.FromBase64String(req.Headers.GetValues("X-Shamash-Signature").Single());
        using var pub = ECDsa.Create();
        pub.ImportFromPem(key.PublicKeyPem());
        Assert.True(pub.VerifyData(HomeKey.Message(ts, body), sig, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        var json = JsonDocument.Parse(body).RootElement;
        Assert.True(json.TryGetProperty("uptimeSeconds", out _));
        Assert.Equal(JsonValueKind.Null, json.GetProperty("phone").ValueKind);
    }

    [Fact]
    public async Task Heartbeat401_ReregistersOnce()
    {
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_old"}""");
        var registers = 0; var beats = 0;
        var h = new FakeHandler((r, b) =>
        {
            if (r.RequestUri!.AbsolutePath == "/api/register") { registers++; return Ok("""{"clientId":"c_new"}"""); }
            beats++;
            return beats == 1 ? new HttpResponseMessage(HttpStatusCode.Unauthorized) : Ok(Reply);
        });
        var svc = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir);
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.Equal(1, registers);
        Assert.Equal(2, beats);
        Assert.Equal("c_new", h.Calls.Last().Req.Headers.GetValues("X-Shamash-Client").Single());

        // A second 401 right after re-registering is not retried again.
        var h2 = new FakeHandler((r, b) => r.RequestUri!.AbsolutePath == "/api/register" ? Ok("""{"clientId":"c_n2"}""") : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var svc2 = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h2, _dir);
        Assert.False(await svc2.TickAsync(Settings(), default));
        Assert.Equal(1, h2.Calls.Count(c => c.Req.RequestUri!.AbsolutePath == "/api/register"));
    }

    private static HttpResponseMessage Unauthorized(string json) =>
        new(HttpStatusCode.Unauthorized) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task StaleTimestamp401_AppliesOffsetAndRetriesOnce_WithoutReregistering()
    {
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_x"}""");
        var local = DateTimeOffset.FromUnixTimeSeconds(1790000000);
        var serverUnix = local.ToUnixTimeSeconds() + 7200;
        var beats = 0;
        var h = new FakeHandler((r, b) =>
        {
            if (r.RequestUri!.AbsolutePath == "/api/register") throw new InvalidOperationException("must not re-register");
            beats++;
            return beats == 1 ? Unauthorized($$"""{"error":"stale timestamp","serverTime":"x","serverUnix":{{serverUnix}}}""") : Ok("""{"desiredVersion":null,"hold":false,"commands":[]}""");
        });
        var svc = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir);
        var client = new HomeClient(new HttpClient(h), new Uri("https://home.example/"), _dir, HomeKey.LoadOrCreate(_dir), () => local);
        await client.HeartbeatAsync(HomeLinkService.BuildBody(), default);
        Assert.Equal(7200, client.ClockOffsetSeconds);
        Assert.Equal(2, h.Calls.Count);
        Assert.Equal("1790000000", h.Calls[0].Req.Headers.GetValues("X-Shamash-Timestamp").Single());
        Assert.Equal(serverUnix.ToString(), h.Calls[1].Req.Headers.GetValues("X-Shamash-Timestamp").Single());

        // Later calls keep using the offset.
        await client.HeartbeatAsync(HomeLinkService.BuildBody(), default);
        Assert.Equal(serverUnix.ToString(), h.Calls[2].Req.Headers.GetValues("X-Shamash-Timestamp").Single());

        // Through the service: succeeds, no registration call.
        beats = 0;
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.DoesNotContain(h.Calls, c => c.Req.RequestUri!.AbsolutePath == "/api/register");
    }

    [Fact]
    public async Task StaleTimestamp401_StillRefusedAfterOffset_DoesNotLoopOrReregister()
    {
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_x"}""");
        var h = new FakeHandler((r, b) => Unauthorized("""{"error":"stale timestamp","serverUnix":1790000000}"""));
        var svc = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir);
        Assert.False(await svc.TickAsync(Settings(), default));
        Assert.Equal(2, h.Calls.Count);
        Assert.True(File.Exists(Path.Combine(_dir, "home-client.json")));
    }

    [Fact]
    public async Task UnknownClient401_Reregisters()
    {
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_old"}""");
        var beats = 0; var registers = 0;
        var h = new FakeHandler((r, b) =>
        {
            if (r.RequestUri!.AbsolutePath == "/api/register") { registers++; return Ok("""{"clientId":"c_new"}"""); }
            return ++beats == 1 ? Unauthorized("""{"error":"unknown client"}""") : Ok(Reply);
        });
        var svc = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir);
        Assert.True(await svc.TickAsync(Settings(), default));
        Assert.Equal(1, registers);
        Assert.Equal(2, beats);
    }

    [Fact]
    public async Task ReplyServerTime_AdjustsOffsetOnlyBeyond60Seconds()
    {
        File.WriteAllText(Path.Combine(_dir, "home-client.json"), """{"clientId":"c_x"}""");
        var local = DateTimeOffset.FromUnixTimeSeconds(1790000000);
        string Reply(long unix) => $$"""{"serverTime":"{{DateTimeOffset.FromUnixTimeSeconds(unix):yyyy-MM-ddTHH:mm:ssZ}}","hold":false,"commands":[]}""";
        var next = Reply(local.ToUnixTimeSeconds() + 30);
        var h = new FakeHandler((r, b) => Ok(next));
        var client = new HomeClient(new HttpClient(h), new Uri("https://home.example/"), _dir, HomeKey.LoadOrCreate(_dir), () => local);
        await client.HeartbeatAsync(HomeLinkService.BuildBody(), default);
        Assert.Equal(0, client.ClockOffsetSeconds);
        next = Reply(local.ToUnixTimeSeconds() - 7200);
        await client.HeartbeatAsync(HomeLinkService.BuildBody(), default);
        Assert.Equal(-7200, client.ClockOffsetSeconds);
    }

    [Fact]
    public async Task NetworkFailure_DoesNotThrow()
    {
        var h = new FakeHandler((r, b) => throw new HttpRequestException("no route"));
        var svc = new HomeLinkService(NullLogger<HomeLinkService>.Instance, Settings(), h, _dir);
        Assert.False(await svc.TickAsync(Settings(), default));
    }

    [Fact]
    public void Settings_ReadHomeKeys()
    {
        var env = new Dictionary<string, string> { ["SHAMASH_HOME_URL"] = "https://h", ["SHAMASH_HOME_ENROL_TOKEN"] = "t", ["SHAMASH_HOME_HEARTBEAT_MINUTES"] = "2" };
        var s = ShamashSettings.Load(k => env.GetValueOrDefault(k), []);
        Assert.Equal(("https://h", "t", 2), (s.HomeUrl, s.HomeEnrolToken, s.HomeHeartbeatMinutes));
        Assert.Equal(5, ShamashSettings.Load(_ => null, []).HomeHeartbeatMinutes);
    }
}
