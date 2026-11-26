using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Shamash.Pc;
using Velopack;

var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var logDir = string.IsNullOrEmpty(baseDir)
    ? Path.Combine(AppContext.BaseDirectory, "logs")
    : Path.Combine(baseDir, "Shamash", "logs");

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
var velopack = VelopackApp.Build();
if (OperatingSystem.IsWindows())
{
    velopack
        .OnAfterInstallFastCallback(v => { HookLog($"after-install {v}"); StartupTask.Register(HookLog); })
        .OnAfterUpdateFastCallback(v => { HookLog($"after-update {v}"); StartupTask.Register(HookLog, run: false); })
        .OnBeforeUninstallFastCallback(v => { HookLog($"before-uninstall {v}"); StartupTask.Unregister(HookLog); });
}
velopack.Run();

// One instance per user: the installer, the logon task and an update restart can all try to start us.
using var singleInstance = new Mutex(true, @"Local\Shamash.Pc.single-instance", out var firstInstance);
if (!firstInstance)
{
    Console.Error.WriteLine("Shamash.Pc is already running for this user; exiting.");
    return;
}

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console()
    .WriteTo.File(Path.Combine(logDir, "shamash-.log"),
        rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
    .CreateLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);
    builder.Services.AddSerilog();
    builder.Services.AddHostedService<UpdateService>();
    builder.Services.AddHostedService<HeartbeatService>();
    builder.Services.AddHostedService<LinkHostedService>();
    await builder.Build().RunAsync();
}
finally
{
    await Log.CloseAndFlushAsync();
}
