namespace Cljr.Repl;

/// <summary>
/// Configuration options for dev mode session
/// </summary>
public class DevModeOptions
{
    /// <summary>
    /// Paths to watch for file changes
    /// </summary>
    public List<string> WatchPaths { get; set; } = ["src"];

    /// <summary>
    /// Paths to search for source files
    /// </summary>
    public List<string> SourcePaths { get; set; } = ["src", "."];

    /// <summary>
    /// Whether to enable file watching
    /// </summary>
    public bool EnableWatching { get; set; } = true;

    /// <summary>
    /// Whether to auto-reload on file change
    /// </summary>
    public bool AutoReload { get; set; } = true;

    /// <summary>
    /// Initial namespace to load
    /// </summary>
    public string? InitialNamespace { get; set; }

    /// <summary>
    /// nREPL port (0 = auto-assign)
    /// </summary>
    public int Port { get; set; } = 0;

    /// <summary>
    /// Whether to print verbose reload messages
    /// </summary>
    public bool Verbose { get; set; } = false;
}

/// <summary>
/// Result of a namespace reload operation
/// </summary>
public class ReloadResult
{
    public string Namespace { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public List<string> ReloadedDependents { get; init; } = [];

    public static ReloadResult Succeeded(string ns, TimeSpan duration, List<string>? dependents = null) =>
        new()
        {
            Namespace = ns,
            Success = true,
            Duration = duration,
            ReloadedDependents = dependents ?? []
        };

    public static ReloadResult Failed(string ns, string error) =>
        new()
        {
            Namespace = ns,
            Success = false,
            Error = error
        };
}

/// <summary>
/// Event args for reload events
/// </summary>
public class ReloadEventArgs : EventArgs
{
    public string Namespace { get; }
    public bool Success { get; }
    public string? Error { get; }
    public string? FilePath { get; }

    public ReloadEventArgs(string ns, bool success, string? error = null, string? filePath = null)
    {
        Namespace = ns;
        Success = success;
        Error = error;
        FilePath = filePath;
    }
}
