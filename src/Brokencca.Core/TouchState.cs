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
}
