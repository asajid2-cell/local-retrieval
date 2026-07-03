using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// Live claude/codex agents on THIS PC, by WMI (pid, parent, resumed-session-id, start time) + a scoped
// kill. Ported from the GUI app (MainPage.RunningChats.cs) into Core so the always-on headless server can
// drive the same "running on PC" remote view + kills when the desktop app is closed. Windows-only.
public static class RunningSessions
{
    public static List<ArchiveService.RunningSessionInfo> Scan()
    {
        var list = new List<ArchiveService.RunningSessionInfo>();
        try
        {
            // pid -> process name, so we can label each agent's parent (a single cheap scan).
            var names = new Dictionary<int, string>();
            try
            {
                using var all = new ManagementObjectSearcher("SELECT ProcessId, Name FROM Win32_Process");
                foreach (ManagementObject mo in all.Get())
                    try { names[Convert.ToInt32(mo["ProcessId"])] = mo["Name"]?.ToString() ?? ""; } catch { }
            }
            catch { }

            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine, CreationDate, Name FROM Win32_Process WHERE Name='claude.exe' OR Name='codex.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                var cl = mo["CommandLine"]?.ToString() ?? "";
                var procName = mo["Name"]?.ToString() ?? "";
                if (!IsLiveAgentProcess(procName, cl)) continue;
                var sid = ArchiveService.ParseResumedSessionId(cl) ?? "";
                var name = procName.ToLowerInvariant();
                var tool = name.Contains("codex") ? "codex" : "claude";
                var pid = 0; try { pid = Convert.ToInt32(mo["ProcessId"]); } catch { }
                var ppid = 0; try { ppid = Convert.ToInt32(mo["ParentProcessId"]); } catch { }
                var parent = LabelParent(names.TryGetValue(ppid, out var pn) ? pn : "");
                var started = "";
                try { started = ManagementDateTimeConverter.ToDateTime(mo["CreationDate"]?.ToString()).ToUniversalTime().ToString("O"); } catch { }
                if (pid > 0) list.Add(new ArchiveService.RunningSessionInfo(pid, tool, sid, parent, started, ""));
            }
        }
        catch { }
        return list;
    }

    public static bool IsLiveAgentProcess(string processName, string commandLine)
    {
        var name = (processName ?? "").ToLowerInvariant();
        if (name != "claude.exe" && name != "codex.exe") return false;
        return name != "codex.exe" || !FirstArgumentIs(commandLine, "app-server");
    }

    private static bool FirstArgumentIs(string commandLine, string expected)
    {
        var rest = StripExecutable(commandLine).TrimStart();
        if (rest.Length == 0) return false;
        var end = rest.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        var first = end >= 0 ? rest[..end] : rest;
        return string.Equals(first, expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripExecutable(string commandLine)
    {
        var cl = commandLine ?? "";
        cl = cl.TrimStart();
        if (cl.StartsWith("\"", StringComparison.Ordinal))
        {
            var end = cl.IndexOf('"', 1);
            return end >= 0 ? cl[(end + 1)..] : "";
        }
        var split = cl.IndexOfAny(new[] { ' ', '\t', '\r', '\n' });
        return split >= 0 ? cl[(split + 1)..] : "";
    }

    private static string LabelParent(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.StartsWith("code")) return "VS Code";
        if (n is "windowsterminal.exe" or "wt.exe" or "openconsole.exe" or "conhost.exe"
              or "cmd.exe" or "powershell.exe" or "pwsh.exe" or "bash.exe" or "sh.exe") return "Terminal";
        if (n.StartsWith("ssh")) return "Multiplex (SSH)";
        if (n.StartsWith("codexlocalretrieval")) return "This app";
        return string.IsNullOrEmpty(name) ? "Unknown" : name.Replace(".exe", "");
    }

    // Kill the live agent for a session id (preferred) or an explicit pid — but ONLY if it resolves to one
    // of OUR scanned claude/codex processes (never an arbitrary pid).
    public static (bool ok, string detail) Kill(string? sessionId, int pid)
    {
        var sessions = Scan();
        var match = (!string.IsNullOrEmpty(sessionId)
                        ? sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        : null)
                    ?? (pid > 0 ? sessions.FirstOrDefault(s => s.Pid == pid) : null);
        // No live match. "Already gone" is the desired end state — but only report it when we can CONFIRM
        // the process is dead, otherwise a transient/failed WMI scan (which returns an empty list) would
        // false-success EVERY kill. Verify the pid directly; if it's still alive, say so (don't claim gone).
        if (match is null)
        {
            if (pid > 0)
            {
                try { using var _ = Process.GetProcessById(pid); return (false, "still running but not a tracked claude/codex agent — not killed"); }
                catch (ArgumentException) { return (true, "already gone"); }   // pid confirmed not a live process
            }
            // session-id only: trust "gone" only if the scan actually returned data (so an empty scan ≠ success)
            return sessions.Count > 0 ? (true, "already gone") : (false, "couldn't verify — process scan returned nothing");
        }
        try
        {
            Process.GetProcessById(match.Pid).Kill(entireProcessTree: true);
            return (true, $"killed {match.Tool} pid {match.Pid}");
        }
        catch (ArgumentException) { return (true, "already gone"); }   // raced out between scan and kill
        catch (Exception ex) { return (false, $"kill failed: {ex.Message}"); }
    }
}
