namespace Cljr;

/// <summary>
/// Represents the result of running a test in REPL mode.
/// Used by deftest forms when evaluated in the REPL.
/// </summary>
public class TestRunResult
{
    public List<TestFailure> Failures { get; } = new();
    public int PassCount { get; set; }
    public int FailCount { get; set; }
}

/// <summary>
/// Represents a single test failure.
/// </summary>
public class TestFailure
{
    public object? Expected { get; init; }
    public object? Actual { get; init; }
    public string? Message { get; init; }
}
