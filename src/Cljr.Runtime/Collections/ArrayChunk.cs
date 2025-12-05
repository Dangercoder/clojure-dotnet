using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// A chunk backed by an array, used for chunked sequence processing.
/// Standard chunk size is 32 elements (matching Clojure JVM).
/// </summary>
public sealed class ArrayChunk : IChunk
{
    public const int CHUNK_SIZE = 32;

    private readonly object?[] _array;
    private readonly int _off;
    private readonly int _end;

    public ArrayChunk(object?[] array) : this(array, 0, array.Length) { }

    public ArrayChunk(object?[] array, int off) : this(array, off, array.Length) { }

    public ArrayChunk(object?[] array, int off, int end)
    {
        _array = array;
        _off = off;
        _end = end;
    }

    public int Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _end - _off;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Nth(int i) => _array[_off + i];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Nth(int i, object? notFound)
    {
        int idx = _off + i;
        return idx >= _off && idx < _end ? _array[idx] : notFound;
    }

    public IChunk DropFirst()
    {
        if (_off >= _end)
            throw new InvalidOperationException("DropFirst on empty chunk");
        return new ArrayChunk(_array, _off + 1, _end);
    }

    public object? Reduce(Func<object?, object?, object?> f, object? init)
    {
        var acc = init;
        for (int i = _off; i < _end; i++)
        {
            acc = f(acc, _array[i]);
            if (acc is Reduced r)
                return r.Value;
        }
        return acc;
    }
}
