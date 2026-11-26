using System.Diagnostics;
using System.Xml.Linq;

namespace Shamash.Pc;

/// <summary>
/// The logon scheduled task that starts the service for the installing user. Registered by the
/// Velopack after-install hook, removed by the before-uninstall hook (Program.cs). A scheduled task
/// rather than a Startup-folder shortcut so there is no console window and no execution time limit.
/// Uses schtasks.exe with a task XML because schtasks' own switches cannot clear the 72 h limit.
/// </summary>
public static class StartupTask
{
    public const string TaskName = "Shamash.Pc";

    /// <summary>The Velopack stub next to <c>current\</c>; it relaunches the current version and exits.</summary>
    public static string StubExePath()
    {
        var stub = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Shamash.Pc.exe"));
        return File.Exists(stub) ? stub : Environment.ProcessPath ?? stub;
    }

    public static string TaskXml(string userId, string exePath, string workingDirectory)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "Starts the Shamash PC service at logon.")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "Delay", "PT10S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7")),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", exePath),
                        new XElement(ns + "WorkingDirectory", workingDirectory)))));
        return doc.Declaration + Environment.NewLine + doc.ToString();
    }

    /// <summary>Creates or replaces the task for the current user and, if <paramref name="run"/>, starts it. Returns schtasks output.</summary>
    public static string Register(Action<string>? log = null, bool run = true)
    {
        var exe = StubExePath();
        var user = Environment.UserDomainName + "\\" + Environment.UserName;
        var xmlPath = Path.Combine(Path.GetTempPath(), "Shamash.Pc.task.xml");
        File.WriteAllText(xmlPath, TaskXml(user, exe, Path.GetDirectoryName(exe)!), System.Text.Encoding.Unicode);
        try
        {
            var created = Schtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            log?.Invoke($"startup task: create -> {created}");
            if (run)
            {
                var started = Schtasks($"/Run /TN \"{TaskName}\"");
                log?.Invoke($"startup task: run -> {started}");
            }
            return created;
        }
        finally
        {
            try { File.Delete(xmlPath); } catch { /* best effort */ }
        }
    }

    public static string Unregister(Action<string>? log = null)
    {
        var r = Schtasks($"/Delete /TN \"{TaskName}\" /F");
        log?.Invoke($"startup task: delete -> {r}");
        return r;
    }

    private static string Schtasks(string arguments)
    {
        var psi = new ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("schtasks.exe did not start");
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(30_000);
        return $"exit {p.ExitCode}: {output.Trim()}";
    }
}
