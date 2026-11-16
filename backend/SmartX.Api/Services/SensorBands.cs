using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Operating bands per metric. Outside [Min, Max] is a Warning; past the
// critical limits it is Critical and the packet is routed to the priority lane.
public static class SensorBands
{
    public sealed record Band(double Min, double Max, double CriticalLow, double CriticalHigh, string Unit);

    private static readonly Dictionary<string, Band> Bands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["moisture"] = new Band(20, 80, 15, 95, "%"),
        ["wattage"] = new Band(0, 2200, double.NegativeInfinity, 2600, "W"),
        ["temperature"] = new Band(-10, 45, -20, 50, "°C"),
    };

    public static bool TryGet(string metric, out Band band) => Bands.TryGetValue(metric, out band!);

    public static PacketSeverity Classify(string metric, double value)
    {
        if (!Bands.TryGetValue(metric, out var band)) return PacketSeverity.Normal;
        if (value <= band.CriticalLow || value >= band.CriticalHigh) return PacketSeverity.Critical;
        if (value < band.Min || value > band.Max) return PacketSeverity.Warning;
        return PacketSeverity.Normal;
    }

    // "low" or "high" - used to build alert keys like alert|moisture-low|MAC.
    public static string Direction(string metric, double value)
    {
        if (!Bands.TryGetValue(metric, out var band)) return "out";
        return value < band.Min ? "low" : "high";
    }

    public static string UnitFor(string metric) => Bands.TryGetValue(metric, out var b) ? b.Unit : "";
}
