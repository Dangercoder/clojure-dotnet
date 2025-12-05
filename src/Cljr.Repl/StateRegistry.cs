namespace Cljr.Repl;

/// <summary>
/// StateRegistry - preserves stateful vars (atoms, volatiles) across namespace reloads.
/// When a namespace is reloaded, we want to keep the current state of atoms
/// rather than resetting them to their initial values.
/// </summary>
public class StateRegistry
{
    /// <summary>
    /// Captures the state of all atoms and volatiles in a namespace.
    /// Call this BEFORE reloading a namespace.
    /// </summary>
    public Dictionary<string, object?> CaptureState(string ns)
    {
        var state = new Dictionary<string, object?>();
        var runtimeNs = RuntimeNamespace.Find(ns);
        if (runtimeNs is null)
            return state;

        foreach (var v in runtimeNs.Vars)
        {
            var value = v.Deref();
            // Only preserve atoms and volatiles - they represent application state
            if (value is Atom or Volatile)
            {
                state[v.Name] = value;
            }
        }

        return state;
    }

    /// <summary>
    /// Restores previously captured state into a namespace.
    /// Call this AFTER reloading a namespace.
    /// </summary>
    public void RestoreState(string ns, Dictionary<string, object?> oldState)
    {
        foreach (var (name, value) in oldState)
        {
            var v = Var.Find(ns, name);
            if (v is not null)
            {
                // Rebind the var to point to the preserved atom/volatile
                // rather than the newly created one
                v.BindRoot(value);
            }
        }
    }

    /// <summary>
    /// Checks if a value is stateful and should be preserved across reloads.
    /// </summary>
    public static bool IsStateful(object? value) =>
        value is Atom or Volatile;
}
