using Microsoft.Win32;
using Serilog;

namespace Shamash.Pc;

/// <summary>
/// Windows 11 puts new notification icons in the overflow flyout. Explorer records each one under
/// HKCU\Control Panel\NotifyIconSettings\&lt;id&gt; (ExecutablePath, IsPromoted); setting IsPromoted=1
/// shows ours on the taskbar. Only done when the value is missing (a fresh entry), so a user who
/// later hides the icon is not overridden.
/// </summary>
internal static class TrayPromotion
{
    private const string KeyPath = @"Control Panel\NotifyIconSettings";

    /// <summary>True once our entry was found (promoted now or already decided); false means try again.</summary>
    public static bool PromoteOnce()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return true;
            using var root = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (root is null) return false;
            foreach (var name in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(name, writable: true);
                if (sub is null) continue;
                if (!string.Equals(sub.GetValue("ExecutablePath") as string, exe, StringComparison.OrdinalIgnoreCase)) continue;
                if (sub.GetValue("IsPromoted") is null)
                {
                    sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                    Log.Information("tray: promoted icon");
                }
                return true;
            }
        }
        catch { /* best effort */ }
        return false;
    }
}
