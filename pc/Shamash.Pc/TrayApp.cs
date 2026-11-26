using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Extensions.Hosting;

namespace Shamash.Pc;

/// <summary>
/// The notification-area icon: the only thing the service shows by default. Its colour follows
/// <see cref="ServiceStatus.Overall"/>; the menu opens the status window, the data folders, an
/// update check, and Quit. Closing the status window hides it; only Quit stops the service.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly ToolStripMenuItem _title;
    private readonly ToolStripMenuItem _update;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Dictionary<ServiceStatus.Health, Icon> _icons = new();
    private readonly string _dataDir;
    private StatusForm? _form;
    private ServiceStatus.Health? _shown;
    private int _promoteTries;

    public TrayApp(string dataDir, IHostApplicationLifetime lifetime)
    {
        _dataDir = dataDir;
        var status = ServiceStatus.Instance;

        _title = new ToolStripMenuItem($"Shamash {ShortVersion(status.Version)}") { Enabled = false };
        _update = new ToolStripMenuItem("Check for updates", null, (_, _) => status.RequestUpdateCheck());
        _menu.Items.Add(_title);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Status…", null, (_, _) => ShowStatus()));
        _menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(Path.Combine(_dataDir, "logs"))));
        _menu.Items.Add(new ToolStripMenuItem("Open recordings folder", null, (_, _) => OpenFolder(Path.Combine(_dataDir, "pclink"))));
        _menu.Items.Add(_update);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("Quit Shamash", null, (_, _) => Quit()));

        _icon = new NotifyIcon
        {
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowStatus();
        Refresh();
        TryPromote();
        _timer.Tick += (_, _) => { Refresh(); TryPromote(); };
        _timer.Start();

        // The host stopping for any other reason (e.g. an update restart) ends the message loop too.
        lifetime.ApplicationStopping.Register(() =>
        {
            try { _menu.BeginInvoke(ExitThread); } catch (InvalidOperationException) { ExitThread(); }
        });
    }

    /// <summary>Explorer creates our registry entry shortly after the icon appears; try for ~30 s.</summary>
    private void TryPromote()
    {
        if (_promoteTries >= 30) return;
        _promoteTries = TrayPromotion.PromoteOnce() ? 30 : _promoteTries + 1;
    }

    private void Refresh()
    {
        var status = ServiceStatus.Instance;
        var health = status.Overall;
        if (health != _shown)
        {
            _shown = health;
            _icon.Icon = IconFor(health);
        }
        var text = health switch
        {
            ServiceStatus.Health.Starting => "Shamash: starting",
            ServiceStatus.Health.Busy => "Shamash: answering",
            ServiceStatus.Health.Error => "Shamash: " + (status.LinkError ?? status.LastTurnSummary ?? "error"),
            _ => "Shamash: ready",
        };
        _icon.Text = text.Length > 63 ? text[..63] : text; // NotifyIcon limit
        _update.Text = status.PendingUpdateVersion is { } v ? $"Install update {v} now" : "Check for updates";
    }

    private void ShowStatus()
    {
        if (_form is null || _form.IsDisposed) _form = new StatusForm(_dataDir);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    private void Quit()
    {
        _timer.Stop();
        _icon.Visible = false;
        ExitThread();
    }

    private static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private Icon IconFor(ServiceStatus.Health health)
    {
        if (_icons.TryGetValue(health, out var cached)) return cached;
        var color = health switch
        {
            ServiceStatus.Health.Starting => Color.Gray,
            ServiceStatus.Health.Busy => Color.DarkOrange,
            ServiceStatus.Health.Error => Color.Firebrick,
            _ => Color.SeaGreen,
        };
        var icon = DrawIcon(color);
        _icons[health] = icon;
        return icon;
    }

    /// <summary>A filled disc with a white "ש" (shin), drawn at runtime so no .ico asset is needed.</summary>
    private static Icon DrawIcon(Color color)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 1, 1, 30, 30);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("ש", font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static string ShortVersion(string v)
    {
        var plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _icon.Dispose();
            _menu.Dispose();
            _form?.Dispose();
        }
        base.Dispose(disposing);
    }
}
