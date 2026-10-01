using System.Buffers.Binary;

namespace Brokencca.Core;

// Independent, host->iOS latest-frame stream on 24866. Input v1 is unchanged.
public static class LedProtocol
{
    public const int HeaderSize = 16, PayloadSize = 1924;
    public static byte[] Encode(uint sequence, ReadOnlySpan<byte> payload)
    {
        if (payload.Length != PayloadSize) throw new InvalidDataException("Invalid LED payload length.");
        var packet = new byte[HeaderSize + PayloadSize];
        "BCLD"u8.CopyTo(packet); packet[4] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), PayloadSize);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), sequence);
        payload.CopyTo(packet.AsSpan(HeaderSize)); return packet;
    }
    public static async Task<(uint Sequence, byte[] Payload)> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[HeaderSize]; await stream.ReadExactlyAsync(header, token);
        if (!header.AsSpan(0, 4).SequenceEqual("BCLD"u8) || header[4] != 1 || header[5] != 0 || header[6] != 0 || header[7] != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8)) != PayloadSize)
            throw new InvalidDataException("Invalid LED stream header.");
        byte[] payload = new byte[PayloadSize]; await stream.ReadExactlyAsync(payload, token);
        return (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)), payload);
    }
}
