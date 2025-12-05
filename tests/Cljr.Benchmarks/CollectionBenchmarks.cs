using BenchmarkDotNet.Attributes;
using Cljr.Collections;

namespace Cljr.Benchmarks;

/// <summary>
/// Benchmarks for persistent collection operations.
/// Measures PersistentVector and PersistentHashMap performance.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
public class CollectionBenchmarks
{
    private PersistentVector _smallVector = null!;
    private PersistentVector _mediumVector = null!;
    private PersistentVector _largeVector = null!;
    private PersistentHashMap _smallMap = null!;
    private PersistentHashMap _mediumMap = null!;
    private PersistentHashMap _largeMap = null!;
    private Keyword _testKey = null!;

    [GlobalSetup]
    public void Setup()
    {
        // Create vectors of different sizes
        _smallVector = PersistentVector.Empty;
        for (int i = 0; i < 10; i++)
            _smallVector = (PersistentVector)_smallVector.Conj(i);

        _mediumVector = PersistentVector.Empty;
        for (int i = 0; i < 1000; i++)
            _mediumVector = (PersistentVector)_mediumVector.Conj(i);

        _largeVector = PersistentVector.Empty;
        for (int i = 0; i < 100000; i++)
            _largeVector = (PersistentVector)_largeVector.Conj(i);

        // Create maps of different sizes
        _smallMap = PersistentHashMap.Empty;
        for (int i = 0; i < 10; i++)
            _smallMap = (PersistentHashMap)_smallMap.Assoc(Keyword.Intern($"key{i}"), i);

        _mediumMap = PersistentHashMap.Empty;
        for (int i = 0; i < 1000; i++)
            _mediumMap = (PersistentHashMap)_mediumMap.Assoc(Keyword.Intern($"key{i}"), i);

        _largeMap = PersistentHashMap.Empty;
        for (int i = 0; i < 100000; i++)
            _largeMap = (PersistentHashMap)_largeMap.Assoc(Keyword.Intern($"key{i}"), i);

        _testKey = Keyword.Intern("key500");
    }

    // Vector Conj (append)
    [Benchmark]
    public object Vector_Conj_Small() => _smallVector.Conj(42);

    [Benchmark]
    public object Vector_Conj_Medium() => _mediumVector.Conj(42);

    [Benchmark]
    public object Vector_Conj_Large() => _largeVector.Conj(42);

    // Vector Nth (random access)
    [Benchmark]
    public object? Vector_Nth_Small() => _smallVector.Nth(5);

    [Benchmark]
    public object? Vector_Nth_Medium() => _mediumVector.Nth(500);

    [Benchmark]
    public object? Vector_Nth_Large() => _largeVector.Nth(50000);

    // Vector AssocN (update)
    [Benchmark]
    public object Vector_AssocN_Small() => _smallVector.AssocN(5, 999);

    [Benchmark]
    public object Vector_AssocN_Medium() => _mediumVector.AssocN(500, 999);

    [Benchmark]
    public object Vector_AssocN_Large() => _largeVector.AssocN(50000, 999);

    // Vector Pop
    [Benchmark]
    public object Vector_Pop_Small() => _smallVector.Pop();

    [Benchmark]
    public object Vector_Pop_Medium() => _mediumVector.Pop();

    [Benchmark]
    public object Vector_Pop_Large() => _largeVector.Pop();

    // Map Assoc (add/update)
    [Benchmark]
    public object Map_Assoc_Small() => _smallMap.Assoc(Keyword.Intern("newkey"), 42);

    [Benchmark]
    public object Map_Assoc_Medium() => _mediumMap.Assoc(Keyword.Intern("newkey"), 42);

    [Benchmark]
    public object Map_Assoc_Large() => _largeMap.Assoc(Keyword.Intern("newkey"), 42);

    // Map ValAt (lookup)
    [Benchmark]
    public object? Map_ValAt_Small() => _smallMap.ValAt(Keyword.Intern("key5"));

    [Benchmark]
    public object? Map_ValAt_Medium() => _mediumMap.ValAt(_testKey);

    [Benchmark]
    public object? Map_ValAt_Large() => _largeMap.ValAt(_testKey);

    // Map Without (dissoc)
    [Benchmark]
    public object Map_Without_Small() => _smallMap.Without(Keyword.Intern("key5"));

    [Benchmark]
    public object Map_Without_Medium() => _mediumMap.Without(_testKey);

    [Benchmark]
    public object Map_Without_Large() => _largeMap.Without(_testKey);

    // Transient operations (batch insert)
    [Benchmark]
    public object Vector_Transient_Build_1000()
    {
        var t = PersistentVector.Empty.AsTransient();
        for (int i = 0; i < 1000; i++)
            t = (ITransientVector)t.Conj(i);
        return t.Persistent();
    }

    [Benchmark]
    public object Map_Transient_Build_1000()
    {
        var t = (ITransientMap)PersistentHashMap.Empty.AsTransient();
        for (int i = 0; i < 1000; i++)
            t = t.Assoc(Keyword.Intern($"k{i}"), i);
        return t.Persistent();
    }
}
