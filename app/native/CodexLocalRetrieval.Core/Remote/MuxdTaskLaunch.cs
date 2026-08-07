using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace CodexLocalRetrieval.Core.Remote;

public static class MuxdTaskLaunch
{
    public sealed record AdopterAction(string PythonwExe, string MuxrunPath);

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

    public static bool TryGetAdopterAction(
        string taskXml,
        Func<string, bool>? fileExists,
        out AdopterAction? action)
        => TryGetAdopterAction(taskXml, fileExists, null, out action);

    public static bool TryGetAdopterAction(
        string taskXml,
        Func<string, bool>? fileExists,
        Func<string, string?>? readText,
        out AdopterAction? action)
    {
        action = null;
        if (!TryReadExec(taskXml, out var command, out var arguments)) return false;

        var image = Path.GetFileName(command);
        if (string.Equals(image, "python.exe", StringComparison.OrdinalIgnoreCase)
            || string.Equals(image, "pythonw.exe", StringComparison.OrdinalIgnoreCase))
            return TryGetDirectPythonAdopter(command, arguments, fileExists, out action);

        if (!string.Equals(image, "wscript.exe", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(image, "cscript.exe", StringComparison.OrdinalIgnoreCase))
            return false;

        var launcherVbs = CommandLineTokens(arguments).FirstOrDefault(
            token => string.Equals(Path.GetFileName(token), "launch_muxd.vbs", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(launcherVbs)
            || (fileExists is not null && !fileExists(launcherVbs))
            || readText is null)
            return false;

        var opsDir = Path.GetDirectoryName(launcherVbs) ?? "";
        var launcherPs1 = Path.Combine(opsDir, "launch_muxd.ps1");
        if (fileExists is not null && !fileExists(launcherPs1)) return false;

        string? vbsText;
        string? ps1Text;
        try
        {
            vbsText = readText(launcherVbs);
            ps1Text = readText(launcherPs1);
        }
        catch
        {
            return false;
        }
        if (string.IsNullOrWhiteSpace(vbsText)
            || string.IsNullOrWhiteSpace(ps1Text)
            || !vbsText.Contains("launch_muxd.ps1", StringComparison.OrdinalIgnoreCase))
            return false;

        var pythonMatch = Regex.Match(
            ps1Text,
            @"-FilePath\s+['""](?<path>[^'""]*pythonw\.exe)['""]",
            RegexOptions.IgnoreCase);
        var muxdMatch = Regex.Match(
            ps1Text,
            @"-ArgumentList\s+['""](?<path>[^'""]*muxd\.py)['""]",
            RegexOptions.IgnoreCase);
        if (!pythonMatch.Success || !muxdMatch.Success) return false;

        var pythonw = pythonMatch.Groups["path"].Value;
        var muxdPath = muxdMatch.Groups["path"].Value;
        var expectedRoot = Path.GetFullPath(Path.Combine(opsDir, ".."));
        var muxdRoot = Path.GetFullPath(Path.GetDirectoryName(muxdPath) ?? "");
        if (!string.Equals(expectedRoot, muxdRoot, StringComparison.OrdinalIgnoreCase)) return false;

        return TryBuildAdopter(pythonw, muxdPath, fileExists, out action);
    }

    private static bool TryGetDirectPythonAdopter(
        string command,
        string arguments,
        Func<string, bool>? fileExists,
        out AdopterAction? action)
    {
        var image = Path.GetFileName(command);
        var pythonw = string.Equals(image, "pythonw.exe", StringComparison.OrdinalIgnoreCase)
            ? command
            : Path.Combine(Path.GetDirectoryName(command) ?? "", "pythonw.exe");
        var muxdPath = CommandLineTokens(arguments)
            .FirstOrDefault(token => string.Equals(Path.GetFileName(token), "muxd.py", StringComparison.OrdinalIgnoreCase));
        return TryBuildAdopter(pythonw, muxdPath, fileExists, out action);
    }

    private static bool TryBuildAdopter(
        string pythonw,
        string? muxdPath,
        Func<string, bool>? fileExists,
        out AdopterAction? action)
    {
        action = null;
        if (string.IsNullOrWhiteSpace(pythonw) || string.IsNullOrWhiteSpace(muxdPath)) return false;
        var muxrun = Path.Combine(Path.GetDirectoryName(muxdPath) ?? "", "muxrun.py");
        if (fileExists is not null && (!fileExists(pythonw) || !fileExists(muxrun))) return false;
        action = new AdopterAction(pythonw, muxrun);
        return true;
    }

    private static bool TryReadExec(string taskXml, out string command, out string arguments)
    {
        command = "";
        arguments = "";
        if (string.IsNullOrWhiteSpace(taskXml)) return false;
        XDocument doc;
        try { doc = XDocument.Parse(taskXml); }
        catch { return false; }
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var exec = doc.Descendants(ns + "Exec").FirstOrDefault();
        command = (exec?.Element(ns + "Command")?.Value ?? "").Trim().Trim('"');
        arguments = (exec?.Element(ns + "Arguments")?.Value ?? "").Trim();
        return command.Length > 0 && arguments.Length > 0;
    }

    private static IEnumerable<string> CommandLineTokens(string value)
    {
        foreach (Match match in Regex.Matches(value ?? "", "\"(?<quoted>[^\"]+)\"|(?<bare>\\S+)"))
        {
            var token = match.Groups["quoted"].Success
                ? match.Groups["quoted"].Value
                : match.Groups["bare"].Value;
            if (token.Length > 0) yield return token;
        }
    }
}
