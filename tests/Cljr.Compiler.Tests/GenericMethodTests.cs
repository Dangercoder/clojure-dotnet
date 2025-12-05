using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Emitter;
using Cljr.Compiler.Reader;

namespace Cljr.Compiler.Tests;

/// <summary>
/// Tests for generic method call support using pipe-escaped symbols.
/// Syntax: (Type/|Method&lt;T&gt;| args) for static methods
///         (.|Method&lt;T&gt;| obj args) for instance methods
/// </summary>
public class GenericMethodTests
{
    private readonly Analyzer.Analyzer _analyzer = new();
    private readonly CSharpEmitter _emitter = new();

    private string CompileExpr(string code)
    {
        var forms = LispReader.ReadAll(code).ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());
        return _emitter.EmitScript(expr, "user");
    }

    #region Static Generic Methods

    [Fact]
    public void StaticGenericMethod_SingleTypeArg_EmitsCorrectly()
    {
        // (System.Linq.Enumerable/|Empty<String>|)
        var code = "(System.Linq.Enumerable/|Empty<String>|)";
        var result = CompileExpr(code);

        Assert.Contains("System.Linq.Enumerable.Empty<String>()", result);
    }

    [Fact]
    public void StaticGenericMethod_WithArgs_EmitsCorrectly()
    {
        // (System.Linq.Enumerable/|Cast<Int32>| coll)
        var code = "(System.Linq.Enumerable/|Cast<Int32>| coll)";
        var result = CompileExpr(code);

        Assert.Contains("System.Linq.Enumerable.Cast<Int32>(", result);
    }

    [Fact]
    public void StaticGenericMethod_MultipleTypeArgs_EmitsCorrectly()
    {
        // (System.Linq.Enumerable/|ToDictionary<String, Int32>| coll)
        var code = "(Dictionary/|Create<String, Int32>|)";
        var result = CompileExpr(code);

        Assert.Contains("Dictionary.Create<String, Int32>()", result);
    }

    [Fact]
    public void StaticGenericMethod_NestedGenericType_EmitsCorrectly()
    {
        // Generic type within generic method type parameter
        var code = "(Foo/|Bar<List<String>>|)";
        var result = CompileExpr(code);

        Assert.Contains("Foo.Bar<List<String>>()", result);
    }

    #endregion

    #region Instance Generic Methods

    [Fact]
    public void InstanceGenericMethod_SingleTypeArg_EmitsCorrectly()
    {
        // (.|GetService<ILogger>| provider)
        var code = "(.|GetService<ILogger>| provider)";
        var result = CompileExpr(code);

        Assert.Contains(".GetService<ILogger>()", result);
    }

    [Fact]
    public void InstanceGenericMethod_WithArgs_EmitsCorrectly()
    {
        // (.|Convert<Int32>| obj value)
        var code = "(.|Convert<Int32>| obj value)";
        var result = CompileExpr(code);

        Assert.Contains(".Convert<Int32>(", result);
    }

    [Fact]
    public void InstanceGenericMethod_MultipleTypeArgs_EmitsCorrectly()
    {
        // (.|Transform<String, Int32>| obj)
        var code = "(.|Transform<String, Int32>| obj)";
        var result = CompileExpr(code);

        Assert.Contains(".Transform<String, Int32>()", result);
    }

    #endregion

    #region Parsing

    [Fact]
    public void ParseGenericMethod_ExtractsTypeArgs()
    {
        var forms = LispReader.ReadAll("(Foo/|Bar<String>| x)").ToList();
        var list = (PersistentList)forms[0];
        var sym = (Reader.Symbol)list[0];

        Assert.Equal("Foo", sym.Namespace);
        Assert.Equal("|Bar<String>|", sym.Name);
    }

    [Fact]
    public void AnalyzeGenericMethod_CreatesCorrectExpr()
    {
        var forms = LispReader.ReadAll("(Foo/|Bar<String, Int32>| x)").ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());

        var staticMethod = Assert.IsType<StaticMethodExpr>(expr);
        Assert.Equal("Foo", staticMethod.TypeName);
        Assert.Equal("Bar", staticMethod.MethodName);
        Assert.NotNull(staticMethod.TypeArguments);
        Assert.Equal(2, staticMethod.TypeArguments.Count);
        Assert.Equal("String", staticMethod.TypeArguments[0]);
        Assert.Equal("Int32", staticMethod.TypeArguments[1]);
    }

    [Fact]
    public void AnalyzeInstanceGenericMethod_CreatesCorrectExpr()
    {
        var forms = LispReader.ReadAll("(.|GetService<ILogger>| provider)").ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());

        var instanceMethod = Assert.IsType<InstanceMethodExpr>(expr);
        Assert.Equal("GetService", instanceMethod.MethodName);
        Assert.NotNull(instanceMethod.TypeArguments);
        Assert.Single(instanceMethod.TypeArguments);
        Assert.Equal("ILogger", instanceMethod.TypeArguments[0]);
    }

    #endregion

    #region Non-Generic Methods (Regression)

    [Fact]
    public void NonGenericStaticMethod_StillWorks()
    {
        var code = "(System.Console/WriteLine \"hello\")";
        var result = CompileExpr(code);

        Assert.Contains("System.Console.WriteLine(", result);
        Assert.DoesNotContain("<", result);
    }

    [Fact]
    public void NonGenericInstanceMethod_StillWorks()
    {
        var code = "(.ToString obj)";
        var result = CompileExpr(code);

        Assert.Contains(".ToString()", result);
        Assert.DoesNotContain("<", result);
    }

    #endregion
}
