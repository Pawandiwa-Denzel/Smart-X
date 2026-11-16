namespace SmartX.Api.Services.Topology;

// ===========================================================================
//  MinHeap<T> - a binary min-heap stored in an array (List<T>).
//
//  The smallest priority is always at index 0. For a node at index i its
//  children sit at 2i+1 and 2i+2 and its parent at (i-1)/2, so the tree needs
//  no pointers at all.
//
//      Push  O(log n)  add at the end, "sift up" while smaller than the parent
//      Pop   O(log n)  move the last item to the root, "sift down"
//      Peek  O(1)
//
//  Prim's algorithm uses it to always grab the cheapest radio link leaving the
//  part of the mesh that is already connected to the gateway.
// ===========================================================================
public sealed class MinHeap<T>
{
    private readonly List<(T Item, double Priority)> _items = new();

    public int Count => _items.Count;

    public void Push(T item, double priority)
    {
        _items.Add((item, priority));
        SiftUp(_items.Count - 1);
    }

    public bool TryPop(out T item, out double priority)
    {
        if (_items.Count == 0)
        {
            item = default!;
            priority = 0;
            return false;
        }

        (item, priority) = _items[0];
        var last = _items[^1];
        _items.RemoveAt(_items.Count - 1);
        if (_items.Count > 0)
        {
            _items[0] = last;
            SiftDown(0);
        }

        return true;
    }

    public (T Item, double Priority) Peek() =>
        _items.Count == 0 ? throw new InvalidOperationException("Heap is empty.") : _items[0];

    private void SiftUp(int i)
    {
        while (i > 0)
        {
            var parent = (i - 1) / 2;
            if (_items[i].Priority >= _items[parent].Priority) break;
            (_items[i], _items[parent]) = (_items[parent], _items[i]);
            i = parent;
        }
    }

    private void SiftDown(int i)
    {
        var n = _items.Count;
        while (true)
        {
            var left = 2 * i + 1;
            var right = left + 1;
            var smallest = i;

            if (left < n && _items[left].Priority < _items[smallest].Priority) smallest = left;
            if (right < n && _items[right].Priority < _items[smallest].Priority) smallest = right;
            if (smallest == i) return;

            (_items[i], _items[smallest]) = (_items[smallest], _items[i]);
            i = smallest;
        }
    }
}
