using System.Text.RegularExpressions;
using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Reads an uploaded .txt device list and registers every device in it.
//
// One device per line:   MAC, Name, Location, Category
//   - separators: comma, pipe, semicolon or tab
//   - '#' starts a comment, blank lines are ignored, a "MAC,..." header row is skipped
//   - Category is optional (Environmental / Power / Actuator, or inferred from the name)
//
// Once registered, a device is in the SensorRegistry (Part 1 list), the
// DeviceRegistry dictionary (Command Stream grid + search) and the category
// index. The simulator is told about it too, so it starts sending telemetry
// to the API like a real board instead of sitting there as an empty tile.
public partial class DeviceListImporter(SensorRegistry registry, SimulationControl simulation)
{
    public const long MaxFileBytes = 1_000_000;

    public sealed record LineIssue(int Line, string Text, string Reason);

    public sealed record ImportResult(
        int Imported,
        int Updated,
        int LinesRead,
        IReadOnlyList<LineIssue> Skipped,
        IReadOnlyList<SensorDevice> Devices);

    [GeneratedRegex(@"^([0-9A-Fa-f]{2}[:-]){2,7}[0-9A-Fa-f]{2}$")]
    private static partial Regex MacPattern();

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_\-]{2,31}$")]
    private static partial Regex NodeIdPattern();

    public async Task<ImportResult> ImportAsync(Stream stream, CancellationToken ct)
    {
        using var reader = new StreamReader(stream);
        var skipped = new List<LineIssue>();
        var devices = new List<SensorDevice>();
        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int imported = 0, updated = 0, lineNo = 0;

        while (await reader.ReadLineAsync(ct) is { } raw)
        {
            lineNo++;
            var line = raw.Split('#')[0].Trim();
            if (line.Length == 0) continue;

            var fields = line.Split([',', '|', ';', '\t'], StringSplitOptions.TrimEntries);
            if (fields[0].Equals("mac", StringComparison.OrdinalIgnoreCase)) continue; // header row

            var mac = fields[0].ToUpperInvariant().Replace('-', ':');
            if (!MacPattern().IsMatch(mac) && !NodeIdPattern().IsMatch(fields[0]))
            {
                skipped.Add(new LineIssue(lineNo, raw, $"'{fields[0]}' is not a MAC address or node ID"));
                continue;
            }

            if (!MacPattern().IsMatch(mac)) mac = fields[0]; // keep node IDs as typed

            if (!seenInFile.Add(mac))
            {
                skipped.Add(new LineIssue(lineNo, raw, "Duplicate of an earlier line in this file"));
                continue;
            }

            var name = fields.Length > 1 && fields[1].Length > 0 ? fields[1] : mac;
            var location = fields.Length > 2 && fields[2].Length > 0 ? fields[2] : "Imported / Unassigned";

            SensorCategory category;
            if (fields.Length > 3 && fields[3].Length > 0)
            {
                if (ParseCategory(fields[3]) is not { } parsed)
                {
                    skipped.Add(new LineIssue(lineNo, raw, $"Unknown category '{fields[3]}' (use Environmental, Power or Actuator)"));
                    continue;
                }

                category = parsed;
            }
            else
            {
                category = ParseCategory(name) ?? SensorCategory.Environmental;
            }

            var existed = registry.TryGet(mac, out _);
            var device = registry.Register(mac, location, category, name);
            simulation.AddImported(device);
            devices.Add(device);
            if (existed) updated++; else imported++;
        }

        return new ImportResult(imported, updated, lineNo, skipped, devices);
    }

    private static SensorCategory? ParseCategory(string text)
    {
        var t = text.ToLowerInvariant();
        if (t.Contains("actuator") || t.Contains("pump") || t.Contains("valve") || t.Contains("relay") || t.Contains("switch"))
            return SensorCategory.Actuator;
        if (t.Contains("power") || t.Contains("meter") || t.Contains("watt") || t.Contains("energy"))
            return SensorCategory.PowerConsumption;
        if (t.Contains("env") || t.Contains("moisture") || t.Contains("temp") || t.Contains("humid") || t.Contains("sensor"))
            return SensorCategory.Environmental;
        return null;
    }
}
