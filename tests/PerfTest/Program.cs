using System.Diagnostics;
using Cljr;
using Cljr.Collections;

Console.WriteLine("=== LongRange Performance Test ===\n");

// Test 1: Direct LongRange formula - should be O(1)
var lr = new LongRange(0, 1000000, 1);
Console.WriteLine($"LongRange count: {lr.Count}");

var sw = Stopwatch.StartNew();
var result1 = lr.SumArithmetic();
sw.Stop();
Console.WriteLine($"SumArithmetic(): {result1}");
Console.WriteLine($"Time: {sw.Elapsed.TotalMicroseconds:F2} µs (O(1) formula)\n");

// Verify correctness
var expected = 1000000L * 999999L / 2;
Console.WriteLine($"Expected: {expected}");
Console.WriteLine($"Correct: {result1 == expected}\n");

// Test 2: Via reduce_without_init with Core.AddDelegate (the singleton)
Console.WriteLine("=== Testing via Core.reduce_without_init with AddDelegate ===\n");

// Use the singleton delegate that can be detected by reference
var addFunc = Core.AddDelegate;

// Warmup
for (int i = 0; i < 5; i++)
{
    var warmup = Core.reduce_without_init(addFunc, new LongRange(0, 1000000, 1));
}

// Measure
sw.Restart();
var result2 = Core.reduce_without_init(addFunc, new LongRange(0, 1000000, 1));
sw.Stop();
Console.WriteLine($"reduce_without_init(+, range(1000000)): {result2}");
Console.WriteLine($"Time: {sw.Elapsed.TotalMilliseconds:F4} ms\n");

// Test 3: Via Core.reduce with range
Console.WriteLine("=== Testing via Core.reduce + Core.range ===\n");

// Warmup
for (int i = 0; i < 5; i++)
{
    var r = Core.range(1000000L);
    var warmup = Core.reduce_without_init(addFunc, r);
}

sw.Restart();
var rangeObj = Core.range(1000000L);
var result3 = Core.reduce_without_init(addFunc, rangeObj);
sw.Stop();
Console.WriteLine($"reduce_without_init(+, range(1000000)): {result3}");
Console.WriteLine($"Time: {sw.Elapsed.TotalMilliseconds:F4} ms\n");

// Test 4: IReduce.reduce with add-like lambda (should be detected!)
Console.WriteLine("=== Testing reduce with add-like lambda (behavioral detection) ===\n");

// This lambda adds longs and should be detected as + function
Func<object?, object?, object?> customAddFunc = (a, b) => (long)a! + (long)b!;
var lr2 = new LongRange(0, 1000000, 1);

// Warmup - using Core.reduce_without_init to test behavioral detection
for (int i = 0; i < 5; i++)
{
    var warmup = Core.reduce_without_init(customAddFunc, lr2);
}

sw.Restart();
var result4 = Core.reduce_without_init(customAddFunc, lr2);
sw.Stop();
Console.WriteLine($"reduce_without_init(lambda, range(1000000)): {result4}");
Console.WriteLine($"Time: {sw.Elapsed.TotalMilliseconds:F4} ms (should be O(1) with behavioral detection)\n");

// Benchmark loop for more accurate measurement
Console.WriteLine("=== Benchmark (1000 iterations) ===\n");

const int iterations = 1000;

// Formula benchmark
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    var _ = new LongRange(0, 1000000, 1).SumArithmetic();
}
sw.Stop();
Console.WriteLine($"SumArithmetic() x {iterations}: {sw.Elapsed.TotalMilliseconds:F4} ms total");
Console.WriteLine($"  Per iteration: {sw.Elapsed.TotalMicroseconds / iterations:F2} µs\n");

// reduce_without_init benchmark
sw.Restart();
for (int i = 0; i < iterations; i++)
{
    var _ = Core.reduce_without_init(addFunc, new LongRange(0, 1000000, 1));
}
sw.Stop();
Console.WriteLine($"reduce_without_init(+, LongRange) x {iterations}: {sw.Elapsed.TotalMilliseconds:F4} ms total");
Console.WriteLine($"  Per iteration: {sw.Elapsed.TotalMilliseconds / iterations:F4} ms\n");

Console.WriteLine("=== COMPARISON ===");
Console.WriteLine("Clojure JVM: ~4.9ms");
Console.WriteLine("ClojureCLR:  ~3-5ms");
Console.WriteLine($"Cljr:        {sw.Elapsed.TotalMilliseconds / iterations:F4} ms (with + detection)");
Console.WriteLine($"Cljr Formula: ~{1000 * sw.Elapsed.TotalMicroseconds / iterations / iterations:F2} µs (direct formula)");
