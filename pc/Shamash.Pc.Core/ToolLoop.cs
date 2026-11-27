using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>A capability the model can call. Errors are returned as text "error: ...", not thrown.</summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    /// <summary>JSON schema of the input object (type "object" with properties and optional required).</summary>
    string InputSchema { get; }
    Task<string> RunAsync(JsonElement input, CancellationToken ct);
}

/// <summary>One executed tool call, kept in the turn record.</summary>
public sealed record ToolCallRecord(string Name, string Input, long Ms, int ResultChars, string ResultPrefix = "");

public sealed record ToolUseRequest(string Id, string Name, JsonElement Input);

/// <summary>One model response: the text and the tool calls it asked for. Raw is the provider's own content, handed back to the conversation.</summary>
public sealed record ModelStep(string Text, IReadOnlyList<ToolUseRequest> ToolUses, object? Raw = null, LlmUsage? Usage = null);

/// <summary>Token counts of one or more model calls (Anthropic usage: input excludes cached tokens).</summary>
public sealed record LlmUsage(int Calls, long Input, long CacheWrite, long CacheRead, long Output)
{
    public static readonly LlmUsage Zero = new(0, 0, 0, 0, 0);
    public static LlmUsage operator +(LlmUsage a, LlmUsage b) =>
        new(a.Calls + b.Calls, a.Input + b.Input, a.CacheWrite + b.CacheWrite, a.CacheRead + b.CacheRead, a.Output + b.Output);
    public override string ToString() => $"calls={Calls} input={Input} cacheWrite={CacheWrite} cacheRead={CacheRead} output={Output}";
}

/// <summary>
/// Per-turn usage accumulator. <see cref="Begin"/> installs a fresh one in an AsyncLocal, so every model call made
/// under that turn (the tool loop and ClaudeTranslator, which runs inside a tool) adds to it without any plumbing
/// through the tool interfaces. Calls outside a turn are ignored.
/// </summary>
public sealed class UsageScope
{
    private static readonly AsyncLocal<UsageScope?> Current = new();
    private LlmUsage _total = LlmUsage.Zero;
    private readonly object _lock = new();

    public LlmUsage Total { get { lock (_lock) return _total; } }

    public static UsageScope Begin() { var s = new UsageScope(); Current.Value = s; return s; }

    public static void Record(LlmUsage? u)
    {
        var s = Current.Value;
        if (s is null || u is null) return;
        lock (s._lock) s._total += u;
    }
}

/// <summary>The "call the model" side of the loop, so the loop can be tested without the SDK.</summary>
public interface IToolConversation
{
    /// <summary>Calls the model with the messages so far. With <paramref name="withTools"/> false the model must answer in text. Refusals throw a StageException.</summary>
    Task<ModelStep> NextAsync(bool withTools, CancellationToken ct);

    /// <summary>Appends the assistant turn of <paramref name="step"/> and a user turn holding one result per tool call.</summary>
    void AddToolResults(ModelStep step, IReadOnlyList<(string Id, string Text)> results);

    /// <summary>Appends a plain user turn (the nudge after an empty reply). Default: nothing, for conversations that need no history.</summary>
    void AddUserText(string text) { }
}

public static class ToolLoop
{
    public const int MaxRounds = 8;

    /// <summary>Sent once when the model's final response has no text.</summary>
    public const string EmptyReplyNudge = "ענה עכשיו במשפט אחד או שניים.";

    public static async Task<string> RunAsync(IToolConversation conversation, IReadOnlyList<IAgentTool> tools,
        List<ToolCallRecord> calls, ILogger? log, CancellationToken ct)
    {
        var text = await LoopAsync(conversation, tools, calls, log, ct);
        if (text.Trim().Length > 0) return text;
        // The model sometimes answers with no text block at all. One retry, tools off so it must speak; not a loop round.
        log?.LogWarning("turn: empty reply, retrying once");
        ct.ThrowIfCancellationRequested();
        conversation.AddUserText(EmptyReplyNudge);
        var retry = await conversation.NextAsync(false, ct);
        UsageScope.Record(retry.Usage);
        return retry.Text;
    }

    private static async Task<string> LoopAsync(IToolConversation conversation, IReadOnlyList<IAgentTool> tools,
        List<ToolCallRecord> calls, ILogger? log, CancellationToken ct)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var step = await conversation.NextAsync(tools.Count > 0, ct);
            UsageScope.Record(step.Usage);
            if (step.ToolUses.Count == 0 || tools.Count == 0) return step.Text;

            var results = new List<(string, string)>();
            foreach (var use in step.ToolUses)
            {
                ct.ThrowIfCancellationRequested();
                results.Add((use.Id, await RunOneAsync(use, tools, calls, log, ct)));
            }
            conversation.AddToolResults(step, results);
        }
        ct.ThrowIfCancellationRequested();
        log?.LogWarning("tools: round limit {Max} reached, asking for a final answer without tools", MaxRounds);
        var last = await conversation.NextAsync(false, ct);
        UsageScope.Record(last.Usage);
        return last.Text;
    }

    private static async Task<string> RunOneAsync(ToolUseRequest use, IReadOnlyList<IAgentTool> tools,
        List<ToolCallRecord> calls, ILogger? log, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        string result;
        var tool = tools.FirstOrDefault(t => t.Name == use.Name);
        if (tool is null) result = $"error: unknown tool {use.Name}";
        else
        {
            try { result = await tool.RunAsync(use.Input, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) { result = $"error: {e.GetType().Name}: {e.Message}"; }
        }
        var input = Compact(use.Input);
        calls.Add(new ToolCallRecord(use.Name, input, sw.ElapsedMilliseconds, result.Length, result.Length <= 80 ? result : result[..80]));
        log?.LogInformation("tool: {Name} {Input} -> {Chars} chars in {Ms} ms", use.Name, input, result.Length, sw.ElapsedMilliseconds);
        return result;
    }

    internal static string Compact(JsonElement input)
    {
        var s = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
        return s.Length <= 200 ? s : s[..200];
    }
}
