using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>The PC's identity toward the home server: an ECDSA P-256 key, PKCS#8 PEM on disk (docs/home-protocol.md).</summary>
public sealed class HomeKey
{
    private readonly ECDsa _key;
    private HomeKey(ECDsa key) => _key = key;

    public static string DefaultDir()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrEmpty(baseDir) ? Path.Combine(AppContext.BaseDirectory, "data") : Path.Combine(baseDir, "Shamash");
    }

    /// <summary>Loads home-key.pem from <paramref name="dir"/>, creating it (user-only access) when missing.</summary>
    public static HomeKey LoadOrCreate(string dir)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "home-key.pem");
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        if (File.Exists(path))
        {
            key.ImportFromPem(File.ReadAllText(path));
            return new HomeKey(key);
        }
        var pem = key.ExportPkcs8PrivateKeyPem();
        // Create the file empty and restrict it first, then write the secret into it.
        var opts = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) opts.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var fs = new FileStream(path, opts))
        {
            RestrictToCurrentUser(path);
            fs.Write(Encoding.ASCII.GetBytes(pem));
        }
        return new HomeKey(key);
    }

    /// <summary>On Windows: drop inherited ACEs and grant only the current user. Elsewhere the 0600 create mode already did it.</summary>
    private static void RestrictToCurrentUser(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var fi = new FileInfo(path);
            var sec = fi.GetAccessControl();
            sec.SetAccessRuleProtection(true, false);
            foreach (System.Security.AccessControl.FileSystemAccessRule r in sec.GetAccessRules(true, false, typeof(System.Security.Principal.SecurityIdentifier)))
                sec.RemoveAccessRule(r);
            var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            sec.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(me,
                System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            fi.SetAccessControl(sec);
        }
        catch
        {
            // Best effort: the default ACL of %LocalAppData% already limits the file to the user, SYSTEM and administrators.
        }
    }

    /// <summary>SubjectPublicKeyInfo PEM, as sent at registration.</summary>
    public string PublicKeyPem() => _key.ExportSubjectPublicKeyInfoPem();

    /// <summary>The message is "{unixSeconds}\n{sha256hex(body)}"; the signature is DER ECDSA-SHA256, base64.</summary>
    public (string Timestamp, string Signature) Sign(byte[] body, DateTimeOffset now)
    {
        var ts = now.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var sig = _key.SignData(Message(ts, body), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return (ts, Convert.ToBase64String(sig));
    }

    public static byte[] Message(string timestamp, byte[] body) =>
        Encoding.UTF8.GetBytes($"{timestamp}\n{Convert.ToHexString(SHA256.HashData(body)).ToLowerInvariant()}");

    public Dictionary<string, string> SignHeaders(string clientId, byte[] body, DateTimeOffset now)
    {
        var (ts, sig) = Sign(body, now);
        return new()
        {
            ["X-Shamash-Client"] = clientId,
            ["X-Shamash-Timestamp"] = ts,
            ["X-Shamash-Signature"] = sig,
        };
    }
}

public sealed record HomeCommand(string Id, string Type, JsonElement? Args);

public sealed record HeartbeatReply(string? DesiredVersion, bool Hold, IReadOnlyList<HomeCommand> Commands);

public sealed record HeartbeatUsage(int Calls, long Input, long CacheWrite, long CacheRead, long Output);
public sealed record HeartbeatError(DateTime At, string Stage, string Message);
public sealed record HeartbeatMachine(string Name, string Os, bool OfficeInstalled, bool? IndexOk);

public sealed record HeartbeatBody(
    string Version, long UptimeSeconds, string Health, DateTime? LastTurnAt, int TurnsToday,
    HeartbeatUsage? UsageToday, HeartbeatError? LastError, object? Phone, HeartbeatMachine? Machine);

public sealed class HomeAuthException(string message) : Exception(message);

/// <summary>The server still rejects the timestamp after the clock offset was applied; not a registration problem.</summary>
public sealed class HomeClockException(string message) : Exception(message);

/// <summary>HTTP client for the home server. Does not retry; <see cref="HomeLinkService"/> owns the schedule.</summary>
public sealed class HomeClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    private readonly HttpClient _http;
    private readonly HomeKey _key;
    private readonly string _clientFile;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger? _log;

    public HomeClient(HttpClient http, Uri baseUri, string dir, HomeKey key, Func<DateTimeOffset>? clock = null, ILogger? log = null)
    {
        _log = log;
        _http = http;
        _key = key;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        BaseUri = baseUri;
        _clientFile = Path.Combine(dir, "home-client.json");
        ClientId = ReadClientId();
    }

    public Uri BaseUri { get; }
    public string? ClientId { get; private set; }

    /// <summary>Server unix time minus local unix time, applied to every signature so a wrong PC clock cannot break the link.</summary>
    public long ClockOffsetSeconds { get; private set; }

    private string? ReadClientId()
    {
        try
        {
            if (!File.Exists(_clientFile)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_clientFile));
            return doc.RootElement.TryGetProperty("clientId", out var p) && p.ValueKind == JsonValueKind.String && p.GetString() is { Length: > 0 } id ? id : null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Forgets the stored registration (the server said 401).</summary>
    public void ForgetRegistration()
    {
        ClientId = null;
        try { File.Delete(_clientFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Registers with the enrolment token and stores home-client.json. Throws on 403 and other failures.</summary>
    public async Task RegisterAsync(string enrolToken, string name, string version, CancellationToken ct)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(new { enrolToken, publicKeyPem = _key.PublicKeyPem(), name, version }, Json);
        using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "api/register")) { Content = JsonBody(body) };
        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.Forbidden) throw new HomeAuthException("registration refused (403, wrong enrolment token?)");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsByteArrayAsync(ct));
        var id = doc.RootElement.TryGetProperty("clientId", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(id)) throw new InvalidDataException("register reply has no clientId");
        ClientId = id;
        File.WriteAllText(_clientFile, JsonSerializer.Serialize(new { clientId = id, registeredAt = _clock().UtcDateTime.ToString("o") }));
    }

    /// <summary>
    /// Signed heartbeat. A 401 carrying serverUnix (stale timestamp) sets the clock offset and is retried once at once;
    /// any other 401 (unknown, revoked or bad signature) throws <see cref="HomeAuthException"/>.
    /// </summary>
    public async Task<HeartbeatReply> HeartbeatAsync(HeartbeatBody hb, CancellationToken ct)
    {
        if (ClientId is null) throw new InvalidOperationException("not registered");
        var body = JsonSerializer.SerializeToUtf8Bytes(hb, Json);
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, "api/heartbeat")) { Content = JsonBody(body) };
            foreach (var (k, v) in _key.SignHeaders(ClientId, body, _clock().AddSeconds(ClockOffsetSeconds))) req.Headers.TryAddWithoutValidation(k, v);
            using var resp = await _http.SendAsync(req, ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized)
            {
                var serverUnix = ReadServerUnix(await resp.Content.ReadAsByteArrayAsync(ct));
                if (serverUnix is null) throw new HomeAuthException("heartbeat refused (401)");
                if (attempt > 0) throw new HomeClockException("heartbeat refused (401) even with the server clock applied");
                ClockOffsetSeconds = serverUnix.Value - _clock().ToUnixTimeSeconds();
                _log?.LogInformation("home: clock offset {Offset} s (server time used for signing)", ClockOffsetSeconds);
                continue;
            }
            resp.EnsureSuccessStatusCode();
            return ParseReply(await resp.Content.ReadAsByteArrayAsync(ct));
        }
    }

    private static long? ReadServerUnix(byte[] body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("serverUnix", out var p)
                && p.ValueKind == JsonValueKind.Number && p.TryGetInt64(out var v) ? v : null;
        }
        catch (JsonException) { return null; }
    }

    private HeartbeatReply ParseReply(byte[] bytes)
    {
        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;
        if (root.TryGetProperty("serverTime", out var st) && st.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(st.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal, out var server))
        {
            var local = _clock().ToUnixTimeSeconds();
            var diff = server.ToUnixTimeSeconds() - (local + ClockOffsetSeconds);
            if (Math.Abs(diff) > 60) ClockOffsetSeconds = server.ToUnixTimeSeconds() - local;
        }
        string? desired = root.TryGetProperty("desiredVersion", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        var hold = root.TryGetProperty("hold", out var h) && h.ValueKind == JsonValueKind.True;
        var cmds = new List<HomeCommand>();
        if (root.TryGetProperty("commands", out var c) && c.ValueKind == JsonValueKind.Array)
            foreach (var e in c.EnumerateArray())
                cmds.Add(new HomeCommand(
                    e.TryGetProperty("id", out var i) ? i.GetString() ?? "" : "",
                    e.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "",
                    e.TryGetProperty("args", out var a) ? a.Clone() : null));
        return new HeartbeatReply(desired, hold, cmds);
    }

    private static ByteArrayContent JsonBody(byte[] body)
    {
        var c = new ByteArrayContent(body);
        c.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return c;
    }
}

/// <summary>
/// Dials home: registers lazily, then heartbeats at startup and every HomeHeartbeatMinutes. Nothing here can
/// block or fail a turn; every exception is caught and logged at warning, one line per state change.
/// </summary>
public sealed class HomeLinkService : BackgroundService
{
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private readonly ILogger<HomeLinkService> _log;
    private readonly ShamashSettings? _settings;
    private readonly HttpMessageHandler? _handler;
    private readonly string? _dir;
    private HomeClient? _client;
    private bool? _lastOk;

    public HomeLinkService(ILogger<HomeLinkService> log) => _log = log;

    public HomeLinkService(ILogger<HomeLinkService> log, ShamashSettings settings, HttpMessageHandler? handler, string dir)
    {
        _log = log; _settings = settings; _handler = handler; _dir = dir;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        try
        {
            var settings = _settings ?? ShamashSettings.LoadDefault();
            if (string.IsNullOrEmpty(settings.HomeUrl)) { _log.LogInformation("home: homeUrl not set, home link disabled"); return; }
            var interval = TimeSpan.FromMinutes(Math.Max(1, settings.HomeHeartbeatMinutes));
            var failures = 0;
            while (!ct.IsCancellationRequested)
            {
                var ok = await TickAsync(settings, ct);
                failures = ok ? 0 : failures + 1;
                var delay = ok ? interval : TimeSpan.FromSeconds(Math.Min(MaxBackoff.TotalSeconds, 30 * Math.Pow(2, Math.Min(failures - 1, 10))));
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { _log.LogWarning("home: link stopped: {Error}", e.GetType().Name + ": " + e.Message); }
    }

    /// <summary>One registration-if-needed plus heartbeat. Never throws; returns success.</summary>
    public async Task<bool> TickAsync(ShamashSettings settings, CancellationToken ct)
    {
        try
        {
            _client ??= CreateClient(settings);
            if (_client.ClientId is null) await RegisterAsync(settings, ct);
            HeartbeatReply reply;
            try { reply = await _client.HeartbeatAsync(BuildBody(), ct); }
            catch (HomeAuthException)
            {
                // Revoked or lost on the server: register again once, then retry the heartbeat once.
                _log.LogWarning("home: heartbeat refused with 401, registering again");
                _client.ForgetRegistration();
                await RegisterAsync(settings, ct);
                reply = await _client.HeartbeatAsync(BuildBody(), ct);
            }
            ServiceStatus.Instance.HomeReply(reply.DesiredVersion, reply.Hold);
            Changed(true, "home: heartbeat ok (desired {0}, hold {1})", reply.DesiredVersion ?? "-", reply.Hold);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return false; }
        catch (Exception e)
        {
            Changed(false, "home: link failed: {0}", e.GetType().Name + ": " + Scrub(e.Message, settings));
            return false;
        }
    }

    private async Task RegisterAsync(ShamashSettings settings, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(settings.HomeEnrolToken)) throw new InvalidOperationException("not registered and homeEnrolToken is not set");
        await _client!.RegisterAsync(settings.HomeEnrolToken, Environment.MachineName, HeartbeatService.AppVersion, ct);
        _log.LogInformation("home: registered as {ClientId}", _client.ClientId);
    }

    private HomeClient CreateClient(ShamashSettings settings)
    {
        var dir = _dir ?? HomeKey.DefaultDir();
        var key = HomeKey.LoadOrCreate(dir);
        var http = _handler is null ? SharedHttp.Client : new HttpClient(_handler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(30) };
        var url = settings.HomeUrl!.TrimEnd('/') + "/";
        return new HomeClient(http, new Uri(url), dir, key, null, _log);
    }

    // One log line per change between ok and failed (failure text changes do not repeat the line).
    private void Changed(bool ok, string format, params object[] args)
    {
        if (_lastOk == ok) return;
        _lastOk = ok;
        var msg = string.Format(format, args);
        if (ok) _log.LogInformation("{Message}", msg); else _log.LogWarning("{Message}", msg);
    }

    private static string Scrub(string message, ShamashSettings s) =>
        string.IsNullOrEmpty(s.HomeEnrolToken) ? message : message.Replace(s.HomeEnrolToken, "***");

    public static HeartbeatBody BuildBody()
    {
        var st = ServiceStatus.Instance;
        var u = st.UsageToday;
        return new HeartbeatBody(
            st.Version,
            (long)(DateTime.Now - st.StartedAt).TotalSeconds,
            st.Overall.ToString().ToLowerInvariant(),
            st.LastTurnAt?.ToUniversalTime(),
            st.TurnsToday,
            u.Calls > 0 ? new HeartbeatUsage(u.Calls, u.Input, u.CacheWrite, u.CacheRead, u.Output) : null,
            st.LastError is { } e ? new HeartbeatError(e.At.ToUniversalTime(), e.Stage, e.Message) : null,
            null,
            new HeartbeatMachine(Environment.MachineName, RuntimeInformation.OSDescription, OfficeInstalled(), null));
    }

    private static bool OfficeInstalled()
    {
        try { return OperatingSystem.IsWindows() && Type.GetTypeFromProgID("Word.Application") is not null; }
        catch { return false; }
    }
}
