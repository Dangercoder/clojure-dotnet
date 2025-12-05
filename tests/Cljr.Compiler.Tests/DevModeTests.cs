using Cljr;
using Cljr.Repl;

namespace Cljr.Compiler.Tests;

// Disable parallel execution with tests that use Var registry
[Collection("VarTests")]
public class DevModeTests
{
    [Fact]
    public void Var_Intern_CreatesVar()
    {
        // Clean up before test
        Var.ClearAll();

        var v = Var.Intern("test-ns", "my-var");

        Assert.Equal("test-ns", v.Namespace);
        Assert.Equal("my-var", v.Name);
        Assert.Equal("test-ns/my-var", v.FullName);
        Assert.False(v.IsBound);
    }

    [Fact]
    public void Var_Intern_ReturnsSameVar()
    {
        Var.ClearAll();

        var v1 = Var.Intern("test-ns", "my-var");
        var v2 = Var.Intern("test-ns", "my-var");

        Assert.Same(v1, v2);
    }

    [Fact]
    public void Var_BindRoot_SetsValue()
    {
        Var.ClearAll();

        var v = Var.Intern("test-ns", "my-var");
        v.BindRoot(42);

        Assert.True(v.IsBound);
        Assert.Equal(42, v.Deref());
    }

    [Fact]
    public void Var_Find_ReturnsVar()
    {
        Var.ClearAll();

        Var.Intern("find-ns", "find-var").BindRoot("found");

        var v = Var.Find("find-ns", "find-var");
        Assert.NotNull(v);
        Assert.Equal("found", v.Deref());
    }

    [Fact]
    public void Var_Find_ReturnsNullForMissing()
    {
        Var.ClearAll();

        var v = Var.Find("nonexistent", "nope");
        Assert.Null(v);
    }

    [Fact]
    public void Var_Invoke_CallsFunction()
    {
        Var.ClearAll();

        var v = Var.Intern("test-ns", "add-one");
        v.BindRoot((Func<object?, object?>)(x => (long)x! + 1));

        var result = v.Invoke(5L);
        Assert.Equal(6L, result);
    }

    [Fact]
    public void Var_Rebind_UpdatesAllCallers()
    {
        Var.ClearAll();

        var v = Var.Intern("test-ns", "greet");
        v.BindRoot((Func<object?, object?>)(x => $"Hello, {x}!"));

        Assert.Equal("Hello, World!", v.Invoke("World"));

        // Rebind to different implementation
        v.BindRoot((Func<object?, object?>)(x => $"Greetings, {x}!"));

        // Same var, new behavior
        Assert.Equal("Greetings, World!", v.Invoke("World"));
    }

    [Fact]
    public void Var_GetNamespaceVars_ReturnsVarsInNamespace()
    {
        Var.ClearAll();

        Var.Intern("ns1", "a").BindRoot(1);
        Var.Intern("ns1", "b").BindRoot(2);
        Var.Intern("ns2", "c").BindRoot(3);

        var ns1Vars = Var.GetNamespaceVars("ns1").ToList();

        Assert.Equal(2, ns1Vars.Count);
        Assert.Contains(ns1Vars, v => v.Name == "a");
        Assert.Contains(ns1Vars, v => v.Name == "b");
    }

    [Fact]
    public void Var_ClearNamespace_RemovesVars()
    {
        Var.ClearAll();

        Var.Intern("clear-ns", "x").BindRoot(1);
        Var.Intern("clear-ns", "y").BindRoot(2);
        Var.Intern("other-ns", "z").BindRoot(3);

        Var.ClearNamespace("clear-ns");

        Assert.Null(Var.Find("clear-ns", "x"));
        Assert.Null(Var.Find("clear-ns", "y"));
        Assert.NotNull(Var.Find("other-ns", "z"));
    }

    [Fact]
    public void RuntimeNamespace_FindOrCreate_CreatesNamespace()
    {
        RuntimeNamespace.ClearAll();

        var ns = RuntimeNamespace.FindOrCreate("my-app.core");

        Assert.Equal("my-app.core", ns.Name);
    }

    [Fact]
    public void RuntimeNamespace_FindOrCreate_ReturnsSameInstance()
    {
        RuntimeNamespace.ClearAll();

        var ns1 = RuntimeNamespace.FindOrCreate("my-app.core");
        var ns2 = RuntimeNamespace.FindOrCreate("my-app.core");

        Assert.Same(ns1, ns2);
    }

    [Fact]
    public void RuntimeNamespace_Intern_CreatesVar()
    {
        RuntimeNamespace.ClearAll();
        Var.ClearAll();

        var ns = RuntimeNamespace.FindOrCreate("intern-ns");
        var v = ns.Intern("my-func");

        Assert.Equal("intern-ns", v.Namespace);
        Assert.Equal("my-func", v.Name);
    }

    [Fact]
    public void RuntimeNamespace_Aliases_Work()
    {
        RuntimeNamespace.ClearAll();
        Var.ClearAll();

        var ns = RuntimeNamespace.FindOrCreate("user");
        ns.AddAlias("str", "cljr.string");

        Assert.Equal("cljr.string", ns.Aliases["str"]);
    }

    [Fact]
    public void RuntimeNamespace_Resolve_FindsLocalVar()
    {
        RuntimeNamespace.ClearAll();
        Var.ClearAll();

        var ns = RuntimeNamespace.FindOrCreate("resolve-test");
        var v = ns.Intern("my-var");
        v.BindRoot(42);

        var resolved = ns.Resolve("my-var");
        Assert.NotNull(resolved);
        Assert.Equal(42, resolved.Deref());
    }

    [Fact]
    public void RuntimeNamespace_Resolve_FindsReferred()
    {
        RuntimeNamespace.ClearAll();
        Var.ClearAll();

        // Create a var in source namespace
        var sourceVar = Var.Intern("source-ns", "exported-fn");
        sourceVar.BindRoot("exported");

        // Refer it in another namespace
        var targetNs = RuntimeNamespace.FindOrCreate("target-ns");
        targetNs.AddRefer("exported-fn", sourceVar);

        var resolved = targetNs.Resolve("exported-fn");
        Assert.NotNull(resolved);
        Assert.Equal("exported", resolved.Deref());
    }

    [Fact]
    public void StateRegistry_CapturesAtoms()
    {
        RuntimeNamespace.ClearAll();
        Var.ClearAll();

        var ns = RuntimeNamespace.FindOrCreate("state-test");
        var atomVar = ns.Intern("app-state");
        var atom = new Atom(new Dictionary<object, object?> { ["count"] = 10L });
        atomVar.BindRoot(atom);

        var regularVar = ns.Intern("config");
        regularVar.BindRoot("some-config");

        var registry = new StateRegistry();
        var captured = registry.CaptureState("state-test");

        // Only atom should be captured
        Assert.Single(captured);
        Assert.True(captured.ContainsKey("app-state"));
        Assert.Same(atom, captured["app-state"]);
    }

    [Fact]
    public void StateRegistry_RestoresAtoms()
    {
        RuntimeNamespace.ClearAll();
        Var.ClearAll();

        // Setup initial state
        var ns = RuntimeNamespace.FindOrCreate("restore-test");
        var atomVar = ns.Intern("my-atom");
        var originalAtom = new Atom(42L);
        atomVar.BindRoot(originalAtom);

        // Capture state
        var registry = new StateRegistry();
        var captured = registry.CaptureState("restore-test");

        // Simulate reload - new atom created
        var newAtom = new Atom(0L);
        atomVar.BindRoot(newAtom);

        // Restore state
        registry.RestoreState("restore-test", captured);

        // Should have original atom back
        Assert.Same(originalAtom, atomVar.Deref());
    }

    [Fact]
    public void NamespaceLoader_FindSourceFile_ConvertsNamespace()
    {
        var session = new NreplSession();
        var loader = new NamespaceLoader(session, ["test-fixtures"]);

        // Create test directory and file
        Directory.CreateDirectory("test-fixtures/my_app");
        File.WriteAllText("test-fixtures/my_app/core.cljr", "(ns my-app.core)");

        try
        {
            var path = loader.FindSourceFile("my-app.core");
            Assert.NotNull(path);
            Assert.EndsWith("my_app/core.cljr", path.Replace('\\', '/'));
        }
        finally
        {
            // Cleanup
            File.Delete("test-fixtures/my_app/core.cljr");
            Directory.Delete("test-fixtures/my_app");
            Directory.Delete("test-fixtures");
        }
    }

    [Fact]
    public void NamespaceLoader_GetNamespaceForPath_DerivesNamespace()
    {
        var session = new NreplSession();
        var loader = new NamespaceLoader(session, ["src"]);

        // Create test directory structure
        Directory.CreateDirectory("src/my_app");
        File.WriteAllText("src/my_app/core.cljr", "(ns my-app.core)");

        try
        {
            // First find the file to register it
            loader.FindSourceFile("my-app.core");

            // Now test path -> namespace conversion
            var ns = loader.GetNamespaceForPath("src/my_app/core.cljr");
            Assert.Equal("my-app.core", ns);
        }
        finally
        {
            File.Delete("src/my_app/core.cljr");
            Directory.Delete("src/my_app");
        }
    }

    [Fact]
    public void NamespaceLoader_TracksDependencies()
    {
        var session = new NreplSession();
        var loader = new NamespaceLoader(session);

        loader.AddDependency("my-app.handler", "my-app.db");
        loader.AddDependency("my-app.handler", "my-app.utils");

        var deps = loader.GetDependencies("my-app.handler").ToList();

        Assert.Contains("my-app.db", deps);
        Assert.Contains("my-app.utils", deps);
    }

    [Fact]
    public void NamespaceLoader_TracksDependents()
    {
        var session = new NreplSession();
        var loader = new NamespaceLoader(session);

        loader.AddDependency("my-app.handler", "my-app.db");
        loader.AddDependency("my-app.api", "my-app.db");

        var dependents = loader.GetDependents("my-app.db").ToList();

        Assert.Contains("my-app.handler", dependents);
        Assert.Contains("my-app.api", dependents);
    }

    [Fact]
    public async Task DevModeSession_CanEvaluateCode()
    {
        var options = new DevModeOptions { EnableWatching = false };
        using var session = new DevModeSession(options);

        var result = await session.EvalAsync("(+ 1 2)");

        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.Equal(3L, result.Values[0]);
    }

    [Fact]
    public void DevModeOptions_HasDefaults()
    {
        var options = new DevModeOptions();

        Assert.Contains("src", options.WatchPaths);
        Assert.Contains("src", options.SourcePaths);
        Assert.True(options.EnableWatching);
        Assert.True(options.AutoReload);
        Assert.Null(options.InitialNamespace);
        Assert.Equal(0, options.Port);
        Assert.False(options.Verbose);
    }
}
