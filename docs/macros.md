# Cljr Macro System

This document covers Cljr's macro implementation, including built-in macros, user-defined macros, and known limitations.

## Overview

Cljr macros are **compile-time transformations** that expand during the analysis phase:

```
Source Form
    ↓
Macro Expansion (if macro)
    ↓
Expanded Form
    ↓
Analysis → AST
```

**Key difference from JVM Clojure:** Macros run in a pure interpreter, not the full runtime.

---

## Built-in Macros

### Control Flow

| Macro | Expansion |
|-------|-----------|
| `(when test body...)` | `(if test (do body...) nil)` |
| `(when-not test body...)` | `(if test nil (do body...))` |
| `(when-let [x expr] body...)` | `(let [x expr] (when x body...))` |
| `(if-let [x expr] then else)` | `(let [x expr] (if x then else))` |
| `(if-not test then else)` | `(if test else then)` |
| `(cond & clauses)` | Nested `if`/`else` chain |

### Short-Circuit Logic

| Macro | Behavior |
|-------|----------|
| `(and a b c)` | Returns first falsy or last value |
| `(or a b c)` | Returns first truthy or last value |
| `(not x)` | `(if x false true)` |

**Implementation:** Uses temp variables to avoid double evaluation:
```clojure
(and x y)
→ (let [__and_0 x] (if __and_0 y __and_0))
```

### Threading Macros

| Macro | Example | Expansion |
|-------|---------|-----------|
| `->` | `(-> x (f a) (g b))` | `(g (f x a) b)` |
| `->>` | `(->> x (f a) (g b))` | `(g b (f a x))` |
| `doto` | `(doto x (.setA 1) (.setB 2))` | Returns `x` after calls |

### Iteration

| Macro | Purpose |
|-------|---------|
| `dotimes` | Loop with counter |

**dotimes expansion:**
```clojure
(dotimes [i 10] (println i))
→
(let [n__0 10]
  (loop [i 0]
    (when (< i n__0)
      (println i)
      (recur (inc i)))))
```

### Async

| Macro | Purpose |
|-------|---------|
| `future` | Wrap body in Task.Run |
| `time` | Measure execution time |

**time expansion:**
```clojure
(time expr)
→
(let [sw (System.Diagnostics.Stopwatch/StartNew)]
  (let [result expr]
    (.Stop sw)
    (println (str "Elapsed time: " (.ElapsedMilliseconds sw) " msecs"))
    result))
```

---

## User-Defined Macros

### Defining Macros

```clojure
(defmacro unless [test & body]
  `(if (not ~test)
     (do ~@body)))

;; Usage
(unless false (println "runs!"))
```

### Syntax-Quote

The backtick (`) enables macro templating:

| Syntax | Meaning |
|--------|---------|
| `` `form`` | Quote entire form |
| `~expr` | Unquote - evaluate and insert |
| `~@expr` | Unquote-splicing - evaluate and splice |
| `foo#` | Auto-gensym - unique symbol |

**Example:**
```clojure
(defmacro my-let [bindings & body]
  `(let ~bindings ~@body))

(my-let [x 10] (+ x 1))
→ (let [x 10] (+ x 1))
```

### Auto-Gensym

Symbols ending in `#` get unique suffixes:

```clojure
(defmacro twice [expr]
  `(let [result# ~expr]
     (+ result# result#)))

;; Expands to something like:
(let [result__12345__auto__ expr]
  (+ result__12345__auto__ result__12345__auto__))
```

---

## Macro Architecture

### Components

| Component | File | Purpose |
|-----------|------|---------|
| `MacroExpander` | `MacroExpander.cs` | Registry + expansion |
| `MacroInterpreter` | `MacroInterpreter.cs` | Pure evaluator |
| `MacroRuntime` | `MacroRuntime.cs` | Built-in functions |
| `MacroEnv` | `MacroExpander.cs` | Lexical scope |

### Full Architecture Diagram

```
┌─────────────────────────────────────────────────────────────┐
│                    COMPILE TIME                              │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              MacroExpander                           │    │
│  │  ┌─────────────────────────────────────────────┐    │    │
│  │  │         MacroInterpreter                     │    │    │
│  │  │  (Pure Lisp interpreter - no Roslyn)        │    │    │
│  │  │                                              │    │    │
│  │  │  ┌─────────────────────────────────────┐    │    │    │
│  │  │  │        MacroRuntime                  │    │    │    │
│  │  │  │  ~50 core fns: first, rest, cons,   │    │    │    │
│  │  │  │  map, filter, str, gensym, etc.     │    │    │    │
│  │  │  └─────────────────────────────────────┘    │    │    │
│  │  └─────────────────────────────────────────────┘    │    │
│  └─────────────────────────────────────────────────────┘    │
│                          ↓                                   │
│                   Expanded Forms                             │
│                          ↓                                   │
│  ┌─────────────────────────────────────────────────────┐    │
│  │              Analyzer → AST                          │    │
│  └─────────────────────────────────────────────────────┘    │
│                          ↓                                   │
│  ┌─────────────────────────────────────────────────────┐    │
│  │         CSharpEmitter → C# Source                    │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
                           ↓
┌─────────────────────────────────────────────────────────────┐
│                     RUNTIME                                  │
│  ┌─────────────────────────────────────────────────────┐    │
│  │         Roslyn Compilation → IL → Execution          │    │
│  │         (Core.cs functions available here)           │    │
│  └─────────────────────────────────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

---

## Why This Architecture?

### The Chicken-and-Egg Problem

Cljr faces a fundamental bootstrapping challenge that doesn't exist in JVM Clojure:

1. **Macros run at compile time** - before any C# code is generated
2. **Runtime functions (Core.cs) don't exist yet** - they're part of the C# being generated
3. **Can't use Roslyn to compile macro bodies** - that would create a circular dependency

**JVM Clojure's Approach:**
- Clojure runs on a JVM that is already running
- Macros execute in the same runtime as regular code
- All vars and functions are available during macro expansion

**Cljr's Challenge:**
- We're *generating* C# code, not running it
- The runtime doesn't exist until *after* compilation
- We can't call `Core.first()` because it's in the C# we're producing

### The Solution: A Lisp Within a Lisp

Cljr implements a **pure interpreter** (`MacroInterpreter`) that evaluates macro bodies without any Roslyn compilation:

```
Clojure macro code → MacroInterpreter evaluates it → Returns expanded form
                           ↓
              Uses only MacroRuntime functions
              (self-contained, no external deps)
```

This is essentially a **mini Lisp interpreter embedded in the compiler**.

### Comparison with JVM Clojure

| Aspect | JVM Clojure | Cljr |
|--------|-------------|------|
| Macro execution environment | Same JVM runtime | Separate interpreter |
| Function access in macros | All vars available | Only MacroRuntime (~50 fns) |
| User functions in macros | Yes | No |
| `&env` (lexical environment) | Available | Not implemented |
| `&form` (original form) | Available | Not implemented |
| Reader macros | Extensible | Fixed set |
| Macro compilation | Same as code | Interpreted |

### Trade-offs

**Pros of this approach:**
- Simple, predictable macro expansion
- No circular compilation dependencies
- Fast expansion (no Roslyn overhead per macro)
- Self-contained - macros work without runtime

**Cons:**
- Limited function access in macros (only MacroRuntime)
- Must manually duplicate core functions in MacroRuntime
- No compile-time computation with arbitrary user code
- Missing `&env`/`&form` for advanced macros
- Can't extend reader syntax

---

### MacroInterpreter

Macros run in a pure interpreter (no Roslyn compilation):

```csharp
public object? Eval(object? form, MacroEnv env)
{
    return form switch
    {
        null => null,
        Symbol s => env.Lookup(s.Name),
        PersistentList list => EvalList(list, env),
        PersistentVector vec => EvalVector(vec, env),
        // ...
    };
}
```

**Supported special forms in macro bodies:**
- `quote`, `if`, `let`, `do`, `fn`, `recur`
- `syntax-quote`, `unquote`, `unquote-splicing`

### MacroRuntime Functions

Available in macro bodies:

**Collections:**
- `first`, `rest`, `next`, `cons`, `conj`
- `concat`, `list`, `vector`, `hash-map`, `hash-set`
- `count`, `empty?`, `seq`, `vec`

**Predicates:**
- `nil?`, `some?`, `list?`, `vector?`, `map?`, `set?`
- `symbol?`, `keyword?`, `string?`, `number?`

**Manipulation:**
- `str`, `name`, `symbol`, `keyword`, `gensym`
- `get`, `assoc`, `dissoc`, `update`

**Higher-order:**
- `map`, `filter`, `reduce`, `mapcat`

**Math/Logic:**
- `+`, `-`, `*`, `inc`, `dec`
- `<`, `<=`, `>`, `>=`, `=`, `not=`
- `and`, `or`, `not`

---

## Macro Expansion Process

### 1. Detection

```csharp
if (_macroExpander.IsMacro(symbolName))
{
    var expanded = _macroExpander.Macroexpand(form);
    return Analyze(expanded, ctx);
}
```

### 2. Parameter Binding

```csharp
void BindParams(PersistentVector params, PersistentList args, MacroEnv env)
{
    for (int i = 0; i < params.Count; i++)
    {
        var param = params[i] as Symbol;
        if (param.Name == "&")
        {
            // Rest parameter
            var restParam = params[i + 1] as Symbol;
            env.Bind(restParam.Name, args.Skip(i).ToList());
            break;
        }
        env.Bind(param.Name, args[i]);
    }
}
```

### 3. Body Evaluation

```csharp
object? result = null;
foreach (var bodyForm in macroBody)
{
    result = _interpreter.Eval(bodyForm, env);
}
return result;  // Expanded form
```

### 4. Recursive Expansion

```csharp
public object? Macroexpand(object? form)
{
    var expanded = MacroexpandOne(form);
    while (!ReferenceEquals(expanded, form))
    {
        form = expanded;
        expanded = MacroexpandOne(form);
    }
    return form;
}
```

---

## Quirks and Limitations

### 1. No `doseq` or `for`

**Issue:** These macros are not implemented.

**Workaround:** Use `dotimes` or manual `loop`:
```clojure
;; Instead of (doseq [x coll] (println x))
(loop [xs (seq coll)]
  (when xs
    (println (first xs))
    (recur (rest xs))))
```

### 2. Limited Function Access

**Issue:** Macro bodies can only call MacroRuntime functions.

```clojure
;; Works (MacroRuntime has str)
(defmacro debug [x]
  `(println ~(str "DEBUG: " x)))

;; Won't work (no user function access)
(defmacro my-macro [x]
  `(my-helper-fn ~x))  ; my-helper-fn unavailable
```

### 3. Qualified Macro Calls Don't Expand

**Issue:** `ns/macro-name` won't trigger expansion.

```clojure
;; Expands
(my-macro x)

;; Does NOT expand
(my-ns/my-macro x)
```

### 4. No Compile-Time Computation

**Issue:** Can't call arbitrary functions during compilation.

```clojure
;; JVM Clojure can do this:
(defmacro constants []
  (read-file "constants.edn"))  ; NOT supported

;; Workaround: inline the data
(defmacro constants []
  '{:a 1 :b 2})
```

### 5. Nested Syntax-Quote

**Issue:** Deeply nested syntax-quote can be confusing.

```clojure
;; Single level - clear
`(list ~x ~@xs)

;; Nested - complex behavior
`(defmacro foo [] `(+ ~~x 1))
```

### 6. Gensym Collisions

**Issue:** Auto-gensym only unique within expansion.

```clojure
;; Two macro calls may conflict if nested improperly
(defmacro twice [x]
  `(let [v# ~x] (+ v# v#)))

(twice (twice 5))  ; May have issues
```

**Workaround:** Use `(gensym "prefix")` for complex macros.

### 7. No &env or &form

**Issue:** Unlike JVM Clojure, no access to:
- `&env` - lexical environment
- `&form` - original form with metadata

### 8. Macroexpand at Runtime

**Issue:** `macroexpand` returns data, not executable code.

```clojure
(macroexpand '(when true 1))
;; Returns quoted list, can't execute directly
```

### 9. Macro Metadata

**Issue:** User macros don't preserve source metadata.

### 10. No Reader Macros

**Issue:** Can't define new reader syntax like `#inst` or `#uuid`.

---

## Examples

### Simple Macro

```clojure
(defmacro unless [test & body]
  `(if (not ~test)
     (do ~@body)))

(unless false
  (println "This runs"))
```

### With Gensym

```clojure
(defmacro with-timer [& body]
  `(let [start# (System.DateTime/Now)]
     (let [result# (do ~@body)]
       (println (str "Took: "
                     (.-TotalMilliseconds
                       (.- (System.DateTime/Now) start#))
                     " ms"))
       result#)))
```

### Conditional Compilation

```clojure
(defmacro debug [& body]
  (if DEBUG-MODE
    `(do ~@body)
    nil))
```

### DSL Building

```clojure
(defmacro html [tag & body]
  `(str "<" ~(name tag) ">"
        ~@body
        "</" ~(name tag) ">"))

(html :div (html :p "Hello"))
;; => "<div><p>Hello</p></div>"
```

---

## Debugging Macros

### Check Expansion

```clojure
(macroexpand '(when true (println "hi")))
;; => (if true (do (println "hi")) nil)
```

### Single Step

```clojure
(macroexpand-1 '(-> x f g))
;; => (g (-> x f))

(macroexpand '(-> x f g))
;; => (g (f x))
```

### Print During Expansion

```clojure
(defmacro debug-macro [form]
  (println "Expanding:" form)
  form)
```

---

## File References

| Component | File | Lines |
|-----------|------|-------|
| Built-in Macros | `Analyzer.cs` | 1489-1863 |
| MacroExpander | `MacroExpander.cs` | 1-588 |
| MacroInterpreter | `MacroInterpreter.cs` | 19-612 |
| MacroRuntime | `MacroRuntime.cs` | 18-543 |
| Syntax-Quote | `MacroExpander.cs` | 217-425 |
| defmacro Analysis | `Analyzer.cs` | 1816-1822 |
