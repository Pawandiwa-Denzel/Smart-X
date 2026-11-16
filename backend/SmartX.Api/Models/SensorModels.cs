namespace SmartX.Api.Models;

public enum SensorCategory
{
    Environmental,
    PowerConsumption,
    Actuator
}

public class SensorDevice
{
    public required string MacAddress { get; init; }
    // Friendly label shown on the dashboard (e.g. "Pump A"). Optional so the
    // Part 1 registration payload still works without it.
    public string? Name { get; init; }
    public required string Location { get; init; }
    public SensorCategory Category { get; init; }
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;
    public List<string> AttachmentFiles { get; init; } = [];
}


public class DeploymentNode
{
    public required string Name { get; init; }
    public string? DeviceMac { get; init; }
    public List<DeploymentNode> Children { get; init; } = [];
}

public record ValidationIssue(string Path, string Message);

public record TelemetryIngestRequest(string DeviceId, string Metric, string ValueType, string RawValue);

public record AnomalyEvent(string DeviceId, string Metric, string Value, string Message, DateTime Timestamp);
