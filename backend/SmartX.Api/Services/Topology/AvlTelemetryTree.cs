using System.Diagnostics;
using SmartX.Api.Models;

namespace SmartX.Api.Services.Topology;

// Statistics returned with every range query so the dashboard (and the README)
// can show how little of the tree a query actually touches.
public sealed record TreeQueryStats(
    int TreeSize,
    int TreeHeight,
    int NodesVisited,
    int ResultCount,
    double ElapsedMicroseconds,
    long TotalRotations);

// ===========================================================================
//  AvlTelemetryTree - a self-balancing binary search tree of timestamped
//  readings for ONE sensor.
//
//  Key   = reading timestamp (UTC, unique per sensor)
//  Value = the reading
//
//  Why AVL and not a plain BST?  Telemetry arrives in timestamp order. A plain
//  BST fed sorted keys degenerates into a linked list (height = n), so every
//  query becomes O(n) again. The AVL rotations keep the height <= ~1.44 log2(n),
//  so with 30 000 readings the tree is only ~16-18 levels deep.
//
//  Range query (e.g. 14:00-16:00): walk down from the root and only descend
//  into a subtree if it can contain keys inside [from, to]. Cost is
//  O(log n + k) where k is the number of readings returned - we never iterate
//  through the whole history like a List<T> scan would.
//
//  Each node also stores the size of its subtree, so "how many readings fall in
//  this window?" is answered in O(log n) without collecting them.
// ===========================================================================
public sealed class AvlTelemetryTree
{
    private sealed class Node(DateTime key, double value)
    {
        public DateTime Key = key;
        public double Value = value;
        public Node? Left;
        public Node? Right;
        public int Height = 1;
        public int Size = 1;
    }

    private readonly object _lock = new();
    private Node? _root;
    private long _rotations;

    public string Metric { get; }

    public AvlTelemetryTree(string metric) => Metric = metric;

    public int Count { get { lock (_lock) return SizeOf(_root); } }
    public int Height { get { lock (_lock) return HeightOf(_root); } }
    public long Rotations { get { lock (_lock) return _rotations; } }

    // ------------------------------------------------------------------ insert

    // Inserts a reading. Timestamps must be unique per sensor, so on a
    // collision the key is nudged forward one tick (no reading is ever lost).
    // Returns the key actually stored.
    public DateTime Insert(DateTime timestamp, double value)
    {
        lock (_lock)
        {
            var key = timestamp;
            while (Contains(_root, key)) key = key.AddTicks(1);
            _root = Insert(_root, key, value);
            return key;
        }
    }

    private Node Insert(Node? node, DateTime key, double value)
    {
        if (node is null) return new Node(key, value);

        if (key < node.Key) node.Left = Insert(node.Left, key, value);
        else node.Right = Insert(node.Right, key, value);

        return Rebalance(node);
    }

    // ------------------------------------------------------------- balancing

    private Node Rebalance(Node node)
    {
        Update(node);
        var balance = BalanceOf(node);

        if (balance > 1)                       // left heavy
        {
            if (BalanceOf(node.Left!) < 0)     // left-right case
                node.Left = RotateLeft(node.Left!);
            return RotateRight(node);          // left-left case
        }

        if (balance < -1)                      // right heavy
        {
            if (BalanceOf(node.Right!) > 0)    // right-left case
                node.Right = RotateRight(node.Right!);
            return RotateLeft(node);           // right-right case (sequential timestamps land here)
        }

        return node;
    }

    private Node RotateRight(Node y)
    {
        var x = y.Left!;
        y.Left = x.Right;
        x.Right = y;
        Update(y);
        Update(x);
        _rotations++;
        return x;
    }

    private Node RotateLeft(Node x)
    {
        var y = x.Right!;
        x.Right = y.Left;
        y.Left = x;
        Update(x);
        Update(y);
        _rotations++;
        return y;
    }

    private static void Update(Node n)
    {
        n.Height = 1 + Math.Max(HeightOf(n.Left), HeightOf(n.Right));
        n.Size = 1 + SizeOf(n.Left) + SizeOf(n.Right);
    }

    private static int HeightOf(Node? n) => n?.Height ?? 0;
    private static int SizeOf(Node? n) => n?.Size ?? 0;
    private static int BalanceOf(Node n) => HeightOf(n.Left) - HeightOf(n.Right);

    private static bool Contains(Node? n, DateTime key)
    {
        while (n is not null)
        {
            if (key == n.Key) return true;
            n = key < n.Key ? n.Left : n.Right;
        }

        return false;
    }

    // ----------------------------------------------------------- range query

    // All readings with from <= timestamp <= to, in time order.
    public List<HistoryPoint> RangeQuery(DateTime from, DateTime to, int limit, out TreeQueryStats stats)
    {
        var sw = Stopwatch.StartNew();
        var result = new List<HistoryPoint>();
        var visited = 0;

        lock (_lock)
        {
            if (from <= to) Collect(_root, from, to, limit, result, ref visited);
            sw.Stop();
            stats = new TreeQueryStats(SizeOf(_root), HeightOf(_root), visited, result.Count,
                Math.Round(sw.Elapsed.TotalMicroseconds, 1), _rotations);
        }

        return result;
    }

    // In-order traversal that PRUNES: the left subtree is only entered if this
    // node's key is after 'from' (smaller keys might still be in range), and the
    // right subtree only if this key is before 'to'.
    private static void Collect(Node? node, DateTime from, DateTime to, int limit, List<HistoryPoint> output, ref int visited)
    {
        if (node is null || output.Count >= limit) return;
        visited++;

        if (from < node.Key) Collect(node.Left, from, to, limit, output, ref visited);

        if (output.Count < limit && node.Key >= from && node.Key <= to)
            output.Add(new HistoryPoint(node.Key, node.Value));

        if (node.Key < to) Collect(node.Right, from, to, limit, output, ref visited);
    }

    // Number of readings in [from, to] in O(log n), using subtree sizes:
    //   count = rank(<= to) - rank(< from)
    public int CountInRange(DateTime from, DateTime to)
    {
        lock (_lock)
        {
            if (from > to) return 0;
            return CountLessOrEqual(_root, to) - CountLess(_root, from);
        }
    }

    private static int CountLess(Node? n, DateTime key)
    {
        var count = 0;
        while (n is not null)
        {
            if (n.Key < key) { count += SizeOf(n.Left) + 1; n = n.Right; }
            else n = n.Left;
        }

        return count;
    }

    private static int CountLessOrEqual(Node? n, DateTime key)
    {
        var count = 0;
        while (n is not null)
        {
            if (n.Key <= key) { count += SizeOf(n.Left) + 1; n = n.Right; }
            else n = n.Left;
        }

        return count;
    }

    // ------------------------------------------------------------ min / max

    public (DateTime? First, DateTime? Last) Bounds()
    {
        lock (_lock)
        {
            if (_root is null) return (null, null);
            var min = _root;
            while (min.Left is not null) min = min.Left;
            var max = _root;
            while (max.Right is not null) max = max.Right;
            return (min.Key, max.Key);
        }
    }

    // Snapshot of every reading in time order - used ONLY by the benchmark to
    // build the "unorganised list" that the tree is compared against.
    public List<HistoryPoint> ToList()
    {
        lock (_lock)
        {
            var list = new List<HistoryPoint>(SizeOf(_root));
            InOrder(_root, list);
            return list;
        }
    }

    private static void InOrder(Node? n, List<HistoryPoint> list)
    {
        if (n is null) return;
        InOrder(n.Left, list);
        list.Add(new HistoryPoint(n.Key, n.Value));
        InOrder(n.Right, list);
    }

    // Self-check used by tests/benchmark: every node is balanced and ordered.
    public bool IsValidAvl()
    {
        lock (_lock) return Check(_root, DateTime.MinValue, DateTime.MaxValue);
    }

    private static bool Check(Node? n, DateTime min, DateTime max)
    {
        if (n is null) return true;
        if (n.Key < min || n.Key > max) return false;
        if (Math.Abs(BalanceOf(n)) > 1) return false;
        return Check(n.Left, min, n.Key) && Check(n.Right, n.Key, max);
    }
}
