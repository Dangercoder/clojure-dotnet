using Cljr;
using static Cljr.Core;

namespace Cljr.Runtime.Tests;

public class VolatileTests
{
    [Fact]
    public void Volatile_CreatesWithInitialValue()
    {
        var v = volatile_BANG_(42);
        Assert.Equal(42, deref(v));
    }

    [Fact]
    public void VReset_SetsNewValue()
    {
        var v = volatile_BANG_(10);
        var result = vreset_BANG_(v, 20);
        Assert.Equal(20, result);
        Assert.Equal(20, deref(v));
    }

    [Fact]
    public void VSwap_AppliesFunction()
    {
        var v = volatile_BANG_(10L);
        var result = vswap_BANG_(v, x => (long)x! * 2);
        Assert.Equal(20L, result);
        Assert.Equal(20L, deref(v));
    }
}

public class DelayTests
{
    [Fact]
    public void Delay_DoesNotEvaluateUntilDeref()
    {
        var evaluated = false;
        var d = delay(() => { evaluated = true; return 42; });

        Assert.False(evaluated);
        Assert.False(realized_QMARK_(d));

        var result = deref(d);

        Assert.True(evaluated);
        Assert.True(realized_QMARK_(d));
        Assert.Equal(42, result);
    }

    [Fact]
    public void Delay_EvaluatesOnlyOnce()
    {
        var evalCount = 0;
        var d = delay(() => { evalCount++; return 42; });

        deref(d);
        deref(d);
        deref(d);

        Assert.Equal(1, evalCount);
    }

    [Fact]
    public void Force_EvaluatesDelay()
    {
        var d = delay(() => 42);
        Assert.Equal(42, force(d));
    }

    [Fact]
    public void Force_ReturnsNonDelayAsIs()
    {
        Assert.Equal(42, force(42));
        Assert.Equal("hello", force("hello"));
        Assert.Null(force(null));
    }

    [Fact]
    public void Delay_PropagatesException()
    {
        var d = delay<object?>(() => throw new InvalidOperationException("test error"));
        Assert.Throws<InvalidOperationException>(() => deref(d));
    }

    private static Delay delay<T>(Func<T> fn) => new Delay(() => fn());
}
