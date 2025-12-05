namespace Cljr;

/// <summary>
/// A delay wraps a computation that is only executed once, on first deref.
/// Thread-safe lazy evaluation.
/// </summary>
public sealed class Delay : IDeref, IBlockingDeref
{
    private readonly Lazy<object?> _lazy;
    private Exception? _error;

    /// <summary>
    /// Creates a delay from a function that will be called on first deref
    /// </summary>
    public Delay(Func<object?> fn)
    {
        _lazy = new Lazy<object?>(() =>
        {
            try
            {
                return fn();
            }
            catch (Exception ex)
            {
                _error = ex;
                throw;
            }
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>
    /// Forces the computation if not already done, returns the value
    /// </summary>
    public object? Deref()
    {
        try
        {
            return _lazy.Value;
        }
        catch
        {
            throw _error ?? new InvalidOperationException("Delay failed");
        }
    }

    /// <summary>
    /// Deref with timeout (timeout not applicable for delay - always completes synchronously)
    /// </summary>
    public object? Deref(long timeoutMs, object? timeoutVal) => Deref();

    /// <summary>
    /// Returns true if the delay has been realized
    /// </summary>
    public bool IsRealized => _lazy.IsValueCreated;

    /// <summary>
    /// Forces the delay and returns the value (same as Deref)
    /// </summary>
    public object? Force() => Deref();

    public override string ToString() =>
        _lazy.IsValueCreated
            ? $"#<Delay@{GetHashCode():x}: {Core.PrStr(_lazy.Value)}>"
            : $"#<Delay@{GetHashCode():x}: pending>";
}
