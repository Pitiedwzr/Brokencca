using System.Net;
using System.Net.Sockets;
using System.Text;
using Brokencca.Capture.Windows;
using Brokencca.Core;
using Brokencca.Video.Windows;

internal static class VideoRecoveryTests
{
    public static void Budgets()
    {
        var recovery = new VideoRecoveryPolicy(10_000_000,1280);
        Check(recovery.RetryDelayMs(VideoRecoveryReason.Decoder,1)==250);
        Check(recovery.Bitrate==10_000_000 && recovery.LongEdge==1280);
        // A hot device can take minutes to cool; probes cannot exhaust retries.
        for (int probe=0;probe<120;probe++)
            Check(recovery.RetryDelayMs(VideoRecoveryReason.Thermal,1)==5000 && recovery.Failures==1);
        Check(recovery.Bitrate==6_000_000 && recovery.LongEdge==960);
        Check(recovery.RetryDelayMs(VideoRecoveryReason.Decoder,1)==500);
        Check(recovery.RetryDelayMs(VideoRecoveryReason.Decoder,1) is null); // genuinely broken decoder remains bounded
        Check(recovery.RetryDelayMs(VideoRecoveryReason.Decoder,30)==250 && recovery.Failures==1); // healthy run resets budget
        var reduced = new VideoRecoveryPolicy(4_000_000,720);
        Check(reduced.RetryDelayMs(VideoRecoveryReason.Thermal,1)==5000);
        Check(reduced.RetryDelayMs(VideoRecoveryReason.Failure,1)==250);
        Check(reduced.Bitrate==4_000_000 && reduced.LongEdge==720); // never raise a user's lower load setting
        var faults = new VideoRecoveryPolicy(10_000_000,1280);
        Check(faults.RetryDelayMs(VideoRecoveryReason.Failure,1)==250 && faults.Bitrate==7_500_000);
        Check(faults.RetryDelayMs(VideoRecoveryReason.Failure,1)==500 && faults.LongEdge==960);
        Check(faults.RetryDelayMs(VideoRecoveryReason.Failure,1) is null);
    }

    public static async Task ThermalHandshake()
    {
        // A real loopback peer refuses HELLO before capture/encoding. The invalid
        // HWND is deliberate: this path must work without any video hardware.
        foreach (string thermal in new[] { "serious", "critical" })
        {
            using var listener = new TcpListener(IPAddress.Loopback,0);
            listener.Start();
            int port=((IPEndPoint)listener.LocalEndpoint).Port;
            var source=new WindowIdentity(0,0,null,"test","test",null);
            var profile=new CaptureProfile(1,1280,720,96,NormalizedCrop.Full,new(.5,.5,.4,"test"),true);
            using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(3));
            TextWriter saved=Console.Error; using var log=new StringWriter();
            Console.SetError(log);
            try
            {
                Task host=VideoStreamer.RunAsync(new(source,profile,Port:port),new byte[16],cancel.Token);
                using var peer=await listener.AcceptTcpClientAsync(cancel.Token);
                NetworkStream socket=peer.GetStream();
                VideoMessage hello=await VideoProtocol.ReadAsync(socket,cancel.Token);
                Check(hello.Type==VideoMessageType.Hello);
                await VideoProtocol.WriteAsync(socket,new(VideoMessageType.Error,0,0,0,0,
                    Encoding.UTF8.GetBytes($"{{\"code\":\"thermal-limit\",\"detail\":\"{thermal}\"}}")),cancel.Token);
                Check(await socket.ReadAsync(new byte[1],cancel.Token)==0); // probe closes without CONFIG or AU
                cancel.Cancel();
                await host.WaitAsync(TimeSpan.FromSeconds(1)); // cancellation interrupts the five-second cooldown
                Check(log.ToString().Contains("video_cooldown") && !log.ToString().Contains("video_failure"));
            }
            finally { cancel.Cancel(); Console.SetError(saved); }
        }
    }
    private static void Check(bool condition) { if (!condition) throw new Exception("Video recovery assertion failed."); }
}
