using System.Collections.Immutable;
using System.Reflection;
using Microsoft.CodeAnalysis;

namespace Cljr.Compiler.Macros;

/// <summary>
/// Holds compilation context for macro expansion (LEGACY).
///
/// NOTE: As of the pure interpreter refactor, MacroExpander no longer uses
/// Roslyn compilation at macro expansion time. Macros are now evaluated
/// using MacroInterpreter, which works in both REPL and source generators.
///
/// This class is retained for backwards compatibility but its functionality
/// is no longer used by the macro system. The IsSourceGeneratorContext flag
/// and reference management are now unnecessary.
///
/// Future use: Could be repurposed for REPL-specific features where user
/// functions are compiled and can be called from macros.
/// </summary>
public class MacroContext
{
    private readonly List<MetadataReference> _externalReferences = new();
    private readonly List<Assembly> _userAssemblies = new();
    private readonly Dictionary<string, MethodInfo> _userFunctions = new();

    /// <summary>
    /// External metadata references from the compilation (NuGet, framework, etc.)
    /// </summary>
    public IReadOnlyList<MetadataReference> ExternalReferences => _externalReferences;

    /// <summary>
    /// Assemblies containing user-defined functions that can be called from macros
    /// </summary>
    public IReadOnlyList<Assembly> UserAssemblies => _userAssemblies;

    /// <summary>
    /// Cached user function method infos (namespace/name -> MethodInfo)
    /// </summary>
    public IReadOnlyDictionary<string, MethodInfo> UserFunctions => _userFunctions;

    /// <summary>
    /// Whether we're in source generator context (some APIs unavailable)
    /// </summary>
    public bool IsSourceGeneratorContext { get; set; }

    /// <summary>
    /// Create an empty macro context (for REPL use)
    /// </summary>
    public MacroContext()
    {
    }

    /// <summary>
    /// Create a macro context from compilation references (for source generator use)
    /// </summary>
    public MacroContext(IEnumerable<MetadataReference> references)
    {
        _externalReferences.AddRange(references);
        IsSourceGeneratorContext = true;
    }

    /// <summary>
    /// Add metadata references from a Compilation object
    /// </summary>
    public void AddReferences(IEnumerable<MetadataReference> references)
    {
        _externalReferences.AddRange(references);
    }

    /// <summary>
    /// Add a user assembly containing compiled functions
    /// </summary>
    public void AddUserAssembly(Assembly assembly)
    {
        _userAssemblies.Add(assembly);
        IndexFunctionsFromAssembly(assembly);
    }

    /// <summary>
    /// Register a user function that can be called from macros
    /// </summary>
    public void RegisterFunction(string qualifiedName, MethodInfo method)
    {
        _userFunctions[qualifiedName] = method;
    }

    /// <summary>
    /// Try to get a user function by qualified name (e.g., "my-ns/my-fn")
    /// </summary>
    public bool TryGetUserFunction(string qualifiedName, out MethodInfo? method)
    {
        return _userFunctions.TryGetValue(qualifiedName, out method);
    }

    /// <summary>
    /// Try to invoke a user-defined function
    /// </summary>
    public object? InvokeUserFunction(string qualifiedName, params object?[] args)
    {
        if (!TryGetUserFunction(qualifiedName, out var method) || method == null)
            throw new MacroException($"User function not found: {qualifiedName}");

        return method.Invoke(null, args);
    }

    /// <summary>
    /// Check if a user function exists
    /// </summary>
    public bool HasUserFunction(string qualifiedName)
    {
        return _userFunctions.ContainsKey(qualifiedName);
    }

    /// <summary>
    /// Index all public static methods from an assembly as potential macro-callable functions
    /// </summary>
    private void IndexFunctionsFromAssembly(Assembly assembly)
    {
        try
        {
            foreach (var type in assembly.GetExportedTypes())
            {
                // Get the Clojure namespace from the C# namespace
                // e.g., MyApp.Core -> my-app.core
                var clojureNs = ToCljNamespace(type.Namespace ?? "");

                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    // Skip special methods
                    if (method.IsSpecialName) continue;

                    // Convert method name to Clojure style
                    var cljName = ToCljName(method.Name);
                    var qualifiedName = string.IsNullOrEmpty(clojureNs)
                        ? cljName
                        : $"{clojureNs}/{cljName}";

                    _userFunctions[qualifiedName] = method;
                }
            }
        }
        catch
        {
            // Ignore assembly loading errors
        }
    }

    /// <summary>
    /// Convert C# namespace to Clojure namespace (e.g., "MyApp.Core" -> "my-app.core")
    /// </summary>
    private static string ToCljNamespace(string csNamespace)
    {
        if (string.IsNullOrEmpty(csNamespace)) return "";

        return string.Join(".", csNamespace.Split('.')
            .Select(part => ToKebabCase(part)));
    }

    /// <summary>
    /// Convert C# method name to Clojure function name (e.g., "_my_fn" -> "my-fn")
    /// </summary>
    private static string ToCljName(string csName)
    {
        // Handle underscore prefix for special chars
        var name = csName.TrimStart('_');

        // Replace underscores with hyphens
        name = name.Replace("_", "-");

        // Handle PascalCase to kebab-case
        return ToKebabCase(name);
    }

    /// <summary>
    /// Convert PascalCase to kebab-case
    /// </summary>
    private static string ToKebabCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;

        var result = new System.Text.StringBuilder();
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsUpper(c) && i > 0)
            {
                result.Append('-');
                result.Append(char.ToLowerInvariant(c));
            }
            else
            {
                result.Append(char.ToLowerInvariant(c));
            }
        }
        return result.ToString();
    }

    /// <summary>
    /// Get all metadata references needed for Roslyn compilation.
    /// Combines external references with standard library references.
    /// </summary>
    public List<MetadataReference> GetAllReferences()
    {
        var references = new List<MetadataReference>(_externalReferences);

        // In source generator context, external references already include everything needed
        if (!IsSourceGeneratorContext)
        {
            // Add core runtime assemblies from TRUSTED_PLATFORM_ASSEMBLIES
            var trustedAssemblies = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
            if (trustedAssemblies != null)
            {
                foreach (var path in trustedAssemblies.Split(Path.PathSeparator))
                {
                    if (File.Exists(path))
                    {
                        try
                        {
                            references.Add(MetadataReference.CreateFromFile(path));
                        }
                        catch
                        {
                            // Skip assemblies that can't be loaded
                        }
                    }
                }
            }
        }

        // Add our own compiler assembly
        AddSelfReference(references);

        // Add user assemblies
        foreach (var assembly in _userAssemblies)
        {
            AddAssemblyReference(references, assembly);
        }

        return references;
    }

    /// <summary>
    /// Add reference to the Cljr.Compiler assembly itself
    /// </summary>
    private static void AddSelfReference(List<MetadataReference> references)
    {
        var compilerAssembly = typeof(MacroRuntime).Assembly;
        AddAssemblyReference(references, compilerAssembly);
    }

    /// <summary>
    /// Add a reference to an assembly, handling both file-based and memory-based assemblies
    /// </summary>
    private static void AddAssemblyReference(List<MetadataReference> references, Assembly assembly)
    {
        // Try file-based reference first
        var location = assembly.Location;
        if (!string.IsNullOrEmpty(location) && File.Exists(location))
        {
            try
            {
                references.Add(MetadataReference.CreateFromFile(location));
                return;
            }
            catch
            {
                // Fall through to other approaches
            }
        }

        // For memory-loaded assemblies (source generator context), we can't easily
        // get metadata references. The caller should pass external references
        // from Compilation.References instead.
        //
        // In source generator context:
        // - Assembly.Location is empty
        // - TryGetRawMetadata requires unsafe code and .NET Core 3.0+
        // - Best approach: use context.Compilation.References from source generator
        //
        // For now, we silently skip assemblies we can't reference.
        // The MacroContext should be initialized with Compilation.References
        // in source generator context.
    }
}
