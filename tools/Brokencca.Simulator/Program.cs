using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Brokencca.Core;

var options = Parse(args);
if (options.ContainsKey("--help"))
{
    Console.WriteLine("""
        Brokencca repeatable input simulator
          --port 24864
          --workload smoke|taps|chords|slides|repress|burst|all
          --rate 120       Snapshots per second (stress workloads)
          --duration 10    Workload duration in seconds
        The default smoke workload retains the short compatibility test.
        """);
    return;
}
int port = int.Parse(options.GetValueOrDefault("--port", "24864"));
string workload = options.GetValueOrDefault("--workload", "smoke");
int rate = int.Parse(options.GetValueOrDefault("--rate", "120"));
double duration = double.Parse(options.GetValueOrDefault("--duration", "10"), System.Globalization.CultureInfo.InvariantCulture);
if (port is < 1 or > 65535 || rate is < 1 or > 10000 || duration is <= 0 or > 3600)
    throw new ArgumentException("Port, rate, or duration is outside its supported range.");
if (workload is not ("smoke" or "taps" or "chords" or "slides" or "repress" or "burst" or "all"))
    throw new ArgumentException("Unknown workload. Use --help.");

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(duration + 30));
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
try
{
    Console.WriteLine($"Simulated iOS listening on {port}; workload={workload}, rate={rate}/s, duration={duration:F1}s.");
    using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
    client.NoDelay = true;
    NetworkStream stream = client.GetStream();
    var hello = await WireProtocol.ReadAsync(stream, stop.Token);
    if (hello.Type != MessageType.Hello) throw new InvalidDataException("Expected HELLO.");
    uint seq = 0;
    await Send(MessageType.Hello, WireProtocol.HelloPayload);
    if (workload == "smoke") await RunSmoke();
    else await RunStress();

    async ValueTask Send(MessageType type, byte[] payload) =>
        await WireProtocol.WriteAsync(stream, new(type, seq++, WireProtocol.NowUs, payload), stop.Token);

    async Task RunSmoke()
    {
        foreach (int zone in new[] { 0, 29, 30, 119, 120, 149, 150, 239 })
        {
            await Send(MessageType.Touch, State(zone));
            await Task.Delay(50, stop.Token);
            await Send(MessageType.Touch, new byte[30]);
            await Task.Delay(50, stop.Token);
        }
        await Send(MessageType.Touch, State(0));
        await Task.Delay(50, stop.Token);
        Console.WriteLine("Smoke script complete; disconnecting with zone 0 held. Host must release it.");
    }

    async Task RunStress()
    {
        byte[][] states = Workload(workload).ToArray();
        long interval = Math.Max(1, Stopwatch.Frequency / rate);
        long started = Stopwatch.GetTimestamp();
        long next = started;
        int sent = 0;
        while (Stopwatch.GetElapsedTime(started).TotalSeconds < duration)
        {
            await Send(MessageType.Touch, states[sent % states.Length]);
            sent++;
            next += interval;
            TimeSpan remaining = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, stop.Token);
        }
        await Send(MessageType.Touch, new byte[30]);
        double elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
        Console.WriteLine($"Workload complete: {sent} snapshots in {elapsed:F3}s ({sent / elapsed:F1}/s); final release sent.");
    }
}
finally { listener.Stop(); }

static Dictionary<string, string> Parse(string[] args)
{
    var parsed = new Dictionary<string, string>();
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--help") { parsed.Add(args[i], "true"); continue; }
        string name = args[i];
        if (name is not ("--port" or "--workload" or "--rate" or "--duration") || ++i >= args.Length)
            throw new ArgumentException($"Invalid option {name}. Use --help.");
        parsed.Add(name, args[i]);
    }
    return parsed;
}

static IEnumerable<byte[]> Workload(string name)
{
    IEnumerable<byte[]> Named(string item) => item switch
    {
        "taps" => [State(0), State()],
        "chords" => [State(0, 12), State(0, 12, 30, 42, 60, 72, 120, 132, 180, 192), State()],
        "slides" => Enumerable.Range(0, 30).Select(step => State(Enumerable.Range(0, 10).Select(finger => (finger * 24 + step) % 240).ToArray())),
        "repress" => [State(7), State(), State(7), State()],
        "burst" => Enumerable.Range(0, 64).Select(step => State(step % 240, (step * 7) % 240, (step * 31) % 240)),
        _ => throw new ArgumentException($"Unknown workload {item}.")
    };
    return name == "all"
        ? new[] { "taps", "chords", "slides", "repress", "burst" }.SelectMany(Named)
        : Named(name);
}

static byte[] State(params int[] zones)
{
    byte[] state = new byte[30];
    foreach (int zone in zones) state[zone / 8] |= (byte)(1 << (zone % 8));
    return state;
}
