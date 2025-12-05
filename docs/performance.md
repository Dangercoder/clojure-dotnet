# Cljr Performance Guide

This document covers Cljr's performance optimizations, quirks, and known limitations.

## Current Benchmarks

| Operation | JVM Clojure | Cljr | Notes |
|-----------|-------------|------|-------|
| `(reduce + (range 1M))` | ~5ms | **<1ms** | O(1) Gauss formula |
| `(mapv (fn [x] "") (map str (range 1M)))` | 14-25ms | ~23ms | Map fusion |
| `(mapv inc (range 1M))` | ~10ms | ~75ms | Boxing overhead |

## Optimization Layers

Cljr uses a multi-tier optimization strategy:

```
Tier 1: O(1) Mathematical Formulas (Gauss sum)
    ↓
Tier 2: SIMD Vectorization (Vector256<long>)
    ↓
Tier 3: Specialized Fast Paths (MapvLongRangeInc)
    ↓
Tier 4: Type-Dispatched Operations (pattern matching)
    ↓
Tier 5: Generic Fallback (dynamic dispatch)
```

---

## 1. Map Fusion

**What it does:** Chains nested `map` operations into a single pass.

```clojure
;; Without fusion: 3 iterations over data
(mapv f (map g (map h source)))

;; With fusion: 1 iteration with composed function f∘g∘h
```

**Implementation:** `Core.cs` lines 464-474

```csharp
var composed = f;
var source = coll;
while (source is MappedEnumerable mapped)
{
    var inner = mapped.Func;
    var outer = composed;
    composed = x => outer(inner(x));  // Function composition
    source = mapped.Source;
}
```

**Quirks:**
- Only fuses `MappedEnumerable` objects (created by `map`)
- Does not fuse `filter`, `take`, etc.
- Function composition creates closure allocations

---

## 2. LongRange Optimizations

### 2.1 O(1) Gauss Formula for Sum

```clojure
(reduce + (range 1000000))  ; O(1), not O(n)!
```

**Implementation:** `LongRange.cs` line 59

```csharp
// sum = n/2 * (first + last)
long first = CurrentStart;
long last = first + (long)(Count - 1) * Step;
return (long)Count * (first + last) / 2;
```

**Detection:** Uses behavioral testing to identify `+` function:
```csharp
// Checks: f(1, 2) == 3
private static bool IsAddFunction(object? f)
```

### 2.2 Specialized MapvLongRange Functions

For common operations, Cljr bypasses delegate calls entirely:

| Function | Optimization |
|----------|--------------|
| `inc` | Direct `current + 1` computation |
| `dec` | Direct `current - 1` computation |
| `identity` | Direct value copy |
| `negate` | Direct `-current` computation |

**8-element loop unrolling:**
```csharp
for (int i = 0; i < unrollEnd; i += 8)
{
    tv.ConjFast(current + 1);
    tv.ConjFast(current + step + 1);
    // ... 6 more elements
    current += step8;
}
```

### 2.3 SIMD Vectorization

When `Vector256.IsHardwareAccelerated` is true (AVX2):

```csharp
var current = Vector256.Create(start+1, start+step+1, ...);
var increment = Vector256.Create(step * 4);

for (int i = 0; i < vectorEnd; i += 4)
{
    current.CopyTo(buffer);
    // Store 4 elements
    current = Vector256.Add(current, increment);
}
```

**Quirk:** SIMD only used for known operations (inc, dec, identity, negate, double).

---

## 3. Type-Dispatched Numeric Operations

**Problem:** `dynamic` dispatch is slow (~15ns per call).

**Solution:** Pattern matching with explicit type combinations:

```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
public static object Add(object? a, object? b) => (a, b) switch
{
    (long la, long lb) => la + lb,
    (long la, int ib) => la + ib,
    (int ia, long lb) => ia + lb,
    (int ia, int ib) => ia + ib,
    (double da, double db) => da + db,
    // ... 6 more combinations
    _ => AddSlow(a, b)  // Fallback to dynamic
};
```

**Performance:** ~3ns for matched types vs ~15ns for dynamic.

**Covered operations:** `+`, `-`, `*`, `/`, `mod`, `<`, `>`, `<=`, `>=`, `inc`, `dec`

---

## 4. PersistentVector Transient Operations

### Standard Path (Thread-Safe)
```csharp
public ITransientVector Conj(object? val)
{
    EnsureEditable();  // Volatile read on every call
    // ... append logic
}
```

### Fast Path (Single-Threaded)
```csharp
[MethodImpl(MethodImplOptions.AggressiveInlining)]
internal void ConjFast(object? val)
{
    // NO volatile check - only safe for local transients
    int tailIdx = _count - TailOffset();
    if (tailIdx < Width) {
        _tail[tailIdx] = val;
        _count++;
        return;
    }
    // ...
}
```

**When to use:** Internal optimizations only. Never expose transients created with `CreateTransientFast()`.

---

## 5. Lazy Sequence Implementation

### Double-Checked Locking
```csharp
private ISeq? Sval()
{
    if (_fn != null)
    {
        lock (this)
        {
            if (_fn != null)  // Double-checked
            {
                _sv = _fn();
                _fn = null;   // Enable GC of closure
            }
        }
    }
    // ...
}
```

### Nested LazySeq Unwinding
Prevents stack overflow from deeply nested lazy sequences:
```csharp
while (ls is LazySeq lazy)  // Iterative, not recursive
{
    ls = lazy.Sval();
}
```

---

## 6. IReduce Protocol

Collections can implement custom reduction strategies:

```csharp
public interface IReduce : IReduceInit
{
    object? reduce(Func<object?, object?, object?> f);
}
```

**LongRange implementation:**
- Uses unboxed `long` iteration
- Avoids IEnumerator allocations
- Supports early termination via `Reduced` wrapper

---

## Known Performance Issues

### 1. Boxing Overhead

**Problem:** `(mapv inc (range 1M))` is 7.5x slower than JVM.

**Root cause:**
```csharp
// f expects object?, but current is long → BOXING
tv = tv.Conj(f(current));  // 1M allocations
```

**Workaround:** Use specialized paths when possible:
```clojure
;; Fast: detected as inc operation
(mapv inc (range 1000000))

;; Slow: generic function path
(mapv (fn [x] (+ x 1)) (range 1000000))
```

### 2. Volatile Reads in Transients

**Problem:** `EnsureEditable()` called on every `Conj()`.

**Impact:** ~10ms overhead for 1M elements.

**Mitigation:** Internal `ConjFast` skips check, but not exposed to users.

### 3. Dynamic Dispatch Fallback

**Problem:** Non-numeric types fall back to `dynamic`:
```csharp
_ => (dynamic?)a + (dynamic?)b  // Slow path
```

**Workaround:** Add type hints for performance-critical code:
```clojure
(defn add [^long a ^long b] (+ a b))
```

### 4. Function Detection Overhead

**Problem:** Behavioral testing calls function 3 times:
```csharp
var r0 = f(0L);
var r1 = f(1L);
var r10 = f(10L);
```

**Impact:** ~100ns per mapv call (amortized over large collections).

**Quirk:** Functions with side effects may behave unexpectedly.

---

## Optimization Tips

### Use Type Hints
```clojure
;; Slow: dynamic dispatch
(defn sum [a b] (+ a b))

;; Fast: primitive arithmetic
(defn sum [^long a ^long b] (+ a b))
```

### Prefer `reduce` for Sums
```clojure
;; O(1) with Gauss formula
(reduce + (range 1000000))

;; O(n) - no optimization
(apply + (range 1000000))
```

### Use `mapv` for Materialization
```clojure
;; Gets map fusion benefits
(mapv f (map g (map h coll)))

;; No fusion
(vec (map f (map g (map h coll))))
```

### Avoid Nested Anonymous Functions
```clojure
;; Slow: composition overhead
(mapv #(f (g %)) coll)

;; Faster: let map fusion work
(mapv f (map g coll))
```

---

## Profiling Tools

### Using `time`
```clojure
(time (reduce + (range 1000000)))
;; "Elapsed time: 0.5 msecs"
```

### JIT Warmup
First call includes JIT compilation. Run twice for accurate measurements:
```clojure
(reduce + (range 1000000))  ; Warmup
(time (reduce + (range 1000000)))  ; Measure
```

---

## File References

| Component | File | Key Lines |
|-----------|------|-----------|
| Map Fusion | `Core.cs` | 403-500 |
| LongRange SIMD | `LongRange.cs` | 254-495 |
| Gauss Formula | `LongRange.cs` | 59 |
| Numeric Dispatch | `Core.cs` | 3320-3509 |
| Transient ConjFast | `PersistentVector.cs` | 739-772 |
| LazySeq | `LazySeq.cs` | 40-72 |
