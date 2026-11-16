namespace SmartX.Api.Services.Topology;

public enum MeshNodeKind
{
    Gateway,
    Router,
    Sensor,
    Actuator,
}

// A vertex: one physical radio in the mesh (the central gateway, a mesh router
// or an ESP32 sensor/actuator).
public sealed class MeshNode
{
    public required string Mac { get; init; }
    public required string Name { get; set; }
    public required MeshNodeKind Kind { get; init; }
    public required string Subnet { get; set; }
    public double X { get; set; }
    public double Y { get; set; }

    // Radio reach in canvas units. Gateways/routers carry high-gain antennas.
    public double Range => Kind switch
    {
        MeshNodeKind.Gateway => 320,
        MeshNodeKind.Router => 300,
        _ => 240,
    };
}

// An edge: a wireless link two radios can both hear. The weight is the energy
// (millijoules) needed to push one telemetry packet across it - radio power
// grows with the square of distance (free-space path loss), so long hops are
// expensive and the MST prefers chains of short hops.
public sealed record MeshLink(string A, string B, double Distance, int Rssi, double PowerCost)
{
    public string Other(string mac) => string.Equals(mac, A, StringComparison.OrdinalIgnoreCase) ? B : A;
}

// ===========================================================================
//  MeshGraph - weighted, undirected graph stored as an ADJACENCY LIST:
//
//      Dictionary<MAC, MeshNode>                         vertices (O(1) lookup)
//      Dictionary<MAC, Dictionary<MAC, MeshLink>>        neighbours of each vertex
//
//  An adjacency list suits a mesh because each radio only hears a handful of
//  neighbours (sparse graph): memory is O(V + E) instead of the O(V^2) of an
//  adjacency matrix, and "who can this node talk to?" is a single lookup.
// ===========================================================================
public sealed class MeshGraph
{
    private readonly Dictionary<string, MeshNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, MeshLink>> _adjacency = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<MeshNode> Nodes => _nodes.Values;
    public int VertexCount => _nodes.Count;
    public int EdgeCount => _adjacency.Values.Sum(n => n.Count) / 2;

    public void AddNode(MeshNode node)
    {
        _nodes[node.Mac] = node;
        if (!_adjacency.ContainsKey(node.Mac))
            _adjacency[node.Mac] = new Dictionary<string, MeshLink>(StringComparer.OrdinalIgnoreCase);
    }

    public bool TryGetNode(string mac, out MeshNode node) => _nodes.TryGetValue(mac, out node!);

    public void AddLink(MeshLink link)
    {
        if (!_nodes.ContainsKey(link.A) || !_nodes.ContainsKey(link.B) || link.A.Equals(link.B, StringComparison.OrdinalIgnoreCase))
            return;
        _adjacency[link.A][link.B] = link;
        _adjacency[link.B][link.A] = link;
    }

    public IEnumerable<MeshLink> Neighbours(string mac) =>
        _adjacency.TryGetValue(mac, out var n) ? n.Values : Enumerable.Empty<MeshLink>();

    public IEnumerable<MeshLink> Links() =>
        _adjacency.SelectMany(kv => kv.Value.Values.Where(l => string.Equals(l.A, kv.Key, StringComparison.OrdinalIgnoreCase)));

    public void Clear()
    {
        _nodes.Clear();
        _adjacency.Clear();
    }

    // Connect every pair of radios that are within each other's reach. Link
    // quality (RSSI) and energy cost are derived from the distance.
    public void LinkByRadioRange()
    {
        var list = _nodes.Values.ToList();
        for (var i = 0; i < list.Count; i++)
        {
            for (var j = i + 1; j < list.Count; j++)
            {
                var a = list[i];
                var b = list[j];
                var d = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
                if (d > (a.Range + b.Range) / 2) continue;

                // Log-distance path loss: -40 dBm at 10 units, -20 dB per decade.
                var rssi = (int)Math.Round(-40 - 20 * Math.Log10(Math.Max(d, 10) / 10));
                var cost = Math.Round(1 + 4 * Math.Pow(d / 200, 2), 2);
                AddLink(new MeshLink(a.Mac, b.Mac, Math.Round(d, 1), rssi, cost));
            }
        }
    }
}
