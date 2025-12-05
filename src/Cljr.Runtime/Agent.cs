using System.Collections.Concurrent;

namespace Cljr;

/// <summary>
/// Clojure Agent - asynchronous, independent state management.
///
/// Agents allow independent, async updates to state.
/// Each agent processes actions sequentially in its own queue.
/// Errors are captured and stored for later inspection.
///
/// Two execution modes:
/// - send: Uses fixed thread pool for CPU-bound work
/// - send-off: Uses unbounded thread pool for IO-bound work
/// </summary>
public sealed class Agent : IRef, IWatchable
{
    private static readonly ThreadPool PooledExecutor = new(Environment.ProcessorCount + 2, "clojure-agent-send-pool");
    private static readonly ThreadPool SoloExecutor = new(0, "clojure-agent-send-off-pool"); // Unbounded

    private volatile object? _state;
    private readonly ConcurrentQueue<AgentAction> _actionQueue = new();
    private volatile int _running; // 0 or 1, for CAS
    private volatile Exception? _error;
    private AgentErrorMode _errorMode = AgentErrorMode.Fail;
    private Func<Agent, Exception, object?>? _errorHandler;
    private Func<object?, bool>? _validator;
    private readonly ConcurrentDictionary<object, Action<object, object?, object?, object?>> _watches = new();

    public Agent(object? initialValue)
    {
        _state = initialValue;
    }

    public Agent(object? initialValue, Func<object?, bool> validator) : this(initialValue)
    {
        if (!validator(initialValue))
            throw new ArgumentException("Initial value failed validation");
        _validator = validator;
    }

    /// <summary>
    /// Returns the current state of the agent.
    /// </summary>
    public object? Deref() => _state;

    /// <summary>
    /// Returns any error that occurred during action processing.
    /// </summary>
    public Exception? Error => _error;

    /// <summary>
    /// Dispatches an action to be executed on the send pool (CPU-bound).
    /// Returns immediately - action runs asynchronously.
    /// </summary>
    public Agent Send(Func<object?, object?> fn)
    {
        return DispatchAction(new AgentAction(this, fn, PooledExecutor));
    }

    /// <summary>
    /// Dispatches an action with additional arguments.
    /// </summary>
    public Agent Send(Func<object?, object?, object?> fn, object? arg)
    {
        return DispatchAction(new AgentAction(this, s => fn(s, arg), PooledExecutor));
    }

    /// <summary>
    /// Dispatches an action to the send-off pool (IO-bound).
    /// Use for actions that may block on IO.
    /// </summary>
    public Agent SendOff(Func<object?, object?> fn)
    {
        return DispatchAction(new AgentAction(this, fn, SoloExecutor));
    }

    private Agent DispatchAction(AgentAction action)
    {
        if (_error != null && _errorMode == AgentErrorMode.Fail)
            throw new InvalidOperationException("Agent is in failed state", _error);

        _actionQueue.Enqueue(action);
        TryStartRunner();
        return this;
    }

    private void TryStartRunner()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            // We acquired the right to run
            Task.Run(RunActions);
        }
    }

    private void RunActions()
    {
        try
        {
            while (_actionQueue.TryDequeue(out var action))
            {
                try
                {
                    var oldVal = _state;
                    var newVal = action.Fn(oldVal);
                    Validate(newVal);
                    _state = newVal;
                    NotifyWatches(oldVal, newVal);
                }
                catch (Exception ex)
                {
                    HandleError(ex);
                    if (_errorMode == AgentErrorMode.Fail)
                        break;
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _running, 0);

            // Check if more actions arrived while we were finishing
            if (!_actionQueue.IsEmpty && _error == null)
                TryStartRunner();
        }
    }

    private void HandleError(Exception ex)
    {
        _error = ex;
        if (_errorHandler != null)
        {
            try
            {
                _errorHandler(this, ex);
            }
            catch
            {
                // Error handler threw, ignore
            }
        }
    }

    /// <summary>
    /// Clears any error state and restarts action processing.
    /// </summary>
    public Agent Restart(object? newState, bool clearActions = false)
    {
        Validate(newState);

        if (clearActions)
        {
            while (_actionQueue.TryDequeue(out _)) { }
        }

        var oldVal = _state;
        _state = newState;
        _error = null;

        NotifyWatches(oldVal, newState);
        TryStartRunner();

        return this;
    }

    /// <summary>
    /// Blocks until all currently queued actions complete.
    /// </summary>
    public static void Await(params Agent[] agents)
    {
        var latch = new CountdownEvent(agents.Length);
        foreach (var agent in agents)
        {
            agent._actionQueue.Enqueue(new AgentAction(agent, s =>
            {
                latch.Signal();
                return s;
            }, PooledExecutor));
            agent.TryStartRunner();
        }
        latch.Wait();
    }

    /// <summary>
    /// Blocks until all queued actions complete, with timeout.
    /// Returns true if completed within timeout.
    /// </summary>
    public static bool Await(TimeSpan timeout, params Agent[] agents)
    {
        var latch = new CountdownEvent(agents.Length);
        foreach (var agent in agents)
        {
            agent._actionQueue.Enqueue(new AgentAction(agent, s =>
            {
                latch.Signal();
                return s;
            }, PooledExecutor));
            agent.TryStartRunner();
        }
        return latch.Wait(timeout);
    }

    #region Error Handling

    public void SetErrorMode(AgentErrorMode mode) => _errorMode = mode;
    public AgentErrorMode GetErrorMode() => _errorMode;

    public void SetErrorHandler(Func<Agent, Exception, object?>? handler) => _errorHandler = handler;
    public Func<Agent, Exception, object?>? GetErrorHandler() => _errorHandler;

    #endregion

    #region IWatchable

    private void Validate(object? val)
    {
        if (_validator != null && !_validator(val))
            throw new InvalidOperationException("Validator rejected value");
    }

    public void SetValidator(Func<object?, bool>? validator)
    {
        if (validator != null)
            Validate(_state);
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
            catch { }
        }
    }

    #endregion

    public override string ToString() => $"#<Agent@{GetHashCode():x}: {Core.PrStr(_state)}>";
}

public enum AgentErrorMode
{
    Continue,  // Continue processing actions after error
    Fail       // Stop processing actions on error
}

internal record AgentAction(Agent Agent, Func<object?, object?> Fn, ThreadPool Executor);

/// <summary>
/// Simple thread pool for agent execution.
/// </summary>
internal sealed class ThreadPool
{
    private readonly int _maxThreads;
    private readonly string _name;

    public ThreadPool(int maxThreads, string name)
    {
        _maxThreads = maxThreads;
        _name = name;
    }

    public void Execute(Action action)
    {
        if (_maxThreads == 0)
        {
            // Unbounded - just queue to .NET thread pool
            Task.Run(action);
        }
        else
        {
            // Could implement a bounded pool, but for now use .NET pool
            Task.Run(action);
        }
    }
}
