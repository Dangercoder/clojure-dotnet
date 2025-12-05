using Cljr.Compiler.Reader;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;

namespace Cljr.Compiler.Macros;

/// <summary>
/// Expands macros in Clojure forms before analysis.
/// Handles syntax-quote, user-defined macros, and built-in macros.
///
/// Uses a pure interpreter (MacroInterpreter) to evaluate macro bodies.
/// This works in both REPL and source generator contexts because
/// no Roslyn compilation or Assembly.Load is required.
/// </summary>
public class MacroExpander
{
    private readonly Dictionary<string, IMacro> _macros = new();
    private readonly Dictionary<string, MacroDefinition> _userMacros = new();
    private readonly MacroInterpreter _interpreter = new();
    private int _gensymCounter = 0;

    public MacroExpander()
    {
        // Register built-in macros
        // Note: Most built-in macros are still handled inline in the analyzer
        // for simplicity. This expander focuses on syntax-quote and user macros.

        // Register future macro - wraps body in a lambda for background execution
        RegisterMacro(new FutureMacro());

        // Register time macro - measures execution time
        RegisterMacro(new TimeMacro());
    }

    /// <summary>
    /// Constructor for backwards compatibility - MacroContext is no longer needed
    /// </summary>
    public MacroExpander(MacroContext context) : this()
    {
    }

    /// <summary>
    /// Register a built-in macro
    /// </summary>
    public void RegisterMacro(IMacro macro)
    {
        _macros[macro.Name] = macro;
    }

    /// <summary>
    /// Register a user-defined macro from a defmacro form
    /// </summary>
    public void RegisterUserMacro(string name, MacroDefinition definition)
    {
        _userMacros[name] = definition;
    }

    /// <summary>
    /// Check if a symbol names a macro
    /// </summary>
    public bool IsMacro(string name)
    {
        return _macros.ContainsKey(name) || _userMacros.ContainsKey(name);
    }

    /// <summary>
    /// Expand all macros in a form recursively
    /// </summary>
    public object? Expand(object? form)
    {
        // Handle syntax-quote specially
        // Check for both Cljr.Compiler.Reader.Symbol and the runtime Cljr.Symbol for compatibility
        if (form is PersistentList list && list.Count > 0 && IsSyntaxQuoteSymbol(list[0]))
        {
            if (list.Count != 2)
                throw new MacroException("syntax-quote takes exactly one argument");
            return ExpandSyntaxQuote(list[1]);
        }

        // Handle defmacro
        if (form is PersistentList defmacro && defmacro.Count >= 4 &&
            IsDefmacroSymbol(defmacro[0]))
        {
            return ProcessDefmacro(defmacro);
        }

        // Not a macro call - recursively expand children
        return form switch
        {
            PersistentList l => ExpandList(l),
            PersistentVector v => ExpandVector(v),
            PersistentMap m => ExpandMap(m),
            PersistentSet s => ExpandSet(s),
            _ => form
        };
    }

    /// <summary>
    /// Check if an object is a symbol with name "syntax-quote".
    /// Handles both Cljr.Compiler.Reader.Symbol and Cljr.Symbol for compatibility.
    /// </summary>
    private static bool IsSyntaxQuoteSymbol(object? obj)
    {
        if (obj is ReaderSymbol sym)
            return sym.Name == "syntax-quote";
#if !NETSTANDARD2_0
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Name == "syntax-quote";
#endif
        return false;
    }

    /// <summary>
    /// Check if an object is a symbol with name "defmacro".
    /// Handles both Cljr.Compiler.Reader.Symbol and Cljr.Symbol for compatibility.
    /// </summary>
    private static bool IsDefmacroSymbol(object? obj)
    {
        if (obj is ReaderSymbol sym)
            return sym.Name == "defmacro";
#if !NETSTANDARD2_0
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Name == "defmacro";
#endif
        return false;
    }

    /// <summary>
    /// Get the name of a symbol, regardless of which Symbol type it is.
    /// Returns null if not a symbol.
    /// </summary>
    private static string? GetSymbolName(object? obj)
    {
        if (obj is null) return null;
        if (obj is ReaderSymbol sym)
            return sym.Name;
#if !NETSTANDARD2_0
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Name;
#endif
        // Fallback: use reflection for assembly loading issues
        var type = obj.GetType();
        var typeName = type.FullName;
        if (typeName == "Cljr.Compiler.Reader.Symbol" || typeName == "Cljr.Symbol")
        {
            var nameProp = type.GetProperty("Name");
            return nameProp?.GetValue(obj) as string;
        }
        return null;
    }

    /// <summary>
    /// Get the namespace of a symbol, regardless of which Symbol type it is.
    /// Returns null if not a symbol or has no namespace.
    /// </summary>
    private static string? GetSymbolNamespace(object? obj)
    {
        if (obj is ReaderSymbol sym)
            return sym.Namespace;
#if !NETSTANDARD2_0
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Namespace;
#endif
        return null;
    }

    /// <summary>
    /// Expand a single macro call (one level)
    /// </summary>
    public object? MacroexpandOne(object? form)
    {
        if (form is not PersistentList list || list.Count == 0)
            return form;

        var head = list[0];
        var symName = GetSymbolName(head);
        if (symName is null)
            return form;

        // Only expand unqualified symbols - qualified symbols like cljr.core/future
        // should NOT be treated as macro calls (they're the result of macro expansion)
        var symNs = GetSymbolNamespace(head);
        if (symNs is not null)
            return form;

        // Check user macros first
        if (_userMacros.TryGetValue(symName, out var userMacro))
        {
            return ExpandUserMacro(userMacro, list.Skip(1).ToList());
        }

        // Check built-in macros
        if (_macros.TryGetValue(symName, out var macro))
        {
            return macro.Expand(list.Skip(1).ToList());
        }

        return form;
    }

    /// <summary>
    /// Expand macros fully (until no more expansions occur)
    /// </summary>
    public object? Macroexpand(object? form)
    {
        var current = form;
        while (true)
        {
            var expanded = MacroexpandOne(current);
            if (ReferenceEquals(expanded, current))
                return current;
            current = expanded;
        }
    }

    #region Syntax-Quote Expansion

    /// <summary>
    /// Expand syntax-quote form: `(foo ~bar ~@baz)
    /// Produces code that constructs the data structure at runtime.
    /// </summary>
    private object? ExpandSyntaxQuote(object? form)
    {
        return SyntaxQuote(form);
    }

    private object? SyntaxQuote(object? form)
    {
        // Handle unquote: ~x -> x
        if (IsUnquote(form))
        {
            return ((PersistentList)form!)[1];
        }

        // Handle unquote-splicing at top level (error)
        if (IsUnquoteSplicing(form))
        {
            throw new MacroException("unquote-splicing not in list context");
        }

        // Handle list: recursively process with splicing support
        if (form is PersistentList list)
        {
            if (list.Count == 0)
                return new PersistentList([ReaderSymbol.Parse("list")]);

            // Check for nested syntax-quote - preserve it as-is for later evaluation
            if (IsSyntaxQuote(list))
            {
                // Quote the entire syntax-quote form so it's preserved
                // (syntax-quote X) becomes (list 'syntax-quote <processed X>)
                var innerTemplate = list[1];
                return new PersistentList([
                    ReaderSymbol.Parse("list"),
                    new PersistentList([ReaderSymbol.Parse("quote"), ReaderSymbol.Parse("syntax-quote")]),
                    SyntaxQuote(innerTemplate)
                ]);
            }

            return SyntaxQuoteList(list);
        }

        // Handle vector: similar to list
        if (form is PersistentVector vec)
        {
            return SyntaxQuoteVector(vec);
        }

        // Handle map
        if (form is PersistentMap map)
        {
            return SyntaxQuoteMap(map);
        }

        // Handle set
        if (form is PersistentSet set)
        {
            return SyntaxQuoteSet(set);
        }

        // Handle symbol: may need namespace qualification
        if (form is ReaderSymbol sym)
        {
            return SyntaxQuoteSymbol(sym);
        }

        // Literals are self-evaluating
        return new PersistentList([ReaderSymbol.Parse("quote"), form]);
    }

    private object? SyntaxQuoteList(PersistentList list)
    {
        // Build a concat of list/list* calls
        var parts = new List<object?>();

        foreach (var item in list)
        {
            if (IsUnquoteSplicing(item))
            {
                // ~@x -> x (the value itself, will be spliced)
                parts.Add(((PersistentList)item!)[1]);
            }
            else
            {
                // Wrap in (list ...) for concat
                parts.Add(new PersistentList([
                    ReaderSymbol.Parse("list"),
                    SyntaxQuote(item)
                ]));
            }
        }

        if (parts.Count == 0)
            return new PersistentList([ReaderSymbol.Parse("list")]);

        if (parts.Count == 1)
            return parts[0];

        // (concat part1 part2 ...)
        var concatArgs = new List<object?> { ReaderSymbol.Parse("concat") };
        concatArgs.AddRange(parts);
        return new PersistentList(concatArgs);
    }

    private object? SyntaxQuoteVector(PersistentVector vec)
    {
        if (vec.Count == 0)
            return new PersistentList([ReaderSymbol.Parse("vector")]);

        // Similar to list, but wrap result in vec
        var parts = new List<object?>();

        foreach (var item in vec)
        {
            if (IsUnquoteSplicing(item))
            {
                parts.Add(((PersistentList)item!)[1]);
            }
            else
            {
                parts.Add(new PersistentList([
                    ReaderSymbol.Parse("list"),
                    SyntaxQuote(item)
                ]));
            }
        }

        object? inner;
        if (parts.Count == 1)
            inner = parts[0];
        else
        {
            var concatArgs = new List<object?> { ReaderSymbol.Parse("concat") };
            concatArgs.AddRange(parts);
            inner = new PersistentList(concatArgs);
        }

        return new PersistentList([ReaderSymbol.Parse("vec"), inner]);
    }

    private object? SyntaxQuoteMap(PersistentMap map)
    {
        if (map.Count == 0)
            return new PersistentList([ReaderSymbol.Parse("hash-map")]);

        var args = new List<object?> { ReaderSymbol.Parse("hash-map") };
        foreach (var kv in map)
        {
            args.Add(SyntaxQuote(kv.Key));
            args.Add(SyntaxQuote(kv.Value));
        }

        return new PersistentList(args);
    }

    private object? SyntaxQuoteSet(PersistentSet set)
    {
        if (set.Count == 0)
            return new PersistentList([ReaderSymbol.Parse("hash-set")]);

        var args = new List<object?> { ReaderSymbol.Parse("hash-set") };
        foreach (var item in set)
        {
            args.Add(SyntaxQuote(item));
        }

        return new PersistentList(args);
    }

    private object? SyntaxQuoteSymbol(ReaderSymbol sym)
    {
        // Handle auto-gensym: foo# -> foo__1234__auto__
        if (sym.Name.EndsWith("#", StringComparison.Ordinal))
        {
            var baseName = sym.Name[..^1];
            var gensym = ReaderSymbol.Parse($"{baseName}__{_gensymCounter++}__auto__");
            return new PersistentList([ReaderSymbol.Parse("quote"), gensym]);
        }

        // Regular symbol - quote it
        return new PersistentList([ReaderSymbol.Parse("quote"), sym]);
    }

    private static bool IsUnquote(object? form)
    {
        return form is PersistentList list &&
               list.Count == 2 &&
               list[0] is ReaderSymbol { Name: "unquote" };
    }

    private static bool IsUnquoteSplicing(object? form)
    {
        return form is PersistentList list &&
               list.Count == 2 &&
               list[0] is ReaderSymbol { Name: "unquote-splicing" };
    }

    private static bool IsSyntaxQuote(object? form)
    {
        return form is PersistentList list &&
               list.Count == 2 &&
               list[0] is ReaderSymbol { Name: "syntax-quote" };
    }

    #endregion

    #region User Macro Support

    private object? ProcessDefmacro(PersistentList list)
    {
        // (defmacro name [params] body...)
        // (defmacro name "doc" [params] body...)
        var name = GetSymbolName(list[1])
            ?? throw new MacroException("defmacro name must be a symbol");

        int idx = 2;
        string? docstring = null;

        if (list[idx] is string doc)
        {
            docstring = doc;
            idx++;
        }

        if (list[idx] is not PersistentVector paramsVec)
            throw new MacroException("defmacro requires a params vector");

        var parameters = paramsVec
            .Select(p => GetSymbolName(p))
            .Where(n => n != null)
            .Cast<string>()
            .ToList();
        string? restParam = null;

        // Check for & rest
        var ampIdx = parameters.IndexOf("&");
        if (ampIdx >= 0)
        {
            if (ampIdx + 1 < parameters.Count)
            {
                restParam = parameters[ampIdx + 1];
                parameters = parameters.Take(ampIdx).ToList();
            }
        }

        var bodyForms = list.Skip(idx + 1).ToList();

        var definition = new MacroDefinition(name, parameters, restParam, bodyForms, docstring);
        _userMacros[name] = definition;

        // Return nil - defmacro doesn't produce runtime code
        return null;
    }

    private object? ExpandUserMacro(MacroDefinition macro, List<object?> args)
    {
        // First, expand syntax-quote in the body forms
        var expandedBody = macro.Body.Select(Expand).ToList();

        // Build environment with parameter bindings
        var env = new MacroEnv();
        for (int i = 0; i < macro.Parameters.Count; i++)
        {
            env.Bind(macro.Parameters[i], i < args.Count ? args[i] : null);
        }

        // Handle rest parameter
        if (macro.RestParam != null)
        {
            var rest = args.Count > macro.Parameters.Count
                ? new PersistentList(args.Skip(macro.Parameters.Count))
                : PersistentList.Empty;
            env.Bind(macro.RestParam, rest);
        }

        // Evaluate body forms using the interpreter, return last result
        object? result = null;
        foreach (var bodyForm in expandedBody)
        {
            result = _interpreter.Eval(bodyForm, env);
        }

        return result;
    }

    #endregion

    #region Recursive Expansion

    private object? ExpandList(PersistentList list)
    {
        if (list.Count == 0)
            return list;

        // First, try macro expansion at this level
        var head = list[0];
        var symName = GetSymbolName(head);
        if (symName is not null)
        {
            var symNs = GetSymbolNamespace(head);
            // Only expand unqualified symbols - qualified symbols like cljr.core/future
            // should NOT be treated as macro calls (they're the result of macro expansion)
            if (symNs is null)
            {
                // Check if it's a macro call
                if (_userMacros.TryGetValue(symName, out var userMacro))
                {
                    var expanded = ExpandUserMacro(userMacro, list.Skip(1).ToList());
                    return Expand(expanded); // Recursively expand the result
                }

                if (_macros.TryGetValue(symName, out var macro))
                {
                    var expanded = macro.Expand(list.Skip(1).ToList());
                    return Expand(expanded);
                }
            }

            // Handle syntax-quote
            if (symName == "syntax-quote")
            {
                if (list.Count != 2)
                    throw new MacroException("syntax-quote takes exactly one argument");
                return ExpandSyntaxQuote(list[1]);
            }

            // Handle defmacro
            if (symName == "defmacro")
            {
                return ProcessDefmacro(list);
            }
        }

        // Not a macro - expand children
        var expandedItems = list.Select(Expand).ToList();
        return new PersistentList(expandedItems);
    }

    private object? ExpandVector(PersistentVector vec)
    {
        var expanded = vec.Select(Expand).ToList();
        return new PersistentVector(expanded);
    }

    private object? ExpandMap(PersistentMap map)
    {
        var pairs = map.Select(kv =>
            new KeyValuePair<object, object?>(Expand(kv.Key)!, Expand(kv.Value)));
        return new PersistentMap(pairs);
    }

    private object? ExpandSet(PersistentSet set)
    {
        var expanded = set.Select(Expand).ToList();
        return new PersistentSet(expanded);
    }

    #endregion

    /// <summary>
    /// Generate a unique symbol for use in macro expansion
    /// </summary>
    public ReaderSymbol Gensym(string prefix = "G__")
    {
        return ReaderSymbol.Parse($"{prefix}{_gensymCounter++}");
    }
}

/// <summary>
/// Represents a user-defined macro
/// </summary>
public record MacroDefinition(
    string Name,
    List<string> Parameters,
    string? RestParam,
    List<object?> Body,
    string? DocString
);

/// <summary>
/// Exception thrown during macro expansion
/// </summary>
public class MacroException : Exception
{
    public MacroException(string message) : base(message) { }
}

/// <summary>
/// Built-in macro for future - runs body on a background thread.
/// Transforms (future body...) into (Cljr.Core/future (fn [] (do body...)))
/// </summary>
public class FutureMacro : IMacro
{
    public string Name => "future";

    public object? Expand(IReadOnlyList<object?> args)
    {
        if (args.Count == 0)
            throw new MacroException("future requires at least one body form");

        // Build the body: if single form, use it directly; otherwise wrap in do
        object? body;
        if (args.Count == 1)
        {
            body = args[0];
        }
        else
        {
            var doForms = new List<object?> { ReaderSymbol.Parse("do") };
            doForms.AddRange(args);
            body = new PersistentList(doForms);
        }

        // Create (fn [] body)
        var fnForm = new PersistentList(new object?[]
        {
            ReaderSymbol.Parse("fn"),
            PersistentVector.Empty,
            body
        });

        // Create (cljr.core/future fn-form)
        return new PersistentList(new object?[]
        {
            ReaderSymbol.Parse("cljr.core/future"),
            fnForm
        });
    }
}

/// <summary>
/// Built-in macro for time - measures and prints execution time.
/// Transforms (time expr) into a let binding that uses System.Diagnostics.Stopwatch.
/// </summary>
public class TimeMacro : IMacro
{
    private static int _counter = 0;

    public string Name => "time";

    public object? Expand(IReadOnlyList<object?> args)
    {
        if (args.Count != 1)
            throw new MacroException("time requires exactly one expression");

        var expr = args[0];
        var id = _counter++;

        // Generate unique symbols
        var startSym = ReaderSymbol.Parse($"start__{id}__auto__");
        var retSym = ReaderSymbol.Parse($"ret__{id}__auto__");
        var elapsedSym = ReaderSymbol.Parse($"elapsed__{id}__auto__");

        // Build: (let [start (System.Diagnostics.Stopwatch/StartNew)
        //              ret expr
        //              elapsed (.TotalMilliseconds (.-Elapsed start))]
        //          (println (str "Elapsed time: " elapsed " msecs"))
        //          ret)
        var elapsedTimeSpan = new PersistentList(new object?[] { ReaderSymbol.Parse(".-Elapsed"), startSym });
        var totalMs = new PersistentList(new object?[] { ReaderSymbol.Parse(".-TotalMilliseconds"), elapsedTimeSpan });

        var bindings = new PersistentVector(new object?[]
        {
            startSym, new PersistentList(new object?[] { ReaderSymbol.Parse("System.Diagnostics.Stopwatch/StartNew") }),
            retSym, expr,
            elapsedSym, totalMs
        });

        var printlnCall = new PersistentList(new object?[]
        {
            ReaderSymbol.Parse("println"),
            new PersistentList(new object?[]
            {
                ReaderSymbol.Parse("str"),
                "Elapsed time: ",
                elapsedSym,
                " msecs"
            })
        });

        return new PersistentList(new object?[]
        {
            ReaderSymbol.Parse("let"),
            bindings,
            printlnCall,
            retSym
        });
    }
}
