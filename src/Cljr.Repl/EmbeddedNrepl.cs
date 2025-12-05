using System.Reflection;

namespace Cljr.Repl;

/// <summary>
/// Simple embeddable nREPL server for user applications.
/// Use this to enable REPL-driven development in your apps.
/// </summary>
/// <example>
/// // In your app's startup:
/// var nrepl = EmbeddedNrepl.Start(7888);
/// Console.WriteLine($"nREPL running on port {nrepl.Port}");
///
/// // Later, to stop:
/// nrepl.Stop();
/// </example>
public static class EmbeddedNrepl
{
    /// <summary>
    /// Start an embedded nREPL server using assemblies from the current AppDomain.
    /// The server runs in the background and returns immediately.
    /// </summary>
    /// <param name="port">Port to listen on. Use 0 for auto-assigned port.</param>
    /// <param name="onLog">Optional logging callback. Default: Console.WriteLine</param>
    /// <returns>The running NreplServer instance</returns>
    public static NreplServer Start(long port = 0, Action<string>? onLog = null)
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        return Start(port, assemblies, onLog: onLog);
    }

    /// <summary>
    /// Start an embedded nREPL server with specific assemblies.
    /// </summary>
    /// <param name="port">Port to listen on. Use 0 for auto-assigned port.</param>
    /// <param name="assemblies">Assemblies to make available in the REPL</param>
    /// <param name="sourcePaths">Paths to search for .cljr source files</param>
    /// <param name="onLog">Optional logging callback</param>
    /// <returns>The running NreplServer instance</returns>
    public static NreplServer Start(
        long port,
        Assembly[] assemblies,
        string[]? sourcePaths = null,
        Action<string>? onLog = null)
    {
        var options = new NreplServerOptions
        {
            OnLog = onLog ?? Console.WriteLine,
            WritePortFile = false // Don't pollute user's app directory
        };

        // Create session factory that uses the provided assemblies
        Func<NreplSession> sessionFactory = () => new NreplSession(assemblies, sourcePaths);

        var server = new NreplServer((int)port, sessionFactory, options);
        server.StartInBackground();

        return server;
    }

    /// <summary>
    /// Start an embedded nREPL server with minimal logging (silent mode).
    /// </summary>
    /// <param name="port">Port to listen on. Use 0 for auto-assigned port.</param>
    /// <returns>The running NreplServer instance</returns>
    public static NreplServer StartSilent(int port = 0)
    {
        return Start(port, onLog: null);
    }
}
