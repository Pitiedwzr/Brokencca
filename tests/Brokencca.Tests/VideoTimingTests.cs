using System.Text.Json;
using System.Text.Json.Nodes;
using Brokencca.Core;

internal static class VideoTimingTests
{
    public static void Pipeline()
    {
        var host = Json("""
            {"kind":"video_frame_host","generation":1,"frame_id":10,"bootstrap":false,"capture_us":1000000,
            "acquired_us":1001000,"submitted_us":1002000,"encoded_us":1007000,"send_started_us":1008000,"send_completed_us":1008400}
            """);
        var ios = Json("""
            {"kind":"video_frame_ios","generation":1,"frame_id":10,"outcome":"presented","redraw":false,"capture_us":1000000,
            "received_us":1409000,"decode_submitted_us":1410000,"decoded_us":1413000,"ready_us":1413500,
            "render_started_us":1414000,"committed_us":1414500,"gpu_started_us":1414600,"gpu_ended_us":1415400,
            "gpu_completed_us":1415500,"presented_us":1416667}
            """);
        var clock = Json("""{"kind":"video_clock","generation":1,"sampled_host_us":1010000,"offset_us":400000,"uncertainty_us":200} """);
        var result = VideoTimingAnalyzer.Analyze(host, ios, clock);
        Check(result.Valid && result.CaptureTimestampOrdered && result.FrameId == 10 && result.ClockUncertaintyMs == .2);
        Near(result.DurationsMs["acquire_to_present"],15.667);
        Near(result.DurationsMs["capture_to_present"],16.667); Near(result.DurationsMs["send_to_receive"],1);
        Near(result.DurationsMs["convert_encode"],5); Near(result.DurationsMs["ready_to_render"],.5);
        Near(result.DurationsMs["gpu"],.8); Near(result.DurationsMs["socket_write"],.4);
        double sum = VideoTimingAnalyzer.StageNames.Where(n => n is not ("socket_write" or "receive_to_present" or "capture_to_present" or "acquire_to_present"))
            .Sum(n => result.DurationsMs[n] ?? 0);
        Near(sum,16.667); // no double-counting CPU/GPU or socket-write overlap
        var unsynced = VideoTimingAnalyzer.Analyze(host,ios,null);
        Check(unsynced.Valid && unsynced.DurationsMs["capture_to_present"] is null && unsynced.DurationsMs["send_to_receive"] is null);
        Near(unsynced.DurationsMs["receive_to_present"],7.667);
        var unavailableGpu = Json(ios.GetRawText().Replace("1414600","0").Replace("1415400","0"));
        var noGpu = VideoTimingAnalyzer.Analyze(host,unavailableGpu,clock);
        Check(noGpu.Valid && noGpu.DurationsMs["gpu"] is null); Near(noGpu.DurationsMs["capture_to_present"],16.667);
        var dropped = Json(ios.GetRawText().Replace("\"presented\"","\"replaced\"").Replace("1416667","0"));
        Check(VideoTimingAnalyzer.Analyze(host,dropped,clock).DurationsMs["capture_to_present"] is null);
        var redraw = Json(ios.GetRawText().Replace("\"redraw\":false","\"redraw\":true"));
        Check(VideoTimingAnalyzer.Analyze(host,redraw,clock).Redraw);
        var invalid = Json(ios.GetRawText().Replace("1413000","1409000"));
        Check(!VideoTimingAnalyzer.Analyze(host,invalid,clock).Valid);
        // Real WGC logs have compositor timestamps several ms ahead of acquisition.
        var futureCaptureHost = Json(host.GetRawText().Replace("\"capture_us\":1000000", "\"capture_us\":1004000"));
        var futureCaptureIos = Json(ios.GetRawText().Replace("\"capture_us\":1000000", "\"capture_us\":1004000"));
        var futureCapture = VideoTimingAnalyzer.Analyze(futureCaptureHost,futureCaptureIos,clock);
        Check(futureCapture.Valid && !futureCapture.CaptureTimestampOrdered);
        Near(futureCapture.DurationsMs["capture_to_acquire"],-3);
        Near(futureCapture.DurationsMs["acquire_to_present"],15.667);
        Near(futureCapture.DurationsMs["decode"],3);
        // Signed clock offsets, uncertainty and sub-millisecond negative estimates.
        var uncertain = Json(clock.GetRawText().Replace("400000","401100"));
        var nearZero = VideoTimingAnalyzer.Analyze(host,ios,uncertain);
        Check(nearZero.Valid); Near(nearZero.DurationsMs["send_to_receive"],-.1);
        var wrongOffset = Json(clock.GetRawText().Replace("400000","410000"));
        Check(!VideoTimingAnalyzer.Analyze(host,ios,wrongOffset).Valid);
        var behind = JsonNode.Parse(ios.GetRawText())!.AsObject();
        foreach (var pair in behind.ToArray().Where(p => p.Key.EndsWith("_us",StringComparison.Ordinal) && p.Key!="capture_us"))
            behind[pair.Key]=pair.Value!.GetValue<long>()-800000;
        var negativeOffset = Json(clock.GetRawText().Replace("400000","-400000"));
        var behindResult = VideoTimingAnalyzer.Analyze(host,Json(behind.ToJsonString()),negativeOffset);
        Check(behindResult.Valid); Near(behindResult.DurationsMs["capture_to_present"],16.667);
        var old = Json(clock.GetRawText().Replace("1010000","7010000"));
        Check(VideoTimingAnalyzer.Analyze(host,ios,old).DurationsMs["capture_to_present"] is null);
        Check(VideoTimingAnalyzer.SelectClock([old],1,1008000) is null);
        var otherGeneration = Json(clock.GetRawText().Replace("\"generation\":1","\"generation\":2"));
        Check(VideoTimingAnalyzer.SelectClock([otherGeneration,clock],1,1008000)!.Value.GetRawText()==clock.GetRawText());
        Reject(() => VideoTimingAnalyzer.Analyze(host,ios,otherGeneration));
        Reject(() => VideoTimingAnalyzer.Analyze(host,Json(ios.GetRawText().Replace("\"frame_id\":10","\"frame_id\":11")),clock));
        var records = VideoTimingAnalyzer.ReadRecords(["[connected]",host.GetRawText(),
            "Oct 3 device[1] <Notice>: BCCA_VIDEO_FRAME " + ios.GetRawText().Replace("\n",""),"{broken",
            "{\"kind\":\"diagnostics\"}"]).ToArray();
        Check(records.Length==2);
        var targeted = Json(ios.GetRawText().Replace("\"presented_us\":1416667", "\"presented_us\":1416667,\"target_present_us\":1416000,\"refresh_us\":1399333"));
        var targetResult = VideoTimingAnalyzer.Analyze(host,targeted,clock);
        Check(targetResult.Valid && targetResult.TargetPresentUs==1416000 && targetResult.RefreshUs==1399333);
        Near(targetResult.DurationsMs["target_to_present"],.667);
        Check(result.DurationsMs["target_to_present"] is null); // pre-build-9 logs remain readable
        var cadence = VideoTimingAnalyzer.PresentationCadence([
            result, result with { FrameId=11 }, // two frames reported at the same presentation time
            result with { FrameId=12,PresentedUs=result.PresentedUs+16667 },
            result with { FrameId=13,PresentedUs=result.PresentedUs+33334 },
            result with { FrameId=14,Redraw=true }, result with { FrameId=15,Bootstrap=true },
            result with { FrameId=16,Valid=false }, result with { FrameId=17,Outcome="replaced" },
            result with { Generation=2,FrameId=18 }
        ]);
        Check(cadence.Length==2 && cadence[0].Presentations==4 && cadence[0].DistinctPresentationTimes==3 &&
            cadence[0].SameTimestampAdditionalFrames==1 && cadence[0].DistinctTimesPerSecond > 59.99 &&
            cadence[0].DistinctTimesPerSecond < 60.01);
        Check(cadence[1].Presentations==1 && cadence[1].DistinctTimesPerSecond is null);
        Check(VideoTimingAnalyzer.PresentationCadence([]).Length==0);
    }
    private static JsonElement Json(string text) { using var doc=JsonDocument.Parse(text); return doc.RootElement.Clone(); }
    private static void Near(double? value,double expected) => Check(value is not null && Math.Abs(value.Value-expected)<1e-8);
    private static void Check(bool condition) { if (!condition) throw new Exception("Video timing assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Expected rejected timing correlation."); }
}
