using System.Collections.Concurrent;

namespace Cljr;

/// <summary>
/// Clojure Ref - coordinated, synchronous reference type for STM.
///
/// Refs can only be modified within a dosync transaction.
/// Changes are atomic, consistent, and isolated.
/// All refs modified in a transaction are committed together or rolled back.
/// </summary>
public sealed class Ref : IRef, IWatchable
{
    private volatile object? _tval;  // Current transaction value
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);
    private Func<object?, bool>? _validator;
    private readonly ConcurrentDictionary<object, Action<object, object?, object?, object?>> _watches = new();
    private readonly long _minHistory;
    private readonly long _maxHistory;

    public Ref(object? initialValue, long minHistory = 0, long maxHistory = 10)
    {
        _tval = initialValue;
        _minHistory = minHistory;
        _maxHistory = maxHistory;
    }

    public Ref(object? initialValue, Func<object?, bool> validator) : this(initialValue)
    {
        if (!validator(initialValue))
            throw new ArgumentException("Initial value failed validation");
        _validator = validator;
    }

    /// <summary>
    /// Returns the current value of the ref.
    /// If in a transaction, returns the in-transaction value.
    /// </summary>
    public object? Deref()
    {
        var txn = LockingTransaction.Current;
        if (txn != null)
            return txn.DoGet(this);
        return _tval;
    }

    /// <summary>
    /// Gets the current committed value (bypassing transaction).
    /// </summary>
    internal object? CurrentVal => _tval;

    /// <summary>
    /// Sets the value within a transaction.
    /// </summary>
    public object? Set(object? val)
    {
        var txn = LockingTransaction.RequireTransaction();
        return txn.DoSet(this, val);
    }

    /// <summary>
    /// Applies function to current value within a transaction.
    /// Function may be called multiple times if transaction retries.
    /// </summary>
    public object? Alter(Func<object?, object?> fn)
    {
        var txn = LockingTransaction.RequireTransaction();
        return txn.DoAlter(this, fn);
    }

    /// <summary>
    /// Like alter, but function is only applied at commit time.
    /// Multiple commutes on same ref are combined efficiently.
    /// Use when the order of updates doesn't matter.
    /// </summary>
    public object? Commute(Func<object?, object?> fn)
    {
        var txn = LockingTransaction.RequireTransaction();
        return txn.DoCommute(this, fn);
    }

    /// <summary>
    /// Ensures the ref's value hasn't changed during the transaction.
    /// Use for read-only refs that must remain consistent.
    /// </summary>
    public void Ensure()
    {
        var txn = LockingTransaction.RequireTransaction();
        txn.DoEnsure(this);
    }

    internal void Validate(object? val)
    {
        if (_validator != null && !_validator(val))
            throw new InvalidOperationException("Validator rejected value");
    }

    internal void Lock() => _lock.EnterWriteLock();
    internal void Unlock() => _lock.ExitWriteLock();
    internal void ReadLock() => _lock.EnterReadLock();
    internal void ReadUnlock() => _lock.ExitReadLock();

    internal void SetValue(object? val, object? oldVal)
    {
        _tval = val;
        NotifyWatches(oldVal, val);
    }

    #region IWatchable

    public void SetValidator(Func<object?, bool>? validator)
    {
        if (validator != null)
            Validate(_tval);
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
                // Watches should not throw
            }
        }
    }

    #endregion

    public override string ToString() => $"#<Ref@{GetHashCode():x}: {Core.PrStr(_tval)}>";
}

/// <summary>
/// Exception thrown when a transaction fails and should be retried.
/// </summary>
public sealed class RetryException : Exception
{
    public static readonly RetryException Instance = new();
    private RetryException() : base("Transaction retry") { }
}

/// <summary>
/// Exception thrown when a transaction exceeds retry limit.
/// </summary>
public sealed class TransactionFailedException : Exception
{
    public TransactionFailedException() : base("Transaction failed after maximum retries") { }
    public TransactionFailedException(string message) : base(message) { }
}
