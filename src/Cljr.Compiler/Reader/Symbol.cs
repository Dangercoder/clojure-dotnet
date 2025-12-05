namespace Cljr.Compiler.Reader;

/// <summary>
/// Represents a Clojure symbol (e.g., foo, bar/baz, +, my-fn)
/// </summary>
public sealed class Symbol : IEquatable<Symbol>
{
    public string? Namespace { get; }
    public string Name { get; }
    public IReadOnlyDictionary<object, object>? Meta { get; init; }

    public Symbol(string name) : this(null, name) { }

    public Symbol(string? ns, string name)
    {
        Namespace = ns;
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>
    /// Creates a symbol with metadata attached
    /// </summary>
    public Symbol WithMeta(IReadOnlyDictionary<object, object>? meta) =>
        new(Namespace, Name) { Meta = meta };

    /// <summary>
    /// Parse a symbol from a string like "foo" or "bar/baz"
    /// </summary>
    public static Symbol Parse(string s)
    {
        if (s is null) throw new ArgumentNullException(nameof(s));

        var slashIdx = s.IndexOf('/');
        if (slashIdx == -1 || s == "/")
            return new Symbol(s);

        var ns = s[..slashIdx];
        var name = s[(slashIdx + 1)..];
        return new Symbol(ns, name);
    }

    public bool Equals(Symbol? other)
    {
        if (other is null) return false;
        return Namespace == other.Namespace && Name == other.Name;
    }

    public override bool Equals(object? obj) => Equals(obj as Symbol);

    public override int GetHashCode() => HashCode.Combine(Namespace, Name);

    public override string ToString() =>
        Namespace is null ? Name : $"{Namespace}/{Name}";

    public static bool operator ==(Symbol? left, Symbol? right) =>
        left?.Equals(right) ?? right is null;

    public static bool operator !=(Symbol? left, Symbol? right) =>
        !(left == right);
}
