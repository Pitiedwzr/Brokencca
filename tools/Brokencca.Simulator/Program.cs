using System.Net;
using System.Net.Sockets;
using Brokencca.Core;

// A finite scripted iOS peer for a host smoke test; no device or COM ports required.
int port = args.Length == 0 ? 24864 : int.Parse(args[0]);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
var listener = new TcpListener(IPAddress.Loopback, port);
listener.Start();
try
{
    Console.WriteLine($"Simulated iOS listening on {port}; start Brokencca.Host --dry-run --port {port}.");
    using TcpClient client = await listener.AcceptTcpClientAsync(stop.Token);
    client.NoDelay = true;
    NetworkStream stream = client.GetStream();
    var hello = await WireProtocol.ReadAsync(stream, stop.Token);
    if (hello.Type != MessageType.Hello) throw new InvalidDataException("Expected HELLO.");
    uint seq = 0;
    await WireProtocol.WriteAsync(stream, new(MessageType.Hello, seq++, WireProtocol.NowUs, WireProtocol.HelloPayload), stop.Token);
    foreach (int zone in new[] { 0, 29, 30, 119, 120, 149, 150, 239 })
    {
        byte[] state = new byte[30];
        state[zone / 8] |= (byte)(1 << (zone % 8));
        await WireProtocol.WriteAsync(stream, new(MessageType.Touch, seq++, WireProtocol.NowUs, state), stop.Token);
        await Task.Delay(50, stop.Token);
        await WireProtocol.WriteAsync(stream, new(MessageType.Touch, seq++, WireProtocol.NowUs, new byte[30]), stop.Token);
        await Task.Delay(50, stop.Token);
    }
    // Disconnect while held to exercise the host's release path.
    byte[] held = new byte[30]; held[0] = 1;
    await WireProtocol.WriteAsync(stream, new(MessageType.Touch, seq++, WireProtocol.NowUs, held), stop.Token);
    await Task.Delay(50, stop.Token);
    Console.WriteLine("Script complete; disconnecting with zone 0 held. Host must release it.");
}
finally { listener.Stop(); }
