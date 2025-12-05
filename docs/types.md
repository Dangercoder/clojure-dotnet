# Cljr Type Definitions: defrecord & deftype

This document covers Cljr's type definition mechanisms, including syntax, generated code, and known quirks.

## Overview

| Form | C# Output | Mutability | Use Case |
|------|-----------|------------|----------|
| `defrecord` | `record` class | Immutable | Data carriers, DTOs |
| `deftype` | `class` | Mutable | Components, stateful objects |

---

## defrecord

### Basic Syntax

```clojure
(defrecord Person [name age])
```

**Generated C#:**
```csharp
public record Person(object? name, object? age);
```

### With Type Hints

```clojure
(defrecord CreateTodoRequest [^String title ^String description ^bool completed])
```

**Generated C#:**
```csharp
public record CreateTodoRequest(string title, string description, bool completed);
```

### Creating Instances

```clojure
;; Positional constructor
(->Person "Alice" 30)
;; => new Person("Alice", 30)

;; Map constructor (if supported)
(map->Person {:name "Alice" :age 30})

;; Direct constructor call
(Person. "Alice" 30)
```

### Field Access

```clojure
(def p (->Person "Alice" 30))

;; Property access
(.-name p)   ; => "Alice"
(:name p)    ; => "Alice" (keyword access)
```

---

## deftype

### Basic Syntax

```clojure
(deftype Counter [^:mutable count])
```

**Generated C#:**
```csharp
public class Counter
{
    public object? count;
    public Counter(object? count) { this.count = count; }
}
```

### With Interfaces

```clojure
(deftype Stack [^:mutable items]
  ISeq
  (first [this] (first items))
  (next [this] (next items))
  (more [this] (rest items)))
```

**Generated C#:**
```csharp
public class Stack : ISeq
{
    public object? items;

    public Stack(object? items) { this.items = items; }

    public object? first() => Core.first(items);
    public ISeq? next() => Core.next(items);
    public ISeq more() => Core.rest(items);
}
```

### With .NET Attributes (Blazor Example)

```clojure
(deftype Counter [^{:attr [Parameter]} ^:mutable Count]
  ComponentBase
  (Render [this]
    (html [:div
           [:p "Count: " Count]
           [:button {:on-click #(set! Count (inc Count))} "+"]])))
```

**Generated C#:**
```csharp
public class Counter : ComponentBase
{
    [Parameter]
    public object? Count { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Blazor render tree building...
    }
}
```

---

## Field Modifiers

| Modifier | Effect | Use Case |
|----------|--------|----------|
| `^:mutable` | Field is mutable | Stateful types, Blazor parameters |
| `^Type` | Type hint | Performance, interop |
| `^{:attr [...]}` | .NET attributes | Blazor `[Parameter]`, serialization |
| `^:volatile` | Volatile field | Thread-safe mutable state |

### Combining Modifiers

```clojure
(deftype BlazorComponent
  [^{:attr [Parameter]} ^:mutable ^String Title
   ^{:attr [Parameter]} ^:mutable ^int Count])
```

---

## Interface Implementation

### Implementing .NET Interfaces

```clojure
(deftype DisposableResource [^:mutable resource]
  IDisposable
  (Dispose [this]
    (when resource
      (.Dispose resource)
      (set! resource nil))))
```

### Implementing Multiple Interfaces

```clojure
(deftype MyCollection [^:mutable items]
  IEnumerable
  (GetEnumerator [this] (.GetEnumerator items))

  ICollection
  (Count [this] (count items))
  (Add [this item] (set! items (conj items item))))
```

### Implementing Clojure Protocols

```clojure
(defprotocol IGreet
  (greet [this]))

(deftype Greeter [name]
  IGreet
  (greet [this] (str "Hello, " name)))
```

---

## Method Definitions

### Instance Methods

```clojure
(deftype Calculator [^:mutable value]
  Object
  (ToString [this] (str "Calculator: " value))

  ;; Custom methods
  (add [this n] (set! value (+ value n)))
  (reset [this] (set! value 0)))
```

### Accessing Fields in Methods

```clojure
(deftype Point [x y]
  Object
  (ToString [this]
    (str "(" x ", " y ")"))

  ;; Access via 'this' or directly
  (distance [this]
    (Math/Sqrt (+ (* x x) (* y y)))))
```

---

## Real-World Examples

### Data Transfer Object (defrecord)

```clojure
(ns my-app.api
  (:import [System.Text.Json.Serialization JsonPropertyName]))

(defrecord CreateUserRequest
  [^{:attr [JsonPropertyName "user_name"]} ^String userName
   ^{:attr [JsonPropertyName "email_address"]} ^String email
   ^int age])

(defrecord UserResponse
  [^long id
   ^String userName
   ^String email
   ^DateTime createdAt])
```

### Blazor Component (deftype)

```clojure
(ns my-app.components
  (:import [Microsoft.AspNetCore.Components ComponentBase]
           [Microsoft.AspNetCore.Components ParameterAttribute]))

(deftype TodoItem
  [^{:attr [Parameter]} ^:mutable ^String Title
   ^{:attr [Parameter]} ^:mutable ^bool Completed
   ^{:attr [Parameter]} ^:mutable OnToggle]
  ComponentBase
  (Render [this]
    (html
      [:div {:class (if Completed "completed" "")}
       [:input {:type "checkbox"
                :checked Completed
                :on-change OnToggle}]
       [:span Title]])))
```

### Repository Pattern (deftype)

```clojure
(deftype UserRepository [^:mutable db-context]
  IUserRepository
  (GetById [this id]
    (.Find db-context id))

  (Create [this user]
    (.Add db-context user)
    (.SaveChanges db-context)
    user)

  (Delete [this id]
    (let [user (.Find db-context id)]
      (when user
        (.Remove db-context user)
        (.SaveChanges db-context)))))
```

---

## Quirks and Limitations

### 1. Type Redefinition in REPL

**Issue:** Cannot redefine types with changed field signatures.

**Symptom:** `CS0433: type exists in two versions`

**Workaround:** Restart REPL after changing defrecord/deftype fields.

```clojure
;; First definition
(defrecord User [name])

;; Changing fields requires REPL restart
(defrecord User [name age])  ; May fail with CS0433
```

### 2. Namespace Isolation

**Issue:** Types are namespace-scoped, not auto-exported.

```clojure
;; In namespace A
(ns namespace-a)
(defrecord User [name age])

;; In namespace B - won't work without require!
(ns namespace-b)
(->User "Alice" 30)  ; Error: User not accessible

;; Correct approach
(ns namespace-b
  (:require [namespace-a :as a]))
(a/->User "Alice" 30)  ; Works
```

### 3. No Positional Destructuring

**Issue:** Records don't support vector destructuring like JVM Clojure.

```clojure
;; JVM Clojure works
(let [[name age] (->Person "Alice" 30)] ...)

;; Cljr - use map destructuring instead
(let [{:keys [name age]} (->Person "Alice" 30)] ...)
```

### 4. Protocol Extension After Type Definition

**Issue:** Cannot extend protocols to existing types via `extend-type`.

**Workaround:** Implement protocol in the deftype declaration:

```clojure
;; This approach may not work
(extend-type MyType
  MyProtocol
  (my-method [this] ...))

;; Do this instead
(deftype MyType [...]
  MyProtocol
  (my-method [this] ...))
```

### 5. Mutable Field Syntax

**Issue:** `^:mutable` is required for `set!` to work.

```clojure
;; This won't compile - field is immutable
(deftype Counter [count]
  (increment [this] (set! count (inc count))))  ; Error!

;; Correct
(deftype Counter [^:mutable count]
  (increment [this] (set! count (inc count))))  ; Works
```

### 6. Attribute Syntax

**Issue:** Attributes must use the full `^{:attr [...]}` syntax.

```clojure
;; Wrong - won't apply attribute
(deftype X [^Parameter ^:mutable Count])

;; Correct
(deftype X [^{:attr [Parameter]} ^:mutable Count])
```

### 7. Generic Type Parameters

**Issue:** Generic type definitions not fully supported.

**Workaround:** Use `object?` fields or raw C# interop.

### 8. Constructor Visibility

**Issue:** All constructors are public, no private constructors.

### 9. Default Values

**Issue:** No direct support for default field values.

**Workaround:** Use factory function:

```clojure
(defrecord Config [host port timeout])

(defn default-config []
  (->Config "localhost" 8080 30000))
```

### 10. Equality Semantics

| Type | Equality |
|------|----------|
| `defrecord` | Value equality (all fields) |
| `deftype` | Reference equality (default) |

```clojure
;; Records compare by value
(= (->Point 1 2) (->Point 1 2))  ; => true

;; Types compare by reference
(= (MyType. 1) (MyType. 1))  ; => false (different instances)
```

---

## Generated Code Reference

### defrecord Emission

```clojure
(defrecord Person [^String name ^int age])
```

**Full Generated C#:**
```csharp
public record Person(string name, int age)
{
    // Auto-generated by C# record:
    // - Constructor
    // - Deconstruct method
    // - Value equality (Equals, GetHashCode)
    // - ToString
    // - With-expression support
}
```

### deftype Emission

```clojure
(deftype Counter [^:mutable ^int count]
  Object
  (ToString [this] (str "Count: " count)))
```

**Full Generated C#:**
```csharp
public class Counter
{
    public int count;

    public Counter(int count)
    {
        this.count = count;
    }

    public override string ToString()
    {
        return Core.str("Count: ", count);
    }
}
```

---

## Best Practices

### When to Use defrecord

- Data carriers / DTOs
- API request/response types
- Immutable domain entities
- Value objects

### When to Use deftype

- UI components (Blazor)
- Stateful services
- Interface implementations with mutable state
- Performance-critical code needing mutable fields

### Type Hints for Performance

```clojure
;; Slow: dynamic dispatch on field access
(defrecord Point [x y])

;; Fast: direct field access
(defrecord Point [^double x ^double y])
```

### Factory Functions

```clojure
(defrecord User [id name email created-at])

;; Provide convenient constructors
(defn create-user [name email]
  (->User (generate-id) name email (DateTime/Now)))

(defn guest-user []
  (->User 0 "Guest" nil nil))
```

---

## File References

| Component | File | Key Lines |
|-----------|------|-----------|
| defrecord Analysis | `Analyzer.cs` | 1823-1863 |
| deftype Analysis | `Analyzer.cs` | 1720-1816 |
| Record Emission | `CSharpEmitter.cs` | 3095-3178 |
| Type Emission | `CSharpEmitter.cs` | 2935-3093 |
| Type Caching (REPL) | `NreplSession.cs` | 142-196 |
