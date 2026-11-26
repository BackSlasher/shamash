using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;
using Serilog.Formatting.Display;

namespace Shamash.Pc;

/// <summary>Keeps the last N formatted log lines in memory for the status window.</summary>
public sealed class MemoryLogSink(int capacity = 400) : ILogEventSink
{
    public static MemoryLogSink Instance { get; } = new();

    private readonly Queue<string> _lines = new();
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
        }
    }

    public string[] Snapshot()
    {
        lock (_lines) return _lines.ToArray();
    }
}
