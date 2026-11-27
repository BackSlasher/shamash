#pragma warning disable CA1416 // every Windows-only call sits behind OfficeCom.Guarded (OperatingSystem.IsWindows)
using System.Globalization;
using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>Short ids for mail items: the model sees "m3", the EntryID never leaves the process. Per process, lost on restart.</summary>
public sealed class MailIdMap
{
    private readonly Dictionary<string, (string EntryId, string StoreId)> _byId = [];
    private readonly Dictionary<string, string> _byEntry = new(StringComparer.Ordinal);
    private readonly HashSet<string> _drafts = [];
    private int _next = 1;

    /// <summary>Marks an id as a draft made by draft_reply; send_draft refuses any other id.</summary>
    public void MarkDraft(string id) { lock (_byId) _drafts.Add(id); }
    public bool IsDraft(string id) { lock (_byId) return _drafts.Contains(id.Trim().ToLowerInvariant()); }

    /// <summary>The same item gets the same id again.</summary>
    public string Add(string entryId, string storeId)
    {
        lock (_byId)
        {
            if (_byEntry.TryGetValue(entryId, out var existing)) return existing;
            var id = "m" + (_next++).ToString(CultureInfo.InvariantCulture);
            _byId[id] = (entryId, storeId);
            _byEntry[entryId] = id;
            return id;
        }
    }

    public bool TryGet(string id, out string entryId, out string storeId)
    {
        lock (_byId)
        {
            if (_byId.TryGetValue(id.Trim().ToLowerInvariant(), out var v)) { (entryId, storeId) = v; return true; }
        }
        entryId = storeId = "";
        return false;
    }
}

public static partial class MailText
{
    public const int SnippetChars = 200;
    public const int BodyChars = 6000;

    [GeneratedRegex(@"\s+")] private static partial Regex Ws();

    /// <summary>Whitespace (newlines included) collapsed to single spaces, cut at <paramref name="max"/> characters with an ellipsis.</summary>
    public static string Snippet(string? body, int max = SnippetChars)
    {
        var s = Ws().Replace(body ?? "", " ").Trim();
        return s.Length <= max ? s : s[..max].TrimEnd() + "...";
    }

    /// <summary>Body for read_mail: CRLF to LF, runs of blank lines squeezed, cut at <paramref name="max"/>.</summary>
    public static string Body(string? body, int max = BodyChars)
    {
        var s = Regex.Replace((body ?? "").Replace("\r\n", "\n").Replace('\r', '\n'), @"\n{3,}", "\n\n").Trim();
        return ToolText.Truncate(s, max);
    }

    /// <summary>Puts the reply text (HTML-encoded, auto direction) right after the opening body tag of Outlook's HTML reply, so the quoted original stays below it.</summary>
    public static string InsertIntoHtml(string html, string text)
    {
        var block = "<div dir=\"auto\">" + WebUtility.HtmlEncode(text).Replace("\r\n", "\n").Replace("\n", "<br>") + "</div><br>";
        var m = Regex.Match(html, @"<body[^>]*>", RegexOptions.IgnoreCase);
        return m.Success ? html.Insert(m.Index + m.Length, block) : block + html;
    }
}

/// <summary>Classic Outlook through COM (Outlook.Application, MAPI namespace). Windows only; needs Outlook installed, in his interactive session.</summary>
public static class OutlookTools
{
    public static IReadOnlyList<IAgentTool> Create(MailIdMap? ids = null)
    {
        ids ??= new MailIdMap();
        return
        [
            new SortTool("list_mail_accounts",
                "Lists the mail accounts (stores) in Outlook with the number of messages and unread messages in each inbox.",
                """{"type":"object","properties":{}}""",
                _ => OfficeCom.Guarded(OutlookSession.ListAccounts)),
            new SortTool("list_recent_mail",
                "Lists the newest inbox messages, newest first, with an id for read_mail and draft_reply, date, sender, subject, unread flag and the start of the text. Without account it covers all accounts.",
                """{"type":"object","properties":{"account":{"type":"string","description":"Part of the account name from list_mail_accounts; default all"},"max":{"type":"integer","description":"Default 10, up to 30"},"unread_only":{"type":"boolean","description":"Default false"}}}""",
                input =>
                {
                    var account = ToolText.GetString(input, "account");
                    var max = Math.Clamp(ToolText.GetInt(input, "max") ?? 10, 1, 30);
                    var unread = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("unread_only", out var u) && u.ValueKind == JsonValueKind.True;
                    return OfficeCom.Guarded(() => OutlookSession.ListRecent(ids, account, max, unread));
                }),
            new SortTool("read_mail",
                "Reads one message by its id: sender, date, subject, plain text body (cut at 6000 characters) and the names of attachments.",
                """{"type":"object","properties":{"id":{"type":"string"}},"required":["id"]}""",
                input => WithItem(ids, input, id => OfficeCom.Guarded(() => OutlookSession.Read(id)))),
            new SortTool("draft_reply",
                "Creates a reply to a message and saves it in Drafts. Does NOT send. text is the reply the user dictated. Returns the draft id, recipient and subject; read the draft back to the user aloud and only then, after a clear yes in the next turn, call send_draft.",
                """{"type":"object","properties":{"id":{"type":"string"},"text":{"type":"string","description":"The reply text, without the quoted original"}},"required":["id","text"]}""",
                input =>
                {
                    var text = ToolText.GetString(input, "text");
                    if (string.IsNullOrWhiteSpace(text)) return "error: text is required";
                    return WithItem(ids, input, id => OfficeCom.Guarded(() => OutlookSession.DraftReply(ids, id, text)));
                }),
            new SortTool("send_draft",
                "Sends a draft created by draft_reply. Call only after the user said yes to the draft that was read back to him in the previous turn. Outlook may show its own security prompt on screen.",
                """{"type":"object","properties":{"id":{"type":"string","description":"The draft id from draft_reply"}},"required":["id"]}""",
                input => ids.IsDraft(ToolText.GetString(input, "id") ?? "")
                    ? WithItem(ids, input, id => OfficeCom.Guarded(() => OutlookSession.SendDraft(id)))
                    : "error: not a draft made by draft_reply"),
        ];
    }

    private static string WithItem(MailIdMap ids, JsonElement input, Func<(string EntryId, string StoreId), string> run)
    {
        var id = ToolText.GetString(input, "id");
        if (string.IsNullOrWhiteSpace(id)) return "error: id is required";
        return ids.TryGet(id, out var entry, out var store) ? run((entry, store)) : "error: unknown id; call list_recent_mail first";
    }
}

[SupportedOSPlatform("windows")]
internal static class OutlookSession
{
    private const string ProgId = "Outlook.Application";
    private const int OlFolderInbox = 6, OlMail = 43, OlFormatHtml = 2;

    /// <summary>Running Outlook or a new instance, plus its MAPI namespace. Release both afterwards (a started Outlook keeps running).</summary>
    private static (dynamic App, dynamic Ns) Open()
    {
        dynamic app = ComInterop.GetActive(ProgId) ?? ComInterop.Create(ProgId);
        try { return (app, app.GetNamespace("MAPI")); }
        catch { ComInterop.Release(app); throw; }
    }

    private static T Use<T>(Func<dynamic, T> f)
    {
        var (app, ns) = Open();
        try { return f(ns); }
        finally { ComInterop.Release(ns, app); }
    }

    public static string ListAccounts() => Use(ns =>
    {
        var sb = new StringBuilder();
        dynamic stores = ns.Stores;
        try
        {
            int count = stores.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic store = stores.Item(i);
                dynamic? inbox = null;
                try
                {
                    string name = store.DisplayName;
                    try
                    {
                        inbox = store.GetDefaultFolder(OlFolderInbox);
                        dynamic items = inbox.Items;
                        int n = items.Count; int unread = inbox.UnReadItemCount;
                        ComInterop.Release(items);
                        sb.Append(CultureInfo.InvariantCulture, $"{i}. {name} | inbox: {n} messages, {unread} unread\n");
                    }
                    catch (System.Runtime.InteropServices.COMException) { sb.Append(CultureInfo.InvariantCulture, $"{i}. {name} | no inbox\n"); }
                }
                finally { ComInterop.Release(inbox, store); }
            }
        }
        finally { ComInterop.Release(stores); }
        return sb.Length == 0 ? "no mail accounts found" : sb.ToString().TrimEnd();
    });

    public static string ListRecent(MailIdMap ids, string? account, int max, bool unreadOnly) => Use(ns =>
    {
        var rows = new List<(DateTime When, string Line)>();
        dynamic stores = ns.Stores;
        try
        {
            int count = stores.Count;
            for (var i = 1; i <= count; i++)
            {
                dynamic store = stores.Item(i);
                dynamic? inbox = null, items = null, sorted = null;
                try
                {
                    string name = store.DisplayName;
                    if (!string.IsNullOrWhiteSpace(account) && !name.Contains(account.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
                    try { inbox = store.GetDefaultFolder(OlFolderInbox); } catch (System.Runtime.InteropServices.COMException) { continue; }
                    string storeId = store.StoreID;
                    items = inbox.Items;
                    sorted = unreadOnly ? items.Restrict("[UnRead] = true") : items;
                    sorted.Sort("[ReceivedTime]", true);
                    var taken = 0;
                    dynamic? item = sorted.GetFirst();
                    while (item is not null && taken < max)
                    {
                        try
                        {
                            if ((int)item.Class == OlMail)
                            {
                                var id = ids.Add((string)item.EntryID, storeId);
                                DateTime when = item.ReceivedTime;
                                string sender = item.SenderName ?? "", subject = item.Subject ?? "", body = item.Body ?? "";
                                bool isUnread = item.UnRead;
                                rows.Add((when, $"{id} | {when:yyyy-MM-dd HH:mm} | {name} | {sender} | {subject} | {(isUnread ? "unread" : "read")} | {MailText.Snippet(body)}"));
                                taken++;
                            }
                        }
                        finally { ComInterop.Release(item); }
                        item = sorted.GetNext();
                    }
                }
                finally { if (!ReferenceEquals(sorted, items)) ComInterop.Release(sorted); ComInterop.Release(items, inbox, store); }
            }
        }
        finally { ComInterop.Release(stores); }
        if (rows.Count == 0) return unreadOnly ? "no unread messages" : "no messages found";
        return string.Join("\n", rows.OrderByDescending(r => r.When).Take(max).Select(r => r.Line));
    });

    private static dynamic GetItem(dynamic ns, (string EntryId, string StoreId) r) => ns.GetItemFromID(r.EntryId, r.StoreId);

    public static string Read((string EntryId, string StoreId) r) => Use(ns =>
    {
        dynamic item = GetItem(ns, r);
        dynamic? atts = null;
        try
        {
            if ((int)item.Class != OlMail) return "error: not a mail message";
            var names = new List<string>();
            atts = item.Attachments;
            int n = atts.Count;
            for (var i = 1; i <= n; i++)
            {
                dynamic a = atts.Item(i);
                try { names.Add((string)a.FileName); } finally { ComInterop.Release(a); }
            }
            DateTime when = item.ReceivedTime;
            return $"from: {item.SenderName}\ndate: {when:yyyy-MM-dd HH:mm}\nsubject: {item.Subject}\nattachments: {(names.Count == 0 ? "none" : string.Join(", ", names))}\n\n{MailText.Body((string?)item.Body)}";
        }
        finally { ComInterop.Release(atts, item); }
    });

    public static string DraftReply(MailIdMap ids, (string EntryId, string StoreId) r, string text) => Use(ns =>
    {
        dynamic item = GetItem(ns, r);
        dynamic? reply = null;
        try
        {
            if ((int)item.Class != OlMail) return "error: not a mail message";
            reply = item.Reply();
            if ((int)reply.BodyFormat == OlFormatHtml) reply.HTMLBody = MailText.InsertIntoHtml((string)reply.HTMLBody, text);
            else reply.Body = text + "\r\n\r\n" + (string)reply.Body;
            reply.Save();
            var id = ids.Add((string)reply.EntryID, r.StoreId);
            ids.MarkDraft(id);
            return $"draft saved, not sent | id: {id} | to: {reply.To} | subject: {reply.Subject}\ntext: {text}";
        }
        finally { ComInterop.Release(reply, item); }
    });

    public static string SendDraft((string EntryId, string StoreId) r) => Use(ns =>
    {
        dynamic item = GetItem(ns, r);
        try
        {
            if ((int)item.Class != OlMail) return "error: not a mail message";
            if ((bool)item.Sent) return "error: this message was already sent or is not a draft";
            string to = item.To;
            item.Send();
            return "sent to " + to;
        }
        finally { ComInterop.Release(item); }
    });
}
