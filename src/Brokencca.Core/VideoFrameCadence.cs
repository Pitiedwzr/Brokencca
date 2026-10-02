namespace Brokencca.Core;

/// <summary>Nominal 60 Hz admission anchored to source time, allowing 1 ms capture jitter.</summary>
public sealed class VideoFrameCadence
{
    private long next, previous;
    public bool Admit(long timestamp100ns)
    {
        if (timestamp100ns <= previous || (next != 0 && timestamp100ns + 10_000 < next)) return false;
        previous = timestamp100ns;
        long interval = 10_000_000 / 60;
        next = next == 0 || timestamp100ns >= next + interval ? timestamp100ns + interval : next + interval;
        return true;
    }
}
