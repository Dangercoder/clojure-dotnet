using System.Text.RegularExpressions;
using Cljr.Compiler.Reader;

// Use Reader types explicitly to avoid conflicts with Cljr.Runtime types
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;
using ReaderKeyword = Cljr.Compiler.Reader.Keyword;

namespace Cljr.Compiler.Macros;

/// <summary>
/// Static runtime functions for macro execution.
/// These operate on COMPILER AST types (Cljr.Compiler.Reader.*),
/// not runtime types (Cljr.*).
///
/// Used by compiled macros to manipulate code structures.
/// </summary>
public static class MacroRuntime
{
    #region Collection Operations

    public static object? First(object? coll)
    {
        return coll switch
        {
            null => null,
            PersistentList list => list.Count > 0 ? list[0] : null,
            PersistentVector vec => vec.Count > 0 ? vec[0] : null,
            string s => s.Length > 0 ? s[0].ToString() : null,
            IEnumerable<object?> en => en.FirstOrDefault(),
            _ => null
        };
    }

    public static object? Second(object? coll) => First(Rest(coll));

    public static object? Rest(object? coll)
    {
        return coll switch
        {
            null => PersistentList.Empty,
            PersistentList list => new PersistentList(list.Skip(1)),
            PersistentVector vec => new PersistentList(vec.Skip(1)),
            _ => PersistentList.Empty
        };
    }

    public static object? Next(object? coll)
    {
        var rest = Rest(coll);
        if (rest is PersistentList list && list.Count == 0)
            return null;
        return rest;
    }

    public static PersistentList Cons(object? x, object? coll)
    {
        var items = new List<object?> { x };
        if (coll != null)
            items.AddRange(ToEnumerable(coll));
        return new PersistentList(items);
    }

    public static object? Conj(object? coll, params object?[] items)
    {
        return coll switch
        {
            null => new PersistentList(items),
            PersistentList list => new PersistentList(items.Concat(list)),
            PersistentVector vec => new PersistentVector(vec.Concat(items)),
            PersistentSet set => new PersistentSet(set.Concat(items)),
            _ => throw new MacroException($"Cannot conj to: {coll.GetType().Name}")
        };
    }

    public static PersistentList Concat(params object?[] colls)
    {
        var result = new List<object?>();
        foreach (var coll in colls)
        {
            if (coll != null)
                result.AddRange(ToEnumerable(coll));
        }
        return new PersistentList(result);
    }

    public static PersistentList List(params object?[] items) => new(items);

    public static PersistentVector Vector(params object?[] items) => new(items);

    public static PersistentMap HashMap(params object?[] kvs)
    {
        var pairs = new List<KeyValuePair<object, object?>>();
        for (int i = 0; i < kvs.Length; i += 2)
        {
            var key = kvs[i]!;
            var val = i + 1 < kvs.Length ? kvs[i + 1] : null;
            pairs.Add(new KeyValuePair<object, object?>(key, val));
        }
        return new PersistentMap(pairs);
    }

    public static PersistentSet HashSet(params object?[] items) => new(items);

    public static PersistentVector Vec(object? coll)
    {
        if (coll == null) return PersistentVector.Empty;
        return new PersistentVector(ToEnumerable(coll));
    }

    public static object? Seq(object? coll)
    {
        if (coll == null) return null;
        var items = ToEnumerable(coll).ToList();
        return items.Count == 0 ? null : new PersistentList(items);
    }

    public static int Count(object? coll)
    {
        return coll switch
        {
            null => 0,
            PersistentList list => list.Count,
            PersistentVector vec => vec.Count,
            PersistentMap map => map.Count,
            PersistentSet set => set.Count,
            string s => s.Length,
            IEnumerable<object?> en => en.Count(),
            _ => 0
        };
    }

    public static object? Nth(object? coll, int index)
    {
        return coll switch
        {
            PersistentList list => index < list.Count ? list[index] : null,
            PersistentVector vec => index < vec.Count ? vec[index] : null,
            string s => index < s.Length ? s[index].ToString() : null,
            _ => null
        };
    }

    public static object? Get(object? coll, object? key, object? notFound = null)
    {
        return coll switch
        {
            PersistentMap map => map.TryGetValue(key!, out var val) ? val : notFound,
            PersistentVector vec when key is int i => i < vec.Count ? vec[i] : notFound,
            PersistentVector vec when key is long l => (int)l < vec.Count ? vec[(int)l] : notFound,
            PersistentSet set => set.Contains(key) ? key : notFound,
            _ => notFound
        };
    }

    public static PersistentMap Assoc(object? coll, params object?[] kvs)
    {
        var pairs = coll is PersistentMap map ? map.ToList() : new List<KeyValuePair<object, object?>>();
        for (int i = 0; i < kvs.Length; i += 2)
        {
            var key = kvs[i]!;
            var val = i + 1 < kvs.Length ? kvs[i + 1] : null;
            pairs.RemoveAll(p => Equals(p.Key, key));
            pairs.Add(new KeyValuePair<object, object?>(key, val));
        }
        return new PersistentMap(pairs);
    }

    #endregion

    #region Predicates

    public static bool IsNil(object? x) => x == null;
    public static bool IsSome(object? x) => x != null;
    public static bool IsSeq(object? x) => x is PersistentList;
    public static bool IsList(object? x) => x is PersistentList;
    public static bool IsVector(object? x) => x is PersistentVector;
    public static bool IsMap(object? x) => x is PersistentMap;
    public static bool IsSet(object? x) => x is PersistentSet;
    public static bool IsSymbol(object? x) => x is ReaderSymbol;
    public static bool IsKeyword(object? x) => x is ReaderKeyword;
    public static bool IsString(object? x) => x is string;
    public static bool IsNumber(object? x) => x is int or long or double or float;
    public static bool IsColl(object? x) => x is PersistentList or PersistentVector or PersistentMap or PersistentSet;

    public static bool IsEmpty(object? coll)
    {
        return coll switch
        {
            null => true,
            PersistentList list => list.Count == 0,
            PersistentVector vec => vec.Count == 0,
            PersistentMap map => map.Count == 0,
            PersistentSet set => set.Count == 0,
            string s => s.Length == 0,
            _ => true
        };
    }

    #endregion

    #region Equality

    public static new bool Equals(object? a, object? b) => DeepEquals(a, b);

    public static bool Not(object? x) => x is null or false;

    public static bool NotEquals(object? a, object? b) => !DeepEquals(a, b);

    private static bool DeepEquals(object? a, object? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null) return b is null;
        if (b is null) return false;

        // Handle numeric comparisons across types (int vs long)
        // This is critical for macros: (= 1 (count clauses)) where 1 is long but count returns int
        if (IsNumeric(a) && IsNumeric(b))
            return ToLong(a) == ToLong(b);

        if (a is PersistentList la && b is PersistentList lb)
            return la.Count == lb.Count && la.Zip(lb, DeepEquals).All(x => x);
        if (a is PersistentVector va && b is PersistentVector vb)
            return va.Count == vb.Count && va.Zip(vb, DeepEquals).All(x => x);
        if (a is ReaderSymbol sa && b is ReaderSymbol sb)
            return sa.Name == sb.Name && sa.Namespace == sb.Namespace;
        if (a is ReaderKeyword ka && b is ReaderKeyword kb)
            return ka.Name == kb.Name && ka.Namespace == kb.Namespace;

        return object.Equals(a, b);
    }

    private static bool IsNumeric(object? x) => x is int or long or double or float or short or byte;

    #endregion

    #region Symbols and Keywords

    public static ReaderSymbol Sym(string name) => ReaderSymbol.Parse(name);
    public static ReaderSymbol Sym(string? ns, string name) => new(ns, name);
    public static ReaderKeyword Kw(string name) => ReaderKeyword.Intern(name);
    public static ReaderKeyword Kw(string? ns, string name) => ReaderKeyword.Intern(ns, name);

    public static string? Name(object? x)
    {
        return x switch
        {
            ReaderSymbol s => s.Name,
            ReaderKeyword k => k.Name,
            string s => s,
            _ => x?.ToString()
        };
    }

    public static string? Namespace(object? x)
    {
        return x switch
        {
            ReaderSymbol s => s.Namespace,
            ReaderKeyword k => k.Namespace,
            _ => null
        };
    }

    #endregion

    #region String Operations

    public static string Str(params object?[] args)
        => string.Concat(args.Select(a => a?.ToString() ?? ""));

    public static string Subs(string s, int start)
        => s.Substring(start);

    public static string Subs(string s, int start, int end)
        => s.Substring(start, end - start);

    public static string StringJoin(string separator, object? coll)
        => string.Join(separator, ToEnumerable(coll).Select(x => x?.ToString() ?? ""));

    public static PersistentVector StringSplit(string s, string pattern)
        => new(Regex.Split(s, pattern).Cast<object?>());

    public static string StringReplace(string s, string match, string replacement)
        => s.Replace(match, replacement);

    public static string StringReplaceRegex(string s, string pattern, string replacement)
        => Regex.Replace(s, pattern, replacement);

    public static bool StringStartsWith(string s, string prefix)
        => s.StartsWith(prefix);

    public static bool StringEndsWith(string s, string suffix)
        => s.EndsWith(suffix);

    public static bool StringIncludes(string s, string sub)
        => s.Contains(sub);

    public static string ToUpper(string s) => s.ToUpperInvariant();
    public static string ToLower(string s) => s.ToLowerInvariant();
    public static string Trim(string s) => s.Trim();

    #endregion

    #region Regex Operations

    public static object? ReFind(string pattern, string s)
    {
        var match = Regex.Match(s, pattern);
        if (!match.Success) return null;

        if (match.Groups.Count == 1)
            return match.Value;

        // Return vector of groups
        var groups = new List<object?>();
        foreach (Group g in match.Groups)
            groups.Add(g.Value);
        return new PersistentVector(groups);
    }

    public static PersistentList ReSeq(string pattern, string s)
    {
        var matches = Regex.Matches(s, pattern);
        var results = new List<object?>();

        foreach (Match match in matches)
        {
            if (match.Groups.Count == 1)
                results.Add(match.Value);
            else
            {
                var groups = new List<object?>();
                foreach (Group g in match.Groups)
                    groups.Add(g.Value);
                results.Add(new PersistentVector(groups));
            }
        }

        return new PersistentList(results);
    }

    public static object? ReMatches(string pattern, string s)
    {
        var match = Regex.Match(s, $"^{pattern}$");
        if (!match.Success) return null;

        if (match.Groups.Count == 1)
            return match.Value;

        var groups = new List<object?>();
        foreach (Group g in match.Groups)
            groups.Add(g.Value);
        return new PersistentVector(groups);
    }

    #endregion

    #region Math Operations

    public static long Add(params object?[] args)
        => args.Aggregate(0L, (acc, x) => acc + ToLong(x));

    public static long Subtract(object? first, params object?[] rest)
    {
        if (rest.Length == 0) return -ToLong(first);
        return rest.Aggregate(ToLong(first), (acc, x) => acc - ToLong(x));
    }

    public static long Multiply(params object?[] args)
        => args.Aggregate(1L, (acc, x) => acc * ToLong(x));

    public static long Inc(object? x) => ToLong(x) + 1;
    public static long Dec(object? x) => ToLong(x) - 1;

    private static long ToLong(object? x)
    {
        return x switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            float f => (long)f,
            _ => 0
        };
    }

    #endregion

    #region Higher-Order Functions

    public static PersistentList Map(Func<object?, object?> f, object? coll)
    {
        var results = ToEnumerable(coll).Select(f).ToList();
        return new PersistentList(results);
    }

    public static PersistentList Filter(Func<object?, bool> pred, object? coll)
    {
        var results = ToEnumerable(coll).Where(pred).ToList();
        return new PersistentList(results);
    }

    public static object? Reduce(Func<object?, object?, object?> f, object? init, object? coll)
    {
        return ToEnumerable(coll).Aggregate(init, f);
    }

    public static object? Reduce(Func<object?, object?, object?> f, object? coll)
    {
        var items = ToEnumerable(coll).ToList();
        if (items.Count == 0) throw new MacroException("reduce of empty collection with no initial value");
        return items.Skip(1).Aggregate(items[0], f);
    }

    /// <summary>
    /// Map a function over a collection and concatenate the results.
    /// </summary>
    public static PersistentList Mapcat(Func<object?, object?> f, object? coll)
    {
        var result = new List<object?>();
        foreach (var item in ToEnumerable(coll))
        {
            var mapped = f(item);
            if (mapped != null)
                result.AddRange(ToEnumerable(mapped));
        }
        return new PersistentList(result);
    }

    /// <summary>
    /// Map a MacroLambda over a collection and concatenate the results.
    /// This overload handles lambdas created by (fn ...) in macro bodies.
    /// </summary>
    public static PersistentList Mapcat(MacroLambda f, object? coll)
    {
        var result = new List<object?>();
        foreach (var item in ToEnumerable(coll))
        {
            var mapped = f.Invoke([item]);
            if (mapped != null)
                result.AddRange(ToEnumerable(mapped));
        }
        return new PersistentList(result);
    }

    /// <summary>
    /// Partition a collection into groups of n elements.
    /// </summary>
    public static PersistentList Partition(int n, object? coll)
    {
        var items = ToEnumerable(coll).ToList();
        var result = new List<object?>();
        for (int i = 0; i + n <= items.Count; i += n)
        {
            // Must call ToList() to materialize the IEnumerable immediately
            // Otherwise closure issues can cause incorrect values
            result.Add(new PersistentList(items.Skip(i).Take(n).ToList()));
        }
        return new PersistentList(result);
    }

    /// <summary>
    /// Return all but the last element of a collection.
    /// </summary>
    public static PersistentList Butlast(object? coll)
    {
        var items = ToEnumerable(coll).ToList();
        if (items.Count <= 1) return PersistentList.Empty;
        return new PersistentList(items.Take(items.Count - 1));
    }

    /// <summary>
    /// Return the last element of a collection.
    /// </summary>
    public static object? Last(object? coll)
    {
        var items = ToEnumerable(coll).ToList();
        return items.Count > 0 ? items[^1] : null;
    }

    #endregion

    #region Numeric Predicates

    public static bool IsOdd(object? x) => ToLong(x) % 2 != 0;
    public static bool IsEven(object? x) => ToLong(x) % 2 == 0;
    public static bool IsZero(object? x) => ToLong(x) == 0;
    public static bool IsPos(object? x) => ToLong(x) > 0;
    public static bool IsNeg(object? x) => ToLong(x) < 0;

    #endregion

    #region Comparison

    public static bool Lt(object? a, object? b) => ToLong(a) < ToLong(b);
    public static bool Lte(object? a, object? b) => ToLong(a) <= ToLong(b);
    public static bool Gt(object? a, object? b) => ToLong(a) > ToLong(b);
    public static bool Gte(object? a, object? b) => ToLong(a) >= ToLong(b);

    #endregion

    #region Utilities

    public static object? Identity(object? x) => x;

    public static IEnumerable<object?> ToEnumerable(object? coll)
    {
        return coll switch
        {
            null => Enumerable.Empty<object?>(),
            PersistentList list => list,
            PersistentVector vec => vec,
            PersistentSet set => set,
            string s => s.Select(c => (object?)c.ToString()),
            IEnumerable<object?> en => en,
            _ => Enumerable.Empty<object?>()
        };
    }

    #endregion

    #region Symbol Generation

    private static int _gensymCounter = 0;

    /// <summary>
    /// Generate a unique symbol for use in macro expansion.
    /// </summary>
    public static ReaderSymbol Gensym()
    {
        return ReaderSymbol.Parse($"G__{_gensymCounter++}");
    }

    /// <summary>
    /// Generate a unique symbol with a custom prefix.
    /// </summary>
    public static ReaderSymbol Gensym(string prefix)
    {
        return ReaderSymbol.Parse($"{prefix}{_gensymCounter++}");
    }

    #endregion
}
