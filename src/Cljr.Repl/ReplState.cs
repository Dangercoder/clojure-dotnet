using Cljr;
using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Macros;
using Cljr.Compiler.Reader;

namespace Cljr.Repl;

/// <summary>
/// Persistent state for a REPL session including namespaces, vars, and macros.
/// This state survives across individual REPL evaluations.
/// </summary>
public class ReplState
{
    /// <summary>
    /// All defined namespaces keyed by name
    /// </summary>
    public Dictionary<string, ReplNamespace> Namespaces { get; } = new();

    /// <summary>
    /// Current namespace name
    /// </summary>
    public string CurrentNamespace { get; private set; } = "user";

    /// <summary>
    /// The macro expander shared across all evaluations
    /// </summary>
    public MacroExpander MacroExpander { get; } = new();

    /// <summary>
    /// Global vars (like *ns*, *1, *2, *3)
    /// </summary>
    public Dictionary<string, object?> GlobalVars { get; } = new();

    /// <summary>
    /// Defined types (defrecord/deftype/defprotocol) keyed by name
    /// </summary>
    public Dictionary<string, Type> DefinedTypes { get; } = new();

    /// <summary>
    /// Maps type name to defining Clojure namespace for namespace isolation
    /// </summary>
    private readonly Dictionary<string, string> _typeToDefiningNamespace = new();

    /// <summary>
    /// History of recent results for *1, *2, *3
    /// </summary>
    private readonly Queue<object?> _resultHistory = new();

    public ReplState()
    {
        // Initialize user namespace
        EnsureNamespace("user");

        // Initialize special vars
        GlobalVars["*ns*"] = Symbol.Parse("user");
        GlobalVars["*1"] = null;
        GlobalVars["*2"] = null;
        GlobalVars["*3"] = null;
    }

    /// <summary>
    /// Get or create a namespace
    /// </summary>
    public ReplNamespace EnsureNamespace(string name)
    {
        if (!Namespaces.TryGetValue(name, out var ns))
        {
            ns = new ReplNamespace(name);
            Namespaces[name] = ns;
        }
        return ns;
    }

    /// <summary>
    /// Get current namespace
    /// </summary>
    public ReplNamespace GetCurrentNamespace()
    {
        return EnsureNamespace(CurrentNamespace);
    }

    /// <summary>
    /// Switch to a namespace (creating if needed)
    /// </summary>
    public void SwitchNamespace(string name)
    {
        EnsureNamespace(name);
        CurrentNamespace = name;
        GlobalVars["*ns*"] = Symbol.Parse(name);
    }

    /// <summary>
    /// Define a var in the current namespace
    /// </summary>
    public void DefineVar(string name, object? value, bool isPrivate = false)
    {
        var ns = GetCurrentNamespace();
        ns.Vars[name] = new VarBinding(name, value, isPrivate);
    }

    /// <summary>
    /// Define a macro in the current namespace
    /// </summary>
    public void DefineMacro(string name, MacroDefinition definition)
    {
        var ns = GetCurrentNamespace();
        ns.Macros[name] = definition;
        MacroExpander.RegisterUserMacro(name, definition);
    }

    /// <summary>
    /// Look up a var, checking current namespace and referred namespaces
    /// </summary>
    public VarBinding? LookupVar(string name)
    {
        var ns = GetCurrentNamespace();

        // Check current namespace
        if (ns.Vars.TryGetValue(name, out var binding))
            return binding;

        // Check referred vars
        if (ns.Refers.TryGetValue(name, out var referredVar))
            return referredVar.Binding;

        // Check clojure.core (when we implement it)
        if (Namespaces.TryGetValue("cljr.core", out var core))
        {
            if (core.Vars.TryGetValue(name, out var coreVar) && !coreVar.IsPrivate)
                return coreVar;
        }

        return null;
    }

    /// <summary>
    /// Add a namespace alias
    /// </summary>
    public void AddAlias(string alias, string targetNamespace)
    {
        var ns = GetCurrentNamespace();
        ns.Aliases[alias] = targetNamespace;
    }

    /// <summary>
    /// Refer vars from another namespace
    /// </summary>
    public void ReferVars(string fromNamespace, IEnumerable<string>? only = null)
    {
        var ns = GetCurrentNamespace();

        // Look up vars from the runtime Var registry (where defn stores vars)
        var runtimeVars = Var.GetNamespaceVars(fromNamespace);

        // Filter vars based on 'only' list or privacy
        var varsToRefer = only != null
            ? runtimeVars.Where(v => only.Contains(v.Name))
            : runtimeVars.Where(v => !v.IsPrivate);

        foreach (var v in varsToRefer)
        {
            // Create a VarBinding to track the refer
            var binding = new VarBinding(v.Name, v.GetRoot(), v.IsPrivate);
            ns.Refers[v.Name] = (fromNamespace, binding);
        }
    }

    /// <summary>
    /// Record a result for *1, *2, *3 history
    /// </summary>
    public void RecordResult(object? result)
    {
        // Shift history
        GlobalVars["*3"] = GlobalVars["*2"];
        GlobalVars["*2"] = GlobalVars["*1"];
        GlobalVars["*1"] = result;
    }

    /// <summary>
    /// Register a dynamically compiled type (defrecord/deftype/defprotocol)
    /// </summary>
    public void RegisterType(string name, Type type, string? definingClojureNs = null)
    {
        // Store with qualified name (namespace/type)
        var qualifiedName = $"{CurrentNamespace}/{name}";
        DefinedTypes[qualifiedName] = type;

        // Also store with simple name for current namespace lookups
        DefinedTypes[name] = type;

        // Track which namespace defined this type for namespace isolation
        _typeToDefiningNamespace[name] = definingClojureNs ?? CurrentNamespace;
    }

    /// <summary>
    /// Get a defined type by name
    /// </summary>
    public Type? GetDefinedType(string name)
    {
        // Try qualified name first
        var qualifiedName = $"{CurrentNamespace}/{name}";
        if (DefinedTypes.TryGetValue(qualifiedName, out var type))
            return type;

        // Try simple name
        if (DefinedTypes.TryGetValue(name, out type))
            return type;

        return null;
    }

    /// <summary>
    /// Get the Clojure namespace where a type was defined
    /// </summary>
    public string? GetTypeDefiningNamespace(string typeName)
    {
        return _typeToDefiningNamespace.TryGetValue(typeName, out var ns) ? ns : null;
    }
}

/// <summary>
/// Represents a namespace in the REPL
/// </summary>
public class ReplNamespace
{
    public string Name { get; }
    public Dictionary<string, VarBinding> Vars { get; } = new();
    public Dictionary<string, MacroDefinition> Macros { get; } = new();
    public Dictionary<string, string> Aliases { get; } = new();  // alias -> full ns name
    public Dictionary<string, (string SourceNamespace, VarBinding Binding)> Refers { get; } = new();  // var name -> (source ns, binding)
    public HashSet<string> RequiredNamespaces { get; } = new();
    public HashSet<string> Imports { get; } = new();  // .NET type imports

    public ReplNamespace(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Get C# namespace equivalent
    /// </summary>
    public string CSharpNamespace => string.Join(".",
        Name.Split('.').Select(s => ToPascalCase(s)));

    /// <summary>
    /// Get C# class name (last segment)
    /// </summary>
    public string CSharpClassName
    {
        get
        {
            var parts = Name.Split('.');
            return ToPascalCase(parts[^1]);
        }
    }

    private static string ToPascalCase(string s)
    {
        if (string.IsNullOrEmpty(s)) return s;
        var parts = s.Split('-');
        return string.Concat(parts.Select(p =>
            p.Length > 0 ? char.ToUpper(p[0]) + p[1..] : p));
    }
}

/// <summary>
/// A var binding in a namespace
/// </summary>
public record VarBinding(string Name, object? Value, bool IsPrivate = false);
