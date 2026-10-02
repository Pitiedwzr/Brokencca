using System.Diagnostics;
using System.Text.Json;
using Brokencca.Capture.Windows;
using Brokencca.Core;
using Brokencca.Video.Windows;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

// Synthetic GPU frames exercise conversion, tracked surface reuse, IDR and stream parsing.
// This is an explicit hardware check, not a required test on GPU-less CI runners.
try
{
    if (args.Contains("--help")) { Console.WriteLine("Brokencca.VideoProbe [output.h264] — encode 180 GPU frames at 720x1280/60, requesting a second IDR at frame 90."); return 0; }
    string path = args.Length == 0 ? "video-probe.h264" : args[0];
    using var file = File.Create(path);
    using var gpu = new GpuDevice(videoSupport: true);
    using var texture = gpu.Device.CreateTexture2D(new Texture2DDescription
    {
        Width = 720, Height = 1280, MipLevels = 1, ArraySize = 1, Format = Format.B8G8R8A8_UNorm,
        SampleDescription = new(1, 0), Usage = ResourceUsage.Default,
        BindFlags = BindFlags.RenderTarget | BindFlags.ShaderResource
    });
    using var view = gpu.Device.CreateRenderTargetView(texture);
    using var encoder = new HardwareEncoder(gpu, 720, 1280, 10_000_000);
    var parser = new H264Stream(); var pending = new Dictionary<long, long>();
    var clock = Stopwatch.StartNew(); int submitted = 0, received = 0, idrs = 0;
    double worstMs = 0;
    while (received < 180 && clock.Elapsed.TotalSeconds < 15)
    {
        var encoded = encoder.Poll();
        if (encoded is not null)
        {
            if (!pending.Remove(encoded.Value.Time100ns, out long start)) throw new IOException("Unexpected sample timestamp.");
            worstMs = Math.Max(worstMs, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            var au = parser.Normalize(encoded.Value.Bytes);
            if (parser.Sps is null || parser.Pps is null) parser.SetHeaders(encoder.Headers());
            if (received == 0 && !au.IsIdr) throw new IOException("First sample is not IDR.");
            if (au.IsIdr) idrs++;
            if (received == 0)
                foreach (byte[] parameter in new[] { parser.Sps!, parser.Pps! }) { file.Write(new byte[] { 0, 0, 0, 1 }); file.Write(parameter); }
            foreach (byte[] nal in H264Stream.Split(au.Bytes)) { file.Write(new byte[] { 0, 0, 0, 1 }); file.Write(nal); }
            received++;
        }
        if (submitted < 180 && pending.Count < 3 && clock.Elapsed.TotalSeconds >= submitted / 60.0)
        {
            lock (gpu.Gate) gpu.Context.ClearRenderTargetView(view, new Color4((submitted % 60) / 59f, .25f, .75f, 1));
            if (submitted == 90) encoder.ForceIdr();
            long pts = 10_000_000L + submitted * 10_000_000L / 60;
            if (encoder.SubmitTexture(texture, new(0, 0, 720, 1280), pts, new(0, 0, 720, 1280)))
            { pending.Add(pts, Stopwatch.GetTimestamp()); submitted++; }
        }
        Thread.Sleep(1);
    }
    if (received != 180 || idrs < 2 || parser.Format is not { Width: 720, Height: 1280 })
        throw new IOException($"Probe failed: {received}/180 pictures, {idrs} IDRs, format {parser.Format}.");
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "video_probe", gpu.Adapter, encoder = encoder.Name, received, idrs, worst_encoder_ms = worstMs, path }));
    return 0;
}
catch (Exception e) { Console.Error.WriteLine(e); return 1; }
