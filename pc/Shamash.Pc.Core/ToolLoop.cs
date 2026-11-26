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
public sealed record ToolCallRecord(string Name, string Input, long Ms, int ResultChars);

public sealed record ToolUseRequest(string Id, string Name, JsonElement Input);

/// <summary>One model response: the text and the tool calls it asked for. Raw is the provider's own content, handed back to the conversation.</summary>
public sealed record ModelStep(string Text, IReadOnlyList<ToolUseRequest> ToolUses, object? Raw = null);

/// <summary>The "call the model" side of the loop, so the loop can be tested without the SDK.</summary>
public interface IToolConversation
{
    /// <summary>Calls the model with the messages so far. With <paramref name="withTools"/> false the model must answer in text. Refusals throw a StageException.</summary>
    Task<ModelStep> NextAsync(bool withTools, CancellationToken ct);

    /// <summary>Appends the assistant turn of <paramref name="step"/> and a user turn holding one result per tool call.</summary>
    void AddToolResults(ModelStep step, IReadOnlyList<(string Id, string Text)> results);
}

public static class ToolLoop
{
    public const int MaxRounds = 8;

    public static async Task<string> RunAsync(IToolConversation conversation, IReadOnlyList<IAgentTool> tools,
        List<ToolCallRecord> calls, ILogger? log, CancellationToken ct)
    {
        for (var round = 0; round < MaxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var step = await conversation.NextAsync(tools.Count > 0, ct);
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
        return (await conversation.NextAsync(false, ct)).Text;
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
        calls.Add(new ToolCallRecord(use.Name, input, sw.ElapsedMilliseconds, result.Length));
        log?.LogInformation("tool: {Name} {Input} -> {Chars} chars in {Ms} ms", use.Name, input, result.Length, sw.ElapsedMilliseconds);
        return result;
    }

    internal static string Compact(JsonElement input)
    {
        var s = input.ValueKind == JsonValueKind.Undefined ? "" : input.GetRawText();
        return s.Length <= 200 ? s : s[..200];
    }
}
