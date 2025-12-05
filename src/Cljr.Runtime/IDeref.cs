namespace Cljr;

/// <summary>
/// Interface for dereferenceable types (atoms, volatiles, delays, futures, etc.)
/// </summary>
public interface IDeref
{
    /// <summary>
    /// Returns the current value
    /// </summary>
    object? Deref();
}

/// <summary>
/// Interface for dereferenceable types that support blocking with timeout
/// </summary>
public interface IBlockingDeref : IDeref
{
    /// <summary>
    /// Returns the current value, blocking for up to timeout milliseconds
    /// </summary>
    /// <param name="timeoutMs">Timeout in milliseconds</param>
    /// <param name="timeoutVal">Value to return if timeout expires</param>
    object? Deref(long timeoutMs, object? timeoutVal);
}

/// <summary>
/// Interface for reference types that support validation
/// </summary>
public interface IRef : IDeref
{
    /// <summary>
    /// Sets a validator function that will be called on any new value
    /// </summary>
    void SetValidator(Func<object?, bool>? validator);

    /// <summary>
    /// Gets the current validator function
    /// </summary>
    Func<object?, bool>? GetValidator();
}

/// <summary>
/// Interface for watchable references
/// </summary>
public interface IWatchable
{
    /// <summary>
    /// Adds a watch function that will be called when the reference changes
    /// </summary>
    void AddWatch(object key, Action<object, object?, object?, object?> callback);

    /// <summary>
    /// Removes a watch by key
    /// </summary>
    void RemoveWatch(object key);
}
