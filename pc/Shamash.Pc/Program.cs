using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Shamash.Pc;
using Velopack;

// Must be first: handles Velopack install/update/uninstall hooks and may exit the process.
VelopackApp.Build().Run();

var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
var logDir = string.IsNullOrEmpty(baseDir)
    ? Path.Combine(AppContext.BaseDirectory, "logs")
    : Path.Combine(baseDir, "Shamash", "logs");

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
