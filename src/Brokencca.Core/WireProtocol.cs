using System.Buffers.Binary;
using System.Diagnostics;

namespace Brokencca.Core;

public enum MessageType : byte { Hello = 1, Touch = 2, Reset = 3, Ping = 4, Pong = 5 }
public sealed record WireMessage(MessageType Type, uint Sequence, ulong TimestampUs, byte[] Payload);

public static class WireProtocol
{
    public const int HeaderSize = 24;
    public const int MaxPayload = 4096;
    public static byte[] HelloPayload => [240, 0, 30, 0];
    public static ulong NowUs => (ulong)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));

    public static byte[] Encode(WireMessage message)
    {
        Validate(message.Type, message.Payload);
        var bytes = new byte[HeaderSize + message.Payload.Length];
        "BCCA"u8.CopyTo(bytes);
        bytes[4] = 1;
        bytes[5] = (byte)message.Type;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), (uint)message.Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), message.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(16), message.TimestampUs);
        message.Payload.CopyTo(bytes, HeaderSize);
        return bytes;
    }

    public static async ValueTask<WireMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        var header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, token);
        if (!header.AsSpan(0, 4).SequenceEqual("BCCA"u8) || header[4] != 1 || header[6] != 0 || header[7] != 0)
            throw new InvalidDataException("Invalid Brokencca header or protocol version.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (length > MaxPayload) throw new InvalidDataException("Control payload exceeds limit.");
        var type = (MessageType)header[5];
        ValidateLength(type, (int)length);
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, token);
        Validate(type, payload);
        return new(type, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16)), payload);
    }

    public static ValueTask WriteAsync(Stream stream, WireMessage message, CancellationToken token) =>
        stream.WriteAsync(Encode(message), token);

    private static void ValidateLength(MessageType type, int length)
    {
        int expected = type switch
        {
            MessageType.Hello => 4, MessageType.Touch => 30,
            MessageType.Reset or MessageType.Ping or MessageType.Pong => 0,
            _ => throw new InvalidDataException("Unknown control message type.")
        };
        if (length != expected) throw new InvalidDataException($"Invalid {type} payload length {length}.");
    }

    private static void Validate(MessageType type, byte[] payload)
    {
        ValidateLength(type, payload.Length);
        if (type == MessageType.Hello && !payload.AsSpan().SequenceEqual(HelloPayload))
            throw new InvalidDataException("Unsupported touch layout.");
    }
}
