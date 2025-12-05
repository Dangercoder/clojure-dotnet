# Expression Types Reference

All expression types defined in `src/Cljr.Compiler/Analyzer/Expr.cs`.

## Base Class

```csharp
public abstract record Expr
{
    public IReadOnlyDictionary<object, object>? Meta { get; init; }
    public bool IsAsync { get; init; }
}
```

## Literals & Collections

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `LiteralExpr(object? Value)` | `42`, `"hello"`, `true`, `nil` | Literal values |
| `SymbolExpr(Symbol Symbol, bool IsLocal)` | `foo`, `bar/baz` | Symbol reference |
| `KeywordExpr(Keyword Keyword)` | `:foo`, `:bar/baz` | Keyword literal |
| `VectorExpr(IReadOnlyList<Expr> Items)` | `[1 2 3]` | Vector literal |
| `MapExpr(IReadOnlyList<(Expr Key, Expr Value)> Pairs)` | `{:a 1 :b 2}` | Map literal |
| `SetExpr(IReadOnlyList<Expr> Items)` | `#{1 2 3}` | Set literal |

## Control Flow

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `IfExpr(Expr Test, Expr Then, Expr? Else)` | `(if test then else)` | Conditional |
| `DoExpr(IReadOnlyList<Expr> Exprs)` | `(do expr1 expr2)` | Sequential execution |
| `LetExpr(bindings, Expr Body)` | `(let [x 1] body)` | Local bindings |
| `LoopExpr(bindings, Expr Body)` | `(loop [x 0] body)` | Loop with recur target |
| `RecurExpr(IReadOnlyList<Expr> Args)` | `(recur args)` | Tail recursion |
| `TryExpr(Expr Body, catches, Expr? Finally)` | `(try ... (catch ...) (finally ...))` | Exception handling |
| `ThrowExpr(Expr Exception)` | `(throw ex)` | Throw exception |

### CatchClause

```csharp
public record CatchClause(string ExceptionType, Symbol Binding, Expr Body);
```

## Functions & Definitions

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `DefExpr(Symbol Name, Expr? Init, string? DocString)` | `(def foo 42)` | Define var |
| `FnExpr(Symbol? Name, methods, bool IsVariadic)` | `(fn [x] body)` | Function definition |
| `InvokeExpr(Expr Fn, IReadOnlyList<Expr> Args)` | `(f arg1 arg2)` | Function invocation |

### FnMethod

```csharp
public record FnMethod(
    IReadOnlyList<Symbol> Params,
    Symbol? RestParam,
    Expr Body,
    string? ReturnType = null,
    IReadOnlyList<string?>? ParamTypes = null
);
```

## .NET Interop

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `InstanceMethodExpr(string MethodName, Expr Target, args)` | `(.method obj args)` | Instance method call |
| `InstancePropertyExpr(string PropertyName, Expr Target)` | `(.-prop obj)` | Instance property access |
| `StaticMethodExpr(string TypeName, string MethodName, args)` | `(Type/method args)` | Static method call |
| `StaticPropertyExpr(string TypeName, string PropertyName)` | `Type/FIELD` | Static property/field |
| `NewExpr(string TypeName, IReadOnlyList<Expr> Args)` | `(Type. args)`, `(new Type args)` | Constructor call |
| `CastExpr(string TypeName, Expr Value)` | `^Type expr` | Type cast |
| `AssignExpr(Expr Target, Expr Value)` | `(set! target value)` | Assignment |

## Async

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `AwaitExpr(Expr Task)` | `(await task)` | Await a Task |

Use `^:async` metadata on functions to make them async.

## Namespacing

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `NsExpr(string Name, requires, imports)` | `(ns foo.bar ...)` | Namespace declaration |
| `InNsExpr(string Name)` | `(in-ns 'foo.bar)` | Switch namespace |
| `RequireExpr(IReadOnlyList<RequireClause> Clauses)` | `(require '[ns :as alias])` | Load namespace |
| `CompilationUnit(NsExpr?, topLevelExprs)` | - | Complete file |

### RequireClause & ImportClause

```csharp
public record RequireClause(string Namespace, string? Alias, IReadOnlyList<string>? Refers);
public record ImportClause(string Namespace, IReadOnlyList<string> Types);
```

## Advanced

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `RawCSharpExpr(string Template, interpolations)` | `(csharp* "code")` | Raw C# embedding |
| `PrimitiveOpExpr(string Operator, string PrimitiveType, operands)` | `(+ ^long a ^long b)` | Optimized primitive ops |
| `QuoteExpr(object? Form)` | `'form`, `(quote form)` | Quote expression |

### RawCSharpExpr Interpolation

Supports `~{expr}` interpolation:
```clojure
(csharp* "~{name}.ToString()")
```

## Type Definitions

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `DefprotocolExpr(Symbol Name, methods)` | `(defprotocol IFoo ...)` | Define interface |
| `DeftypeExpr(Symbol Name, fields, interfaces, methods)` | `(deftype Point [...] ...)` | Define class |
| `DefrecordExpr(Symbol Name, fields, interfaces, methods)` | `(defrecord User [...])` | Define record |

### Supporting Records

```csharp
public record ProtocolMethod(
    Symbol Name,
    IReadOnlyList<(Symbol Name, string? TypeHint)> Params,
    string? ReturnType
);

public record TypeMethodImpl(
    Symbol Name,
    IReadOnlyList<Symbol> Params,
    Expr Body,
    string? ReturnType,
    IReadOnlyList<string?>? ParamTypes
);
```

## Testing

| Expression | Clojure Syntax | Description |
|------------|----------------|-------------|
| `DefTestExpr(Symbol Name, Expr Body)` | `(deftest test-name body)` | Define test |
| `IsExpr(Expr Test, Expr? Expected, Expr? Actual, Expr? Message)` | `(is expr)` | Test assertion |

## Adding a New Expression Type

1. Add record in `Expr.cs`:
```csharp
/// <summary>
/// Description of the expression
/// </summary>
public record MyExpr(
    TypeHere Property1,
    Expr Property2
) : Expr;
```

2. Add analysis in `Analyzer.cs` to create the expression from parsed forms

3. Add emission in `CSharpEmitter.cs` to generate C# code

4. Write tests in `ReplNamespaceTests.cs` using `NreplSession.EvalAsync()`
