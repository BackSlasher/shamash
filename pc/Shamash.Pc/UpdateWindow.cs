namespace Shamash.Pc;

/// <summary>A daily local-time window, e.g. 03:00-05:00. End may be earlier than start (crosses midnight).</summary>
public readonly record struct UpdateWindow(TimeOnly Start, TimeOnly End)
{
    public static readonly UpdateWindow Default = new(new TimeOnly(3, 0), new TimeOnly(5, 0));

    /// <summary>Parses "HH:mm-HH:mm". Null, empty or malformed input yields <see cref="Default"/>.</summary>
    public static UpdateWindow Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Default;
        var parts = text.Split('-');
        if (parts.Length != 2) return Default;
        if (!TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", out var start)) return Default;
        if (!TimeOnly.TryParseExact(parts[1].Trim(), "HH:mm", out var end)) return Default;
        return new UpdateWindow(start, end);
    }

    /// <summary>True when <paramref name="now"/> is in [Start, End). Start == End means an empty window.</summary>
    public bool Contains(TimeOnly now)
    {
        if (Start == End) return false;
        return Start < End
            ? now >= Start && now < End
            : now >= Start || now < End;
    }
}
