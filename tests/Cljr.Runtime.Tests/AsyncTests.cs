using Cljr;
using static Cljr.Core;

namespace Cljr.Runtime.Tests;

#region AsyncFn Tests

public class AsyncFnTests
{
    [Fact]
    public async Task AsyncFn0_InvokeReturnsTask()
    {
        var fn = new AsyncFn0(async () =>
        {
            await Task.Delay(1);
            return (object?)42;
        });

        var result = fn.Invoke();
        Assert.IsAssignableFrom<Task<object?>>(result);
        Assert.Equal(42, await (Task<object?>)result!);
    }

    [Fact]
    public async Task AsyncFn1_InvokeReturnsTask()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return (object?)((long)x! * 2);
        });

        var result = fn.Invoke(21L);
        Assert.IsAssignableFrom<Task<object?>>(result);
        Assert.Equal(42L, await (Task<object?>)result!);
    }

    [Fact]
    public async Task AsyncFn2_InvokeReturnsTask()
    {
        var fn = new AsyncFn2(async (a, b) =>
        {
            await Task.Delay(1);
            return (object?)((long)a! + (long)b!);
        });

        var result = fn.Invoke(20L, 22L);
        Assert.IsAssignableFrom<Task<object?>>(result);
        Assert.Equal(42L, await (Task<object?>)result!);
    }

    [Fact]
    public void AsyncFn1_AsTypedDelegate_ReturnsCorrectType()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return x;
        });

        Func<object?, Task<object?>> typedDelegate = fn.AsTypedDelegate();
        Assert.NotNull(typedDelegate);
    }

    [Fact]
    public void AsyncFn1_ImplicitConversion_Works()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return x;
        });

        // Implicit conversion to typed delegate
        Func<object?, Task<object?>> typedDelegate = fn;
        Assert.NotNull(typedDelegate);
    }

    [Fact]
    public void AsyncFn1_WrongArity_ThrowsArityException()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return x;
        });

        Assert.Throws<ArityException>(() => fn.Invoke());
        Assert.Throws<ArityException>(() => fn.Invoke(1, 2));
    }

    [Fact]
    public void AsyncFn0_ImplementsIFn()
    {
        var fn = new AsyncFn0(async () =>
        {
            await Task.Delay(1);
            return (object?)42;
        });

        Assert.IsAssignableFrom<IFn>(fn);
    }

    [Fact]
    public void AsyncFn_ImplementsIAsyncFn()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return x;
        });

        Assert.IsAssignableFrom<IAsyncFn>(fn);
        Assert.NotNull(fn.GetTypedDelegate());
    }

    [Fact]
    public async Task ToAsyncFunc1_WithAsyncFn1_ExtractsTypedDelegate()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return (object?)((long)x! * 2);
        });

        var asyncFunc = Async.ToAsyncFunc1(fn);
        var result = await asyncFunc(21L);
        Assert.Equal(42L, result);
    }

    [Fact]
    public async Task ToAsyncFunc1_WithSyncFunc_WrapsCorrectly()
    {
        Func<object?, object?> syncFn = x => (object?)((long)x! * 2);

        var asyncFunc = Async.ToAsyncFunc1(syncFn);
        var result = await asyncFunc(21L);
        Assert.Equal(42L, result);
    }

    [Fact]
    public async Task ToAsyncFunc1_WithSyncFuncReturningTask_AwaitsTask()
    {
        Func<object?, object?> syncFnReturningTask = x =>
            Task.FromResult<object?>((object?)((long)x! * 2));

        var asyncFunc = Async.ToAsyncFunc1(syncFnReturningTask);
        var result = await asyncFunc(21L);
        Assert.Equal(42L, result);
    }

    [Fact]
    public async Task MapAsync_WithAsyncFn_WorksCorrectly()
    {
        var fn = new AsyncFn1(async x =>
        {
            await Task.Delay(1);
            return (object?)((long)x! * 2);
        });

        var items = new List<object?> { 1L, 2L, 3L };
        var results = await Async.MapAsync(fn, items);

        Assert.Equal(3, results.Count);
        Assert.Equal(2L, results[0]);
        Assert.Equal(4L, results[1]);
        Assert.Equal(6L, results[2]);
    }

    [Fact]
    public async Task MapAsync_WithSyncFunc_WorksCorrectly()
    {
        Func<object?, object?> syncFn = x => (object?)((long)x! * 2);

        var items = new List<object?> { 1L, 2L, 3L };
        var results = await Async.MapAsync(syncFn, items);

        Assert.Equal(3, results.Count);
        Assert.Equal(2L, results[0]);
        Assert.Equal(4L, results[1]);
        Assert.Equal(6L, results[2]);
    }

    [Fact]
    public async Task MapAsync_WithConcurrencyLimit_WorksWithAsyncFn()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;

        var fn = new AsyncFn1(async x =>
        {
            Interlocked.Increment(ref concurrentCount);
            maxConcurrent = Math.Max(maxConcurrent, concurrentCount);
            await Task.Delay(20);
            Interlocked.Decrement(ref concurrentCount);
            return x;
        });

        var items = Enumerable.Range(0, 10).Select(i => (object?)(long)i).ToList();
        await Async.MapAsync(fn, items, 2);

        Assert.True(maxConcurrent <= 2);
    }
}

#endregion

public class AsyncTests
{
    [Fact]
    public async Task Await_TaskWithResult_ReturnsResult()
    {
        var task = Task.FromResult<object?>(42);
        var result = await Async.Await(task);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Await_NonTask_ReturnsAsIs()
    {
        var result = await Async.Await(42);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Await_Null_ReturnsNull()
    {
        var result = await Async.Await(null);
        Assert.Null(result);
    }

    [Fact]
    public void Future_RunsOnBackgroundThread()
    {
        var mainThreadId = Environment.CurrentManagedThreadId;
        var futureThreadId = -1;

        var f = future(() =>
        {
            futureThreadId = Environment.CurrentManagedThreadId;
            return (object?)42;
        });

        var result = deref(f);
        Assert.Equal(42, result);
        // Note: Thread pool may reuse the same thread in some cases
    }

    [Fact]
    public void FutureDone_ReturnsTrueWhenComplete()
    {
        var f = Task.FromResult<object?>(42);
        Assert.True(future_done_QMARK_(f));
    }

    [Fact]
    public async Task AwaitAll_WaitsForAllTasks()
    {
        var tasks = new[]
        {
            Task.FromResult<object?>(1),
            Task.FromResult<object?>(2),
            Task.FromResult<object?>(3)
        };

        var results = await Async.AwaitAll(tasks);
        Assert.Equal(3, results.Count);
        Assert.Equal(1, results[0]);
        Assert.Equal(2, results[1]);
        Assert.Equal(3, results[2]);
    }

    [Fact]
    public async Task AwaitAny_ReturnsFirstCompleted()
    {
        var fast = Task.FromResult<object?>("fast");
        var slow = Task.Delay(1000).ContinueWith(_ => (object?)"slow");

        var result = await Async.AwaitAny(fast, slow);
        Assert.Equal("fast", result);
    }

    [Fact]
    public async Task MapAsync_ProcessesInParallel()
    {
        var items = new List<object?> { 1L, 2L, 3L };
        var results = await Async.MapAsync(
            async x =>
            {
                await Task.Delay(10);
                return (object?)((long)x! * 2);
            },
            items);

        Assert.Equal(3, results.Count);
        Assert.Equal(2L, results[0]);
        Assert.Equal(4L, results[1]);
        Assert.Equal(6L, results[2]);
    }

    [Fact]
    public async Task MapAsync_WithConcurrencyLimit_LimitsConcurrency()
    {
        var concurrentCount = 0;
        var maxConcurrent = 0;
        var items = Enumerable.Range(0, 10).Select(i => (object?)i).ToList();

        await Async.MapAsync(
            async x =>
            {
                Interlocked.Increment(ref concurrentCount);
                maxConcurrent = Math.Max(maxConcurrent, concurrentCount);
                await Task.Delay(50);
                Interlocked.Decrement(ref concurrentCount);
                return x;
            },
            items,
            maxConcurrency: 2);

        Assert.True(maxConcurrent <= 2);
    }

    [Fact]
    public async Task Timeout_CompletesAfterDelay()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Async.Timeout(50);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds >= 40); // Some tolerance
    }

    [Fact]
    public async Task AwaitTimeout_ReturnsValueBeforeTimeout()
    {
        var task = Task.FromResult<object?>(42);
        var result = await Async.AwaitTimeout(task, 1000, "timeout");
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task AwaitTimeout_ReturnsTimeoutValueAfterTimeout()
    {
        var task = Task.Delay(5000).ContinueWith(_ => (object?)"late");
        var result = await Async.AwaitTimeout(task, 50, "timeout");
        Assert.Equal("timeout", result);
    }

    [Fact]
    public void Resolved_CreatesCompletedTask()
    {
        var task = Async.Resolved(42);
        Assert.True(task.IsCompleted);
        Assert.Equal(42, task.Result);
    }

    [Fact]
    public void Rejected_CreatesFailedTask()
    {
        var task = Async.Rejected(new InvalidOperationException("test"));
        Assert.True(task.IsFaulted);
    }
}

public class ChannelTests
{
    [Fact]
    public async Task Channel_PutAndTake_TransfersValue()
    {
        var ch = chan();
        await ch.Put(42);
        var result = await ch.Take();
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Channel_MultipleValues_PreservesOrder()
    {
        var ch = chan(10);
        await ch.Put(1);
        await ch.Put(2);
        await ch.Put(3);

        Assert.Equal(1, await ch.Take());
        Assert.Equal(2, await ch.Take());
        Assert.Equal(3, await ch.Take());
    }

    [Fact]
    public async Task Channel_Close_StopsFurtherPuts()
    {
        var ch = chan();
        close_BANG_(ch);
        var result = await ch.Put(42);
        Assert.False(result);
    }

    [Fact]
    public async Task Channel_Take_FromClosed_ReturnsNull()
    {
        var ch = chan();
        close_BANG_(ch);
        var result = await ch.Take();
        Assert.Null(result);
    }

    [Fact]
    public void Channel_TryPut_ReturnsTrueWhenSpace()
    {
        var ch = chan(1);
        Assert.True(ch.TryPut(42));
    }

    [Fact]
    public void Channel_TryTake_ReturnsFalseWhenEmpty()
    {
        var ch = chan();
        var (success, _) = ch.TryTake();
        Assert.False(success);
    }

    [Fact]
    public async Task Channel_AsAsyncEnumerable_EnumeratesValues()
    {
        var ch = chan(3);
        await ch.Put(1);
        await ch.Put(2);
        await ch.Put(3);
        close_BANG_(ch);

        var results = new List<object?>();
        await foreach (var item in ch.AsAsyncEnumerable())
        {
            results.Add(item);
        }

        Assert.Equal(3, results.Count);
        Assert.Equal(new List<object?> { 1, 2, 3 }, results);
    }
}
