using System.Runtime.CompilerServices;

namespace Cljr;

/// <summary>
/// Clojure STM transaction implementation.
///
/// Uses optimistic concurrency with retry on conflict:
/// - Tracks all reads and writes within transaction
/// - Validates read set at commit time
/// - Acquires write locks in order to avoid deadlock
/// - Commits atomically or retries entire transaction
///
/// Based on multi-version concurrency control (MVCC) principles.
/// </summary>
public sealed class LockingTransaction
{
    private const int RetryLimit = 10000;
    private const int LockWaitMsecs = 100;

    [ThreadStatic] private static LockingTransaction? _current;

    public static LockingTransaction? Current => _current;

    /// <summary>
    /// Ensures we're running in a transaction, throws otherwise.
    /// </summary>
    public static LockingTransaction RequireTransaction()
    {
        if (_current == null)
            throw new InvalidOperationException("No transaction running. Use dosync.");
        return _current;
    }

    private enum TransactionState { Running, Committing, Retry, Committed }

    private readonly Dictionary<Ref, object?> _vals = new();     // In-transaction values
    private readonly Dictionary<Ref, object?> _sets = new();     // Written values
    private readonly Dictionary<Ref, List<Func<object?, object?>>> _commutes = new();  // Commute functions
    private readonly HashSet<Ref> _ensures = new();               // Ensured refs
    private TransactionState _state = TransactionState.Running;
    private readonly long _startTime;

    public LockingTransaction()
    {
        _startTime = Environment.TickCount64;
    }

    /// <summary>
    /// Runs the function in a transaction.
    /// Automatically retries on conflict up to RetryLimit times.
    /// </summary>
    public static object? RunInTransaction(Func<object?> fn)
    {
        if (_current != null)
        {
            // Already in a transaction, just run
            return fn();
        }

        var txn = new LockingTransaction();
        _current = txn;

        try
        {
            for (int i = 0; i < RetryLimit; i++)
            {
                try
                {
                    txn.Reset();
                    var result = fn();
                    txn.Commit();
                    return result;
                }
                catch (RetryException)
                {
                    // Transaction conflict, retry
                    continue;
                }
            }
            throw new TransactionFailedException();
        }
        finally
        {
            _current = null;
        }
    }

    private void Reset()
    {
        _vals.Clear();
        _sets.Clear();
        _commutes.Clear();
        _ensures.Clear();
        _state = TransactionState.Running;
    }

    /// <summary>
    /// Gets the in-transaction value of a ref.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? DoGet(Ref r)
    {
        EnsureRunning();

        // Check if we've already read/written this ref
        if (_vals.TryGetValue(r, out var val))
            return val;

        // First read - get current value
        r.ReadLock();
        try
        {
            val = r.CurrentVal;
            _vals[r] = val;
            return val;
        }
        finally
        {
            r.ReadUnlock();
        }
    }

    /// <summary>
    /// Sets the ref value within the transaction.
    /// </summary>
    public object? DoSet(Ref r, object? val)
    {
        EnsureRunning();
        r.Validate(val);

        _vals[r] = val;
        _sets[r] = val;
        return val;
    }

    /// <summary>
    /// Applies function to ref value within transaction.
    /// </summary>
    public object? DoAlter(Ref r, Func<object?, object?> fn)
    {
        var oldVal = DoGet(r);
        var newVal = fn(oldVal);
        return DoSet(r, newVal);
    }

    /// <summary>
    /// Registers a commute function to be applied at commit.
    /// </summary>
    public object? DoCommute(Ref r, Func<object?, object?> fn)
    {
        EnsureRunning();

        // Apply function to get return value
        var oldVal = DoGet(r);
        var newVal = fn(oldVal);
        _vals[r] = newVal;

        // Record commute to be re-applied at commit
        if (!_commutes.TryGetValue(r, out var fns))
        {
            fns = new List<Func<object?, object?>>();
            _commutes[r] = fns;
        }
        fns.Add(fn);

        return newVal;
    }

    /// <summary>
    /// Ensures ref value hasn't changed (read consistency).
    /// </summary>
    public void DoEnsure(Ref r)
    {
        EnsureRunning();
        _ensures.Add(r);
        // Make sure we have the value
        DoGet(r);
    }

    /// <summary>
    /// Commits all changes atomically.
    /// </summary>
    private void Commit()
    {
        if (_sets.Count == 0 && _commutes.Count == 0)
            return; // Nothing to commit

        _state = TransactionState.Committing;

        // Get all refs we need to lock, in consistent order to avoid deadlock
        var refsToLock = new SortedSet<Ref>(
            _sets.Keys.Concat(_commutes.Keys).Concat(_ensures),
            Comparer<Ref>.Create((a, b) => a.GetHashCode().CompareTo(b.GetHashCode())));

        var locked = new List<Ref>();
        try
        {
            // Acquire all write locks
            foreach (var r in refsToLock)
            {
                if (!TryLock(r))
                    throw RetryException.Instance;
                locked.Add(r);
            }

            // Validate ensures - check values haven't changed
            foreach (var r in _ensures)
            {
                if (_vals.TryGetValue(r, out var oldVal))
                {
                    if (!Core.Equals(r.CurrentVal, oldVal))
                        throw RetryException.Instance;
                }
            }

            // Apply commutes with current values
            foreach (var (r, fns) in _commutes)
            {
                var val = r.CurrentVal;
                foreach (var fn in fns)
                {
                    val = fn(val);
                    r.Validate(val);
                }
                _sets[r] = val;
            }

            // Apply all writes
            foreach (var (r, newVal) in _sets)
            {
                var oldVal = r.CurrentVal;
                r.SetValue(newVal, oldVal);
            }

            _state = TransactionState.Committed;
        }
        finally
        {
            // Release all locks
            foreach (var r in locked)
                r.Unlock();
        }
    }

    private bool TryLock(Ref r)
    {
        // Simple spin-wait lock with timeout
        var deadline = Environment.TickCount64 + LockWaitMsecs;
        while (Environment.TickCount64 < deadline)
        {
            try
            {
                r.Lock();
                return true;
            }
            catch
            {
                Thread.Yield();
            }
        }
        return false;
    }

    private void EnsureRunning()
    {
        if (_state != TransactionState.Running)
            throw new InvalidOperationException("Transaction not running");
    }
}

/// <summary>
/// Helper class for running code in a transaction.
/// </summary>
public static class STM
{
    /// <summary>
    /// Runs the function in a transaction (dosync equivalent).
    /// </summary>
    public static object? DoSync(Func<object?> fn) =>
        LockingTransaction.RunInTransaction(fn);

    /// <summary>
    /// Creates a new ref with the given initial value.
    /// </summary>
    public static Ref CreateRef(object? initialValue) =>
        new(initialValue);

    /// <summary>
    /// Creates a new ref with validator.
    /// </summary>
    public static Ref CreateRef(object? initialValue, Func<object?, bool> validator) =>
        new(initialValue, validator);
}
