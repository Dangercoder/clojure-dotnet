# Cljr Transpiler Architecture

This document covers how Cljr transpiles Clojure code to C#.

## Pipeline Overview

```
Clojure Source Text
    ↓
┌─────────────────────────────────────────┐
│ READER (LispReader.cs)                   │
│ - Tokenization                           │
│ - S-expression parsing                   │
│ - Produces: Lists, Vectors, Maps, etc.  │
└─────────────────────────────────────────┘
    ↓
┌─────────────────────────────────────────┐
│ ANALYZER (Analyzer.cs)                   │
│ - Special form handling                  │
│ - Macro expansion                        │
│ - Type checking                          │
│ - Produces: Expr AST nodes              │
└─────────────────────────────────────────┘
    ↓
┌─────────────────────────────────────────┐
│ EMITTER (CSharpEmitter.cs)               │
│ - C# code generation                     │
│ - Name munging                           │
│ - Type resolution                        │
│ - Produces: C# source code string       │
└─────────────────────────────────────────┘
    ↓
C# Source Code
```

---

## Reader Stage

**File:** `src/Cljr.Compiler/Reader/LispReader.cs`

### Supported Syntax

| Syntax | Example | Output Type |
|--------|---------|-------------|
| Lists | `(a b c)` | `PersistentList` |
| Vectors | `[1 2 3]` | `PersistentVector` |
| Maps | `{:a 1}` | `PersistentMap` |
| Sets | `#{1 2}` | `PersistentSet` |
| Keywords | `:name` | `Keyword` |
| Symbols | `foo` | `Symbol` |
| Strings | `"hello"` | `string` |
| Numbers | `42`, `3.14` | `long`, `double` |
| Booleans | `true`, `false` | `bool` |
| Nil | `nil` | `null` |
| Quote | `'x` | `(quote x)` |
| Deref | `@x` | `(deref x)` |
| Metadata | `^Type x` | Attached to form |

### Special Reader Syntax

| Syntax | Expansion |
|--------|-----------|
| `'form` | `(quote form)` |
| `@form` | `(deref form)` |
| `` `form`` | `(syntax-quote form)` |
| `~form` | `(unquote form)` |
| `~@form` | `(unquote-splicing form)` |
| `#'var` | `(var var)` |
| `#(...)` | Anonymous function |

---

## Analyzer Stage

**File:** `src/Cljr.Compiler/Analyzer/Analyzer.cs`

### Expression Types

**File:** `src/Cljr.Compiler/Analyzer/Expr.cs`

#### Literals
```csharp
LiteralExpr(value)        // 42, "hello", true
KeywordExpr(keyword)      // :name
VectorExpr(items)         // [1 2 3]
MapExpr(pairs)            // {:a 1}
SetExpr(items)            // #{1 2}
```

#### Definitions
```csharp
DefExpr(name, init, docString, typeHint, isPrivate)
DefrecordExpr(name, fields, interfaces, methods)
DeftypeExpr(name, fields, interfaces, methods)
DefprotocolExpr(name, methods)
```

#### Functions
```csharp
FnExpr(name?, methods, isVariadic)
FnMethod(params, restParam, body, returnType, paramTypes)
```

#### Control Flow
```csharp
LetExpr(bindings, body)
DoExpr(exprs)
IfExpr(test, then, else?)
LoopExpr(bindings, body)
RecurExpr(args)
TryExpr(body, catches, finally?)
ThrowExpr(exception)
```

#### Interop
```csharp
InstanceMethodExpr(methodName, target, args, typeArgs?)
InstancePropertyExpr(propName, target)
StaticMethodExpr(typeName, methodName, args, typeArgs?)
StaticPropertyExpr(typeName, propName)
NewExpr(typeName, args)
CastExpr(typeName, value)
RawCSharpExpr(template, interpolations)
```

### Special Form Dispatch

```csharp
switch (sym.Name)
{
    case "def": return AnalyzeDef(list, ctx);
    case "defn": return AnalyzeDefn(list, ctx);
    case "fn" or "fn*": return AnalyzeFn(list, ctx);
    case "let": return AnalyzeLet(list, ctx);
    case "do": return AnalyzeDo(list, ctx);
    case "if": return AnalyzeIf(list, ctx);
    case "loop": return AnalyzeLoop(list, ctx);
    case "recur": return AnalyzeRecur(list, ctx);
    case "quote": return AnalyzeQuote(list, ctx);
    case "try": return AnalyzeTry(list, ctx);
    // ... 30+ more special forms
}
```

### Macro Expansion

Macros are expanded during analysis:

```csharp
if (sym.Namespace is null && _macroExpander.IsMacro(sym.Name))
{
    var expanded = _macroExpander.Macroexpand(list);
    return Analyze(expanded, ctx);
}
```

---

## Emitter Stage

**File:** `src/Cljr.Compiler/Emitter/CSharpEmitter.cs`

### Emission Contexts

```csharp
enum ExprContext
{
    Expression,  // Part of larger expression
    Statement,   // Standalone statement (emit semicolon)
    Return       // Final return statement
}
```

### Name Munging

| Clojure | C# |
|---------|-----|
| `my-fn` | `my_fn` |
| `+` | `_PLUS_` |
| `first?` | `first_QUESTION` |
| `set!` | `set_BANG` |
| `->vector` | `__GT_vector` |

### Type Normalization

| .NET Type | C# Keyword |
|-----------|------------|
| `String` | `string` |
| `Int32` | `int` |
| `Int64` | `long` |
| `Double` | `double` |
| `Boolean` | `bool` |

---

## Code Generation Examples

### Simple Function

**Clojure:**
```clojure
(defn add [x y] (+ x y))
```

**C#:**
```csharp
public static object? add(object? x, object? y)
{
    return _PLUS_(x, y);
}
```

### Typed Function

**Clojure:**
```clojure
(defn add [^long x ^long y] ^long (+ x y))
```

**C#:**
```csharp
public static long add(long x, long y)
{
    return x + y;  // Primitive operation
}
```

### Multi-Arity Function

**Clojure:**
```clojure
(defn greet
  ([name] (str "Hello, " name))
  ([title name] (str "Hello, " title " " name)))
```

**C#:**
```csharp
public static object? greet(object? name)
{
    return str("Hello, ", name);
}

public static object? greet(object? title, object? name)
{
    return str("Hello, ", title, " ", name);
}
```

### Let Bindings

**Clojure:**
```clojure
(let [x 10 y 20] (+ x y))
```

**C# (Statement):**
```csharp
var x = 10L;
var y = 20L;
return _PLUS_(x, y);
```

**C# (Expression):**
```csharp
((Func<object?>)(() => {
    var x = 10L;
    var y = 20L;
    return _PLUS_(x, y);
}))()
```

### Loop/Recur

**Clojure:**
```clojure
(loop [i 0 sum 0]
  (if (< i 10)
    (recur (inc i) (+ sum i))
    sum))
```

**C#:**
```csharp
object? __result0 = null;
object? i = 0L;
object? sum = 0L;
while (true)
{
    if (IsTruthy(_LESS_(i, 10L)))
    {
        var __recur0 = _INC_(i);
        var __recur1 = _PLUS_(sum, i);
        i = __recur0;
        sum = __recur1;
        continue;
    }
    else
    {
        __result0 = sum;
        break;
    }
}
return __result0;
```

### Anonymous Functions

**Clojure:**
```clojure
(fn [x] (* x 2))
```

**C# (Lambda):**
```csharp
(object? x) => _MULTIPLY_(x, 2L)
```

**Clojure (Multi-statement):**
```clojure
(fn [x]
  (println x)
  (* x 2))
```

**C# (Block Lambda):**
```csharp
(object? x) => {
    println(x);
    return _MULTIPLY_(x, 2L);
}
```

### Interop

**Clojure:**
```clojure
(defn process [^HttpClient client url]
  (.GetStringAsync client url))
```

**C#:**
```csharp
public static object? process(object? client, object? url)
{
    return ((HttpClient)client).GetStringAsync(url);
}
```

---

## Var-Based Code Generation

For REPL/dev mode, functions use Var indirection for hot reload:

**Clojure:**
```clojure
(defn add [x y] (+ x y))
```

**C# (Var Mode):**
```csharp
private static readonly Var _var_add = Var.Intern("my.ns", "add").BindRoot(
    (Func<object?, object?, object?>)((object? x, object? y) => {
        return _PLUS_(x, y);
    })
);

public static object? add(object? x, object? y)
{
    return _var_add.Invoke(x, y);
}
```

**Benefit:** Redefining `add` updates `_var_add.Root`, affecting all callers.

---

## Quirks and Limitations

### 1. Void Return Handling

**Problem:** C# void methods can't be used in expressions.

**Solution:** Detect void-returning calls and emit `null`:
```csharp
if (MightBeVoidReturning(expr))
{
    EmitExpr(expr);
    sb.Append("; return null;");
}
```

### 2. Recur Restrictions

**Valid locations:**
- Tail position of `loop`
- Tail position of `fn` body

**Invalid:**
- Inside nested functions
- Non-tail position

### 3. Primitive Type Detection

For primitive operations to work, ALL operands need type hints:
```clojure
;; Works (primitive add)
(+ ^long a ^long b)

;; Fallback (Core._PLUS_)
(+ a b)
```

### 4. Generic Method Syntax

Angle brackets require pipe escaping:
```clojure
;; Correct
(.|Method<String>| obj)

;; Incorrect (parsed as comparison)
(.Method<String> obj)
```

### 5. Multi-Arity Lambdas

Anonymous functions with multiple arities are limited:
```clojure
;; Only works at top-level (defn)
(defn foo ([x] x) ([x y] (+ x y)))

;; May not work in all contexts
(fn ([x] x) ([x y] (+ x y)))
```

### 6. Async Functions

Must declare async explicitly:
```clojure
(defn ^:async fetch [url]
  (await (.GetStringAsync client url)))
```

Without `^:async`, `await` won't compile correctly.

### 7. Namespace Class Names

Namespace `my-app.core` becomes:
- Class: `Core`
- C# Namespace: `My_app`

Collisions possible with multiple namespaces ending in same segment.

### 8. Local Shadowing

Inner let bindings shadow outer ones correctly, but name collisions with core functions can be confusing:
```clojure
(let [+ (fn [a b] "not addition")]
  (+ 1 2))  ; Returns "not addition"
```

### 9. Metadata Loss

Some metadata is lost during emission:
- Only `:tag`, `:async`, `:private`, `:attr` are processed
- Other metadata ignored

### 10. Error Location

C# compilation errors reference emitted code, not Clojure source:
```
error CS1234: 'my_fn' at line 42
```
Line 42 is in generated C#, not original .cljr file.

---

## Compilation Modes

### 1. Standard Compilation
```csharp
public string Emit(CompilationUnit unit)
```
Full namespace + class wrapper for source files.

### 2. Script Compilation
```csharp
public string EmitAsScript(CompilationUnit unit)
```
Class-based script without namespace.

### 3. REPL Expression
```csharp
public string EmitScript(Expr expr, string ns, ...)
```
Single expression for interactive evaluation.

---

## File References

| Component | File |
|-----------|------|
| Reader | `src/Cljr.Compiler/Reader/LispReader.cs` |
| Analyzer | `src/Cljr.Compiler/Analyzer/Analyzer.cs` |
| Expression Types | `src/Cljr.Compiler/Analyzer/Expr.cs` |
| Emitter | `src/Cljr.Compiler/Emitter/CSharpEmitter.cs` |
| Name Munging | `src/Cljr.Compiler/Emitter/CSharpEmitter.cs` |
