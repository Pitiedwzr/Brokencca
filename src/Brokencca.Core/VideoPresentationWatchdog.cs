namespace Brokencca.Core;

public enum VideoPresentationHealth { Healthy, FirstFrameTimedOut, Stalled }

/// <summary>Times outstanding transmitted frames, not source inactivity. Shared by sender and feedback receiver.</summary>
public sealed class VideoPresentationWatchdog
{
    private readonly object gate = new();
    private ulong sent, presented;
    private long firstSentUs, pendingSinceUs;
    private bool started;

    public void Sent(ulong frame, long nowUs)
    {
        lock (gate)
        {
            if (!started) { started = true; firstSentUs = nowUs; }
            if (sent <= presented) pendingSinceUs = nowUs;
            sent = frame;
        }
    }

    public void Presented(ulong frame, long nowUs)
    {
        lock (gate)
        {
            if (frame <= presented) return; // repeated feedback must not extend a stalled deadline
            presented = frame;
            if (presented < sent) pendingSinceUs = nowUs;
        }
    }

    public VideoPresentationHealth Evaluate(long nowUs)
    {
        lock (gate)
        {
            if (!started || sent <= presented) return VideoPresentationHealth.Healthy;
            if (presented == 0) return nowUs-firstSentUs > 3_000_000
                ? VideoPresentationHealth.FirstFrameTimedOut : VideoPresentationHealth.Healthy;
            return nowUs-pendingSinceUs > 250_000 ? VideoPresentationHealth.Stalled : VideoPresentationHealth.Healthy;
        }
    }
}
