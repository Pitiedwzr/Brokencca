using System.Diagnostics;
using Brokencca.Core;

namespace Brokencca.Host;

// Producers never perform I/O. Only the serial owner may take/activate entries.
internal sealed class TouchOutputQueue(HostDiagnostics? diagnostics)
{
    internal readonly record struct Entry(TouchState State, long Generation, long Enqueued, long Received);
    private readonly object gate = new();
    private readonly Queue<Entry> pending = new();
    private TouchState accepted = TouchState.Empty;
    private TouchState current = TouchState.Empty;
    private long generation;
    internal const int Capacity = 64;

    public void Apply(TouchState state, long received)
    {
        lock (gate)
        {
            if (accepted.SameAs(state)) return;
            if (pending.Count >= Capacity)
            {
                diagnostics?.QueueOverflow();
                ResetLocked();
                throw new IOException("Serial input queue overflow; releasing all touches.");
            }
            accepted = state;
            pending.Enqueue(new(state, generation, Stopwatch.GetTimestamp(), received));
            diagnostics?.QueueEnqueued(pending.Count, Stopwatch.GetElapsedTime(pending.Peek().Enqueued).TotalMilliseconds);
        }
    }

    public void Reset() { lock (gate) ResetLocked(); }

    private void ResetLocked()
    {
        generation++;
        pending.Clear();
        accepted = current = TouchState.Empty;
        long now = Stopwatch.GetTimestamp();
        pending.Enqueue(new(TouchState.Empty, generation, now, 0));
        diagnostics?.QueueReset();
    }

    public bool TryTake(out Entry entry)
    {
        lock (gate) return pending.TryDequeue(out entry);
    }

    // This commits one write pair. A later reset cannot interrupt a port write;
    // the single I/O owner finishes that pair before applying the queued release.
    public bool TryActivate(Entry entry)
    {
        lock (gate)
        {
            if (entry.Generation != generation) return false;
            current = entry.State;
            return true;
        }
    }

    public bool TryHeartbeat(out Entry entry)
    {
        lock (gate)
        {
            entry = new(current, generation, 0, 0);
            return pending.Count == 0;
        }
    }
}
