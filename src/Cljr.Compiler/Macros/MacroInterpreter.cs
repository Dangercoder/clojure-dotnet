using System.Reflection;
using Cljr.Compiler.Reader;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;
using ReaderKeyword = Cljr.Compiler.Reader.Keyword;

namespace Cljr.Compiler.Macros;

/// <summary>
/// Pure tree-walking interpreter for macro bodies.
/// Evaluates macro code using MacroRuntime functions.
///
/// This works in source generators because:
/// - No Roslyn compilation
/// - No Assembly.Load
/// - Just in-memory AST manipulation
/// </summary>
public class MacroInterpreter
{
    // Registry mapping Clojure function names to MacroRuntime methods
    private static readonly Dictionary<string, MethodInfo[]> _builtins = BuildBuiltinRegistry();

    private static Dictionary<string, MethodInfo[]> BuildBuiltinRegistry()
    {
        var registry = new Dictionary<string, MethodInfo[]>();

        foreach (var method in typeof(MacroRuntime).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (method.IsSpecialName) continue;

            var cljName = ToCljName(method.Name);

            if (!registry.TryGetValue(cljName, out var methods))
            {
                methods = [];
                registry[cljName] = methods;
            }

            // Add this overload
            registry[cljName] = [.. methods, method];
        }

        // Add operator aliases
        if (registry.TryGetValue("add", out var addMethods))
            registry["+"] = addMethods;
        if (registry.TryGetValue("subtract", out var subMethods))
            registry["-"] = subMethods;
        if (registry.TryGetValue("multiply", out var mulMethods))
            registry["*"] = mulMethods;
        if (registry.TryGetValue("equals", out var eqMethods))
            registry["="] = eqMethods;
        if (registry.TryGetValue("not-equals", out var neqMethods))
            registry["not="] = neqMethods;

        // Comparison operator aliases
        if (registry.TryGetValue("lt", out var ltMethods))
            registry["<"] = ltMethods;
        if (registry.TryGetValue("lte", out var lteMethods))
            registry["<="] = lteMethods;
        if (registry.TryGetValue("gt", out var gtMethods))
            registry[">"] = gtMethods;
        if (registry.TryGetValue("gte", out var gteMethods))
            registry[">="] = gteMethods;

        return registry;
    }

    /// <summary>
    /// Convert C# method name to Clojure function name.
    /// IsNil -> nil?, HashMap -> hash-map, First -> first
    /// </summary>
    private static string ToCljName(string csName)
    {
        // Is* predicates -> *?
        if (csName.StartsWith("Is", StringComparison.Ordinal) && csName.Length > 2)
        {
            return ToKebabCase(csName.Substring(2)) + "?";
        }

        return ToKebabCase(csName);
    }

    private static string ToKebabCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c) && i > 0)
            {
                result.Append('-');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Evaluate a form in the given environment.
    /// </summary>
    public object? Eval(object? form, MacroEnv env)
    {
        return form switch
        {
            // Self-evaluating forms
            null => null,
            bool b => b,
            int i => i,
            long l => l,
            double d => d,
            string s => s,

            // Symbols: look up in env, return symbol if not found
            ReaderSymbol sym => EvalSymbol(sym, env),

            // Keywords are self-evaluating
            ReaderKeyword kw => kw,

            // Lists: function calls or special forms
            PersistentList list => EvalList(list, env),

            // Vectors: evaluate each element
            PersistentVector vec => EvalVector(vec, env),

            // Maps: evaluate keys and values
            PersistentMap map => EvalMap(map, env),

            // Sets: evaluate each element
            PersistentSet set => EvalSet(set, env),

            // Lambda values (from fn special form)
            MacroLambda lambda => lambda,

            // Anything else passes through
            _ => form
        };
    }

    private object? EvalSymbol(ReaderSymbol sym, MacroEnv env)
    {
        // Check environment first
        var value = env.Lookup(sym.Name);
        if (value != MacroEnv.NotFound)
            return value;

        // Symbol not in env - return it as a literal symbol
        return sym;
    }

    private object? EvalList(PersistentList list, MacroEnv env)
    {
        if (list.Count == 0)
            return list;

        var head = list[0];
        var symName = GetSymbolName(head);

        // Check for special forms
        if (symName != null)
        {
            switch (symName)
            {
                case "quote":
                    if (list.Count != 2)
                        throw new MacroException("quote takes exactly one argument");
                    return list[1];

                case "if":
                    return EvalIf(list, env);

                case "let":
                    return EvalLet(list, env);

                case "do":
                    return EvalDo(list, env);

                case "fn":
                    return EvalFn(list, env);

                case "recur":
                    return EvalRecur(list, env);

                case "syntax-quote":
                    if (list.Count != 2)
                        throw new MacroException("syntax-quote takes exactly one argument");
                    return EvalSyntaxQuote(list[1], env);
            }
        }

        // Regular function call
        return EvalCall(list, env);
    }

    /// <summary>
    /// Evaluate syntax-quote (backtick) form.
    /// This expands the template, evaluating unquote (~) and unquote-splicing (~@) forms.
    /// </summary>
    private object? EvalSyntaxQuote(object? form, MacroEnv env)
    {
        // Handle unquote: ~x -> evaluate x
        if (IsUnquote(form))
        {
            var inner = ((PersistentList)form!)[1];
            return Eval(inner, env);
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
                return PersistentList.Empty;

            return EvalSyntaxQuoteList(list, env);
        }

        // Handle vector
        if (form is PersistentVector vec)
        {
            return EvalSyntaxQuoteVector(vec, env);
        }

        // Handle map
        if (form is PersistentMap map)
        {
            return EvalSyntaxQuoteMap(map, env);
        }

        // Symbols are quoted (returned as-is)
        if (form is ReaderSymbol)
            return form;

        // Keywords and literals pass through
        return form;
    }

    private object? EvalSyntaxQuoteList(PersistentList list, MacroEnv env)
    {
        var result = new List<object?>();

        foreach (var item in list)
        {
            if (IsUnquoteSplicing(item))
            {
                // ~@x -> evaluate x and splice results
                var inner = ((PersistentList)item!)[1];
                var spliced = Eval(inner, env);
                if (spliced != null)
                {
                    foreach (var splicedItem in MacroRuntime.ToEnumerable(spliced))
                    {
                        result.Add(splicedItem);
                    }
                }
            }
            else
            {
                // Recursively process the item
                result.Add(EvalSyntaxQuote(item, env));
            }
        }

        return new PersistentList(result);
    }

    private PersistentVector EvalSyntaxQuoteVector(PersistentVector vec, MacroEnv env)
    {
        var result = new List<object?>();

        foreach (var item in vec)
        {
            if (IsUnquoteSplicing(item))
            {
                var inner = ((PersistentList)item!)[1];
                var spliced = Eval(inner, env);
                if (spliced != null)
                {
                    foreach (var splicedItem in MacroRuntime.ToEnumerable(spliced))
                    {
                        result.Add(splicedItem);
                    }
                }
            }
            else
            {
                result.Add(EvalSyntaxQuote(item, env));
            }
        }

        return new PersistentVector(result);
    }

    private PersistentMap EvalSyntaxQuoteMap(PersistentMap map, MacroEnv env)
    {
        var pairs = new List<KeyValuePair<object, object?>>();

        foreach (var kv in map)
        {
            var key = EvalSyntaxQuote(kv.Key, env);
            var value = EvalSyntaxQuote(kv.Value, env);
            pairs.Add(new KeyValuePair<object, object?>(key!, value));
        }

        return new PersistentMap(pairs);
    }

    private static bool IsUnquote(object? form)
    {
        if (form is PersistentList list && list.Count == 2)
        {
            var head = list[0];
            if (head is ReaderSymbol sym && sym.Name == "unquote")
                return true;
        }
        return false;
    }

    private static bool IsUnquoteSplicing(object? form)
    {
        if (form is PersistentList list && list.Count == 2)
        {
            var head = list[0];
            if (head is ReaderSymbol sym && sym.Name == "unquote-splicing")
                return true;
        }
        return false;
    }

    private object? EvalIf(PersistentList list, MacroEnv env)
    {
        if (list.Count < 3)
            throw new MacroException("if requires at least 2 arguments (test, then)");

        var test = Eval(list[1], env);

        // In Clojure, nil and false are falsy, everything else is truthy
        var isTruthy = test is not null and not false;

        if (isTruthy)
            return Eval(list[2], env);
        else if (list.Count > 3)
            return Eval(list[3], env);
        else
            return null;
    }

    private object? EvalLet(PersistentList list, MacroEnv env)
    {
        if (list.Count < 2)
            throw new MacroException("let requires bindings vector");

        if (list[1] is not PersistentVector bindings)
            throw new MacroException("let bindings must be a vector");

        if (bindings.Count % 2 != 0)
            throw new MacroException("let bindings must have even number of forms");

        // Create new environment scope
        var letEnv = env.Extend();

        // Process bindings
        for (int i = 0; i < bindings.Count; i += 2)
        {
            var bindName = GetSymbolName(bindings[i])
                ?? throw new MacroException($"let binding name must be a symbol, got {bindings[i]}");
            var bindValue = Eval(bindings[i + 1], letEnv);
            letEnv.Bind(bindName, bindValue);
        }

        // Evaluate body forms, return last
        object? result = null;
        for (int i = 2; i < list.Count; i++)
        {
            result = Eval(list[i], letEnv);
        }

        return result;
    }

    private object? EvalDo(PersistentList list, MacroEnv env)
    {
        object? result = null;
        for (int i = 1; i < list.Count; i++)
        {
            result = Eval(list[i], env);
        }
        return result;
    }

    private MacroLambda EvalFn(PersistentList list, MacroEnv env)
    {
        // (fn [params] body...)
        // (fn name [params] body...)

        int paramsIdx = 1;
        string? fnName = null;

        if (list[1] is ReaderSymbol nameSym)
        {
            fnName = nameSym.Name;
            paramsIdx = 2;
        }

        if (list[paramsIdx] is not PersistentVector paramsVec)
            throw new MacroException("fn requires params vector");

        var parameters = new List<string>();
        string? restParam = null;

        for (int i = 0; i < paramsVec.Count; i++)
        {
            var paramName = GetSymbolName(paramsVec[i])
                ?? throw new MacroException($"fn param must be a symbol");

            if (paramName == "&")
            {
                if (i + 1 >= paramsVec.Count)
                    throw new MacroException("& must be followed by a rest parameter");
                restParam = GetSymbolName(paramsVec[i + 1])
                    ?? throw new MacroException("rest param must be a symbol");
                break;
            }

            parameters.Add(paramName);
        }

        var body = list.Skip(paramsIdx + 1).ToList();

        return new MacroLambda(parameters, restParam, body, env, fnName, this);
    }

    private object? EvalRecur(PersistentList list, MacroEnv env)
    {
        // Evaluate recur args
        var args = list.Skip(1).Select(a => Eval(a, env)).ToArray();
        throw new RecurException(args);
    }

    private object? EvalCall(PersistentList list, MacroEnv env)
    {
        var fn = Eval(list[0], env);
        var args = list.Skip(1).Select(a => Eval(a, env)).ToArray();

        // If fn is a MacroLambda, invoke it
        if (fn is MacroLambda lambda)
            return lambda.Invoke(args);

        // If fn is a Symbol, try to call a builtin
        if (fn is ReaderSymbol sym)
        {
            var name = sym.Name;

            // Try to call MacroRuntime method
            var result = TryCallBuiltin(name, args);
            if (result.HasValue)
                return result.Value;

            // Unknown function - this is a list literal in output AST
            var items = new List<object?> { fn };
            items.AddRange(args);
            return new PersistentList(items);
        }

        // If fn is a Keyword, use it as a lookup function
        if (fn is ReaderKeyword kw)
        {
            if (args.Length == 0)
                throw new MacroException($"Keyword {kw} used as function requires at least one argument");
            return MacroRuntime.Get(args[0], kw, args.Length > 1 ? args[1] : null);
        }

        // Otherwise, return as a list literal
        var resultItems = new List<object?> { fn };
        resultItems.AddRange(args);
        return new PersistentList(resultItems);
    }

    private (bool HasValue, object? Value) TryCallBuiltin(string name, object?[] args)
    {
        if (!_builtins.TryGetValue(name, out var methods))
            return (false, null);

        // Find a method that matches argument count AND types
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();

            // Handle params array
            if (parameters.Length > 0 && parameters[^1].GetCustomAttribute<ParamArrayAttribute>() != null)
            {
                if (args.Length >= parameters.Length - 1 && AreArgsCompatible(parameters, args, hasParams: true))
                {
                    try
                    {
                        return (true, InvokeWithParams(method, parameters, args));
                    }
                    catch
                    {
                        continue; // Try next overload
                    }
                }
            }
            else if (parameters.Length == args.Length && AreArgsCompatible(parameters, args, hasParams: false))
            {
                try
                {
                    return (true, method.Invoke(null, args));
                }
                catch
                {
                    continue; // Try next overload
                }
            }
        }

        return (false, null);
    }

    /// <summary>
    /// Check if arguments are type-compatible with method parameters.
    /// This ensures proper overload selection (e.g., MacroLambda vs Func&lt;...&gt;).
    /// </summary>
    private static bool AreArgsCompatible(ParameterInfo[] parameters, object?[] args, bool hasParams)
    {
        var regularParamCount = hasParams ? parameters.Length - 1 : parameters.Length;

        for (int i = 0; i < regularParamCount && i < args.Length; i++)
        {
            var paramType = parameters[i].ParameterType;

            if (args[i] == null)
            {
                // null is compatible with nullable types and reference types
                if (paramType.IsValueType && Nullable.GetUnderlyingType(paramType) == null)
                    return false;
            }
            else
            {
                var argType = args[i]!.GetType();
                // Check if arg type is assignable to parameter type
                if (!paramType.IsAssignableFrom(argType))
                    return false;
            }
        }

        return true;
    }

    private static object? InvokeWithParams(MethodInfo method, ParameterInfo[] parameters, object?[] args)
    {
        var invokeArgs = new object?[parameters.Length];

        // Copy regular args
        for (int i = 0; i < parameters.Length - 1; i++)
        {
            invokeArgs[i] = args[i];
        }

        // Pack remaining args into params array
        var paramsType = parameters[^1].ParameterType.GetElementType()!;
        var paramsArray = Array.CreateInstance(paramsType, args.Length - parameters.Length + 1);
        for (int i = parameters.Length - 1; i < args.Length; i++)
        {
            paramsArray.SetValue(args[i], i - parameters.Length + 1);
        }
        invokeArgs[^1] = paramsArray;

        return method.Invoke(null, invokeArgs);
    }

    private PersistentVector EvalVector(PersistentVector vec, MacroEnv env)
    {
        var items = vec.Select(item => Eval(item, env)).ToList();
        return new PersistentVector(items);
    }

    private PersistentMap EvalMap(PersistentMap map, MacroEnv env)
    {
        var pairs = map.Select(kv =>
            new KeyValuePair<object, object?>(Eval(kv.Key, env)!, Eval(kv.Value, env)));
        return new PersistentMap(pairs);
    }

    private PersistentSet EvalSet(PersistentSet set, MacroEnv env)
    {
        var items = set.Select(item => Eval(item, env)).ToList();
        return new PersistentSet(items);
    }

    private static string? GetSymbolName(object? obj)
    {
        if (obj is ReaderSymbol sym)
            return sym.Name;
#if !NETSTANDARD2_0
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Name;
#endif
        return null;
    }
}

/// <summary>
/// Environment for macro evaluation with lexical scoping.
/// </summary>
public class MacroEnv
{
    public static readonly object NotFound = new object();

    private readonly Dictionary<string, object?> _bindings = new();
    private readonly MacroEnv? _parent;

    public MacroEnv() { }

    private MacroEnv(MacroEnv parent)
    {
        _parent = parent;
    }

    public void Bind(string name, object? value)
    {
        _bindings[name] = value;
    }

    public object? Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var value))
            return value;
        if (_parent != null)
            return _parent.Lookup(name);
        return NotFound;
    }

    public MacroEnv Extend() => new MacroEnv(this);
}

/// <summary>
/// Lambda value created by the fn special form.
/// Captures the closure environment and can be invoked.
/// </summary>
public class MacroLambda
{
    public IReadOnlyList<string> Params { get; }
    public string? RestParam { get; }
    public IReadOnlyList<object?> Body { get; }
    public MacroEnv ClosureEnv { get; }
    public string? Name { get; }

    private readonly MacroInterpreter _interpreter;

    public MacroLambda(
        List<string> parameters,
        string? restParam,
        List<object?> body,
        MacroEnv closureEnv,
        string? name,
        MacroInterpreter interpreter)
    {
        Params = parameters;
        RestParam = restParam;
        Body = body;
        ClosureEnv = closureEnv;
        Name = name;
        _interpreter = interpreter;
    }

    public object? Invoke(object?[] args)
    {
        while (true)
        {
            var env = ClosureEnv.Extend();

            // Bind named function for recursion
            if (Name != null)
                env.Bind(Name, this);

            // Bind regular params
            for (int i = 0; i < Params.Count; i++)
            {
                env.Bind(Params[i], i < args.Length ? args[i] : null);
            }

            // Bind rest param
            if (RestParam != null)
            {
                var rest = args.Length > Params.Count
                    ? new PersistentList(args.Skip(Params.Count))
                    : PersistentList.Empty;
                env.Bind(RestParam, rest);
            }

            // Evaluate body forms
            try
            {
                object? result = null;
                foreach (var bodyForm in Body)
                {
                    result = _interpreter.Eval(bodyForm, env);
                }
                return result;
            }
            catch (RecurException recur)
            {
                // Loop with new args
                args = recur.Args;
            }
        }
    }
}

/// <summary>
/// Exception thrown by recur to jump back to the enclosing loop/fn
/// </summary>
internal class RecurException : Exception
{
    public object?[] Args { get; }

    public RecurException(object?[] args)
    {
        Args = args;
    }
}
