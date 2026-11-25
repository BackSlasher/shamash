using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Shamash.Pc;

/// <summary>Checks GitHub Releases for a new version and applies it inside the update window.</summary>
public sealed class UpdateService(ILogger<UpdateService> log) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(6);
    private static readonly TimeSpan WindowPoll = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mgr = new UpdateManager(new GithubSource("https://github.com/BackSlasher/shamash", null, false));
        if (!mgr.IsInstalled)
        {
            log.LogInformation("not installed via Velopack, skipping updates");
            return;
        }

        var window = UpdateWindow.Parse(Environment.GetEnvironmentVariable("SHAMASH_UPDATE_WINDOW"));
        var now = Environment.GetEnvironmentVariable("SHAMASH_UPDATE_NOW") == "1";

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var update = await mgr.CheckForUpdatesAsync();
                if (update != null)
                {
                    log.LogInformation("Update available: {Version}", update.TargetFullRelease.Version);
                    await mgr.DownloadUpdatesAsync(update, cancelToken: stoppingToken);
                    log.LogInformation("Update {Version} downloaded", update.TargetFullRelease.Version);

                    while (!now && !window.Contains(TimeOnly.FromDateTime(DateTime.Now)))
                        await Task.Delay(WindowPoll, stoppingToken);

                    log.LogInformation("Applying update {Version} and restarting", update.TargetFullRelease.Version);
                    mgr.ApplyUpdatesAndRestart(update);
                    return;
                }
                log.LogInformation("No update available");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Update check failed; will retry");
            }

            try { await Task.Delay(CheckInterval, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }
}
