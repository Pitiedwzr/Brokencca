using System.Globalization;
using System.Text.Json;
using Brokencca.Core;

if (args.Length is not (4 or 6) || args[0] != "--host" || args[2] != "--ios" || (args.Length == 6 && args[4] != "--csv")) {
    Console.Error.WriteLine("Usage: Brokencca.VideoTiming --host host.log --ios ios.log [--csv frames.csv]");
    return 2;
}
try
{
    var hostRecords = VideoTimingAnalyzer.ReadRecords(File.ReadLines(args[1])).ToArray();
    var hosts = hostRecords.Where(r => r.GetProperty("kind").GetString() == "video_frame_host")
        .ToDictionary(r => (r.GetProperty("generation").GetUInt64(), r.GetProperty("frame_id").GetUInt64()));
    var clocks = hostRecords.Where(r => r.GetProperty("kind").GetString() == "video_clock").ToArray();
    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    List<VideoTimingResult> results = []; int unmatchedIos = 0;
    HashSet<(ulong, ulong)> matched = [];
    foreach (var ios in VideoTimingAnalyzer.ReadRecords(File.ReadLines(args[3])))
    {
        if (ios.GetProperty("kind").GetString() != "video_frame_ios") continue;
        var key = (ios.GetProperty("generation").GetUInt64(), ios.GetProperty("frame_id").GetUInt64());
        if (!hosts.TryGetValue(key, out var host)) { unmatchedIos++; continue; }
        var clock = VideoTimingAnalyzer.SelectClock(clocks, key.Item1, host.GetProperty("send_started_us").GetInt64());
        var result = VideoTimingAnalyzer.Analyze(host, ios, clock);
        matched.Add(key); results.Add(result);
        Console.WriteLine(JsonSerializer.Serialize(new { kind = "video_frame_timing", timing = result }, jsonOptions));
    }
    if (results.Count == 0) {
        Console.Error.WriteLine("No matching frame records. Use matching build-7+ host/IPA with --video-diagnostics and capture both logs from one host run.");
        return 1;
    }
    if (args.Length == 6)
    {
        using var csv = new StreamWriter(args[5]);
        csv.WriteLine("generation,frame_id,outcome,bootstrap,redraw,valid,capture_timestamp_ordered,clock_uncertainty_ms,clock_sample_age_ms,presented_us,target_present_us,refresh_us," +
            string.Join(',', VideoTimingAnalyzer.StageNames.Select(n => n + "_ms")));
        string Number(double? value) => value?.ToString("F6", CultureInfo.InvariantCulture) ?? "";
        foreach (var r in results) csv.WriteLine(string.Join(',', new[] {
            r.Generation.ToString(CultureInfo.InvariantCulture), r.FrameId.ToString(CultureInfo.InvariantCulture), r.Outcome,
            r.Bootstrap.ToString(), r.Redraw.ToString(), r.Valid.ToString(), r.CaptureTimestampOrdered.ToString(), Number(r.ClockUncertaintyMs), Number(r.ClockSampleAgeMs),
            r.PresentedUs.ToString(CultureInfo.InvariantCulture), r.TargetPresentUs.ToString(CultureInfo.InvariantCulture), r.RefreshUs.ToString(CultureInfo.InvariantCulture)
        }.Concat(VideoTimingAnalyzer.StageNames.Select(n => Number(r.DurationsMs[n])))));
    }
    var steady = results.Where(r => r.Valid && r.Outcome == "presented" && !r.Bootstrap && !r.Redraw).ToArray();
    var stages = VideoTimingAnalyzer.StageNames.ToDictionary(name => name, name => {
        var values = steady.Where(r => r.CaptureTimestampOrdered || name is not ("capture_to_acquire" or "capture_to_present"))
            .Select(r => r.DurationsMs[name]).Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        double? Percentile(double p) => values.Length == 0 ? null : values[Math.Max(0, (int)Math.Ceiling(values.Length*p)-1)];
        return new { count = values.Length, median_ms = Percentile(.5), p95_ms = Percentile(.95), max_ms = Percentile(1) };
    });
    Console.WriteLine(JsonSerializer.Serialize(new { kind = "video_timing_summary", matched_frames = matched.Count,
        unmatched_host = hosts.Count-matched.Count, unmatched_ios = unmatchedIos, steady_presentations = steady.Length,
        replaced = results.Count(r => r.Outcome == "replaced"), invalid = results.Count(r => !r.Valid),
        capture_timestamp_unordered = results.Count(r => !r.CaptureTimestampOrdered),
        presentation_cadence = VideoTimingAnalyzer.PresentationCadence(results), stages }, jsonOptions));
    Console.Error.WriteLine("Cross-device times are clock estimates; socket_write overlaps send_to_receive. Bootstrap/redraw/invalid/dropped frames are excluded from steady presentation summaries. Presentation timestamps do not measure camera-visible pixel response.");
    return 0;
}
catch (Exception e) when (e is IOException or JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
{
    Console.Error.WriteLine("Could not analyze timing logs: " + e.Message);
    return 1;
}
