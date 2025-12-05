# Cljr .NET Interop Guide

This document covers how Cljr interoperates with .NET, including syntax, type resolution, and known quirks.

## Syntax Overview

| Operation | Clojure Syntax | C# Equivalent |
|-----------|---------------|---------------|
| Static method | `(Type/Method args)` | `Type.Method(args)` |
| Static property | `Type/PROPERTY` | `Type.PROPERTY` |
| Instance method | `(.method obj args)` | `obj.Method(args)` |
| Instance property | `(.-prop obj)` | `obj.Prop` |
| Constructor | `(Type. args)` | `new Type(args)` |
| Generic method | `(.\|Method<T>\| obj)` | `obj.Method<T>()` |

---

## 1. Static Method Calls

**Syntax:** `(Type/Method args...)`

```clojure
;; Simple static call
(String/IsNullOrEmpty "")
;; => String.IsNullOrEmpty("")

;; With namespace
(System.Math/Max 10 20)
;; => System.Math.Max(10, 20)

;; Generic method
(Enumerable/|ToList<String>| coll)
;; => Enumerable.ToList<String>(coll)
```

**Detection:** Symbol with uppercase namespace prefix is treated as static call.

---

## 2. Instance Method Calls

**Syntax:** `(.method obj args...)`

```clojure
(.ToUpper "hello")
;; => "hello".ToUpper()

(.Substring "hello" 0 3)
;; => "hello".Substring(0, 3)

;; Generic instance method
(.|GetEnumerator<T>| collection)
;; => collection.GetEnumerator<T>()
```

**With type hints:**
```clojure
(defn fetch [^HttpClient client url]
  (.GetStringAsync client url))
;; => ((HttpClient)client).GetStringAsync(url)
```

---

## 3. Instance Property/Field Access

**Syntax:** `(.-property obj)`

```clojure
(.-Length "hello")
;; => "hello".Length

(.-Count my-list)
;; => myList.Count
```

**Note:** Both properties and fields use the same syntax. C# compiler resolves at compile time.

---

## 4. Static Property/Field Access

**Syntax:** `Type/PROPERTY`

```clojure
Int32/MaxValue
;; => Int32.MaxValue

DateTime/Now
;; => DateTime.Now
```

---

## 5. Constructor Calls

**Syntax:** `(Type. args...)` or `(new Type args...)`

```clojure
;; Short form
(String. "hello")
;; => new String("hello")

;; Explicit new
(new System.Text.StringBuilder 100)
;; => new System.Text.StringBuilder(100)

;; With namespace (converted to C# format)
(System.Collections.Generic/List. 10)
;; => new System.Collections.Generic.List(10)
```

---

## 6. Generic Methods

Generic type arguments require pipe-escaping when angle brackets conflict with Clojure syntax:

```clojure
;; Pipe-escaped (recommended)
(.|Parse<Int32>| "42")

;; Instance generic
(.GetService<ILogger> provider)

;; Static generic
(JsonSerializer/|Deserialize<MyRecord>| json)
```

**Nested generics:**
```clojure
(.|GetEnumerator<Dictionary<String, List<Int32>>>| obj)
```

---

## 7. Type Hints

Type hints improve performance and ensure correct method dispatch:

**Syntax:** `^Type` or `^{:tag Type}`

```clojure
;; Parameter type hint
(defn process [^HttpClient client]
  (.GetStringAsync client "http://example.com"))

;; Return type hint
(defn ^String get-name [] "hello")

;; Local binding type hint
(let [^StringBuilder sb (StringBuilder. 100)]
  (.Append sb "hello"))
```

### Type Hint Behavior

| Type | Cast Method | Why |
|------|-------------|-----|
| Reference types | `((Type)expr)` | Standard C# cast |
| Primitive numerics | `Convert.ToXXX(expr)` | Handles boxed values safely |

**Quirk:** Numeric casts use `Convert.ToXXX()` to avoid `InvalidCastException` when unboxing:
```clojure
;; This works correctly:
(let [x (long 5)]
  (int x))  ; Uses Convert.ToInt32, not (int)
```

---

## 8. Raw C# Embedding

For complex interop scenarios, embed raw C# code:

**Syntax:** `(csharp* "code")`

```clojure
;; Simple expression
(csharp* "DateTime.Now.Year")

;; With interpolation
(let [name "world"]
  (csharp* "~{name}.ToUpper()"))
;; => name.ToUpper()

;; Multi-expression
(csharp* "~{a} + ~{b}")
```

---

## 9. Async/Await

**Syntax:** `(await expr)` with `^:async` metadata

```clojure
(defn ^:async fetch-data [url]
  (let [^HttpClient client (HttpClient.)]
    (await (.GetStringAsync client url))))
```

**Generated C#:**
```csharp
public static async Task<object?> fetch_data(object? url)
{
    var client = new HttpClient();
    return await client.GetStringAsync(url);
}
```

---

## 10. Imports and Requires

### Importing .NET Types

```clojure
(ns my-app.core
  (:import [System.Net.Http HttpClient HttpResponseMessage]
           [System.Text.Json JsonSerializer]))

;; Now accessible without full qualification
(HttpClient.)
(JsonSerializer/Serialize data)
```

### Namespace Munging

Clojure namespace names are converted to C#:

| Clojure | C# |
|---------|-----|
| `my-app.core` | `My_app.Core` |
| `foo.bar-baz` | `Foo.Bar_baz` |

---

## Quirks and Limitations

### 1. Overload Resolution

**Issue:** C# method overloading is handled by the C# compiler, not Cljr.

**Workaround:** Use type hints to guide overload selection:
```clojure
;; Ambiguous - compiler picks based on argument types
(.Parse "42")

;; Explicit - ensures Int32.Parse overload
(Int32/Parse "42")
```

### 2. Generic Type Inference

**Issue:** Generic type parameters must be explicit.

```clojure
;; Won't work - no type inference
(.ToList coll)

;; Must specify type
(.|ToList<String>| coll)
```

### 3. Out/Ref Parameters

**Issue:** Not directly supported.

**Workaround:** Use `csharp*` for out/ref scenarios:
```clojure
(csharp* "int.TryParse(~{s}, out var result) ? result : 0")
```

### 4. Null-Conditional Operators

**Issue:** No `?.` syntax in Clojure.

**Workaround:**
```clojure
;; Manual null check
(when obj (.-Property obj))

;; Or use csharp*
(csharp* "~{obj}?.Property")
```

### 5. Extension Methods

**Issue:** Extension methods must be called as static methods.

```clojure
;; LINQ extension methods
(Enumerable/Where coll pred)
(Enumerable/Select coll selector)
```

### 6. Static Members on Lowercase Types

**Issue:** Static member detection requires uppercase first character.

**Workaround:** Use fully qualified names or `csharp*`:
```clojure
(csharp* "myNamespace.myType.StaticMethod()")
```

### 7. Assembly Loading

**Issue:** No direct `Assembly.Load` support.

**Solution:** Types must be:
1. Referenced via project dependencies
2. Imported in namespace declaration
3. Or accessed via fully qualified names

### 8. Nullable Reference Types

**Issue:** Cljr uses `object?` everywhere - no NRT enforcement.

All interop values are treated as potentially null.

### 9. Property vs Method Ambiguity

Both use parenthesized syntax in Clojure, but:
- `(.-Prop obj)` for properties
- `(.Method obj)` for methods

**Quirk:** If you write `(.Count list)` when `Count` is a property, it may fail.

### 10. REPL Type Resolution

In REPL mode, types are resolved through namespace isolation:
- Types from `defrecord`/`deftype` only visible if imported
- Use `(require '[ns :as alias])` to access types from other namespaces

---

## Examples

### HTTP Client
```clojure
(ns my-app.http
  (:import [System.Net.Http HttpClient]))

(defn ^:async fetch [^String url]
  (let [^HttpClient client (HttpClient.)]
    (try
      (await (.GetStringAsync client url))
      (finally
        (.Dispose client)))))
```

### JSON Serialization
```clojure
(ns my-app.json
  (:import [System.Text.Json JsonSerializer]))

(defn to-json [obj]
  (JsonSerializer/Serialize obj))

(defn from-json [^String json]
  (JsonSerializer/|Deserialize<Object>| json))
```

### Collections Interop
```clojure
(ns my-app.collections
  (:import [System.Collections.Generic List Dictionary]))

(defn make-list []
  (let [^List<String> list (List.)]
    (.Add list "one")
    (.Add list "two")
    list))

(defn make-dict []
  (let [dict (Dictionary.)]
    (.Add dict "key" "value")
    dict))
```

### LINQ Usage
```clojure
(ns my-app.linq
  (:import [System.Linq Enumerable]))

(defn filter-evens [coll]
  (Enumerable/Where coll (fn [x] (= 0 (mod x 2)))))

(defn select-names [people]
  (Enumerable/Select people (fn [p] (.-Name p))))
```

---

## File References

| Component | File | Key Lines |
|-----------|------|-----------|
| Interop Analysis | `Analyzer.cs` | 304-430 |
| Type Resolution | `Analyzer.cs` | 590-612 |
| Interop Emission | `CSharpEmitter.cs` | 1884-2007 |
| Generic Parsing | `CSharpEmitter.cs` | 1378-1420 |
| Cast Emission | `CSharpEmitter.cs` | 2743-2781 |
