using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Cljr.Compiler.Emitter;
using Cljr.Compiler.Macros;
using Cljr.Compiler.Namespace;
using Cljr.Compiler.Reader;
using CljrAnalyzer = Cljr.Compiler.Analyzer.Analyzer;

namespace Cljr.SourceGenerator;

/// <summary>
/// Embedded clojure.core source containing standard macros.
/// These macros are processed before any user code to ensure availability.
/// </summary>
internal static class EmbeddedCore
{
    public const string ClojureCore = @"
(ns clojure.core)

;; with-open - Resource Management (IDisposable)
(defmacro with-open [bindings & body]
  (if (empty? bindings)
    `(do ~@body)
    (let [name (first bindings)
          init (second bindings)
          more (vec (rest (rest bindings)))]
      `(let [~name ~init]
         (try
           (with-open ~more ~@body)
           (finally
             (.Dispose ~name)))))))

;; when - Single-branch conditional
(defmacro when [test & body]
  `(if ~test
     (do ~@body)
     nil))

;; when-not - Negated single-branch conditional
(defmacro when-not [test & body]
  `(if ~test
     nil
     (do ~@body)))

;; if-not - Negated if
(defmacro if-not [test then else]
  `(if ~test ~else ~then))

;; cond - Multi-branch conditional
(defmacro cond [& clauses]
  (if (empty? clauses)
    nil
    (let [test (first clauses)
          expr (second clauses)
          more (rest (rest clauses))]
      `(if ~test
         ~expr
         (cond ~@more)))))

;; -> (thread-first)
(defmacro -> [x & forms]
  (if (empty? forms)
    x
    (let [form (first forms)
          more (rest forms)
          threaded (if (seq? form)
                     (let [head (first form)
                           tail (rest form)]
                       `(~head ~x ~@tail))
                     `(~form ~x))]
      `(-> ~threaded ~@more))))

;; ->> (thread-last)
(defmacro ->> [x & forms]
  (if (empty? forms)
    x
    (let [form (first forms)
          more (rest forms)
          threaded (if (seq? form)
                     (concat form [x])
                     `(~form ~x))]
      `(->> ~threaded ~@more))))

;; doto - Method chaining
(defmacro doto [x & forms]
  (let [gx (gensym ""doto__"")]
    `(let [~gx ~x]
       ~@(map (fn [form]
                (if (seq? form)
                  (let [head (first form)
                        tail (rest form)]
                    `(~head ~gx ~@tail))
                  `(~form ~gx)))
              forms)
       ~gx)))

;; and - Short-circuit logical and
(defmacro and [& args]
  (if (empty? args)
    true
    (if (= 1 (count args))
      (first args)
      (let [x (first args)
            more (rest args)]
        `(let [and# ~x]
           (if and#
             (and ~@more)
             and#))))))

;; or - Short-circuit logical or
(defmacro or [& args]
  (if (empty? args)
    nil
    (if (= 1 (count args))
      (first args)
      (let [x (first args)
            more (rest args)]
        `(let [or# ~x]
           (if or#
             or#
             (or ~@more)))))))

;; when-let - Conditional binding
(defmacro when-let [bindings & body]
  (let [name (first bindings)
        init (second bindings)]
    `(let [temp# ~init]
       (when temp#
         (let [~name temp#]
           ~@body)))))

;; if-let - Conditional binding with else branch
(defmacro if-let [bindings then else]
  (let [name (first bindings)
        init (second bindings)]
    `(let [temp# ~init]
       (if temp#
         (let [~name temp#]
           ~then)
         ~else))))

;; dotimes - Execute body n times
(defmacro dotimes [bindings & body]
  (let [i (first bindings)
        n (second bindings)
        n-sym (gensym ""n__"")]
    `(let [~n-sym ~n]
       (loop [~i 0]
         (when (< ~i ~n-sym)
           ~@body
           (recur (inc ~i)))))))

;; while - Loop while condition is true
(defmacro while [test & body]
  `(loop []
     (when ~test
       ~@body
       (recur))))

;; doseq - Side-effect iteration
(defmacro doseq [bindings & body]
  (let [binding (first bindings)
        coll (second bindings)
        s-sym (gensym ""s__"")
        xs-sym (gensym ""xs__"")]
    `(let [~s-sym (seq ~coll)]
       (loop [~xs-sym ~s-sym]
         (when ~xs-sym
           (let [~binding (first ~xs-sym)]
             ~@body)
           (recur (next ~xs-sym)))))))

;; lazy-seq - Create lazy sequence
(defmacro lazy-seq [& body]
  `(new Cljr.Collections.LazySeq (fn [] ~@body)))

;; lazy-cat - Lazy concatenation
(defmacro lazy-cat [& colls]
  `(lazy-seq (concat ~@colls)))

;; for - List comprehension (eager version)
(defmacro for [seq-exprs body-expr]
  (let [result-sym (gensym ""result__"")]
    `(let [~result-sym (atom [])]
       (doseq ~seq-exprs
         (swap! ~result-sym conj ~body-expr))
       @~result-sym)))

;; condp - Predicate dispatch
;; Generates nested if expressions directly without helper macros
(defmacro condp [pred expr & clauses]
  (let [gexpr (gensym ""expr__"")
        build-cond (fn build-cond [clauses]
                     (if (empty? clauses)
                       nil
                       (if (= 1 (count clauses))
                         (first clauses)
                         (list 'if
                               (list pred (first clauses) gexpr)
                               (second clauses)
                               (build-cond (rest (rest clauses)))))))]
    `(let [~gexpr ~expr]
       ~(build-cond clauses))))

;; case - Fast constant dispatch
;; Generates nested if expressions directly without helper macros
(defmacro case [expr & clauses]
  (let [gexpr (gensym ""expr__"")
        build-cond (fn build-cond [clauses]
                     (if (empty? clauses)
                       nil
                       (if (= 1 (count clauses))
                         (first clauses)
                         (list 'if
                               (list '= gexpr (first clauses))
                               (second clauses)
                               (build-cond (rest (rest clauses)))))))]
    `(let [~gexpr ~expr]
       ~(build-cond clauses))))
";
}

/// <summary>
/// Source generator that compiles .cljr files into C# code during build.
/// Supports multi-file compilation with dependency ordering.
/// </summary>
[Generator(LanguageNames.CSharp)]
public class CljrSourceGenerator : IIncrementalGenerator
{
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all .cljr files in AdditionalFiles
        var cljrFiles = context.AdditionalTextsProvider
            .Where(file => file.Path.EndsWith(".cljr", StringComparison.OrdinalIgnoreCase));

        // Get analyzer config options (for MSBuild properties like CljrDevMode)
        var configOptions = context.AnalyzerConfigOptionsProvider;

        // Combine with compilation and config options
        var compilationFilesAndConfig = context.CompilationProvider
            .Combine(cljrFiles.Collect())
            .Combine(configOptions);

        // Register source generation
        context.RegisterSourceOutput(compilationFilesAndConfig, (spc, source) =>
        {
            var ((compilation, files), options) = source;

            // Check if CljrDevMode is enabled (enables live function redefinition via Vars)
            var devMode = false;
            if (options.GlobalOptions.TryGetValue("build_property.CljrDevMode", out var devModeValue))
            {
                devMode = string.Equals(devModeValue, "true", StringComparison.OrdinalIgnoreCase);
            }

            GenerateSources(spc, compilation, files, devMode);
        });
    }

    private void GenerateSources(SourceProductionContext context, Compilation compilation, ImmutableArray<AdditionalText> files, bool devMode = false)
    {
        if (files.IsDefaultOrEmpty)
            return;

        // Phase 1: Build dependency graph
        var graph = new DependencyGraph();
        foreach (var file in files)
        {
            var sourceText = file.GetText(context.CancellationToken);
            if (sourceText != null)
            {
                graph.AddFile(file.Path, sourceText.ToString());
            }
        }

        // Phase 2: Report missing requires (warnings)
        foreach (var missing in graph.GetMissingRequires())
        {
            var descriptor = new DiagnosticDescriptor(
                "CLJR002",
                "Missing Required Namespace",
                "Required namespace '{0}' not found in project",
                "Cljr.Compiler",
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            var diagnostic = Diagnostic.Create(descriptor, Location.None, missing.RequiredNamespace);
            context.ReportDiagnostic(diagnostic);
        }

        // Phase 3: Topological sort with cycle detection
        var result = graph.GetOrderedFiles();
        if (result is DependencyResult.Failure failure)
        {
            foreach (var error in failure.CircularErrors)
            {
                var descriptor = new DiagnosticDescriptor(
                    "CLJR003",
                    "Circular Dependency",
                    "{0}",
                    "Cljr.Compiler",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true);

                var diagnostic = Diagnostic.Create(descriptor, Location.None, error);
                context.ReportDiagnostic(diagnostic);
            }
            return;
        }

        // Phase 4: Compile with shared state
        var registry = new NamespaceRegistry();

        // MacroExpander uses pure interpretation (no Roslyn compilation at macro time),
        // so it works in source generators without needing compilation references.
        var macroExpander = new MacroExpander(); // Shared across files!

        // Phase 4a: Process embedded clojure.core first to register standard macros
        // This ensures with-open, when, cond, ->, etc. are available for all user code
        try
        {
            var coreForms = LispReader.ReadAll(EmbeddedCore.ClojureCore).ToList();
            var coreAnalyzer = new CljrAnalyzer(macroExpander);
            coreAnalyzer.AnalyzeFile(coreForms);
            // Note: We don't emit clojure.core - it only contains defmacro which returns nil
        }
        catch (Exception ex)
        {
            var descriptor = new DiagnosticDescriptor(
                "CLJR004",
                "Core Macro Initialization Error",
                "Failed to initialize clojure.core macros: {0}",
                "Cljr.Compiler",
                DiagnosticSeverity.Warning,
                isEnabledByDefault: true);

            var diagnostic = Diagnostic.Create(descriptor, Location.None, ex.Message);
            context.ReportDiagnostic(diagnostic);
        }

        var successResult = (DependencyResult.Success)result;

        foreach (var node in successResult.Files)
        {
            try
            {
                var forms = LispReader.ReadAll(node.SourceText).ToList();
                if (forms.Count == 0)
                    continue;

                var analyzer = new CljrAnalyzer(macroExpander);
                var unit = analyzer.AnalyzeFile(forms);

                // Register namespace for subsequent files
                if (unit.Namespace != null)
                {
                    registry.Register(new NamespaceInfo(
                        unit.Namespace.Name,
                        NamespaceRegistry.ToCSharpNamespace(unit.Namespace.Name),
                        NamespaceRegistry.ToCSharpClassName(unit.Namespace.Name),
                        unit.Namespace.Requires.Select(r =>
                            new RequireInfo(r.Namespace, r.Alias, r.Refers)).ToList(),
                        unit.Namespace.Imports.Select(i =>
                            new ImportInfo(i.Namespace, i.Types.ToList())).ToList()
                    ));
                }

                var emitter = new CSharpEmitter { UseVarBasedCodegen = devMode };
                var csharpCode = emitter.EmitWithRequires(unit, registry);

                var fileName = node.FileName;
                var hintName = $"{fileName}.g.cs";

                context.AddSource(hintName, SourceText.From(csharpCode, Encoding.UTF8));
            }
            catch (Exception ex)
            {
                var descriptor = new DiagnosticDescriptor(
                    "CLJR001",
                    "Cljr Compilation Error",
                    "Failed to compile {0}: {1}",
                    "Cljr.Compiler",
                    DiagnosticSeverity.Error,
                    isEnabledByDefault: true);

                var diagnostic = Diagnostic.Create(descriptor, Location.None, node.FilePath, ex.Message);
                context.ReportDiagnostic(diagnostic);
            }
        }
    }

    private string GenerateEntryPoint(string ns, string className)
    {
        return $@"// <auto-generated/>
// Entry point generated by Cljr Source Generator

namespace {ns};

public static partial class Program
{{
    public static void Main(string[] args)
    {{
        {className}._main(args);
    }}
}}
";
    }

    /// <summary>
    /// Convert Clojure namespace name to C# namespace (e.g., "my-app.core" -> "MyApp.Core")
    /// </summary>
    private static string ToCSharpNamespace(string clojureNs)
    {
        return clojureNs.Replace("-", "").Split('.')
            .Select(s => s.Length > 0 ? char.ToUpper(s[0]) + s.Substring(1) : s)
            .Aggregate((a, b) => $"{a}.{b}");
    }

    /// <summary>
    /// Extract C# class name from Clojure namespace (e.g., "my-app.core" -> "Core")
    /// </summary>
    private static string ToCSharpClassName(string clojureNs)
    {
        var lastPart = clojureNs.Split('.').Last();
        return lastPart.Length > 0
            ? char.ToUpper(lastPart[0]) + lastPart.Substring(1).Replace("-", "")
            : "Program";
    }
}
