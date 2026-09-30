using System.Diagnostics;
using System.IO.Ports;
using Brokencca.Core;

namespace Brokencca.Host;

// One worker owns all serial I/O. A bounded FIFO preserves transitions; reset discards stale work.
public sealed class SerialTouchSink : ITouchSink, IDisposable
{
    private readonly object gate = new();
    private readonly Queue<(TouchState State, long Enqueued)> pending = new();
    private readonly SerialPort left;
    private readonly SerialPort right;
    private readonly List<byte>[] incoming = [new(), new()];
    private readonly long[] lastByte = new long[2];
    private readonly bool[] scanning = new bool[2];
    private readonly HostDiagnostics? diagnostics;
    private TouchState accepted = TouchState.Empty;
    private TouchState current = TouchState.Empty;
    private byte counter;
    public HostDiagnostics? Diagnostics => diagnostics;

    public SerialTouchSink(string leftPort, string rightPort, HostDiagnostics? diagnostics = null)
    {
        this.diagnostics = diagnostics;
        left = NewPort(leftPort);
        right = NewPort(rightPort);
        try { left.Open(); right.Open(); }
        catch { left.Dispose(); right.Dispose(); throw; }
    }

    private static SerialPort NewPort(string name) => new(name, 115200, Parity.None, 8, StopBits.One)
    { ReadTimeout = 100, WriteTimeout = 100, Handshake = Handshake.None };

    public void Apply(TouchState state)
    {
        lock (gate)
        {
            if (accepted.SameAs(state)) return;
            if (pending.Count >= 64)
            {
                diagnostics?.QueueOverflow();
                Reset();
                throw new IOException("Serial input queue overflow; releasing all touches.");
            }
            accepted = state;
            pending.Enqueue((state, Stopwatch.GetTimestamp()));
            diagnostics?.QueueEnqueued(pending.Count,
                Stopwatch.GetElapsedTime(pending.Peek().Enqueued).TotalMilliseconds);
        }
    }

    public void Reset()
    {
        lock (gate)
        {
            pending.Clear();
            accepted = TouchState.Empty;
            pending.Enqueue((TouchState.Empty, Stopwatch.GetTimestamp()));
            diagnostics?.QueueReset();
        }
    }

    public Task RunAsync(CancellationToken token) => Task.Run(() =>
    {
        long lastSent = 0;
        try
        {
            while (!token.IsCancellationRequested)
            {
                ReadCommands(left, 0);
                ReadCommands(right, 1);
                lock (gate)
                {
                    bool changed = pending.TryDequeue(out var next);
                    if (changed)
                    {
                        current = next.State;
                        diagnostics?.QueueDequeued(Stopwatch.GetElapsedTime(next.Enqueued).TotalMilliseconds);
                    }
                    if (changed || Stopwatch.GetElapsedTime(lastSent).TotalMilliseconds >= 100)
                    {
                        SendCurrent();
                        lastSent = Stopwatch.GetTimestamp();
                    }
                }
                Thread.Sleep(1);
            }
        }
        finally
        {
            lock (gate)
            {
                current = TouchState.Empty;
                pending.Clear();
                try { SendCurrent(); }
                catch (Exception ex) { Console.Error.WriteLine($"Final serial release failed: {ex.Message}"); }
            }
        }
    }, CancellationToken.None);

    private void SendCurrent()
    {
        long started = Stopwatch.GetTimestamp();
        counter = (byte)((counter + 1) & 127);
        var packets = SerialPackets.Encode(current, counter);
        if (scanning[0]) left.Write(packets.Left, 0, packets.Left.Length);
        if (scanning[1]) right.Write(packets.Right, 0, packets.Right.Length);
        diagnostics?.SerialWrite(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
    }

    private void ReadCommands(SerialPort port, int side)
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
        if (response.Length > 0) port.Write(response, 0, response.Length);
        if (command == 0xc9) scanning[side] = true;
        Console.WriteLine($"{port.PortName}: command 0x{command:X2}, response {response.Length} bytes.");
    }

    public void Dispose() { left.Dispose(); right.Dispose(); }
}
