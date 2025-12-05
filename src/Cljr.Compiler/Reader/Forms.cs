using System.Collections;

namespace Cljr.Compiler.Reader;

/// <summary>
/// Represents a Clojure list: (a b c)
/// </summary>
public sealed class PersistentList : IReadOnlyList<object?>
{
    public static readonly PersistentList Empty = new([]);

    private readonly List<object?> _items;
    public IReadOnlyDictionary<object, object>? Meta { get; init; }

    public PersistentList(IEnumerable<object?> items)
    {
        _items = items.ToList();
    }

    public object? this[int index] => _items[index];
    public int Count => _items.Count;

    public PersistentList WithMeta(IReadOnlyDictionary<object, object>? meta) =>
        new(_items) { Meta = meta };

    public IEnumerator<object?> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        $"({string.Join(" ", _items.Select(FormatItem))})";

    private static string FormatItem(object? item) => item switch
    {
        null => "nil",
        string s => $"\"{s}\"",
        _ => item.ToString() ?? "nil"
    };
}

/// <summary>
/// Represents a Clojure vector: [a b c]
/// </summary>
public sealed class PersistentVector : IReadOnlyList<object?>
{
    public static readonly PersistentVector Empty = new([]);

    private readonly List<object?> _items;
    public IReadOnlyDictionary<object, object>? Meta { get; init; }

    public PersistentVector(IEnumerable<object?> items)
    {
        _items = items.ToList();
    }

    public object? this[int index] => _items[index];
    public int Count => _items.Count;

    public PersistentVector WithMeta(IReadOnlyDictionary<object, object>? meta) =>
        new(_items) { Meta = meta };

    public IEnumerator<object?> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString() =>
        $"[{string.Join(" ", _items.Select(FormatItem))}]";

    private static string FormatItem(object? item) => item switch
    {
        null => "nil",
        string s => $"\"{s}\"",
        _ => item.ToString() ?? "nil"
    };
}

/// <summary>
/// Represents a Clojure map: {:a 1 :b 2}
/// </summary>
public sealed class PersistentMap : IReadOnlyDictionary<object, object?>
{
    public static readonly PersistentMap Empty = new(new Dictionary<object, object?>());

    private readonly Dictionary<object, object?> _items;
    public IReadOnlyDictionary<object, object>? Meta { get; init; }

    public PersistentMap(IEnumerable<KeyValuePair<object, object?>> items)
    {
        _items = new Dictionary<object, object?>(MapKeyComparer.Instance);
        foreach (var kv in items)
            _items[kv.Key] = kv.Value;
    }

    public object? this[object key] => _items[key];
    public IEnumerable<object> Keys => _items.Keys;
    public IEnumerable<object?> Values => _items.Values;
    public int Count => _items.Count;

    public bool ContainsKey(object key) => _items.ContainsKey(key);
    public bool TryGetValue(object key, out object? value) => _items.TryGetValue(key, out value);

    public PersistentMap WithMeta(IReadOnlyDictionary<object, object>? meta) =>
        new(_items) { Meta = meta };

    public IEnumerator<KeyValuePair<object, object?>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public override string ToString()
    {
        var pairs = _items.Select(kv => $"{FormatItem(kv.Key)} {FormatItem(kv.Value)}");
        return $"{{{string.Join(" ", pairs)}}}";
    }

    private static string FormatItem(object? item) => item switch
    {
        null => "nil",
        string s => $"\"{s}\"",
        _ => item.ToString() ?? "nil"
    };
}

/// <summary>
/// Represents a Clojure set: #{a b c}
/// </summary>
public sealed class PersistentSet : IReadOnlySet<object?>
{
    public static readonly PersistentSet Empty = new([]);

    private readonly HashSet<object?> _items;
    public IReadOnlyDictionary<object, object>? Meta { get; init; }

    public PersistentSet(IEnumerable<object?> items)
    {
        _items = new HashSet<object?>(items, SetItemComparer.Instance);
    }

    public int Count => _items.Count;

    public bool Contains(object? item) => _items.Contains(item);

    public PersistentSet WithMeta(IReadOnlyDictionary<object, object>? meta) =>
        new(_items) { Meta = meta };

    public IEnumerator<object?> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    // IReadOnlySet methods
    public bool IsProperSubsetOf(IEnumerable<object?> other) => _items.IsProperSubsetOf(other);
    public bool IsProperSupersetOf(IEnumerable<object?> other) => _items.IsProperSupersetOf(other);
    public bool IsSubsetOf(IEnumerable<object?> other) => _items.IsSubsetOf(other);
    public bool IsSupersetOf(IEnumerable<object?> other) => _items.IsSupersetOf(other);
    public bool Overlaps(IEnumerable<object?> other) => _items.Overlaps(other);
    public bool SetEquals(IEnumerable<object?> other) => _items.SetEquals(other);

    public override string ToString() =>
        $"#{{{string.Join(" ", _items.Select(FormatItem))}}}";

    private static string FormatItem(object? item) => item switch
    {
        null => "nil",
        string s => $"\"{s}\"",
        _ => item.ToString() ?? "nil"
    };
}

/// <summary>
/// Comparer for map keys using Clojure equality semantics
/// </summary>
internal sealed class MapKeyComparer : IEqualityComparer<object>
{
    public static readonly MapKeyComparer Instance = new();

#if NETSTANDARD2_0
    public new bool Equals(object? x, object? y) => PolyfillExtensions.CljEquals(x, y);
#else
    public new bool Equals(object? x, object? y) => Core.Equals(x, y);
#endif

    public int GetHashCode(object obj) => obj?.GetHashCode() ?? 0;
}

/// <summary>
/// Comparer for set items using Clojure equality semantics
/// </summary>
internal sealed class SetItemComparer : IEqualityComparer<object?>
{
    public static readonly SetItemComparer Instance = new();

#if NETSTANDARD2_0
    public new bool Equals(object? x, object? y) => PolyfillExtensions.CljEquals(x, y);
#else
    public new bool Equals(object? x, object? y) => Core.Equals(x, y);
#endif

    public int GetHashCode(object? obj) => obj?.GetHashCode() ?? 0;
}
