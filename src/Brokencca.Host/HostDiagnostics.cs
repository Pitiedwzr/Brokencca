using System.Diagnostics;
using System.Text.Json;
using Brokencca.Core;

namespace Brokencca.Host;

public sealed class HostDiagnostics : IInputSessionObserver
{
    private readonly object gate = new();
    private readonly List<double> arrivalIntervalsMs = [];
    private readonly List<double> senderIntervalsMs = [];
    private readonly List<double> sinkDurationsMs = [];
    private readonly List<double> queueAgesMs = [];
    private readonly List<double> serialWriteDurationsMs = [];
    private readonly List<double> receiveToSerialMs = [];
    private readonly List<double> receiveToHookMs = [];
    private uint hookDepth;
    private bool hookConnected;
    private uint hookWatchdogResets;
    private uint ledSequence;
    private double ledCaptureAgeMs;
    private int ledPayloadsSent;
    private readonly List<double> workerIntervalsMs = [];
    private readonly List<double>[] portWriteDurationsMs = [new(), new()];
    private readonly int[] driverBytesHighWater = new int[2];
    private long lastArrival;
    private ulong lastSenderTimestampUs;
    private long reportStarted = Stopwatch.GetTimestamp();
    private int frames;
    private int touches;
    private int resets;
    private int sequenceGaps;
    private int receiverStalls;
    private int queueHighWater;
    private double oldestQueueAgeMs;
    private int queueResets;
    private int queueOverflows;

    public void MessageReceived(MessageType type, uint sequence, ulong senderTimestampUs, long arrivalTimestamp)
    {
        lock (gate)
        {
            frames++;
            if (type == MessageType.Touch) touches++;
            if (type == MessageType.Reset) resets++;
            if (lastArrival != 0)
            {
                double interval = Stopwatch.GetElapsedTime(lastArrival, arrivalTimestamp).TotalMilliseconds;
                arrivalIntervalsMs.Add(interval);
                if (interval >= 20) receiverStalls++;
            }
            if (lastSenderTimestampUs != 0 && senderTimestampUs > lastSenderTimestampUs)
                senderIntervalsMs.Add((senderTimestampUs - lastSenderTimestampUs) / 1000.0);
            lastArrival = arrivalTimestamp;
            lastSenderTimestampUs = senderTimestampUs;
        }
    }

    public void SinkCompleted(MessageType type, long startedTimestamp, long completedTimestamp)
    {
        lock (gate) sinkDurationsMs.Add(Stopwatch.GetElapsedTime(startedTimestamp, completedTimestamp).TotalMilliseconds);
    }

    public void SequenceGap(uint missing)
    {
        lock (gate) sequenceGaps += checked((int)Math.Min(missing, int.MaxValue));
    }

    public void QueueEnqueued(int depth, double oldestAgeMs)
    {
        lock (gate)
        {
            queueHighWater = Math.Max(queueHighWater, depth);
            oldestQueueAgeMs = Math.Max(oldestQueueAgeMs, oldestAgeMs);
        }
    }

    public void QueueDequeued(double ageMs)
    {
        lock (gate) queueAgesMs.Add(ageMs);
    }

    public void SerialWrite(double durationMs)
    {
        lock (gate) serialWriteDurationsMs.Add(durationMs);
    }

    public void SerialCompleted(double durationMs)
    {
        lock (gate) receiveToSerialMs.Add(durationMs);
    }

    public void HookCompleted(double durationMs) { lock (gate) receiveToHookMs.Add(durationMs); }
    public void HookStatus(uint depth, bool connected, uint watchdogResets)
    {
        lock (gate) { hookDepth = depth; hookConnected = connected; hookWatchdogResets = watchdogResets; }
    }
    public void LedCapture(uint sequence, double ageMs) { lock (gate) { ledSequence = sequence; ledCaptureAgeMs = ageMs; } }
    public void LedSent() { lock (gate) ledPayloadsSent++; }

    public void WorkerIteration(double intervalMs)
    {
        lock (gate) workerIntervalsMs.Add(intervalMs);
    }

    public void PortWrite(int side, double durationMs, int pendingDriverBytes)
    {
        lock (gate)
        {
            portWriteDurationsMs[side].Add(durationMs);
            driverBytesHighWater[side] = Math.Max(driverBytesHighWater[side], pendingDriverBytes);
        }
    }

    public void QueueReset()
    {
        lock (gate) queueResets++;
    }

    public void QueueOverflow()
    {
        lock (gate) queueOverflows++;
    }

    public string TakeReport()
    {
        object report;
        lock (gate)
        {
            long now = Stopwatch.GetTimestamp();
            double intervalMs = Stopwatch.GetElapsedTime(reportStarted, now).TotalMilliseconds;
            report = new
            {
                kind = "diagnostics",
                interval_ms = Math.Round(intervalMs, 3),
                frames,
                touches,
                resets,
                fps = Math.Round(frames * 1000 / intervalMs, 1),
                sequence_gaps = sequenceGaps,
                receiver_stalls_20ms = receiverStalls,
                arrival_interval_ms = Summary(arrivalIntervalsMs),
                sender_interval_ms = Summary(senderIntervalsMs),
                sink_duration_ms = Summary(sinkDurationsMs),
                queue_age_ms = Summary(queueAgesMs),
                queue_high_water = queueHighWater,
                oldest_queue_age_ms = Math.Round(oldestQueueAgeMs, 3),
                queue_resets = queueResets,
                queue_overflows = queueOverflows,
                serial_write_ms = Summary(serialWriteDurationsMs),
                receive_to_serial_ms = Summary(receiveToSerialMs),
                receive_to_hook_sampled_ms = Summary(receiveToHookMs),
                hook_queue_depth = hookDepth,
                hook_connected = hookConnected,
                hook_watchdog_resets_total = hookWatchdogResets,
                led_capture_sequence = ledSequence,
                led_capture_age_ms = Math.Round(ledCaptureAgeMs, 3),
                led_payloads_sent = ledPayloadsSent,
                worker_interval_ms = Summary(workerIntervalsMs),
                left_write_ms = Summary(portWriteDurationsMs[0]),
                right_write_ms = Summary(portWriteDurationsMs[1]),
                left_driver_bytes_high_water = driverBytesHighWater[0],
                right_driver_bytes_high_water = driverBytesHighWater[1]
            };
            frames = touches = resets = sequenceGaps = receiverStalls = queueHighWater = queueResets = queueOverflows = 0;
            oldestQueueAgeMs = 0;
            ledPayloadsSent = 0;
            arrivalIntervalsMs.Clear();
            senderIntervalsMs.Clear();
            sinkDurationsMs.Clear();
            queueAgesMs.Clear();
            serialWriteDurationsMs.Clear();
            receiveToSerialMs.Clear();
            receiveToHookMs.Clear();
            workerIntervalsMs.Clear();
            foreach (var durations in portWriteDurationsMs) durations.Clear();
            Array.Clear(driverBytesHighWater);
            reportStarted = now;
        }
        return JsonSerializer.Serialize(report);
    }

    private static object Summary(List<double> values)
    {
        if (values.Count == 0) return new { count = 0 };
        values.Sort();
        return new
        {
            count = values.Count,
            median = Percentile(values, .5),
            p95 = Percentile(values, .95),
            p99 = Percentile(values, .99),
            max = Math.Round(values[^1], 3)
        };
    }

    private static double Percentile(List<double> values, double percentile) =>
        Math.Round(values[(int)Math.Ceiling(percentile * values.Count) - 1], 3);
}
