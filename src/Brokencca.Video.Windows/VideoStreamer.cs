using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using Brokencca.Capture.Windows;
using Brokencca.Core;

namespace Brokencca.Video.Windows;

public sealed record VideoOptions(WindowIdentity Source, CaptureProfile Profile, int Port = 24865,
    int LongEdge = 1280, int Bitrate = 10_000_000, bool Diagnostics = false, Func<bool>? InputOverloaded = null);

/// <summary>A video child of one live control session. All failures and retries stay outside the input worker.</summary>
public static class VideoStreamer
{
    private sealed class InputOverloadException() : IOException("Video stopped because input queue age/depth exceeded its protection budget.");
    private static long generations, frameIds;
    private sealed record Frame(ulong Id, ulong CaptureUs, byte[] Bytes, bool Idr, byte[] Config, long Enqueued,
        long AcquiredUs, long SubmittedUs, long EncodedUs);
    private sealed class PipelineState { public volatile string Status = "source-idle"; public long LastAdmission; }
    public static async Task RunAsync(VideoOptions options, ReadOnlyMemory<byte> sessionToken, CancellationToken token)
    {
        int bitrate = options.Bitrate, longEdge = options.LongEdge;
        for (int attempt = 0; attempt < 3 && !token.IsCancellationRequested; attempt++)
        {
            long started = Stopwatch.GetTimestamp();
            try { await ConnectionAsync(options with { Bitrate = bitrate, LongEdge = longEdge }, sessionToken, token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception e)
            {
                Console.Error.WriteLine(JsonSerializer.Serialize(new { kind = "video_failure", attempt = attempt + 1, message = e.Message, input_preserved = true }));
                if (e is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException or ArgumentException or InputOverloadException) break;
            }
            if (Stopwatch.GetElapsedTime(started).TotalSeconds >= 30) attempt = -1;
            bitrate = Math.Max(6_000_000, bitrate * 3 / 4);
            if (bitrate == 6_000_000) longEdge = longEdge > 1280 ? 1280 : 960;
            await Task.Delay(attempt <= 0 ? 250 : 500, token);
        }
        if (!token.IsCancellationRequested) Console.Error.WriteLine("Video disabled after repeated failures. Input remains active; restart the host to retry video.");
    }

    private static async Task ConnectionAsync(VideoOptions options, ReadOnlyMemory<byte> sessionToken, CancellationToken token)
    {
        using var child = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var client = new TcpClient { NoDelay = true, SendBufferSize = 64 * 1024, ReceiveBufferSize = 16 * 1024 };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        { connect.CancelAfter(3000); await client.ConnectAsync(IPAddress.Loopback, options.Port, connect.Token); }
        NetworkStream socket = client.GetStream();
        uint outgoing = 0, previous = 0;
        bool havePrevious = false;
        ulong generation = 0;
        string receiveStage = "HELLO_ACK (before capture/encoding starts)";
        async Task Send(VideoMessageType type, byte[] payload, ulong id = 0, ulong capture = 0, bool idr = false)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(child.Token);
            deadline.CancelAfter(100);
            await VideoProtocol.WriteAsync(socket, new(type, outgoing++, generation, id, capture, payload, idr), deadline.Token);
        }
        async Task SendFrame(Frame frame, bool bootstrap)
        {
            long startedUs = MonotonicUs();
            await Send(VideoMessageType.AccessUnit, frame.Bytes, frame.Id, frame.CaptureUs, frame.Idr);
            long completedUs = MonotonicUs();
            if (options.Diagnostics) Console.WriteLine(JsonSerializer.Serialize(new {
                kind = "video_frame_host", generation, frame_id = frame.Id, bootstrap,
                capture_us = frame.CaptureUs, acquired_us = frame.AcquiredUs, submitted_us = frame.SubmittedUs,
                encoded_us = frame.EncodedUs, send_started_us = startedUs, send_completed_us = completedUs
            }));
        }
        async Task<VideoMessage> Receive(int timeout)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(child.Token); deadline.CancelAfter(timeout);
            VideoMessage message;
            try { message = await VideoProtocol.ReadAsync(socket, deadline.Token); }
            catch (EndOfStreamException e)
            {
                throw new IOException($"Video peer closed while waiting for {receiveStage}. Check the iOS video status/log and port forwarding.", e);
            }
            if (havePrevious && unchecked((int)(message.Sequence - previous)) <= 0) throw new InvalidDataException("Stale video peer sequence.");
            previous = message.Sequence; havePrevious = true;
            if (message.Type == VideoMessageType.Error) throw new IOException("iOS video error: " + System.Text.Encoding.UTF8.GetString(message.Payload));
            return message;
        }
        var helloBody = new Dictionary<string, object> { ["sessionToken"] = Convert.ToHexString(sessionToken.Span), ["videoVersion"] = 1 };
        if (options.Diagnostics) helloBody["frameTiming"] = true;
        await Send(VideoMessageType.Hello, JsonSerializer.SerializeToUtf8Bytes(helloBody));
        VideoMessage hello = await Receive(3000);
        if (hello.Type != VideoMessageType.HelloAck || hello.Generation != 0) throw new InvalidDataException("Expected video HELLO_ACK.");
        using JsonDocument caps = JsonDocument.Parse(hello.Payload);
        var channel = Channel.CreateBounded<Frame>(new BoundedChannelOptions(1) { SingleReader = true, SingleWriter = true });
        using var ready = new ManualResetEventSlim(false);
        var state = new PipelineState();
        Task producer = Task.Factory.StartNew(() => Produce(options, channel.Writer, ready, state, child.Token),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Task? receiver = null;
        try
        {
            Frame bootstrap = await ReadFrame(channel.Reader, 3000, child.Token);
            if (!bootstrap.Idr) throw new InvalidDataException("Hardware encoder's first picture was not IDR.");
            using JsonDocument config = JsonDocument.Parse(bootstrap.Config);
            JsonElement c = config.RootElement, limit = caps.RootElement;
            int width = c.GetProperty("codedWidth").GetInt32(), height = c.GetProperty("codedHeight").GetInt32();
            if (width > limit.GetProperty("maxWidth").GetInt32() || height > limit.GetProperty("maxHeight").GetInt32() ||
                width * height > limit.GetProperty("maxPixels").GetInt32() || limit.GetProperty("maxFps").GetInt32() < 60 ||
                c.GetProperty("level").GetInt32() > limit.GetProperty("maxLevel").GetInt32() ||
                !limit.GetProperty("profiles").EnumerateArray().Any(p => p.GetString() == c.GetProperty("profile").GetString()))
                throw new InvalidDataException("iOS video capabilities do not support this configuration.");
            int maxAu = limit.GetProperty("maxAuBytes").GetInt32();
            generation = (ulong)Interlocked.Increment(ref generations);
            await Send(VideoMessageType.Config, bootstrap.Config);
            receiveStage = "decoder READY";
            VideoMessage acknowledged = await Receive(3000);
            if (acknowledged.Type != VideoMessageType.Ready || acknowledged.Generation != generation)
                throw new InvalidDataException("Expected hardware decoder READY.");
            using (JsonDocument ack = JsonDocument.Parse(acknowledged.Payload))
                Console.WriteLine(ack.RootElement.GetProperty("hardwareVerified").GetBoolean()
                    ? "iOS decoder hardware verified." : "iOS decoder capability checked; session verification unavailable on iOS 15–16.");
            long sentId = 0;
            ulong lastReceived = 0, lastDecoded = 0, lastPresented = 0;
            long progress = Stopwatch.GetTimestamp();
            long presentationStarted = progress;
            int pendingDecode = 0;
            int displayMilliHz = 0; long replacedDecoded = 0, pingUs = 0;
            double clockOffsetUs = 0, clockUncertaintyUs = double.PositiveInfinity;
            string thermal = "nominal";
            receiveStage = "video feedback";
            receiver = Task.Run(async () =>
            {
                try
                {
                    while (!child.IsCancellationRequested)
                    {
                        VideoMessage feedback = await Receive(1000);
                        if (feedback.Generation != generation) throw new InvalidDataException("Video feedback generation changed.");
                        if (feedback.Type == VideoMessageType.RequestIdr) throw new IOException("iOS requested a fresh IDR generation.");
                        if (feedback.Type == VideoMessageType.Error) throw new IOException("iOS video error: " + System.Text.Encoding.UTF8.GetString(feedback.Payload));
                        if (feedback.Type == VideoMessageType.ClockPong)
                        {
                            using JsonDocument reply = JsonDocument.Parse(feedback.Payload); var p = reply.RootElement;
                            long t1 = long.Parse(p.GetProperty("t1Us").GetString()!), t2 = long.Parse(p.GetProperty("t2Us").GetString()!), t3 = long.Parse(p.GetProperty("t3Us").GetString()!);
                            long t4 = MonotonicUs();
                            if (t1 != Interlocked.Read(ref pingUs) || t4 < t1 || t3 < t2 || t3 - t2 > t4 - t1) throw new InvalidDataException("Invalid clock reply.");
                            double uncertainty = ((double)(t4 - t1) - (t3 - t2)) / 2;
                            double offset = ((double)t2 - t1 + ((double)t3 - t4)) / 2;
                            if (options.Diagnostics) Console.WriteLine(JsonSerializer.Serialize(new {
                                kind = "video_clock", generation, sampled_host_us = t4, offset_us = offset, uncertainty_us = uncertainty
                            }));
                            if (uncertainty < Volatile.Read(ref clockUncertaintyUs))
                            {
                                Volatile.Write(ref clockOffsetUs, offset);
                                Volatile.Write(ref clockUncertaintyUs, uncertainty);
                            }
                            continue;
                        }
                        if (feedback.Type != VideoMessageType.Feedback) throw new InvalidDataException("Unexpected video feedback type.");
                        using JsonDocument report = JsonDocument.Parse(feedback.Payload); var f = report.RootElement;
                        ulong received = ulong.Parse(f.GetProperty("receivedId").GetString()!), decoded = ulong.Parse(f.GetProperty("decodedId").GetString()!), presented = ulong.Parse(f.GetProperty("presentedId").GetString()!);
                        if (received < lastReceived || decoded < lastDecoded || presented < lastPresented || received > (ulong)Interlocked.Read(ref sentId))
                            throw new InvalidDataException("Invalid video feedback frame history.");
                        if (presented > lastPresented) Interlocked.Exchange(ref progress, Stopwatch.GetTimestamp());
                        lastReceived = received; lastDecoded = decoded; lastPresented = presented;
                        Volatile.Write(ref pendingDecode, f.GetProperty("pendingDecode").GetInt32());
                        Volatile.Write(ref displayMilliHz, f.GetProperty("displayMilliHz").GetInt32());
                        Interlocked.Exchange(ref replacedDecoded, f.GetProperty("replacedDecoded").GetInt64());
                        Volatile.Write(ref thermal, f.GetProperty("thermal").GetString()!);
                    }
                }
                finally { child.Cancel(); client.Close(); }
            }, CancellationToken.None);
            bool initialIdr = false;
            // Keep the bootstrap IDR through negotiation: a static source may not emit another frame.
            // Its original capture timestamp remains intact for honest latency diagnostics.
                if (bootstrap.Bytes.Length > maxAu) throw new IOException("Bootstrap AU exceeds receiver limit.");
                Interlocked.Exchange(ref sentId, (long)bootstrap.Id);
                await SendFrame(bootstrap, true);
                initialIdr = true;
            ready.Set();
            Console.WriteLine($"Video active: {width}x{height} @ 60 fps, {options.Bitrate / 1_000_000.0:F1} Mbps, generation {generation}.");
            long lastReport = Stopwatch.GetTimestamp(); int sent = 0; long bytes = 0;
            while (!child.IsCancellationRequested)
            {
                if (options.InputOverloaded?.Invoke() == true) throw new InputOverloadException();
                Frame? next = null;
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(child.Token))
                {
                    wait.CancelAfter(250);
                    try { next = await channel.Reader.ReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!child.IsCancellationRequested) { }
                }
                if (next is not null)
                {
                    if (Stopwatch.GetElapsedTime(next.Enqueued).TotalMilliseconds > 50 || next.Bytes.Length > maxAu)
                        throw new IOException("Encoded backlog exceeds age/size limit.");
                    if (!initialIdr && !next.Idr) throw new InvalidDataException("First video frame is not IDR.");
                    initialIdr = true;
                    Interlocked.Exchange(ref sentId, (long)next.Id);
                    await SendFrame(next, false);
                    sent++; bytes += next.Bytes.Length;
                }
                else await Send(VideoMessageType.Status, JsonSerializer.SerializeToUtf8Bytes(new { state = state.Status, reason = state.Status == "paused" ? "Source minimized or unavailable" : "Waiting for source update" }));
                if (Volatile.Read(ref thermal) is "serious" or "critical") throw new IOException("iOS thermal limit: " + thermal);
                if (lastPresented == 0 && Stopwatch.GetElapsedTime(presentationStarted).TotalSeconds > 3)
                    throw new IOException("iOS did not present the first video frame.");
                if (Stopwatch.GetElapsedTime(Interlocked.Read(ref state.LastAdmission)).TotalMilliseconds < 100 && Stopwatch.GetElapsedTime(Interlocked.Read(ref progress)).TotalMilliseconds > 250)
                    throw new IOException("Video presentation feedback stalled.");
                if (options.Diagnostics && Stopwatch.GetElapsedTime(lastReport).TotalSeconds >= 1)
                {
                    Console.WriteLine(JsonSerializer.Serialize(new { kind = "video", generation, sent_frames = sent, sent_bytes = bytes, received_id = lastReceived, decoded_id = lastDecoded, presented_id = lastPresented, pending_decode = Volatile.Read(ref pendingDecode), thermal,
                        display_millihz = Volatile.Read(ref displayMilliHz), replaced_decoded = Interlocked.Read(ref replacedDecoded),
                        clock_offset_us = Volatile.Read(ref clockOffsetUs), clock_uncertainty_us = double.IsFinite(Volatile.Read(ref clockUncertaintyUs)) ? Volatile.Read(ref clockUncertaintyUs) : (double?)null }));
                    Interlocked.Exchange(ref pingUs, MonotonicUs());
                    await Send(VideoMessageType.ClockPing, JsonSerializer.SerializeToUtf8Bytes(new { t1Us = Interlocked.Read(ref pingUs).ToString(System.Globalization.CultureInfo.InvariantCulture) }));
                    sent = 0; bytes = 0; lastReport = Stopwatch.GetTimestamp();
                }
            }
            if (receiver is not null) await receiver;
        }
        catch (ChannelClosedException e) when (e.InnerException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
            throw;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && receiver is not null)
        {
            // The feedback task cancels the sender on failure; preserve its useful error message.
            await receiver;
            throw;
        }
        finally
        {
            child.Cancel(); client.Close(); ready.Set();
            try { await producer; } catch (Exception) { }
            if (receiver is not null) try { await receiver; } catch (Exception) { }
        }
    }
    private static long MonotonicUs() => (long)(Stopwatch.GetTimestamp() * (1_000_000.0 / Stopwatch.Frequency));
    private static async Task<Frame> ReadFrame(ChannelReader<Frame> reader, int timeoutMs, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(timeoutMs);
        return await reader.ReadAsync(deadline.Token);
    }
    private static void Produce(VideoOptions options, ChannelWriter<Frame> output, ManualResetEventSlim ready, PipelineState state, CancellationToken token)
    {
        try
        {
            using var gpu = new GpuDevice(videoSupport: true);
            using var capture = new WgcCaptureSession(gpu, options.Source, new(options.Profile.Crop));
            HardwareEncoder? encoder = null;
            var h264 = new H264Stream();
            var pending = new Dictionary<long, (ulong Id, ulong CaptureUs, long Submitted, long AcquiredUs, long SubmittedUs)>();
            long geometry = 0; int width = 0, height = 0, sourceWidth = 0, sourceHeight = 0;
            var cadence = new VideoFrameCadence();
            PixelRect content = default; byte[]? configuration = null; bool bootstrapSent = false;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (options.InputOverloaded?.Invoke() == true) throw new InputOverloadException();
                    if (capture.State == CaptureState.Faulted) throw new IOException("Capture failed.", capture.Failure);
                    if (capture.State == CaptureState.Stopped || !options.Source.IsAlive) throw new IOException("Selected source window closed; select it again.");
                    state.Status = capture.State == CaptureState.Paused ? "paused" : "source-idle";
                    if (encoder is not null)
                    {
                        var encoded = encoder.Poll();
                        if (encoded is not null)
                        {
                            long encodedUs = MonotonicUs();
                            if (!pending.Remove(encoded.Value.Time100ns, out var info)) throw new IOException("Unmatched encoder output timestamp.");
                            H264AccessUnit au = h264.Normalize(encoded.Value.Bytes);
                            // Some MFT media types retain a provisional SPS whose VUI differs from
                            // the actual bitstream. Prefer the first picture's in-band parameter sets.
                            if (h264.Sps is null || h264.Pps is null) h264.SetHeaders(encoder.Headers());
                            H264Format format = h264.Format ?? throw new IOException("Encoder did not supply SPS.");
                            if (h264.Pps is null || format.Width != width || format.Height != height) throw new IOException("Encoder codec configuration mismatches dimensions.");
                            configuration ??= Config(options, width, height, sourceWidth, sourceHeight, content, format, h264.Sps!, h264.Pps);
                            if (!output.TryWrite(new(info.Id, info.CaptureUs, au.Bytes, au.IsIdr, configuration, Stopwatch.GetTimestamp(),
                                info.AcquiredUs, info.SubmittedUs, encodedUs)))
                                throw new IOException("Encoded video queue overflow; start fresh at IDR.");
                            if (!bootstrapSent) { bootstrapSent = true; ready.Wait(token); }
                        }
                        if (pending.Values.Any(p => Stopwatch.GetElapsedTime(p.Submitted).TotalMilliseconds > 100)) throw new IOException("Hardware encoder stopped making progress.");
                    }
                    if (pending.Count >= (bootstrapSent ? 3 : 1)) { Thread.Sleep(1); continue; }
                    using var frame = capture.TryAcquire();
                    if (frame is null) { Thread.Sleep(1); continue; }
                    var f = frame.Info;
                    long acquiredUs = MonotonicUs();
                    if (!f.GeometryResolved) throw new IOException("Capture geometry unresolved; use the preview to validate the window.");
                    options.Profile.ValidateFor(f.Client.Width, f.Client.Height);
                    if (encoder is null)
                    {
                        geometry = f.GeometryGeneration;
                        sourceWidth = f.Client.Width; sourceHeight = f.Client.Height;
                        double scale = Math.Min(Math.Min(1, (double)options.LongEdge / Math.Max(f.Crop.Width, f.Crop.Height)),
                            Math.Sqrt(2_070_000.0 / ((double)f.Crop.Width * f.Crop.Height)));
                        int targetWidth = Math.Max(2, (int)Math.Round(f.Crop.Width * scale)), targetHeight = Math.Max(2, (int)Math.Round(f.Crop.Height * scale));
                        width = (targetWidth + 1) & ~1; height = (targetHeight + 1) & ~1;
                        content = new(0, 0, targetWidth, targetHeight);
                        encoder = new(gpu, width, height, options.Bitrate);
                        Console.WriteLine(JsonSerializer.Serialize(new { kind = "video_encoder", hardware = true, name = encoder.Name, adapter = gpu.Adapter, width, height, bitrate = options.Bitrate }));
                        // Retain the first lease while initial NeedInput credits arrive. Otherwise a
                        // static window can lose its only frame and never complete negotiation.
                        long startup = Stopwatch.GetTimestamp();
                        while (!token.IsCancellationRequested && Stopwatch.GetElapsedTime(startup).TotalMilliseconds < 100)
                        {
                            encoder.Poll();
                            long submittedUs = MonotonicUs();
                            if (encoder.Submit(frame, f.Timestamp100ns, content))
                            {
                                pending.Add(f.Timestamp100ns, ((ulong)Interlocked.Increment(ref frameIds),
                                    (ulong)(f.Timestamp100ns / 10), Stopwatch.GetTimestamp(), acquiredUs, submittedUs));
                                Interlocked.Exchange(ref state.LastAdmission, Stopwatch.GetTimestamp());
                                cadence.Admit(f.Timestamp100ns);
                                break;
                            }
                            Thread.Sleep(1);
                        }
                        if (pending.Count == 0) throw new IOException("Hardware encoder did not request its first frame.");
                        continue;
                    }
                    if (geometry != f.GeometryGeneration) throw new IOException("Capture geometry changed; new video generation required.");
                    long now100ns = (long)(Stopwatch.GetTimestamp() * (10_000_000.0 / Stopwatch.Frequency));
                    double rawAgeMs = (now100ns - f.Timestamp100ns) / 10000.0;
                    if (rawAgeMs < -20) throw new IOException("Capture and host monotonic epochs do not agree.");
                    if (rawAgeMs > 33) continue;
                    if (!cadence.Admit(f.Timestamp100ns)) continue;
                    long encoderSubmittedUs = MonotonicUs();
                    if (encoder.Submit(frame, f.Timestamp100ns, content))
                    {
                        ulong id = (ulong)Interlocked.Increment(ref frameIds);
                        pending.Add(f.Timestamp100ns, (id, (ulong)(f.Timestamp100ns / 10), Stopwatch.GetTimestamp(), acquiredUs, encoderSubmittedUs));
                        Interlocked.Exchange(ref state.LastAdmission, Stopwatch.GetTimestamp());
                        state.Status = "running";
                    }
                    Thread.Sleep(1);
                }
            }
            finally { encoder?.Dispose(); }
            output.TryComplete();
        }
        catch (Exception e) { output.TryComplete(e); }
    }
    private static byte[] Config(VideoOptions options, int width, int height, int sourceWidth, int sourceHeight, PixelRect content, H264Format format, byte[] sps, byte[] pps)
    {
        var profile = options.Profile; var crop = profile.Crop; var circle = profile.Circle;
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            codec = "h264", profile = format.Profile, level = format.Level, codedWidth = width, codedHeight = height,
            fpsNum = 60, fpsDen = 1, bitrateBps = options.Bitrate, nalLengthBytes = 4, color = "bt709-limited", rotation = 0,
            sourceWidth, sourceHeight,
            crop = new[] { crop.X, crop.Y, crop.Width, crop.Height }, contentRect = new[] { content.X, content.Y, content.Width, content.Height },
            circle = new[] { circle.CenterX, circle.CenterY, circle.Radius }, sps = Convert.ToBase64String(sps), pps = Convert.ToBase64String(pps)
        });
    }
}
