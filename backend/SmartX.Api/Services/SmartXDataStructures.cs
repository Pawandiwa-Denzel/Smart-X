using SmartX.Api.Models;
using SmartX.Api.Services.Topology;

namespace SmartX.Api.Services;

// ===========================================================================
//  SmartXDataStructures - every Part 2 data structure in ONE place.
//
//  Each collection is named after the job it does in the brief, so it is easy
//  to find. The services (GatewayPipeline, DeviceStateStore, AlertTracker,
//  CommandService, ActionEngine) don't create their own collections - they are
//  handed these instances and contain the logic that uses them.
//
//  Registered as a singleton, so there is exactly one of each for the whole API.
//  Thread-safety is handled by the service that owns each structure (each
//  locks around its reads/writes).
// ===========================================================================
public sealed class SmartXDataStructures
{
    // ------------------------------------------------ Stacks & Queues (15)

    /// <summary>
    /// FIFO queue of normal (in-band) telemetry packets. Processed strictly in
    /// arrival order by the TelemetryProcessor background worker.
    /// Used by: DualLaneQueue / GatewayPipeline.
    /// </summary>
    public Queue<QueuedPacket> TelemetryQueue { get; } = new();

    /// <summary>
    /// Priority queue for critical alerts (severe power spike, critical
    /// moisture drop...). Priority = (severity, arrival sequence), so Critical
    /// beats Warning and equal severities stay FIFO. Always drained BEFORE
    /// TelemetryQueue, so these packets bypass the normal backlog.
    /// Used by: DualLaneQueue / GatewayPipeline.
    /// </summary>
    public PriorityQueue<QueuedPacket, (int Severity, long Sequence)> CriticalAlertQueue { get; } = new();

    /// <summary>
    /// Manual override commands (pump on, relay toggle, shut all valves...).
    /// Undo pops the most recent command (LIFO) and restores the previous
    /// state of every device it changed.
    /// Used by: CommandService.
    /// </summary>
    public Stack<CommandRecord> CommandUndoStack { get; } = new();

    /// <summary>Commands that were undone, so they can be redone. Cleared on any new command.</summary>
    public Stack<CommandRecord> CommandRedoStack { get; } = new();

    // ------------------------------------ Dictionaries & Sorted Dictionaries (15)

    /// <summary>
    /// Every registered device keyed by MAC address (case-insensitive).
    /// A telemetry packet finds its device with one O(1) lookup - no looping
    /// through lists.
    /// Used by: DeviceStateStore.
    /// </summary>
    public Dictionary<string, DeviceLiveState> DeviceRegistry { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Secondary index: category -> MACs in that category. The category filter
    /// on the dashboard reads one bucket instead of scanning every device.
    /// Used by: DeviceStateStore.
    /// </summary>
    public Dictionary<SensorCategory, HashSet<string>> CategoryIndex { get; } = Enum.GetValues<SensorCategory>()
        .ToDictionary(c => c, _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Historical sensor logs: MAC -> (timestamp -> value). The inner
    /// SortedDictionary keeps readings ordered by timestamp, so timeline graphs
    /// always come out in the right order and range queries can stop early.
    /// Used by: DeviceStateStore (history / timeline endpoints).
    /// </summary>
    public Dictionary<string, SortedDictionary<DateTime, double>> SensorTimeline { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// When the gateway last heard from each device (stamped on arrival).
    /// Used by: HeartbeatMonitor via DeviceStateStore.
    /// </summary>
    public Dictionary<string, DateTime> LastHeartbeat { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------- Sets (10)

    /// <summary>
    /// Error states that are currently open, e.g. "alert|moisture-low|AA:BB:CC:00:02".
    /// HashSet.Add returns false for a duplicate, so the same alert is only
    /// ever raised once while it stays open.
    /// Used by: AlertTracker.
    /// </summary>
    public HashSet<string> ActiveErrorStates { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MACs of nodes that have stopped reporting. Stops the heartbeat monitor
    /// from raising the same "node disconnected" alert every time it checks.
    /// Used by: AlertTracker / HeartbeatMonitor.
    /// </summary>
    public HashSet<string> DisconnectedNodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------ Predictive Action Engine (30)

    /// <summary>
    /// Learned behaviour: trigger -> (what the operator did next -> count).
    /// e.g. ActionPatterns["alert|moisture-low|..02"]["cmd|TurnOn|..05"] = 5.
    /// Used by: ActionEngine.
    /// </summary>
    public Dictionary<string, Dictionary<string, int>> ActionPatterns { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How many times each trigger has happened (denominator for confidence).</summary>
    public Dictionary<string, int> TriggerOccurrences { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Sliding 3-minute window of recent alerts, searches and commands (FIFO -
    /// old events fall off the front). Everything still in here when the
    /// operator acts is counted as a possible cause of that action.
    /// Used by: ActionEngine.
    /// </summary>
    public Queue<ActivityEvent> RecentActivityWindow { get; } = new();

    /// <summary>Full search history counts (what operators look for most).</summary>
    public Dictionary<string, int> SearchHistory { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Alert occurrences the Auto-Engine has already acted on - so it acts once, not every tick.</summary>
    public HashSet<string> AutoExecutedActions { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------- Final PoE: Trees, Heaps, Graphs (50)

    /// <summary>
    /// Historical telemetry per sensor: MAC -> self-balancing AVL tree keyed by
    /// timestamp. Range queries ("moisture between 14:00 and 16:00 for Node A")
    /// cost O(log n + k) instead of scanning the whole history.
    /// Used by: TelemetryHistoryIndex.
    /// </summary>
    public Dictionary<string, AvlTelemetryTree> TelemetryHistory { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The physical IoT mesh: vertices are radios (gateway, routers, ESP32s),
    /// weighted edges are wireless links (weight = transmit energy per packet).
    /// Stored as an adjacency list. BFS and Prim's MST (with a MinHeap) run on it.
    /// Used by: TopologyService / MeshRouting.
    /// </summary>
    public MeshGraph MeshTopology { get; } = new();

    /// <summary>Radios an operator has taken down from the topology page (self-healing demo).</summary>
    public HashSet<string> FailedMeshNodes { get; } = new(StringComparer.OrdinalIgnoreCase);

    // ----------------------------------------------------------- inspection

    // Live sizes for the "Data structures in play" card and GET /api/structures.
    public object Describe() => new
    {
        telemetryQueue = new { type = "Queue<QueuedPacket>", count = TelemetryQueue.Count, purpose = "FIFO processing of normal telemetry" },
        criticalAlertQueue = new { type = "PriorityQueue<QueuedPacket,(severity,seq)>", count = CriticalAlertQueue.Count, purpose = "Critical alerts bypass the normal queue" },
        commandUndoStack = new { type = "Stack<CommandRecord>", count = CommandUndoStack.Count, purpose = "Undo last manual override" },
        commandRedoStack = new { type = "Stack<CommandRecord>", count = CommandRedoStack.Count, purpose = "Redo an undone override" },
        deviceRegistry = new { type = "Dictionary<MAC, DeviceLiveState>", count = DeviceRegistry.Count, purpose = "O(1) device lookup per packet" },
        categoryIndex = new { type = "Dictionary<SensorCategory, HashSet<MAC>>", count = CategoryIndex.ToDictionary(k => k.Key.ToString(), v => v.Value.Count), purpose = "Fast category filter" },
        sensorTimeline = new { type = "Dictionary<MAC, SortedDictionary<DateTime,double>>", count = SensorTimeline.Sum(t => t.Value.Count), purpose = "Ordered history for timeline graphs" },
        activeErrorStates = new { type = "HashSet<string>", count = ActiveErrorStates.Count, purpose = "Open alerts - no duplicates" },
        disconnectedNodes = new { type = "HashSet<string>", count = DisconnectedNodes.Count, purpose = "Offline nodes - no duplicate disconnect alerts" },
        actionPatterns = new { type = "Dictionary<trigger, Dictionary<next,int>>", count = ActionPatterns.Sum(a => a.Value.Count), purpose = "Learned operator behaviour" },
        telemetryHistory = new { type = "Dictionary<MAC, AvlTelemetryTree>", count = TelemetryHistory.Values.Sum(t => t.Count), purpose = "AVL trees for timestamp range queries" },
        meshTopology = new { type = "MeshGraph (adjacency list)", count = MeshTopology.VertexCount, purpose = "Mesh radios (vertices) and wireless links (edges)" },
        failedMeshNodes = new { type = "HashSet<string>", count = FailedMeshNodes.Count, purpose = "Radios taken down - left out of BFS and the MST" },
        recentActivityWindow = new { type = "Queue<ActivityEvent>", count = RecentActivityWindow.Count, purpose = "Last 3 minutes of context" },
    };
}
