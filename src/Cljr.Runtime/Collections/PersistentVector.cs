using System.Buffers;
using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// High-performance persistent vector using a 32-way branching trie with tail optimization.
///
/// Structure:
/// - Root: 32-way trie storing most elements
/// - Tail: Up to 32 elements for O(1) amortized append
/// - Shift: Depth * 5 (where 5 = log2(32))
///
/// Performance:
/// - nth: O(log32 n) - effectively O(1) for practical sizes
/// - assoc: O(log32 n) with structural sharing
/// - conj: O(1) amortized via tail optimization
/// - pop: O(log32 n)
///
/// Based on Bagwell's Ideal Hash Trees paper and Clojure's implementation.
/// </summary>
public sealed class PersistentVector : IPersistentVector, IEditableCollection, Reversible
{
    private const int Bits = 5;
    private const int Width = 1 << Bits; // 32
    private const int Mask = Width - 1;  // 0x1F

    public static readonly PersistentVector Empty = new(0, Bits, Node.EmptyNode, []);

    private readonly int _count;
    private readonly int _shift;
    private readonly Node _root;
    private readonly object?[] _tail;

    private PersistentVector(int count, int shift, Node root, object?[] tail)
    {
        _count = count;
        _shift = shift;
        _root = root;
        _tail = tail;
    }

    public int Count => _count;

    #region Factory Methods

    public static PersistentVector Create(params object?[] items)
    {
        if (items.Length == 0) return Empty;
        if (items.Length <= Width)
        {
            var tail = new object?[items.Length];
            Array.Copy(items, tail, items.Length);
            return new PersistentVector(items.Length, Bits, Node.EmptyNode, tail);
        }

        // Use transient for efficient batch construction
        var tv = Empty.AsTransient();
        foreach (var item in items)
            tv = tv.Conj(item);
        return (PersistentVector)tv.Persistent();
    }

    /// <summary>
    /// Creates vector from span - high-performance path.
    /// </summary>
    public static PersistentVector Create(ReadOnlySpan<object?> items)
    {
        if (items.Length == 0) return Empty;
        if (items.Length <= Width)
        {
            var tail = new object?[items.Length];
            items.CopyTo(tail);
            return new PersistentVector(items.Length, Bits, Node.EmptyNode, tail);
        }

        var tv = Empty.AsTransient();
        foreach (var item in items)
            tv = tv.Conj(item);
        return (PersistentVector)tv.Persistent();
    }

    /// <summary>
    /// High-performance bulk creation from pre-computed array.
    /// Builds the trie structure directly - O(n) with minimal overhead.
    /// Uses array slicing to avoid copying when possible.
    /// </summary>
    public static PersistentVector CreateFromObjectArray(object?[] items)
    {
        int count = items.Length;
        if (count == 0) return Empty;

        // Small vectors: tail only, no trie needed
        if (count <= Width)
        {
            // Must copy since tail could be modified
            var tail = new object?[count];
            Array.Copy(items, tail, count);
            return new PersistentVector(count, Bits, Node.EmptyNode, tail);
        }

        // Calculate tail offset: the last chunk goes in tail
        int tailOffset = ((count - 1) >> Bits) << Bits;
        int tailSize = count - tailOffset;

        // Create tail array
        var tailArr = new object?[tailSize];
        Array.Copy(items, tailOffset, tailArr, 0, tailSize);

        // Build leaf nodes
        int numLeaves = tailOffset >> Bits;
        var leaves = new Node[numLeaves];

        for (int i = 0; i < numLeaves; i++)
        {
            var arr = new object?[Width];
            Array.Copy(items, i * Width, arr, 0, Width);
            leaves[i] = new Node(arr);
        }

        // Build tree bottom-up
        Node[] level = leaves;
        int shift = Bits;

        while (level.Length > Width)
        {
            int nextSize = (level.Length + Width - 1) >> Bits;
            var nextLevel = new Node[nextSize];

            for (int i = 0; i < nextSize; i++)
            {
                int start = i * Width;
                int end = Math.Min(start + Width, level.Length);
                int childCount = end - start;
                var children = new object?[childCount];
                for (int j = 0; j < childCount; j++)
                    children[j] = level[start + j];
                nextLevel[i] = new Node(children);
            }

            level = nextLevel;
            shift += Bits;
        }

        // Final root node
        Node root;
        if (level.Length == 1)
        {
            root = level[0];
        }
        else
        {
            var rootChildren = new object?[level.Length];
            for (int i = 0; i < level.Length; i++)
                rootChildren[i] = level[i];
            root = new Node(rootChildren);
        }

        return new PersistentVector(count, shift, root, tailArr);
    }

    #endregion

    #region Indexed Access

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int TailOffset()
    {
        if (_count < Width) return 0;
        return ((_count - 1) >> Bits) << Bits;
    }

    /// <summary>
    /// Returns the array containing element at index i.
    /// Used for bulk operations on contiguous ranges.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadOnlySpan<object?> ArrayFor(int i)
    {
        if (i >= 0 && i < _count)
        {
            if (i >= TailOffset())
                return _tail.AsSpan();

            var node = _root;
            for (int level = _shift; level > 0; level -= Bits)
                node = (Node)node.Array[(i >> level) & Mask]!;
            return node.Array.AsSpan();
        }
        throw new ArgumentOutOfRangeException(nameof(i));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Nth(int i)
    {
        var arr = ArrayFor(i);
        return arr[i & Mask];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? Nth(int i, object? notFound)
    {
        if (i >= 0 && i < _count)
            return Nth(i);
        return notFound;
    }

    public object? ValAt(object key) => ValAt(key, null);

    public object? ValAt(object key, object? notFound)
    {
        if (key is int i)
            return Nth(i, notFound);
        return notFound;
    }

    public bool ContainsKey(object key) =>
        key is int i && i >= 0 && i < _count;

    public IMapEntry? EntryAt(object key)
    {
        if (key is int i && i >= 0 && i < _count)
            return new MapEntry(i, Nth(i));
        return null;
    }

    public object? Peek() => _count > 0 ? Nth(_count - 1) : null;

    #endregion

    #region Persistent Operations

    Associative Associative.Assoc(object key, object? val)
    {
        if (key is int i) return AssocN(i, val);
        throw new ArgumentException("Key must be integer", nameof(key));
    }

    public IPersistentVector AssocN(int i, object? val)
    {
        if (i >= 0 && i < _count)
        {
            if (i >= TailOffset())
            {
                var newTail = new object?[_tail.Length];
                Array.Copy(_tail, newTail, _tail.Length);
                newTail[i & Mask] = val;
                return new PersistentVector(_count, _shift, _root, newTail);
            }

            return new PersistentVector(_count, _shift, DoAssoc(_shift, _root, i, val), _tail);
        }
        if (i == _count)
            return Conj(val);
        throw new ArgumentOutOfRangeException(nameof(i));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Node DoAssoc(int level, Node node, int i, object? val)
    {
        var newArray = new object?[node.Array.Length];
        Array.Copy(node.Array, newArray, node.Array.Length);
        var ret = new Node(newArray);

        if (level == 0)
        {
            ret.Array[i & Mask] = val;
        }
        else
        {
            int subidx = (i >> level) & Mask;
            ret.Array[subidx] = DoAssoc(level - Bits, (Node)node.Array[subidx]!, i, val);
        }
        return ret;
    }

    public IPersistentVector Conj(object? o)
    {
        // Room in tail?
        if (_count - TailOffset() < Width)
        {
            var newTail = new object?[_tail.Length + 1];
            Array.Copy(_tail, newTail, _tail.Length);
            newTail[_tail.Length] = o;
            return new PersistentVector(_count + 1, _shift, _root, newTail);
        }

        // Full tail, push into tree
        var tailNode = new Node(_tail);
        int newShift = _shift;
        Node newRoot;

        // Overflow root?
        if ((_count >> Bits) > (1 << _shift))
        {
            newRoot = new Node([_root, NewPath(_shift, tailNode)]);
            newShift += Bits;
        }
        else
        {
            newRoot = PushTail(_shift, _root, tailNode);
        }

        return new PersistentVector(_count + 1, newShift, newRoot, [o]);
    }

    IPersistentCollection IPersistentCollection.Conj(object? o) => Conj(o);

    private Node PushTail(int level, Node parent, Node tailNode)
    {
        int subidx = ((_count - 1) >> level) & Mask;
        var newArray = new object?[parent.Array.Length + (subidx >= parent.Array.Length ? 1 : 0)];
        Array.Copy(parent.Array, newArray, parent.Array.Length);
        var ret = new Node(newArray);

        Node nodeToInsert;
        if (level == Bits)
        {
            nodeToInsert = tailNode;
        }
        else
        {
            var child = subidx < parent.Array.Length ? (Node?)parent.Array[subidx] : null;
            nodeToInsert = child != null
                ? PushTail(level - Bits, child, tailNode)
                : NewPath(level - Bits, tailNode);
        }
        ret.Array[subidx] = nodeToInsert;
        return ret;
    }

    private static Node NewPath(int level, Node node)
    {
        if (level == 0)
            return node;
        return new Node([NewPath(level - Bits, node)]);
    }

    public IPersistentVector Pop()
    {
        if (_count == 0)
            throw new InvalidOperationException("Can't pop empty vector");
        if (_count == 1)
            return Empty;

        // Pop from tail
        if (_count - TailOffset() > 1)
        {
            var newTail = new object?[_tail.Length - 1];
            Array.Copy(_tail, newTail, newTail.Length);
            return new PersistentVector(_count - 1, _shift, _root, newTail);
        }

        // Need to pop tail node from tree
        var newTailArr = ArrayFor(_count - 2);
        var newRoot = PopTail(_shift, _root);
        int newShift = _shift;

        if (newRoot == null)
            newRoot = Node.EmptyNode;
        if (_shift > Bits && newRoot.Array.Length == 1)
        {
            newRoot = (Node)newRoot.Array[0]!;
            newShift -= Bits;
        }

        return new PersistentVector(_count - 1, newShift, newRoot, newTailArr.ToArray());
    }

    private Node? PopTail(int level, Node node)
    {
        int subidx = ((_count - 2) >> level) & Mask;
        if (level > Bits)
        {
            var newChild = PopTail(level - Bits, (Node)node.Array[subidx]!);
            if (newChild == null && subidx == 0)
                return null;

            var newArray = new object?[subidx + (newChild != null ? 1 : 0)];
            Array.Copy(node.Array, newArray, subidx);
            if (newChild != null)
                newArray[subidx] = newChild;
            return new Node(newArray);
        }
        else if (subidx == 0)
        {
            return null;
        }
        else
        {
            var newArray = new object?[subidx];
            Array.Copy(node.Array, newArray, subidx);
            return new Node(newArray);
        }
    }

    public IPersistentVector SubVec(int start, int end)
    {
        if (start < 0 || end > _count || start > end)
            throw new ArgumentOutOfRangeException();
        if (start == end)
            return Empty;
        if (start == 0 && end == _count)
            return this;

        // For now, create new vector. Could optimize with SubVector wrapper.
        var tv = Empty.AsTransient();
        for (int i = start; i < end; i++)
            tv = tv.Conj(Nth(i));
        return (IPersistentVector)tv.Persistent();
    }

    #endregion

    #region Transient Support

    public ITransientCollection AsTransient() => new TransientVector(this);

    /// <summary>
    /// Internal method for high-performance paths that need direct transient access.
    /// </summary>
    internal static TransientVector CreateTransientFast() => new TransientVector(Empty);

    #endregion

    #region Seq and Enumeration

    public ISeq? Seq() => _count > 0 ? new ChunkedSeq(this, 0, 0) : null;

    public ISeq RSeq() => _count > 0 ? new ReverseSeq(this, _count - 1) : PersistentList.Empty;

    IPersistentCollection IPersistentCollection.Empty() => PersistentVector.Empty;

    public bool Equiv(object? o)
    {
        if (o is IPersistentVector v)
        {
            if (v.Count != _count) return false;
            for (int i = 0; i < _count; i++)
                if (!Core.Equals(Nth(i), v.Nth(i)))
                    return false;
            return true;
        }
        return Core.SeqEquals(this, o);
    }

    public int CompareTo(object? obj)
    {
        if (obj is IPersistentVector v)
        {
            int c = _count.CompareTo(v.Count);
            if (c != 0) return c;
            for (int i = 0; i < _count; i++)
            {
                var a = Nth(i);
                var b = v.Nth(i);
                if (a is IComparable ca)
                {
                    c = ca.CompareTo(b);
                    if (c != 0) return c;
                }
            }
            return 0;
        }
        throw new ArgumentException("Cannot compare");
    }

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return Nth(i);
    }

    public override bool Equals(object? obj) => Equiv(obj);

    public override int GetHashCode()
    {
        int hash = 1;
        for (int i = 0; i < _count; i++)
            hash = 31 * hash + (Nth(i)?.GetHashCode() ?? 0);
        return hash;
    }

    public override string ToString() => Core.PrStr(this);

    #endregion

    #region Node

    /// <summary>
    /// Internal node of the 32-way trie.
    /// Uses object array for polymorphism (can hold Node or values).
    /// </summary>
    internal sealed class Node
    {
        public static readonly Node EmptyNode = new([]);
        public readonly object?[] Array;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public Node(object?[] array) => Array = array;
    }

    #endregion

    #region ChunkedSeq

    /// <summary>
    /// Chunked sequence for efficient iteration.
    /// Iterates in 32-element chunks to minimize trie traversals.
    /// </summary>
    private sealed class ChunkedSeq : ISeq, Counted
    {
        private readonly PersistentVector _vec;
        private readonly object?[] _node;
        private readonly int _i;
        private readonly int _offset;

        public ChunkedSeq(PersistentVector vec, int i, int offset)
        {
            _vec = vec;
            _node = vec.ArrayFor(i).ToArray();
            _i = i;
            _offset = offset;
        }

        private ChunkedSeq(PersistentVector vec, object?[] node, int i, int offset)
        {
            _vec = vec;
            _node = node;
            _i = i;
            _offset = offset;
        }

        public object? First() => _node[_offset];

        public ISeq? Next()
        {
            if (_offset + 1 < _node.Length)
                return new ChunkedSeq(_vec, _node, _i, _offset + 1);
            return ChunkedNext();
        }

        public ISeq Cons(object? o) => new Cons(o, this);

        public ISeq More() => Next() ?? PersistentList.Empty;

        public int Count => _vec._count - (_i + _offset);

        public IPersistentCollection Empty() => PersistentList.Empty;

        IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);

        public ISeq? Seq() => this;

        public bool Equiv(object? o) => Core.SeqEquals(this, o);

        private ISeq? ChunkedNext()
        {
            int nextI = _i + _node.Length;
            if (nextI < _vec._count)
                return new ChunkedSeq(_vec, nextI, 0);
            return null;
        }

        public IEnumerator GetEnumerator()
        {
            for (int i = _i + _offset; i < _vec._count; i++)
                yield return _vec.Nth(i);
        }
    }

    #endregion

    #region ReverseSeq (Reverse)

    private sealed class ReverseSeq : ISeq, Counted
    {
        private readonly PersistentVector _vec;
        private readonly int _i;

        public ReverseSeq(PersistentVector vec, int i)
        {
            _vec = vec;
            _i = i;
        }

        public object? First() => _vec.Nth(_i);
        public ISeq? Next() => _i > 0 ? new ReverseSeq(_vec, _i - 1) : null;
        public ISeq Cons(object? o) => new Cons(o, this);
        public ISeq More() => Next() ?? PersistentList.Empty;
        public int Count => _i + 1;
        public IPersistentCollection Empty() => PersistentList.Empty;
        IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);
        public ISeq? Seq() => this;
        public bool Equiv(object? o) => Core.SeqEquals(this, o);
        public IEnumerator GetEnumerator()
        {
            for (int i = _i; i >= 0; i--)
                yield return _vec.Nth(i);
        }
    }

    #endregion

    #region TransientVector

    /// <summary>
    /// Mutable transient vector for batch operations.
    /// 10x+ faster than persistent operations for bulk construction.
    ///
    /// Thread-safety: Must be used from single thread only.
    /// Call Persistent() when done to get back immutable vector.
    /// </summary>
    internal sealed class TransientVector : ITransientVector
    {
        private volatile bool _editable = true;
        private int _count;
        private int _shift;
        private Node _root;
        private object?[] _tail;

        public TransientVector(PersistentVector v)
        {
            _count = v._count;
            _shift = v._shift;
            _root = v._root;
            _tail = new object?[Width];
            Array.Copy(v._tail, _tail, v._tail.Length);
        }

        private void EnsureEditable()
        {
            if (!_editable)
                throw new InvalidOperationException("Transient used after persistent!");
        }

        public int Count => _count;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int TailOffset()
        {
            if (_count < Width) return 0;
            return ((_count - 1) >> Bits) << Bits;
        }

        public object? Nth(int i)
        {
            EnsureEditable();
            if (i >= 0 && i < _count)
            {
                if (i >= TailOffset())
                    return _tail[i & Mask];
                var node = _root;
                for (int level = _shift; level > 0; level -= Bits)
                    node = (Node)node.Array[(i >> level) & Mask]!;
                return node.Array[i & Mask];
            }
            throw new ArgumentOutOfRangeException(nameof(i));
        }

        public object? Nth(int i, object? notFound)
        {
            EnsureEditable();
            if (i >= 0 && i < _count)
                return Nth(i);
            return notFound;
        }

        public ITransientVector AssocN(int i, object? val)
        {
            EnsureEditable();
            if (i >= 0 && i < _count)
            {
                if (i >= TailOffset())
                {
                    _tail[i & Mask] = val;
                    return this;
                }
                _root = DoAssocMut(_shift, _root, i, val);
                return this;
            }
            if (i == _count)
                return Conj(val);
            throw new ArgumentOutOfRangeException(nameof(i));
        }

        private Node DoAssocMut(int level, Node node, int i, object? val)
        {
            if (level == 0)
            {
                node.Array[i & Mask] = val;
            }
            else
            {
                int subidx = (i >> level) & Mask;
                node.Array[subidx] = DoAssocMut(level - Bits, (Node)node.Array[subidx]!, i, val);
            }
            return node;
        }

        public ITransientVector Conj(object? val)
        {
            EnsureEditable();
            int tailIdx = _count - TailOffset();

            if (tailIdx < Width)
            {
                _tail[tailIdx] = val;
                _count++;
                return this;
            }

            // Full tail, push into tree
            var newTail = new object?[Width];
            newTail[0] = val;
            var tailNode = new Node(_tail);
            _tail = newTail;

            if ((_count >> Bits) > (1 << _shift))
            {
                var newRoot = new Node([_root, NewPath(_shift, tailNode)]);
                _shift += Bits;
                _root = newRoot;
            }
            else
            {
                _root = PushTailMut(_shift, _root, tailNode);
            }

            _count++;
            return this;
        }

        ITransientCollection ITransientCollection.Conj(object? val) => Conj(val);

        /// <summary>
        /// Fast path for internal use - skips volatile EnsureEditable() check.
        /// ONLY safe when: transient is local, never escapes, and caller guarantees validity.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void ConjFast(object? val)
        {
            int tailIdx = _count - TailOffset();

            if (tailIdx < Width)
            {
                _tail[tailIdx] = val;
                _count++;
                return;
            }

            // Full tail, push into tree
            var newTail = new object?[Width];
            newTail[0] = val;
            var tailNode = new Node(_tail);
            _tail = newTail;

            if ((_count >> Bits) > (1 << _shift))
            {
                var newRoot = new Node([_root, NewPath(_shift, tailNode)]);
                _shift += Bits;
                _root = newRoot;
            }
            else
            {
                _root = PushTailMut(_shift, _root, tailNode);
            }

            _count++;
        }

        private Node PushTailMut(int level, Node parent, Node tailNode)
        {
            int subidx = ((_count - 1) >> level) & Mask;
            Node nodeToInsert;

            if (level == Bits)
            {
                nodeToInsert = tailNode;
            }
            else
            {
                var child = subidx < parent.Array.Length ? (Node?)parent.Array[subidx] : null;
                nodeToInsert = child != null
                    ? PushTailMut(level - Bits, child, tailNode)
                    : NewPath(level - Bits, tailNode);
            }

            if (subidx >= parent.Array.Length)
            {
                var newArray = new object?[subidx + 1];
                Array.Copy(parent.Array, newArray, parent.Array.Length);
                parent = new Node(newArray);
            }
            parent.Array[subidx] = nodeToInsert;
            return parent;
        }

        public ITransientVector Pop()
        {
            EnsureEditable();
            if (_count == 0)
                throw new InvalidOperationException("Can't pop empty vector");
            if (_count == 1)
            {
                _count = 0;
                return this;
            }

            int tailIdx = (_count - 1) & Mask;
            if (tailIdx > 0)
            {
                _count--;
                return this;
            }

            // Pop from tree
            var newTail = ArrayForMut(_count - 2);
            var newRoot = PopTailMut(_shift, _root);
            int newShift = _shift;

            if (newRoot == null)
                newRoot = Node.EmptyNode;
            if (_shift > Bits && newRoot.Array.Length == 1)
            {
                newRoot = (Node)newRoot.Array[0]!;
                newShift -= Bits;
            }

            _root = newRoot;
            _shift = newShift;
            _tail = newTail;
            _count--;
            return this;
        }

        private object?[] ArrayForMut(int i)
        {
            if (i >= TailOffset())
                return _tail;
            var node = _root;
            for (int level = _shift; level > 0; level -= Bits)
                node = (Node)node.Array[(i >> level) & Mask]!;
            return node.Array;
        }

        private Node? PopTailMut(int level, Node node)
        {
            int subidx = ((_count - 2) >> level) & Mask;
            if (level > Bits)
            {
                var newChild = PopTailMut(level - Bits, (Node)node.Array[subidx]!);
                if (newChild == null && subidx == 0)
                    return null;
                if (newChild != null)
                    node.Array[subidx] = newChild;
                return node;
            }
            else if (subidx == 0)
            {
                return null;
            }
            return node;
        }

        public IPersistentVector Persistent()
        {
            EnsureEditable();
            _editable = false;
            var trimmedTail = new object?[_count - TailOffset()];
            Array.Copy(_tail, trimmedTail, trimmedTail.Length);
            return new PersistentVector(_count, _shift, _root, trimmedTail);
        }

        IPersistentCollection ITransientCollection.Persistent() => Persistent();
    }

    #endregion
}
