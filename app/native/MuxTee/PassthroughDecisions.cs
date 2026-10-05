using System;

namespace MuxTee;

internal enum LaunchMode
{
    // muxtee owns an inner ConPTY and tees the output (the full tier).
    Tee,
    // muxtee gets out of the way: launch the child on the inherited handles and touch nothing.
    Passthrough,
}

internal static class PassthroughDecisions
{
    internal const string DisableVar = "MUXTEE_DISABLE";
    internal const string ActiveVar = "MUXTEE_ACTIVE";

    // Invariant 5: passthrough when we can't help. Three ways that happens, and all three must be free
    // of side effects - a wrapped harness (a Gateway leaf, a test runner) sets MUXTEE_DISABLE and must
    // come out byte-identical to running the child directly.
    //
    //   * stdin or stdout is not a console - we have nothing to read keys from or paint into;
    //   * MUXTEE_DISABLE=1 - the explicit escape hatch, and the "no mux" profile uses it;
    //   * MUXTEE_ACTIVE is already set - an outer muxtee already owns a ConPTY for us, so nesting
    //     again would spend a second ConPTY layer and change the child's geometry for no gain.
    public static LaunchMode Decide(bool stdinIsConsole, bool stdoutIsConsole, Func<string, string?> env)
    {
        if (!stdinIsConsole || !stdoutIsConsole) return LaunchMode.Passthrough;
        if (IsSet(env(DisableVar))) return LaunchMode.Passthrough;
        if (IsSet(env(ActiveVar))) return LaunchMode.Passthrough;
        return LaunchMode.Tee;
    }

    // "1", "true", "yes", "on" - anything else, including "" or unset, is off. Only truthy values turn
    // the escape hatch on, so a stray MUXTEE_DISABLE=0 in a shell does not silently disable the tee.
    internal static bool IsSet(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        var v = value.Trim();
        return v == "1"
            || v.Equals("true", StringComparison.OrdinalIgnoreCase)
            || v.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || v.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
}
