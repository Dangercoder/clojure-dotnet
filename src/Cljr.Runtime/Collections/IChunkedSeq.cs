namespace Cljr.Collections;

/// <summary>
/// Interface for sequences that support chunked access.
/// Chunked sequences process elements in batches (typically 32)
/// for improved performance by reducing function call overhead.
/// </summary>
public interface IChunkedSeq : ISeq
{
    /// <summary>
    /// Returns the first chunk of this sequence.
    /// </summary>
    IChunk ChunkedFirst();

    /// <summary>
    /// Returns the remaining sequence after the first chunk, or null if exhausted.
    /// </summary>
    ISeq? ChunkedNext();

    /// <summary>
    /// Returns the remaining sequence after the first chunk, or empty if exhausted.
    /// </summary>
    ISeq ChunkedMore();
}
