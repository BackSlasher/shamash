using System.Text.Json;

namespace Shamash.Pc;

/// <summary>Battery state the phone reports in its link <c>hello</c> (docs/link-protocol.md).</summary>
public sealed record PhoneBattery(int Level, bool Charging, DateTime SeenAt)
{
    /// <summary>"battery 71%, not charging": the log text (after "phone: ") and the status window text.</summary>
    public string Describe() => $"battery {Level}%, {(Charging ? "charging" : "not charging")}";

    /// <summary>Reads the optional <c>battery</c> object of a hello; null when absent or malformed (level outside 0..100).</summary>
    public static PhoneBattery? FromHello(JsonElement hello, DateTime now)
    {
        if (!hello.TryGetProperty("battery", out var b) || b.ValueKind != JsonValueKind.Object) return null;
        if (!b.TryGetProperty("level", out var l) || l.ValueKind != JsonValueKind.Number || !l.TryGetInt32(out var level) || level is < 0 or > 100) return null;
        if (!b.TryGetProperty("charging", out var c) || c.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
        return new PhoneBattery(level, c.GetBoolean(), now);
    }

    /// <summary>The status window line.</summary>
    public static string StatusLine(PhoneBattery? p) =>
        p is null ? "Phone: no battery report yet" : $"Phone: {p.Describe()} (seen {p.SeenAt:HH:mm:ss})";
}
