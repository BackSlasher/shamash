using System.Buffers.Binary;

namespace Shamash.Pc;

/// <summary>One binary probe frame: uint32 BE seq, int64 BE phone send time (ms), then raw PCM.</summary>
public readonly record struct LinkFrame(uint Seq, long TPhoneMs, ReadOnlyMemory<byte> Pcm)
{
    public const int HeaderBytes = 12;

    /// <summary>Returns false (and ignores the message) when it is shorter than the 12-byte header.</summary>
    public static bool TryParse(ReadOnlyMemory<byte> message, out LinkFrame frame)
    {
        if (message.Length < HeaderBytes)
        {
            frame = default;
            return false;
        }
        var span = message.Span;
        frame = new LinkFrame(
            BinaryPrimitives.ReadUInt32BigEndian(span),
            BinaryPrimitives.ReadInt64BigEndian(span[4..]),
            message[HeaderBytes..]);
        return true;
    }
}
