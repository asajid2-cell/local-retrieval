using System;
using System.IO;

namespace MuxTee;

// muxtee must never write diagnostics onto the tab's screen - the screen belongs to the child. Anything
// worth keeping goes to %LOCALAPPDATA%\muxtee\muxtee.log, best-effort, and a failure to log is silent
// (a broken log must not take a tab down).
internal static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                _path ??= ResolvePath();
                if (_path is null) return;
                File.AppendAllText(_path, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Nothing to do: logging is not on the critical path.
        }
    }

    private static string? ResolvePath()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "muxtee");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "muxtee.log");
        }
        catch
        {
            return null;
        }
    }
}
