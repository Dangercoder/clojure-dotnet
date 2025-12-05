# .NET 10 and C# 14 Performance Features

This reference covers performance-relevant features in .NET 10 (LTS) and C# 14 that can benefit the Cljr compiler and runtime.

## .NET 10 Performance Highlights

.NET 10 delivers **49% faster average response times** than .NET 8, with significant JIT and GC improvements.

### JIT Compiler Improvements

#### Better Struct Handling

The JIT can now place promoted struct members directly into shared registers, eliminating memory round-trips:

```csharp
// .NET 10 JIT handles this more efficiently
public readonly struct Point(int x, int y)
{
    public int X { get; } = x;
    public int Y { get; } = y;
}
```

#### Array Interface Devirtualization

The JIT can now devirtualize and inline array interface methods:

```csharp
// Now inlineable in .NET 10
IList<int> list = new int[] { 1, 2, 3 };
var count = list.Count;  // Devirtualized
```

#### Improved Inlining

Methods that become eligible for devirtualization due to previous inlining are now also inlined, enabling cascading optimizations.

#### Block Reordering (3-opt)

The JIT uses the 3-opt heuristic for near-optimal code block ordering, improving:
- Hot path density
- Branch prediction
- Instruction cache utilization

### GC Improvements

- Better memory compaction reduces fragmentation
- Reduced GC pauses in real-world scenarios
- Background GC optimizations

### AVX10.2 Support

AVX10.2 intrinsics are available (disabled by default). When enabled on compatible processors, benefits:
- Numeric computations
- AI/ML workloads
- Graphics processing
- Scientific computing

```csharp
// Future use - check for availability
if (Avx10v2.IsSupported)
{
    // Use AVX10.2 intrinsics
}
```

### Library Improvements

- **JSON serialization**: 20-40% faster
- **ZipArchiveEntry**: Lazy loading reduces memory
- **Cryptography**: New APIs and performance improvements

## C# 14 Performance Features

### Implicit Span Conversions

C# 14 adds implicit conversions between `T[]`, `Span<T>`, and `ReadOnlySpan<T>`:

```csharp
// Before C# 14 - explicit conversion needed
void ProcessSpan(ReadOnlySpan<int> data) { }
int[] array = { 1, 2, 3 };
ProcessSpan(array.AsSpan());  // Explicit

// C# 14 - implicit conversion
ProcessSpan(array);  // Implicit conversion

// Also works for Span<T> to ReadOnlySpan<T>
void ProcessReadOnly(ReadOnlySpan<int> data) { }
Span<int> span = stackalloc int[3];
ProcessReadOnly(span);  // Implicit conversion
```

**Benefits for Cljr:**
- Cleaner APIs in PersistentVector
- Fewer temporary variables
- More aggressive JIT inlining

### User-Defined Compound Assignment

Enables in-place modification without allocation:

```csharp
// Before C# 14 - allocates new BigInteger
bigInt += other;  // Calls op_Addition, creates new instance

// C# 14 - can define compound assignment operator
public static BigInteger operator +=(ref BigInteger left, BigInteger right)
{
    // In-place modification, no allocation
}
```

**Benefits for Cljr:**
- Could benefit numeric operations in runtime
- Potential for transient-like optimizations

### Extension Members

Extension properties, operators, and static members:

```csharp
// C# 14 extension property
public extension VectorExtensions for PersistentVector
{
    public bool IsSmall => Count < 32;

    public static PersistentVector Empty => PersistentVector.Empty;
}
```

### The `field` Keyword

Direct access to auto-property backing field:

```csharp
public string Name
{
    get => field;
    set => field = value?.Trim() ?? throw new ArgumentNullException();
}
```

### Lambda Parameter Modifiers

Add `scoped`, `ref`, `in`, `out`, `ref readonly` to lambdas:

```csharp
// C# 14 - ref parameter in lambda
ReadOnlySpan<int> span = stackalloc int[] { 1, 2, 3 };
var sum = span.Aggregate(0, (ref readonly int acc, ref readonly int x) => acc + x);
```

**Benefits for Cljr:**
- Enables stack allocation with lambda operations
- Reduces allocations in functional patterns

## Applicable Optimizations for Cljr

### 1. Update Span APIs (C# 14)

```csharp
// Current - in PersistentVector
public static PersistentVector Create(ReadOnlySpan<object?> items) { }

// With C# 14 implicit conversions, callers can pass arrays directly
var vec = PersistentVector.Create(myArray);  // No .AsSpan() needed
```

### 2. Consider User-Defined Compound Assignment

For numeric wrappers or transient operations:

```csharp
public static TransientVector operator +=(ref TransientVector left, object? item)
{
    left.ConjBang(item);
    return left;
}
```

### 3. Use stackalloc with Lambda Modifiers

```csharp
// For small temporary allocations in hot paths
Span<object?> temp = stackalloc object?[4];
// Use with ref lambdas for zero-allocation processing
```

### 4. Leverage Extension Members

For cleaner Clojure-like APIs:

```csharp
public extension SeqExtensions for ISeq
{
    public ISeq Rest => Cljr.Core.Rest(this);
    public object? First => Cljr.Core.First(this);
}
```

## Version Compatibility Notes

| Feature | Cljr.Runtime | Cljr.Compiler |
|---------|--------------|---------------|
| C# version | preview (14) | 12 |
| Target | net10.0 | net10.0 + netstandard2.0 |
| Span conversions | ✓ Use freely | ✗ Explicit only |
| Extension members | ✓ Use freely | ✗ Not available |
| field keyword | ✓ Use freely | ✗ Not available |

The **Compiler** must maintain netstandard2.0 compatibility for source generators.

## References

- [What's new in .NET 10](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/overview)
- [.NET 10 Runtime](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime)
- [What's new in C# 14](https://learn.microsoft.com/en-us/dotnet/csharp/whats-new/csharp-14)
- [Introducing C# 14 Blog](https://devblogs.microsoft.com/dotnet/introducing-csharp-14/)
