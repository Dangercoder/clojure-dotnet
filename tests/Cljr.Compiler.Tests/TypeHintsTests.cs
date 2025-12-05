using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Emitter;
using Cljr.Compiler.Reader;

namespace Cljr.Compiler.Tests;

/// <summary>
/// Tests for Phase 6: Type hints, csharp*, defprotocol, deftype, defrecord
/// </summary>
public class TypeHintsTests
{
    private readonly Analyzer.Analyzer _analyzer = new();
    private readonly CSharpEmitter _emitter = new();

    private string Compile(string code)
    {
        var forms = LispReader.ReadAll(code).ToList();
        var unit = _analyzer.AnalyzeFile(forms);
        return _emitter.Emit(unit);
    }

    private string CompileExpr(string code)
    {
        var forms = LispReader.ReadAll(code).ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());
        return _emitter.EmitScript(expr, "user");
    }

    #region Pipe-Escaped Symbols

    [Fact]
    public void PipeEscapedSymbol_ArrayType_ReadsCorrectly()
    {
        var forms = LispReader.ReadAll("|string[]|").ToList();
        Assert.Single(forms);
        var sym = Assert.IsType<Reader.Symbol>(forms[0]);
        Assert.Equal("string[]", sym.Name);
        Assert.Null(sym.Namespace);
    }

    [Fact]
    public void PipeEscapedSymbol_GenericType_ReadsCorrectly()
    {
        var forms = LispReader.ReadAll("|Task<IList<User>>|").ToList();
        Assert.Single(forms);
        var sym = Assert.IsType<Reader.Symbol>(forms[0]);
        Assert.Equal("Task<IList<User>>", sym.Name);
    }

    [Fact]
    public void PipeEscapedSymbol_EscapedPipe_ReadsCorrectly()
    {
        // || inside pipes represents a literal |
        var forms = LispReader.ReadAll("|a||b|").ToList();
        Assert.Single(forms);
        var sym = Assert.IsType<Reader.Symbol>(forms[0]);
        Assert.Equal("a|b", sym.Name);
    }

    [Fact]
    public void PipeEscapedSymbol_AsTypeHint_WorksWithMeta()
    {
        var forms = LispReader.ReadAll("^|string[]| args").ToList();
        Assert.Single(forms);
        var sym = Assert.IsType<Reader.Symbol>(forms[0]);
        Assert.NotNull(sym.Meta);
    }

    [Fact]
    public void PipeEscapedSymbol_Empty_ThrowsReaderException()
    {
        Assert.Throws<ReaderException>(() =>
            LispReader.ReadAll("||").ToList());
    }

    [Fact]
    public void PipeEscapedSymbol_Unclosed_ThrowsReaderException()
    {
        var ex = Assert.Throws<ReaderException>(() =>
            LispReader.ReadAll("|unclosed").ToList());
        Assert.Contains("EOF", ex.Message);
    }

    [Fact]
    public void TypeHint_ArrayTypeWithPipeEscape_EmitsTypedSignature()
    {
        var code = "(ns test.core) (defn process [^|string[]| args] nil)";
        var csharp = Compile(code);
        Assert.Contains("string[] args", csharp);
    }

    #endregion

    #region Type Hints

    [Fact(Skip = "Phase 6: Type hint return types not yet emitted")]
    public void TypeHint_SimpleReturnType_EmitsTypedSignature()
    {
        var code = "(ns test.core) (defn ^String greet [name] (str \"Hello \" name))";
        var csharp = Compile(code);

        Assert.Contains("public static String greet(object? name)", csharp);
    }

    [Fact]
    public void TypeHint_GenericReturnType_EmitsTypedSignature()
    {
        var code = "(ns test.core) (defn ^Task<String> fetch-data [] \"data\")";
        var csharp = Compile(code);

        Assert.Contains("public static async Task<String> fetch_data()", csharp);
    }

    [Fact]
    public void TypeHint_ComplexGeneric_EmitsTypedSignature()
    {
        var code = "(ns test.core) (defn ^Task<IList<User>> get-users [] nil)";
        var csharp = Compile(code);

        Assert.Contains("Task<IList<User>>", csharp);
    }

    [Fact(Skip = "Phase 6: Type hint parameter types not yet emitted")]
    public void TypeHint_ParameterType_EmitsTypedParameter()
    {
        var code = "(ns test.core) (defn greet [^String name] (str \"Hello \" name))";
        var csharp = Compile(code);

        Assert.Contains("String name", csharp);
    }

    [Fact]
    public void TypeHint_MultipleParameterTypes_EmitsTypedParameters()
    {
        var code = "(ns test.core) (defn add [^int a ^int b] (+ a b))";
        var csharp = Compile(code);

        Assert.Contains("int a", csharp);
        Assert.Contains("int b", csharp);
    }

    [Fact(Skip = "Phase 6: Type hint return and parameter types not yet emitted")]
    public void TypeHint_ReturnAndParamTypes_EmitsFullyTyped()
    {
        var code = "(ns test.core) (defn ^String greet [^String name] (str \"Hello \" name))";
        var csharp = Compile(code);

        Assert.Contains("public static String greet(String name)", csharp);
    }

    [Fact]
    public void TypeHint_TaskReturnType_AutoDetectsAsync()
    {
        var code = "(ns test.core) (defn ^Task<int> compute [] 42)";
        var csharp = Compile(code);

        Assert.Contains("async Task<int>", csharp);
    }

    #endregion

    #region csharp*

    [Fact]
    public void CSharp_SimpleExpression_EmitsRawCode()
    {
        var code = "(csharp* \"DateTime.Now.Year\")";
        var csharp = CompileExpr(code);

        Assert.Contains("DateTime.Now.Year", csharp);
    }

    [Fact]
    public void CSharp_WithInterpolation_SubstitutesExpression()
    {
        // Test interpolation with a simple expression
        var code = "(let [x 42] (csharp* \"~{x}.ToString()\"))";
        var forms = LispReader.ReadAll(code).ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());
        var csharp = _emitter.EmitScript(expr, "user");

        // The let-bound variable 'x' should appear in the output
        // (it's a local variable in the let binding)
        Assert.Contains("var x = 42L", csharp);
        Assert.Contains(".ToString()", csharp);
    }

    [Fact]
    public void CSharp_WithLiteralInterpolation_SubstitutesValue()
    {
        // Test interpolation with a literal expression
        var code = "(csharp* \"(~{42} + 1).ToString()\")";
        var forms = LispReader.ReadAll(code).ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());
        var csharp = _emitter.EmitScript(expr, "user");

        // 42 is emitted as 42L (long) in C#
        Assert.Contains("(42L + 1).ToString()", csharp);
    }

    [Fact]
    public void CSharp_InLet_ReturnsValue()
    {
        // csharp* in let body should be properly returned
        var code = "(let [x 42] (csharp* \"~{x}.ToString()\"))";
        var forms = LispReader.ReadAll(code).ToList();
        var expr = _analyzer.Analyze(forms[0], new AnalyzerContext());
        var csharp = _emitter.EmitScript(expr, "user");

        // In script mode, x is resolved through Var system, but the key is that
        // csharp* wraps the expression in parens and has a return statement
        Assert.Contains("return (", csharp);
        Assert.Contains(".ToString())", csharp);
    }

    [Fact]
    public void CSharp_InDefn_ReturnsValue()
    {
        // csharp* as function return should emit return statement
        var code = "(ns test.core) (defn year [] (csharp* \"DateTime.Now.Year\"))";
        var csharp = Compile(code);

        // Should emit: return (DateTime.Now.Year);
        Assert.Contains("return (DateTime.Now.Year);", csharp);
    }

    [Fact]
    public void CSharp_AsArgument_WrappedInParens()
    {
        // csharp* nested in function call should be wrapped
        var code = "(str \"Year: \" (csharp* \"DateTime.Now.Year\"))";
        var csharp = CompileExpr(code);

        // Should wrap expression in parens for proper evaluation
        Assert.Contains("(DateTime.Now.Year)", csharp);
    }

    #endregion

    #region defprotocol

    [Fact]
    public void Defprotocol_Simple_GeneratesInterface()
    {
        var code = "(ns test.core) (defprotocol IGreeter (greet [this name]))";
        var csharp = Compile(code);

        Assert.Contains("public interface IGreeter", csharp);
        Assert.Contains("greet(object? name)", csharp);
    }

    [Fact(Skip = "Phase 6: defprotocol typed methods not yet emitted")]
    public void Defprotocol_WithTypedMethods_GeneratesTypedInterface()
    {
        var code = "(ns test.core) (defprotocol IUserService (^Task<User> get-user [this ^String id]))";
        var csharp = Compile(code);

        Assert.Contains("public interface IUserService", csharp);
        Assert.Contains("Task<User> get_user(String id)", csharp);
    }

    [Fact(Skip = "Phase 6: defprotocol multiple methods not yet fully emitted")]
    public void Defprotocol_MultipleMethods_GeneratesAllMethods()
    {
        var code = @"(ns test.core)
                     (defprotocol IService
                       (start [this])
                       (stop [this])
                       (^String status [this]))";
        var csharp = Compile(code);

        Assert.Contains("start()", csharp);
        Assert.Contains("stop()", csharp);
        Assert.Contains("String status()", csharp);
    }

    #endregion

    #region deftype

    [Fact]
    public void Deftype_Simple_GeneratesClass()
    {
        var code = "(ns test.core) (deftype Point [x y])";
        var csharp = Compile(code);

        Assert.Contains("public class Point", csharp);
        Assert.Contains("public object? x { get; }", csharp);
        Assert.Contains("public object? y { get; }", csharp);
        Assert.Contains("public Point(object? x, object? y)", csharp);
    }

    [Fact]
    public void Deftype_WithTypedFields_GeneratesTypedClass()
    {
        var code = "(ns test.core) (deftype Point [^double x ^double y])";
        var csharp = Compile(code);

        Assert.Contains("public double x { get; }", csharp);
        Assert.Contains("public double y { get; }", csharp);
        Assert.Contains("public Point(double x, double y)", csharp);
    }

    [Fact]
    public void Deftype_WithInterface_ImplementsInterface()
    {
        var code = "(ns test.core) (deftype Greeter [] IGreeter (greet [this name] (str \"Hello \" name)))";
        var csharp = Compile(code);

        Assert.Contains("public class Greeter : IGreeter", csharp);
        Assert.Contains("public object? greet(object? name)", csharp);
    }

    [Fact]
    public void Deftype_WithTypedMethod_GeneratesTypedMethod()
    {
        var code = @"(ns test.core)
                     (deftype Calculator [^int value]
                       (^int add [this ^int n] (+ value n)))";
        var csharp = Compile(code);

        Assert.Contains("public int add(int n)", csharp);
    }

    #endregion

    #region defrecord

    [Fact]
    public void Defrecord_Simple_GeneratesRecord()
    {
        var code = "(ns test.core) (defrecord User [id name email])";
        var csharp = Compile(code);

        Assert.Contains("public record User(object? id, object? name, object? email)", csharp);
    }

    [Fact(Skip = "Phase 6: defrecord typed fields not yet emitted")]
    public void Defrecord_WithTypedFields_GeneratesTypedRecord()
    {
        var code = "(ns test.core) (defrecord User [^String id ^String name ^String email])";
        var csharp = Compile(code);

        Assert.Contains("public record User(String id, String name, String email)", csharp);
    }

    [Fact(Skip = "Phase 6: defrecord interface implementation not yet emitted")]
    public void Defrecord_WithInterface_ImplementsInterface()
    {
        var code = @"(ns test.core)
                     (defrecord ValidatedUser [^String id ^String name]
                       IValidatable
                       (validate [this] true))";
        var csharp = Compile(code);

        Assert.Contains("public record ValidatedUser(String id, String name) : IValidatable", csharp);
        Assert.Contains("validate()", csharp);
    }

    #endregion

    #region Attribute Support

    [Fact]
    public void Defrecord_WithAttribute_EmitsAttribute()
    {
        var code = "(ns test.core) (defrecord Counter [^{:attr [Parameter]} ^int CurrentCount])";
        var csharp = Compile(code);

        Assert.Contains("[Parameter]", csharp);
        Assert.Contains("public int CurrentCount { get; set; }", csharp);
    }

    [Fact(Skip = "Phase 6: defrecord attribute arguments not yet fully emitted")]
    public void Defrecord_WithAttributeArg_EmitsAttributeWithArg()
    {
        var code = "(ns test.core) (defrecord User [^{:attr [Required \"Name is required\"]} ^String name])";
        var csharp = Compile(code);

        Assert.Contains("[Required(\"Name is required\")]", csharp);
        Assert.Contains("public String name { get; set; }", csharp);
    }

    [Fact]
    public void Defrecord_WithMultipleAttributes_EmitsAllAttributes()
    {
        var code = "(ns test.core) (defrecord Service [^{:attr [Inject Required]} ^ILogger logger])";
        var csharp = Compile(code);

        Assert.Contains("[Inject]", csharp);
        Assert.Contains("[Required]", csharp);
        Assert.Contains("public ILogger logger { get; set; }", csharp);
    }

    [Fact(Skip = "Phase 6: defrecord mixed attribute fields not yet fully emitted")]
    public void Defrecord_MixedAttributeFields_EmitsCorrectly()
    {
        // Mix of fields with and without attributes
        var code = "(ns test.core) (defrecord Component [^{:attr [Parameter]} ^int Count ^String name])";
        var csharp = Compile(code);

        Assert.Contains("[Parameter]", csharp);
        Assert.Contains("public int Count { get; set; }", csharp);
        Assert.Contains("public String name { get; set; }", csharp);
    }

    [Fact]
    public void Deftype_WithAttribute_EmitsAttribute()
    {
        var code = "(ns test.core) (deftype Service [^{:attr [Inject]} ^ILogger logger])";
        var csharp = Compile(code);

        Assert.Contains("[Inject]", csharp);
        // When fields have attributes, use { get; set; } for Blazor compatibility
        Assert.Contains("public ILogger logger { get; set; }", csharp);
    }

    [Fact]
    public void Method_WithAttribute_EmitsAttribute()
    {
        var code = @"(ns test.core)
                     (defrecord Handler []
                       (^{:attr [HttpGet]} handle [this] ""OK""))";
        var csharp = Compile(code);

        Assert.Contains("[HttpGet]", csharp);
        Assert.Contains("public object? handle()", csharp);
    }

    [Fact]
    public void Defrecord_BlazorParameter_EmitsParameterAttribute()
    {
        // Real-world Blazor scenario
        var code = @"(ns test.core)
                     (defrecord Counter [^{:attr [Parameter]} ^int CurrentCount]
                       (IncrementCount [this] nil))";
        var csharp = Compile(code);

        Assert.Contains("public record Counter", csharp);
        Assert.Contains("[Parameter]", csharp);
        Assert.Contains("public int CurrentCount { get; set; }", csharp);
        Assert.Contains("IncrementCount()", csharp);
    }

    #endregion
}
