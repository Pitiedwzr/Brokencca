namespace Brokencca.Core;

public sealed class TouchState
{
    public const int ZoneCount = 240;
    public const int ByteCount = 30;
    private readonly byte[] bytes;
    public static TouchState Empty { get; } = new(new byte[ByteCount]);

    public TouchState(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != ByteCount) throw new ArgumentException("Expected a 30-byte touch bitmap.");
        this.bytes = bytes.ToArray();
    }

    public bool this[int zone] => zone is >= 0 and < ZoneCount
        ? (bytes[zone / 8] & (1 << (zone % 8))) != 0
        : throw new ArgumentOutOfRangeException(nameof(zone));
    public byte[] ToArray() => (byte[])bytes.Clone();
    public bool SameAs(TouchState other) => bytes.AsSpan().SequenceEqual(other.bytes);
}

// Adapted from toucca/web/controller.js (GPL-3.0-or-later).
public static class TouchGeometry
{
    public const double SectorMargin = 0.25;
    public const double RadialMargin = 0.25;

    // Compatibility behavior: no inner/outer radius rejection; exact centre line is ignored.
    public static int? ZoneAt(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width <= 0 || height <= 0) return null;
        double dx = x - width / 2, dy = y - height / 2;
        if (dx == 0) return null;
        double radius = Math.Min(width, height) / 2;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        int ring = 0;
        foreach (double threshold in new[] { 0.7, 0.8, 0.9 })
            if (distance > radius * threshold) ring++;
        double angle = Math.Atan(dy / dx) * (dx < 0 ? -1 : 1);
        int sector = Math.Clamp((int)Math.Floor((angle + Math.PI / 2) / (Math.PI / 30)), 0, 29);
        return (dx < 0 ? 120 : 0) + ring * 30 + sector;
    }

    // Populates active zones (primary + boundary-expanded neighbors).
    public static int[] ZonesForPoint(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width <= 0 || height <= 0) return [];
        double dx = x - width / 2, dy = y - height / 2;
        double radius = Math.Min(width, height) / 2;
        double distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance == 0 || radius <= 0) return [];

        int primaryRing = 0;
        foreach (double threshold in new[] { 0.7, 0.8, 0.9 })
            if (distance > radius * threshold) primaryRing++;

        Span<int> rings = stackalloc int[2];
        int ringCount = 1;
        rings[0] = primaryRing;

        double ringMargin = RadialMargin * 0.1 * radius;
        if (primaryRing == 0 && distance > 0.7 * radius - ringMargin)
            rings[ringCount++] = 1;
        else if (primaryRing == 1)
        {
            if (distance < 0.7 * radius + ringMargin) rings[ringCount++] = 0;
            else if (distance > 0.8 * radius - ringMargin) rings[ringCount++] = 2;
        }
        else if (primaryRing == 2)
        {
            if (distance < 0.8 * radius + ringMargin) rings[ringCount++] = 1;
            else if (distance > 0.9 * radius - ringMargin) rings[ringCount++] = 3;
        }
        else if (primaryRing == 3 && distance < 0.9 * radius + ringMargin)
            rings[ringCount++] = 2;

        Span<int> sides = stackalloc int[2];
        Span<int> sectors = stackalloc int[2];
        int sectorCount;

        if (dx == 0)
        {
            if (dy < 0)
            {
                sides[0] = 0; sectors[0] = 0;
                sides[1] = 1; sectors[1] = 0;
                sectorCount = 2;
            }
            else if (dy > 0)
            {
                sides[0] = 0; sectors[0] = 29;
                sides[1] = 1; sectors[1] = 29;
                sectorCount = 2;
            }
            else return [];
        }
        else
        {
            int primarySide = dx < 0 ? 1 : 0;
            double angle = Math.Atan(dy / dx) * (dx < 0 ? -1 : 1);
            double sectorPos = (angle + Math.PI / 2) / (Math.PI / 30);
            int primarySector = Math.Clamp((int)Math.Floor(sectorPos), 0, 29);

            sides[0] = primarySide;
            sectors[0] = primarySector;
            sectorCount = 1;

            double frac = sectorPos - primarySector;
            if (frac < SectorMargin)
            {
                if (primarySector > 0)
                {
                    sides[1] = primarySide;
                    sectors[1] = primarySector - 1;
                    sectorCount = 2;
                }
                else
                {
                    sides[1] = primarySide ^ 1;
                    sectors[1] = 0;
                    sectorCount = 2;
                }
            }
            else if (frac > 1.0 - SectorMargin)
            {
                if (primarySector < 29)
                {
                    sides[1] = primarySide;
                    sectors[1] = primarySector + 1;
                    sectorCount = 2;
                }
                else
                {
                    sides[1] = primarySide ^ 1;
                    sectors[1] = 29;
                    sectorCount = 2;
                }
            }
        }

        var result = new int[ringCount * sectorCount];
        int idx = 0;
        for (int r = 0; r < ringCount; r++)
            for (int s = 0; s < sectorCount; s++)
                result[idx++] = sides[s] * 120 + rings[r] * 30 + sectors[s];
        return result;
    }

    public static void ApplyTouch(double x, double y, double width, double height, Span<byte> bitmap)
    {
        foreach (int zone in ZonesForPoint(x, y, width, height))
            if ((uint)zone < TouchState.ZoneCount)
                bitmap[zone / 8] |= (byte)(1 << (zone % 8));
    }
}
