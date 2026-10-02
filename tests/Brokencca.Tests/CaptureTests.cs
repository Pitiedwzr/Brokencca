using Brokencca.Core;

internal static class CaptureTests
{
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); }
    private static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Expected invalid geometry/profile rejection."); }
    public static void Geometry()
    {
        var client = new PixelRect(-1400, -220, 1280, 720);
        Check(new NormalizedCrop(.1, .1, .8, .8).ToPixels(client) == new PixelRect(-1272, -148, 1024, 576), "Physical pixels/negative origins.");
        Check(new NormalizedCrop(.11, .21, .3, .4).ToPixels(new(0, 0, 11, 13)) == new PixelRect(1, 2, 4, 6), "Half-open outward rounding.");
        Reject(() => new NormalizedCrop(double.NaN, 0, 1, 1).Validate());
        Reject(() => new NormalizedCrop(0, 0, 0, 1).Validate());
        Reject(() => new NormalizedCrop(.9, 0, .2, 1).Validate());
        var view = new CaptureViewport(new(20, 30, 1280, 720), 1000, 1000);
        var p = view.ToDisplay(333, 245);
        Check(view.TryToSource(p.X, p.Y, out double x, out double y) && Math.Abs(x - 333) < 1e-9 && Math.Abs(y - 245) < 1e-9, "Inverse transform.");
        Check(!view.TryToSource(500, 0, out _, out _), "Letterbox rejection.");
        Check(!view.TryToSource(1000, 500, out _, out _), "Exclusive edge.");
        var circle = PlayfieldCircle.TouccaReference(1280, 720, 1288, 728);
        var (_, cy, radius) = circle.ToPixels(1280, 720);
        Check(cy == (int)(718 * .938) / 2.0 && radius == cy, "Toucca integer placement and nonsquare circle.");
        var profile = new CaptureProfile(1, 1280, 720, 120, NormalizedCrop.Full, circle, true);
        profile.ValidateFor(1920, 1080);
        Reject(() => profile.ValidateFor(1000, 1000));
        Reject(() => (profile with { Confirmed = false }).ValidateFor(1280, 720));
    }
    public static void Slots()
    {
        var pool = new FrameSlotPool();
        int a = pool.Reserve(); Check(a == 0 && pool.Acquire() == a, "First lease.");
        Check(pool.Acquire() == -1, "Single consumer lease.");
        int b = pool.Reserve(), c = pool.Reserve();
        Check(b == 1 && c == 2 && pool.States[b] == FrameSlotState.Retiring && pool.Pending == c, "Newest pending replaces older frame.");
        Check(pool.Reserve() == -1 && pool.Leased == a, "Capacity cannot overwrite live/pending GPU work.");
        pool.Complete(b); Check(pool.Reserve() == b && pool.States[c] == FrameSlotState.Retiring, "Only completed slot reused.");
        pool.InvalidatePending(); pool.Release(a);
        Check(!pool.IsQuiescent && pool.Pending == -1 && pool.Leased == -1, "Resize invalidates pending, waits for GPU retirements.");
        pool.Complete(a); pool.Complete(b); pool.Complete(c); Check(pool.IsQuiescent, "All GPU fences complete.");
    }
    public static void Calibration()
    {
        const int w = 320, h = 220, stride = w * 4 + 16;
        byte[] Image(Func<int, int, bool> lit)
        {
            byte[] bytes = new byte[stride * h];
            for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (lit(x, y))
            { int p = y * stride + x * 4; bytes[p] = 180; bytes[p + 1] = 110; bytes[p + 2] = 70; bytes[p + 3] = 255; }
            return bytes;
        }
        CircleDetection? Detect(Func<int, int, bool> lit) => BlackBackgroundCalibration.Detect(Image(lit), w, h, stride);
        var disk = Detect((x, y) => Math.Pow(x - 175, 2) + Math.Pow(y - 103, 2) < 80 * 80);
        Check(disk is not null, "Lit circular screen on boot black is detected.");
        var (cx, cy, r) = disk!.Circle.ToPixels(w, h);
        Check(Math.Abs(cx - 175.5) < 1 && Math.Abs(cy - 103.5) < 1 && Math.Abs(r - 80) < 1, "Offset circle accurately recovered with row padding.");
        Check(Detect((_, _) => false) is null, "All-black is not calibration.");
        Check(Detect((_, _) => true) is null, "Solid rectangle rejected.");
        Check(Detect((x, y) => x is > 50 and < 250 && y is > 45 and < 170) is null, "Rectangular logo rejected.");
        Check(Detect((x, y) => Math.Pow((x - 175) / 1.6, 2) + Math.Pow(y - 103, 2) < 70 * 70) is null, "Ellipse rejected.");
        Check(Detect((x, y) => Math.Pow(x - 20, 2) + Math.Pow(y - 100, 2) < 80 * 80) is null, "Clipped circle rejected.");
        Check(Detect((x, y) => Math.Pow(x - 160, 2) + Math.Pow(y - 100, 2) < 12 * 12) is null, "Small loading icon rejected.");
        Check(Detect((x, y) => Math.Abs(Math.Sqrt(Math.Pow(x - 175, 2) + Math.Pow(y - 103, 2)) - 80) < 3) is not null, "Continuous outer ring can define an otherwise black interior.");
        var consensus = new CircleConsensus();
        Check(consensus.Observe(disk.Circle) is null && consensus.Observe(disk.Circle) is null && consensus.Observe(disk.Circle) is not null, "Three consistent boot observations.");
        Check(consensus.Observe(null) is null && consensus.Observe(disk.Circle) is null, "Dark frame breaks consensus.");
        Check(consensus.Observe(disk.Circle with { Radius = .2 }) is null, "Changing content cannot confirm old suggestion.");
    }
    public static void Recovery()
    {
        var recovery = new CaptureRecovery(); int lost = unchecked((int)0x887A0005);
        Check(recovery.NextDelay(unchecked((int)0x80070005)) is null && recovery.Attempts == 0, "Access denial does not retry as device loss.");
        Check(recovery.NextDelay(lost)?.TotalMilliseconds == 250 && recovery.NextDelay(lost)?.TotalMilliseconds == 500 && recovery.NextDelay(lost)?.TotalMilliseconds == 1000 && recovery.NextDelay(lost) is null, "Three bounded recovery attempts.");
    }
}
