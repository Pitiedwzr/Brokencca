using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using Brokencca.Core;

namespace Brokencca.Host;

[SupportedOSPlatform("windows")]
internal static class LedForwarder
{
    public static async Task RunAsync(HookTouchSink sink, int port, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient { NoDelay = true };
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                connect.CancelAfter(1000);
                await client.ConnectAsync(IPAddress.Loopback, port, connect.Token);
                Console.WriteLine("LED stream connected (independent of input).");
                uint sequence = 0;
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(1000.0 / 30));
                while (await timer.WaitForNextTickAsync(token))
                {
                    byte[] packet = LedProtocol.Encode(sequence++, sink.ReadLeds());
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    deadline.CancelAfter(250);
                    await client.GetStream().WriteAsync(packet, deadline.Token);
                    sink.RecordLedSent();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
            {
                // No touch reset, no LED FIFO, and no shared outgoing input socket.
                await Task.Delay(1000, token);
            }
        }
    }
}
