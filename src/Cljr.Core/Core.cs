using System.Collections;
using System.Text;
using Cljr.Collections;

namespace Cljr;

/// <summary>
/// Core runtime functions for Cljr - Clojure semantics in C#.
/// Single source of truth for both compile-time (macros) and runtime.
/// Named CoreFunctions to avoid conflict with Cljr.Runtime.Core.
/// </summary>
public static class CoreFunctions
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

    public static bool SeqEquals(IEnumerable a, IEnumerable b)
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

    public static bool SeqEquals(IEnumerable a, object? b)
    {
        if (b is null) return false;
        if (b is string) return false;
        if (b is not IEnumerable bEnum) return false;
        return SeqEquals(a, bEnum);
    }

    #endregion

    #region Truthiness

    /// <summary>
    /// Clojure truthiness: nil and false are falsy, everything else is truthy
    /// </summary>
    public static bool IsTruthy(object? x) => x is not null && x is not false;

    #endregion

    #region Sequence Operations

    public static object? First(object? coll)
    {
        if (coll is null) return null;
        if (coll is ISeq seq) return seq.First();
        if (coll is string s) return s.Length > 0 ? s[0].ToString() : null;
        if (coll is IEnumerable en)
        {
            var e = en.GetEnumerator();
            return e.MoveNext() ? e.Current : null;
        }
        return null;
    }

    public static object? Second(object? coll) => First(Rest(coll));

    public static ISeq Rest(object? coll)
    {
        if (coll is null) return PersistentList.Empty;
        if (coll is ISeq seq) return seq.More();
        if (coll is string s)
            return s.Length > 1 ? PersistentList.Create(s.Substring(1).Cast<object?>().ToArray()) : PersistentList.Empty;
        if (coll is IEnumerable en)
        {
            var items = new List<object?>();
            var e = en.GetEnumerator();
            if (e.MoveNext())
            {
                while (e.MoveNext())
                    items.Add(e.Current);
            }
            return items.Count > 0 ? PersistentList.Create(items) : PersistentList.Empty;
        }
        return PersistentList.Empty;
    }

    public static ISeq? Next(object? coll)
    {
        if (coll is null) return null;
        if (coll is ISeq seq) return seq.Next();
        var rest = Rest(coll);
        return rest is PersistentList pl && pl.Count == 0 ? null : rest;
    }

    public static ISeq Cons(object? x, object? coll)
    {
        if (coll is null) return PersistentList.Create(x);
        if (coll is ISeq seq) return new Cons(x, seq);
        return new Cons(x, Seq(coll));
    }

    public static ISeq? Seq(object? coll)
    {
        if (coll is null) return null;
        if (coll is ISeq seq) return seq.Seq();
        if (coll is IPersistentCollection pc) return pc.Seq();
        if (coll is string s)
            return s.Length > 0 ? PersistentList.Create(s.Cast<object?>().Select(c => c?.ToString()).ToArray()) : null;
        if (coll is IEnumerable en)
        {
            var items = en.Cast<object?>().ToArray();
            return items.Length > 0 ? PersistentList.Create(items) : null;
        }
        return null;
    }

    public static PersistentList List(params object?[] items) => PersistentList.Create(items);

    public static ISeq Concat(params object?[] colls)
    {
        var result = new List<object?>();
        foreach (var coll in colls)
        {
            if (coll != null)
            {
                if (coll is IEnumerable en)
                    foreach (var item in en)
                        result.Add(item);
            }
        }
        return result.Count > 0 ? PersistentList.Create(result) : PersistentList.Empty;
    }

    public static int Count(object? coll)
    {
        if (coll is null) return 0;
        if (coll is IPersistentCollection pc) return pc.Count;
        if (coll is string s) return s.Length;
        if (coll is ICollection col) return col.Count;
        if (coll is IEnumerable en) return en.Cast<object?>().Count();
        return 0;
    }

    public static object? Nth(object? coll, int index, object? notFound = null)
    {
        if (coll is null) return notFound;
        if (coll is Indexed idx) return idx.Nth(index, notFound);
        if (coll is IList list) return index >= 0 && index < list.Count ? list[index] : notFound;
        if (coll is string s) return index >= 0 && index < s.Length ? s[index].ToString() : notFound;
        if (coll is IEnumerable en)
        {
            int i = 0;
            foreach (var item in en)
            {
                if (i == index) return item;
                i++;
            }
        }
        return notFound;
    }

    public static object? Get(object? coll, object? key, object? notFound = null)
    {
        if (coll is null) return notFound;
        if (coll is ILookup lookup) return lookup.ValAt(key!, notFound);
        if (coll is IDictionary dict) return dict.Contains(key!) ? dict[key!] : notFound;
        if (key is int idx && coll is IList list) return idx >= 0 && idx < list.Count ? list[idx] : notFound;
        return notFound;
    }

    #endregion

    #region Predicates

    public static bool IsNil(object? x) => x is null;
    public static bool IsSome(object? x) => x is not null;
    public static bool IsSeq(object? x) => x is ISeq;
    public static bool IsList(object? x) => x is PersistentList;
    public static bool IsVector(object? x) => x is IPersistentVector;
    public static bool IsMap(object? x) => x is IPersistentMap;
    public static bool IsSet(object? x) => x is IPersistentSet;
    public static bool IsSymbol(object? x) => x is Symbol;
    public static bool IsKeyword(object? x) => x is Keyword;
    public static bool IsString(object? x) => x is string;
    public static bool IsNumber(object? x) => x is int or long or double or float or decimal;
    public static bool IsColl(object? x) => x is IPersistentCollection or IEnumerable and not string;

    public static bool IsEmpty(object? coll)
    {
        if (coll is null) return true;
        if (coll is IPersistentCollection pc) return pc.Count == 0;
        if (coll is string s) return s.Length == 0;
        if (coll is ICollection col) return col.Count == 0;
        return !((IEnumerable)coll).GetEnumerator().MoveNext();
    }

    public static bool Not(object? x) => x is null or false;

    public static bool NotEquals(object? a, object? b) => !Equals(a, b);

    #endregion

    #region Symbol and Keyword

    public static Symbol Sym(string name) => Symbol.Parse(name);
    public static Symbol Sym(string? ns, string name) => Symbol.Intern(ns, name);
    public static Keyword Kw(string name) => Keyword.Parse(name);
    public static Keyword Kw(string? ns, string name) => Keyword.Intern(ns, name);

    public static string? Name(object? x)
    {
        return x switch
        {
            Symbol s => s.Name,
            Keyword k => k.Name,
            string str => str,
            _ => x?.ToString()
        };
    }

    public static string? Namespace(object? x)
    {
        return x switch
        {
            Symbol s => s.Namespace,
            Keyword k => k.Namespace,
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

    #endregion

    #region Math Operations

    public static object Add(params object?[] args)
    {
        if (args.Length == 0) return 0L;
        bool hasDouble = args.Any(a => a is double or float);
        if (hasDouble)
            return args.Aggregate(0.0, (acc, x) => acc + ToDouble(x));
        return args.Aggregate(0L, (acc, x) => acc + ToLong(x));
    }

    public static object Subtract(object? first, params object?[] rest)
    {
        if (rest.Length == 0)
        {
            if (first is double d) return -d;
            return -ToLong(first);
        }
        bool hasDouble = first is double or float || rest.Any(a => a is double or float);
        if (hasDouble)
            return rest.Aggregate(ToDouble(first), (acc, x) => acc - ToDouble(x));
        return rest.Aggregate(ToLong(first), (acc, x) => acc - ToLong(x));
    }

    public static object Multiply(params object?[] args)
    {
        if (args.Length == 0) return 1L;
        bool hasDouble = args.Any(a => a is double or float);
        if (hasDouble)
            return args.Aggregate(1.0, (acc, x) => acc * ToDouble(x));
        return args.Aggregate(1L, (acc, x) => acc * ToLong(x));
    }

    public static object Divide(object? first, params object?[] rest)
    {
        if (rest.Length == 0)
            return 1.0 / ToDouble(first);
        return rest.Aggregate(ToDouble(first), (acc, x) => acc / ToDouble(x));
    }

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

    private static double ToDouble(object? x)
    {
        return x switch
        {
            int i => i,
            long l => l,
            double d => d,
            float f => f,
            _ => 0.0
        };
    }

    #endregion

    #region Print

    /// <summary>
    /// Print to readable string representation (pr-str)
    /// </summary>
    public static string PrStr(object? x)
    {
        if (x is null) return "nil";

        return x switch
        {
            string s => $"\"{EscapeString(s)}\"",
            char c => $"\\{c}",
            bool b => b ? "true" : "false",
            Symbol sym => sym.ToString(),
            Keyword kw => kw.ToString(),
            PersistentList list => PrintSeq(list, "(", ")"),
            IPersistentVector vec => PrintSeq(vec, "[", "]"),
            IPersistentMap map => PrintMap(map),
            IPersistentSet set => PrintSet(set),
            ISeq seq => PrintSeq(seq, "(", ")"),
            IEnumerable en => PrintEnumerable(en),
            _ => x.ToString() ?? ""
        };
    }

    private static string PrintSeq(IEnumerable seq, string open, string close)
    {
        var sb = new StringBuilder();
        sb.Append(open);
        bool first = true;
        foreach (var item in seq)
        {
            if (!first) sb.Append(' ');
            sb.Append(PrStr(item));
            first = false;
        }
        sb.Append(close);
        return sb.ToString();
    }

    private static string PrintMap(IPersistentMap map)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        bool first = true;
        var seq = map.Seq();
        while (seq != null)
        {
            if (!first) sb.Append(", ");
            var entry = (IMapEntry)seq.First()!;
            sb.Append(PrStr(entry.Key()));
            sb.Append(' ');
            sb.Append(PrStr(entry.Val()));
            first = false;
            seq = seq.Next();
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string PrintSet(IPersistentSet set)
    {
        var sb = new StringBuilder();
        sb.Append("#{");
        bool first = true;
        var seq = set.Seq();
        while (seq != null)
        {
            if (!first) sb.Append(' ');
            sb.Append(PrStr(seq.First()));
            first = false;
            seq = seq.Next();
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string PrintEnumerable(IEnumerable en)
    {
        var sb = new StringBuilder();
        sb.Append('(');
        bool first = true;
        foreach (var item in en)
        {
            if (!first) sb.Append(' ');
            sb.Append(PrStr(item));
            first = false;
        }
        sb.Append(')');
        return sb.ToString();
    }

    private static string EscapeString(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
    }

    #endregion

    #region Identity

    public static object? Identity(object? x) => x;

    #endregion

    #region Apply

    /// <summary>
    /// Applies a function to arguments. Last arg should be a sequence.
    /// </summary>
    public static object? apply(object? f, params object?[] args)
    {
        if (f is null) return null;

        // Collect all arguments - last arg should be a sequence
        var allArgs = new List<object?>();
        for (int i = 0; i < args.Length - 1; i++)
            allArgs.Add(args[i]);

        if (args.Length > 0)
        {
            var lastArg = args[args.Length - 1];
            if (lastArg is IEnumerable seq && !(lastArg is string))
                foreach (var item in seq) allArgs.Add(item);
            else
                allArgs.Add(lastArg);
        }

        return f switch
        {
            Func<object?> f0 when allArgs.Count == 0 => f0(),
            Func<object?, object?> f1 when allArgs.Count == 1 => f1(allArgs[0]),
            Func<object?, object?, object?> f2 when allArgs.Count == 2 => f2(allArgs[0], allArgs[1]),
            Func<object?, object?, object?> f2 when allArgs.Count > 2 => allArgs.Skip(1).Aggregate(allArgs[0], (acc, x) => f2(acc, x)),
            Func<object?, object?, object?, object?> f3 when allArgs.Count == 3 => f3(allArgs[0], allArgs[1], allArgs[2]),
            Func<object?[], object?> paramsFunc => paramsFunc(allArgs.ToArray()),
            Delegate d => InvokeDelegate(d, allArgs),
            _ => throw new ArgumentException($"Cannot apply {f?.GetType().Name}")
        };
    }

    private static object? InvokeDelegate(Delegate d, List<object?> args)
    {
        var method = d.Method;
        var parameters = method.GetParameters();

        // Check if last parameter is params array
        if (parameters.Length == 1 && parameters[0].GetCustomAttributes(typeof(ParamArrayAttribute), false).Length > 0)
        {
            return d.DynamicInvoke(new object?[] { args.ToArray() });
        }

        return d.DynamicInvoke(args.ToArray());
    }

    #endregion
}
