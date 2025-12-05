namespace Cljr.Repl;

/// <summary>
/// Configuration options for NreplServer.
/// Use these to customize server behavior when embedding nREPL in user applications.
/// </summary>
public class NreplServerOptions
{
    /// <summary>
    /// Optional logging callback. If null, no logging is performed.
    /// Use this instead of Console.WriteLine for embedded scenarios.
    /// </summary>
    public Action<string>? OnLog { get; set; }

    /// <summary>
    /// Whether to write a .nrepl-port file in the current directory.
    /// Default: true for CLI usage, set to false for embedded usage.
    /// </summary>
    public bool WritePortFile { get; set; } = true;

    /// <summary>
    /// Path for the .nrepl-port file. Default: ".nrepl-port"
    /// </summary>
    public string PortFilePath { get; set; } = ".nrepl-port";

    /// <summary>
    /// Default options for CLI usage (logging to console, writes port file)
    /// </summary>
    public static NreplServerOptions Default => new()
    {
        OnLog = Console.WriteLine,
        WritePortFile = true
    };

    /// <summary>
    /// Options for embedded usage (no logging, no port file)
    /// </summary>
    public static NreplServerOptions Embedded => new()
    {
        OnLog = null,
        WritePortFile = false
    };
}
