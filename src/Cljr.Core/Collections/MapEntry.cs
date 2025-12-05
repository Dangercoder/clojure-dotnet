namespace Cljr.Collections;

/// <summary>
/// Immutable key-value pair implementation.
/// </summary>
public sealed class MapEntry : IMapEntry
{
    private readonly object _key;
    private readonly object? _val;

    public MapEntry(object key, object? val)
    {
        _key = key ?? throw new ArgumentNullException(nameof(key));
        _val = val;
    }

    public object Key() => _key;
    public object? Val() => _val;

    public override string ToString() => $"[{_key} {_val}]";

    public override bool Equals(object? obj)
    {
        if (obj is IMapEntry other)
            return CoreFunctions.Equals(_key, other.Key()) && CoreFunctions.Equals(_val, other.Val());
        return false;
    }

    public override int GetHashCode() => HashCode.Combine(_key, _val);

    public void Deconstruct(out object key, out object? val)
    {
        key = _key;
        val = _val;
    }
}
