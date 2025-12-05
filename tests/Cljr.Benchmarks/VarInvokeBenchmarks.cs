using BenchmarkDotNet.Attributes;

namespace Cljr.Benchmarks;

/// <summary>
/// Benchmarks for Var.Invoke overhead.
/// The current implementation uses params object?[] which boxes arguments.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class VarInvokeBenchmarks
{
    private Var _identityVar = null!;
    private Var _addVar = null!;
    private Var _strVar = null!;
    private Func<object?, object?> _identityDirect = null!;
    private Func<object?, object?, object?> _addDirect = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Create vars with simple functions - using runtime Var.Intern
        _identityVar = Var.Intern("bench", "identity-fn");
        _identityVar.BindRoot(new IdentityFn());

        _addVar = Var.Intern("bench", "add-fn");
        _addVar.BindRoot(new AddFn());

        _strVar = Var.Intern("bench", "str-fn");
        _strVar.BindRoot(new StrFn());

        // Direct delegates for comparison
        _identityDirect = x => x;
        _addDirect = (a, b) => (long)a! + (long)b!;
    }

    // Direct delegate calls (no Var overhead)
    [Benchmark(Baseline = true)]
    public object? DirectDelegate_Identity() => _identityDirect(42L);

    [Benchmark]
    public object? DirectDelegate_Add() => _addDirect(1L, 2L);

    // Var.Invoke with params array (current implementation)
    [Benchmark]
    public object? VarInvoke_Identity() => _identityVar.Invoke(42L);

    [Benchmark]
    public object? VarInvoke_Add() => _addVar.Invoke(1L, 2L);

    // Var.Invoke with more args (more boxing)
    [Benchmark]
    public object? VarInvoke_Str_3Args() => _strVar.Invoke("Hello", " ", "World");

    [Benchmark]
    public object? VarInvoke_Str_5Args() => _strVar.Invoke("a", "b", "c", "d", "e");

    // Measure the IFn interface dispatch overhead
    [Benchmark]
    public object? IFnCast_Then_Invoke()
    {
        var fn = (IFn)_identityVar.Deref()!;
        return fn.Invoke(42L);
    }

    // Baseline: Pure C# method call
    [Benchmark]
    public long PureCSharp_Add() => Add(1L, 2L);

    private static long Add(long a, long b) => a + b;

    // Simple IFn implementations for testing
    private sealed class IdentityFn : IFn
    {
        public object? Invoke(params object?[] args) => args[0];
        public object? Invoke() => null;
        public object? Invoke(object? a) => a;
        public object? Invoke(object? a, object? b) => a;
        public object? Invoke(object? a, object? b, object? c) => a;
        public object? Invoke(object? a, object? b, object? c, object? d) => a;
    }

    private sealed class AddFn : IFn
    {
        public object? Invoke(params object?[] args) => (long)args[0]! + (long)args[1]!;
        public object? Invoke() => 0L;
        public object? Invoke(object? a) => a;
        public object? Invoke(object? a, object? b) => (long)a! + (long)b!;
        public object? Invoke(object? a, object? b, object? c) => (long)a! + (long)b! + (long)c!;
        public object? Invoke(object? a, object? b, object? c, object? d) => (long)a! + (long)b! + (long)c! + (long)d!;
    }

    private sealed class StrFn : IFn
    {
        public object? Invoke(params object?[] args) => string.Concat(args.Select(a => a?.ToString() ?? ""));
        public object? Invoke() => "";
        public object? Invoke(object? a) => a?.ToString() ?? "";
        public object? Invoke(object? a, object? b) => $"{a}{b}";
        public object? Invoke(object? a, object? b, object? c) => $"{a}{b}{c}";
        public object? Invoke(object? a, object? b, object? c, object? d) => $"{a}{b}{c}{d}";
    }
}
