using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Tracks which error states are currently active.
//
// Two HashSet<string>s from SmartXDataStructures do the heavy lifting:
//   ActiveErrorStates - keys like "alert|moisture-low|AA:BB:CC:00:02"
//   DisconnectedNodes - MAC addresses of nodes that have stopped reporting
//
// HashSet.Add returns false if the item is already there. That one bool is
// what stops the gateway from raising the same "Node B disconnected" alert
// every two seconds while the node stays down - the duplicate is counted and
// dropped instead of being pushed to the dashboard / action engine again.
public class AlertTracker(SmartXDataStructures ds)
{
    private readonly object _lock = new();
    private HashSet<string> _activeErrors => ds.ActiveErrorStates;
    private HashSet<string> _disconnected => ds.DisconnectedNodes;

    // Recent alert timestamps per device, used to spot "flapping" sensors.
    private readonly Dictionary<string, Queue<DateTime>> _recentByDevice = new(StringComparer.OrdinalIgnoreCase);

    // Feed of raised/resolved events for the dashboard (newest first, capped).
    private readonly LinkedList<AlertEventView> _feed = new();

    // Increments every time a key is (re)raised, so the auto-engine can tell
    // "the same alert again later" apart from "the alert that's still open".
    private readonly Dictionary<string, int> _occurrence = new(StringComparer.OrdinalIgnoreCase);

    public long DuplicatesSuppressed { get; private set; }
    public long AlertsRaised { get; private set; }

    public static string ErrorKey(string metric, string direction, string mac) => $"alert|{metric}-{direction}|{mac}";

    // Returns true only if this is a NEW error state.
    public bool Raise(string key, string mac, string name, AlertLevel level, string message, DateTime at)
    {
        lock (_lock)
        {
            if (!_activeErrors.Add(key))
            {
                DuplicatesSuppressed++;
                return false;
            }

            AlertsRaised++;
            _occurrence[key] = _occurrence.GetValueOrDefault(key) + 1;
            TrackForFlapping(mac, at);
            Push(new AlertEventView(key, mac, name, "raised", level, message, at));
            return true;
        }
    }

    // Clears every active error for this device whose key isn't 'stillActive'.
    // Returns the keys that were resolved.
    public List<string> ResolveDevice(string mac, string name, string? stillActive, DateTime at)
    {
        lock (_lock)
        {
            var resolved = _activeErrors
                .Where(k => k.StartsWith("alert|") && k.EndsWith("|" + mac, StringComparison.OrdinalIgnoreCase))
                .Where(k => !string.Equals(k, stillActive, StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in resolved)
            {
                _activeErrors.Remove(key);
                Push(new AlertEventView(key, mac, name, "resolved", AlertLevel.Ok, "Back within operating band", at));
            }

            return resolved;
        }
    }

    public bool MarkDisconnected(string mac, string name, double secondsSilent, DateTime at)
    {
        lock (_lock)
        {
            if (!_disconnected.Add(mac))
            {
                DuplicatesSuppressed++;
                return false;
            }

            var key = $"offline|{mac}";
            AlertsRaised++;
            _occurrence[key] = _occurrence.GetValueOrDefault(key) + 1;
            TrackForFlapping(mac, at);
            Push(new AlertEventView(key, mac, name, "offline", AlertLevel.Offline,
                $"Node disconnected - last packet {secondsSilent:F0}s ago", at));
            return true;
        }
    }

    public bool MarkReconnected(string mac, string name, DateTime at)
    {
        lock (_lock)
        {
            if (!_disconnected.Remove(mac)) return false;
            Push(new AlertEventView($"offline|{mac}", mac, name, "reconnected", AlertLevel.Ok, "Node is reporting again", at));
            return true;
        }
    }

    public bool IsDisconnected(string mac)
    {
        lock (_lock) return _disconnected.Contains(mac);
    }

    public bool IsActive(string key)
    {
        lock (_lock) return _activeErrors.Contains(key) ||
                            (key.StartsWith("offline|") && _disconnected.Contains(key[8..]));
    }

    public int OccurrenceOf(string key)
    {
        lock (_lock) return _occurrence.GetValueOrDefault(key);
    }

    // Every currently-open trigger: error keys plus offline|MAC keys.
    public List<string> ActiveKeys()
    {
        lock (_lock)
        {
            return _activeErrors.Concat(_disconnected.Select(m => $"offline|{m}")).ToList();
        }
    }

    public object Snapshot()
    {
        lock (_lock)
        {
            return new
            {
                activeErrors = _activeErrors.OrderBy(k => k).ToList(),
                disconnected = _disconnected.OrderBy(m => m).ToList(),
                duplicatesSuppressed = DuplicatesSuppressed,
                alertsRaised = AlertsRaised,
                feed = _feed.Take(40).ToList(),
            };
        }
    }

    public int ActiveErrorCount { get { lock (_lock) return _activeErrors.Count; } }
    public int DisconnectedCount { get { lock (_lock) return _disconnected.Count; } }

    public Dictionary<string, int> RecentAlertCounts(TimeSpan window)
    {
        lock (_lock)
        {
            var cutoff = DateTime.UtcNow - window;
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (mac, times) in _recentByDevice)
            {
                while (times.Count > 0 && times.Peek() < cutoff) times.Dequeue();
                if (times.Count > 0) result[mac] = times.Count;
            }

            return result;
        }
    }

    private void TrackForFlapping(string mac, DateTime at)
    {
        if (!_recentByDevice.TryGetValue(mac, out var q))
        {
            q = new Queue<DateTime>();
            _recentByDevice[mac] = q;
        }

        q.Enqueue(at);
        while (q.Count > 50) q.Dequeue();
    }

    private void Push(AlertEventView e)
    {
        // If the same key flips the same way again within 2 minutes (a sensor
        // hovering on the band edge), bump a repeat counter on the existing
        // entry instead of adding a duplicate line to the dashboard.
        var match = _feed.Take(6).FirstOrDefault(x =>
            x.Key == e.Key && x.Kind == e.Kind && e.At - x.At < TimeSpan.FromMinutes(2));
        if (match is not null)
        {
            _feed.Remove(match);
            _feed.AddFirst(e with { Repeat = match.Repeat + 1 });
            return;
        }

        _feed.AddFirst(e);
        while (_feed.Count > 100) _feed.RemoveLast();
    }
}
