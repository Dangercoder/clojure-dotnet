using System.Collections;
using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// Lazy sequence - delays computation until elements are accessed.
/// Used by map, filter, and other sequence functions to enable
/// infinite sequences and avoid unnecessary computation.
///
/// Thread-safe: Uses double-checked locking for lazy initialization.
/// </summary>
public sealed class LazySeq : ISeq, IPending
{
    private Func<object?>? _fn;
    private object? _sv;
    private ISeq? _s;

    public LazySeq(Func<object?> fn)
    {
        _fn = fn;
    }

    private LazySeq(ISeq? s)
    {
        _fn = null;
        _s = s;
    }

    /// <summary>
    /// Returns true if the sequence has been realized.
    /// </summary>
    public bool IsRealized => _fn == null;

    /// <summary>
    /// Forces realization of the lazy sequence.
    /// Thread-safe via double-checked locking.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private ISeq? Sval()
    {
        if (_fn != null)
        {
            lock (this)
            {
                if (_fn != null)
                {
                    _sv = _fn();
                    _fn = null;
                }
            }
        }
        if (_sv != null)
            return _sv as ISeq ?? RT.Seq(_sv);
        return _s;
    }

    public ISeq? Seq()
    {
        Sval();
        if (_sv != null)
        {
            var ls = _sv;
            _sv = null;
            while (ls is LazySeq lazy)
            {
                ls = lazy.Sval();
            }
            _s = RT.Seq(ls);
        }
        return _s;
    }

    public object? First()
    {
        Seq();
        return _s?.First();
    }

    public ISeq? Next()
    {
        Seq();
        return _s?.Next();
    }

    public ISeq Cons(object? o) => new Cons(o, Seq());

    public ISeq More()
    {
        Seq();
        return _s?.More() ?? PersistentList.Empty;
    }

    public int Count
    {
        get
        {
            int c = 0;
            for (ISeq? s = Seq(); s != null; s = s.Next())
                c++;
            return c;
        }
    }

    public IPersistentCollection Empty() => PersistentList.Empty;

    IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);

    public bool Equiv(object? o) => Core.SeqEquals(Seq(), o);

    public IEnumerator GetEnumerator()
    {
        for (ISeq? s = Seq(); s != null; s = s.Next())
            yield return s.First();
    }

    public override bool Equals(object? obj) => Equiv(obj);

    public override int GetHashCode()
    {
        var s = Seq();
        if (s == null) return 1;
        return s.GetHashCode();
    }

    public override string ToString() => Core.PrStr(this);
}

/// <summary>
/// Marker interface for pending/realized computation.
/// </summary>
public interface IPending
{
    bool IsRealized { get; }
}

/// <summary>
/// Runtime helpers for sequence operations.
/// </summary>
internal static class RT
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ISeq? Seq(object? coll)
    {
        if (coll == null) return null;
        if (coll is ISeq s) return s.Seq();
        if (coll is Seqable seqable) return seqable.Seq();
        if (coll is string str) return StringSeq.Create(str);
        if (coll is IEnumerable enumerable) return EnumeratorSeq.Create(enumerable.GetEnumerator());
        throw new ArgumentException($"Don't know how to create ISeq from: {coll.GetType()}");
    }
}

/// <summary>
/// Sequence over a string's characters.
/// </summary>
public sealed class StringSeq : ISeq, Indexed, Counted
{
    private readonly string _s;
    private readonly int _i;

    private StringSeq(string s, int i)
    {
        _s = s;
        _i = i;
    }

    public static ISeq? Create(string s) =>
        s.Length > 0 ? new StringSeq(s, 0) : null;

    public object? First() => _s[_i];
    public ISeq? Next() => _i + 1 < _s.Length ? new StringSeq(_s, _i + 1) : null;
    public ISeq Cons(object? o) => new Cons(o, this);
    public ISeq More() => Next() ?? PersistentList.Empty;
    public int Count => _s.Length - _i;

    public object? Nth(int i)
    {
        int idx = _i + i;
        if (idx < 0 || idx >= _s.Length)
            throw new ArgumentOutOfRangeException(nameof(i));
        return _s[idx];
    }

    public object? Nth(int i, object? notFound)
    {
        int idx = _i + i;
        if (idx < 0 || idx >= _s.Length)
            return notFound;
        return _s[idx];
    }

    public IPersistentCollection Empty() => PersistentList.Empty;
    IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);
    public ISeq? Seq() => this;
    public bool Equiv(object? o) => Core.SeqEquals(this, o);

    public IEnumerator GetEnumerator()
    {
        for (int i = _i; i < _s.Length; i++)
            yield return _s[i];
    }
}

/// <summary>
/// Sequence over an IEnumerator.
/// Caches values for multiple traversals.
/// </summary>
public sealed class EnumeratorSeq : ISeq
{
    private readonly IEnumerator _iter;
    private readonly object? _first;
    private ISeq? _rest;
    private volatile bool _restComputed;

    private EnumeratorSeq(IEnumerator iter, object? first)
    {
        _iter = iter;
        _first = first;
    }

    public static ISeq? Create(IEnumerator iter)
    {
        if (!iter.MoveNext()) return null;
        return new EnumeratorSeq(iter, iter.Current);
    }

    public object? First() => _first;

    public ISeq? Next()
    {
        if (!_restComputed)
        {
            lock (this)
            {
                if (!_restComputed)
                {
                    if (_iter.MoveNext())
                        _rest = new EnumeratorSeq(_iter, _iter.Current);
                    _restComputed = true;
                }
            }
        }
        return _rest;
    }

    public ISeq Cons(object? o) => new Cons(o, this);
    public ISeq More() => Next() ?? PersistentList.Empty;

    public int Count
    {
        get
        {
            int c = 0;
            for (ISeq? s = this; s != null; s = s.Next()) c++;
            return c;
        }
    }

    public IPersistentCollection Empty() => PersistentList.Empty;
    IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);
    public ISeq? Seq() => this;
    public bool Equiv(object? o) => Core.SeqEquals(this, o);

    public IEnumerator GetEnumerator()
    {
        for (ISeq? s = this; s != null; s = s.Next())
            yield return s.First();
    }
}
