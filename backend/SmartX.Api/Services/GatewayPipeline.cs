using Stopwatch = System.Diagnostics.Stopwatch;
using System.Globalization;
using SmartX.Api.Models;

namespace SmartX.Api.Services;

// The real-time ingestion pipeline:
//
//   POST /api/telemetry  ->  Accept()  ->  DualLaneQueue  ->  TelemetryProcessor  ->  ProcessOne()
//
// Accept() parses + classifies the packet and decides which lane it goes into.
// ProcessOne() is the only place state is mutated (Part 1 store, live device
// dictionary, sorted history, alert sets, action engine, SSE broadcast).
public class GatewayPipeline(
    SmartXDataStructures ds,
    TelemetryStore store,
    DeviceStateStore states,
    AlertTracker alerts,
    ActionEngine engine,
    LiveBroadcaster live,
    SmartX.Api.Services.Topology.TelemetryHistoryIndex history,
    IConfiguration config)
{
    private readonly object _statsLock = new();
    private long _sequence;

    private readonly LaneCounter _standardStats = new("standard");
    private readonly LaneCounter _priorityStats = new("priority");

    // Recently processed packets for the live feed (FIFO, capped).
    private readonly Queue<ProcessedPacketView> _recent = new();

    // Timestamps of the last few seconds of processing, for a packets/sec gauge.
    private readonly Queue<long> _processedTicks = new();

    // TelemetryQueue (FIFO) + CriticalAlertQueue (priority) from SmartXDataStructures.
    public DualLaneQueue<QueuedPacket> Queue { get; } = new(ds.TelemetryQueue, ds.CriticalAlertQueue);

    public int ProcessingRatePerSecond { get; set; } = config.GetValue("Gateway:ProcessingRatePerSecond", 60);

    public sealed record AcceptResult(bool Ok, string? Error, QueuedPacket? Packet, string? Lane, int Position);

    public AcceptResult Accept(TelemetryIngestRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.DeviceId) || string.IsNullOrWhiteSpace(req.Metric))
            return new AcceptResult(false, "deviceId and metric are required.", null, null, 0);

        ValueKind kind;
        double numeric = 0;
        bool boolean = false;

        // TryParse rather than Parse (Part 1 used Parse, which threw a 500 on a
        // bad payload - a malformed packet from one device shouldn't crash anything).
        switch (req.ValueType?.ToLowerInvariant())
        {
            case "float":
                if (!double.TryParse(req.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out numeric))
                    return new AcceptResult(false, $"'{req.RawValue}' is not a valid float.", null, null, 0);
                kind = ValueKind.Float;
                break;
            case "int":
                if (!int.TryParse(req.RawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i))
                    return new AcceptResult(false, $"'{req.RawValue}' is not a valid int.", null, null, 0);
                numeric = i;
                kind = ValueKind.Int;
                break;
            case "bool":
                if (!bool.TryParse(req.RawValue, out boolean))
                    return new AcceptResult(false, $"'{req.RawValue}' is not a valid bool.", null, null, 0);
                kind = ValueKind.Bool;
                break;
            default:
                return new AcceptResult(false, "valueType must be float, int or bool.", null, null, 0);
        }

        var severity = kind == ValueKind.Bool ? PacketSeverity.Normal : SensorBands.Classify(req.Metric, numeric);

        var packet = new QueuedPacket(
            Interlocked.Increment(ref _sequence),
            req.DeviceId.Trim(),
            req.Metric.Trim().ToLowerInvariant(),
            kind,
            numeric,
            boolean,
            severity,
            DateTime.UtcNow,
            Stopwatch.GetTimestamp());

        states.Touch(packet.DeviceId, packet.ReceivedAt);

        if (severity == PacketSeverity.Normal)
        {
            if (!Queue.EnqueueStandard(packet))
                return new AcceptResult(false, "Gateway queue is full - try again shortly.", null, null, 0);

            _standardStats.Enqueued();
            return new AcceptResult(true, null, packet, "standard", Queue.StandardCount);
        }

        Queue.EnqueuePriority(packet, (int)severity);
        _priorityStats.Enqueued();
        return new AcceptResult(true, null, packet, "priority", Queue.PriorityCount);
    }

    public void ProcessOne(QueuedPacket p, bool fromPriorityLane)
    {
        var now = DateTime.UtcNow;
        var latencyMs = Stopwatch.GetElapsedTime(p.EnqueuedTimestamp).TotalMilliseconds;

        // 1. Part 1 typed store (TelemetryPacket<T>, no boxing) + anomaly log.
        switch (p.Kind)
        {
            case ValueKind.Float: store.Ingest(p.DeviceId, p.Metric, (float)p.NumericValue); break;
            case ValueKind.Int: store.Ingest(p.DeviceId, p.Metric, (int)p.NumericValue); break;
            case ValueKind.Bool: store.Ingest(p.DeviceId, p.Metric, p.BoolValue); break;
        }

        // 2. O(1) dictionary lookup + sorted history insert.
        var state = states.Apply(p, now);
        var name = state.Name;

        // Final PoE: every numeric reading also goes into that sensor's AVL tree.
        if (p.Kind != ValueKind.Bool) history.Add(p.DeviceId, p.Metric, p.ReceivedAt, p.NumericValue);

        // 3. Any packet from a node proves it is alive again.
        if (alerts.MarkReconnected(p.DeviceId, name, now))
        {
            states.SetAlert(p.DeviceId, AlertLevel.Ok, null);
            live.Publish("alert", new { kind = "reconnected", deviceId = p.DeviceId, deviceName = name, at = now });
        }

        // 4. Alert state (HashSet de-duplication lives inside AlertTracker).
        if (p.Kind != ValueKind.Bool)
        {
            if (p.Severity == PacketSeverity.Normal)
            {
                var resolved = alerts.ResolveDevice(p.DeviceId, name, null, now);
                if (resolved.Count > 0 || state.Alert is AlertLevel.Warning or AlertLevel.Critical)
                {
                    states.SetAlert(p.DeviceId, AlertLevel.Ok, null);
                    if (resolved.Count > 0)
                        live.Publish("alert", new { kind = "resolved", deviceId = p.DeviceId, deviceName = name, keys = resolved, at = now });
                }
            }
            else
            {
                var direction = SensorBands.Direction(p.Metric, p.NumericValue);
                var key = AlertTracker.ErrorKey(p.Metric, direction, p.DeviceId);
                var level = p.Severity == PacketSeverity.Critical ? AlertLevel.Critical : AlertLevel.Warning;
                SensorBands.TryGet(p.Metric, out var band);
                var unit = SensorBands.UnitFor(p.Metric);
                var message = $"{p.Metric} {p.DisplayValue}{unit} is {(direction == "low" ? "below" : "above")} the {band?.Min}-{band?.Max}{unit} band";

                alerts.ResolveDevice(p.DeviceId, name, key, now); // e.g. clears "high" if it is now "low"
                states.SetAlert(p.DeviceId, level, message);

                if (alerts.Raise(key, p.DeviceId, name, level, message, now))
                {
                    var label = $"{name} {p.Metric} {direction}";
                    engine.Record(new ActivityEvent(ActivityKind.Alert, key, label, now));
                    live.Publish("alert", new { kind = "raised", key, deviceId = p.DeviceId, deviceName = name, level = level.ToString(), message, at = now });
                }
            }
        }

        // 5. Stats + live feed.
        var view = new ProcessedPacketView(
            p.Sequence, p.DeviceId, name, p.Metric, p.DisplayValue, p.Severity.ToString(),
            fromPriorityLane ? "priority" : "standard", Math.Round(latencyMs, 1), now);

        lock (_statsLock)
        {
            (fromPriorityLane ? _priorityStats : _standardStats).Processed(latencyMs);
            _recent.Enqueue(view);
            while (_recent.Count > 60) _recent.Dequeue();

            var t = Stopwatch.GetTimestamp();
            _processedTicks.Enqueue(t);
            var cutoff = t - Stopwatch.Frequency * 5;
            while (_processedTicks.Count > 0 && _processedTicks.Peek() < cutoff) _processedTicks.Dequeue();
        }

        live.Publish("packet", view);
    }

    public object Stats()
    {
        lock (_statsLock)
        {
            var now = Stopwatch.GetTimestamp();
            var cutoff = now - Stopwatch.Frequency * 5;
            while (_processedTicks.Count > 0 && _processedTicks.Peek() < cutoff) _processedTicks.Dequeue();

            return new
            {
                processingRatePerSecond = ProcessingRatePerSecond,
                throughputPerSecond = Math.Round(_processedTicks.Count / 5.0, 1),
                standard = _standardStats.Snapshot(Queue.StandardCount),
                priority = _priorityStats.Snapshot(Queue.PriorityCount),
                priorityBypasses = _priorityStats.BypassCount,
            };
        }
    }

    public List<ProcessedPacketView> Recent(int max)
    {
        lock (_statsLock) return _recent.Reverse().Take(max).ToList();
    }

    // Called by the processor when a priority packet is served while normal
    // packets are still waiting - i.e. it genuinely jumped the queue.
    public void NoteBypass()
    {
        lock (_statsLock) _priorityStats.BypassCount++;
    }

    private sealed class LaneCounter(string lane)
    {
        private long _enqueued;
        private long _processed;
        private double _totalLatency;
        private double _maxLatency;
        public long BypassCount;

        public void Enqueued() => Interlocked.Increment(ref _enqueued);

        public void Processed(double latencyMs)
        {
            _processed++;
            _totalLatency += latencyMs;
            if (latencyMs > _maxLatency) _maxLatency = latencyMs;
        }

        public LaneStats Snapshot(int depth) => new(
            lane,
            depth,
            Interlocked.Read(ref _enqueued),
            _processed,
            _processed == 0 ? 0 : Math.Round(_totalLatency / _processed, 1),
            Math.Round(_maxLatency, 1));
    }
}
