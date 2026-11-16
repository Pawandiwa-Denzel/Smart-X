namespace SmartX.Api.Services;

// Custom two-lane queue used as the gateway's inbox.
//
//  - Standard lane: a plain Queue<T>. Normal telemetry is processed strictly
//    First-In-First-Out so readings from a device never arrive out of order.
//  - Priority lane: a PriorityQueue<T, (severity, sequence)>. Critical alerts
//    (power spike, moisture crash) go here and ALWAYS come out first, no
//    matter how many normal packets are waiting in the standard lane.
//
// PriorityQueue in .NET isn't stable on its own, so the priority is a tuple of
// (severity, sequence number). Two critical packets therefore still come out
// in the order they arrived.
public sealed class DualLaneQueue<T>
{
    private readonly Queue<T> _standard;
    private readonly PriorityQueue<T, (int Severity, long Sequence)> _priority;
    private readonly object _gate = new();
    private readonly int _maxStandardDepth;
    private long _sequence;

    // The two collections are passed in (they live in SmartXDataStructures as
    // TelemetryQueue and CriticalAlertQueue); this class adds the routing rules.
    public DualLaneQueue(Queue<T> standardLane, PriorityQueue<T, (int Severity, long Sequence)> priorityLane, int maxStandardDepth = 50_000)
    {
        _standard = standardLane;
        _priority = priorityLane;
        _maxStandardDepth = maxStandardDepth;
    }

    public int StandardCount { get { lock (_gate) return _standard.Count; } }
    public int PriorityCount { get { lock (_gate) return _priority.Count; } }

    // Returns false when the standard lane is full (back-pressure) so the API
    // can answer 503 instead of the gateway running out of memory.
    public bool EnqueueStandard(T item)
    {
        lock (_gate)
        {
            if (_standard.Count >= _maxStandardDepth) return false;
            _standard.Enqueue(item);
            return true;
        }
    }

    // The priority lane is never capped - dropping a critical alert is worse
    // than using a bit more memory.
    public void EnqueuePriority(T item, int severity)
    {
        lock (_gate)
        {
            _priority.Enqueue(item, (severity, _sequence++));
        }
    }

    // Priority lane is drained first; only when it is empty do we fall back to
    // the FIFO lane. This is what lets a critical packet "jump the queue".
    public bool TryDequeue(out T item, out bool fromPriorityLane)
    {
        lock (_gate)
        {
            if (_priority.TryDequeue(out item!, out _))
            {
                fromPriorityLane = true;
                return true;
            }

            if (_standard.TryDequeue(out item!))
            {
                fromPriorityLane = false;
                return true;
            }

            fromPriorityLane = false;
            return false;
        }
    }

    // Read-only peek used by the dashboard to draw the two lanes.
    public (List<T> Priority, List<T> Standard) Peek(int max)
    {
        lock (_gate)
        {
            var priority = _priority.UnorderedItems
                .OrderBy(x => x.Priority.Severity)
                .ThenBy(x => x.Priority.Sequence)
                .Take(max)
                .Select(x => x.Element)
                .ToList();

            // Queue<T> enumerates from head to tail, i.e. next-to-be-processed first.
            var standard = _standard.Take(max).ToList();
            return (priority, standard);
        }
    }
}
