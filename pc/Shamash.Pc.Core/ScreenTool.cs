using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>
/// look_at_screen: a screenshot the model sees, plus a text line naming the windows so it can follow up with describe_window.
/// The picture is also saved under pclink/screens (last 50) so the owner can see what the model saw.
/// </summary>
public sealed class LookAtScreenTool(IScreenCapture capture, IDesktopAutomation auto, string dataDir, ILogger? log = null, Func<DateTime>? now = null) : IRichAgentTool
{
    public const int KeepScreens = 50;
    public const int MaxOtherWindows = 10;

    public string Name => "look_at_screen";
    public string Description =>
        "Takes a screenshot of the whole screen and shows it to you, with the names of the windows that are open. " +
        "Use it when the user describes something on the screen he does not understand, or a tool failed for no clear reason. " +
        "For an alert that Word, Excel or Outlook is showing, describe_office_dialogs reads the exact text and buttons; use that first when Word is open. " +
        "Only for getting out of unexpected situations; do not use it to work in Word, Excel or Outlook.";
    public string InputSchema => """{"type":"object","properties":{}}""";

    public async Task<string> RunAsync(JsonElement input, CancellationToken ct) => (await RunRichAsync(input, ct)).Text;

    public Task<ToolResult> RunRichAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        ToolImage? image;
        try { image = capture.Capture(); }
        catch (Exception e) when (e is not OperationCanceledException) { return new ToolResult($"error: screen capture failed: {e.Message}"); }
        if (image is null) return new ToolResult("error: screen capture not available");

        var taken = (now ?? (() => DateTime.Now))();
        Save(image, taken);

        var sb = new StringBuilder(FormattableString.Invariant($"screenshot {image.Width}x{image.Height} of the whole screen taken at {taken:HH:mm}"));
        try
        {
            var front = auto.Foreground();
            sb.Append("; foreground window: ").Append(front is null ? "none" : DesktopText.Label(front, auto));
            var others = auto.Windows().Where(w => front is null || w.Handle != front.Handle).Take(MaxOtherWindows).ToList();
            if (others.Count > 0) sb.Append("; other visible windows: ").Append(string.Join(", ", others.Select(w => DesktopText.Label(w, auto))));
        }
        catch (Exception e) when (e is not OperationCanceledException) { log?.LogWarning(e, "look_at_screen: could not list windows"); }
        return new ToolResult(sb.ToString(), image);
    }, ct);

    private void Save(ToolImage image, DateTime taken)
    {
        try
        {
            var dir = Path.Combine(dataDir, "pclink", "screens");
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, $"{taken:yyyyMMdd-HHmmss}.png"), image.Data);
            foreach (var old in Directory.GetFiles(dir, "*.png").OrderByDescending(Path.GetFileName, StringComparer.Ordinal).Skip(KeepScreens))
                File.Delete(old);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.LogWarning(e, "look_at_screen: could not save the screenshot");
        }
    }
}
