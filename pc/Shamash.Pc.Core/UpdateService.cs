using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Shamash.Pc;

/// <summary>Checks GitHub Releases for a new version and applies it inside the update window.</summary>
public sealed class UpdateService(ILogger<UpdateService> log) : BackgroundService
{
    private static readonly TimeSpan WindowPoll = TimeSpan.FromMinutes(5);

    /// <summary>Minutes between update checks: SHAMASH_UPDATE_CHECK_MINUTES, default 360 (6 h), minimum 1.</summary>
    internal static TimeSpan CheckInterval =>
        TimeSpan.FromMinutes(Math.Max(1, int.TryParse(Environment.GetEnvironmentVariable("SHAMASH_UPDATE_CHECK_MINUTES"), out var m) ? m : 360));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mgr = new UpdateManager(new GithubSource("https://github.com/BackSlasher/shamash", null, false));
        log.LogInformation("Shamash.Pc {Version} starting, velopackInstalled={Installed}, currentVersion={Current}",
            HeartbeatService.AppVersion, mgr.IsInstalled, mgr.IsInstalled ? mgr.CurrentVersion?.ToString() : "n/a");
        if (!mgr.IsInstalled)
        {
            log.LogInformation("not installed via Velopack, skipping updates");
            ServiceStatus.Instance.UpdateChecked("not installed via Velopack, no updates");
            return;
        }

        var window = UpdateWindow.Parse(Environment.GetEnvironmentVariable("SHAMASH_UPDATE_WINDOW"));
        var now = Environment.GetEnvironmentVariable("SHAMASH_UPDATE_NOW") == "1";
        log.LogInformation("Update window {Start}-{End} local time, applyImmediately={Now}, checkEvery={Interval}",
            window.Start, window.End, now, CheckInterval);

        var status = ServiceStatus.Instance;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var update = await mgr.CheckForUpdatesAsync();
                if (update != null)
                {
                    var v = update.TargetFullRelease.Version.ToString();
                    log.LogInformation("Update available: {Version}", v);
                    status.UpdateChecked($"downloading {v}", v);
                    await mgr.DownloadUpdatesAsync(update, cancelToken: stoppingToken);
                    log.LogInformation("Update {Version} downloaded", v);

                    if (!now && !window.Contains(TimeOnly.FromDateTime(DateTime.Now)))
                    {
                        log.LogInformation("Outside the update window, holding update {Version} until {Start}",
                            v, window.Start);
                        status.UpdateChecked($"{v} downloaded, waiting for the {window.Start:HH\\:mm}-{window.End:HH\\:mm} window", v);
                        // The tray menu's "Install update now" ends the wait early.
                        while (!window.Contains(TimeOnly.FromDateTime(DateTime.Now)) && !status.ApplyNowRequested)
                            await status.WaitForCheckRequestAsync(WindowPoll, stoppingToken);
                    }

                    log.LogInformation("Applying update {Version} and restarting", v);
                    status.UpdateChecked($"applying {v}", v);
                    mgr.ApplyUpdatesAndRestart(update);
                    return;
                }
                log.LogInformation("No update available");
                status.UpdateChecked("up to date");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Update check failed; will retry");
                status.UpdateChecked("check failed: " + ex.Message);
            }

            // Sleep until the next scheduled check, or until the tray menu asks for one.
            try { await status.WaitForCheckRequestAsync(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
