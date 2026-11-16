namespace SmartX.Api.Models;


public sealed class TelemetryPacket<T> where T : struct, IComparable<T>
{
    public required string DeviceId { get; init; }
    public required string Metric { get; init; }
    public required T Value { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    public int CompareValueTo(T other) => Value.CompareTo(other);

    public override string ToString() => $"{DeviceId}/{Metric} = {Value} @ {Timestamp:HH:mm:ss}";
}
