using SmartX.Api.Services.Topology;

// ===========================================================================
//  SmartX.Tests - checks the final PoE data structures against simple,
//  obviously-correct reference implementations (brute force / Kruskal).
//
//      dotnet run --project backend/SmartX.Tests
// ===========================================================================

var t = new TestRunner();
var rng = new Random(7312);

// ---------------------------------------------------------------- AVL tree
t.Section("AVL tree (timestamped telemetry history)");
{
    var tree = new AvlTelemetryTree("moisture");
    var start = new DateTime(2026, 11, 10, 0, 0, 0, DateTimeKind.Utc);
    const int n = 20_000;
    for (var i = 0; i < n; i++) tree.Insert(start.AddSeconds(i * 15), i);   // sorted input: worst case for a plain BST

    t.Check("all readings stored", tree.Count == n);
    t.Check($"height stays logarithmic (height {tree.Height} <= 1.44 log2 n = {1.44 * Math.Log2(n):0.0})", tree.Height <= 1.44 * Math.Log2(n) + 2);
    t.Check("balance + BST order invariant holds", tree.IsValidAvl());
    t.Check("rotations happened while inserting sorted keys", tree.Rotations > 0);

    var from = start.AddHours(14);
    var to = start.AddHours(16);
    var result = tree.RangeQuery(from, to, int.MaxValue, out var stats);
    var expected = Enumerable.Range(0, n).Select(i => start.AddSeconds(i * 15)).Count(ts => ts >= from && ts <= to);
    t.Check($"14:00-16:00 range query returns {expected} readings", result.Count == expected);
    t.Check("range query results are in time order", result.Zip(result.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
    t.Check($"range query visited {stats.NodesVisited} nodes, far fewer than the {n} stored", stats.NodesVisited < expected + 4 * tree.Height);
    t.Check("CountInRange (subtree sizes) agrees with the query", tree.CountInRange(from, to) == expected);
    t.Check("empty window returns nothing", tree.RangeQuery(start.AddDays(5), start.AddDays(6), 100, out _).Count == 0);

    // Random insertion order + duplicate timestamps.
    var random = new AvlTelemetryTree("wattage");
    var keys = new List<DateTime>();
    for (var i = 0; i < 5_000; i++)
    {
        var key = random.Insert(start.AddSeconds(rng.Next(0, 3_000)), rng.NextDouble());
        keys.Add(key);
    }
    t.Check("duplicate timestamps are kept (nudged by a tick, not overwritten)", random.Count == 5_000 && keys.Distinct().Count() == 5_000);
    t.Check("random-order tree is still a valid AVL tree", random.IsValidAvl());
    var list = random.ToList();
    t.Check("in-order traversal is sorted", list.Zip(list.Skip(1)).All(p => p.First.Timestamp < p.Second.Timestamp));
    for (var trial = 0; trial < 50; trial++)
    {
        var a = start.AddSeconds(rng.Next(0, 3_000));
        var b = a.AddSeconds(rng.Next(0, 600));
        var got = random.RangeQuery(a, b, int.MaxValue, out _).Count;
        var brute = keys.Count(k => k >= a && k <= b);
        if (got != brute || random.CountInRange(a, b) != brute) { t.Check($"random range #{trial} matches brute force", false); break; }
        if (trial == 49) t.Check("50 random range queries match a brute-force scan", true);
    }
}

// ---------------------------------------------------------------- MinHeap
t.Section("MinHeap (priority queue used by Prim's algorithm)");
{
    var heap = new MinHeap<int>();
    var values = Enumerable.Range(0, 1_000).Select(_ => rng.NextDouble() * 100).ToList();
    for (var i = 0; i < values.Count; i++) heap.Push(i, values[i]);
    var popped = new List<double>();
    while (heap.TryPop(out _, out var p)) popped.Add(p);
    t.Check("pops every item", popped.Count == values.Count);
    t.Check("pops in ascending priority order", popped.Zip(popped.Skip(1)).All(p => p.First <= p.Second));
    t.Check("TryPop on an empty heap returns false", !heap.TryPop(out _, out _));
}

// ---------------------------------------------------------------- Graph + BFS
t.Section("Mesh graph + Breadth-First Search");
//
//   G -- R1 -- S1 -- S2
//   |          |
//   R2 ------- S3          S4 (out of range of everyone)
//
var graph = new MeshGraph();
void Node(string mac, MeshNodeKind kind, string subnet) => graph.AddNode(new MeshNode { Mac = mac, Name = mac, Kind = kind, Subnet = subnet });
void Link(string a, string b, double cost) => graph.AddLink(new MeshLink(a, b, cost * 10, -60, cost));
Node("G", MeshNodeKind.Gateway, "Core");
Node("R1", MeshNodeKind.Router, "Zone 1");
Node("R2", MeshNodeKind.Router, "Zone 2");
Node("S1", MeshNodeKind.Sensor, "Zone 1");
Node("S2", MeshNodeKind.Sensor, "Zone 1");
Node("S3", MeshNodeKind.Sensor, "Zone 2");
Node("S4", MeshNodeKind.Sensor, "Zone 2");
Link("G", "R1", 2); Link("G", "R2", 3); Link("R1", "S1", 1); Link("S1", "S2", 1); Link("S1", "S3", 4); Link("R2", "S3", 1);
var none = new HashSet<string>();
{
    t.Check("vertex and edge counts", graph.VertexCount == 7 && graph.EdgeCount == 6);
    var bfs = MeshRouting.BreadthFirst(graph, "G", none);
    var hops = bfs.Order.ToDictionary(v => v.Mac, v => v.Hop);
    t.Check("BFS reaches every connected radio", hops.Count == 6 && !hops.ContainsKey("S4"));
    t.Check("BFS finds fewest hops (S2 is 3 hops away)", hops["S2"] == 3 && hops["S3"] == 2);
    t.Check("BFS visits level by level", bfs.Order.Zip(bfs.Order.Skip(1)).All(p => p.First.Hop <= p.Second.Hop));
    t.Check("fewest-hop path S2 -> S1 -> R1 -> G", string.Join(">", MeshRouting.PathFromBfs(bfs, "S2")) == "S2>S1>R1>G");

    var withR1Down = MeshRouting.BreadthFirst(graph, "G", new HashSet<string> { "R1" });
    var reached = withR1Down.Order.Select(v => v.Mac).ToHashSet();
    t.Check("with R1 down, S1/S2 are still reached through S3 (self-healing)", reached.SetEquals(new[] { "G", "R2", "S3", "S1", "S2" }));
    t.Check("BFS reports the down neighbour it bumped into", withR1Down.DownNeighbours.Contains("R1"));

    var subnet = MeshRouting.BreadthFirst(graph, "R1", none, "Zone 1");
    t.Check("subnet-only BFS stays inside Zone 1", subnet.Order.All(v => graph.TryGetNode(v.Mac, out var nd) && nd.Subnet == "Zone 1"));
}

// ---------------------------------------------------------------- MST
t.Section("Prim's Minimum Spanning Tree (checked against Kruskal)");
{
    var mst = MeshRouting.PrimMst(graph, "G", none);
    t.Check("tree has V-1 edges for the 6 connected radios", mst.Edges.Count == 5);
    t.Check("isolated radio S4 is reported unreachable", mst.Unreachable.SequenceEqual(new[] { "S4" }));
    var kruskal = KruskalCost(graph, none);
    t.Check($"Prim total cost {mst.TotalCost} equals Kruskal {kruskal}", Math.Abs(mst.TotalCost - kruskal) < 1e-9);
    t.Check("expensive S1-S3 link (4 mJ) is left out", !mst.Edges.Any(e => e.PowerCost == 4));
    t.Check("MST route S2 -> S1 -> R1 -> G", string.Join(">", MeshRouting.PathToRoot(mst, "S2")) == "S2>S1>R1>G");

    var healed = MeshRouting.PrimMst(graph, "G", new HashSet<string> { "R1" });
    t.Check("after R1 fails, S2 re-routes S2 -> S1 -> S3 -> R2 -> G", string.Join(">", MeshRouting.PathToRoot(healed, "S2")) == "S2>S1>S3>R2>G");

    // Random dense graphs: Prim must always agree with Kruskal.
    var ok = true;
    for (var trial = 0; trial < 30 && ok; trial++)
    {
        var g = new MeshGraph();
        var count = rng.Next(5, 40);
        for (var i = 0; i < count; i++)
            g.AddNode(new MeshNode { Mac = $"N{i}", Name = $"N{i}", Kind = i == 0 ? MeshNodeKind.Gateway : MeshNodeKind.Sensor, Subnet = "x", X = rng.Next(0, 1000), Y = rng.Next(0, 640) });
        g.LinkByRadioRange();
        var prim = MeshRouting.PrimMst(g, "N0", none);
        var reachable = MeshRouting.BreadthFirst(g, "N0", none).Order.Count;
        ok = Math.Abs(prim.TotalCost - KruskalCost(g, none, "N0")) < 1e-6 && prim.Edges.Count == reachable - 1;
    }
    t.Check("30 random meshes: Prim == Kruskal and edges == reachable - 1", ok);
}

return t.Finish();

// Kruskal with union-find, restricted to the component containing 'root'.
static double KruskalCost(MeshGraph g, ISet<string> down, string root = "G")
{
    var component = MeshRouting.BreadthFirst(g, root, down).Order.Select(v => v.Mac).ToHashSet();
    var parent = component.ToDictionary(m => m, m => m);
    string Find(string x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
    double total = 0;
    foreach (var link in g.Links().Where(l => component.Contains(l.A) && component.Contains(l.B)).OrderBy(l => l.PowerCost))
    {
        var (a, b) = (Find(link.A), Find(link.B));
        if (a == b) continue;
        parent[a] = b;
        total += link.PowerCost;
    }
    return Math.Round(total, 2);
}

sealed class TestRunner
{
    private int _passed, _failed;

    public void Section(string name) => Console.WriteLine($"\n== {name}");

    public void Check(string name, bool ok)
    {
        if (ok) _passed++; else _failed++;
        Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
        Console.Write(ok ? "  PASS " : "  FAIL ");
        Console.ResetColor();
        Console.WriteLine(name);
    }

    public int Finish()
    {
        Console.WriteLine($"\n{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }
}
