using System.Diagnostics;

namespace Cljr.Repl;

/// <summary>
/// DevModeSession - Enhanced REPL session for development with file watching
/// and hot-reload capabilities. Extends NreplSession with:
/// - Automatic file watching and reload
/// - State preservation across reloads (atoms survive)
/// - Dependency-aware reloading (reload dependents after their dependencies)
/// </summary>
public class DevModeSession : NreplSession, IDisposable
{
    private readonly DevModeOptions _options;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly NamespaceLoader _nsLoader;
    private readonly StateRegistry _stateRegistry = new();
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    /// <summary>
    /// Fired when a namespace is reloaded
    /// </summary>
    public event EventHandler<ReloadEventArgs>? OnReload;

    /// <summary>
    /// Fired when a file change is detected
    /// </summary>
    public event EventHandler<string>? OnFileChanged;

    /// <summary>
    /// Whether file watching is currently active
    /// </summary>
    public bool IsWatching { get; private set; }

    public DevModeSession() : this(new DevModeOptions())
    {
    }

    public DevModeSession(DevModeOptions options) : base()
    {
        _options = options;
        _nsLoader = new NamespaceLoader(this, options.SourcePaths);

        if (options.EnableWatching)
        {
            SetupFileWatching(options.WatchPaths);
        }
    }

    /// <summary>
    /// Sets up file watchers for the given paths
    /// </summary>
    private void SetupFileWatching(IEnumerable<string> paths)
    {
        foreach (var path in paths)
        {
            if (!Directory.Exists(path))
                continue;

            var watcher = new FileSystemWatcher(path)
            {
                Filter = "*.cljr",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = false
            };

            watcher.Changed += OnFileSystemChanged;
            watcher.Created += OnFileSystemChanged;
            watcher.Renamed += OnFileSystemRenamed;

            _watchers.Add(watcher);
        }
    }

    /// <summary>
    /// Starts file watching
    /// </summary>
    public void StartWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = true;
        }
        IsWatching = true;

        if (_options.Verbose)
            Console.WriteLine($"[dev] Watching: {string.Join(", ", _options.WatchPaths)}");
    }

    /// <summary>
    /// Stops file watching
    /// </summary>
    public void StopWatching()
    {
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
        }
        IsWatching = false;

        if (_options.Verbose)
            Console.WriteLine("[dev] File watching stopped");
    }

    private DateTime _lastReload = DateTime.MinValue;

    private async void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce rapid file changes
        var now = DateTime.UtcNow;
        if ((now - _lastReload).TotalMilliseconds < 500)
            return;
        _lastReload = now;

        OnFileChanged?.Invoke(this, e.FullPath);

        if (_options.AutoReload)
        {
            var ns = _nsLoader.GetNamespaceForPath(e.FullPath);
            if (ns is not null)
            {
                await ReloadNamespaceAsync(ns, e.FullPath);
            }
        }
    }

    private async void OnFileSystemRenamed(object sender, RenamedEventArgs e)
    {
        if (e.FullPath.EndsWith(".cljr"))
        {
            OnFileChanged?.Invoke(this, e.FullPath);

            if (_options.AutoReload)
            {
                var ns = _nsLoader.GetNamespaceForPath(e.FullPath);
                if (ns is not null)
                {
                    await ReloadNamespaceAsync(ns, e.FullPath);
                }
            }
        }
    }

    /// <summary>
    /// Reloads a namespace from its source file.
    /// Preserves atoms, reloads dependents.
    /// </summary>
    public async Task<ReloadResult> ReloadNamespaceAsync(string ns, string? triggerPath = null)
    {
        await _reloadLock.WaitAsync();
        try
        {
            var sw = Stopwatch.StartNew();
            var reloadedDependents = new List<string>();

            // 1. Capture state (atoms) before reload
            var oldState = _stateRegistry.CaptureState(ns);

            // 2. Clear old dependency tracking
            _nsLoader.ClearDependencies(ns);

            // 3. Find and load source
            var path = triggerPath ?? _nsLoader.FindSourceFile(ns);
            if (path is null)
            {
                var error = $"Source file not found for namespace: {ns}";
                OnReload?.Invoke(this, new ReloadEventArgs(ns, false, error));
                return ReloadResult.Failed(ns, error);
            }

            try
            {
                var source = await File.ReadAllTextAsync(path);

                // 4. Extract and track dependencies
                var deps = NamespaceLoader.ExtractDependencies(source);
                foreach (var dep in deps)
                {
                    _nsLoader.AddDependency(ns, dep);
                }

                // 5. Eval the source
                var result = await EvalAsync(source);

                if (result.Error is not null)
                {
                    OnReload?.Invoke(this, new ReloadEventArgs(ns, false, result.Error, path));
                    return ReloadResult.Failed(ns, result.Error);
                }

                // 6. Restore state (atoms)
                _stateRegistry.RestoreState(ns, oldState);

                // 7. Reload dependents
                foreach (var dependent in _nsLoader.GetDependents(ns))
                {
                    var depResult = await ReloadNamespaceAsync(dependent);
                    if (depResult.Success)
                        reloadedDependents.Add(dependent);
                }

                sw.Stop();

                if (_options.Verbose)
                {
                    Console.WriteLine($"[reload] {ns} ok ({sw.ElapsedMilliseconds}ms)");
                    if (reloadedDependents.Count > 0)
                        Console.WriteLine($"[reload] dependents: {string.Join(", ", reloadedDependents)}");
                }

                OnReload?.Invoke(this, new ReloadEventArgs(ns, true, filePath: path));
                return ReloadResult.Succeeded(ns, sw.Elapsed, reloadedDependents);
            }
            catch (Exception ex)
            {
                var error = ex.Message;
                OnReload?.Invoke(this, new ReloadEventArgs(ns, false, error, path));
                return ReloadResult.Failed(ns, error);
            }
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    /// <summary>
    /// Loads the initial namespace and starts watching
    /// </summary>
    public async Task<EvalResult?> StartAsync()
    {
        EvalResult? result = null;

        if (_options.InitialNamespace is not null)
        {
            if (_options.Verbose)
                Console.WriteLine($"[dev] Loading {_options.InitialNamespace}...");

            result = await _nsLoader.LoadNamespaceAsync(_options.InitialNamespace);

            if (result?.Error is null)
            {
                // Check for -main function and run it
                await EvalAsync($"(in-ns '{_options.InitialNamespace})");
            }
        }

        if (_options.EnableWatching)
        {
            StartWatching();
        }

        return result;
    }

    /// <summary>
    /// Gets the namespace loader for direct access
    /// </summary>
    public NamespaceLoader NamespaceLoader => _nsLoader;

    /// <summary>
    /// Manually triggers a reload of all loaded namespaces
    /// </summary>
    public async Task<List<ReloadResult>> ReloadAllAsync()
    {
        var results = new List<ReloadResult>();

        foreach (var ns in RuntimeNamespace.AllNamespaces)
        {
            var result = await ReloadNamespaceAsync(ns);
            results.Add(result);
        }

        return results;
    }

    /// <summary>
    /// Disposes the session and stops file watching
    /// </summary>
    public void Dispose()
    {
        StopWatching();
        foreach (var watcher in _watchers)
        {
            watcher.Dispose();
        }
        _watchers.Clear();
        _reloadLock.Dispose();
    }
}
