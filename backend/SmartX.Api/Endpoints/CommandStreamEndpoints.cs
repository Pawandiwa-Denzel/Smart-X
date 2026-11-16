using SmartX.Api.Models;
using SmartX.Api.Services;

namespace SmartX.Api.Endpoints;

// Part 2 routes: Real-Time Command Stream & History.
public static class CommandStreamEndpoints
{
    public static void MapCommandStream(this WebApplication app)
    {
        // -------------------------------------------------------------- devices

        // Search / filter. ?q= free text, ?category= Environmental|PowerConsumption|Actuator,
        // ?state= Ok|Warning|Critical|Offline. track=false is used by the
        // dashboard's background refresh so it doesn't count as a user search.
        app.MapGet("/api/devices", (string? q, string? category, string? state, bool? track,
            DeviceStateStore states, ActionEngine engine) =>
        {
            SensorCategory? cat = Enum.TryParse<SensorCategory>(category, true, out var c) ? c : null;
            AlertLevel? level = Enum.TryParse<AlertLevel>(state, true, out var l) ? l : null;

            if (track == true) engine.RecordSearch(q, cat, level);
            return states.Query(q, cat, level);
        });

        app.MapGet("/api/devices/{mac}", (string mac, DeviceStateStore states) =>
            states.Get(mac) is { } d ? Results.Ok(d) : Results.NotFound());

        // Timeline straight out of the SortedDictionary (already in time order).
        app.MapGet("/api/devices/{mac}/history", (string mac, DateTime? from, DateTime? to, int? limit, DeviceStateStore states) =>
        {
            if (!states.Exists(mac)) return Results.NotFound();
            return Results.Ok(states.History(mac, from?.ToUniversalTime(), to?.ToUniversalTime(), Math.Clamp(limit ?? 180, 1, 600)));
        });

        // Device shadow - what the gateway WANTS an actuator to be. Devices poll this.
        app.MapGet("/api/devices/{mac}/shadow", (string mac, DeviceStateStore states) =>
            states.TryGetActuator(mac, out var on, out _)
                ? Results.Ok(new { mac, desiredOn = on })
                : Results.NotFound());

        // ------------------------------------------------------------- commands

        app.MapPost("/api/commands", (CommandRequest request, CommandService commands) =>
        {
            var result = commands.Execute(request);
            return result.Success ? Results.Ok(result) : Results.BadRequest(result);
        });

        app.MapPost("/api/commands/undo", (CommandService commands) => commands.Undo());
        app.MapPost("/api/commands/redo", (CommandService commands) => commands.Redo());
        app.MapGet("/api/commands/history", (CommandService commands) => commands.Snapshot());

        // ---------------------------------------------------------- stream info

        app.MapGet("/api/stream/stats", (GatewayPipeline pipeline) => pipeline.Stats());

        app.MapGet("/api/stream/queue", (GatewayPipeline pipeline, DeviceStateStore states) =>
        {
            var (priority, standard) = pipeline.Queue.Peek(24);
            return new
            {
                priority = priority.Select(p => Lane(p, states)),
                standard = standard.Select(p => Lane(p, states)),
                priorityDepth = pipeline.Queue.PriorityCount,
                standardDepth = pipeline.Queue.StandardCount,
            };
        });

        app.MapGet("/api/stream/recent", (int? max, GatewayPipeline pipeline) => pipeline.Recent(Math.Clamp(max ?? 30, 1, 60)));

        app.MapPost("/api/stream/rate", (int perSecond, GatewayPipeline pipeline) =>
        {
            pipeline.ProcessingRatePerSecond = Math.Clamp(perSecond, 1, 5000);
            return new { pipeline.ProcessingRatePerSecond };
        });

        // Every named data structure in SmartXDataStructures with its live size.
        app.MapGet("/api/structures", (SmartXDataStructures ds) => DescribeSafely(ds));

        app.MapGet("/api/alerts/active", (AlertTracker alerts) => alerts.Snapshot());

        // Server-Sent Events: packet / alert / command / automation events pushed live.
        app.MapGet("/api/events", (LiveBroadcaster live, CancellationToken ct) =>
            TypedResults.ServerSentEvents(live.Subscribe(ct)));

        // --------------------------------------------------------------- engine

        app.MapGet("/api/engine/suggestions", (ActionEngine engine) => engine.Suggestions());
        app.MapGet("/api/engine/insights", (ActionEngine engine) => engine.Insights());
        app.MapGet("/api/engine/patterns", (ActionEngine engine) => engine.Patterns());
        app.MapGet("/api/engine/summary", (ActionEngine engine) => engine.Summary());

        app.MapPost("/api/engine/automode", (bool enabled, ActionEngine engine) =>
        {
            engine.AutoMode = enabled;
            return new { autoMode = engine.AutoMode };
        });

        // ------------------------------------------------------------ dashboard

        // One call that returns everything the Command Stream page needs, so the
        // dashboard refresh is a single round-trip instead of six.
        app.MapGet("/api/dashboard", (GatewayPipeline pipeline, AlertTracker alerts, CommandService commands,
            ActionEngine engine, DeviceStateStore states, LiveBroadcaster live, SimulationControl sim, SmartXDataStructures ds) =>
        {
            var (priority, standard) = pipeline.Queue.Peek(18);
            return new
            {
                serverTime = DateTime.UtcNow,
                stats = pipeline.Stats(),
                queue = new
                {
                    priority = priority.Select(p => Lane(p, states)),
                    standard = standard.Select(p => Lane(p, states)),
                },
                alerts = alerts.Snapshot(),
                commands = commands.Snapshot(),
                suggestions = engine.Suggestions(),
                insights = engine.Insights(),
                engine = engine.Summary(),
                named = DescribeSafely(ds),
                structures = new
                {
                    liveDevices = states.Count,
                    categoryIndex = states.CategoryCounts(),
                    activeErrorSet = alerts.ActiveErrorCount,
                    disconnectedSet = alerts.DisconnectedCount,
                    sseSubscribers = live.SubscriberCount,
                },
                simulatedOffline = sim.OfflineNodes.Keys,
            };
        });

        // ------------------------------------------------------- simulation aids

        // Fires N in-band packets from the simulated fleet over HTTP so the FIFO
        // lane backs up - then inject a critical packet and watch it jump ahead.
        app.MapPost("/api/simulate/burst", (int? count, SimulationControl sim) =>
        {
            var n = Math.Clamp(count ?? 600, 1, 20_000);
            sim.RequestBurst(n);
            return Results.Accepted(value: new { burst = n });
        });

        app.MapPost("/api/simulate/offline/{mac}", (string mac, int? seconds, SimulationControl sim) =>
        {
            var s = Math.Clamp(seconds ?? 30, 5, 600);
            sim.TakeOffline(mac, TimeSpan.FromSeconds(s));
            return Results.Accepted(value: new { mac, silentForSeconds = s });
        });
    }

    // Describe() reads sizes without taking every service's lock. If a worker
    // adds a key mid-read the enumeration can throw, so just try again.
    private static object DescribeSafely(SmartXDataStructures ds)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { return ds.Describe(); }
            catch (InvalidOperationException) when (attempt < 3) { Thread.Yield(); }
        }
    }

    private static object Lane(QueuedPacket p, DeviceStateStore states) => new
    {
        p.Sequence,
        p.DeviceId,
        deviceName = states.NameOf(p.DeviceId),
        p.Metric,
        value = p.DisplayValue,
        severity = p.Severity.ToString(),
        waitingMs = Math.Round((DateTime.UtcNow - p.ReceivedAt).TotalMilliseconds),
    };
}
