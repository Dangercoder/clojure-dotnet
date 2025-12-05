using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;

namespace Cljr.Repl;

/// <summary>
/// Represents the context of a .NET project, including all resolved assemblies
/// from NuGet packages, framework references, and project output.
/// </summary>
public class ProjectContext
{
    public string ProjectPath { get; }
    public string ProjectDir { get; }
    public string TargetFramework { get; private set; } = "net10.0";
    public string? Sdk { get; private set; }
    public List<string> SourcePaths { get; } = new() { "src", "." };

    private readonly List<string> _assemblyPaths = new();
    private readonly Dictionary<string, Assembly> _loadedAssemblies = new();
    private readonly Dictionary<string, Assembly> _typeToAssembly = new();

    public IReadOnlyList<string> AssemblyPaths => _assemblyPaths;

    private ProjectContext(string projectPath)
    {
        ProjectPath = Path.GetFullPath(projectPath);
        ProjectDir = Path.GetDirectoryName(ProjectPath)!;
    }

    /// <summary>
    /// Private constructor for FromAssemblies factory
    /// </summary>
    private ProjectContext()
    {
        ProjectPath = "";
        ProjectDir = Environment.CurrentDirectory;
    }

    /// <summary>
    /// Load project context from a .csproj file
    /// </summary>
    public static async Task<ProjectContext> LoadAsync(string csprojPath)
    {
        var context = new ProjectContext(csprojPath);
        await context.InitializeAsync();
        return context;
    }

    /// <summary>
    /// Try to find and load a project from the current directory
    /// </summary>
    public static async Task<ProjectContext?> FromCurrentDirectoryAsync()
    {
        var csprojFiles = Directory.GetFiles(".", "*.csproj");
        if (csprojFiles.Length == 1)
        {
            return await LoadAsync(csprojFiles[0]);
        }
        return null;
    }

    /// <summary>
    /// Synchronous version for convenience
    /// </summary>
    public static ProjectContext? FromCurrentDirectory()
    {
        return FromCurrentDirectoryAsync().GetAwaiter().GetResult();
    }

    public static ProjectContext? FromProjectFile(string path)
    {
        return LoadAsync(path).GetAwaiter().GetResult();
    }

    /// <summary>
    /// Create a ProjectContext from pre-loaded assemblies.
    /// Use this for embedding nREPL in running applications without needing a .csproj file.
    /// </summary>
    /// <param name="assemblies">Pre-loaded assemblies to make available</param>
    /// <param name="sourcePaths">Optional paths to search for .cljr source files</param>
    public static ProjectContext FromAssemblies(Assembly[] assemblies, string[]? sourcePaths = null)
    {
        var context = new ProjectContext();

        // Set source paths
        if (sourcePaths != null)
        {
            context.SourcePaths.Clear();
            context.SourcePaths.AddRange(sourcePaths);
        }

        // Load assemblies directly (no file paths needed)
        foreach (var assembly in assemblies)
        {
            try
            {
                // Skip dynamic assemblies and assemblies without location
                if (assembly.IsDynamic) continue;

                // Add to loaded assemblies using a synthetic path
                var path = !string.IsNullOrEmpty(assembly.Location)
                    ? assembly.Location
                    : assembly.FullName ?? assembly.GetName().Name ?? Guid.NewGuid().ToString();

                if (context._loadedAssemblies.ContainsKey(path)) continue;

                context._loadedAssemblies[path] = assembly;

                // Build type catalog
                foreach (var type in assembly.GetExportedTypes())
                {
                    context._typeToAssembly[type.FullName ?? type.Name] = assembly;
                }
            }
            catch
            {
                // Skip assemblies that fail to process
            }
        }

        return context;
    }

    private async Task InitializeAsync()
    {
        // 1. Parse .csproj
        ParseProjectFile();

        // 2. Load from project.assets.json (NuGet packages + transitive deps)
        await LoadFromAssetsFileAsync();

        // 3. Load framework assemblies based on SDK
        LoadFrameworkAssemblies();

        // 4. Load project output assemblies
        LoadProjectOutput();

        // 5. Build type catalog for fast lookup
        BuildTypeCatalog();
    }

    private void ParseProjectFile()
    {
        var doc = XDocument.Load(ProjectPath);
        var root = doc.Root;
        if (root == null) return;

        // Get SDK from Project element
        Sdk = root.Attribute("Sdk")?.Value;

        // Get TargetFramework
        var tfm = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TargetFramework")?.Value;
        if (!string.IsNullOrEmpty(tfm))
        {
            TargetFramework = tfm;
        }

        // Also check TargetFrameworks (plural) and take first
        var tfms = root.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "TargetFrameworks")?.Value;
        if (!string.IsNullOrEmpty(tfms))
        {
            TargetFramework = tfms.Split(';')[0];
        }
    }

    private async Task LoadFromAssetsFileAsync()
    {
        var assetsPath = Path.Combine(ProjectDir, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            // Try to restore
            Console.WriteLine("project.assets.json not found, running dotnet restore...");
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "restore",
                WorkingDirectory = ProjectDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            var process = System.Diagnostics.Process.Start(psi);
            if (process != null)
            {
                await process.WaitForExitAsync();
            }
        }

        if (!File.Exists(assetsPath))
        {
            Console.WriteLine("Warning: Could not find or generate project.assets.json");
            return;
        }

        var json = await File.ReadAllTextAsync(assetsPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Get package folders (NuGet cache locations)
        var packageFolders = new List<string>();
        if (root.TryGetProperty("packageFolders", out var folders))
        {
            foreach (var folder in folders.EnumerateObject())
            {
                packageFolders.Add(folder.Name);
            }
        }

        // Get target framework specific packages
        if (root.TryGetProperty("targets", out var targets))
        {
            // Find the matching target (e.g., "net10.0")
            foreach (var target in targets.EnumerateObject())
            {
                if (!target.Name.StartsWith(TargetFramework, StringComparison.OrdinalIgnoreCase))
                    continue;

                foreach (var package in target.Value.EnumerateObject())
                {
                    // Skip framework references (they're handled separately)
                    if (package.Value.TryGetProperty("type", out var typeElem) &&
                        typeElem.GetString() == "project")
                        continue;

                    // Get runtime assemblies
                    if (package.Value.TryGetProperty("runtime", out var runtime))
                    {
                        foreach (var dll in runtime.EnumerateObject())
                        {
                            var dllPath = dll.Name;
                            var packagePath = GetPackagePath(root, package.Name, packageFolders);
                            if (packagePath != null)
                            {
                                var fullPath = Path.Combine(packagePath, dllPath);
                                if (File.Exists(fullPath))
                                {
                                    _assemblyPaths.Add(fullPath);
                                }
                            }
                        }
                    }

                    // Also check compile assemblies if no runtime
                    if (!package.Value.TryGetProperty("runtime", out _) &&
                        package.Value.TryGetProperty("compile", out var compile))
                    {
                        foreach (var dll in compile.EnumerateObject())
                        {
                            if (dll.Name.EndsWith("_._")) continue; // Placeholder

                            var dllPath = dll.Name;
                            var packagePath = GetPackagePath(root, package.Name, packageFolders);
                            if (packagePath != null)
                            {
                                var fullPath = Path.Combine(packagePath, dllPath);
                                if (File.Exists(fullPath))
                                {
                                    _assemblyPaths.Add(fullPath);
                                }
                            }
                        }
                    }
                }
                break; // Only process matching target
            }
        }
    }

    private static string? GetPackagePath(JsonElement root, string packageId, List<string> packageFolders)
    {
        // packageId is like "Humanizer/2.14.1"
        if (!root.TryGetProperty("libraries", out var libraries))
            return null;

        if (!libraries.TryGetProperty(packageId, out var library))
            return null;

        if (!library.TryGetProperty("path", out var pathElem))
            return null;

        var relativePath = pathElem.GetString();
        if (relativePath == null) return null;

        // Find in package folders
        foreach (var folder in packageFolders)
        {
            var fullPath = Path.Combine(folder, relativePath);
            if (Directory.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private void LoadFrameworkAssemblies()
    {
        if (string.IsNullOrEmpty(Sdk)) return;

        // Determine which shared frameworks to load based on SDK
        var frameworksToLoad = new List<string>();

        if (Sdk.Contains("Web", StringComparison.OrdinalIgnoreCase))
        {
            frameworksToLoad.Add("Microsoft.AspNetCore.App");
            frameworksToLoad.Add("Microsoft.NETCore.App");
        }
        else if (Sdk.Contains("Worker", StringComparison.OrdinalIgnoreCase))
        {
            frameworksToLoad.Add("Microsoft.NETCore.App");
        }
        else
        {
            // Default SDK - just load base framework
            frameworksToLoad.Add("Microsoft.NETCore.App");
        }

        var dotnetRoot = GetDotNetRoot();
        var sharedPath = Path.Combine(dotnetRoot, "shared");

        foreach (var framework in frameworksToLoad)
        {
            var frameworkPath = Path.Combine(sharedPath, framework);
            if (!Directory.Exists(frameworkPath)) continue;

            // Find version matching our TFM
            var version = GetFrameworkVersion();
            var versionDir = FindBestVersionDir(frameworkPath, version);

            if (versionDir != null)
            {
                foreach (var dll in Directory.GetFiles(versionDir, "*.dll"))
                {
                    // Skip some problematic assemblies
                    var fileName = Path.GetFileName(dll);
                    if (fileName.StartsWith("System.Private.") ||
                        fileName.Contains("Native"))
                        continue;

                    _assemblyPaths.Add(dll);
                }
            }
        }
    }

    private static string GetDotNetRoot()
    {
        // Check DOTNET_ROOT environment variable first
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        if (!string.IsNullOrEmpty(dotnetRoot) && Directory.Exists(dotnetRoot))
            return dotnetRoot;

        // Platform-specific defaults
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return @"C:\Program Files\dotnet";
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // Check both possible locations on macOS
            if (Directory.Exists("/usr/local/share/dotnet"))
                return "/usr/local/share/dotnet";
            return "/usr/share/dotnet";
        }
        else
        {
            return "/usr/share/dotnet";
        }
    }

    private string GetFrameworkVersion()
    {
        // Extract version from TFM: net10.0 -> 10.0
        var tfm = TargetFramework;
        if (tfm.StartsWith("net"))
        {
            return tfm.Substring(3); // "10.0" from "net10.0"
        }
        return "10.0";
    }

    private static string? FindBestVersionDir(string frameworkPath, string targetVersion)
    {
        if (!Directory.Exists(frameworkPath)) return null;

        var dirs = Directory.GetDirectories(frameworkPath);
        var majorVersion = targetVersion.Split('.')[0]; // "10" from "10.0"

        // Find directories matching major version
        var matching = dirs
            .Where(d => Path.GetFileName(d).StartsWith(majorVersion + "."))
            .OrderByDescending(d => d)
            .ToList();

        if (matching.Count > 0)
            return matching[0];

        // Fallback to latest available
        return dirs.OrderByDescending(d => d).FirstOrDefault();
    }

    private void LoadProjectOutput()
    {
        // Look for compiled output
        var configurations = new[] { "Debug", "Release" };

        foreach (var config in configurations)
        {
            var outputDir = Path.Combine(ProjectDir, "bin", config, TargetFramework);
            if (!Directory.Exists(outputDir)) continue;

            foreach (var dll in Directory.GetFiles(outputDir, "*.dll"))
            {
                // Skip if already in our list
                var fileName = Path.GetFileName(dll);
                if (_assemblyPaths.Any(p => Path.GetFileName(p) == fileName))
                    continue;

                _assemblyPaths.Add(dll);
            }
            break; // Only use first found configuration
        }
    }

    private void BuildTypeCatalog()
    {
        foreach (var path in _assemblyPaths)
        {
            try
            {
                var assembly = Assembly.LoadFrom(path);
                _loadedAssemblies[path] = assembly;

                foreach (var type in assembly.GetExportedTypes())
                {
                    _typeToAssembly[type.FullName ?? type.Name] = assembly;
                }
            }
            catch
            {
                // Skip assemblies that fail to load
            }
        }
    }

    /// <summary>
    /// Resolve an assembly containing the specified type
    /// </summary>
    public Assembly? ResolveAssembly(string fullTypeName)
    {
        if (_typeToAssembly.TryGetValue(fullTypeName, out var assembly))
            return assembly;

        // Try partial match (just type name without namespace)
        var typeName = fullTypeName.Split('.').Last();
        foreach (var kvp in _typeToAssembly)
        {
            if (kvp.Key.EndsWith("." + typeName) || kvp.Key == typeName)
                return kvp.Value;
        }

        return null;
    }

    /// <summary>
    /// Get all loaded assemblies for ScriptOptions
    /// </summary>
    public IEnumerable<Assembly> GetLoadedAssemblies()
    {
        return _loadedAssemblies.Values;
    }

    /// <summary>
    /// Find a .cljr source file for a namespace
    /// </summary>
    public string? FindSourceFile(string ns)
    {
        // Convert namespace to path: my-app.core -> my_app/core.cljr or my-app/core.cljr
        var pathVariants = new[]
        {
            ns.Replace('.', '/').Replace('-', '_') + ".cljr",
            ns.Replace('.', '/') + ".cljr"
        };

        foreach (var sourcePath in SourcePaths)
        {
            var baseDir = Path.IsPathRooted(sourcePath)
                ? sourcePath
                : Path.Combine(ProjectDir, sourcePath);

            foreach (var pathVariant in pathVariants)
            {
                var fullPath = Path.Combine(baseDir, pathVariant);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Print diagnostic information about loaded assemblies
    /// </summary>
    public void PrintDiagnostics()
    {
        Console.WriteLine($"Project: {ProjectPath}");
        Console.WriteLine($"SDK: {Sdk ?? "default"}");
        Console.WriteLine($"Target Framework: {TargetFramework}");
        Console.WriteLine($"Loaded {_assemblyPaths.Count} assembly paths");
        Console.WriteLine($"Type catalog: {_typeToAssembly.Count} types");

        Console.WriteLine("\nSample assemblies:");
        foreach (var path in _assemblyPaths.Take(10))
        {
            Console.WriteLine($"  {Path.GetFileName(path)}");
        }
        if (_assemblyPaths.Count > 10)
        {
            Console.WriteLine($"  ... and {_assemblyPaths.Count - 10} more");
        }
    }
}
