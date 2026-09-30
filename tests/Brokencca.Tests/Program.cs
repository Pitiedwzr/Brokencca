using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Brokencca.Core;

var tests = new (string Name, Func<Task> Run)[]
{
    ("wire golden bytes", () => Sync(WireGolden)),
    ("fragmented and concatenated frames", Fragments),
    ("reject malformed headers and payloads", InvalidFrames),
    ("reject truncated header and payload", TruncatedFrames),
    ("immutable snapshots", () => Sync(Immutable)),
    ("all 240 serial zones and checksums", () => Sync(SerialMapping)),
    ("serial startup response fixtures", () => Sync(SerialResponses)),
    ("touch geometry fixtures and every zone centre", () => Sync(Geometry)),
    ("ordered transitions and reset", Transitions),
    ("disconnect releases held touch", Disconnect),
    ("heartbeat timeout releases held touch", Timeout),
    ("stale sequence rejected and released", StaleSequence),
    ("sequence wraps correctly", SequenceWrap),
    ("input diagnostics observe frames and sequence gaps", DiagnosticsObserver),
    ("wrong handshake releases state", WrongHandshake),
    ("partial frame timeout releases state", PartialTimeout),
    ("cancellation releases held touch", Cancellation)
};
int failures = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine($"PASS {test.Name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {test.Name}: {ex}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed.");
return failures == 0 ? 0 : 1;

static Task Sync(Action action) { action(); return Task.CompletedTask; }
static void Check(bool value, string reason = "Assertion failed") { if (!value) throw new Exception(reason); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}
static byte[] Frame(MessageType type, uint seq, byte[]? payload = null) =>
    WireProtocol.Encode(new(type, seq, 0x0102030405060708, payload ?? []));

static void WireGolden()
{
    byte[] bytes = Frame(MessageType.Hello, 0x11223344, WireProtocol.HelloPayload);
    Check(Convert.ToHexString(bytes) == "424343410101000004000000443322110807060504030201F0001E00");
}

static async Task Fragments()
{
    byte[] state = new byte[30]; state[29] = 128;
    using var stream = new FragmentStream([.. Frame(MessageType.Hello, 5, WireProtocol.HelloPayload), .. Frame(MessageType.Touch, 6, state)]);
    var first = await WireProtocol.ReadAsync(stream, default);
    var second = await WireProtocol.ReadAsync(stream, default);
    Check(first.Type == MessageType.Hello && second.Sequence == 6 && second.Payload.SequenceEqual(state));
}

static async Task InvalidFrames()
{
    foreach (int offset in new[] { 0, 4, 5, 6, 7, 8, 24 })
    {
        byte[] bytes = Frame(MessageType.Hello, 0, WireProtocol.HelloPayload);
        bytes[offset] = 255;
        await Throws<InvalidDataException>(async () => await WireProtocol.ReadAsync(new MemoryStream(bytes), default));
    }
    byte[] oversized = Frame(MessageType.Reset, 0);
    BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(8), uint.MaxValue);
    await Throws<InvalidDataException>(async () => await WireProtocol.ReadAsync(new MemoryStream(oversized), default));
    await Throws<InvalidDataException>(() => Sync(() => Frame(MessageType.Touch, 0, new byte[29])));
}

static async Task TruncatedFrames()
{
    byte[] bytes = Frame(MessageType.Touch, 0, new byte[30]);
    foreach (int length in new[] { 0, 1, 23, 24, 53 })
        await Throws<EndOfStreamException>(async () => await WireProtocol.ReadAsync(new FragmentStream(bytes[..length]), default));
}

static void Immutable()
{
    byte[] bytes = new byte[30]; bytes[0] = 1;
    var state = new TouchState(bytes); bytes[0] = 0;
    byte[] copy = state.ToArray(); copy[0] = 0;
    Check(state[0] && !TouchState.Empty[0]);
}

static void SerialMapping()
{
    // Physical fixture: each group of five zones occupies the low five bits of one byte.
    // Frontend zones 0..119 feed host COM5; zones 120..239 feed COM6 after toucca's half swap.
    for (int zone = 0; zone < 240; zone++)
    {
        byte[] bits = new byte[30]; bits[zone / 8] = (byte)(1 << (zone % 8));
        var (left, right) = SerialPackets.Encode(new(bits), 127);
        byte[] active = zone < 120 ? left : right, inactive = zone < 120 ? right : left;
        int local = zone % 120;
        Check(active[1 + local / 5] == (1 << (local % 5)), $"Zone {zone}");
        Check(active.Skip(1).Take(33).Sum(b => (int)b) == 1 << (local % 5));
        Check(inactive.Skip(1).Take(33).All(b => b == 0));
        Check(left[0] == 129 && right[0] == 129 && left[34] == 127 && right[34] == 127);
        Check(SerialPackets.Xor(left) == 128 && SerialPackets.Xor(right) == 128);
    }
    var all = SerialPackets.Encode(new(Enumerable.Repeat((byte)255, 30).ToArray()), 0);
    Check(all.Left.Skip(1).Take(24).All(b => b == 31));
    Check(all.Right.Skip(1).Take(24).All(b => b == 31));
    Check(SerialPackets.Xor(all.Left) == 128);
}

static void SerialResponses()
{
    Check(Convert.ToHexString(SerialPackets.Response(0xa0, true)) == "A03139303532332C");
    Check(Convert.ToHexString(SerialPackets.Response(0xc9, true)) == "C90049");
    Check(Convert.ToHexString(SerialPackets.Response(0xa2, true)) == "A23F1D");
    Check(Convert.ToHexString(SerialPackets.Response(0x94, true)) == "940014");
    foreach (byte address in new byte[] { 0x30, 0x31, 0x33 })
    {
        byte[] response = SerialPackets.Response(0x72, true, address);
        Check(response.Length > 1 && SerialPackets.Xor(response) == 0);
    }
    Check(SerialPackets.Response(0x72, true, 0xff).Length == 0);
    var left = SerialPackets.Response(0xa8, true); var right = SerialPackets.Response(0xa8, false);
    Check(left.Length == 45 && right.Length == 45 && left[7] == 'R' && right[7] == 'L');
    Check(left[^1] == 118 && right[^1] == 104);
}

static void Geometry()
{
    Check(TouchGeometry.ZoneAt(500, 500, 1000, 1000) is null);
    Check(TouchGeometry.ZoneAt(double.NaN, 500, 1000, 1000) is null);
    Check(TouchGeometry.ZoneAt(501, 500, 1000, 1000) == 15); // Inner radius is deliberately accepted.
    Check(TouchGeometry.ZoneAt(1100, 500, 1000, 1000) == 105); // Outer radius accepted too.
    Check(TouchGeometry.ZoneAt(850, 500, 1000, 1000) == 15); // Exactly .7 belongs to inner ring.
    Check(TouchGeometry.ZoneAt(850.01, 500, 1000, 1000) == 45);
    for (int side = 0; side < 2; side++) for (int ring = 0; ring < 4; ring++) for (int sector = 0; sector < 30; sector++)
    {
        double angle = -Math.PI / 2 + (sector + 0.5) * Math.PI / 30;
        double radius = 500 * (0.65 + ring * 0.1);
        double x = 500 + Math.Cos(angle) * radius * (side == 0 ? 1 : -1);
        double y = 500 + Math.Sin(angle) * radius;
        Check(TouchGeometry.ZoneAt(x, y, 1000, 1000) == side * 120 + ring * 30 + sector);
    }
}

static async Task Transitions()
{
    using var peer = await Peer.Create();
    await peer.Hello();
    await peer.Touch(1, true); await peer.Touch(2, false); await peer.Touch(3, true);
    await peer.Send(MessageType.Reset, 4);
    await peer.WaitForCount(5); // Initial reset + three transitions + explicit reset.
    Check(peer.Sink.States.Select(s => s[0]).Take(5).SequenceEqual(new[] { false, true, false, true, false }));
    peer.Client.Close();
    await Throws<EndOfStreamException>(() => peer.Run);
    Check(!peer.Sink.States.Last()[0]);
}
static async Task Disconnect()
{
    using var peer = await Peer.Create(); await peer.Hello(); await peer.Touch(1, true);
    await peer.WaitForCount(2); peer.Client.Close();
    await Throws<EndOfStreamException>(() => peer.Run);
    Check(peer.Sink.States[^2][0] && !peer.Sink.States[^1][0]);
}
static async Task Timeout()
{
    using var peer = await Peer.Create(100); await peer.Hello(); await peer.Touch(1, true);
    await Throws<OperationCanceledException>(() => peer.Run);
    Check(peer.Sink.States[^2][0] && !peer.Sink.States[^1][0]);
}
static async Task StaleSequence()
{
    using var peer = await Peer.Create(); await peer.Hello(); await peer.Touch(1, true); await peer.Touch(1, false);
    await Throws<InvalidDataException>(() => peer.Run);
    Check(peer.Sink.States.Count == 3 && !peer.Sink.States[^1][0]);
}
static async Task SequenceWrap()
{
    using var peer = await Peer.Create(); await peer.Hello(uint.MaxValue); await peer.Touch(0, true);
    await peer.WaitForCount(2); peer.Client.Close();
    await Throws<EndOfStreamException>(() => peer.Run);
    Check(peer.Sink.States[^2][0]);
}
static async Task DiagnosticsObserver()
{
    var observer = new RecordingObserver();
    using var peer = await Peer.Create(observer: observer);
    await peer.Hello();
    await peer.Touch(3, true);
    await peer.WaitForCount(2);
    Check(observer.Messages == 1 && observer.Gaps == 2 && observer.SinkCompletions == 1);
    peer.Client.Close();
    await Throws<EndOfStreamException>(() => peer.Run);
}
static async Task WrongHandshake()
{
    using var peer = await Peer.Create(); await peer.Send(MessageType.Reset, 0);
    await Throws<InvalidDataException>(() => peer.Run);
    Check(peer.Sink.States.Count == 2 && !peer.Sink.States[^1][0]);
}
static async Task PartialTimeout()
{
    using var peer = await Peer.Create(100); await peer.Hello(); await peer.Touch(1, true);
    await peer.Stream.WriteAsync(Frame(MessageType.Touch, 2, new byte[30]).AsMemory(0, 27));
    await Throws<OperationCanceledException>(() => peer.Run);
    Check(!peer.Sink.States[^1][0]);
}
static async Task Cancellation()
{
    using var peer = await Peer.Create(); await peer.Hello(); await peer.Touch(1, true);
    await peer.WaitForCount(2); peer.Stop.Cancel();
    await Throws<OperationCanceledException>(() => peer.Run);
    Check(peer.Sink.States[^2][0] && !peer.Sink.States[^1][0]);
}

sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], cancellationToken);
}
sealed class RecordingSink : ITouchSink
{
    private readonly List<TouchState> states = new();
    public List<TouchState> States { get { lock (states) return states.ToList(); } }
    public void Apply(TouchState state) { lock (states) states.Add(state); }
    public void Reset() => Apply(TouchState.Empty);
}
sealed class RecordingObserver : IInputSessionObserver
{
    public int Messages { get; private set; }
    public uint Gaps { get; private set; }
    public int SinkCompletions { get; private set; }
    public void MessageReceived(MessageType type, uint sequence, ulong senderTimestampUs, long arrivalTimestamp) => Messages++;
    public void SequenceGap(uint missingMessages) => Gaps += missingMessages;
    public void SinkCompleted(MessageType type, long startedTimestamp, long completedTimestamp) => SinkCompletions++;
}
sealed class Peer : IDisposable
{
    public TcpClient Client { get; } = new() { NoDelay = true };
    private TcpClient server = null!;
    public CancellationTokenSource Stop { get; } = new(TimeSpan.FromSeconds(5));
    public NetworkStream Stream => Client.GetStream();
    public RecordingSink Sink { get; } = new();
    public Task Run { get; private set; } = null!;
    public static async Task<Peer> Create(int timeoutMs = 2000, IInputSessionObserver? observer = null)
    {
        var peer = new Peer();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            await peer.Client.ConnectAsync((IPEndPoint)listener.LocalEndpoint, peer.Stop.Token);
            peer.server = await listener.AcceptTcpClientAsync(peer.Stop.Token);
            peer.Run = new InputSession(peer.Sink, TimeSpan.FromMilliseconds(timeoutMs), observer).RunAsync(peer.server.GetStream(), peer.Stop.Token);
            var hello = await WireProtocol.ReadAsync(peer.Stream, peer.Stop.Token);
            if (hello.Type != MessageType.Hello) throw new Exception("Host did not send HELLO.");
            return peer;
        }
        finally { listener.Stop(); }
    }
    public ValueTask Hello(uint seq = 0) => Send(MessageType.Hello, seq, WireProtocol.HelloPayload);
    public ValueTask Touch(uint seq, bool held)
    {
        byte[] bitmap = new byte[30]; if (held) bitmap[0] = 1;
        return Send(MessageType.Touch, seq, bitmap);
    }
    public ValueTask Send(MessageType type, uint seq, byte[]? bytes = null) =>
        WireProtocol.WriteAsync(Stream, new(type, seq, WireProtocol.NowUs, bytes ?? []), Stop.Token);
    public async Task WaitForCount(int count)
    {
        while (Sink.States.Count < count) await Task.Delay(1, Stop.Token);
    }
    public void Dispose() { Stop.Cancel(); Client.Dispose(); server.Dispose(); Stop.Dispose(); }
}
