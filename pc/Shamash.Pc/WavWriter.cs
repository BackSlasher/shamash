using System.Buffers.Binary;

namespace Shamash.Pc;

/// <summary>Minimal PCM WAV writer (mono, 16-bit).</summary>
public static class WavWriter
{
    public const int HeaderBytes = 44;

    public static byte[] Header(int dataBytes, int sampleRate, short channels = 1, short bitsPerSample = 16)
    {
        var h = new byte[HeaderBytes];
        var blockAlign = (short)(channels * bitsPerSample / 8);
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), (uint)(36 + dataBytes));
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(20), 1); // PCM
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(22), channels);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(24), sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(28), sampleRate * blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(32), blockAlign);
        BinaryPrimitives.WriteInt16LittleEndian(h.AsSpan(34), bitsPerSample);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), (uint)dataBytes);
        return h;
    }

    public static void Write(Stream output, ReadOnlySpan<byte> pcm, int sampleRate)
    {
        output.Write(Header(pcm.Length, sampleRate));
        output.Write(pcm);
    }
}
