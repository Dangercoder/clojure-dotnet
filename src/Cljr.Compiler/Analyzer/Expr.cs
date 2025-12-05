using Cljr.Compiler.Reader;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;

namespace Cljr.Compiler.Analyzer;

/// <summary>
/// Base class for all analyzed expressions
/// </summary>
public abstract record Expr
{
    public IReadOnlyDictionary<object, object>? Meta { get; init; }

    /// <summary>
    /// Is this expression in an async context?
    /// </summary>
    public bool IsAsync { get; init; }
}

/// <summary>
/// Literal value: 42, "hello", true, nil
/// </summary>
public record LiteralExpr(object? Value) : Expr;

/// <summary>
/// Symbol reference: foo, bar/baz
/// </summary>
public record SymbolExpr(ReaderSymbol Symbol, bool IsLocal) : Expr;

/// <summary>
/// Keyword literal: :foo, :bar/baz
/// </summary>
public record KeywordExpr(Keyword Keyword) : Expr;

/// <summary>
/// Vector literal: [1 2 3]
/// </summary>
public record VectorExpr(IReadOnlyList<Expr> Items) : Expr;

/// <summary>
/// Map literal: {:a 1 :b 2}
/// </summary>
public record MapExpr(IReadOnlyList<(Expr Key, Expr Value)> Pairs) : Expr;

/// <summary>
/// Set literal: #{1 2 3}
/// </summary>
public record SetExpr(IReadOnlyList<Expr> Items) : Expr;

/// <summary>
/// def: (def foo 42) or (defn- foo [] ...) for private functions
/// </summary>
public record DefExpr(ReaderSymbol Name, Expr? Init, string? DocString, string? TypeHint = null, bool IsPrivate = false) : Expr;

/// <summary>
/// Function definition: (fn [x] body) or (defn name [x] body)
/// </summary>
public record FnExpr(
    ReaderSymbol? Name,
    IReadOnlyList<FnMethod> Methods,
    bool IsVariadic
) : Expr;

/// <summary>
/// A single arity method of a function
/// </summary>
public record FnMethod(
    IReadOnlyList<ReaderSymbol> Params,
    ReaderSymbol? RestParam,
    Expr Body,
    string? ReturnType = null,
    IReadOnlyList<string?>? ParamTypes = null
);

/// <summary>
/// let binding: (let [x 1 y 2] body)
/// </summary>
public record LetExpr(
    IReadOnlyList<(ReaderSymbol Name, Expr Init)> Bindings,
    Expr Body
) : Expr;

/// <summary>
/// do block: (do expr1 expr2 expr3)
/// </summary>
public record DoExpr(IReadOnlyList<Expr> Exprs) : Expr;

/// <summary>
/// if expression: (if test then else)
/// </summary>
public record IfExpr(Expr Test, Expr Then, Expr? Else) : Expr;

/// <summary>
/// Function invocation: (f arg1 arg2)
/// </summary>
public record InvokeExpr(Expr Fn, IReadOnlyList<Expr> Args) : Expr;

/// <summary>
/// .NET instance method call: (.method obj args)
/// Supports generic methods: (.|Method&lt;T&gt;| obj args)
/// </summary>
public record InstanceMethodExpr(
    string MethodName,
    Expr Target,
    IReadOnlyList<Expr> Args,
    IReadOnlyList<string>? TypeArguments = null
) : Expr;

/// <summary>
/// .NET instance property/field access: (.-prop obj)
/// </summary>
public record InstancePropertyExpr(string PropertyName, Expr Target) : Expr;

/// <summary>
/// .NET static method call: (Type/method args)
/// Supports generic methods: (Type/|Method&lt;T&gt;| args)
/// </summary>
public record StaticMethodExpr(
    string TypeName,
    string MethodName,
    IReadOnlyList<Expr> Args,
    IReadOnlyList<string>? TypeArguments = null
) : Expr;

/// <summary>
/// .NET static property/field access: Type/FIELD
/// </summary>
public record StaticPropertyExpr(string TypeName, string PropertyName) : Expr;

/// <summary>
/// new expression: (new Type args) or (Type. args)
/// </summary>
public record NewExpr(string TypeName, IReadOnlyList<Expr> Args) : Expr;

/// <summary>
/// Cast expression from type hint: ^Type expr
/// Generates C# cast: ((Type)expr)
/// </summary>
public record CastExpr(string TypeName, Expr Value) : Expr;

/// <summary>
/// set! expression: (set! target value)
/// </summary>
public record AssignExpr(Expr Target, Expr Value) : Expr;

/// <summary>
/// throw expression: (throw ex)
/// </summary>
public record ThrowExpr(Expr Exception) : Expr;

/// <summary>
/// try/catch/finally: (try body (catch Ex e handler) (finally cleanup))
/// </summary>
public record TryExpr(
    Expr Body,
    IReadOnlyList<CatchClause> Catches,
    Expr? Finally
) : Expr;

public record CatchClause(string ExceptionType, ReaderSymbol Binding, Expr Body);

/// <summary>
/// loop/recur: (loop [x 0] (if (> x 10) x (recur (inc x))))
/// </summary>
public record LoopExpr(
    IReadOnlyList<(ReaderSymbol Name, Expr Init)> Bindings,
    Expr Body
) : Expr;

/// <summary>
/// recur expression: (recur args)
/// </summary>
public record RecurExpr(IReadOnlyList<Expr> Args) : Expr;

/// <summary>
/// quote expression: 'form or (quote form)
/// </summary>
public record QuoteExpr(object? Form) : Expr;

/// <summary>
/// await expression: (await task)
/// </summary>
public record AwaitExpr(Expr Task) : Expr;

/// <summary>
/// ns declaration: (ns foo.bar (:require ...) (:import ...))
/// </summary>
public record NsExpr(
    string Name,
    IReadOnlyList<RequireClause> Requires,
    IReadOnlyList<ImportClause> Imports
) : Expr;

public record RequireClause(string Namespace, string? Alias, IReadOnlyList<string>? Refers);
public record ImportClause(string Namespace, IReadOnlyList<string> Types);

/// <summary>
/// in-ns expression: (in-ns 'foo.bar)
/// Switches to a namespace (creating if needed) - primarily for REPL use
/// </summary>
public record InNsExpr(string Name) : Expr;

/// <summary>
/// Standalone require expression: (require '[namespace :as alias])
/// Loads a namespace and optionally creates an alias or refers vars.
/// Used at REPL for dynamic loading; in source files, use (ns ... (:require ...))
/// </summary>
public record RequireExpr(IReadOnlyList<RequireClause> Clauses) : Expr;

/// <summary>
/// A complete compilation unit (file)
/// </summary>
public record CompilationUnit(
    NsExpr? Namespace,
    IReadOnlyList<Expr> TopLevelExprs
);

/// <summary>
/// Raw C# code embedding: (csharp* "DateTime.Now.Year")
/// Supports interpolation: (csharp* "~{name}.ToString()")
/// </summary>
public record RawCSharpExpr(
    string Template,
    IReadOnlyList<(string Placeholder, Expr Value)> Interpolations
) : Expr;

/// <summary>
/// Primitive arithmetic/comparison operation with known types.
/// Emits native C# operators instead of Core function calls.
/// Example: (+ ^long a ^long b) → (a + b) instead of Core._PLUS_(a, b)
/// </summary>
public record PrimitiveOpExpr(
    string Operator,      // "+", "-", "*", "/", "<", ">", "<=", ">=", "=="
    string PrimitiveType, // "long", "double", "int"
    IReadOnlyList<Expr> Operands
) : Expr;

/// <summary>
/// Represents a .NET attribute to be emitted: [AttributeName] or [AttributeName(args)]
/// Example: [Parameter], [Inject], [Required("message")]
/// </summary>
public record AttributeSpec(string Name, IReadOnlyList<object?>? Arguments = null);

/// <summary>
/// Represents a field in defrecord/deftype with optional type hint and attributes.
/// Example: ^{:attr [Parameter]} ^int CurrentCount
/// </summary>
public record FieldSpec(ReaderSymbol Name, string? TypeHint, IReadOnlyList<AttributeSpec>? Attributes = null);

/// <summary>
/// Protocol definition: (defprotocol IFoo (^String bar [this ^int x]))
/// Generates a C# interface
/// </summary>
public record DefprotocolExpr(
    ReaderSymbol Name,
    IReadOnlyList<ProtocolMethod> Methods
) : Expr;

/// <summary>
/// A method signature in a protocol
/// </summary>
public record ProtocolMethod(
    ReaderSymbol Name,
    IReadOnlyList<(ReaderSymbol Name, string? TypeHint)> Params,
    string? ReturnType
);

/// <summary>
/// Type definition: (deftype Point [^double x ^double y] IFoo (bar [this] ...))
/// Generates a C# class
/// </summary>
public record DeftypeExpr(
    ReaderSymbol Name,
    IReadOnlyList<FieldSpec> Fields,
    IReadOnlyList<ReaderSymbol> Interfaces,
    IReadOnlyList<TypeMethodImpl> Methods
) : Expr;

/// <summary>
/// Record definition: (defrecord User [^String id ^String name])
/// Generates a C# record
/// </summary>
public record DefrecordExpr(
    ReaderSymbol Name,
    IReadOnlyList<FieldSpec> Fields,
    IReadOnlyList<ReaderSymbol> Interfaces,
    IReadOnlyList<TypeMethodImpl> Methods
) : Expr;

/// <summary>
/// A method implementation in deftype/defrecord
/// </summary>
public record TypeMethodImpl(
    ReaderSymbol Name,
    IReadOnlyList<ReaderSymbol> Params,
    Expr Body,
    string? ReturnType,
    IReadOnlyList<string?>? ParamTypes,
    IReadOnlyList<AttributeSpec>? Attributes = null
);

/// <summary>
/// Test definition: (deftest test-name body...)
/// Defines a test function that tracks assertions and returns TestRunResult
/// </summary>
public record DefTestExpr(
    ReaderSymbol Name,
    Expr Body
) : Expr;

/// <summary>
/// Test assertion: (is expr) or (is (= expected actual)) or (is expr "message")
/// Records pass/fail and returns the result
/// </summary>
public record IsExpr(
    Expr Test,
    Expr? Expected,  // For (is (= expected actual))
    Expr? Actual,    // For (is (= expected actual))
    Expr? Message
) : Expr;

/// <summary>
/// Type check expression: (instance? Type value)
/// Generates C# pattern matching: value is Type
/// </summary>
public record InstanceCheckExpr(string TypeName, Expr Value) : Expr;
