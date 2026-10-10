namespace Brokencca.Core;

public enum VideoRecoveryReason { Failure, Decoder, Thermal }

/// <summary>Video-only recovery budgets. Cooling is a wait condition, not a failed retry.</summary>
public sealed class VideoRecoveryPolicy(int bitrate, int longEdge)
{
    public int Bitrate { get; private set; } = bitrate;
    public int LongEdge { get; private set; } = longEdge;
    public int Failures { get; private set; }

    public int? RetryDelayMs(VideoRecoveryReason reason, double connectedSeconds)
    {
        if (connectedSeconds >= 30) Failures = 0;
        if (reason == VideoRecoveryReason.Thermal)
        {
            Bitrate = Math.Min(Bitrate, 6_000_000);
            LongEdge = Math.Min(LongEdge, 960);
            return 5000;
        }
        if (++Failures >= 3) return null;
        if (reason == VideoRecoveryReason.Failure)
        {
            Bitrate = Math.Min(Bitrate, Math.Max(6_000_000, Bitrate * 3 / 4));
            if (Bitrate <= 6_000_000) LongEdge = Math.Min(LongEdge, 960);
        }
        // A decoder reset gets a fresh IDR at the same quality; short stalls
        // must not progressively reduce picture quality.
        return Failures == 1 ? 250 : 500;
    }
}
