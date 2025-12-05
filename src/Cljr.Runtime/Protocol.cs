using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using Cljr.Collections;

namespace Cljr;

/// <summary>
/// Clojure protocol implementation.
/// Provides type-based polymorphic dispatch with inheritance support.
///
/// Uses FrozenDictionary for high-performance dispatch after protocol is stable.
/// Falls back to ConcurrentDictionary during extension.
/// </summary>
public sealed class Protocol
{
    private readonly Symbol _name;
    private readonly string _ns;
    private readonly IReadOnlyList<Symbol> _methods;
    private readonly ConcurrentDictionary<Type, MethodImplCache> _impls = new();
    private volatile FrozenDictionary<Type, MethodImplCache>? _frozenImpls;

    public Protocol(Symbol name, string ns, IReadOnlyList<Symbol> methods)
    {
        _name = name;
        _ns = ns;
        _methods = methods;
    }

    public Symbol Name => _name;
    public string Namespace => _ns;
    public IReadOnlyList<Symbol> Methods => _methods;

    /// <summary>
    /// Extends this protocol to the given type with method implementations.
    /// </summary>
    public void Extend(Type type, IReadOnlyDictionary<Symbol, Delegate> methodImpls)
    {
        var cache = new MethodImplCache();
        foreach (var (method, impl) in methodImpls)
            cache.Add(method, impl);
        _impls[type] = cache;
        _frozenImpls = null; // Invalidate frozen cache
    }

    /// <summary>
    /// Gets the method implementation for the given type.
    /// Uses cached lookup with type hierarchy support.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Delegate? GetMethod(Type type, Symbol methodName)
    {
        // Fast path: exact type match in frozen cache
        var frozen = _frozenImpls;
        if (frozen != null && frozen.TryGetValue(type, out var cache))
            return cache.Get(methodName);

        // Slow path: search hierarchy
        return GetMethodSlow(type, methodName);
    }

    private Delegate? GetMethodSlow(Type type, Symbol methodName)
    {
        // Check exact type
        if (_impls.TryGetValue(type, out var cache))
            return cache.Get(methodName);

        // Check interfaces
        foreach (var iface in type.GetInterfaces())
        {
            if (_impls.TryGetValue(iface, out cache))
                return cache.Get(methodName);
        }

        // Check base types
        var baseType = type.BaseType;
        while (baseType != null)
        {
            if (_impls.TryGetValue(baseType, out cache))
                return cache.Get(methodName);
            baseType = baseType.BaseType;
        }

        // Check for Object fallback
        if (_impls.TryGetValue(typeof(object), out cache))
            return cache.Get(methodName);

        return null;
    }

    /// <summary>
    /// Returns true if the type satisfies this protocol.
    /// </summary>
    public bool Satisfies(Type type)
    {
        if (_impls.ContainsKey(type)) return true;

        foreach (var iface in type.GetInterfaces())
            if (_impls.ContainsKey(iface)) return true;

        var baseType = type.BaseType;
        while (baseType != null)
        {
            if (_impls.ContainsKey(baseType)) return true;
            baseType = baseType.BaseType;
        }

        return _impls.ContainsKey(typeof(object));
    }

    /// <summary>
    /// Returns all types that have been extended to this protocol.
    /// </summary>
    public IEnumerable<Type> Extenders() => _impls.Keys;

    /// <summary>
    /// Freezes the protocol for maximum dispatch performance.
    /// Call after all extensions are complete.
    /// </summary>
    public void Freeze()
    {
        _frozenImpls = _impls.ToFrozenDictionary();
    }

    public override string ToString() => $"#Protocol[{_ns}/{_name}]";
}

/// <summary>
/// Fast cache for method implementations within a protocol extension.
/// </summary>
internal sealed class MethodImplCache
{
    private readonly ConcurrentDictionary<Symbol, Delegate> _methods = new();
    private volatile FrozenDictionary<Symbol, Delegate>? _frozen;

    public void Add(Symbol method, Delegate impl)
    {
        _methods[method] = impl;
        _frozen = null;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Delegate? Get(Symbol method)
    {
        var frozen = _frozen;
        if (frozen != null)
            return frozen.GetValueOrDefault(method);

        return _methods.GetValueOrDefault(method);
    }

    public void Freeze() => _frozen = _methods.ToFrozenDictionary();
}

/// <summary>
/// Registry for all protocols in the system.
/// </summary>
public static class ProtocolRegistry
{
    private static readonly ConcurrentDictionary<Symbol, Protocol> _protocols = new();

    public static void Register(Symbol name, Protocol protocol)
    {
        _protocols[name] = protocol;
    }

    public static Protocol? Get(Symbol name)
    {
        return _protocols.GetValueOrDefault(name);
    }

    public static IEnumerable<Protocol> All => _protocols.Values;
}
