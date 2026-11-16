using System.Collections.Concurrent;
using SmartX.Api.Models;

namespace SmartX.Api.Services;


// Live registry of sensors keyed by MAC address for O(1) lookups when a
// telemetry packet arrives and the gateway needs to know where it came from.

public class SensorRegistry(DeviceStateStore liveState)
{
    private readonly ConcurrentDictionary<string, SensorDevice> _devices = new(StringComparer.OrdinalIgnoreCase);

    public SensorDevice Register(SensorDevice device)
    {
        _devices[device.MacAddress] = device;
        // Part 2: mirror the device into the live state cache used by the
        // command stream so it shows up on the dashboard straight away.
        liveState.Upsert(device);
        return device;
    }

    // Method overload: same name, different parameter list. Lets a caller
    // register a sensor from raw values without having to build a
    // SensorDevice object first - handy for quick simulation/test code.
    public SensorDevice Register(string macAddress, string location, SensorCategory category, string? name = null)
    {
        return Register(new SensorDevice
        {
            MacAddress = macAddress,
            Name = name,
            Location = location,
            Category = category,
        });
    }

    public bool TryGet(string mac, out SensorDevice device) => _devices.TryGetValue(mac, out device!);

    public IEnumerable<SensorDevice> All() => _devices.Values.OrderBy(d => d.Location);

    public bool AddAttachment(string mac, string fileName)
    {
        if (!_devices.TryGetValue(mac, out var device)) return false;
        device.AttachmentFiles.Add(fileName);
        return true;
    }
}
