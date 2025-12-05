using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Cljr.Collections;

namespace Cljr;

/// <summary>
/// Clojure multimethod implementation.
/// Provides runtime polymorphism based on arbitrary dispatch functions.
///
/// Unlike protocols (type-based), multimethods can dispatch on any value
/// computed from the arguments. Supports isa? hierarchy and preferences.
/// </summary>
public sealed class MultiFn : IFn
{
    private readonly Symbol _name;
    private readonly Func<object?[], object?> _dispatchFn;
    private readonly object? _defaultDispatchVal;
    private readonly ConcurrentDictionary<object, Func<object?[], object?>> _methodTable = new(ObjectEqualityComparer.Instance);
    private readonly ConcurrentDictionary<object, HashSet<object>> _preferTable = new(ObjectEqualityComparer.Instance);
    private volatile MethodCache? _cache;

    public MultiFn(Symbol name, Func<object?[], object?> dispatchFn, object? defaultDispatchVal = null)
    {
        _name = name;
        _dispatchFn = dispatchFn;
        _defaultDispatchVal = defaultDispatchVal ?? Keyword.Intern("default");
    }

    public Symbol Name => _name;

    /// <summary>
    /// Adds a method for the given dispatch value.
    /// </summary>
    public void AddMethod(object dispatchVal, Func<object?[], object?> method)
    {
        _methodTable[dispatchVal] = method;
        _cache = null; // Invalidate cache
    }

    /// <summary>
    /// Removes the method for the given dispatch value.
    /// </summary>
    public void RemoveMethod(object dispatchVal)
    {
        _methodTable.TryRemove(dispatchVal, out _);
        _cache = null;
    }

    /// <summary>
    /// Prefers one dispatch value over another when both match.
    /// </summary>
    public void PreferMethod(object preferred, object other)
    {
        var prefs = _preferTable.GetOrAdd(preferred, _ => new HashSet<object>(ObjectEqualityComparer.Instance));
        lock (prefs)
        {
            prefs.Add(other);
        }
        _cache = null;
    }

    /// <summary>
    /// Returns the current method table as a map.
    /// </summary>
    public IPersistentMap GetMethodTable()
    {
        var result = PersistentHashMap.Empty;
        foreach (var (k, v) in _methodTable)
            result = (PersistentHashMap)result.Assoc(k, v);
        return result;
    }

    /// <summary>
    /// Returns the preference table as a map.
    /// </summary>
    public IPersistentMap GetPreferTable()
    {
        var result = PersistentHashMap.Empty;
        foreach (var (k, v) in _preferTable)
        {
            var set = PersistentHashSet.Empty;
            lock (v) { foreach (var item in v) set = (PersistentHashSet)set.Conj(item); }
            result = (PersistentHashMap)result.Assoc(k, set);
        }
        return result;
    }

    /// <summary>
    /// Invokes the multimethod with the given arguments.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Invoke(params object?[] args)
    {
        var dispatchVal = _dispatchFn(args);
        var method = FindMethod(dispatchVal);
        if (method == null)
            throw new InvalidOperationException($"No method in multimethod '{_name}' for dispatch value: {dispatchVal}");
        return method(args);
    }

    /// <summary>
    /// Finds the best matching method for the dispatch value.
    /// Handles isa? hierarchy and preferences.
    /// </summary>
    private Func<object?[], object?>? FindMethod(object? dispatchVal)
    {
        if (dispatchVal == null)
            return _methodTable.GetValueOrDefault(_defaultDispatchVal!);

        // Check cache first
        var cache = _cache;
        if (cache != null && cache.TryGet(dispatchVal, out var cached))
            return cached;

        // Exact match
        if (_methodTable.TryGetValue(dispatchVal, out var method))
        {
            CacheMethod(dispatchVal, method);
            return method;
        }

        // Find all matching methods via isa?
        var candidates = new List<(object key, Func<object?[], object?> method)>();
        foreach (var (key, m) in _methodTable)
        {
            if (IsA(dispatchVal, key))
                candidates.Add((key, m));
        }

        if (candidates.Count == 0)
        {
            // Try default
            method = _methodTable.GetValueOrDefault(_defaultDispatchVal!);
            if (method != null) CacheMethod(dispatchVal, method);
            return method;
        }

        if (candidates.Count == 1)
        {
            CacheMethod(dispatchVal, candidates[0].method);
            return candidates[0].method;
        }

        // Multiple matches - use preferences to resolve
        var best = ResolvePref(candidates);
        if (best != null)
        {
            CacheMethod(dispatchVal, best);
            return best;
        }

        throw new InvalidOperationException(
            $"Multiple methods in multimethod '{_name}' match dispatch value {dispatchVal}, " +
            $"and none is preferred");
    }

    private Func<object?[], object?>? ResolvePref(List<(object key, Func<object?[], object?> method)> candidates)
    {
        // Find a candidate that is preferred over all others
        foreach (var (key, method) in candidates)
        {
            bool isPreferred = true;
            foreach (var (otherKey, _) in candidates)
            {
                if (ReferenceEquals(key, otherKey)) continue;
                if (!IsPreferred(key, otherKey))
                {
                    isPreferred = false;
                    break;
                }
            }
            if (isPreferred) return method;
        }
        return null;
    }

    private bool IsPreferred(object x, object y)
    {
        if (_preferTable.TryGetValue(x, out var prefs))
        {
            lock (prefs)
            {
                if (prefs.Contains(y)) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Checks if x isa y (type hierarchy or value equality).
    /// </summary>
    private static bool IsA(object? x, object? y)
    {
        if (Core.Equals(x, y)) return true;

        // Type-based isa?
        if (x is Type tx && y is Type ty)
            return ty.IsAssignableFrom(tx);

        // Check if x's type is y
        if (y is Type t && x != null)
            return t.IsInstanceOfType(x);

        // Could add custom hierarchy here
        return false;
    }

    private void CacheMethod(object dispatchVal, Func<object?[], object?> method)
    {
        var newCache = new MethodCache(_cache);
        newCache.Add(dispatchVal, method);
        _cache = newCache;
    }

    public override string ToString() => $"#MultiFn[{_name}]";

    #region IFn Implementation

    public object? Invoke() => Invoke(Array.Empty<object?>());
    public object? Invoke(object? a) => Invoke([a]);
    public object? Invoke(object? a, object? b) => Invoke([a, b]);
    public object? Invoke(object? a, object? b, object? c) => Invoke([a, b, c]);
    public object? Invoke(object? a, object? b, object? c, object? d) => Invoke([a, b, c, d]);

    #endregion

    #region MethodCache

    private sealed class MethodCache
    {
        private readonly ConcurrentDictionary<object, Func<object?[], object?>> _cache = new(ObjectEqualityComparer.Instance);

        public MethodCache(MethodCache? previous)
        {
            if (previous != null)
                foreach (var (k, v) in previous._cache)
                    _cache[k] = v;
        }

        public void Add(object key, Func<object?[], object?> method) => _cache[key] = method;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGet(object key, out Func<object?[], object?>? method) =>
            _cache.TryGetValue(key, out method);
    }

    #endregion
}

/// <summary>
/// Uses Clojure equality for dictionary keys.
/// </summary>
internal sealed class ObjectEqualityComparer : IEqualityComparer<object>
{
    public static readonly ObjectEqualityComparer Instance = new();

    public new bool Equals(object? x, object? y) => Core.Equals(x, y);

    public int GetHashCode(object obj) => obj?.GetHashCode() ?? 0;
}

/// <summary>
/// Interface for invocable objects (functions).
/// </summary>
public interface IFn
{
    object? Invoke(params object?[] args);
    object? Invoke();
    object? Invoke(object? a);
    object? Invoke(object? a, object? b);
    object? Invoke(object? a, object? b, object? c);
    object? Invoke(object? a, object? b, object? c, object? d);
}
