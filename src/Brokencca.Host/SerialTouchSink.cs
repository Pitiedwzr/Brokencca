using System.Diagnostics;
using Brokencca.Core;

namespace Brokencca.Host;

// One worker owns all serial I/O. A bounded FIFO preserves transitions; reset discards stale work.
public sealed class SerialTouchSink : ITimedTouchSink, IDisposable
{
    private readonly TouchOutputQueue pending;
    private readonly AutoResetEvent wake = new(false);
    private readonly ISerialEndpoint left;
    private readonly ISerialEndpoint right;
    private readonly List<byte>[] incoming = [new(), new()];
    private readonly long[] lastByte = new long[2];
    private readonly bool[] scanning = new bool[2];
    private readonly HostDiagnostics? diagnostics;
    private byte counter;
    public HostDiagnostics? Diagnostics => diagnostics;

    public SerialTouchSink(string leftPort, string rightPort, HostDiagnostics? diagnostics = null)
        : this(OpenPorts(leftPort, rightPort), diagnostics) { }

    private SerialTouchSink((ISerialEndpoint Left, ISerialEndpoint Right) ports, HostDiagnostics? diagnostics)
        : this(ports.Left, ports.Right, diagnostics) { }

    internal SerialTouchSink(ISerialEndpoint left, ISerialEndpoint right, HostDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        pending = new(diagnostics);
        this.left = left;
        this.right = right;
        left.DataAvailable += Signal;
        right.DataAvailable += Signal;
    }

    private static (ISerialEndpoint, ISerialEndpoint) OpenPorts(string leftName, string rightName)
    {
        var left = new SerialEndpoint(leftName);
        try { return (left, new SerialEndpoint(rightName)); }
        catch { left.Dispose(); throw; }
    }

    private void Signal()
    {
        try { wake.Set(); }
        catch (ObjectDisposedException) { /* An already-dispatched port callback may race disposal. */ }
    }

    public void Apply(TouchState state) => Apply(state, Stopwatch.GetTimestamp());

    public void Apply(TouchState state, long arrivalTimestamp)
    {
        try { pending.Apply(state, arrivalTimestamp); }
        finally { Signal(); } // Includes overflow's fail-safe release.
    }

    public void Reset()
    {
        pending.Reset();
        Signal();
    }

    public Task RunAsync(CancellationToken token) => Task.Factory.StartNew(() =>
    {
        long lastSent = 0;
        long previousIteration = Stopwatch.GetTimestamp();
        using var cancellation = token.Register(Signal);
        try
        {
            while (!token.IsCancellationRequested)
            {
                long iteration = Stopwatch.GetTimestamp();
                diagnostics?.WorkerIteration(Stopwatch.GetElapsedTime(previousIteration, iteration).TotalMilliseconds);
                previousIteration = iteration;
                ReadCommands(left, 0);
                ReadCommands(right, 1);
                // Revisit commands, resets and cancellation between bounded batches.
                // There is no sleep between ready transitions.
                for (int drained = 0; drained < 16 && !token.IsCancellationRequested; drained++)
                {
                    if (!pending.TryTake(out var next)) break;
                    if (!pending.TryActivate(next)) continue;
                    diagnostics?.QueueDequeued(Stopwatch.GetElapsedTime(next.Enqueued).TotalMilliseconds);
                    SendCurrent(next.State, next.Received);
                    lastSent = Stopwatch.GetTimestamp();
                    if (Stopwatch.GetElapsedTime(iteration).TotalMilliseconds >= 2) break;
                }
                if (token.IsCancellationRequested) break;
                if (!pending.TryHeartbeat(out var heartbeat)) continue;
                if (Stopwatch.GetElapsedTime(lastSent).TotalMilliseconds >= 100 && pending.TryActivate(heartbeat))
                {
                    SendCurrent(heartbeat.State);
                    lastSent = Stopwatch.GetTimestamp();
                }
                // Touch producers/data arrival/cancellation wake immediately. The timeout
                // maintains the inherited command idle-gap parser and serial keepalive.
                wake.WaitOne(WaitMilliseconds(lastSent));
            }
        }
        finally
        {
            pending.Reset();
            // A broken port must not prevent best-effort release on the other half.
            counter = (byte)((counter + 1) & 127);
            var packets = SerialPackets.Encode(TouchState.Empty, counter);
            for (int side = 0; side < 2; side++)
            {
                if (!scanning[side]) continue;
                var port = side == 0 ? left : right;
                try { WritePort(port, side, side == 0 ? packets.Left : packets.Right); }
                catch (Exception ex) { Console.Error.WriteLine($"Final serial release failed on {port.PortName}: {ex.Message}"); }
            }
        }
    }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private int WaitMilliseconds(long lastSent)
    {
        double wait = Math.Min(10, Math.Max(0, 100 - Stopwatch.GetElapsedTime(lastSent).TotalMilliseconds));
        for (int side = 0; side < 2; side++)
            if (incoming[side].Count > 0)
            {
                double gap = incoming[side][0] == 0x72 && incoming[side].Count < 4 ? 100 : 3;
                wait = Math.Min(wait, Math.Max(0, gap - Stopwatch.GetElapsedTime(lastByte[side]).TotalMilliseconds));
            }
        return (int)Math.Ceiling(wait);
    }

    private void SendCurrent(TouchState state, long received = 0)
    {
        long started = Stopwatch.GetTimestamp();
        counter = (byte)((counter + 1) & 127);
        var packets = SerialPackets.Encode(state, counter);
        if (scanning[0]) WritePort(left, 0, packets.Left);
        if (scanning[1]) WritePort(right, 1, packets.Right);
        if (scanning[0] || scanning[1]) diagnostics?.SerialWrite(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        if (received != 0 && scanning[0] && scanning[1])
            diagnostics?.SerialCompleted(Stopwatch.GetElapsedTime(received).TotalMilliseconds);
    }

    private void WritePort(ISerialEndpoint port, int side, byte[] bytes)
    {
        long started = Stopwatch.GetTimestamp();
        port.Write(bytes, 0, bytes.Length);
        if (diagnostics is not null)
            diagnostics.PortWrite(side, Stopwatch.GetElapsedTime(started).TotalMilliseconds, port.BytesToWrite);
    }

    private void ReadCommands(ISerialEndpoint port, int side)
    {
        // toucca handles request/response bursts, not a documented length-framed protocol.
        // Accumulate binary bytes across reads and wait for a gap instead of ReadExisting's text decoding.
        // This compatibility parser still requires real-game verification; see docs/PLAN.md.
        var bytes = incoming[side];
        int available = port.BytesToRead;
        if (available > 0)
        {
            if (bytes.Count + available > 4096) throw new IOException($"Serial command overflow on {port.PortName}.");
            var buffer = new byte[available];
            int count = port.Read(buffer, 0, available);
            bytes.AddRange(buffer.AsSpan(0, count).ToArray());
            lastByte[side] = Stopwatch.GetTimestamp();
        }
        if (bytes.Count == 0 || Stopwatch.GetElapsedTime(lastByte[side]).TotalMilliseconds < 3) return;
        byte command = bytes[0];
        if (command == 0x72 && bytes.Count < 4)
        {
            if (Stopwatch.GetElapsedTime(lastByte[side]).TotalMilliseconds < 100) return;
            throw new IOException($"Truncated serial read command on {port.PortName}.");
        }
        byte address = command == 0x72 ? bytes[3] : (byte)0;
        bytes.Clear();
        if (command is 0xa0 or 0xa8 or 0xa2 or 0x94 or 0x72 or 0x9a) scanning[side] = false;
        byte[] response = SerialPackets.Response(command, side == 0, address);
        if (response.Length > 0) WritePort(port, side, response);
        if (command == 0xc9) scanning[side] = true;
        Console.WriteLine($"{port.PortName}: command 0x{command:X2}, response {response.Length} bytes.");
    }

    public void Dispose()
    {
        left.DataAvailable -= Signal;
        right.DataAvailable -= Signal;
        left.Dispose();
        right.Dispose();
        wake.Dispose();
    }
}
