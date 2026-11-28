using System.Text;
using System.Text.Json;

namespace Shamash.Pc;

/// <summary>One finished turn waiting to be uploaded: the per-turn JSON file's base name, its time and its content.</summary>
public sealed record HomeTurn(string TurnId, DateTimeOffset Time, string Json);

/// <summary>
/// Finished turns waiting for the next upload to the home server. In memory only: a restart loses the unsent turns,
/// which is acceptable (the per-turn files stay on disk). Capped at <see cref="Capacity"/>; the oldest are dropped.
/// </summary>
public sealed class HomeUploadQueue(int capacity = HomeUploadQueue.DefaultCapacity)
{
    public const int DefaultCapacity = 500;
    public const int BatchSize = 50;

    public static HomeUploadQueue Instance { get; } = new();

    private readonly List<HomeTurn> _items = [];
    private readonly SemaphoreSlim _signal = new(0, 1);

    public int Capacity => capacity;

    public int Count { get { lock (_items) return _items.Count; } }

    public void Enqueue(HomeTurn turn)
    {
        lock (_items)
        {
            _items.Add(turn);
            if (_items.Count > capacity) _items.RemoveRange(0, _items.Count - capacity);
        }
        if (_signal.CurrentCount == 0) { try { _signal.Release(); } catch (SemaphoreFullException) { } }
    }

    /// <summary>The oldest <paramref name="max"/> items, left in the queue until <see cref="Remove"/> confirms the upload.</summary>
    public IReadOnlyList<HomeTurn> Peek(int max = BatchSize)
    {
        lock (_items) return _items.Take(max).ToList();
    }

    public void Remove(IEnumerable<HomeTurn> sent)
    {
        var set = new HashSet<HomeTurn>(sent, ReferenceEqualityComparer.Instance);
        lock (_items) _items.RemoveAll(set.Contains);
    }

    /// <summary>Waits up to <paramref name="timeout"/> for a turn to be queued; false on timeout.</summary>
    public Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct) => _signal.WaitAsync(timeout, ct);

    /// <summary>The <c>/api/turns</c> request body; each record is the turn's JSON passed through as is.</summary>
    public static byte[] BuildBody(IEnumerable<HomeTurn> turns)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("turns");
            foreach (var t in turns)
            {
                w.WriteStartObject();
                w.WriteString("turnId", t.TurnId);
                w.WriteString("time", t.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture));
                w.WritePropertyName("record");
                w.WriteRawValue(t.Json);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }
}

/// <summary>Which log lines go to the home server, and the <c>/api/logs</c> request body.</summary>
public static class HomeLogUpload
{
    public const int MaxLines = 500;

    private static readonly string[] InformationPrefixes = ["turn:", "tool:", "llm usage:", "missing feature:"];

    /// <summary>Warning and above, plus the Information lines that tell what a turn did.</summary>
    public static bool Wants(string level, string message) => level switch
    {
        "Warning" or "Error" or "Fatal" => true,
        "Information" => InformationPrefixes.Any(p => message.StartsWith(p, StringComparison.Ordinal)),
        _ => false,
    };

    public static byte[] BuildBody(IEnumerable<LogLine> lines) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        lines = lines.Select(l => new
        {
            time = l.Time.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture),
            level = l.Level,
            message = l.Message,
        }),
    });
}

/// <summary>
/// What the home server asks of the update machinery. Written by <see cref="HomeLinkService"/>, read by
/// <see cref="UpdateService"/>. Hold stops every update check; desiredVersion is only recorded in
/// <see cref="ServiceStatus"/> (Velopack updates to the latest GitHub release, pinning is not implemented).
/// </summary>
public sealed class UpdateControl
{
    public static UpdateControl Instance { get; } = new();

    private volatile bool _hold;
    private int _triggers;

    public bool Hold { get => _hold; set => _hold = value; }

    /// <summary>How many "check now" commands arrived (for tests and the log).</summary>
    public int CheckTriggers => Volatile.Read(ref _triggers);

    /// <summary>Wakes the update loop for an immediate check (the same wake-up the tray's "check for updates" uses).</summary>
    public void TriggerCheck()
    {
        Interlocked.Increment(ref _triggers);
        ServiceStatus.Instance.WakeUpdateLoop();
    }
}
