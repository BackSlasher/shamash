using System.Diagnostics;
using System.Text;

namespace Shamash.Pc;

/// <summary>
/// The status window: a block of labels fed by <see cref="ServiceStatus"/> and the recent log
/// lines from <see cref="MemoryLogSink"/>. Closing hides it; the tray keeps one instance.
/// </summary>
internal sealed class StatusForm : Form
{
    private readonly string _dataDir;
    private readonly Label _info = new() { Dock = DockStyle.Fill, AutoSize = false, RightToLeft = RightToLeft.No };
    private readonly TextBox _log = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false,
        Dock = DockStyle.Fill, Font = new Font("Consolas", 9f), RightToLeft = RightToLeft.No,
    };
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private int _lineCount = -1;
    private string? _lastLine;

    public StatusForm(string dataDir)
    {
        _dataDir = dataDir;
        Text = "Shamash status";
        ClientSize = new Size(700, 500);
        StartPosition = FormStartPosition.CenterScreen;
        RightToLeft = RightToLeft.No;

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 38, Padding = new Padding(4) };
        buttons.Controls.Add(MakeButton("Open log folder", OpenLogFolder));
        buttons.Controls.Add(MakeButton("Check for updates", () => ServiceStatus.Instance.RequestUpdateCheck()));
        buttons.Controls.Add(MakeButton("Copy diagnostics", CopyDiagnostics));

        var top = new Panel { Dock = DockStyle.Top, Height = 170, Padding = new Padding(8) };
        top.Controls.Add(_info);

        Controls.Add(_log);
        Controls.Add(buttons);
        Controls.Add(top);

        _timer.Tick += (_, _) => RefreshAll();
        _timer.Start();
        RefreshAll();
    }

    private static Button MakeButton(string text, Action onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += (_, _) => onClick();
        return b;
    }

    private string InfoText()
    {
        var s = ServiceStatus.Instance;
        var sb = new StringBuilder();
        sb.AppendLine($"Version: {s.Version}");
        sb.AppendLine($"Started: {s.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine(s.LinkError is not null ? $"Link error: {s.LinkError}" : $"Link: {s.LinkUrl ?? "not listening yet"}");
        sb.AppendLine(PhoneBattery.StatusLine(s.PhoneBattery));
        sb.AppendLine($"Config: {s.ConfigSummary ?? "-"}");
        sb.AppendLine($"Active turns: {s.ActiveTurns}");
        sb.AppendLine($"Last turn: {(s.LastTurnAt is { } t ? t.ToString("HH:mm:ss") : "none")} {s.LastTurnSummary}");
        sb.AppendLine($"Last update check: {(s.LastUpdateCheck is { } u ? u.ToString("yyyy-MM-dd HH:mm:ss") : "never")}; {s.UpdateState}"
            + (s.PendingUpdateVersion is { } p ? $"; pending {p}" : ""));
        sb.AppendLine($"Home: {(s.HomeLastContact is { } h ? h.ToString("HH:mm:ss") : "no contact")}; desired {s.HomeDesiredVersion ?? "-"}{(s.HomeHold ? ", HOLD" : "")}; {s.HomeQueuedTurns} turns queued, last upload {(s.HomeLastUpload is { } up ? up.ToString("HH:mm:ss") : "never")}");
        return sb.ToString().TrimEnd();
    }

    private void RefreshAll()
    {
        _info.Text = InfoText();
        var lines = MemoryLogSink.Instance.Snapshot();
        var last = lines.Length > 0 ? lines[^1] : null;
        if (lines.Length == _lineCount && last == _lastLine) return;
        _lineCount = lines.Length;
        _lastLine = last;
        _log.Text = string.Join(Environment.NewLine, lines);
        _log.SelectionStart = _log.TextLength;
        _log.ScrollToCaret();
    }

    private void OpenLogFolder()
    {
        try
        {
            var path = Path.Combine(_dataDir, "logs");
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { /* best effort */ }
    }

    private void CopyDiagnostics()
    {
        try { Clipboard.SetText(InfoText() + Environment.NewLine + Environment.NewLine + _log.Text); }
        catch { /* clipboard may be busy */ }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }
        base.OnFormClosing(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _timer.Dispose();
        base.Dispose(disposing);
    }
}
