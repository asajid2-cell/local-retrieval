using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text.Json;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// Live claude/codex agents on THIS PC, by WMI (pid, parent, resumed-session-id, start time) + a scoped
// kill. Ported from the GUI app (MainPage.RunningChats.cs) into Core so the always-on headless server can
// drive the same "running on PC" remote view + kills when the desktop app is closed. Windows-only.
public static class RunningSessions
{
    private static int _openHandleScanDisabled;
    private static readonly TimeSpan OpenHandleScanTimeout = TimeSpan.FromSeconds(2);
    private static readonly System.Management.EnumerationOptions BoundedWmiOptions = new()
    {
        ReturnImmediately = true,
        Timeout = TimeSpan.FromSeconds(2)
    };

    public static List<ArchiveService.RunningSessionInfo> Scan()
    {
        TryScan(out var list, out _);
        return list;
    }

    public static bool TryScan(out List<ArchiveService.RunningSessionInfo> list, out string detail)
    {
        list = new List<ArchiveService.RunningSessionInfo>();
        detail = "";

        // pid -> process name, so we can label each agent's parent (a single cheap scan).
        var names = new Dictionary<int, string>();
        try
        {
            using var all = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT ProcessId, Name FROM Win32_Process"),
                BoundedWmiOptions);
            foreach (ManagementObject mo in all.Get())
                try { names[Convert.ToInt32(mo["ProcessId"])] = mo["Name"]?.ToString() ?? ""; } catch { }
        }
        catch { }

        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT ProcessId, ParentProcessId, CommandLine, CreationDate, Name FROM Win32_Process WHERE Name='claude.exe' OR Name='codex.exe'"),
                BoundedWmiOptions);
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
            return true;
        }
        catch (Exception ex)
        {
            detail = "couldn't verify live claude/codex processes (" + ex.Message + "); refusing to risk a second writer";
            return false;
        }
    }

    // pid -> parent pid for every process, so the mux-tab resolver can walk an agent's ancestry up to the
    // shell muxd spawned (a claude/codex is a child of that shell) and link agent -> tab deterministically.
    public static Dictionary<int, int> ProcessParentMap()
    {
        var map = new Dictionary<int, int>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT ProcessId, ParentProcessId FROM Win32_Process");
            foreach (ManagementObject mo in s.Get())
            {
                int pid = 0, pp = 0;
                try { pid = Convert.ToInt32(mo["ProcessId"]); } catch { }
                try { pp = Convert.ToInt32(mo["ParentProcessId"]); } catch { }
                if (pid > 0) map[pid] = pp;
            }
        }
        catch { }
        return map;
    }

    // Live claude/codex agents with their PARENT pid, any resume id parsed from the command line, and the
    // process START TIME (UTC) — used to correlate a FRESH agent (launched without --resume) to the exact
    // transcript it created at launch, so its tab's session history is right even in a shared folder.
    public static List<(int Pid, int Ppid, string Tool, string SessionId, DateTime StartedUtc)> ScanAgentsWithPpid()
    {
        var list = new List<(int, int, string, string, DateTime)>();
        try
        {
            using var s = new ManagementObjectSearcher(
                "SELECT ProcessId, ParentProcessId, CommandLine, Name, CreationDate FROM Win32_Process WHERE Name='claude.exe' OR Name='codex.exe'");
            foreach (ManagementObject mo in s.Get())
            {
                var cl = mo["CommandLine"]?.ToString() ?? "";
                var nm = (mo["Name"]?.ToString() ?? "").ToLowerInvariant();
                if (!IsLiveAgentProcess(nm, cl)) continue;
                int pid = 0, pp = 0;
                try { pid = Convert.ToInt32(mo["ProcessId"]); } catch { }
                try { pp = Convert.ToInt32(mo["ParentProcessId"]); } catch { }
                DateTime started = default;
                try { started = ManagementDateTimeConverter.ToDateTime(mo["CreationDate"]?.ToString()).ToUniversalTime(); } catch { }
                var tool = nm.Contains("codex") ? "codex" : "claude";
                var sid = ArchiveService.ParseResumedSessionId(cl) ?? "";
                if (pid > 0) list.Add((pid, pp, tool, sid, started));
            }
        }
        catch { }
        return list;
    }

    // Claude Code's OWN per-live-process registry: ~/.claude/sessions/<pid>.json = { pid, sessionId, cwd,
    // status, ... } for EVERY live claude session — including IDLE ones and FORKED/branch ones whose id
    // isn't on the command line. This is the ground truth for "is this claude session live?" (claude
    // open-append-closes its transcript, so a file-handle/mtime check can't see an idle one). Stale entries
    // (process already gone) are dropped when `livePids` is supplied. Returns sessionId -> pid.
    public static Dictionary<string, int> ClaudeLiveSessionIds(HashSet<int>? livePids = null)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
            if (!Directory.Exists(dir)) return map;
            foreach (var f in Directory.EnumerateFiles(dir, "*.json"))
            {
                try
                {
                    // shared read (ReadWrite|Delete): claude writes these registry files live — the default
                    // deny-write share could block its update. Read without ever locking out the writer.
                    string json;
                    using (var rfs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var rsr = new StreamReader(rfs))
                        json = rsr.ReadToEnd();
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    var pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                    var sid = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                    if (pid <= 0 || string.IsNullOrEmpty(sid)) continue;
                    if (livePids is not null && !livePids.Contains(pid)) continue;   // stale entry (process exited)
                    if (!map.ContainsKey(sid!)) map[sid!] = pid;
                }
                catch { }
            }
        }
        catch { }
        return map;
    }

    public static bool TryClaudeLiveSessionIds(HashSet<int>? livePids, out Dictionary<string, int> map, out string detail)
    {
        map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        detail = "";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "sessions");
        if (!Directory.Exists(dir)) return true;

        List<string> files;
        try { files = Directory.EnumerateFiles(dir, "*.json").ToList(); }
        catch (Exception ex)
        {
            detail = "couldn't enumerate Claude live-session registry (" + ex.Message + "); refusing to risk a second writer";
            return false;
        }

        foreach (var f in files)
        {
            if (livePids is not null &&
                int.TryParse(Path.GetFileNameWithoutExtension(f), out var filePid) &&
                !livePids.Contains(filePid))
                continue;

            try
            {
                string json;
                using (var rfs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var rsr = new StreamReader(rfs))
                    json = rsr.ReadToEnd();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                var sid = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                if (pid <= 0 || string.IsNullOrEmpty(sid)) continue;
                if (livePids is not null && !livePids.Contains(pid)) continue;
                if (!map.ContainsKey(sid!)) map[sid!] = pid;
            }
            catch (Exception ex)
            {
                detail = "couldn't verify Claude live-session registry file " + Path.GetFileName(f) + " (" + ex.Message + "); refusing to risk a second writer";
                return false;
            }
        }
        return true;
    }

    // Best-effort set of session ids a live claude/codex process is currently running on this PC, from
    // ALL ground-truth sources unioned: the resume id on a live command line, Claude's live-session
    // registry (idle/forked sessions with no id on the command line), and open transcript handles
    // (codex holds its rollout open; a claude mid-write). Launch guards use TryAllLiveSessionIds so
    // uncertainty fails closed instead of looking like "not live".
    public static HashSet<string> AllLiveSessionIds()
    {
        TryAllLiveSessionIds(out var live, out _);
        return live;
    }

    public static bool TryAllLiveSessionIds(out HashSet<string> live, out string detail)
    {
        live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!TryLiveSessionPids(out var livePids, out detail)) return false;
        foreach (var id in livePids.Keys) live.Add(id);
        return true;
    }

    public static bool TryLiveSessionPids(out Dictionary<string, HashSet<int>> live, out string detail)
    {
        live = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        detail = "";
        var pids = new HashSet<int>();
        if (!TryScan(out var sessions, out detail)) return false;
        foreach (var s in sessions)
        {
            AddLivePid(live, s.SessionId, s.Pid);
            if (s.Pid > 0) pids.Add(s.Pid);
        }
        if (!TryClaudeLiveSessionIds(pids, out var claudeLiveIds, out detail))
            return false;
        foreach (var kv in claudeLiveIds)
            AddLivePid(live, kv.Key, kv.Value);
        if (!TryOpenTranscriptSessionIdsBounded(pids, out var openTranscriptIds, out detail))
            return false;
        foreach (var kv in openTranscriptIds)
            AddLivePid(live, kv.Key, kv.Value);
        return true;
    }

    private static void AddLivePid(Dictionary<string, HashSet<int>> live, string? sessionId, int pid)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || pid <= 0) return;
        if (!live.TryGetValue(sessionId, out var pids))
            live[sessionId] = pids = new HashSet<int>();
        pids.Add(pid);
    }

    public static bool TryMuxOwnedAgentPids(
        string muxName,
        IEnumerable<int> candidatePids,
        out HashSet<int> owned,
        out string detail)
    {
        owned = new HashSet<int>();
        detail = "";
        var liveTabs = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "muxd",
            "live-tabs.json");
        int shellPid;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(liveTabs));
            if (!doc.RootElement.TryGetProperty(muxName, out var row)
                || !row.TryGetProperty("pid", out var pidElement)
                || !pidElement.TryGetInt32(out shellPid)
                || shellPid <= 0)
                return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (Exception ex)
        {
            detail = "could not inspect mux process custody: " + ex.Message;
            return false;
        }

        var parents = new Dictionary<int, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery("SELECT ProcessId, ParentProcessId FROM Win32_Process"),
                BoundedWmiOptions);
            foreach (ManagementObject mo in searcher.Get())
            {
                var pid = Convert.ToInt32(mo["ProcessId"]);
                var parent = Convert.ToInt32(mo["ParentProcessId"]);
                if (pid > 0) parents[pid] = parent;
            }
        }
        catch (Exception ex)
        {
            detail = "could not verify mux process ancestry: " + ex.Message;
            return false;
        }

        foreach (var candidate in candidatePids.Where(pid => pid > 0).Distinct())
        {
            var current = candidate;
            var seen = new HashSet<int>();
            while (current > 0 && seen.Add(current))
            {
                if (current == shellPid)
                {
                    owned.Add(candidate);
                    break;
                }
                if (!parents.TryGetValue(current, out current)) break;
            }
        }
        return true;
    }

    private static bool TryOpenTranscriptSessionIdsBounded(HashSet<int> pids, out Dictionary<string, int> ids, out string detail)
    {
        ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        detail = "";
        if (pids.Count == 0) return true;
        if (Volatile.Read(ref _openHandleScanDisabled) != 0)
        {
            detail = "open transcript handle scan is disabled after a previous timeout; refusing to risk a second writer";
            return false;
        }

        try
        {
            var task = Task.Run(() =>
            {
                var ok = OpenHandles.TryOpenTranscriptSessionIds(pids, out var found, out var scanDetail);
                return (ok, found, scanDetail);
            });
            if (task.Wait(OpenHandleScanTimeout))
            {
                var result = task.Result;
                if (!result.ok)
                {
                    detail = result.scanDetail;
                    return false;
                }
                ids = result.found;
                return true;
            }

            // A timed-out handle scan means we cannot prove whether a transcript-only agent is live.
            // Disable future scans in this process and make launch guards fail closed.
            Volatile.Write(ref _openHandleScanDisabled, 1);
            detail = "open transcript handle scan timed out; refusing to risk a second writer";
            return false;
        }
        catch (Exception ex)
        {
            detail = "open transcript handle scan failed (" + ex.Message + "); refusing to risk a second writer";
            return false;
        }
    }

    // Pure decision (testable): does any of a chat's ids (its own + lineage aliases) appear in the live set?
    public static bool AnyLive(IEnumerable<string?> candidateIds, ISet<string> liveIds)
    {
        foreach (var id in candidateIds)
            if (!string.IsNullOrEmpty(id) && liveIds.Contains(id!)) return true;
        return false;
    }

    public static bool ResumeCommandConflicts(string commandLine, ISet<string> liveIds, out string sessionId)
    {
        sessionId = ArchiveService.ParseResumedSessionId(commandLine);
        return !string.IsNullOrEmpty(sessionId) && liveIds.Contains(sessionId);
    }

    public static bool ResumeCommandConflicts(string commandLine, out string sessionId)
    {
        sessionId = ArchiveService.ParseResumedSessionId(commandLine);
        if (string.IsNullOrEmpty(sessionId)) return false;
        return !TryAllLiveSessionIds(out var live, out _) || live.Contains(sessionId);
    }

    // THE gate the app must consult before spawning a resume: is this chat (by id OR any lineage alias)
    // already being run by a live process? If so, launching another `--resume` makes two writers on one
    // transcript and (verified) Claude then SILENTLY drops writes → lost work. Never spawn when this is true.
    public static bool IsSessionLive(string? sessionId, IEnumerable<string>? aliases = null)
    {
        var ids = new List<string?> { sessionId };
        if (aliases != null) ids.AddRange(aliases);
        return !TryAllLiveSessionIds(out var live, out _) || AnyLive(ids, live);
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
