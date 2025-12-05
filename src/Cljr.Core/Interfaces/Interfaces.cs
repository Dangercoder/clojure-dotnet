using System.Collections;

namespace Cljr;

/// <summary>
/// Base interface for all persistent (immutable) collections.
/// All operations return new collections with structural sharing.
/// </summary>
public interface IPersistentCollection : IEnumerable
{
    /// <summary>O(1) count.</summary>
    int Count { get; }

    /// <summary>Returns empty collection of same type.</summary>
    IPersistentCollection Empty();

    /// <summary>Returns new collection with element added.</summary>
    IPersistentCollection Conj(object? o);

    /// <summary>Returns seq view, or null if empty.</summary>
    ISeq? Seq();

    /// <summary>Value equality check.</summary>
    bool Equiv(object? o);
}

/// <summary>
/// The fundamental sequence abstraction - immutable linked list interface.
/// All sequence operations are lazy and support structural sharing.
/// </summary>
public interface ISeq : IPersistentCollection
{
    /// <summary>Returns first element.</summary>
    object? First();

    /// <summary>Returns rest of sequence, null if empty.</summary>
    ISeq? Next();

    /// <summary>Returns seq with o prepended.</summary>
    ISeq Cons(object? o);

    /// <summary>Returns rest, never null (returns empty seq).</summary>
    ISeq More();
}

/// <summary>
/// Marker interface for collections with O(1) count.
/// Types implementing this guarantee that their Count property is O(1).
/// This is a marker interface - the actual Count comes from IPersistentCollection.
/// </summary>
public interface Counted
{
    // Marker interface - no members.
}

/// <summary>
/// Marker interface for anything that can produce a seq.
/// </summary>
public interface Seqable
{
    ISeq? Seq();
}

/// <summary>
/// Key-based lookup interface.
/// </summary>
public interface ILookup
{
    object? ValAt(object key);
    object? ValAt(object key, object? notFound);
}

/// <summary>
/// Interface for O(log32 n) indexed access.
/// </summary>
public interface Indexed : Counted
{
    object? Nth(int i);
    object? Nth(int i, object? notFound);
}

/// <summary>
/// Interface for associative collections (maps and vectors).
/// </summary>
public interface Associative : IPersistentCollection, ILookup
{
    bool ContainsKey(object key);
    IMapEntry? EntryAt(object key);
    Associative Assoc(object key, object? val);
}

/// <summary>
/// Immutable key-value pair.
/// </summary>
public interface IMapEntry
{
    object Key();
    object? Val();
}

/// <summary>
/// Persistent vector interface - O(log32 n) indexed access.
/// </summary>
public interface IPersistentVector : Associative, Indexed, IComparable
{
    IPersistentVector AssocN(int i, object? val);
    new IPersistentVector Conj(object? o);
    IPersistentVector Pop();
    IPersistentVector SubVec(int start, int end);
    object? Peek();
}

/// <summary>
/// Persistent map interface - O(log32 n) operations.
/// </summary>
public interface IPersistentMap : Associative, Counted
{
    new IPersistentMap Assoc(object key, object? val);
    IPersistentMap AssocEx(object key, object? val);
    IPersistentMap Without(object key);
    new ISeq? Seq();
}

/// <summary>
/// Persistent set interface - O(log32 n) operations.
/// </summary>
public interface IPersistentSet : IPersistentCollection, Counted
{
    new IPersistentSet Conj(object? o);
    IPersistentSet Disjoin(object key);
    bool Contains(object key);
    object? Get(object key);
}

/// <summary>
/// Interface for collections that support transient (mutable) versions.
/// </summary>
public interface IEditableCollection
{
    ITransientCollection AsTransient();
}

/// <summary>
/// Base transient collection interface.
/// </summary>
public interface ITransientCollection
{
    int Count { get; }
    ITransientCollection Conj(object? val);
    IPersistentCollection Persistent();
}

/// <summary>
/// Transient vector interface.
/// </summary>
public interface ITransientVector : ITransientCollection, Indexed
{
    ITransientVector AssocN(int i, object? val);
    new ITransientVector Conj(object? val);
    ITransientVector Pop();
    new IPersistentVector Persistent();
}

/// <summary>
/// Transient map interface.
/// </summary>
public interface ITransientMap : ITransientCollection
{
    object? ValAt(object key);
    object? ValAt(object key, object? notFound);
    ITransientMap Assoc(object key, object? val);
    ITransientMap Without(object key);
    new IPersistentMap Persistent();
}

/// <summary>
/// Transient set interface.
/// </summary>
public interface ITransientSet : ITransientCollection
{
    new ITransientSet Conj(object? val);
    ITransientSet Disjoin(object key);
    new IPersistentSet Persistent();
    bool Contains(object key);
}

/// <summary>
/// Marker interface for sorted collections.
/// </summary>
public interface Sorted
{
    IComparer Comparator();
    object? EntryKey(object entry);
    ISeq? Seq(bool ascending);
    ISeq? SeqFrom(object key, bool ascending);
}

/// <summary>
/// Interface for reversible collections.
/// </summary>
public interface Reversible
{
    ISeq RSeq();
}
