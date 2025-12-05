using System.Collections;
using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// A cons cell - first element plus a seq.
/// Used for lazy prepending without full list construction.
/// </summary>
public sealed class Cons : ISeq, Counted
{
    private readonly object? _first;
    private readonly ISeq? _more;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Cons(object? first, ISeq? more)
    {
        _first = first;
        _more = more;
    }

    public int Count
    {
        get
        {
            int c = 1;
            for (ISeq? s = _more; s != null; s = s.Next())
                c++;
            return c;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? First() => _first;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ISeq? Next() => _more?.Seq();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    ISeq ISeq.Cons(object? o) => new Cons(o, this);

    public ISeq More() => _more ?? PersistentList.Empty;

    public IPersistentCollection Empty() => PersistentList.Empty;

    IPersistentCollection IPersistentCollection.Conj(object? o) => new Cons(o, this);

    public ISeq? Seq() => this;

    public bool Equiv(object? o) => CoreFunctions.SeqEquals(this, o);

    public IEnumerator GetEnumerator()
    {
        yield return _first;
        if (_more != null)
        {
            foreach (var item in _more)
                yield return item;
        }
    }

    public override bool Equals(object? obj) => Equiv(obj);

    public override int GetHashCode()
    {
        int hash = 1;
        hash = 31 * hash + (_first?.GetHashCode() ?? 0);
        if (_more != null)
        {
            foreach (var item in _more)
                hash = 31 * hash + (item?.GetHashCode() ?? 0);
        }
        return hash;
    }

    public override string ToString() => CoreFunctions.PrStr(this);
}
