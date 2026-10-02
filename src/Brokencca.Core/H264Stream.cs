using System.Buffers.Binary;

namespace Brokencca.Core;

public sealed record H264Format(int Width, int Height, string Profile, int Level);
public sealed record H264AccessUnit(byte[] Bytes, bool IsIdr);

/// <summary>Normalizes MF AVC samples and checks the first-release progressive, non-B-frame contract.</summary>
public sealed class H264Stream
{
    public byte[]? Sps { get; private set; }
    public byte[]? Pps { get; private set; }
    public H264Format? Format { get; private set; }
    public void SetHeaders(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if (bytes[0] == 1 && bytes.Length >= 7) // AVCDecoderConfigurationRecord
        {
            if ((bytes[4] & 3) != 3) throw new InvalidDataException("Encoder AVC NAL prefix must be four bytes.");
            int offset = 6;
            for (int i = 0; i < (bytes[5] & 31); i++) ReadParameter(bytes, ref offset);
            if (offset >= bytes.Length) throw new InvalidDataException("Truncated AVC configuration.");
            int count = bytes[offset++];
            for (int i = 0; i < count; i++) ReadParameter(bytes, ref offset);
            return;
        }
        foreach (byte[] nal in Split(bytes)) Parameter(nal);
    }
    private void ReadParameter(ReadOnlySpan<byte> bytes, ref int offset)
    {
        if (offset + 2 > bytes.Length) throw new InvalidDataException("Truncated parameter set.");
        int size = BinaryPrimitives.ReadUInt16BigEndian(bytes[offset..]); offset += 2;
        if (size == 0 || size > bytes.Length - offset) throw new InvalidDataException("Invalid parameter-set size.");
        Parameter(bytes.Slice(offset, size).ToArray()); offset += size;
    }
    private void Parameter(byte[] nal)
    {
        if (nal.Length == 0 || (nal[0] & 128) != 0) throw new InvalidDataException("Invalid H.264 NAL.");
        int type = nal[0] & 31;
        if (type is not (7 or 8)) return;
        if (nal.Length > 1024) throw new InvalidDataException("Parameter set exceeds limit.");
        byte[]? previous = type == 7 ? Sps : Pps;
        if (previous is not null && !previous.AsSpan().SequenceEqual(nal))
            throw new InvalidDataException($"H.264 parameter set {type} changed; restart video generation ({Convert.ToHexString(previous)} -> {Convert.ToHexString(nal)}).");
        if (type == 7) { Sps = nal; Format = ParseSps(nal); } else Pps = nal;
    }
    public H264AccessUnit Normalize(ReadOnlySpan<byte> sample)
    {
        List<byte[]> nals = Split(sample);
        bool idr = false;
        int pictures = 0;
        foreach (byte[] nal in nals)
        {
            Parameter(nal);
            int type = nal[0] & 31;
            if (type is >= 2 and <= 4) throw new InvalidDataException("Partitioned H.264 pictures are unsupported.");
            if (type is not (1 or 5)) continue;
            var bits = new Bits(nal.AsSpan(1));
            uint firstMb = bits.UE(), sliceType = bits.UE();
            if (sliceType > 9 || sliceType % 5 == 1) throw new InvalidDataException("Encoder produced a B slice.");
            if (firstMb == 0) pictures++;
            if (type == 5) idr = true;
        }
        if (pictures != 1) throw new InvalidDataException("Encoder output must contain exactly one complete picture.");
        int size = nals.Sum(nal => checked(nal.Length + 4));
        if (size > VideoProtocol.MaxAccessUnit) throw new InvalidDataException("Encoded picture exceeds video limit.");
        byte[] avcc = new byte[size]; int position = 0;
        foreach (byte[] nal in nals)
        {
            BinaryPrimitives.WriteUInt32BigEndian(avcc.AsSpan(position), (uint)nal.Length); position += 4;
            nal.CopyTo(avcc, position); position += nal.Length;
        }
        VideoProtocol.ValidateAvcc(avcc, idr);
        return new(avcc, idr);
    }
    public static List<byte[]> Split(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0 || data.Length > VideoProtocol.MaxAccessUnit) throw new InvalidDataException("Invalid H.264 sample size.");
        List<byte[]> result = [];
        int prefix = Prefix(data, 0);
        if (prefix > 0)
        {
            int start = prefix;
            while (start < data.Length)
            {
                int next = start;
                while (next < data.Length && Prefix(data, next) == 0) next++;
                int end = next;
                while (end > start && data[end - 1] == 0) end--; // Annex B trailing_zero_8bits
                if (end == start) throw new InvalidDataException("Empty Annex B NAL.");
                result.Add(data[start..end].ToArray());
                if (next == data.Length) break;
                start = next + Prefix(data, next);
            }
        }
        else
        {
            int offset = 0;
            while (offset < data.Length)
            {
                if (data.Length - offset < 5) throw new InvalidDataException("Truncated AVC sample.");
                uint size = BinaryPrimitives.ReadUInt32BigEndian(data[offset..]); offset += 4;
                if (size == 0 || size > data.Length - offset) throw new InvalidDataException("Invalid AVC NAL length.");
                result.Add(data.Slice(offset, (int)size).ToArray()); offset += (int)size;
            }
        }
        if (result.Count == 0) throw new InvalidDataException("No H.264 NAL units.");
        return result;
    }
    private static int Prefix(ReadOnlySpan<byte> data, int index)
    {
        if (data.Length - index >= 3 && data[index] == 0 && data[index + 1] == 0)
        {
            if (data[index + 2] == 1) return 3;
            if (data.Length - index >= 4 && data[index + 2] == 0 && data[index + 3] == 1) return 4;
        }
        return 0;
    }
    public static H264Format ParseSps(ReadOnlySpan<byte> sps)
    {
        if (sps.Length is < 5 or > 1024 || (sps[0] & 31) != 7) throw new InvalidDataException("Invalid SPS.");
        var bits = new Bits(sps[1..]);
        uint profile = bits.Read(8); bits.Read(8); int level = (int)bits.Read(8); bits.UE();
        if (profile is not (66 or 77) || level is < 1 or > 42) throw new InvalidDataException("Unsupported H.264 profile/level.");
        if (bits.UE() > 12) throw new InvalidDataException("Invalid frame numbering.");
        uint poc = bits.UE();
        if (poc == 0) { if (bits.UE() > 12) throw new InvalidDataException("Invalid picture ordering."); }
        else if (poc == 1)
        {
            bits.Read(1); bits.SE(); bits.SE(); uint cycle = bits.UE();
            if (cycle > 256) throw new InvalidDataException("Invalid POC cycle.");
            for (uint i = 0; i < cycle; i++) bits.SE();
        }
        else if (poc != 2) throw new InvalidDataException("Invalid picture-order type.");
        bits.UE(); bits.Read(1);
        uint mbWidth = bits.UE() + 1, mbHeight = bits.UE() + 1;
        if (mbWidth > 120 || mbHeight > 120 || bits.Read(1) != 1) throw new InvalidDataException("Unsupported SPS dimensions/interlacing.");
        bits.Read(1);
        uint left = 0, right = 0, top = 0, bottom = 0;
        if (bits.Read(1) == 1) { left = bits.UE(); right = bits.UE(); top = bits.UE(); bottom = bits.UE(); }
        long width = (long)mbWidth * 16 - 2L * ((long)left + right);
        long height = (long)mbHeight * 16 - 2L * ((long)top + bottom);
        if (width < 2 || height < 2 || width > 1920 || height > 1920 || width * height > 2073600)
            throw new InvalidDataException("Invalid SPS crop.");
        return new((int)width, (int)height, profile == 77 ? "main" : "baseline", level);
    }
    private sealed class Bits
    {
        private readonly byte[] bytes;
        private int position;
        public Bits(ReadOnlySpan<byte> rbsp)
        {
            List<byte> clean = []; int zeros = 0;
            foreach (byte b in rbsp)
            {
                if (zeros >= 2 && b == 3) { zeros = 0; continue; }
                clean.Add(b); zeros = b == 0 ? zeros + 1 : 0;
            }
            bytes = clean.ToArray();
        }
        public uint Read(int count)
        {
            if (count is < 0 or > 32 || position + count > bytes.Length * 8) throw new InvalidDataException("Truncated H.264 bits.");
            uint value = 0;
            for (int i = 0; i < count; i++, position++) value = (value << 1) | (uint)((bytes[position / 8] >> (7 - position % 8)) & 1);
            return value;
        }
        public uint UE()
        {
            int zeros = 0;
            while (Read(1) == 0) if (++zeros > 30) throw new InvalidDataException("Invalid Exp-Golomb code.");
            return (1u << zeros) - 1 + Read(zeros);
        }
        public int SE() { uint value = UE(); return (value & 1) == 0 ? -(int)(value / 2) : (int)((value + 1) / 2); }
    }
}
