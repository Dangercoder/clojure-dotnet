using System.Collections;

namespace Cljr.Collections;

/// <summary>
/// A chunked sequence that holds a chunk plus the rest of the sequence.
/// This is the standard wrapper for chunked sequences.
/// </summary>
public sealed class ChunkedCons : IChunkedSeq, Counted
{
    private readonly IChunk _chunk;
    private readonly ISeq? _more;

    public ChunkedCons(IChunk chunk, ISeq? more)
    {
        _chunk = chunk;
        _more = more;
    }

    // IChunkedSeq implementation
    public IChunk ChunkedFirst() => _chunk;

    public ISeq? ChunkedNext()
    {
        if (_chunk.Count > 1)
            return new ChunkedCons(_chunk.DropFirst(), _more);
        return ChunkedMore().Seq();
    }

    public ISeq ChunkedMore()
    {
        if (_chunk.Count > 1)
            return new ChunkedCons(_chunk.DropFirst(), _more);
        return _more ?? PersistentList.Empty;
    }

    // ISeq implementation
    public object? First() => _chunk.Nth(0);

    public ISeq? Next()
    {
        if (_chunk.Count > 1)
            return new ChunkedCons(_chunk.DropFirst(), _more);
        return _more?.Seq();
    }

    public ISeq More()
    {
        if (_chunk.Count > 1)
            return new ChunkedCons(_chunk.DropFirst(), _more);
        return _more ?? PersistentList.Empty;
    }

    public ISeq? Seq() => _chunk.Count > 0 ? this : _more?.Seq();

    public ISeq Cons(object? o) => new Cons(o, this);

    // Counted implementation
    public int Count
    {
        get
        {
            int count = _chunk.Count;
            if (_more is IPersistentCollection c)
                return count + c.Count;
            // Fall back to iteration for non-counted rest
            for (var s = _more; s != null; s = s.Next())
                count++;
            return count;
        }
    }

    // IPersistentCollection implementation
    public IPersistentCollection Empty() => PersistentList.Empty;
    IPersistentCollection IPersistentCollection.Conj(object? o) => Cons(o);
    public bool Equiv(object? o) => Core.SeqEquals(this, o);

    // IEnumerable implementation
    public IEnumerator GetEnumerator()
    {
        // Iterate through chunk first
        for (int i = 0; i < _chunk.Count; i++)
            yield return _chunk.Nth(i);
        // Then through rest
        for (var s = _more?.Seq(); s != null; s = s.Next())
            yield return s.First();
    }

    public override bool Equals(object? obj) => Equiv(obj);
    public override int GetHashCode() => Core.SeqHashCode(this);
    public override string ToString() => Core.PrStr(this);
}
