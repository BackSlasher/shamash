namespace Shamash.Pc;

/// <summary>
/// What the tray icon and the status window show. Written by the services, read by the UI on a
/// timer; one process-wide instance because the link server is built outside DI.
/// </summary>
public sealed class ServiceStatus
{
    public static ServiceStatus Instance { get; } = new();

    private readonly object _lock = new();
    private readonly SemaphoreSlim _checkNow = new(0, 1);

    public string Version => HeartbeatService.AppVersion;
    public DateTime StartedAt { get; } = DateTime.Now;

    public string? LinkUrl { get; private set; }
    public string? LinkError { get; private set; }
    public string? ConfigSummary { get; private set; }

    public int ActiveTurns { get; private set; }
    public DateTime? LastTurnAt { get; private set; }
    public string? LastTurnSummary { get; private set; }
    public bool LastTurnFailed { get; private set; }

    public DateTime? LastUpdateCheck { get; private set; }
    public string UpdateState { get; private set; } = "not checked yet";
    /// <summary>Set while a downloaded update waits for the window; "Install update now" clears the wait.</summary>
    public string? PendingUpdateVersion { get; private set; }
    public bool ApplyNowRequested { get; private set; }

    // Per-day counters (local midnight reset) for the home heartbeat.
    public int TurnsToday { get { lock (_lock) { RollDay(); return _turnsToday; } } }
    public LlmUsage UsageToday { get { lock (_lock) { RollDay(); return _usageToday; } } }
    private int _turnsToday;
    private LlmUsage _usageToday = LlmUsage.Zero;
    private DateOnly _day = DateOnly.FromDateTime(DateTime.Now);

    public sealed record ErrorInfo(DateTime At, string Stage, string Message);
    public ErrorInfo? LastError { get; private set; }

    // Home link (docs/home-protocol.md): last server reply, for the status window and update steering.
    public DateTime? HomeLastContact { get; private set; }
    public string? HomeDesiredVersion { get; private set; }
    public bool HomeHold { get; private set; }
    public int HomeQueuedTurns { get; private set; }
    public DateTime? HomeLastUpload { get; private set; }

    /// <summary>Turns still waiting for upload; <paramref name="uploaded"/> marks that something was just sent.</summary>
    public void HomeUpload(int queuedTurns, bool uploaded)
    {
        lock (_lock) { HomeQueuedTurns = queuedTurns; if (uploaded) HomeLastUpload = DateTime.Now; }
    }

    public void HomeReply(string? desiredVersion, bool hold)
    {
        lock (_lock) { HomeLastContact = DateTime.Now; HomeDesiredVersion = desiredVersion; HomeHold = hold; }
    }

    private void RollDay()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (today == _day) return;
        _day = today; _turnsToday = 0; _usageToday = LlmUsage.Zero;
    }

    public enum Health { Starting, Idle, Busy, Error }

    public Health Overall
    {
        get
        {
            lock (_lock)
            {
                if (LinkError is not null) return Health.Error;
                if (LinkUrl is null) return Health.Starting;
                if (ActiveTurns > 0) return Health.Busy;
                return LastTurnFailed ? Health.Error : Health.Idle;
            }
        }
    }

    public void SetLink(string? url, string? error)
    {
        lock (_lock) { LinkUrl = url; LinkError = error; }
    }

    public void SetConfig(string summary)
    {
        lock (_lock) ConfigSummary = summary;
    }

    public void TurnStarted()
    {
        lock (_lock) ActiveTurns++;
    }

    public void TurnEnded(TurnResult? r)
    {
        lock (_lock)
        {
            ActiveTurns = Math.Max(0, ActiveTurns - 1);
            LastTurnAt = DateTime.Now;
            RollDay();
            if (r is not null)
            {
                _turnsToday++;
                _usageToday += r.Usage;
                if (r.ErrorStage is not null) LastError = new ErrorInfo(DateTime.Now, r.ErrorStage, Clip(r.ErrorMessage, 300));
            }
            if (r is null) { LastTurnSummary = "closed before end"; LastTurnFailed = false; return; }
            LastTurnFailed = r.ErrorStage is not null;
            LastTurnSummary = r.ErrorStage is not null
                ? $"error at {r.ErrorStage}: {r.ErrorMessage}"
                : r.CancelledAt is not null
                    ? $"cancelled at {r.CancelledAt}"
                    : $"\"{Clip(r.Transcript, 60)}\" -> \"{Clip(r.Reply, 60)}\" (stt {r.SttMs} ms, llm {r.LlmMs} ms, {r.ToolCalls.Count} tool calls, tts {r.TtsTotalMs} ms, total {r.TotalMs} ms)";
        }
    }

    public void UpdateChecked(string state, string? pendingVersion = null)
    {
        lock (_lock)
        {
            LastUpdateCheck = DateTime.Now;
            UpdateState = state;
            PendingUpdateVersion = pendingVersion;
            if (pendingVersion is null) ApplyNowRequested = false;
        }
    }

    /// <summary>Tray menu: check now (wakes the update loop) or, with a pending update, apply it now.</summary>
    public void RequestUpdateCheck()
    {
        lock (_lock) { if (PendingUpdateVersion is not null) ApplyNowRequested = true; }
        if (_checkNow.CurrentCount == 0) { try { _checkNow.Release(); } catch (SemaphoreFullException) { } }
    }

    /// <summary>Home server's check_update command: wakes the update loop without asking it to apply a pending update.</summary>
    public void WakeUpdateLoop()
    {
        if (_checkNow.CurrentCount == 0) { try { _checkNow.Release(); } catch (SemaphoreFullException) { } }
    }

    /// <summary>Waits up to <paramref name="timeout"/>; returns early when a check was requested.</summary>
    public Task<bool> WaitForCheckRequestAsync(TimeSpan timeout, CancellationToken ct) => _checkNow.WaitAsync(timeout, ct);

    private static string Clip(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.ReplaceLineEndings(" ");
        return s.Length <= max ? s : s[..max] + "…";
    }
}
