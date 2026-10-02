namespace Brokencca.Core;

public enum FrameSlotState { Free, Pending, Leased, Retiring }

/// <summary>Caller serializes operations and retires a slot only after its GPU fence completes.</summary>
public sealed class FrameSlotPool
{
    private readonly FrameSlotState[] states = new FrameSlotState[3];
    public IReadOnlyList<FrameSlotState> States => states;
    public int Pending => Array.IndexOf(states, FrameSlotState.Pending);
    public int Leased => Array.IndexOf(states, FrameSlotState.Leased);
    public int Reserve()
    {
        int free = Array.IndexOf(states, FrameSlotState.Free);
        if (free < 0) return -1;
        int pending = Pending;
        if (pending >= 0) states[pending] = FrameSlotState.Retiring;
        states[free] = FrameSlotState.Pending;
        return free;
    }
    public int Acquire()
    {
        if (Leased >= 0) return -1;
        int pending = Pending;
        if (pending >= 0) states[pending] = FrameSlotState.Leased;
        return pending;
    }
    public void Release(int index)
    {
        if (states[index] != FrameSlotState.Leased) throw new InvalidOperationException("Slot has no active lease.");
        states[index] = FrameSlotState.Retiring;
    }
    public void Complete(int index)
    {
        if (states[index] != FrameSlotState.Retiring) throw new InvalidOperationException("Only retired slots can become free.");
        states[index] = FrameSlotState.Free;
    }
    public void InvalidatePending() { int pending = Pending; if (pending >= 0) states[pending] = FrameSlotState.Retiring; }
    public bool IsQuiescent => states.All(s => s == FrameSlotState.Free);
}
