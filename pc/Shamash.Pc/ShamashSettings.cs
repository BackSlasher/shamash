using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>
/// Service settings, loaded once at startup. Environment variables win over the JSON config file
/// (%LocalAppData%\Shamash\config.json, fallback ./config.json, camelCase keys).
/// </summary>
public sealed record ShamashSettings(
    string? ElevenLabsKey,
    string? AnthropicApiKey,
    string Llm,
    string TtsVoiceId,
    string ClaudeModel)
{
    public const string DefaultVoiceId = "JBFqnCBsd6RMkjVDRZzb";
    public const string DefaultClaudeModel = "claude-opus-5-5";

    public bool UseEchoLlm => string.Equals(Llm, "echo", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> DefaultConfigCandidates()
    {
        var list = new List<string>();
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrEmpty(baseDir)) list.Add(Path.Combine(baseDir, "Shamash", "config.json"));
        list.Add(Path.GetFullPath("config.json"));
        return list;
    }

    public static ShamashSettings LoadDefault(ILogger? log = null) =>
        Load(Environment.GetEnvironmentVariable, DefaultConfigCandidates(), log);

    /// <summary>The first existing candidate file is used. A malformed file is logged and ignored.</summary>
    public static ShamashSettings Load(Func<string, string?> getEnv, IEnumerable<string> configCandidates, ILogger? log = null)
    {
        JsonElement? file = null;
        JsonDocument? doc = null;
        foreach (var path in configCandidates)
        {
            if (!File.Exists(path)) continue;
            try
            {
                doc = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.ValueKind == JsonValueKind.Object) file = doc.RootElement;
                log?.LogInformation("config: read {Path}", path);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                log?.LogWarning("config: could not read {Path}: {Error}", path, e.GetType().Name);
            }
            break;
        }

        string? Pick(string envName, string fileKey)
        {
            var v = getEnv(envName);
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
            if (file is { } f && f.TryGetProperty(fileKey, out var p) && p.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(p.GetString())) return p.GetString()!.Trim();
            return null;
        }

        var s = new ShamashSettings(
            Pick("ELEVENLABS_KEY", "elevenLabsKey"),
            Pick("ANTHROPIC_API_KEY", "anthropicApiKey"),
            (Pick("SHAMASH_LLM", "llm") ?? "claude").ToLowerInvariant(),
            Pick("SHAMASH_TTS_VOICE_ID", "ttsVoiceId") ?? DefaultVoiceId,
            Pick("SHAMASH_CLAUDE_MODEL", "claudeModel") ?? DefaultClaudeModel);
        doc?.Dispose();
        return s;
    }

    /// <summary>Booleans only; key values never appear.</summary>
    public string Describe()
    {
        static string B(string? v) => string.IsNullOrEmpty(v) ? "false" : "true";
        return $"config: elevenLabsKey={B(ElevenLabsKey)} anthropicApiKey={B(AnthropicApiKey)} llm={Llm}";
    }

    public void LogPresence(ILogger log) => log.LogInformation("{Config}", Describe());
}
