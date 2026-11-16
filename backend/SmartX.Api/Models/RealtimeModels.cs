namespace SmartX.Api.Models;

// ---------------------------------------------------------------------------
// Part 2 models - everything the real-time command stream passes around.
// ---------------------------------------------------------------------------

// Lower number = more urgent. The priority lane orders on this value, so a
// Critical packet always comes out of the PriorityQueue before a Warning.
public enum PacketSeverity
{
    Critical = 0,
    Warning = 1,
    Normal = 2,
}

public enum ValueKind
{
    Float,
    Int,
    Bool,
}

public enum AlertLevel
{
    Ok,
    Warning,
    Critical,
    Offline,
}

// One packet waiting in the gateway queue. The value is parsed once at the
// door (float / int / bool) so the processor never has to re-parse strings.
public sealed record QueuedPacket(
    long Sequence,
    string DeviceId,
    string Metric,
    ValueKind Kind,
    double NumericValue,
    bool BoolValue,
    PacketSeverity Severity,
    DateTime ReceivedAt,
    long EnqueuedTimestamp)
{
    public string DisplayValue => Kind switch
    {
        ValueKind.Bool => BoolValue ? "true" : "false",
        ValueKind.Int => ((int)NumericValue).ToString(),
        _ => NumericValue.ToString("F1"),
    };
}

// Live, mutable state for one device - stored in a Dictionary keyed by MAC.
public sealed class DeviceLiveState
{
    public required string Mac { get; init; }
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public SensorCategory Category { get; set; }

    public string? LastMetric { get; set; }
    public double? LastValue { get; set; }
    public bool? LastBool { get; set; }
    public DateTime? LastSeen { get; set; }
    public long PacketCount { get; set; }

    // Only used for actuators (pumps, relays, valves). true = on / open.
    public bool? ActuatorOn { get; set; }

    public AlertLevel Alert { get; set; } = AlertLevel.Ok;
    public string? AlertMessage { get; set; }

    // History lives in SmartXDataStructures.SensorTimeline[Mac].
}

public record HistoryPoint(DateTime Timestamp, double Value);

public record DeviceView(
    string Mac,
    string Name,
    string Location,
    SensorCategory Category,
    string? Metric,
    double? Value,
    bool? BoolValue,
    bool? ActuatorOn,
    DateTime? LastSeen,
    double? SecondsSinceSeen,
    long PacketCount,
    AlertLevel Alert,
    string? AlertMessage,
    IReadOnlyList<double> Sparkline);

public record LaneStats(
    string Lane,
    int Depth,
    long Enqueued,
    long Processed,
    double AvgLatencyMs,
    double MaxLatencyMs);

public record ProcessedPacketView(
    long Sequence,
    string DeviceId,
    string DeviceName,
    string Metric,
    string Value,
    string Severity,
    string Lane,
    double LatencyMs,
    DateTime ProcessedAt);

// ---------------------------------------------------------------------------
// Manual override commands (Stack<T> based undo / redo)
// ---------------------------------------------------------------------------

public enum CommandType
{
    TurnOn,
    TurnOff,
    Toggle,
    ShutDownAllValves,
    OpenAllValves,
}

public record CommandRequest(CommandType Type, string? TargetMac, string? IssuedBy = null);

public sealed class CommandRecord
{
    public required int Id { get; init; }
    public required CommandType Type { get; init; }
    public string? TargetMac { get; init; }
    public required string Label { get; init; }
    public required string IssuedBy { get; init; }
    public DateTime IssuedAt { get; init; } = DateTime.UtcNow;

    // What every affected actuator looked like before / after. Undo just
    // writes PreviousStates back, so even a multi-device command like
    // "shut down all valves" is reverted in one step.
    public Dictionary<string, bool> PreviousStates { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, bool> NewStates { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public record CommandLogEntry(int CommandId, string Action, string Label, string IssuedBy, DateTime At);

public record CommandResult(bool Success, string Message, CommandRecord? Command);

// ---------------------------------------------------------------------------
// Predictive Action Engine
// ---------------------------------------------------------------------------

public enum ActivityKind
{
    Alert,
    Offline,
    Search,
    Command,
}

public record ActivityEvent(ActivityKind Kind, string Key, string Label, DateTime At, bool Seeded = false);

public record Suggestion(
    string Id,
    string Title,
    string Reason,
    double Confidence,
    int Support,
    string ActionKind,          // "command" or "focus"
    CommandRequest? Command,
    string? FocusMac,
    string TriggerKey,
    bool TriggerIsLiveAlert);

public record Insight(
    string Id,
    string Kind,                // "trend", "flapping", "offline", "hotspot"
    string Severity,            // "critical", "warning", "info"
    string Title,
    string Detail,
    string? DeviceMac,
    double Score);

public record LearnedPattern(
    string Antecedent,
    string AntecedentLabel,
    string Consequent,
    string ConsequentLabel,
    int Support,
    double Confidence);

public record AlertEventView(
    string Key,
    string DeviceId,
    string DeviceName,
    string Kind,                // "raised", "resolved", "offline", "reconnected"
    AlertLevel Level,
    string Message,
    DateTime At,
    int Repeat = 1);

public record LiveEvent(string Type, object Payload);
