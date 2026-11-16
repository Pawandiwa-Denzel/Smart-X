namespace SmartX.Api.Services.Topology;

public sealed record BfsVisit(string Mac, int Hop, string? Via);

public sealed record BfsResult(
    string Start,
    IReadOnlyList<BfsVisit> Order,                 // visit order, level by level
    IReadOnlyDictionary<string, string?> Parent,   // breadth-first tree (fewest hops back to Start)
    IReadOnlyList<string> DownNeighbours);         // offline radios the search bumped into

public sealed record MstResult(
    string Root,
    IReadOnlyDictionary<string, MeshLink> ParentLink,   // child MAC -> link to its parent in the tree
    IReadOnlyList<MeshLink> Edges,
    double TotalCost,
    IReadOnlyList<string> Unreachable,
    int HeapPushes);

// ===========================================================================
//  MeshRouting - the graph algorithms behind the Network Topology page.
//
//  * Breadth-First Search: explores the mesh one hop at a time from a gateway
//    or router using a Queue<T>. Every node is reached by the FEWEST hops, and
//    offline radios are skipped, so the search shows exactly which devices are
//    still connected and which ones were cut off.
//
//  * Prim's Minimum Spanning Tree: grows a tree from the central gateway,
//    always adding the cheapest link (lowest radio energy) that reaches a new
//    node. A MinHeap supplies that cheapest link in O(log E). The result
//    connects every reachable node with the least total transmit energy - the
//    power-efficient backbone each sensor uses to hop its packets home.
// ===========================================================================
public static class MeshRouting
{
    // BFS from 'start'. Nodes in 'down' are never entered. If 'subnet' is set,
    // the search stays inside that subnet (it won't walk out through the
    // central gateway into other zones).
    public static BfsResult BreadthFirst(MeshGraph graph, string start, ISet<string> down, string? subnet = null)
    {
        var order = new List<BfsVisit>();
        var parent = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var downSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!graph.TryGetNode(start, out _) || down.Contains(start))
            return new BfsResult(start, order, parent, downSeen.ToList());

        var queue = new Queue<(string Mac, int Hop)>();
        queue.Enqueue((start, 0));
        parent[start] = null;

        while (queue.TryDequeue(out var current))
        {
            order.Add(new BfsVisit(current.Mac, current.Hop, parent[current.Mac]));

            // Neighbours in a stable order (closest first) so the output is repeatable.
            foreach (var link in graph.Neighbours(current.Mac).OrderBy(l => l.Distance))
            {
                var next = link.Other(current.Mac);
                if (parent.ContainsKey(next)) continue;               // already visited

                if (down.Contains(next)) { downSeen.Add(next); continue; }

                if (subnet is not null && graph.TryGetNode(next, out var nn) && nn.Subnet != subnet)
                    continue;                                          // stay inside the subnet

                parent[next] = current.Mac;
                queue.Enqueue((next, current.Hop + 1));
            }
        }

        return new BfsResult(start, order, parent, downSeen.ToList());
    }

    // Prim's algorithm (lazy version) rooted at the gateway.
    public static MstResult PrimMst(MeshGraph graph, string root, ISet<string> down)
    {
        var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var parentLink = new Dictionary<string, MeshLink>(StringComparer.OrdinalIgnoreCase);
        var edges = new List<MeshLink>();
        var heap = new MinHeap<(string From, string To, MeshLink Link)>();
        double total = 0;
        var pushes = 0;

        void AddToTree(string mac)
        {
            inTree.Add(mac);
            foreach (var link in graph.Neighbours(mac))
            {
                var to = link.Other(mac);
                if (inTree.Contains(to) || down.Contains(to)) continue;
                heap.Push((mac, to, link), link.PowerCost);
                pushes++;
            }
        }

        if (graph.TryGetNode(root, out _) && !down.Contains(root))
        {
            AddToTree(root);

            while (heap.TryPop(out var candidate, out var cost))
            {
                if (inTree.Contains(candidate.To)) continue;   // stale entry - node already joined cheaper
                parentLink[candidate.To] = candidate.Link;
                edges.Add(candidate.Link);
                total += cost;
                AddToTree(candidate.To);
            }
        }

        var unreachable = graph.Nodes
            .Where(n => !inTree.Contains(n.Mac) && !down.Contains(n.Mac))
            .Select(n => n.Mac)
            .ToList();

        return new MstResult(root, parentLink, edges, Math.Round(total, 2), unreachable, pushes);
    }

    // Follow the MST parent links from a node up to the gateway.
    public static List<string> PathToRoot(MstResult mst, string mac)
    {
        var path = new List<string>();
        var current = mac;
        var guard = 0;
        while (guard++ < 10_000)
        {
            path.Add(current);
            if (string.Equals(current, mst.Root, StringComparison.OrdinalIgnoreCase)) return path;
            if (!mst.ParentLink.TryGetValue(current, out var link)) return new List<string>(); // no route
            current = link.Other(current);
        }

        return new List<string>();
    }

    // Fewest-hop path back to the BFS start, using the BFS parent map.
    public static List<string> PathFromBfs(BfsResult bfs, string mac)
    {
        if (!bfs.Parent.ContainsKey(mac)) return new List<string>();
        var path = new List<string>();
        string? current = mac;
        while (current is not null)
        {
            path.Add(current);
            current = bfs.Parent[current];
        }

        return path;
    }
}
