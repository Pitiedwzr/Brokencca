using System.Buffers.Binary;
using System.Text;
using Brokencca.Core;

internal static class VideoProtocolTests
{
    public static void Codec()
    {
        // Actual libx264 Main/4.2 SPS from a progressive 720x1280, non-B-frame stream.
        byte[] sps = Convert.FromHexString("674D402AD900B40A1B0110000003001000000780F1832480");
        byte[] pps = Convert.FromHexString("68EBC3CB20");
        var parser = new H264Stream();
        parser.SetHeaders([0, 0, 0, 1, .. sps, 0, 0, 1, .. pps]);
        Assert(parser.Format == new H264Format(720, 1280, "main", 42));
        var idr = parser.Normalize([0, 0, 1, 0x65, 0xB8]); // first_mb=0, I slice
        Assert(idr.IsIdr && idr.Bytes.SequenceEqual(new byte[] { 0, 0, 0, 2, 0x65, 0xB8 }));
        Assert(parser.Normalize(idr.Bytes).Bytes.SequenceEqual(idr.Bytes));
        Reject(() => parser.Normalize([0, 0, 1, 0x41, 0xA0])); // B slice
        Reject(() => parser.Normalize([0, 0, 1, 0x65, 0xB8, 0, 0, 1, 0x65, 0xB8])); // two pictures
        Reject(() => parser.Normalize([0, 0, 1, 0x65])); // truncated slice
        byte[] changed = (byte[])sps.Clone(); changed[3] = 41;
        Reject(() => parser.SetHeaders([0, 0, 0, 1, .. changed]));
        byte[] high = (byte[])sps.Clone(); high[1] = 100;
        Reject(() => H264Stream.ParseSps(high));
        foreach (bool verified in new[] { false, true })
            VideoProtocol.Encode(new(VideoMessageType.Ready, 2, 1, 0, 0, Encoding.UTF8.GetBytes($"{{\"hardwareVerified\":{verified.ToString().ToLowerInvariant()}}}")));
        var diagnostics = new Brokencca.Host.HostDiagnostics();
        Assert(!diagnostics.InputOverloaded);
        diagnostics.QueueEnqueued(2, 21);
        Assert(diagnostics.InputOverloaded);
        var cadence = new VideoFrameCadence(); int admitted = 0;
        for (int i = 0; i < 600; i++)
            if (cadence.Admit(10_000_000L + i * 10_000_000L / 60 + (i % 2 == 0 ? 0 : -1000))) admitted++;
        Assert(admitted == 600); // 0.1 ms jitter must not halve a 60 fps source
        cadence = new VideoFrameCadence(); admitted = 0;
        for (int i = 0; i < 1200; i++) if (cadence.Admit(10_000_000L + i * 10_000_000L / 120)) admitted++;
        Assert(admitted == 600);
        diagnostics.TakeReport(); // reporting must not erase the protection signal
        Assert(diagnostics.InputOverloaded);
    }
    public static async Task Framing()
    {
        byte[] au = [0, 0, 0, 2, 0x65, 0x88];
        var frame = new VideoMessage(VideoMessageType.AccessUnit, 0x11223344,
            0x0102030405060708, 0x1112131415161718, 0x2122232425262728, au, true);
        byte[] encoded = VideoProtocol.Encode(frame);
        Assert(Convert.ToHexString(encoded) ==
            "42435644010501000600000044332211080706050403020118171615141312112827262524232221000000026588");
        using var stream = new OneByteStream([.. encoded, .. encoded]);
        var first = await VideoProtocol.ReadAsync(stream, default);
        var second = await VideoProtocol.ReadAsync(stream, default);
        Assert(first == frame || (first.Type == frame.Type && first.Payload.SequenceEqual(au) && first.IsIdr));
        Assert(second.FrameId == frame.FrameId && second.Sequence == frame.Sequence);
        foreach (int count in new[] { 0, 1, 39, 40, encoded.Length - 1 })
            await RejectEnd(new OneByteStream(encoded[..count]));
        byte[] tooLarge = (byte[])encoded.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(tooLarge.AsSpan(8), 2_097_153);
        await RejectInvalid(new MemoryStream(tooLarge));
        byte[] wrongFlags = (byte[])encoded.Clone(); wrongFlags[6] = 2;
        await RejectInvalid(new MemoryStream(wrongFlags));
        byte[] wrongIdr = (byte[])encoded.Clone(); wrongIdr[6] = 0;
        await RejectInvalid(new MemoryStream(wrongIdr));
        byte[] brokenNal = (byte[])encoded.Clone(); brokenNal[43] = 3;
        await RejectInvalid(new MemoryStream(brokenNal));
        byte[] json = Encoding.UTF8.GetBytes("{\"sessionToken\":\"00000000000000000000000000000000\",\"videoVersion\":1}");
        var hello = new VideoMessage(VideoMessageType.Hello, 0, 0, 0, 0, json);
        string timingHello = Encoding.UTF8.GetString(json)[..^1] + ",\"frameTiming\":true}";
        VideoProtocol.Encode(hello with { Payload = Encoding.UTF8.GetBytes(timingHello) });
        VideoProtocol.Encode(hello with { Payload = Encoding.UTF8.GetBytes(timingHello.Replace("true", "false")) });
        foreach (string badTiming in new[] { "1", "null", "\"true\"", "[]" })
            Reject(() => VideoProtocol.Encode(hello with { Payload = Encoding.UTF8.GetBytes(timingHello.Replace("true", badTiming)) }));
        Assert((await VideoProtocol.ReadAsync(new OneByteStream(VideoProtocol.Encode(hello)), default)).Type == VideoMessageType.Hello);
        byte[] duplicateJson = Encoding.UTF8.GetBytes("{\"videoVersion\":1,\"videoVersion\":1}");
        Reject(() => VideoProtocol.Encode(hello with { Payload = duplicateJson }));
        Reject(() => VideoProtocol.Encode(hello with { Payload = Encoding.UTF8.GetBytes("{\"sessionToken\":\"x\",\"videoVersion\":1}") }));
        Reject(() => VideoProtocol.Encode(hello with { Payload = Encoding.UTF8.GetBytes("{\"sessionToken\":\"00000000000000000000000000000000\",\"videoVersion\":1,\"extra\":0}") }));
        byte[] validConfig = Encoding.UTF8.GetBytes("""
            {"codec":"h264","profile":"main","level":42,"codedWidth":720,"codedHeight":1280,
             "fpsNum":60,"fpsDen":1,"bitrateBps":10000000,"nalLengthBytes":4,"color":"bt709-limited",
             "rotation":0,"sourceWidth":1080,"sourceHeight":1920,"crop":[0,0,1,1],
             "contentRect":[0,0,720,1280],"circle":[0.5,0.47,0.489],"sps":"ZwA=","pps":"aAA="}
            """);
        var config = new VideoMessage(VideoMessageType.Config, 1, 1, 0, 0, validConfig);
        VideoProtocol.Encode(config);
        string clipped = Encoding.UTF8.GetString(validConfig).Replace("[0,0,1,1]", "[0.1,0.1,0.8,0.8]");
        Reject(() => VideoProtocol.Encode(config with { Payload = Encoding.UTF8.GetBytes(clipped) }));
    }

    private static void Assert(bool result) { if (!result) throw new Exception("Video protocol assertion failed."); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid video frame.");
    }
    private static async Task RejectInvalid(Stream stream)
    {
        try { await VideoProtocol.ReadAsync(stream, default); }
        catch (InvalidDataException) { return; }
        throw new Exception("Expected invalid video frame.");
    }
    private static async Task RejectEnd(Stream stream)
    {
        try { await VideoProtocol.ReadAsync(stream, default); }
        catch (EndOfStreamException) { return; }
        throw new Exception("Expected truncated video frame.");
    }
    private sealed class OneByteStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], token);
    }
}
