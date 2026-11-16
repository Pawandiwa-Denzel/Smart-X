namespace SmartX.Api.Models;




public readonly struct PowerReading
{
    public string MeterId { get; init; }
    public double Watts { get; init; }
    public DateTime Timestamp { get; init; }

    public PowerReading(string meterId, double watts, DateTime? timestamp = null)
    {
        MeterId = meterId;
        Watts = watts;
        Timestamp = timestamp ?? DateTime.UtcNow;
    }

    // e.g. combining two smart meters onto one feed.
    public static PowerReading operator +(PowerReading a, PowerReading b) =>
        new($"{a.MeterId}+{b.MeterId}", a.Watts + b.Watts);

    // Delta between two readings, useful for spotting a sudden drop or spike.
    public static PowerReading operator -(PowerReading a, PowerReading b) =>
        new($"{a.MeterId}-{b.MeterId}", a.Watts - b.Watts);

    public static bool operator >(PowerReading a, PowerReading b) => a.Watts > b.Watts;
    public static bool operator <(PowerReading a, PowerReading b) => a.Watts < b.Watts;
    public static bool operator >=(PowerReading a, PowerReading b) => a.Watts >= b.Watts;
    public static bool operator <=(PowerReading a, PowerReading b) => a.Watts <= b.Watts;

    public override string ToString() => $"{MeterId}: {Watts:F2} W";
}
