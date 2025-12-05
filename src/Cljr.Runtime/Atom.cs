using System.Collections.Concurrent;

namespace Cljr;

/// <summary>
/// Thread-safe mutable reference type.
/// Provides atomic compare-and-swap semantics using spin loops.
/// </summary>
public sealed class Atom : IRef, IWatchable
{
    private volatile object? _state;
    private Func<object?, bool>? _validator;
    private readonly ConcurrentDictionary<object, Action<object, object?, object?, object?>> _watches = new();

    /// <summary>
    /// Creates a new atom with the given initial value
    /// </summary>
    public Atom(object? initialValue)
    {
        _state = initialValue;
    }

    /// <summary>
    /// Creates a new atom with an initial value and validator
    /// </summary>
    public Atom(object? initialValue, Func<object?, bool> validator)
    {
        if (!validator(initialValue))
            throw new ArgumentException("Initial value failed validation");
        _validator = validator;
        _state = initialValue;
    }

    /// <summary>
    /// Returns the current value of the atom
    /// </summary>
    public object? Deref() => _state;

    /// <summary>
    /// Atomically swaps the value by applying function f to current value.
    /// f may be called multiple times due to contention - should be side-effect free.
    /// </summary>
    public object? Swap(Func<object?, object?> f)
    {
        while (true)
        {
            var oldVal = _state;
            var newVal = f(oldVal);
            Validate(newVal);
            if (CompareAndSet(oldVal, newVal))
            {
                NotifyWatches(oldVal, newVal);
                return newVal;
            }
        }
    }

    /// <summary>
    /// Atomically swaps the value by applying function f to current value and arg.
    /// </summary>
    public object? Swap(Func<object?, object?, object?> f, object? arg)
    {
        while (true)
        {
            var oldVal = _state;
            var newVal = f(oldVal, arg);
            Validate(newVal);
            if (CompareAndSet(oldVal, newVal))
            {
                NotifyWatches(oldVal, newVal);
                return newVal;
            }
        }
    }

    /// <summary>
    /// Atomically swaps the value by applying function f to current value and args.
    /// </summary>
    public object? Swap(Func<object?, object?, object?, object?> f, object? arg1, object? arg2)
    {
        while (true)
        {
            var oldVal = _state;
            var newVal = f(oldVal, arg1, arg2);
            Validate(newVal);
            if (CompareAndSet(oldVal, newVal))
            {
                NotifyWatches(oldVal, newVal);
                return newVal;
            }
        }
    }

    /// <summary>
    /// Atomically swaps the value, returning [old-val new-val]
    /// </summary>
    public (object? OldVal, object? NewVal) SwapVals(Func<object?, object?> f)
    {
        while (true)
        {
            var oldVal = _state;
            var newVal = f(oldVal);
            Validate(newVal);
            if (CompareAndSet(oldVal, newVal))
            {
                NotifyWatches(oldVal, newVal);
                return (oldVal, newVal);
            }
        }
    }

    /// <summary>
    /// Sets the value without regard to current value
    /// </summary>
    public object? Reset(object? newVal)
    {
        var oldVal = _state;
        Validate(newVal);
        _state = newVal;
        NotifyWatches(oldVal, newVal);
        return newVal;
    }

    /// <summary>
    /// Resets the value, returning [old-val new-val]
    /// </summary>
    public (object? OldVal, object? NewVal) ResetVals(object? newVal)
    {
        while (true)
        {
            var oldVal = _state;
            Validate(newVal);
            if (CompareAndSet(oldVal, newVal))
            {
                NotifyWatches(oldVal, newVal);
                return (oldVal, newVal);
            }
        }
    }

    /// <summary>
    /// Atomically sets value to newVal if and only if current value equals oldVal
    /// </summary>
    public bool CompareAndSet(object? oldVal, object? newVal)
    {
        Validate(newVal);
        // Use a spin loop with value comparison (like Clojure)
        // because Interlocked.CompareExchange uses reference equality
        while (true)
        {
            var current = _state;
            if (!Core.Equals(current, oldVal))
                return false;
            if (Interlocked.CompareExchange(ref _state, newVal, current) == current)
            {
                NotifyWatches(oldVal, newVal);
                return true;
            }
            // If exchange failed due to concurrent modification, retry comparison
        }
    }

    private void Validate(object? val)
    {
        if (_validator != null && !_validator(val))
            throw new InvalidOperationException("Validator rejected value");
    }

    public void SetValidator(Func<object?, bool>? validator)
    {
        if (validator != null)
            Validate(_state); // Validate current value
        _validator = validator;
    }

    public Func<object?, bool>? GetValidator() => _validator;

    public void AddWatch(object key, Action<object, object?, object?, object?> callback)
    {
        _watches[key] = callback;
    }

    public void RemoveWatch(object key)
    {
        _watches.TryRemove(key, out _);
    }

    private void NotifyWatches(object? oldVal, object? newVal)
    {
        foreach (var watch in _watches)
        {
            try
            {
                watch.Value(watch.Key, this, oldVal, newVal);
            }
            catch
            {
                // Watches should not throw, but if they do, continue with other watches
            }
        }
    }

    public override string ToString() => $"#<Atom@{GetHashCode():x}: {Core.PrStr(_state)}>";
}
