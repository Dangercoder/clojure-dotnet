# Cljr REPL Guide

This document covers the Cljr REPL implementation, including architecture, features, and known quirks.

## Overview

Cljr provides an nREPL-compatible REPL with:
- Roslyn-based runtime compilation
- Namespace isolation
- Hot reload (dev mode)
- CIDER/Calva integration

## Starting the REPL

### Standalone
```bash
cljr nrepl --port 7888
```

### With Project Context
```bash
cd my-project
cljr nrepl  # Auto-detects .csproj
```

### Dev Mode (Hot Reload)
```bash
cljr dev my-app.core --watch src
```

---

## Architecture

### Compilation Pipeline

```
Clojure Code (String)
    ↓
LispReader.ReadAll() → Clojure Data Structures
    ↓
Analyzer.Analyze() → Expression AST
    ↓
CSharpEmitter.EmitScript() → C# Code (String)
    ↓
CSharpScript.RunAsync() → Roslyn Execution
    ↓
Result Value
```

### Key Components

| Component | File | Purpose |
|-----------|------|---------|
| `NreplSession` | `NreplSession.cs` | Core session management |
| `ReplState` | `ReplState.cs` | Namespace/var tracking |
| `NreplServer` | `NreplServer.cs` | TCP server + Bencode protocol |
| `DevModeSession` | `DevModeSession.cs` | Hot reload + file watching |
| `ProjectContext` | `ProjectContext.cs` | Assembly resolution |

---

## Session State

Each REPL session maintains:

```csharp
ScriptState<object>     // Roslyn continuation state
ReplState               // Namespaces, vars, macros
Dictionary<string, Type> _typeCache  // Compiled types
string CurrentNamespace // Active namespace
```

### Roslyn Script State

Evaluation uses Roslyn's scripting API:
```csharp
// First evaluation
_state = await CSharpScript.RunAsync<object>(script, options);

// Subsequent evaluations (stateful continuation)
_state = await _state.ContinueWithAsync<object>(script, options);
```

---

## Namespace Management

### Switching Namespaces
```clojure
(in-ns 'my-app.core)
;; Creates namespace if needed, switches to it
```

### Declaring Namespaces
```clojure
(ns my-app.core
  (:require [my-app.util :as util])
  (:import [System.Net.Http HttpClient]))
```

### Namespace Isolation

**Important:** Types and vars are namespace-scoped:
- `defrecord`/`deftype` types only visible if imported
- Vars accessible via qualified names or `:refer`

```clojure
;; In namespace A
(defrecord User [name age])

;; In namespace B - won't work without require!
(User. "John" 30)  ; Error: User not accessible

;; Correct approach
(ns namespace-b
  (:require [namespace-a :as a]))
(a/->User "John" 30)
```

---

## Type Definition in REPL

### The Problem

Roslyn scripting doesn't support top-level type declarations:
```csharp
// This fails in Roslyn scripting:
public record User(string Name);  // Error!
```

### The Solution

Cljr compiles types to separate assemblies:

```
defrecord/deftype/defprotocol
    ↓
Generate C# class/interface
    ↓
Compile with CSharpCompilation (not scripting)
    ↓
Assembly.Load(bytes)
    ↓
Cache by signature
```

### Type Caching

Types are cached by signature to prevent CS0433 errors:
```csharp
// Signature: "MyApp.User:Name=String,Age=Int32"
_typeCache[signature] = (type, assembly, reference);
```

**Quirk:** If you change a type's fields, you may need to restart the REPL.

---

## Stdout Capture

Output from `println`, `print`, etc. is captured:

```csharp
var originalOut = Console.Out;
var sw = new StringWriter();
Console.SetOut(sw);
try {
    // Evaluation
} finally {
    Console.SetOut(originalOut);
}
output = sw.ToString();
```

**Limitations:**
- Only captures during evaluation
- Background threads not captured
- No stderr capture

---

## nREPL Protocol

### Supported Operations

| Operation | Description |
|-----------|-------------|
| `clone` | Create new session |
| `close` | Close session |
| `eval` | Evaluate code |
| `interrupt` | Cancel running eval |
| `completions` | Basic completion |
| `cljr/reload` | Reload namespace |
| `cljr/reload-all` | Reload all |
| `cljr/watch-start` | Start file watching |
| `cljr/watch-stop` | Stop file watching |

### CIDER/Calva Integration

Connect with standard nREPL clients:
```
;; .dir-locals.el for Emacs/CIDER
((nil . ((cider-clojure-cli-command . "cljr"))))
```

Calva connects automatically when nREPL server is running.

---

## Embedding in Applications

```csharp
using Cljr.Repl;

// Start embedded nREPL
var server = await EmbeddedNrepl.Start(
    port: 7888,
    assemblies: AppDomain.CurrentDomain.GetAssemblies(),
    sourcePaths: new[] { "src" },
    onLog: Console.WriteLine
);

// Later: stop server
server.Stop();
```

---

## Quirks and Limitations

### 1. Type Redefinition

**Issue:** Can't redefine types with changed signatures.

**Symptom:** CS0433 "type exists in two versions"

**Workaround:** Restart REPL after changing `defrecord`/`deftype` fields.

### 2. Namespace Visibility

**Issue:** Types not auto-exported like Clojure.

**Workaround:** Always use explicit require:
```clojure
(require '[other.ns :as other])
```

### 3. Source File Location

**Issue:** `require` expects files at conventional paths.

**Convention:**
```
src/my_app/core.cljr  ; for (ns my-app.core)
src/my_app/util.cljr  ; for (ns my-app.util)
```

### 4. Hot Reload Limitations

**Issues:**
- Regex-based dependency detection (may miss edge cases)
- 500ms debounce may miss rapid changes
- Closures may reference stale atom instances

### 5. Assembly Resolution

**Issue:** Types from external assemblies need project context.

**Solution:** Run REPL from project directory:
```bash
cd my-project  # Contains .csproj
cljr nrepl
```

### 6. Stdout in Background Operations

**Issue:** Background threads bypass stdout capture.

```clojure
;; Output NOT captured
(future (println "hello"))

;; Output captured
(println "hello")
```

### 7. Script State Continuation

**Issue:** Roslyn maintains state between evaluations.

**Quirk:** Variables defined earlier remain accessible:
```clojure
(def x 10)
;; Later...
x  ; Still 10, even without re-def
```

### 8. Error Messages

**Issue:** C# error messages may reference munged names.

**Example:** `my_fn` instead of `my-fn`

### 9. Macro State

**Issue:** User-defined macros stored in session, not persisted.

**Workaround:** Re-evaluate `defmacro` forms on REPL restart.

### 10. Performance

**Issue:** Each eval compiles C# code.

**Impact:** ~50-100ms per evaluation (varies by complexity).

---

## Special Variables

| Variable | Description |
|----------|-------------|
| `*ns*` | Current namespace |
| `*1` | Last result |
| `*2` | Second-to-last result |
| `*3` | Third-to-last result |
| `*e` | Last exception (if any) |

```clojure
(+ 1 2)
;; => 3
*1
;; => 3
```

---

## Debugging Tips

### Check Current Namespace
```clojure
*ns*
;; => #namespace[my-app.core]
```

### List Defined Vars
```clojure
(keys (ns-publics *ns*))
```

### Force Reload
```clojure
(require '[my-app.core :reload true])
```

### View Generated C#

Enable verbose mode to see emitted code (debug builds only).

---

## File References

| Component | File |
|-----------|------|
| Session Core | `src/Cljr.Repl/NreplSession.cs` |
| Namespace State | `src/Cljr.Repl/ReplState.cs` |
| Dev Mode | `src/Cljr.Repl/DevModeSession.cs` |
| File Watching | `src/Cljr.Repl/DevModeSession.cs` |
| State Preservation | `src/Cljr.Repl/StateRegistry.cs` |
| nREPL Server | `src/Cljr.Repl/NreplServer.cs` |
| Project Loading | `src/Cljr.Repl/ProjectContext.cs` |
| CLI Commands | `src/Cljr.Cli/Program.cs` |
