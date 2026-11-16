using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Live device cache for the command stream. All collections come from
// SmartXDataStructures - this class holds the logic and the lock:
//
//  - DeviceRegistry   Dictionary<MAC, DeviceLiveState>: O(1) lookup per packet.
//  - CategoryIndex    Dictionary<Category, HashSet<MAC>>: fast category filter.
//  - SensorTimeline   Dictionary<MAC, SortedDictionary<DateTime,double>>:
//                     history kept in timestamp order for timeline graphs.
//  - LastHeartbeat    when each device was last heard from.
public class DeviceStateStore(SmartXDataStructures ds)
{
    private const int MaxHistoryPerDevice = 600;

    private readonly object _lock = new();
    private Dictionary<string, DeviceLiveState> _devices => ds.DeviceRegistry;
    private Dictionary<SensorCategory, HashSet<string>> _byCategory => ds.CategoryIndex;

    // Stamped on ARRIVAL (before queueing), so a backed-up queue during a burst
    // doesn't make healthy nodes look offline.
    private Dictionary<string, DateTime> _lastHeard => ds.LastHeartbeat;

    // The SortedDictionary timeline for one device (created on first use).
    private SortedDictionary<DateTime, double> TimelineOf(string mac)
    {
        if (!ds.SensorTimeline.TryGetValue(mac, out var timeline))
        {
            timeline = new SortedDictionary<DateTime, double>();
            ds.SensorTimeline[mac] = timeline;
        }

        return timeline;
    }

    public void Upsert(SensorDevice device)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(device.MacAddress, out var state))
            {
                state = new DeviceLiveState { Mac = device.MacAddress };
                _devices[device.MacAddress] = state;
            }
            else
            {
                _byCategory[state.Category].Remove(device.MacAddress);
            }

            state.Name = string.IsNullOrWhiteSpace(device.Name) ? device.Location : device.Name;
            state.Location = device.Location;
            state.Category = device.Category;
            _byCategory[device.Category].Add(device.MacAddress);

            // Sensible default positions for actuators: valves open, pumps/relays off.
            if (device.Category == SensorCategory.Actuator && state.ActuatorOn is null)
            {
                state.ActuatorOn = state.Name.Contains("valve", StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public void Touch(string mac, DateTime at)
    {
        lock (_lock) _lastHeard[mac] = at;
    }

    // Called by the processor for every packet. Returns the state after the
    // update (or a freshly created "unregistered" entry if the MAC is unknown).
    public DeviceLiveState Apply(QueuedPacket packet, DateTime processedAt)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(packet.DeviceId, out var state))
            {
                // A real gateway still accepts packets from devices that were
                // never provisioned - we flag them rather than drop them.
                state = new DeviceLiveState
                {
                    Mac = packet.DeviceId,
                    Name = $"Unregistered {packet.DeviceId}",
                    Location = "Unregistered",
                    Category = SensorCategory.Environmental,
                };
                _devices[packet.DeviceId] = state;
                _byCategory[state.Category].Add(packet.DeviceId);
            }

            state.LastSeen = processedAt;
            state.PacketCount++;
            state.LastMetric = packet.Metric;

            if (packet.Kind == ValueKind.Bool)
            {
                state.LastBool = packet.BoolValue;
                return state;
            }

            state.LastValue = packet.NumericValue;

            // Timestamps can collide during a burst, so nudge forward one tick
            // until the key is unique rather than overwrite a reading.
            var timeline = TimelineOf(state.Mac);
            var key = packet.ReceivedAt;
            while (timeline.ContainsKey(key)) key = key.AddTicks(1);
            timeline[key] = packet.NumericValue;

            // Cap memory: the oldest key is always the first one in a SortedDictionary.
            while (timeline.Count > MaxHistoryPerDevice)
            {
                timeline.Remove(timeline.Keys.First());
            }

            return state;
        }
    }

    public void SetAlert(string mac, AlertLevel level, string? message)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(mac, out var state))
            {
                state.Alert = level;
                state.AlertMessage = message;
            }
        }
    }

    public bool TryGetActuator(string mac, out bool isOn, out string name)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(mac, out var s) && s.Category == SensorCategory.Actuator)
            {
                isOn = s.ActuatorOn ?? false;
                name = s.Name;
                return true;
            }

            isOn = false;
            name = mac;
            return false;
        }
    }

    public void SetActuator(string mac, bool on)
    {
        lock (_lock)
        {
            if (_devices.TryGetValue(mac, out var s)) s.ActuatorOn = on;
        }
    }

    // All valves = actuators with "valve" in the name; used by "shut down all valves".
    public List<(string Mac, bool On)> Valves()
    {
        lock (_lock)
        {
            return _byCategory[SensorCategory.Actuator]
                .Select(mac => _devices[mac])
                .Where(d => d.Name.Contains("valve", StringComparison.OrdinalIgnoreCase))
                .Select(d => (d.Mac, d.ActuatorOn ?? false))
                .ToList();
        }
    }

    public string NameOf(string mac)
    {
        lock (_lock) return _devices.TryGetValue(mac, out var s) ? s.Name : mac;
    }

    public bool Exists(string mac)
    {
        lock (_lock) return _devices.ContainsKey(mac);
    }

    public DeviceView? Get(string mac)
    {
        lock (_lock) return _devices.TryGetValue(mac, out var s) ? ToView(s, DateTime.UtcNow) : null;
    }

    // Search / filter. Category goes through the category index first so only
    // the matching bucket is scanned; text matches on name, location or MAC.
    public List<DeviceView> Query(string? text, SensorCategory? category, AlertLevel? alert)
    {
        lock (_lock)
        {
            IEnumerable<DeviceLiveState> source = category is { } c
                ? _byCategory[c].Select(mac => _devices[mac])
                : _devices.Values;

            if (alert is { } a) source = source.Where(d => d.Alert == a);

            if (!string.IsNullOrWhiteSpace(text))
            {
                var t = text.Trim();
                source = source.Where(d =>
                    d.Name.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    d.Location.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                    d.Mac.Contains(t, StringComparison.OrdinalIgnoreCase));
            }

            var now = DateTime.UtcNow;
            return source
                .OrderBy(d => d.Alert switch
                {
                    AlertLevel.Critical => 0,
                    AlertLevel.Offline => 1,
                    AlertLevel.Warning => 2,
                    _ => 3,
                })
                .ThenBy(d => d.Name)
                .Select(d => ToView(d, now))
                .ToList();
        }
    }

    // Resolve a free-text search to a single device if it only matches one -
    // the action engine uses this so "pump" and "Pump A" count as the same thing.
    public string? ResolveSingle(string text)
    {
        lock (_lock)
        {
            if (_devices.ContainsKey(text)) return _devices[text].Mac;
            var hits = _devices.Values
                .Where(d => d.Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                            d.Mac.Contains(text, StringComparison.OrdinalIgnoreCase))
                .Take(2)
                .ToList();
            return hits.Count == 1 ? hits[0].Mac : null;
        }
    }

    // Range query on the sorted history. Because keys are ordered we can skip
    // until 'from' and stop as soon as we pass 'to' instead of scanning it all.
    public List<HistoryPoint> History(string mac, DateTime? from, DateTime? to, int limit)
    {
        lock (_lock)
        {
            if (!_devices.TryGetValue(mac, out var s)) return [];

            IEnumerable<KeyValuePair<DateTime, double>> range = TimelineOf(s.Mac);
            if (from is { } f) range = range.SkipWhile(kv => kv.Key < f);
            if (to is { } t) range = range.TakeWhile(kv => kv.Key <= t);

            var points = range.Select(kv => new HistoryPoint(kv.Key, kv.Value)).ToList();
            return points.Count > limit ? points.GetRange(points.Count - limit, limit) : points;
        }
    }

    // Last N values for trend analysis (newest last).
    public List<(string Mac, string Name, string? Metric, List<HistoryPoint> Points)> RecentSeries(int n)
    {
        lock (_lock)
        {
            return _devices.Values
                .Where(d => TimelineOf(d.Mac).Count >= 6)
                .Select(d => (d.Mac, d.Name, d.LastMetric,
                    TimelineOf(d.Mac).Skip(Math.Max(0, TimelineOf(d.Mac).Count - n))
                        .Select(kv => new HistoryPoint(kv.Key, kv.Value)).ToList()))
                .ToList();
        }
    }

    public List<(string Mac, DateTime LastSeen)> LastSeenSnapshot()
    {
        lock (_lock)
        {
            return _lastHeard
                .Where(kv => _devices.ContainsKey(kv.Key))
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }
    }

    public int Count
    {
        get { lock (_lock) return _devices.Count; }
    }

    public Dictionary<string, int> CategoryCounts()
    {
        lock (_lock) return _byCategory.ToDictionary(kv => kv.Key.ToString(), kv => kv.Value.Count);
    }

    private DeviceView ToView(DeviceLiveState s, DateTime now)
    {
        var timeline = TimelineOf(s.Mac);
        var spark = timeline.Count == 0
            ? []
            : timeline.Values.Skip(Math.Max(0, timeline.Count - 30)).ToList();

        return new DeviceView(
            s.Mac, s.Name, s.Location, s.Category,
            s.LastMetric, s.LastValue, s.LastBool, s.ActuatorOn,
            s.LastSeen,
            s.LastSeen is { } seen ? Math.Round((now - seen).TotalSeconds, 1) : null,
            s.PacketCount, s.Alert, s.AlertMessage, spark);
    }
}
