using System;
using System.IO;

namespace CodexLocalRetrieval_Native;

// Lightweight startup/runtime tracer. Writes to a known file so an external
// harness can read exactly how far startup got and where it blocks. Enabled
// whenever the CLR_DEBUG env var is set or always in Debug builds.
internal static class Diag
{
    private static readonly string LogPath =
        Path.Combine(Environment.GetEnvironmentVariable("CLR_DIAG_DIR")
                     ?? Path.GetTempPath(), "clr-startup.log");
    private static readonly object Gate = new();

    public static string Path_ => LogPath;

    public static void Log(string msg)
    {
        try
        {
            lock (Gate)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}");
            }
        }
        catch { /* tracing must never throw */ }
    }
}
