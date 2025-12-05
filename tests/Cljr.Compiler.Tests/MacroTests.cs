using Cljr.Compiler.Reader;
using Cljr.Compiler.Macros;
using Cljr.Compiler.Analyzer;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using Symbol = Cljr.Compiler.Reader.Symbol;

namespace Cljr.Compiler.Tests;

public class MacroExpanderTests
{
    private readonly MacroExpander _expander = new();

    [Fact]
    public void SyntaxQuote_SimpleLiteral_ReturnsQuoted()
    {
        // `42 -> (quote 42)
        var form = new PersistentList([Symbol.Parse("syntax-quote"), 42]);
        var result = _expander.Expand(form);

        Assert.IsType<PersistentList>(result);
        var list = (PersistentList)result!;
        Assert.Equal("quote", GetSymbolName(list[0]));
        Assert.Equal(42, list[1]);
    }

    // Helper to get symbol name regardless of which Symbol type it is
    // Uses reflection to handle potential assembly loading issues
    private static string? GetSymbolName(object? obj)
    {
        if (obj is null) return null;

        // Direct type check
        if (obj is Symbol sym)
            return sym.Name;
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Name;

        // Fallback: use reflection if type has Name property
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.Symbol" || type.FullName == "Cljr.Symbol")
        {
            var nameProp = type.GetProperty("Name");
            return nameProp?.GetValue(obj) as string;
        }

        return null;
    }

    [Fact]
    public void SyntaxQuote_Symbol_ReturnsQuotedSymbol()
    {
        // `foo -> (quote foo)
        var form = new PersistentList([Symbol.Parse("syntax-quote"), Symbol.Parse("foo")]);
        var result = _expander.Expand(form);

        Assert.IsType<PersistentList>(result);
        var list = (PersistentList)result!;
        Assert.Equal("quote", GetSymbolName(list[0]));
        Assert.NotNull(GetSymbolName(list[1])); // It's a symbol
    }

    [Fact]
    public void SyntaxQuote_List_ReturnsConcatForm()
    {
        // `(a b) -> (concat (list (quote a)) (list (quote b)))
        var form = new PersistentList([
            Symbol.Parse("syntax-quote"),
            new PersistentList([Symbol.Parse("a"), Symbol.Parse("b")])
        ]);

        var result = _expander.Expand(form);
        Assert.IsType<PersistentList>(result);

        // The result should be a concat form
        var list = (PersistentList)result!;
        Assert.Equal("concat", GetSymbolName(list[0]));
    }

    [Fact]
    public void SyntaxQuote_WithUnquote_InsertsValue()
    {
        // `(a ~b) -> (concat (list (quote a)) (list b))
        var form = new PersistentList([
            Symbol.Parse("syntax-quote"),
            new PersistentList([
                Symbol.Parse("a"),
                new PersistentList([Symbol.Parse("unquote"), Symbol.Parse("b")])
            ])
        ]);

        var result = _expander.Expand(form);
        Assert.IsType<PersistentList>(result);

        // The unquoted 'b' should appear without quote
        var resultStr = result!.ToString();
        Assert.Contains("concat", resultStr);
    }

    [Fact]
    public void SyntaxQuote_WithUnquoteSplicing_SplicesCollection()
    {
        // `(a ~@xs) -> (concat (list (quote a)) xs)
        var form = new PersistentList([
            Symbol.Parse("syntax-quote"),
            new PersistentList([
                Symbol.Parse("a"),
                new PersistentList([Symbol.Parse("unquote-splicing"), Symbol.Parse("xs")])
            ])
        ]);

        var result = _expander.Expand(form);
        Assert.IsType<PersistentList>(result);
    }

    [Fact]
    public void SyntaxQuote_Vector_ReturnsVecForm()
    {
        // `[a b] -> (vec (concat ...))
        var form = new PersistentList([
            Symbol.Parse("syntax-quote"),
            new PersistentVector([Symbol.Parse("a"), Symbol.Parse("b")])
        ]);

        var result = _expander.Expand(form);
        Assert.IsType<PersistentList>(result);
        var list = (PersistentList)result!;
        Assert.Equal("vec", GetSymbolName(list[0]));
    }

    [Fact]
    public void SyntaxQuote_EmptyList_ReturnsListCall()
    {
        // `() -> (list)
        var form = new PersistentList([
            Symbol.Parse("syntax-quote"),
            PersistentList.Empty
        ]);

        var result = _expander.Expand(form);
        Assert.IsType<PersistentList>(result);
        var list = (PersistentList)result!;
        Assert.Equal("list", GetSymbolName(list[0]));
    }

    [Fact]
    public void Gensym_GeneratesUniqueSymbols()
    {
        var sym1 = _expander.Gensym("test__");
        var sym2 = _expander.Gensym("test__");

        Assert.NotEqual(sym1.Name, sym2.Name);
        Assert.StartsWith("test__", sym1.Name);
    }
}

// Note: MacroInterpreterTests removed - MacroInterpreter replaced with compiled macros

public class DefmacroTests
{
    [Fact]
    public void Defmacro_RegistersMacro()
    {
        var expander = new MacroExpander();

        // (defmacro unless [test body] `(if (not ~test) ~body))
        var defmacroForm = new PersistentList([
            Symbol.Parse("defmacro"),
            Symbol.Parse("unless"),
            new PersistentVector([Symbol.Parse("test"), Symbol.Parse("body")]),
            new PersistentList([
                Symbol.Parse("syntax-quote"),
                new PersistentList([
                    Symbol.Parse("if"),
                    new PersistentList([
                        Symbol.Parse("not"),
                        new PersistentList([Symbol.Parse("unquote"), Symbol.Parse("test")])
                    ]),
                    new PersistentList([Symbol.Parse("unquote"), Symbol.Parse("body")])
                ])
            ])
        ]);

        expander.Expand(defmacroForm);
        Assert.True(expander.IsMacro("unless"));
    }
}

public class FutureMacroTests
{
    // Helper to get symbol name regardless of which Symbol type it is
    private static string? GetSymbolName(object? obj)
    {
        if (obj is null) return null;

        if (obj is Symbol sym)
            return sym.Name;
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Name;

        // Fallback: use reflection if type has Name property
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.Symbol" || type.FullName == "Cljr.Symbol")
        {
            var nameProp = type.GetProperty("Name");
            return nameProp?.GetValue(obj) as string;
        }

        return null;
    }

    // Helper to get symbol namespace regardless of which Symbol type it is
    private static string? GetSymbolNamespace(object? obj)
    {
        if (obj is null) return null;

        if (obj is Symbol sym)
            return sym.Namespace;
        if (obj is Cljr.Symbol runtimeSym)
            return runtimeSym.Namespace;

        // Fallback: use reflection if type has Namespace property
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.Symbol" || type.FullName == "Cljr.Symbol")
        {
            var nsProp = type.GetProperty("Namespace");
            return nsProp?.GetValue(obj) as string;
        }

        return null;
    }

    // Helper to check if an object is a Symbol type
    private static bool IsSymbol(object? obj)
    {
        if (obj is null) return false;
        if (obj is Symbol) return true;
        if (obj is Cljr.Symbol) return true;

        var typeName = obj.GetType().FullName;
        return typeName == "Cljr.Compiler.Reader.Symbol" || typeName == "Cljr.Symbol";
    }

    [Fact]
    public void FutureMacro_IsRegistered()
    {
        var expander = new MacroExpander();
        Assert.True(expander.IsMacro("future"));
    }

    [Fact]
    public void FutureMacro_ExpandsToFunctionCall()
    {
        var expander = new MacroExpander();

        // (future 42)
        var form = new PersistentList([Symbol.Parse("future"), 42]);
        var expanded = expander.Macroexpand(form);

        // Should expand to (cljr.core/future (fn [] 42))
        Assert.IsType<PersistentList>(expanded);
        var list = (PersistentList)expanded!;
        Assert.Equal(2, list.Count);

        // First element should be cljr.core/future symbol
        Assert.True(IsSymbol(list[0]), $"Expected symbol but got {list[0]?.GetType().FullName}");
        Assert.Equal("future", GetSymbolName(list[0]));
        Assert.Equal("cljr.core", GetSymbolNamespace(list[0]));

        // Second element should be (fn [] 42)
        Assert.IsType<PersistentList>(list[1]);
        var fnForm = (PersistentList)list[1]!;
        Assert.Equal("fn", GetSymbolName(fnForm[0]));
    }

    [Fact]
    public void FutureMacro_MultipleBody_WrapsInDo()
    {
        var expander = new MacroExpander();

        // (future (println "a") 42)
        var form = new PersistentList([
            Symbol.Parse("future"),
            new PersistentList([Symbol.Parse("println"), "a"]),
            42
        ]);
        var expanded = expander.Macroexpand(form);

        Assert.IsType<PersistentList>(expanded);
        var list = (PersistentList)expanded!;

        // The fn body should be (do (println "a") 42)
        var fnForm = (PersistentList)list[1]!;
        var body = fnForm[2]; // fn [] body
        Assert.IsType<PersistentList>(body);
        var doForm = (PersistentList)body!;
        Assert.Equal("do", GetSymbolName(doForm[0]));
    }
}

public class AnalyzerMacroIntegrationTests
{
    [Fact]
    public void Analyzer_ExpandsFutureMacro()
    {
        var analyzer = new Analyzer.Analyzer();
        var forms = LispReader.ReadAll("(future 42)").ToList();

        // Should not throw - future macro should expand
        var unit = analyzer.AnalyzeFile(forms);
        Assert.NotNull(unit);
    }

    [Fact]
    public void Analyzer_ExpandsSyntaxQuote()
    {
        var analyzer = new Analyzer.Analyzer();
        var forms = LispReader.ReadAll("`(foo bar)").ToList();

        // Should not throw - syntax-quote should expand
        var unit = analyzer.AnalyzeFile(forms);
        Assert.NotNull(unit);
    }

    [Fact]
    public void Analyzer_ThreadingMacros_AlreadyWork()
    {
        var analyzer = new Analyzer.Analyzer();

        // -> is already implemented in the analyzer
        var forms = LispReader.ReadAll("(-> 1 inc inc)").ToList();
        var unit = analyzer.AnalyzeFile(forms);
        Assert.NotNull(unit);
    }

    [Fact]
    public void Analyzer_CondMacro_AlreadyWorks()
    {
        var analyzer = new Analyzer.Analyzer();
        var forms = LispReader.ReadAll("(cond true 1 false 2 :else 3)").ToList();
        var unit = analyzer.AnalyzeFile(forms);
        Assert.NotNull(unit);
    }
}

/// <summary>
/// Tests for MacroContext and compiled macro system.
/// Validates that macros work in both REPL and source generator contexts.
/// </summary>
public class MacroContextTests
{
    [Fact]
    public void MacroContext_DefaultContext_HasNoExternalReferences()
    {
        var context = new MacroContext();

        Assert.Empty(context.ExternalReferences);
        Assert.Empty(context.UserAssemblies);
        Assert.False(context.IsSourceGeneratorContext);
    }

    [Fact]
    public void MacroContext_WithReferences_StoresReferences()
    {
        // Create a simple reference for testing
        var references = new List<Microsoft.CodeAnalysis.MetadataReference>
        {
            Microsoft.CodeAnalysis.MetadataReference.CreateFromFile(typeof(object).Assembly.Location)
        };

        var context = new MacroContext(references);

        Assert.Single(context.ExternalReferences);
        Assert.True(context.IsSourceGeneratorContext);
    }

    [Fact]
    public void UserDefinedMacro_WithSyntaxQuote_ExpandsCorrectly()
    {
        var expander = new MacroExpander();

        // Define a simple macro: (defmacro unless [test body] `(if (not ~test) ~body))
        var defmacroForm = new PersistentList([
            Symbol.Parse("defmacro"),
            Symbol.Parse("unless"),
            new PersistentVector([Symbol.Parse("test"), Symbol.Parse("body")]),
            new PersistentList([
                Symbol.Parse("syntax-quote"),
                new PersistentList([
                    Symbol.Parse("if"),
                    new PersistentList([
                        Symbol.Parse("not"),
                        new PersistentList([Symbol.Parse("unquote"), Symbol.Parse("test")])
                    ]),
                    new PersistentList([Symbol.Parse("unquote"), Symbol.Parse("body")])
                ])
            ])
        ]);

        // Register the macro
        expander.Expand(defmacroForm);
        Assert.True(expander.IsMacro("unless"));

        // Expand a call to the macro: (unless false 42)
        var callForm = new PersistentList([
            Symbol.Parse("unless"),
            false,
            42
        ]);

        var expanded = expander.Expand(callForm);

        // The result should be a list starting with 'if'
        Assert.IsType<PersistentList>(expanded);
        var list = (PersistentList)expanded!;

        // Find the 'if' in the expanded form
        // Due to macro expansion, it might be wrapped in concat/list calls
        var expandedStr = expanded.ToString();
        Assert.Contains("if", expandedStr);
        Assert.Contains("not", expandedStr);
    }

    [Fact]
    public void MacroExpander_Gensym_GeneratesUniqueSymbols()
    {
        // Test the Gensym method directly
        var expander = new MacroExpander();

        var sym1 = expander.Gensym("test__");
        var sym2 = expander.Gensym("test__");

        Assert.NotEqual(sym1.Name, sym2.Name);
        Assert.StartsWith("test__", sym1.Name);
        Assert.StartsWith("test__", sym2.Name);
    }

    [Fact]
    public void SyntaxQuote_Vector_ExpandsCorrectly()
    {
        var expander = new MacroExpander();

        // Syntax-quote a vector: `[a b]
        var form = new PersistentList([
            Symbol.Parse("syntax-quote"),
            new PersistentVector([Symbol.Parse("a"), Symbol.Parse("b")])
        ]);

        var expanded = expander.Expand(form);
        var expandedStr = expanded!.ToString();

        // The result should be a vec call wrapping list/concat
        Assert.Contains("vec", expandedStr);
        Assert.Contains("a", expandedStr);
        Assert.Contains("b", expandedStr);
    }

    [Fact]
    public void CompiledMacro_WithConditional_Works()
    {
        var expander = new MacroExpander();

        // Define a macro that uses if: (defmacro when-not [test & body] `(if (not ~test) (do ~@body)))
        var defmacroForm = new PersistentList([
            Symbol.Parse("defmacro"),
            Symbol.Parse("when-not"),
            new PersistentVector([Symbol.Parse("test"), Symbol.Parse("&"), Symbol.Parse("body")]),
            new PersistentList([
                Symbol.Parse("syntax-quote"),
                new PersistentList([
                    Symbol.Parse("if"),
                    new PersistentList([
                        Symbol.Parse("not"),
                        new PersistentList([Symbol.Parse("unquote"), Symbol.Parse("test")])
                    ]),
                    new PersistentList([
                        Symbol.Parse("do"),
                        new PersistentList([Symbol.Parse("unquote-splicing"), Symbol.Parse("body")])
                    ])
                ])
            ])
        ]);

        expander.Expand(defmacroForm);
        Assert.True(expander.IsMacro("when-not"));

        // Expand: (when-not false (println "a") (println "b"))
        var callForm = new PersistentList([
            Symbol.Parse("when-not"),
            false,
            new PersistentList([Symbol.Parse("println"), "a"]),
            new PersistentList([Symbol.Parse("println"), "b"])
        ]);

        var expanded = expander.Expand(callForm);
        var expandedStr = expanded!.ToString();

        Assert.Contains("if", expandedStr);
        Assert.Contains("not", expandedStr);
        Assert.Contains("do", expandedStr);
    }

    [Fact]
    public void MacroContext_GetAllReferences_IncludesRuntimeReferences()
    {
        var context = new MacroContext();
        var references = context.GetAllReferences();

        // In test context, should have runtime references from TRUSTED_PLATFORM_ASSEMBLIES
        Assert.NotEmpty(references);

        // Should include core .NET assemblies
        var refNames = references
            .Select(r => r.Display ?? "")
            .Where(d => !string.IsNullOrEmpty(d))
            .ToList();

        // System.Runtime or mscorlib should be present
        Assert.True(
            refNames.Any(n => n.Contains("System.Runtime") || n.Contains("mscorlib")),
            $"Expected System.Runtime or mscorlib in references, got: {string.Join(", ", refNames.Take(10))}"
        );
    }
}
