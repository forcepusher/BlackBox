namespace Sloperator.Core;

/// <summary>
/// A map that keeps only the entries set last, at most <paramref name="max"/>: for what is
/// remembered for the process's whole life (the ComfyUI jobs queued, the documents written), which
/// only grew. Setting a key again makes it the newest.
/// </summary>
internal sealed class RecentMap<TKey, TValue>(int max, IEqualityComparer<TKey>? comparer = null) where TKey : notnull
{
    private readonly Dictionary<TKey, (TValue Value, long At)> _items = new(comparer);
    private long _clock;

    public int Count
    {
        get { lock (_items) return _items.Count; }
    }

    public void Set(TKey key, TValue value)
    {
        lock (_items)
        {
            _items[key] = (value, ++_clock);
            while (_items.Count > max)
                _items.Remove(_items.MinBy(kv => kv.Value.At).Key);
        }
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_items)
        {
            var found = _items.TryGetValue(key, out var item);
            value = item.Value;
            return found;
        }
    }

    public void Remove(TKey key)
    {
        lock (_items)
            _items.Remove(key);
    }
}
