using System.Collections.Concurrent;

namespace Cljr;

/// <summary>
/// RuntimeNamespace - tracks vars and their bindings at runtime.
/// This is the runtime counterpart to compile-time namespace tracking.
/// </summary>
public sealed class RuntimeNamespace
{
    private static readonly ConcurrentDictionary<string, RuntimeNamespace> _namespaces = new();

    /// <summary>The name of this namespace</summary>
    public string Name { get; }

    /// <summary>Aliases mapping short name to full namespace name</summary>
    public ConcurrentDictionary<string, string> Aliases { get; } = new();

    /// <summary>Referred vars mapping local name to full var</summary>
    public ConcurrentDictionary<string, Var> Refers { get; } = new();

    /// <summary>Imported types</summary>
    public ConcurrentDictionary<string, Type> Imports { get; } = new();

    /// <summary>Namespace dependencies (requires)</summary>
    public ConcurrentDictionary<string, bool> Dependencies { get; } = new();

    /// <summary>Source file path if loaded from file</summary>
    public string? SourcePath { get; set; }

    /// <summary>Last modification time of source file</summary>
    public DateTime LastModified { get; set; }

    private RuntimeNamespace(string name)
    {
        Name = name;
    }

    /// <summary>
    /// Finds or creates a namespace by name. Thread-safe.
    /// </summary>
    public static RuntimeNamespace FindOrCreate(string name) =>
        _namespaces.GetOrAdd(name, n => new RuntimeNamespace(n));

    /// <summary>
    /// Finds a namespace by name. Returns null if not found.
    /// </summary>
    public static RuntimeNamespace? Find(string name) =>
        _namespaces.TryGetValue(name, out var ns) ? ns : null;

    /// <summary>
    /// Gets all namespace names.
    /// </summary>
    public static IEnumerable<string> AllNamespaces => _namespaces.Keys;

    /// <summary>
    /// Interns a var in this namespace.
    /// </summary>
    public Var Intern(string name)
    {
        var v = Var.Intern(Name, name);
        return v;
    }

    /// <summary>
    /// Gets a var by name in this namespace.
    /// First checks local vars, then refers, then aliases.
    /// </summary>
    public Var? Resolve(string name)
    {
        // Check local var first
        var localVar = Var.Find(Name, name);
        if (localVar is not null)
            return localVar;

        // Check refers
        if (Refers.TryGetValue(name, out var referred))
            return referred;

        // Check qualified name with aliases
        var slashIdx = name.IndexOf('/');
        if (slashIdx > 0)
        {
            var alias = name[..slashIdx];
            var varName = name[(slashIdx + 1)..];

            if (Aliases.TryGetValue(alias, out var fullNs))
                return Var.Find(fullNs, varName);

            // Try as full namespace name
            return Var.Find(alias, varName);
        }

        return null;
    }

    /// <summary>
    /// Gets all vars defined in this namespace (not refers).
    /// </summary>
    public IEnumerable<Var> Vars => Var.GetNamespaceVars(Name);

    /// <summary>
    /// Gets all public vars in this namespace.
    /// </summary>
    public IEnumerable<Var> PublicVars => Vars.Where(v => !v.IsPrivate);

    /// <summary>
    /// Adds a refer from another namespace.
    /// </summary>
    public void AddRefer(string localName, Var v)
    {
        Refers[localName] = v;
    }

    /// <summary>
    /// Adds an alias for another namespace.
    /// </summary>
    public void AddAlias(string alias, string fullNamespace)
    {
        Aliases[alias] = fullNamespace;
    }

    /// <summary>
    /// Adds a type import.
    /// </summary>
    public void AddImport(string shortName, Type type)
    {
        Imports[shortName] = type;
    }

    /// <summary>
    /// Adds a dependency on another namespace.
    /// </summary>
    public void AddDependency(string ns)
    {
        Dependencies[ns] = true;
    }

    /// <summary>
    /// Gets namespaces that depend on this one.
    /// </summary>
    public IEnumerable<RuntimeNamespace> GetDependents() =>
        _namespaces.Values.Where(ns => ns.Dependencies.ContainsKey(Name));

    /// <summary>
    /// Clears this namespace's state (for reload).
    /// Does NOT clear the vars themselves - that's done separately
    /// so atoms can be preserved.
    /// </summary>
    public void Clear()
    {
        Aliases.Clear();
        Refers.Clear();
        Imports.Clear();
        Dependencies.Clear();
    }

    /// <summary>
    /// Removes a namespace entirely.
    /// </summary>
    public static bool Remove(string name) =>
        _namespaces.TryRemove(name, out _);

    /// <summary>
    /// Clears all namespaces (used in tests).
    /// </summary>
    public static void ClearAll()
    {
        _namespaces.Clear();
    }

    public override string ToString() => Name;
}
