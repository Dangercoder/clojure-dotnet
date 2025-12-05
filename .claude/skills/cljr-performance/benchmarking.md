# Benchmarking Guide

How to measure, compare, and optimize performance in the Cljr project using BenchmarkDotNet.

## Project Location

```
tests/Cljr.Benchmarks/
├── Cljr.Benchmarks.csproj
└── CollectionBenchmarks.cs
```

## Running Benchmarks

### Run All Benchmarks

```bash
cd tests/Cljr.Benchmarks
dotnet run -c Release
```

### Run Specific Benchmarks

```bash
# Filter by class name
dotnet run -c Release -- --filter "VectorBenchmarks"
dotnet run -c Release -- --filter "HashMapBenchmarks"

# Filter by method name
dotnet run -c Release -- --filter "*Conj*"
dotnet run -c Release -- --filter "*ValAt*"

# Combine filters
dotnet run -c Release -- --filter "VectorBenchmarks.Conj*"
```

### Quick Benchmarks (Development)

For faster iteration during development:

```bash
# Short run (less accurate, but faster)
dotnet run -c Release -- --filter "YourBenchmark" -j short
```

## Writing Benchmarks

### Basic Benchmark Structure

```csharp
using BenchmarkDotNet.Attributes;

[MemoryDiagnoser]  // Track allocations
public class MyBenchmarks
{
    private PersistentVector _smallVector;
    private PersistentVector _largeVector;

    [GlobalSetup]
    public void Setup()
    {
        _smallVector = CreateVector(100);
        _largeVector = CreateVector(10000);
    }

    [Benchmark]
    public object SmallVectorConj()
    {
        return _smallVector.Conj(42);
    }

    [Benchmark]
    public object LargeVectorConj()
    {
        return _largeVector.Conj(42);
    }
}
```

### Parameterized Benchmarks

Test across different sizes:

```csharp
[MemoryDiagnoser]
public class SizedBenchmarks
{
    [Params(10, 100, 1000, 10000)]
    public int Size;

    private PersistentVector _vector;

    [GlobalSetup]
    public void Setup()
    {
        _vector = CreateVector(Size);
    }

    [Benchmark]
    public object Nth()
    {
        return _vector.Nth(Size / 2);
    }
}
```

### Comparing Implementations

```csharp
[MemoryDiagnoser]
public class ComparisonBenchmarks
{
    private object?[] _items;

    [GlobalSetup]
    public void Setup()
    {
        _items = Enumerable.Range(0, 1000).Cast<object?>().ToArray();
    }

    [Benchmark(Baseline = true)]
    public PersistentVector CreateWithLoop()
    {
        var vec = PersistentVector.Empty;
        foreach (var item in _items)
            vec = vec.Conj(item);
        return vec;
    }

    [Benchmark]
    public PersistentVector CreateWithTransient()
    {
        var t = PersistentVector.Empty.AsTransient();
        foreach (var item in _items)
            t.ConjBang(item);
        return t.Persistent();
    }

    [Benchmark]
    public PersistentVector CreateWithSpan()
    {
        return PersistentVector.Create(_items.AsSpan());
    }
}
```

## Reading Results

### Key Metrics

| Metric | Description |
|--------|-------------|
| **Mean** | Average execution time |
| **Allocated** | Memory allocated per operation |
| **Ratio** | Comparison to baseline (if set) |
| **Gen0/1/2** | GC collections per 1000 ops |

### Example Output

```
|            Method |  Size |       Mean |   Allocated |
|------------------ |------ |-----------:|------------:|
|       VectorConj  |   100 |   45.23 ns |        64 B |
|       VectorConj  | 10000 |   48.12 ns |        64 B |
|   TransientConj   |   100 |   12.34 ns |         0 B |
|   TransientConj   | 10000 |   12.56 ns |         0 B |
```

### What to Look For

1. **Mean time** - Lower is better
2. **Allocated** - 0 B is ideal for hot paths
3. **Consistency across sizes** - O(1) vs O(n) behavior
4. **GC pressure** - Gen0/1/2 collections indicate allocation patterns

## Benchmark Patterns for Cljr

### Collection Operations

```csharp
[MemoryDiagnoser]
public class CollectionBenchmarks
{
    // Read operations
    [Benchmark]
    public object VectorNth() => _vector.Nth(500);

    [Benchmark]
    public object MapValAt() => _map.ValAt(_existingKey);

    // Write operations (return result to prevent dead code elimination)
    [Benchmark]
    public object VectorConj() => _vector.Conj(42);

    [Benchmark]
    public object MapAssoc() => _map.Assoc(_newKey, 42);

    // Batch operations
    [Benchmark]
    public object TransientBatch()
    {
        var t = _vector.AsTransient();
        for (int i = 0; i < 100; i++)
            t.ConjBang(i);
        return t.Persistent();
    }
}
```

### Protocol Dispatch

```csharp
[MemoryDiagnoser]
public class DispatchBenchmarks
{
    private Protocol _protocol;
    private object _target;

    [Benchmark]
    public object CachedDispatch()
    {
        var method = _protocol.GetMethod(_target.GetType(), Symbol.Intern("invoke"));
        return method?.DynamicInvoke(_target);
    }
}
```

### REPL Evaluation

```csharp
[MemoryDiagnoser]
public class ReplBenchmarks
{
    private NreplSession _session;

    [GlobalSetup]
    public void Setup()
    {
        _session = new NreplSession();
    }

    [Benchmark]
    public async Task<EvalResult> SimpleEval()
    {
        return await _session.EvalAsync("(+ 1 2)");
    }

    [Benchmark]
    public async Task<EvalResult> FunctionDefAndCall()
    {
        await _session.EvalAsync("(defn f [x] (* x 2))");
        return await _session.EvalAsync("(f 21)");
    }
}
```

## Best Practices

### DO

- **Always use Release mode** (`-c Release`)
- **Use `[MemoryDiagnoser]`** to track allocations
- **Run multiple times** before drawing conclusions
- **Compare baseline** with `Baseline = true`
- **Test realistic data sizes**
- **Return values** to prevent dead code elimination

### DON'T

- **Don't benchmark in Debug mode**
- **Don't ignore GC allocations** - they add up
- **Don't micro-optimize prematurely** - measure first
- **Don't benchmark cold code** - let JIT warm up (BenchmarkDotNet handles this)

## Adding New Benchmarks

1. Create or modify file in `tests/Cljr.Benchmarks/`
2. Add `[Benchmark]` attribute to methods
3. Add `[MemoryDiagnoser]` to class
4. Use `[GlobalSetup]` for one-time initialization
5. Run and compare results

```csharp
// Template for new benchmark file
using BenchmarkDotNet.Attributes;
using Cljr.Collections;

namespace Cljr.Benchmarks;

[MemoryDiagnoser]
public class MyNewBenchmarks
{
    [GlobalSetup]
    public void Setup()
    {
        // Initialize test data
    }

    [Benchmark]
    public object MyOperation()
    {
        // Return result to prevent elimination
        return SomeOperation();
    }
}
```

## Running from Main Program

The benchmark project entry point:

```csharp
// Program.cs
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
```

## Comparing Before/After

When optimizing, save results:

```bash
# Before changes
dotnet run -c Release -- --filter "MyBenchmark" --exporters json

# After changes
dotnet run -c Release -- --filter "MyBenchmark" --exporters json

# Compare JSON outputs
```

Or use the `--join` option to run multiple configurations:

```bash
dotnet run -c Release -- --filter "MyBenchmark" --runtimes net9.0 net10.0
```
