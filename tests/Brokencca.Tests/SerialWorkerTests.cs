using System.Diagnostics;
using System.Text.Json;
using Brokencca.Core;
using Brokencca.Host;

internal static class SerialWorkerTests
{
    private static void Check(bool condition, string message = "Assertion failed")
    {
        if (!condition) throw new Exception(message);
    }

    private static TouchState State(byte bits)
    {
        byte[] bitmap = new byte[30];
        bitmap[0] = bitmap[15] = bits; // Matching fixtures on both halves.
        return new(bitmap);
    }

    public static void QueueGeneration()
    {
        var queue = new TouchOutputQueue(null);
        queue.Apply(State(1), 42);
        Check(queue.TryTake(out var stale));
        queue.Reset();
        queue.Apply(State(4), 43);
        Check(!queue.TryActivate(stale), "Dequeued state survived a reset");
        Check(queue.TryTake(out var release) && release.State.SameAs(TouchState.Empty));
        Check(queue.TryActivate(release));
        Check(queue.TryTake(out var fresh) && fresh.State.SameAs(State(4)));
        Check(queue.TryActivate(fresh));
        Check(queue.TryHeartbeat(out var heartbeat) && heartbeat.State.SameAs(State(4)));
        queue.Reset();
        Check(!queue.TryActivate(heartbeat), "Old heartbeat survived a reset");
        Check(!queue.TryHeartbeat(out _), "Heartbeat bypassed a queued release");
    }

    public static void QueueOverflow()
    {
        var diagnostics = new HostDiagnostics();
        var queue = new TouchOutputQueue(diagnostics);
        for (int i = 0; i < TouchOutputQueue.Capacity; i++) queue.Apply(State((byte)(i % 2 == 0 ? 1 : 0)), 1);
        queue.Apply(State(0), 2); // Exact duplicate at capacity must not overflow.
        bool threw = false;
        try { queue.Apply(State(1), 3); }
        catch (IOException) { threw = true; }
        Check(threw);
        Check(queue.TryTake(out var release) && release.State.SameAs(TouchState.Empty));
        Check(queue.TryActivate(release) && !queue.TryTake(out _));
        using var report = JsonDocument.Parse(diagnostics.TakeReport());
        Check(report.RootElement.GetProperty("queue_high_water").GetInt32() == 64);
        Check(report.RootElement.GetProperty("queue_overflows").GetInt32() == 1);
    }

    public static async Task OrderedBurst()
    {
        await using var host = await Harness.Start();
        host.Left.BlockNextPacket();
        host.Sink.Apply(State(1));
        await host.Left.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // First pair is committed; the remaining 63 transitions must all survive.
        for (int i = 0; i < 63; i++) host.Sink.Apply(State((byte)(i % 2 == 0 ? 0 : 1)));
        host.Left.Unblock();
        byte[] expected = [0, .. Enumerable.Range(0, 64).Select(i => (byte)(i % 2 == 0 ? 1 : 0))];
        await Until(() => Edges(host.Right).Length >= expected.Length);
        Check(Edges(host.Left).SequenceEqual(expected));
        Check(Edges(host.Right).SequenceEqual(expected));
        var leftBurst = host.Left.Packets.SkipWhile(p => p[1] != 1).Take(64).ToArray();
        var rightBurst = host.Right.Packets.SkipWhile(p => p[1] != 1).Take(64).ToArray();
        Check(leftBurst.Select(p => p[34]).SequenceEqual(rightBurst.Select(p => p[34])), "Pair counters diverged");
        foreach (var packet in host.Left.Packets.Concat(host.Right.Packets))
            Check(SerialPackets.Xor(packet) == 128, "Invalid checksum after burst");
    }

    public static async Task ResetInFlight()
    {
        await using var host = await Harness.Start();
        host.Left.BlockNextPacket();
        host.Sink.Apply(State(1));
        await host.Left.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Completion before unblocking proves producers do not wait for port I/O.
        await Task.Run(() =>
        {
            host.Sink.Apply(State(2));
            host.Sink.Reset();
            host.Sink.Apply(State(4));
        }).WaitAsync(TimeSpan.FromSeconds(2));
        host.Left.Unblock();
        await Until(() => Edges(host.Right).Last() == 4);
        Check(Edges(host.Left).SequenceEqual(new byte[] { 0, 1, 0, 4 }));
        Check(Edges(host.Right).SequenceEqual(new byte[] { 0, 1, 0, 4 }), "Old queued state replayed after reset");
    }

    public static async Task OverflowInFlight()
    {
        await using var host = await Harness.Start();
        host.Left.BlockNextPacket();
        host.Sink.Apply(State(1));
        await host.Left.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Run(() =>
        {
            for (int i = 0; i < 64; i++) host.Sink.Apply(State((byte)(i % 2 == 0 ? 2 : 4)));
            bool threw = false;
            try { host.Sink.Apply(State(2)); }
            catch (IOException) { threw = true; }
            Check(threw);
        }).WaitAsync(TimeSpan.FromSeconds(2));
        host.Left.Unblock();
        await Until(() => Edges(host.Right).Length >= 3);
        Check(Edges(host.Left).SequenceEqual(new byte[] { 0, 1, 0 }));
        Check(Edges(host.Right).SequenceEqual(new byte[] { 0, 1, 0 }));
        using var report = JsonDocument.Parse(host.Diagnostics.TakeReport());
        Check(report.RootElement.GetProperty("queue_overflows").GetInt32() == 1);
    }

    public static async Task StartupAndCancellation()
    {
        await using var host = new Harness();
        host.Run();
        host.Left.Feed(0xc9);
        await Until(() => host.Left.Packets.Length > 0);
        Check(host.Right.Packets.Length == 0, "Right port scanned before its own startup");
        host.Right.Feed(0xc9);
        await Until(() => host.Right.Packets.Length > 0);
        host.Sink.Apply(State(1));
        await Until(() => Edges(host.Right).Last() == 1);
        int count = host.Right.Packets.Length;
        await Until(() => host.Right.Packets.Length > count); // Unchanged-state keepalive.
        Check(host.Right.Packets.Last()[1] == 1);
        host.Stop.Cancel();
        await host.Worker.WaitAsync(TimeSpan.FromSeconds(5));
        Check(host.Left.Packets.Last()[1] == 0 && host.Right.Packets.Last()[1] == 0);
    }

    public static async Task CommandFairness()
    {
        await using var host = await Harness.Start();
        host.Left.PacketDelayMs = 1;
        host.Right.PacketDelayMs = 1;
        host.Left.BlockNextPacket();
        host.Sink.Apply(State(1));
        await host.Left.Blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        for (int i = 0; i < 63; i++) host.Sink.Apply(State((byte)(i % 2 == 0 ? 0 : 1)));
        host.Left.Feed(0xc9); // Same command, preserves scanning while queue remains busy.
        host.Left.Unblock();
        await Until(() => host.Left.Writes.Count(p => p[0] == 0xc9) >= 2);
        Check(host.Left.PacketCountAtLastStartup < 65, "Command waited until all queued touches drained");
        await Until(() => Edges(host.Right).Length == 65);
    }

    public static async Task WriteFailure()
    {
        await using var host = await Harness.Start();
        host.Sink.Apply(State(1));
        await Until(() => Edges(host.Right).Last() == 1);
        host.Left.FailPackets = true;
        host.Sink.Apply(State(2));
        bool faulted = false;
        try { await host.Worker.WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (IOException) { faulted = true; }
        Check(faulted, "Failed serial write did not stop the worker");
        Check(host.Right.Packets.Last()[1] == 0, "Broken left port prevented right-port release");
    }

    public static void Diagnostics()
    {
        var diagnostics = new HostDiagnostics();
        diagnostics.SerialCompleted(2.5);
        diagnostics.WorkerIteration(3);
        diagnostics.PortWrite(0, .1, 36);
        diagnostics.PortWrite(1, .2, 72);
        using var report = JsonDocument.Parse(diagnostics.TakeReport());
        var root = report.RootElement;
        Check(root.GetProperty("receive_to_serial_ms").GetProperty("max").GetDouble() == 2.5);
        Check(root.GetProperty("worker_interval_ms").GetProperty("max").GetDouble() == 3);
        Check(root.GetProperty("left_write_ms").GetProperty("max").GetDouble() == .1);
        Check(root.GetProperty("right_write_ms").GetProperty("max").GetDouble() == .2);
        Check(root.GetProperty("left_driver_bytes_high_water").GetInt32() == 36);
        Check(root.GetProperty("right_driver_bytes_high_water").GetInt32() == 72);
        using var cleared = JsonDocument.Parse(diagnostics.TakeReport());
        Check(cleared.RootElement.GetProperty("receive_to_serial_ms").GetProperty("count").GetInt32() == 0);
        Check(cleared.RootElement.GetProperty("right_driver_bytes_high_water").GetInt32() == 0);
    }

    private static byte[] Edges(FakeEndpoint port)
    {
        var edges = new List<byte>();
        foreach (var packet in port.Packets)
            if (edges.Count == 0 || edges[^1] != packet[1]) edges.Add(packet[1]);
        return edges.ToArray();
    }

    private static async Task Until(Func<bool> condition)
    {
        long start = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(start).TotalSeconds > 5) throw new TimeoutException("Serial test did not progress");
            await Task.Delay(1);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public FakeEndpoint Left { get; } = new("LEFT");
        public FakeEndpoint Right { get; } = new("RIGHT");
        public HostDiagnostics Diagnostics { get; } = new();
        public CancellationTokenSource Stop { get; } = new();
        public SerialTouchSink Sink { get; }
        public Task Worker { get; private set; } = Task.CompletedTask;
        public Harness() => Sink = new(Left, Right, Diagnostics);
        public void Run() => Worker = Sink.RunAsync(Stop.Token);
        public static async Task<Harness> Start()
        {
            var host = new Harness();
            try
            {
                host.Run();
                host.Left.Feed(0xc9);
                host.Right.Feed(0xc9);
                await Until(() => host.Left.Packets.Length > 0 && host.Right.Packets.Length > 0);
                return host;
            }
            catch { await host.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            Left.Unblock();
            Right.Unblock();
            try { await Worker.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (IOException) { /* Expected by the write-failure test. */ }
            finally { Sink.Dispose(); Stop.Dispose(); }
        }
    }

    private sealed class FakeEndpoint(string name) : ISerialEndpoint
    {
        private readonly object gate = new();
        private readonly Queue<byte> input = new();
        private readonly List<byte[]> writes = new();
        private readonly ManualResetEventSlim proceed = new(true);
        private int blockNext;
        public TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int PacketDelayMs { get; set; }
        public bool FailPackets { get; set; }
        public int PacketCountAtLastStartup { get; private set; }
        public string PortName => name;
        public int BytesToRead { get { lock (gate) return input.Count; } }
        public int BytesToWrite => 0;
        public event Action? DataAvailable;
        public byte[][] Writes { get { lock (gate) return writes.ToArray(); } }
        public byte[][] Packets => Writes.Where(p => p.Length == 36).ToArray();
        public void BlockNextPacket() { proceed.Reset(); Interlocked.Exchange(ref blockNext, 1); }
        public void Unblock() => proceed.Set();
        public void Feed(params byte[] bytes)
        {
            lock (gate) foreach (byte value in bytes) input.Enqueue(value);
            DataAvailable?.Invoke();
        }
        public int Read(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                int read = Math.Min(count, input.Count);
                for (int i = 0; i < read; i++) buffer[offset + i] = input.Dequeue();
                return read;
            }
        }
        public void Write(byte[] buffer, int offset, int count)
        {
            if (count == 36)
            {
                if (FailPackets) throw new IOException("Injected port failure");
                if (buffer[offset + 1] == 1 && Interlocked.Exchange(ref blockNext, 0) != 0)
                {
                    Blocked.TrySetResult();
                    if (!proceed.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Fake write never unblocked");
                }
                if (PacketDelayMs > 0) Thread.Sleep(PacketDelayMs);
            }
            lock (gate)
            {
                if (buffer[offset] == 0xc9) PacketCountAtLastStartup = writes.Count(p => p.Length == 36);
                writes.Add(buffer.AsSpan(offset, count).ToArray());
            }
        }
        public void Dispose() => proceed.Dispose();
    }
}
