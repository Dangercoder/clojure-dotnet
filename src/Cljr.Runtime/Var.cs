using System.Collections.Concurrent;

namespace Cljr;

/// <summary>
/// Var - mutable indirection to a value, allowing hot-reload of functions.
/// In dev mode, all function calls go through Var.Invoke() enabling
/// redefinition to affect all callers immediately.
/// </summary>
public sealed class Var : IDeref
{
    private static readonly ConcurrentDictionary<(string Ns, string Name), Var> _registry = new();

    /// <summary>The namespace this var belongs to</summary>
    public string Namespace { get; }

    /// <summary>The name of this var within its namespace</summary>
    public string Name { get; }

    /// <summary>The fully qualified name (ns/name)</summary>
    public string FullName => $"{Namespace}/{Name}";

    /// <summary>Whether this var holds a macro</summary>
    public bool IsMacro { get; set; }

    /// <summary>Whether this var is dynamic (supports thread-local binding)</summary>
    public bool IsDynamic { get; set; }

    /// <summary>Whether this var is private to its namespace</summary>
    public bool IsPrivate { get; set; }

    /// <summary>Documentation string for this var</summary>
    public string? Doc { get; set; }

    private volatile object? _root;

    private Var(string ns, string name)
    {
        Namespace = ns;
        Name = name;
    }

    /// <summary>
    /// Interns (creates or retrieves) a var in the global registry.
    /// Thread-safe - same var is always returned for same ns/name.
    /// </summary>
    public static Var Intern(string ns, string name) =>
        _registry.GetOrAdd((ns, name), key => new Var(key.Ns, key.Name));

    /// <summary>
    /// Finds a var without creating it. Returns null if not found.
    /// </summary>
    public static Var? Find(string ns, string name) =>
        _registry.TryGetValue((ns, name), out var v) ? v : null;

    /// <summary>
    /// Finds a var by fully qualified name (ns/name).
    /// </summary>
    public static Var? Find(string qualifiedName)
    {
        var parts = qualifiedName.Split('/');
        return parts.Length == 2 ? Find(parts[0], parts[1]) : null;
    }

    /// <summary>
    /// Sets the root binding of this var.
    /// This is what enables hot-reload - rebinding updates all callers.
    /// Returns this Var for chaining (def returns the Var).
    /// </summary>
    public Var BindRoot(object? value)
    {
        _root = value;
        return this;
    }

    /// <summary>
    /// Gets the root binding of this var.
    /// </summary>
    public object? GetRoot() => _root;

    /// <summary>
    /// Dereferences this var, returning its current value.
    /// </summary>
    public object? Deref() => _root;

    /// <summary>
    /// Returns true if this var has been bound to a value.
    /// </summary>
    public bool IsBound => _root is not null;

    /// <summary>
    /// Invokes this var as a function with the given arguments.
    /// This is the key to hot-reload - calls go through var indirection.
    /// FAST arity-specific overloads avoid array allocation.
    /// </summary>
    /// <summary>
    /// Check if a delegate is specifically a Func taking object?[] (multi-arity function).
    /// Due to delegate contravariance, we need to check the actual method signature.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsParamsFunc(Delegate fn)
    {
        var parameters = fn.Method.GetParameters();
        return parameters.Length == 1 && parameters[0].ParameterType == typeof(object?[]);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public object? Invoke()
    {
        var fn = _root ?? throw new InvalidOperationException($"Var {FullName} is unbound");
        if (fn is IFn ifn) return ifn.Invoke();
        // Multi-arity functions are Func<object?[], object?> - call directly without apply's sequence unpacking
        if (fn is Delegate d && IsParamsFunc(d)) return ((Func<object?[], object?>)d)(Array.Empty<object?>());
        return Core.apply(fn, Array.Empty<object?>());
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public object? Invoke(object? a)
    {
        var fn = _root ?? throw new InvalidOperationException($"Var {FullName} is unbound");
        if (fn is IFn ifn) return ifn.Invoke(a);
        // Multi-arity functions are Func<object?[], object?> - call directly without apply's sequence unpacking
        if (fn is Delegate d && IsParamsFunc(d)) return ((Func<object?[], object?>)d)(new[] { a });
        // Single-arg functions - invoke directly to avoid Core.apply's sequence unpacking
        if (fn is Func<object?, object?> f1) return f1(a);
        return Core.apply(fn, new[] { a });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public object? Invoke(object? a, object? b)
    {
        var fn = _root ?? throw new InvalidOperationException($"Var {FullName} is unbound");
        if (fn is IFn ifn) return ifn.Invoke(a, b);
        // Multi-arity functions are Func<object?[], object?> - call directly without apply's sequence unpacking
        if (fn is Delegate d && IsParamsFunc(d)) return ((Func<object?[], object?>)d)(new[] { a, b });
        // Two-arg functions - invoke directly to avoid Core.apply's sequence unpacking
        if (fn is Func<object?, object?, object?> f2) return f2(a, b);
        return Core.apply(fn, new[] { a, b });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public object? Invoke(object? a, object? b, object? c)
    {
        var fn = _root ?? throw new InvalidOperationException($"Var {FullName} is unbound");
        if (fn is IFn ifn) return ifn.Invoke(a, b, c);
        // Multi-arity functions are Func<object?[], object?> - call directly without apply's sequence unpacking
        if (fn is Delegate d && IsParamsFunc(d)) return ((Func<object?[], object?>)d)(new[] { a, b, c });
        // Three-arg functions - invoke directly to avoid Core.apply's sequence unpacking
        if (fn is Func<object?, object?, object?, object?> f3) return f3(a, b, c);
        return Core.apply(fn, new[] { a, b, c });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public object? Invoke(object? a, object? b, object? c, object? d)
    {
        var fn = _root ?? throw new InvalidOperationException($"Var {FullName} is unbound");
        if (fn is IFn ifn) return ifn.Invoke(a, b, c, d);
        // Multi-arity functions are Func<object?[], object?> - call directly without apply's sequence unpacking
        if (fn is Delegate del && IsParamsFunc(del)) return ((Func<object?[], object?>)del)(new[] { a, b, c, d });
        // Four-arg functions - invoke directly to avoid Core.apply's sequence unpacking
        if (fn is Func<object?, object?, object?, object?, object?> f4) return f4(a, b, c, d);
        return Core.apply(fn, new[] { a, b, c, d });
    }

    // Fallback for 5+ args
    public object? Invoke(object? a, object? b, object? c, object? d, params object?[] more)
    {
        var fn = _root ?? throw new InvalidOperationException($"Var {FullName} is unbound");
        var allArgs = new object?[4 + more.Length];
        allArgs[0] = a; allArgs[1] = b; allArgs[2] = c; allArgs[3] = d;
        Array.Copy(more, 0, allArgs, 4, more.Length);

        if (fn is IFn ifn) return ifn.Invoke(allArgs);
        // Multi-arity functions are Func<object?[], object?> - call directly without apply's sequence unpacking
        if (fn is Delegate del && IsParamsFunc(del)) return ((Func<object?[], object?>)del)(allArgs);
        return Core.apply(fn, allArgs);
    }

    /// <summary>
    /// Gets all vars in a namespace.
    /// </summary>
    public static IEnumerable<Var> GetNamespaceVars(string ns) =>
        _registry.Values.Where(v => v.Namespace == ns);

    /// <summary>
    /// Gets all namespace names that have vars.
    /// </summary>
    public static IEnumerable<string> GetAllNamespaces() =>
        _registry.Keys.Select(k => k.Ns).Distinct();

    /// <summary>
    /// Clears all vars in a namespace (used during reload).
    /// </summary>
    public static void ClearNamespace(string ns)
    {
        var toRemove = _registry.Keys.Where(k => k.Ns == ns).ToList();
        foreach (var key in toRemove)
            _registry.TryRemove(key, out _);
    }

    /// <summary>
    /// Clears all vars (used in tests).
    /// </summary>
    public static void ClearAll()
    {
        _registry.Clear();
    }

    public override string ToString() => $"#'({FullName})";

    public override int GetHashCode() => HashCode.Combine(Namespace, Name);

    public override bool Equals(object? obj) =>
        obj is Var other && Namespace == other.Namespace && Name == other.Name;
}
