namespace Cljr;

/// <summary>
/// Interface for collections that can be reduced with an initial value.
/// </summary>
public interface IReduceInit
{
    /// <summary>
    /// Reduces the collection using function f and initial value init.
    /// </summary>
    object? reduce(Func<object?, object?, object?> f, object? init);
}

/// <summary>
/// Interface for collections that can be reduced without an initial value.
/// The first element becomes the initial accumulator.
/// </summary>
public interface IReduce : IReduceInit
{
    /// <summary>
    /// Reduces the collection using function f.
    /// First element is used as initial accumulator.
    /// </summary>
    object? reduce(Func<object?, object?, object?> f);
}

/// <summary>
/// Wrapper for early termination in reduce operations.
/// When reduce encounters a Reduced value, it stops iteration and returns the wrapped value.
/// </summary>
public sealed class Reduced
{
    public object? Value { get; }

    public Reduced(object? value) => Value = value;
}
