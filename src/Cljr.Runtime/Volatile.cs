namespace Cljr;

/// <summary>
/// A volatile reference for use in single-threaded contexts.
/// Faster than Atom when thread-safety is not required.
/// </summary>
public sealed class Volatile : IDeref
{
    private volatile object? _state;

    /// <summary>
    /// Creates a new volatile with the given initial value
    /// </summary>
    public Volatile(object? initialValue)
    {
        _state = initialValue;
    }

    /// <summary>
    /// Returns the current value
    /// </summary>
    public object? Deref() => _state;

    /// <summary>
    /// Sets a new value, returning the new value
    /// </summary>
    public object? Reset(object? newVal)
    {
        _state = newVal;
        return newVal;
    }

    /// <summary>
    /// Swaps the value by applying f to the current value
    /// NOT thread-safe - use Atom if concurrency is needed
    /// </summary>
    public object? Swap(Func<object?, object?> f)
    {
        var newVal = f(_state);
        _state = newVal;
        return newVal;
    }

    public override string ToString() => $"#<Volatile@{GetHashCode():x}: {Core.PrStr(_state)}>";
}
