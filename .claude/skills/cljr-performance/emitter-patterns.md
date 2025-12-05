# Emitter Performance Patterns

Guidelines for emitting optimized C# code from Clojure in `CSharpEmitter.cs`.

## Principles

1. **Emit what the JIT optimizes best** - Simple, predictable patterns
2. **Avoid boxing** - Use type hints and primitive operations
3. **Minimize allocations** - Prefer stack allocation and reuse
4. **Enable inlining** - Emit small methods, avoid virtual calls in hot paths

## Primitive Operations (PrimitiveOpExpr)

The `PrimitiveOpExpr` bypasses `Core._PLUS_()` etc. for type-hinted arithmetic:

```clojure
;; Clojure with type hints
(+ ^long a ^long b)
```

```csharp
// Emits native C# instead of Core._PLUS_(a, b)
(a + b)
```

**When to emit PrimitiveOpExpr:**
- Both operands have matching primitive type hints (`^long`, `^double`, `^int`)
- Operation is `+`, `-`, `*`, `/`, `<`, `>`, `<=`, `>=`, `==`

**Benefits:**
- No boxing
- No method call overhead
- JIT can optimize further

## Avoid Boxing in Collections

### Bad: Boxing in Vectors

```csharp
// Emitting object[] boxes primitives
new object[] { 1, 2, 3 }  // Each int is boxed
```

### Better: Use typed arrays when possible

For homogeneous type-hinted collections, consider emitting typed arrays:

```csharp
// If all elements are known to be long
new long[] { 1, 2, 3 }
```

## Function Calls

### Prefer Static Dispatch

When the target is known at compile time, emit direct calls:

```csharp
// Good: Direct static call
MyNamespace.MyFunction(arg1, arg2)

// Avoid: Dynamic invoke when not needed
((Func<object, object>)fn).Invoke(arg)
```

### Inline Small Functions

For very small function bodies, the emitter could inline directly:

```clojure
(defn identity [x] x)
(identity 42)
```

```csharp
// Could inline as just: 42
// Rather than: Identity(42)
```

## Collection Literals

### Vector Literal Optimization

```clojure
[1 2 3]
```

```csharp
// Current emission
PersistentVector.Create(new object?[] { 1, 2, 3 })

// Better for small vectors (<=32 elements) - uses tail directly
// The Create method already optimizes this internally
```

### Map Literal Optimization

```clojure
{:a 1 :b 2}
```

```csharp
// Use transient for larger maps (>8 pairs)
var t = PersistentHashMap.Empty.AsTransient();
t.AssocBang(Keyword.Intern(null, "a"), 1);
t.AssocBang(Keyword.Intern(null, "b"), 2);
t.Persistent()

// For small maps, direct creation is fine
PersistentHashMap.Create(new object?[] { ... })
```

## Interop Calls

### Instance Methods

```clojure
(.ToUpper s)
```

```csharp
// Good: Direct method call
((string)s).ToUpper()

// With type hint - no cast needed
s.ToUpper()  // when s is known to be string
```

### Avoiding Reflection

When type is known via hints, emit direct calls:

```clojure
(defn get-length ^int [^String s]
  (.Length s))
```

```csharp
// Emit without reflection
public static int GetLength(string s) => s.Length;
```

## Async/Await

### Emit Proper async Task Methods

```clojure
(defn ^:async fetch-data []
  (await (Http/GetAsync url)))
```

```csharp
public static async Task<object?> FetchData()
{
    return await Http.GetAsync(url);
}
```

### Avoid Task Wrapping

Don't wrap already-async operations:

```csharp
// Bad: Unnecessary wrapping
Task.FromResult(await someTask)

// Good: Return directly
await someTask
```

## String Operations

### Keyword/Symbol Creation

Keywords and symbols are interned - emit `Keyword.Intern()`:

```csharp
// Good: Uses interning
Keyword.Intern(null, "foo")

// The runtime caches these, so repeated calls return same instance
```

### String Concatenation

For multiple concatenations, use `StringBuilder` or interpolation:

```csharp
// For 2-3 strings
$"{a}{b}{c}"

// For many strings in a loop
var sb = new StringBuilder();
foreach (var s in strings)
    sb.Append(s);
```

## Loop/Recur

### Emit as while(true) with goto

```clojure
(loop [x 0]
  (if (> x 10)
    x
    (recur (inc x))))
```

```csharp
int x = 0;
while (true)
{
    if (x > 10)
        return x;
    x = x + 1;
    continue;  // recur
}
```

**Benefits:**
- No stack growth
- JIT can optimize tight loops
- Proper tail call elimination

## let Bindings

### Emit as Local Variables

```clojure
(let [x 1 y (+ x 2)]
  (+ x y))
```

```csharp
{
    var x = 1;
    var y = x + 2;
    return x + y;
}
```

### Use Appropriate Types

With type hints, emit typed locals:

```clojure
(let [^long x 1]
  (* x 2))
```

```csharp
{
    long x = 1;
    return x * 2;  // No boxing
}
```

## defrecord / deftype

### Emit as C# Records/Classes

```clojure
(defrecord Point [^double x ^double y])
```

```csharp
public record Point(double X, double Y) : ILookup
{
    // Implement ILookup for keyword access
}
```

### Generate Efficient Constructors

Use primary constructors (C# 12+) for clean emission:

```csharp
public record Point(double X, double Y);  // Primary constructor
```

## Performance Anti-Patterns to Avoid

### Don't Emit Unnecessary Casts

```csharp
// Bad: Cast when type is already known
(object)(long)x

// Good: Direct use
x
```

### Don't Box/Unbox in Tight Loops

```csharp
// Bad: Boxing on each iteration
for (int i = 0; i < n; i++)
{
    list.Add((object)i);  // Boxing
}

// Better: Use typed collection or batch
```

### Don't Create Closures Unnecessarily

```csharp
// Bad: Creates closure object
items.Select(x => x + capturedVar)

// Better: Pass captured values as parameters when possible
```

## Emitter Optimization Checklist

When adding or modifying emitter code:

- [ ] Use `PrimitiveOpExpr` for type-hinted arithmetic
- [ ] Emit direct method calls when type is known
- [ ] Use typed locals for type-hinted bindings
- [ ] Emit `while(true)` for loop/recur
- [ ] Use transients for large collection construction
- [ ] Avoid boxing primitives when type hints available
- [ ] Generate async/await properly for `^:async` functions
- [ ] Use `Keyword.Intern()` and `Symbol.Intern()` for interning
