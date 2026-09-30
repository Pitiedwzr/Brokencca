using System.Text;

namespace Brokencca.Core;

// Packet layout and response constants adapted from toucca/SerialManager.cs (GPL-3.0-or-later).
public static class SerialPackets
{
    public static (byte[] Left, byte[] Right) Encode(TouchState state, byte counter)
    {
        if (counter > 127) throw new ArgumentOutOfRangeException(nameof(counter));
        byte[] left = new byte[36], right = new byte[36];
        for (int zone = 0; zone < TouchState.ZoneCount; zone++)
        {
            if (!state[zone]) continue;
            int area = (zone >= 120 ? zone - 120 : zone + 120) + 1;
            byte[] packet = area < 121 ? right : left;
            if (area >= 121) area -= 120;
            int bit = area + (area - 1) / 5 * 3 + 7;
            packet[bit / 8] |= (byte)(1 << (bit % 8));
        }
        foreach (byte[] packet in new[] { left, right })
        {
            packet[0] = 129;
            packet[34] = counter;
            packet[35] = (byte)(Xor(packet.AsSpan(0, 35)) ^ 128);
        }
        return (left, right);
    }

    public static byte Xor(ReadOnlySpan<byte> bytes)
    {
        byte value = 0;
        foreach (byte b in bytes) value ^= b;
        return value;
    }

    public static byte[] Response(byte command, bool leftPort, byte address = 0)
    {
        switch (command)
        {
            case 0xa0: return [0xa0, .. Encoding.ASCII.GetBytes("190523"), 44];
            case 0xa8:
                return [0xa8, .. Encoding.ASCII.GetBytes("190523"), (byte)(leftPort ? 'R' : 'L'),
                    .. Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("190514", 6))),
                    (byte)(leftPort ? 118 : 104)];
            case 0xa2: return [162, 63, 29];
            case 0x94: return [148, 0, 20];
            case 0xc9: return [201, 0, 73];
            case 0x72:
                string? value = address switch
                {
                    0x30 => "    0    0    1    2    3    4    5   15   15   15   15   15   15   11   11   11",
                    0x31 => "   11   11   11  128  103  103  115  138  127  103  105  111  126  113   95  100",
                    0x33 => "  101  115   98   86   76   67   68   48  117    0   82  154    0    6   35    4",
                    _ => null
                };
                if (value is null) return [];
                byte[] bytes = Encoding.ASCII.GetBytes(value);
                return [.. bytes, Xor(bytes)];
            default: return [];
        }
    }
}
