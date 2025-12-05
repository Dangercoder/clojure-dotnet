using BenchmarkDotNet.Attributes;
using Cljr.Compiler.Reader;

namespace Cljr.Benchmarks;

/// <summary>
/// Benchmarks for the Clojure reader/parser.
/// Measures string parsing throughput - a key area for Span&lt;T&gt; optimization.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class ReaderBenchmarks
{
    private string _simpleSymbol = null!;
    private string _qualifiedSymbol = null!;
    private string _keyword = null!;
    private string _qualifiedKeyword = null!;
    private string _smallList = null!;
    private string _mediumList = null!;
    private string _vector = null!;
    private string _map = null!;
    private string _nestedForm = null!;
    private string _stringLiteral = null!;
    private string _longString = null!;
    private string _multipleFormsCode = null!;

    [GlobalSetup]
    public void Setup()
    {
        _simpleSymbol = "my-symbol";
        _qualifiedSymbol = "clojure.core/map";
        _keyword = ":my-keyword";
        _qualifiedKeyword = ":clojure.core/type";
        _smallList = "(+ 1 2 3)";
        _mediumList = "(defn factorial [n] (if (<= n 1) 1 (* n (factorial (dec n)))))";
        _vector = "[1 2 3 4 5 6 7 8 9 10]";
        _map = "{:name \"Alice\" :age 30 :city \"NYC\" :active true}";
        _nestedForm = "(let [x (+ 1 2)] (if (> x 2) (println \"yes\") (println \"no\")))";
        _stringLiteral = "\"Hello, World!\"";
        _longString = "\"" + new string('a', 1000) + "\"";

        // Multiple forms for bulk parsing
        _multipleFormsCode = string.Join("\n", Enumerable.Range(0, 100)
            .Select(i => $"(defn func{i} [x] (+ x {i}))"));
    }

    // Symbol parsing
    [Benchmark]
    public object? Parse_SimpleSymbol() => new LispReader(_simpleSymbol).Read();

    [Benchmark]
    public object? Parse_QualifiedSymbol() => new LispReader(_qualifiedSymbol).Read();

    // Keyword parsing
    [Benchmark]
    public object? Parse_Keyword() => new LispReader(_keyword).Read();

    [Benchmark]
    public object? Parse_QualifiedKeyword() => new LispReader(_qualifiedKeyword).Read();

    // Number parsing
    [Benchmark]
    public object? Parse_Integer() => new LispReader("42").Read();

    [Benchmark]
    public object? Parse_Long() => new LispReader("9223372036854775807").Read();

    [Benchmark]
    public object? Parse_Double() => new LispReader("3.14159265358979").Read();

    // String parsing (key optimization target for Span<T>)
    [Benchmark]
    public object? Parse_ShortString() => new LispReader(_stringLiteral).Read();

    [Benchmark]
    public object? Parse_LongString() => new LispReader(_longString).Read();

    // Collection parsing
    [Benchmark]
    public object? Parse_SmallList() => new LispReader(_smallList).Read();

    [Benchmark]
    public object? Parse_MediumList() => new LispReader(_mediumList).Read();

    [Benchmark]
    public object? Parse_Vector() => new LispReader(_vector).Read();

    [Benchmark]
    public object? Parse_Map() => new LispReader(_map).Read();

    [Benchmark]
    public object? Parse_NestedForm() => new LispReader(_nestedForm).Read();

    // Bulk parsing (realistic workload)
    [Benchmark]
    public int Parse_MultipleForms()
    {
        int count = 0;
        foreach (var _ in LispReader.ReadAll(_multipleFormsCode))
            count++;
        return count;
    }

    // Character escape parsing (tests ReadUnicodeEscape)
    [Benchmark]
    public object? Parse_StringWithEscapes() => new LispReader("\"Hello\\nWorld\\t\\\"Quoted\\\"\"").Read();
}
