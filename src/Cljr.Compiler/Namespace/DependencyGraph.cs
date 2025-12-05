namespace Cljr.Compiler.Namespace;

/// <summary>
/// Builds and sorts file dependencies for multi-file compilation.
/// Uses Kahn's algorithm for topological sorting with cycle detection.
/// </summary>
public class DependencyGraph
{
    private readonly Dictionary<string, FileNode> _nodes = new Dictionary<string, FileNode>();
    private readonly Dictionary<string, string> _namespaceToFile = new Dictionary<string, string>();

    /// <summary>
    /// Add a file to the dependency graph.
    /// </summary>
    public void AddFile(string filePath, string sourceText)
    {
        var info = RequireExtractor.Extract(sourceText);
        var node = new FileNode(filePath, sourceText, info);
        _nodes[filePath] = node;

        if (info != null)
        {
            _namespaceToFile[info.Namespace] = filePath;
        }
    }

    /// <summary>
    /// Get all files ordered by dependencies (topological sort).
    /// Files with no dependencies come first.
    /// </summary>
    public DependencyResult GetOrderedFiles()
    {
        // Build adjacency lists
        var inDegree = new Dictionary<string, int>();
        var dependents = new Dictionary<string, List<string>>();

        foreach (var kvp in _nodes)
        {
            var path = kvp.Key;
            inDegree[path] = 0;
            dependents[path] = new List<string>();
        }

        // Calculate in-degrees and build dependency edges
        foreach (var kvp in _nodes)
        {
            var path = kvp.Key;
            var node = kvp.Value;

            if (node.NamespaceInfo == null)
                continue;

            foreach (var req in node.NamespaceInfo.Requires)
            {
                if (_namespaceToFile.TryGetValue(req.Namespace, out var reqPath))
                {
                    // reqPath -> path (path depends on reqPath)
                    dependents[reqPath].Add(path);
                    inDegree[path]++;
                }
            }
        }

        // Kahn's algorithm for topological sort
        var queue = new Queue<string>();
        var result = new List<FileNode>();

        // Start with nodes that have no dependencies
        foreach (var kvp in inDegree)
        {
            var path = kvp.Key;
            var degree = kvp.Value;
            if (degree == 0)
                queue.Enqueue(path);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            result.Add(_nodes[current]);

            foreach (var dependent in dependents[current])
            {
                inDegree[dependent]--;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        // If we didn't process all nodes, there's a cycle
        if (result.Count != _nodes.Count)
        {
            var processedPaths = new HashSet<string>();
            foreach (var node in result)
            {
                processedPaths.Add(node.FilePath);
            }

            var cycleNodes = new List<string>();
            foreach (var path in _nodes.Keys)
            {
                if (!processedPaths.Contains(path))
                    cycleNodes.Add(path);
            }

            var cycles = DetectCycles(cycleNodes, dependents);
            return new DependencyResult.Failure(cycles);
        }

        return new DependencyResult.Success(result);
    }

    /// <summary>
    /// Get requires that reference namespaces not found in the project.
    /// These might be external dependencies (NuGet packages, etc.)
    /// </summary>
    public IReadOnlyList<MissingRequire> GetMissingRequires()
    {
        var missing = new List<MissingRequire>();

        foreach (var kvp in _nodes)
        {
            var path = kvp.Key;
            var node = kvp.Value;

            if (node.NamespaceInfo == null)
                continue;

            foreach (var req in node.NamespaceInfo.Requires)
            {
                if (!_namespaceToFile.ContainsKey(req.Namespace))
                {
                    missing.Add(new MissingRequire(path, req.Namespace));
                }
            }
        }

        return missing;
    }

    private List<string> DetectCycles(List<string> cycleNodes, Dictionary<string, List<string>> dependents)
    {
        var errors = new List<string>();
        var visited = new HashSet<string>();

        foreach (var start in cycleNodes)
        {
            if (visited.Contains(start))
                continue;

            var path = new List<string>();
            var found = FindCycle(start, path, visited, dependents);
            if (found != null)
            {
                var nsNames = new List<string>();
                foreach (var p in found)
                {
                    var nsInfo = _nodes[p].NamespaceInfo;
                    nsNames.Add(nsInfo?.Namespace ?? System.IO.Path.GetFileName(p));
                }
                errors.Add($"Circular dependency: {string.Join(" -> ", nsNames)}");
            }
        }

        return errors;
    }

    private List<string>? FindCycle(
        string current,
        List<string> path,
        HashSet<string> visited,
        Dictionary<string, List<string>> dependents)
    {
        if (path.Contains(current))
        {
            var cycleStart = path.IndexOf(current);
            var cycle = new List<string>();
            for (int i = cycleStart; i < path.Count; i++)
            {
                cycle.Add(path[i]);
            }
            cycle.Add(current);
            return cycle;
        }

        if (visited.Contains(current))
            return null;

        path.Add(current);

        var nodeInfo = _nodes[current].NamespaceInfo;
        if (nodeInfo != null)
        {
            foreach (var req in nodeInfo.Requires)
            {
                if (_namespaceToFile.TryGetValue(req.Namespace, out var reqPath))
                {
                    var cycle = FindCycle(reqPath, path, visited, dependents);
                    if (cycle != null)
                        return cycle;
                }
            }
        }

        path.RemoveAt(path.Count - 1);
        visited.Add(current);
        return null;
    }
}

/// <summary>
/// A node in the dependency graph representing a single .cljr file.
/// </summary>
public class FileNode
{
    public string FilePath { get; }
    public string SourceText { get; }
    public FileNamespaceInfo? NamespaceInfo { get; }
    public string FileName => System.IO.Path.GetFileNameWithoutExtension(FilePath);

    public FileNode(string filePath, string sourceText, FileNamespaceInfo? namespaceInfo)
    {
        FilePath = filePath;
        SourceText = sourceText;
        NamespaceInfo = namespaceInfo;
    }
}

/// <summary>
/// Result of dependency resolution.
/// </summary>
public abstract class DependencyResult
{
    private DependencyResult() { }

    public sealed class Success : DependencyResult
    {
        public IReadOnlyList<FileNode> Files { get; }
        public Success(IReadOnlyList<FileNode> files) => Files = files;
    }

    public sealed class Failure : DependencyResult
    {
        public IReadOnlyList<string> CircularErrors { get; }
        public Failure(IReadOnlyList<string> circularErrors) => CircularErrors = circularErrors;
    }
}

/// <summary>
/// A require that references a namespace not found in the project.
/// </summary>
public class MissingRequire
{
    public string FilePath { get; }
    public string RequiredNamespace { get; }

    public MissingRequire(string filePath, string requiredNamespace)
    {
        FilePath = filePath;
        RequiredNamespace = requiredNamespace;
    }
}
