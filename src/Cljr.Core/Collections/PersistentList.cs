using System.Collections;
using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// Persistent linked list with O(1) cons and first/rest.
/// Uses structural sharing - rest is shared between lists.
/// </summary>
public sealed class PersistentList : ISeq, Counted
{
    public static readonly PersistentList Empty = new PersistentList();

    private readonly object? _first;
    private readonly PersistentList? _rest;
    private readonly int _count;

    private PersistentList()
    {
        _first = null;
        _rest = null;
        _count = 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private PersistentList(object? first, PersistentList? rest)
    {
        _first = first;
        _rest = rest;
        _count = (rest?._count ?? 0) + 1;
    }

    /// <summary>
    /// Creates a list from elements (first element becomes head).
    /// </summary>
    public static PersistentList Create(params object?[] items)
    {
        var result = Empty;
        for (int i = items.Length - 1; i >= 0; i--)
            result = new PersistentList(items[i], result);
        return result;
    }

    /// <summary>
    /// Creates a list from an enumerable.
    /// </summary>
    public static PersistentList Create(IEnumerable<object?> items)
    {
        var array = items as object?[] ?? items.ToArray();
        return Create(array);
    }

    public int Count => _count;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? First() => _first;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ISeq? Next() => _rest?._count > 0 ? _rest : null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ISeq Cons(object? o) => new PersistentList(o, this);

    public ISeq More() => _rest ?? Empty;

    IPersistentCollection IPersistentCollection.Empty() => PersistentList.Empty;

    IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);

    public ISeq? Seq() => _count > 0 ? this : null;

    public bool Equiv(object? o) => CoreFunctions.SeqEquals(this, o);

    public IEnumerator GetEnumerator()
    {
        for (PersistentList? node = this; node != null && node._count > 0; node = node._rest)
            yield return node._first;
    }

    public override bool Equals(object? obj) => Equiv(obj);

    public override int GetHashCode()
    {
        int hash = 1;
        for (PersistentList? node = this; node != null && node._count > 0; node = node._rest)
            hash = 31 * hash + (node._first?.GetHashCode() ?? 0);
        return hash;
    }

    public override string ToString() => CoreFunctions.PrStr(this);
}
