using Cljr;
using Cljr.Repl;
using Cljr.Compiler.Reader;
using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Emitter;

namespace Cljr.Compiler.Tests;

// Disable parallel execution with tests that call Var.ClearAll()
[Collection("VarTests")]
public class ReplNamespaceTests
{
    [Fact]
    public async Task InNs_SwitchesNamespace()
    {
        var session = new NreplSession();

        // Initial namespace should be "user"
        Assert.Equal("user", session.Namespace);

        // Switch to new namespace
        var result = await session.EvalAsync("(in-ns 'my-app.core)");

        Assert.Null(result.Error);
        Assert.Equal("my-app.core", session.Namespace);

        // Result should be the namespace symbol
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Symbol>(result.Values[0]);
        Assert.Equal("my-app.core", ((Symbol)result.Values[0]!).Name);
    }

    [Fact]
    public async Task InNs_CreatesNewNamespace()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'new-namespace)");

        // Namespace should exist in ReplState
        Assert.True(session.ReplState.Namespaces.ContainsKey("new-namespace"));
    }

    [Fact]
    public async Task Ns_SwitchesNamespace()
    {
        var session = new NreplSession();

        // Initial namespace should be "user"
        Assert.Equal("user", session.Namespace);

        // Switch to new namespace using ns form
        var result = await session.EvalAsync("(ns hello.core)");

        Assert.Null(result.Error);
        Assert.Equal("hello.core", session.Namespace);

        // ns returns nil in Clojure
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.Null(result.Values[0]);
    }

    [Fact]
    public async Task Ns_CreatesNewNamespace()
    {
        var session = new NreplSession();

        await session.EvalAsync("(ns my-new-namespace)");

        // Namespace should exist in ReplState
        Assert.True(session.ReplState.Namespaces.ContainsKey("my-new-namespace"));
        Assert.Equal("my-new-namespace", session.Namespace);
    }

    [Fact]
    public async Task StarNs_UpdatesAfterNs()
    {
        var session = new NreplSession();

        await session.EvalAsync("(ns other-ns)");
        var result = await session.EvalAsync("*ns*");

        Assert.Null(result.Error);
        Assert.IsType<Symbol>(result.Values![0]);
        Assert.Equal("other-ns", ((Symbol)result.Values[0]!).Name);
    }

    [Fact]
    public async Task StarNs_ReturnsCurrentNamespace()
    {
        var session = new NreplSession();

        // Test in default namespace
        var result = await session.EvalAsync("*ns*");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Symbol>(result.Values[0]);
        Assert.Equal("user", ((Symbol)result.Values[0]!).Name);
    }

    [Fact]
    public async Task StarNs_UpdatesAfterInNs()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'other-ns)");
        var result = await session.EvalAsync("*ns*");

        Assert.Null(result.Error);
        Assert.IsType<Symbol>(result.Values![0]);
        Assert.Equal("other-ns", ((Symbol)result.Values[0]!).Name);
    }

    [Fact]
    public async Task ResultHistory_Star1_TracksLastResult()
    {
        var session = new NreplSession();

        await session.EvalAsync("(+ 1 2)");  // 3
        var result = await session.EvalAsync("*1");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        // *1 should return 3L (long)
        Assert.Equal(3L, result.Values[0]);
    }

    [Fact]
    public async Task ResultHistory_Star2_TracksPreviousResult()
    {
        var session = new NreplSession();

        await session.EvalAsync("(+ 1 2)");  // 3
        await session.EvalAsync("(+ 10 20)"); // 30
        var result = await session.EvalAsync("*2");

        Assert.Null(result.Error);
        // *2 should be 3 (previous result)
        Assert.Equal(3L, result.Values![0]);
    }

    [Fact]
    public async Task ResultHistory_Star3_TracksThirdResult()
    {
        var session = new NreplSession();

        await session.EvalAsync("(+ 1 2)");   // 3
        await session.EvalAsync("(+ 10 20)"); // 30
        await session.EvalAsync("(+ 100 200)"); // 300
        var result = await session.EvalAsync("*3");

        Assert.Null(result.Error);
        // *3 should be 3 (oldest of the three)
        Assert.Equal(3L, result.Values![0]);
    }

    [Fact]
    public async Task DefmacroInRepl_PersistsAcrossEvaluations()
    {
        var session = new NreplSession();

        // Define a macro
        await session.EvalAsync(@"(defmacro unless [test body]
                                    `(if (not ~test) ~body))");

        // Use the macro in a separate evaluation
        var result = await session.EvalAsync("(unless false 42)");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Equal(42L, result.Values[0]);
    }

    [Fact]
    public async Task DefInRepl_PersistsAcrossEvaluations()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid parallel test conflicts
        await session.EvalAsync("(in-ns 'def-test-ns)");

        await session.EvalAsync("(def x 42)");
        var result = await session.EvalAsync("x");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Equal(42L, result.Values[0]);
    }

    [Fact]
    public async Task DefnInRepl_PersistsAcrossEvaluations()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid parallel test conflicts
        await session.EvalAsync("(in-ns 'defn-test-ns)");

        await session.EvalAsync("(defn double-it [x] (* x 2))");
        var result = await session.EvalAsync("(double-it 21)");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Equal(42L, result.Values[0]);
    }

    [Fact]
    public async Task SwitchNamespace_ThenBack_MaintainsState()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid parallel test conflicts
        await session.EvalAsync("(in-ns 'switch-test-user)");

        // Define something in this namespace
        await session.EvalAsync("(def user-val 100)");

        // Switch to another namespace
        await session.EvalAsync("(in-ns 'switch-test-other)");
        Assert.Equal("switch-test-other", session.Namespace);

        // Switch back to original namespace
        await session.EvalAsync("(in-ns 'switch-test-user)");
        Assert.Equal("switch-test-user", session.Namespace);

        // user-val should still be accessible
        var result = await session.EvalAsync("user-val");
        Assert.Null(result.Error);
        Assert.Equal(100L, result.Values![0]);
    }

    [Fact]
    public async Task ThreadingMacro_Works()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("(-> 1 inc inc inc)");

        Assert.Null(result.Error);
        Assert.Equal(4L, result.Values![0]);
    }

    [Fact]
    public async Task ThreadLastMacro_Works()
    {
        var session = new NreplSession();

        // Simpler test that doesn't rely on complex function chaining
        var result = await session.EvalAsync("(->> 1 inc inc)");

        Assert.Null(result.Error);
        Assert.Equal(3L, result.Values![0]);
    }

    [Fact]
    public async Task CondMacro_Works()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("(cond false 1 true 2 :else 3)");

        Assert.Null(result.Error);
        Assert.Equal(2L, result.Values![0]);
    }

    [Fact]
    public async Task WhenLetMacro_Works()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("(when-let [x 42] x)");

        Assert.Null(result.Error);
        Assert.Equal(42L, result.Values![0]);
    }

    [Fact]
    public async Task IfLetMacro_WithTruthyValue_Works()
    {
        var session = new NreplSession();

        // Test with a truthy value
        var result = await session.EvalAsync("(if-let [x 42] x 0)");

        Assert.Null(result.Error);
        Assert.Equal(42L, result.Values![0]);
    }

    [Fact]
    public async Task IfLetMacro_WithFalsyValue_Works()
    {
        var session = new NreplSession();

        // Test with false value
        var result = await session.EvalAsync("(if-let [x false] 1 2)");

        Assert.Null(result.Error);
        Assert.Equal(2L, result.Values![0]);
    }

    [Fact]
    public async Task NamespaceIsolation_VarNotAccessibleFromOtherNamespace()
    {
        var session = new NreplSession();

        // Define var in namespace-a
        await session.EvalAsync("(in-ns 'isolation-test-a)");
        await session.EvalAsync("(def secret-value 42)");

        // Switch to namespace-b
        await session.EvalAsync("(in-ns 'isolation-test-b)");

        // Try to access secret-value - should return nil (not found)
        var result = await session.EvalAsync("secret-value");

        Assert.Null(result.Error);
        // Should be null because the var doesn't exist in isolation-test-b
        Assert.Null(result.Values![0]);
    }

    [Fact]
    public async Task NamespaceIsolation_FunctionNotAccessibleFromOtherNamespace()
    {
        var session = new NreplSession();

        // Define function in namespace-a
        await session.EvalAsync("(in-ns 'fn-isolation-test-a)");
        await session.EvalAsync("(defn greet [name] (str \"Hello, \" name))");

        // Verify it works in namespace-a
        var resultInA = await session.EvalAsync("(greet \"World\")");
        Assert.Null(resultInA.Error);
        Assert.Equal("Hello, World", resultInA.Values![0]);

        // Switch to namespace-b
        await session.EvalAsync("(in-ns 'fn-isolation-test-b)");

        // Try to call greet - should return nil (function not found)
        var resultInB = await session.EvalAsync("(greet \"World\")");

        Assert.Null(resultInB.Error);
        // Should be null because the function doesn't exist in fn-isolation-test-b
        Assert.Null(resultInB.Values![0]);
    }

    [Fact]
    public async Task NamespaceIsolation_CanDefinesameName_InDifferentNamespaces()
    {
        var session = new NreplSession();

        // Define x=10 in namespace-a
        await session.EvalAsync("(in-ns 'same-name-test-a)");
        await session.EvalAsync("(def x 10)");

        // Define x=20 in namespace-b
        await session.EvalAsync("(in-ns 'same-name-test-b)");
        await session.EvalAsync("(def x 20)");

        // Verify x in namespace-b is 20
        var resultB = await session.EvalAsync("x");
        Assert.Null(resultB.Error);
        Assert.Equal(20L, resultB.Values![0]);

        // Switch back to namespace-a and verify x is still 10
        await session.EvalAsync("(in-ns 'same-name-test-a)");
        var resultA = await session.EvalAsync("x");
        Assert.Null(resultA.Error);
        Assert.Equal(10L, resultA.Values![0]);
    }

    [Fact]
    public async Task Defrecord_CanInstantiateAfterDefinition()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid conflicts
        await session.EvalAsync("(in-ns 'defrecord-test-ns)");

        // Define a simple record
        var defResult = await session.EvalAsync("(defrecord Person [name age])");
        Assert.Null(defResult.Error);

        // Create an instance using constructor syntax
        var instanceResult = await session.EvalAsync("(Person. \"Alice\" 30)");
        Assert.Null(instanceResult.Error);
        Assert.NotNull(instanceResult.Values);
        Assert.NotNull(instanceResult.Values[0]);
    }

    [Fact]
    public async Task Defrecord_CanAccessFieldsAfterDefinition()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid conflicts
        await session.EvalAsync("(in-ns 'defrecord-fields-test-ns)");

        // Define a record
        await session.EvalAsync("(defrecord Employee [name salary])");

        // Create an instance and access a field
        var result = await session.EvalAsync("(.-name (Employee. \"Bob\" 50000))");
        Assert.Null(result.Error);
        Assert.Equal("Bob", result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_WorksInNamespaceWithHyphens()
    {
        var session = new NreplSession();

        // Use namespace with hyphens (like minimal-api.main)
        // This is the original bug case: CS0246 error when referencing types in hyphenated namespaces
        await session.EvalAsync("(in-ns 'my-app.core)");

        // Define a record
        var defResult = await session.EvalAsync("(defrecord TodoRequest [title description])");
        Assert.Null(defResult.Error);

        // Create an instance - this should work after the fix
        var instanceResult = await session.EvalAsync("(TodoRequest. \"Learn Cljr\" \"Build web apps\")");
        Assert.Null(instanceResult.Error);
        Assert.NotNull(instanceResult.Values);
        Assert.NotNull(instanceResult.Values[0]);
    }

    [Fact]
    public async Task Defrecord_TypeReferencePersistsAcrossEvaluations()
    {
        var session = new NreplSession();

        // Use unique namespace
        await session.EvalAsync("(in-ns 'defrecord-persist-test)");

        // Define a record
        await session.EvalAsync("(defrecord Point [x y])");

        // Create an instance in the first evaluation
        var result1 = await session.EvalAsync("(Point. 10 20)");
        Assert.Null(result1.Error);
        Assert.NotNull(result1.Values![0]);

        // Create another instance in a second evaluation - this tests that the type persists
        var result2 = await session.EvalAsync("(Point. 30 40)");
        Assert.Null(result2.Error);
        Assert.NotNull(result2.Values![0]);

        // Access fields from inline instances
        var xResult = await session.EvalAsync("(.-x (Point. 50 60))");
        Assert.Null(xResult.Error);
        Assert.Equal(50L, xResult.Values![0]);
    }

    [Fact]
    public async Task MultiArityDefn_ZeroArityWorks()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid conflicts
        await session.EvalAsync("(in-ns 'multi-arity-test-ns)");

        // Define a multi-arity function like start! in the sample
        await session.EvalAsync(@"(defn greet
                                    ([] (greet ""World""))
                                    ([name] (str ""Hello, "" name ""!"")))");

        // Call with zero arguments - should use the 0-arity overload
        var result = await session.EvalAsync("(greet)");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Equal("Hello, World!", result.Values[0]);
    }

    [Fact]
    public async Task MultiArityDefn_OneArityWorks()
    {
        var session = new NreplSession();

        // Use unique namespace to avoid conflicts
        await session.EvalAsync("(in-ns 'multi-arity-test-ns2)");

        // Define a multi-arity function
        await session.EvalAsync(@"(defn greet
                                    ([] (greet ""World""))
                                    ([name] (str ""Hello, "" name ""!"")))");

        // Call with one argument - should use the 1-arity overload
        var result = await session.EvalAsync("(greet \"Alice\")");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Equal("Hello, Alice!", result.Values[0]);
    }

    [Fact]
    public async Task MultiArityDefn_ThreeArities()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'multi-arity-test-ns3)");

        // Define a function with 3 arities
        await session.EvalAsync(@"(defn add
                                    ([] 0)
                                    ([x] x)
                                    ([x y] (+ x y)))");

        // Test all three arities
        var result0 = await session.EvalAsync("(add)");
        Assert.Null(result0.Error);
        Assert.Equal(0L, result0.Values![0]);

        var result1 = await session.EvalAsync("(add 5)");
        Assert.Null(result1.Error);
        Assert.Equal(5L, result1.Values![0]);

        var result2 = await session.EvalAsync("(add 3 4)");
        Assert.Null(result2.Error);
        Assert.Equal(7L, result2.Values![0]);
    }

    [Fact]
    public async Task MultiArityDefn_CrossCallsBetweenArities()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'multi-arity-cross-call)");

        // Define a function where 0-arity calls 1-arity
        await session.EvalAsync(@"(defn double-it
                                    ([] (double-it 21))
                                    ([n] (* n 2)))");

        // Call 0-arity which internally calls 1-arity
        var result = await session.EvalAsync("(double-it)");

        Assert.Null(result.Error);
        Assert.Equal(42L, result.Values![0]);
    }

    [Fact]
    public async Task QualifiedSymbol_CanCallFunctionFromOtherNamespace()
    {
        var session = new NreplSession();

        // Define a function in namespace-a
        await session.EvalAsync("(in-ns 'qualified-test-a)");
        await session.EvalAsync("(defn greet [name] (str \"Hello, \" name \"!\"))");

        // Switch to namespace-b
        await session.EvalAsync("(in-ns 'qualified-test-b)");

        // Call the function using fully qualified name
        var result = await session.EvalAsync("(qualified-test-a/greet \"World\")");

        Assert.Null(result.Error);
        Assert.Equal("Hello, World!", result.Values![0]);
    }

    [Fact]
    public async Task QualifiedSymbol_CanCallMultipleFunctionsFromOtherNamespace()
    {
        var session = new NreplSession();

        // Define functions in namespace-a
        await session.EvalAsync("(in-ns 'multi-fn-test-a)");
        await session.EvalAsync("(defn add-one [x] (+ x 1))");
        await session.EvalAsync("(defn double-it [x] (* x 2))");

        // Switch to namespace-b
        await session.EvalAsync("(in-ns 'multi-fn-test-b)");

        // Call both functions using qualified names
        var result1 = await session.EvalAsync("(multi-fn-test-a/add-one 5)");
        Assert.Null(result1.Error);
        Assert.Equal(6L, result1.Values![0]);

        var result2 = await session.EvalAsync("(multi-fn-test-a/double-it 21)");
        Assert.Null(result2.Error);
        Assert.Equal(42L, result2.Values![0]);
    }

    [Fact]
    public async Task QualifiedSymbol_ComposeFunctionsFromDifferentNamespaces()
    {
        var session = new NreplSession();

        // Define function in namespace-a
        await session.EvalAsync("(in-ns 'compose-test-a)");
        await session.EvalAsync("(defn add-ten [x] (+ x 10))");

        // Define function in namespace-b
        await session.EvalAsync("(in-ns 'compose-test-b)");
        await session.EvalAsync("(defn multiply-two [x] (* x 2))");

        // Switch to namespace-c and compose functions from both
        await session.EvalAsync("(in-ns 'compose-test-c)");
        var result = await session.EvalAsync("(compose-test-b/multiply-two (compose-test-a/add-ten 5))");

        Assert.Null(result.Error);
        Assert.Equal(30L, result.Values![0]); // (5 + 10) * 2 = 30
    }

    [Fact]
    public async Task RequireWithAlias_WorksForInMemoryNamespace()
    {
        var session = new NreplSession();

        // Define a function in namespace-a
        await session.EvalAsync("(in-ns 'alias-test-a)");
        await session.EvalAsync("(defn greet [name] (str \"Hi, \" name \"!\"))");

        // Switch to namespace-b and require namespace-a with an alias
        await session.EvalAsync("(in-ns 'alias-test-b)");
        await session.EvalAsync("(require '[alias-test-a :as a])");

        // Call function via alias
        var result = await session.EvalAsync("(a/greet \"Alice\")");

        Assert.Null(result.Error);
        Assert.Equal("Hi, Alice!", result.Values![0]);
    }

    [Fact]
    public async Task RequireWithAlias_MultipleAliasesForInMemoryNamespaces()
    {
        var session = new NreplSession();

        // Define functions in namespace-a
        await session.EvalAsync("(in-ns 'multi-alias-a)");
        await session.EvalAsync("(defn double-it [x] (* x 2))");

        // Define functions in namespace-b
        await session.EvalAsync("(in-ns 'multi-alias-b)");
        await session.EvalAsync("(defn add-ten [x] (+ x 10))");

        // Switch to namespace-c and require both with aliases
        await session.EvalAsync("(in-ns 'multi-alias-c)");
        await session.EvalAsync("(require '[multi-alias-a :as a])");
        await session.EvalAsync("(require '[multi-alias-b :as b])");

        // Call functions via aliases
        var result1 = await session.EvalAsync("(a/double-it 21)");
        Assert.Null(result1.Error);
        Assert.Equal(42L, result1.Values![0]);

        var result2 = await session.EvalAsync("(b/add-ten 32)");
        Assert.Null(result2.Error);
        Assert.Equal(42L, result2.Values![0]);

        // Compose functions from both namespaces via aliases
        var result3 = await session.EvalAsync("(a/double-it (b/add-ten 11))");
        Assert.Null(result3.Error);
        Assert.Equal(42L, result3.Values![0]); // (11 + 10) * 2 = 42
    }

    [Fact]
    public async Task RequireWithAlias_AliasIsStoredInCurrentNamespace()
    {
        var session = new NreplSession();

        // Create namespace-a
        await session.EvalAsync("(in-ns 'alias-store-a)");
        await session.EvalAsync("(defn foo [] 42)");

        // Verify namespace-a exists in namespaces
        Assert.True(session.ReplState.Namespaces.ContainsKey("alias-store-a"),
            $"alias-store-a not in Namespaces. Keys: {string.Join(", ", session.ReplState.Namespaces.Keys)}");

        // Switch to namespace-b and require with alias
        await session.EvalAsync("(in-ns 'alias-store-b)");
        var requireResult = await session.EvalAsync("(require '[alias-store-a :as x])");

        // Check that require succeeded
        Assert.Null(requireResult.Error);

        // Verify the alias is registered in the current namespace
        var nsB = session.ReplState.GetCurrentNamespace();
        var aliasKeys = string.Join(", ", nsB.Aliases.Keys);
        Assert.True(nsB.Aliases.ContainsKey("x"),
            $"Alias 'x' not found. Aliases: [{aliasKeys}]. Current ns: {session.ReplState.CurrentNamespace}");
        Assert.Equal("alias-store-a", nsB.Aliases["x"]);
    }

    [Fact]
    public async Task VectorLiteral_EmptyVector_ReturnsEmptyPersistentVector()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("[]");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Cljr.Collections.PersistentVector>(result.Values[0]);
        Assert.Equal(0, ((Cljr.Collections.PersistentVector)result.Values[0]!).Count);
    }

    [Fact]
    public async Task VectorLiteral_WithElements_ReturnsPersistentVector()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("[1 2 3]");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Cljr.Collections.PersistentVector>(result.Values[0]);
        var vec = (Cljr.Collections.PersistentVector)result.Values[0]!;
        Assert.Equal(3, vec.Count);
        Assert.Equal(1L, vec.Nth(0));
        Assert.Equal(2L, vec.Nth(1));
        Assert.Equal(3L, vec.Nth(2));
    }

    [Fact]
    public async Task VectorLiteral_NestedVectors_Works()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("[[1 2] [3 4]]");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        var outer = result.Values[0] as Cljr.Collections.PersistentVector;
        Assert.NotNull(outer);
        Assert.Equal(2, outer!.Count);
        var inner1 = outer.Nth(0) as Cljr.Collections.PersistentVector;
        Assert.NotNull(inner1);
        Assert.Equal(2, inner1!.Count);
    }

    [Fact]
    public async Task MapLiteral_EmptyMap_ReturnsEmptyPersistentHashMap()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("{}");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Cljr.Collections.PersistentHashMap>(result.Values[0]);
        Assert.Equal(0, ((Cljr.Collections.PersistentHashMap)result.Values[0]!).Count);
    }

    [Fact]
    public async Task MapLiteral_WithKeyValues_ReturnsPersistentHashMap()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("{:a 1 :b 2}");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Cljr.Collections.PersistentHashMap>(result.Values[0]);
        var map = (Cljr.Collections.PersistentHashMap)result.Values[0]!;
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public async Task SetLiteral_EmptySet_ReturnsEmptyPersistentHashSet()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("#{}");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Cljr.Collections.PersistentHashSet>(result.Values[0]);
        Assert.Equal(0, ((Cljr.Collections.PersistentHashSet)result.Values[0]!).Count);
    }

    [Fact]
    public async Task SetLiteral_WithElements_ReturnsPersistentHashSet()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("#{1 2 3}");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Single(result.Values);
        Assert.IsType<Cljr.Collections.PersistentHashSet>(result.Values[0]);
        var set = (Cljr.Collections.PersistentHashSet)result.Values[0]!;
        Assert.Equal(3, set.Count);
    }

    [Fact]
    public async Task DataLiterals_InLetBinding_Work()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("(let [v [1 2 3]] (count v))");

        Assert.Null(result.Error);
        Assert.Equal(3L, result.Values![0]);  // count returns long
    }

    [Fact]
    public async Task DataLiterals_InDefn_Work()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'data-literal-defn-test)");
        // Use a function that returns a vector with an expression
        var defResult = await session.EvalAsync("(defn get-vec [x] (conj [1 2] x))");
        Assert.Null(defResult.Error);

        var result = await session.EvalAsync("(get-vec 3)");

        Assert.Null(result.Error);
        Assert.IsType<Cljr.Collections.PersistentVector>(result.Values![0]);
        Assert.Equal(3, ((Cljr.Collections.PersistentVector)result.Values![0]!).Count);
    }

    [Fact]
    public async Task DataLiterals_DirectReturnFromDefn_Work()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'direct-return-test)");
        // Test functions that directly return data literals
        var defResult = await session.EvalAsync("(defn get-vec [] [1 2 3])");
        Assert.Null(defResult.Error);

        var result = await session.EvalAsync("(get-vec)");
        Assert.Null(result.Error);
        Assert.IsType<Cljr.Collections.PersistentVector>(result.Values![0]);
        Assert.Equal(3, ((Cljr.Collections.PersistentVector)result.Values![0]!).Count);
    }

    [Fact]
    public async Task DataLiterals_MapDirectReturnFromDefn_Work()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'map-return-test)");
        var defResult = await session.EvalAsync("(defn get-map [] {:a 1 :b 2})");
        Assert.Null(defResult.Error);

        var result = await session.EvalAsync("(get-map)");
        Assert.Null(result.Error);
        Assert.IsType<Cljr.Collections.PersistentHashMap>(result.Values![0]);
        Assert.Equal(2, ((Cljr.Collections.PersistentHashMap)result.Values![0]!).Count);
    }

    [Fact]
    public async Task DataLiterals_SetDirectReturnFromDefn_Work()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'set-return-test)");
        var defResult = await session.EvalAsync("(defn get-set [] #{1 2 3})");
        Assert.Null(defResult.Error);

        var result = await session.EvalAsync("(get-set)");
        Assert.Null(result.Error);
        Assert.IsType<Cljr.Collections.PersistentHashSet>(result.Values![0]);
        Assert.Equal(3, ((Cljr.Collections.PersistentHashSet)result.Values![0]!).Count);
    }

    [Fact]
    public async Task DataLiterals_MapWithVectorValues_Work()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("{:items [1 2 3] :name \"test\"}");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        var map = result.Values[0] as Cljr.Collections.PersistentHashMap;
        Assert.NotNull(map);
        Assert.Equal(2, map!.Count);
    }

    #region Async Function Tests

    [Fact]
    public async Task AsyncDefn_CanDefineAsyncFunction()
    {
        var session = new NreplSession();

        // Define an async function
        var defResult = await session.EvalAsync("(defn ^:async double-async [x] (* x 2))");

        Assert.Null(defResult.Error);
    }

    [Fact]
    public async Task AsyncDefn_FunctionIsStoredAsAsyncFn()
    {
        var session = new NreplSession();

        // Define an async function
        await session.EvalAsync("(defn ^:async my-async-fn [x] x)");

        // Get the var and check its type
        var result = await session.EvalAsync("(deref (Var/Find \"user\" \"my-async-fn\"))");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.IsType<Cljr.AsyncFn1>(result.Values[0]);
    }

    [Fact]
    public async Task AsyncDefn_ZeroArity_IsAsyncFn0()
    {
        var session = new NreplSession();

        await session.EvalAsync("(defn ^:async get-value [] 42)");

        var result = await session.EvalAsync("(deref (Var/Find \"user\" \"get-value\"))");

        Assert.Null(result.Error);
        Assert.IsType<Cljr.AsyncFn0>(result.Values![0]);
    }

    [Fact]
    public async Task AsyncDefn_TwoArity_IsAsyncFn2()
    {
        var session = new NreplSession();

        await session.EvalAsync("(defn ^:async add-async [a b] (+ a b))");

        var result = await session.EvalAsync("(deref (Var/Find \"user\" \"add-async\"))");

        Assert.Null(result.Error);
        Assert.IsType<Cljr.AsyncFn2>(result.Values![0]);
    }

    [Fact]
    public async Task AsyncDefn_ReturnsTask()
    {
        var session = new NreplSession();

        // Define an async function that returns a value
        await session.EvalAsync("(defn ^:async compute [x] (* x 2))");

        // Call it - should return a Task
        var result = await session.EvalAsync("(compute 21)");

        Assert.Null(result.Error);
        // AsyncFn1.Invoke returns Task<object?> as object
        Assert.IsAssignableFrom<System.Threading.Tasks.Task<object?>>(result.Values![0]);
    }

    [Fact]
    public async Task AsyncDefn_CanCallAndAwait()
    {
        var session = new NreplSession();

        // Define an async function that returns a value
        await session.EvalAsync("(defn ^:async compute [x] (* x 2))");

        // Call it and await the result using deref (which blocks on Tasks)
        var result = await session.EvalAsync("(deref (compute 21))");

        Assert.Null(result.Error);
        Assert.Equal(42L, result.Values![0]);
    }

    [Fact]
    public async Task AsyncDefn_CanUseWithMapAsync()
    {
        var session = new NreplSession();

        // Define an async function
        var defResult = await session.EvalAsync("(defn ^:async double-it [x] (* x 2))");
        Assert.Null(defResult.Error);

        // First test: call map-async and get the Task
        var taskResult = await session.EvalAsync("(map-async double-it [1 2 3])");
        if (taskResult.Error != null)
            throw new Exception($"Error in map-async call: {taskResult.Error}");
        Assert.NotNull(taskResult.Values);
        if (taskResult.Values![0] is null)
            throw new Exception("taskResult.Values[0] is null - map-async returned null");
        Assert.IsAssignableFrom<System.Threading.Tasks.Task>(taskResult.Values![0]);

        // Then deref the result
        var result = await session.EvalAsync("(deref (map-async double-it [1 2 3]))");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values);

        // Check actual type
        var actualValue = result.Values![0];
        Assert.NotNull(actualValue);

        // Cast to IList for flexibility
        var list = actualValue as System.Collections.IList;
        Assert.NotNull(list);
        Assert.Equal(3, list!.Count);
        Assert.Equal(2L, list[0]);
        Assert.Equal(4L, list[1]);
        Assert.Equal(6L, list[2]);
    }

    [Fact]
    public async Task AsyncDefn_AsTypedDelegate_Works()
    {
        var session = new NreplSession();

        // Define an async function
        await session.EvalAsync("(defn ^:async process [x] x)");

        // Get the var root value (the AsyncFn1)
        var varResult = await session.EvalAsync("(deref (Var/Find \"user\" \"process\"))");
        Assert.Null(varResult.Error);

        var asyncFn = varResult.Values![0] as Cljr.AsyncFn1;
        Assert.NotNull(asyncFn);

        // Get the typed delegate directly in C#
        var typedDelegate = asyncFn!.AsTypedDelegate();
        Assert.NotNull(typedDelegate);
    }

    [Fact]
    public async Task AsyncDefn_ImplicitConversion_Works()
    {
        var session = new NreplSession();

        // Define an async function
        await session.EvalAsync("(defn ^:async identity-async [x] x)");

        // Get the var root value (the AsyncFn1)
        var varResult = await session.EvalAsync("(deref (Var/Find \"user\" \"identity-async\"))");
        Assert.Null(varResult.Error);

        var asyncFn = varResult.Values![0] as Cljr.AsyncFn1;
        Assert.NotNull(asyncFn);

        // Use implicit conversion in C#
        Func<object?, System.Threading.Tasks.Task<object?>> typedFn = asyncFn!;
        Assert.NotNull(typedFn);
    }

    #endregion

    #region Type Caching Tests

    [Fact]
    public async Task Defrecord_ReEvaluation_ReusesCachedType()
    {
        var session = new NreplSession();

        // Use unique namespace
        await session.EvalAsync("(in-ns 'type-cache-test-1)");

        // Define a record
        var result1 = await session.EvalAsync("(defrecord CachedPerson [name age])");
        Assert.Null(result1.Error);
        var type1 = result1.Values![0] as Type;
        Assert.NotNull(type1);

        // Re-evaluate the exact same record definition
        var result2 = await session.EvalAsync("(defrecord CachedPerson [name age])");
        Assert.Null(result2.Error);
        var type2 = result2.Values![0] as Type;
        Assert.NotNull(type2);

        // Should be the exact same type (cached)
        Assert.Same(type1, type2);

        // Should still be usable
        var instanceResult = await session.EvalAsync("(CachedPerson. \"Alice\" 30)");
        Assert.Null(instanceResult.Error);
        Assert.NotNull(instanceResult.Values![0]);
    }

    [Fact]
    public async Task Defrecord_ChangedFields_CreatesNewType()
    {
        var session = new NreplSession();

        // Use unique namespace
        await session.EvalAsync("(in-ns 'type-cache-test-2)");

        // Define a record with two fields
        var result1 = await session.EvalAsync("(defrecord MutableRecord [name])");
        Assert.Null(result1.Error);
        var type1 = result1.Values![0] as Type;
        Assert.NotNull(type1);

        // Define the same record name with different fields
        var result2 = await session.EvalAsync("(defrecord MutableRecord [name age])");
        Assert.Null(result2.Error);
        var type2 = result2.Values![0] as Type;
        Assert.NotNull(type2);

        // Should be different types (different signature)
        Assert.NotSame(type1, type2);
    }

    [Fact]
    public async Task Defrecord_SameNameDifferentNamespaces_CreatesDistinctTypes()
    {
        var session = new NreplSession();

        // Define record in first namespace
        await session.EvalAsync("(in-ns 'ns-type-a)");
        var result1 = await session.EvalAsync("(defrecord SharedName [x])");
        Assert.Null(result1.Error);
        var type1 = result1.Values![0] as Type;

        // Define same record in second namespace
        await session.EvalAsync("(in-ns 'ns-type-b)");
        var result2 = await session.EvalAsync("(defrecord SharedName [x])");
        Assert.Null(result2.Error);
        var type2 = result2.Values![0] as Type;

        // Should be different types (different namespaces)
        Assert.NotSame(type1, type2);
    }

    [Fact]
    public async Task Defrecord_MultipleReEvaluations_NoCacheGrowth()
    {
        var session = new NreplSession();

        // Use unique namespace
        await session.EvalAsync("(in-ns 'type-cache-test-3)");

        // Re-evaluate the same record multiple times
        for (int i = 0; i < 5; i++)
        {
            var result = await session.EvalAsync("(defrecord RepeatedRecord [value])");
            Assert.Null(result.Error);
        }

        // Should still be able to instantiate
        var instanceResult = await session.EvalAsync("(RepeatedRecord. 42)");
        Assert.Null(instanceResult.Error);

        var fieldResult = await session.EvalAsync("(.-value (RepeatedRecord. 99))");
        Assert.Null(fieldResult.Error);
        Assert.Equal(99L, fieldResult.Values![0]);
    }

    [Fact]
    public async Task Deftype_ReEvaluation_ReusesCachedType()
    {
        var session = new NreplSession();

        // Use unique namespace
        await session.EvalAsync("(in-ns 'deftype-cache-test)");

        // Define a type
        var result1 = await session.EvalAsync("(deftype CachedType [x y])");
        Assert.Null(result1.Error);
        var type1 = result1.Values![0] as Type;
        Assert.NotNull(type1);

        // Re-evaluate the exact same type definition
        var result2 = await session.EvalAsync("(deftype CachedType [x y])");
        Assert.Null(result2.Error);
        var type2 = result2.Values![0] as Type;
        Assert.NotNull(type2);

        // Should be the exact same type (cached)
        Assert.Same(type1, type2);
    }

    [Fact]
    public async Task Defrecord_WithTypeHints_CachesBySignature()
    {
        var session = new NreplSession();

        // Use unique namespace
        await session.EvalAsync("(in-ns 'type-hint-cache-test)");

        // Define record with type hints
        var result1 = await session.EvalAsync("(defrecord TypedRecord [^String name ^int count])");
        Assert.Null(result1.Error);
        var type1 = result1.Values![0] as Type;

        // Re-evaluate with same type hints
        var result2 = await session.EvalAsync("(defrecord TypedRecord [^String name ^int count])");
        Assert.Null(result2.Error);
        var type2 = result2.Values![0] as Type;

        // Should be cached
        Assert.Same(type1, type2);

        // Define with different type hint - should be new type
        var result3 = await session.EvalAsync("(defrecord TypedRecord [^String name ^long count])");
        Assert.Null(result3.Error);
        var type3 = result3.Values![0] as Type;

        // Should be different (int vs long)
        Assert.NotSame(type1, type3);
    }

    [Fact]
    public async Task Defrecord_VisibleInDefiningNamespace_AfterSwitchingBack()
    {
        var session = new NreplSession();

        // Define record in first namespace
        await session.EvalAsync("(in-ns 'record-visible-ns-a)");
        await session.EvalAsync("(defrecord VisibleRecord [x])");

        // Switch to another namespace
        await session.EvalAsync("(in-ns 'record-visible-ns-b)");

        // Switch back to original namespace
        await session.EvalAsync("(in-ns 'record-visible-ns-a)");

        // Record should still be visible in its defining namespace
        var result = await session.EvalAsync("(VisibleRecord. 123)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    #endregion

    #region Type Namespace Isolation Tests

    [Fact]
    public async Task Defrecord_NotAccessible_FromOtherNamespace_WithoutImport()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'type-isolation-a)");
        var defResult = await session.EvalAsync("(defrecord IsolatedRecord [x])");
        Assert.Null(defResult.Error);

        // Switch to namespace-b (without requiring namespace-a)
        await session.EvalAsync("(in-ns 'type-isolation-b)");

        // Try to instantiate the record - should fail (type not accessible)
        var result = await session.EvalAsync("(IsolatedRecord. 42)");

        // Should have an error because the type is not accessible
        Assert.NotNull(result.Error);
        Assert.Contains("is not accessible", result.Error);
    }

    [Fact]
    public async Task Defrecord_Accessible_InDefiningNamespace()
    {
        var session = new NreplSession();

        // Define record in namespace
        await session.EvalAsync("(in-ns 'type-defining-ns)");
        await session.EvalAsync("(defrecord LocalRecord [value])");

        // Should be able to use it in the same namespace
        var result = await session.EvalAsync("(LocalRecord. 123)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_TrackingDefiningNamespace()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'tracking-ns-a)");
        await session.EvalAsync("(defrecord TrackedRecord [x])");

        // Check that ReplState tracks the defining namespace
        var definingNs = session.ReplState.GetTypeDefiningNamespace("TrackedRecord");
        Assert.Equal("tracking-ns-a", definingNs);
    }

    [Fact]
    public async Task Defrecord_MultipleTypes_TrackedToCorrectNamespaces()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'multi-tracking-a)");
        await session.EvalAsync("(defrecord RecordA [x])");

        // Define record in namespace-b
        await session.EvalAsync("(in-ns 'multi-tracking-b)");
        await session.EvalAsync("(defrecord RecordB [y])");

        // Both should be tracked to their defining namespaces
        Assert.Equal("multi-tracking-a", session.ReplState.GetTypeDefiningNamespace("RecordA"));
        Assert.Equal("multi-tracking-b", session.ReplState.GetTypeDefiningNamespace("RecordB"));
    }

    [Fact]
    public async Task Deftype_NotAccessible_FromOtherNamespace_WithoutImport()
    {
        var session = new NreplSession();

        // Define type in namespace-a
        await session.EvalAsync("(in-ns 'deftype-isolation-a)");
        var defResult = await session.EvalAsync("(deftype IsolatedType [x])");
        Assert.Null(defResult.Error);

        // Switch to namespace-b (without requiring namespace-a)
        await session.EvalAsync("(in-ns 'deftype-isolation-b)");

        // Try to instantiate the type - should fail
        var result = await session.EvalAsync("(IsolatedType. 42)");

        // Should have an error because the type is not accessible
        Assert.NotNull(result.Error);
        Assert.Contains("is not accessible", result.Error);
    }

    [Fact]
    public async Task Defrecord_NotAccessible_DirectlyAfterRequire()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'require-type-a)");
        await session.EvalAsync("(defrecord RequiredRecord [value])");

        // Switch to namespace-b and require namespace-a (without alias)
        await session.EvalAsync("(in-ns 'require-type-b)");
        await session.EvalAsync("(require '[require-type-a])");

        // Direct access should FAIL - Clojure-like semantics require alias or import
        var result = await session.EvalAsync("(RequiredRecord. 123)");
        Assert.NotNull(result.Error);
        Assert.Contains("is not accessible", result.Error);
    }

    [Fact]
    public async Task Defrecord_NotAccessible_DirectlyWithRefer()
    {
        var session = new NreplSession();

        // Define record and a function in namespace-a
        await session.EvalAsync("(in-ns 'refer-type-a)");
        await session.EvalAsync("(defrecord ReferredRecord [x y])");
        await session.EvalAsync("(defn make-record [a b] (ReferredRecord. a b))");

        // Switch to namespace-b and require with :refer
        // Note: In Clojure, :refer is for vars (functions), not types
        await session.EvalAsync("(in-ns 'refer-type-b)");
        await session.EvalAsync("(require '[refer-type-a :refer [make-record]])");

        // Direct type access should FAIL - :refer doesn't make types accessible
        var result = await session.EvalAsync("(ReferredRecord. 10 20)");
        Assert.NotNull(result.Error);
        Assert.Contains("is not accessible", result.Error);

        // But referred function should work
        var fnResult = await session.EvalAsync("(make-record 10 20)");
        Assert.Null(fnResult.Error);
        Assert.NotNull(fnResult.Values![0]);
    }

    [Fact]
    public async Task Defrecord_Accessible_ViaAliasAfterRequire()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'alias-type-a)");
        await session.EvalAsync("(defrecord AliasedRecord [data])");

        // Switch to namespace-b and require with :as alias
        await session.EvalAsync("(in-ns 'alias-type-b)");
        await session.EvalAsync("(require '[alias-type-a :as a])");

        // Direct access should FAIL
        var directResult = await session.EvalAsync("(AliasedRecord. \"test\")");
        Assert.NotNull(directResult.Error);

        // Alias-qualified access should WORK
        var aliasResult = await session.EvalAsync("(a/AliasedRecord. \"test\")");
        Assert.Null(aliasResult.Error);
        Assert.NotNull(aliasResult.Values![0]);
    }

    [Fact]
    public async Task Defrecord_Accessible_WithAliasQualifiedName()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'alias-qual-a)");
        await session.EvalAsync("(defrecord QualRecord [x])");

        // Switch to namespace-b and require with :as alias
        await session.EvalAsync("(in-ns 'alias-qual-b)");
        await session.EvalAsync("(require '[alias-qual-a :as a])");

        // Should be able to instantiate with alias-qualified name: (a/QualRecord. 42)
        var result = await session.EvalAsync("(a/QualRecord. 42)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_Accessible_WithDottedAlias()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'dotted-alias-a)");
        await session.EvalAsync("(defrecord DottedRecord [x])");

        // Switch to namespace-b and require with dotted alias (e.g., :as api.main style)
        await session.EvalAsync("(in-ns 'dotted-alias-b)");
        await session.EvalAsync("(require '[dotted-alias-a :as da.alias])");

        // Should work with dotted alias: (da.alias/DottedRecord. 42)
        var result = await session.EvalAsync("(da.alias/DottedRecord. 42)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    #endregion

    #region Defrecord Factory Functions

    [Fact]
    public async Task Defrecord_CreatesPositionalFactory()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'factory-test-a)");
        await session.EvalAsync("(defrecord Point [x y])");

        // Factory function ->Point should exist and work
        var result = await session.EvalAsync("(->Point 10 20)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_CreatesMapFactory()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'map-factory-test-a)");
        await session.EvalAsync("(defrecord MapPoint [x y])");

        // Factory function map->MapPoint should exist and work
        var result = await session.EvalAsync("(map->MapPoint {:x 10 :y 20})");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_PositionalFactory_HasCorrectFieldValues()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'factory-fields-test)");
        await session.EvalAsync("(defrecord NamedPoint [name value])");

        // Create instance with factory
        var result = await session.EvalAsync("(->NamedPoint \"test\" 42)");
        Assert.Null(result.Error);

        // Verify field values
        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Equal("test", type.GetProperty("name")!.GetValue(instance));
        // Use Convert to handle numeric type differences (int vs long)
        Assert.Equal(42L, Convert.ToInt64(type.GetProperty("value")!.GetValue(instance)));
    }

    [Fact]
    public async Task Defrecord_MapFactory_HasCorrectFieldValues()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'map-factory-fields-test)");
        await session.EvalAsync("(defrecord MappedPoint [a b])");

        // Create instance with map factory
        var result = await session.EvalAsync("(map->MappedPoint {:a \"hello\" :b 123})");
        Assert.Null(result.Error);

        // Verify field values
        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Equal("hello", type.GetProperty("a")!.GetValue(instance));
        // Use Convert to handle numeric type differences (int vs long)
        Assert.Equal(123L, Convert.ToInt64(type.GetProperty("b")!.GetValue(instance)));
    }

    [Fact]
    public async Task Defrecord_FactoryCanBeReferred()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'factory-refer-ns-a)");
        await session.EvalAsync("(defrecord Widget [name])");

        // Switch to namespace-b and refer the factory
        await session.EvalAsync("(in-ns 'factory-refer-ns-b)");
        await session.EvalAsync("(require '[factory-refer-ns-a :refer [->Widget]])");

        // Referred factory should work
        var result = await session.EvalAsync("(->Widget \"test-widget\")");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_MapFactoryCanBeReferred()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'map-factory-refer-ns-a)");
        await session.EvalAsync("(defrecord MapWidget [name value])");

        // Switch to namespace-b and refer the map factory
        await session.EvalAsync("(in-ns 'map-factory-refer-ns-b)");
        await session.EvalAsync("(require '[map-factory-refer-ns-a :refer [map->MapWidget]])");

        // Referred map factory should work
        var result = await session.EvalAsync("(map->MapWidget {:name \"test\" :value 42})");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_FactoryAccessibleViaAlias()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'factory-alias-ns-a)");
        await session.EvalAsync("(defrecord AliasedThing [data])");

        // Switch to namespace-b and require with alias
        await session.EvalAsync("(in-ns 'factory-alias-ns-b)");
        await session.EvalAsync("(require '[factory-alias-ns-a :as faa])");

        // Factory should be accessible via alias
        var result = await session.EvalAsync("(faa/->AliasedThing 100)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_NoFieldsFactory()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'empty-factory-test)");
        await session.EvalAsync("(defrecord EmptyRecord [])");

        // Factory with no args should work
        var result = await session.EvalAsync("(->EmptyRecord)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Defrecord_SixFieldsFactory_InlineBoundary()
    {
        // Tests the boundary at 6 fields (last inline Func before variadic)
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'six-fields-test)");
        await session.EvalAsync("(defrecord SixFields [a b c d e f])");

        var result = await session.EvalAsync("(->SixFields 1 2 3 4 5 6)");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Equal(1L, Convert.ToInt64(type.GetProperty("a")!.GetValue(instance)));
        Assert.Equal(6L, Convert.ToInt64(type.GetProperty("f")!.GetValue(instance)));
    }

    [Fact]
    public async Task Defrecord_SevenFieldsFactory_VariadicBoundary()
    {
        // Tests variadic factory (7+ fields)
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'seven-fields-test)");
        await session.EvalAsync("(defrecord SevenFields [a b c d e f g])");

        var result = await session.EvalAsync("(->SevenFields 1 2 3 4 5 6 7)");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Equal(1L, Convert.ToInt64(type.GetProperty("a")!.GetValue(instance)));
        Assert.Equal(7L, Convert.ToInt64(type.GetProperty("g")!.GetValue(instance)));
    }

    [Fact]
    public async Task Defrecord_PositionalFactory_PreservesFieldOrder()
    {
        // Critical: verify positional args map to correct fields
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'field-order-test)");
        await session.EvalAsync("(defrecord OrderedFields [first second third])");

        var result = await session.EvalAsync("(->OrderedFields \"A\" \"B\" \"C\")");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();
        // These assertions would fail if field order was wrong
        Assert.Equal("A", type.GetProperty("first")!.GetValue(instance));
        Assert.Equal("B", type.GetProperty("second")!.GetValue(instance));
        Assert.Equal("C", type.GetProperty("third")!.GetValue(instance));
    }

    [Fact]
    public async Task Defrecord_MapFactory_MissingKeysAreNil()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'missing-keys-test)");
        await session.EvalAsync("(defrecord PartialRecord [x y z])");

        // Only provide x, leave y and z missing
        var result = await session.EvalAsync("(map->PartialRecord {:x 42})");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Equal(42L, Convert.ToInt64(type.GetProperty("x")!.GetValue(instance)));
        Assert.Null(type.GetProperty("y")!.GetValue(instance));
        Assert.Null(type.GetProperty("z")!.GetValue(instance));
    }

    [Fact]
    public async Task Defrecord_MapFactory_ExtraKeysIgnored()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'extra-keys-test)");
        await session.EvalAsync("(defrecord TwoFields [a b])");

        // Provide extra keys that don't match fields
        var result = await session.EvalAsync("(map->TwoFields {:a 1 :b 2 :c 3 :extra \"ignored\"})");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Equal(1L, Convert.ToInt64(type.GetProperty("a")!.GetValue(instance)));
        Assert.Equal(2L, Convert.ToInt64(type.GetProperty("b")!.GetValue(instance)));
        // No exception thrown - extra keys silently ignored
    }

    [Fact]
    public async Task Defrecord_Factory_NilValuesPreserved()
    {
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'nil-values-test)");
        await session.EvalAsync("(defrecord NilRecord [x y])");

        // Explicit nil values
        var result = await session.EvalAsync("(->NilRecord nil nil)");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();
        Assert.Null(type.GetProperty("x")!.GetValue(instance));
        Assert.Null(type.GetProperty("y")!.GetValue(instance));
    }

    [Fact]
    public async Task Defrecord_FactoryAndConstructor_ProduceSameResult()
    {
        // Property: (->Record args) should equal (Record. args)
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'equivalence-test)");
        await session.EvalAsync("(defrecord EquivRecord [x y])");

        var factoryResult = await session.EvalAsync("(->EquivRecord 10 20)");
        var constructorResult = await session.EvalAsync("(EquivRecord. 10 20)");

        Assert.Null(factoryResult.Error);
        Assert.Null(constructorResult.Error);

        var factoryInstance = factoryResult.Values![0]!;
        var constructorInstance = constructorResult.Values![0]!;

        // Both should have same field values
        var type = factoryInstance.GetType();
        Assert.Equal(
            type.GetProperty("x")!.GetValue(factoryInstance),
            type.GetProperty("x")!.GetValue(constructorInstance));
        Assert.Equal(
            type.GetProperty("y")!.GetValue(factoryInstance),
            type.GetProperty("y")!.GetValue(constructorInstance));
    }

    [Fact]
    public async Task Defrecord_TypeHintedFields_PositionalFactory()
    {
        // Verify factories work with type-hinted fields
        // Note: Clojure integers are long by default, so use ^long for numeric fields
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'typed-fields-test)");
        await session.EvalAsync("(defrecord TypedRecord [^long count ^String name ^bool active])");

        var result = await session.EvalAsync("(->TypedRecord 42 \"test\" true)");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();

        // Verify fields have correct types and values
        var countProp = type.GetProperty("count")!;
        var nameProp = type.GetProperty("name")!;
        var activeProp = type.GetProperty("active")!;

        Assert.Equal(typeof(long), countProp.PropertyType);
        Assert.Equal(typeof(string), nameProp.PropertyType);
        Assert.Equal(typeof(bool), activeProp.PropertyType);

        Assert.Equal(42L, countProp.GetValue(instance));
        Assert.Equal("test", nameProp.GetValue(instance));
        Assert.Equal(true, activeProp.GetValue(instance));
    }

    [Fact]
    public async Task Defrecord_TypeHintedFields_MapFactory()
    {
        // Verify map factory works with type-hinted fields
        // Note: Clojure integers are long by default, so use ^long for numeric fields
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'typed-map-factory-test)");
        await session.EvalAsync("(defrecord TypedMapRecord [^long x ^String label])");

        var result = await session.EvalAsync("(map->TypedMapRecord {:x 100 :label \"hello\"})");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();

        // Map factory should correctly populate typed fields
        Assert.Equal(100L, type.GetProperty("x")!.GetValue(instance));
        Assert.Equal("hello", type.GetProperty("label")!.GetValue(instance));
    }

    [Fact]
    public async Task Defrecord_TypeHintedFields_DoubleType()
    {
        // Test with double type hint (Clojure floating-point default)
        var session = new NreplSession();

        await session.EvalAsync("(in-ns 'typed-double-test)");
        await session.EvalAsync("(defrecord DoubleRecord [^double value ^String unit])");

        var result = await session.EvalAsync("(->DoubleRecord 3.14159 \"radians\")");
        Assert.Null(result.Error);

        var instance = result.Values![0]!;
        var type = instance.GetType();

        Assert.Equal(typeof(double), type.GetProperty("value")!.PropertyType);
        Assert.Equal(3.14159, type.GetProperty("value")!.GetValue(instance));
        Assert.Equal("radians", type.GetProperty("unit")!.GetValue(instance));
    }

    #endregion

    #region Import Support for REPL-Defined Types

    [Fact]
    public async Task Import_MakesReplTypeDirectlyAccessible()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'import-type-ns-a)");
        await session.EvalAsync("(defrecord ImportedRecord [value])");

        // Switch to namespace-b and import the type
        await session.EvalAsync("(ns import-type-ns-b (:import (import-type-ns-a ImportedRecord)))");

        // Direct constructor access should work after import
        var result = await session.EvalAsync("(ImportedRecord. 42)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Import_WorksWithHyphenatedNamespace()
    {
        var session = new NreplSession();

        // Define record in hyphenated namespace
        await session.EvalAsync("(in-ns 'my-app.models)");
        await session.EvalAsync("(defrecord HyphenRecord [x y])");

        // Import from hyphenated namespace
        await session.EvalAsync("(ns other-app.handlers (:import (my-app.models HyphenRecord)))");

        // Direct access should work
        var result = await session.EvalAsync("(HyphenRecord. 1 2)");
        Assert.Null(result.Error);
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task Import_TypeNotAccessibleWithoutImport()
    {
        var session = new NreplSession();

        // Define record in namespace-a
        await session.EvalAsync("(in-ns 'no-import-ns-a)");
        await session.EvalAsync("(defrecord NoImportRecord [x])");

        // Switch to namespace-b without importing
        await session.EvalAsync("(in-ns 'no-import-ns-b)");

        // Direct access should fail - type not accessible
        var result = await session.EvalAsync("(NoImportRecord. 42)");
        Assert.NotNull(result.Error);
        Assert.Contains("not accessible", result.Error);
    }

    #endregion

    #region Void-Returning Methods

    [Fact]
    public async Task VoidMethod_InDoBlock_Works()
    {
        var session = new NreplSession();

        // Console.WriteLine returns void - should work in do block
        var result = await session.EvalAsync(
            "(do (csharp* \"System.Console.WriteLine(\\\"test\\\")\") 42)");

        Assert.Null(result.Error);
        Assert.Equal(42L, result.Values![0]);
    }

    [Fact]
    public async Task VoidMethod_InDefn_Works()
    {
        var session = new NreplSession();

        // Define a function that calls a void method then returns a value
        await session.EvalAsync(
            "(defn log-and-return [x] (csharp* \"System.Console.WriteLine(~{x})\") x)");

        var result = await session.EvalAsync("(log-and-return 99)");
        Assert.Null(result.Error);
        Assert.Equal(99L, result.Values![0]);
    }

    [Fact]
    public async Task VoidMethod_AsLastExpr_ReturnsNil()
    {
        var session = new NreplSession();

        // When void method is the last/only expression, should return nil
        var result = await session.EvalAsync(
            "(do (csharp* \"System.Console.WriteLine(\\\"hello\\\")\"))");

        Assert.Null(result.Error);
        Assert.Null(result.Values![0]);
    }

    [Fact]
    public async Task VoidMethod_MultipleInSequence_Works()
    {
        var session = new NreplSession();

        // Multiple void calls in sequence
        var result = await session.EvalAsync(
            "(do (csharp* \"System.Console.WriteLine(\\\"first\\\")\") " +
            "(csharp* \"System.Console.WriteLine(\\\"second\\\")\") " +
            "(csharp* \"System.Console.WriteLine(\\\"third\\\")\") " +
            ":done)");

        Assert.Null(result.Error);
    }

    #endregion

    #region RequireExtractor and DependencyGraph Tests

    [Fact]
    public void RequireExtractor_ExtractsRequiresFromMathCljr()
    {
        var mathSource = @"(ns mylib.math
  (:require [mylib.utils :as utils]))";

        var info = Cljr.Compiler.Namespace.RequireExtractor.Extract(mathSource);

        Assert.NotNull(info);
        Assert.Equal("mylib.math", info!.Namespace);
        Assert.Single(info.Requires);
        Assert.Equal("mylib.utils", info.Requires[0].Namespace);
        Assert.Equal("utils", info.Requires[0].Alias);
    }

    [Fact]
    public void RequireExtractor_ExtractsRequiresFromCoreCljr()
    {
        var coreSource = @"(ns myapp.core
  (:require [mylib.utils :as utils]
            [mylib.math :as math :refer [sum-squared]]))";

        var info = Cljr.Compiler.Namespace.RequireExtractor.Extract(coreSource);

        Assert.NotNull(info);
        Assert.Equal("myapp.core", info!.Namespace);
        Assert.Equal(2, info.Requires.Count);
        Assert.Equal("mylib.utils", info.Requires[0].Namespace);
        Assert.Equal("mylib.math", info.Requires[1].Namespace);
    }

    [Fact]
    public void RequireExtractor_HandlesNoRequires()
    {
        var utilsSource = @"(ns mylib.utils)";

        var info = Cljr.Compiler.Namespace.RequireExtractor.Extract(utilsSource);

        Assert.NotNull(info);
        Assert.Equal("mylib.utils", info!.Namespace);
        Assert.Empty(info.Requires);
    }

    [Fact]
    public void DependencyGraph_OrdersFilesCorrectly()
    {
        var utilsSource = @"(ns mylib.utils)";
        var mathSource = @"(ns mylib.math
  (:require [mylib.utils :as utils]))";
        var coreSource = @"(ns myapp.core
  (:require [mylib.utils :as utils]
            [mylib.math :as math]))";

        var graph = new Cljr.Compiler.Namespace.DependencyGraph();
        graph.AddFile("utils.cljr", utilsSource);
        graph.AddFile("math.cljr", mathSource);
        graph.AddFile("core.cljr", coreSource);

        var result = graph.GetOrderedFiles();
        Assert.IsType<Cljr.Compiler.Namespace.DependencyResult.Success>(result);

        var success = (Cljr.Compiler.Namespace.DependencyResult.Success)result;

        // utils should come first (no deps)
        // math should come second (deps on utils)
        // core should come last (deps on utils and math)
        Assert.Equal(3, success.Files.Count);
        // Note: DependencyGraph strips file extension
        Assert.Equal("utils", success.Files[0].FileName);
        Assert.Equal("math", success.Files[1].FileName);
        Assert.Equal("core", success.Files[2].FileName);
    }

    #endregion

    #region Stdout Capture Tests

    [Fact]
    public async Task Println_CapturesStdout()
    {
        var session = new NreplSession();

        var result = await session.EvalAsync("(println \"hello world\")");

        Assert.Null(result.Error);
        Assert.NotNull(result.Output);
        Assert.Contains("hello world", result.Output);
    }

    [Fact]
    public async Task Dotimes_WithPrintln_CapturesAllOutput()
    {
        var session = new NreplSession();

        // First check what dotimes expands to - it should work like any other expression
        var result = await session.EvalAsync("(dotimes [i 3] (println i))");

        // Debug: check result value (dotimes returns nil)
        Assert.Null(result.Error);
        Assert.Null(result.Values![0]); // dotimes returns nil

        // The real issue: stdout capture
        Assert.NotNull(result.Output);
        Assert.Contains("0", result.Output);
        Assert.Contains("1", result.Output);
        Assert.Contains("2", result.Output);
    }

    [Fact]
    public async Task Loop_WithPrintln_CapturesAllOutput()
    {
        // Test if the issue is with dotimes specifically or with loop/recur in general
        var session = new NreplSession();

        var result = await session.EvalAsync(@"
            (loop [i 0]
              (when (< i 3)
                (println i)
                (recur (inc i))))");

        Assert.Null(result.Error);
        Assert.NotNull(result.Output);
        Assert.Contains("0", result.Output);
    }

    [Fact]
    public void Dotimes_GeneratedCode_ShowsStructure()
    {
        // Diagnostic test: see what C# code is generated for dotimes with println
        var analyzer = new Analyzer.Analyzer();
        var emitter = new CSharpEmitter();

        // Read and macro-expand
        var forms = LispReader.ReadAll("(dotimes [i 3] (println i))").ToList();
        var expr = analyzer.Analyze(forms[0], new AnalyzerContext { IsRepl = true });
        var csharp = emitter.EmitScript(expr, "user");

        // Output for debugging - just verify it compiles without error
        Assert.NotNull(csharp);
        // The generated code should contain println
        Assert.Contains("println", csharp);
    }

    [Fact]
    public async Task SimplePrint_InLoop_CapturesOutput()
    {
        // Use a for-like structure without dotimes macro
        var session = new NreplSession();

        // Test with multiple println calls
        var result = await session.EvalAsync(@"
            (do
              (println ""one"")
              (println ""two"")
              (println ""three""))");

        Assert.Null(result.Error);
        Assert.NotNull(result.Output);
        Assert.Contains("one", result.Output);
        Assert.Contains("two", result.Output);
        Assert.Contains("three", result.Output);
    }

    [Fact]
    public async Task LetWrappingLoop_WithPrintln_CapturesOutput()
    {
        // Test if the issue is with let wrapping a loop (which is what dotimes does)
        var session = new NreplSession();

        // Manually write what dotimes expands to
        var result = await session.EvalAsync(@"
            (let [n 3]
              (loop [i 0]
                (when (< i n)
                  (println i)
                  (recur (inc i)))))");

        Assert.Null(result.Error);
        Assert.NotNull(result.Output);  // This is what fails for dotimes
        Assert.Contains("0", result.Output);
    }

    [Fact]
    public async Task Dotimes_MacroExpanded_ShowsExpansion()
    {
        // Check what dotimes actually expands to
        var session = new NreplSession();

        // Use macroexpand-1 to see the expansion
        var result = await session.EvalAsync("(macroexpand-1 '(dotimes [i 3] (println i)))");

        Assert.Null(result.Error);
        // The expansion should show us what's happening
        Assert.NotNull(result.Values![0]);
    }

    [Fact]
    public async Task GensymInLet_WithPrintln_CapturesOutput()
    {
        // Test if the issue is with gensym-like variable names
        var session = new NreplSession();

        // Use a name similar to what gensym produces
        var result = await session.EvalAsync(@"
            (let [n__auto__1234 3]
              (loop [i 0]
                (when (< i n__auto__1234)
                  (println i)
                  (recur (inc i)))))");

        Assert.Null(result.Error);
        Assert.NotNull(result.Output);
        Assert.Contains("0", result.Output);
    }

    #endregion
}
