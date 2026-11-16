using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Shared switches the dashboard can flip to stage a demo (take a node offline,
// fire a burst of traffic). The simulator reads these - the gateway itself
// never looks at them, so the pipeline is tested exactly as with real devices.
public class SimulationControl
{
    private readonly ConcurrentDictionary<string, DateTime> _offlineUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly Channel<int> _bursts = Channel.CreateUnbounded<int>();

    public void TakeOffline(string mac, TimeSpan duration) => _offlineUntil[mac] = DateTime.UtcNow + duration;

    public void BringOnline(string mac) => _offlineUntil.TryRemove(mac, out _);

    public bool IsOffline(string mac) =>
        _offlineUntil.TryGetValue(mac, out var until) && until > DateTime.UtcNow;

    public void RequestBurst(int count) => _bursts.Writer.TryWrite(count);

    public ChannelReader<int> Bursts => _bursts.Reader;

    public IReadOnlyDictionary<string, DateTime> OfflineNodes =>
        _offlineUntil.Where(kv => kv.Value > DateTime.UtcNow).ToDictionary(kv => kv.Key, kv => kv.Value);

    // Devices added from an uploaded .txt list. The simulator "switches them on"
    // so they start publishing telemetry like the rest of the fleet.
    private readonly ConcurrentDictionary<string, SensorDevice> _imported = new(StringComparer.OrdinalIgnoreCase);

    public void AddImported(SensorDevice device) => _imported[device.MacAddress] = device;

    public ICollection<SensorDevice> Imported => _imported.Values;
}

// Part 2 rewrite of Part 1's MockTelemetrySeeder.
//
// Part 1 wrote straight into TelemetryStore. The brief for Part 2 says the
// simulated data must hit the API "like it was a device", so this now behaves
// like a fleet of ESP32 boards: it provisions itself with POST /api/sensors,
// publishes readings with POST /api/telemetry over HTTP, and polls its device
// shadow (GET /api/devices/{mac}/shadow) to find out if the operator switched
// a pump or relay on - exactly how a real board would pick up a command.
public class DeviceFleetSimulator(
    IServer server,
    IHostApplicationLifetime lifetime,
    SimulationControl control,
    SmartX.Api.Services.Topology.TopologyService topology,
    IConfiguration config,
    ILogger<DeviceFleetSimulator> log) : BackgroundService
{
    private readonly Random _rng = new();
    private HttpClient? _http;

    private sealed record SimDevice(string Mac, string Name, string Location, SensorCategory Category, string? Metric, string ValueType, int IntervalSeconds);

    public static readonly string SensorA = "AA:BB:CC:00:01";
    public static readonly string SensorB = "AA:BB:CC:00:02";
    public static readonly string UtilityMeter = "AA:BB:CC:00:03";
    public static readonly string GreenhouseValve = "AA:BB:CC:00:04";
    public static readonly string PumpA = "AA:BB:CC:00:05";
    public static readonly string Relay1 = "AA:BB:CC:00:06";
    public static readonly string TempC = "AA:BB:CC:00:07";
    public static readonly string GridMeter = "AA:BB:CC:00:08";
    public static readonly string IrrigationValve = "AA:BB:CC:00:09";

    private static readonly SimDevice[] Fleet =
    [
        new(SensorA, "Moisture Sensor A", "Facility A / Zone 1 / Sub-Zone B / Hydroponics Bay 1", SensorCategory.Environmental, "moisture", "float", 2),
        new(SensorB, "Moisture Sensor B", "Facility A / Zone 1 / Sub-Zone B / Hydroponics Bay 2", SensorCategory.Environmental, "moisture", "float", 2),
        new(UtilityMeter, "Smart Meter - Utility Room", "Facility A / Zone 2 / Utility Room", SensorCategory.PowerConsumption, "wattage", "float", 2),
        new(GreenhouseValve, "Greenhouse Valve", "Facility A / Zone 2 / Greenhouse", SensorCategory.Actuator, "heartbeat", "bool", 4),
        new(PumpA, "Pump A", "Facility A / Zone 1 / Pump House", SensorCategory.Actuator, "heartbeat", "bool", 4),
        new(Relay1, "Smart Relay 1", "Facility A / Zone 2 / Utility Room", SensorCategory.Actuator, "heartbeat", "bool", 4),
        new(TempC, "Temperature Node C", "Facility A / Zone 2 / Greenhouse", SensorCategory.Environmental, "temperature", "float", 3),
        new(GridMeter, "Smart Meter - Grid Tie", "Facility A / Main Board", SensorCategory.PowerConsumption, "wattage", "int", 3),
        new(IrrigationValve, "Irrigation Valve 2", "Facility A / Zone 1 / Sub-Zone B", SensorCategory.Actuator, "heartbeat", "bool", 4),
    ];

    // Physical state of the simulated world.
    private double _moistureB = 46;
    private bool _pumpOn;
    private bool _relayOn;
    private readonly Dictionary<string, DateTime> _nextSend = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Simulator:Enabled", true))
        {
            log.LogInformation("Device simulator disabled.");
            return;
        }

        // Wait until Kestrel is actually listening, then find out where.
        var started = new TaskCompletionSource();
        using (lifetime.ApplicationStarted.Register(() => started.TrySetResult()))
        {
            await Task.WhenAny(started.Task, Task.Delay(Timeout.Infinite, stoppingToken));
        }

        if (stoppingToken.IsCancellationRequested) return;

        var baseUrl = ResolveBaseUrl();
        _http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(10) };
        // Devices authenticate with the fleet key, not a person's Firebase login.
        _http.DefaultRequestHeaders.Add("X-Device-Key", config["Devices:ApiKey"] ?? "");
        log.LogInformation("Device simulator publishing to {Url}", baseUrl);

        await ProvisionAsync(stoppingToken);

        var burstTask = RunBurstsAsync(stoppingToken);
        var nextOutage = DateTime.UtcNow.AddSeconds(150);
        var tick = 0;

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            tick++;
            try
            {
                if (tick % 2 == 0) await SyncShadowsAsync(stoppingToken);

                // Random outage now and then so the disconnect HashSet gets exercised.
                if (DateTime.UtcNow >= nextOutage)
                {
                    var victim = _rng.NextDouble() < 0.5 ? SensorA : TempC;
                    control.TakeOffline(victim, TimeSpan.FromSeconds(25));
                    nextOutage = DateTime.UtcNow.AddSeconds(150 + _rng.Next(60));
                }

                StepPhysics();

                var now = DateTime.UtcNow;
                foreach (var d in Fleet.Concat(ImportedAsSimDevices()))
                {
                    if (_nextSend.TryGetValue(d.Mac, out var due) && due > now) continue;
                    _nextSend[d.Mac] = now.AddSeconds(d.IntervalSeconds);
                    if (control.IsOffline(d.Mac)) continue;
                    // Final PoE: no radio path left to the gateway -> packets never arrive.
                    if (!topology.CanReachGateway(d.Mac)) continue;

                    await PublishAsync(d.Mac, d.Metric!, d.ValueType, NextValue(d), stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogDebug(ex, "Simulator tick failed (API may be restarting)");
            }
        }

        await burstTask;
    }

    private async Task ProvisionAsync(CancellationToken ct)
    {
        foreach (var d in Fleet)
        {
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    var res = await _http!.PostAsJsonAsync("/api/sensors", new
                    {
                        macAddress = d.Mac,
                        name = d.Name,
                        location = d.Location,
                        category = d.Category.ToString(),
                    }, ct);
                    if (res.IsSuccessStatusCode) break;
                }
                catch (HttpRequestException)
                {
                    await Task.Delay(500, ct);
                }
            }
        }
    }

    private async Task SyncShadowsAsync(CancellationToken ct)
    {
        _pumpOn = await ReadShadowAsync(PumpA, ct) ?? _pumpOn;
        _relayOn = await ReadShadowAsync(Relay1, ct) ?? _relayOn;
    }

    private async Task<bool?> ReadShadowAsync(string mac, CancellationToken ct)
    {
        var res = await _http!.GetAsync($"/api/devices/{Uri.EscapeDataString(mac)}/shadow", ct);
        if (!res.IsSuccessStatusCode) return null;
        var shadow = await res.Content.ReadFromJsonAsync<ShadowDto>(cancellationToken: ct);
        return shadow?.DesiredOn;
    }

    private sealed record ShadowDto(string Mac, bool DesiredOn);

    // Moisture Sensor B dries out steadily until Pump A is switched on, then
    // recovers - and overshoots if the pump is left on. This gives the trend
    // predictor and the "Moisture B low -> turn on Pump A" pattern a real story.
    private void StepPhysics()
    {
        _moistureB += _pumpOn ? 0.9 : -0.2;
        _moistureB = Math.Clamp(_moistureB, 6, 96);
    }

    private IEnumerable<SimDevice> ImportedAsSimDevices() =>
        control.Imported
            .Where(d => Fleet.All(f => !f.Mac.Equals(d.MacAddress, StringComparison.OrdinalIgnoreCase)))
            .Select(d =>
            {
                var label = $"{d.Name} {d.Location}".ToLowerInvariant();
                return d.Category switch
                {
                    SensorCategory.Actuator => new SimDevice(d.MacAddress, d.Name ?? d.MacAddress, d.Location, d.Category, "heartbeat", "bool", 4),
                    SensorCategory.PowerConsumption => new SimDevice(d.MacAddress, d.Name ?? d.MacAddress, d.Location, d.Category, "wattage", "float", 3),
                    _ when label.Contains("temp") => new SimDevice(d.MacAddress, d.Name ?? d.MacAddress, d.Location, d.Category, "temperature", "float", 3),
                    _ => new SimDevice(d.MacAddress, d.Name ?? d.MacAddress, d.Location, d.Category, "moisture", "float", 3),
                };
            });

    private string NextValue(SimDevice d)
    {
        double v;
        switch (d.Mac)
        {
            case var m when m == SensorA:
                v = 45 + _rng.NextDouble() * 22;
                if (_rng.NextDouble() < 0.04) v = 9 + _rng.NextDouble() * 9; // occasional dry spike
                return F(v);

            case var m when m == SensorB:
                return F(_moistureB + (_rng.NextDouble() - 0.5) * 0.8);

            case var m when m == UtilityMeter:
                v = 520 + _rng.NextDouble() * 650 + (_relayOn ? 1450 : 0);
                if (_rng.NextDouble() < 0.03) v = 2650 + _rng.NextDouble() * 300; // severe spike
                return F(v);

            case var m when m == TempC:
                v = 22 + _rng.NextDouble() * 8;
                if (_rng.NextDouble() < 0.03) v = 46 + _rng.NextDouble() * 6;
                return F(v);

            case var m when m == GridMeter:
                return ((int)(900 + _rng.NextDouble() * 700)).ToString(CultureInfo.InvariantCulture);

            default:
                // Imported devices: a normal reading, with the odd out-of-band one.
                var spike = _rng.NextDouble() < 0.03;
                return d.Metric switch
                {
                    "moisture" => F(spike ? 8 + _rng.NextDouble() * 8 : 35 + _rng.NextDouble() * 35),
                    "temperature" => F(spike ? 47 + _rng.NextDouble() * 5 : 19 + _rng.NextDouble() * 9),
                    "wattage" => F(spike ? 2300 + _rng.NextDouble() * 500 : 300 + _rng.NextDouble() * 1100),
                    _ => "true", // actuator heartbeat
                };
        }
    }

    private async Task RunBurstsAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var count in control.Bursts.ReadAllAsync(ct))
            {
                // 16 "devices" talking at once - realistic for a mesh flush after
                // a gateway reboot. All of these are in-band so they go to the
                // standard FIFO lane and pile up behind the rate limit.
                await Parallel.ForAsync(0, count, new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = ct },
                    async (i, token) =>
                    {
                        var mac = i % 2 == 0 ? SensorA : GridMeter;
                        if (mac == SensorA)
                            await PublishAsync(mac, "moisture", "float", F(45 + _rng.NextDouble() * 20), token);
                        else
                            await PublishAsync(mac, "wattage", "int", ((int)(1000 + i % 400)).ToString(CultureInfo.InvariantCulture), token);
                    });
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task PublishAsync(string mac, string metric, string valueType, string raw, CancellationToken ct)
    {
        try
        {
            await _http!.PostAsJsonAsync("/api/telemetry", new { deviceId = mac, metric, valueType, rawValue = raw }, ct);
        }
        catch (HttpRequestException ex)
        {
            log.LogDebug(ex, "Publish failed for {Mac}", mac);
        }
    }

    private string ResolveBaseUrl()
    {
        var configured = config["Simulator:TargetUrl"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;

        var address = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .OrderBy(a => a.StartsWith("https") ? 1 : 0)
            .FirstOrDefault() ?? "http://localhost:5080";

        return address
            .Replace("://+", "://localhost")
            .Replace("://*", "://localhost")
            .Replace("://[::]", "://localhost")
            .Replace("://0.0.0.0", "://localhost");
    }

    private static string F(double v) => Math.Round(v, 1).ToString(CultureInfo.InvariantCulture);
}
