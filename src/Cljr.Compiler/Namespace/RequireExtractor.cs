// Use fully qualified type names to avoid namespace resolution issues.
// Cljr.Symbol (from Cljr.Core) is visible as a parent namespace type and
// takes precedence over using aliases in C#. We need to use explicit
// Cljr.Compiler.Reader.* types throughout.

// Type aliases with full qualification - BEFORE namespace declaration
using ReaderSymbol = Cljr.Compiler.Reader.Symbol;
using ReaderKeyword = Cljr.Compiler.Reader.Keyword;
using ReaderPersistentList = Cljr.Compiler.Reader.PersistentList;
using ReaderPersistentVector = Cljr.Compiler.Reader.PersistentVector;

using Cljr.Compiler.Reader;

namespace Cljr.Compiler.Namespace;

/// <summary>
/// Lightweight extractor for namespace and require information from .cljr source.
/// Used by the source generator to build dependency graphs without full analysis.
/// </summary>
public static class RequireExtractor
{
    /// <summary>
    /// Extract namespace info from source code.
    /// Returns null if no ns form is found.
    /// </summary>
    public static FileNamespaceInfo? Extract(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return null;

        try
        {
            foreach (var form in LispReader.ReadAll(source))
            {
                if (form is ReaderPersistentList list && list.Count >= 2)
                {
                    if (list[0] is ReaderSymbol sym && sym.Name == "ns")
                    {
                        return ExtractFromNsForm(list);
                    }
                }
            }
        }
        catch
        {
            // Parse errors - return null
        }

        return null;
    }

    private static FileNamespaceInfo ExtractFromNsForm(ReaderPersistentList nsList)
    {
        // (ns foo.bar (:require [other.ns :as o]))
        var nsName = GetSymbolName(nsList[1]);
        if (nsName == null)
            return null!;

        var requires = new List<RequireInfo>();

        for (int i = 2; i < nsList.Count; i++)
        {
            if (nsList[i] is ReaderPersistentList clause && clause.Count >= 1)
            {
                if (clause[0] is ReaderKeyword kw && kw.Name == "require")
                {
                    for (int j = 1; j < clause.Count; j++)
                    {
                        var req = ParseRequireSpec(clause[j]);
                        if (req != null)
                            requires.Add(req);
                    }
                }
            }
        }

        return new FileNamespaceInfo(nsName, requires);
    }

    private static RequireInfo? ParseRequireSpec(object? form)
    {
        // Symbol: my.namespace
        if (form is ReaderSymbol sym)
        {
            var name = GetSymbolName(sym);
            return name != null ? new RequireInfo(name, null, null) : null;
        }

        // Vector: [my.namespace :as m :refer [foo bar]]
        if (form is ReaderPersistentVector vec && vec.Count >= 1)
        {
            var nsName = GetSymbolName(vec[0]);
            if (nsName == null)
                return null;

            string? alias = null;
            List<string>? refers = null;

            for (int i = 1; i < vec.Count; i += 2)
            {
                if (i + 1 >= vec.Count)
                    break;

                if (vec[i] is ReaderKeyword kw)
                {
                    if (kw.Name == "as" && vec[i + 1] is ReaderSymbol aliasSym)
                    {
                        alias = aliasSym.Name;
                    }
                    else if (kw.Name == "refer" && vec[i + 1] is ReaderPersistentVector referVec)
                    {
                        refers = new List<string>();
                        foreach (var item in referVec)
                        {
                            if (item is ReaderSymbol refSym)
                                refers.Add(refSym.Name);
                        }
                    }
                }
            }

            // Note: Using Refer (from RequireInfo in NamespaceRegistry) instead of Refers
            return new RequireInfo(nsName, alias, refers);
        }

        return null;
    }

    private static string? GetSymbolName(object? form)
    {
        if (form is ReaderSymbol sym)
        {
            // Return the full name including namespace if present
            return sym.Namespace != null
                ? $"{sym.Namespace}/{sym.Name}"
                : sym.Name;
        }
        return null;
    }
}

/// <summary>
/// Information about a file's namespace and its dependencies.
/// </summary>
public class FileNamespaceInfo
{
    public string Namespace { get; }
    public IReadOnlyList<RequireInfo> Requires { get; }

    public FileNamespaceInfo(string ns, IReadOnlyList<RequireInfo> requires)
    {
        Namespace = ns;
        Requires = requires;
    }
}
