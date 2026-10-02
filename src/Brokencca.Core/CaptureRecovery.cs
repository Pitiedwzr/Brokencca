namespace Brokencca.Core;

/// <summary>Only device failures get automatic retries; unsupported/geometry/source errors do not.</summary>
public sealed class CaptureRecovery
{
    private int attempts;
    public int Attempts => attempts;
    public TimeSpan? NextDelay(int hresult)
    {
        if (hresult != unchecked((int)0x887A0005) && hresult != unchecked((int)0x887A0006) && hresult != unchecked((int)0x887A0007)) return null;
        if (attempts >= 3) return null;
        return TimeSpan.FromMilliseconds(new[] { 250, 500, 1000 }[attempts++]);
    }
}
