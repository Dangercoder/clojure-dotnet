using System.Collections;
using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// Persistent hash set backed by PersistentHashMap.
/// All set operations delegate to the underlying map.
///
/// Performance: Same as PersistentHashMap - O(log32 n) for all operations.
/// </summary>
public sealed class PersistentHashSet : IPersistentSet, IEditableCollection
{
    public static readonly PersistentHashSet Empty = new(PersistentHashMap.Empty);

    private readonly PersistentHashMap _impl;

    private PersistentHashSet(PersistentHashMap impl) => _impl = impl;

    public int Count => _impl.Count;

    #region Factory Methods

    public static PersistentHashSet Create(params object?[] items)
    {
        if (items.Length == 0) return Empty;

        // Use transient for efficient batch construction
        var tm = (ITransientMap)PersistentHashMap.Empty.AsTransient();
        foreach (var item in items)
            tm = tm.Assoc(item!, item);
        return new PersistentHashSet((PersistentHashMap)tm.Persistent());
    }

    public static PersistentHashSet CreateWithCheck(params object?[] items)
    {
        var map = PersistentHashMap.Empty;
        foreach (var item in items)
        {
            if (map.ContainsKey(item!))
                throw new ArgumentException($"Duplicate key: {item}");
            map = (PersistentHashMap)map.Assoc(item!, item);
        }
        return new PersistentHashSet(map);
    }

    #endregion

    #region Set Operations

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Contains(object key) => _impl.ContainsKey(key);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Get(object key) => _impl.ValAt(key);

    public IPersistentSet Conj(object? o)
    {
        if (Contains(o!)) return this;
        return new PersistentHashSet((PersistentHashMap)_impl.Assoc(o!, o));
    }

    IPersistentCollection IPersistentCollection.Conj(object? o) => Conj(o);

    public IPersistentSet Disjoin(object key)
    {
        if (!Contains(key)) return this;
        return new PersistentHashSet((PersistentHashMap)_impl.Without(key));
    }

    #endregion

    #region Collection Operations

    IPersistentCollection IPersistentCollection.Empty() => PersistentHashSet.Empty;

    public ISeq? Seq() => _impl.Seq() is { } s ? new KeySeq(s) : null;

    public bool Equiv(object? o)
    {
        if (o is IPersistentSet s)
        {
            if (s.Count != Count) return false;
            for (var seq = Seq(); seq != null; seq = seq.Next())
            {
                if (!s.Contains(seq.First()!))
                    return false;
            }
            return true;
        }
        return false;
    }

    public IEnumerator GetEnumerator()
    {
        for (var s = Seq(); s != null; s = s.Next())
            yield return s.First();
    }

    public override bool Equals(object? obj) => Equiv(obj);

    public override int GetHashCode()
    {
        int hash = 0;
        for (var s = Seq(); s != null; s = s.Next())
            hash += s.First()?.GetHashCode() ?? 0;
        return hash;
    }

    public override string ToString() => Core.PrStr(this);

    #endregion

    #region Transient Support

    public ITransientCollection AsTransient() => new TransientHashSet(this);

    #endregion

    #region KeySeq

    /// <summary>Extracts keys from map entry sequence.</summary>
    private sealed class KeySeq : ISeq, Counted
    {
        private readonly ISeq _seq;

        public KeySeq(ISeq seq) => _seq = seq;

        public object? First() => ((IMapEntry)_seq.First()!).Key();
        public ISeq? Next() => _seq.Next() is { } s ? new KeySeq(s) : null;
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

    #endregion

    #region TransientHashSet

    private sealed class TransientHashSet : ITransientSet
    {
        private volatile bool _editable = true;
        private PersistentHashMap _impl;

        public TransientHashSet(PersistentHashSet set) => _impl = set._impl;

        private void EnsureEditable()
        {
            if (!_editable)
                throw new InvalidOperationException("Transient used after persistent!");
        }

        public int Count => _impl.Count;

        public bool Contains(object key)
        {
            EnsureEditable();
            return _impl.ContainsKey(key);
        }

        public ITransientSet Conj(object? val)
        {
            EnsureEditable();
            _impl = (PersistentHashMap)_impl.Assoc(val!, val);
            return this;
        }

        ITransientCollection ITransientCollection.Conj(object? val) => Conj(val);

        public ITransientSet Disjoin(object key)
        {
            EnsureEditable();
            _impl = (PersistentHashMap)_impl.Without(key);
            return this;
        }

        public IPersistentSet Persistent()
        {
            EnsureEditable();
            _editable = false;
            return new PersistentHashSet(_impl);
        }

        IPersistentCollection ITransientCollection.Persistent() => Persistent();
    }

    #endregion
}
