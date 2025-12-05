using Cljr.Compiler.Reader;
using Cljr.Compiler.Macros;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;

namespace Cljr.Compiler.Analyzer;

/// <summary>
/// Analyzes parsed Clojure forms into typed expression AST
/// </summary>
public class Analyzer
{
    private readonly NamespaceManager _namespaces = new();
    private readonly Stack<HashSet<string>> _localScopes = new();
    private readonly MacroExpander _macroExpander;

    public NamespaceManager Namespaces => _namespaces;
    public MacroExpander MacroExpander => _macroExpander;

    /// <summary>
    /// Create an analyzer with a new macro expander
    /// </summary>
    public Analyzer() : this(new MacroExpander()) { }

    /// <summary>
    /// Create an analyzer with a shared macro expander (for REPL use)
    /// </summary>
    public Analyzer(MacroExpander macroExpander)
    {
        _macroExpander = macroExpander;
    }

    /// <summary>
    /// Analyze a complete file
    /// </summary>
    public CompilationUnit AnalyzeFile(IEnumerable<object?> forms)
    {
        NsExpr? ns = null;
        var exprs = new List<Expr>();

        foreach (var form in forms)
        {
            var expr = Analyze(form, new AnalyzerContext());

            if (expr is NsExpr nsExpr && ns is null)
            {
                ns = nsExpr;
                _namespaces.SwitchTo(nsExpr.Name);
            }
            else
            {
                exprs.Add(expr);
            }
        }

        return new CompilationUnit(ns, exprs);
    }

    /// <summary>
    /// Analyze a single form
    /// </summary>
    public Expr Analyze(object? form, AnalyzerContext ctx)
    {
        // Extract type hint from form's metadata before analyzing
        var typeHint = ExtractTypeHintFromForm(form);

        // Normalize runtime Symbol to reader Symbol before analysis
        form = NormalizeForm(form);

        // Handle Symbol types that might come from different assembly contexts
        // by checking the type name rather than relying solely on pattern matching.
        // Use reflection to extract properties since direct casting fails across assembly contexts.
        if (form?.GetType().FullName == "Cljr.Compiler.Reader.Symbol")
        {
            var type = form.GetType();
            var ns = (string?)type.GetProperty("Namespace")?.GetValue(form);
            var name = (string?)type.GetProperty("Name")?.GetValue(form);
            // Also extract metadata to preserve type hints
            var meta = type.GetProperty("Meta")?.GetValue(form) as IReadOnlyDictionary<object, object>;
            var sym = new ReaderSymbol(ns, name ?? throw new AnalyzerException("Symbol has no name")) { Meta = meta };
            var symResult = AnalyzeSymbol(sym, ctx);
            if (typeHint is not null)
                return new CastExpr(typeHint, symResult);
            return symResult;
        }

        var result = form switch
        {
            null => new LiteralExpr(null),
            bool b => new LiteralExpr(b),
            int or long or double or float or decimal => new LiteralExpr(form),
            string s => new LiteralExpr(s),
            char c => new LiteralExpr(c),

            Keyword kw => new KeywordExpr(kw),
            ReaderSymbol sym => AnalyzeSymbol(sym, ctx),

            PersistentList list => AnalyzeList(list, ctx),
            PersistentVector vec => AnalyzeVector(vec, ctx),
            PersistentMap map => AnalyzeMap(map, ctx),
            PersistentSet set => AnalyzeSet(set, ctx),

            // Fallback for assembly loading issues with Keyword type
            _ when form?.GetType().FullName == "Cljr.Compiler.Reader.Keyword" =>
                AnalyzeKeywordByReflection(form),

            _ => throw new AnalyzerException($"Cannot analyze: {form?.GetType().FullName} value={form}")
        };

        // If form has type hint, wrap in cast expression
        if (typeHint is not null)
        {
            return new CastExpr(typeHint, result);
        }

        return result;
    }

    /// <summary>
    /// Extract type hint from a form's metadata (works on Symbol, List, Vector, Map)
    /// </summary>
    private static string? ExtractTypeHintFromForm(object? form)
    {
        var meta = GetMetaFromForm(form);
        return ExtractTypeHint(meta);
    }

    /// <summary>
    /// Analyze a Keyword type using reflection (fallback for assembly loading issues)
    /// </summary>
    private static KeywordExpr AnalyzeKeywordByReflection(object form)
    {
        var type = form.GetType();
        var ns = type.GetProperty("Namespace")?.GetValue(form) as string;
        var name = type.GetProperty("Name")?.GetValue(form) as string
            ?? throw new AnalyzerException("Keyword has no name");
        var kw = Keyword.Intern(ns, name);
        return new KeywordExpr(kw);
    }

    /// <summary>
    /// Check if an object is a Symbol type (handling assembly loading issues)
    /// </summary>
    private static bool IsSymbol(object? obj)
    {
        if (obj is null) return false;
        if (obj is ReaderSymbol) return true;
#if !NETSTANDARD2_0
        if (obj is global::Cljr.Symbol) return true;
#endif
        var typeName = obj.GetType().FullName;
        return typeName == "Cljr.Compiler.Reader.Symbol" || typeName == "Cljr.Symbol";
    }

    /// <summary>
    /// Get Symbol name using reflection (fallback for assembly loading issues)
    /// </summary>
    private static string? GetSymbolName(object? obj)
    {
        if (obj is null) return null;
        if (obj is ReaderSymbol sym) return sym.Name;
#if !NETSTANDARD2_0
        if (obj is global::Cljr.Symbol runtimeSym) return runtimeSym.Name;
#endif
        var type = obj.GetType();
        var typeName = type.FullName;
        if (typeName == "Cljr.Compiler.Reader.Symbol" || typeName == "Cljr.Symbol")
        {
            return type.GetProperty("Name")?.GetValue(obj) as string;
        }
        return null;
    }

    /// <summary>
    /// Get Symbol namespace using reflection (fallback for assembly loading issues)
    /// </summary>
    private static string? GetSymbolNamespace(object? obj)
    {
        if (obj is null) return null;
        if (obj is ReaderSymbol sym) return sym.Namespace;
#if !NETSTANDARD2_0
        if (obj is global::Cljr.Symbol runtimeSym) return runtimeSym.Namespace;
#endif
        var type = obj.GetType();
        var typeName = type.FullName;
        if (typeName == "Cljr.Compiler.Reader.Symbol" || typeName == "Cljr.Symbol")
        {
            return type.GetProperty("Namespace")?.GetValue(obj) as string;
        }
        return null;
    }

    /// <summary>
    /// Get full symbol name (namespace.name or just name) using reflection
    /// </summary>
    private static string? GetSymbolFullName(object? obj)
    {
        var ns = GetSymbolNamespace(obj);
        var name = GetSymbolName(obj);
        if (name is null) return null;
        return ns is not null ? $"{ns}.{name}" : name;
    }

    /// <summary>
    /// Normalize runtime types to reader types.
    /// Recursively converts Cljr.Symbol (runtime) to Cljr.Compiler.Reader.Symbol
    /// throughout nested data structures.
    /// </summary>
    private static object? NormalizeForm(object? form)
    {
#if NET
        // Convert runtime Symbol to reader Symbol
        if (form is global::Cljr.Symbol rtSym)
            return new Cljr.Compiler.Reader.Symbol(rtSym.Namespace, rtSym.Name);

        // Recursively normalize nested collections
        if (form is PersistentList list)
        {
            var normalized = list.Select(NormalizeForm).ToList();
            return new PersistentList(normalized) { Meta = list.Meta };
        }

        if (form is PersistentVector vec)
        {
            var normalized = vec.Select(NormalizeForm).ToList();
            return new PersistentVector(normalized) { Meta = vec.Meta };
        }

        if (form is PersistentMap map)
        {
            var normalized = map.Select(kv =>
                new KeyValuePair<object, object?>(
                    NormalizeForm(kv.Key)!,
                    NormalizeForm(kv.Value)));
            return new PersistentMap(normalized) { Meta = map.Meta };
        }

        if (form is PersistentSet set)
        {
            var normalized = set.Select(NormalizeForm);
            return new PersistentSet(normalized) { Meta = set.Meta };
        }
#endif
        return form;
    }

    /// <summary>
    /// Get metadata from a form, handling both reader and runtime types.
    /// </summary>
    private static IReadOnlyDictionary<object, object>? GetMetaFromForm(object? form)
    {
#if NET
        if (form is global::Cljr.Symbol rtSym)
            return rtSym.Meta;
#endif
        return form switch
        {
            Symbol s => s.Meta,
            PersistentList l => l.Meta,
            PersistentVector v => v.Meta,
            PersistentMap m => m.Meta,
            _ => null
        };
    }

    #region Collection Analysis

    private Expr AnalyzeVector(PersistentVector vec, AnalyzerContext ctx)
    {
        var items = vec.Select(item => Analyze(item, ctx)).ToList();
        return new VectorExpr(items);
    }

    private Expr AnalyzeMap(PersistentMap map, AnalyzerContext ctx)
    {
        var pairs = map.Select(kv =>
            (Key: Analyze(kv.Key, ctx), Value: Analyze(kv.Value, ctx))).ToList();
        return new MapExpr(pairs);
    }

    private Expr AnalyzeSet(PersistentSet set, AnalyzerContext ctx)
    {
        var items = set.Select(item => Analyze(item, ctx)).ToList();
        return new SetExpr(items);
    }

    #endregion

    #region Symbol Analysis

    private Expr AnalyzeSymbol(ReaderSymbol sym, AnalyzerContext ctx)
    {
        // Strip clojure.core namespace - our Core functions are available directly
        if (sym.Namespace is "clojure.core" or "cljs.core" or "cljr.core")
        {
            sym = ReaderSymbol.Parse(sym.Name);
        }

        // Check if it's a local variable
        if (sym.Namespace is null && IsLocal(sym.Name))
        {
            return new SymbolExpr(sym, IsLocal: true);
        }

        // Check for static field/property access (Type/FIELD)
        if (sym.Namespace is not null && char.IsUpper(sym.Namespace[0]))
        {
            return new StaticPropertyExpr(sym.Namespace, sym.Name);
        }

        // It's a var reference
        return new SymbolExpr(sym, IsLocal: false);
    }

    private bool IsLocal(string name)
    {
        foreach (var scope in _localScopes)
        {
            if (scope.Contains(name)) return true;
        }
        return false;
    }

    private void PushScope() => _localScopes.Push([]);
    private void PopScope() => _localScopes.Pop();
    private void AddLocal(string name) => _localScopes.Peek().Add(name);

    #endregion

    #region List Analysis (Special Forms & Invocations)

    private Expr AnalyzeList(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count == 0)
            return new LiteralExpr(list);

        var first = list[0];

        // Check for special forms
        if (first is ReaderSymbol sym)
        {
            Expr? expr = sym.Name switch
            {
                "def" => AnalyzeDef(list, ctx),
                "defn" => AnalyzeDefn(list, ctx, isPrivate: false),
                "defn-" => AnalyzeDefn(list, ctx, isPrivate: true),
                "fn" or "fn*" => AnalyzeFn(list, ctx),
                "let" => AnalyzeLet(list, ctx),
                "do" => AnalyzeDo(list, ctx),
                "if" => AnalyzeIf(list, ctx),
                "quote" => AnalyzeQuote(list),
                "new" => AnalyzeNew(list, ctx),
                "set!" => AnalyzeAssign(list, ctx),
                "throw" => AnalyzeThrow(list, ctx),
                "try" => AnalyzeTry(list, ctx),
                "loop" => AnalyzeLoop(list, ctx),
                "recur" => AnalyzeRecur(list, ctx),
                "await" => AnalyzeAwait(list, ctx),
                "ns" => AnalyzeNs(list, ctx),
                "in-ns" => AnalyzeInNs(list, ctx),
                "require" => AnalyzeRequire(list, ctx),
                // Macro expansions
                "when" => AnalyzeWhen(list, ctx),
                "when-not" => AnalyzeWhenNot(list, ctx),
                "when-let" => AnalyzeWhenLet(list, ctx),
                "if-let" => AnalyzeIfLet(list, ctx),
                "if-not" => AnalyzeIfNot(list, ctx),
                "cond" => AnalyzeCond(list, ctx),
                "and" => AnalyzeAnd(list, ctx),
                "or" => AnalyzeOr(list, ctx),
                "not" => AnalyzeNot(list, ctx),
                "dotimes" => AnalyzeDotimes(list, ctx),
                "->" => AnalyzeThreadFirst(list, ctx),
                "->>" => AnalyzeThreadLast(list, ctx),
                "doto" => AnalyzeDoto(list, ctx),
                "comment" => new LiteralExpr(null), // comment returns nil
                "syntax-quote" => AnalyzeSyntaxQuote(list, ctx),
                "defmacro" => AnalyzeDefmacro(list, ctx),
                "macroexpand" => AnalyzeMacroexpand(list, ctx),
                "macroexpand-1" => AnalyzeMacroexpandOne(list, ctx),
                // C# interop
                "csharp*" => AnalyzeCSharp(list, ctx),
                "defprotocol" => AnalyzeDefprotocol(list, ctx),
                "deftype" => AnalyzeDeftype(list, ctx),
                "defrecord" => AnalyzeDefrecord(list, ctx),
                "deftest" => AnalyzeDeftest(list, ctx),
                "is" => AnalyzeIs(list, ctx),
                "instance?" => AnalyzeInstanceCheck(list, ctx),
                _ => null
            };

            if (expr is not null) return expr;

            // Check for user-defined macros (only unqualified symbols)
            // Qualified symbols like cljr.core/future should NOT be treated as macro calls
            if (sym.Namespace is null && _macroExpander.IsMacro(sym.Name))
            {
                var expanded = _macroExpander.Macroexpand(list);
                return Analyze(expanded, ctx);
            }

            // Check for interop
            // IMPORTANT: Check for property access (.-) BEFORE method access (.)
            // because ".-prop" starts with "." but should be treated as property access
            if (sym.Name.StartsWith(".-", StringComparison.Ordinal))
            {
                // Instance property access: (.-prop obj)
                return AnalyzeInstanceProperty(sym.Name[2..], list, ctx);
            }

            if (sym.Name.StartsWith(".", StringComparison.Ordinal) && sym.Name.Length > 1)
            {
                // Instance method call: (.method obj args)
                return AnalyzeInstanceMethod(sym.Name[1..], list, ctx);
            }

            if (sym.Namespace is not null && char.IsUpper(sym.Namespace[0]))
            {
                // Static method call: (Type/method args)
                return AnalyzeStaticMethod(sym, list, ctx);
            }

            if (sym.Name.EndsWith(".", StringComparison.Ordinal) && sym.Name.Length > 1)
            {
                // Constructor: (Type. args) or (namespace/Type. args)
                var typeName = sym.Name[..^1];
                if (sym.Namespace is not null)
                {
                    typeName = $"{ToCSharpNamespace(sym.Namespace)}.{typeName}";
                }
                return AnalyzeConstructor(typeName, list, ctx);
            }
        }

        // Regular function invocation
        return AnalyzeInvoke(list, ctx);
    }

    #endregion

    #region Special Form Analyzers

    private DefExpr AnalyzeDef(PersistentList list, AnalyzerContext ctx)
    {
        // (def name) or (def name init) or (def name "docstring" init)
        // Type hint: (def ^Type name init)
        if (list.Count < 2)
            throw new AnalyzerException("def requires a name");

        var name = list[1] as ReaderSymbol
            ?? throw new AnalyzerException("def name must be a symbol");

        // Extract type hint from symbol metadata: (def ^WebApplicationBuilder builder ...)
        string? typeHint = ExtractTypeHint(name.Meta);

        string? docString = null;
        Expr? init = null;

        if (list.Count == 3)
        {
            if (list[2] is string doc)
                docString = doc;
            else
                init = Analyze(list[2], ctx);
        }
        else if (list.Count == 4)
        {
            docString = list[2] as string;
            init = Analyze(list[3], ctx);
        }
        else if (list.Count > 4)
        {
            throw new AnalyzerException("def takes 1-3 arguments");
        }

        return new DefExpr(name, init, docString, typeHint);
    }

    private DefExpr AnalyzeDefn(PersistentList list, AnalyzerContext ctx, bool isPrivate = false)
    {
        // (defn name [params] body) or (defn name "doc" [params] body)
        // or multi-arity: (defn name ([x] body1) ([x y] body2))
        // Also handles: (defn name "doc" {:added "1.0"} ([x] body)) - with metadata map
        // defn- is the same but generates private methods
        if (list.Count < 3)
            throw new AnalyzerException("defn requires name and body");

        var name = list[1] as ReaderSymbol
            ?? throw new AnalyzerException("defn name must be a symbol");

        // Check for ^:async metadata (cross-assembly safe)
        var isAsync = HasMetadataKey(name.Meta, "async");

        // Extract return type from ^Type metadata (:tag)
        string? returnType = ExtractTypeHint(name.Meta);

        // Auto-detect async from Task<T> return type
        if (returnType is not null && returnType.StartsWith("Task"))
            isAsync = true;

        int idx = 2;
        string? docString = null;

        // Skip optional docstring
        if (idx < list.Count && list[idx] is string doc)
        {
            docString = doc;
            idx++;
        }

        // Skip optional metadata map (e.g., {:added "1.0"})
        // This is common in ClojureCLR core library functions
        if (idx < list.Count && list[idx] is PersistentMap)
        {
            // Metadata map is for documentation purposes, skip it
            idx++;
        }

        var fnList = new List<object?> { ReaderSymbol.Parse("fn") };
        for (int i = idx; i < list.Count; i++)
            fnList.Add(list[i]);

        var fnExpr = AnalyzeFn(new PersistentList(fnList), ctx with { IsAsync = isAsync, ReturnType = returnType });

        // Create a named function
        var namedFn = fnExpr with { Name = name, IsAsync = isAsync };

        return new DefExpr(name, namedFn, docString, TypeHint: null, IsPrivate: isPrivate);
    }

    /// <summary>
    /// Extract type hint from :tag metadata
    /// </summary>
    private static string? ExtractTypeHint(IReadOnlyDictionary<object, object>? meta)
    {
        if (meta is null) return null;

        // Find the :tag key - can't use TryGetValue directly because Keyword uses
        // ReferenceEquals for equality, which fails across assembly contexts.
        // Instead, find the key by comparing the keyword name string.
        object? tag = null;
        foreach (var kv in meta)
        {
            if (IsKeywordWithName(kv.Key, "tag"))
            {
                tag = kv.Value;
                break;
            }
        }

        if (tag is null) return null;

        // Direct type check first
        if (tag is ReaderSymbol typeSym)
        {
            // Return the full symbol name (including namespace if present)
            // This handles both simple types (String) and generics (Task<IList<User>>)
            var typeName = typeSym.Namespace is not null
                ? $"{typeSym.Namespace}.{typeSym.Name}"
                : typeSym.Name;
            return NormalizeTypeName(typeName);
        }

        // Support string type hints for array types like "string[]" that can't be symbols
        // Usage: ^{:tag "string[]"} args
        if (tag is string typeStr)
        {
            return NormalizeTypeName(typeStr);
        }

        // Fallback: use reflection for assembly loading issues
        // The tag might be a Symbol from a different assembly context
        if (IsSymbol(tag))
        {
            var ns = GetSymbolNamespace(tag);
            var name = GetSymbolName(tag);
            if (name is not null)
            {
                var typeName = ns is not null ? $"{ns}.{name}" : name;
                return NormalizeTypeName(typeName);
            }
        }

        return null;
    }

    /// <summary>
    /// Normalize .NET type names to C# keyword aliases to avoid conflicts
    /// with user-defined class names (e.g., clojure.string.String class).
    /// </summary>
    private static string NormalizeTypeName(string typeName)
    {
        return typeName switch
        {
            "String" or "System.String" => "string",
            "Int32" or "System.Int32" => "int",
            "Int64" or "System.Int64" => "long",
            "Double" or "System.Double" => "double",
            "Single" or "System.Single" => "float",
            "Boolean" or "System.Boolean" => "bool",
            "Byte" or "System.Byte" => "byte",
            "SByte" or "System.SByte" => "sbyte",
            "Int16" or "System.Int16" => "short",
            "UInt16" or "System.UInt16" => "ushort",
            "UInt32" or "System.UInt32" => "uint",
            "UInt64" or "System.UInt64" => "ulong",
            "Decimal" or "System.Decimal" => "decimal",
            "Char" or "System.Char" => "char",
            "Object" or "System.Object" => "object",
            "Void" or "System.Void" => "void",
            _ => typeName
        };
    }

    /// <summary>
    /// Extract .NET attributes from :attr metadata.
    /// Syntax: ^{:attr [Parameter]} or ^{:attr [Required "message"]}
    /// </summary>
    private static IReadOnlyList<AttributeSpec>? ExtractAttributes(IReadOnlyDictionary<object, object>? meta)
    {
        if (meta is null) return null;

        // Find the :attr key
        object? attrValue = null;
        foreach (var kv in meta)
        {
            if (IsKeywordWithName(kv.Key, "attr"))
            {
                attrValue = kv.Value;
                break;
            }
        }

        if (attrValue is null) return null;

        // The value should be a vector: [Attribute1 Attribute2] or [Required "message"]
        var (isVector, items) = GetPersistentVectorItems(attrValue);
        if (!isVector || items is null || items.Count == 0) return null;

        var attributes = new List<AttributeSpec>();

        // Parse attribute specifications
        // Simple: [Parameter] -> AttributeSpec("Parameter", null)
        // With args: [Required "message"] -> AttributeSpec("Required", ["message"])
        int i = 0;
        while (i < items.Count)
        {
            var item = items[i];

            // Get the attribute name (must be a symbol)
            string? attrName = null;
            if (item is ReaderSymbol sym)
            {
                attrName = sym.Name;
            }
            else if (IsSymbol(item))
            {
                attrName = GetSymbolName(item);
            }

            if (attrName is null)
            {
                i++;
                continue;
            }

            // Check if the next items are arguments (not symbols = likely args)
            var args = new List<object?>();
            i++;
            while (i < items.Count)
            {
                var nextItem = items[i];
                // If next item is a symbol, it's a new attribute, not an argument
                if (nextItem is ReaderSymbol || IsSymbol(nextItem))
                    break;
                args.Add(nextItem);
                i++;
            }

            attributes.Add(new AttributeSpec(attrName, args.Count > 0 ? args : null));
        }

        return attributes.Count > 0 ? attributes : null;
    }

    /// <summary>
    /// Check if object is a Keyword with the given name (handles cross-assembly contexts)
    /// </summary>
    private static bool IsKeywordWithName(object? obj, string name, string? ns = null)
    {
        if (obj is null) return false;

        // Direct type check first
        if (obj is Keyword kw)
        {
            return kw.Name == name && kw.Namespace == ns;
        }

        // Fallback: use reflection for assembly loading issues
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.Keyword")
        {
            var kwName = (string?)type.GetProperty("Name")?.GetValue(obj);
            var kwNs = (string?)type.GetProperty("Namespace")?.GetValue(obj);
            return kwName == name && kwNs == ns;
        }

        return false;
    }

    /// <summary>
    /// Check if metadata dictionary contains a keyword with the given name (cross-assembly safe)
    /// </summary>
    private static bool HasMetadataKey(IReadOnlyDictionary<object, object>? meta, string keyName)
    {
        if (meta is null) return false;

        foreach (var kv in meta)
        {
            if (IsKeywordWithName(kv.Key, keyName))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Get items from a PersistentList (handles cross-assembly contexts)
    /// </summary>
    private static (bool IsList, IReadOnlyList<object?>? Items) GetPersistentListItems(object? obj)
    {
        if (obj is null) return (false, null);

        // Direct type check first
        if (obj is PersistentList list)
            return (true, list);

        // Fallback: use reflection for assembly loading issues
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.PersistentList")
        {
            // PersistentList implements IReadOnlyList<object?>
            if (obj is IReadOnlyList<object?> items)
                return (true, items);
        }

        return (false, null);
    }

    /// <summary>
    /// Get items from a PersistentVector (handles cross-assembly contexts)
    /// </summary>
    private static (bool IsVector, IReadOnlyList<object?>? Items) GetPersistentVectorItems(object? obj)
    {
        if (obj is null) return (false, null);

        // Direct type check first
        if (obj is PersistentVector vec)
            return (true, vec);

        // Fallback: use reflection for assembly loading issues
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.PersistentVector")
        {
            // PersistentVector implements IReadOnlyList<object?>
            if (obj is IReadOnlyList<object?> items)
                return (true, items);
        }

        return (false, null);
    }

    private FnExpr AnalyzeFn(PersistentList list, AnalyzerContext ctx)
    {
        // (fn [params] body) or (fn name [params] body)
        // or multi-arity: (fn ([x] body1) ([x y] body2))
        if (list.Count < 2)
            throw new AnalyzerException("fn requires params and body");

        int idx = 1;
        ReaderSymbol? name = null;

        if (list[idx] is ReaderSymbol sym)
        {
            name = sym;
            idx++;
        }

        var methods = new List<FnMethod>();

        if (list[idx] is PersistentVector)
        {
            // Single arity: (fn [x] body)
            var method = AnalyzeFnMethod(list.Skip(idx).ToList(), ctx);
            methods.Add(method);
        }
        else if (list[idx] is PersistentList)
        {
            // Multi-arity: (fn ([x] body1) ([x y] body2))
            for (int i = idx; i < list.Count; i++)
            {
                if (list[i] is PersistentList arity)
                {
                    var method = AnalyzeFnMethod(arity.ToList(), ctx);
                    methods.Add(method);
                }
            }
        }

        var isVariadic = methods.Any(m => m.RestParam is not null);

        return new FnExpr(name, methods, isVariadic) { IsAsync = ctx.IsAsync };
    }

    private FnMethod AnalyzeFnMethod(IList<object?> forms, AnalyzerContext ctx)
    {
        if (forms.Count == 0 || forms[0] is not PersistentVector paramsVec)
            throw new AnalyzerException("fn method requires params vector");

        var parameters = new List<ReaderSymbol>();
        var paramTypes = new List<string?>();
        ReaderSymbol? restParam = null;

        for (int i = 0; i < paramsVec.Count; i++)
        {
            if (paramsVec[i] is ReaderSymbol p)
            {
                if (p.Name == "&")
                {
                    if (i + 1 < paramsVec.Count && paramsVec[i + 1] is ReaderSymbol rest)
                    {
                        restParam = rest;
                        break;
                    }
                }
                else
                {
                    parameters.Add(p);
                    // Extract type hint from parameter metadata
                    paramTypes.Add(ExtractTypeHint(p.Meta));
                }
            }
        }

        PushScope();
        foreach (var p in parameters)
            AddLocal(p.Name);
        if (restParam is not null)
            AddLocal(restParam.Name);

        // Body is wrapped in implicit do
        var bodyForms = forms.Skip(1).ToList();
        var body = bodyForms.Count == 1
            ? Analyze(bodyForms[0], ctx)
            : new DoExpr(bodyForms.Select(f => Analyze(f, ctx)).ToList());

        PopScope();

        // Only include param types if at least one is specified
        var hasAnyParamTypes = paramTypes.Any(t => t is not null);

        return new FnMethod(
            parameters,
            restParam,
            body,
            ctx.ReturnType,
            hasAnyParamTypes ? paramTypes : null
        );
    }

    private LetExpr AnalyzeLet(PersistentList list, AnalyzerContext ctx)
    {
        // (let [x 1 y 2] body)
        if (list.Count < 3)
            throw new AnalyzerException("let requires bindings and body");

        if (list[1] is not PersistentVector bindingsVec)
            throw new AnalyzerException("let bindings must be a vector");

        if (bindingsVec.Count % 2 != 0)
            throw new AnalyzerException("let bindings must have even count");

        PushScope();

        var bindings = new List<(ReaderSymbol, Expr)>();
        for (int i = 0; i < bindingsVec.Count; i += 2)
        {
            var name = bindingsVec[i] as ReaderSymbol
                ?? throw new AnalyzerException("let binding name must be a symbol");
            var init = Analyze(bindingsVec[i + 1], ctx);
            bindings.Add((name, init));
            AddLocal(name.Name);
        }

        var bodyForms = list.Skip(2).ToList();
        var body = bodyForms.Count == 1
            ? Analyze(bodyForms[0], ctx)
            : new DoExpr(bodyForms.Select(f => Analyze(f, ctx)).ToList());

        PopScope();

        return new LetExpr(bindings, body);
    }

    private DoExpr AnalyzeDo(PersistentList list, AnalyzerContext ctx)
    {
        var exprs = list.Skip(1).Select(f => Analyze(f, ctx)).ToList();
        return new DoExpr(exprs);
    }

    private IfExpr AnalyzeIf(PersistentList list, AnalyzerContext ctx)
    {
        // (if test then) or (if test then else)
        if (list.Count < 3)
            throw new AnalyzerException("if requires test and then");

        var test = Analyze(list[1], ctx);
        var then = Analyze(list[2], ctx);
        var @else = list.Count > 3 ? Analyze(list[3], ctx) : null;

        return new IfExpr(test, then, @else);
    }

    private QuoteExpr AnalyzeQuote(PersistentList list)
    {
        if (list.Count != 2)
            throw new AnalyzerException("quote takes exactly one argument");
        return new QuoteExpr(list[1]);
    }

    private NewExpr AnalyzeNew(PersistentList list, AnalyzerContext ctx)
    {
        // (new Type args...)
        if (list.Count < 2)
            throw new AnalyzerException("new requires a type");

        var typeName = list[1] switch
        {
            Symbol s => s.Namespace is not null
                ? $"{ToCSharpNamespace(s.Namespace)}.{s.Name}"
                : s.Name,
            _ => throw new AnalyzerException("new type must be a symbol")
        };

        var args = list.Skip(2).Select(f => Analyze(f, ctx)).ToList();
        return new NewExpr(typeName, args);
    }

    /// <summary>
    /// Convert Clojure namespace to C# namespace format
    /// minimal-api.main -> minimal_api_main
    /// </summary>
    private static string ToCSharpNamespace(string clojureNs)
    {
        return clojureNs.Replace("-", "_").Replace(".", "_");
    }

    private AssignExpr AnalyzeAssign(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 3)
            throw new AnalyzerException("set! takes exactly two arguments");

        var target = Analyze(list[1], ctx);
        var value = Analyze(list[2], ctx);
        return new AssignExpr(target, value);
    }

    private ThrowExpr AnalyzeThrow(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("throw takes exactly one argument");

        return new ThrowExpr(Analyze(list[1], ctx));
    }

    private TryExpr AnalyzeTry(PersistentList list, AnalyzerContext ctx)
    {
        var body = new List<object?>();
        var catches = new List<CatchClause>();
        Expr? finallyExpr = null;

        for (int i = 1; i < list.Count; i++)
        {
            if (list[i] is PersistentList clause && clause.Count > 0)
            {
                if (clause[0] is ReaderSymbol { Name: "catch" })
                {
                    // (catch ExType e body)
                    var exType = (clause[1] as ReaderSymbol)?.Name ?? "Exception";
                    var binding = clause[2] as ReaderSymbol ?? throw new AnalyzerException("catch binding must be symbol");

                    PushScope();
                    AddLocal(binding.Name);
                    var catchBody = clause.Count == 4
                        ? Analyze(clause[3], ctx)
                        : new DoExpr(clause.Skip(3).Select(f => Analyze(f, ctx)).ToList());
                    PopScope();

                    catches.Add(new CatchClause(exType, binding, catchBody));
                    continue;
                }

                if (clause[0] is ReaderSymbol { Name: "finally" })
                {
                    finallyExpr = clause.Count == 2
                        ? Analyze(clause[1], ctx)
                        : new DoExpr(clause.Skip(1).Select(f => Analyze(f, ctx)).ToList());
                    continue;
                }
            }
            body.Add(list[i]);
        }

        var bodyExpr = body.Count == 1
            ? Analyze(body[0], ctx)
            : new DoExpr(body.Select(f => Analyze(f, ctx)).ToList());

        return new TryExpr(bodyExpr, catches, finallyExpr);
    }

    private LoopExpr AnalyzeLoop(PersistentList list, AnalyzerContext ctx)
    {
        // (loop [x 0] body)
        if (list.Count < 3)
            throw new AnalyzerException("loop requires bindings and body");

        if (list[1] is not PersistentVector bindingsVec)
            throw new AnalyzerException("loop bindings must be a vector");

        PushScope();

        var bindings = new List<(ReaderSymbol, Expr)>();
        for (int i = 0; i < bindingsVec.Count; i += 2)
        {
            var name = bindingsVec[i] as ReaderSymbol
                ?? throw new AnalyzerException("loop binding name must be a symbol");
            var init = Analyze(bindingsVec[i + 1], ctx);
            bindings.Add((name, init));
            AddLocal(name.Name);
        }

        var bodyForms = list.Skip(2).ToList();
        var body = bodyForms.Count == 1
            ? Analyze(bodyForms[0], ctx)
            : new DoExpr(bodyForms.Select(f => Analyze(f, ctx)).ToList());

        PopScope();

        return new LoopExpr(bindings, body);
    }

    private RecurExpr AnalyzeRecur(PersistentList list, AnalyzerContext ctx)
    {
        var args = list.Skip(1).Select(f => Analyze(f, ctx)).ToList();
        return new RecurExpr(args);
    }

    private AwaitExpr AnalyzeAwait(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("await takes exactly one argument");

        return new AwaitExpr(Analyze(list[1], ctx));
    }

    private NsExpr AnalyzeNs(PersistentList list, AnalyzerContext ctx)
    {
        // (ns foo.bar (:require [...]) (:import [...]))
        if (list.Count < 2)
            throw new AnalyzerException("ns requires a name");

        var name = GetSymbolFullName(list[1])
            ?? throw new AnalyzerException("ns name must be a symbol");

        var requires = new List<RequireClause>();
        var imports = new List<ImportClause>();

        for (int i = 2; i < list.Count; i++)
        {
            // Use cross-assembly safe type checking
            var (isList, clauseItems) = GetPersistentListItems(list[i]);
            if (isList && clauseItems != null && clauseItems.Count > 0)
            {
                if (IsKeywordWithName(clauseItems[0], "require"))
                {
                    for (int j = 1; j < clauseItems.Count; j++)
                    {
                        requires.Add(ParseRequire(clauseItems[j]));
                    }
                }
                else if (IsKeywordWithName(clauseItems[0], "import"))
                {
                    for (int j = 1; j < clauseItems.Count; j++)
                    {
                        imports.Add(ParseImport(clauseItems[j]));
                    }
                }
            }
        }

        return new NsExpr(name, requires, imports);
    }

    private InNsExpr AnalyzeInNs(PersistentList list, AnalyzerContext ctx)
    {
        // (in-ns 'foo.bar) - switches to namespace (creating if needed)
        if (list.Count != 2)
            throw new AnalyzerException("in-ns requires a namespace name");

        var arg = list[1];
        string name;

        // Handle quoted symbol: (in-ns 'foo.bar)
        if (arg is PersistentList quotedList &&
            quotedList.Count == 2 &&
            quotedList[0] is ReaderSymbol { Name: "quote" } &&
            quotedList[1] is ReaderSymbol quotedSym)
        {
            name = quotedSym.Namespace is not null
                ? $"{quotedSym.Namespace}.{quotedSym.Name}"
                : quotedSym.Name;
        }
        // Handle direct symbol (for programmatic use)
        else if (arg is ReaderSymbol sym)
        {
            name = sym.Namespace is not null
                ? $"{sym.Namespace}.{sym.Name}"
                : sym.Name;
        }
        else
        {
            throw new AnalyzerException("in-ns requires a symbol argument");
        }

        // Switch the namespace manager
        _namespaces.SwitchTo(name);

        return new InNsExpr(name);
    }

    private RequireExpr AnalyzeRequire(PersistentList list, AnalyzerContext ctx)
    {
        // (require 'foo.bar) or (require '[foo.bar :as fb])
        // (require '[foo.bar :as fb] '[baz.qux :refer [x y]])
        if (list.Count < 2)
            throw new AnalyzerException("require requires at least one namespace");

        var clauses = new List<RequireClause>();

        for (int i = 1; i < list.Count; i++)
        {
            var arg = list[i];

            // Handle quoted symbol or vector: 'foo.bar or '[foo.bar :as fb]
            if (arg is PersistentList quotedList &&
                quotedList.Count == 2 &&
                quotedList[0] is ReaderSymbol { Name: "quote" })
            {
                arg = quotedList[1];
            }

            if (arg is ReaderSymbol sym)
            {
                // Simple namespace: 'foo.bar
                clauses.Add(new RequireClause(sym.ToString(), null, null));
            }
            else if (arg is PersistentVector vec && vec.Count >= 1)
            {
                // Vector form: '[foo.bar :as fb :refer [x y]]
                clauses.Add(ParseRequire(vec));
            }
            else
            {
                throw new AnalyzerException($"Invalid require clause: {arg}");
            }
        }

        return new RequireExpr(clauses);
    }

    private RequireClause ParseRequire(object? form)
    {
        if (form is ReaderSymbol sym)
            return new RequireClause(sym.ToString(), null, null);

        if (form is PersistentVector vec && vec.Count >= 1)
        {
            var ns = (vec[0] as ReaderSymbol)?.ToString()
                ?? throw new AnalyzerException("require namespace must be symbol");
            string? alias = null;
            List<string>? refers = null;

            for (int i = 1; i < vec.Count; i += 2)
            {
                // Use cross-assembly safe keyword checking by examining type name and ToString
                var elem = vec[i];
                if (elem != null && i + 1 < vec.Count)
                {
                    var elemTypeName = elem.GetType().Name;
                    var elemStr = elem.ToString();

                    if (elemTypeName == "Keyword")
                    {
                        // Extract keyword name from string representation (e.g., ":as" -> "as")
                        var keywordName = elemStr?.TrimStart(':');

                        if (keywordName == "as")
                        {
                            // Extract alias from next element
                            var aliasObj = vec[i + 1];
                            if (aliasObj != null && aliasObj.GetType().Name == "Symbol")
                            {
                                // Get the Name property via reflection for cross-assembly safety
                                var nameProp = aliasObj.GetType().GetProperty("Name");
                                if (nameProp != null)
                                {
                                    alias = nameProp.GetValue(aliasObj)?.ToString();
                                }
                            }
                        }
                        else if (keywordName == "refer" && vec[i + 1] is PersistentVector referVec)
                        {
                            refers = referVec
                                .Where(s => s?.GetType().Name == "Symbol")
                                .Select(s => s?.GetType().GetProperty("Name")?.GetValue(s)?.ToString())
                                .Where(n => n != null)
                                .Select(n => n!)
                                .ToList();
                        }
                    }
                }
            }

            return new RequireClause(ns, alias, refers);
        }

        throw new AnalyzerException("Invalid require clause");
    }

    private ImportClause ParseImport(object? form)
    {
        if (form is PersistentVector vec && vec.Count >= 1)
        {
            var ns = (vec[0] as ReaderSymbol)?.ToString()
                ?? throw new AnalyzerException("import namespace must be symbol");
            var types = vec.Skip(1).OfType<ReaderSymbol>().Select(s => s.Name).ToList();
            return new ImportClause(ns, types);
        }

        if (form is ReaderSymbol sym)
        {
            var fullName = sym.ToString();
            var lastDot = fullName.LastIndexOf('.');
            if (lastDot > 0)
            {
                var ns = fullName[..lastDot];
                var type = fullName[(lastDot + 1)..];
                return new ImportClause(ns, [type]);
            }
        }

        throw new AnalyzerException("Invalid import clause");
    }

    #endregion

    #region Primitive Arithmetic Optimization

    /// <summary>
    /// Operators that can be optimized to native C# when types are known
    /// </summary>
    private static readonly HashSet<string> PrimitiveOps = ["+", "-", "*", "/", "<", ">", "<=", ">=", "="];

    /// <summary>
    /// Numeric types that support primitive arithmetic
    /// </summary>
    private static readonly HashSet<string> NumericTypes = ["long", "int", "double", "float", "decimal", "Int64", "Int32", "Double", "Single"];

    /// <summary>
    /// Try to infer the type of an expression
    /// </summary>
    private static string? InferType(Expr expr, AnalyzerContext ctx)
    {
        return expr switch
        {
            // Literals have known types
            LiteralExpr lit => lit.Value switch
            {
                long => "long",
                int => "int",
                double => "double",
                float => "float",
                decimal => "decimal",
                _ => null
            },
            // Symbols with type hints or tracked locals
            SymbolExpr sym when sym.IsLocal && ctx.LocalTypes.TryGetValue(sym.Symbol.Name, out var localType) => localType,
            // Cast expressions have the cast type
            CastExpr cast => cast.TypeName,
            // Primitive ops propagate their type
            PrimitiveOpExpr prim => prim.PrimitiveType,
            _ => null
        };
    }

    /// <summary>
    /// Check if a type is a numeric primitive
    /// </summary>
    private static bool IsNumericType(string? type) => type is not null && NumericTypes.Contains(type);

    /// <summary>
    /// Get the common numeric type for binary operations
    /// </summary>
    private static string? GetCommonNumericType(string? type1, string? type2)
    {
        if (type1 is null || type2 is null) return null;

        // Promotion rules: double > float > decimal > long > int
        if (type1 == "double" || type2 == "double") return "double";
        if (type1 == "Double" || type2 == "Double") return "double";
        if (type1 == "float" || type2 == "float") return "float";
        if (type1 == "Single" || type2 == "Single") return "float";
        if (type1 == "decimal" || type2 == "decimal") return "decimal";
        if (type1 == "long" || type2 == "long") return "long";
        if (type1 == "Int64" || type2 == "Int64") return "long";
        if (type1 == "int" || type2 == "int") return "int";
        if (type1 == "Int32" || type2 == "Int32") return "int";

        return null;
    }

    /// <summary>
    /// Try to optimize an arithmetic/comparison call to primitive operation
    /// </summary>
    private Expr? TryOptimizePrimitiveOp(string opName, IReadOnlyList<Expr> args, AnalyzerContext ctx)
    {
        if (args.Count < 2) return null; // Need at least 2 operands

        // Infer types for all operands
        var types = args.Select(a => InferType(a, ctx)).ToList();

        // Check if all operands have known numeric types
        if (!types.All(IsNumericType)) return null;

        // Get the common type
        var commonType = types.Aggregate(GetCommonNumericType);
        if (commonType is null) return null;

        // Map Clojure op to C# op
        var csOp = opName switch
        {
            "+" => "+",
            "-" => "-",
            "*" => "*",
            "/" => "/",
            "<" => "<",
            ">" => ">",
            "<=" => "<=",
            ">=" => ">=",
            "=" => "==",
            _ => null
        };

        if (csOp is null) return null;

        return new PrimitiveOpExpr(csOp, commonType, args);
    }

    #endregion

    #region Interop Analyzers

    /// <summary>
    /// Parses a method name that may contain generic type arguments.
    /// Handles pipe-escaped symbols: |Method&lt;T&gt;| -> Method, [T]
    /// Input: "Method" -> ("Method", null)
    /// Input: "|Method&lt;String&gt;|" -> ("Method", ["String"])
    /// Input: "Method&lt;T1, T2&gt;" -> ("Method", ["T1", "T2"])
    /// </summary>
    private static (string MethodName, IReadOnlyList<string>? TypeArgs) ParseGenericMethod(string name)
    {
        // Strip pipe escaping if present
        var stripped = name;
        if (name.StartsWith("|") && name.EndsWith("|"))
        {
            stripped = name[1..^1];
        }

        var ltIndex = stripped.IndexOf('<');
        if (ltIndex < 0)
            return (stripped, null);

        var gtIndex = stripped.LastIndexOf('>');
        if (gtIndex < ltIndex)
            return (stripped, null); // Malformed, treat as literal

        var methodName = stripped[..ltIndex];
        var typeArgsStr = stripped[(ltIndex + 1)..gtIndex];

        // Split by comma, handling nested generics
        var typeArgs = new List<string>();
        var depth = 0;
        var start = 0;

        for (int i = 0; i < typeArgsStr.Length; i++)
        {
            var c = typeArgsStr[i];
            if (c == '<') depth++;
            else if (c == '>') depth--;
            else if (c == ',' && depth == 0)
            {
                typeArgs.Add(typeArgsStr[start..i].Trim());
                start = i + 1;
            }
        }

        // Add the last type argument
        if (start < typeArgsStr.Length)
            typeArgs.Add(typeArgsStr[start..].Trim());

        return (methodName, typeArgs.Count > 0 ? typeArgs : null);
    }

    private InstanceMethodExpr AnalyzeInstanceMethod(string methodName, PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("instance method call requires target");

        var target = Analyze(list[1], ctx);
        var args = list.Skip(2).Select(f => Analyze(f, ctx)).ToList();

        // Parse generic type arguments from method name
        var (parsedMethodName, typeArgs) = ParseGenericMethod(methodName);

        return new InstanceMethodExpr(parsedMethodName, target, args, typeArgs);
    }

    private InstancePropertyExpr AnalyzeInstanceProperty(string propName, PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("instance property access requires target");

        var target = Analyze(list[1], ctx);
        return new InstancePropertyExpr(propName, target);
    }

    private StaticMethodExpr AnalyzeStaticMethod(ReaderSymbol sym, PersistentList list, AnalyzerContext ctx)
    {
        var args = list.Skip(1).Select(f => Analyze(f, ctx)).ToList();

        // Parse generic type arguments from method name
        var (parsedMethodName, typeArgs) = ParseGenericMethod(sym.Name);

        return new StaticMethodExpr(sym.Namespace!, parsedMethodName, args, typeArgs);
    }

    private NewExpr AnalyzeConstructor(string typeName, PersistentList list, AnalyzerContext ctx)
    {
        var args = list.Skip(1).Select(f => Analyze(f, ctx)).ToList();
        return new NewExpr(typeName, args);
    }

    private Expr AnalyzeInvoke(PersistentList list, AnalyzerContext ctx)
    {
        var first = list[0];

        // Check for primitive arithmetic optimization
        if (first is ReaderSymbol sym && sym.Namespace is null && PrimitiveOps.Contains(sym.Name))
        {
            // Analyze arguments first
            var args = list.Skip(1).Select(f => Analyze(f, ctx)).ToList();

            // Try to optimize to primitive operation
            var primitiveOp = TryOptimizePrimitiveOp(sym.Name, args, ctx);
            if (primitiveOp is not null)
                return primitiveOp;

            // Fall through to normal invoke if optimization not possible
            var fn = Analyze(first, ctx);
            return new InvokeExpr(fn, args);
        }

        // Standard invocation
        var fnExpr = Analyze(first, ctx);
        var argExprs = list.Skip(1).Select(f => Analyze(f, ctx)).ToList();
        return new InvokeExpr(fnExpr, argExprs);
    }

    #endregion

    #region Macro Expansions

    // (when test body...) -> (if test (do body...) nil)
    private Expr AnalyzeWhen(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("when requires a test");

        var test = Analyze(list[1], ctx);
        var body = list.Count == 3
            ? Analyze(list[2], ctx)
            : new DoExpr(list.Skip(2).Select(f => Analyze(f, ctx)).ToList());

        return new IfExpr(test, body, new LiteralExpr(null));
    }

    // (when-not test body...) -> (if test nil (do body...))
    private Expr AnalyzeWhenNot(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("when-not requires a test");

        var test = Analyze(list[1], ctx);
        var body = list.Count == 3
            ? Analyze(list[2], ctx)
            : new DoExpr(list.Skip(2).Select(f => Analyze(f, ctx)).ToList());

        return new IfExpr(test, new LiteralExpr(null), body);
    }

    // (when-let [binding expr] body...) -> (let [binding expr] (when binding body...))
    private Expr AnalyzeWhenLet(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 3)
            throw new AnalyzerException("when-let requires bindings and body");

        if (list[1] is not PersistentVector bindingsVec || bindingsVec.Count != 2)
            throw new AnalyzerException("when-let requires a single binding pair");

        var bindingName = bindingsVec[0] as ReaderSymbol
            ?? throw new AnalyzerException("when-let binding must be a symbol");
        var bindingExpr = bindingsVec[1];

        PushScope();
        AddLocal(bindingName.Name);

        var init = Analyze(bindingExpr, ctx);
        var body = list.Count == 3
            ? Analyze(list[2], ctx)
            : new DoExpr(list.Skip(2).Select(f => Analyze(f, ctx)).ToList());

        // when binding: (if binding body nil)
        var whenBody = new IfExpr(new SymbolExpr(bindingName, IsLocal: true), body, new LiteralExpr(null));

        PopScope();

        return new LetExpr([(bindingName, init)], whenBody);
    }

    // (if-let [binding expr] then else?) -> (let [binding expr] (if binding then else))
    private Expr AnalyzeIfLet(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 3)
            throw new AnalyzerException("if-let requires bindings and then branch");

        if (list[1] is not PersistentVector bindingsVec || bindingsVec.Count != 2)
            throw new AnalyzerException("if-let requires a single binding pair");

        var bindingName = bindingsVec[0] as ReaderSymbol
            ?? throw new AnalyzerException("if-let binding must be a symbol");
        var bindingExpr = bindingsVec[1];

        PushScope();
        AddLocal(bindingName.Name);

        var init = Analyze(bindingExpr, ctx);
        var thenBranch = Analyze(list[2], ctx);
        var elseBranch = list.Count > 3 ? Analyze(list[3], ctx) : new LiteralExpr(null);

        var ifExpr = new IfExpr(new SymbolExpr(bindingName, IsLocal: true), thenBranch, elseBranch);

        PopScope();

        return new LetExpr([(bindingName, init)], ifExpr);
    }

    // (if-not test then else?) -> (if test else then)
    private Expr AnalyzeIfNot(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 3)
            throw new AnalyzerException("if-not requires test and then branch");

        var test = Analyze(list[1], ctx);
        var thenBranch = Analyze(list[2], ctx);
        var elseBranch = list.Count > 3 ? Analyze(list[3], ctx) : new LiteralExpr(null);

        return new IfExpr(test, elseBranch, thenBranch);
    }

    // (cond test1 expr1 test2 expr2 ...) -> nested if/else
    private Expr AnalyzeCond(PersistentList list, AnalyzerContext ctx)
    {
        var clauses = list.Skip(1).ToList();
        if (clauses.Count == 0)
            return new LiteralExpr(null);

        if (clauses.Count % 2 != 0)
            throw new AnalyzerException("cond requires even number of forms");

        return AnalyzeCondClauses(clauses, 0, ctx);
    }

    private Expr AnalyzeCondClauses(List<object?> clauses, int index, AnalyzerContext ctx)
    {
        if (index >= clauses.Count)
            return new LiteralExpr(null);

        var test = clauses[index];
        var expr = clauses[index + 1];

        // :else is always truthy
        if (test is Keyword kw && kw == Keyword.Intern("else"))
            return Analyze(expr, ctx);

        var testExpr = Analyze(test, ctx);
        var thenExpr = Analyze(expr, ctx);
        var elseExpr = AnalyzeCondClauses(clauses, index + 2, ctx);

        return new IfExpr(testExpr, thenExpr, elseExpr);
    }

    // (and) -> true, (and x) -> x, (and x y ...) -> (if x (and y ...) x)
    private Expr AnalyzeAnd(PersistentList list, AnalyzerContext ctx)
    {
        var args = list.Skip(1).ToList();
        if (args.Count == 0)
            return new LiteralExpr(true);
        if (args.Count == 1)
            return Analyze(args[0], ctx);

        var first = Analyze(args[0], ctx);
        var restList = new PersistentList(new object?[] { ReaderSymbol.Parse("and") }.Concat(args.Skip(1)));
        var rest = AnalyzeAnd(restList, ctx);

        // Use let to avoid evaluating first twice
        var tempSym = ReaderSymbol.Parse($"__and_{Guid.NewGuid():N}");
        PushScope();
        AddLocal(tempSym.Name);
        var tempRef = new SymbolExpr(tempSym, IsLocal: true);
        PopScope();

        return new LetExpr([(tempSym, first)], new IfExpr(tempRef, rest, tempRef));
    }

    // (or) -> nil, (or x) -> x, (or x y ...) -> (if x x (or y ...))
    private Expr AnalyzeOr(PersistentList list, AnalyzerContext ctx)
    {
        var args = list.Skip(1).ToList();
        if (args.Count == 0)
            return new LiteralExpr(null);
        if (args.Count == 1)
            return Analyze(args[0], ctx);

        var first = Analyze(args[0], ctx);
        var restList = new PersistentList(new object?[] { ReaderSymbol.Parse("or") }.Concat(args.Skip(1)));
        var rest = AnalyzeOr(restList, ctx);

        // Use let to avoid evaluating first twice
        var tempSym = ReaderSymbol.Parse($"__or_{Guid.NewGuid():N}");
        PushScope();
        AddLocal(tempSym.Name);
        var tempRef = new SymbolExpr(tempSym, IsLocal: true);
        PopScope();

        return new LetExpr([(tempSym, first)], new IfExpr(tempRef, tempRef, rest));
    }

    // (not x) -> (if x false true)
    private Expr AnalyzeNot(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("not takes exactly one argument");

        var arg = Analyze(list[1], ctx);
        return new IfExpr(arg, new LiteralExpr(false), new LiteralExpr(true));
    }

    // (-> x (f a) (g b)) -> (g (f x a) b)
    private Expr AnalyzeThreadFirst(PersistentList list, AnalyzerContext ctx)
    {
        var forms = list.Skip(1).ToList();
        if (forms.Count == 0)
            throw new AnalyzerException("-> requires at least one form");
        if (forms.Count == 1)
            return Analyze(forms[0], ctx);

        object? result = forms[0];
        foreach (var form in forms.Skip(1))
        {
            result = ThreadFirst(result, form);
        }

        return Analyze(result, ctx);
    }

    private static object? ThreadFirst(object? x, object? form)
    {
        if (form is ReaderSymbol sym)
            return new PersistentList([sym, x]);
        if (form is PersistentList list)
            return new PersistentList(new[] { list[0], x }.Concat(list.Skip(1)));
        throw new AnalyzerException("-> form must be symbol or list");
    }

    // (->> x (f a) (g b)) -> (g b (f a x))
    private Expr AnalyzeThreadLast(PersistentList list, AnalyzerContext ctx)
    {
        var forms = list.Skip(1).ToList();
        if (forms.Count == 0)
            throw new AnalyzerException("->> requires at least one form");
        if (forms.Count == 1)
            return Analyze(forms[0], ctx);

        object? result = forms[0];
        foreach (var form in forms.Skip(1))
        {
            result = ThreadLast(result, form);
        }

        return Analyze(result, ctx);
    }

    private static object? ThreadLast(object? x, object? form)
    {
        if (form is ReaderSymbol sym)
            return new PersistentList([sym, x]);
        if (form is PersistentList list)
            return new PersistentList(list.Concat([x]));
        throw new AnalyzerException("->> form must be symbol or list");
    }

    // (doto x (f a) (g b)) -> (let [__doto x] (f __doto a) (g __doto b) __doto)
    private Expr AnalyzeDoto(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("doto requires at least one form");

        var x = list[1];
        var forms = list.Skip(2).ToList();

        var tempSym = ReaderSymbol.Parse($"__doto_{Guid.NewGuid():N}");
        PushScope();
        AddLocal(tempSym.Name);

        var init = Analyze(x, ctx);

        var bodyExprs = new List<Expr>();
        foreach (var form in forms)
        {
            var threaded = ThreadFirst(tempSym, form);
            bodyExprs.Add(Analyze(threaded, ctx));
        }
        bodyExprs.Add(new SymbolExpr(tempSym, IsLocal: true));

        PopScope();

        return new LetExpr([(tempSym, init)], new DoExpr(bodyExprs));
    }

    // (dotimes [i n] body...) -> (let [n# n] (loop [i 0] (when (< i n#) body... (recur (inc i)))))
    private Expr AnalyzeDotimes(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("dotimes requires bindings");

        if (list[1] is not PersistentVector bindings || bindings.Count != 2)
            throw new AnalyzerException("dotimes requires a binding vector of [var count]");

        var loopVar = bindings[0] as ReaderSymbol
            ?? throw new AnalyzerException("dotimes binding must be a symbol");
        var countExpr = bindings[1];

        // Create unique symbol for count to avoid double evaluation
        var countSym = ReaderSymbol.Parse($"__dotimes_n_{Guid.NewGuid():N}");

        // Build the body forms
        var bodyForms = list.Skip(2).ToList();

        // Build: (recur (inc i))
        var incForm = new PersistentList([ReaderSymbol.Parse("inc"), loopVar]);
        var recurForm = new PersistentList([ReaderSymbol.Parse("recur"), incForm]);

        // Build: (when (< i n#) body... (recur (inc i)))
        var ltForm = new PersistentList([ReaderSymbol.Parse("<"), loopVar, countSym]);
        var whenBody = new List<object?> { ReaderSymbol.Parse("when"), ltForm };
        whenBody.AddRange(bodyForms);
        whenBody.Add(recurForm);
        var whenForm = new PersistentList(whenBody);

        // Build: (loop [i 0] whenForm)
        var loopBindings = new PersistentVector([loopVar, 0L]);
        var loopForm = new PersistentList([ReaderSymbol.Parse("loop"), loopBindings, whenForm]);

        // Build: (let [n# countExpr] loopForm)
        var letBindings = new PersistentVector([countSym, countExpr]);
        var letForm = new PersistentList([ReaderSymbol.Parse("let"), letBindings, loopForm]);

        // Analyze the expanded form
        return Analyze(letForm, ctx);
    }

    #endregion

    #region Macro System Integration

    // (syntax-quote form) -> expand and analyze
    private Expr AnalyzeSyntaxQuote(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("syntax-quote takes exactly one argument");

        // Expand syntax-quote using the macro expander
        var expanded = _macroExpander.Expand(list);
        return Analyze(expanded, ctx);
    }

    // (defmacro name [params] body) -> register macro, return nil
    private Expr AnalyzeDefmacro(PersistentList list, AnalyzerContext ctx)
    {
        // Let the macro expander handle registration
        _macroExpander.Expand(list);
        // defmacro returns nil at runtime
        return new LiteralExpr(null);
    }

    // (macroexpand form) -> expand and quote the result
    private Expr AnalyzeMacroexpand(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("macroexpand takes exactly one argument");

        // The argument should be a quoted form
        var form = list[1];
        if (form is PersistentList quotedList &&
            quotedList.Count == 2 &&
            quotedList[0] is ReaderSymbol { Name: "quote" })
        {
            form = quotedList[1];
        }

        // Expand fully
        var expanded = _macroExpander.Macroexpand(form);

        // Return as a quoted expression (for inspection)
        return new QuoteExpr(expanded);
    }

    // (macroexpand-1 form) -> single expansion step
    private Expr AnalyzeMacroexpandOne(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("macroexpand-1 takes exactly one argument");

        var form = list[1];
        if (form is PersistentList quotedList &&
            quotedList.Count == 2 &&
            quotedList[0] is ReaderSymbol { Name: "quote" })
        {
            form = quotedList[1];
        }

        var expanded = _macroExpander.MacroexpandOne(form);
        return new QuoteExpr(expanded);
    }

    #endregion

    #region C# Interop Special Forms

    // (csharp* "template with ~{expr} interpolation")
    private RawCSharpExpr AnalyzeCSharp(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 2)
            throw new AnalyzerException("csharp* takes exactly one string argument");

        if (list[1] is not string template)
            throw new AnalyzerException("csharp* requires a string template");

        // Parse interpolations: ~{expr} patterns
        var interpolations = new List<(string Placeholder, Expr Value)>();
        var regex = new System.Text.RegularExpressions.Regex(@"~\{([^}]+)\}");
        var matches = regex.Matches(template);

        foreach (System.Text.RegularExpressions.Match match in matches)
        {
            var placeholder = match.Value; // e.g., "~{name}"
            var exprText = match.Groups[1].Value; // e.g., "name"

            // Parse and analyze the expression
            var forms = LispReader.ReadAll(exprText).ToList();
            if (forms.Count != 1)
                throw new AnalyzerException($"Invalid interpolation in csharp*: {placeholder}");

            var expr = Analyze(forms[0], ctx);
            interpolations.Add((placeholder, expr));
        }

        return new RawCSharpExpr(template, interpolations);
    }

    // (defprotocol IName (^RetType method [this ^Type arg]))
    private DefprotocolExpr AnalyzeDefprotocol(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("defprotocol requires a name");

        var name = list[1] as ReaderSymbol
            ?? throw new AnalyzerException("defprotocol name must be a symbol");

        var methods = new List<ProtocolMethod>();

        for (int i = 2; i < list.Count; i++)
        {
            if (list[i] is PersistentList methodList && methodList.Count >= 2)
            {
                var methodName = methodList[0] as ReaderSymbol
                    ?? throw new AnalyzerException("protocol method name must be a symbol");

                // Extract return type from method name metadata
                var returnType = ExtractTypeHint(methodName.Meta);

                // Parse parameter vector
                if (methodList[1] is not PersistentVector paramsVec)
                    throw new AnalyzerException("protocol method requires params vector");

                var methodParams = new List<(ReaderSymbol Name, string? TypeHint)>();
                for (int j = 0; j < paramsVec.Count; j++)
                {
                    if (paramsVec[j] is ReaderSymbol p && p.Name != "this")
                    {
                        methodParams.Add((p, ExtractTypeHint(p.Meta)));
                    }
                }

                methods.Add(new ProtocolMethod(methodName, methodParams, returnType));
            }
        }

        return new DefprotocolExpr(name, methods);
    }

    // (deftype Name [^Type field1 ^Type field2] Interface (method [this args] body))
    private DeftypeExpr AnalyzeDeftype(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 3)
            throw new AnalyzerException("deftype requires name and fields");

        var name = list[1] as ReaderSymbol
            ?? throw new AnalyzerException("deftype name must be a symbol");

        if (list[2] is not PersistentVector fieldsVec)
            throw new AnalyzerException("deftype requires fields vector");

        // Parse fields with type hints and attributes
        var fields = new List<FieldSpec>();
        foreach (var item in fieldsVec)
        {
            if (item is ReaderSymbol fieldSym)
            {
                fields.Add(new FieldSpec(
                    fieldSym,
                    ExtractTypeHint(fieldSym.Meta),
                    ExtractAttributes(fieldSym.Meta)));
            }
        }

        // Parse interfaces and method implementations
        var interfaces = new List<ReaderSymbol>();
        var methods = new List<TypeMethodImpl>();

        for (int i = 3; i < list.Count; i++)
        {
            if (list[i] is ReaderSymbol interfaceSym)
            {
                // Interface name
                interfaces.Add(interfaceSym);
            }
            else if (list[i] is PersistentList methodList && methodList.Count >= 2)
            {
                // Method implementation
                var method = AnalyzeTypeMethod(methodList, fields, ctx);
                methods.Add(method);
            }
        }

        return new DeftypeExpr(name, fields, interfaces, methods);
    }

    // (defrecord Name [^Type field1 ^Type field2] Interface (method [this] body))
    private DefrecordExpr AnalyzeDefrecord(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 3)
            throw new AnalyzerException("defrecord requires name and fields");

        var name = list[1] as ReaderSymbol
            ?? throw new AnalyzerException("defrecord name must be a symbol");

        if (list[2] is not PersistentVector fieldsVec)
            throw new AnalyzerException("defrecord requires fields vector");

        // Parse fields with type hints and attributes
        var fields = new List<FieldSpec>();
        foreach (var item in fieldsVec)
        {
            if (item is ReaderSymbol fieldSym)
            {
                fields.Add(new FieldSpec(
                    fieldSym,
                    ExtractTypeHint(fieldSym.Meta),
                    ExtractAttributes(fieldSym.Meta)));
            }
        }

        // Parse interfaces and method implementations
        var interfaces = new List<ReaderSymbol>();
        var methods = new List<TypeMethodImpl>();

        for (int i = 3; i < list.Count; i++)
        {
            if (list[i] is ReaderSymbol interfaceSym)
            {
                interfaces.Add(interfaceSym);
            }
            else if (list[i] is PersistentList methodList && methodList.Count >= 2)
            {
                var method = AnalyzeTypeMethod(methodList, fields, ctx);
                methods.Add(method);
            }
        }

        return new DefrecordExpr(name, fields, interfaces, methods);
    }

    private TypeMethodImpl AnalyzeTypeMethod(
        PersistentList methodList,
        IReadOnlyList<FieldSpec> typeFields,
        AnalyzerContext ctx)
    {
        var methodName = methodList[0] as ReaderSymbol
            ?? throw new AnalyzerException("method name must be a symbol");

        // Extract return type and attributes from method name metadata
        var returnType = ExtractTypeHint(methodName.Meta);
        var attributes = ExtractAttributes(methodName.Meta);

        // Auto-async for Task<T> return types
        var isAsync = returnType?.StartsWith("Task") == true;

        if (methodList[1] is not PersistentVector paramsVec)
            throw new AnalyzerException("method requires params vector");

        var parameters = new List<ReaderSymbol>();
        var paramTypes = new List<string?>();

        for (int i = 0; i < paramsVec.Count; i++)
        {
            if (paramsVec[i] is ReaderSymbol p)
            {
                if (p.Name == "this") continue; // Skip 'this' parameter
                parameters.Add(p);
                paramTypes.Add(ExtractTypeHint(p.Meta));
            }
        }

        // Add fields to scope for the method body
        PushScope();
        foreach (var field in typeFields)
            AddLocal(field.Name.Name);
        foreach (var p in parameters)
            AddLocal(p.Name);

        // Parse method body
        var bodyForms = methodList.Skip(2).ToList();
        var body = bodyForms.Count == 1
            ? Analyze(bodyForms[0], ctx with { IsAsync = isAsync })
            : new DoExpr(bodyForms.Select(f => Analyze(f, ctx with { IsAsync = isAsync })).ToList());

        PopScope();

        var hasAnyParamTypes = paramTypes.Any(t => t is not null);

        return new TypeMethodImpl(
            methodName,
            parameters,
            body,
            returnType,
            hasAnyParamTypes ? paramTypes : null,
            attributes
        );
    }

    #endregion

    #region Testing Special Forms

    /// <summary>
    /// Analyze (deftest test-name body...)
    /// Creates a test function that tracks assertions and returns TestRunResult
    /// </summary>
    private DefTestExpr AnalyzeDeftest(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 3)
            throw new AnalyzerException("deftest requires name and body");

        var name = list[1] as ReaderSymbol
            ?? throw new AnalyzerException("deftest name must be a symbol");

        // Body is all forms after name, wrapped in implicit do
        var bodyForms = list.Skip(2).ToList();
        var body = bodyForms.Count == 1
            ? Analyze(bodyForms[0], ctx)
            : new DoExpr(bodyForms.Select(f => Analyze(f, ctx)).ToList());

        return new DefTestExpr(name, body);
    }

    /// <summary>
    /// Analyze (is expr) or (is (= expected actual)) or (is expr "message")
    /// Records assertion result for test tracking
    /// </summary>
    private IsExpr AnalyzeIs(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count < 2)
            throw new AnalyzerException("is requires an expression");

        var expr = list[1];
        Expr? message = list.Count > 2 ? Analyze(list[2], ctx) : null;

        // Check if expression is (= expected actual)
        if (expr is PersistentList eqList && eqList.Count == 3 &&
            eqList[0] is ReaderSymbol sym && sym.Name == "=")
        {
            var expected = Analyze(eqList[1], ctx);
            var actual = Analyze(eqList[2], ctx);
            // For equality checks, the test is (= expected actual)
            var test = Analyze(expr, ctx);
            return new IsExpr(test, expected, actual, message);
        }

        // Regular truthy check
        return new IsExpr(Analyze(expr, ctx), null, null, message);
    }

    /// <summary>
    /// Analyze (instance? Type value)
    /// Returns true if value is an instance of Type
    /// </summary>
    private InstanceCheckExpr AnalyzeInstanceCheck(PersistentList list, AnalyzerContext ctx)
    {
        if (list.Count != 3)
            throw new AnalyzerException("instance? requires exactly 2 arguments: type and value");

        // First argument is the type (a symbol)
        var typeArg = list[1];
        string typeName;

        if (typeArg is ReaderSymbol typeSym)
        {
            typeName = typeSym.Namespace is not null
                ? $"{typeSym.Namespace}.{typeSym.Name}"
                : typeSym.Name;
        }
        else if (IsSymbol(typeArg))
        {
            var ns = GetSymbolNamespace(typeArg);
            var name = GetSymbolName(typeArg);
            typeName = ns is not null ? $"{ns}.{name}" : name ?? throw new AnalyzerException("instance? type has no name");
        }
        else
        {
            throw new AnalyzerException("instance? first argument must be a type symbol");
        }

        // Normalize type name (Char -> char, String -> string, etc.)
        typeName = NormalizeTypeName(typeName);

        // Second argument is the value to check
        var value = Analyze(list[2], ctx);

        return new InstanceCheckExpr(typeName, value);
    }

    #endregion
}

public record AnalyzerContext
{
    public bool IsAsync { get; init; }
    public bool IsRepl { get; init; }
    public string? ReturnType { get; init; }

    /// <summary>
    /// Known types of local variables (name -> type)
    /// Used for primitive arithmetic optimization
    /// </summary>
    public IReadOnlyDictionary<string, string> LocalTypes { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Add a local with known type
    /// </summary>
    public AnalyzerContext WithLocalType(string name, string type)
    {
        var newTypes = new Dictionary<string, string>();
        foreach (var kvp in LocalTypes)
            newTypes[kvp.Key] = kvp.Value;
        newTypes[name] = type;
        return this with { LocalTypes = newTypes };
    }

    /// <summary>
    /// Add multiple locals with known types
    /// </summary>
    public AnalyzerContext WithLocalTypes(IEnumerable<(string Name, string Type)> types)
    {
        var newTypes = new Dictionary<string, string>();
        foreach (var kvp in LocalTypes)
            newTypes[kvp.Key] = kvp.Value;
        foreach (var (name, type) in types)
            newTypes[name] = type;
        return this with { LocalTypes = newTypes };
    }
}

public class AnalyzerException : Exception
{
    public AnalyzerException(string message) : base(message) { }
}
