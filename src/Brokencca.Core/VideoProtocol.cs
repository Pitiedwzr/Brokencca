using System.Buffers.Binary;
using System.Text.Json;

namespace Brokencca.Core;

public enum VideoMessageType : byte
{
    Hello = 1, HelloAck = 2, Config = 3, Ready = 4, AccessUnit = 5,
    Feedback = 6, RequestIdr = 7, ClockPing = 8, ClockPong = 9, Status = 10, Error = 11
}

public sealed record VideoMessage(VideoMessageType Type, uint Sequence, ulong Generation,
    ulong FrameId, ulong CaptureTimestampUs, byte[] Payload, bool IsIdr = false);

/// <summary>Video v1 framing. Session ordering and JSON field semantics belong to the video session owner.</summary>
public static class VideoProtocol
{
    public const int HeaderSize = 40;
    public const int MaxJsonPayload = 16 * 1024;
    public const int MaxAccessUnit = 2 * 1024 * 1024;

    public static byte[] Encode(VideoMessage message)
    {
        Validate(message);
        byte[] packet = new byte[checked(HeaderSize + message.Payload.Length)];
        "BCVD"u8.CopyTo(packet);
        packet[4] = 1;
        packet[5] = (byte)message.Type;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), message.IsIdr ? (ushort)1 : (ushort)0);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), (uint)message.Payload.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), message.Sequence);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(16), message.Generation);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(24), message.FrameId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(32), message.CaptureTimestampUs);
        message.Payload.CopyTo(packet, HeaderSize);
        return packet;
    }

    public static async ValueTask<VideoMessage> ReadAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[HeaderSize];
        await stream.ReadExactlyAsync(header, token);
        if (!header.AsSpan(0, 4).SequenceEqual("BCVD"u8) || header[4] != 1)
            throw new InvalidDataException("Invalid video header or version.");
        var type = (VideoMessageType)header[5];
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(6));
        if ((byte)type is < 1 or > 11 || flags > (type == VideoMessageType.AccessUnit ? 1 : 0))
            throw new InvalidDataException("Invalid video type or flags.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
        if (length > (type == VideoMessageType.AccessUnit ? MaxAccessUnit : MaxJsonPayload))
            throw new InvalidDataException("Video payload exceeds limit.");
        var payload = new byte[(int)length];
        await stream.ReadExactlyAsync(payload, token);
        var message = new VideoMessage(type, BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(16)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(24)),
            BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(32)), payload, flags == 1);
        Validate(message);
        return message;
    }

    public static ValueTask WriteAsync(Stream stream, VideoMessage message, CancellationToken token) =>
        stream.WriteAsync(Encode(message), token);

    public static void Validate(VideoMessage message)
    {
        if ((byte)message.Type is < 1 or > 11) throw new InvalidDataException("Unknown video message type.");
        if (message.Payload is null) throw new InvalidDataException("Video payload is null.");
        if (message.Type == VideoMessageType.AccessUnit)
        {
            if (message.Payload.Length > MaxAccessUnit || message.FrameId == 0 ||
                message.CaptureTimestampUs == 0 || message.Generation == 0)
                throw new InvalidDataException("Invalid access-unit metadata or length.");
            ValidateAvcc(message.Payload, message.IsIdr);
        }
        else
        {
            if (message.Payload.Length is 0 or > MaxJsonPayload || message.IsIdr ||
                message.FrameId != 0 || message.CaptureTimestampUs != 0)
                throw new InvalidDataException("Invalid video control metadata or length.");
            bool handshake = message.Type is VideoMessageType.Hello or VideoMessageType.HelloAck;
            if (handshake ? message.Generation != 0 : message.Type != VideoMessageType.Error && message.Generation == 0)
                throw new InvalidDataException("Invalid video generation.");
            JsonDocument doc;
            try { doc = JsonDocument.Parse(message.Payload, new JsonDocumentOptions { MaxDepth = 4 }); }
            catch (JsonException e) { throw new InvalidDataException("Malformed video JSON.", e); }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected video JSON object.");
                RejectDuplicateKeys(doc.RootElement);
                VideoJson.Validate(message.Type, doc.RootElement);
            }
        }
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate video JSON key.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) RejectDuplicateKeys(child);
    }

    public static void ValidateAvcc(ReadOnlySpan<byte> payload, bool isIdr)
    {
        int position = 0;
        bool hasIdr = false, hasPicture = false;
        while (position < payload.Length)
        {
            if (payload.Length - position < 5) throw new InvalidDataException("Truncated AVC NAL length.");
            uint length = BinaryPrimitives.ReadUInt32BigEndian(payload[position..]);
            position += 4;
            if (length == 0 || length > payload.Length - position) throw new InvalidDataException("Invalid AVC NAL length.");
            byte nalType = (byte)(payload[position] & 0x1f);
            if (nalType is 1 or 5) hasPicture = true;
            if (nalType == 5) hasIdr = true;
            position += (int)length;
        }
        if (!hasPicture || isIdr != hasIdr) throw new InvalidDataException("AVC picture/IDR flag mismatch.");
    }
}
