using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Reader;
using Cljr.Compiler.Emitter;

// Type alias to disambiguate between Cljr.Compiler.Reader.Symbol and Cljr.Symbol
using Symbol = Cljr.Compiler.Reader.Symbol;

namespace Cljr.Compiler.Namespace;

/// <summary>
/// Resolves and loads required namespaces from the file system.
/// </summary>
public class RequireResolver
{
    private readonly NamespaceRegistry _registry;
    private readonly List<string> _sourcePaths;

    public RequireResolver(NamespaceRegistry registry, IEnumerable<string>? sourcePaths = null)
    {
        _registry = registry;
        _sourcePaths = sourcePaths?.ToList() ?? [".", "src"];
    }

    /// <summary>
    /// Add a source path to search for namespace files
    /// </summary>
    public void AddSourcePath(string path)
    {
        if (!_sourcePaths.Contains(path))
            _sourcePaths.Add(path);
    }

    /// <summary>
    /// Resolve and load a namespace, returning its info
    /// </summary>
    public NamespaceInfo Require(string clojureNs, Analyzer.Analyzer? parentAnalyzer = null)
    {
        // Check if already loaded
        var existing = _registry.Get(clojureNs);
        if (existing != null)
            return existing;

        // Check for circular dependency
        if (_registry.IsCurrentlyLoading(clojureNs))
            throw new NamespaceException($"Circular dependency while loading: {clojureNs}");

        // Find the source file
        var filePath = FindSourceFile(clojureNs);
        if (filePath == null)
            throw new NamespaceException($"Could not find namespace: {clojureNs}");

        // Load and compile the file
        return LoadNamespace(clojureNs, filePath, parentAnalyzer);
    }

    /// <summary>
    /// Try to require a namespace, returns null if not found
    /// </summary>
    public NamespaceInfo? TryRequire(string clojureNs, Analyzer.Analyzer? parentAnalyzer = null)
    {
        try
        {
            return Require(clojureNs, parentAnalyzer);
        }
        catch (NamespaceException)
        {
            return null;
        }
    }

    /// <summary>
    /// Find the source file for a namespace
    /// </summary>
    public string? FindSourceFile(string clojureNs)
    {
        // Convert namespace to file path: cljr.core -> cljr/core.cljr
        var relativePath = clojureNs.Replace('.', Path.DirectorySeparatorChar);
        var extensions = new[] { ".cljr", ".clj", ".cljc" };

        foreach (var basePath in _sourcePaths)
        {
            foreach (var ext in extensions)
            {
                var fullPath = Path.Combine(basePath, relativePath + ext);
                if (File.Exists(fullPath))
                    return Path.GetFullPath(fullPath);
            }
        }

        return null;
    }

    private NamespaceInfo LoadNamespace(string clojureNs, string filePath, Analyzer.Analyzer? parentAnalyzer)
    {
        _registry.BeginLoading(clojureNs);

        try
        {
            // Read the file
            var source = File.ReadAllText(filePath);
            var forms = LispReader.ReadAll(source).ToList();

            // Create analyzer (inheriting macros from parent if provided)
            var analyzer = parentAnalyzer != null
                ? new Analyzer.Analyzer() // TODO: Share macro expander
                : new Analyzer.Analyzer();

            // Analyze the file
            var unit = analyzer.AnalyzeFile(forms);

            // Process requires from the ns form
            var requires = new List<RequireInfo>();
            var imports = new List<ImportInfo>();

            if (unit.Namespace != null)
            {
                foreach (var req in unit.Namespace.Requires)
                {
                    requires.Add(new RequireInfo(req.Namespace, req.Alias, req.Refers));

                    // Recursively load required namespaces
                    TryRequire(req.Namespace, analyzer);
                }

                foreach (var imp in unit.Namespace.Imports)
                {
                    imports.Add(new ImportInfo(imp.Namespace, imp.Types));
                }
            }

            // Extract namespace info
            var nsInfo = new NamespaceInfo(
                clojureNs,
                NamespaceRegistry.ToCSharpNamespace(clojureNs),
                NamespaceRegistry.ToCSharpClassName(clojureNs),
                requires,
                imports,
                filePath
            );

            // Register the namespace
            _registry.Register(nsInfo);
            _registry.EndLoading(clojureNs);

            return nsInfo;
        }
        catch
        {
            _registry.EndLoading(clojureNs);
            throw;
        }
    }

    /// <summary>
    /// Compile a namespace to C# source
    /// </summary>
    public string CompileNamespace(string clojureNs)
    {
        var nsInfo = _registry.Get(clojureNs);
        if (nsInfo == null)
            nsInfo = Require(clojureNs);

        if (nsInfo.FilePath == null)
            throw new NamespaceException($"Namespace {clojureNs} has no source file");

        var source = File.ReadAllText(nsInfo.FilePath);
        var forms = LispReader.ReadAll(source).ToList();
        var analyzer = new Analyzer.Analyzer();
        var unit = analyzer.AnalyzeFile(forms);
        var emitter = new CSharpEmitter();
        return emitter.Emit(unit);
    }
}
