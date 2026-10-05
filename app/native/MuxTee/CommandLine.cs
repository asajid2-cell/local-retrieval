using System;
using System.Collections.Generic;

namespace MuxTee;

internal sealed class LaunchSpec
{
    // null/empty argv means "the user's default shell" (muxtee with no `--`).
    public string Image { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
}

internal static class CommandLine
{
    // muxtee [--] <cmd> [args...]
    //
    // Everything after the first `--` is the child command line, verbatim - that is the only way to pass
    // a child an argument that looks like one of our own switches. With no `--`, the first token is the
    // image and the rest are its arguments. With nothing at all, the tab's own shell is the child.
    public static LaunchSpec Parse(string[] argv)
    {
        if (argv.Length == 0)
            return new LaunchSpec { Image = DefaultShell(), Args = Array.Empty<string>() };

        var sep = Array.IndexOf(argv, "--");
        if (sep == 0)
        {
            var child = argv[1..];
            if (child.Length == 0)
                return new LaunchSpec { Image = DefaultShell(), Args = Array.Empty<string>() };
            return new LaunchSpec { Image = child[0], Args = child[1..] };
        }
        if (sep > 0)
        {
            // Tokens before `--` are muxtee's own; none are defined yet, so they are ignored rather than
            // swallowed silently into the child line.
            return new LaunchSpec { Image = argv[0], Args = argv[1..sep] };
        }
        return new LaunchSpec { Image = argv[0], Args = argv[1..] };
    }

    private static string DefaultShell()
        => Environment.GetEnvironmentVariable("COMSPEC") is { Length: > 0 } comspec
            ? comspec
            : "cmd.exe";
}
