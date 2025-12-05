namespace Cljr;

/// <summary>
/// Clojure keyword - a self-evaluating, interned identifier prefixed with :.
/// Keywords are commonly used as map keys and enum-like values.
/// </summary>
public sealed class Keyword : IEquatable<Keyword>, IComparable<Keyword>
{
    private static readonly Dictionary<(string?, string), Keyword> _cache = new Dictionary<(string?, string), Keyword>();
    private static readonly object _cacheLock = new object();

    public string? Namespace { get; }
    public string Name { get; }
    private readonly int _hashCode;

    private Keyword(string? ns, string name)
    {
        Namespace = ns;
        Name = name ?? throw new ArgumentNullException(nameof(name));
        _hashCode = HashCode.Combine(ns, name) ^ unchecked((int)0x9e3779b9); // Different from Symbol
    }

    /// <summary>
    /// Interns a keyword with the given name (no namespace).
    /// </summary>
    public static Keyword Intern(string name) => Intern(null, name);

    /// <summary>
    /// Interns a keyword with the given namespace and name.
    /// </summary>
    public static Keyword Intern(string? ns, string name)
    {
        var key = (ns, name);
        lock (_cacheLock)
        {
            if (_cache.TryGetValue(key, out var existing))
                return existing;
            var kw = new Keyword(ns, name);
            _cache[key] = kw;
            return kw;
        }
    }

    /// <summary>
    /// Parses a keyword from a string like "foo" or "bar/baz" (without the :).
    /// </summary>
    public static Keyword Parse(string s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var slashIdx = s.IndexOf('/');
        if (slashIdx == -1 || s == "/")
            return Intern(s);

        var ns = s.Substring(0, slashIdx);
        var name = s.Substring(slashIdx + 1);
        return Intern(ns, name);
    }

    public bool Equals(Keyword? other)
    {
        // Keywords are interned, so reference equality is sufficient
        return ReferenceEquals(this, other);
    }

    public override bool Equals(object? obj) => Equals(obj as Keyword);

    public override int GetHashCode() => _hashCode;

    public int CompareTo(Keyword? other)
    {
        if (other is null) return 1;
        if (ReferenceEquals(this, other)) return 0;

        var nsCompare = string.Compare(Namespace, other.Namespace, StringComparison.Ordinal);
        if (nsCompare != 0) return nsCompare;

        return string.Compare(Name, other.Name, StringComparison.Ordinal);
    }

    public override string ToString() =>
        Namespace is null ? $":{Name}" : $":{Namespace}/{Name}";

    public static bool operator ==(Keyword? left, Keyword? right) =>
        ReferenceEquals(left, right);

    public static bool operator !=(Keyword? left, Keyword? right) =>
        !ReferenceEquals(left, right);
}
