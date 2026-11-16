using SmartX.Api.Models;

namespace SmartX.Api.Services;


// Holds live telemetry in memory. Each value type gets its own List&lt;T&gt; so
// nothing is ever boxed into object on the hot ingestion path, and raw batches
// that arrive as jagged arrays are copied into those typed lists once validated.

public class TelemetryStore
{
    private readonly object _lock = new();

    private readonly List<TelemetryPacket<float>> _floatReadings = [];
    private readonly List<TelemetryPacket<int>> _intReadings = [];
    private readonly List<TelemetryPacket<bool>> _boolReadings = [];
    private readonly List<AnomalyEvent> _anomalies = [];

    // Part 2 fix: device|metric pairs that already have an open anomaly. A
    // sensor stuck out of band used to add the same annotation on every
    // packet - now it is annotated once until it comes back into band.
    private readonly HashSet<string> _openAnomalies = new(StringComparer.OrdinalIgnoreCase);

    // Each row is one raw batch of samples pulled off a device before it is
    // parsed into typed packets - rows can be different lengths, hence jagged
    // rather than a rectangular 2D array.
    private double[][] _rawBatchHistory = [];

    private readonly Dictionary<string, (double Min, double Max)> _bands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["moisture"] = (20, 80),
        ["wattage"] = (0, 2200),
        ["temperature"] = (-10, 45),
    };

    // Three overloads of the same method name, Ingest - the compiler picks the
    // right one based on the type of `value`, so callers never need to know
    // there are three different code paths underneath (classic method
    // overloading, separate from the operator overloading on PowerReading).
    public AnomalyEvent? Ingest(string deviceId, string metric, float value)
    {
        lock (_lock)
        {
            _floatReadings.Add(new TelemetryPacket<float> { DeviceId = deviceId, Metric = metric, Value = value });
            return CheckBand(deviceId, metric, value);
        }
    }

    public AnomalyEvent? Ingest(string deviceId, string metric, int value)
    {
        lock (_lock)
        {
            _intReadings.Add(new TelemetryPacket<int> { DeviceId = deviceId, Metric = metric, Value = value });
            return CheckBand(deviceId, metric, value);
        }
    }

    public void Ingest(string deviceId, string metric, bool value)
    {
        lock (_lock)
        {
            _boolReadings.Add(new TelemetryPacket<bool> { DeviceId = deviceId, Metric = metric, Value = value });
        }
    }

    
    public void IngestRawBatches(string deviceId, string metric, double[][] batches)
    {
        lock (_lock)
        {
            _rawBatchHistory = [.. _rawBatchHistory, .. batches];
            foreach (var row in batches)
            {
                foreach (var sample in row)
                {
                    Ingest(deviceId, metric, (float)sample);
                }
            }
        }
    }

    private AnomalyEvent? CheckBand(string deviceId, string metric, double value)
    {
        if (!_bands.TryGetValue(metric, out var band)) return null;

        var key = $"{deviceId}|{metric}";
        if (value >= band.Min && value <= band.Max)
        {
            _openAnomalies.Remove(key);
            return null;
        }

        if (!_openAnomalies.Add(key)) return null; // already annotated - skip the duplicate

        var evt = new AnomalyEvent(
            deviceId,
            metric,
            value.ToString("F2"),
            $"{metric} reading {value:F2} is outside the expected {band.Min}-{band.Max} range",
            DateTime.UtcNow);

        _anomalies.Insert(0, evt);
        if (_anomalies.Count > 50) _anomalies.RemoveAt(_anomalies.Count - 1);
        return evt;
    }

    public IReadOnlyList<AnomalyEvent> RecentAnomalies()
    {
        lock (_lock) return _anomalies.ToList();
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                floatCount = _floatReadings.Count,
                intCount = _intReadings.Count,
                boolCount = _boolReadings.Count,
                rawBatchRows = _rawBatchHistory.Length,
                recentFloats = _floatReadings.TakeLast(20).Select(p => new { p.DeviceId, p.Metric, p.Value, p.Timestamp }),
                recentInts = _intReadings.TakeLast(20).Select(p => new { p.DeviceId, p.Metric, p.Value, p.Timestamp }),
                recentBools = _boolReadings.TakeLast(20).Select(p => new { p.DeviceId, p.Metric, p.Value, p.Timestamp }),
            };
        }
    }
}
