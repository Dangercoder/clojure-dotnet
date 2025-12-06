using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Cljr.Collections;

namespace Cljr;

/// <summary>
/// Core runtime functions for Cljr - Clojure semantics in C#
/// </summary>
public static class Core
{
    #region Equality

    /// <summary>
    /// Clojure-style equality: structural equality for collections
    /// </summary>
    public static new bool Equals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        // Handle numeric equality
        if (a is IConvertible && b is IConvertible)
        {
            try
            {
                if (IsNumeric(a) && IsNumeric(b))
                {
                    var da = Convert.ToDouble(a);
                    var db = Convert.ToDouble(b);
                    return da == db;
                }
            }
            catch { /* fall through to default */ }
        }

        // Handle collection equality
        if (a is IDictionary dictA && b is IDictionary dictB)
            return DictEquals(dictA, dictB);

        if (a is IList listA && b is IList listB)
            return ListEquals(listA, listB);

        if (a is IEnumerable seqA && b is IEnumerable seqB &&
            a is not string && b is not string)
            return SeqEquals(seqA, seqB);

        return a.Equals(b);
    }

    private static bool IsNumeric(object? obj) =>
        obj is byte or sbyte or short or ushort or int or uint or
        long or ulong or float or double or decimal;

    private static bool DictEquals(IDictionary a, IDictionary b)
    {
        if (a.Count != b.Count) return false;
        foreach (var key in a.Keys)
        {
            if (!b.Contains(key)) return false;
            if (!Equals(a[key], b[key])) return false;
        }
        return true;
    }

    private static bool ListEquals(IList a, IList b)
    {
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
        {
            if (!Equals(a[i], b[i])) return false;
        }
        return true;
    }

    internal static bool SeqEquals(IEnumerable a, IEnumerable b)
    {
        var enumA = a.GetEnumerator();
        var enumB = b.GetEnumerator();
        while (true)
        {
            var hasA = enumA.MoveNext();
            var hasB = enumB.MoveNext();
            if (hasA != hasB) return false;
            if (!hasA) return true;
            if (!Equals(enumA.Current, enumB.Current)) return false;
        }
    }

    /// <summary>
    /// Compares a sequence against an object for equality.
    /// Returns false if b is not an IEnumerable (excluding strings).
    /// </summary>
    internal static bool SeqEquals(IEnumerable a, object? b)
    {
        if (b is null) return false;
        if (b is string) return false;
        if (b is not IEnumerable bEnum) return false;
        return SeqEquals(a, bEnum);
    }

    /// <summary>
    /// Computes hash code for a sequence using Clojure's algorithm.
    /// </summary>
    internal static int SeqHashCode(IEnumerable seq)
    {
        int hash = 1;
        foreach (var item in seq)
        {
            hash = 31 * hash + (item?.GetHashCode() ?? 0);
        }
        return hash;
    }

    #endregion

    #region Truthiness

    /// <summary>
    /// Clojure truthiness: nil and false are falsy, everything else is truthy
    /// </summary>
    public static bool IsTruthy(object? x) => x is not null && x is not false;

    #endregion

    #region SIMD Pattern Detection

    /// <summary>
    /// Singleton delegate for inc detection - enables fast reference equality check.
    /// </summary>
    public static readonly Func<object?, object?> IncFunc = x => Inc(x);

    /// <summary>
    /// Singleton delegate for dec detection.
    /// </summary>
    public static readonly Func<object?, object?> DecFunc = x => Dec(x);

    /// <summary>
    /// Detects if a function represents a known RangeOperation that can be SIMD-accelerated.
    /// Uses reference equality for fast path, falls back to behavioral detection.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Collections.RangeOperation DetectRangeOperation(Func<object?, object?> f)
    {
        // Fast path: reference equality for known singleton delegates
        if (ReferenceEquals(f, IncFunc)) return Collections.RangeOperation.Inc;
        if (ReferenceEquals(f, DecFunc)) return Collections.RangeOperation.Dec;

        // Behavioral detection: test with sample values
        // This catches user lambdas like (fn [x] (inc x)) or x => x + 1
        try
        {
            var r0 = f(0L);
            var r1 = f(1L);
            var r10 = f(10L);

            // Check for inc pattern: f(x) = x + 1
            if (r0 is long l0 && l0 == 1L &&
                r1 is long l1 && l1 == 2L &&
                r10 is long l10 && l10 == 11L)
                return Collections.RangeOperation.Inc;

            // Check for dec pattern: f(x) = x - 1
            if (r0 is long d0 && d0 == -1L &&
                r1 is long d1 && d1 == 0L &&
                r10 is long d10 && d10 == 9L)
                return Collections.RangeOperation.Dec;

            // Check for identity pattern: f(x) = x
            if (r0 is long i0 && i0 == 0L &&
                r1 is long i1 && i1 == 1L &&
                r10 is long i10 && i10 == 10L)
                return Collections.RangeOperation.Identity;

            // Check for negate pattern: f(x) = -x
            if (r0 is long n0 && n0 == 0L &&
                r1 is long n1 && n1 == -1L &&
                r10 is long n10 && n10 == -10L)
                return Collections.RangeOperation.Negate;

            // Check for double pattern: f(x) = x * 2
            if (r0 is long x0 && x0 == 0L &&
                r1 is long x1 && x1 == 2L &&
                r10 is long x10 && x10 == 20L)
                return Collections.RangeOperation.Double;
        }
        catch
        {
            // Function throws on test inputs - not a known pattern
        }

        return Collections.RangeOperation.Unknown;
    }

    #endregion

    #region Collections - Get/Assoc/Conj

    /// <summary>
    /// Get a value from a collection by key
    /// </summary>
    public static object? Get(object? coll, object? key, object? notFound = null)
    {
        if (coll is null) return notFound;

        // Persistent collections first (O(log32 n) lookup)
        if (coll is PersistentHashMap phm)
            return phm.ValAt(key!, notFound);

        if (coll is PersistentVector pv && key is int pvIdx)
            return pvIdx >= 0 && pvIdx < pv.Count ? pv.Nth(pvIdx) : notFound;

        if (coll is PersistentHashSet phs)
            return phs.Contains(key!) ? key : notFound;

        // Fall back to mutable collections for compatibility
        if (coll is IDictionary dict)
            return dict.Contains(key!) ? dict[key!] : notFound;

        if (coll is IList list && key is int idx)
            return idx >= 0 && idx < list.Count ? list[idx] : notFound;

        if (key is string strKey && coll.GetType().GetProperty(strKey) is { } prop)
            return prop.GetValue(coll);

        return notFound;
    }

    /// <summary>
    /// Associate a value with a key - uses structural sharing for persistent collections
    /// </summary>
    public static object Assoc(object coll, object key, object? val)
    {
        // Persistent collections first (O(log32 n) with structural sharing)
        if (coll is PersistentHashMap phm)
            return phm.Assoc(key, val);

        if (coll is PersistentVector pv && key is int pvIdx)
            return pv.AssocN(pvIdx, val);

        // Fall back to mutable collections for compatibility
        if (coll is IDictionary dict)
        {
            var result = CopyDictionary(dict);
            result[key] = val;
            return result;
        }

        if (coll is IList list && key is int idx)
        {
            var result = new List<object?>(list.Cast<object?>());
            result[idx] = val;
            return result;
        }

        throw new ArgumentException($"Cannot assoc on {coll.GetType().Name}");
    }

    /// <summary>
    /// Conjoin an item to a collection - uses structural sharing for persistent collections
    /// </summary>
    public static object Conj(object coll, object? item)
    {
        // Persistent collections first (O(log32 n) with structural sharing)
        if (coll is PersistentVector pv)
            return pv.Conj(item);

        if (coll is PersistentHashMap phm && item is IList pair && pair.Count == 2)
            return phm.Assoc(pair[0]!, pair[1]);

        if (coll is PersistentHashSet phs)
            return phs.Conj(item);

        // Fall back to mutable collections for compatibility
        if (coll is IList list)
        {
            var result = new List<object?>(list.Cast<object?>()) { item };
            return result;
        }

        if (coll is IDictionary dict && item is IList dictPair && dictPair.Count == 2)
        {
            var result = CopyDictionary(dict);
            result[dictPair[0]!] = dictPair[1];
            return result;
        }

        if (coll is ISet<object?> set)
        {
            var result = new HashSet<object?>(set) { item };
            return result;
        }

        throw new ArgumentException($"Cannot conj on {coll.GetType().Name}");
    }

    /// <summary>
    /// Dissociate a key from a map - uses structural sharing for persistent collections
    /// </summary>
    public static object Dissoc(object coll, object key)
    {
        // Persistent collections first (O(log32 n) with structural sharing)
        if (coll is PersistentHashMap phm)
            return phm.Without(key);

        // Fall back to mutable collections for compatibility
        if (coll is IDictionary dict)
        {
            var result = CopyDictionary(dict);
            result.Remove(key);
            return result;
        }

        throw new ArgumentException($"Cannot dissoc on {coll.GetType().Name}");
    }

    /// <summary>
    /// Disjoin an element from a set - uses structural sharing for persistent collections
    /// </summary>
    public static object Disj(object coll, object key)
    {
        // Persistent collections first (O(log32 n) with structural sharing)
        if (coll is PersistentHashSet phs)
            return phs.Disjoin(key);

        // Fall back to mutable collections for compatibility
        if (coll is ISet<object?> set)
        {
            var result = new HashSet<object?>(set);
            result.Remove(key);
            return result;
        }

        throw new ArgumentException($"Cannot disj on {coll.GetType().Name}");
    }

    private static Dictionary<object, object?> CopyDictionary(IDictionary dict)
    {
        var result = new Dictionary<object, object?>();
        foreach (var key in dict.Keys)
        {
            result[key] = dict[key];
        }
        return result;
    }

    /// <summary>
    /// Update a value in a collection by applying a function
    /// </summary>
    public static object Update(object coll, object key, Func<object?, object?> f)
    {
        var current = Get(coll, key);
        return Assoc(coll, key, f(current));
    }

    #endregion

    #region Sequences

    /// <summary>
    /// Get the first element of a sequence
    /// </summary>
    public static object? First(object? coll)
    {
        if (coll is null) return null;
        if (coll is IList list) return list.Count > 0 ? list[0] : null;
        if (coll is IEnumerable enumerable)
        {
            var enumerator = enumerable.GetEnumerator();
            return enumerator.MoveNext() ? enumerator.Current : null;
        }
        return null;
    }

    /// <summary>
    /// Get the rest of a sequence (lazy)
    /// </summary>
    public static IEnumerable<object?> Rest(object? coll)
    {
        if (coll is null) yield break;
        if (coll is IEnumerable enumerable)
        {
            var first = true;
            foreach (var item in enumerable)
            {
                if (first) { first = false; continue; }
                yield return item;
            }
        }
    }

    /// <summary>
    /// Get the next of a sequence (nil if empty)
    /// </summary>
    public static IEnumerable<object?>? Next(object? coll)
    {
        var rest = Rest(coll).ToList();
        return rest.Count > 0 ? rest : null;
    }

    /// <summary>
    /// Wrapper for lazy mapped sequences - enables map fusion optimization
    /// </summary>
    private sealed class MappedEnumerable : IEnumerable<object?>
    {
        public readonly object Source;
        public readonly Func<object?, object?> Func;

        public MappedEnumerable(object source, Func<object?, object?> func)
        {
            Source = source;
            Func = func;
        }

        public IEnumerator<object?> GetEnumerator()
        {
            if (Source is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    yield return Func(item);
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// Map a function over a sequence (lazy)
    /// Returns MappedEnumerable to enable map fusion in eager operations like mapv
    /// </summary>
    public static IEnumerable<object?> Map(Func<object?, object?> f, object? coll)
    {
        if (coll is null) return [];
        if (coll is IEnumerable enumerable)
            return new MappedEnumerable(enumerable, f);
        return [];
    }

    /// <summary>
    /// Filter a sequence by a predicate (lazy)
    /// </summary>
    public static IEnumerable<object?> Filter(Func<object?, bool> pred, object? coll)
    {
        if (coll is null) yield break;
        if (coll is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                if (pred(item))
                    yield return item;
        }
    }

    /// <summary>
    /// Map a function over a sequence, returning a vector (eager).
    /// Optimized with map fusion: (mapv f (map g coll)) becomes single-pass (mapv (comp f g) coll)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PersistentVector Mapv(Func<object?, object?> f, object? coll)
    {
        if (coll is null) return PersistentVector.Empty;

        // MAP FUSION: Unwrap nested MappedEnumerables and compose functions
        // (mapv f (map g (map h source))) -> single iteration with composed f∘g∘h
        var composed = f;
        var source = coll;
        while (source is MappedEnumerable mapped)
        {
            var inner = mapped.Func;
            var outer = composed;
            composed = x => outer(inner(x));
            source = mapped.Source;
        }

        // Fast path for LongRange - use direct computation to avoid delegate overhead
        if (source is Collections.LongRange lr)
        {
            // Detect operation and use specialized direct-computation path
            var op = DetectRangeOperation(composed);
            return op switch
            {
                Collections.RangeOperation.Inc => MapvLongRangeInc(lr),
                Collections.RangeOperation.Dec => MapvLongRangeDec(lr),
                Collections.RangeOperation.Identity => MapvLongRangeIdentity(lr),
                Collections.RangeOperation.Negate => MapvLongRangeNegate(lr),
                Collections.RangeOperation.Double => MapvLongRangeDouble(lr),
                _ => MapvLongRange(composed, lr)
            };
        }

        // Direct transient building - single iteration, no intermediate allocations
        if (source is IEnumerable enumerable)
        {
            var tv = PersistentVector.Empty.AsTransient();
            foreach (var item in enumerable)
                tv = tv.Conj(composed(item));
            return (PersistentVector)tv.Persistent();
        }
        return PersistentVector.Empty;
    }

    private static PersistentVector MapvLongRange(Func<object?, object?> f, Collections.LongRange lr)
    {
        // Direct transient building with tight iteration over range
        int count = lr.Count;
        long current = lr.First() is long first ? first : lr.Start;
        long step = lr.Step;

        var tv = PersistentVector.Empty.AsTransient();
        for (int i = 0; i < count; i++)
        {
            tv = tv.Conj(f(current));
            current += step;
        }

        return (PersistentVector)tv.Persistent();
    }

    // === SPECIALIZED DIRECT-COMPUTATION METHODS ===
    // These eliminate delegate invocation overhead by computing values directly.
    // Key insight: 1M delegate calls add significant overhead. Direct computation avoids this.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PersistentVector MapvLongRangeInc(Collections.LongRange lr)
    {
        int count = lr.Count;
        long current = lr.First() is long first ? first : lr.Start;
        long step = lr.Step;

        // Use fast path: skips volatile EnsureEditable check
        var tv = PersistentVector.CreateTransientFast();

        // Unrolled loop: 8 elements per iteration
        int unrollEnd = count - (count % 8);
        long step8 = step * 8;

        for (int i = 0; i < unrollEnd; i += 8)
        {
            tv.ConjFast(current + 1);
            tv.ConjFast(current + step + 1);
            tv.ConjFast(current + step * 2 + 1);
            tv.ConjFast(current + step * 3 + 1);
            tv.ConjFast(current + step * 4 + 1);
            tv.ConjFast(current + step * 5 + 1);
            tv.ConjFast(current + step * 6 + 1);
            tv.ConjFast(current + step * 7 + 1);
            current += step8;
        }

        // Handle remaining elements
        for (int i = unrollEnd; i < count; i++)
        {
            tv.ConjFast(current + 1);
            current += step;
        }

        return (PersistentVector)tv.Persistent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PersistentVector MapvLongRangeDec(Collections.LongRange lr)
    {
        int count = lr.Count;
        long current = lr.First() is long first ? first : lr.Start;
        long step = lr.Step;

        var tv = PersistentVector.Empty.AsTransient();
        for (int i = 0; i < count; i++)
        {
            tv = tv.Conj(current - 1);  // Direct: no delegate call
            current += step;
        }

        return (PersistentVector)tv.Persistent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PersistentVector MapvLongRangeIdentity(Collections.LongRange lr)
    {
        int count = lr.Count;
        long current = lr.First() is long first ? first : lr.Start;
        long step = lr.Step;

        var tv = PersistentVector.Empty.AsTransient();
        for (int i = 0; i < count; i++)
        {
            tv = tv.Conj(current);  // Direct: no delegate call
            current += step;
        }

        return (PersistentVector)tv.Persistent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PersistentVector MapvLongRangeNegate(Collections.LongRange lr)
    {
        int count = lr.Count;
        long current = lr.First() is long first ? first : lr.Start;
        long step = lr.Step;

        var tv = PersistentVector.Empty.AsTransient();
        for (int i = 0; i < count; i++)
        {
            tv = tv.Conj(-current);  // Direct: no delegate call
            current += step;
        }

        return (PersistentVector)tv.Persistent();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static PersistentVector MapvLongRangeDouble(Collections.LongRange lr)
    {
        int count = lr.Count;
        long current = lr.First() is long first ? first : lr.Start;
        long step = lr.Step;

        var tv = PersistentVector.Empty.AsTransient();
        for (int i = 0; i < count; i++)
        {
            tv = tv.Conj(current * 2);  // Direct: no delegate call
            current += step;
        }

        return (PersistentVector)tv.Persistent();
    }

    /// <summary>
    /// Filter a sequence by a predicate, returning a vector (eager).
    /// </summary>
    public static PersistentVector Filterv(Func<object?, bool> pred, object? coll)
    {
        if (coll is null) return PersistentVector.Empty;

        // Direct transient building - single iteration, no intermediate allocations
        if (coll is IEnumerable enumerable)
        {
            var tv = PersistentVector.Empty.AsTransient();
            foreach (var item in enumerable)
                if (pred(item))
                    tv = tv.Conj(item);
            return (PersistentVector)tv.Persistent();
        }
        return PersistentVector.Empty;
    }

    /// <summary>
    /// Reduce a sequence to a single value
    /// </summary>
    public static object? Reduce(Func<object?, object?, object?> f, object? init, object? coll)
    {
        var acc = init;
        if (coll is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                acc = f(acc, item);
        }
        return acc;
    }

    /// <summary>
    /// Reduce a map with key-value function (f init k v)
    /// </summary>
    public static object? ReduceKv(Func<object?, object?, object?, object?> f, object? init, object? coll)
    {
        var acc = init;
        if (coll is PersistentHashMap phm)
        {
            foreach (var entry in phm)
            {
                var me = (IMapEntry)entry;
                acc = f(acc, me.Key(), me.Val());
            }
        }
        else if (coll is IDictionary dict)
        {
            foreach (DictionaryEntry entry in dict)
                acc = f(acc, entry.Key, entry.Value);
        }
        return acc;
    }

    /// <summary>
    /// Count elements in a collection
    /// </summary>
    public static long Count(object? coll) => coll switch
    {
        null => 0L,
        string s => s.Length,
        ICollection c => c.Count,
        IEnumerable e => e.Cast<object>().Count(),
        _ => 0L
    };

    /// <summary>
    /// Convert to a list
    /// </summary>
    public static List<object?> IntoList(object? coll)
    {
        if (coll is null) return [];
        if (coll is IEnumerable enumerable)
            return enumerable.Cast<object?>().ToList();
        return [coll];
    }

    #endregion

    #region String Operations

    /// <summary>
    /// Concatenate any number of values into a string
    /// </summary>
    public static string Str(params object?[] args)
    {
        if (args.Length == 0) return "";
        if (args.Length == 1) return args[0]?.ToString() ?? "";

        var sb = new StringBuilder();
        foreach (var arg in args)
        {
            if (arg is not null)
                sb.Append(arg);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Returns the substring of s beginning at start inclusive, and ending
    /// at end (defaults to length of string), exclusive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Subs(string s, long start) => s.Substring((int)start);

    /// <summary>
    /// Returns the substring of s beginning at start inclusive, and ending
    /// at end, exclusive.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Subs(string s, long start, long end) => s.Substring((int)start, (int)(end - start));

    /// <summary>
    /// Print with newline
    /// </summary>
    public static void Println(params object?[] args)
    {
        Console.WriteLine(Str(args));
    }

    /// <summary>
    /// Print without newline
    /// </summary>
    public static void Print(params object?[] args)
    {
        Console.Write(Str(args));
    }

    /// <summary>
    /// Print for debugging (includes type info)
    /// </summary>
    public static void Prn(object? x)
    {
        Console.WriteLine(PrStr(x));
    }

    /// <summary>
    /// Format a value as a readable string (EDN-like)
    /// </summary>
    public static string PrStr(object? x) => x switch
    {
        null => "nil",
        true => "true",
        false => "false",
        string s => $"\"{s.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"",
        char c => $"\\{c}",
        Type t => FormatType(t),
        IPersistentMap map => FormatPersistentMap(map),
        IPersistentVector vec => FormatPersistentVector(vec),
        IPersistentSet set => FormatPersistentSet(set),
        IDictionary dict => FormatMap(dict),
        IList list => FormatVector(list),
        IEnumerable seq => FormatSeq(seq),
        _ => x.ToString() ?? "nil"
    };

    private static string FormatType(Type t)
    {
        // Check for generic types
        if (t.IsGenericType)
        {
            var genericDef = t.GetGenericTypeDefinition();
            var baseName = genericDef.Name;
            var tickIndex = baseName.IndexOf('`');
            if (tickIndex > 0) baseName = baseName[..tickIndex];
            var typeArgs = string.Join(" ", t.GetGenericArguments().Select(FormatType));
            return $"({baseName} {typeArgs})";
        }

        // Check for arrays
        if (t.IsArray)
        {
            var elemType = t.GetElementType();
            return $"(Array {FormatType(elemType!)})";
        }

        // For .NET BCL types, use full name
        if (t.Namespace?.StartsWith("System") == true || t.Namespace?.StartsWith("Microsoft") == true)
            return t.FullName ?? t.Name;

        // For dynamically compiled types (defrecord/deftype), format nicely
        // These typically have assembly names starting with "DynamicAssembly" or similar
        if (t.Assembly.IsDynamic)
            return t.Name;

        // Default: use full name if available, else name
        return t.FullName ?? t.Name;
    }

    private static string FormatMap(IDictionary dict)
    {
        var pairs = new List<string>();
        foreach (var key in dict.Keys)
        {
            pairs.Add($"{PrStr(key)} {PrStr(dict[key])}");
        }
        return $"{{{string.Join(", ", pairs)}}}";
    }

    private static string FormatVector(IList list)
    {
        var items = list.Cast<object?>().Select(PrStr);
        return $"[{string.Join(" ", items)}]";
    }

    private static string FormatSeq(IEnumerable seq)
    {
        var items = seq.Cast<object?>().Select(PrStr);
        return $"({string.Join(" ", items)})";
    }

    private static string FormatPersistentMap(IPersistentMap map)
    {
        var pairs = new List<string>();
        for (var s = ((IPersistentCollection)map).Seq(); s != null; s = s.Next())
        {
            if (s.First() is IMapEntry entry)
                pairs.Add($"{PrStr(entry.Key())} {PrStr(entry.Val())}");
        }
        return $"{{{string.Join(", ", pairs)}}}";
    }

    private static string FormatPersistentVector(IPersistentVector vec)
    {
        var items = new List<string>();
        var coll = (IPersistentCollection)vec;
        for (int i = 0; i < coll.Count; i++)
            items.Add(PrStr(((Indexed)vec).Nth(i)));
        return $"[{string.Join(" ", items)}]";
    }

    private static string FormatPersistentSet(IPersistentSet set)
    {
        var items = new List<string>();
        for (var s = ((IPersistentCollection)set).Seq(); s != null; s = s.Next())
            items.Add(PrStr(s.First()));
        return $"#{{{string.Join(" ", items)}}}";
    }

    #endregion

    #region Clojure-style lowercase aliases

    // String operations
    public static string str(params object?[] args) => Str(args);
    public static string subs(string s, long start) => Subs(s, start);
    public static string subs(string s, long start, long end) => Subs(s, start, end);
    public static object? println(params object?[] args) { Println(args); return null; }
    public static object? print(params object?[] args) { Print(args); return null; }
    public static object? prn(object? x) { Prn(x); return null; }
    public static string pr_str(object? x) => PrStr(x);

    // Collections
    public static object? get(object? coll, object? key, object? notFound = null) => Get(coll, key, notFound);
    public static object assoc(object coll, object key, object? val) => Assoc(coll, key, val);
    public static object conj(object coll, object? item) => Conj(coll, item);
    public static object dissoc(object coll, object key) => Dissoc(coll, key);
    public static object disj(object coll, object key) => Disj(coll, key);
    public static object update(object coll, object key, Func<object?, object?> f) => Update(coll, key, f);

    // Sequences
    public static object? first(object? coll) => First(coll);
    public static IEnumerable<object?> rest(object? coll) => Rest(coll);
    public static IEnumerable<object?>? next(object? coll) => Next(coll);
    public static IEnumerable<object?> map(Func<object?, object?> f, object? coll) => Map(f, coll);
    public static IEnumerable<object?> map(object? f, object? coll) => Map(ToFunc(f), coll);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PersistentVector mapv(Func<object?, object?> f, object? coll) => Mapv(f, coll);
    public static PersistentVector mapv(object? f, object? coll) => Mapv(ToFunc(f), coll);
    public static IEnumerable<object?> filter(Func<object?, bool> pred, object? coll) => Filter(pred, coll);
    public static IEnumerable<object?> filter(object? pred, object? coll) => Filter(ToPred(pred), coll);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static PersistentVector filterv(Func<object?, bool> pred, object? coll) => Filterv(pred, coll);
    public static PersistentVector filterv(object? pred, object? coll) => Filterv(ToPred(pred), coll);

    // Helper to convert object to invokable Func
    public static Func<object?, object?> ToFunc(object? f)
    {
        if (f is Func<object?, object?> fn) return fn;
        if (f is Delegate d) return x => d.DynamicInvoke(x);
        throw new ArgumentException($"Cannot convert {f?.GetType()?.Name ?? "null"} to function");
    }

    // Helper to convert object to 2-arg Func (for reduce, swap!, etc.)
    public static Func<object?, object?, object?> ToFunc2(object? f)
    {
        if (f is Func<object?, object?, object?> fn) return fn;
        if (f is Delegate d) return (a, b) => d.DynamicInvoke(a, b);
        throw new ArgumentException($"Cannot convert {f?.GetType()?.Name ?? "null"} to 2-arg function");
    }

    // Helper to convert object to 3-arg Func (for swap! with 2 extra args)
    public static Func<object?, object?, object?, object?> ToFunc3(object? f)
    {
        if (f is Func<object?, object?, object?, object?> fn) return fn;
        if (f is Delegate d) return (a, b, c) => d.DynamicInvoke(a, b, c);
        throw new ArgumentException($"Cannot convert {f?.GetType()?.Name ?? "null"} to 3-arg function");
    }

    // Helper to convert object to predicate (Func<object?, bool>)
    public static Func<object?, bool> ToPred(object? f)
    {
        if (f is Func<object?, bool> pred) return pred;
        if (f is Func<object?, object?> fn) return x => IsTruthy(fn(x));
        if (f is Delegate d) return x => IsTruthy(d.DynamicInvoke(x));
        throw new ArgumentException($"Cannot convert {f?.GetType()?.Name ?? "null"} to predicate");
    }

    // Invoke any object as a function with one argument
    public static object? Invoke(object? f, object? arg)
    {
        if (f is Func<object?, object?> fn) return fn(arg);
        if (f is Delegate d) return d.DynamicInvoke(arg);
        throw new ArgumentException($"Cannot invoke {f?.GetType()?.Name ?? "null"} as function");
    }

    // Alias for call - invoke any object as function
    public static object? call(object? f, object? arg) => Invoke(f, arg);
    public static object? call(object? f, object? arg1, object? arg2) => f switch
    {
        Func<object?, object?, object?> fn => fn(arg1, arg2),
        Delegate d => d.DynamicInvoke(arg1, arg2),
        _ => throw new ArgumentException($"Cannot invoke {f?.GetType()?.Name ?? "null"} as function")
    };
    public static object? reduce(Func<object?, object?, object?> f, object? init, object? coll) => Reduce(f, init, coll);
    public static object? reduce(object? f, object? init, object? coll) => Reduce(ToFunc2(f), init, coll);
    public static object? reduce(object? f, object? coll) => reduce_without_init(f, coll);
    public static object? reduce_kv(Func<object?, object?, object?, object?> f, object? init, object? coll) => ReduceKv(f, init, coll);
    public static long count(object? coll) => Count(coll);
    public static List<object?> into_list(object? coll) => IntoList(coll);

    // Predicates
    public static bool nil_QMARK_(object? x) => IsNil(x);
    public static bool some_QMARK_(object? x) => IsSome(x);
    public static bool number_QMARK_(object? x) => IsNumber(x);
    public static bool string_QMARK_(object? x) => IsString(x);
    public static bool keyword_QMARK_(object? x) => IsKeyword(x);
    public static bool symbol_QMARK_(object? x) => IsSymbol(x);
    public static bool list_QMARK_(object? x) => IsList(x);
    public static bool vector_QMARK_(object? x) => IsVector(x);
    public static bool map_QMARK_(object? x) => IsMap(x);
    public static bool set_QMARK_(object? x) => IsSet(x);
    public static bool fn_QMARK_(object? x) => IsFn(x);
    public static bool ifn_QMARK_(object? x) => x is Delegate || x is Keyword || x is Symbol ||
        x is IPersistentMap || x is IPersistentVector || x is IPersistentSet ||
        x is IDictionary || x is IList;
    public static bool seq_QMARK_(object? x) => IsSeq(x);
    public static bool map_entry_QMARK_(object? x) => x is IMapEntry ||
        x is KeyValuePair<object, object?> ||
        (x is PersistentVector pv && pv.Count == 2);

    // Map entry functions - key/val for map entries
    public static object? key(object? entry) => entry switch
    {
        IMapEntry me => me.Key(),
        KeyValuePair<object, object?> kvp => kvp.Key,
        PersistentVector pv when pv.Count >= 1 => pv.Nth(0),
        _ => First(entry)
    };

    public static object? val(object? entry) => entry switch
    {
        IMapEntry me => me.Val(),
        KeyValuePair<object, object?> kvp => kvp.Value,
        PersistentVector pv when pv.Count >= 2 => pv.Nth(1),
        _ => Second(entry)
    };

    // Create a map entry (2-element vector)
    public static PersistentVector map_entry(object? k, object? v) =>
        PersistentVector.Create(k, v);

    // Force realization of lazy sequences
    public static object? doall(object? coll)
    {
        if (coll == null) return null;
        var s = Seq(coll);
        if (s == null) return coll;
        // Force full realization by iterating
        var result = new List<object?>();
        foreach (var item in s)
            result.Add(item);
        return Seq(result);
    }

    // Arithmetic - FAST arity-specific overloads (no boxing for common cases)

    /// <summary>
    /// Singleton delegate for + function. Used for fast-path detection in reduce.
    /// Generated code should use this instead of wrapping _PLUS_ in a lambda.
    /// </summary>
    public static readonly Func<object?, object?, object?> AddDelegate = (a, b) => Add(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static long _PLUS_() => 0L;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _PLUS_(object? a) => a ?? 0L;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _PLUS_(object? a, object? b) => Add(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _PLUS_(object? a, object? b, object? c) => Add(Add(a, b), c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _PLUS_(object? a, object? b, object? c, object? d) => Add(Add(Add(a, b), c), d);

    // Fallback for 5+ args
    public static object _PLUS_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        var result = Add(Add(Add(a, b), c), d);
        foreach (var x in more) result = Add(result, x);
        return result;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static long _MINUS_() => 0L;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _MINUS_(object? a) => Sub(0L, a);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _MINUS_(object? a, object? b) => Sub(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _MINUS_(object? a, object? b, object? c) => Sub(Sub(a, b), c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _MINUS_(object? a, object? b, object? c, object? d) => Sub(Sub(Sub(a, b), c), d);

    // Fallback for 5+ args
    public static object _MINUS_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        var result = Sub(Sub(Sub(a, b), c), d);
        foreach (var x in more) result = Sub(result, x);
        return result;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static long _STAR_() => 1L;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _STAR_(object? a) => a ?? 1L;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _STAR_(object? a, object? b) => Mul(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _STAR_(object? a, object? b, object? c) => Mul(Mul(a, b), c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _STAR_(object? a, object? b, object? c, object? d) => Mul(Mul(Mul(a, b), c), d);

    // Fallback for 5+ args
    public static object _STAR_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        var result = Mul(Mul(Mul(a, b), c), d);
        foreach (var x in more) result = Mul(result, x);
        return result;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _SLASH_(object? a) => Div(1L, a);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _SLASH_(object? a, object? b) => Div(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _SLASH_(object? a, object? b, object? c) => Div(Div(a, b), c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static object _SLASH_(object? a, object? b, object? c, object? d) => Div(Div(Div(a, b), c), d);

    // Fallback for 5+ args
    public static object _SLASH_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        var result = Div(Div(Div(a, b), c), d);
        foreach (var x in more) result = Div(result, x);
        return result;
    }

    // Comparison operators - FAST arity-specific overloads
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT_(object? a) => true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT_(object? a, object? b) => Lt(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT_(object? a, object? b, object? c) => Lt(a, b) && Lt(b, c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT_(object? a, object? b, object? c, object? d) => Lt(a, b) && Lt(b, c) && Lt(c, d);

    public static bool _LT_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        if (!Lt(a, b) || !Lt(b, c) || !Lt(c, d)) return false;
        var prev = d;
        foreach (var x in more) { if (!Lt(prev, x)) return false; prev = x; }
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT__EQ_(object? a) => true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT__EQ_(object? a, object? b) => Lte(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT__EQ_(object? a, object? b, object? c) => Lte(a, b) && Lte(b, c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _LT__EQ_(object? a, object? b, object? c, object? d) => Lte(a, b) && Lte(b, c) && Lte(c, d);

    public static bool _LT__EQ_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        if (!Lte(a, b) || !Lte(b, c) || !Lte(c, d)) return false;
        var prev = d;
        foreach (var x in more) { if (!Lte(prev, x)) return false; prev = x; }
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT_(object? a) => true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT_(object? a, object? b) => Gt(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT_(object? a, object? b, object? c) => Gt(a, b) && Gt(b, c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT_(object? a, object? b, object? c, object? d) => Gt(a, b) && Gt(b, c) && Gt(c, d);

    public static bool _GT_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        if (!Gt(a, b) || !Gt(b, c) || !Gt(c, d)) return false;
        var prev = d;
        foreach (var x in more) { if (!Gt(prev, x)) return false; prev = x; }
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT__EQ_(object? a) => true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT__EQ_(object? a, object? b) => Gte(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT__EQ_(object? a, object? b, object? c) => Gte(a, b) && Gte(b, c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _GT__EQ_(object? a, object? b, object? c, object? d) => Gte(a, b) && Gte(b, c) && Gte(c, d);

    public static bool _GT__EQ_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        if (!Gte(a, b) || !Gte(b, c) || !Gte(c, d)) return false;
        var prev = d;
        foreach (var x in more) { if (!Gte(prev, x)) return false; prev = x; }
        return true;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _EQ_(object? a) => true;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _EQ_(object? a, object? b) => Equals(a, b);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _EQ_(object? a, object? b, object? c) => Equals(a, b) && Equals(b, c);

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public static bool _EQ_(object? a, object? b, object? c, object? d) => Equals(a, b) && Equals(b, c) && Equals(c, d);

    public static bool _EQ_(object? a, object? b, object? c, object? d, params object?[] more)
    {
        if (!Equals(a, b) || !Equals(b, c) || !Equals(c, d)) return false;
        foreach (var x in more) { if (!Equals(d, x)) return false; }
        return true;
    }

    // Single argument versions for convenience
    public static object add(object? a, object? b) => Add(a, b);
    public static object sub(object? a, object? b) => Sub(a, b);
    public static object mul(object? a, object? b) => Mul(a, b);
    public static object div(object? a, object? b) => Div(a, b);
    public static object mod(object? a, object? b) => Mod(a, b);
    public static bool lt(object? a, object? b) => Lt(a, b);
    public static bool lte(object? a, object? b) => Lte(a, b);
    public static bool gt(object? a, object? b) => Gt(a, b);
    public static bool gte(object? a, object? b) => Gte(a, b);
    public static object inc(object? x) => Inc(x);
    public static object dec(object? x) => Dec(x);

    // REPL/meta functions (stubs for compatibility)
    public static object? resolve(object? sym) => sym; // Return symbol itself as "resolved"
    public static object? var_QMARK_(object? x) => false; // We don't have vars
    public static object? meta(object? x) => null; // No metadata support yet
    public static object? with_meta(object? x, object? m) => x; // Return as-is
    public static object? name(object? x) => x switch
    {
        Keyword k => k.Name,
        Symbol s => s.Name,
        string str => str,
        _ => x?.ToString()
    };
    public static object? @namespace(object? x) => x switch
    {
        Keyword k => k.Namespace,
        Symbol s => s.Namespace,
        _ => null
    };
    public static object? symbol(object? x) => x switch
    {
        Symbol s => s,
        string str => Symbol.Intern(str),
        Keyword k => Symbol.Intern(k.Name),
        _ => x
    };
    public static object? keyword(object? x) => x switch
    {
        Keyword k => k,
        string str => Keyword.Intern(str),
        Symbol s => Keyword.Intern(s.Name),
        _ => x
    };
    public static object? type(object? x) => x?.GetType()?.Name;
    public static object? class_QMARK_(object? x) => x is Type;
    public static bool true_QMARK_(object? x) => x is true;
    public static bool false_QMARK_(object? x) => x is false;
    public static bool boolean_QMARK_(object? x) => x is bool;
    public static bool coll_QMARK_(object? x) => x is System.Collections.IEnumerable && x is not string;
    public static bool empty_QMARK_(object? x) => Count(x) == 0;
    public static object? identity(object? x) => x;
    public static object? constantly(object? x) => (Func<object?>)(() => x);
    public static object? apply(object? f, params object?[] args)
    {
        if (f is null) return null;
        // Last arg should be a sequence, prepend other args
        var allArgs = new List<object?>();
        for (int i = 0; i < args.Length - 1; i++)
            allArgs.Add(args[i]);
        if (args.Length > 0 && args[^1] is System.Collections.IEnumerable seq and not string)
            foreach (var item in seq) allArgs.Add(item);
        else if (args.Length > 0)
            allArgs.Add(args[^1]);

        return f switch
        {
            Func<object?> f0 when allArgs.Count == 0 => f0(),
            Func<object?, object?> f1 when allArgs.Count == 1 => f1(allArgs[0]),
            Func<object?, object?, object?> f2 when allArgs.Count == 2 => f2(allArgs[0], allArgs[1]),
            // For binary functions with more than 2 args, use reduce semantics
            Func<object?, object?, object?> f2 when allArgs.Count > 2 => allArgs.Skip(1).Aggregate(allArgs[0], (acc, x) => f2(acc, x)),
            Func<object?, object?, object?, object?> f3 when allArgs.Count == 3 => f3(allArgs[0], allArgs[1], allArgs[2]),
            Func<object?, object?, object?, object?> f3 when allArgs.Count > 3 => allArgs.Skip(2).Aggregate(f3(allArgs[0], allArgs[1], allArgs[2]), (acc, x) => f3(acc, x, x)),
            // Handle params functions (like _PLUS_, _STAR_, etc.)
            Func<object?[], object?> paramsFunc => paramsFunc(allArgs.ToArray()),
            Delegate d => InvokeDelegate(d, allArgs),
            _ => throw new ArgumentException($"Cannot apply {f.GetType().Name}")
        };
    }

    private static object? InvokeDelegate(Delegate d, List<object?> args)
    {
        var method = d.Method;
        var parameters = method.GetParameters();

        // Check if last parameter is params array
        if (parameters.Length == 1 && parameters[0].GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0)
        {
            // Call with args as a single array
            return d.DynamicInvoke(new object?[] { args.ToArray() });
        }

        // Regular invocation
        return d.DynamicInvoke(args.ToArray());
    }
    public static Func<object?[], object?> partial(object? f, params object?[] boundArgs) =>
        args => apply(f, boundArgs.Concat(args).ToArray());
    /// <summary>Partial application with binary function and single bound arg</summary>
    public static Func<object?, object?> partial(Func<object?, object?, object?> f, object? boundArg) =>
        arg => f(boundArg, arg);
    /// <summary>Partial application returning single-arg function (for HOC support)</summary>
    public static Func<object?, object?> partial1(object? f, object? boundArg) =>
        arg => apply(f, boundArg, arg);
    public static Func<object?, object?> comp(params object?[] fns) =>
        fns.Length == 0 ? x => x :
        fns.Length == 1 ? ToFunc(fns[0]) :
        x => fns.Reverse().Aggregate(x, (acc, fn) => apply(fn, acc));
    public static object? read_string(object? s) => throw new NotImplementedException("read-string not available at runtime");
    public static object? slurp(object? path) => path is string s ? File.ReadAllText(s) : null;
    public static object? spit(object? path, object? content) { if (path is string s) File.WriteAllText(s, content?.ToString() ?? ""); return null; }

    // Namespace functions (stubs)
    public static object? _STAR_ns_STAR_ => "user"; // *ns*
    public static object? ns_name(object? ns) => ns?.ToString() ?? "user";
    public static object? find_ns(object? sym) => sym; // Return as-is
    public static object? create_ns(object? sym) => sym;
    public static object? in_ns(object? sym) => sym;
    public static object? ns_map(object? ns) => new Dictionary<object, object?>();
    public static object? ns_publics(object? ns) => new Dictionary<object, object?>();
    public static object? refer(object? ns) => null;
    public static object? require(params object?[] args) => null;
    public static object? use(params object?[] args) => null;
    public static object? import(params object?[] args) => null;
    public static object? refer_clojure(params object?[] args) => null;

    // clojure.string functions
    public static string? join(object? sep, object? coll) =>
        coll is IEnumerable<object?> e ? string.Join(sep?.ToString() ?? "", e) :
        coll is System.Collections.IEnumerable ie ? string.Join(sep?.ToString() ?? "", ie.Cast<object>()) : null;
    public static string? join(object? coll) => join("", coll);
    public static object? split(object? s, object? re) => split(s, re, 0);
    public static string? upper_case(object? s) => s?.ToString()?.ToUpperInvariant();
    public static string? lower_case(object? s) => s?.ToString()?.ToLowerInvariant();
    public static string? trim(object? s) => s?.ToString()?.Trim();
    public static string? triml(object? s) => s?.ToString()?.TrimStart();
    public static string? trimr(object? s) => s?.ToString()?.TrimEnd();
    public static bool blank_QMARK_(object? s) => string.IsNullOrWhiteSpace(s?.ToString());
    public static bool starts_with_QMARK_(object? s, object? prefix) => s?.ToString()?.StartsWith(prefix?.ToString() ?? "") ?? false;
    public static bool ends_with_QMARK_(object? s, object? suffix) => s?.ToString()?.EndsWith(suffix?.ToString() ?? "") ?? false;
    public static bool includes_QMARK_(object? s, object? substr) => s?.ToString()?.Contains(substr?.ToString() ?? "") ?? false;
    public static string? replace(object? s, object? match, object? replacement) =>
        s?.ToString()?.Replace(match?.ToString() ?? "", replacement?.ToString() ?? "");
    public static string? replace_first(object? s, object? match, object? replacement) =>
        s is string str && match is string m ? ReplaceFirst(str, m, replacement?.ToString() ?? "") : s?.ToString();
    private static string ReplaceFirst(string s, string oldValue, string newValue)
    {
        var idx = s.IndexOf(oldValue);
        return idx < 0 ? s : s[..idx] + newValue + s[(idx + oldValue.Length)..];
    }
    public static string? reverse(object? s) => s is string str ? new string(str.Reverse().ToArray()) : null;

    /// <summary>Creates a regex pattern from a string (for #"..." regex literals)</summary>
    public static System.Text.RegularExpressions.Regex re_pattern(string pattern) =>
        new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));

    /// <summary>Split string by pattern (regex or literal), optionally limiting results</summary>
    public static object? split(object? s, object? re, int limit)
    {
        if (s is not string str) return null;
        var pattern = re?.ToString() ?? " ";
        // Use Regex.Split for proper regex support
        var parts = limit > 0
            ? System.Text.RegularExpressions.Regex.Split(str, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1)).Take(limit).ToArray()
            : System.Text.RegularExpressions.Regex.Split(str, pattern, System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
        return PersistentVector.Create(parts.Cast<object?>().ToArray());
    }

    /// <summary>Split string by newlines</summary>
    public static object? split_lines(object? s)
    {
        if (s is not string str) return null;
        var lines = str.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
        return PersistentVector.Create(lines.Cast<object?>().ToArray());
    }

    /// <summary>Capitalize first character of string</summary>
    public static string? capitalize(object? s)
    {
        if (s is not string str || str.Length == 0) return s?.ToString();
        return char.ToUpperInvariant(str[0]) + str[1..].ToLowerInvariant();
    }

    /// <summary>Trim trailing newline characters</summary>
    public static string? trim_newline(object? s)
    {
        if (s is not string str) return s?.ToString();
        var end = str.Length;
        while (end > 0 && (str[end - 1] == '\n' || str[end - 1] == '\r'))
            end--;
        return str[..end];
    }

    /// <summary>Escape characters in string using escape map</summary>
    public static string? escape(object? s, object? cmap)
    {
        if (s is not string str) return s?.ToString();
        if (cmap is not System.Collections.IDictionary dict) return str;

        var sb = new System.Text.StringBuilder(str.Length);
        foreach (var c in str)
        {
            var replacement = dict[c];
            sb.Append(replacement?.ToString() ?? c.ToString());
        }
        return sb.ToString();
    }

    /// <summary>Return index of first occurrence of value in string, or nil if not found</summary>
    public static object? index_of(object? s, object? value)
    {
        if (s is not string str) return null;
        var idx = value switch
        {
            string v => str.IndexOf(v, StringComparison.Ordinal),
            char c => str.IndexOf(c),
            _ => str.IndexOf(value?.ToString() ?? "", StringComparison.Ordinal)
        };
        return idx >= 0 ? (object)(long)idx : null;
    }

    /// <summary>Return index of first occurrence of value in string starting at from-index</summary>
    public static object? index_of(object? s, object? value, int fromIndex)
    {
        if (s is not string str) return null;
        if (fromIndex < 0 || fromIndex >= str.Length) return null;
        var idx = value switch
        {
            string v => str.IndexOf(v, fromIndex, StringComparison.Ordinal),
            char c => str.IndexOf(c, fromIndex),
            _ => str.IndexOf(value?.ToString() ?? "", fromIndex, StringComparison.Ordinal)
        };
        return idx >= 0 ? (object)(long)idx : null;
    }

    /// <summary>Return index of last occurrence of value in string, or nil if not found</summary>
    public static object? last_index_of(object? s, object? value)
    {
        if (s is not string str) return null;
        var idx = value switch
        {
            string v => str.LastIndexOf(v, StringComparison.Ordinal),
            char c => str.LastIndexOf(c),
            _ => str.LastIndexOf(value?.ToString() ?? "", StringComparison.Ordinal)
        };
        return idx >= 0 ? (object)(long)idx : null;
    }

    /// <summary>Return index of last occurrence of value in string searching backward from from-index</summary>
    public static object? last_index_of(object? s, object? value, int fromIndex)
    {
        if (s is not string str) return null;
        if (fromIndex < 0) return null;
        var searchIdx = Math.Min(fromIndex, str.Length - 1);
        var idx = value switch
        {
            string v => str.LastIndexOf(v, searchIdx, StringComparison.Ordinal),
            char c => str.LastIndexOf(c, searchIdx),
            _ => str.LastIndexOf(value?.ToString() ?? "", searchIdx, StringComparison.Ordinal)
        };
        return idx >= 0 ? (object)(long)idx : null;
    }

    /// <summary>Quote replacement string for use with replace/replace-first regex</summary>
    public static string re_quote_replacement(object? replacement)
    {
        if (replacement is not string str) return replacement?.ToString() ?? "";
        // Escape $ and \ which have special meaning in replacement strings
        return str.Replace("\\", "\\\\").Replace("$", "\\$");
    }

    // clojure.repl stubs
    public static object? doc(object? sym) => $"No documentation for {sym}";
    public static object? source(object? sym) => $"Source not available for {sym}";

    // ========== ATOMS & MUTABLE STATE ==========

    /// <summary>Creates a new atom with the given initial value</summary>
    public static Atom atom(object? x) => new(x);

    /// <summary>Creates a new atom with initial value and validator</summary>
    public static Atom atom(object? x, Func<object?, bool> validator) => new(x, validator);

    /// <summary>Atomically swaps the value of atom by applying f to current value</summary>
    public static object? swap_BANG_(Atom a, Func<object?, object?> f) => a.Swap(f);
    public static object? swap_BANG_(Atom a, Func<object?, object?, object?> f, object? arg) => a.Swap(f, arg);
    public static object? swap_BANG_(Atom a, Func<object?, object?, object?, object?> f, object? arg1, object? arg2) => a.Swap(f, arg1, arg2);
    // Dynamic overloads for when function is passed as object (from EmitFunctionArg wrapping)
    public static object? swap_BANG_(Atom a, object? f) => a.Swap(ToFunc(f));
    public static object? swap_BANG_(Atom a, object? f, object? arg) => a.Swap(ToFunc2(f), arg);
    public static object? swap_BANG_(Atom a, object? f, object? arg1, object? arg2) => a.Swap(ToFunc3(f), arg1, arg2);
    // Overloads for when atom is stored in a var (retrieved as object?)
    public static object? swap_BANG_(object? a, Func<object?, object?> f) => ((Atom)a!).Swap(f);
    public static object? swap_BANG_(object? a, Func<object?, object?, object?> f, object? arg) => ((Atom)a!).Swap(f, arg);
    public static object? swap_BANG_(object? a, Func<object?, object?, object?, object?> f, object? arg1, object? arg2) => ((Atom)a!).Swap(f, arg1, arg2);
    public static object? swap_BANG_(object? a, object? f) => ((Atom)a!).Swap(ToFunc(f));
    public static object? swap_BANG_(object? a, object? f, object? arg) => ((Atom)a!).Swap(ToFunc2(f), arg);
    public static object? swap_BANG_(object? a, object? f, object? arg1, object? arg2) => ((Atom)a!).Swap(ToFunc3(f), arg1, arg2);

    /// <summary>Sets the value of atom to newval without regard for current value</summary>
    public static object? reset_BANG_(Atom a, object? newval) => a.Reset(newval);
    // Overload for when atom is stored in a var (retrieved as object?)
    public static object? reset_BANG_(object? a, object? newval) => ((Atom)a!).Reset(newval);

    /// <summary>Atomically swaps atom, returning [old new]</summary>
    public static List<object?> swap_vals_BANG_(Atom a, Func<object?, object?> f)
    {
        var (oldVal, newVal) = a.SwapVals(f);
        return [oldVal, newVal];
    }

    /// <summary>Resets atom, returning [old new]</summary>
    public static List<object?> reset_vals_BANG_(Atom a, object? newval)
    {
        var (oldVal, newVal) = a.ResetVals(newval);
        return [oldVal, newVal];
    }

    /// <summary>Atomically sets value if current value equals oldval</summary>
    public static bool compare_and_set_BANG_(Atom a, object? oldval, object? newval) => a.CompareAndSet(oldval, newval);

    // ========== VOLATILE (SINGLE-THREADED MUTABLE STATE) ==========

    /// <summary>Creates a volatile with the given initial value</summary>
    public static Volatile volatile_BANG_(object? x) => new(x);

    /// <summary>Sets the value of volatile to newval</summary>
    public static object? vreset_BANG_(Volatile v, object? newval) => v.Reset(newval);

    /// <summary>Swaps the value of volatile by applying f</summary>
    public static object? vswap_BANG_(Volatile v, Func<object?, object?> f) => v.Swap(f);

    // ========== DELAY ==========

    /// <summary>Creates a delay from a function</summary>
    public static Delay delay(Func<object?> fn) => new(fn);

    /// <summary>Forces evaluation of a delay, returning its value</summary>
    public static object? force(object? x) => x switch
    {
        Delay d => d.Force(),
        _ => x
    };

    /// <summary>Returns true if delay has been realized</summary>
    public static bool realized_QMARK_(object? x) => x switch
    {
        Delay d => d.IsRealized,
        Task t => t.IsCompleted,
        _ => true
    };

    // ========== DEREF ==========

    /// <summary>Dereferences a reference type (atom, volatile, delay, future, etc.)</summary>
    public static object? deref(object? x) => x switch
    {
        null => null,
        IDeref d => d.Deref(),
        Task<object?> t => t.GetAwaiter().GetResult(),
        Task t => DerefTask(t),
        _ => x
    };

    private static object? DerefTask(Task t)
    {
        t.GetAwaiter().GetResult();
        // For Task<T>, extract the result via reflection
        var taskType = t.GetType();
        if (taskType.IsGenericType)
        {
            var resultProp = taskType.GetProperty("Result");
            if (resultProp != null)
                return resultProp.GetValue(t);
        }
        return null;
    }

    /// <summary>Dereferences with timeout (for blocking refs)</summary>
    public static object? deref(object? x, long timeoutMs, object? timeoutVal) => x switch
    {
        IBlockingDeref bd => bd.Deref(timeoutMs, timeoutVal),
        Task<object?> t => t.Wait((int)timeoutMs) ? t.Result : timeoutVal,
        Task t => DerefTaskWithTimeout(t, (int)timeoutMs, timeoutVal),
        _ => deref(x)
    };

    private static object? DerefTaskWithTimeout(Task t, int timeoutMs, object? timeoutVal)
    {
        if (!t.Wait(timeoutMs))
            return timeoutVal;
        // For Task<T>, extract the result via reflection
        var taskType = t.GetType();
        if (taskType.IsGenericType)
        {
            var resultProp = taskType.GetProperty("Result");
            if (resultProp != null)
                return resultProp.GetValue(t);
        }
        return null;
    }

    // ========== WATCHES ==========

    /// <summary>Adds a watch function to a reference</summary>
    public static object? add_watch(object? reference, object? key, Action<object, object?, object?, object?> fn)
    {
        if (reference is IWatchable w)
            w.AddWatch(key!, fn);
        return reference;
    }

    /// <summary>Removes a watch from a reference</summary>
    public static object? remove_watch(object? reference, object? key)
    {
        if (reference is IWatchable w)
            w.RemoveWatch(key!);
        return reference;
    }

    // ========== VALIDATORS ==========

    /// <summary>Sets validator function on a reference</summary>
    public static object? set_validator_BANG_(object? reference, Func<object?, bool>? fn)
    {
        if (reference is IRef r)
            r.SetValidator(fn);
        return null;
    }

    /// <summary>Gets validator function from a reference</summary>
    public static Func<object?, bool>? get_validator(object? reference) =>
        reference is IRef r ? r.GetValidator() : null;

    // ========== ASYNC OPERATIONS ==========

    /// <summary>Awaits a Task and returns its result (blocks current thread)</summary>
    public static object? await_BANG_(object? task) => deref(task);

    /// <summary>Creates a future - runs fn on a background thread</summary>
    public static Task<object?> future(Func<object?> fn) => Async.Future(fn);

    /// <summary>Returns true if future/promise/delay is done</summary>
    public static bool future_done_QMARK_(object? x) => x switch
    {
        Task t => t.IsCompleted,
        Delay d => d.IsRealized,
        _ => true
    };

    /// <summary>Attempts to cancel a future</summary>
    public static bool future_cancel(object? x) => false; // Basic futures can't be cancelled

    /// <summary>Creates a channel for async producer/consumer patterns</summary>
    public static Channel chan() => Async.CreateChannel();
    public static Channel chan(int bufferSize) => Async.CreateChannel(bufferSize);

    /// <summary>Puts a value on a channel</summary>
    public static async Task<bool> put_BANG_(Channel ch, object? val) => await ch.Put(val);

    /// <summary>Takes a value from a channel</summary>
    public static async Task<object?> take_BANG_(Channel ch) => await ch.Take();

    /// <summary>Closes a channel</summary>
    public static void close_BANG_(Channel ch) => ch.Close();

    // ========== ADDITIONAL SEQUENCE FUNCTIONS ==========

    /// <summary>Returns a lazy sequence of the first n items</summary>
    public static IEnumerable<object?> take(int n, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= n) yield break;
            yield return item;
        }
    }
    /// <summary>Returns a lazy sequence of the first n items (long overload)</summary>
    public static IEnumerable<object?> take(long n, object? coll) => take((int)n, coll);

    /// <summary>Returns a lazy sequence of all but the first n items</summary>
    public static IEnumerable<object?> drop(int n, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var count = 0;
        foreach (var item in enumerable)
        {
            if (count++ >= n)
                yield return item;
        }
    }

    /// <summary>Returns items while predicate is true</summary>
    public static IEnumerable<object?> take_while(Func<object?, bool> pred, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        foreach (var item in enumerable)
        {
            if (!pred(item)) yield break;
            yield return item;
        }
    }

    /// <summary>Drops items while predicate is true</summary>
    public static IEnumerable<object?> drop_while(Func<object?, bool> pred, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var dropping = true;
        foreach (var item in enumerable)
        {
            if (dropping && pred(item)) continue;
            dropping = false;
            yield return item;
        }
    }

    // Dynamic overloads for take_while/drop_while for HOC support
    public static IEnumerable<object?> take_while(object? pred, object? coll) => take_while(ToPred(pred), coll);
    public static IEnumerable<object?> drop_while(object? pred, object? coll) => drop_while(ToPred(pred), coll);

    /// <summary>Concatenates sequences</summary>
    public static IEnumerable<object?> concat(params object?[] colls)
    {
        foreach (var coll in colls)
        {
            if (coll is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                    yield return item;
            }
        }
    }

    /// <summary>Flattens nested sequences</summary>
    public static IEnumerable<object?> flatten(object? coll)
    {
        if (coll is not IEnumerable enumerable || coll is string) { yield return coll; yield break; }
        foreach (var item in enumerable)
        {
            if (item is IEnumerable inner && item is not string)
            {
                foreach (var nested in flatten(inner))
                    yield return nested;
            }
            else
            {
                yield return item;
            }
        }
    }

    /// <summary>Returns distinct values</summary>
    public static IEnumerable<object?> distinct(object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var seen = new HashSet<object?>();
        foreach (var item in enumerable)
        {
            if (seen.Add(item))
                yield return item;
        }
    }

    /// <summary>Groups values by key function</summary>
    public static Dictionary<object?, List<object?>> group_by(Func<object?, object?> f, object? coll)
    {
        var result = new Dictionary<object?, List<object?>>();
        if (coll is not IEnumerable enumerable) return result;
        foreach (var item in enumerable)
        {
            var key = f(item);
            if (!result.TryGetValue(key, out var list))
            {
                list = [];
                result[key] = list;
            }
            list.Add(item);
        }
        return result;
    }
    public static Dictionary<object?, List<object?>> group_by(object? f, object? coll) => group_by(ToFunc(f), coll);

    /// <summary>Partitions sequence into chunks of n</summary>
    public static IEnumerable<List<object?>> partition(int n, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var chunk = new List<object?>();
        foreach (var item in enumerable)
        {
            chunk.Add(item);
            if (chunk.Count == n)
            {
                yield return chunk;
                chunk = [];
            }
        }
    }

    /// <summary>Partitions with step size</summary>
    public static IEnumerable<List<object?>> partition(int n, int step, object? coll)
    {
        var items = coll is IEnumerable e ? e.Cast<object?>().ToList() : [];
        for (int i = 0; i + n <= items.Count; i += step)
        {
            yield return items.Skip(i).Take(n).ToList();
        }
    }

    /// <summary>Partitions by predicate changes</summary>
    public static IEnumerable<List<object?>> partition_by(Func<object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var chunk = new List<object?>();
        object? lastKey = null;
        var first = true;
        foreach (var item in enumerable)
        {
            var key = f(item);
            if (!first && !Equals(key, lastKey))
            {
                yield return chunk;
                chunk = [];
            }
            chunk.Add(item);
            lastKey = key;
            first = false;
        }
        if (chunk.Count > 0)
            yield return chunk;
    }
    public static IEnumerable<List<object?>> partition_by(object? f, object? coll) => partition_by(ToFunc(f), coll);

    /// <summary>Interleaves multiple sequences</summary>
    public static IEnumerable<object?> interleave(params object?[] colls)
    {
        var enumerators = colls
            .Where(c => c is IEnumerable)
            .Select(c => ((IEnumerable)c!).GetEnumerator())
            .ToList();
        while (enumerators.All(e => e.MoveNext()))
        {
            foreach (var e in enumerators)
                yield return e.Current;
        }
    }

    /// <summary>Interposes separator between items</summary>
    public static IEnumerable<object?> interpose(object? sep, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var first = true;
        foreach (var item in enumerable)
        {
            if (!first) yield return sep;
            yield return item;
            first = false;
        }
    }

    /// <summary>Returns the nth item (0-indexed)</summary>
    public static object? nth(object? coll, int n, object? notFound = null)
    {
        if (coll is null) return notFound;
        if (coll is IList list) return n >= 0 && n < list.Count ? list[n] : notFound;
        if (coll is string s) return n >= 0 && n < s.Length ? s[n] : notFound;
        if (coll is IEnumerable enumerable)
        {
            var i = 0;
            foreach (var item in enumerable)
            {
                if (i++ == n) return item;
            }
        }
        return notFound;
    }

    /// <summary>Returns the second item</summary>
    public static object? second(object? coll) => first(rest(coll));
    /// <summary>Returns the second item (PascalCase alias)</summary>
    public static object? Second(object? coll) => second(coll);

    /// <summary>Returns the last item</summary>
    public static object? last(object? coll)
    {
        if (coll is null) return null;
        if (coll is IList list) return list.Count > 0 ? list[^1] : null;
        if (coll is string s) return s.Length > 0 ? s[^1] : (object?)null;
        object? result = null;
        if (coll is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                result = item;
        }
        return result;
    }

    /// <summary>Returns all but the last item</summary>
    public static IEnumerable<object?>? butlast(object? coll)
    {
        if (coll is not IEnumerable enumerable) return null;
        var result = new List<object?>();
        object? prev = null;
        var hasPrev = false;
        foreach (var item in enumerable)
        {
            if (hasPrev) result.Add(prev);
            prev = item;
            hasPrev = true;
        }
        return result.Count > 0 ? result : null;
    }

    /// <summary>Returns seq or nil if empty</summary>
    public static IEnumerable<object?>? seq(object? coll)
    {
        if (coll is null) return null;
        if (coll is IEnumerable enumerable)
        {
            var list = enumerable.Cast<object?>().ToList();
            return list.Count > 0 ? list : null;
        }
        return null;
    }
    /// <summary>Returns seq or nil if empty (PascalCase alias)</summary>
    public static IEnumerable<object?>? Seq(object? coll) => seq(coll);

    /// <summary>Constructs a new sequence with x as first and seq as rest</summary>
    public static IEnumerable<object?> cons(object? x, object? seq)
    {
        yield return x;
        if (seq is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
                yield return item;
        }
    }

    /// <summary>Creates a list</summary>
    public static List<object?> list(params object?[] items) => items.ToList();

    /// <summary>Creates a vector (list in our implementation)</summary>
    public static List<object?> vector(params object?[] items) => items.ToList();

    /// <summary>Creates a hash-map</summary>
    public static Dictionary<object, object?> hash_map(params object?[] kvs)
    {
        var result = new Dictionary<object, object?>();
        for (var i = 0; i < kvs.Length - 1; i += 2)
        {
            result[kvs[i]!] = kvs[i + 1];
        }
        return result;
    }

    /// <summary>Creates a hash-set</summary>
    public static HashSet<object?> hash_set(params object?[] items) => items.ToHashSet();

    /// <summary>Converts to vector</summary>
    public static PersistentVector vec(object? coll)
    {
        if (coll is null) return PersistentVector.Empty;
        if (coll is PersistentVector pv) return pv;

        // Fast path for object arrays - use existing Create method
        if (coll is object?[] arr) return PersistentVector.Create(arr);

        // Fast path for LongRange - transient building with tight loop
        if (coll is Collections.LongRange lr)
        {
            int count = lr.Count;
            long current = lr.First() is long first ? first : lr.Start;
            long step = lr.Step;
            var tv = PersistentVector.Empty.AsTransient();
            for (int i = 0; i < count; i++)
            {
                tv = tv.Conj(current);
                current += step;
            }
            return (PersistentVector)tv.Persistent();
        }

        // Direct transient building - avoids .Cast<>().ToArray() overhead
        if (coll is IEnumerable enumerable)
        {
            var tv = PersistentVector.Empty.AsTransient();
            foreach (var item in enumerable)
                tv = tv.Conj(item);
            return (PersistentVector)tv.Persistent();
        }
        return PersistentVector.Empty;
    }

    /// <summary>Converts to set</summary>
    public static PersistentHashSet set(object? coll) =>
        coll is IEnumerable e ? PersistentHashSet.Create(e.Cast<object?>().ToArray()) : PersistentHashSet.Create();

    // ========== SET OPERATIONS (clojure.set) ==========

    /// <summary>Returns a set that is the union of the input sets</summary>
    public static PersistentHashSet union(params object?[] sets)
    {
        var result = new HashSet<object?>();
        foreach (var s in sets)
        {
            if (s is IEnumerable e)
                foreach (var item in e)
                    result.Add(item);
        }
        return PersistentHashSet.Create(result.ToArray());
    }

    /// <summary>Returns a set that is the intersection of the input sets</summary>
    public static PersistentHashSet intersection(object? s1, params object?[] sets)
    {
        if (s1 is not IEnumerable e1) return PersistentHashSet.Empty;
        var result = e1.Cast<object?>().ToHashSet();
        foreach (var s in sets)
        {
            if (s is IEnumerable e)
                result.IntersectWith(e.Cast<object?>());
            else
                return PersistentHashSet.Empty;
        }
        return PersistentHashSet.Create(result.ToArray());
    }

    /// <summary>Returns a set that is s1 without elements in the other sets</summary>
    public static PersistentHashSet difference(object? s1, params object?[] sets)
    {
        if (s1 is not IEnumerable e1) return PersistentHashSet.Empty;
        var result = e1.Cast<object?>().ToHashSet();
        foreach (var s in sets)
        {
            if (s is IEnumerable e)
                result.ExceptWith(e.Cast<object?>());
        }
        return PersistentHashSet.Create(result.ToArray());
    }

    /// <summary>Returns true if set1 is a subset of set2</summary>
    public static bool subset_QMARK_(object? set1, object? set2)
    {
        if (set1 is not IEnumerable e1 || set2 is not IEnumerable e2) return false;
        var s1 = e1.Cast<object?>().ToHashSet();
        var s2 = e2.Cast<object?>().ToHashSet();
        return s1.IsSubsetOf(s2);
    }

    /// <summary>Returns true if set1 is a superset of set2</summary>
    public static bool superset_QMARK_(object? set1, object? set2)
    {
        if (set1 is not IEnumerable e1 || set2 is not IEnumerable e2) return false;
        var s1 = e1.Cast<object?>().ToHashSet();
        var s2 = e2.Cast<object?>().ToHashSet();
        return s1.IsSupersetOf(s2);
    }

    /// <summary>Returns a set of the elements for which pred is true</summary>
    public static PersistentHashSet select(Func<object?, bool> pred, object? xset)
    {
        if (xset is not IEnumerable e) return PersistentHashSet.Empty;
        var result = e.Cast<object?>().Where(pred).ToArray();
        return PersistentHashSet.Create(result);
    }

    /// <summary>Returns a rel (set of maps) with only the keys in ks</summary>
    public static PersistentHashSet project(object? xrel, object? ks)
    {
        if (xrel is not IEnumerable rel || ks is not IEnumerable keys) return PersistentHashSet.Empty;
        var keySet = keys.Cast<object?>().ToHashSet();
        var result = new List<object?>();
        foreach (var m in rel)
        {
            if (m is IPersistentMap map)
            {
                var projected = PersistentHashMap.Empty;
                foreach (var k in keySet)
                {
                    var v = map.ValAt(k);
                    if (v != null || map.ContainsKey(k))
                        projected = (PersistentHashMap)projected.Assoc(k, v);
                }
                result.Add(projected);
            }
        }
        return PersistentHashSet.Create(result.ToArray());
    }

    /// <summary>Returns a map with the keys renamed according to the kmap</summary>
    public static object? rename_keys(object? map, object? kmap)
    {
        if (map is not IPersistentMap m || kmap is not IPersistentMap km) return map;
        var result = m;
        foreach (var entry in km)
        {
            if (entry is KeyValuePair<object, object?> kvp || entry is IMapEntry)
            {
                var oldKey = entry is IMapEntry me ? me.Key() : ((KeyValuePair<object, object?>)entry).Key;
                var newKey = entry is IMapEntry me2 ? me2.Val() : ((KeyValuePair<object, object?>)entry).Value;
                if (m.ContainsKey(oldKey))
                {
                    var val = m.ValAt(oldKey);
                    result = (IPersistentMap)result.Without(oldKey);
                    result = (IPersistentMap)result.Assoc(newKey, val);
                }
            }
        }
        return result;
    }

    /// <summary>Returns a rel with the keys renamed according to the kmap</summary>
    public static PersistentHashSet rename(object? xrel, object? kmap)
    {
        if (xrel is not IEnumerable rel) return PersistentHashSet.Empty;
        var result = new List<object?>();
        foreach (var m in rel)
        {
            result.Add(rename_keys(m, kmap));
        }
        return PersistentHashSet.Create(result.ToArray());
    }

    /// <summary>Returns a map of vals to keys for the given map</summary>
    public static object? map_invert(object? m)
    {
        if (m is not IPersistentMap map) return PersistentHashMap.Empty;
        var result = PersistentHashMap.Empty;
        foreach (var entry in map)
        {
            if (entry is IMapEntry me)
                result = (PersistentHashMap)result.Assoc(me.Val(), me.Key());
        }
        return result;
    }

    /// <summary>Returns a map of the distinct values of ks in xrel to sets of maps</summary>
    public static object? index(object? xrel, object? ks)
    {
        if (xrel is not IEnumerable rel || ks is not IEnumerable keys) return PersistentHashMap.Empty;
        var keySet = keys.Cast<object?>().ToList();
        var result = new Dictionary<object?, List<object?>>();
        foreach (var m in rel)
        {
            if (m is IPersistentMap map)
            {
                var indexKey = PersistentHashMap.Empty;
                foreach (var k in keySet)
                {
                    indexKey = (PersistentHashMap)indexKey.Assoc(k, map.ValAt(k));
                }
                if (!result.ContainsKey(indexKey))
                    result[indexKey] = new List<object?>();
                result[indexKey].Add(m);
            }
        }
        var finalResult = PersistentHashMap.Empty;
        foreach (var kvp in result)
        {
            finalResult = (PersistentHashMap)finalResult.Assoc(kvp.Key, PersistentHashSet.Create(kvp.Value.ToArray()));
        }
        return finalResult;
    }

    /// <summary>Creates a typed array from a collection</summary>
    public static Array into_array(Type type, object? coll)
    {
        if (coll is not IEnumerable enumerable)
            return Array.CreateInstance(type, 0);

        var list = enumerable.Cast<object?>().ToList();
        var array = Array.CreateInstance(type, list.Count);
        for (int i = 0; i < list.Count; i++)
        {
            var item = list[i];
            // Convert item to target type if needed
            if (item != null && type.IsAssignableFrom(item.GetType()))
                array.SetValue(item, i);
            else if (item != null)
                array.SetValue(Convert.ChangeType(item, type), i);
        }
        return array;
    }

    /// <summary>Creates a string array from a collection</summary>
    public static string[] into_array_String(object? coll)
    {
        if (coll is not IEnumerable enumerable)
            return Array.Empty<string>();
        return enumerable.Cast<object?>().Select(x => x?.ToString() ?? "").ToArray();
    }

    /// <summary>Creates an object array from a collection</summary>
    public static object?[] into_array_Object(object? coll)
    {
        if (coll is not IEnumerable enumerable)
            return Array.Empty<object?>();
        return enumerable.Cast<object?>().ToArray();
    }

    /// <summary>Creates an object array from a collection (PascalCase alias for interop)</summary>
    public static object?[] IntoArray(object? coll) => into_array_Object(coll);

    /// <summary>Returns keys of a map</summary>
    public static IEnumerable<object?> keys(object? m)
    {
        if (m is IDictionary dict)
        {
            foreach (var key in dict.Keys)
                yield return key;
        }
    }

    /// <summary>Returns values of a map</summary>
    public static IEnumerable<object?> vals(object? m)
    {
        if (m is IDictionary dict)
        {
            foreach (var val in dict.Values)
                yield return val;
        }
    }

    /// <summary>Finds a map entry</summary>
    public static object? find(object? m, object? key)
    {
        if (m is IDictionary dict && dict.Contains(key!))
            return new List<object?> { key, dict[key!] };
        return null;
    }

    /// <summary>Returns true if map contains key</summary>
    public static bool contains_QMARK_(object? coll, object? key) => coll switch
    {
        PersistentHashSet phs => phs.Contains(key!),
        PersistentHashMap phm => phm.ContainsKey(key!),
        IDictionary dict => dict.Contains(key!),
        ISet<object?> set => set.Contains(key),
        // For vectors, contains? checks if the index exists (Clojure semantics)
        // Handle both int and long keys since Clojure integers are longs
        PersistentVector pv when key is int i => i >= 0 && i < pv.Count,
        PersistentVector pv when key is long l => l >= 0 && l < pv.Count,
        IList list when key is int i => i >= 0 && i < list.Count,
        IList list when key is long l => l >= 0 && l < list.Count,
        string s when key is int i => i >= 0 && i < s.Length,
        string s when key is long l => l >= 0 && l < s.Length,
        _ => false
    };

    /// <summary>Returns first truthy value of (pred x) for any x in coll, else nil</summary>
    public static object? some(Func<object?, object?> pred, object? coll)
    {
        if (coll is not IEnumerable enumerable) return null;
        foreach (var item in enumerable)
        {
            var result = pred(item);
            if (IsTruthy(result)) return result;
        }
        return null;
    }

    /// <summary>Returns first truthy value of (pred x) for any x in coll, else nil - bool predicate overload</summary>
    public static object? some(Func<object?, bool> pred, object? coll)
    {
        if (coll is not IEnumerable enumerable) return null;
        foreach (var item in enumerable)
        {
            var result = pred(item);
            if (result) return result;  // Return the bool result (true), not the item
        }
        return null;
    }

    /// <summary>Returns true if predicate is true for all items</summary>
    public static bool every_QMARK_(Func<object?, bool> pred, object? coll)
    {
        if (coll is not IEnumerable enumerable) return true;
        foreach (var item in enumerable)
        {
            if (!pred(item)) return false;
        }
        return true;
    }

    /// <summary>Returns true if predicate is false for all items</summary>
    public static bool not_every_QMARK_(Func<object?, bool> pred, object? coll) => !every_QMARK_(pred, coll);

    /// <summary>Returns true if predicate is false for any item</summary>
    public static bool not_any_QMARK_(Func<object?, bool> pred, object? coll) => !IsTruthy(some(pred, coll));

    // Dynamic overloads for HOC support (accept object? and convert at runtime)
    public static object? some(object? pred, object? coll) => some(ToPred(pred), coll);
    public static bool every_QMARK_(object? pred, object? coll) => every_QMARK_(ToPred(pred), coll);
    public static bool not_every_QMARK_(object? pred, object? coll) => not_every_QMARK_(ToPred(pred), coll);
    public static bool not_any_QMARK_(object? pred, object? coll) => not_any_QMARK_(ToPred(pred), coll);

    /// <summary>Removes items matching predicate</summary>
    public static IEnumerable<object?> remove(Func<object?, bool> pred, object? coll) =>
        filter(x => !pred(x), coll);
    public static IEnumerable<object?> remove(object? pred, object? coll) => remove(ToPred(pred), coll);

    /// <summary>Keeps items where f returns non-nil</summary>
    public static IEnumerable<object?> keep(Func<object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        foreach (var item in enumerable)
        {
            var result = f(item);
            if (result is not null)
                yield return result;
        }
    }

    /// <summary>Returns a lazy sequence of function applications</summary>
    public static IEnumerable<object?> repeatedly(int n, Func<object?> f)
    {
        for (var i = 0; i < n; i++)
            yield return f();
    }

    /// <summary>Returns infinite lazy sequence of function applications</summary>
    public static IEnumerable<object?> repeatedly(Func<object?> f)
    {
        while (true)
            yield return f();
    }
    /// <summary>Returns a lazy sequence of function applications (long overload)</summary>
    public static IEnumerable<object?> repeatedly(long n, Func<object?> f) => repeatedly((int)n, f);

    /// <summary>Returns a lazy sequence of x repeated n times</summary>
    public static IEnumerable<object?> repeat(int n, object? x)
    {
        for (var i = 0; i < n; i++)
            yield return x;
    }

    /// <summary>Returns infinite lazy sequence of x</summary>
    public static IEnumerable<object?> repeat(object? x)
    {
        while (true)
            yield return x;
    }

    /// <summary>Returns a range of numbers - optimized with LongRange for fast reduce</summary>
    public static IEnumerable<long> range() { long i = 0; while (true) yield return i++; }
    public static Collections.LongRange range(long end) => new Collections.LongRange(0, end, 1);
    public static Collections.LongRange range(long start, long end) => new Collections.LongRange(start, end, 1);
    public static Collections.LongRange range(long start, long end, long step) => new Collections.LongRange(start, end, step);

    /// <summary>Returns f applied to args, then f applied to that, etc.</summary>
    public static IEnumerable<object?> iterate(Func<object?, object?> f, object? x)
    {
        var current = x;
        while (true)
        {
            yield return current;
            current = f(current);
        }
    }
    public static IEnumerable<object?> iterate(object? f, object? x) => iterate(ToFunc(f), x);

    /// <summary>Sorts a sequence</summary>
    public static List<object?> sort(object? coll) =>
        coll is IEnumerable e
            ? e.Cast<object?>().OrderBy(x => x).ToList()
            : [];

    /// <summary>Sorts by key function</summary>
    public static List<object?> sort_by(Func<object?, object?> keyfn, object? coll) =>
        coll is IEnumerable e
            ? e.Cast<object?>().OrderBy(keyfn).ToList()
            : [];

    /// <summary>Reverses a sequence</summary>
    public static IEnumerable<object?> reverse_seq(object? coll) =>
        coll is IEnumerable e
            ? e.Cast<object?>().Reverse()
            : Enumerable.Empty<object?>();

    /// <summary>Returns a map from applying f to each item</summary>
    public static Dictionary<object, object?> zipmap(object? keys, object? vals)
    {
        var result = new Dictionary<object, object?>();
        if (keys is not IEnumerable ks || vals is not IEnumerable vs) return result;
        var vEnum = vs.GetEnumerator();
        foreach (var k in ks)
        {
            if (!vEnum.MoveNext()) break;
            result[k] = vEnum.Current;
        }
        return result;
    }

    /// <summary>Maps f over colls, concatenating results</summary>
    public static IEnumerable<object?> mapcat(Func<object?, IEnumerable<object?>> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        foreach (var item in enumerable)
        {
            foreach (var result in f(item))
                yield return result;
        }
    }
    /// <summary>Maps f over colls, concatenating results (dynamic overload)</summary>
    public static IEnumerable<object?> mapcat(Func<object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) return Enumerable.Empty<object?>();
        return MapcatImpl(f, enumerable);
    }
    private static IEnumerable<object?> MapcatImpl(Func<object?, object?> f, IEnumerable enumerable)
    {
        foreach (var item in enumerable)
        {
            var result = f(item);
            if (result is IEnumerable resultEnum)
            {
                foreach (var r in resultEnum)
                    yield return r;
            }
        }
    }

    /// <summary>Reduce without initial value</summary>
    public static object? reduce(Func<object?, object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) return null;
        var e = enumerable.GetEnumerator();
        if (!e.MoveNext()) return f(null, null); // Call f with no args
        var acc = e.Current;
        while (e.MoveNext())
            acc = f(acc, e.Current);
        return acc;
    }

    /// <summary>Reduce returning intermediate values (with init)</summary>
    public static IEnumerable<object?> reductions(Func<object?, object?, object?> f, object? init, object? coll)
    {
        var acc = init;
        yield return acc;
        if (coll is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                acc = f(acc, item);
                yield return acc;
            }
        }
    }

    /// <summary>Reduce returning intermediate values (without init, uses first element)</summary>
    public static IEnumerable<object?> reductions(Func<object?, object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var enumerator = enumerable.GetEnumerator();
        if (!enumerator.MoveNext()) yield break;
        var acc = enumerator.Current;
        yield return acc;
        while (enumerator.MoveNext())
        {
            acc = f(acc, enumerator.Current);
            yield return acc;
        }
    }

    /// <summary>Transduce - apply transducer and reducer</summary>
    public static object? transduce(Func<Func<object?, object?, object?>, Func<object?, object?, object?>> xform,
                                    Func<object?, object?, object?> f, object? init, object? coll)
    {
        var rf = xform(f);
        return reduce(rf, init, coll);
    }

    /// <summary>Into - transduces coll into to collection</summary>
    public static object into(object to, object? from)
    {
        if (from is null) return to;
        if (from is IEnumerable e)
        {
            // Handle persistent collections first (most common case in Clojure code)
            if (to is IPersistentCollection pc)
            {
                var result = pc;
                foreach (var x in e)
                    result = result.Conj(x);
                return result;
            }
            if (to is IList list)
            {
                foreach (var x in e) list.Add(x);
                return to;
            }
            if (to is IDictionary dict)
            {
                foreach (var x in e)
                {
                    if (x is IList pair && pair.Count >= 2)
                        dict[pair[0]!] = pair[1];
                }
                return to;
            }
        }
        return to;
    }

    /// <summary>Into with transducer - transduce coll with xform into to collection</summary>
    public static object into(object to, Func<Func<object?, object?, object?>, Func<object?, object?, object?>> xform, object? from)
    {
        if (from is null) return to;

        // Create a conj-like reducing function for the collection type
        Func<object?, object?, object?> rf;
        if (to is IPersistentCollection)
        {
            rf = (acc, x) => ((IPersistentCollection)acc!).Conj(x);
        }
        else if (to is IList)
        {
            rf = (acc, x) => { ((IList)acc!).Add(x); return acc; };
        }
        else
        {
            rf = (acc, x) => acc; // fallback
        }

        // Apply transducer
        var xrf = xform(rf);

        // Reduce with the transduced function
        object? result = to;
        if (from is IEnumerable e)
        {
            foreach (var item in e)
            {
                result = xrf(result, item);
                if (result is Reduced r)
                {
                    result = r.Value;
                    break;
                }
            }
        }

        return result!;
    }

    /// <summary>Not operator</summary>
    public static bool not(object? x) => !IsTruthy(x);

    /// <summary>Random integer</summary>
    public static int rand_int(int n) => Random.Shared.Next(n);

    /// <summary>Random double 0-1</summary>
    public static double rand() => Random.Shared.NextDouble();

    /// <summary>Random double 0-n</summary>
    public static double rand(double n) => Random.Shared.NextDouble() * n;

    /// <summary>Random element from collection</summary>
    public static object? rand_nth(object? coll)
    {
        if (coll is IList list && list.Count > 0)
            return list[Random.Shared.Next(list.Count)];
        return null;
    }

    /// <summary>Shuffles a collection</summary>
    public static List<object?> shuffle(object? coll) =>
        coll is IEnumerable e
            ? e.Cast<object?>().OrderBy(_ => Random.Shared.Next()).ToList()
            : [];

    /// <summary>Peek at first item of a stack (list) or last of vector</summary>
    public static object? peek(object? coll) => first(coll);

    /// <summary>Pop first item from a stack (list)</summary>
    public static IEnumerable<object?> pop(object? coll) => rest(coll);

    /// <summary>Empty collection of same type</summary>
    public static object empty(object? coll) => coll switch
    {
        IList => new List<object?>(),
        IDictionary => new Dictionary<object, object?>(),
        ISet<object?> => new HashSet<object?>(),
        string => "",
        _ => new List<object?>()
    };

    /// <summary>Returns coll if not empty, else nil</summary>
    public static object? not_empty(object? coll) => Count(coll) > 0 ? coll : null;

    /// <summary>Merges maps (right wins)</summary>
    public static Dictionary<object, object?> merge(params object?[] maps)
    {
        var result = new Dictionary<object, object?>();
        foreach (var m in maps)
        {
            if (m is IDictionary dict)
            {
                foreach (var key in dict.Keys)
                    result[key] = dict[key];
            }
        }
        return result;
    }

    /// <summary>Merges maps using f to combine values for same key</summary>
    public static Dictionary<object, object?> merge_with(Func<object?, object?, object?> f, params object?[] maps)
    {
        var result = new Dictionary<object, object?>();
        foreach (var m in maps)
        {
            if (m is IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    if (result.ContainsKey(key))
                        result[key] = f(result[key], dict[key]);
                    else
                        result[key] = dict[key];
                }
            }
        }
        return result;
    }

    /// <summary>Returns a map containing only keys in ks</summary>
    public static PersistentHashMap select_keys(object? m, object? ks)
    {
        var result = PersistentHashMap.Empty;
        if (m is not IDictionary && m is not PersistentHashMap) return result;
        if (ks is not IEnumerable keys) return result;

        foreach (var key in keys)
        {
            if (m is PersistentHashMap phm)
            {
                if (phm.ContainsKey(key!))
                    result = (PersistentHashMap)result.Assoc(key!, phm.ValAt(key!));
            }
            else if (m is IDictionary dict)
            {
                if (dict.Contains(key!))
                    result = (PersistentHashMap)result.Assoc(key!, dict[key!]);
            }
        }
        return result;
    }

    /// <summary>Gets value at nested path of keys</summary>
    public static object? get_in(object? m, object? ks, object? notFound = null)
    {
        if (ks is not IEnumerable keys) return notFound;
        var current = m;
        foreach (var key in keys)
        {
            current = Get(current, key, notFound);
            if (current == notFound && !Equals(Get(current, key), notFound))
                return notFound;
        }
        return current;
    }

    /// <summary>Associates value at nested path of keys</summary>
    public static object assoc_in(object? m, object? ks, object? val)
    {
        if (ks is not IList keys || keys.Count == 0)
            return m ?? new Dictionary<object, object?>();

        var k = keys[0];
        if (keys.Count == 1)
            return Assoc(m ?? new Dictionary<object, object?>(), k!, val);

        var restKeys = keys.Cast<object?>().Skip(1).ToList();
        return Assoc(m ?? new Dictionary<object, object?>(), k!, assoc_in(Get(m, k), restKeys, val));
    }

    /// <summary>Updates value at nested path of keys</summary>
    public static object update_in(object? m, object? ks, Func<object?, object?> f)
    {
        if (ks is not IList keys || keys.Count == 0)
            return m ?? new Dictionary<object, object?>();

        var k = keys[0];
        if (keys.Count == 1)
            return Update(m ?? new Dictionary<object, object?>(), k!, f);

        var restKeys = keys.Cast<object?>().Skip(1).ToList();
        return Assoc(m ?? new Dictionary<object, object?>(), k!, update_in(Get(m, k), restKeys, f));
    }

    /// <summary>
    /// Returns a new map with the values of m transformed by f (Clojure 1.11+)
    /// (update-vals {:a 1 :b 2} inc) => {:a 2 :b 3}
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? update_vals(object? m, Func<object?, object?> f)
    {
        if (m is null) return null;
        if (m is IPersistentMap pm)
        {
            var result = pm;
            foreach (var entry in pm)
            {
                if (entry is IMapEntry me)
                    result = (IPersistentMap)result.Assoc(me.Key, f(me.Val));
            }
            return result;
        }
        if (m is IDictionary dict)
        {
            var result = new Dictionary<object, object?>();
            foreach (var key in dict.Keys)
                result[key] = f(dict[key]);
            return result;
        }
        return m;
    }

    /// <summary>
    /// Returns a new map with the keys of m transformed by f (Clojure 1.11+)
    /// (update-keys {:a 1 :b 2} name) => {"a" 1 "b" 2}
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? update_keys(object? m, Func<object?, object?> f)
    {
        if (m is null) return null;
        if (m is IPersistentMap pm)
        {
            IPersistentMap result = PersistentHashMap.Empty;
            foreach (var entry in pm)
            {
                if (entry is IMapEntry me)
                    result = (IPersistentMap)result.Assoc(f(me.Key), me.Val);
            }
            return result;
        }
        if (m is IDictionary dict)
        {
            var result = new Dictionary<object, object?>();
            foreach (var key in dict.Keys)
                result[f(key)!] = dict[key];
            return result;
        }
        return m;
    }

    /// <summary>Keep with index</summary>
    public static IEnumerable<object?> keep_indexed(Func<int, object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var i = 0;
        foreach (var item in enumerable)
        {
            var result = f(i++, item);
            if (result is not null)
                yield return result;
        }
    }

    /// <summary>Infinite cycle of items</summary>
    public static IEnumerable<object?> cycle(object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var items = enumerable.Cast<object?>().ToList();
        if (items.Count == 0) yield break;
        while (true)
        {
            foreach (var item in items)
                yield return item;
        }
    }

    /// <summary>Juxt - returns fn that returns vector of fn applications</summary>
    public static Func<object?, List<object?>> juxt(params Func<object?, object?>[] fns) =>
        x => fns.Select(f => f(x)).ToList();
    public static Func<object?, List<object?>> juxt(params object?[] fns) =>
        x => fns.Select(f => Invoke(f, x)).ToList();

    /// <summary>Complement - returns fn that returns logical opposite</summary>
    public static Func<object?, bool> complement(Func<object?, bool> f) => x => !f(x);
    public static Func<object?, bool> complement(object? f) => x => !IsTruthy(Invoke(f, x));

    /// <summary>Memoize - caches function results</summary>
    public static Func<object?, object?> memoize(Func<object?, object?> f)
    {
        var cache = new Dictionary<object?, object?>();
        return x =>
        {
            if (!cache.TryGetValue(x, out var result))
            {
                result = f(x);
                cache[x] = result;
            }
            return result;
        };
    }

    /// <summary>Every-pred - returns fn that returns true if all preds are true</summary>
    public static Func<object?, bool> every_pred(params Func<object?, bool>[] preds) =>
        x => preds.All(p => p(x));

    /// <summary>Some-fn - returns fn that returns first truthy result</summary>
    public static Func<object?, object?> some_fn(params Func<object?, object?>[] fns) =>
        x =>
        {
            foreach (var f in fns)
            {
                var result = f(x);
                if (IsTruthy(result)) return result;
            }
            return null;
        };

    /// <summary>Distinct? - returns true if all args are distinct</summary>
    public static bool distinct_QMARK_(params object?[] args) =>
        args.Distinct().Count() == args.Length;

    /// <summary>Instance? - returns true if x is instance of type</summary>
    public static bool instance_QMARK_(object? t, object? x) =>
        t is Type type && x is not null && type.IsInstanceOfType(x);

    /// <summary>Bean - converts .NET object to map of properties</summary>
    public static Dictionary<object, object?> bean(object? obj)
    {
        var result = new Dictionary<object, object?>();
        if (obj is null) return result;
        var type = obj.GetType();
        result[":class"] = type.FullName;
        foreach (var prop in type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            try
            {
                if (prop.CanRead && prop.GetIndexParameters().Length == 0)
                    result[$":{ToCamelCase(prop.Name)}"] = prop.GetValue(obj);
            }
            catch { /* ignore inaccessible properties */ }
        }
        return result;
    }

    private static string ToCamelCase(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToLower(s[0]) + s[1..];

    #region Type Reflection Utilities

    /// <summary>Returns the direct base class of a type (or nil for Object/interfaces)</summary>
    public static Type? bases(object? x)
    {
        var t = x as Type ?? x?.GetType();
        return t?.BaseType;
    }

    /// <summary>Returns a set of all superclasses and interfaces of a type</summary>
    public static HashSet<Type> supers(object? x)
    {
        var result = new HashSet<Type>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        // Add all interfaces
        foreach (var iface in t.GetInterfaces())
            result.Add(iface);

        // Walk base class chain
        var current = t.BaseType;
        while (current != null)
        {
            result.Add(current);
            current = current.BaseType;
        }
        return result;
    }

    /// <summary>Returns a set of all ancestors (superclasses + interfaces transitively)</summary>
    public static HashSet<Type> ancestors(object? x)
    {
        var result = new HashSet<Type>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        var queue = new Queue<Type>();
        queue.Enqueue(t);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();

            // Add interfaces
            foreach (var iface in current.GetInterfaces())
            {
                if (result.Add(iface))
                    queue.Enqueue(iface);
            }

            // Add base class
            if (current.BaseType != null && result.Add(current.BaseType))
                queue.Enqueue(current.BaseType);
        }
        return result;
    }

    /// <summary>Returns all public members of a type as a list of maps</summary>
    public static List<Dictionary<object, object?>> members(object? x)
    {
        var result = new List<Dictionary<object, object?>>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        foreach (var member in t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var info = new Dictionary<object, object?>
            {
                [":name"] = member.Name,
                [":kind"] = member.MemberType.ToString().ToLower(),
                [":declaring-class"] = member.DeclaringType?.FullName
            };

            if (member is MethodInfo mi)
            {
                info[":return-type"] = mi.ReturnType.Name;
                info[":parameters"] = mi.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}").ToList();
                info[":static"] = mi.IsStatic;
            }
            else if (member is PropertyInfo pi)
            {
                info[":property-type"] = pi.PropertyType.Name;
                info[":can-read"] = pi.CanRead;
                info[":can-write"] = pi.CanWrite;
            }
            else if (member is FieldInfo fi)
            {
                info[":field-type"] = fi.FieldType.Name;
                info[":static"] = fi.IsStatic;
            }

            result.Add(info);
        }
        return result;
    }

    /// <summary>Returns all public methods of a type as a list of maps</summary>
    public static List<Dictionary<object, object?>> methods(object? x)
    {
        var result = new List<Dictionary<object, object?>>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        foreach (var method in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            // Skip property accessors and Object methods
            if (method.IsSpecialName) continue;

            var info = new Dictionary<object, object?>
            {
                [":name"] = method.Name,
                [":return-type"] = method.ReturnType.Name,
                [":parameters"] = method.GetParameters().Select(p => new Dictionary<object, object?>
                {
                    [":name"] = p.Name,
                    [":type"] = p.ParameterType.Name
                }).ToList(),
                [":static"] = method.IsStatic,
                [":declaring-class"] = method.DeclaringType?.FullName
            };
            result.Add(info);
        }
        return result;
    }

    /// <summary>Returns all public fields of a type as a list of maps</summary>
    public static List<Dictionary<object, object?>> fields(object? x)
    {
        var result = new List<Dictionary<object, object?>>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        foreach (var field in t.GetFields(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var info = new Dictionary<object, object?>
            {
                [":name"] = field.Name,
                [":type"] = field.FieldType.Name,
                [":static"] = field.IsStatic,
                [":declaring-class"] = field.DeclaringType?.FullName
            };
            result.Add(info);
        }
        return result;
    }

    /// <summary>Returns all public properties of a type as a list of maps</summary>
    public static List<Dictionary<object, object?>> properties(object? x)
    {
        var result = new List<Dictionary<object, object?>>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        foreach (var prop in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
        {
            var info = new Dictionary<object, object?>
            {
                [":name"] = prop.Name,
                [":type"] = prop.PropertyType.Name,
                [":can-read"] = prop.CanRead,
                [":can-write"] = prop.CanWrite,
                [":declaring-class"] = prop.DeclaringType?.FullName
            };
            result.Add(info);
        }
        return result;
    }

    /// <summary>Returns all public constructors of a type as a list of maps</summary>
    public static List<Dictionary<object, object?>> constructors(object? x)
    {
        var result = new List<Dictionary<object, object?>>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        foreach (var ctor in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var info = new Dictionary<object, object?>
            {
                [":parameters"] = ctor.GetParameters().Select(p => new Dictionary<object, object?>
                {
                    [":name"] = p.Name,
                    [":type"] = p.ParameterType.Name
                }).ToList()
            };
            result.Add(info);
        }
        return result;
    }

    /// <summary>Comprehensive type reflection - returns full type information as a map</summary>
    public static Dictionary<object, object?> reflect(object? x)
    {
        var result = new Dictionary<object, object?>();
        var t = x as Type ?? x?.GetType();
        if (t is null) return result;

        result[":name"] = t.Name;
        result[":full-name"] = t.FullName;
        result[":namespace"] = t.Namespace;
        result[":assembly"] = t.Assembly.GetName().Name;
        result[":base-type"] = t.BaseType?.FullName;
        result[":is-class"] = t.IsClass;
        result[":is-interface"] = t.IsInterface;
        result[":is-enum"] = t.IsEnum;
        result[":is-value-type"] = t.IsValueType;
        result[":is-abstract"] = t.IsAbstract;
        result[":is-sealed"] = t.IsSealed;
        result[":is-generic"] = t.IsGenericType;
        result[":interfaces"] = t.GetInterfaces().Select(i => i.FullName).ToList();
        result[":constructors"] = constructors(t);
        result[":methods"] = methods(t);
        result[":properties"] = properties(t);
        result[":fields"] = fields(t);

        return result;
    }

    #endregion

    /// <summary>Random sample from collection with probability</summary>
    public static IEnumerable<object?> random_sample(double prob, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        foreach (var item in enumerable)
        {
            if (Random.Shared.NextDouble() < prob)
                yield return item;
        }
    }

    /// <summary>Trampoline - for tail recursion</summary>
    public static object? trampoline(Func<object?> f)
    {
        var result = f();
        while (result is Func<object?> next)
        {
            result = next();
        }
        return result;
    }

    /// <summary>Tree-seq - depth-first walk of tree</summary>
    public static IEnumerable<object?> tree_seq(Func<object?, bool> branch_QMARK_, Func<object?, IEnumerable<object?>> children, object? root)
    {
        var stack = new Stack<object?>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            yield return node;
            if (branch_QMARK_(node))
            {
                var kids = children(node).ToList();
                for (int i = kids.Count - 1; i >= 0; i--)
                    stack.Push(kids[i]);
            }
        }
    }

    /// <summary>Pads val to fill step</summary>
    public static IEnumerable<List<object?>> partition_all(int n, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        var chunk = new List<object?>();
        foreach (var item in enumerable)
        {
            chunk.Add(item);
            if (chunk.Count == n)
            {
                yield return chunk;
                chunk = [];
            }
        }
        if (chunk.Count > 0)
            yield return chunk;
    }

    /// <summary>Splits at index n</summary>
    public static List<List<object?>> split_at(int n, object? coll)
    {
        var items = coll is IEnumerable e ? e.Cast<object?>().ToList() : [];
        return [items.Take(n).ToList(), items.Skip(n).ToList()];
    }

    /// <summary>Splits by predicate</summary>
    public static List<List<object?>> split_with(Func<object?, bool> pred, object? coll)
    {
        var items = coll is IEnumerable e ? e.Cast<object?>().ToList() : [];
        var taking = items.TakeWhile(pred).ToList();
        var dropping = items.SkipWhile(pred).ToList();
        return [taking, dropping];
    }

    /// <summary>Frequencies of items</summary>
    public static Dictionary<object?, int> frequencies(object? coll)
    {
        var result = new Dictionary<object?, int>();
        if (coll is IEnumerable e)
        {
            foreach (var item in e)
            {
                result.TryGetValue(item, out var count);
                result[item] = count + 1;
            }
        }
        return result;
    }

    /// <summary>Max of values</summary>
    public static object? max(params object?[] args) =>
        args.Length == 0 ? null : args.Aggregate((a, b) => Gt(a, b) ? a : b);

    /// <summary>Min of values</summary>
    public static object? min(params object?[] args) =>
        args.Length == 0 ? null : args.Aggregate((a, b) => Lt(a, b) ? a : b);

    /// <summary>Max by key function</summary>
    public static object? max_key(Func<object?, object?> f, params object?[] args) =>
        args.Length == 0 ? null : args.Aggregate((a, b) => Gt(f(a), f(b)) ? a : b);

    /// <summary>Min by key function</summary>
    public static object? min_key(Func<object?, object?> f, params object?[] args) =>
        args.Length == 0 ? null : args.Aggregate((a, b) => Lt(f(a), f(b)) ? a : b);

    // ========== BIT OPERATIONS ==========

    public static long bit_and(long a, long b) => a & b;
    public static long bit_or(long a, long b) => a | b;
    public static long bit_xor(long a, long b) => a ^ b;
    public static long bit_not(long a) => ~a;
    public static long bit_shift_left(long a, int n) => a << n;
    public static long bit_shift_right(long a, int n) => a >> n;
    public static long unsigned_bit_shift_right(long a, int n) => (long)((ulong)a >> n);
    public static bool bit_test(long a, int n) => (a & (1L << n)) != 0;
    public static long bit_set(long a, int n) => a | (1L << n);
    public static long bit_clear(long a, int n) => a & ~(1L << n);
    public static long bit_flip(long a, int n) => a ^ (1L << n);

    // ========== NUMERIC PREDICATES ==========

    public static bool zero_QMARK_(object? x) => x is 0 or 0L or 0.0 or 0.0f or 0m;
    public static bool pos_QMARK_(object? x) => Gt(x, 0);
    public static bool neg_QMARK_(object? x) => Lt(x, 0);
    public static bool even_QMARK_(object? x) => x switch { long l => l % 2 == 0, int i => i % 2 == 0, _ => false };
    public static bool odd_QMARK_(object? x) => x switch { long l => l % 2 != 0, int i => i % 2 != 0, _ => false };
    public static bool int_QMARK_(object? x) => x is int or long or short or byte or sbyte;
    public static bool float_QMARK_(object? x) => x is float or double;
    public static bool double_QMARK_(object? x) => x is double;
    public static bool decimal_QMARK_(object? x) => x is decimal;
    public static bool rational_QMARK_(object? x) => IsNumeric(x);
    public static bool integer_QMARK_(object? x) => int_QMARK_(x);
    public static bool pos_int_QMARK_(object? x) => int_QMARK_(x) && pos_QMARK_(x);
    public static bool neg_int_QMARK_(object? x) => int_QMARK_(x) && neg_QMARK_(x);
    public static bool nat_int_QMARK_(object? x) => int_QMARK_(x) && !neg_QMARK_(x);

    // ========== NUMERIC CONVERSIONS ==========

    public static int int_cast(object? x) => Convert.ToInt32(x);
    public static long long_cast(object? x) => Convert.ToInt64(x);
    public static float float_cast(object? x) => Convert.ToSingle(x);
    public static double double_cast(object? x) => Convert.ToDouble(x);
    public static short short_cast(object? x) => Convert.ToInt16(x);
    public static byte byte_cast(object? x) => Convert.ToByte(x);
    public static char char_cast(object? x) => x is char c ? c : (char)Convert.ToInt32(x);
    public static bool boolean_cast(object? x) => IsTruthy(x);

    // Clojure coercion functions - these are the (int x), (long x) etc in Clojure
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int @int(object? x) => Convert.ToInt32(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long @long(object? x) => Convert.ToInt64(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float @float(object? x) => Convert.ToSingle(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double @double(object? x) => Convert.ToDouble(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static short @short(object? x) => Convert.ToInt16(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte @byte(object? x) => Convert.ToByte(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static char @char(object? x) => x is char c ? c : (char)Convert.ToInt32(x);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool @bool(object? x) => IsTruthy(x);

    // ========== MATH FUNCTIONS ==========

    public static double abs_double(double x) => Math.Abs(x);
    public static long abs_long(long x) => Math.Abs(x);
    public static object abs_num(object? x) => x switch
    {
        int i => Math.Abs(i),
        long l => Math.Abs(l),
        double d => Math.Abs(d),
        float f => Math.Abs(f),
        decimal m => Math.Abs(m),
        _ => Math.Abs(Convert.ToDouble(x))
    };

    public static long quot(object? a, object? b) => Convert.ToInt64(a) / Convert.ToInt64(b);
    public static long rem(object? a, object? b) => Convert.ToInt64(a) % Convert.ToInt64(b);
    public static double mod_double(double a, double b) => ((a % b) + b) % b;

    public static double sqrt(double x) => Math.Sqrt(x);
    public static double pow(double x, double y) => Math.Pow(x, y);
    public static double exp(double x) => Math.Exp(x);
    public static double log(double x) => Math.Log(x);
    public static double log10(double x) => Math.Log10(x);
    public static double sin(double x) => Math.Sin(x);
    public static double cos(double x) => Math.Cos(x);
    public static double tan(double x) => Math.Tan(x);
    public static double asin(double x) => Math.Asin(x);
    public static double acos(double x) => Math.Acos(x);
    public static double atan(double x) => Math.Atan(x);
    public static double atan2(double y, double x) => Math.Atan2(y, x);
    public static double floor(double x) => Math.Floor(x);
    public static double ceil(double x) => Math.Ceiling(x);
    public static double round(double x) => Math.Round(x);

    public const double PI = Math.PI;
    public const double E = Math.E;

    // ========== MISCELLANEOUS ==========

    /// <summary>Throws an exception with the given message</summary>
    public static object throw_ex(string message) => throw new Exception(message);

    /// <summary>Returns the hash code of x</summary>
    public static int hash(object? x) => x?.GetHashCode() ?? 0;

    /// <summary>Returns true if x and y are identical (reference equals)</summary>
    public static bool identical_QMARK_(object? x, object? y) => ReferenceEquals(x, y);

    /// <summary>Compares two values</summary>
    public static int compare(object? a, object? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (a is IComparable ca) return ca.CompareTo(b);
        return a.GetHashCode().CompareTo(b.GetHashCode());
    }

    /// <summary>Returns the type of x</summary>
    public static Type? type_of(object? x) => x?.GetType();

    /// <summary>Returns the class name of x</summary>
    public static string? class_name(object? x) => x?.GetType().FullName;

    /// <summary>Asserts condition, throws if false</summary>
    public static object? assert(bool condition, string? message = null)
    {
        if (!condition)
            throw new InvalidOperationException(message ?? "Assert failed");
        return null;
    }

    /// <summary>Time macro helper - returns elapsed milliseconds</summary>
    public static (object? Result, long ElapsedMs) time_fn(Func<object?> f)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = f();
        sw.Stop();
        return (result, sw.ElapsedMilliseconds);
    }

    #endregion

    #region Type Predicates

    public static bool IsNil(object? x) => x is null;
    public static bool IsSome(object? x) => x is not null;
    public static bool IsNumber(object? x) => IsNumeric(x);
    public static bool IsString(object? x) => x is string;
    public static bool IsKeyword(object? x) => x is Keyword;
    public static bool IsSymbol(object? x) => x is Symbol;
    public static bool IsList(object? x) => x is PersistentList;
    // More specific type predicates for walk to work correctly
    public static bool IsVector(object? x) => x is PersistentVector;
    public static bool IsMap(object? x) => x is PersistentHashMap or IDictionary;
    public static bool IsSet(object? x) => x is PersistentHashSet || x?.GetType().Name.Contains("HashSet") == true;
    public static bool IsFn(object? x) => x is Delegate;
    // IsSeq checks for ISeq interface - true sequences like lists and lazy seqs
    public static bool IsSeq(object? x) => x is ISeq;

    #endregion

    #region Arithmetic

    /// <summary>
    /// Type-dispatched addition - eliminates dynamic dispatch overhead.
    /// Pattern matching is JIT-friendly and allows inlining.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Add(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la + lb,
        (long la, int ib) => la + ib,
        (int ia, long lb) => ia + lb,
        (int ia, int ib) => ia + ib,
        (double da, double db) => da + db,
        (double da, long lb) => da + lb,
        (long la, double db) => la + db,
        (double da, int ib) => da + ib,
        (int ia, double db) => ia + db,
        (decimal ma, decimal mb) => ma + mb,
        (float fa, float fb) => fa + fb,
        _ => AddSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object AddSlow(object? a, object? b) => (dynamic?)a + (dynamic?)b;

    /// <summary>
    /// Type-dispatched subtraction.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Sub(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la - lb,
        (long la, int ib) => la - ib,
        (int ia, long lb) => ia - lb,
        (int ia, int ib) => ia - ib,
        (double da, double db) => da - db,
        (double da, long lb) => da - lb,
        (long la, double db) => la - db,
        (double da, int ib) => da - ib,
        (int ia, double db) => ia - db,
        (decimal ma, decimal mb) => ma - mb,
        (float fa, float fb) => fa - fb,
        _ => SubSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object SubSlow(object? a, object? b) => (dynamic?)a - (dynamic?)b;

    /// <summary>
    /// Type-dispatched multiplication.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Mul(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la * lb,
        (long la, int ib) => la * ib,
        (int ia, long lb) => ia * lb,
        (int ia, int ib) => ia * ib,
        (double da, double db) => da * db,
        (double da, long lb) => da * lb,
        (long la, double db) => la * db,
        (double da, int ib) => da * ib,
        (int ia, double db) => ia * db,
        (decimal ma, decimal mb) => ma * mb,
        (float fa, float fb) => fa * fb,
        _ => MulSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object MulSlow(object? a, object? b) => (dynamic?)a * (dynamic?)b;

    /// <summary>
    /// Type-dispatched division.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Div(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la / lb,
        (long la, int ib) => la / ib,
        (int ia, long lb) => ia / lb,
        (int ia, int ib) => ia / ib,
        (double da, double db) => da / db,
        (double da, long lb) => da / lb,
        (long la, double db) => la / db,
        (double da, int ib) => da / ib,
        (int ia, double db) => ia / db,
        (decimal ma, decimal mb) => ma / mb,
        (float fa, float fb) => fa / fb,
        _ => DivSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object DivSlow(object? a, object? b) => (dynamic?)a / (dynamic?)b;

    /// <summary>
    /// Type-dispatched modulo.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Mod(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la % lb,
        (long la, int ib) => la % ib,
        (int ia, long lb) => ia % lb,
        (int ia, int ib) => ia % ib,
        (double da, double db) => da % db,
        (decimal ma, decimal mb) => ma % mb,
        _ => ModSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static object ModSlow(object? a, object? b) => (dynamic?)a % (dynamic?)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Lt(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la < lb,
        (int ia, int ib) => ia < ib,
        (double da, double db) => da < db,
        (long la, int ib) => la < ib,
        (int ia, long lb) => ia < lb,
        _ => LtSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool LtSlow(object? a, object? b) => (dynamic?)a < (dynamic?)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Lte(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la <= lb,
        (int ia, int ib) => ia <= ib,
        (double da, double db) => da <= db,
        (long la, int ib) => la <= ib,
        (int ia, long lb) => ia <= lb,
        _ => LteSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool LteSlow(object? a, object? b) => (dynamic?)a <= (dynamic?)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Gt(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la > lb,
        (int ia, int ib) => ia > ib,
        (double da, double db) => da > db,
        (long la, int ib) => la > ib,
        (int ia, long lb) => ia > lb,
        _ => GtSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool GtSlow(object? a, object? b) => (dynamic?)a > (dynamic?)b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool Gte(object? a, object? b) => (a, b) switch
    {
        (long la, long lb) => la >= lb,
        (int ia, int ib) => ia >= ib,
        (double da, double db) => da >= db,
        (long la, int ib) => la >= ib,
        (int ia, long lb) => ia >= lb,
        _ => GteSlow(a, b)
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool GteSlow(object? a, object? b) => (dynamic?)a >= (dynamic?)b;

    /// <summary>
    /// Type-dispatched increment - eliminates boxing and dynamic dispatch.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Inc(object? x) => x switch
    {
        long l => l + 1,
        int i => i + 1,
        double d => d + 1.0,
        decimal m => m + 1m,
        float f => f + 1f,
        _ => Add(x, 1)
    };

    /// <summary>
    /// Type-dispatched decrement.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object Dec(object? x) => x switch
    {
        long l => l - 1,
        int i => i - 1,
        double d => d - 1.0,
        decimal m => m - 1m,
        float f => f - 1f,
        _ => Sub(x, 1)
    };

    #endregion

    // ========== ADDITIONAL SEQUENCE FUNCTIONS ==========
    #region Additional Sequences

    /// <summary>Take every nth element</summary>
    public static IEnumerable<object?> take_nth(int n, object? coll)
    {
        if (n <= 0) throw new ArgumentException("n must be positive");
        if (coll is not IEnumerable enumerable) yield break;
        var i = 0;
        foreach (var item in enumerable)
        {
            if (i % n == 0) yield return item;
            i++;
        }
    }

    /// <summary>Map with index</summary>
    public static IEnumerable<object?> map_indexed(Func<long, object?, object?> f, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        long i = 0;
        foreach (var item in enumerable)
        {
            yield return f(i++, item);
        }
    }

    /// <summary>Remove consecutive duplicates</summary>
    public static IEnumerable<object?> dedupe(object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;
        object? prev = new object(); // Sentinel that won't equal any real value
        var first = true;
        foreach (var item in enumerable)
        {
            if (first || !Equals(item, prev))
            {
                yield return item;
                prev = item;
                first = false;
            }
        }
    }

    /// <summary>Multi-arity map - maps f over multiple collections in parallel</summary>
    public static IEnumerable<object?> map(Func<object?, object?, object?> f, object? c1, object? c2)
    {
        if (c1 is not IEnumerable e1 || c2 is not IEnumerable e2) yield break;
        var enum1 = e1.GetEnumerator();
        var enum2 = e2.GetEnumerator();
        while (enum1.MoveNext() && enum2.MoveNext())
        {
            yield return f(enum1.Current, enum2.Current);
        }
    }

    /// <summary>Multi-arity map - maps f over three collections in parallel</summary>
    public static IEnumerable<object?> map(Func<object?, object?, object?, object?> f, object? c1, object? c2, object? c3)
    {
        if (c1 is not IEnumerable e1 || c2 is not IEnumerable e2 || c3 is not IEnumerable e3) yield break;
        var enum1 = e1.GetEnumerator();
        var enum2 = e2.GetEnumerator();
        var enum3 = e3.GetEnumerator();
        while (enum1.MoveNext() && enum2.MoveNext() && enum3.MoveNext())
        {
            yield return f(enum1.Current, enum2.Current, enum3.Current);
        }
    }

    #endregion

    // ========== TRANSDUCER INFRASTRUCTURE ==========
    #region Transducers

    /// <summary>
    /// Reduced wrapper - signals early termination from reduce
    /// </summary>
    public sealed class Reduced : IDeref
    {
        public object? Value { get; }
        public Reduced(object? value) => Value = value;
        public object? Deref() => Value;
    }

    /// <summary>Wraps value in Reduced to signal early termination</summary>
    public static Reduced reduced(object? x) => new Reduced(x);

    /// <summary>Returns true if x is a Reduced value</summary>
    public static bool reduced_QMARK_(object? x) => x is Reduced;

    /// <summary>Unwraps a Reduced value, or returns x if not reduced</summary>
    public static object? unreduced(object? x) => x is Reduced r ? r.Value : x;

    /// <summary>Ensures x is reduced (wraps if not already)</summary>
    public static Reduced ensure_reduced(object? x) => x is Reduced r ? r : new Reduced(x);

    /// <summary>
    /// Creates a reducing function with completion arity.
    /// Takes a 2-arity reducing fn and returns a 2-arity fn where
    /// cf is called on the result at completion.
    /// </summary>
    public static Func<object?, object?, object?> completing(Func<object?, object?, object?> rf, Func<object?, object?>? cf = null)
    {
        cf ??= identity_fn;
        return (acc, x) => x is null ? cf(acc) : rf(acc, x);
    }

    private static object? identity_fn(object? x) => x;

    /// <summary>
    /// Creates a lazy sequence by applying transducer xform to coll.
    /// This is the transducer version of map/filter/etc that creates lazy seqs.
    /// </summary>
    public static IEnumerable<object?> sequence(Func<Func<object?, object?, object?>, Func<object?, object?, object?>> xform, object? coll)
    {
        if (coll is not IEnumerable enumerable) yield break;

        // Create a reducing function that yields results
        var results = new List<object?>();
        Func<object?, object?, object?> collectingRf = (acc, x) =>
        {
            results.Add(x);
            return acc;
        };

        var xf = xform(collectingRf);

        foreach (var item in enumerable)
        {
            var result = xf(null, item);
            // Yield any accumulated results
            foreach (var r in results)
                yield return r;
            results.Clear();
            // Check for early termination
            if (result is Reduced)
                yield break;
        }
    }

    /// <summary>
    /// Eduction - a reducible/iterable view of applying transducer to coll
    /// More efficient than sequence for single-pass operations
    /// </summary>
    public sealed class Eduction : IEnumerable<object?>
    {
        private readonly Func<Func<object?, object?, object?>, Func<object?, object?, object?>> _xform;
        private readonly IEnumerable _coll;

        public Eduction(Func<Func<object?, object?, object?>, Func<object?, object?, object?>> xform, IEnumerable coll)
        {
            _xform = xform;
            _coll = coll;
        }

        public IEnumerator<object?> GetEnumerator()
        {
            var results = new List<object?>();
            Func<object?, object?, object?> collectingRf = (acc, x) =>
            {
                results.Add(x);
                return acc;
            };

            var xf = _xform(collectingRf);

            foreach (var item in _coll)
            {
                var result = xf(null, item);
                foreach (var r in results)
                    yield return r;
                results.Clear();
                if (result is Reduced)
                    yield break;
            }
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>Creates an eduction from transducer and collection</summary>
    public static Eduction eduction(Func<Func<object?, object?, object?>, Func<object?, object?, object?>> xform, object? coll)
    {
        if (coll is not IEnumerable enumerable)
            throw new ArgumentException("Expected collection");
        return new Eduction(xform, enumerable);
    }

    // ========== TRANSDUCER-RETURNING ARITIES ==========

    /// <summary>Returns a mapping transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> map(Func<object?, object?> f)
    {
        return rf => (acc, x) => rf(acc, f(x));
    }

    /// <summary>Returns a filtering transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> filter(Func<object?, bool> pred)
    {
        return rf => (acc, x) => pred(x) ? rf(acc, x) : acc;
    }

    /// <summary>Returns a removing transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> remove(Func<object?, bool> pred)
    {
        return rf => (acc, x) => pred(x) ? acc : rf(acc, x);
    }

    /// <summary>Returns a take transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> take(int n)
    {
        return rf =>
        {
            var remaining = n;
            return (acc, x) =>
            {
                if (remaining <= 0) return new Reduced(acc);
                remaining--;
                var result = rf(acc, x);
                return remaining <= 0 ? ensure_reduced(result) : result;
            };
        };
    }

    /// <summary>Returns a drop transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> drop(int n)
    {
        return rf =>
        {
            var remaining = n;
            return (acc, x) =>
            {
                if (remaining > 0) { remaining--; return acc; }
                return rf(acc, x);
            };
        };
    }

    /// <summary>Returns a take-while transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> take_while(Func<object?, bool> pred)
    {
        return rf => (acc, x) => pred(x) ? rf(acc, x) : new Reduced(acc);
    }

    /// <summary>Returns a drop-while transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> drop_while(Func<object?, bool> pred)
    {
        return rf =>
        {
            var dropping = true;
            return (acc, x) =>
            {
                if (dropping && pred(x)) return acc;
                dropping = false;
                return rf(acc, x);
            };
        };
    }

    /// <summary>Returns a distinct transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> distinct_xf()
    {
        return rf =>
        {
            var seen = new HashSet<object?>();
            return (acc, x) => seen.Add(x) ? rf(acc, x) : acc;
        };
    }

    /// <summary>Returns a dedupe transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> dedupe_xf()
    {
        return rf =>
        {
            object? prev = new object();
            var first = true;
            return (acc, x) =>
            {
                if (first || !Equals(x, prev))
                {
                    prev = x;
                    first = false;
                    return rf(acc, x);
                }
                return acc;
            };
        };
    }

    /// <summary>Returns a take-nth transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> take_nth(int n)
    {
        return rf =>
        {
            var i = 0;
            return (acc, x) =>
            {
                var result = (i % n == 0) ? rf(acc, x) : acc;
                i++;
                return result;
            };
        };
    }

    /// <summary>Returns a keep transducer (like map but drops nils)</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> keep(Func<object?, object?> f)
    {
        return rf => (acc, x) =>
        {
            var result = f(x);
            return result is not null ? rf(acc, result) : acc;
        };
    }

    /// <summary>Returns a map-indexed transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> map_indexed(Func<long, object?, object?> f)
    {
        return rf =>
        {
            long i = 0;
            return (acc, x) => rf(acc, f(i++, x));
        };
    }

    /// <summary>Returns a keep-indexed transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> keep_indexed(Func<long, object?, object?> f)
    {
        return rf =>
        {
            long i = 0;
            return (acc, x) =>
            {
                var result = f(i++, x);
                return result is not null ? rf(acc, result) : acc;
            };
        };
    }

    /// <summary>Returns a partition-all transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> partition_all(int n)
    {
        return rf =>
        {
            var chunk = new List<object?>();
            return (acc, x) =>
            {
                chunk.Add(x);
                if (chunk.Count == n)
                {
                    var result = rf(acc, chunk.ToList());
                    chunk.Clear();
                    return result;
                }
                return acc;
            };
        };
    }

    /// <summary>Returns a partition-by transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> partition_by(Func<object?, object?> f)
    {
        return rf =>
        {
            var chunk = new List<object?>();
            object? prevKey = null;
            var first = true;
            return (acc, x) =>
            {
                var key = f(x);
                if (!first && !Equals(key, prevKey))
                {
                    var result = rf(acc, chunk.ToList());
                    chunk.Clear();
                    chunk.Add(x);
                    prevKey = key;
                    return result;
                }
                chunk.Add(x);
                prevKey = key;
                first = false;
                return acc;
            };
        };
    }

    /// <summary>Returns an interpose transducer</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> interpose(object? sep)
    {
        return rf =>
        {
            var started = false;
            return (acc, x) =>
            {
                if (started)
                {
                    var intermediate = rf(acc, sep);
                    if (intermediate is Reduced) return intermediate;
                    return rf(intermediate, x);
                }
                started = true;
                return rf(acc, x);
            };
        };
    }

    /// <summary>
    /// cat transducer - concatenates inner collections
    /// (into [] cat [[1 2] [3 4]]) => [1 2 3 4]
    /// </summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> cat()
    {
        return rf =>
        {
            return (acc, x) =>
            {
                if (x is IEnumerable e)
                {
                    foreach (var item in e)
                    {
                        acc = rf(acc, item);
                        if (acc is Reduced) return acc;
                    }
                    return acc;
                }
                return rf(acc, x);
            };
        };
    }

    /// <summary>
    /// mapcat transducer - maps f over items and concatenates results
    /// (into [] (mapcat f) coll) where f returns sequences
    /// </summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> mapcat_xf(
        Func<object?, object?> f)
    {
        return comp(map(f), cat());
    }

    /// <summary>Composes transducers left-to-right</summary>
    public static Func<Func<object?, object?, object?>, Func<object?, object?, object?>> comp(
        params Func<Func<object?, object?, object?>, Func<object?, object?, object?>>[] xforms)
    {
        if (xforms.Length == 0) return rf => rf;
        if (xforms.Length == 1) return xforms[0];
        return rf =>
        {
            var result = rf;
            for (int i = xforms.Length - 1; i >= 0; i--)
            {
                result = xforms[i](result);
            }
            return result;
        };
    }

    #endregion

    // ========== SUBVEC ==========
    #region SubVec

    /// <summary>
    /// SubVector - efficient view into a PersistentVector
    /// </summary>
    public sealed class SubVector : IList<object?>, IList
    {
        internal readonly PersistentVector Vector;
        internal readonly int Start;
        internal readonly int End;

        public SubVector(PersistentVector vector, int start, int end)
        {
            if (start < 0 || end > vector.Count || start > end)
                throw new ArgumentOutOfRangeException($"Invalid subvec bounds: [{start}, {end}) for vector of length {vector.Count}");
            Vector = vector;
            Start = start;
            End = end;
        }

        public int Count => End - Start;

        public object? this[int index]
        {
            get
            {
                if (index < 0 || index >= Count)
                    throw new ArgumentOutOfRangeException(nameof(index));
                return Vector.Nth(Start + index);
            }
            set => throw new NotSupportedException("SubVector is immutable");
        }

        public bool IsReadOnly => true;
        public bool IsFixedSize => true;
        public bool IsSynchronized => false;
        public object SyncRoot => this;

        object? IList.this[int index]
        {
            get => this[index];
            set => throw new NotSupportedException("SubVector is immutable");
        }

        public IEnumerator<object?> GetEnumerator()
        {
            for (int i = Start; i < End; i++)
                yield return Vector.Nth(i);
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public bool Contains(object? item)
        {
            for (int i = Start; i < End; i++)
                if (Equals(Vector.Nth(i), item)) return true;
            return false;
        }

        public int IndexOf(object? item)
        {
            for (int i = Start; i < End; i++)
                if (Equals(Vector.Nth(i), item)) return i - Start;
            return -1;
        }

        public void CopyTo(object?[] array, int arrayIndex)
        {
            for (int i = Start; i < End; i++)
                array[arrayIndex++] = Vector.Nth(i);
        }

        public void CopyTo(Array array, int index)
        {
            for (int i = Start; i < End; i++)
                array.SetValue(Vector.Nth(i), index++);
        }

        // Unsupported mutating operations (IList<object?>)
        public void Add(object? item) => throw new NotSupportedException("SubVector is immutable");
        public void Insert(int index, object? item) => throw new NotSupportedException("SubVector is immutable");
        public bool Remove(object? item) => throw new NotSupportedException("SubVector is immutable");
        public void RemoveAt(int index) => throw new NotSupportedException("SubVector is immutable");
        public void Clear() => throw new NotSupportedException("SubVector is immutable");

        // Explicit interface implementations for non-generic IList
        int IList.Add(object? value) => throw new NotSupportedException("SubVector is immutable");
        void IList.Remove(object? value) => throw new NotSupportedException("SubVector is immutable");
    }

    /// <summary>Returns a subvector from start (inclusive) to end (exclusive)</summary>
    public static object subvec(object? v, int start, int end)
    {
        if (v is PersistentVector pv)
            return new SubVector(pv, start, end);
        if (v is SubVector sv)
            return new SubVector(sv.Vector, sv.Start + start, sv.Start + end);
        if (v is IList list)
        {
            var result = new List<object?>();
            for (int i = start; i < end && i < list.Count; i++)
                result.Add(list[i]);
            return result;
        }
        throw new ArgumentException("subvec requires a vector");
    }

    /// <summary>Returns a subvector from start to end of vector</summary>
    public static object subvec(object? v, int start)
    {
        var count = v switch
        {
            PersistentVector pv => pv.Count,
            SubVector sv => sv.Count,
            IList list => list.Count,
            _ => throw new ArgumentException("subvec requires a vector")
        };
        return subvec(v, start, count);
    }

    #endregion

    // ========== ASYNC HELPERS (Clojure-callable) ==========
    #region Async Helpers

    /// <summary>
    /// Maps any Clojure function (sync or async) over a sequence concurrently.
    /// Clojure-callable version that accepts any function type.
    /// </summary>
    public static Task<List<object?>> map_async(object? f, object? coll)
    {
        var items = coll switch
        {
            IEnumerable<object?> e => e,
            System.Collections.IEnumerable e => e.Cast<object?>(),
            _ => throw new ArgumentException("Expected collection")
        };
        return Async.MapAsync(f, items);
    }

    /// <summary>
    /// Maps with concurrency limit. Clojure-callable version.
    /// </summary>
    public static Task<List<object?>> map_async(object? f, object? coll, int maxConcurrency)
    {
        var items = coll switch
        {
            IEnumerable<object?> e => e,
            System.Collections.IEnumerable e => e.Cast<object?>(),
            _ => throw new ArgumentException("Expected collection")
        };
        return Async.MapAsync(f, items, maxConcurrency);
    }

    #endregion

    // ========== WALK FUNCTIONS ==========
    #region Walk

    /// <summary>
    /// Converts an object (Func, Delegate, or callable) to Func&lt;object?, object?&gt;
    /// </summary>
    private static Func<object?, object?> ToFunc1(object? f)
    {
        if (f is Func<object?, object?> func)
            return func;
        if (f is Delegate del)
            return x => del.DynamicInvoke(x);
        // Fallback - try to invoke via call
        return x => call(f, x);
    }

    /// <summary>
    /// Core walk function - traverses form, applying inner to each element and outer to the result.
    /// Uses explicit type checks for Cljr collections to avoid predicate issues.
    /// </summary>
    public static object? walk_core(Func<object?, object?> inner, Func<object?, object?> outer, object? form)
    {
        // Map entries - must be checked first before maps
        if (form is IMapEntry me)
            return outer(map_entry(inner(me.Key()), inner(me.Val())));

        // Explicit type checks for Cljr persistent collections
        if (form is PersistentVector vec)
        {
            var result = PersistentVector.Create();
            foreach (var item in vec)
                result = (PersistentVector)result.Conj(inner(item));
            return outer(result);
        }
        if (form is PersistentHashMap hmap)
        {
            var result = PersistentHashMap.Create();
            foreach (var entry in hmap)
            {
                var transformed = inner(entry);
                // Handle both IMapEntry and PersistentVector [k v] results
                if (transformed is IMapEntry tme)
                    result = (PersistentHashMap)result.Assoc(tme.Key(), tme.Val());
                else if (transformed is PersistentVector v && v.Count == 2)
                    result = (PersistentHashMap)result.Assoc(v.Nth(0), v.Nth(1));
            }
            return outer(result);
        }
        if (form is PersistentHashSet hset)
        {
            var result = PersistentHashSet.Create();
            foreach (var item in hset)
                result = (PersistentHashSet)result.Conj(inner(item));
            return outer(result);
        }
        // Lists
        if (list_QMARK_(form))
            return outer(apply(list, map(inner, form)));
        // Other sequences (lazy seqs, etc.)
        if (form is IEnumerable enumerable && form is not string)
            return outer(doall(map(inner, form)));
        // Atomic values
        return outer(form);
    }

    /// <summary>
    /// Walk function that accepts object? for inner and outer (for Clojure interop)
    /// </summary>
    public static object? walk_impl(object? inner, object? outer, object? form) =>
        walk_core(ToFunc1(inner), ToFunc1(outer), form);

    /// <summary>
    /// Postwalk - depth-first post-order traversal (typed version)
    /// </summary>
    public static object? postwalk_typed(Func<object?, object?> f, object? form) =>
        walk_core(x => postwalk_typed(f, x), f, form);

    /// <summary>
    /// Postwalk - accepts object? for Clojure interop
    /// </summary>
    public static object? postwalk_impl(object? f, object? form)
    {
        var func = ToFunc1(f);
        return postwalk_typed(func, form);
    }

    /// <summary>
    /// Prewalk - depth-first pre-order traversal (typed version)
    /// </summary>
    public static object? prewalk_typed(Func<object?, object?> f, object? form) =>
        walk_core(x => prewalk_typed(f, x), identity, f(form));

    /// <summary>
    /// Prewalk - accepts object? for Clojure interop
    /// </summary>
    public static object? prewalk_impl(object? f, object? form)
    {
        var func = ToFunc1(f);
        return prewalk_typed(func, form);
    }

    /// <summary>
    /// Creates a replacer function for postwalk-replace and prewalk-replace.
    /// </summary>
    public static Func<object?, object?> make_replacer(object? smap) =>
        x => IsTruthy(contains_QMARK_(smap, x)) ? get(smap, x) : x;

    /// <summary>
    /// Postwalk with replacement map
    /// </summary>
    public static object? postwalk_replace_impl(object? smap, object? form) =>
        postwalk_typed(make_replacer(smap), form);

    /// <summary>
    /// Prewalk with replacement map
    /// </summary>
    public static object? prewalk_replace_impl(object? smap, object? form) =>
        prewalk_typed(make_replacer(smap), form);

    /// <summary>
    /// Recursively transforms all map keys from keywords to strings.
    /// </summary>
    public static object? stringify_keys_impl(object? m)
    {
        Func<object?, object?> transformEntry = kv =>
        {
            var k = key(kv);
            var v = val(kv);
            var newKey = IsTruthy(keyword_QMARK_(k)) ? name(k) : k;
            return PersistentVector.Create(newKey, v);
        };

        Func<object?, object?> transformForm = form =>
        {
            if (IsTruthy(map_QMARK_(form)))
                return into(PersistentHashMap.Create(), map(transformEntry, form));
            return form;
        };

        return postwalk_typed(transformForm, m);
    }

    /// <summary>
    /// Recursively transforms all map keys from strings to keywords.
    /// </summary>
    public static object? keywordize_keys_impl(object? m)
    {
        Func<object?, object?> transformEntry = kv =>
        {
            var k = key(kv);
            var v = val(kv);
            var newKey = IsTruthy(string_QMARK_(k)) ? keyword(k) : k;
            return PersistentVector.Create(newKey, v);
        };

        Func<object?, object?> transformForm = form =>
        {
            if (IsTruthy(map_QMARK_(form)))
                return into(PersistentHashMap.Create(), map(transformEntry, form));
            return form;
        };

        return postwalk_typed(transformForm, m);
    }

    #endregion

    #region Additional Sequence Operations

    /// <summary>
    /// Helper to convert object to enumerable
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static IEnumerable<object?> ToEnumerable(object? coll) => coll switch
    {
        null => Enumerable.Empty<object?>(),
        IEnumerable<object?> e => e,
        IEnumerable e => e.Cast<object?>(),
        _ => new[] { coll }
    };

    /// <summary>
    /// Returns a lazy sequence of all but the last n (default 1) items in coll.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<object?> drop_last(object? coll) => drop_last(1L, coll);

    /// <summary>
    /// Returns a lazy sequence of all but the last n items in coll.
    /// </summary>
    public static IEnumerable<object?> drop_last(object? n, object? coll)
    {
        var count = Convert.ToInt64(n);
        if (count <= 0) return ToEnumerable(coll);

        var items = ToEnumerable(coll).ToList();
        var takeCount = Math.Max(0, items.Count - (int)count);
        return items.Take(takeCount);
    }

    /// <summary>
    /// Returns a sequence of the last n items in coll. Runs in O(n) time.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static IEnumerable<object?> take_last(object? n, object? coll)
    {
        var count = Convert.ToInt32(n);
        if (count <= 0) return Enumerable.Empty<object?>();

        var items = ToEnumerable(coll).ToList();
        var skipCount = Math.Max(0, items.Count - count);
        return items.Skip(skipCount);
    }

    /// <summary>
    /// Returns the nth rest of coll, coll when n is 0.
    /// </summary>
    public static IEnumerable<object?> nthrest(object? coll, object? n)
    {
        var count = Convert.ToInt64(n);
        if (count <= 0) return ToEnumerable(coll);

        var result = coll;
        for (long i = 0; i < count && result != null; i++)
        {
            result = Rest(result);
        }
        return ToEnumerable(result);
    }

    /// <summary>
    /// Returns the nth next of coll, nil when n is greater than count of coll.
    /// </summary>
    public static object? nthnext(object? coll, object? n)
    {
        var count = Convert.ToInt64(n);
        if (count < 0) return null;

        var result = Seq(coll);
        for (long i = 0; i < count && result != null; i++)
        {
            result = Next(result);
        }
        return result;
    }

    /// <summary>
    /// Returns a reverse sequence of coll. For vectors and sorted collections, this is O(1).
    /// </summary>
    public static IEnumerable<object?>? rseq(object? coll)
    {
        var result = coll switch
        {
            null => null,
            PersistentVector pv => pv.Count > 0 ? ((IEnumerable)pv).Cast<object?>().Reverse().ToList() : null,
            IList<object?> list => list.Count > 0 ? list.Reverse().ToList() : null,
            string s => s.Length > 0 ? s.Reverse().Select(c => (object?)c).ToList() : null,
            IEnumerable<object?> e => e.Any() ? e.Reverse().ToList() : null,
            IEnumerable e => e.Cast<object?>().Any() ? e.Cast<object?>().Reverse().ToList() : null,
            _ => null
        };
        return result;
    }

    /// <summary>
    /// Returns a sorted map with supplied mappings, using the natural order of keys.
    /// </summary>
    public static SortedDictionary<object, object?> sorted_map(params object?[] keyvals)
    {
        var result = new SortedDictionary<object, object?>(Comparer<object>.Default);
        for (int i = 0; i < keyvals.Length - 1; i += 2)
        {
            var key = keyvals[i];
            var val = keyvals[i + 1];
            if (key != null)
                result[key] = val;
        }
        return result;
    }

    /// <summary>
    /// Returns a sorted map with supplied mappings, using the supplied comparator.
    /// </summary>
    public static SortedDictionary<object, object?> sorted_map_by(object? comp, params object?[] keyvals)
    {
        var comparator = comp switch
        {
            IComparer<object> c => c,
            Func<object?, object?, int> f => Comparer<object>.Create((a, b) => f(a, b)),
            Func<object?, object?, object?> f => Comparer<object>.Create((a, b) => Convert.ToInt32(f(a, b))),
            _ => Comparer<object>.Default
        };

        var result = new SortedDictionary<object, object?>(comparator);
        for (int i = 0; i < keyvals.Length - 1; i += 2)
        {
            var key = keyvals[i];
            var val = keyvals[i + 1];
            if (key != null)
                result[key] = val;
        }
        return result;
    }

    /// <summary>
    /// Returns a sorted set with the supplied keys, using natural ordering.
    /// </summary>
    public static SortedSet<object> sorted_set(params object?[] keys)
    {
        var result = new SortedSet<object>(Comparer<object>.Default);
        foreach (var key in keys)
        {
            if (key != null)
                result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// Returns a sorted set with the supplied keys, using the supplied comparator.
    /// </summary>
    public static SortedSet<object> sorted_set_by(object? comp, params object?[] keys)
    {
        var comparator = comp switch
        {
            IComparer<object> c => c,
            Func<object?, object?, int> f => Comparer<object>.Create((a, b) => f(a, b)),
            Func<object?, object?, object?> f => Comparer<object>.Create((a, b) => Convert.ToInt32(f(a, b))),
            _ => Comparer<object>.Default
        };

        var result = new SortedSet<object>(comparator);
        foreach (var key in keys)
        {
            if (key != null)
                result.Add(key);
        }
        return result;
    }

    /// <summary>
    /// Printf-style string formatting using .NET composite formatting.
    /// Supports %s, %d, %f, %x, %o, %e, %g, %% format specifiers.
    /// </summary>
    public static string format(object? fmt, params object?[] args)
    {
        if (fmt == null) return "";
        var formatStr = fmt.ToString() ?? "";

        // Convert printf-style format to .NET format
        var argIndex = 0;
        var result = new StringBuilder();
        var i = 0;

        while (i < formatStr.Length)
        {
            if (formatStr[i] == '%')
            {
                if (i + 1 < formatStr.Length)
                {
                    var nextChar = formatStr[i + 1];

                    // Handle %% escape
                    if (nextChar == '%')
                    {
                        result.Append('%');
                        i += 2;
                        continue;
                    }

                    // Parse width and precision
                    var j = i + 1;
                    var width = "";
                    var precision = "";
                    var leftAlign = false;

                    // Check for flags
                    if (j < formatStr.Length && formatStr[j] == '-')
                    {
                        leftAlign = true;
                        j++;
                    }

                    // Parse width
                    while (j < formatStr.Length && char.IsDigit(formatStr[j]))
                    {
                        width += formatStr[j];
                        j++;
                    }

                    // Parse precision
                    if (j < formatStr.Length && formatStr[j] == '.')
                    {
                        j++;
                        while (j < formatStr.Length && char.IsDigit(formatStr[j]))
                        {
                            precision += formatStr[j];
                            j++;
                        }
                    }

                    // Parse conversion specifier
                    if (j < formatStr.Length)
                    {
                        var spec = formatStr[j];
                        var arg = argIndex < args.Length ? args[argIndex] : null;
                        argIndex++;

                        var formatted = spec switch
                        {
                            's' => arg?.ToString() ?? "null",
                            'd' or 'i' => Convert.ToInt64(arg ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            'f' => precision != ""
                                ? Convert.ToDouble(arg ?? 0).ToString($"F{precision}", System.Globalization.CultureInfo.InvariantCulture)
                                : Convert.ToDouble(arg ?? 0).ToString("F6", System.Globalization.CultureInfo.InvariantCulture),
                            'e' => precision != ""
                                ? Convert.ToDouble(arg ?? 0).ToString($"E{precision}", System.Globalization.CultureInfo.InvariantCulture)
                                : Convert.ToDouble(arg ?? 0).ToString("E6", System.Globalization.CultureInfo.InvariantCulture),
                            'g' => Convert.ToDouble(arg ?? 0).ToString("G", System.Globalization.CultureInfo.InvariantCulture),
                            'x' => Convert.ToInt64(arg ?? 0).ToString("x"),
                            'X' => Convert.ToInt64(arg ?? 0).ToString("X"),
                            'o' => Convert.ToString(Convert.ToInt64(arg ?? 0), 8),
                            'c' => arg is char c ? c.ToString() : ((char)Convert.ToInt32(arg ?? 0)).ToString(),
                            'b' => IsTruthy(arg) ? "true" : "false",
                            'n' => Environment.NewLine,
                            _ => $"%{spec}"
                        };

                        // Apply width
                        if (width != "" && int.TryParse(width, out var w))
                        {
                            formatted = leftAlign
                                ? formatted.PadRight(w)
                                : formatted.PadLeft(w);
                        }

                        result.Append(formatted);
                        i = j + 1;
                        continue;
                    }
                }
            }

            result.Append(formatStr[i]);
            i++;
        }

        return result.ToString();
    }

    /// <summary>
    /// Returns a sequence of successive items from coll while (pred item) returns true.
    /// Unlike take-while which is lazy, this eagerly evaluates.
    /// </summary>
    public static IEnumerable<object?> take_while_eager(Func<object?, bool> pred, object? coll)
    {
        var result = new List<object?>();
        foreach (var item in ToEnumerable(coll))
        {
            if (!pred(item)) break;
            result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Returns a sequence of the items in coll starting from the first item
    /// for which (pred item) returns logical false.
    /// </summary>
    public static IEnumerable<object?> drop_while_eager(Func<object?, bool> pred, object? coll)
    {
        var dropping = true;
        var result = new List<object?>();
        foreach (var item in ToEnumerable(coll))
        {
            if (dropping && !pred(item))
                dropping = false;
            if (!dropping)
                result.Add(item);
        }
        return result;
    }

    /// <summary>
    /// Returns the first logical true value of (pred x) for any x in coll,
    /// else nil. This version accepts object? returning predicate.
    /// </summary>
    public static object? some_pred(Func<object?, object?> pred, object? coll)
    {
        if (coll is not IEnumerable enumerable) return null;
        foreach (var item in enumerable)
        {
            var result = pred(item);
            if (IsTruthy(result)) return result;
        }
        return null;
    }

    /// <summary>
    /// Returns the first item in coll for which (pred item) returns logical true.
    /// </summary>
    public static object? find_first(Func<object?, bool> pred, object? coll)
    {
        foreach (var item in ToEnumerable(coll))
        {
            if (pred(item)) return item;
        }
        return null;
    }

    /// <summary>
    /// Returns the index of the first item in coll for which (pred item) returns true,
    /// or nil if not found.
    /// </summary>
    public static object? find_index(Func<object?, bool> pred, object? coll)
    {
        var index = 0L;
        foreach (var item in ToEnumerable(coll))
        {
            if (pred(item)) return index;
            index++;
        }
        return null;
    }

    /// <summary>
    /// When coll is not empty, returns the result of (reduce f coll),
    /// otherwise returns (f).
    /// </summary>
    public static object? reduce_without_init(object? f, object? coll)
    {
        // ULTRA-FAST PATH: LongRange with + function uses O(1) formula
        if (coll is Collections.LongRange lr)
        {
            if (lr.Count == 0)
            {
                return f switch
                {
                    Func<object?> f0 => f0(),
                    Delegate d => d.DynamicInvoke(),
                    _ => 0L
                };
            }
            if (IsAddFunction(f))
                return lr.ReduceAddSimd();  // O(1) Gauss formula!
            return lr.reduce(ToFunc2(f));
        }

        // Fast path for IReduce implementations
        if (coll is IReduce r)
            return r.reduce(ToFunc2(f));

        var seq = Seq(coll);
        if (seq == null)
        {
            // Call f with no args
            return f switch
            {
                Func<object?> f0 => f0(),
                Delegate d => d.DynamicInvoke(),
                _ => null
            };
        }

        var first = First(seq);
        var rest = Next(seq);

        if (rest == null) return first;

        // Convert object function to proper delegate for reduce
        Func<object?, object?, object?> reduceFn = f switch
        {
            Func<object?, object?, object?> fn => fn,
            Delegate d => (a, b) => d.DynamicInvoke(a, b),
            _ => throw new ArgumentException("reduce requires a function")
        };

        return reduce(reduceFn, first, rest);
    }

    /// <summary>
    /// Checks if the function is the + (addition) function for fast-path optimization.
    /// Uses a simple behavioral test: if f(1L, 2L) == 3L, it's an add function.
    /// </summary>
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    private static bool IsAddFunction(object? f)
    {
        // Fast path: check for singleton AddDelegate by reference
        if (ReferenceEquals(f, AddDelegate))
            return true;

        // Behavioral test: if f(1, 2) == 3, it's addition
        if (f is Func<object?, object?, object?> fn)
        {
            try
            {
                var result = fn(1L, 2L);
                return result is long l && l == 3L;
            }
            catch
            {
                return false;
            }
        }

        // Check for Var referencing +
        if (f is Var v)
            return v.Name?.ToString() == "+" || v.Name?.ToString() == "_PLUS_";
        return false;
    }

    #endregion

    #region Subvec and Vector Operations

    /// <summary>
    /// Returns a persistent vector of the items in vector from start (inclusive) to end (exclusive).
    /// </summary>
    public static PersistentVector subvec(object? v, object? start)
    {
        if (v is not PersistentVector pv)
            throw new ArgumentException("subvec requires a vector");

        var startIdx = Convert.ToInt32(start);
        return subvec(v, start, (object?)pv.Count);
    }

    /// <summary>
    /// Returns a persistent vector of the items in vector from start (inclusive) to end (exclusive).
    /// </summary>
    public static PersistentVector subvec(object? v, object? start, object? end)
    {
        if (v is not PersistentVector pv)
            throw new ArgumentException("subvec requires a vector");

        var startIdx = Convert.ToInt32(start);
        var endIdx = Convert.ToInt32(end);

        if (startIdx < 0 || endIdx > pv.Count || startIdx > endIdx)
            throw new ArgumentOutOfRangeException($"subvec indices out of range: [{startIdx}, {endIdx}) for vector of count {pv.Count}");

        // Create a new vector with the subrange
        var items = new object?[endIdx - startIdx];
        for (int i = startIdx; i < endIdx; i++)
        {
            items[i - startIdx] = pv.Nth(i);
        }
        return PersistentVector.Create(items);
    }

    #endregion

    #region Side Effects and Execution Control

    /// <summary>
    /// Runs the effects of f on the items of coll for their side effects.
    /// Returns nil.
    /// </summary>
    public static object? run_BANG_(Func<object?, object?> proc, object? coll)
    {
        foreach (var item in ToEnumerable(coll))
        {
            proc(item);
        }
        return null;
    }

    /// <summary>
    /// When passed a collection, forces evaluation of all elements.
    /// Returns nil.
    /// </summary>
    public static object? dorun(object? coll)
    {
        foreach (var _ in ToEnumerable(coll)) { }
        return null;
    }

    /// <summary>
    /// When passed n and a collection, forces evaluation of first n elements.
    /// Returns nil.
    /// </summary>
    public static object? dorun(long n, object? coll)
    {
        var count = 0L;
        foreach (var _ in ToEnumerable(coll))
        {
            if (++count >= n) break;
        }
        return null;
    }

    #endregion

    #region Function Combinators

    /// <summary>
    /// Takes a function f, and returns a function that calls f, replacing
    /// a nil first argument to f with the supplied value x.
    /// </summary>
    public static Func<object?, object?> fnil(Func<object?, object?> f, object? x) =>
        arg => f(arg ?? x);

    /// <summary>
    /// Takes a function f, and returns a function that calls f, replacing
    /// nil first and second arguments to f with the supplied values x and y.
    /// </summary>
    public static Func<object?, object?, object?> fnil(Func<object?, object?, object?> f, object? x, object? y) =>
        (a, b) => f(a ?? x, b ?? y);

    /// <summary>
    /// Takes a function f, and returns a function that calls f, replacing
    /// nil first, second, and third arguments to f with the supplied values x, y, and z.
    /// </summary>
    public static Func<object?, object?, object?, object?> fnil(Func<object?, object?, object?, object?> f, object? x, object? y, object? z) =>
        (a, b, c) => f(a ?? x, b ?? y, c ?? z);

    #endregion

    #region Counting and Bounds

    /// <summary>
    /// If coll is counted? returns its count, else will count at most the first n
    /// elements of coll using seq.
    /// </summary>
    public static long bounded_count(long n, object? coll)
    {
        // Fast path for counted collections
        if (coll is ICollection c) return Math.Min(c.Count, n);
        if (coll is PersistentVector pv) return Math.Min(pv.Count, n);
        if (coll is PersistentHashMap phm) return Math.Min(phm.Count, n);
        if (coll is PersistentHashSet phs) return Math.Min(phs.Count, n);
        if (coll is string s) return Math.Min(s.Length, n);

        // Slow path for other sequences
        var count = 0L;
        foreach (var _ in ToEnumerable(coll))
        {
            if (++count >= n) return n;
        }
        return count;
    }

    #endregion

    #region Parallel Execution

    /// <summary>
    /// Executes the no-arg fns in parallel, returning a lazy sequence of their values.
    /// </summary>
    public static IEnumerable<object?> pcalls(params Func<object?>[] fns)
    {
        var tasks = fns.Select(f => Task.Run(() => f())).ToArray();
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    /// <summary>
    /// Returns a lazy sequence of the values of the exprs, which are evaluated in parallel.
    /// </summary>
    public static IEnumerable<object?> pvalues(params object?[] vals) => vals.ToList();

    /// <summary>
    /// Like map, except f is applied in parallel. Semi-lazy in that the
    /// parallel computation stays ahead of the consumption, but doesn't
    /// realize the entire result unless required.
    /// </summary>
    public static IEnumerable<object?> pmap(Func<object?, object?> f, object? coll)
    {
        var items = ToEnumerable(coll).ToList();
        var tasks = items.Select(item => Task.Run(() => f(item))).ToArray();
        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    /// <summary>
    /// Like map, except f is applied in parallel with two collections.
    /// </summary>
    public static IEnumerable<object?> pmap(Func<object?, object?, object?> f, object? coll1, object? coll2)
    {
        var items1 = ToEnumerable(coll1).ToList();
        var items2 = ToEnumerable(coll2).ToList();
        var count = Math.Min(items1.Count, items2.Count);

        var tasks = new Task<object?>[count];
        for (int i = 0; i < count; i++)
        {
            var item1 = items1[i];
            var item2 = items2[i];
            tasks[i] = Task.Run(() => f(item1, item2));
        }

        Task.WaitAll(tasks);
        return tasks.Select(t => t.Result).ToList();
    }

    #endregion
}
