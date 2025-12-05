namespace Cljr;

/// <summary>
/// Base interface for async functions that preserves typed delegates for .NET interop.
/// AsyncFn types store the actual Func&lt;..., Task&lt;object?&gt;&gt; delegate internally,
/// enabling clean interop with .NET APIs that expect typed async delegates.
/// </summary>
public interface IAsyncFn : IFn
{
    /// <summary>Returns the typed async delegate for .NET interop</summary>
    Delegate GetTypedDelegate();
}

/// <summary>
/// Exception thrown when a function is called with wrong number of arguments.
/// </summary>
public class ArityException : Exception
{
    public int Got { get; }
    public int Expected { get; }

    public ArityException(int got, int expected)
        : base($"Wrong number of args ({got}) passed to fn expecting {expected}")
    {
        Got = got;
        Expected = expected;
    }
}

/// <summary>Async function wrapper for 0-arg functions</summary>
public sealed class AsyncFn0 : IAsyncFn
{
    private readonly Func<Task<object?>> _fn;

    public AsyncFn0(Func<Task<object?>> fn) => _fn = fn ?? throw new ArgumentNullException(nameof(fn));

    public object? Invoke() => _fn();
    public object? Invoke(object? a) => throw new ArityException(1, 0);
    public object? Invoke(object? a, object? b) => throw new ArityException(2, 0);
    public object? Invoke(object? a, object? b, object? c) => throw new ArityException(3, 0);
    public object? Invoke(object? a, object? b, object? c, object? d) => throw new ArityException(4, 0);
    public object? Invoke(params object?[] args) => args.Length == 0 ? _fn() : throw new ArityException(args.Length, 0);

    public Func<Task<object?>> AsTypedDelegate() => _fn;
    public Delegate GetTypedDelegate() => _fn;
    public static implicit operator Func<Task<object?>>(AsyncFn0 f) => f._fn;

    public override string ToString() => $"#<AsyncFn0@{GetHashCode():x}>";
}

/// <summary>Async function wrapper for 1-arg functions</summary>
public sealed class AsyncFn1 : IAsyncFn
{
    private readonly Func<object?, Task<object?>> _fn;

    public AsyncFn1(Func<object?, Task<object?>> fn) => _fn = fn ?? throw new ArgumentNullException(nameof(fn));

    public object? Invoke() => throw new ArityException(0, 1);
    public object? Invoke(object? a) => _fn(a);
    public object? Invoke(object? a, object? b) => throw new ArityException(2, 1);
    public object? Invoke(object? a, object? b, object? c) => throw new ArityException(3, 1);
    public object? Invoke(object? a, object? b, object? c, object? d) => throw new ArityException(4, 1);
    public object? Invoke(params object?[] args) => args.Length == 1 ? _fn(args[0]) : throw new ArityException(args.Length, 1);

    public Func<object?, Task<object?>> AsTypedDelegate() => _fn;
    public Delegate GetTypedDelegate() => _fn;
    public static implicit operator Func<object?, Task<object?>>(AsyncFn1 f) => f._fn;

    public override string ToString() => $"#<AsyncFn1@{GetHashCode():x}>";
}

/// <summary>Async function wrapper for 2-arg functions</summary>
public sealed class AsyncFn2 : IAsyncFn
{
    private readonly Func<object?, object?, Task<object?>> _fn;

    public AsyncFn2(Func<object?, object?, Task<object?>> fn) => _fn = fn ?? throw new ArgumentNullException(nameof(fn));

    public object? Invoke() => throw new ArityException(0, 2);
    public object? Invoke(object? a) => throw new ArityException(1, 2);
    public object? Invoke(object? a, object? b) => _fn(a, b);
    public object? Invoke(object? a, object? b, object? c) => throw new ArityException(3, 2);
    public object? Invoke(object? a, object? b, object? c, object? d) => throw new ArityException(4, 2);
    public object? Invoke(params object?[] args) => args.Length == 2 ? _fn(args[0], args[1]) : throw new ArityException(args.Length, 2);

    public Func<object?, object?, Task<object?>> AsTypedDelegate() => _fn;
    public Delegate GetTypedDelegate() => _fn;
    public static implicit operator Func<object?, object?, Task<object?>>(AsyncFn2 f) => f._fn;

    public override string ToString() => $"#<AsyncFn2@{GetHashCode():x}>";
}

/// <summary>Async function wrapper for 3-arg functions</summary>
public sealed class AsyncFn3 : IAsyncFn
{
    private readonly Func<object?, object?, object?, Task<object?>> _fn;

    public AsyncFn3(Func<object?, object?, object?, Task<object?>> fn) => _fn = fn ?? throw new ArgumentNullException(nameof(fn));

    public object? Invoke() => throw new ArityException(0, 3);
    public object? Invoke(object? a) => throw new ArityException(1, 3);
    public object? Invoke(object? a, object? b) => throw new ArityException(2, 3);
    public object? Invoke(object? a, object? b, object? c) => _fn(a, b, c);
    public object? Invoke(object? a, object? b, object? c, object? d) => throw new ArityException(4, 3);
    public object? Invoke(params object?[] args) => args.Length == 3 ? _fn(args[0], args[1], args[2]) : throw new ArityException(args.Length, 3);

    public Func<object?, object?, object?, Task<object?>> AsTypedDelegate() => _fn;
    public Delegate GetTypedDelegate() => _fn;
    public static implicit operator Func<object?, object?, object?, Task<object?>>(AsyncFn3 f) => f._fn;

    public override string ToString() => $"#<AsyncFn3@{GetHashCode():x}>";
}

/// <summary>Async function wrapper for 4-arg functions</summary>
public sealed class AsyncFn4 : IAsyncFn
{
    private readonly Func<object?, object?, object?, object?, Task<object?>> _fn;

    public AsyncFn4(Func<object?, object?, object?, object?, Task<object?>> fn) => _fn = fn ?? throw new ArgumentNullException(nameof(fn));

    public object? Invoke() => throw new ArityException(0, 4);
    public object? Invoke(object? a) => throw new ArityException(1, 4);
    public object? Invoke(object? a, object? b) => throw new ArityException(2, 4);
    public object? Invoke(object? a, object? b, object? c) => throw new ArityException(3, 4);
    public object? Invoke(object? a, object? b, object? c, object? d) => _fn(a, b, c, d);
    public object? Invoke(params object?[] args) => args.Length == 4 ? _fn(args[0], args[1], args[2], args[3]) : throw new ArityException(args.Length, 4);

    public Func<object?, object?, object?, object?, Task<object?>> AsTypedDelegate() => _fn;
    public Delegate GetTypedDelegate() => _fn;
    public static implicit operator Func<object?, object?, object?, object?, Task<object?>>(AsyncFn4 f) => f._fn;

    public override string ToString() => $"#<AsyncFn4@{GetHashCode():x}>";
}
