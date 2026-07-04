using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace CodexLocalRetrieval.Core.Remote;

public static class MuxdTaskLaunch
{
    public static bool TryGetHiddenPythonAction(string taskXml, Func<string, bool>? fileExists, out string exe, out string arguments)
    {
        exe = "";
        arguments = "";
        if (string.IsNullOrWhiteSpace(taskXml)) return false;

        XDocument doc;
        try { doc = XDocument.Parse(taskXml); }
        catch { return false; }

        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var exec = doc.Descendants(ns + "Exec").FirstOrDefault();
        var command = (exec?.Element(ns + "Command")?.Value ?? "").Trim().Trim('"');
        var args = (exec?.Element(ns + "Arguments")?.Value ?? "").Trim();
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrWhiteSpace(args)) return false;
        if (!string.Equals(Path.GetFileName(command), "python.exe", StringComparison.OrdinalIgnoreCase)) return false;
        if (!args.Contains("muxd.py", StringComparison.OrdinalIgnoreCase)) return false;

        var dir = Path.GetDirectoryName(command) ?? "";
        var candidate = string.IsNullOrWhiteSpace(dir) ? "pythonw.exe" : Path.Combine(dir, "pythonw.exe");
        if (fileExists is not null && !fileExists(candidate)) return false;

        exe = candidate;
        arguments = args;
        return true;
    }
}
