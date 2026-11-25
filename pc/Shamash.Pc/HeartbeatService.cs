using System.Reflection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>Placeholder for the real service: logs the version once a minute.</summary>
public sealed class HeartbeatService(ILogger<HeartbeatService> log) : BackgroundService
{
    public static string AppVersion { get; } =
        typeof(HeartbeatService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            log.LogInformation("Shamash.Pc heartbeat, version {Version}", AppVersion);
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
