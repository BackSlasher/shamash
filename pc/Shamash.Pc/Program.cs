using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Velopack;

namespace Shamash.Pc;

/// <summary>
/// Windows host: no console window; the Generic Host (updates, heartbeat, link server) runs on
/// background threads while the main thread owns the WinForms message loop for the tray icon.
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var dataDir = string.IsNullOrEmpty(baseDir) ? AppContext.BaseDirectory : Path.Combine(baseDir, "Shamash");
        var logDir = Path.Combine(dataDir, "logs");

        // Hooks run before Serilog exists and the process exits right after them, so they append to a plain file.
        void HookLog(string line)
        {
            try
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "install.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
            }
            catch { /* best effort */ }
        }

        // Must be first: handles Velopack install/update/uninstall hooks and may exit the process.
        // Install registers the logon scheduled task that keeps the service running; uninstall removes it.
        VelopackApp.Build()
            .OnAfterInstallFastCallback(v => { HookLog($"after-install {v}"); StartupTask.Register(HookLog); })
            .OnAfterUpdateFastCallback(v => { HookLog($"after-update {v}"); StartupTask.Register(HookLog, run: false); })
            .OnBeforeUninstallFastCallback(v => { HookLog($"before-uninstall {v}"); StartupTask.Unregister(HookLog); })
            .Run();

        // One instance per user: the installer, the logon task and an update restart can all try to start us.
        using var singleInstance = new Mutex(true, @"Local\Shamash.Pc.single-instance", out var firstInstance);
        if (!firstInstance) return 0;

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(logDir, "shamash-.log"),
                rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .WriteTo.Sink(MemoryLogSink.Instance)
            .CreateLogger();

        try
        {
            var builder = Host.CreateApplicationBuilder(args);
            builder.Services.AddSerilog();
            builder.Services.AddSingleton(sp => new DesktopServices(
                new WindowsScreenCapture(),
                new UiaDesktopAutomation(sp.GetRequiredService<ILoggerFactory>().CreateLogger<UiaDesktopAutomation>()),
                dataDir));
            builder.Services.AddHostedService<UpdateService>();
            builder.Services.AddHostedService<HeartbeatService>();
            builder.Services.AddHostedService<LinkHostedService>();
            builder.Services.AddHostedService<HomeLinkService>();
            using var host = builder.Build();
            host.Start();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            using var tray = new TrayApp(dataDir, host.Services.GetRequiredService<IHostApplicationLifetime>());
            Application.Run(tray);

            Log.Information("Quit from the tray menu; stopping");
            host.StopAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception e)
        {
            Log.Fatal(e, "Shamash.Pc crashed");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
