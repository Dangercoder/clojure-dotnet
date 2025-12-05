using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Cljr.Repl;

/// <summary>
/// nREPL server implementation for CIDER/Calva compatibility
/// </summary>
public class NreplServer
{
    private readonly TcpListener _listener;
    private readonly ConcurrentDictionary<string, NreplSession> _sessions = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<NreplSession>? _sessionFactory;
    private readonly NreplServerOptions _options;
    private Task? _runTask;

    public int Port { get; }

    public NreplServer(int port = 0) : this(port, null, null)
    {
    }

    public NreplServer(int port, Func<NreplSession>? sessionFactory) : this(port, sessionFactory, null)
    {
    }

    public NreplServer(int port, Func<NreplSession>? sessionFactory, NreplServerOptions? options)
    {
        _sessionFactory = sessionFactory;
        _options = options ?? NreplServerOptions.Default;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
    }

    /// <summary>
    /// Start the server in the background and return immediately.
    /// Use this for embedding nREPL in applications.
    /// </summary>
    public void StartInBackground()
    {
        _runTask = Task.Run(RunAsync);
    }

    public async Task RunAsync()
    {
        _options.OnLog?.Invoke($"nREPL server started on port {Port}");
        _options.OnLog?.Invoke($"Connect with: clj -Sdeps '{{:deps {{nrepl/nrepl {{:mvn/version \"1.0.0\"}}}}}}' -m nrepl.cmdline -c -p {Port}");

        // Write .nrepl-port file if enabled
        if (_options.WritePortFile)
        {
            await File.WriteAllTextAsync(_options.PortFilePath, Port.ToString());
        }

        try
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        finally
        {
            _listener.Stop();
            if (_options.WritePortFile && File.Exists(_options.PortFilePath))
                File.Delete(_options.PortFilePath);
        }
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        await using (var stream = client.GetStream())
        {
            try
            {
                while (client.Connected && !_cts.Token.IsCancellationRequested)
                {
                    var message = Bencode.Decode(stream) as Dictionary<string, object?>;
                    if (message == null) break;

                    var responses = await HandleMessageAsync(message);
                    foreach (var response in responses)
                    {
                        var bytes = Bencode.Encode(response);
                        await stream.WriteAsync(bytes, _cts.Token);
                    }
                }
            }
            catch (Exception ex)
            {
                _options.OnLog?.Invoke($"Client error: {ex.Message}");
            }
        }
    }

    private async Task<List<Dictionary<string, object?>>> HandleMessageAsync(Dictionary<string, object?> message)
    {
        var op = message.GetValueOrDefault("op") as string;
        var id = message.GetValueOrDefault("id") as string ?? Guid.NewGuid().ToString();
        var sessionId = message.GetValueOrDefault("session") as string;

        var responses = new List<Dictionary<string, object?>>();

        switch (op)
        {
            case "clone":
                responses.Add(HandleClone(id, sessionId));
                break;

            case "close":
                responses.Add(HandleClose(id, sessionId));
                break;

            case "describe":
                responses.Add(HandleDescribe(id, sessionId));
                break;

            case "eval":
                var code = message.GetValueOrDefault("code") as string ?? "";
                responses.AddRange(await HandleEvalAsync(id, sessionId, code));
                break;

            case "interrupt":
                responses.Add(HandleInterrupt(id, sessionId));
                break;

            case "ls-sessions":
                responses.Add(HandleLsSessions(id));
                break;

            case "load-file":
                var file = message.GetValueOrDefault("file") as string ?? "";
                responses.AddRange(await HandleEvalAsync(id, sessionId, file));
                break;

            case "completions":
                responses.Add(HandleCompletions(id, sessionId, message));
                break;

            case "cljr/reload":
                responses.Add(await HandleReloadAsync(id, sessionId, message));
                break;

            case "cljr/reload-all":
                responses.Add(await HandleReloadAllAsync(id, sessionId));
                break;

            case "cljr/watch-start":
                responses.Add(HandleWatchStart(id, sessionId));
                break;

            case "cljr/watch-stop":
                responses.Add(HandleWatchStop(id, sessionId));
                break;

            default:
                responses.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["session"] = sessionId ?? "",
                    ["status"] = new List<object?> { "error", "unknown-op", "done" }
                });
                break;
        }

        return responses;
    }

    private Dictionary<string, object?> HandleClone(string id, string? sessionId)
    {
        var newSession = _sessionFactory?.Invoke() ?? new NreplSession();
        _sessions[newSession.Id] = newSession;

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = sessionId ?? "",
            ["new-session"] = newSession.Id,
            ["status"] = new List<object?> { "done" }
        };
    }

    private Dictionary<string, object?> HandleClose(string id, string? sessionId)
    {
        if (sessionId != null)
        {
            _sessions.TryRemove(sessionId, out _);
        }

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = sessionId ?? "",
            ["status"] = new List<object?> { "done" }
        };
    }

    private Dictionary<string, object?> HandleDescribe(string id, string? sessionId)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = sessionId ?? "",
            ["ops"] = new Dictionary<string, object?>
            {
                ["clone"] = new Dictionary<string, object?>(),
                ["close"] = new Dictionary<string, object?>(),
                ["describe"] = new Dictionary<string, object?>(),
                ["eval"] = new Dictionary<string, object?>
                {
                    ["doc"] = "Evaluate code",
                    ["requires"] = new Dictionary<string, object?> { ["code"] = "The code to evaluate" }
                },
                ["interrupt"] = new Dictionary<string, object?>(),
                ["ls-sessions"] = new Dictionary<string, object?>(),
                ["load-file"] = new Dictionary<string, object?>(),
                ["completions"] = new Dictionary<string, object?>(),
                ["cljr/reload"] = new Dictionary<string, object?>
                {
                    ["doc"] = "Reload a namespace from disk (dev mode only)",
                    ["requires"] = new Dictionary<string, object?> { ["ns"] = "The namespace to reload" }
                },
                ["cljr/reload-all"] = new Dictionary<string, object?>
                {
                    ["doc"] = "Reload all loaded namespaces (dev mode only)"
                },
                ["cljr/watch-start"] = new Dictionary<string, object?>
                {
                    ["doc"] = "Start file watching for auto-reload (dev mode only)"
                },
                ["cljr/watch-stop"] = new Dictionary<string, object?>
                {
                    ["doc"] = "Stop file watching (dev mode only)"
                }
            },
            ["versions"] = new Dictionary<string, object?>
            {
                ["cljr"] = new Dictionary<string, object?> { ["version-string"] = "0.1.0" },
                ["nrepl"] = new Dictionary<string, object?> { ["version-string"] = "1.0.0" }
            },
            ["status"] = new List<object?> { "done" }
        };
    }

    private async Task<List<Dictionary<string, object?>>> HandleEvalAsync(string id, string? sessionId, string code)
    {
        var responses = new List<Dictionary<string, object?>>();

        var session = GetOrCreateSession(sessionId);
        var result = await session.EvalAsync(code);

        // Send output if any
        if (!string.IsNullOrEmpty(result.Output))
        {
            responses.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["out"] = result.Output
            });
        }

        // Send error if any
        if (!string.IsNullOrEmpty(result.Error))
        {
            responses.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["err"] = result.Error + "\n"
            });
            responses.Add(new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["ex"] = result.Error,
                ["status"] = new List<object?> { "eval-error" }
            });
        }

        // Send values
        if (result.Values != null)
        {
            foreach (var value in result.Values)
            {
                responses.Add(new Dictionary<string, object?>
                {
                    ["id"] = id,
                    ["session"] = session.Id,
                    ["value"] = Core.PrStr(value),
                    ["ns"] = result.Namespace ?? "user"
                });
            }
        }

        // Done
        responses.Add(new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = session.Id,
            ["status"] = new List<object?> { "done" }
        });

        return responses;
    }

    private Dictionary<string, object?> HandleInterrupt(string id, string? sessionId)
    {
        if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
        {
            session.Interrupt();
        }

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = sessionId ?? "",
            ["status"] = new List<object?> { "done" }
        };
    }

    private Dictionary<string, object?> HandleLsSessions(string id)
    {
        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["sessions"] = _sessions.Keys.ToList<object?>(),
            ["status"] = new List<object?> { "done" }
        };
    }

    private Dictionary<string, object?> HandleCompletions(string id, string? sessionId, Dictionary<string, object?> message)
    {
        // Basic completions - return core functions
        var prefix = message.GetValueOrDefault("prefix") as string ?? "";
        var completions = GetCoreCompletions(prefix);

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = sessionId ?? "",
            ["completions"] = completions.Select(c => new Dictionary<string, object?>
            {
                ["candidate"] = c,
                ["type"] = "function"
            } as object).ToList<object?>(),
            ["status"] = new List<object?> { "done" }
        };
    }

    private NreplSession GetOrCreateSession(string? sessionId)
    {
        if (sessionId != null && _sessions.TryGetValue(sessionId, out var session))
        {
            session.Touch();
            return session;
        }

        var newSession = _sessionFactory?.Invoke() ?? new NreplSession();
        _sessions[newSession.Id] = newSession;
        return newSession;
    }

    private async Task<Dictionary<string, object?>> HandleReloadAsync(string id, string? sessionId, Dictionary<string, object?> message)
    {
        var session = GetOrCreateSession(sessionId);
        var ns = message.GetValueOrDefault("ns") as string;

        if (session is not DevModeSession devSession)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["err"] = "reload only available in dev mode\n",
                ["status"] = new List<object?> { "error", "done" }
            };
        }

        if (string.IsNullOrEmpty(ns))
        {
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["err"] = "ns parameter required\n",
                ["status"] = new List<object?> { "error", "done" }
            };
        }

        var result = await devSession.ReloadNamespaceAsync(ns);

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = session.Id,
            ["value"] = result.Success ? $":ok ({result.Duration.TotalMilliseconds:F0}ms)" : $":error {result.Error}",
            ["reloaded"] = result.Success ? new List<object?> { ns }.Concat(result.ReloadedDependents).ToList<object?>() : null,
            ["status"] = new List<object?> { result.Success ? "done" : "error", "done" }
        };
    }

    private async Task<Dictionary<string, object?>> HandleReloadAllAsync(string id, string? sessionId)
    {
        var session = GetOrCreateSession(sessionId);

        if (session is not DevModeSession devSession)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["err"] = "reload-all only available in dev mode\n",
                ["status"] = new List<object?> { "error", "done" }
            };
        }

        var results = await devSession.ReloadAllAsync();
        var reloaded = results.Where(r => r.Success).Select(r => r.Namespace).ToList<object?>();
        var errors = results.Where(r => !r.Success).Select(r => $"{r.Namespace}: {r.Error}").ToList();

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = session.Id,
            ["value"] = errors.Count == 0 ? $":ok ({reloaded.Count} namespaces)" : $":error {string.Join(", ", errors)}",
            ["reloaded"] = reloaded,
            ["status"] = new List<object?> { "done" }
        };
    }

    private Dictionary<string, object?> HandleWatchStart(string id, string? sessionId)
    {
        var session = GetOrCreateSession(sessionId);

        if (session is not DevModeSession devSession)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["err"] = "watch only available in dev mode\n",
                ["status"] = new List<object?> { "error", "done" }
            };
        }

        devSession.StartWatching();

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = session.Id,
            ["value"] = ":watching",
            ["status"] = new List<object?> { "done" }
        };
    }

    private Dictionary<string, object?> HandleWatchStop(string id, string? sessionId)
    {
        var session = GetOrCreateSession(sessionId);

        if (session is not DevModeSession devSession)
        {
            return new Dictionary<string, object?>
            {
                ["id"] = id,
                ["session"] = session.Id,
                ["err"] = "watch only available in dev mode\n",
                ["status"] = new List<object?> { "error", "done" }
            };
        }

        devSession.StopWatching();

        return new Dictionary<string, object?>
        {
            ["id"] = id,
            ["session"] = session.Id,
            ["value"] = ":stopped",
            ["status"] = new List<object?> { "done" }
        };
    }

    private static List<string> GetCoreCompletions(string prefix)
    {
        var coreFunctions = new[]
        {
            "+", "-", "*", "/", "<", ">", "<=", ">=", "=",
            "str", "println", "print", "prn", "pr-str",
            "first", "rest", "next", "map", "filter", "reduce", "count",
            "get", "assoc", "dissoc", "conj", "update",
            "inc", "dec", "nil?", "some?", "number?", "string?",
            "keyword?", "symbol?", "list?", "vector?", "map?", "set?", "fn?", "seq?",
            "def", "defn", "let", "if", "do", "fn", "loop", "recur",
            "try", "catch", "finally", "throw", "await", "ns", "quote"
        };

        return coreFunctions.Where(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
    }
}
