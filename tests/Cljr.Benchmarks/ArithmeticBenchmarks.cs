using BenchmarkDotNet.Attributes;

namespace Cljr.Benchmarks;

/// <summary>
/// Benchmarks to measure boxing overhead in arithmetic operations.
/// The current Core._PLUS_ uses params object?[] which boxes all primitives.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ArithmeticBenchmarks
{
    // Baseline: Direct C# arithmetic (no boxing)
    [Benchmark(Baseline = true)]
    public long Direct_Plus_2Args() => 1L + 2L;

    [Benchmark]
    public long Direct_Plus_3Args() => 1L + 2L + 3L;

    [Benchmark]
    public long Direct_Plus_5Args() => 1L + 2L + 3L + 4L + 5L;

    // Current implementation: Uses params object?[] - boxes all args
    [Benchmark]
    public object? Core_Plus_2Args() => Core._PLUS_(1L, 2L);

    [Benchmark]
    public object? Core_Plus_3Args() => Core._PLUS_(1L, 2L, 3L);

    [Benchmark]
    public object? Core_Plus_5Args() => Core._PLUS_(1L, 2L, 3L, 4L, 5L);

    // Subtraction
    [Benchmark]
    public long Direct_Minus_2Args() => 10L - 3L;

    [Benchmark]
    public object? Core_Minus_2Args() => Core._MINUS_(10L, 3L);

    // Multiplication
    [Benchmark]
    public long Direct_Mult_2Args() => 6L * 7L;

    [Benchmark]
    public object? Core_Mult_2Args() => Core._STAR_(6L, 7L);

    // Division
    [Benchmark]
    public long Direct_Div_2Args() => 42L / 6L;

    [Benchmark]
    public object? Core_Div_2Args() => Core._SLASH_(42L, 6L);

    // Comparison operators
    [Benchmark]
    public bool Direct_LessThan() => 1L < 2L;

    [Benchmark]
    public object? Core_LessThan() => Core._LT_(1L, 2L);

    [Benchmark]
    public bool Direct_Equals() => 42L == 42L;

    [Benchmark]
    public object? Core_Equals() => Core._EQ_(42L, 42L);

    // inc/dec - very common operations
    [Benchmark]
    public long Direct_Inc() => 42L + 1L;

    [Benchmark]
    public object? Core_Inc() => Core.Inc(42L);

    [Benchmark]
    public long Direct_Dec() => 42L - 1L;

    [Benchmark]
    public object? Core_Dec() => Core.Dec(42L);
}
