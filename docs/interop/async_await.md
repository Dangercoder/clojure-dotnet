# Async/Await Interop

Cljr provides seamless integration with .NET's async/await model. Unlike Clojure's `core.async`, Cljr compiles directly to C# `async`/`await`, giving you native performance and full interoperability with .NET async libraries.

## Table of Contents

- [Overview](#overview)
- [Making Functions Async](#making-functions-async)
- [Awaiting Tasks](#awaiting-tasks)
- [Background Execution](#background-execution)
- [Task Composition](#task-composition)
- [Channels](#channels)
- [Calling .NET Async Methods](#calling-net-async-methods)
- [Complete Examples](#complete-examples)

---

## Overview

Key differences from Clojure:

| Clojure | Cljr |
|---------|------|
| `core.async` channels | .NET `Task` and `Channel` |
| `go` blocks | `^:async` functions |
| `<!` / `>!` | `await` / `put!` |
| `future` returns a future | `future` returns a `Task<object?>` |

Cljr's async model is built on:
- **`^:async` annotation** - marks functions as async
- **`await` special form** - non-blocking await (compiles to C# `await`)
- **`await!` function** - blocking wait
- **`future`** - run code on thread pool
- **`delay`/`force`** - lazy evaluation
- **Channels** - async producer/consumer queues

---

## Making Functions Async

### The `^:async` Annotation

Add `^:async` metadata to make a function async:

```clojure
(ns myapp.api)

(defn ^:async fetch-user [id]
  (let [response (await (http-get (str "/users/" id)))]
    (parse-json response)))
```

This compiles to:

```csharp
public static async Task<object?> fetch_user(object? id)
{
    var response = await http_get($"/users/{id}");
    return parse_json(response);
}
```

### Auto-Detection via Return Type

Functions with `^Task<T>` return type hints automatically become async:

```clojure
(ns myapp.api)

;; Explicit async annotation
(defn ^:async compute-value []
  42)

;; Auto-detected from Task<T> return type
(defn ^Task<String> fetch-name []
  (await (get-name-async)))

;; Also works with bare Task
(defn ^Task do-work []
  (await (process-async)))
```

Generated signatures:

```csharp
public static async Task<object?> compute_value()
public static async Task<String> fetch_name()
public static async Task do_work()
```

---

## Awaiting Tasks

### The `await` Special Form

Use `await` inside `^:async` functions to await Tasks without blocking:

```clojure
(ns myapp.core)

(defn ^:async fetch-all-users []
  (let [users-task (fetch-users-async)
        ;; await yields control, doesn't block thread
        users (await users-task)]
    (process-users users)))
```

`await` compiles directly to C# `await`:

```csharp
var users = await fetch_users_async();
```

### The `await!` Function (Blocking)

Use `await!` when you need to block and wait for a result outside an async context:

```clojure
(ns myapp.core)

;; In a non-async context, use await! to block
(defn get-user-sync [id]
  (await! (fetch-user-async id)))

;; Equivalent to:
(defn get-user-sync [id]
  (deref (fetch-user-async id)))
```

**Warning:** `await!` blocks the current thread. Use `await` in async functions for non-blocking behavior.

### Using `deref` with Tasks

`deref` works uniformly with Tasks, atoms, delays, and other reference types:

```clojure
(ns myapp.core)

;; deref blocks until Task completes
(let [task (future (do-expensive-work))]
  (deref task))

;; With timeout (returns timeout-val if expired)
(deref task 5000 :timeout)
```

---

## Background Execution

### `future` - Thread Pool Execution

`future` runs code on a background thread and returns a `Task<object?>`:

```clojure
(ns myapp.core)

;; Run expensive computation in background
(defn compute-async []
  (let [task (future (do
                       (Thread/Sleep 1000)
                       (* 42 42)))]
    ;; Do other work while computing...
    (println "Computing in background...")
    ;; Get result (blocks if not ready)
    (deref task)))

;; Check if future is complete
(defn example []
  (let [f (future (slow-computation))]
    (if (future-done? f)
      (println "Already done!")
      (println "Still working..."))))
```

### `delay` / `force` - Lazy Evaluation

`delay` creates a lazy value that's computed once on first access:

```clojure
(ns myapp.core)

;; Create a delay - not evaluated yet
(def expensive-config
  (delay (do
           (println "Loading config...")
           (load-config-from-disk))))

;; First access triggers evaluation
(defn get-config []
  (force expensive-config))  ; or (deref expensive-config)

;; Check if already evaluated
(realized? expensive-config)  ; => true after first access
```

Delays are thread-safe - concurrent access will only compute once.

---

## Task Composition

### Waiting for Multiple Tasks

```clojure
(ns myapp.core
  (:import [Cljr Async]))

;; Wait for all tasks to complete
(defn ^:async fetch-all-data []
  (let [task1 (fetch-users)
        task2 (fetch-posts)
        task3 (fetch-comments)
        ;; AwaitAll returns a list of results
        results (await (Async/AwaitAll task1 task2 task3))]
    {:users (first results)
     :posts (second results)
     :comments (nth results 2)}))

;; Wait for first task to complete
(defn ^:async fetch-fastest []
  (let [task1 (fetch-from-server-a)
        task2 (fetch-from-server-b)
        ;; Returns result of first completed task
        result (await (Async/AwaitAny task1 task2))]
    result))
```

### Concurrent Mapping

```clojure
(ns myapp.core
  (:import [Cljr Async]))

;; Process items concurrently (unlimited parallelism)
(defn ^:async process-all [items]
  (await (Async/MapAsync
           (fn [item] (process-item-async item))
           items)))

;; With concurrency limit (max 5 concurrent tasks)
(defn ^:async process-all-limited [items]
  (await (Async/MapAsync
           (fn [item] (process-item-async item))
           items
           5)))  ; maxConcurrency
```

### Timeouts

```clojure
(ns myapp.core
  (:import [Cljr Async]))

;; Await with timeout
(defn ^:async fetch-with-timeout []
  (let [task (fetch-slow-data)
        ;; Returns :timeout if task takes > 5000ms
        result (await (Async/AwaitTimeout task 5000 :timeout))]
    (if (= result :timeout)
      (handle-timeout)
      (process-result result))))

;; Simple delay
(defn ^:async wait-a-bit []
  (await (Async/Timeout 1000))  ; Wait 1 second
  (println "Done waiting!"))
```

---

## Channels

Channels provide async producer/consumer communication, similar to Go channels or `core.async`.

### Creating Channels

```clojure
(ns myapp.core)

;; Unbuffered channel (put blocks until take)
(def ch (chan))

;; Buffered channel (can hold 10 items before blocking)
(def buffered-ch (chan 10))
```

### Channel Operations

```clojure
(ns myapp.core)

;; Put a value (async, returns true if successful)
(defn ^:async send-message [ch msg]
  (await (put! ch msg)))

;; Take a value (async, returns nil if channel closed)
(defn ^:async receive-message [ch]
  (await (take! ch)))

;; Close a channel
(close! ch)

;; Check if closed
(.IsClosed ch)
```

### Producer/Consumer Pattern

```clojure
(ns myapp.core)

(defn ^:async producer [ch items]
  (doseq [item items]
    (await (put! ch item)))
  (close! ch))

(defn ^:async consumer [ch]
  (loop [results []]
    (let [item (await (take! ch))]
      (if (nil? item)
        results  ; Channel closed
        (recur (conj results (process item)))))))

;; Usage
(defn ^:async run-pipeline [data]
  (let [ch (chan 10)]
    ;; Start producer (don't await - let it run)
    (producer ch data)
    ;; Consume all results
    (await (consumer ch))))
```

### Non-Blocking Operations

```clojure
(ns myapp.core)

;; Try to put without blocking
(.TryPut ch value)  ; => true/false

;; Try to take without blocking
(let [[success value] (.TryTake ch)]
  (when success
    (process value)))
```

---

## Calling .NET Async Methods

### Instance Async Methods

```clojure
(ns myapp.web
  (:import [Microsoft.AspNetCore.Builder WebApplication]))

(defn ^:async start-server [app]
  ;; Call .NET async method and await it
  (await (.StartAsync app))
  (println "Server started!"))

(defn ^:async stop-server [app]
  (await (.StopAsync app)))
```

### Static Async Methods

```clojure
(ns myapp.http
  (:import [System.Net.Http HttpClient]))

(defn ^:async fetch-url [url]
  (let [client (HttpClient.)]
    ;; Call static async method
    (await (.GetStringAsync client url))))
```

### Working with Task<T>

```clojure
(ns myapp.core)

;; .NET methods return Task<T>, await extracts T
(defn ^:async read-file [path]
  (let [content (await (System.IO.File/ReadAllTextAsync path))]
    (parse-content content)))

;; Chain async operations
(defn ^:async process-file [path]
  (let [content (await (read-file path))
        processed (transform content)]
    (await (save-file processed))))
```

---

## Complete Examples

### Example 1: HTTP API Client

```clojure
(ns myapp.api
  (:import [System.Net.Http HttpClient]
           [System.Text.Json JsonSerializer]
           [Cljr Async]))

(def ^:private client (HttpClient.))

(defn ^:async http-get [url]
  (await (.GetStringAsync client url)))

(defn ^:async http-post [url body]
  (let [content (StringContent. (JsonSerializer/Serialize body))]
    (await (.PostAsync client url content))))

(defn ^:async fetch-user [id]
  (let [response (await (http-get (str "https://api.example.com/users/" id)))]
    (JsonSerializer/Deserialize response)))

(defn ^:async fetch-all-users [ids]
  ;; Fetch all users concurrently, max 5 at a time
  (await (Async/MapAsync fetch-user ids 5)))

;; Usage
(defn ^:async main []
  (let [users (await (fetch-all-users [1 2 3 4 5]))]
    (doseq [user users]
      (println (:name user)))))
```

### Example 2: Producer/Consumer with Channels

```clojure
(ns myapp.pipeline)

(defn ^:async producer [ch data]
  (println "Producer: Starting...")
  (doseq [item data]
    (println (str "Producer: Sending " item))
    (await (put! ch item))
    (await (Async/Timeout 100)))  ; Simulate work
  (println "Producer: Done, closing channel")
  (close! ch))

(defn ^:async consumer [ch name]
  (println (str name ": Starting..."))
  (loop []
    (let [item (await (take! ch))]
      (if (nil? item)
        (println (str name ": Channel closed, exiting"))
        (do
          (println (str name ": Received " item))
          (await (Async/Timeout 200))  ; Simulate processing
          (recur))))))

(defn ^:async run-pipeline []
  (let [ch (chan 5)
        data (range 10)]
    ;; Start producer
    (producer ch data)
    ;; Start multiple consumers
    (let [c1 (consumer ch "Consumer-1")
          c2 (consumer ch "Consumer-2")]
      ;; Wait for both consumers to finish
      (await (Async/AwaitAll c1 c2)))
    (println "Pipeline complete!")))
```

### Example 3: Web Server with Async Handlers

```clojure
(ns myapp.server
  (:import [Microsoft.AspNetCore.Builder WebApplication EndpointRouteBuilderExtensions]))

(defn create-app []
  (let [builder (WebApplication/CreateBuilder (csharp* "new string[0]"))
        app (.Build builder)]

    ;; Sync handler
    (EndpointRouteBuilderExtensions/MapGet
      app "/"
      (fn [] "Hello World!"))

    ;; Async handler (use ^:async on the fn)
    (EndpointRouteBuilderExtensions/MapGet
      app "/users/{id}"
      (fn ^:async [^int id]
        (let [user (await (fetch-user-from-db id))]
          (JsonSerializer/Serialize user))))

    app))

(defn ^:async start! []
  (let [app (create-app)]
    (await (.StartAsync app))
    (println "Server running at http://localhost:5000")
    app))

(defn ^:async stop! [app]
  (await (.StopAsync app))
  (println "Server stopped"))
```

### Example 4: Retry with Exponential Backoff

```clojure
(ns myapp.retry
  (:import [Cljr Async]))

(defn ^:async retry-with-backoff [f max-retries]
  (loop [attempt 0]
    (let [result (try
                   {:success true :value (await (f))}
                   (catch Exception e
                     {:success false :error e}))]
      (if (:success result)
        (:value result)
        (if (< attempt max-retries)
          (do
            (println (str "Attempt " (inc attempt) " failed, retrying..."))
            ;; Exponential backoff: 100ms, 200ms, 400ms, 800ms...
            (await (Async/Timeout (* 100 (bit-shift-left 1 attempt))))
            (recur (inc attempt)))
          (throw (:error result)))))))

;; Usage
(defn ^:async fetch-with-retry [url]
  (retry-with-backoff
    (fn [] (http-get url))
    3))
```

---

## Summary

| Function | Purpose | Blocking? |
|----------|---------|-----------|
| `^:async` | Mark function as async | - |
| `await` | Await Task in async context | No |
| `await!` | Block and wait for Task | Yes |
| `deref` | Dereference any ref type | Yes (for Tasks) |
| `future` | Run on thread pool | No (returns Task) |
| `delay` | Lazy evaluation | - |
| `force` | Force delay evaluation | Depends |
| `chan` | Create channel | No |
| `put!` | Put to channel | No (async) |
| `take!` | Take from channel | No (async) |
| `close!` | Close channel | No |
