namespace Cljr;

/// <summary>
/// Async support for Cljr - native Task/async/await integration.
/// This is our unique feature: seamless .NET async interop.
/// </summary>
public static class Async
{
    /// <summary>
    /// Awaits a Task and returns its result.
    /// Can be used with any Task or Task&lt;T&gt;.
    /// </summary>
    public static async Task<object?> Await(object? taskObj)
    {
        return taskObj switch
        {
            null => null,
            Task<object?> typedTask => await typedTask,
            Task task => await AwaitNonGenericTask(task),
            ValueTask<object?> vt => await vt,
            ValueTask vt => await AwaitValueTask(vt),
            _ => taskObj // Not a task, return as-is
        };
    }

    private static async Task<object?> AwaitValueTask(ValueTask vt)
    {
        await vt;
        return null;
    }

    private static async Task<object?> AwaitNonGenericTask(Task task)
    {
        await task;
        // Try to get result via reflection for Task<T>
        var taskType = task.GetType();
        if (taskType.IsGenericType)
        {
            var resultProp = taskType.GetProperty("Result");
            if (resultProp != null)
                return resultProp.GetValue(task);
        }
        return null;
    }

    /// <summary>
    /// Creates an async function wrapper that returns Task&lt;object?&gt;
    /// </summary>
    public static Func<Task<object?>> AsyncFn(Func<Task<object?>> fn) => fn;

    /// <summary>
    /// Creates an async function wrapper with one parameter
    /// </summary>
    public static Func<object?, Task<object?>> AsyncFn(Func<object?, Task<object?>> fn) => fn;

    /// <summary>
    /// Creates an async function wrapper with two parameters
    /// </summary>
    public static Func<object?, object?, Task<object?>> AsyncFn(Func<object?, object?, Task<object?>> fn) => fn;

    /// <summary>
    /// Runs a function on a background thread, returning a Task
    /// Similar to Clojure's future
    /// </summary>
    public static Task<object?> Future(Func<object?> fn)
    {
        return Task.Run(fn);
    }

    /// <summary>
    /// Creates a completed Task with the given value
    /// </summary>
    public static Task<object?> Resolved(object? value) => Task.FromResult(value);

    /// <summary>
    /// Creates a failed Task with the given exception
    /// </summary>
    public static Task<object?> Rejected(Exception ex) => Task.FromException<object?>(ex);

    /// <summary>
    /// Awaits all tasks and returns results as a list
    /// </summary>
    public static async Task<List<object?>> AwaitAll(params Task<object?>[] tasks)
    {
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Awaits all tasks from a collection
    /// </summary>
    public static async Task<List<object?>> AwaitAll(IEnumerable<Task<object?>> tasks)
    {
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Awaits the first task to complete
    /// </summary>
    public static async Task<object?> AwaitAny(params Task<object?>[] tasks)
    {
        var winner = await Task.WhenAny(tasks);
        return await winner;
    }

    /// <summary>
    /// Creates a timeout that completes after the specified milliseconds
    /// </summary>
    public static async Task<object?> Timeout(int ms)
    {
        await Task.Delay(ms);
        return null;
    }

    /// <summary>
    /// Awaits a task with timeout, returning timeoutVal if timeout expires
    /// </summary>
    public static async Task<object?> AwaitTimeout(Task<object?> task, int timeoutMs, object? timeoutVal)
    {
        var timeoutTask = Task.Delay(timeoutMs);
        var winner = await Task.WhenAny(task, timeoutTask);
        return winner == task ? await task : timeoutVal;
    }

    /// <summary>
    /// Creates a channel (async producer/consumer queue)
    /// </summary>
    public static Channel CreateChannel(int? bufferSize = null)
    {
        return new Channel(bufferSize ?? 0);
    }

    /// <summary>
    /// Maps an async function over a sequence, running tasks concurrently
    /// </summary>
    public static async Task<List<object?>> MapAsync(Func<object?, Task<object?>> f, IEnumerable<object?> coll)
    {
        var tasks = coll.Select(f);
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Maps an async function over a sequence with limited concurrency
    /// </summary>
    public static async Task<List<object?>> MapAsync(Func<object?, Task<object?>> f, IEnumerable<object?> coll, int maxConcurrency)
    {
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = coll.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await f(item);
            }
            finally
            {
                semaphore.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    #region Clojure Function Interop

    /// <summary>
    /// Maps any Clojure function (sync or async) over a sequence concurrently.
    /// Works with AsyncFn, IFn, Func delegates, or any callable.
    /// </summary>
    public static async Task<List<object?>> MapAsync(object? f, IEnumerable<object?> coll)
    {
        var asyncFn = ToAsyncFunc1(f);
        var results = await Task.WhenAll(coll.Select(asyncFn));
        return results.ToList();
    }

    /// <summary>
    /// Maps any Clojure function over a sequence with limited concurrency.
    /// </summary>
    public static async Task<List<object?>> MapAsync(object? f, IEnumerable<object?> coll, int maxConcurrency)
    {
        var asyncFn = ToAsyncFunc1(f);
        var semaphore = new SemaphoreSlim(maxConcurrency);
        var tasks = coll.Select(async item =>
        {
            await semaphore.WaitAsync();
            try
            {
                return await asyncFn(item);
            }
            finally
            {
                semaphore.Release();
            }
        });
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    /// <summary>
    /// Converts any Clojure function to Func&lt;Task&lt;object?&gt;&gt; (0-arg async delegate).
    /// Works with AsyncFn0, IFn, or Func delegates.
    /// </summary>
    public static Func<Task<object?>> ToAsyncFunc0(object? f)
    {
        return f switch
        {
            AsyncFn0 af => af.AsTypedDelegate(),
            IAsyncFn iaf => async () => await Await(((IFn)iaf).Invoke()),
            Func<Task<object?>> fn => fn,
            Func<object?> sync => async () =>
            {
                var r = sync();
                return r is Task<object?> t ? await t : r;
            },
            IFn ifn => async () =>
            {
                var r = ifn.Invoke();
                return r is Task<object?> t ? await t : r;
            },
            _ => throw new ArgumentException($"Expected function, got {f?.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts any Clojure function to Func&lt;object?, Task&lt;object?&gt;&gt; (1-arg async delegate).
    /// Works with AsyncFn1, IFn, or Func delegates.
    /// </summary>
    public static Func<object?, Task<object?>> ToAsyncFunc1(object? f)
    {
        return f switch
        {
            AsyncFn1 af => af.AsTypedDelegate(),
            IAsyncFn iaf => async x => await Await(((IFn)iaf).Invoke(x)),
            Func<object?, Task<object?>> fn => fn,
            Func<object?, object?> sync => async x =>
            {
                var r = sync(x);
                return r is Task<object?> t ? await t : r;
            },
            IFn ifn => async x =>
            {
                var r = ifn.Invoke(x);
                return r is Task<object?> t ? await t : r;
            },
            _ => throw new ArgumentException($"Expected function, got {f?.GetType().Name}")
        };
    }

    /// <summary>
    /// Converts any Clojure function to Func&lt;object?, object?, Task&lt;object?&gt;&gt; (2-arg async delegate).
    /// </summary>
    public static Func<object?, object?, Task<object?>> ToAsyncFunc2(object? f)
    {
        return f switch
        {
            AsyncFn2 af => af.AsTypedDelegate(),
            IAsyncFn iaf => async (a, b) => await Await(((IFn)iaf).Invoke(a, b)),
            Func<object?, object?, Task<object?>> fn => fn,
            Func<object?, object?, object?> sync => async (a, b) =>
            {
                var r = sync(a, b);
                return r is Task<object?> t ? await t : r;
            },
            IFn ifn => async (a, b) =>
            {
                var r = ifn.Invoke(a, b);
                return r is Task<object?> t ? await t : r;
            },
            _ => throw new ArgumentException($"Expected function, got {f?.GetType().Name}")
        };
    }

    #endregion
}

/// <summary>
/// A simple async channel for producer/consumer patterns.
/// Inspired by core.async channels but using .NET async primitives.
/// </summary>
public sealed class Channel : IAsyncDisposable
{
    private readonly System.Threading.Channels.Channel<object?> _channel;
    private bool _closed;

    public Channel(int bufferSize)
    {
        _channel = bufferSize > 0
            ? System.Threading.Channels.Channel.CreateBounded<object?>(bufferSize)
            : System.Threading.Channels.Channel.CreateUnbounded<object?>();
    }

    /// <summary>
    /// Puts a value onto the channel. Returns true if successful.
    /// </summary>
    public async ValueTask<bool> Put(object? value)
    {
        if (_closed) return false;
        try
        {
            await _channel.Writer.WriteAsync(value);
            return true;
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Takes a value from the channel. Returns null if channel is closed and empty.
    /// </summary>
    public async ValueTask<object?> Take()
    {
        try
        {
            return await _channel.Reader.ReadAsync();
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Attempts to put without blocking
    /// </summary>
    public bool TryPut(object? value)
    {
        if (_closed) return false;
        return _channel.Writer.TryWrite(value);
    }

    /// <summary>
    /// Attempts to take without blocking
    /// </summary>
    public (bool Success, object? Value) TryTake()
    {
        if (_channel.Reader.TryRead(out var value))
            return (true, value);
        return (false, null);
    }

    /// <summary>
    /// Closes the channel. No more values can be put.
    /// </summary>
    public void Close()
    {
        _closed = true;
        _channel.Writer.Complete();
    }

    /// <summary>
    /// Returns true if the channel is closed
    /// </summary>
    public bool IsClosed => _closed;

    /// <summary>
    /// Returns an async enumerable to consume all values
    /// </summary>
    public IAsyncEnumerable<object?> AsAsyncEnumerable() => _channel.Reader.ReadAllAsync();

    public ValueTask DisposeAsync()
    {
        Close();
        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"#<Channel@{GetHashCode():x}: {(_closed ? "closed" : "open")}>";
}
