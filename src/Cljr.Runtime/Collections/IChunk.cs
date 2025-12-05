namespace Cljr.Collections;

/// <summary>
/// Represents a chunk of elements in a chunked sequence.
/// Chunks are fixed-size arrays (typically 32 elements) that allow
/// batch processing of lazy sequences for better performance.
/// </summary>
public interface IChunk
{
    /// <summary>
    /// The number of elements remaining in this chunk.
    /// </summary>
    int Count { get; }

    /// <summary>
    /// Returns the element at the given index within the chunk.
    /// </summary>
    object? Nth(int i);

    /// <summary>
    /// Returns the element at the given index, or notFound if out of bounds.
    /// </summary>
    object? Nth(int i, object? notFound);

    /// <summary>
    /// Returns a chunk with the first element removed.
    /// </summary>
    IChunk DropFirst();

    /// <summary>
    /// Reduces over the chunk elements.
    /// </summary>
    object? Reduce(Func<object?, object?, object?> f, object? init);
}
