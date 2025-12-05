using System.Reflection;
using Cljr.Compiler.Analyzer;
using Cljr.Compiler.Emitter;
using Cljr.Compiler.Reader;
using Cljr.Repl;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace Cljr.Cli;

class Program
{
    static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 0;
        }

        var command = args[0].ToLowerInvariant();

        return command switch
        {
            "compile" => Compile(args[1..]),
            "run" => Run(args[1..]),
            "eval" => Eval(args[1..]),
            "repl" => StartRepl(args[1..]),
            "nrepl" => StartNrepl(args[1..]),
            "dev" => StartDev(args[1..]),
            "version" => Version(),
            "--help" or "-h" or "help" => Help(),
            _ => Unknown(command)
        };
    }

    static int Compile(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cljr compile <file.cljr> [-o output.cs]");
            return 1;
        }

        var inputFile = args[0];
        var outputFile = args.Length > 2 && args[1] == "-o" ? args[2] : null;

        try
        {
            var source = File.ReadAllText(inputFile);
            var forms = LispReader.ReadAll(source).ToList();

            var analyzer = new Analyzer();
            var unit = analyzer.AnalyzeFile(forms);

            var emitter = new CSharpEmitter();
            var csharp = emitter.Emit(unit);

            if (outputFile is not null)
            {
                File.WriteAllText(outputFile, csharp);
                Console.WriteLine($"Compiled {inputFile} -> {outputFile}");
            }
            else
            {
                Console.WriteLine(csharp);
            }

            return 0;
        }
        catch (ReaderException ex)
        {
            Console.Error.WriteLine($"Reader error: {ex.Message}");
            return 1;
        }
        catch (AnalyzerException ex)
        {
            Console.Error.WriteLine($"Analyzer error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cljr run <file.cljr>");
            return 1;
        }

        var inputFile = args[0];
        var programArgs = args.Length > 1 ? args[1..] : [];

        try
        {
            var source = File.ReadAllText(inputFile);
            var forms = LispReader.ReadAll(source).ToList();

            var analyzer = new Analyzer();
            var unit = analyzer.AnalyzeFile(forms);

            var emitter = new CSharpEmitter();
            var csharp = emitter.EmitAsScript(unit);

            // Get class name from namespace
            var ns = unit.Namespace;
            var className = ns?.Name.Split('.').Last() ?? "Program";
            className = char.ToUpper(className[0]) + className[1..].Replace("-", "");

            // Add call to _main if it exists
            if (csharp.Contains("_main"))
            {
                var argsLiteral = programArgs.Length > 0
                    ? string.Join(", ", programArgs.Select(a => $"\"{a}\""))
                    : "";
                csharp += $"\n{className}._main({(argsLiteral.Length > 0 ? $"new object[] {{ {argsLiteral} }}" : "")});";
            }

            // Build the script with Core runtime functions
            var scriptOptions = ScriptOptions.Default
                .AddReferences(typeof(Core).Assembly)
                .AddReferences(typeof(Console).Assembly)
                .AddReferences(typeof(List<>).Assembly)
                .AddImports("System", "System.Collections.Generic", "Cljr")
                .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

            // Run the script
            CSharpScript.EvaluateAsync(csharp, scriptOptions).GetAwaiter().GetResult();

            return 0;
        }
        catch (CompilationErrorException ex)
        {
            Console.Error.WriteLine("Compilation errors:");
            foreach (var diagnostic in ex.Diagnostics)
            {
                Console.Error.WriteLine($"  {diagnostic}");
            }
            return 1;
        }
        catch (ReaderException ex)
        {
            Console.Error.WriteLine($"Reader error: {ex.Message}");
            return 1;
        }
        catch (AnalyzerException ex)
        {
            Console.Error.WriteLine($"Analyzer error: {ex.Message}");
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    static int Eval(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Usage: cljr eval '<expression>'");
            return 1;
        }

        var code = string.Join(" ", args);

        try
        {
            var forms = LispReader.ReadAll(code).ToList();

            var scriptOptions = ScriptOptions.Default
                .AddReferences(typeof(Core).Assembly)
                .AddReferences(typeof(Console).Assembly)
                .AddReferences(typeof(List<>).Assembly)
                .AddImports("System", "System.Collections.Generic", "System.Linq", "Cljr")
                .WithImports("Cljr.Core")
                .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

            foreach (var form in forms)
            {
                var analyzer = new Analyzer();
                var expr = analyzer.Analyze(form, new AnalyzerContext { IsRepl = true });

                var emitter = new CSharpEmitter();
                var csharp = emitter.EmitScript(expr);

                // Wrap in usings for script execution
                var script = $@"using System;
using System.Collections.Generic;
using System.Linq;
using Cljr;
using Cljr.Collections;
using static Cljr.Core;
{csharp}";

                // Debug: show generated script
                if (Environment.GetEnvironmentVariable("CLJR_DEBUG") == "1")
                {
                    Console.Error.WriteLine($"=== Generated script ===\n{script}\n========================");
                }

                var result = CSharpScript.EvaluateAsync<object>(script, scriptOptions).GetAwaiter().GetResult();

                // Print result Clojure-style
                Console.WriteLine(Core.PrStr(result));
            }

            return 0;
        }
        catch (CompilationErrorException ex)
        {
            Console.Error.WriteLine("Compilation errors:");
            foreach (var diagnostic in ex.Diagnostics)
            {
                Console.Error.WriteLine($"  {diagnostic}");
            }
            return 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    static int StartRepl(string[] args)
    {
        Console.WriteLine("Cljr REPL v0.1.0");
        Console.WriteLine("Type :quit to exit");
        Console.WriteLine();

        var analyzer = new Analyzer();
        var emitter = new CSharpEmitter();

        var scriptOptions = ScriptOptions.Default
            .AddReferences(typeof(Core).Assembly)
            .AddReferences(typeof(Console).Assembly)
            .AddReferences(typeof(List<>).Assembly)
            .AddImports("System", "System.Collections.Generic", "System.Linq", "Cljr")
            .WithImports("Cljr.Core")
            .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest);

        ScriptState<object>? state = null;

        while (true)
        {
            Console.Write("cljr=> ");
            var line = Console.ReadLine();

            if (line is null || line.Trim() == ":quit")
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            try
            {
                var forms = LispReader.ReadAll(line).ToList();

                foreach (var form in forms)
                {
                    var expr = analyzer.Analyze(form, new AnalyzerContext { IsRepl = true });
                    var csharp = emitter.EmitScript(expr);

                    // Wrap in usings for script execution
                    var script = $@"using System;
using System.Collections.Generic;
using System.Linq;
using Cljr;
using static Cljr.Core;
{csharp}";

                    // Evaluate and maintain state between expressions
                    if (state == null)
                    {
                        state = CSharpScript.RunAsync<object>(script, scriptOptions).GetAwaiter().GetResult();
                    }
                    else
                    {
                        state = state.ContinueWithAsync<object>(csharp).GetAwaiter().GetResult();
                    }

                    // Print result Clojure-style
                    Console.WriteLine(Core.PrStr(state.ReturnValue));
                }
            }
            catch (CompilationErrorException ex)
            {
                Console.Error.WriteLine("Compilation error:");
                foreach (var diagnostic in ex.Diagnostics)
                {
                    Console.Error.WriteLine($"  {diagnostic}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex.Message}");
            }
        }

        Console.WriteLine("Goodbye!");
        return 0;
    }

    static int StartNrepl(string[] args)
    {
        var port = 0;
        string? projectPath = null;

        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            if ((args[i] == "-p" || args[i] == "--port") && i + 1 < args.Length)
            {
                if (!int.TryParse(args[i + 1], out port))
                {
                    Console.Error.WriteLine("Invalid port number");
                    return 1;
                }
                i++;
            }
            else if (args[i] == "--project" && i + 1 < args.Length)
            {
                projectPath = args[i + 1];
                i++;
            }
        }

        // Auto-detect project if not specified
        projectPath ??= FindProjectFile();

        // Load project context if available
        ProjectContext? projectContext = null;
        if (projectPath != null)
        {
            Console.WriteLine($"Loading project: {projectPath}");
            try
            {
                projectContext = ProjectContext.FromProjectFile(projectPath);
                if (projectContext != null)
                {
                    Console.WriteLine($"  SDK: {projectContext.Sdk ?? "default"}");
                    Console.WriteLine($"  Target: {projectContext.TargetFramework}");
                    Console.WriteLine($"  Assemblies: {projectContext.AssemblyPaths.Count}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Warning: Could not load project context: {ex.Message}");
            }
        }

        var server = new NreplServer(port, () => new NreplSession(projectContext));
        Console.WriteLine($"Cljr nREPL server v0.1.0");
        Console.WriteLine($"Listening on port {server.Port}");
        Console.WriteLine("Press Ctrl+C to stop");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            server.Stop();
        };

        try
        {
            server.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Server error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    static int StartDev(string[] args)
    {
        var options = new DevModeOptions { Verbose = true };

        // Parse arguments
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-p" or "--port":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
                    {
                        options.Port = port;
                        i++;
                    }
                    break;
                case "-w" or "--watch":
                    if (i + 1 < args.Length)
                    {
                        options.WatchPaths.Add(args[i + 1]);
                        i++;
                    }
                    break;
                case "--no-watch":
                    options.EnableWatching = false;
                    break;
                case "-q" or "--quiet":
                    options.Verbose = false;
                    break;
                default:
                    // Assume it's the initial namespace
                    if (!args[i].StartsWith("-"))
                    {
                        options.InitialNamespace = args[i];
                    }
                    break;
            }
        }

        // Default watch path if none specified
        if (options.WatchPaths.Count == 0)
        {
            options.WatchPaths.Add("src");
        }

        Console.WriteLine("Cljr Dev Mode v0.1.0");

        using var session = new DevModeSession(options);

        // Wire up reload events
        session.OnReload += (_, e) =>
        {
            if (e.Success)
                Console.WriteLine($"[reload] {e.Namespace} ok");
            else
                Console.Error.WriteLine($"[reload] {e.Namespace} FAILED: {e.Error}");
        };

        session.OnFileChanged += (_, path) =>
        {
            if (options.Verbose)
                Console.WriteLine($"[change] {path}");
        };

        // Start the session (loads initial namespace if specified)
        var startResult = session.StartAsync().GetAwaiter().GetResult();
        if (startResult?.Error is not null)
        {
            Console.Error.WriteLine($"Error loading initial namespace: {startResult.Error}");
            return 1;
        }

        // Start nREPL server
        var server = new NreplServer(options.Port, () => session);
        Console.WriteLine($"nREPL server on port {server.Port}");

        if (options.EnableWatching)
            Console.WriteLine($"Watching: {string.Join(", ", options.WatchPaths)}");

        Console.WriteLine("Press Ctrl+C to stop");

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            server.Stop();
            session.StopWatching();
        };

        try
        {
            server.RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Server error: {ex.Message}");
            return 1;
        }

        return 0;
    }

    static int Version()
    {
        Console.WriteLine("cljr 0.1.0");
        Console.WriteLine("Clojure-to-C# transpiler for .NET 10");
        return 0;
    }

    static int Help()
    {
        PrintUsage();
        return 0;
    }

    static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 1;
    }

    static string? FindProjectFile()
    {
        var files = Directory.GetFiles(".", "*.csproj");
        return files.Length == 1 ? files[0] : null;
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
            Cljr - Clojure-to-C# Transpiler

            Usage: cljr <command> [options]

            Commands:
              compile <file.cljr> [-o output.cs]   Compile a .cljr file to C#
              run <file.cljr>                      Compile and run a .cljr file
              eval '<expression>'                  Evaluate an expression
              repl                                 Start interactive REPL
              nrepl [options]                      Start nREPL server (for CIDER/Calva)
              dev [namespace] [options]            Start dev mode with hot-reload
              version                              Print version info
              help                                 Print this help message

            nREPL options:
              -p, --port <port>    nREPL port (default: auto)
              --project <path>     Path to .csproj (default: auto-detect)

            Dev mode options:
              -p, --port <port>    nREPL port (default: auto)
              -w, --watch <path>   Path to watch (can repeat, default: src)
              --no-watch           Disable file watching
              -q, --quiet          Less verbose output

            Examples:
              cljr compile src/hello.cljr -o Hello.cs
              cljr eval '(+ 1 2 3)'
              cljr repl
              cljr nrepl --project MyApp.csproj
              cljr dev myapp.core -p 7888
              cljr dev -w src -w test
            """);
    }
}
