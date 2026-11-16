using System.Diagnostics;
using SmartX.Api.Models;

namespace SmartX.Api.Services.Topology;

public sealed record HistoryQueryResult(
    string Mac,
    string Name,
    string Metric,
    DateTime From,
    DateTime To,
    int TotalInRange,
    bool Truncated,
    double? Min,
    double? Max,
    double? Average,
    TreeQueryStats Stats,
    IReadOnlyList<HistoryPoint> Points);

public sealed record HistoryBenchmark(
    string Mac,
    int TreeSize,
    int TreeHeight,
    int ResultCount,
    int AvlNodesVisited,
    double AvlMicroseconds,
    int ListItemsScanned,
    double ListMicroseconds,
    double Speedup,
    bool TreeIsValidAvl);

// Owns one AvlTelemetryTree per sensor (MAC -> tree, the Dictionary lives in
// SmartXDataStructures.TelemetryHistory). Every numeric packet the gateway
// processes is inserted here, and the Network Topology page queries it for the
// selected sensor's historical log.
public sealed class TelemetryHistoryIndex(SmartXDataStructures ds, DeviceStateStore states)
{
    private readonly object _lock = new();
    private Dictionary<string, AvlTelemetryTree> Trees => ds.TelemetryHistory;

    public void Add(string mac, string metric, DateTime timestamp, double value) =>
        TreeFor(mac, metric).Insert(timestamp, value);

    public bool Has(string mac)
    {
        lock (_lock) return Trees.ContainsKey(mac);
    }

    public HistoryQueryResult? Query(string mac, DateTime from, DateTime to, int limit)
    {
        AvlTelemetryTree? tree;
        lock (_lock) Trees.TryGetValue(mac, out tree);
        if (tree is null) return null;

        var points = tree.RangeQuery(from, to, limit, out var stats);
        var total = tree.CountInRange(from, to);

        double? min = null, max = null, avg = null;
        if (points.Count > 0)
        {
            min = points.Min(p => p.Value);
            max = points.Max(p => p.Value);
            avg = Math.Round(points.Average(p => p.Value), 2);
        }

        return new HistoryQueryResult(mac, states.NameOf(mac), tree.Metric, from, to, total,
            total > points.Count, min, max, avg, stats, points);
    }

    public object? Summary(string mac)
    {
        AvlTelemetryTree? tree;
        lock (_lock) Trees.TryGetValue(mac, out tree);
        if (tree is null) return null;
        var (first, last) = tree.Bounds();
        return new { mac, metric = tree.Metric, readings = tree.Count, height = tree.Height, rotations = tree.Rotations, first, last };
    }

    public List<object> Overview()
    {
        lock (_lock)
        {
            return Trees.Select(kv => (object)new
            {
                mac = kv.Key,
                name = states.NameOf(kv.Key),
                metric = kv.Value.Metric,
                readings = kv.Value.Count,
                height = kv.Value.Height,
            }).ToList();
        }
    }

    // Same window queried two ways: the AVL tree vs a linear scan of an
    // unorganised List<T> holding identical data.
    public HistoryBenchmark? Benchmark(string mac, DateTime from, DateTime to)
    {
        AvlTelemetryTree? tree;
        lock (_lock) Trees.TryGetValue(mac, out tree);
        if (tree is null) return null;

        // Shuffle a copy so the list really is "unorganised" (no early exit possible).
        var list = tree.ToList();
        var rng = new Random(42);
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }

        // Warm-up both paths once so JIT time isn't measured.
        tree.RangeQuery(from, to, int.MaxValue, out _);
        list.Where(p => p.Timestamp >= from && p.Timestamp <= to).OrderBy(p => p.Timestamp).ToList();

        var result = tree.RangeQuery(from, to, int.MaxValue, out var stats);

        var sw = Stopwatch.StartNew();
        var scanned = list.Where(p => p.Timestamp >= from && p.Timestamp <= to).OrderBy(p => p.Timestamp).ToList();
        sw.Stop();
        var listUs = Math.Round(sw.Elapsed.TotalMicroseconds, 1);

        return new HistoryBenchmark(mac, stats.TreeSize, stats.TreeHeight, result.Count, stats.NodesVisited,
            stats.ElapsedMicroseconds, list.Count, listUs,
            stats.ElapsedMicroseconds > 0 ? Math.Round(listUs / stats.ElapsedMicroseconds, 1) : 0,
            tree.IsValidAvl() && scanned.Count == result.Count);
    }

    // ----------------------------------------------------------------- seeding

    // The brief allows heavy mock seeding: load the last N hours of readings for
    // the simulated fleet so range queries such as "14:00-16:00" have real data
    // from the first run. Values follow simple day/night patterns.
    public int SeedHistory(IEnumerable<(string Mac, string Metric)> sensors, TimeSpan span, TimeSpan interval)
    {
        var end = DateTime.UtcNow;
        var start = end - span;
        var total = 0;
        var rng = new Random(7312);

        foreach (var (mac, metric) in sensors)
        {
            var tree = TreeFor(mac, metric);
            var phase = rng.NextDouble() * Math.PI;
            for (var t = start; t < end; t = t.Add(interval))
            {
                // Hour of day in South Africa (UTC+2) drives the daily curve.
                var hour = (t.Hour + 2) % 24 + t.Minute / 60.0;
                var day = Math.Sin((hour - 8) / 24.0 * 2 * Math.PI);
                var noise = rng.NextDouble() - 0.5;

                var value = metric switch
                {
                    "moisture" => 52 + 14 * Math.Sin(t.Ticks / (double)TimeSpan.FromHours(5).Ticks + phase) - 6 * Math.Max(0, day) + noise * 3,
                    "temperature" => 24 + 7 * day + noise * 1.2,
                    "wattage" => 900 + 550 * Math.Max(0, day) + noise * 180 + (rng.NextDouble() < 0.004 ? 1500 : 0),
                    _ => 50 + noise * 10,
                };

                tree.Insert(t, Math.Round(value, 1));
                total++;
            }
        }

        return total;
    }

    private AvlTelemetryTree TreeFor(string mac, string metric)
    {
        lock (_lock)
        {
            if (!Trees.TryGetValue(mac, out var tree))
            {
                tree = new AvlTelemetryTree(metric);
                Trees[mac] = tree;
            }

            return tree;
        }
    }
}
