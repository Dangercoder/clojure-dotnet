namespace Cljr.Compiler.Macros;

/// <summary>
/// Interface for macro definitions (both built-in and user-defined)
/// </summary>
public interface IMacro
{
    /// <summary>
    /// The name of the macro (e.g., "when", "->")
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Expand the macro form to produce new code
    /// </summary>
    /// <param name="args">The arguments passed to the macro (excluding the macro name)</param>
    /// <returns>The expanded form</returns>
    object? Expand(IReadOnlyList<object?> args);
}
