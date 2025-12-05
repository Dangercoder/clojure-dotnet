using System.Text;
using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Reader;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;

namespace Cljr.Compiler.Emitter;

/// <summary>
/// Emits C# source code from analyzed Clojure expressions
/// </summary>
public class CSharpEmitter
{
    private readonly StringBuilder _sb = new();
    private int _indent = 0;
    private int _tempVarCounter = 0;
    private string? _currentNamespace;
    private bool _isReplMode;
    private readonly HashSet<string> _locals = new();
    private readonly Dictionary<string, string> _localTypes = new();  // local name -> type hint (for typed params)
    private IReadOnlyDictionary<string, string>? _aliases;  // alias -> full namespace
    private IReadOnlyDictionary<string, string>? _refers;   // var name -> source namespace
    private Func<string, string>? _typeResolver;  // Resolves type names for namespace isolation

    // Loop context for proper recur emission
    private List<string>? _loopBindings;  // Names of loop bindings in order
    private string? _loopResultVar;       // Result variable for the current loop

    /// <summary>
    /// When true, emit Var-based code for dev mode (enables live function redefinition).
    /// Functions are stored in Vars and calls go through Var.Invoke().
    /// </summary>
    public bool UseVarBasedCodegen { get; set; }

    // Clojure namespace name for Var-based codegen (e.g., "my-app.core")
    private string? _clojureNamespace;

    /// <summary>
    /// Emit a complete compilation unit to C# source
    /// </summary>
    public string Emit(CompilationUnit unit)
    {
        _sb.Clear();
        _indent = 0;

        // Check if this is a test file (contains deftest forms)
        var hasTests = unit.TopLevelExprs.Any(e => e is DefTestExpr);

        // Emit usings
        EmitLine("using System;");
        EmitLine("using System.Collections.Generic;");
        EmitLine("using System.Threading.Tasks;");
        EmitLine("using Cljr;");
        EmitLine("using Cljr.Collections;");
        EmitLine("using static Cljr.Core;");
        if (hasTests)
        {
            EmitLine("using Microsoft.VisualStudio.TestTools.UnitTesting;");
        }
        EmitLine();

        // Get namespace info
        var ns = unit.Namespace;
        var csNamespace = ns?.Name.Replace("-", "").Split('.')
            .Select(s => char.ToUpper(s[0]) + s[1..])
            .Aggregate((a, b) => $"{a}.{b}") ?? "User";

        var className = ns?.Name.Split('.').Last() ?? "Program";
        className = char.ToUpper(className[0]) + className[1..].Replace("-", "");

        // Avoid class name collision with Main method (from -main function)
        if (className == "Main")
            className = "Program";

        // Emit imports from ns
        if (ns is not null)
        {
            foreach (var import in ns.Imports)
            {
                EmitLine($"using {import.Namespace};");
            }
            if (ns.Imports.Count > 0) EmitLine();
        }

        // Emit namespace and class
        EmitLine($"namespace {csNamespace}");
        EmitLine("{");
        _indent++;

        // For test classes, add [TestClass] attribute and make non-static
        if (hasTests)
        {
            EmitLine("[TestClass]");
            EmitLine($"public class {className}");
        }
        else
        {
            EmitLine($"public static class {className}");
        }
        EmitLine("{");
        _indent++;

        // Store Clojure namespace for Var-based codegen
        _clojureNamespace = ns?.Name ?? "user";

        // Emit top-level expressions
        foreach (var expr in unit.TopLevelExprs)
        {
            EmitTopLevel(expr);
            EmitLine();
        }

        _indent--;
        EmitLine("}");
        _indent--;
        EmitLine("}");

        return _sb.ToString();
    }

    /// <summary>
    /// Emit a complete compilation unit to C# source with proper using statements for requires.
    /// Used by the source generator for multi-file compilation.
    /// </summary>
    public string EmitWithRequires(CompilationUnit unit, Cljr.Compiler.Namespace.NamespaceRegistry registry)
    {
        _sb.Clear();
        _indent = 0;

        // Check if this is a test file (contains deftest forms)
        var hasTests = unit.TopLevelExprs.Any(e => e is DefTestExpr);

        // Emit standard usings
        EmitLine("using System;");
        EmitLine("using System.Collections.Generic;");
        EmitLine("using System.Threading.Tasks;");
        EmitLine("using Cljr;");
        EmitLine("using Cljr.Collections;");
        EmitLine("using static Cljr.Core;");
        if (hasTests)
        {
            EmitLine("using Microsoft.VisualStudio.TestTools.UnitTesting;");
        }

        // Emit usings for required namespaces
        var ns = unit.Namespace;
        if (ns != null)
        {
            foreach (var req in ns.Requires)
            {
                var nsInfo = registry.Get(req.Namespace);
                if (nsInfo != null)
                {
                    var fullName = $"{nsInfo.CSharpNamespace}.{nsInfo.CSharpClass}";

                    // If there's an alias, emit: using alias = Namespace.Class;
                    if (req.Alias != null)
                    {
                        EmitLine($"using {MungeName(req.Alias)} = {fullName};");
                    }

                    // If there are refers or it's a bare require, emit: using static Namespace.Class;
                    if (req.Refers != null && req.Refers.Count > 0)
                    {
                        EmitLine($"using static {fullName};");
                    }
                    else if (req.Alias == null)
                    {
                        // Bare require without alias - import as static
                        EmitLine($"using static {fullName};");
                    }
                }
            }
        }
        EmitLine();

        // Get namespace info
        var csNamespace = ns?.Name.Replace("-", "").Split('.')
            .Select(s => char.ToUpper(s[0]) + s.Substring(1))
            .Aggregate((a, b) => $"{a}.{b}") ?? "User";

        var className = ns?.Name.Split('.').Last() ?? "Program";
        className = char.ToUpper(className[0]) + className.Substring(1).Replace("-", "");

        // Avoid class name collision with Main method (from -main function)
        if (className == "Main")
            className = "Program";

        // Emit imports from ns
        if (ns is not null)
        {
            foreach (var import in ns.Imports)
            {
                EmitLine($"using {import.Namespace};");
            }
            if (ns.Imports.Count > 0) EmitLine();
        }

        // Emit namespace and class
        EmitLine($"namespace {csNamespace}");
        EmitLine("{");
        _indent++;

        // For test classes, add [TestClass] attribute and make non-static
        if (hasTests)
        {
            EmitLine("[TestClass]");
            EmitLine($"public class {className}");
        }
        else
        {
            EmitLine($"public static class {className}");
        }
        EmitLine("{");
        _indent++;

        // Store Clojure namespace for Var-based codegen (hot-reload)
        _clojureNamespace = ns?.Name ?? "user";

        // Emit top-level expressions
        foreach (var expr in unit.TopLevelExprs)
        {
            EmitTopLevel(expr);
            EmitLine();
        }

        _indent--;
        EmitLine("}");
        _indent--;
        EmitLine("}");

        return _sb.ToString();
    }

    /// <summary>
    /// Emit a complete compilation unit as executable script code (no namespace)
    /// </summary>
    public string EmitAsScript(CompilationUnit unit)
    {
        _sb.Clear();
        _indent = 0;

        // Emit usings as script globals
        EmitLine("using System;");
        EmitLine("using System.Collections.Generic;");
        EmitLine("using System.Threading.Tasks;");
        EmitLine("using Cljr;");
        EmitLine("using Cljr.Collections;");
        EmitLine("using static Cljr.Core;");
        EmitLine();

        // Get class name from namespace
        var ns = unit.Namespace;
        var className = ns?.Name.Split('.').Last() ?? "Program";
        className = char.ToUpper(className[0]) + className[1..].Replace("-", "");

        // Emit as a static class without namespace
        EmitLine($"public static class {className}");
        EmitLine("{");
        _indent++;

        // Emit top-level expressions
        foreach (var expr in unit.TopLevelExprs)
        {
            EmitTopLevel(expr);
            EmitLine();
        }

        _indent--;
        EmitLine("}");

        return _sb.ToString();
    }

    /// <summary>
    /// Emit a single expression as a C# script fragment (for REPL)
    /// </summary>
    public string EmitScript(Expr expr) => EmitScript(expr, "user", null);

    /// <summary>
    /// Emit a single expression as a C# script fragment (for REPL) with namespace context
    /// </summary>
    public string EmitScript(Expr expr, string currentNamespace) => EmitScript(expr, currentNamespace, null);

    /// <summary>
    /// Emit a single expression as a C# script fragment (for REPL) with namespace context and aliases
    /// </summary>
    public string EmitScript(Expr expr, string currentNamespace, IReadOnlyDictionary<string, string>? aliases)
        => EmitScript(expr, currentNamespace, aliases, null);

    /// <summary>
    /// Emit a single expression as a C# script fragment (for REPL) with namespace context, aliases, and refers
    /// </summary>
    public string EmitScript(Expr expr, string currentNamespace, IReadOnlyDictionary<string, string>? aliases, IReadOnlyDictionary<string, string>? refers)
        => EmitScript(expr, currentNamespace, aliases, refers, null);

    /// <summary>
    /// Emit a single expression as a C# script fragment (for REPL) with namespace context, aliases, refers, and type resolver
    /// </summary>
    public string EmitScript(Expr expr, string currentNamespace, IReadOnlyDictionary<string, string>? aliases, IReadOnlyDictionary<string, string>? refers, Func<string, string>? typeResolver)
    {
        _sb.Clear();
        _indent = 0;
        _currentNamespace = currentNamespace;
        _isReplMode = true;
        _aliases = aliases;
        _refers = refers;
        _typeResolver = typeResolver;
        _locals.Clear();

        if (expr is DefExpr def)
        {
            EmitDefAsScript(def);
        }
        else
        {
            EmitExpr(expr, ExprContext.Return);
        }

        _isReplMode = false;
        _aliases = null;
        _typeResolver = null;
        return _sb.ToString();
    }

    #region Top-Level Emitters

    private void EmitTopLevel(Expr expr)
    {
        switch (expr)
        {
            case DefExpr def:
                if (UseVarBasedCodegen)
                    EmitDefAsVar(def);
                else
                    EmitDef(def);
                break;
            case DefrecordExpr defrecord:
                EmitDefrecord(defrecord, ExprContext.Statement);
                break;
            case DeftypeExpr deftype:
                EmitDeftype(deftype, ExprContext.Statement);
                break;
            case DefprotocolExpr defprotocol:
                EmitDefprotocol(defprotocol, ExprContext.Statement);
                break;
            case DefTestExpr defTest:
                EmitDefTest(defTest, ExprContext.Statement);
                break;
            default:
                // Wrap in static initializer or Main
                EmitLine("// Top-level expression");
                Emit("static object _init = ");
                EmitExpr(expr, ExprContext.Expression);
                EmitLine(";");
                break;
        }
    }

    private void EmitDef(DefExpr def)
    {
        if (def.DocString is not null)
        {
            EmitDocComment(def.DocString);
        }

        if (def.Init is FnExpr fn)
        {
            EmitDefn(def.Name, fn, def.IsPrivate);
        }
        else
        {
            // Use type hint if provided, otherwise default to object
            var typeName = def.TypeHint ?? "object";
            var visibility = def.IsPrivate ? "private" : "public";
            Emit($"{visibility} static {typeName} {MungeName(def.Name.Name)}");
            if (def.Init is not null)
            {
                Emit(" = ");
                // Cast init expression if type hint is provided
                if (def.TypeHint is not null)
                    Emit($"({def.TypeHint})");
                EmitExpr(def.Init, ExprContext.Expression);
            }
            EmitLine(";");
        }
    }

    private void EmitDefn(ReaderSymbol name, FnExpr fn, bool isPrivate = false)
    {
        foreach (var method in fn.Methods)
        {
            var isAsync = fn.IsAsync;
            var methodName = MungeName(name.Name);

            // Special handling for -main: generate proper entry point signature
            var isMain = name.Name == "-main";
            if (isMain)
            {
                // -main becomes Main with proper entry point signature
                methodName = "Main";
            }

            // Determine return type
            string returnType;
            if (isMain)
            {
                // Entry point must return void
                returnType = "void";
            }
            else if (method.ReturnType is not null)
            {
                // Use explicit type hint
                returnType = isAsync && !method.ReturnType.StartsWith("Task", StringComparison.Ordinal)
                    ? $"async Task<{method.ReturnType}>"
                    : isAsync ? $"async {method.ReturnType}" : method.ReturnType;
            }
            else
            {
                // Default to object
                returnType = isAsync ? "async Task<object?>" : "object?";
            }

            // Build parameter list with type hints
            var paramList = new List<string>();
            for (int i = 0; i < method.Params.Count; i++)
            {
                var p = method.Params[i];
                string paramType;
                if (isMain && i == 0)
                {
                    // Entry point first param is always string[]
                    paramType = "string[]";
                }
                else
                {
                    paramType = method.ParamTypes?[i] ?? "object?";
                }
                paramList.Add($"{paramType} {MungeName(p.Name)}");
            }
            if (method.RestParam is not null)
            {
                if (isMain)
                {
                    // Entry point rest param becomes string[] args
                    paramList.Add($"string[] {MungeName(method.RestParam.Name)}");
                }
                else
                {
                    paramList.Add($"params object?[] {MungeName(method.RestParam.Name)}");
                }
            }

            // Use private visibility for defn- functions
            var visibility = isPrivate ? "private" : "public";
            EmitLine($"{visibility} static {returnType} {methodName}({string.Join(", ", paramList)})");
            EmitLine("{");
            _indent++;

            if (isMain)
            {
                // For entry point, emit body as statement (don't return)
                EmitExpr(method.Body, ExprContext.Statement);
            }
            else
            {
                EmitExpr(method.Body, ExprContext.Return);
            }

            _indent--;
            EmitLine("}");
        }
    }

    /// <summary>
    /// Emit a def as Var-based code for dev mode (enables live function redefinition).
    /// Generates:
    /// 1. A static Var field initialized with the function value
    /// 2. A public static method that calls through the Var
    /// </summary>
    private void EmitDefAsVar(DefExpr def)
    {
        var ns = _clojureNamespace ?? "user";
        var varName = def.Name.Name;
        var fieldName = $"_var_{MungeName(varName)}";

        if (def.DocString is not null)
        {
            EmitLine("/// <summary>");
            EmitLine($"/// {def.DocString}");
            EmitLine("/// </summary>");
        }

        if (def.Init is FnExpr fn)
        {
            EmitDefnAsVar(def.Name, fn, ns, fieldName);
        }
        else
        {
            // Non-function def: emit Var field with value
            Emit($"private static readonly Var {fieldName} = Var.Intern(\"{ns}\", \"{varName}\").BindRoot(");
            if (def.Init is not null)
            {
                EmitExpr(def.Init, ExprContext.Expression);
            }
            else
            {
                Emit("null");
            }
            EmitLine(");");
            EmitLine();

            // Emit property that reads from Var
            var typeName = def.TypeHint ?? "object?";
            EmitLine($"public static {typeName} {MungeName(varName)} => ({typeName}){fieldName}.Deref();");
        }
    }

    /// <summary>
    /// Emit a function as Var-based code for dev mode (enables hot-reload via REPL).
    /// Uses casts inside the lambda to handle typed parameters while keeping the Var rebindable.
    /// </summary>
    private void EmitDefnAsVar(ReaderSymbol name, FnExpr fn, string ns, string fieldName)
    {
        var methodName = MungeName(name.Name);

        // Special handling for -main: skip Var-based codegen, use normal emission
        // Entry point must be a real method, not Var-based
        if (name.Name == "-main")
        {
            EmitDefn(name, fn);
            return;
        }

        // Multi-arity functions need special handling
        if (fn.Methods.Count > 1)
        {
            EmitMultiArityDefnAsVar(name, fn, ns, fieldName);
            return;
        }

        var method = fn.Methods[0];
        EmitSingleArityDefnAsVar(name, method, fn.IsAsync, ns, fieldName);
    }

    /// <summary>
    /// Emit a single-arity function as Var-based code with typed parameter casts inside lambda.
    /// </summary>
    private void EmitSingleArityDefnAsVar(ReaderSymbol name, FnMethod method, bool isAsync, string ns, string fieldName)
    {
        var methodName = MungeName(name.Name);

        // Check if any params have type hints (need casts)
        var hasTypedParams = method.ParamTypes?.Any(t => t is not null && t != "object?" && t != "object") == true;

        // Build lambda parameter names - use placeholders if typed params need casting
        var lambdaParamNames = new List<string>();
        var actualParamNames = method.Params.Select(p => MungeName(p.Name)).ToList();

        for (int i = 0; i < method.Params.Count; i++)
        {
            var typeHint = method.ParamTypes is not null && i < method.ParamTypes.Count ? method.ParamTypes[i] : null;
            var needsCast = typeHint is not null && typeHint != "object?" && typeHint != "object";
            lambdaParamNames.Add(needsCast ? $"_p{i}" : actualParamNames[i]);
        }

        if (method.RestParam is not null)
        {
            lambdaParamNames.Add(MungeName(method.RestParam.Name));
            actualParamNames.Add(MungeName(method.RestParam.Name));
        }

        // Track params as locals for the lambda body (use actual names, they'll be aliased via cast)
        var addedLocals = new List<string>();
        foreach (var p in method.Params)
        {
            _locals.Add(p.Name);
            addedLocals.Add(p.Name);
        }
        if (method.RestParam is not null)
        {
            _locals.Add(method.RestParam.Name);
            addedLocals.Add(method.RestParam.Name);
        }

        // Build Func type (always object? params)
        var paramCount = lambdaParamNames.Count;
        var funcType = paramCount switch
        {
            0 => "Func<object?>",
            1 => "Func<object?, object?>",
            2 => "Func<object?, object?, object?>",
            3 => "Func<object?, object?, object?, object?>",
            4 => "Func<object?, object?, object?, object?, object?>",
            _ => $"Func<{string.Join(", ", Enumerable.Repeat("object?", paramCount + 1))}>"
        };

        // Emit static Var field with lambda
        Emit($"private static readonly Var {fieldName} = Var.Intern(\"{ns}\", \"{name.Name}\").BindRoot(({funcType})(");
        if (paramCount == 1)
            Emit($"{lambdaParamNames[0]} => ");
        else
            Emit($"({string.Join(", ", lambdaParamNames)}) => ");

        EmitLine("{");
        _indent++;

        // Emit casts for typed parameters at start of lambda body
        for (int i = 0; i < method.Params.Count; i++)
        {
            var typeHint = method.ParamTypes is not null && i < method.ParamTypes.Count ? method.ParamTypes[i] : null;
            var needsCast = typeHint is not null && typeHint != "object?" && typeHint != "object";
            if (needsCast)
            {
                EmitLine($"var {actualParamNames[i]} = ({typeHint}){lambdaParamNames[i]};");
            }
        }

        EmitExpr(method.Body, ExprContext.Return);
        _indent--;
        EmitLine("}));");
        EmitLine();

        // Remove params from locals
        foreach (var local in addedLocals)
            _locals.Remove(local);

        // Emit public method that calls through Var (with typed signature for callers)
        EmitTypedPublicMethod(name.Name, methodName, method, isAsync, fieldName);
    }

    /// <summary>
    /// Emit a multi-arity function as Var-based code using Func&lt;object?[], object?&gt; with switch dispatch.
    /// </summary>
    private void EmitMultiArityDefnAsVar(ReaderSymbol name, FnExpr fn, string ns, string fieldName)
    {
        var methodName = MungeName(name.Name);

        // Emit static Var field with dispatcher lambda
        EmitLine($"private static readonly Var {fieldName} = Var.Intern(\"{ns}\", \"{name.Name}\").BindRoot((Func<object?[], object?>)(__args__ => {{");
        _indent++;

        // Sort methods by param count
        var methods = fn.Methods.OrderBy(m => m.Params.Count + (m.RestParam is not null ? 1 : 0)).ToList();
        var variadicMethod = methods.FirstOrDefault(m => m.RestParam is not null);

        // Emit switch on args.Length
        EmitLine("switch (__args__.Length)");
        EmitLine("{");
        _indent++;

        foreach (var method in methods)
        {
            var paramCount = method.Params.Count;
            var hasRest = method.RestParam is not null;

            if (hasRest)
            {
                // Variadic method handles paramCount or more args via default case
                continue;
            }

            EmitLine($"case {paramCount}:");
            EmitLine("{");
            _indent++;

            // Track params as locals
            var addedLocals = new List<string>();
            for (int i = 0; i < method.Params.Count; i++)
            {
                var p = method.Params[i];
                _locals.Add(p.Name);
                addedLocals.Add(p.Name);
                var typeHint = method.ParamTypes is not null && i < method.ParamTypes.Count ? method.ParamTypes[i] : null;
                if (typeHint is not null && typeHint != "object?" && typeHint != "object")
                    EmitLine($"var {MungeName(p.Name)} = ({typeHint})__args__[{i}];");
                else
                    EmitLine($"var {MungeName(p.Name)} = __args__[{i}];");
            }

            EmitExpr(method.Body, ExprContext.Return);

            foreach (var local in addedLocals)
                _locals.Remove(local);

            _indent--;
            EmitLine("}");
        }

        // Default case - either variadic or error
        EmitLine("default:");
        EmitLine("{");
        _indent++;

        if (variadicMethod is not null)
        {
            var addedLocals = new List<string>();
            var fixedCount = variadicMethod.Params.Count;

            for (int i = 0; i < variadicMethod.Params.Count; i++)
            {
                var p = variadicMethod.Params[i];
                _locals.Add(p.Name);
                addedLocals.Add(p.Name);
                var typeHint = variadicMethod.ParamTypes is not null && i < variadicMethod.ParamTypes.Count ? variadicMethod.ParamTypes[i] : null;
                if (typeHint is not null && typeHint != "object?" && typeHint != "object")
                    EmitLine($"var {MungeName(p.Name)} = ({typeHint})__args__[{i}];");
                else
                    EmitLine($"var {MungeName(p.Name)} = __args__[{i}];");
            }

            var restParam = variadicMethod.RestParam!;
            _locals.Add(restParam.Name);
            addedLocals.Add(restParam.Name);
            EmitLine($"var {MungeName(restParam.Name)} = __args__.Skip({fixedCount}).ToArray();");

            EmitExpr(variadicMethod.Body, ExprContext.Return);

            foreach (var local in addedLocals)
                _locals.Remove(local);
        }
        else
        {
            var arities = string.Join(", ", methods.Select(m => m.Params.Count.ToString()));
            EmitLine($"throw new ArgumentException($\"Wrong number of args ({{__args__.Length}}) passed to {name.Name}. Expected: {arities}\");");
        }

        _indent--;
        EmitLine("}");

        _indent--;
        EmitLine("}"); // end switch

        _indent--;
        EmitLine("}));"); // end lambda and BindRoot
        EmitLine();

        // Emit public method overloads for each arity
        foreach (var method in fn.Methods)
        {
            EmitTypedPublicMethodForMultiArity(name.Name, methodName, method, fn.IsAsync, fieldName);
        }
    }

    /// <summary>
    /// Emit a typed public method that calls through the Var.
    /// </summary>
    private void EmitTypedPublicMethod(string clojureName, string methodName, FnMethod method, bool isAsync, string fieldName)
    {
        string returnType;
        if (method.ReturnType is not null)
        {
            returnType = isAsync && !method.ReturnType.StartsWith("Task", StringComparison.Ordinal)
                ? $"async Task<{method.ReturnType}>"
                : isAsync ? $"async {method.ReturnType}" : method.ReturnType;
        }
        else
        {
            returnType = isAsync ? "async Task<object?>" : "object?";
        }

        // Build parameter list with type hints
        var paramList = new List<string>();
        var argNames = new List<string>();
        for (int i = 0; i < method.Params.Count; i++)
        {
            var paramType = method.ParamTypes?[i] ?? "object?";
            var paramName = MungeName(method.Params[i].Name);
            paramList.Add($"{paramType} {paramName}");
            argNames.Add(paramName);
        }
        if (method.RestParam is not null)
        {
            var restName = MungeName(method.RestParam.Name);
            paramList.Add($"params object?[] {restName}");
            argNames.Add(restName);
        }

        EmitLine($"public static {returnType} {methodName}({string.Join(", ", paramList)})");
        EmitLine("{");
        _indent++;

        var argList = string.Join(", ", argNames);
        if (isAsync)
        {
            EmitLine($"return await (Task<object?>){fieldName}.Invoke({argList});");
        }
        else
        {
            EmitLine($"return {fieldName}.Invoke({argList});");
        }

        _indent--;
        EmitLine("}");
    }

    /// <summary>
    /// Emit a typed public method overload for multi-arity functions.
    /// </summary>
    private void EmitTypedPublicMethodForMultiArity(string clojureName, string methodName, FnMethod method, bool isAsync, string fieldName)
    {
        string returnType;
        if (method.ReturnType is not null)
        {
            returnType = isAsync && !method.ReturnType.StartsWith("Task", StringComparison.Ordinal)
                ? $"async Task<{method.ReturnType}>"
                : isAsync ? $"async {method.ReturnType}" : method.ReturnType;
        }
        else
        {
            returnType = isAsync ? "async Task<object?>" : "object?";
        }

        // Build parameter list with type hints
        var paramList = new List<string>();
        var argNames = new List<string>();
        for (int i = 0; i < method.Params.Count; i++)
        {
            var paramType = method.ParamTypes?[i] ?? "object?";
            var paramName = MungeName(method.Params[i].Name);
            paramList.Add($"{paramType} {paramName}");
            argNames.Add(paramName);
        }
        if (method.RestParam is not null)
        {
            var restName = MungeName(method.RestParam.Name);
            paramList.Add($"params object?[] {restName}");
            argNames.Add(restName);
        }

        EmitLine($"public static {returnType} {methodName}({string.Join(", ", paramList)})");
        EmitLine("{");
        _indent++;

        // For multi-arity, we call with an object?[] array
        if (argNames.Count == 0)
        {
            if (isAsync)
                EmitLine($"return await (Task<object?>){fieldName}.Invoke(Array.Empty<object?>());");
            else
                EmitLine($"return {fieldName}.Invoke(Array.Empty<object?>());");
        }
        else
        {
            // Combine fixed args + rest args into array
            var hasRest = method.RestParam is not null;
            if (hasRest)
            {
                var fixedArgs = argNames.Take(argNames.Count - 1).ToList();
                var restArg = argNames.Last();
                if (fixedArgs.Count == 0)
                {
                    if (isAsync)
                        EmitLine($"return await (Task<object?>){fieldName}.Invoke({restArg});");
                    else
                        EmitLine($"return {fieldName}.Invoke({restArg});");
                }
                else
                {
                    if (isAsync)
                        EmitLine($"return await (Task<object?>){fieldName}.Invoke(new object?[] {{ {string.Join(", ", fixedArgs)} }}.Concat({restArg}).ToArray());");
                    else
                        EmitLine($"return {fieldName}.Invoke(new object?[] {{ {string.Join(", ", fixedArgs)} }}.Concat({restArg}).ToArray());");
                }
            }
            else
            {
                if (isAsync)
                    EmitLine($"return await (Task<object?>){fieldName}.Invoke(new object?[] {{ {string.Join(", ", argNames)} }});");
                else
                    EmitLine($"return {fieldName}.Invoke(new object?[] {{ {string.Join(", ", argNames)} }});");
            }
        }

        _indent--;
        EmitLine("}");
    }

    private void EmitDefAsScript(DefExpr def)
    {
        var ns = _currentNamespace ?? "user";
        var varName = def.Name.Name;

        if (def.Init is FnExpr fn)
        {
            // Handle multi-arity functions
            if (fn.Methods.Count > 1)
            {
                EmitMultiArityDefAsScript(ns, varName, fn);
                return;
            }

            // Store function in Var for namespace isolation and hot-reload
            var method = fn.Methods[0];

            // Build parameter list for lambda
            var paramList = method.Params.Select(p => MungeName(p.Name));
            if (method.RestParam is not null)
            {
                paramList = paramList.Append(MungeName(method.RestParam.Name));
            }

            // Track params as locals for the lambda body, including type hints
            var addedLocals = new List<string>();
            var addedTypes = new List<string>();
            for (int i = 0; i < method.Params.Count; i++)
            {
                var p = method.Params[i];
                _locals.Add(p.Name);
                addedLocals.Add(p.Name);
                // Track type hints for typed parameters (enables proper interop method calls)
                if (method.ParamTypes is not null && i < method.ParamTypes.Count && method.ParamTypes[i] is not null)
                {
                    _localTypes[p.Name] = method.ParamTypes[i]!;
                    addedTypes.Add(p.Name);
                }
            }
            if (method.RestParam is not null)
            {
                _locals.Add(method.RestParam.Name);
                addedLocals.Add(method.RestParam.Name);
            }

            // Build Func type based on param count (e.g., Func<object?, object?> for 1 param)
            var paramCount = method.Params.Count + (method.RestParam is not null ? 1 : 0);

            if (fn.IsAsync)
            {
                // Emit AsyncFn wrapper for async functions - preserves typed delegate for .NET interop
                var asyncFnType = paramCount switch
                {
                    0 => "AsyncFn0",
                    1 => "AsyncFn1",
                    2 => "AsyncFn2",
                    3 => "AsyncFn3",
                    4 => "AsyncFn4",
                    _ => throw new NotSupportedException($"Async functions with {paramCount} parameters not yet supported (max 4)")
                };

                // Emit: Var.Intern("ns", "name").BindRoot(new AsyncFnN(async (params) => { ... }))
                Emit($"Var.Intern(\"{ns}\", \"{varName}\").BindRoot(new {asyncFnType}(");
                Emit($"async ({string.Join(", ", paramList)}) => ");
                EmitLine("{");
                _indent++;
                EmitExpr(method.Body, ExprContext.Return);
                _indent--;
                Emit("}");
                EmitLine("))");
            }
            else
            {
                var funcType = paramCount switch
                {
                    0 => "Func<object?>",
                    1 => "Func<object?, object?>",
                    2 => "Func<object?, object?, object?>",
                    3 => "Func<object?, object?, object?, object?>",
                    4 => "Func<object?, object?, object?, object?, object?>",
                    _ => $"Func<{string.Join(", ", Enumerable.Repeat("object?", paramCount + 1))}>"
                };

                // Emit: Var.Intern("ns", "name").BindRoot((Func<...>)((params) => { ... }))
                Emit($"Var.Intern(\"{ns}\", \"{varName}\").BindRoot(({funcType})(");

                // Emit lambda
                if (paramCount == 1)
                    Emit($"{paramList.First()} => ");
                else
                    Emit($"({string.Join(", ", paramList)}) => ");

                EmitLine("{");
                _indent++;
                EmitExpr(method.Body, ExprContext.Return);
                _indent--;
                Emit("}");
                EmitLine("))");
            }

            // Remove params from locals and their type hints
            foreach (var local in addedLocals)
            {
                _locals.Remove(local);
            }
            foreach (var typedLocal in addedTypes)
            {
                _localTypes.Remove(typedLocal);
            }
        }
        else
        {
            // If type hint is provided, emit a typed local variable for REPL use
            // This allows subsequent evaluations to access the value with proper typing
            if (def.TypeHint is not null && def.Init is not null)
            {
                // Emit: TypeName varname = (TypeName)(init);
                // Then store in Var for namespace isolation too
                var localName = MungeName(varName);
                Emit($"{def.TypeHint} {localName} = ({def.TypeHint})");
                EmitExpr(def.Init, ExprContext.Expression);
                EmitLine(";");
                EmitLine($"Var.Intern(\"{ns}\", \"{varName}\").BindRoot({localName})");
            }
            else
            {
                // Store value in Var for namespace isolation
                Emit($"Var.Intern(\"{ns}\", \"{varName}\").BindRoot(");
                if (def.Init is not null)
                    EmitExpr(def.Init, ExprContext.Expression);
                else
                    Emit("null");
                EmitLine(")");
            }
        }
    }

    private void EmitMultiArityDefAsScript(string ns, string varName, FnExpr fn)
    {
        // For multi-arity functions, emit a dispatcher that takes params object?[] args
        // and routes to the correct arity based on args.Length
        EmitLine($"Var.Intern(\"{ns}\", \"{varName}\").BindRoot((Func<object?[], object?>)(__args__ => {{");
        _indent++;

        // Sort methods by param count (ascending) to handle them in order
        var methods = fn.Methods.OrderBy(m => m.Params.Count + (m.RestParam is not null ? 1 : 0)).ToList();

        // Find if any method has a rest param (variadic)
        var variadicMethod = methods.FirstOrDefault(m => m.RestParam is not null);

        // Emit switch on args.Length
        EmitLine("switch (__args__.Length)");
        EmitLine("{");
        _indent++;

        foreach (var method in methods)
        {
            var paramCount = method.Params.Count;
            var hasRest = method.RestParam is not null;

            if (hasRest)
            {
                // Variadic method handles paramCount or more args
                // Use default case for variadic
                continue;
            }

            EmitLine($"case {paramCount}:");
            EmitLine("{");
            _indent++;

            // Bind parameters from __args__
            var addedLocals = new List<string>();
            for (int i = 0; i < method.Params.Count; i++)
            {
                var p = method.Params[i];
                _locals.Add(p.Name);
                addedLocals.Add(p.Name);
                // Use the ParamTypes field from FnMethod if available
                var typeHint = method.ParamTypes is not null && i < method.ParamTypes.Count ? method.ParamTypes[i] : null;
                if (typeHint != null)
                    EmitLine($"var {MungeName(p.Name)} = ({typeHint})__args__[{i}];");
                else
                    EmitLine($"var {MungeName(p.Name)} = __args__[{i}];");
            }

            // Emit body
            EmitExpr(method.Body, ExprContext.Return);

            // Remove params from locals
            foreach (var local in addedLocals)
            {
                _locals.Remove(local);
            }

            _indent--;
            EmitLine("}");
        }

        // Default case - either variadic or error
        EmitLine("default:");
        EmitLine("{");
        _indent++;

        if (variadicMethod is not null)
        {
            // Handle variadic method
            var addedLocals = new List<string>();
            var fixedCount = variadicMethod.Params.Count;

            // Bind fixed parameters
            for (int i = 0; i < variadicMethod.Params.Count; i++)
            {
                var p = variadicMethod.Params[i];
                _locals.Add(p.Name);
                addedLocals.Add(p.Name);
                // Use the ParamTypes field from FnMethod if available
                var typeHint = variadicMethod.ParamTypes is not null && i < variadicMethod.ParamTypes.Count ? variadicMethod.ParamTypes[i] : null;
                if (typeHint != null)
                    EmitLine($"var {MungeName(p.Name)} = ({typeHint})__args__[{i}];");
                else
                    EmitLine($"var {MungeName(p.Name)} = __args__[{i}];");
            }

            // Bind rest parameter as array of remaining args
            var restParam = variadicMethod.RestParam!;
            _locals.Add(restParam.Name);
            addedLocals.Add(restParam.Name);
            EmitLine($"var {MungeName(restParam.Name)} = __args__.Skip({fixedCount}).ToArray();");

            // Emit body
            EmitExpr(variadicMethod.Body, ExprContext.Return);

            // Remove params from locals
            foreach (var local in addedLocals)
            {
                _locals.Remove(local);
            }
        }
        else
        {
            // No variadic - throw error for unexpected arg count
            var arities = string.Join(", ", methods.Select(m => m.Params.Count.ToString()));
            EmitLine($"throw new ArgumentException($\"Wrong number of args ({{__args__.Length}}) passed to {varName}. Expected: {arities}\");");
        }

        _indent--;
        EmitLine("}");

        _indent--;
        EmitLine("}"); // end switch

        _indent--;
        EmitLine("}))"); // end lambda and BindRoot
    }

    #endregion

    #region Expression Emitters

    private void EmitExpr(Expr expr, ExprContext ctx)
    {
        switch (expr)
        {
            case LiteralExpr lit:
                EmitLiteral(lit, ctx);
                break;
            case KeywordExpr kw:
                // Emit as runtime Keyword for proper map lookups
                if (kw.Keyword.Namespace != null)
                    Emit($"Keyword.Intern(\"{kw.Keyword.Namespace}\", \"{kw.Keyword.Name}\")");
                else
                    Emit($"Keyword.Intern(\"{kw.Keyword.Name}\")");
                break;
            case SymbolExpr sym:
                EmitSymbol(sym, ctx);
                break;
            case VectorExpr vec:
                EmitVector(vec, ctx);
                break;
            case MapExpr map:
                EmitMap(map, ctx);
                break;
            case Analyzer.SetExpr set:
                EmitSetLiteral(set, ctx);
                break;
            case LetExpr let:
                EmitLet(let, ctx);
                break;
            case DoExpr @do:
                EmitDo(@do, ctx);
                break;
            case IfExpr @if:
                EmitIf(@if, ctx);
                break;
            case FnExpr fn:
                EmitLambda(fn);
                break;
            case InvokeExpr invoke:
                EmitInvoke(invoke, ctx);
                break;
            case InstanceMethodExpr im:
                EmitInstanceMethod(im, ctx);
                break;
            case InstancePropertyExpr ip:
                EmitInstanceProperty(ip, ctx);
                break;
            case StaticMethodExpr sm:
                EmitStaticMethod(sm, ctx);
                break;
            case StaticPropertyExpr sp:
                EmitStaticProperty(sp, ctx);
                break;
            case NewExpr @new:
                EmitNew(@new, ctx);
                break;
            case AssignExpr assign:
                EmitAssign(assign, ctx);
                break;
            case ThrowExpr @throw:
                EmitThrow(@throw, ctx);
                break;
            case TryExpr @try:
                EmitTry(@try, ctx);
                break;
            case LoopExpr loop:
                EmitLoop(loop, ctx);
                break;
            case RecurExpr recur:
                EmitRecur(recur);
                break;
            case AwaitExpr await:
                EmitAwait(await, ctx);
                break;
            case QuoteExpr quote:
                EmitQuote(quote);
                break;
            case InNsExpr inNs:
                EmitInNs(inNs, ctx);
                break;
            case RawCSharpExpr rawCs:
                EmitRawCSharp(rawCs, ctx);
                break;
            case CastExpr cast:
                EmitCast(cast, ctx);
                break;
            case DefprotocolExpr protocol:
                EmitDefprotocol(protocol, ctx);
                break;
            case DeftypeExpr deftype:
                EmitDeftype(deftype, ctx);
                break;
            case DefrecordExpr defrecord:
                EmitDefrecord(defrecord, ctx);
                break;
            case PrimitiveOpExpr primOp:
                EmitPrimitiveOp(primOp, ctx);
                break;
            case DefTestExpr defTest:
                EmitDefTest(defTest, ctx);
                break;
            case IsExpr isExpr:
                EmitIs(isExpr, ctx);
                break;
            case InstanceCheckExpr instanceCheck:
                EmitInstanceCheck(instanceCheck, ctx);
                break;
            case NsExpr nsExpr:
                // ns is a side-effect-only form, returns nil
                EmitNs(nsExpr, ctx);
                break;
            case RequireExpr requireExpr:
                // require is handled at REPL level, returns nil
                EmitRequire(requireExpr, ctx);
                break;
            case DefExpr def:
                // def inside function body - enables (def x x) debugging pattern
                EmitDefInExprContext(def, ctx);
                break;
            default:
                Emit($"/* TODO: {expr.GetType().Name} */null");
                break;
        }
    }

    private void EmitLiteral(LiteralExpr lit, ExprContext ctx)
    {
        var value = lit.Value;
        var result = value switch
        {
            null => "null",
            true => "true",
            false => "false",
            string s => $"\"{EscapeString(s)}\"",
            char c => $"'{EscapeChar(c)}'",
            int i => i.ToString(),
            long l => $"{l}L",
            double d => FormatDouble(d),
            float f => $"{f}f",
            decimal m => $"{m}m",
            _ => value.ToString() ?? "null"
        };

        if (ctx == ExprContext.Return)
            EmitLine($"return {result};");
        else
            Emit(result);
    }

    private void EmitSymbol(SymbolExpr sym, ExprContext ctx)
    {
        var symbolName = sym.Symbol.Name;
        var ns = sym.Symbol.Namespace;

        // In REPL mode, resolve non-local unqualified symbols through Var system
        if (_isReplMode && ns is null && !_locals.Contains(symbolName) && !IsCoreFunction(symbolName))
        {
            // Check if symbol is referred from another namespace
            var lookupNs = _currentNamespace ?? "user";
            if (_refers != null && _refers.TryGetValue(symbolName, out var referredNs))
            {
                lookupNs = referredNs;
            }
            // Resolve through Var: Var.Find("ns", "name")?.Deref()
            var varLookup = $"Var.Find(\"{lookupNs}\", \"{symbolName}\")?.Deref()";
            if (ctx == ExprContext.Return)
                EmitLine($"return {varLookup};");
            else
                Emit(varLookup);
            return;
        }

        // Standard resolution for locals, Core functions, or qualified symbols
        var name = MungeName(symbolName);

        // Strip Clojure namespaces - our Core functions are available directly
        if (ns is not null && !ns.StartsWith("clojure.") && !ns.StartsWith("cljs.") && !ns.StartsWith("cljr."))
        {
            // Qualified symbol - could be namespace alias or type
            name = $"{ns}.{name}";
        }

        if (ctx == ExprContext.Return)
            EmitLine($"return {name};");
        else
            Emit(name);
    }

    private static bool IsCoreFunction(string name)
    {
        // Core functions from Cljr.Core that should NOT be resolved through Var
        return name switch
        {
            "+" or "-" or "*" or "/" or "<" or ">" or "<=" or ">=" or "=" or "!=" or "not=" => true,
            "str" or "println" or "print" or "prn" or "pr-str" => true,
            "first" or "rest" or "next" or "map" or "filter" or "reduce" or "count" or "empty?" => true,
            "get" or "assoc" or "dissoc" or "conj" or "update" or "merge" or "keys" or "vals" => true,
            "inc" or "dec" or "nil?" or "some?" or "number?" or "string?" or "seq" => true,
            "keyword?" or "symbol?" or "list?" or "vector?" or "map?" or "set?" or "fn?" or "seq?" => true,
            "list" or "vector" or "hash-map" or "hash-set" => true,
            "apply" or "partial" or "comp" or "identity" or "constantly" => true,
            "not" or "and" or "or" => true,
            "atom" or "deref" or "reset!" or "swap!" => true,
            "range" or "repeat" or "take" or "drop" or "concat" or "reverse" or "sort" => true,
            "nth" or "last" or "butlast" or "second" or "ffirst" => true,
            "contains?" or "find" or "select-keys" or "into" or "zipmap" => true,
            "type" or "class" or "instance?" => true,
            "into-array" or "future" => true,
            "map-async" => true, // Async helper
            "IsTruthy" => true, // Helper function
            "even?" or "odd?" or "pos?" or "neg?" or "zero?" => true, // Numeric predicates
            "true?" or "false?" or "boolean?" or "coll?" or "sorted?" or "counted?" => true, // Type predicates
            "realized?" or "associative?" or "reversible?" or "indexed?" => true, // Collection predicates
            "abs" or "min" or "max" or "quot" or "rem" or "mod" => true, // Math functions
            "name" or "namespace" or "keyword" or "symbol" => true, // Named functions
            "meta" or "with-meta" or "vary-meta" => true, // Metadata functions
            "compare" or "hash" => true, // Comparison/hashing
            "vec" or "set" or "sorted-map" or "sorted-set" => true, // Collection constructors
            "peek" or "pop" or "empty" or "not-empty" => true, // Collection ops
            "mapv" or "filterv" => true, // Eager sequence functions
            "every?" or "some" or "not-every?" or "not-any?" => true, // Sequence predicates
            "reduce-kv" or "group-by" or "partition-by" or "frequencies" => true, // Grouping functions
            "keep" or "remove" or "distinct" or "dedupe" => true, // Filtering functions
            "sort-by" or "max-key" or "min-key" => true, // Sorting functions
            "take-while" or "drop-while" or "split-with" => true, // Sequence splitting
            "iterate" or "repeatedly" or "cycle" => true, // Infinite sequences
            "interleave" or "interpose" or "mapcat" or "flatten" => true, // Sequence transformation
            "juxt" or "complement" or "fnil" => true, // Function combinators
            _ => false
        };
    }

    /// <summary>
    /// Emits an argument expression, wrapping core function symbols in lambdas for HOC compatibility.
    /// Core functions like inc, dec, even? are method groups in C# and can't be passed as Func directly.
    /// This wraps them as typed lambdas: inc -> (Func&lt;object?, object?&gt;)((__a) => inc(__a))
    /// The explicit Func cast ensures the correct overload is selected at compile time.
    /// </summary>
    /// <param name="arg">The argument expression to emit</param>
    /// <param name="arity">Expected arity for the function wrapper (1, 2, or 3 args)</param>
    private void EmitFunctionArg(Expr arg, int arity = 1)
    {
        // Check if this is a core function symbol being passed as an argument
        if (arg is SymbolExpr sym &&
            sym.Symbol.Namespace is null &&
            !_locals.Contains(sym.Symbol.Name) &&
            IsCoreFunction(sym.Symbol.Name))
        {
            var funcName = MungeName(sym.Symbol.Name);
            // Wrap in a typed lambda - this allows proper overload resolution
            switch (arity)
            {
                case 1:
                    Emit($"(Func<object?, object?>)((__a) => {funcName}(__a))");
                    break;
                case 2:
                    Emit($"(Func<object?, object?, object?>)((__a, __b) => {funcName}(__a, __b))");
                    break;
                case 3:
                    Emit($"(Func<object?, object?, object?, object?>)((__a, __b, __c) => {funcName}(__a, __b, __c))");
                    break;
                default:
                    // For higher arities, fall back to 1-arg (shouldn't happen in practice)
                    Emit($"(Func<object?, object?>)((__a) => {funcName}(__a))");
                    break;
            }
        }
        else
        {
            // Normal argument emission
            EmitExpr(arg, ExprContext.Expression);
        }
    }

    private void EmitVector(VectorExpr vec, ExprContext ctx)
    {
        if (ctx == ExprContext.Return)
            Emit("return ");
        Emit("PersistentVector.Create(");
        for (int i = 0; i < vec.Items.Count; i++)
        {
            if (i > 0) Emit(", ");
            EmitExpr(vec.Items[i], ExprContext.Expression);
        }
        if (ctx == ExprContext.Return)
            EmitLine(");");
        else
            Emit(")");
    }

    private void EmitMap(MapExpr map, ExprContext ctx)
    {
        if (ctx == ExprContext.Return)
            Emit("return ");
        Emit("PersistentHashMap.Create(");
        for (int i = 0; i < map.Pairs.Count; i++)
        {
            if (i > 0) Emit(", ");
            EmitExpr(map.Pairs[i].Key, ExprContext.Expression);
            Emit(", ");
            EmitExpr(map.Pairs[i].Value, ExprContext.Expression);
        }
        if (ctx == ExprContext.Return)
            EmitLine(");");
        else
            Emit(")");
    }

    private void EmitSetLiteral(Analyzer.SetExpr set, ExprContext ctx)
    {
        if (ctx == ExprContext.Return)
            Emit("return ");
        Emit("PersistentHashSet.Create(");
        for (int i = 0; i < set.Items.Count; i++)
        {
            if (i > 0) Emit(", ");
            EmitExpr(set.Items[i], ExprContext.Expression);
        }
        if (ctx == ExprContext.Return)
            EmitLine(");");
        else
            Emit(")");
    }

    private void EmitLet(LetExpr let, ExprContext ctx)
    {
        if (ctx == ExprContext.Expression)
        {
            // Need to wrap in a lambda for expression context
            Emit("((Func<object?>)(() => { ");
        }

        // Track local bindings for REPL mode symbol resolution
        var addedLocals = new List<string>();
        foreach (var (name, init) in let.Bindings)
        {
            var localName = name.Name;

            // For _ bindings (discard pattern), emit as statement only - handles void returns
            if (localName == "_")
            {
                EmitExpr(init, ExprContext.Statement);
                EmitLine(";");
                continue;
            }

            _locals.Add(localName);
            addedLocals.Add(localName);

            // Check if init is null literal - C# can't infer type from null alone
            var isNullInit = init is LiteralExpr lit && lit.Value is null;
            if (isNullInit)
            {
                Emit($"object? {MungeName(localName)} = null");
            }
            else
            {
                Emit($"var {MungeName(localName)} = ");
                EmitExpr(init, ExprContext.Expression);
            }
            EmitLine(";");
        }

        // Handle body - special case for method calls that might be void
        if (ctx == ExprContext.Expression && MightBeVoidReturning(let.Body))
        {
            // Emit as statement and return null to avoid void-to-object conversion error
            EmitExpr(let.Body, ExprContext.Statement);
            EmitLine(";");
            Emit("return null;");
        }
        else
        {
            // Propagate context - if outer is Statement, body should also be Statement
            var bodyContext = ctx == ExprContext.Expression ? ExprContext.Return : ctx;
            EmitExpr(let.Body, bodyContext);
        }

        // Remove locals that went out of scope
        foreach (var local in addedLocals)
        {
            _locals.Remove(local);
        }

        if (ctx == ExprContext.Expression)
        {
            Emit(" }))()");
        }
    }

    /// <summary>
    /// Check if an expression might return void (method calls that we can't type-check)
    /// </summary>
    private static bool MightBeVoidReturning(Expr expr)
    {
        return expr switch
        {
            // Instance and static method calls might return void
            InstanceMethodExpr => true,
            StaticMethodExpr => true,
            // Raw C# expressions (csharp*) might return void
            RawCSharpExpr => true,
            // Test assertions (is) return void (Assert.IsTrue)
            IsExpr => true,
            // DoExpr: check the last expression
            DoExpr @do when @do.Exprs.Count > 0 => MightBeVoidReturning(@do.Exprs[^1]),
            // Everything else returns a value
            _ => false
        };
    }

    private void EmitDo(DoExpr @do, ExprContext ctx)
    {
        if (@do.Exprs.Count == 0)
        {
            if (ctx == ExprContext.Return)
                EmitLine("return null;");
            else
                Emit("null");
            return;
        }

        if (ctx == ExprContext.Expression && @do.Exprs.Count > 1)
        {
            Emit("((Func<object?>)(() => { ");
        }

        // Emit all but the last expression - use Statement for void-returning, Expression for others
        for (int i = 0; i < @do.Exprs.Count - 1; i++)
        {
            EmitIndent();
            if (MightBeVoidReturning(@do.Exprs[i]))
            {
                EmitExpr(@do.Exprs[i], ExprContext.Statement);
            }
            else
            {
                EmitExpr(@do.Exprs[i], ExprContext.Expression);
                _sb.AppendLine(";");
            }
        }

        var last = @do.Exprs[^1];
        if (ctx == ExprContext.Expression && @do.Exprs.Count > 1)
        {
            // Handle void-returning methods - emit as statement and return null
            if (MightBeVoidReturning(last))
            {
                EmitIndent();
                EmitExpr(last, ExprContext.Statement);
                Emit("return null; }))()");
            }
            else
            {
                EmitExpr(last, ExprContext.Return);
                Emit(" }))()");
            }
        }
        else if (ctx == ExprContext.Statement)
        {
            // Last statement in Statement context - check if void-returning
            EmitIndent();
            if (MightBeVoidReturning(last))
            {
                EmitExpr(last, ExprContext.Statement);
            }
            else
            {
                EmitExpr(last, ExprContext.Expression);
                _sb.AppendLine(";");
            }
        }
        else if (ctx == ExprContext.Return && MightBeVoidReturning(last))
        {
            // Void-returning last expression in Return context - emit as statement then return null
            EmitIndent();
            EmitExpr(last, ExprContext.Statement);
            EmitLine("return null;");
        }
        else
        {
            EmitExpr(last, ctx);
        }
    }

    private void EmitIf(IfExpr @if, ExprContext ctx)
    {
        if (ctx == ExprContext.Expression)
        {
            // Ternary expression - cast both branches to object? for type compatibility
            // This handles cases like (if true 42 nil) where 42L and null have different types
            Emit("(IsTruthy(");
            EmitExpr(@if.Test, ExprContext.Expression);
            Emit(") ? (object?)(");
            EmitExpr(@if.Then, ExprContext.Expression);
            Emit(") : ");
            if (@if.Else is not null)
            {
                Emit("(object?)(");
                EmitExpr(@if.Else, ExprContext.Expression);
                Emit(")");
            }
            else
                Emit("null");
            Emit(")");
        }
        else
        {
            // Statement form
            Emit("if (IsTruthy(");
            EmitExpr(@if.Test, ExprContext.Expression);
            EmitLine("))");
            EmitLine("{");
            _indent++;
            // When in Return context, emit "return expr;" to ensure all expressions
            // properly return values (simple expressions like KeywordExpr don't handle Return context)
            if (ctx == ExprContext.Return)
            {
                Emit("return ");
                EmitExpr(@if.Then, ExprContext.Expression);
                EmitLine(";");
            }
            else
            {
                EmitExpr(@if.Then, ctx);
            }
            _indent--;
            EmitLine("}");

            if (@if.Else is not null)
            {
                EmitLine("else");
                EmitLine("{");
                _indent++;
                // Same fix for Else branch - emit "return expr;" in Return context
                if (ctx == ExprContext.Return)
                {
                    Emit("return ");
                    EmitExpr(@if.Else, ExprContext.Expression);
                    EmitLine(";");
                }
                else
                {
                    EmitExpr(@if.Else, ctx);
                }
                _indent--;
                EmitLine("}");
            }
            else if (ctx == ExprContext.Return)
            {
                EmitLine("else");
                EmitLine("{");
                _indent++;
                EmitLine("return null;");
                _indent--;
                EmitLine("}");
            }
        }
    }

    private void EmitLambda(FnExpr fn)
    {
        var method = fn.Methods[0]; // TODO: handle multi-arity

        // Build parameter list with optional type hints
        // Default to object? for untyped params to ensure proper overload resolution
        var paramStrings = new List<string>();
        for (int i = 0; i < method.Params.Count; i++)
        {
            var paramName = MungeName(method.Params[i].Name);
            var typeHint = method.ParamTypes?.ElementAtOrDefault(i);
            if (typeHint is not null)
                paramStrings.Add($"{typeHint} {paramName}");
            else
                paramStrings.Add($"object? {paramName}");
        }
        if (method.RestParam is not null)
        {
            // Can't have params in lambda, need different approach
            paramStrings.Add($"object? {MungeName(method.RestParam.Name)}");
        }

        // Track params as locals for REPL mode symbol resolution
        var addedLocals = new List<string>();
        foreach (var p in method.Params)
        {
            _locals.Add(p.Name);
            addedLocals.Add(p.Name);
        }
        if (method.RestParam is not null)
        {
            _locals.Add(method.RestParam.Name);
            addedLocals.Add(method.RestParam.Name);
        }

        if (fn.IsAsync)
        {
            Emit($"async ({string.Join(", ", paramStrings)}) => ");
        }
        else
        {
            // Always use parentheses since all params are now typed with object?
            Emit($"({string.Join(", ", paramStrings)}) => ");
        }

        // Check if body is simple enough for expression lambda
        if (IsSimpleExpr(method.Body))
        {
            EmitExpr(method.Body, ExprContext.Expression);
        }
        else
        {
            EmitLine("{");
            _indent++;
            EmitExpr(method.Body, ExprContext.Return);
            _indent--;
            Emit("}");
        }

        // Remove params from locals
        foreach (var local in addedLocals)
        {
            _locals.Remove(local);
        }
    }

    private void EmitInvoke(InvokeExpr invoke, ExprContext ctx)
    {
        if (ctx == ExprContext.Return)
        {
            EmitIndent();
            Emit("return ");
        }

        // Check if function is a symbol that should be invoked through Var
        if (_isReplMode && invoke.Fn is SymbolExpr symExpr &&
            symExpr.Symbol.Namespace is null &&
            !_locals.Contains(symExpr.Symbol.Name) &&
            !IsCoreFunction(symExpr.Symbol.Name))
        {
            // Check if symbol is referred from another namespace
            var lookupNs = _currentNamespace ?? "user";
            if (_refers != null && _refers.TryGetValue(symExpr.Symbol.Name, out var referredNs))
            {
                lookupNs = referredNs;
            }
            // Invoke through Var: Var.Find("ns", "name")?.Invoke(args)
            Emit($"Var.Find(\"{lookupNs}\", \"{symExpr.Symbol.Name}\")?.Invoke(");
            for (int i = 0; i < invoke.Args.Count; i++)
            {
                if (i > 0) Emit(", ");
                EmitExpr(invoke.Args[i], ExprContext.Expression);
            }
            Emit(")");
        }
        // Qualified Clojure symbol in REPL mode: ns/name -> Var.Find("ns", "name")?.Invoke(args)
        // Lowercase namespace prefix indicates Clojure namespace (vs uppercase .NET type)
        else if (_isReplMode && invoke.Fn is SymbolExpr qualifiedSym &&
                 qualifiedSym.Symbol.Namespace is not null &&
                 qualifiedSym.Symbol.Namespace.Length > 0 &&
                 !char.IsUpper(qualifiedSym.Symbol.Namespace[0]))
        {
            // Expand alias to full namespace if it exists
            var ns = qualifiedSym.Symbol.Namespace;
            if (_aliases != null && _aliases.TryGetValue(ns, out var fullNs))
            {
                ns = fullNs;
            }
            var name = qualifiedSym.Symbol.Name;
            Emit($"Var.Find(\"{ns}\", \"{name}\")?.Invoke(");
            for (int i = 0; i < invoke.Args.Count; i++)
            {
                if (i > 0) Emit(", ");
                EmitExpr(invoke.Args[i], ExprContext.Expression);
            }
            Emit(")");
        }
        // Special handling for into-array: first arg is a type, wrap in typeof()
        else if (invoke.Fn is SymbolExpr fnSym && fnSym.Symbol.Name == "into-array" && invoke.Args.Count >= 1)
        {
            Emit("into_array(");
            // First argument is a type - emit as typeof(TypeName)
            if (invoke.Args[0] is SymbolExpr typeArg)
            {
                Emit($"typeof({typeArg.Symbol.Name})");
            }
            else
            {
                EmitExpr(invoke.Args[0], ExprContext.Expression);
            }
            // Emit remaining arguments
            for (int i = 1; i < invoke.Args.Count; i++)
            {
                Emit(", ");
                EmitExpr(invoke.Args[i], ExprContext.Expression);
            }
            Emit(")");
        }
        else
        {
            // Standard invocation
            EmitExpr(invoke.Fn, ExprContext.Expression);
            Emit("(");

            // Detect special HOCs that need arity-aware function wrapping
            // swap! (atom f [args...]) - f receives (current-val + args), so arity = 1 + extra-args
            // reduce (f init coll) - f receives 2 args (acc, val), so arity = 2
            var fnName = invoke.Fn is SymbolExpr fnSym2 ? fnSym2.Symbol.Name : null;
            var isSwapCall = fnName == "swap!";
            var isReduceCall = fnName == "reduce";

            for (int i = 0; i < invoke.Args.Count; i++)
            {
                if (i > 0) Emit(", ");

                // Calculate expected arity for function argument
                int arity = 1; // default
                if (isSwapCall && i == 1)
                {
                    // For swap!, function arg (index 1) needs arity = 1 + extra args count
                    // (swap! atom f) -> f needs 1 arg (current val)
                    // (swap! atom f x) -> f needs 2 args (current val, x)
                    // (swap! atom f x y) -> f needs 3 args (current val, x, y)
                    arity = 1 + (invoke.Args.Count - 2);
                }
                else if (isReduceCall && i == 0)
                {
                    // For reduce, the function (index 0) always needs 2 args (acc, val)
                    arity = 2;
                }

                // Use EmitFunctionArg to wrap core function symbols in lambdas for HOC compatibility
                EmitFunctionArg(invoke.Args[i], arity);
            }
            Emit(")");
        }

        if (ctx == ExprContext.Return)
        {
            Emit(";");
            _sb.AppendLine();
        }
    }

    private void EmitInstanceMethod(InstanceMethodExpr im, ExprContext ctx)
    {
        var needsReturn = ctx == ExprContext.Return;
        if (needsReturn) Emit("return ");

        // Check if target has a type hint (inline metadata or from let binding)
        var typeHint = GetTypeHintFromExpr(im.Target);
        if (typeHint != null)
        {
            Emit($"(({typeHint})");
            EmitExpr(im.Target, ExprContext.Expression);
            Emit(")");
        }
        else
        {
            EmitExpr(im.Target, ExprContext.Expression);
        }

        // Emit method name with generic type arguments if present
        var typeArgsStr = im.TypeArguments is { Count: > 0 }
            ? $"<{string.Join(", ", im.TypeArguments)}>"
            : "";
        Emit($".{im.MethodName}{typeArgsStr}(");

        for (int i = 0; i < im.Args.Count; i++)
        {
            if (i > 0) Emit(", ");
            // Use EmitTypedExpr to cast typed locals (from type hints) when passed as arguments
            EmitTypedExpr(im.Args[i]);
        }
        Emit(")");

        if (needsReturn || ctx == ExprContext.Statement) EmitLine(";");
    }

    private void EmitInstanceProperty(InstancePropertyExpr ip, ExprContext ctx)
    {
        if (ctx == ExprContext.Return) Emit("return ");

        // Check if target has a type hint (inline metadata or from let binding)
        var typeHint = GetTypeHintFromExpr(ip.Target);
        if (typeHint != null)
        {
            Emit($"(({typeHint})");
            EmitExpr(ip.Target, ExprContext.Expression);
            Emit(")");
        }
        else
        {
            EmitExpr(ip.Target, ExprContext.Expression);
        }
        Emit($".{ip.PropertyName}");

        if (ctx == ExprContext.Return) EmitLine(";");
    }

    private void EmitStaticMethod(StaticMethodExpr sm, ExprContext ctx)
    {
        if (ctx == ExprContext.Return) Emit("return ");

        // Resolve type name for namespace isolation in REPL mode
        var typeName = _isReplMode ? ResolveTypeName(sm.TypeName) : sm.TypeName;

        // Emit method name with generic type arguments if present
        var typeArgsStr = sm.TypeArguments is { Count: > 0 }
            ? $"<{string.Join(", ", sm.TypeArguments)}>"
            : "";
        Emit($"{typeName}.{sm.MethodName}{typeArgsStr}(");

        for (int i = 0; i < sm.Args.Count; i++)
        {
            if (i > 0) Emit(", ");
            // Use EmitTypedExpr to cast typed locals (from type hints) when passed as arguments
            EmitTypedExpr(sm.Args[i]);
        }
        Emit(")");

        if (ctx == ExprContext.Return || ctx == ExprContext.Statement) EmitLine(";");
    }

    private void EmitStaticProperty(StaticPropertyExpr sp, ExprContext ctx)
    {
        if (ctx == ExprContext.Return) Emit("return ");
        // Resolve type name for namespace isolation in REPL mode
        var typeName = _isReplMode ? ResolveTypeName(sp.TypeName) : sp.TypeName;
        Emit($"{typeName}.{sp.PropertyName}");
        if (ctx == ExprContext.Return) EmitLine(";");
    }

    /// <summary>
    /// Emits an expression, casting it if it's a typed local (from type hint).
    /// Used for method arguments to ensure typed locals are properly cast when passed to .NET methods.
    /// </summary>
    private void EmitTypedExpr(Expr expr)
    {
        if (expr is SymbolExpr sym && _localTypes.TryGetValue(sym.Symbol.Name, out var typeHint))
        {
            Emit($"(({typeHint})");
            EmitExpr(expr, ExprContext.Expression);
            Emit(")");
        }
        else
        {
            EmitExpr(expr, ExprContext.Expression);
        }
    }

    /// <summary>
    /// Gets type hint from an expression - checks both _localTypes and inline metadata.
    /// Inline metadata (^Type expr) takes precedence.
    /// </summary>
    private string? GetTypeHintFromExpr(Expr expr)
    {
        if (expr is not SymbolExpr sym) return null;

        // Check inline metadata first (^Type symbol)
        var inlineHint = ExtractTypeHintFromMeta(sym.Symbol.Meta);
        if (inlineHint != null) return inlineHint;

        // Fall back to _localTypes (from let bindings with type hints)
        return _localTypes.TryGetValue(sym.Symbol.Name, out var localHint) ? localHint : null;
    }

    /// <summary>
    /// Extract type hint from :tag metadata (mirrors Analyzer.ExtractTypeHint)
    /// </summary>
    private static string? ExtractTypeHintFromMeta(IReadOnlyDictionary<object, object>? meta)
    {
        if (meta is null) return null;

        // Find the :tag key by comparing keyword name
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

        // Extract type name from symbol
        if (tag is ReaderSymbol typeSym)
        {
            return typeSym.Namespace is not null
                ? $"{typeSym.Namespace}.{typeSym.Name}"
                : typeSym.Name;
        }

        // Handle string type hints
        if (tag is string typeStr)
        {
            return typeStr;
        }

        return null;
    }

    /// <summary>
    /// Check if an object is a keyword with a specific name (handles cross-assembly issues)
    /// </summary>
    private static bool IsKeywordWithName(object? obj, string name)
    {
        if (obj is null) return false;

        // Try direct type check first
        if (obj is Cljr.Keyword kw)
            return kw.Name == name && kw.Namespace is null;

        // Fallback for cross-assembly issues
        var type = obj.GetType();
        if (type.FullName == "Cljr.Keyword" || type.FullName == "Cljr.Compiler.Reader.Keyword")
        {
            var nameProp = type.GetProperty("Name")?.GetValue(obj) as string;
            var nsProp = type.GetProperty("Namespace")?.GetValue(obj) as string;
            return nameProp == name && nsProp is null;
        }

        return false;
    }

    private void EmitNew(NewExpr @new, ExprContext ctx)
    {
        if (ctx == ExprContext.Return) Emit("return ");

        // Resolve type name for namespace isolation in REPL mode
        var typeName = _isReplMode ? ResolveTypeName(@new.TypeName) : @new.TypeName;
        // Normalize type names to avoid conflicts with generated class names
        typeName = NormalizeTypeName(typeName);
        Emit($"new {typeName}(");
        for (int i = 0; i < @new.Args.Count; i++)
        {
            if (i > 0) Emit(", ");
            // Use EmitTypedExpr to cast typed locals (from type hints) when passed as arguments
            EmitTypedExpr(@new.Args[i]);
        }
        Emit(")");

        if (ctx == ExprContext.Return) EmitLine(";");
    }

    private void EmitAssign(AssignExpr assign, ExprContext ctx)
    {
        EmitExpr(assign.Target, ExprContext.Expression);
        Emit(" = ");
        EmitExpr(assign.Value, ExprContext.Expression);

        if (ctx == ExprContext.Return)
        {
            EmitLine(";");
            Emit("return ");
            EmitExpr(assign.Target, ExprContext.Expression);
            EmitLine(";");
        }
    }

    private void EmitThrow(ThrowExpr @throw, ExprContext ctx)
    {
        Emit("throw ");
        EmitExpr(@throw.Exception, ExprContext.Expression);
        // In statement context, add semicolon
        if (ctx == ExprContext.Statement || ctx == ExprContext.Return)
        {
            EmitLine(";");
        }
    }

    private void EmitTry(TryExpr @try, ExprContext ctx)
    {
        var needsResult = ctx == ExprContext.Return || ctx == ExprContext.Expression;
        var resultVar = needsResult ? $"__result{_tempVarCounter++}" : null;

        if (needsResult && ctx == ExprContext.Expression)
        {
            Emit("((Func<object>)(() => { ");
        }

        if (needsResult)
        {
            EmitLine($"object {resultVar} = null;");
        }

        EmitLine("try");
        EmitLine("{");
        _indent++;
        if (needsResult)
        {
            Emit($"{resultVar} = ");
            EmitExpr(@try.Body, ExprContext.Expression);
            EmitLine(";");
        }
        else
        {
            EmitExpr(@try.Body, ExprContext.Statement);
            EmitLine(";");
        }
        _indent--;
        EmitLine("}");

        foreach (var @catch in @try.Catches)
        {
            EmitLine($"catch ({@catch.ExceptionType} {MungeName(@catch.Binding.Name)})");
            EmitLine("{");
            _indent++;
            if (needsResult)
            {
                Emit($"{resultVar} = ");
                EmitExpr(@catch.Body, ExprContext.Expression);
                EmitLine(";");
            }
            else
            {
                EmitExpr(@catch.Body, ExprContext.Statement);
                EmitLine(";");
            }
            _indent--;
            EmitLine("}");
        }

        if (@try.Finally is not null)
        {
            EmitLine("finally");
            EmitLine("{");
            _indent++;
            EmitExpr(@try.Finally, ExprContext.Statement);
            EmitLine(";");
            _indent--;
            EmitLine("}");
        }

        if (ctx == ExprContext.Return)
        {
            EmitLine($"return {resultVar};");
        }
        else if (ctx == ExprContext.Expression)
        {
            Emit($"return {resultVar}; }}))()");
        }
    }

    private void EmitLoop(LoopExpr loop, ExprContext ctx)
    {
        var needsResult = ctx == ExprContext.Return || ctx == ExprContext.Expression;
        var resultVar = needsResult ? $"__result{_tempVarCounter++}" : null;
        var hasRecur = ContainsRecur(loop.Body);

        if (needsResult && ctx == ExprContext.Expression)
        {
            Emit("((Func<object>)(() => { ");
        }

        if (needsResult)
        {
            EmitLine($"object {resultVar} = null;");
        }

        // Track loop bindings as locals
        var addedLocals = new List<string>();
        foreach (var (name, init) in loop.Bindings)
        {
            _locals.Add(name.Name);
            addedLocals.Add(name.Name);

            // Use object? for loop bindings when recur is present to avoid type mismatch
            // (e.g., var i = 0L makes i be long, but recur returns object?)
            if (hasRecur)
            {
                Emit($"object? {MungeName(name.Name)} = ");
            }
            else
            {
                Emit($"var {MungeName(name.Name)} = ");
            }
            EmitExpr(init, ExprContext.Expression);
            EmitLine(";");
        }

        // Save and set up loop context for recur emission
        var prevLoopBindings = _loopBindings;
        var prevLoopResultVar = _loopResultVar;
        _loopBindings = addedLocals;
        _loopResultVar = resultVar;

        EmitLine("while (true)");
        EmitLine("{");
        _indent++;

        if (hasRecur)
        {
            // Body contains recur - emit as statements with special handling
            // Recur will update bindings and continue
            // Non-recur paths will set result and break
            EmitLoopBody(loop.Body, needsResult ? resultVar : null);
        }
        else if (needsResult)
        {
            Emit($"{resultVar} = ");
            EmitExpr(loop.Body, ExprContext.Expression);
            EmitLine(";");
            EmitLine("break;");
        }
        else
        {
            EmitExpr(loop.Body, ExprContext.Statement);
            EmitLine(";");
            EmitLine("break;");
        }

        _indent--;
        EmitLine("}");

        // Restore previous loop context
        _loopBindings = prevLoopBindings;
        _loopResultVar = prevLoopResultVar;

        // Remove locals that went out of scope
        foreach (var local in addedLocals)
        {
            _locals.Remove(local);
        }

        if (ctx == ExprContext.Return)
        {
            EmitLine($"return {resultVar};");
        }
        else if (ctx == ExprContext.Expression)
        {
            Emit($"return {resultVar}; }}))()");
        }
    }

    /// <summary>
    /// Emit a loop body that contains recur. Handles if expressions specially
    /// to ensure recur branches update bindings and continue, while non-recur
    /// branches set the result and break.
    /// </summary>
    private void EmitLoopBody(Expr body, string? resultVar)
    {
        switch (body)
        {
            case RecurExpr recur:
                EmitRecurInLoop(recur);
                break;

            case IfExpr @if:
                Emit("if (IsTruthy(");
                EmitExpr(@if.Test, ExprContext.Expression);
                EmitLine("))");
                EmitLine("{");
                _indent++;
                EmitLoopBody(@if.Then, resultVar);
                _indent--;
                EmitLine("}");

                if (@if.Else is not null)
                {
                    EmitLine("else");
                    EmitLine("{");
                    _indent++;
                    EmitLoopBody(@if.Else, resultVar);
                    _indent--;
                    EmitLine("}");
                }
                else
                {
                    // No else - if we get here without recur, return null
                    EmitLine("else");
                    EmitLine("{");
                    _indent++;
                    if (resultVar is not null)
                        EmitLine($"{resultVar} = null;");
                    EmitLine("break;");
                    _indent--;
                    EmitLine("}");
                }
                break;

            case DoExpr @do:
                // Emit all but last as statements
                for (int i = 0; i < @do.Exprs.Count - 1; i++)
                {
                    EmitExpr(@do.Exprs[i], ExprContext.Statement);
                    EmitLine(";");
                }
                // Last expression determines recur vs result
                if (@do.Exprs.Count > 0)
                    EmitLoopBody(@do.Exprs[^1], resultVar);
                break;

            case LetExpr let:
                // Emit bindings
                foreach (var (name, init) in let.Bindings)
                {
                    _locals.Add(name.Name);
                    Emit($"var {MungeName(name.Name)} = ");
                    EmitExpr(init, ExprContext.Expression);
                    EmitLine(";");
                }
                // Emit body
                EmitLoopBody(let.Body, resultVar);
                // Clean up locals
                foreach (var (name, _) in let.Bindings)
                    _locals.Remove(name.Name);
                break;

            default:
                // Non-recur expression - set result and break
                if (resultVar is not null)
                {
                    Emit($"{resultVar} = ");
                    EmitExpr(body, ExprContext.Expression);
                    EmitLine(";");
                }
                else
                {
                    EmitExpr(body, ExprContext.Statement);
                    EmitLine(";");
                }
                EmitLine("break;");
                break;
        }
    }

    /// <summary>
    /// Emit a recur expression when inside a loop - updates bindings and continues
    /// </summary>
    private void EmitRecurInLoop(RecurExpr recur)
    {
        if (_loopBindings is null)
        {
            // Fallback for recur without loop context
            EmitLine("// ERROR: recur without loop context");
            EmitLine("continue;");
            return;
        }

        // Generate temp vars for new values (to handle self-referencing updates)
        var tempVars = new List<string>();
        for (int i = 0; i < recur.Args.Count && i < _loopBindings.Count; i++)
        {
            var tempVar = $"__recur{_tempVarCounter++}";
            tempVars.Add(tempVar);
            Emit($"var {tempVar} = ");
            EmitExpr(recur.Args[i], ExprContext.Expression);
            EmitLine(";");
        }

        // Assign temp vars to loop bindings
        for (int i = 0; i < tempVars.Count; i++)
        {
            EmitLine($"{MungeName(_loopBindings[i])} = {tempVars[i]};");
        }

        EmitLine("continue;");
    }

    private void EmitRecur(RecurExpr recur)
    {
        // This is called when recur appears outside of EmitLoopBody
        // It shouldn't happen in well-formed code, but handle gracefully
        EmitRecurInLoop(recur);
    }

    private void EmitAwait(AwaitExpr await, ExprContext ctx)
    {
        if (ctx == ExprContext.Return) Emit("return ");

        Emit("await ");
        EmitExpr(await.Task, ExprContext.Expression);

        if (ctx == ExprContext.Return) EmitLine(";");
    }

    private void EmitQuote(QuoteExpr quote)
    {
        EmitQuotedValue(quote.Form);
    }

    private void EmitQuotedValue(object? form)
    {
        switch (form)
        {
            case null:
                Emit("null");
                break;
            case ReaderSymbol sym:
                // Emit Symbol.Intern for quoted symbols (use ReaderSymbol to disambiguate from Cljr.Symbol)
                if (sym.Namespace != null)
                    Emit($"Symbol.Intern(\"{sym.Namespace}\", \"{sym.Name}\")");
                else
                    Emit($"Symbol.Intern(\"{sym.Name}\")");
                break;
            case IEnumerable<object?> list when form.GetType().Name.Contains("List") || form.GetType().Name.Contains("Cons"):
                // Emit PersistentList.Create for quoted lists
                var items = list.ToList();
                if (items.Count == 0)
                    Emit("PersistentList.Empty");
                else
                {
                    Emit("PersistentList.Create(");
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (i > 0) Emit(", ");
                        EmitQuotedValue(items[i]);
                    }
                    Emit(")");
                }
                break;
            case long l:
                Emit($"{l}L");
                break;
            case int n:
                Emit($"{n}L");
                break;
            case double d:
                Emit($"{d}D");
                break;
            case string s:
                Emit($"\"{EscapeString(s)}\"");
                break;
            case bool b:
                Emit(b ? "true" : "false");
                break;
            case Keyword kw:
                if (kw.Namespace != null)
                    Emit($"Keyword.Intern(\"{kw.Namespace}\", \"{kw.Name}\")");
                else
                    Emit($"Keyword.Intern(\"{kw.Name}\")");
                break;
            default:
                // Fallback for unrecognized forms
                Emit($"/* quote: {form} */ null");
                break;
        }
    }

    private void EmitInNs(InNsExpr inNs, ExprContext ctx)
    {
        // in-ns returns a symbol representing the namespace
        // In the REPL, the actual namespace switching is handled by ReplState
        var result = $"Cljr.Compiler.Reader.Symbol.Parse(\"{inNs.Name}\")";

        if (ctx == ExprContext.Return)
            EmitLine($"return {result};");
        else
            Emit(result);
    }

    private void EmitNs(NsExpr nsExpr, ExprContext ctx)
    {
        // ns is a side-effect-only form - the actual work is done at REPL/compile level
        // In runtime, it just returns nil
        if (ctx == ExprContext.Return)
            EmitLine("return null;");
        else
            Emit("null");
    }

    private void EmitRequire(RequireExpr requireExpr, ExprContext ctx)
    {
        // require is handled at REPL level, returns nil at runtime
        // The actual file loading is done by NreplSession
        if (ctx == ExprContext.Return)
            EmitLine("return null;");
        else
            Emit("null");
    }

    private void EmitDefInExprContext(DefExpr def, ExprContext ctx)
    {
        // def inside function body - enables (def x x) debugging pattern
        // Creates/updates a Var at runtime, like Clojure
        var ns = _clojureNamespace ?? _currentNamespace ?? "user";
        var varName = def.Name.Name;

        // Generate: Var.Intern("ns", "name").BindRoot(<init>)
        var initCode = def.Init != null ? EmitExprToString(def.Init) : "null";
        var code = $"Var.Intern(\"{ns}\", \"{varName}\").BindRoot({initCode})";

        // Handle context
        switch (ctx)
        {
            case ExprContext.Return:
                EmitLine($"return {code};");
                break;
            case ExprContext.Statement:
                EmitLine($"{code};");
                break;
            case ExprContext.Expression:
                Emit(code);
                break;
        }
    }

    #endregion

    #region Helpers

    /// <summary>
    /// Resolve a type name through the type resolver for namespace isolation.
    /// Handles alias-qualified types (e.g., "a.MyType" where "a" is an alias).
    /// Returns the original name if no resolver is set or for external .NET types.
    /// </summary>
    private string ResolveTypeName(string typeName)
    {
        // Check if this is an alias-qualified type (contains a dot)
        var dotIndex = typeName.LastIndexOf('.');
        if (dotIndex > 0 && _aliases != null)
        {
            var nsOrAlias = typeName[..dotIndex];
            var simpleTypeName = typeName[(dotIndex + 1)..];

            // Check if the namespace part is an alias
            if (TryResolveAliasedType(nsOrAlias, simpleTypeName, out var resolved))
            {
                return resolved;
            }

            // Try un-munging the namespace (convert underscores back to dots)
            // This handles aliases like "api.main" which get munged to "api_main" by the analyzer
            var unmungedAlias = nsOrAlias.Replace("_", ".");
            if (unmungedAlias != nsOrAlias && TryResolveAliasedType(unmungedAlias, simpleTypeName, out resolved))
            {
                return resolved;
            }
        }

        // No alias resolution needed - use standard type resolution
        return _typeResolver?.Invoke(typeName) ?? typeName;
    }

    /// <summary>
    /// Try to resolve a type using an alias. Returns true if the alias was found.
    /// When using an alias, the type is always accessible - the alias IS the explicit permission.
    /// </summary>
    private bool TryResolveAliasedType(string alias, string simpleTypeName, out string resolved)
    {
        resolved = string.Empty;

        if (_aliases == null || !_aliases.TryGetValue(alias, out var fullNamespace))
        {
            return false;
        }

        // Using an alias means you've explicitly required the namespace,
        // so the type is accessible - just emit the fully qualified name
        var fullCsharpNs = fullNamespace.Replace("-", "_").Replace(".", "_");
        resolved = $"{fullCsharpNs}.{simpleTypeName}";
        return true;
    }

    private void Emit(string s) => _sb.Append(s);

    private void EmitIndent() => _sb.Append(new string(' ', _indent * 4));

    private void EmitLine(string s = "")
    {
        if (!string.IsNullOrEmpty(s))
        {
            _sb.Append(new string(' ', _indent * 4));
            _sb.Append(s);
        }
        _sb.AppendLine();
    }

    /// <summary>
    /// Emit a multi-line docstring as XML doc comments.
    /// Each line gets a /// prefix to maintain valid C# syntax.
    /// </summary>
    private void EmitDocComment(string docString)
    {
        EmitLine("/// <summary>");
        // Split docstring by newlines and emit each line with /// prefix
        var lines = docString.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            // Escape XML special characters and trim leading whitespace
            var escapedLine = line
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
            EmitLine($"/// {escapedLine}");
        }
        EmitLine("/// </summary>");
    }

    private static string MungeName(string name)
    {
        // Handle special operator symbols first (before general replacements)
        var result = name switch
        {
            "+" => "_PLUS_",
            "-" => "_MINUS_",
            "*" => "_STAR_",
            "/" => "_SLASH_",
            "<" => "_LT_",
            ">" => "_GT_",
            "<=" => "_LT__EQ_",
            ">=" => "_GT__EQ_",
            "=" => "_EQ_",
            "!=" => "_BANG__EQ_",
            "not=" => "not_EQ_",
            _ => name
                .Replace("-", "_")
                .Replace("?", "_QMARK_")
                .Replace("!", "_BANG_")
                .Replace("*", "_STAR_")
                .Replace("+", "_PLUS_")
                .Replace("<", "_LT_")
                .Replace(">", "_GT_")
                .Replace("=", "_EQ_")
                .Replace("'", "_QUOTE_")
                .Replace("/", "_SLASH_")
        };

        // Handle reserved words
        return result switch
        {
            "class" or "namespace" or "public" or "private" or "static" or
            "void" or "int" or "string" or "bool" or "new" or "return" or
            "if" or "else" or "while" or "for" or "try" or "catch" or
            "finally" or "throw" or "true" or "false" or "null" or
            "this" or "base" or "object" or "typeof" or "is" or "as" or
            "ref" or "out" or "in" or "params" or "override" or "virtual" or
            "abstract" or "sealed" or "const" or "readonly" or "event" or
            "delegate" or "interface" or "struct" or "enum" or "using" or
            "extern" or "checked" or "unchecked" or "fixed" or "unsafe" or
            "volatile" or "lock" or "goto" or "break" or "continue" or
            "default" or "switch" or "case" or "do" or "foreach" or "await" or "async"
                => $"@{result}",
            _ => result
        };
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

    private static string EscapeChar(char c) => c switch
    {
        '\'' => "\\'",
        '\\' => "\\\\",
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => c.ToString()
    };

    private static string FormatDouble(double d)
    {
        var s = d.ToString("G17", System.Globalization.CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e'))
            s += ".0";
        return s;
    }

    private static bool IsSimpleExpr(Expr expr) => expr switch
    {
        LiteralExpr or SymbolExpr or KeywordExpr => true,
        InvokeExpr inv => inv.Args.All(IsSimpleExpr),
        InstanceMethodExpr im => IsSimpleExpr(im.Target) && im.Args.All(IsSimpleExpr),
        InstancePropertyExpr ip => IsSimpleExpr(ip.Target),
        StaticMethodExpr sm => sm.Args.All(IsSimpleExpr),
        StaticPropertyExpr => true,
        PrimitiveOpExpr primOp => primOp.Operands.All(IsSimpleExpr),
        CastExpr cast => IsSimpleExpr(cast.Value),
        // IfExpr is never simple - always use block form for lambdas with conditionals
        // This avoids issues with expression lambdas and ternary operators
        IfExpr => false,
        _ => false
    };

    /// <summary>
    /// Checks if an expression contains a RecurExpr anywhere within it.
    /// Used to determine if we need statement form for loop bodies.
    /// </summary>
    private static bool ContainsRecur(Expr expr) => expr switch
    {
        RecurExpr => true,
        IfExpr @if => ContainsRecur(@if.Then) || (@if.Else is not null && ContainsRecur(@if.Else)),
        DoExpr @do => @do.Exprs.Any(ContainsRecur),
        LetExpr let => let.Bindings.Any(b => ContainsRecur(b.Init)) || ContainsRecur(let.Body),
        _ => false
    };

    #endregion

    #region C# Interop Emitters

    /// <summary>
    /// Emit native C# arithmetic/comparison operators for primitive types.
    /// This generates (a + b) instead of Core._PLUS_(a, b) for type-hinted operands.
    /// </summary>
    private void EmitPrimitiveOp(PrimitiveOpExpr primOp, ExprContext ctx)
    {
        // Build the expression: (operand1 op operand2 op operand3 ...)
        var parts = new List<string>();
        foreach (var operand in primOp.Operands)
        {
            parts.Add(EmitExprToString(operand));
        }

        // For comparison operators with more than 2 operands, we need to chain: (a < b && b < c)
        string code;
        if (IsComparisonOperator(primOp.Operator) && primOp.Operands.Count > 2)
        {
            // Chain comparisons: (a < b) && (b < c) && ...
            var comparisons = new List<string>();
            for (int i = 0; i < parts.Count - 1; i++)
            {
                comparisons.Add($"({parts[i]} {primOp.Operator} {parts[i + 1]})");
            }
            code = string.Join(" && ", comparisons);
        }
        else
        {
            // Simple binary or n-ary arithmetic: (a + b + c)
            code = string.Join($" {primOp.Operator} ", parts);
        }

        // Wrap in parentheses for safety
        code = $"({code})";

        switch (ctx)
        {
            case ExprContext.Return:
                EmitLine($"return {code};");
                break;
            case ExprContext.Statement:
                EmitLine($"{code};");
                break;
            case ExprContext.Expression:
                Emit(code);
                break;
        }
    }

    private static bool IsComparisonOperator(string op) =>
        op is "<" or ">" or "<=" or ">=" or "==" or "!=";

    private void EmitRawCSharp(RawCSharpExpr rawCs, ExprContext ctx)
    {
        var code = rawCs.Template;

        // Replace interpolations with emitted expressions
        foreach (var (placeholder, value) in rawCs.Interpolations)
        {
            var exprCode = EmitExprToString(value);
            code = code.Replace(placeholder, exprCode);
        }

        code = code.Trim();

        // csharp* handling: Statement context emits as-is, others wrap for return handling
        switch (ctx)
        {
            case ExprContext.Return:
                EmitLine($"return ({code});");
                break;
            case ExprContext.Statement:
                // Don't wrap in parens - code might be void-returning
                EmitLine($"{code};");
                break;
            case ExprContext.Expression:
                Emit($"({code})");
                break;
        }
    }

    private void EmitCast(CastExpr cast, ExprContext ctx)
    {
        var exprCode = EmitExprToString(cast.Value);
        // Resolve type name for namespace isolation in REPL mode
        var typeName = _isReplMode ? ResolveTypeName(cast.TypeName) : cast.TypeName;

        // For primitive numeric types, use Convert.ToXXX to handle boxed value conversions
        // This avoids InvalidCastException when unboxing (e.g., boxed long to int)
        var code = typeName switch
        {
            "int" or "Int32" or "System.Int32" => $"Convert.ToInt32({exprCode})",
            "long" or "Int64" or "System.Int64" => $"Convert.ToInt64({exprCode})",
            "short" or "Int16" or "System.Int16" => $"Convert.ToInt16({exprCode})",
            "byte" or "Byte" or "System.Byte" => $"Convert.ToByte({exprCode})",
            "sbyte" or "SByte" or "System.SByte" => $"Convert.ToSByte({exprCode})",
            "uint" or "UInt32" or "System.UInt32" => $"Convert.ToUInt32({exprCode})",
            "ulong" or "UInt64" or "System.UInt64" => $"Convert.ToUInt64({exprCode})",
            "ushort" or "UInt16" or "System.UInt16" => $"Convert.ToUInt16({exprCode})",
            "float" or "Single" or "System.Single" => $"Convert.ToSingle({exprCode})",
            "double" or "Double" or "System.Double" => $"Convert.ToDouble({exprCode})",
            "decimal" or "Decimal" or "System.Decimal" => $"Convert.ToDecimal({exprCode})",
            "bool" or "Boolean" or "System.Boolean" => $"Convert.ToBoolean({exprCode})",
            "char" or "Char" or "System.Char" => $"Convert.ToChar({exprCode})",
            _ => $"(({typeName}){exprCode})"  // Fall back to regular cast for reference types
        };

        switch (ctx)
        {
            case ExprContext.Return:
                EmitLine($"return {code};");
                break;
            case ExprContext.Statement:
                EmitLine($"{code};");
                break;
            case ExprContext.Expression:
                Emit(code);
                break;
        }
    }

    /// <summary>
    /// Emit an expression to a string (without affecting the main StringBuilder)
    /// </summary>
    private string EmitExprToString(Expr expr)
    {
        // Create a temporary emitter for the expression
        var tempEmitter = new CSharpEmitter
        {
            _currentNamespace = _currentNamespace,
            _isReplMode = _isReplMode
        };

        // Copy locals from parent emitter so local variables resolve correctly
        foreach (var local in _locals)
            tempEmitter._locals.Add(local);

        tempEmitter.EmitExpr(expr, ExprContext.Expression);
        return tempEmitter._sb.ToString();
    }

    private void EmitDefprotocol(DefprotocolExpr protocol, ExprContext ctx)
    {
        // Generate C# interface
        EmitLine($"public interface {protocol.Name.Name}");
        EmitLine("{");
        _indent++;

        foreach (var method in protocol.Methods)
        {
            var returnType = method.ReturnType ?? "object?";
            var methodName = MungeName(method.Name.Name);

            // Build parameter list (skip 'this')
            var paramList = new List<string>();
            foreach (var (pName, pType) in method.Params)
            {
                paramList.Add($"{pType ?? "object?"} {MungeName(pName.Name)}");
            }

            EmitLine($"{returnType} {methodName}({string.Join(", ", paramList)});");
        }

        _indent--;
        EmitLine("}");

        if (ctx == ExprContext.Return)
            EmitLine("return null;");
    }

    private void EmitDeftype(DeftypeExpr deftype, ExprContext ctx)
    {
        var typeName = deftype.Name.Name;

        // Build interface list
        var interfaces = deftype.Interfaces.Count > 0
            ? " : " + string.Join(", ", deftype.Interfaces.Select(i => i.Name))
            : "";

        // Check if any fields have attributes - if so, use { get; set; } for Blazor compatibility
        var hasFieldAttributes = deftype.Fields.Any(f => f.Attributes is { Count: > 0 });

        EmitLine($"public class {typeName}{interfaces}");
        EmitLine("{");
        _indent++;

        // Emit properties with attributes
        foreach (var field in deftype.Fields)
        {
            EmitAttributes(field.Attributes);
            var type = field.TypeHint ?? "object?";
            // Use { get; set; } for attributed fields (for Blazor [Parameter], etc.)
            var accessor = hasFieldAttributes ? "{ get; set; }" : "{ get; }";
            EmitLine($"public {type} {MungeName(field.Name.Name)} {accessor}");
        }

        EmitLine();

        if (hasFieldAttributes)
        {
            // Emit parameterless constructor for framework use (e.g., Blazor component instantiation)
            EmitLine($"public {typeName}() {{ }}");
            EmitLine();
        }

        // Emit constructor with parameters (if there are fields)
        if (deftype.Fields.Count > 0)
        {
            var ctorParams = deftype.Fields.Select(f =>
                $"{f.TypeHint ?? "object?"} {MungeName(f.Name.Name)}").ToList();
            EmitLine($"public {typeName}({string.Join(", ", ctorParams)})");
            EmitLine("{");
            _indent++;
            foreach (var field in deftype.Fields)
            {
                var munged = MungeName(field.Name.Name);
                EmitLine($"this.{munged} = {munged};");
            }
            _indent--;
            EmitLine("}");
        }

        // Emit methods
        foreach (var method in deftype.Methods)
        {
            EmitLine();
            EmitTypeMethod(method);
        }

        _indent--;
        EmitLine("}");

        if (ctx == ExprContext.Return)
            EmitLine("return null;");
    }

    /// <summary>
    /// Emit a type definition (defrecord/deftype/defprotocol) for REPL dynamic compilation.
    /// Writes directly to the provided StringBuilder without the wrapper script context.
    /// </summary>
    public void EmitTypeDefinition(Expr expr, StringBuilder target)
    {
        // Save current content and indent
        var savedContent = _sb.ToString();
        var oldIndent = _indent;

        // Clear and set indent for inside namespace
        _sb.Clear();
        _indent = 1;

        switch (expr)
        {
            case DefrecordExpr defrecord:
                EmitDefrecord(defrecord, ExprContext.Statement);
                break;
            case DeftypeExpr deftype:
                EmitDeftype(deftype, ExprContext.Statement);
                break;
            case DefprotocolExpr defprotocol:
                EmitDefprotocol(defprotocol, ExprContext.Statement);
                break;
        }

        // Copy emitted content to target
        target.Append(_sb.ToString());

        // Restore original state
        _sb.Clear();
        _sb.Append(savedContent);
        _indent = oldIndent;
    }

    private void EmitDefrecord(DefrecordExpr defrecord, ExprContext ctx)
    {
        var typeName = defrecord.Name.Name;

        // Build interface list
        var interfaces = defrecord.Interfaces.Count > 0
            ? " : " + string.Join(", ", defrecord.Interfaces.Select(i => i.Name))
            : "";

        // Check if any fields have attributes - if so, we need to emit as class-style record
        // with explicit properties so we can put [Attribute] on them
        var hasFieldAttributes = defrecord.Fields.Any(f => f.Attributes is { Count: > 0 });

        if (hasFieldAttributes)
        {
            // Emit as record with explicit properties for attribute support
            // This allows [Parameter], [Inject] etc. on Blazor component properties
            EmitLine($"public record {typeName}{interfaces}");
            EmitLine("{");
            _indent++;

            // Emit properties with attributes and { get; set; } for Blazor compatibility
            foreach (var field in defrecord.Fields)
            {
                EmitAttributes(field.Attributes);
                var type = field.TypeHint ?? "object?";
                EmitLine($"public {type} {MungeName(field.Name.Name)} {{ get; set; }}");
            }

            // Emit methods
            foreach (var method in defrecord.Methods)
            {
                EmitLine();
                EmitTypeMethod(method);
            }

            _indent--;
            EmitLine("}");
        }
        else
        {
            // Build primary constructor params (no attributes - use compact syntax)
            var primaryCtorParams = defrecord.Fields.Select(f =>
                $"{f.TypeHint ?? "object?"} {MungeName(f.Name.Name)}").ToList();

            if (defrecord.Methods.Count == 0)
            {
                // Simple record with no methods
                EmitLine($"public record {typeName}({string.Join(", ", primaryCtorParams)}){interfaces};");
            }
            else
            {
                // Record with methods
                EmitLine($"public record {typeName}({string.Join(", ", primaryCtorParams)}){interfaces}");
                EmitLine("{");
                _indent++;

                foreach (var method in defrecord.Methods)
                {
                    EmitTypeMethod(method);
                    EmitLine();
                }

                _indent--;
                EmitLine("}");
            }
        }

        if (ctx == ExprContext.Return)
            EmitLine("return null;");
    }

    private void EmitTypeMethod(TypeMethodImpl method)
    {
        // Emit method attributes
        EmitAttributes(method.Attributes);

        var returnType = method.ReturnType ?? "object?";
        var methodName = MungeName(method.Name.Name);
        var isAsync = returnType.StartsWith("Task");

        if (isAsync)
            returnType = $"async {returnType}";

        // Build parameter list
        var paramList = new List<string>();
        for (int i = 0; i < method.Params.Count; i++)
        {
            var p = method.Params[i];
            var paramType = method.ParamTypes?[i] ?? "object?";
            paramList.Add($"{paramType} {MungeName(p.Name)}");
        }

        EmitLine($"public {returnType} {methodName}({string.Join(", ", paramList)})");
        EmitLine("{");
        _indent++;

        EmitExpr(method.Body, ExprContext.Return);

        _indent--;
        EmitLine("}");
    }

    /// <summary>
    /// Emit .NET attributes on separate lines: [Attribute1] [Attribute2(args)]
    /// </summary>
    private void EmitAttributes(IReadOnlyList<AttributeSpec>? attributes)
    {
        if (attributes is null || attributes.Count == 0) return;

        foreach (var attr in attributes)
        {
            if (attr.Arguments is { Count: > 0 })
            {
                var args = string.Join(", ", attr.Arguments.Select(FormatAttributeArg));
                EmitLine($"[{attr.Name}({args})]");
            }
            else
            {
                EmitLine($"[{attr.Name}]");
            }
        }
    }

    /// <summary>
    /// Format an attribute argument value for C# output
    /// </summary>
    private static string FormatAttributeArg(object? arg) => arg switch
    {
        null => "null",
        string s => $"\"{s.Replace("\"", "\\\"")}\"",
        bool b => b ? "true" : "false",
        char c => $"'{c}'",
        _ => arg.ToString() ?? "null"
    };

    #endregion

    #region Testing Special Forms Emitters

    /// <summary>
    /// Emit (deftest test-name body...) as MSTest [TestMethod] method
    /// </summary>
    private void EmitDefTest(DefTestExpr defTest, ExprContext ctx)
    {
        var testName = MungeName(defTest.Name.Name);
        var ns = _currentNamespace ?? "user";

        // In REPL mode, emit as a Var-bound function (keep old behavior for REPL compatibility)
        if (_isReplMode)
        {
            // Define the test function in the Var system
            Emit($"Var.Intern(\"{ns}\", \"{defTest.Name.Name}\").BindRoot((Func<Cljr.TestRunResult>)(() => ");
            EmitLine("{");
            _indent++;

            // Create result collector
            EmitLine("var __testResult__ = new Cljr.TestRunResult();");

            // Emit body - is forms will add to __testResult__
            EmitExpr(defTest.Body, ExprContext.Statement);
            EmitLine(";");

            // Return the result
            EmitLine("return __testResult__;");

            _indent--;
            EmitLine("}))");
        }
        else
        {
            // In file compilation mode, emit as MSTest [TestMethod] instance method
            EmitLine("[TestMethod]");
            EmitLine($"public void {testName}()");
            EmitLine("{");
            _indent++;

            // Emit body - is forms will emit Assert calls directly
            EmitExpr(defTest.Body, ExprContext.Statement);

            _indent--;
            EmitLine("}");
        }

        if (ctx == ExprContext.Return)
            EmitLine("return null;");
    }

    /// <summary>
    /// Emit (is expr) as MSTest assertion or REPL-compatible pass/fail recorder
    /// </summary>
    private void EmitIs(IsExpr isExpr, ExprContext ctx)
    {
        // In REPL mode, keep the old behavior with __testResult__
        if (_isReplMode)
        {
            EmitIsForRepl(isExpr, ctx);
            return;
        }

        // In file compilation mode, emit MSTest assertions
        if (isExpr.Expected is not null && isExpr.Actual is not null)
        {
            // For (is (= expected actual)) - use Assert.AreEqual
            // Cast both to object? to help type inference when types differ
            Emit("Assert.AreEqual((object?)(");
            EmitExpr(isExpr.Expected, ExprContext.Expression);
            Emit("), (object?)(");
            EmitExpr(isExpr.Actual, ExprContext.Expression);
            Emit(")");
            if (isExpr.Message is not null)
            {
                Emit(", (");
                EmitExpr(isExpr.Message, ExprContext.Expression);
                Emit(")?.ToString()");
            }
            Emit(")");
        }
        else
        {
            // For (is expr) - use Assert.IsTrue
            Emit("Assert.IsTrue(IsTruthy(");
            EmitExpr(isExpr.Test, ExprContext.Expression);
            Emit(")");
            if (isExpr.Message is not null)
            {
                Emit(", (");
                EmitExpr(isExpr.Message, ExprContext.Expression);
                Emit(")?.ToString()");
            }
            Emit(")");
        }

        // Add semicolon for both Statement and Return contexts
        // Return context is used inside lambda bodies where statements need semicolons
        if (ctx == ExprContext.Statement || ctx == ExprContext.Return)
            EmitLine(";");
    }

    /// <summary>
    /// Emit (is expr) for REPL mode - records pass/fail to __testResult__
    /// </summary>
    private void EmitIsForRepl(IsExpr isExpr, ExprContext ctx)
    {
        // Generate a unique var for the test result
        var resultVar = $"__isResult{_tempVarCounter++}__";

        if (ctx == ExprContext.Expression)
        {
            // Wrap in a lambda for expression context
            Emit("((Func<bool>)(() => { ");
        }

        // Evaluate the test
        Emit($"var {resultVar} = IsTruthy(");
        EmitExpr(isExpr.Test, ExprContext.Expression);
        EmitLine(");");

        // Record the assertion result
        EmitLine($"if ({resultVar})");
        EmitLine("{");
        _indent++;
        EmitLine("__testResult__.PassCount++;");
        _indent--;
        EmitLine("}");
        EmitLine("else");
        EmitLine("{");
        _indent++;
        EmitLine("__testResult__.FailCount++;");

        // Add failure details
        Emit("__testResult__.Failures.Add(new Cljr.TestFailure { ");

        if (isExpr.Expected is not null && isExpr.Actual is not null)
        {
            Emit("Expected = ");
            EmitExpr(isExpr.Expected, ExprContext.Expression);
            Emit(", Actual = ");
            EmitExpr(isExpr.Actual, ExprContext.Expression);
        }
        else
        {
            Emit("Expected = true, Actual = false");
        }

        if (isExpr.Message is not null)
        {
            Emit(", Message = (");
            EmitExpr(isExpr.Message, ExprContext.Expression);
            Emit(")?.ToString()");
        }

        EmitLine(" });");

        _indent--;
        EmitLine("}");

        if (ctx == ExprContext.Expression)
        {
            Emit($"return {resultVar}; }}))()");
        }
        else if (ctx == ExprContext.Return)
        {
            EmitLine($"return {resultVar};");
        }
    }

    /// <summary>
    /// Emit (instance? Type value) as C# pattern matching: value is Type
    /// </summary>
    private void EmitInstanceCheck(InstanceCheckExpr instanceCheck, ExprContext ctx)
    {
        // Emit: (value is Type)
        Emit("(");
        EmitExpr(instanceCheck.Value, ExprContext.Expression);
        Emit($" is {instanceCheck.TypeName})");

        if (ctx == ExprContext.Statement)
            EmitLine(";");
    }

    #endregion

    #region Type Hint Helpers

    /// <summary>
    /// Extract type hint from :tag metadata on a symbol
    /// </summary>
    private static string? ExtractTypeHint(ReaderSymbol param)
    {
        if (param.Meta is null) return null;

        // Find the :tag key
        object? tag = null;
        foreach (var kv in param.Meta)
        {
            if (kv.Key is Keyword kw && kw.Name == "tag")
            {
                tag = kv.Value;
                break;
            }
        }

        if (tag is null) return null;

        // Get the type name from the tag symbol
        if (tag is ReaderSymbol typeSym)
        {
            var typeName = typeSym.Namespace is null ? typeSym.Name : $"{typeSym.Namespace}.{typeSym.Name}";
            return NormalizeTypeName(typeName);
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

    #endregion
}

public enum ExprContext
{
    Statement,
    Expression,
    Return
}
