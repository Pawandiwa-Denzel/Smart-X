using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Predictive "Automated Action Engine".
//
// 1. PATTERN ANALYSIS - every alert, search and manual command is recorded as an
//    ActivityEvent. A sliding window (Queue<ActivityEvent>, last 3 minutes)
//    holds recent context. When the operator does something (search / command)
//    we count it as "following" everything still in the window:
//
//        ActionPatterns[antecedent][consequent]++      e.g.
//        ActionPatterns["alert|moisture-low|..02"]["cmd|TurnOn|..05"] = 5
//
//    That's a first-order transition table (a simple Markov / association-rule
//    model). Confidence = count(A -> B) / occurrences(A).
//
// 2. ALGORITHMIC SUGGESTION - for every trigger that is live right now (open
//    alerts from AlertTracker + the operator's last few actions) we look up what
//    usually follows and rank candidates in a PriorityQueue by
//    confidence x support weight. Top-k become "Suggested Actions".
//
// 3. PREDICTION - a least-squares trend line over each sensor's sorted history
//    estimates WHEN it will leave its band, so a node is flagged before it
//    actually breaches (and before the operator searches for it). If we already
//    know what the operator normally does after that breach, the insight says so.
public class ActionEngine(SmartXDataStructures ds, DeviceStateStore states, AlertTracker alerts)
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(3);

    private readonly object _lock = new();
    // Structures from SmartXDataStructures (RecentActivityWindow, ActionPatterns,
    // TriggerOccurrences, SearchHistory, AutoExecutedActions).
    private Queue<ActivityEvent> _window => ds.RecentActivityWindow;
    private Dictionary<string, Dictionary<string, int>> _follows => ds.ActionPatterns;
    private Dictionary<string, int> _occurrences => ds.TriggerOccurrences;
    private Dictionary<string, int> _searchCounts => ds.SearchHistory;
    private HashSet<string> _autoExecuted => ds.AutoExecutedActions;

    private readonly LinkedList<ActivityEvent> _log = new();
    private readonly Dictionary<string, DateTime> _lastSeenKey = new(StringComparer.OrdinalIgnoreCase);

    private int _seededSessions;
    private int _liveObservations;

    public bool AutoMode { get; set; }

    // ------------------------------------------------------------------ input

    public void Record(ActivityEvent e)
    {
        lock (_lock)
        {
            Prune(e.At);

            // Ignore the same search twice in 20s (typing / refreshing the filter
            // shouldn't count as two separate decisions).
            if (e.Kind == ActivityKind.Search &&
                _lastSeenKey.TryGetValue(e.Key, out var last) && e.At - last < TimeSpan.FromSeconds(20))
            {
                return;
            }

            _lastSeenKey[e.Key] = e.At;

            if (e.Kind is ActivityKind.Search or ActivityKind.Command)
            {
                var counted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var prior in _window)
                {
                    if (string.Equals(prior.Key, e.Key, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!counted.Add(prior.Key)) continue; // count each antecedent once
                    Increment(prior.Key, e.Key);
                    _liveObservations++;
                }

                if (e.Kind == ActivityKind.Search)
                    _searchCounts[e.Key] = _searchCounts.GetValueOrDefault(e.Key) + 1;
            }

            _occurrences[e.Key] = _occurrences.GetValueOrDefault(e.Key) + 1;
            _window.Enqueue(e);
            _log.AddFirst(e);
            while (_log.Count > 60) _log.RemoveLast();
        }
    }

    public void RecordSearch(string? text, SensorCategory? category, AlertLevel? alert)
    {
        var now = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(text) && text.Trim().Length >= 2)
        {
            var t = text.Trim();
            var mac = states.ResolveSingle(t);
            var key = mac is null ? $"search|{t.ToLowerInvariant()}" : $"search|{mac}";
            Record(new ActivityEvent(ActivityKind.Search, key, LabelFor(key), now));
        }

        if (category is { } c)
        {
            var key = $"filter|category|{c}";
            Record(new ActivityEvent(ActivityKind.Search, key, LabelFor(key), now));
        }

        if (alert is { } a)
        {
            var key = $"filter|alert|{a}";
            Record(new ActivityEvent(ActivityKind.Search, key, LabelFor(key), now));
        }
    }

    // Historical operator sessions so the engine has something to work with on
    // a fresh start (the brief asks for heavy mock seeding). Each array is one
    // session in the order things happened.
    public void SeedHistoricalSessions()
    {
        const string sensorA = "AA:BB:CC:00:01", sensorB = "AA:BB:CC:00:02", meter = "AA:BB:CC:00:03",
            greenhouseValve = "AA:BB:CC:00:04", pumpA = "AA:BB:CC:00:05", relay = "AA:BB:CC:00:06", tempC = "AA:BB:CC:00:07";

        var sessions = new List<(int Times, string[] Keys)>
        {
            (5, [$"alert|moisture-low|{sensorB}", $"search|{pumpA}", $"cmd|TurnOn|{pumpA}"]),
            (3, [$"alert|moisture-high|{sensorB}", $"cmd|TurnOff|{pumpA}"]),
            (4, [$"alert|wattage-high|{meter}", $"search|{relay}", $"cmd|TurnOff|{relay}"]),
            (3, [$"alert|temperature-high|{tempC}", $"cmd|TurnOn|{greenhouseValve}"]),
            (3, [$"offline|{sensorA}", $"search|{sensorA}"]),
            (1, [$"alert|moisture-low|{sensorB}", $"search|{sensorB}"]),
        };

        lock (_lock)
        {
            foreach (var (times, keys) in sessions)
            {
                for (var n = 0; n < times; n++)
                {
                    for (var i = 0; i < keys.Length; i++)
                    {
                        _occurrences[keys[i]] = _occurrences.GetValueOrDefault(keys[i]) + 1;
                        if (keys[i].StartsWith("search|"))
                            _searchCounts[keys[i]] = _searchCounts.GetValueOrDefault(keys[i]) + 1;
                        for (var j = 0; j < i; j++) Increment(keys[j], keys[i]);
                    }

                    _seededSessions++;
                }
            }
        }
    }

    // ----------------------------------------------------------------- output

    public List<Suggestion> Suggestions(int max = 5)
    {
        var liveTriggers = alerts.ActiveKeys();

        lock (_lock)
        {
            Prune(DateTime.UtcNow);

            // Live alerts count fully; the operator's own recent actions count a
            // bit less (they're context, not a problem that needs fixing).
            var contexts = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in liveTriggers) contexts[k] = true;
            foreach (var e in _window.Reverse().Where(e => e.Kind is ActivityKind.Search or ActivityKind.Command).Take(3))
                contexts.TryAdd(e.Key, false);

            // Max-heap via negated score.
            var heap = new PriorityQueue<Suggestion, double>();

            foreach (var (trigger, isLive) in contexts)
            {
                if (!_follows.TryGetValue(trigger, out var next)) continue;
                var occ = Math.Max(1, _occurrences.GetValueOrDefault(trigger));

                foreach (var (consequent, count) in next)
                {
                    if (count < 2) continue;
                    var confidence = Math.Min(1.0, (double)count / occ);
                    if (confidence < 0.3) continue;
                    if (AlreadySatisfied(consequent)) continue;
                    if (contexts.ContainsKey(consequent)) continue; // the user just did it

                    var score = confidence * (1 - 1.0 / (1 + count)) * (isLive ? 1.0 : 0.6);
                    heap.Enqueue(Build(trigger, consequent, count, occ, confidence, isLive), -score);
                }
            }

            var result = new List<Suggestion>();
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (result.Count < max && heap.TryDequeue(out var s, out _))
            {
                var consequentKey = s.Id[(s.Id.IndexOf("=>", StringComparison.Ordinal) + 2)..];
                if (unique.Add(consequentKey)) result.Add(s);
            }

            return result;
        }
    }

    public List<(Suggestion Suggestion, string OccurrenceKey)> AutoExecutable()
    {
        var list = new List<(Suggestion, string)>();
        foreach (var s in Suggestions(8))
        {
            if (s.ActionKind != "command" || s.Command is null || !s.TriggerIsLiveAlert) continue;
            if (s.Confidence < 0.7 || s.Support < 3) continue;

            var occKey = $"{s.TriggerKey}#{alerts.OccurrenceOf(s.TriggerKey)}=>{s.Id}";
            lock (_lock)
            {
                if (_autoExecuted.Contains(occKey)) continue;
            }

            list.Add((s, occKey));
        }

        return list;
    }

    public void MarkAutoExecuted(string occurrenceKey)
    {
        lock (_lock) _autoExecuted.Add(occurrenceKey);
    }

    public List<Insight> Insights(int max = 8)
    {
        var heap = new PriorityQueue<Insight, double>();
        var now = DateTime.UtcNow;

        // --- trend prediction (linear regression over the SortedDictionary history)
        foreach (var (mac, name, metric, points) in states.RecentSeries(12))
        {
            if (metric is null || !SensorBands.TryGet(metric, out var band)) continue;
            if (alerts.IsDisconnected(mac)) continue;

            // Need a real time span to call it a trend - a burst of packets a
            // few microseconds apart would otherwise produce a silly slope.
            if ((points[^1].Timestamp - points[0].Timestamp).TotalSeconds < 15) continue;

            var (slopePerSec, r2) = Regress(points);
            var latest = points[^1].Value;
            if (latest < band.Min || latest > band.Max) continue; // already alerting
            if (r2 < 0.55 || Math.Abs(slopePerSec) < 1e-6) continue; // noise, not a trend

            var falling = slopePerSec < 0;

            // The last few readings must agree with the fitted direction,
            // otherwise the line is still describing a trend that just reversed.
            var recentDelta = points[^1].Value - points[^4].Value;
            if (Math.Sign(recentDelta) != Math.Sign(slopePerSec)) continue;
            var limit = falling ? band.Min : band.Max;
            var eta = (limit - latest) / slopePerSec; // seconds
            if (eta <= 0 || eta > 600) continue;

            var unit = SensorBands.UnitFor(metric);
            var direction = falling ? "low" : "high";
            var futureKey = AlertTracker.ErrorKey(metric, direction, mac);

            var detail = $"{name} is {(falling ? "falling" : "rising")} {Math.Abs(slopePerSec * 60):F1}{unit}/min " +
                         $"(fit R² {r2:F2}). Predicted to cross {limit}{unit} in ~{FormatEta(eta)}.";

            var follow = BestFollowUp(futureKey);
            if (follow is not null) detail += $" Last time this happened you usually: {follow}.";

            heap.Enqueue(new Insight(
                $"trend|{mac}", "trend", eta < 120 ? "critical" : "warning",
                $"{name}: {metric} {direction} predicted",
                detail, mac, 200 - eta / 6), -(200 - eta / 6));
        }

        // --- offline nodes
        foreach (var key in alerts.ActiveKeys().Where(k => k.StartsWith("offline|")))
        {
            var mac = key[8..];
            var name = states.NameOf(mac);
            var follow = BestFollowUp(key);
            heap.Enqueue(new Insight(
                key, "offline", "critical", $"{name} is not reporting",
                "No heartbeat from this node. Check its power supply or the nearest mesh hop." +
                (follow is not null ? $" Usual next step: {follow}." : ""),
                mac, 180), -180);
        }

        // --- flapping devices (lots of alerts in a short window)
        foreach (var (mac, count) in alerts.RecentAlertCounts(TimeSpan.FromMinutes(10)))
        {
            if (count < 3) continue;
            var name = states.NameOf(mac);
            var score = 100 + count * 5;
            heap.Enqueue(new Insight(
                $"flap|{mac}", "flapping", count >= 6 ? "warning" : "info",
                $"{name} keeps tripping alerts",
                $"{count} alerts in the last 10 minutes - looks like an unstable reading. Worth checking calibration or wiring.",
                mac, score), -score);
        }

        // --- what the operator keeps looking for
        lock (_lock)
        {
            var top = _searchCounts.OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (top.Value >= 3)
            {
                var mac = top.Key.StartsWith("search|") && states.Exists(top.Key[7..]) ? top.Key[7..] : null;
                heap.Enqueue(new Insight(
                    "hotspot", "hotspot", "info",
                    $"Most searched: {LabelFor(top.Key).Replace("Searched ", "")}",
                    $"Looked up {top.Value} times - jump straight to it from here instead of searching again.",
                    mac, 20), -20);
            }
        }

        var result = new List<Insight>();
        while (result.Count < max && heap.TryDequeue(out var i, out _)) result.Add(i);
        return result;
    }

    public List<LearnedPattern> Patterns(int max = 15)
    {
        lock (_lock)
        {
            return _follows
                .SelectMany(a => a.Value.Select(c => (A: a.Key, C: c.Key, Count: c.Value)))
                .Where(x => x.Count >= 2)
                .Select(x =>
                {
                    var conf = Math.Min(1.0, (double)x.Count / Math.Max(1, _occurrences.GetValueOrDefault(x.A)));
                    return new LearnedPattern(x.A, LabelFor(x.A), x.C, LabelFor(x.C), x.Count, Math.Round(conf, 2));
                })
                .OrderByDescending(p => p.Confidence * p.Support)
                .Take(max)
                .ToList();
        }
    }

    public object Summary()
    {
        lock (_lock)
        {
            return new
            {
                autoMode = AutoMode,
                seededSessions = _seededSessions,
                liveObservations = _liveObservations,
                trackedTriggers = _follows.Count,
                windowSize = _window.Count,
                recentActivity = _log.Take(20).Select(e => new { kind = e.Kind.ToString(), e.Key, label = LabelFor(e.Key), e.At }),
            };
        }
    }

    // ---------------------------------------------------------------- helpers

    // Human readable label for any activity key.
    public string LabelFor(string key)
    {
        var parts = key.Split('|');
        return parts[0] switch
        {
            "alert" when parts.Length == 3 => $"{states.NameOf(parts[2])}: {parts[1].Replace('-', ' ')}",
            "offline" => $"{states.NameOf(parts[1])} offline",
            "search" when states.Exists(parts[1]) => $"Searched “{states.NameOf(parts[1])}”",
            "search" => $"Searched “{parts[1]}”",
            "filter" when parts.Length == 3 => $"Filtered {parts[1]} = {parts[2]}",
            "cmd" when parts.Length == 3 => parts[1] switch
            {
                "TurnOn" => $"Turn ON {states.NameOf(parts[2])}",
                "TurnOff" => $"Turn OFF {states.NameOf(parts[2])}",
                "Toggle" => $"Toggle {states.NameOf(parts[2])}",
                "ShutDownAllValves" => "Shut down all valves",
                "OpenAllValves" => "Open all valves",
                _ => key,
            },
            _ => key,
        };
    }

    private Suggestion Build(string trigger, string consequent, int count, int occ, double confidence, bool isLive)
    {
        var triggerLabel = LabelFor(trigger);
        var reason = isLive
            ? $"“{triggerLabel}” is active. Operators did this next {count} of {occ} times."
            : $"After “{triggerLabel}” you usually do this next ({count} of {occ} times).";

        var parts = consequent.Split('|');
        CommandRequest? command = null;
        string actionKind;
        string? focus = null;

        if (parts[0] == "cmd" && parts.Length == 3 && Enum.TryParse<CommandType>(parts[1], out var type))
        {
            actionKind = "command";
            command = new CommandRequest(type, parts[2] == "*" ? null : parts[2]);
        }
        else if (parts[0] == "search" && states.Exists(parts[1]))
        {
            actionKind = "focus";
            focus = parts[1];
        }
        else
        {
            actionKind = "search";
            focus = parts.Length > 1 ? parts[^1] : consequent;
        }

        return new Suggestion(
            $"{trigger}=>{consequent}",
            LabelFor(consequent).Replace("Searched ", "Open "),
            reason,
            Math.Round(confidence, 2),
            count,
            actionKind,
            command,
            focus,
            trigger,
            isLive);
    }

    // Don't suggest "Turn ON Pump A" if Pump A is already on.
    private bool AlreadySatisfied(string consequent)
    {
        var parts = consequent.Split('|');
        if (parts[0] != "cmd" || parts.Length != 3) return false;

        if (parts[1] is "ShutDownAllValves" or "OpenAllValves")
        {
            var wantOpen = parts[1] == "OpenAllValves";
            var valves = states.Valves();
            return valves.Count > 0 && valves.All(v => v.On == wantOpen);
        }

        if (!states.TryGetActuator(parts[2], out var on, out _)) return true; // unknown device: can't run it
        return parts[1] switch
        {
            "TurnOn" => on,
            "TurnOff" => !on,
            _ => false,
        };
    }

    private string? BestFollowUp(string trigger)
    {
        lock (_lock)
        {
            if (!_follows.TryGetValue(trigger, out var next) || next.Count == 0) return null;
            var best = next.Where(kv => kv.Key.StartsWith("cmd|")).OrderByDescending(kv => kv.Value).FirstOrDefault();
            if (best.Key is null) best = next.OrderByDescending(kv => kv.Value).First();
            return LabelFor(best.Key);
        }
    }

    private void Increment(string antecedent, string consequent)
    {
        if (!_follows.TryGetValue(antecedent, out var next))
        {
            next = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            _follows[antecedent] = next;
        }

        next[consequent] = next.GetValueOrDefault(consequent) + 1;
    }

    private void Prune(DateTime now)
    {
        while (_window.Count > 0 && now - _window.Peek().At > Window) _window.Dequeue();
    }

    // Ordinary least squares: value = a + b * t. Returns slope b (per second) and R².
    private static (double Slope, double R2) Regress(List<HistoryPoint> points)
    {
        var t0 = points[0].Timestamp;
        var n = points.Count;
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        foreach (var p in points)
        {
            var x = (p.Timestamp - t0).TotalSeconds;
            var y = p.Value;
            sx += x; sy += y; sxx += x * x; sxy += x * y; syy += y * y;
        }

        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-9) return (0, 0);

        var slope = (n * sxy - sx * sy) / denom;
        var rNum = n * sxy - sx * sy;
        var rDen = Math.Sqrt(denom * (n * syy - sy * sy));
        var r = rDen < 1e-9 ? 0 : rNum / rDen;
        return (slope, r * r);
    }

    private static string FormatEta(double seconds) =>
        seconds < 60 ? $"{seconds:F0}s" : $"{(int)(seconds / 60)}m {(int)(seconds % 60):D2}s";
}
