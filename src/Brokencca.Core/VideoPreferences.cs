namespace Brokencca.Core;

public sealed record VideoPreferences(int BitrateBps = 0, bool AutomaticReduction = true)
{
    public void Validate()
    {
        if (BitrateBps != 0 && BitrateBps is < 6_000_000 or > 20_000_000)
            throw new ArgumentException("Preferred video bitrate must be zero (host default) or 6–20 Mbps.");
    }
}
