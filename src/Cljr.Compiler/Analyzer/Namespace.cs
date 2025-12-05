namespace Cljr.Compiler.Analyzer;

/// <summary>
/// Represents a Clojure namespace
/// </summary>
public class Namespace
{
    public string Name { get; }
    public Dictionary<string, VarInfo> Mappings { get; } = new();
    public Dictionary<string, Namespace> Aliases { get; } = new();
    public HashSet<string> Imports { get; } = new();

    public Namespace(string name) => Name = name;

    /// <summary>
    /// Get the C# namespace name (convert kebab-case to PascalCase)
    /// </summary>
    public string CSharpNamespace => ToCSharpNamespace(Name);

    /// <summary>
    /// Get the C# class name (last segment of namespace)
    /// </summary>
    public string CSharpClassName
    {
        get
        {
            var lastDot = Name.LastIndexOf('.');
            var segment = lastDot >= 0 ? Name[(lastDot + 1)..] : Name;
            return ToPascalCase(segment);
        }
    }

    private static string ToCSharpNamespace(string clojureNs)
    {
        return string.Join(".", clojureNs.Split('.').Select(ToPascalCase));
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
/// Information about a var
/// </summary>
public record VarInfo(string Name, bool IsPublic, bool IsMacro, Type? Type);

/// <summary>
/// Manages namespaces during compilation
/// </summary>
public class NamespaceManager
{
    private readonly Dictionary<string, Namespace> _namespaces = new();
    public Namespace? Current { get; private set; }

    public NamespaceManager()
    {
        // Create default user namespace
        SwitchTo("user");
    }

    public void SwitchTo(string name)
    {
        if (!_namespaces.TryGetValue(name, out var ns))
        {
            ns = new Namespace(name);
            _namespaces[name] = ns;
        }
        Current = ns;
    }

    public Namespace? Get(string name) =>
        _namespaces.TryGetValue(name, out var ns) ? ns : null;

    public void AddAlias(string alias, string namespaceName)
    {
        if (Current is null) return;
        if (_namespaces.TryGetValue(namespaceName, out var ns))
        {
            Current.Aliases[alias] = ns;
        }
    }

    public void Import(string fullTypeName)
    {
        Current?.Imports.Add(fullTypeName);
    }

    public void DefineVar(string name, bool isPublic = true, bool isMacro = false, Type? type = null)
    {
        if (Current is not null)
            Current.Mappings[name] = new VarInfo(name, isPublic, isMacro, type);
    }
}
