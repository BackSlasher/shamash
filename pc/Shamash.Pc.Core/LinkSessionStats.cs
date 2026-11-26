namespace Shamash.Pc;

/// <summary>Per-connection frame statistics, matching tools/pclink/server.py.</summary>
public sealed class LinkSessionStats
{
    private readonly HashSet<uint> _seqs = [];
    private readonly List<double> _arrivalsMs = [];
    private uint _min = uint.MaxValue, _max;

    public int Frames { get; private set; }
    public long Bytes { get; private set; }

    public void Add(uint seq, int pcmBytes, double arrivalMs)
    {
        Frames++;
        Bytes += pcmBytes;
        _seqs.Add(seq);
        if (seq < _min) _min = seq;
        if (seq > _max) _max = seq;
        _arrivalsMs.Add(arrivalMs);
    }

    /// <summary>Missing sequence numbers between the lowest and highest seen; duplicates do not count.</summary>
    public long Gaps => _seqs.Count == 0 ? 0 : ((long)_max - _min + 1) - _seqs.Count;

    public double AudioSeconds(int sampleRate) => (double)Bytes / (sampleRate * 2);

    /// <summary>Inter-arrival times in ms (min, median, max); null with fewer than two frames.</summary>
    public (double Min, double Median, double Max)? InterArrival()
    {
        if (_arrivalsMs.Count < 2) return null;
        var d = new double[_arrivalsMs.Count - 1];
        for (var i = 0; i < d.Length; i++) d[i] = _arrivalsMs[i + 1] - _arrivalsMs[i];
        Array.Sort(d);
        var n = d.Length;
        var median = n % 2 == 1 ? d[n / 2] : (d[n / 2 - 1] + d[n / 2]) / 2; // statistics.median
        return (d[0], median, d[^1]);
    }
}
