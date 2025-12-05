namespace Cljr.Repl;

/// <summary>
/// NamespaceLoader - handles loading namespaces from source files
/// and tracking dependencies between namespaces.
/// </summary>
public class NamespaceLoader
{
    private readonly NreplSession _session;

    /// <summary>
    /// Paths to search for source files
    /// </summary>
    public List<string> SourcePaths { get; } = ["src", "."];

    /// <summary>
    /// Maps namespace name to source file path
    /// </summary>
    private readonly Dictionary<string, string> _sourcePaths = new();

    /// <summary>
    /// Maps namespace to its dependencies (namespaces it requires)
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _dependencies = new();

    /// <summary>
    /// Maps namespace to namespaces that depend on it
    /// </summary>
    private readonly Dictionary<string, HashSet<string>> _dependents = new();

    public NamespaceLoader(NreplSession session)
    {
        _session = session;
    }

    public NamespaceLoader(NreplSession session, IEnumerable<string> sourcePaths) : this(session)
    {
        SourcePaths.Clear();
        SourcePaths.AddRange(sourcePaths);
    }

    /// <summary>
    /// Finds the source file for a namespace.
    /// Converts namespace name to path (e.g., "my-app.core" -> "my_app/core.cljr")
    /// </summary>
    public string? FindSourceFile(string ns)
    {
        // Check cache first
        if (_sourcePaths.TryGetValue(ns, out var cached))
        {
            if (File.Exists(cached))
                return cached;
            _sourcePaths.Remove(ns);
        }

        // Convert namespace to path: my-app.core -> my_app/core.cljr
        var pathPart = ns.Replace('.', Path.DirectorySeparatorChar)
                        .Replace('-', '_');
        var fileName = pathPart + ".cljr";

        foreach (var basePath in SourcePaths)
        {
            var fullPath = Path.Combine(basePath, fileName);
            if (File.Exists(fullPath))
            {
                _sourcePaths[ns] = fullPath;
                return fullPath;
            }

            // Also try with dashes preserved (some projects use this)
            var altFileName = ns.Replace('.', Path.DirectorySeparatorChar) + ".cljr";
            var altPath = Path.Combine(basePath, altFileName);
            if (File.Exists(altPath))
            {
                _sourcePaths[ns] = altPath;
                return altPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets namespace name from a file path.
    /// </summary>
    public string? GetNamespaceForPath(string filePath)
    {
        foreach (var (cachedNs, cachedPath) in _sourcePaths)
        {
            if (Path.GetFullPath(cachedPath) == Path.GetFullPath(filePath))
                return cachedNs;
        }

        // Try to derive namespace from path
        var relativePath = filePath;
        foreach (var sourcePath in SourcePaths)
        {
            var fullSourcePath = Path.GetFullPath(sourcePath);
            var fullFilePath = Path.GetFullPath(filePath);
            if (fullFilePath.StartsWith(fullSourcePath))
            {
                relativePath = fullFilePath[(fullSourcePath.Length + 1)..];
                break;
            }
        }

        // Convert path to namespace: my_app/core.cljr -> my-app.core
        var nsPath = Path.ChangeExtension(relativePath, null);
        var ns = nsPath.Replace(Path.DirectorySeparatorChar, '.')
                      .Replace('_', '-');
        return ns;
    }

    /// <summary>
    /// Registers a dependency between namespaces.
    /// </summary>
    public void AddDependency(string dependent, string dependency)
    {
        if (!_dependencies.TryGetValue(dependent, out var deps))
        {
            deps = [];
            _dependencies[dependent] = deps;
        }
        deps.Add(dependency);

        if (!_dependents.TryGetValue(dependency, out var depnts))
        {
            depnts = [];
            _dependents[dependency] = depnts;
        }
        depnts.Add(dependent);
    }

    /// <summary>
    /// Gets all namespaces that depend on the given namespace.
    /// </summary>
    public IEnumerable<string> GetDependents(string ns) =>
        _dependents.TryGetValue(ns, out var deps) ? deps : [];

    /// <summary>
    /// Gets all namespaces that the given namespace depends on.
    /// </summary>
    public IEnumerable<string> GetDependencies(string ns) =>
        _dependencies.TryGetValue(ns, out var deps) ? deps : [];

    /// <summary>
    /// Clears dependency tracking for a namespace (called before reload).
    /// </summary>
    public void ClearDependencies(string ns)
    {
        if (_dependencies.TryGetValue(ns, out var oldDeps))
        {
            foreach (var dep in oldDeps)
            {
                if (_dependents.TryGetValue(dep, out var depnts))
                    depnts.Remove(ns);
            }
            _dependencies.Remove(ns);
        }
    }

    /// <summary>
    /// Loads a namespace from its source file.
    /// Returns null if source file not found.
    /// </summary>
    public async Task<EvalResult?> LoadNamespaceAsync(string ns)
    {
        var path = FindSourceFile(ns);
        if (path is null)
            return null;

        var source = await File.ReadAllTextAsync(path);
        return await _session.EvalAsync(source);
    }

    /// <summary>
    /// Gets a topologically sorted list of namespaces to reload.
    /// Ensures dependencies are reloaded before dependents.
    /// </summary>
    public List<string> GetReloadOrder(string ns)
    {
        var result = new List<string> { ns };
        var visited = new HashSet<string> { ns };
        var queue = new Queue<string>(GetDependents(ns));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (visited.Add(current))
            {
                result.Add(current);
                foreach (var dep in GetDependents(current))
                    queue.Enqueue(dep);
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts dependencies from source code (parses ns form).
    /// This is a simplified parser that looks for :require clauses.
    /// </summary>
    public static IEnumerable<string> ExtractDependencies(string source)
    {
        // Simple regex-based extraction for :require clauses
        // Full implementation would use the reader
        var deps = new List<string>();

        // Look for patterns like [some.namespace ...] or some.namespace in require
        var requireMatch = System.Text.RegularExpressions.Regex.Match(
            source,
            @":require\s+\[(.*?)\]",
            System.Text.RegularExpressions.RegexOptions.Singleline);

        if (requireMatch.Success)
        {
            var requireContent = requireMatch.Groups[1].Value;
            // Extract namespace names (simplified)
            var nsMatches = System.Text.RegularExpressions.Regex.Matches(
                requireContent,
                @"\[([a-z][a-z0-9\-\.]+)");

            foreach (System.Text.RegularExpressions.Match m in nsMatches)
            {
                deps.Add(m.Groups[1].Value);
            }
        }

        return deps;
    }
}
