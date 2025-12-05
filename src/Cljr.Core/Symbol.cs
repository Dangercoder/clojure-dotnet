namespace Cljr;

/// <summary>
/// Clojure symbol - an identifier that can be namespace-qualified.
/// Symbols can optionally have metadata (for type hints, etc).
/// Non-meta symbols are interned for fast equality checks.
/// </summary>
public sealed class Symbol : IEquatable<Symbol>, IComparable<Symbol>
{
    private static readonly Dictionary<(string?, string), Symbol> _cache = new Dictionary<(string?, string), Symbol>();
    private static readonly object _cacheLock = new object();

    public string? Namespace { get; }
    public string Name { get; }

    /// <summary>
    /// Metadata map (type hints, documentation, etc). May be null.
    /// </summary>
    public IReadOnlyDictionary<object, object>? Meta { get; }

    private readonly int _hashCode;

    private Symbol(string? ns, string name, IReadOnlyDictionary<object, object>? meta = null)
    {
        Namespace = ns;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Meta = meta;
        _hashCode = HashCode.Combine(ns, name);
    }

    /// <summary>
    /// Interns a symbol with the given name (no namespace).
    /// </summary>
    public static Symbol Intern(string name) => Intern(null, name);

    /// <summary>
    /// Interns a symbol with the given namespace and name.
    /// </summary>
    public static Symbol Intern(string? ns, string name)
    {
        var key = (ns, name);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var existing))
                return existing;
            var sym = new Symbol(ns, name);
            _cache[key] = sym;
            return sym;
        }
    }

    /// <summary>
    /// Parses a symbol from a string like "foo" or "bar/baz".
    /// </summary>
    public static Symbol Parse(string s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var slashIdx = s.IndexOf('/');
        if (slashIdx == -1 || s == "/")
            return Intern(s);

        var ns = s.Substring(0, slashIdx);
        var name = s.Substring(slashIdx + 1);
        return Intern(ns, name);
    }

    /// <summary>
    /// Creates a new symbol with the given metadata.
    /// Symbols with metadata are NOT interned.
    /// </summary>
    public Symbol WithMeta(IReadOnlyDictionary<object, object>? meta)
    {
        if (meta == null && Meta == null) return this;
        if (meta == Meta) return this;
        return new Symbol(Namespace, Name, meta);
    }

    public bool Equals(Symbol? other)
    {
        if (ReferenceEquals(this, other)) return true;
        if (other is null) return false;
        return Namespace == other.Namespace && Name == other.Name;
    }

    public override bool Equals(object? obj) => Equals(obj as Symbol);

    public override int GetHashCode() => _hashCode;

    public int CompareTo(Symbol? other)
    {
        if (other is null) return 1;

        // Compare namespaces first
        var nsCompare = string.Compare(Namespace, other.Namespace, StringComparison.Ordinal);
        if (nsCompare != 0) return nsCompare;

        return string.Compare(Name, other.Name, StringComparison.Ordinal);
    }

    public override string ToString() =>
        Namespace is null ? Name : $"{Namespace}/{Name}";

    public static bool operator ==(Symbol? left, Symbol? right) =>
        ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

    public static bool operator !=(Symbol? left, Symbol? right) =>
        !(left == right);
}
