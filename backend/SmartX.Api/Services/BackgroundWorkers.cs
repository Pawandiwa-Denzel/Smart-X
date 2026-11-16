using SmartX.Api.Models;

namespace SmartX.Api.Services;

// Drains the DualLaneQueue. Processing is deliberately rate-limited (like a
// small gateway board with a fixed CPU budget) so that under a burst you can
// SEE the standard lane back up while critical packets still go straight
// through the priority lane.
public class TelemetryProcessor(GatewayPipeline pipeline, ILogger<TelemetryProcessor> log) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        double budget = 0;

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // Packets allowed this tick, carrying fractions over so low rates still work.
            budget = Math.Min(budget + pipeline.ProcessingRatePerSecond * Tick.TotalSeconds, pipeline.ProcessingRatePerSecond);

            while (budget >= 1)
            {
                // Read this BEFORE dequeuing: if normal packets are waiting and we
                // still serve a priority one, that's a genuine queue bypass.
                var standardWaiting = pipeline.Queue.StandardCount;

                if (!pipeline.Queue.TryDequeue(out var packet, out var fromPriority)) break;
                budget--;

                if (fromPriority && standardWaiting > 0) pipeline.NoteBypass();

                try
                {
                    pipeline.ProcessOne(packet, fromPriority);
                }
                catch (Exception ex)
                {
                    // One bad packet must never kill the background worker.
                    log.LogError(ex, "Failed to process packet {Seq} from {Device}", packet.Sequence, packet.DeviceId);
                }
            }
        }
    }
}

// Checks every couple of seconds for nodes that have gone quiet. The HashSet
// inside AlertTracker means each outage is raised once, however many times
// this loop notices it.
public class HeartbeatMonitor(
    DeviceStateStore states,
    AlertTracker alerts,
    ActionEngine engine,
    LiveBroadcaster live,
    IConfiguration config) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timeout = TimeSpan.FromSeconds(config.GetValue("Gateway:HeartbeatTimeoutSeconds", 12));
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var now = DateTime.UtcNow;
            foreach (var (mac, lastSeen) in states.LastSeenSnapshot())
            {
                var silent = now - lastSeen;
                if (silent < timeout) continue;

                var name = states.NameOf(mac);
                if (alerts.MarkDisconnected(mac, name, silent.TotalSeconds, now))
                {
                    states.SetAlert(mac, AlertLevel.Offline, $"No packet for {silent.TotalSeconds:F0}s");
                    engine.Record(new ActivityEvent(ActivityKind.Offline, $"offline|{mac}", $"{name} offline", now));
                    live.Publish("alert", new { kind = "offline", deviceId = mac, deviceName = name, at = now });
                }
            }
        }
    }
}

// Runs the action engine on a timer. When Auto mode is on, high-confidence
// suggestions that are tied to a live alert are executed automatically (and
// pushed onto the same undo stack, so an operator can still revert them).
public class AutomationLoop(ActionEngine engine, CommandService commands, LiveBroadcaster live) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (!engine.AutoMode) continue;

            foreach (var (suggestion, occurrenceKey) in engine.AutoExecutable())
            {
                var result = commands.Execute(suggestion.Command! with { IssuedBy = "Auto-Engine" });
                engine.MarkAutoExecuted(occurrenceKey);
                live.Publish("automation", new { suggestion.Title, result.Success, result.Message, at = DateTime.UtcNow });
            }
        }
    }
}
