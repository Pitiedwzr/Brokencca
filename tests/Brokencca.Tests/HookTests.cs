using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Net;
using System.Net.Sockets;
using Brokencca.Core;
using Brokencca.Host;

internal static class HookTests
{
    private static void Check(bool condition, string reason = "Assertion failed") { if (!condition) throw new Exception(reason); }
    private static TouchState State(int zone)
    {
        byte[] bits = new byte[30]; if (zone >= 0) bits[zone / 8] = (byte)(1 << (zone % 8)); return new(bits);
    }
    private static async Task Until(Func<bool> test)
    {
        long started = Stopwatch.GetTimestamp();
        while (!test()) { if (Stopwatch.GetElapsedTime(started).TotalSeconds > 5) throw new TimeoutException("Hook test stalled"); await Task.Delay(1); }
    }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); } catch (Exception ex) when (ex is IOException or InvalidDataException) { return; }
        throw new Exception("Expected rejected LED frame");
    }
    public static async Task LedFraming()
    {
        byte[] payload = Enumerable.Range(0, LedProtocol.PayloadSize).Select(i => (byte)i).ToArray();
        byte[] first = LedProtocol.Encode(uint.MaxValue, payload), second = LedProtocol.Encode(0, payload);
        using var stream = new FragmentStream([.. first, .. second]);
        var a = await LedProtocol.ReadAsync(stream, default); var b = await LedProtocol.ReadAsync(stream, default);
        Check(a.Sequence == uint.MaxValue && b.Sequence == 0 && a.Payload.SequenceEqual(payload));
        for (int i = 0; i < 12; i++)
        {
            byte[] malformed = (byte[])first.Clone(); malformed[i] ^= 0x80;
            await Reject(async () => await LedProtocol.ReadAsync(new MemoryStream(malformed), default));
        }
        await Reject(async () => await LedProtocol.ReadAsync(new MemoryStream(first[..^1]), default));
        await Reject(() => { LedProtocol.Encode(0, new byte[1920]); return Task.CompletedTask; });
    }
    public static Task Ipc()
    {
        if (!OperatingSystem.IsWindows()) { Console.WriteLine("SKIP Windows IPC on non-Windows"); return Task.CompletedTask; }
        IpcWindows(); return Task.CompletedTask;
    }
    [SupportedOSPlatform("windows")]
    private static void IpcWindows()
    {
        string prefix = @"Local\BROKENCCA_TEST_" + Guid.NewGuid().ToString("N");
        using var sink = new HookTouchSink(prefix);
        using var mapping = MemoryMappedFile.OpenExisting(prefix + ".Input");
        using var view = mapping.CreateViewAccessor(0, HookTouchSink.InputSize);
        using var gate = new Mutex(false, prefix + ".InputMutex");
        void Locked(Action action) { gate.WaitOne(); try { action(); } finally { gate.ReleaseMutex(); } }
        Locked(() =>
        {
            Check(view.ReadUInt32(0) == HookTouchSink.Magic && view.ReadUInt32(4) == 1);
            Check(view.ReadUInt32(28) == 4167 && view.ReadUInt32(16) == 1);
            Check(view.ReadInt64(128 + 8) == 0);
            view.Write(12, 1u); // Consume startup release.
        });
        bool duplicateHostRejected = false;
        try { using var other = new HookTouchSink(prefix); }
        catch (IOException) { duplicateHostRejected = true; }
        Check(duplicateHostRejected);
        for (int i = 0; i < 64; i++) sink.Apply(State(i % 2 == 0 ? 0 : -1));
        sink.Apply(State(-1)); // Duplicate must not trigger overflow at capacity.
        bool overflow = false;
        try { sink.Apply(State(0)); } catch (IOException) { overflow = true; }
        Check(overflow);
        uint generation = 0;
        Locked(() =>
        {
            generation = view.ReadUInt32(8); Check(view.ReadUInt32(12) == 0 && view.ReadUInt32(16) == 1);
            byte[] bits = new byte[30]; view.ReadArray(128 + 24, bits, 0, 30); Check(bits.All(b => b == 0));
        });
        sink.Apply(State(239)); sink.Reset(); sink.Apply(State(120));
        Locked(() =>
        {
            Check(view.ReadUInt32(8) != generation && view.ReadUInt32(16) == 2);
            byte[] bits = new byte[30]; view.ReadArray(128 + 56 + 24, bits, 0, 30);
            Check(new TouchState(bits).SameAs(State(120)), "Reset kept old history");
        });
        using var leds = MemoryMappedFile.OpenExisting(prefix + ".Leds");
        using var ledView = leds.CreateViewAccessor(0, HookTouchSink.LedSize);
        using var ledGate = new Mutex(false, prefix + ".LedMutex");
        ledGate.WaitOne();
        try
        {
            byte[] colors = Enumerable.Range(0, 1920).Select(i => (byte)i).ToArray();
            ledView.Write(8, 480u); ledView.Write(16, unchecked((ulong)Environment.TickCount64));
            ledView.WriteArray(32, colors, 0, colors.Length);
            // Separate thread cannot acquire the LED lock; input/reset must still complete.
            Task.Run(() => { sink.Reset(); sink.Apply(State(1)); Check(sink.ReadLeds().All(b => b == 0)); }).GetAwaiter().GetResult();
        }
        finally { ledGate.ReleaseMutex(); }
        byte[] frame = sink.ReadLeds();
        Check(BinaryPrimitives.ReadUInt32LittleEndian(frame) == 480);
        Check(frame.AsSpan(4).SequenceEqual(Enumerable.Range(0, 1920).Select(i => (byte)i).ToArray()));
        ledView.Write(16, unchecked((ulong)Environment.TickCount64 - 1001));
        Check(sink.ReadLeds().All(b => b == 0));
        // Duplicate heartbeats must still notice a dead callback consumer.
        Locked(() => { view.Write(20, 1u); view.Write(40, unchecked((ulong)Environment.TickCount64 - 501)); });
        bool stalled = false;
        try { sink.Apply(State(1)); } catch (IOException) { stalled = true; }
        Check(stalled); Locked(() => view.Write(20, 0u));
    }

    public static async Task LedIsolation()
    {
        if (!OperatingSystem.IsWindows()) return;
        await LedIsolationWindows();
    }
    [SupportedOSPlatform("windows")]
    private static async Task LedIsolationWindows()
    {
        string prefix = @"Local\BROKENCCA_LED_TEST_" + Guid.NewGuid().ToString("N");
        using var sink = new HookTouchSink(prefix);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var stop = new CancellationTokenSource();
        Task sender = LedForwarder.RunAsync(sink, ((IPEndPoint)listener.LocalEndpoint).Port, stop.Token);
        try
        {
            using var peer = await listener.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var first = await LedProtocol.ReadAsync(peer.GetStream(), stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
            var second = await LedProtocol.ReadAsync(peer.GetStream(), stop.Token).WaitAsync(TimeSpan.FromSeconds(5));
            Check(first.Sequence == 0 && second.Sequence == 1 && first.Payload.Length == 1924);
            using var map = MemoryMappedFile.OpenExisting(prefix + ".Input");
            using var view = map.CreateViewAccessor(0, HookTouchSink.InputSize);
            using var gate = new Mutex(false, prefix + ".InputMutex");
            gate.WaitOne(); uint generation; try { generation = view.ReadUInt32(8); } finally { gate.ReleaseMutex(); }
            peer.Close(); // LED failure must neither clear touch history nor reset its generation.
            sink.Apply(State(0)); await Task.Delay(100);
            gate.WaitOne();
            try { Check(view.ReadUInt32(8) == generation && view.ReadUInt32(16) == 2); }
            finally { gate.ReleaseMutex(); }
        }
        finally
        {
            stop.Cancel(); listener.Stop();
            try { await sender; } catch (OperationCanceledException) { }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate ushort Version();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Start(Callback callback);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Stop();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Callback(IntPtr cells);
    [StructLayout(LayoutKind.Sequential)] private struct NativeLed
    {
        public uint UnitCount;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1920)] public byte[] Rgba;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void SetLeds(NativeLed data);

    public static async Task Native()
    {
        if (!OperatingSystem.IsWindows()) return;
        await NativeWindows();
    }
    [SupportedOSPlatform("windows")]
    private static async Task NativeWindows()
    {
        string prefix = @"Local\BROKENCCA_NATIVE_TEST_" + Guid.NewGuid().ToString("N");
        string? previous = Environment.GetEnvironmentVariable("BROKENCCA_IPC_PREFIX");
        Environment.SetEnvironmentVariable("BROKENCCA_IPC_PREFIX", prefix);
        IntPtr dll = NativeLibrary.Load(Path.GetFullPath(Environment.GetEnvironmentVariable("BROKENCCA_TEST_DLL")!));
        T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(dll, name));
        var start = Export<Start>("mercury_io_touch_start"); var stop = Export<Stop>("mercury_io_touch_stop");
        var setLeds = Export<SetLeds>("mercury_io_touch_set_leds");
        Check(Export<Version>("mercury_io_get_api_version")() == 0x0100);
        Check(Export<Init>("mercury_io_init")() == 0 && Export<Init>("mercury_io_touch_init")() == 0);
        foreach (string name in new[] { "mercury_io_poll", "mercury_io_get_opbtns", "mercury_io_get_gamebtns" }) NativeLibrary.GetExport(dll, name);
        using var sink = new HookTouchSink(prefix, rate: 1000);
        using var inputMap = MemoryMappedFile.OpenExisting(prefix + ".Input");
        using var input = inputMap.CreateViewAccessor(0, HookTouchSink.InputSize);
        using var inputMutex = new Mutex(false, prefix + ".InputMutex");
        using var entered = new ManualResetEventSlim(); using var proceed = new ManualResetEventSlim(true);
        var states = new List<byte[]>(); object stateGate = new(); int blockNext = 0;
        Callback callback = cells =>
        {
            byte[] bytes = new byte[240]; Marshal.Copy(cells, bytes, 0, 240);
            if (bytes[0] != 0 && Interlocked.Exchange(ref blockNext, 0) != 0)
            { entered.Set(); if (!proceed.Wait(5000)) return; }
            lock (stateGate) states.Add(bytes);
        };
        byte[][] States() { lock (stateGate) return states.ToArray(); }
        void Locked(Action action) { inputMutex.WaitOne(); try { action(); } finally { inputMutex.ReleaseMutex(); } }
        void Pulse() => Locked(() => input.Write(32, unchecked((ulong)Environment.TickCount64)));
        using var lifetime = new CancellationTokenSource();
        Task heartbeat = sink.RunAsync(lifetime.Token);
        try
        {
            start(callback);
            await Until(() => States().Length > 0);
            // Cross-language mapping/packing, one state per zone; compare every cell.
            for (int zone = 0; zone < 240; zone++)
            {
                sink.Apply(State(zone)); int expected = zone;
                await Until(() => States().Last()[expected] == 1);
                byte[] latest = States().Last(); Check(latest.Count(b => b != 0) == 1);
                // Recreate touch.c's cells->COM3/COM4 data1 fields and compare
                // against the already-tested external serial path, not WACVR's frontend.
                var packets = SerialPackets.Encode(State(zone), 0);
                for (int side = 0; side < 2; side++) for (int group = 0; group < 24; group++)
                {
                    int actual = 0;
                    for (int bit = 0; bit < 5; bit++) actual |= latest[side * 120 + group * 5 + bit] << bit;
                    Check(actual == (side == 0 ? packets.Left : packets.Right)[1 + group], "Hook/serial mapping diverged");
                }
            }
            sink.Reset(); await Until(() => States().Last().All(b => b == 0));
            // Native FIFO preserves rapid press/release/repress, not just the final hold.
            int repressStart = States().Length;
            entered.Reset(); proceed.Reset(); Interlocked.Exchange(ref blockNext, 1); sink.Apply(State(0));
            Check(entered.Wait(5000)); sink.Apply(State(-1)); sink.Apply(State(0)); proceed.Set();
            await Until(() =>
            {
                var edges = new List<byte>();
                foreach (byte[] s in States().Skip(repressStart)) if (edges.Count == 0 || edges[^1] != s[0]) edges.Add(s[0]);
                return edges.SkipWhile(x => x == 0).SequenceEqual(new byte[] { 1, 0, 1 });
            });
            sink.Reset(); await Until(() => States().Last().All(b => b == 0));
            int resetTestStart = States().Length;
            // Deterministic callback-in-flight reset: old queued zone must never appear.
            entered.Reset(); proceed.Reset(); Interlocked.Exchange(ref blockNext, 1); sink.Apply(State(0));
            Check(entered.Wait(5000));
            await Task.Run(() => { sink.Apply(State(1)); sink.Reset(); sink.Apply(State(2)); }).WaitAsync(TimeSpan.FromSeconds(2));
            proceed.Set(); await Until(() => States().Last()[2] == 1);
            Check(States().Skip(resetTestStart).All(s => s[1] == 0), "Stale state replayed after reset");
            // Both LED ABI fields preserved, especially the alpha formerly used as a flag.
            byte[] colors = Enumerable.Range(0, 1920).Select(i => (byte)i).ToArray();
            setLeds(new NativeLed { UnitCount = 480, Rgba = colors });
            byte[] ledPayload = sink.ReadLeds(); Check(BinaryPrimitives.ReadUInt32LittleEndian(ledPayload) == 480);
            Check(ledPayload.AsSpan(4).SequenceEqual(colors));
            // Stop publisher heartbeat, simulate process death; native watchdog releases.
            lifetime.Cancel(); await heartbeat;
            await Until(() => States().Last().All(b => b == 0));
            uint watchdog = 0; Locked(() => watchdog = input.ReadUInt32(72));
            await Until(() => { Locked(() => watchdog = input.ReadUInt32(72)); return watchdog > 0; });
            // A crashed producer leaves a nonzero PID: lease expiry must also release.
            Locked(() => input.Write(24, (uint)Environment.ProcessId)); Pulse(); sink.Apply(State(3));
            await Until(() => States().Last()[3] == 1);
            Locked(() => input.Write(32, unchecked((ulong)Environment.TickCount64 - 501)));
            await Until(() => States().Last().All(b => b == 0));
            // Game restart discards unconsumed history and begins released.
            stop();
            Locked(() => input.Write(24, (uint)Environment.ProcessId)); Pulse(); sink.Apply(State(4));
            int beforeRestart = States().Length;
            start(callback);
            await Until(() => States().Length > beforeRestart);
            Check(States().Last().All(b => b == 0));
            string? hostPath = Environment.GetEnvironmentVariable("BROKENCCA_TEST_HOST");
            if (hostPath is not null)
            {
                stop(); sink.Dispose(); // Release producer ownership, retain fake game/IPC.
                start(callback);
                await PackagedHost(hostPath, () => States().Last(), () => setLeds(new NativeLed { UnitCount = 480, Rgba = colors }));
            }
        }
        finally
        {
            proceed.Set(); lifetime.Cancel(); await heartbeat; stop(); GC.KeepAlive(callback);
            Environment.SetEnvironmentVariable("BROKENCCA_IPC_PREFIX", previous);
            NativeLibrary.Free(dll); // DLL is pinned during callbacks, as in a real game.
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task PackagedHost(string path, Func<byte[]> latest, Action publishLeds)
    {
        var touches = new TcpListener(IPAddress.Loopback, 0); touches.Start();
        var leds = new TcpListener(IPAddress.Loopback, 0); leds.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var info = new ProcessStartInfo(Path.GetFullPath(path))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { "--hook", "--leds", "--diagnostics", "--port", ((IPEndPoint)touches.LocalEndpoint).Port.ToString(),
            "--led-port", ((IPEndPoint)leds.LocalEndpoint).Port.ToString() }) info.ArgumentList.Add(argument);
        using var host = Process.Start(info) ?? throw new Exception("Could not start packaged host");
        Task<string> output = host.StandardOutput.ReadToEndAsync(), errors = host.StandardError.ReadToEndAsync();
        try
        {
            using var peer = await touches.AcceptTcpClientAsync(stop.Token);
            var hello = await WireProtocol.ReadAsync(peer.GetStream(), stop.Token);
            Check(hello.Type == MessageType.Hello);
            await WireProtocol.WriteAsync(peer.GetStream(), new(MessageType.Hello, 0, WireProtocol.NowUs, WireProtocol.HelloPayload), stop.Token);
            await WireProtocol.WriteAsync(peer.GetStream(), new(MessageType.Touch, 1, WireProtocol.NowUs, State(5).ToArray()), stop.Token);
            await Until(() => latest()[5] == 1);
            publishLeds();
            using var ledPeer = await leds.AcceptTcpClientAsync(stop.Token);
            var frame = await LedProtocol.ReadAsync(ledPeer.GetStream(), stop.Token);
            // Stream can connect before the first game LED callback; allow the next frame.
            while (BinaryPrimitives.ReadUInt32LittleEndian(frame.Payload) != 480)
            { publishLeds(); frame = await LedProtocol.ReadAsync(ledPeer.GetStream(), stop.Token); }
            Check(frame.Payload.AsSpan(4).SequenceEqual(Enumerable.Range(0, 1920).Select(i => (byte)i).ToArray()));
            ledPeer.Close();
            await WireProtocol.WriteAsync(peer.GetStream(), new(MessageType.Touch, 2, WireProtocol.NowUs, State(6).ToArray()), stop.Token);
            await Until(() => latest()[6] == 1); // Lost LED socket does not disrupt input.
            peer.Close(); await Until(() => latest().All(b => b == 0));
            using var reconnect = await touches.AcceptTcpClientAsync(stop.Token);
            await WireProtocol.ReadAsync(reconnect.GetStream(), stop.Token);
            await WireProtocol.WriteAsync(reconnect.GetStream(), new(MessageType.Hello, 0, WireProtocol.NowUs, WireProtocol.HelloPayload), stop.Token);
            await WireProtocol.WriteAsync(reconnect.GetStream(), new(MessageType.Touch, 1, WireProtocol.NowUs, State(7).ToArray()), stop.Token);
            await Until(() => latest()[7] == 1);
            host.Kill(); await host.WaitForExitAsync(stop.Token); // Only this isolated fake-game test child.
            await Until(() => latest().All(b => b == 0)); // Native watchdog handles real producer process death.
            Check((await output).Contains("\"sink\":\"hook\""));
            Console.WriteLine("PASS packaged hook host: cross-process touch/LED IPC, reconnect, LED isolation and crash release");
        }
        finally
        {
            if (!host.HasExited) { host.Kill(); await host.WaitForExitAsync(); }
            touches.Stop(); leds.Stop(); await output; await errors;
        }
    }
}
