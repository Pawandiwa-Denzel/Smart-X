using SmartX.Api.Services.Topology;

namespace SmartX.Api.Endpoints;

// Final PoE routes: Network Topology & Mesh Routing (Tab 3).
public static class TopologyEndpoints
{
    public static void MapTopology(this WebApplication app)
    {
        // ---------------------------------------------------------- mesh graph

        // Whole mesh: vertices, weighted edges, which edges are in the MST, status.
        app.MapGet("/api/topology", (TopologyService topology) => topology.Snapshot());

        // Hop-by-hop route a node's packets take to the central gateway (MST path),
        // plus the fewest-hop BFS path and the neighbours it could fall back to.
        // Accepts a MAC address or part of a device name.
        app.MapGet("/api/topology/route/{query}", (string query, TopologyService topology) =>
            topology.Route(query) is { } route ? Results.Ok(route) : Results.NotFound(new { error = $"No mesh node matches '{query}'." }));

        // BFS from a gateway/router: which devices are still connected, which are cut off
        // and where traffic re-routes. subnetOnly=true keeps the search inside that subnet.
        app.MapGet("/api/topology/explore/{start}", (string start, bool? subnetOnly, TopologyService topology) =>
            topology.Explore(start, subnetOnly ?? true) is { } result ? Results.Ok(result) : Results.NotFound(new { error = $"No mesh node matches '{start}'." }));

        // Take a radio down / bring it back (self-healing demo).
        app.MapPost("/api/topology/nodes/{mac}/fail", (string mac, TopologyService topology) =>
            topology.Fail(mac) ? Results.Ok(new { mac, status = "failed" }) : Results.BadRequest(new { error = "Unknown node, or it is the central gateway." }));

        app.MapPost("/api/topology/nodes/{mac}/restore", (string mac, TopologyService topology) =>
            Results.Ok(new { mac, restored = topology.Restore(mac) }));

        app.MapPost("/api/topology/restore-all", (TopologyService topology) =>
        {
            topology.RestoreAll();
            return Results.Ok(new { restored = true });
        });

        // --------------------------------------------------- AVL history tree

        // Which sensors have history, and how big/tall each tree is.
        app.MapGet("/api/history", (TelemetryHistoryIndex history) => history.Overview());

        app.MapGet("/api/history/{mac}/summary", (string mac, TelemetryHistoryIndex history) =>
            history.Summary(mac) is { } s ? Results.Ok(s) : Results.NotFound());

        // Range query, e.g. /api/history/AA:BB:CC:00:01?from=2026-11-10T12:00:00Z&to=2026-11-10T14:00:00Z
        app.MapGet("/api/history/{mac}", (string mac, DateTime? from, DateTime? to, int? limit, TelemetryHistoryIndex history) =>
        {
            var end = (to ?? DateTime.UtcNow).ToUniversalTime();
            var start = (from ?? end.AddHours(-2)).ToUniversalTime();
            if (start > end) return Results.BadRequest(new { error = "'from' must be before 'to'." });

            return history.Query(mac, start, end, Math.Clamp(limit ?? 5000, 1, 50_000)) is { } result
                ? Results.Ok(result)
                : Results.NotFound(new { error = $"No history for {mac}." });
        });

        // AVL range query vs a linear scan of an unsorted List<T> over the same data.
        app.MapGet("/api/history/{mac}/benchmark", (string mac, DateTime? from, DateTime? to, TelemetryHistoryIndex history) =>
        {
            var end = (to ?? DateTime.UtcNow).ToUniversalTime();
            var start = (from ?? end.AddHours(-2)).ToUniversalTime();
            return history.Benchmark(mac, start, end) is { } b ? Results.Ok(b) : Results.NotFound();
        });
    }
}
