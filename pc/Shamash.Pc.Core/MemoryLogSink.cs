using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Shamash.Pc;

/// <summary>A log line with its parts, for the home log upload.</summary>
public sealed record LogLine(DateTimeOffset Time, string Level, string Message);

/// <summary>Keeps the last N formatted log lines in memory for the status window.</summary>
public sealed class MemoryLogSink(int capacity = 400) : ILogEventSink
{
    public static MemoryLogSink Instance { get; } = new();

    private readonly Queue<string> _lines = new();
    private readonly Queue<LogLine> _records = new();
    private readonly ITextFormatter _formatter =
        new MessageTemplateTextFormatter("{Timestamp:HH:mm:ss} [{Level:u3}] {Message:lj}{NewLine}{Exception}");

    public void Emit(LogEvent logEvent)
    {
        using var w = new StringWriter();
        _formatter.Format(logEvent, w);
        var line = w.ToString().TrimEnd();
        lock (_lines)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity) _lines.Dequeue();
            var message = logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
            if (logEvent.Exception is { } e) message += " | " + e.GetType().Name + ": " + e.Message;
            _records.Enqueue(new LogLine(logEvent.Timestamp, logEvent.Level.ToString(), message));
            while (_records.Count > capacity) _records.Dequeue();
        }
    }

    /// <summary>The oldest <paramref name="max"/> lines logged after <paramref name="after"/> (the ring keeps the last N).</summary>
    public LogLine[] Since(DateTimeOffset after, int max)
    {
        lock (_lines) return _records.Where(r => r.Time > after).Take(max).ToArray();
    }

    public string[] Snapshot()
    {
        lock (_lines) return _lines.ToArray();
    }
}
