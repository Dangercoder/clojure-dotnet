using Cljr;
using static Cljr.Core;

namespace Cljr.Runtime.Tests;

public class AtomTests
{
    [Fact]
    public void Atom_CreatesWithInitialValue()
    {
        var a = atom(42);
        Assert.Equal(42, deref(a));
    }

    [Fact]
    public void Swap_AppliesFunctionToValue()
    {
        var a = atom(10L);
        var result = swap_BANG_(a, x => (long)x! + 5L);
        Assert.Equal(15L, result);
        Assert.Equal(15L, deref(a));
    }

    [Fact]
    public void Reset_SetsNewValue()
    {
        var a = atom(10);
        var result = reset_BANG_(a, 20);
        Assert.Equal(20, result);
        Assert.Equal(20, deref(a));
    }

    [Fact]
    public void SwapVals_ReturnsOldAndNewValues()
    {
        var a = atom(10L);
        var result = swap_vals_BANG_(a, x => (long)x! * 2L);
        Assert.Equal(2, result.Count);
        Assert.Equal(10L, result[0]);
        Assert.Equal(20L, result[1]);
    }

    [Fact]
    public void ResetVals_ReturnsOldAndNewValues()
    {
        var a = atom(10);
        var result = reset_vals_BANG_(a, 20);
        Assert.Equal(2, result.Count);
        Assert.Equal(10, result[0]);
        Assert.Equal(20, result[1]);
    }

    [Fact]
    public void CompareAndSet_SucceedsWhenMatches()
    {
        var a = atom(10);
        var result = compare_and_set_BANG_(a, 10, 20);
        Assert.True(result);
        Assert.Equal(20, deref(a));
    }

    [Fact]
    public void CompareAndSet_FailsWhenNoMatch()
    {
        var a = atom(10);
        var result = compare_and_set_BANG_(a, 15, 20);
        Assert.False(result);
        Assert.Equal(10, deref(a));
    }

    [Fact]
    public void Atom_WithValidator_AcceptsValidValues()
    {
        var a = atom(10L, x => (long)x! > 0);
        reset_BANG_(a, 20L);
        Assert.Equal(20L, deref(a));
    }

    [Fact]
    public void Atom_WithValidator_RejectsInvalidValues()
    {
        var a = atom(10L, x => (long)x! > 0);
        Assert.Throws<InvalidOperationException>(() => reset_BANG_(a, -5L));
    }

    [Fact]
    public void Atom_Swap_IsThreadSafe()
    {
        var a = atom(0L);
        var tasks = Enumerable.Range(0, 100).Select(_ => Task.Run(() =>
        {
            for (int i = 0; i < 100; i++)
            {
                swap_BANG_(a, x => (long)x! + 1L);
            }
        })).ToArray();

        Task.WaitAll(tasks);
        Assert.Equal(10000L, deref(a));
    }

    [Fact]
    public void AddWatch_NotifiesOnChange()
    {
        var a = atom(10);
        var notified = false;
        object? oldValue = null;
        object? newValue = null;

        add_watch(a, "test", (key, reference, oldV, newV) =>
        {
            notified = true;
            oldValue = oldV;
            newValue = newV;
        });

        reset_BANG_(a, 20);

        Assert.True(notified);
        Assert.Equal(10, oldValue);
        Assert.Equal(20, newValue);
    }

    [Fact]
    public void RemoveWatch_StopsNotifications()
    {
        var a = atom(10);
        var notifyCount = 0;

        add_watch(a, "test", (_, _, _, _) => notifyCount++);
        reset_BANG_(a, 20);
        Assert.Equal(1, notifyCount);

        remove_watch(a, "test");
        reset_BANG_(a, 30);
        Assert.Equal(1, notifyCount); // Still 1, no new notification
    }
}
