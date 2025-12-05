namespace Cljr.Collections;

/// <summary>
/// Operations that can be detected and optimized for range-based SIMD computation.
/// These operations have known mathematical properties that allow efficient vectorization.
/// </summary>
public enum RangeOperation
{
    /// <summary>Unknown or non-optimizable operation.</summary>
    Unknown,

    /// <summary>Identity: x => x</summary>
    Identity,

    /// <summary>Increment: x => x + 1</summary>
    Inc,

    /// <summary>Decrement: x => x - 1</summary>
    Dec,

    /// <summary>Negate: x => -x</summary>
    Negate,

    /// <summary>Double: x => x * 2</summary>
    Double
}
