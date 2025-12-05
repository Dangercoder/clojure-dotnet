using System.Buffers;
using System.Collections;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Cljr.Collections;

/// <summary>
/// High-performance persistent hash map using Hash Array Mapped Trie (HAMT).
///
/// Structure:
/// - Uses 32-bit bitmap to compress storage (only stores occupied slots)
/// - 5 bits of hash per level (32-way branching)
/// - Maximum depth of 7 levels (32 bits / 5 bits = 6.4)
/// - Hash collisions handled via linked list nodes
///
/// Performance:
/// - get: O(log32 n) - effectively O(1) for practical sizes
/// - assoc: O(log32 n) with structural sharing
/// - dissoc: O(log32 n)
///
/// Uses SIMD BitOperations.PopCount for fast bitmap operations.
/// Based on Bagwell's "Ideal Hash Trees" paper.
/// </summary>
public sealed class PersistentHashMap : IPersistentMap, IEditableCollection
{
    private const int Bits = 5;
    private const int Width = 1 << Bits; // 32
    private const int Mask = Width - 1;  // 0x1F

    public static readonly PersistentHashMap Empty = new(0, null);

    private readonly int _count;
    private readonly INode? _root;

    private PersistentHashMap(int count, INode? root)
    {
        _count = count;
        _root = root;
    }

    public int Count => _count;

    #region Factory Methods

    /// <summary>
    /// Creates a persistent hash map from alternating key-value pairs.
    /// </summary>
    public static PersistentHashMap Create(params object?[] kvs)
    {
        if (kvs.Length == 0) return Empty;
        if (kvs.Length % 2 != 0)
            throw new ArgumentException("Create requires an even number of arguments (key-value pairs)");

        var map = Empty;
        for (int i = 0; i < kvs.Length; i += 2)
        {
            var key = kvs[i] ?? throw new ArgumentNullException(nameof(kvs), "Map keys cannot be null");
            map = (PersistentHashMap)map.Assoc(key, kvs[i + 1]);
        }
        return map;
    }

    #endregion

    #region Lookup

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? ValAt(object key) => ValAt(key, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public object? ValAt(object key, object? notFound)
    {
        if (_root == null) return notFound;
        var entry = _root.Find(0, Hash(key), key);
        return entry != null ? entry.Val() : notFound;
    }

    public bool ContainsKey(object key)
    {
        if (_root == null) return false;
        return _root.Find(0, Hash(key), key) != null;
    }

    public IMapEntry? EntryAt(object key)
    {
        if (_root == null) return null;
        return _root.Find(0, Hash(key), key);
    }

    #endregion

    #region Persistent Operations

    Associative Associative.Assoc(object key, object? val) => Assoc(key, val);

    public IPersistentMap Assoc(object key, object? val)
    {
        var addedLeaf = new Box(null);
        var newRoot = (_root ?? BitmapIndexedNode.Empty).Assoc(0, Hash(key), key, val, addedLeaf);
        if (newRoot == _root)
            return this;
        return new PersistentHashMap(_count + (addedLeaf.Value != null ? 1 : 0), newRoot);
    }

    public IPersistentMap AssocEx(object key, object? val)
    {
        if (ContainsKey(key))
            throw new InvalidOperationException("Key already present");
        return Assoc(key, val);
    }

    public IPersistentMap Without(object key)
    {
        if (_root == null) return this;
        var newRoot = _root.Without(0, Hash(key), key);
        if (newRoot == _root) return this;
        return new PersistentHashMap(_count - 1, newRoot);
    }

    #endregion

    #region Collection Operations

    IPersistentCollection IPersistentCollection.Conj(object? o)
    {
        if (o is IMapEntry entry)
            return Assoc(entry.Key(), entry.Val());
        if (o is DictionaryEntry de)
            return Assoc(de.Key!, de.Value);
        // Support vectors [k v] like Clojure does
        if (o is PersistentVector pv && pv.Count == 2)
            return Assoc(pv.Nth(0)!, pv.Nth(1));
        throw new ArgumentException("conj on map requires MapEntry, DictionaryEntry, or [k v] vector");
    }

    IPersistentCollection IPersistentCollection.Empty() => PersistentHashMap.Empty;

    public ISeq? Seq() => _root?.GetSeq();

    public bool Equiv(object? o)
    {
        if (o is IPersistentMap m)
        {
            if (m.Count != _count) return false;
            for (var s = Seq(); s != null; s = s.Next())
            {
                var entry = (IMapEntry)s.First()!;
                var found = m.EntryAt(entry.Key());
                if (found == null || !Core.Equals(entry.Val(), found.Val()))
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
        {
            var entry = (IMapEntry)s.First()!;
            hash += (entry.Key()?.GetHashCode() ?? 0) ^ (entry.Val()?.GetHashCode() ?? 0);
        }
        return hash;
    }

    public override string ToString() => Core.PrStr(this);

    #endregion

    #region Transient Support

    public ITransientCollection AsTransient() => new TransientHashMap(this);

    #endregion

    #region Hashing

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Hash(object? key)
    {
        if (key == null) return 0;
        int h = key.GetHashCode();
        // Spread bits for better distribution
        return h ^ (h >> 16);
    }

    #endregion

    #region Node Interface

    private interface INode
    {
        INode Assoc(int shift, int hash, object key, object? val, Box addedLeaf);
        INode? Without(int shift, int hash, object key);
        IMapEntry? Find(int shift, int hash, object key);
        ISeq? GetSeq();
    }

    /// <summary>Mutable box for tracking additions.</summary>
    private sealed class Box
    {
        public object? Value;
        public Box(object? value) => Value = value;
    }

    #endregion

    #region BitmapIndexedNode

    /// <summary>
    /// Main HAMT node type using bitmap compression.
    /// Only stores occupied slots, using popcount for indexing.
    /// </summary>
    private sealed class BitmapIndexedNode : INode
    {
        public static readonly BitmapIndexedNode Empty = new(0, []);

        private readonly int _bitmap;
        private readonly object?[] _array; // Alternating keys and values, or nodes

        public BitmapIndexedNode(int bitmap, object?[] array)
        {
            _bitmap = bitmap;
            _array = array;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int BitPos(int hash, int shift) => 1 << ((hash >> shift) & Mask);

        /// <summary>
        /// Uses SIMD popcount for fast index calculation.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int Index(int bitmap, int bit) =>
            BitOperations.PopCount((uint)(bitmap & (bit - 1)));

        public INode Assoc(int shift, int hash, object key, object? val, Box addedLeaf)
        {
            int bit = BitPos(hash, shift);
            int idx = Index(_bitmap, bit);

            if ((_bitmap & bit) != 0)
            {
                // Slot occupied
                var keyOrNull = _array[2 * idx];
                var valOrNode = _array[2 * idx + 1];

                if (keyOrNull == null)
                {
                    // It's a sub-node
                    var n = ((INode)valOrNode!).Assoc(shift + Bits, hash, key, val, addedLeaf);
                    if (n == valOrNode) return this;
                    return new BitmapIndexedNode(_bitmap, CloneAndSet(_array, 2 * idx + 1, n));
                }

                if (Core.Equals(key, keyOrNull))
                {
                    // Same key, update value
                    if (val == valOrNode) return this;
                    return new BitmapIndexedNode(_bitmap, CloneAndSet(_array, 2 * idx + 1, val));
                }

                // Hash collision at this level, create sub-node
                addedLeaf.Value = addedLeaf;
                return new BitmapIndexedNode(_bitmap,
                    CloneAndSet(_array, 2 * idx, null, 2 * idx + 1,
                        CreateNode(shift + Bits, keyOrNull, valOrNode, hash, key, val)));
            }
            else
            {
                // Empty slot, insert
                int n = BitOperations.PopCount((uint)_bitmap);
                if (n >= 16)
                {
                    // Too full, convert to ArrayNode
                    var nodes = new INode?[Width];
                    int jdx = (hash >> shift) & Mask;
                    nodes[jdx] = Empty.Assoc(shift + Bits, hash, key, val, addedLeaf);
                    int j = 0;
                    for (int i = 0; i < Width; i++)
                    {
                        if (((_bitmap >> i) & 1) != 0)
                        {
                            if (_array[j] == null)
                                nodes[i] = (INode)_array[j + 1]!;
                            else
                                nodes[i] = Empty.Assoc(shift + Bits, Hash(_array[j]), _array[j]!, _array[j + 1], addedLeaf);
                            j += 2;
                        }
                    }
                    return new ArrayNode(n + 1, nodes);
                }

                var newArray = new object?[2 * (n + 1)];
                Array.Copy(_array, 0, newArray, 0, 2 * idx);
                newArray[2 * idx] = key;
                addedLeaf.Value = addedLeaf;
                newArray[2 * idx + 1] = val;
                Array.Copy(_array, 2 * idx, newArray, 2 * (idx + 1), 2 * (n - idx));
                return new BitmapIndexedNode(_bitmap | bit, newArray);
            }
        }

        public INode? Without(int shift, int hash, object key)
        {
            int bit = BitPos(hash, shift);
            if ((_bitmap & bit) == 0) return this;

            int idx = Index(_bitmap, bit);
            var keyOrNull = _array[2 * idx];
            var valOrNode = _array[2 * idx + 1];

            if (keyOrNull == null)
            {
                var n = ((INode)valOrNode!).Without(shift + Bits, hash, key);
                if (n == valOrNode) return this;
                if (n != null)
                    return new BitmapIndexedNode(_bitmap, CloneAndSet(_array, 2 * idx + 1, n));
                if (_bitmap == bit) return null;
                return new BitmapIndexedNode(_bitmap ^ bit, RemovePair(_array, idx));
            }

            if (Core.Equals(key, keyOrNull))
            {
                if (_bitmap == bit) return null;
                return new BitmapIndexedNode(_bitmap ^ bit, RemovePair(_array, idx));
            }

            return this;
        }

        public IMapEntry? Find(int shift, int hash, object key)
        {
            int bit = BitPos(hash, shift);
            if ((_bitmap & bit) == 0) return null;

            int idx = Index(_bitmap, bit);
            var keyOrNull = _array[2 * idx];
            var valOrNode = _array[2 * idx + 1];

            if (keyOrNull == null)
                return ((INode)valOrNode!).Find(shift + Bits, hash, key);

            if (Core.Equals(key, keyOrNull))
                return new MapEntry(keyOrNull, valOrNode);

            return null;
        }

        public ISeq? GetSeq() => NodeSeq.Create(_array);

        private static object?[] CloneAndSet(object?[] array, int i, object? a)
        {
            var clone = new object?[array.Length];
            Array.Copy(array, clone, array.Length);
            clone[i] = a;
            return clone;
        }

        private static object?[] CloneAndSet(object?[] array, int i, object? a, int j, object? b)
        {
            var clone = new object?[array.Length];
            Array.Copy(array, clone, array.Length);
            clone[i] = a;
            clone[j] = b;
            return clone;
        }

        private static object?[] RemovePair(object?[] array, int i)
        {
            var newArray = new object?[array.Length - 2];
            Array.Copy(array, 0, newArray, 0, 2 * i);
            Array.Copy(array, 2 * (i + 1), newArray, 2 * i, newArray.Length - 2 * i);
            return newArray;
        }

        private static INode CreateNode(int shift, object key1, object? val1, int hash2, object key2, object? val2)
        {
            int hash1 = Hash(key1);
            if (hash1 == hash2)
                return new HashCollisionNode(hash1, 2, [key1, val1, key2, val2]);

            var addedLeaf = new Box(null);
            return Empty
                .Assoc(shift, hash1, key1, val1, addedLeaf)
                .Assoc(shift, hash2, key2, val2, addedLeaf);
        }
    }

    #endregion

    #region ArrayNode

    /// <summary>
    /// Full 32-slot array node, used when bitmap node gets too full.
    /// </summary>
    private sealed class ArrayNode : INode
    {
        private readonly int _count;
        private readonly INode?[] _array;

        public ArrayNode(int count, INode?[] array)
        {
            _count = count;
            _array = array;
        }

        public INode Assoc(int shift, int hash, object key, object? val, Box addedLeaf)
        {
            int idx = (hash >> shift) & Mask;
            var node = _array[idx];
            if (node == null)
            {
                return new ArrayNode(_count + 1,
                    CloneAndSet(_array, idx, BitmapIndexedNode.Empty.Assoc(shift + Bits, hash, key, val, addedLeaf)));
            }

            var n = node.Assoc(shift + Bits, hash, key, val, addedLeaf);
            if (n == node) return this;
            return new ArrayNode(_count, CloneAndSet(_array, idx, n));
        }

        public INode? Without(int shift, int hash, object key)
        {
            int idx = (hash >> shift) & Mask;
            var node = _array[idx];
            if (node == null) return this;

            var n = node.Without(shift + Bits, hash, key);
            if (n == node) return this;
            if (n == null)
            {
                if (_count <= 8)
                    return Pack(idx);
                return new ArrayNode(_count - 1, CloneAndSet(_array, idx, null));
            }
            return new ArrayNode(_count, CloneAndSet(_array, idx, n));
        }

        public IMapEntry? Find(int shift, int hash, object key)
        {
            int idx = (hash >> shift) & Mask;
            var node = _array[idx];
            return node?.Find(shift + Bits, hash, key);
        }

        public ISeq? GetSeq() => ArrayNodeSeq.Create(_array, 0);

        private INode Pack(int idx)
        {
            var newArray = new object?[2 * (_count - 1)];
            int j = 1;
            int bitmap = 0;
            for (int i = 0; i < idx; i++)
            {
                if (_array[i] != null)
                {
                    newArray[j] = _array[i];
                    bitmap |= 1 << i;
                    j += 2;
                }
            }
            for (int i = idx + 1; i < _array.Length; i++)
            {
                if (_array[i] != null)
                {
                    newArray[j] = _array[i];
                    bitmap |= 1 << i;
                    j += 2;
                }
            }
            return new BitmapIndexedNode(bitmap, newArray);
        }

        private static INode?[] CloneAndSet(INode?[] array, int i, INode? a)
        {
            var clone = new INode?[array.Length];
            Array.Copy(array, clone, array.Length);
            clone[i] = a;
            return clone;
        }
    }

    #endregion

    #region HashCollisionNode

    /// <summary>
    /// Node for handling hash collisions (same hash, different keys).
    /// Uses linear search since collisions are rare.
    /// </summary>
    private sealed class HashCollisionNode : INode
    {
        private readonly int _hash;
        private readonly int _count;
        private readonly object?[] _array;

        public HashCollisionNode(int hash, int count, object?[] array)
        {
            _hash = hash;
            _count = count;
            _array = array;
        }

        public INode Assoc(int shift, int hash, object key, object? val, Box addedLeaf)
        {
            if (hash == _hash)
            {
                int idx = FindIndex(key);
                if (idx != -1)
                {
                    if (_array[idx + 1] == val) return this;
                    return new HashCollisionNode(_hash, _count, CloneAndSet(_array, idx + 1, val));
                }
                var newArray = new object?[_array.Length + 2];
                Array.Copy(_array, newArray, _array.Length);
                newArray[_array.Length] = key;
                newArray[_array.Length + 1] = val;
                addedLeaf.Value = addedLeaf;
                return new HashCollisionNode(_hash, _count + 1, newArray);
            }
            // Nest under bitmap node
            return new BitmapIndexedNode(1 << ((_hash >> shift) & Mask), [null, this])
                .Assoc(shift, hash, key, val, addedLeaf);
        }

        public INode? Without(int shift, int hash, object key)
        {
            int idx = FindIndex(key);
            if (idx == -1) return this;
            if (_count == 1) return null;
            return new HashCollisionNode(_hash, _count - 1, RemovePair(_array, idx / 2));
        }

        public IMapEntry? Find(int shift, int hash, object key)
        {
            int idx = FindIndex(key);
            if (idx < 0) return null;
            return new MapEntry(_array[idx]!, _array[idx + 1]);
        }

        public ISeq? GetSeq() => NodeSeq.Create(_array);

        private int FindIndex(object key)
        {
            for (int i = 0; i < 2 * _count; i += 2)
                if (Core.Equals(key, _array[i]))
                    return i;
            return -1;
        }

        private static object?[] CloneAndSet(object?[] array, int i, object? a)
        {
            var clone = new object?[array.Length];
            Array.Copy(array, clone, array.Length);
            clone[i] = a;
            return clone;
        }

        private static object?[] RemovePair(object?[] array, int i)
        {
            var newArray = new object?[array.Length - 2];
            Array.Copy(array, 0, newArray, 0, 2 * i);
            Array.Copy(array, 2 * (i + 1), newArray, 2 * i, newArray.Length - 2 * i);
            return newArray;
        }
    }

    #endregion

    #region Seq Classes

    private sealed class NodeSeq : ISeq, Counted
    {
        private readonly object?[] _array;
        private readonly int _i;
        private readonly ISeq? _s;

        private NodeSeq(object?[] array, int i, ISeq? s)
        {
            _array = array;
            _i = i;
            _s = s;
        }

        public static ISeq? Create(object?[] array) => Create(array, 0, null);

        private static ISeq? Create(object?[] array, int i, ISeq? s)
        {
            if (s != null) return new NodeSeq(array, i, s);
            for (int j = i; j < array.Length; j += 2)
            {
                if (array[j] != null)
                    return new NodeSeq(array, j, null);
                var node = (INode?)array[j + 1];
                if (node != null)
                {
                    var nodeSeq = node.GetSeq();
                    if (nodeSeq != null)
                        return new NodeSeq(array, j + 2, nodeSeq);
                }
            }
            return null;
        }

        public object? First()
        {
            if (_s != null) return _s.First();
            return new MapEntry(_array[_i]!, _array[_i + 1]);
        }

        public ISeq? Next()
        {
            if (_s != null)
                return Create(_array, _i, _s.Next());
            return Create(_array, _i + 2, null);
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

    private sealed class ArrayNodeSeq : ISeq, Counted
    {
        private readonly INode?[] _nodes;
        private readonly int _i;
        private readonly ISeq? _s;

        private ArrayNodeSeq(INode?[] nodes, int i, ISeq? s)
        {
            _nodes = nodes;
            _i = i;
            _s = s;
        }

        public static ISeq? Create(INode?[] nodes, int i)
        {
            for (int j = i; j < nodes.Length; j++)
            {
                if (nodes[j] != null)
                {
                    var ns = nodes[j]!.GetSeq();
                    if (ns != null)
                        return new ArrayNodeSeq(nodes, j + 1, ns);
                }
            }
            return null;
        }

        public object? First() => _s!.First();

        public ISeq? Next()
        {
            var next = _s!.Next();
            if (next != null)
                return new ArrayNodeSeq(_nodes, _i, next);
            return Create(_nodes, _i);
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

    #endregion

    #region TransientHashMap

    private sealed class TransientHashMap : ITransientMap
    {
        private volatile bool _editable = true;
        private int _count;
        private INode? _root;

        public TransientHashMap(PersistentHashMap m)
        {
            _count = m._count;
            _root = m._root;
        }

        private void EnsureEditable()
        {
            if (!_editable)
                throw new InvalidOperationException("Transient used after persistent!");
        }

        public int Count => _count;

        public object? ValAt(object key) => ValAt(key, null);

        public object? ValAt(object key, object? notFound)
        {
            EnsureEditable();
            if (_root == null) return notFound;
            var entry = _root.Find(0, Hash(key), key);
            return entry != null ? entry.Val() : notFound;
        }

        public ITransientMap Assoc(object key, object? val)
        {
            EnsureEditable();
            var addedLeaf = new Box(null);
            var newRoot = (_root ?? BitmapIndexedNode.Empty).Assoc(0, Hash(key), key, val, addedLeaf);
            if (newRoot != _root) _root = newRoot;
            if (addedLeaf.Value != null) _count++;
            return this;
        }

        public ITransientMap Without(object key)
        {
            EnsureEditable();
            if (_root == null) return this;
            var addedLeaf = new Box(null);
            var newRoot = _root.Without(0, Hash(key), key);
            if (newRoot != _root)
            {
                _root = newRoot;
                _count--;
            }
            return this;
        }

        ITransientCollection ITransientCollection.Conj(object? val)
        {
            EnsureEditable();
            if (val is IMapEntry entry)
                return Assoc(entry.Key(), entry.Val());
            throw new ArgumentException("Requires MapEntry");
        }

        public IPersistentMap Persistent()
        {
            EnsureEditable();
            _editable = false;
            return new PersistentHashMap(_count, _root);
        }

        IPersistentCollection ITransientCollection.Persistent() => Persistent();
    }

    #endregion
}
