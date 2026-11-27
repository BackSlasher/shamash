using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Models.Messages;
using Microsoft.Extensions.Logging;

namespace Shamash.Pc;

/// <summary>A turn failed at a known stage ("stt", "llm", "tts", "internal"); the message goes to the phone.</summary>
public sealed class StageException(string stage, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Stage { get; } = stage;
}

/// <summary>One history entry; Role is "user" or "assistant".</summary>
public readonly record struct ChatTurn(string Role, string Text);

public interface ISpeechToText
{
    /// <summary>Transcribes a complete WAV file (16 kHz mono 16-bit). Returns the text, possibly empty.</summary>
    Task<string> TranscribeAsync(byte[] wav, CancellationToken ct);
}

public interface IReplyModel
{
    /// <summary>History is alternating user/assistant turns, oldest first, not including <paramref name="userText"/>.</summary>
    Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, CancellationToken ct);
}

/// <summary>A reply model that can call tools; the calls made are appended to <c>toolCalls</c>.</summary>
public interface IToolReplyModel : IReplyModel
{
    Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, List<ToolCallRecord> toolCalls, CancellationToken ct);
}

public interface ITextToSpeech
{
    /// <summary>Raw 16 kHz mono 16-bit little-endian PCM, as chunks arrive. Chunks have even length.</summary>
    IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, CancellationToken ct);
}

/// <summary>Where the pipeline writes its output (the WebSocket in production).</summary>
public interface ITurnSender
{
    Task SendTextAsync(string json, CancellationToken ct);
    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct);
}

public static class TurnJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    public static string Message(string type, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in fields)
            o[k] = v switch { null => null, string s => s, int i => i, long l => l, _ => v.ToString() };
        return o.ToJsonString(Options);
    }
}

/// <summary>Per-turn record saved next to the WAV.</summary>
public sealed class TurnResult
{
    public string? Transcript { get; set; }
    public string? Reply { get; set; }
    public long? SttMs { get; set; }
    public long? LlmMs { get; set; }
    public long? TtsFirstByteMs { get; set; }
    public long? TtsTotalMs { get; set; }
    public long TtsBytes { get; set; }
    public long TotalMs { get; set; }
    public string? ErrorStage { get; set; }
    public string? ErrorMessage { get; set; }
    public string? CancelledAt { get; set; }
    public bool Done { get; set; }
    public List<ToolCallRecord> ToolCalls { get; } = [];
    /// <summary>Sum over every model call of the turn (chat loop and translator).</summary>
    public LlmUsage Usage { get; set; } = LlmUsage.Zero;

    public string ToJson() => JsonSerializer.Serialize(new
    {
        transcript = Transcript,
        reply = Reply,
        timingsMs = new { stt = SttMs, llm = LlmMs, ttsFirstByte = TtsFirstByteMs, ttsTotal = TtsTotalMs, total = TotalMs },
        ttsBytes = TtsBytes,
        error = ErrorStage is null ? null : new { stage = ErrorStage, message = ErrorMessage },
        toolCalls = ToolCalls.Select(c => new { name = c.Name, input = c.Input, ms = c.Ms, resultChars = c.ResultChars, resultPrefix = c.ResultPrefix }),
        usage = new { calls = Usage.Calls, input = Usage.Input, cacheWrite = Usage.CacheWrite, cacheRead = Usage.CacheRead, output = Usage.Output },
        cancelledAt = CancelledAt,
        done = Done,
    }, new JsonSerializerOptions(TurnJson.Options) { WriteIndented = true });
}

/// <summary>Runs one conversation turn: STT, LLM, TTS, against an abstract sender. Holds the running history.</summary>
public sealed class TurnPipeline(ISpeechToText stt, IReplyModel llm, ITextToSpeech tts, ILogger log, bool allowTextTurns = false, IReadOnlyList<IAgentTool>? tools = null)
{
    /// <summary>The tools the conversation uses; the link's direct `tool` message (test aid) looks them up here.</summary>
    public IReadOnlyList<IAgentTool> Tools { get; } = tools ?? [];

    /// <summary>Test aid: a hello may carry the transcript as text and skip STT (settings allowTextTurns).</summary>
    public bool AllowTextTurns => allowTextTurns;

    public const int MinAudioMs = 500;
    public const int MaxHistoryTurns = 40;
    public const int TtsSampleRate = 16000;

    private readonly object _lock = new();
    private readonly List<ChatTurn> _history = [];

    public IReadOnlyList<ChatTurn> HistorySnapshot() { lock (_lock) return [.. _history]; }

    /// <summary>Test aid: forget the conversation so far (link message "reset", allowTextTurns only).</summary>
    public void ResetHistory() { lock (_lock) _history.Clear(); }

    private void Append(string user, string assistant)
    {
        lock (_lock)
        {
            _history.Add(new ChatTurn("user", user));
            _history.Add(new ChatTurn("assistant", assistant));
            // "40 turns" = 40 history entries; always drop in pairs so the list starts with a user turn.
            while (_history.Count > MaxHistoryTurns) _history.RemoveRange(0, 2);
        }
    }

    public static TurnPipeline Create(ShamashSettings settings, HttpClient http, ILoggerFactory lf)
    {
        var log = lf.CreateLogger<TurnPipeline>();
        var tools = BuildTools(settings);
        IReplyModel llm = settings.UseEchoLlm
            ? new EchoReplyModel()
            : new ClaudeReplyModel(settings.AnthropicApiKey, settings.ClaudeModel, tools, log);
        return new TurnPipeline(
            new ElevenLabsSpeechToText(http, settings.ElevenLabsKey),
            llm,
            new ElevenLabsTextToSpeech(http, settings.ElevenLabsKey, settings.TtsVoiceId),
            log, settings.AllowTextTurns, tools);
    }

    /// <summary>The tools Claude may call, built from settings.</summary>
    public static IReadOnlyList<IAgentTool> BuildTools(ShamashSettings settings) =>
    [
        new SearchDocumentsTool(settings.DocumentRoots),
        new ReadDocumentTool(settings.DocumentRoots),
        new ShowInExplorerTool(settings.DocumentRoots),
        new OpenFileTool(settings.DocumentRoots),
        new IsDocumentOpenTool(settings.DocumentRoots),
        new FocusDocumentTool(settings.DocumentRoots),
        new CloseDocumentTool(settings.DocumentRoots),
        new DescribeOfficeDialogsTool(),
        .. OutlookTools.Create(),
        .. OrganizeStore.CreateTools(settings.DocumentRoots, null, settings.MaxOrganizeFiles),
        .. SortStore.CreateTools(settings.DocumentRoots, settings.MaxOrganizeFiles),
        new TranslateDocumentTool(settings.DocumentRoots, new ClaudeTranslator(settings.AnthropicApiKey, settings.TranslateModel ?? settings.ClaudeModel)),
    ];

    public async Task<TurnResult> RunAsync(byte[] pcm, int sampleRate, ITurnSender sender, CancellationToken ct, string? text = null, bool noTts = false)
    {
        var result = new TurnResult();
        var usage = UsageScope.Begin();
        var total = Stopwatch.StartNew();
        var stage = "stt";
        try
        {
            // 1. STT
            var sw = Stopwatch.StartNew();
            string transcript;
            if (allowTextTurns && !string.IsNullOrWhiteSpace(text))
                transcript = text.Trim();
            else if (pcm.Length < sampleRate * 2 * MinAudioMs / 1000)
                transcript = "";
            else
            {
                var ms = new MemoryStream(pcm.Length + WavWriter.HeaderBytes);
                WavWriter.Write(ms, pcm, sampleRate);
                transcript = (await stt.TranscribeAsync(ms.ToArray(), ct)).Trim();
            }
            result.Transcript = transcript;
            result.SttMs = sw.ElapsedMilliseconds;
            log.LogInformation("turn: transcript {Chars} chars in {Ms} ms", transcript.Length, result.SttMs);

            // 2. transcript
            stage = "internal";
            await sender.SendTextAsync(TurnJson.Message("transcript", ("text", transcript)), ct);
            if (transcript.Length == 0)
            {
                await sender.SendTextAsync(TurnJson.Message("done"), ct);
                result.Done = true;
                Finish();
                return result;
            }

            // 3. LLM
            stage = "llm";
            sw.Restart();
            var raw = llm is IToolReplyModel tm
                ? await tm.ReplyAsync(HistorySnapshot(), transcript, result.ToolCalls, ct)
                : await llm.ReplyAsync(HistorySnapshot(), transcript, ct);
            var reply = raw.Trim();
            if (reply.Length == 0) throw new StageException("llm", "empty reply");
            result.Reply = reply;
            result.LlmMs = sw.ElapsedMilliseconds;
            Append(transcript, reply);
            log.LogInformation("turn: reply {Chars} chars in {Ms} ms, {Tools} tool calls", reply.Length, result.LlmMs, result.ToolCalls.Count);

            // 4. reply text
            stage = "internal";
            await sender.SendTextAsync(TurnJson.Message("reply", ("text", reply)), ct);

            // 5. TTS, streamed (test aid: skipped for a text turn with noTts, allowTextTurns only)
            if (allowTextTurns && noTts && !string.IsNullOrWhiteSpace(text))
            {
                log.LogInformation("turn: TTS skipped (noTts text turn)");
                await sender.SendTextAsync(TurnJson.Message("done"), ct);
                result.Done = true;
                Finish();
                return result;
            }
            stage = "tts";
            sw.Restart();
            var started = false;
            var carry = new List<byte>(1);
            await foreach (var chunk in tts.SynthesizeAsync(reply, ct))
            {
                if (chunk.Length == 0) continue;
                if (!started)
                {
                    result.TtsFirstByteMs = sw.ElapsedMilliseconds;
                    await sender.SendTextAsync(TurnJson.Message("audio_start", ("sampleRate", TtsSampleRate)), ct);
                    started = true;
                }
                // Defence in depth: the TTS implementation already yields even-length chunks.
                ReadOnlyMemory<byte> data = chunk;
                if (carry.Count > 0)
                {
                    var joined = new byte[carry.Count + chunk.Length];
                    carry.CopyTo(joined);
                    chunk.CopyTo(joined.AsMemory(carry.Count));
                    carry.Clear();
                    data = joined;
                }
                if ((data.Length & 1) == 1)
                {
                    carry.Add(data.Span[^1]);
                    data = data[..^1];
                }
                if (data.Length == 0) continue;
                result.TtsBytes += data.Length;
                await sender.SendBinaryAsync(data, ct);
            }
            if (!started)
                throw new StageException("tts", "no audio returned");
            result.TtsTotalMs = sw.ElapsedMilliseconds;
            log.LogInformation("turn: tts first byte {First} ms, total {Total} ms, bytes {Bytes}",
                result.TtsFirstByteMs, result.TtsTotalMs, result.TtsBytes);
            await sender.SendTextAsync(TurnJson.Message("audio_end"), ct);
            await sender.SendTextAsync(TurnJson.Message("done"), ct);
            result.Done = true;
            Finish();
        }
        catch (Exception e) when (ct.IsCancellationRequested)
        {
            _ = e;
            result.CancelledAt = stage;
            result.TotalMs = total.ElapsedMilliseconds;
            log.LogInformation("turn: cancelled at {Stage}", stage);
        }
        catch (Exception e)
        {
            var st = e is StageException se ? se.Stage : stage;
            var msg = e is StageException ? e.Message : $"{e.GetType().Name}: {e.Message}";
            result.ErrorStage = st;
            result.ErrorMessage = msg;
            result.TotalMs = total.ElapsedMilliseconds;
            log.LogWarning("turn: error at {Stage}: {Message}", st, msg);
            try { await sender.SendTextAsync(TurnJson.Message("error", ("stage", st), ("message", msg)), ct); }
            catch (Exception) { /* peer is gone */ }
        }
        result.Usage = usage.Total;
        if (result.Usage.Calls > 0) log.LogInformation("llm usage: {Usage}", result.Usage);
        return result;

        void Finish()
        {
            result.TotalMs = total.ElapsedMilliseconds;
            log.LogInformation("turn: done total {Ms} ms", result.TotalMs);
        }
    }
}

public sealed class EchoReplyModel : IReplyModel
{
    public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, CancellationToken ct) => Task.FromResult(userText);
}

public sealed class ClaudeReplyModel(string? apiKey, string model, IReadOnlyList<IAgentTool>? tools = null, ILogger? log = null) : IToolReplyModel
{
    public const string SystemPrompt =
        "אתה שַׁמָּשׁ, עוזר קולי לאדם מבוגר, איש אקדמיה דובר עברית. ענה תמיד בעברית, במשפט אחד עד שלושה, בשפה מדוברת וברורה. התשובה מוקראת בקול, לכן בלי רשימות, בלי סימני עיצוב, בלי אימוג'י ובלי קישורים. אם לא הבנת, שאל שאלה קצרה. " +
        "אתה רץ על המחשב האישי שלו, שהוא מחשב Windows, ויש לך כלים לחפש ולקרוא את המסמכים שלו. תמיד חפש בכלי לפני שאתה אומר שקובץ קיים או שהוא לא קיים. כשמצאת קובץ, אמור באיזו תיקייה הוא נמצא, לפי שם התיקייה ולא הנתיב המלא. אם החיפוש מדווח שאינדקס החיפוש לא זמין, אמור זאת למשתמש במשפט אחד (\"אינדקס החיפוש לא זמין כרגע, נסה שוב בעוד דקה\") ואל תנחש שמות קבצים. " +
        "אתה יכול גם לפתוח קובץ, להראות אותו בסייר הקבצים, לבדוק אם מסמך פתוח כרגע ולהעביר מסמך פתוח לקדמת המסך. אל תקריא מסמך שלם, סכם אותו במשפט אחד עד שלושה. " +
        "כשמבקשים לסדר תיקייה, קודם קרא ל-find_duplicates ואז תאר את התוכנית בשלושה משפטים לכל היותר (כמה עותקים של כמה מסמכים ולאן הם יועברו) ושאל אם לבצע. קרא ל-apply_plan רק אחרי שהוא אמר כן בבירור בתור הבא. לעולם אל תמחק קבצים, רק הזז, ואמור שאפשר לבטל. " +
        "סידור תיקייה מבולגנת נעשה קובץ אחד בכל פעם: קרא ל-sort_folder_start, ואז לכל קובץ קרא ל-sort_folder_next, תאר אותו במשפט אחד (מה זה, לפי הקטע שחזר) והצע יעד אחד בלבד, בצורת שאלה, לפי הראיות שחזרו: אם יש עותק זהה בתיקייה אחרת הצע להעביר את זה לכפולים; אחרת אם יש גרסה דומה בתיקייה אחרת הצע את אותה תיקייה; אחרת התיקייה עם התוכן הדומה ביותר; ואם שום דבר לא מתאים, תיקייה חדשה. אם הוא אמר כן קרא ל-sort_folder_move, אם אמר לא או דלג קרא ל-sort_folder_skip, ואם אמר תחזיר קרא ל-undo_last_move. אחרי כל חמישה קבצים אמור כמה נשארו ושאל אם להמשיך, וכשהוא אומר שמספיק, עצור ואמור שאפשר להמשיך בפעם אחרת. " +
        "אתה יכול לתרגם מסמך לאנגלית או לעברית; התוצאה נשמרת כקובץ חדש ליד המקור, ואחרי התרגום אמור באיזו תיקייה הקובץ נמצא והצע לפתוח אותו. כשמבקשים לסגור מסמך קרא ל-close_document; אם הוא מדווח על שינויים שלא נשמרו, שאל בקול \"יש שינויים שלא נשמרו, לשמור?\" וקרא שוב עם save=yes אם אמר כן או save=no אם אמר לא. " +
        "יש לך גם כלים למייל ב-Outlook שלו: קרא הודעות אחרונות וסכם אותן במשפט, והכן טיוטת תשובה עם draft_reply. אחרי יצירת הטיוטה הקרא אותה בקול ושאל אם לשלוח, וקרא ל-send_draft רק אחרי שהוא אמר כן בבירור בתור הבא. לעולם אל תמחק הודעות. אם Outlook מציג חלון אבטחה משלו, אמור לו לאשר אותו על המסך. אם כלי מדווח על חלון של Office, אמור למשתמש בקצרה מה כתוב בו ואיזה כפתור ללחוץ. " +
        "אם ביקשו ממך משהו שאף כלי עדיין לא יודע לעשות, אמור את זה בקצרה בעברית.";

    public const int MaxTokens = 1024;
    public const int MaxHistoryTurns = 40;

    private AnthropicClient? _client;
    private readonly IReadOnlyList<IAgentTool> _tools = tools ?? [];

    public Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, CancellationToken ct) =>
        ReplyAsync(history, userText, [], ct);

    public async Task<string> ReplyAsync(IReadOnlyList<ChatTurn> history, string userText, List<ToolCallRecord> toolCalls, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) throw new StageException("llm", "no Anthropic key configured");
        _client ??= new AnthropicClient { ApiKey = apiKey };
        var messages = new List<MessageParam>();
        foreach (var t in history)
            messages.Add(t.Role == "user"
                ? new UserMessageParam { Content = t.Text }
                : new AssistantMessageParam { Content = t.Text });
        messages.Add(new UserMessageParam { Content = userText });
        var conversation = new SdkConversation(_client, model, _tools, messages);
        return await ToolLoop.RunAsync(conversation, _tools, toolCalls, log, ct);
    }

    /// <summary>
    /// Tool definitions with a cache breakpoint on the last one: the prefix (all tools) is identical on every call, so the
    /// API serves it from the prompt cache. Request order is tools, system, messages; nothing dynamic precedes the breakpoints.
    /// </summary>
    public static List<ToolUnion> BuildToolDefs(IReadOnlyList<IAgentTool> tools)
    {
        var defs = new List<ToolUnion>();
        for (var i = 0; i < tools.Count; i++)
        {
            var d = SdkConversation.ToDefinition(tools[i]);
            defs.Add(i == tools.Count - 1 ? d with { CacheControl = new CacheControlEphemeral() } : d);
        }
        return defs;
    }

    /// <summary>The request for one model call. The system prompt is a text block carrying a cache breakpoint (cached after the tools).</summary>
    public static MessageCreateParams BuildParams(string model, string systemPrompt, List<ToolUnion> toolDefs, List<MessageParam> messages, bool withTools)
    {
        var p = new MessageCreateParams
        {
            Model = model,
            MaxTokens = MaxTokens,
            System = new List<TextBlockParam> { new() { Text = systemPrompt, CacheControl = new CacheControlEphemeral() } },
            Messages = messages,
            OutputConfig = new() { Effort = Effort.Low },
        };
        if (toolDefs.Count > 0)
        {
            // Tools stay declared after the round limit (the history holds tool_use blocks); tool_choice none forces a text answer.
            p = p with { Tools = toolDefs };
            if (!withTools) p = p with { ToolChoice = new ToolChoiceNone() };
        }
        return p;
    }

    /// <summary>The Anthropic SDK side of the tool loop: Messages.Create, content echo, tool_result turns.</summary>
    private sealed class SdkConversation(AnthropicClient client, string model, IReadOnlyList<IAgentTool> tools, List<MessageParam> messages) : IToolConversation
    {
        private readonly List<ToolUnion> _toolDefs = BuildToolDefs(tools);

        public async Task<ModelStep> NextAsync(bool withTools, CancellationToken ct)
        {
            var p = BuildParams(model, SystemPrompt, _toolDefs, messages, withTools);
            var response = await client.Messages.Create(p, cancellationToken: ct);
            if (response.StopReason == StopReason.Refusal)
                throw new StageException("llm", "the model refused to answer");
            var sb = new StringBuilder();
            var uses = new List<ToolUseRequest>();
            foreach (var block in response.Content)
            {
                if (block.TryPickText(out TextBlock? t) && t is not null) sb.Append(t.Text);
                else if (block.TryPickToolUse(out ToolUseBlock? u) && u is not null)
                    uses.Add(new ToolUseRequest(u.ID, u.Name, JsonSerializer.SerializeToElement(u.Input)));
            }
            var ru = response.Usage;
            var usage = new LlmUsage(1, ru.InputTokens, ru.CacheCreationInputTokens ?? 0, ru.CacheReadInputTokens ?? 0, ru.OutputTokens);
            return new ModelStep(sb.ToString(), uses, response.Content, usage);
        }

        public void AddToolResults(ModelStep step, IReadOnlyList<(string Id, string Text)> results)
        {
            // No ToParam helper in the SDK: rebuild each content block as its *Param twin. Thinking signatures must be kept.
            var assistant = new List<ContentBlockParam>();
            foreach (var block in (IReadOnlyList<ContentBlock>)step.Raw!)
            {
                if (block.TryPickText(out TextBlock? t) && t is not null)
                {
                    if (t.Text.Length > 0) assistant.Add(new TextBlockParam { Text = t.Text });
                }
                else if (block.TryPickThinking(out ThinkingBlock? th) && th is not null)
                    assistant.Add(new ThinkingBlockParam { Thinking = th.Thinking, Signature = th.Signature });
                else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? rt) && rt is not null)
                    assistant.Add(new RedactedThinkingBlockParam { Data = rt.Data });
                else if (block.TryPickToolUse(out ToolUseBlock? u) && u is not null)
                    assistant.Add(new ToolUseBlockParam { ID = u.ID, Name = u.Name, Input = u.Input });
            }
            messages.Add(new AssistantMessageParam { Content = assistant });
            var toolResults = new List<ContentBlockParam>();
            foreach (var (id, text) in results)
                toolResults.Add(new ToolResultBlockParam { ToolUseID = id, Content = text, IsError = text.StartsWith("error:", StringComparison.Ordinal) });
            messages.Add(new UserMessageParam { Content = toolResults });
        }

        public static Tool ToDefinition(IAgentTool t)
        {
            using var doc = JsonDocument.Parse(t.InputSchema);
            var root = doc.RootElement;
            var props = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("properties", out var pe))
                foreach (var prop in pe.EnumerateObject()) props[prop.Name] = prop.Value.Clone();
            var required = root.TryGetProperty("required", out var re)
                ? re.EnumerateArray().Select(x => x.GetString()!).ToList()
                : [];
            return new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = new() { Properties = props, Required = required },
            };
        }
    }
}

public sealed class ElevenLabsSpeechToText(HttpClient http, string? apiKey) : ISpeechToText
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(85);

    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) throw new StageException("internal", "no ElevenLabs key configured");
        using var form = new MultipartFormDataContent
        {
            { new StringContent("scribe_v3"), "model_id" },
            { new StringContent("heb"), "language_code" },
            { new StringContent("false"), "tag_audio_events" },
            { new StringContent("false"), "diarize" },
        };
        var file = new ByteArrayContent(wav);
        file.Headers.ContentType = new("audio/wav");
        form.Add(file, "file", "turn.wav");
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.elevenlabs.io/v1/speech-to-text") { Content = form };
        req.Headers.Add("xi-api-key", apiKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var resp = await http.SendAsync(req, cts.Token);
            var body = await resp.Content.ReadAsStringAsync(cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new StageException("stt", $"HTTP {(int)resp.StatusCode}: {Trim(body)}");
            try
            {
                using var doc = JsonDocument.Parse(body);
                var t = doc.RootElement.GetProperty("text");
                return t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            }
            catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
            {
                throw new StageException("stt", $"unexpected response: {Trim(body)}", e);
            }
        }
        catch (HttpRequestException e) { throw new StageException("stt", $"network: {e.Message}", e); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StageException("stt", "timed out"); }
    }

    internal static string Trim(string s) => s.Length <= 200 ? s : s[..200];
}

public sealed class ElevenLabsTextToSpeech(HttpClient http, string? apiKey, string voiceId) : ITextToSpeech
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> SynthesizeAsync(string text, [EnumeratorCancellation] CancellationToken ct)
    {
        if (string.IsNullOrEmpty(apiKey)) throw new StageException("internal", "no ElevenLabs key configured");
        var url = $"https://api.elevenlabs.io/v1/text-to-speech/{Uri.EscapeDataString(voiceId)}/stream?output_format=pcm_16000";
        var json = new JsonObject { ["text"] = text, ["model_id"] = "eleven_v4_turbo", ["language_code"] = "he" }.ToJsonString(TurnJson.Options);
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("xi-api-key", apiKey);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);

        HttpResponseMessage resp;
        Stream stream;
        try
        {
            resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(cts.Token);
                var code = (int)resp.StatusCode;
                resp.Dispose();
                throw new StageException("tts", $"HTTP {code}: {ElevenLabsSpeechToText.Trim(body)}");
            }
            stream = await resp.Content.ReadAsStreamAsync(cts.Token);
        }
        catch (HttpRequestException e) { throw new StageException("tts", $"network: {e.Message}", e); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StageException("tts", "timed out"); }

        using (resp)
        await using (stream)
        {
            var buf = new byte[8192];
            var have = 0; // bytes in buf not yet yielded (0 or 1 carried byte at the start)
            while (true)
            {
                int n;
                try { n = await stream.ReadAsync(buf.AsMemory(have), cts.Token); }
                catch (HttpRequestException e) { throw new StageException("tts", $"network: {e.Message}", e); }
                catch (IOException e) { throw new StageException("tts", $"network: {e.Message}", e); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new StageException("tts", "timed out"); }
                if (n == 0) break;
                var total = have + n;
                var even = total & ~1;
                if (even > 0)
                {
                    yield return buf.AsMemory(0, even).ToArray();
                    if (even < total) buf[0] = buf[even];
                    have = total - even;
                }
                else have = total;
            }
        }
    }
}
