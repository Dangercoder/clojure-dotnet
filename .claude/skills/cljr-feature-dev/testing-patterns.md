# Testing Patterns

Comprehensive testing patterns for Cljr features. Every feature needs both compilation tests and NRepl evaluation tests.

## Test File Locations

| Test Type | Location |
|-----------|----------|
| NRepl evaluation tests | `tests/Cljr.Compiler.Tests/ReplNamespaceTests.cs` |
| Macro tests | `tests/Cljr.Compiler.Tests/MacroTests.cs` |
| Type hint tests | `tests/Cljr.Compiler.Tests/TypeHintsTests.cs` |
| Import tests | `tests/Cljr.Compiler.Tests/ImportTests.cs` |
| Dev mode tests | `tests/Cljr.Compiler.Tests/DevModeTests.cs` |
| Runtime tests | `tests/Cljr.Runtime.Tests/` |

## NRepl Evaluation Tests (CRITICAL)

These tests verify features work in the REPL-oriented workflow using `NreplSession.EvalAsync()`.

### Basic Evaluation Test

```csharp
[Fact]
public async Task FeatureName_WorksInRepl()
{
    var session = new NreplSession();

    var result = await session.EvalAsync("(your-feature args)");

    Assert.Null(result.Error);
    Assert.Equal(expectedValue, result.Values[0]);
}
```

### Test with Namespace Isolation

Use unique namespaces to avoid parallel test conflicts:

```csharp
[Fact]
public async Task FeatureName_InNamespace()
{
    var session = new NreplSession();

    // Use unique namespace for this test
    await session.EvalAsync("(in-ns 'feature-test-ns)");

    var result = await session.EvalAsync("(your-feature args)");

    Assert.Null(result.Error);
    Assert.Equal(expectedValue, result.Values[0]);
}
```

### State Persistence Across Evaluations

```csharp
[Fact]
public async Task FeatureName_PersistsAcrossEvaluations()
{
    var session = new NreplSession();

    await session.EvalAsync("(in-ns 'persist-test-ns)");

    // First evaluation - define something
    await session.EvalAsync("(def my-val (your-feature args))");

    // Second evaluation - use it
    var result = await session.EvalAsync("my-val");

    Assert.Null(result.Error);
    Assert.Equal(expectedValue, result.Values[0]);
}
```

### Cross-Namespace Access

```csharp
[Fact]
public async Task FeatureName_WorksAcrossNamespaces()
{
    var session = new NreplSession();

    // Define in one namespace
    await session.EvalAsync("(in-ns 'ns-a)");
    await session.EvalAsync("(def x (your-feature))");

    // Access from another namespace
    await session.EvalAsync("(in-ns 'ns-b)");
    var result = await session.EvalAsync("ns-a/x");

    Assert.Null(result.Error);
    Assert.NotNull(result.Values[0]);
}
```

### Error Handling

```csharp
[Fact]
public async Task FeatureName_ReportsErrorsCorrectly()
{
    var session = new NreplSession();

    var result = await session.EvalAsync("(your-feature invalid-args)");

    Assert.NotNull(result.Error);
    Assert.Contains("expected error message", result.Error);
}
```

### Testing Macros

```csharp
[Fact]
public async Task MyMacro_ExpandsCorrectly()
{
    var session = new NreplSession();

    // Define macro
    await session.EvalAsync(@"(defmacro my-macro [x]
                                `(+ ~x 1))");

    // Use macro
    var result = await session.EvalAsync("(my-macro 41)");

    Assert.Null(result.Error);
    Assert.Equal(42L, result.Values[0]);
}
```

### Testing defn Functions

```csharp
[Fact]
public async Task Defn_MultiArity_WorksInRepl()
{
    var session = new NreplSession();

    await session.EvalAsync("(in-ns 'defn-test)");

    await session.EvalAsync(@"(defn greet
                                ([] ""Hello"")
                                ([name] (str ""Hello, "" name)))");

    var result1 = await session.EvalAsync("(greet)");
    Assert.Equal("Hello", result1.Values![0]);

    var result2 = await session.EvalAsync("(greet \"World\")");
    Assert.Equal("Hello, World", result2.Values![0]);
}
```

### Testing defrecord

```csharp
[Fact]
public async Task Defrecord_WorksInRepl()
{
    var session = new NreplSession();

    await session.EvalAsync("(in-ns 'record-test)");

    // Define record
    await session.EvalAsync("(defrecord Person [name age])");

    // Instantiate and access fields
    await session.EvalAsync("(def p (Person. \"Alice\" 30))");

    var nameResult = await session.EvalAsync("(.-name p)");
    Assert.Equal("Alice", nameResult.Values![0]);

    var ageResult = await session.EvalAsync("(.-age p)");
    Assert.Equal(30L, ageResult.Values![0]);
}
```

### Testing Async Functions

```csharp
[Fact]
public async Task AsyncDefn_WorksInRepl()
{
    var session = new NreplSession();

    await session.EvalAsync("(in-ns 'async-test)");

    // Define async function
    await session.EvalAsync(@"(defn ^:async fetch-data []
                                (await (Task/Delay 10))
                                42)");

    // Call and await
    var result = await session.EvalAsync("(await (fetch-data))");

    Assert.Null(result.Error);
    Assert.Equal(42L, result.Values![0]);
}
```

### Testing Result History (*1, *2, *3)

```csharp
[Fact]
public async Task ResultHistory_TracksResults()
{
    var session = new NreplSession();

    await session.EvalAsync("(+ 1 2)");   // 3
    await session.EvalAsync("(+ 10 20)"); // 30
    await session.EvalAsync("(+ 100 200)"); // 300

    var star1 = await session.EvalAsync("*1");
    Assert.Equal(300L, star1.Values![0]);

    var star2 = await session.EvalAsync("*2");
    Assert.Equal(30L, star2.Values![0]);

    var star3 = await session.EvalAsync("*3");
    Assert.Equal(3L, star3.Values![0]);
}
```

## Test Collection Attribute

For tests that need exclusive access (e.g., tests that call `Var.ClearAll()`):

```csharp
[Collection("VarTests")]
public class MyTests
{
    // Tests run sequentially within this collection
}
```

## Running Tests

```bash
# Run all compiler tests
dotnet test tests/Cljr.Compiler.Tests/

# Run specific test file
dotnet test tests/Cljr.Compiler.Tests/ReplNamespaceTests.cs

# Run tests matching a filter
dotnet test tests/Cljr.Compiler.Tests/ --filter "FeatureName"

# Run with verbose output
dotnet test tests/Cljr.Compiler.Tests/ -v n
```

## EvalResult Structure

The `EvalAsync()` method returns an `EvalResult`:

```csharp
public class EvalResult
{
    public List<object?>? Values { get; }  // Return values
    public string? Error { get; }          // Error message if failed
    public string? Output { get; }         // Stdout output
}
```

## Best Practices

1. **Always use unique namespaces** - Prevents test interference when running in parallel
2. **Test error cases** - Verify errors are reported correctly
3. **Test state persistence** - Verify `def`, `defn`, `defmacro` persist across evaluations
4. **Test cross-namespace access** - Verify qualified symbols work
5. **Check both `result.Error` and `result.Values`** - A test should assert on both
6. **Use descriptive test names** - `FeatureName_Scenario_ExpectedBehavior`
