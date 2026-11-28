using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>
/// Escape hatch for "nothing can do this": the model records what the user asked for, in his words, and tells him so.
/// The line goes to a local file only. No network here on purpose: a later slice uploads the turn records, which already
/// contain every tool call with its input, so the home server sees these requests through them.
/// </summary>
public sealed class ReportMissingFeatureTool(string dataDir, ILogger? log = null) : IAgentTool
{
    public const string FileName = "missing-features.jsonl";
    public const string Recorded = "recorded. Tell the user in one short sentence that this is not something you can do yet and that the request was noted for the maintainer.";

    private static readonly object FileLock = new();

    public string Name => "report_missing_feature";
    public string Description =>
        "Call this when the user asks for something that none of your tools can do. Records the request for the maintainer; it does not do the task. " +
        "request is what the user asked, in his own words. details (optional) is what you would have needed in order to do it.";
    public string InputSchema => """
        {"type":"object","properties":{"request":{"type":"string","description":"What the user asked for, in his words"},"details":{"type":"string","description":"What you would have needed to do it (optional)"}},"required":["request"]}
        """;

    public Task<string> RunAsync(JsonElement input, CancellationToken ct) => Task.Run(() =>
    {
        var request = ToolText.GetString(input, "request");
        if (string.IsNullOrWhiteSpace(request)) return "error: request is required";
        var details = ToolText.GetString(input, "details");
        var line = JsonSerializer.Serialize(new { time = DateTimeOffset.Now.ToString("o"), request, details }, TurnJson.Options);
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(dataDir);
                File.AppendAllText(Path.Combine(dataDir, FileName), line + "\n");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"error: could not record the request: {e.Message}";
        }
        log?.LogWarning("missing feature: {Request}", request);
        return Recorded;
    }, ct);
}
