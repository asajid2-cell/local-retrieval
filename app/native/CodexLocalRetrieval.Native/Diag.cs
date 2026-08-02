using System;
using System.IO;

namespace CodexLocalRetrieval_Native;

// Lightweight startup/runtime tracer. Writes to a known file so an external
// harness can read exactly how far startup got and where it blocks. Enabled
// whenever the CLR_DEBUG env var is set or always in Debug builds.
internal static class Diag
{
    private const long DefaultMaxBytes = 2L * 1024 * 1024;
    private const int DefaultRetainedFiles = 4;
    private static readonly string LogPath =
        Path.Combine(Environment.GetEnvironmentVariable("CLR_DIAG_DIR")
                     ?? Path.GetTempPath(), "clr-startup.log");
    private static readonly object Gate = new();

    public static string Path_ => LogPath;

    public static void Log(string msg)
    {
        Write(LogPath, msg, DefaultMaxBytes, DefaultRetainedFiles, DateTime.Now);
    }

    internal static void Write(string path, string msg, long maxBytes, int retainedFiles, DateTime now)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path) ?? Path.GetTempPath());
                var line = $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Environment.CurrentManagedThreadId}] {msg}{Environment.NewLine}";
                RotateIfNeeded(path, System.Text.Encoding.UTF8.GetByteCount(line), Math.Max(1024, maxBytes), Math.Max(1, retainedFiles));
                File.AppendAllText(path, line);
            }
        }
        catch { /* tracing must never throw */ }
    }

    private static void RotateIfNeeded(string path, int appendBytes, long maxBytes, int retainedFiles)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + appendBytes <= maxBytes) return;

        var oldest = path + "." + retainedFiles;
        try { File.Delete(oldest); } catch { }
        for (var index = retainedFiles - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            var destination = path + "." + (index + 1);
            if (!File.Exists(source)) continue;
            try { File.Move(source, destination, overwrite: true); } catch { }
        }
        try { File.Move(path, path + ".1", overwrite: true); } catch { }
    }
}
