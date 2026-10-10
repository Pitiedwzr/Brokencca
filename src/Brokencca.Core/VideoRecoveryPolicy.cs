namespace Brokencca.Core;

public enum VideoRecoveryReason { Failure, Decoder, Thermal, Congestion }

/// <summary>Video-only recovery budgets. Cooling is a wait condition, not a failed retry.</summary>
public sealed class VideoRecoveryPolicy(int bitrate, int longEdge)
{
    private readonly int hostBitrate = bitrate;
    public int Bitrate { get; private set; } = bitrate;
    public int LongEdge { get; private set; } = longEdge;
    public int Failures { get; private set; }
    public VideoPreferences Preferences { get; private set; } = new();

    public bool ApplyPreferences(VideoPreferences preferences)
    {
        preferences.Validate();
        if (preferences == Preferences) return false;
        Preferences = preferences;
        Bitrate = preferences.BitrateBps == 0 ? hostBitrate : preferences.BitrateBps;
        Failures = 0;
        return true;
    }

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
        if (reason == VideoRecoveryReason.Congestion && Preferences.AutomaticReduction)
        {
            Bitrate = Math.Min(Bitrate, Math.Max(6_000_000, Bitrate * 3 / 4));
            if (Bitrate <= 6_000_000) LongEdge = Math.Min(LongEdge, 960);
        }
        // A decoder reset gets a fresh IDR at the same quality; short stalls
        // must not progressively reduce picture quality.
        return Failures == 1 ? 250 : 500;
    }
}
