using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Emitter;
using Cljr.Compiler.Reader;
using Cljr.Compiler.Macros;

namespace Cljr.Repl;

/// <summary>
/// Represents an nREPL session with persistent state
/// </summary>
public class NreplSession
{
    public string Id { get; } = Guid.NewGuid().ToString();
    public string Namespace => _replState.CurrentNamespace;
    public DateTime LastAccess { get; private set; } = DateTime.UtcNow;

    private ScriptState<object>? _state;
    private ScriptOptions _scriptOptions; // Mutable to support dynamic imports
    private readonly Analyzer _analyzer;
    private readonly CSharpEmitter _emitter = new();
    private readonly ReplState _replState = new();
    private CancellationTokenSource? _cts;

    // Project context for assembly resolution
    private readonly ProjectContext? _projectContext;

    // Track loaded assemblies to avoid duplicates
    private readonly HashSet<string> _loadedAssemblyPaths = new();

    // Track dynamically compiled assemblies by name for assembly resolution
    private readonly Dictionary<string, Assembly> _dynamicAssemblies = new();

    // Cache compiled types by signature to avoid CS0433 conflicts when re-evaluating
    // Key: type signature (namespace.name|field:type|...), Value: (compiled type, assembly)
    private readonly Dictionary<string, (Type Type, Assembly Assembly, MetadataReference Reference)> _typeCache = new();

    public NreplSession() : this((ProjectContext?)null)
    {
    }

    public NreplSession(ProjectContext? projectContext)
    {
        _projectContext = projectContext;

        // Create analyzer with shared macro expander from ReplState
        // This ensures user-defined macros persist across REPL evaluations
        _analyzer = new Analyzer(_replState.MacroExpander);

        // Register assembly resolver for dynamically compiled types
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        _scriptOptions = BuildInitialScriptOptions();
    }

    /// <summary>
    /// Create an NreplSession with pre-loaded assemblies.
    /// Use this for embedding nREPL in applications where you want to provide
    /// access to the app's loaded assemblies without parsing a .csproj file.
    /// </summary>
    /// <param name="assemblies">Assemblies to make available for code completion and evaluation</param>
    /// <param name="sourcePaths">Optional paths to search for .cljr source files</param>
    public NreplSession(Assembly[] assemblies, string[]? sourcePaths = null)
    {
        // Create a lightweight ProjectContext from the provided assemblies
        _projectContext = ProjectContext.FromAssemblies(assemblies, sourcePaths);

        // Create analyzer with shared macro expander from ReplState
        _analyzer = new Analyzer(_replState.MacroExpander);

        // Register assembly resolver for dynamically compiled types
        AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

        _scriptOptions = BuildInitialScriptOptions();
    }

    private ScriptOptions BuildInitialScriptOptions()
    {
        var options = ScriptOptions.Default
            .AddReferences(typeof(Core).Assembly)
            .AddReferences(typeof(Console).Assembly)
            .AddReferences(typeof(List<>).Assembly)
            .AddReferences(typeof(System.Linq.Enumerable).Assembly)
            .AddReferences(typeof(Symbol).Assembly) // Cljr.Compiler for Symbol type
            .AddImports("System", "System.Collections.Generic", "System.Linq", "Cljr", "Cljr.Collections")
            .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

        // Track base assemblies
        _loadedAssemblyPaths.Add(typeof(Core).Assembly.Location);
        _loadedAssemblyPaths.Add(typeof(Console).Assembly.Location);
        _loadedAssemblyPaths.Add(typeof(List<>).Assembly.Location);
        _loadedAssemblyPaths.Add(typeof(System.Linq.Enumerable).Assembly.Location);
        _loadedAssemblyPaths.Add(typeof(Symbol).Assembly.Location);

        // Add project assemblies if available
        if (_projectContext != null)
        {
            foreach (var assembly in _projectContext.GetLoadedAssemblies())
            {
                if (!string.IsNullOrEmpty(assembly.Location) &&
                    !_loadedAssemblyPaths.Contains(assembly.Location))
                {
                    try
                    {
                        options = options.AddReferences(assembly);
                        _loadedAssemblyPaths.Add(assembly.Location);
                    }
                    catch
                    {
                        // Skip assemblies that fail to add
                    }
                }
            }
        }

        return options;
    }

    /// <summary>
    /// Get the REPL state for testing/inspection
    /// </summary>
    public ReplState ReplState => _replState;

    /// <summary>
    /// Get the project context if available
    /// </summary>
    public ProjectContext? ProjectContext => _projectContext;

    public void Touch() => LastAccess = DateTime.UtcNow;

    public async Task<EvalResult> EvalAsync(string code)
    {
        Touch();
        _cts = new CancellationTokenSource();

        try
        {
            var forms = LispReader.ReadAll(code).ToList();
            var results = new List<object?>();
            string? output = null;
            string? error = null;

            // Capture stdout
            var originalOut = Console.Out;
            var sw = new StringWriter();
            Console.SetOut(sw);

            try
            {
                foreach (var form in forms)
                {
                    // Check for special REPL forms that need direct handling
                    var result = await EvalFormAsync(form);
                    results.Add(result);

                    // Record result for *1, *2, *3 history
                    _replState.RecordResult(result);
                }

                output = sw.ToString();
            }
            catch (Exception ex)
            {
                error = TranslateErrorMessage(ex.Message);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            return new EvalResult
            {
                Values = results,
                Output = string.IsNullOrEmpty(output) ? null : output,
                Error = error,
                Namespace = Namespace
            };
        }
        catch (ReaderException ex)
        {
            return new EvalResult { Error = $"Reader error: {ex.Message}", Namespace = Namespace };
        }
        catch (AnalyzerException ex)
        {
            return new EvalResult { Error = $"Analyzer error: {ex.Message}", Namespace = Namespace };
        }
        catch (OperationCanceledException)
        {
            return new EvalResult { Error = "Evaluation interrupted", Namespace = Namespace };
        }
        catch (Exception ex)
        {
            return new EvalResult { Error = ex.Message, Namespace = Namespace };
        }
        finally
        {
            _cts = null;
        }
    }

    private async Task<object?> EvalFormAsync(object? form)
    {
        // Handle special REPL vars directly
        // Use cross-assembly safe symbol checking
        var (isSymbol, symName, symNs) = GetSymbolInfo(form);
        if (isSymbol && symNs is null)
        {
            if (symName == "*ns*")
                return Symbol.Parse(_replState.CurrentNamespace);
            if (symName == "*1")
                return _replState.GlobalVars.GetValueOrDefault("*1");
            if (symName == "*2")
                return _replState.GlobalVars.GetValueOrDefault("*2");
            if (symName == "*3")
                return _replState.GlobalVars.GetValueOrDefault("*3");
        }

        // Check for in-ns and ns forms and update ReplState
        // Use cross-assembly safe type checking
        var (isList, listItems) = GetListInfo(form);
        if (isList && listItems != null && listItems.Count >= 1)
        {
            var (headIsSymbol, headName, _) = GetSymbolInfo(listItems[0]);
            if (headIsSymbol)
            {
                if (headName == "in-ns")
                {
                    // Extract namespace name and switch
                    var nsName = ExtractNamespaceName(listItems);
                    if (nsName != null)
                    {
                        _replState.SwitchNamespace(nsName);
                        return Symbol.Parse(nsName);
                    }
                }

                if (headName == "ns")
                {
                    // Process full ns form through analyzer to get imports/requires
                    return await ProcessNsFormAsync(form);
                }

                if (headName == "require")
                {
                    // Process standalone require form
                    return await ProcessRequireFormAsync(form);
                }
            }
        }

        // Standard evaluation path
        var expr = _analyzer.Analyze(form, new AnalyzerContext { IsRepl = true });

        // Handle type definitions (defrecord/deftype/defprotocol) via dynamic compilation
        // Roslyn scripting doesn't support top-level type declarations
        // We cache compiled types by signature to avoid CS0433 conflicts when re-evaluating
        if (expr is DefrecordExpr or DeftypeExpr or DefprotocolExpr)
        {
            var typeName = expr switch
            {
                DefrecordExpr r => r.Name.Name,
                DeftypeExpr t => t.Name.Name,
                DefprotocolExpr p => p.Name.Name,
                _ => throw new InvalidOperationException()
            };

            var csharpNs = _replState.CurrentNamespace.Replace("-", "_").Replace(".", "_");
            var signature = ComputeTypeSignature(expr, csharpNs);
            var typeDefNs = _replState.GetCurrentNamespace();

            // Check if we already have an identical type compiled
            if (_typeCache.TryGetValue(signature, out var cached))
            {
                // Reuse the cached type - no recompilation needed
                // Ensure the namespace is in current namespace's imports (namespace-scoped visibility)
                typeDefNs.Imports.Add(csharpNs);
                return cached.Type;
            }

            // Compile new type
            var compiled = CompileTypeDefinition(expr, typeName);
            if (compiled != null)
            {
                var (assembly, reference) = compiled.Value;
                _scriptOptions = _scriptOptions.AddReferences(reference);
                // Add namespace to current namespace's imports (namespace-scoped, not global)
                // This ensures type is only visible in the namespace where it was defined
                // and in namespaces that explicitly require it
                typeDefNs.Imports.Add(csharpNs);
                var type = assembly.GetType($"{csharpNs}.{typeName}");
                if (type != null)
                {
                    _replState.RegisterType(typeName, type, _replState.CurrentNamespace);
                    // Cache the compiled type for future re-evaluations
                    _typeCache[signature] = (type, assembly, reference);

                    // Create factory functions for defrecord (->TypeName and map->TypeName)
                    if (expr is DefrecordExpr defrecord)
                    {
                        CreateRecordFactoryFunctions(type, typeName, defrecord);
                    }
                }
                return type;
            }
            return null;
        }

        // Handle require forms - process require clauses for file loading and alias registration
        if (expr is RequireExpr requireExpr)
        {
            foreach (var clause in requireExpr.Clauses)
            {
                await ProcessRequireAsync(clause);
            }
            return null; // require returns nil like Clojure
        }

        var currentNs = _replState.GetCurrentNamespace();
        var refers = currentNs.Refers.ToDictionary(kv => kv.Key, kv => kv.Value.SourceNamespace);

        // Create type resolver for namespace isolation
        // Always returns fully qualified type names for REPL-defined types to avoid
        // ambiguity with types from other namespaces that Roslyn's script continuation sees
        Func<string, string> typeResolver = typeName =>
        {
            var definingNs = _replState.GetTypeDefiningNamespace(typeName);
            if (definingNs == null) return typeName;  // External .NET type - use as-is

            var typeCsharpNs = definingNs.Replace("-", "_").Replace(".", "_");
            var currentCsharpNs = _replState.CurrentNamespace.Replace("-", "_").Replace(".", "_");

            // If defined in current namespace, use fully qualified name to avoid
            // ambiguity with same-named types from other namespaces
            if (typeCsharpNs == currentCsharpNs) return $"{currentCsharpNs}.{typeName}";

            // If the type's C# namespace is imported, use fully qualified name
            if (currentNs.Imports.Contains(typeCsharpNs)) return $"{typeCsharpNs}.{typeName}";

            // Type NOT accessible - emit invalid namespace to trigger compile error
            // This enforces require/import semantics like Clojure
            return $"__TYPE_NOT_ACCESSIBLE_{definingNs.Replace("-", "_").Replace(".", "_")}__.{typeName}";
        };

        var csharp = _emitter.EmitScript(expr, _replState.CurrentNamespace, currentNs.Aliases, refers, typeResolver);

        // Build script with dynamic usings
        var script = BuildScriptWrapper(csharp);

        if (_state == null)
        {
            _state = await CSharpScript.RunAsync<object>(script, _scriptOptions, cancellationToken: _cts!.Token);
        }
        else
        {
            // Pass updated _scriptOptions to pick up new imports from defrecord/deftype/defprotocol
            _state = await _state.ContinueWithAsync<object>(script, _scriptOptions, cancellationToken: _cts!.Token);
        }

        return _state.ReturnValue;
    }

    /// <summary>
    /// Process a full ns form, extracting and handling imports and requires
    /// </summary>
    private async Task<object?> ProcessNsFormAsync(object? form)
    {
        // Analyze the ns form to get NsExpr with imports and requires
        var expr = _analyzer.Analyze(form, new AnalyzerContext { IsRepl = true });

        if (expr is not NsExpr nsExpr)
        {
            // Fallback to simple namespace extraction using cross-assembly safe type checking
            var (isList, listItems) = GetListInfo(form);
            if (isList && listItems != null)
            {
                var nsName = ExtractNsFormName(listItems);
                if (nsName != null)
                {
                    _replState.SwitchNamespace(nsName);
                }
            }
            return null;
        }

        // Switch to the namespace
        _replState.SwitchNamespace(nsExpr.Name);

        // Process imports
        foreach (var import in nsExpr.Imports)
        {
            ProcessImport(import);
        }

        // Process requires
        foreach (var require in nsExpr.Requires)
        {
            await ProcessRequireAsync(require);
        }

        return null; // ns returns nil like Clojure
    }

    /// <summary>
    /// Process a standalone require form: (require 'foo.bar) or (require '[foo.bar :as fb])
    /// </summary>
    private async Task<object?> ProcessRequireFormAsync(object? form)
    {
        // Analyze the require form to get RequireExpr with clauses
        var expr = _analyzer.Analyze(form, new AnalyzerContext { IsRepl = true });

        if (expr is not RequireExpr requireExpr)
        {
            throw new Exception("Invalid require form");
        }

        // Process each require clause
        foreach (var clause in requireExpr.Clauses)
        {
            await ProcessRequireAsync(clause);
        }

        return null; // require returns nil like Clojure
    }

    /// <summary>
    /// Process an import clause - add namespace to usings and load assembly if needed.
    /// For REPL-defined types (Clojure namespaces with hyphens), also adds the munged C# namespace.
    /// </summary>
    private void ProcessImport(ImportClause import)
    {
        var currentNs = _replState.GetCurrentNamespace();

        // Add namespace to current namespace's imports (namespace-scoped visibility)
        currentNs.Imports.Add(import.Namespace);

        // For REPL-defined types, also add the munged C# namespace.
        // Clojure namespaces use hyphens (e.g., "minimal-api.main") which get munged to underscores.
        // Only apply munging for Clojure-style namespaces (those with hyphens), not .NET namespaces.
        if (import.Namespace.Contains('-'))
        {
            var csharpNs = import.Namespace.Replace("-", "_").Replace(".", "_");
            currentNs.Imports.Add(csharpNs);
        }

        // Try to load assembly if we have project context
        if (_projectContext != null)
        {
            foreach (var typeName in import.Types)
            {
                var fullTypeName = $"{import.Namespace}.{typeName}";
                var assembly = _projectContext.ResolveAssembly(fullTypeName);

                if (assembly != null && !string.IsNullOrEmpty(assembly.Location) &&
                    !_loadedAssemblyPaths.Contains(assembly.Location))
                {
                    try
                    {
                        _scriptOptions = _scriptOptions.AddReferences(assembly);
                        _loadedAssemblyPaths.Add(assembly.Location);
                    }
                    catch
                    {
                        // Skip if assembly fails to add
                    }
                }
            }
        }
    }

    /// <summary>
    /// Process a require clause - load the .cljr file or handle in-memory namespace
    /// </summary>
    private async Task ProcessRequireAsync(RequireClause require)
    {
        // Check if the namespace already exists in memory (created via in-ns or previously loaded)
        var namespaceExistsInMemory = _replState.Namespaces.ContainsKey(require.Namespace);

        if (!namespaceExistsInMemory)
        {
            // Find the source file
            string? sourcePath = null;

            if (_projectContext != null)
            {
                sourcePath = _projectContext.FindSourceFile(require.Namespace);
            }
            else
            {
                // Try default paths
                sourcePath = FindSourceFile(require.Namespace);
            }

            if (sourcePath == null)
            {
                throw new Exception($"Cannot find namespace: {require.Namespace}");
            }

            // Load and eval the file
            var source = await File.ReadAllTextAsync(sourcePath);
            await EvalAsync(source);
        }

        // Handle :as alias (works for both file-backed and in-memory namespaces)
        if (require.Alias != null)
        {
            _replState.AddAlias(require.Alias, require.Namespace);
        }

        // Handle :refer
        if (require.Refers != null)
        {
            _replState.ReferVars(require.Namespace, require.Refers);
        }

        // NOTE: We intentionally do NOT add the required namespace to imports here.
        // This enforces Clojure-like semantics where types must be accessed via alias
        // (e.g., api/CreateTodoRequest.) rather than directly (CreateTodoRequest.).
        // Types are only directly accessible in their defining namespace.
    }

    /// <summary>
    /// Find a source file for a namespace without project context
    /// </summary>
    private static string? FindSourceFile(string ns)
    {
        var basePaths = new[] { "src", "." };
        var pathVariants = new[]
        {
            ns.Replace('.', '/').Replace('-', '_') + ".cljr",
            ns.Replace('.', '/') + ".cljr"
        };

        foreach (var basePath in basePaths)
        {
            foreach (var pathVariant in pathVariants)
            {
                var fullPath = Path.Combine(basePath, pathVariant);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Build script wrapper with standard and dynamic usings
    /// </summary>
    private string BuildScriptWrapper(string csharp)
    {
        var sb = new StringBuilder();

        // Standard usings
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using Cljr;");
        sb.AppendLine("using Cljr.Collections;");
        sb.AppendLine("using static Cljr.Core;");

        // Dynamic usings from current namespace's imports only (namespace-scoped visibility)
        // This ensures types defined in other namespaces are only visible if explicitly imported/required
        var currentNs = _replState.GetCurrentNamespace();
        foreach (var ns in currentNs.Imports)
        {
            sb.AppendLine($"using {ns};");
        }

        sb.Append(csharp);
        return sb.ToString();
    }

    /// <summary>
    /// Extract namespace name from in-ns form.
    /// Uses cross-assembly safe type checking.
    /// </summary>
    private static string? ExtractNamespaceName(IReadOnlyList<object?> listItems)
    {
        if (listItems.Count != 2) return null;

        var arg = listItems[1];

        // Handle quoted symbol: (in-ns 'foo.bar)
        var (isQuotedList, quotedItems) = GetListInfo(arg);
        if (isQuotedList && quotedItems != null && quotedItems.Count == 2)
        {
            var (isQuoteSym, quoteName, _) = GetSymbolInfo(quotedItems[0]);
            if (isQuoteSym && quoteName == "quote")
            {
                var (isQuotedSym, quotedSymName, quotedSymNs) = GetSymbolInfo(quotedItems[1]);
                if (isQuotedSym && quotedSymName != null)
                {
                    return quotedSymNs is not null
                        ? $"{quotedSymNs}.{quotedSymName}"
                        : quotedSymName;
                }
            }
        }

        // Handle direct symbol
        var (isSymbol, symName, symNs) = GetSymbolInfo(arg);
        if (isSymbol && symName != null)
        {
            return symNs is not null
                ? $"{symNs}.{symName}"
                : symName;
        }

        return null;
    }

    /// <summary>
    /// Extract namespace name from ns form: (ns foo.bar ...)
    /// Unlike in-ns, ns takes an unquoted symbol
    /// Uses cross-assembly safe type checking.
    /// </summary>
    private static string? ExtractNsFormName(IReadOnlyList<object?> listItems)
    {
        if (listItems.Count < 2) return null;

        var arg = listItems[1];

        // ns takes an unquoted symbol: (ns foo.bar ...)
        var (isSymbol, symName, symNs) = GetSymbolInfo(arg);
        if (isSymbol && symName != null)
        {
            return symNs is not null
                ? $"{symNs}.{symName}"
                : symName;
        }

        return null;
    }

    #region Cross-Assembly Type Helpers

    /// <summary>
    /// Check if an object is a Symbol and extract its name and namespace.
    /// Works across assembly boundaries using reflection as fallback.
    /// </summary>
    private static (bool IsSymbol, string? Name, string? Namespace) GetSymbolInfo(object? obj)
    {
        if (obj is null) return (false, null, null);

        // Direct type check first
        if (obj is Symbol sym)
            return (true, sym.Name, sym.Namespace);

        // Fallback: use reflection for assembly loading issues
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.Symbol")
        {
            var name = (string?)type.GetProperty("Name")?.GetValue(obj);
            var ns = (string?)type.GetProperty("Namespace")?.GetValue(obj);
            return (true, name, ns);
        }

        return (false, null, null);
    }

    /// <summary>
    /// Check if an object is a PersistentList and get its items.
    /// Works across assembly boundaries using reflection as fallback.
    /// </summary>
    private static (bool IsList, IReadOnlyList<object?>? Items) GetListInfo(object? obj)
    {
        if (obj is null) return (false, null);

        // Direct type check first
        if (obj is PersistentList list)
            return (true, list);

        // Fallback: use reflection for assembly loading issues
        var type = obj.GetType();
        if (type.FullName == "Cljr.Compiler.Reader.PersistentList")
        {
            // PersistentList implements IReadOnlyList<object?>
            if (obj is IReadOnlyList<object?> items)
                return (true, items);
        }

        return (false, null);
    }

    #endregion

    public void Interrupt()
    {
        _cts?.Cancel();
    }

    /// <summary>
    /// Compute a signature for a type definition that uniquely identifies its structure.
    /// Used for caching to avoid recompiling identical type definitions.
    /// </summary>
    private string ComputeTypeSignature(Expr expr, string csharpNs)
    {
        var sb = new StringBuilder();
        sb.Append(csharpNs).Append('.');

        switch (expr)
        {
            case DefrecordExpr defrecord:
                sb.Append("record:").Append(defrecord.Name.Name);
                foreach (var field in defrecord.Fields)
                {
                    sb.Append('|').Append(field.Name.Name).Append(':').Append(field.TypeHint ?? "object?");
                }
                foreach (var iface in defrecord.Interfaces)
                {
                    sb.Append('+').Append(iface.Namespace != null ? $"{iface.Namespace}.{iface.Name}" : iface.Name);
                }
                foreach (var method in defrecord.Methods)
                {
                    AppendMethodSignature(sb, method);
                }
                break;

            case DeftypeExpr deftype:
                sb.Append("type:").Append(deftype.Name.Name);
                foreach (var field in deftype.Fields)
                {
                    sb.Append('|').Append(field.Name.Name).Append(':').Append(field.TypeHint ?? "object?");
                }
                foreach (var iface in deftype.Interfaces)
                {
                    sb.Append('+').Append(iface.Namespace != null ? $"{iface.Namespace}.{iface.Name}" : iface.Name);
                }
                foreach (var method in deftype.Methods)
                {
                    AppendMethodSignature(sb, method);
                }
                break;

            case DefprotocolExpr defprotocol:
                sb.Append("protocol:").Append(defprotocol.Name.Name);
                foreach (var method in defprotocol.Methods)
                {
                    sb.Append('#').Append(method.Name.Name);
                    sb.Append('(').Append(method.Params.Count).Append(')');
                    if (method.ReturnType != null)
                        sb.Append(':').Append(method.ReturnType);
                }
                break;
        }

        return sb.ToString();
    }

    private static void AppendMethodSignature(StringBuilder sb, TypeMethodImpl method)
    {
        sb.Append('#').Append(method.Name.Name);
        sb.Append('(');
        if (method.ParamTypes != null)
        {
            sb.Append(string.Join(",", method.ParamTypes.Select(t => t ?? "object?")));
        }
        else
        {
            sb.Append(string.Join(",", method.Params.Select(_ => "object?")));
        }
        sb.Append(')');
        if (method.ReturnType != null)
            sb.Append(':').Append(method.ReturnType);
    }

    /// <summary>
    /// Compile a type definition (defrecord/deftype/defprotocol) into a dynamic assembly.
    /// This is needed because Roslyn scripting doesn't support top-level type declarations.
    /// Returns both the loaded assembly and a MetadataReference for adding to ScriptOptions.
    /// </summary>
    private (Assembly Assembly, MetadataReference Reference)? CompileTypeDefinition(Expr expr, string typeName)
    {
        var sb = new StringBuilder();

        // Standard usings
        sb.AppendLine("using System;");
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine("using System.Linq;");
        sb.AppendLine("using Cljr;");
        sb.AppendLine("using Cljr.Collections;");

        // Dynamic usings from current namespace's imports only (namespace-scoped visibility)
        var currentNs = _replState.GetCurrentNamespace();
        foreach (var ns in currentNs.Imports)
        {
            sb.AppendLine($"using {ns};");
        }

        // Namespace - convert clojure-style to C#-style
        var csharpNs = _replState.CurrentNamespace.Replace("-", "_").Replace(".", "_");
        sb.AppendLine();
        sb.AppendLine($"namespace {csharpNs}");
        sb.AppendLine("{");

        // Emit the type definition using the emitter's existing logic
        _emitter.EmitTypeDefinition(expr, sb);

        sb.AppendLine("}");

        var sourceCode = sb.ToString();
        var syntaxTree = CSharpSyntaxTree.ParseText(sourceCode);

        // Get references from current ScriptOptions - filter out unresolved ones
        var references = _scriptOptions.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Cast<MetadataReference>()
            .ToList();

        // Add System.Runtime if not present (needed for records)
        var runtimeAssembly = typeof(object).Assembly;
        if (!references.Any(r => r.Display?.Contains("System.Runtime") == true))
        {
            references.Add(MetadataReference.CreateFromFile(runtimeAssembly.Location));
        }

        var compilation = CSharpCompilation.Create(
            $"CljrRepl_{typeName}_{Guid.NewGuid():N}",
            new[] { syntaxTree },
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

        using var ms = new MemoryStream();
        var result = compilation.Emit(ms);

        if (!result.Success)
        {
            var errors = result.Diagnostics
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => d.GetMessage());
            throw new Exception($"Failed to compile type definition:\n{string.Join("\n", errors)}\n\nGenerated code:\n{sourceCode}");
        }

        ms.Seek(0, SeekOrigin.Begin);
        var bytes = ms.ToArray();
        var assembly = Assembly.Load(bytes);

        // Register the assembly for future resolution by name
        var assemblyName = assembly.GetName().Name;
        if (assemblyName != null)
        {
            _dynamicAssemblies[assemblyName] = assembly;
        }

        // Create metadata reference from bytes (not from assembly which may lack location)
        var metadataReference = MetadataReference.CreateFromImage(bytes);

        return (assembly, metadataReference);
    }

    /// <summary>
    /// Assembly resolver for dynamically compiled types
    /// </summary>
    private Assembly? OnAssemblyResolve(object? sender, ResolveEventArgs args)
    {
        // Extract simple name from full assembly name
        var assemblyName = new AssemblyName(args.Name).Name;
        if (assemblyName != null && _dynamicAssemblies.TryGetValue(assemblyName, out var assembly))
        {
            return assembly;
        }
        return null;
    }

    /// <summary>
    /// Translate cryptic error messages into helpful user-facing messages
    /// </summary>
    private string TranslateErrorMessage(string message)
    {
        // Detect our special __TYPE_NOT_ACCESSIBLE__ marker
        // Pattern: "__TYPE_NOT_ACCESSIBLE_some_namespace__"
        const string marker = "__TYPE_NOT_ACCESSIBLE_";
        var markerIndex = message.IndexOf(marker, StringComparison.Ordinal);

        if (markerIndex >= 0)
        {
            // Extract the namespace from the marker
            var startIndex = markerIndex + marker.Length;
            var endIndex = message.IndexOf("__", startIndex, StringComparison.Ordinal);

            if (endIndex > startIndex)
            {
                var csharpNs = message[startIndex..endIndex];
                // Convert C# namespace back to Clojure namespace (approximate)
                var clojureNs = csharpNs.Replace("_", "-");

                // Try to extract the type name from the error message
                // Pattern: "The type or namespace name 'SomeType'"
                var typeName = "the type";
                var typeMatch = System.Text.RegularExpressions.Regex.Match(
                    message, @"__TYPE_NOT_ACCESSIBLE_[^_]+__\.(\w+)");
                if (typeMatch.Success)
                {
                    typeName = typeMatch.Groups[1].Value;
                }

                return $"Type '{typeName}' is not accessible from the current namespace.\n" +
                       $"It is defined in namespace '{clojureNs}'.\n" +
                       $"To use it, require the namespace with an alias:\n" +
                       $"  (require '[{clojureNs} :as alias])\n" +
                       $"Then use: (alias/{typeName}. ...)";
            }
        }

        return message;
    }

    /// <summary>
    /// Creates factory functions for a defrecord type.
    /// Clojure creates two factory functions for each defrecord:
    /// - ->TypeName (positional factory): takes args in field order
    /// - map->TypeName (map factory): takes a map with keyword keys
    /// </summary>
    private void CreateRecordFactoryFunctions(Type type, string typeName, DefrecordExpr defrecord)
    {
        var ns = _replState.CurrentNamespace;

        // Create ->TypeName (positional factory)
        var positionalFactory = CreatePositionalFactory(type, defrecord.Fields.Count);
        Var.Intern(ns, $"->{typeName}").BindRoot(positionalFactory);

        // Create map->TypeName (map factory)
        var mapFactory = CreateMapFactory(type, defrecord);
        Var.Intern(ns, $"map->{typeName}").BindRoot(mapFactory);
    }

    /// <summary>
    /// Creates a positional factory function that invokes the type's constructor.
    /// Returns appropriate Func delegate based on arity.
    /// </summary>
    private object CreatePositionalFactory(Type type, int fieldCount)
    {
        var ctor = type.GetConstructors().FirstOrDefault()
            ?? throw new InvalidOperationException($"Type {type.Name} has no public constructor");

        return fieldCount switch
        {
            0 => (Func<object?>)(() => ctor.Invoke(Array.Empty<object>())),
            1 => (Func<object?, object?>)(a => ctor.Invoke(new[] { a })),
            2 => (Func<object?, object?, object?>)((a, b) => ctor.Invoke(new[] { a, b })),
            3 => (Func<object?, object?, object?, object?>)((a, b, c) => ctor.Invoke(new[] { a, b, c })),
            4 => (Func<object?, object?, object?, object?, object?>)((a, b, c, d) => ctor.Invoke(new[] { a, b, c, d })),
            5 => (Func<object?, object?, object?, object?, object?, object?>)((a, b, c, d, e) => ctor.Invoke(new[] { a, b, c, d, e })),
            6 => (Func<object?, object?, object?, object?, object?, object?, object?>)((a, b, c, d, e, f) => ctor.Invoke(new[] { a, b, c, d, e, f })),
            _ => CreateVariadicFactory(ctor)
        };
    }

    /// <summary>
    /// Creates a variadic factory function for types with more than 6 fields.
    /// </summary>
    private static object CreateVariadicFactory(ConstructorInfo ctor)
    {
        return (Func<object?[], object?>)(args => ctor.Invoke(args));
    }

    /// <summary>
    /// Creates a map factory function that extracts field values from a map using keyword keys.
    /// </summary>
    private object CreateMapFactory(Type type, DefrecordExpr defrecord)
    {
        var ctor = type.GetConstructors().FirstOrDefault()
            ?? throw new InvalidOperationException($"Type {type.Name} has no public constructor");

        // Pre-intern RUNTIME keywords for field names (Cljr.Keyword, not Reader.Keyword)
        // The map passed at runtime uses Cljr.Keyword, so we must use the same type for lookups
        var fieldKeywords = defrecord.Fields.Select(f => Cljr.Keyword.Intern(f.Name.Name)).ToArray();

        return (Func<object?, object?>)(map =>
        {
            var args = new object?[fieldKeywords.Length];
            if (map is ILookup lookup)
            {
                for (int i = 0; i < fieldKeywords.Length; i++)
                {
                    args[i] = lookup.ValAt(fieldKeywords[i]);
                }
            }
            return ctor.Invoke(args);
        });
    }
}

public class EvalResult
{
    public List<object?>? Values { get; init; }
    public string? Output { get; init; }
    public string? Error { get; init; }
    public string? Namespace { get; init; }
}
