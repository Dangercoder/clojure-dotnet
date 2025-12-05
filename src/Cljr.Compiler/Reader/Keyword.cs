using System.Runtime.CompilerServices;

namespace Cljr.Compiler.Reader;

/// <summary>
/// Represents a Clojure keyword (e.g., :foo, :bar/baz)
/// Keywords are interned for identity comparison.
/// </summary>
public sealed class Keyword : IEquatable<Keyword>
{
    private static readonly Dictionary<(string?, string), Keyword> _interned = new();
    private static readonly object _lock = new();

    public string? Namespace { get; }
    public string Name { get; }

    private Keyword(string? ns, string name)
    {
        Namespace = ns;
        Name = name;
    }

    /// <summary>
    /// Interns a keyword, returning the canonical instance
    /// </summary>
    public static Keyword Intern(string name) => Intern(null, name);

    /// <summary>
    /// Interns a keyword with namespace, returning the canonical instance
    /// </summary>
    public static Keyword Intern(string? ns, string name)
    {
        var key = (ns, name);
        lock (_lock)
        {
            if (!_interned.TryGetValue(key, out var kw))
            {
                kw = new Keyword(ns, name);
                _interned[key] = kw;
            }
            return kw;
        }
    }

    /// <summary>
    /// Parse a keyword from a string like "foo" or "bar/baz" (without the leading :)
    /// </summary>
    public static Keyword Parse(string s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var slashIdx = s.IndexOf('/');
        if (slashIdx == -1 || s == "/")
            return Intern(s);

        var ns = s[..slashIdx];
        var name = s[(slashIdx + 1)..];
        return Intern(ns, name);
    }

    public bool Equals(Keyword? other) => ReferenceEquals(this, other);

    public override bool Equals(object? obj) => Equals(obj as Keyword);

    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);

    public override string ToString() =>
        Namespace is null ? $":{Name}" : $":{Namespace}/{Name}";

    public static bool operator ==(Keyword? left, Keyword? right) =>
        ReferenceEquals(left, right);

    public static bool operator !=(Keyword? left, Keyword? right) =>
        !ReferenceEquals(left, right);
}
