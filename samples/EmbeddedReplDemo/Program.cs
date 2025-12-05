using Cljr.Repl;

namespace EmbeddedReplDemo;

// Example C# classes that can be used from the Clojure REPL
// Note: Using 'long' for numeric types since Clojure defaults to long
public record User(string Name, long Age);

public class UserService
{
    private readonly List<User> _users = [];

    public User Add(string name, long age)
    {
        var user = new User(name, age);
        _users.Add(user);
        return user;
    }

    public IReadOnlyList<User> GetAll() => _users;

    public User? FindByName(string name) =>
        _users.FirstOrDefault(u => u.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public static class Calculator
{
    // Using long/double since Clojure numbers are long by default
    public static long Add(long a, long b) => a + b;
    public static long Multiply(long a, long b) => a * b;
    public static double Divide(double a, double b) => a / b;
}

public class Program
{
    // Static service instance accessible from REPL
    public static UserService Users { get; } = new();

    public static async Task Main(string[] args)
    {
        if (args.Contains("--test"))
        {
            await RunTests();
            return;
        }

        Console.WriteLine("=== Embedded Clojure REPL Demo ===\n");

        // Start nREPL server
        var nrepl = EmbeddedNrepl.Start(7888);

        Console.WriteLine($"nREPL server running on port {nrepl.Port}");
        Console.WriteLine();
        Console.WriteLine("Connect with your editor:");
        Console.WriteLine("  VS Code + Calva: 'Connect to running REPL' -> localhost:7888");
        Console.WriteLine("  Emacs + CIDER:   M-x cider-connect localhost 7888");
        Console.WriteLine();
        Console.WriteLine("Example expressions to try:");
        Console.WriteLine();
        Console.WriteLine("  ;; Calculator - static methods work directly");
        Console.WriteLine("  (EmbeddedReplDemo.Calculator/Add 10 20)");
        Console.WriteLine("  (EmbeddedReplDemo.Calculator/Multiply 6 7)");
        Console.WriteLine();
        Console.WriteLine("  ;; UserService - use inline type hints for instance methods");
        Console.WriteLine("  (def users EmbeddedReplDemo.Program/Users)");
        Console.WriteLine("  (.Add ^EmbeddedReplDemo.UserService users \"Alice\" 30)");
        Console.WriteLine("  (.Add ^EmbeddedReplDemo.UserService users \"Bob\" 25)");
        Console.WriteLine("  (.GetAll ^EmbeddedReplDemo.UserService users)");
        Console.WriteLine();
        Console.WriteLine("  ;; Or use let binding with type hint");
        Console.WriteLine("  (let [^EmbeddedReplDemo.UserService svc EmbeddedReplDemo.Program/Users]");
        Console.WriteLine("    (.Add svc \"Test\" 42))");
        Console.WriteLine();
        Console.WriteLine("  ;; Access User record properties with type hint");
        Console.WriteLine("  (def alice (.FindByName ^EmbeddedReplDemo.UserService users \"Alice\"))");
        Console.WriteLine("  (.-Name ^EmbeddedReplDemo.User alice)");
        Console.WriteLine("  (.-Age ^EmbeddedReplDemo.User alice)");
        Console.WriteLine();
        Console.WriteLine("Press Ctrl+C to stop...");

        // Keep the app running
        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            await Task.Delay(Timeout.Infinite, cts.Token);
        }
        catch (TaskCanceledException)
        {
            // Cancelled
        }

        nrepl.Stop();
        Console.WriteLine("\nGoodbye!");
    }

    private static async Task RunTests()
    {
        Console.WriteLine("=== Running REPL Tests ===\n");

        var session = new NreplSession(
            AppDomain.CurrentDomain.GetAssemblies(),
            sourcePaths: null
        );

        var passed = 0;
        var failed = 0;

        async Task Test(string description, string code, Func<object?, bool> check)
        {
            Console.Write($"  {description}... ");
            var result = await session.EvalAsync(code);

            if (result.Error != null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL (Error: {result.Error})");
                Console.ResetColor();
                failed++;
                return;
            }

            var value = result.Values?.LastOrDefault();
            if (check(value))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"PASS => {value}");
                Console.ResetColor();
                passed++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"FAIL (got: {value})");
                Console.ResetColor();
                failed++;
            }
        }

        // Basic tests
        Console.WriteLine("Basic evaluation:");
        await Test("Simple addition", "(+ 1 2 3)", v => Equals(v, 6L));
        await Test("Define var", "(def x 42)", v => true);
        await Test("Read var", "x", v => Equals(v, 42L));
        await Test("String", "\"hello\"", v => Equals(v, "hello"));
        Console.WriteLine();

        // Calculator tests
        Console.WriteLine("Calculator static methods:");
        await Test("Calculator/Add", "(EmbeddedReplDemo.Calculator/Add 10 20)", v => Equals(v, 30L));
        await Test("Calculator/Multiply", "(EmbeddedReplDemo.Calculator/Multiply 6 7)", v => Equals(v, 42L));
        await Test("Calculator/Divide", "(EmbeddedReplDemo.Calculator/Divide 10.0 4.0)", v => Equals(v, 2.5));
        Console.WriteLine();

        // UserService tests - type hint at each call site
        Console.WriteLine("UserService instance methods:");
        await Test("Get Users static property",
            "(def users EmbeddedReplDemo.Program/Users)",
            v => true);
        await Test("Add user Alice (inline type hint)",
            "(.Add ^EmbeddedReplDemo.UserService users \"Alice\" 30)",
            v => v?.ToString()?.Contains("Alice") == true);
        await Test("Add user Bob (inline type hint)",
            "(.Add ^EmbeddedReplDemo.UserService users \"Bob\" 25)",
            v => v?.ToString()?.Contains("Bob") == true);
        await Test("GetAll returns list",
            "(.GetAll ^EmbeddedReplDemo.UserService users)",
            v => v != null);
        await Test("FindByName",
            "(.FindByName ^EmbeddedReplDemo.UserService users \"alice\")",
            v => v?.ToString()?.Contains("Alice") == true);
        Console.WriteLine();

        // User record property access
        Console.WriteLine("User record properties:");
        await Test("Def alice with FindByName",
            "(def alice (.FindByName ^EmbeddedReplDemo.UserService users \"Alice\"))",
            v => true);
        await Test("Access Name property (inline hint)",
            "(.-Name ^EmbeddedReplDemo.User alice)",
            v => Equals(v, "Alice"));
        await Test("Access Age property (inline hint)",
            "(.-Age ^EmbeddedReplDemo.User alice)",
            v => Equals(v, 30L));
        Console.WriteLine();

        // Summary
        Console.WriteLine("---");
        Console.Write($"Results: ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"{passed} passed");
        Console.ResetColor();
        Console.Write(", ");
        if (failed > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"{failed} failed");
            Console.ResetColor();
        }
        else
        {
            Console.Write("0 failed");
        }
        Console.WriteLine();

        Environment.ExitCode = failed > 0 ? 1 : 0;
    }
}
