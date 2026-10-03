using Brokencca.Core;

internal static class VideoPresentationTests
{
    public static void Watchdog()
    {
        var w = new VideoPresentationWatchdog();
        Check(w.Evaluate(10_000_000) == VideoPresentationHealth.Healthy);
        w.Sent(1,1_000_000);
        Check(w.Evaluate(4_000_000) == VideoPresentationHealth.Healthy);
        Check(w.Evaluate(4_000_001) == VideoPresentationHealth.FirstFrameTimedOut);
        w.Presented(1,4_000_002);
        Check(w.Evaluate(20_000_000) == VideoPresentationHealth.Healthy);
        // Actual game trace: a 919 ms source gap must grant the resumed frame
        // its own 250 ms budget, without reconnecting or lowering the bitrate.
        w.Sent(2,20_919_000);
        Check(w.Evaluate(20_919_001) == VideoPresentationHealth.Healthy);
        Check(w.Evaluate(21_169_000) == VideoPresentationHealth.Healthy);
        w.Presented(1,21_169_000); // repeated feedback cannot keep it alive
        Check(w.Evaluate(21_169_001) == VideoPresentationHealth.Stalled);
        w.Presented(2,21_169_002);
        w.Sent(3,22_000_000); w.Sent(4,22_200_000);
        Check(w.Evaluate(22_250_001) == VideoPresentationHealth.Stalled); // sending isn't presentation progress
        w.Presented(3,22_250_002);
        Check(w.Evaluate(22_500_002) == VideoPresentationHealth.Healthy);
        Check(w.Evaluate(22_500_003) == VideoPresentationHealth.Stalled);
        w.Presented(4,22_500_004);
        Check(w.Evaluate(40_000_000) == VideoPresentationHealth.Healthy);
        // A static source whose final frame never presents is still detected.
        w.Sent(5,40_000_000);
        Check(w.Evaluate(40_250_001) == VideoPresentationHealth.Stalled);
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Presentation watchdog assertion failed."); }
}
