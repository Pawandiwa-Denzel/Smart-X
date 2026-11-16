using SmartX.Api.Models;

namespace SmartX.Api.Services.Topology;

// ===========================================================================
//  TopologyService - keeps the MeshGraph in step with the device registry and
//  answers the Network Topology page:
//
//    Snapshot()      every node/link, which links form the MST, who is down
//    Route(mac)      the hop-by-hop path a sensor's packets take to the gateway
//    Explore(start)  BFS of a gateway/subnet: what's connected, what's cut off
//    Fail/Restore    take a radio down to watch the mesh heal itself
//
//  A node counts as DOWN if an operator failed it here, or if the heartbeat
//  monitor has it in the DisconnectedNodes HashSet (Part 2).
// ===========================================================================
public sealed class TopologyService(
    SmartXDataStructures ds,
    DeviceStateStore states,
    AlertTracker alerts,
    SimulationControl simulation)
{
    public const string GatewayMac = "AA:BB:CC:FF:00";

    private readonly object _lock = new();
    private int _builtForDeviceCount = -1;
    private MstResult? _baseline;   // MST with every radio healthy - used to spot re-routes

    private MeshGraph Graph => ds.MeshTopology;
    private HashSet<string> Failed => ds.FailedMeshNodes;

    // Fixed infrastructure: the central gateway and one mesh router per subnet.
    private static readonly (string Mac, string Name, string Subnet, double X, double Y)[] Infrastructure =
    [
        (GatewayMac, "Central Gateway", "Core", 500, 320),
        ("AA:BB:CC:FF:01", "Zone 1 Mesh Router", "Zone 1", 255, 195),
        ("AA:BB:CC:FF:02", "Zone 2 Mesh Router", "Zone 2", 745, 195),
        ("AA:BB:CC:FF:03", "Main Board Router", "Main Board", 330, 505),
        ("AA:BB:CC:FF:04", "Facility B Router", "Facility B", 690, 505),
    ];

    public static string SubnetFor(string location)
    {
        var l = location.ToLowerInvariant();
        if (l.Contains("facility b")) return "Facility B";
        if (l.Contains("zone 1")) return "Zone 1";
        if (l.Contains("zone 2")) return "Zone 2";
        return "Main Board";
    }

    // ------------------------------------------------------------- graph build

    private void EnsureGraph()
    {
        var count = states.Count;
        if (count == _builtForDeviceCount) return;

        Graph.Clear();
        foreach (var (mac, name, subnet, x, y) in Infrastructure)
        {
            Graph.AddNode(new MeshNode
            {
                Mac = mac, Name = name, Subnet = subnet, X = x, Y = y,
                Kind = subnet == "Core" ? MeshNodeKind.Gateway : MeshNodeKind.Router,
            });
        }

        var devices = states.Query(null, null, null);
        foreach (var group in devices.GroupBy(d => SubnetFor(d.Location)))
        {
            var router = Infrastructure.First(i => i.Subnet == group.Key);
            var list = group.OrderBy(d => d.Mac, StringComparer.OrdinalIgnoreCase).ToList();

            // Fan the devices out around their router, centred on the direction
            // pointing away from the gateway; the fan wraps round far enough that
            // some devices also sit between the router and the gateway, giving
            // the mesh alternative paths when a router fails.
            var away = Math.Atan2(router.Y - 320, router.X - 500);
            var spread = Math.PI * 1.75;
            for (var i = 0; i < list.Count; i++)
            {
                var t = list.Count == 1 ? 0.5 : i / (double)(list.Count - 1);
                var angle = away - spread / 2 + spread * t;
                var radius = i % 2 == 0 ? 120 : 165;
                var d = list[i];
                Graph.AddNode(new MeshNode
                {
                    Mac = d.Mac,
                    Name = d.Name,
                    Subnet = group.Key,
                    Kind = d.Category == SensorCategory.Actuator ? MeshNodeKind.Actuator : MeshNodeKind.Sensor,
                    X = Math.Clamp(router.X + radius * Math.Cos(angle), 30, 970),
                    Y = Math.Clamp(router.Y + radius * Math.Sin(angle), 30, 610),
                });
            }
        }

        Graph.LinkByRadioRange();
        _baseline = MeshRouting.PrimMst(Graph, GatewayMac, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        _builtForDeviceCount = count;
    }

    // Radios with no possible path to the gateway because the radios around them
    // were failed. They go silent (heartbeat lost) but are not broken themselves,
    // so they are reported as "isolated", not "offline".
    private HashSet<string> CutOffByFailures()
    {
        var bfs = MeshRouting.BreadthFirst(Graph, GatewayMac, new HashSet<string>(Failed, StringComparer.OrdinalIgnoreCase));
        return new HashSet<string>(Graph.Nodes.Where(n => !Failed.Contains(n.Mac) && !bfs.Parent.ContainsKey(n.Mac)).Select(n => n.Mac),
            StringComparer.OrdinalIgnoreCase);
    }

    private HashSet<string> DownSet()
    {
        var down = new HashSet<string>(Failed, StringComparer.OrdinalIgnoreCase);
        foreach (var node in Graph.Nodes)
            if (alerts.IsDisconnected(node.Mac)) down.Add(node.Mac);
        return down;
    }

    // --------------------------------------------------------------- snapshot

    public object Snapshot()
    {
        lock (_lock)
        {
            EnsureGraph();
            var down = DownSet();
            var mst = MeshRouting.PrimMst(Graph, GatewayMac, down);
            var mstKeys = new HashSet<string>(mst.Edges.Select(Key));
            var unreachable = new HashSet<string>(mst.Unreachable, StringComparer.OrdinalIgnoreCase);
            unreachable.UnionWith(CutOffByFailures());

            var nodes = Graph.Nodes.Select(n =>
            {
                var path = MeshRouting.PathToRoot(mst, n.Mac);
                var basePath = _baseline is null ? path : MeshRouting.PathToRoot(_baseline, n.Mac);
                var view = states.Get(n.Mac);
                var status = Failed.Contains(n.Mac) ? "failed"
                    : unreachable.Contains(n.Mac) ? "isolated"
                    : down.Contains(n.Mac) ? "offline"
                    : "online";

                return new
                {
                    n.Mac, n.Name, kind = n.Kind.ToString(), n.Subnet,
                    x = Math.Round(n.X, 1), y = Math.Round(n.Y, 1),
                    status,
                    hops = path.Count > 0 ? path.Count - 1 : (int?)null,
                    nextHop = path.Count > 1 ? path[1] : null,
                    rerouted = status == "online" && path.Count > 0 && basePath.Count > 0 && !path.SequenceEqual(basePath, StringComparer.OrdinalIgnoreCase),
                    metric = view?.Metric,
                    value = view?.Value,
                    alert = view?.Alert.ToString(),
                };
            }).ToList();

            var links = Graph.Links().Select(l => new
            {
                l.A, l.B, l.Distance, l.Rssi, l.PowerCost,
                inMst = mstKeys.Contains(Key(l)),
                active = !down.Contains(l.A) && !down.Contains(l.B),
            }).ToList();

            var activeLinkCost = Graph.Links().Where(l => !down.Contains(l.A) && !down.Contains(l.B)).Sum(l => l.PowerCost);

            return new
            {
                gateway = GatewayMac,
                canvas = new { width = 1000, height = 640 },
                nodes,
                links,
                mst = new
                {
                    totalCost = mst.TotalCost,
                    edgeCount = mst.Edges.Count,
                    baselineCost = _baseline?.TotalCost,
                    allActiveLinksCost = Math.Round(activeLinkCost, 2),
                    heapPushes = mst.HeapPushes,
                },
                stats = new
                {
                    vertices = Graph.VertexCount,
                    edges = Graph.EdgeCount,
                    online = nodes.Count(n => n.status == "online"),
                    down = nodes.Count(n => n.status is "failed" or "offline"),
                    isolated = unreachable.Count,
                    rerouted = nodes.Count(n => n.rerouted),
                },
                subnets = Infrastructure.Where(i => i.Subnet != "Core").Select(i => new
                {
                    name = i.Subnet,
                    router = i.Mac,
                    devices = Graph.Nodes.Count(n => n.Subnet == i.Subnet && n.Kind != MeshNodeKind.Router),
                }),
                failed = Failed.ToList(),
            };
        }
    }

    // ------------------------------------------------------------------ route

    public object? Route(string query)
    {
        lock (_lock)
        {
            EnsureGraph();
            var mac = Resolve(query);
            if (mac is null || !Graph.TryGetNode(mac, out var node)) return null;

            var down = DownSet();
            var mst = MeshRouting.PrimMst(Graph, GatewayMac, down);
            var path = down.Contains(mac) ? new List<string>() : MeshRouting.PathToRoot(mst, mac);
            var basePath = _baseline is null ? new List<string>() : MeshRouting.PathToRoot(_baseline, mac);

            // Fewest-hop alternative from a BFS rooted at the gateway (reversed so it reads node -> gateway).
            var bfs = MeshRouting.BreadthFirst(Graph, GatewayMac, down);
            var fewestHops = MeshRouting.PathFromBfs(bfs, mac);

            var hops = new List<object>();
            double total = 0;
            for (var i = 0; i + 1 < path.Count; i++)
            {
                var link = Graph.Neighbours(path[i]).First(l => string.Equals(l.Other(path[i]), path[i + 1], StringComparison.OrdinalIgnoreCase));
                total += link.PowerCost;
                hops.Add(new { from = path[i], fromName = NameOf(path[i]), to = path[i + 1], toName = NameOf(path[i + 1]), link.Distance, link.Rssi, link.PowerCost });
            }

            // If there's no route, list the neighbours it could hop to and why they can't help.
            var neighbours = Graph.Neighbours(mac).OrderBy(l => l.PowerCost).Select(l =>
            {
                var other = l.Other(mac);
                var otherPath = down.Contains(other) ? new List<string>() : MeshRouting.PathToRoot(mst, other);
                return new
                {
                    mac = other,
                    name = NameOf(other),
                    l.PowerCost,
                    l.Rssi,
                    state = down.Contains(other) ? "down" : otherPath.Count == 0 ? "no route" : "usable",
                    hopsToGateway = otherPath.Count > 0 ? otherPath.Count - 1 : (int?)null,
                };
            }).ToList();

            var status = Failed.Contains(mac) ? "failed"
                : CutOffByFailures().Contains(mac) ? "isolated"
                : down.Contains(mac) ? "offline"
                : path.Count == 0 ? "isolated" : "online";

            return new
            {
                mac,
                name = node.Name,
                node.Subnet,
                kind = node.Kind.ToString(),
                status,
                path,
                pathNames = path.Select(NameOf),
                hops,
                hopCount = Math.Max(0, path.Count - 1),
                totalCost = Math.Round(total, 2),
                baselinePath = basePath,
                rerouted = path.Count > 0 && basePath.Count > 0 && !path.SequenceEqual(basePath, StringComparer.OrdinalIgnoreCase),
                fewestHopPath = fewestHops,
                neighbours,
            };
        }
    }

    // ---------------------------------------------------------------- explore

    public object? Explore(string start, bool subnetOnly)
    {
        lock (_lock)
        {
            EnsureGraph();
            var mac = Resolve(start);
            if (mac is null || !Graph.TryGetNode(mac, out var root)) return null;

            var down = DownSet();
            var subnet = subnetOnly && root.Kind != MeshNodeKind.Gateway ? root.Subnet : null;
            var bfs = MeshRouting.BreadthFirst(Graph, mac, down, subnet);
            var reached = new HashSet<string>(bfs.Order.Select(v => v.Mac), StringComparer.OrdinalIgnoreCase);

            // Everything that SHOULD be in scope but wasn't reached.
            var scope = Graph.Nodes.Where(n => subnet is null || n.Subnet == subnet).ToList();
            var mst = MeshRouting.PrimMst(Graph, GatewayMac, down);

            var cutOffByFailures = CutOffByFailures();
            var cutOff = scope.Where(n => !reached.Contains(n.Mac)).Select(n =>
            {
                var isDown = down.Contains(n.Mac) && !cutOffByFailures.Contains(n.Mac);
                // Who used to route through this node, and where do they hop now?
                var dependants = isDown && _baseline is not null
                    ? Graph.Nodes.Where(o => !Failed.Contains(o.Mac) &&
                                             MeshRouting.PathToRoot(_baseline, o.Mac).Skip(1).Contains(n.Mac, StringComparer.OrdinalIgnoreCase))
                        .Select(o =>
                        {
                            var now = MeshRouting.PathToRoot(mst, o.Mac);
                            return new { mac = o.Mac, name = o.Name, newNextHop = now.Count > 1 ? NameOf(now[1]) : null, hopsNow = now.Count > 0 ? now.Count - 1 : (int?)null };
                        }).ToList<object>()
                    : new List<object>();

                // Not reached by THIS search, but maybe still reaching the gateway
                // another way (e.g. through a neighbouring subnet) - that's a re-route.
                var globalPath = isDown ? new List<string>() : MeshRouting.PathToRoot(mst, n.Mac);
                var state = isDown ? "down" : globalPath.Count > 0 ? "rerouted" : "isolated";

                return new
                {
                    n.Mac, n.Name, n.Subnet,
                    state,
                    reason = Failed.Contains(n.Mac) ? "failed by operator"
                        : isDown ? "no heartbeat"
                        : globalPath.Count > 0 ? $"reaches the gateway via {NameOf(globalPath[1])} ({globalPath.Count - 1} hops)"
                        : "no path left - every neighbour it can hear is down, so it has gone silent",
                    dependants,
                };
            }).ToList();

            return new
            {
                start = mac,
                startName = root.Name,
                startDown = down.Contains(mac),
                subnet = subnet ?? "Entire mesh",
                visited = bfs.Order.Select(v => new { v.Mac, name = NameOf(v.Mac), v.Hop, via = v.Via, viaName = v.Via is null ? null : NameOf(v.Via) }),
                levels = bfs.Order.GroupBy(v => v.Hop).Select(g => new { hop = g.Key, nodes = g.Select(v => NameOf(v.Mac)) }),
                reachedCount = reached.Count,
                scopeCount = scope.Count,
                cutOff,
                downNeighbours = bfs.DownNeighbours,
            };
        }
    }

    // ------------------------------------------------------- fail / restore

    public bool Fail(string mac)
    {
        lock (_lock)
        {
            EnsureGraph();
            if (!Graph.TryGetNode(mac, out var node) || node.Kind == MeshNodeKind.Gateway) return false;
            Failed.Add(node.Mac);
            simulation.TakeOffline(node.Mac, TimeSpan.FromDays(1));  // the radio really stops transmitting
            return true;
        }
    }

    public bool Restore(string mac)
    {
        lock (_lock)
        {
            simulation.BringOnline(mac);
            return Failed.Remove(mac);
        }
    }

    public void RestoreAll()
    {
        lock (_lock)
        {
            foreach (var mac in Failed) simulation.BringOnline(mac);
            Failed.Clear();
        }
    }

    // Used by the device simulator: a packet only reaches the gateway if there
    // is still a radio path to it. Isolated sensors therefore go silent and the
    // heartbeat monitor flags them - just like a real cut-off mesh island.
    public bool CanReachGateway(string mac)
    {
        lock (_lock)
        {
            EnsureGraph();
            if (!Graph.TryGetNode(mac, out _)) return true;
            var failedOnly = new HashSet<string>(Failed, StringComparer.OrdinalIgnoreCase);
            var bfs = MeshRouting.BreadthFirst(Graph, GatewayMac, failedOnly);
            return bfs.Parent.ContainsKey(mac);
        }
    }

    // ---------------------------------------------------------------- helpers

    private string? Resolve(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var q = query.Trim();
        if (Graph.TryGetNode(q, out var exact)) return exact.Mac;
        var hits = Graph.Nodes.Where(n => n.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                                          n.Mac.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
        return hits.Count >= 1 ? hits.OrderBy(n => n.Name.Length).First().Mac : null;
    }

    private string NameOf(string mac) => Graph.TryGetNode(mac, out var n) ? n.Name : mac;

    private static string Key(MeshLink l) =>
        string.CompareOrdinal(l.A.ToUpperInvariant(), l.B.ToUpperInvariant()) < 0
            ? $"{l.A.ToUpperInvariant()}|{l.B.ToUpperInvariant()}"
            : $"{l.B.ToUpperInvariant()}|{l.A.ToUpperInvariant()}";
}
