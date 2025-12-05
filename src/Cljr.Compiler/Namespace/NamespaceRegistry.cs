namespace Cljr.Compiler.Namespace;

/// <summary>
/// Registry that tracks loaded Clojure namespaces and their C# mappings.
/// </summary>
public class NamespaceRegistry
{
    private readonly Dictionary<string, NamespaceInfo> _namespaces = new();
    private readonly HashSet<string> _loadingStack = new(); // Circular dependency detection

    /// <summary>
    /// Register a namespace
    /// </summary>
    public void Register(NamespaceInfo ns)
    {
        _namespaces[ns.ClojureNs] = ns;
    }

    /// <summary>
    /// Check if a namespace is loaded
    /// </summary>
    public bool IsLoaded(string clojureNs) => _namespaces.ContainsKey(clojureNs);

    /// <summary>
    /// Get namespace info
    /// </summary>
    public NamespaceInfo? Get(string clojureNs)
    {
        return _namespaces.TryGetValue(clojureNs, out var ns) ? ns : null;
    }

    /// <summary>
    /// Get all registered namespaces
    /// </summary>
    public IEnumerable<NamespaceInfo> GetAll() => _namespaces.Values;

    /// <summary>
    /// Check if we're currently loading a namespace (for circular dependency detection)
    /// </summary>
    public bool IsCurrentlyLoading(string clojureNs) => _loadingStack.Contains(clojureNs);

    /// <summary>
    /// Mark a namespace as being loaded
    /// </summary>
    public void BeginLoading(string clojureNs)
    {
        if (_loadingStack.Contains(clojureNs))
            throw new NamespaceException($"Circular dependency detected: {clojureNs}");
        _loadingStack.Add(clojureNs);
    }

    /// <summary>
    /// Mark a namespace as finished loading
    /// </summary>
    public void EndLoading(string clojureNs)
    {
        _loadingStack.Remove(clojureNs);
    }

    /// <summary>
    /// Convert Clojure namespace to C# namespace
    /// </summary>
    public static string ToCSharpNamespace(string clojureNs)
    {
        // cljr.core -> Cljr.Core
        // my-app.utils -> MyApp.Utils
        var parts = clojureNs.Split('.');
        return string.Join(".", parts.Select(ToCSharpName));
    }

    /// <summary>
    /// Convert Clojure namespace to C# class name
    /// </summary>
    public static string ToCSharpClassName(string clojureNs)
    {
        // cljr.core -> Core
        // my-app.utils -> Utils
        var lastPart = clojureNs.Split('.').Last();
        return ToCSharpName(lastPart);
    }

    private static string ToCSharpName(string name)
    {
        // my-utils -> MyUtils
        var result = name.Replace("-", "_");
        if (result.Length > 0)
            result = char.ToUpper(result[0]) + result[1..];
        return result;
    }
}

/// <summary>
/// Information about a loaded namespace
/// </summary>
public class NamespaceInfo
{
    public string ClojureNs { get; }
    public string CSharpNamespace { get; }
    public string CSharpClass { get; }
    public string? FilePath { get; }
    public List<RequireInfo> Requires { get; } = new();
    public List<ImportInfo> Imports { get; } = new();
    public HashSet<string> PublicVars { get; } = new();

    public NamespaceInfo(string clojureNs, string csharpNamespace, string csharpClass,
                         IEnumerable<RequireInfo>? requires = null,
                         IEnumerable<ImportInfo>? imports = null,
                         string? filePath = null)
    {
        ClojureNs = clojureNs;
        CSharpNamespace = csharpNamespace;
        CSharpClass = csharpClass;
        FilePath = filePath;
        if (requires != null)
            Requires.AddRange(requires);
        if (imports != null)
            Imports.AddRange(imports);
    }
}

/// <summary>
/// Information about a require clause
/// </summary>
public record RequireInfo(
    string Namespace,
    string? Alias,
    IReadOnlyList<string>? Refer
);

/// <summary>
/// Information about an import clause
/// </summary>
public record ImportInfo(
    string Namespace,
    IReadOnlyList<string> Types
);

/// <summary>
/// Exception for namespace-related errors
/// </summary>
public class NamespaceException : Exception
{
    public NamespaceException(string message) : base(message) { }
}
