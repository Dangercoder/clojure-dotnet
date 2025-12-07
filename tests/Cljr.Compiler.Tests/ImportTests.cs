using Xunit;
using Xunit.Abstractions;
using Cljr.Repl;

namespace Cljr.Compiler.Tests;

[Collection("VarTests")]
public class ImportTests
{
    private readonly ITestOutputHelper _output;

    public ImportTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task Import_AddsNamespaceToUsings()
    {
        // Arrange
        var session = new NreplSession();

        // Act - import System.Text namespace
        var result = await session.EvalAsync(@"
            (ns test.core
              (:import [System.Text StringBuilder]))
        ");

        // Assert - no error
        Assert.Null(result.Error);

        // Verify import was recorded
        var currentNs = session.ReplState.GetCurrentNamespace();
        Assert.Contains("System.Text", currentNs.Imports);
    }

    [Fact]
    public async Task Import_AllowsCreatingImportedTypes()
    {
        // Arrange
        var session = new NreplSession();

        // Act - import and create StringBuilder
        await session.EvalAsync(@"
            (ns test.core
              (:import [System.Text StringBuilder]))
        ");

        // Create a StringBuilder - this tests that the type is accessible
        var result = await session.EvalAsync(@"(StringBuilder.)");

        _output.WriteLine($"Result: {result.Values?.LastOrDefault()}");
        _output.WriteLine($"Error: {result.Error}");

        // Assert - StringBuilder was created successfully
        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.NotNull(result.Values.LastOrDefault());
        Assert.IsType<System.Text.StringBuilder>(result.Values.LastOrDefault());
    }

    [Fact]
    public async Task Import_WorksWithStaticMethods()
    {
        // Arrange
        var session = new NreplSession();

        // Act - use a static method from imported type
        await session.EvalAsync(@"
            (ns test.core
              (:import [System Guid]))
        ");

        var result = await session.EvalAsync(@"(Guid/NewGuid)");

        _output.WriteLine($"Result: {result.Values?.LastOrDefault()}");
        _output.WriteLine($"Error: {result.Error}");

        // Assert
        Assert.Null(result.Error);
        Assert.NotNull(result.Values);
        Assert.IsType<Guid>(result.Values.LastOrDefault());
    }

    [Fact]
    public async Task ProjectContext_LoadsFromCsproj()
    {
        // Arrange - find the MinimalApi project
        var projectPath = FindMinimalApiProject();
        if (projectPath == null)
        {
            _output.WriteLine("MinimalApi project not found, skipping test");
            return;
        }

        // Act
        var context = await ProjectContext.LoadAsync(projectPath);

        // Assert
        _output.WriteLine($"SDK: {context.Sdk}");
        _output.WriteLine($"TFM: {context.TargetFramework}");
        _output.WriteLine($"Assemblies: {context.AssemblyPaths.Count}");

        Assert.Equal("Microsoft.NET.Sdk.Web", context.Sdk);
        Assert.Equal("net10.0", context.TargetFramework);
        Assert.True(context.AssemblyPaths.Count > 0, "Should have loaded assemblies");

        // Should have ASP.NET Core assemblies
        var hasAspNet = context.AssemblyPaths.Any(p =>
            p.Contains("Microsoft.AspNetCore", StringComparison.OrdinalIgnoreCase));
        Assert.True(hasAspNet, "Should have ASP.NET Core assemblies");
    }

    [Fact]
    public async Task ProjectContext_ResolvesAspNetTypes()
    {
        // Arrange
        var projectPath = FindMinimalApiProject();
        if (projectPath == null)
        {
            _output.WriteLine("MinimalApi project not found, skipping test");
            return;
        }

        var context = await ProjectContext.LoadAsync(projectPath);

        // Act - try to resolve WebApplication type
        var assembly = context.ResolveAssembly("Microsoft.AspNetCore.Builder.WebApplication");

        // Assert
        _output.WriteLine($"Resolved assembly: {assembly?.FullName ?? "null"}");
        Assert.NotNull(assembly);
    }

    [Fact]
    public async Task NreplSession_WithProjectContext_CanImportAspNetTypes()
    {
        // Arrange
        var projectPath = FindMinimalApiProject();
        if (projectPath == null)
        {
            _output.WriteLine("MinimalApi project not found, skipping test");
            return;
        }

        var context = await ProjectContext.LoadAsync(projectPath);
        _output.WriteLine($"Loaded {context.AssemblyPaths.Count} assemblies");

        var session = new NreplSession(context);

        // Act - import ASP.NET types
        var result = await session.EvalAsync(@"
            (ns test.api
              (:import [Microsoft.AspNetCore.Builder WebApplication]))
        ");

        _output.WriteLine($"ns result error: {result.Error}");
        Assert.Null(result.Error);

        // Verify import was recorded
        var currentNs = session.ReplState.GetCurrentNamespace();
        Assert.Contains("Microsoft.AspNetCore.Builder", currentNs.Imports);
    }

    private static string? FindMinimalApiProject()
    {
        // Try various relative paths from the test directory
        var possiblePaths = new[]
        {
            "samples/MinimalApi/MinimalApi.csproj",
            "../../../samples/MinimalApi/MinimalApi.csproj",
            "../../../../samples/MinimalApi/MinimalApi.csproj",
            "../../../../../samples/MinimalApi/MinimalApi.csproj"
        };

        foreach (var path in possiblePaths)
        {
            var fullPath = Path.GetFullPath(path);
            if (File.Exists(fullPath))
                return fullPath;
        }

        // Try from git root
        var gitRoot = FindGitRoot();
        if (gitRoot != null)
        {
            var path = Path.Combine(gitRoot, "samples", "MinimalApi", "MinimalApi.csproj");
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? FindGitRoot()
    {
        var dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir, ".git")))
                return dir;
            dir = Directory.GetParent(dir)?.FullName;
        }
        return null;
    }
}
