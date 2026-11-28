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
    public const int DefaultMaxOrganizeFiles = 3000;
    public const string DefaultClaudeModel = "claude-opus-5-5";

    /// <summary>Folders the document tools may search and read. Default on Windows: Documents and Desktop.</summary>
    public IReadOnlyList<string> DocumentRoots { get; init; } = [];

    /// <summary>Extra folders searched and read for pictures only (search_documents kind picture, insert_picture). Default on Windows: the user's Pictures folder. Env SHAMASH_PICTURE_ROOTS, config key pictureRoots.</summary>
    public IReadOnlyList<string> PictureRoots { get; init; } = [];

    /// <summary>Folders with more files than this are refused by find_duplicates and the sort_folder tools. Env SHAMASH_MAX_ORGANIZE_FILES, config key maxOrganizeFiles.</summary>
    public int MaxOrganizeFiles { get; init; } = DefaultMaxOrganizeFiles;

    /// <summary>Model for translate_document; null means the chat model. Env SHAMASH_TRANSLATE_MODEL, config key translateModel.</summary>
    public string? TranslateModel { get; init; }

    /// <summary>Home server base URL (docs/home-protocol.md); empty disables the home link. Env SHAMASH_HOME_URL, config key homeUrl.</summary>
    public string? HomeUrl { get; init; }

    /// <summary>Shared enrolment token, only used to register. Never logged. Env SHAMASH_HOME_ENROL_TOKEN, config key homeEnrolToken.</summary>
    public string? HomeEnrolToken { get; init; }

    /// <summary>Minutes between home heartbeats, default 5. Env SHAMASH_HOME_HEARTBEAT_MINUTES, config key homeHeartbeatMinutes.</summary>
    public int HomeHeartbeatMinutes { get; init; } = 5;

    /// <summary>Test aid: allow a conversation hello to carry "text" instead of audio. Default false.</summary>
    public bool AllowTextTurns { get; init; }

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
            if (file is { } g && g.TryGetProperty(fileKey, out var q) && q.ValueKind == JsonValueKind.Number) return q.GetRawText();
            return null;
        }

        var s = new ShamashSettings(
            Pick("ELEVENLABS_KEY", "elevenLabsKey"),
            Pick("ANTHROPIC_API_KEY", "anthropicApiKey"),
            (Pick("SHAMASH_LLM", "llm") ?? "claude").ToLowerInvariant(),
            Pick("SHAMASH_TTS_VOICE_ID", "ttsVoiceId") ?? DefaultVoiceId,
            Pick("SHAMASH_CLAUDE_MODEL", "claudeModel") ?? DefaultClaudeModel)
        {
            DocumentRoots = PickRoots(getEnv("SHAMASH_DOCUMENT_ROOTS"), file),
            PictureRoots = PickRoots(getEnv("SHAMASH_PICTURE_ROOTS"), file, "pictureRoots", [Environment.SpecialFolder.MyPictures]),
            TranslateModel = Pick("SHAMASH_TRANSLATE_MODEL", "translateModel"),
            MaxOrganizeFiles = int.TryParse(Pick("SHAMASH_MAX_ORGANIZE_FILES", "maxOrganizeFiles"), out var mo) && mo > 0 ? mo : DefaultMaxOrganizeFiles,
            HomeUrl = Pick("SHAMASH_HOME_URL", "homeUrl"),
            HomeEnrolToken = Pick("SHAMASH_HOME_ENROL_TOKEN", "homeEnrolToken"),
            HomeHeartbeatMinutes = int.TryParse(Pick("SHAMASH_HOME_HEARTBEAT_MINUTES", "homeHeartbeatMinutes"), out var hb) && hb > 0 ? hb : 5,
            AllowTextTurns = PickBool(getEnv("SHAMASH_ALLOW_TEXT_TURNS"), file, "allowTextTurns"),
        };
        doc?.Dispose();
        return s;
    }

    private static bool PickBool(string? env, JsonElement? file, string key)
    {
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim() is "1" or "true" or "True" or "TRUE";
        return file is { } f && f.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.True;
    }

    private static List<string> PickRoots(string? env, JsonElement? file, string key = "documentRoots",
        Environment.SpecialFolder[]? windowsDefaults = null)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(env))
            roots.AddRange(env.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        else if (file is { } f && f.TryGetProperty(key, out var p))
        {
            if (p.ValueKind == JsonValueKind.Array)
                roots.AddRange(p.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()));
            else if (p.ValueKind == JsonValueKind.String)
                roots.AddRange(p.GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }
        else if (OperatingSystem.IsWindows())
        {
            foreach (var folder in windowsDefaults ?? [Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.DesktopDirectory])
                roots.Add(Environment.GetFolderPath(folder));
        }
        return roots.Where(r => r.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Booleans only; key values never appear.</summary>
    public string Describe()
    {
        static string B(string? v) => string.IsNullOrEmpty(v) ? "false" : "true";
        return $"config: elevenLabsKey={B(ElevenLabsKey)} anthropicApiKey={B(AnthropicApiKey)} llm={Llm}";
    }

    public void LogPresence(ILogger log) => log.LogInformation("{Config}", Describe());
}
