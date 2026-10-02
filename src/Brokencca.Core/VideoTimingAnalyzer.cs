using System.Text.Json;

namespace Brokencca.Core;

public sealed record VideoTimingResult(ulong Generation, ulong FrameId, string Outcome, bool Bootstrap, bool Redraw,
    bool Valid, double? ClockUncertaintyMs, double? ClockSampleAgeMs, Dictionary<string, double?> DurationsMs);

/// <summary>Correlates one captured frame through its actual Metal presentation.
/// Cross-device durations are estimates; local durations never require clock synchronization.</summary>
public static class VideoTimingAnalyzer
{
    public static readonly string[] StageNames = ["capture_to_acquire", "acquire_to_submit", "convert_encode",
        "encoded_queue", "socket_write", "send_to_receive", "decode_queue", "decode", "decode_dispatch",
        "ready_to_render", "render_cpu", "gpu_queue", "gpu", "gpu_to_present", "receive_to_present", "capture_to_present"];

    public static IEnumerable<JsonElement> ReadRecords(IEnumerable<string> lines)
    {
        foreach (string line in lines)
        {
            int start = line.IndexOf('{');
            if (start < 0) continue;
            JsonElement record;
            try {
                using var json = JsonDocument.Parse(line[start..]);
                record = json.RootElement.Clone();
            } catch (JsonException) { continue; } // syslog can include unrelated text or incomplete final lines
            if (record.ValueKind == JsonValueKind.Object && record.TryGetProperty("kind", out var kind) && kind.ValueKind == JsonValueKind.String &&
                kind.GetString() is "video_frame_host" or "video_frame_ios" or "video_clock") yield return record;
        }
    }

    public static VideoTimingResult Analyze(JsonElement host, JsonElement ios, JsonElement? clock)
    {
        ulong generation = host.GetProperty("generation").GetUInt64(), frame = host.GetProperty("frame_id").GetUInt64();
        if (ios.GetProperty("generation").GetUInt64() != generation || ios.GetProperty("frame_id").GetUInt64() != frame ||
            Timestamp(host, "capture_us") != Timestamp(ios, "capture_us")) throw new InvalidDataException("Frame timing identity mismatch.");
        bool valid = true;
        var stages = StageNames.ToDictionary(name => name, _ => (double?)null);
        void Local(string name, JsonElement record, string first, string second)
        {
            long a = Timestamp(record, first), b = Timestamp(record, second);
            if (a == 0 || b == 0) return; // absent GPU timestamps or a frame that was never rendered
            stages[name] = (b - a) / 1000.0;
            if (b < a) valid = false;
        }
        Local("capture_to_acquire", host, "capture_us", "acquired_us");
        Local("acquire_to_submit", host, "acquired_us", "submitted_us");
        Local("convert_encode", host, "submitted_us", "encoded_us");
        Local("encoded_queue", host, "encoded_us", "send_started_us");
        Local("socket_write", host, "send_started_us", "send_completed_us");
        Local("decode_queue", ios, "received_us", "decode_submitted_us");
        Local("decode", ios, "decode_submitted_us", "decoded_us");
        Local("decode_dispatch", ios, "decoded_us", "ready_us");
        Local("ready_to_render", ios, "ready_us", "render_started_us");
        Local("render_cpu", ios, "render_started_us", "committed_us");
        Local("gpu_queue", ios, "committed_us", "gpu_started_us");
        Local("gpu", ios, "gpu_started_us", "gpu_ended_us");
        string outcome = ios.GetProperty("outcome").GetString()!;
        bool presented = outcome == "presented" && Timestamp(ios, "presented_us") > 0;
        if (presented) {
            Local("gpu_to_present", ios, "gpu_ended_us", "presented_us");
            Local("receive_to_present", ios, "received_us", "presented_us");
        }
        double? uncertaintyMs = null, clockAgeMs = null;
        if (clock is { } sample)
        {
            double offset = sample.GetProperty("offset_us").GetDouble(), uncertainty = sample.GetProperty("uncertainty_us").GetDouble();
            clockAgeMs = Math.Abs((double)Timestamp(host, "send_started_us") - Timestamp(sample, "sampled_host_us")) / 1000;
            if (sample.GetProperty("generation").GetUInt64() != generation || !double.IsFinite(offset) ||
                !double.IsFinite(uncertainty) || uncertainty < 0) throw new InvalidDataException("Invalid clock timing sample.");
            if (clockAgeMs <= 5000)
            {
                uncertaintyMs = uncertainty / 1000;
                stages["send_to_receive"] = (Timestamp(ios, "received_us") - offset - Timestamp(host, "send_started_us")) / 1000;
                if (presented) stages["capture_to_present"] = (Timestamp(ios, "presented_us") - offset - Timestamp(host, "capture_us")) / 1000;
                // Preserve small negative estimates within synchronization uncertainty.
                // Clamping them to zero would conceal measurement uncertainty.
                if (stages["send_to_receive"] < -uncertaintyMs || stages["capture_to_present"] < -uncertaintyMs) valid = false;
            }
        }
        return new(generation, frame, outcome, host.GetProperty("bootstrap").GetBoolean(), ios.GetProperty("redraw").GetBoolean(),
            valid, uncertaintyMs, clockAgeMs, stages);
    }

    public static JsonElement? SelectClock(IEnumerable<JsonElement> samples, ulong generation, long sendStartedUs)
    {
        var nearby = samples.Where(s => s.GetProperty("generation").GetUInt64() == generation)
            .Select(s => (Sample: s, Age: Math.Abs((double)Timestamp(s, "sampled_host_us") - sendStartedUs)))
            .Where(s => s.Age <= 5_000_000).ToArray();
        if (nearby.Length == 0) return null;
        // Prefer a precise nearby sample; fall back to the closest one after a gap.
        var close = nearby.Where(s => s.Age <= 2_000_000).ToArray();
        return close.Length != 0 ? close.OrderBy(s => s.Sample.GetProperty("uncertainty_us").GetDouble()).ThenBy(s => s.Age).First().Sample
            : nearby.OrderBy(s => s.Age).First().Sample;
    }

    private static long Timestamp(JsonElement record, string field)
    {
        long value = record.GetProperty(field).GetInt64();
        if (value < 0) throw new InvalidDataException($"Negative {field} timestamp.");
        return value;
    }
}
