using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace CodexLocalRetrieval_Native;

// Don't double-run a chat. Resuming a chat that's ALREADY running — in a local terminal or inside a
// multiplex (which runs the agent on this PC via SSH-back) — makes two processes append to the same
// transcript/rollout and corrupts it. We scan live claude/codex processes for the session id each is
// resuming (off their command lines) and guard the resume flows: attach instead of re-injecting, or
// ask to kill the running copy before taking over.
public sealed partial class MainPage
{
    // Every live claude/codex agent process on this PC, with enough context to identify it on the web:
    // which chat it's resuming (SessionId), where it lives (parent: VS Code / Terminal / Multiplex / app),
    // its pid and start time. This is the source of truth for "what's actually running" — including
    // forgotten background VS Code sessions the user can't otherwise see.
    // PERF: two WMI process sweeps per call (~100-300ms each) and several callers per poll cycle
    // (resume guards, remote pushes, the Running page). A 4s cache collapses a burst into ONE sweep;
    // anything mutating processes (kill/launch) can call InvalidateRunningCache() for a fresh view.
    private List<ArchiveService.RunningSessionInfo>? _runningCache;
    private DateTime _runningCacheAt;
    private readonly object _runningCacheLock = new();
    private void InvalidateRunningCache() { lock (_runningCacheLock) _runningCache = null; }

    private List<ArchiveService.RunningSessionInfo> GetRunningSessions()
    {
        lock (_runningCacheLock)
        {
            if (_runningCache is not null && (DateTime.UtcNow - _runningCacheAt).TotalSeconds < 4)
                return _runningCache;
        }
        var fresh = GetRunningSessionsUncached();
        lock (_runningCacheLock) { _runningCache = fresh; _runningCacheAt = DateTime.UtcNow; }
        return fresh;
    }

    private List<ArchiveService.RunningSessionInfo> GetRunningSessionsUncached()
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
                if (!CodexLocalRetrieval.Core.Remote.RunningSessions.IsLiveAgentProcess(procName, cl)) continue;
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
        catch (Exception ex) { Diag.Log("GetRunningSessions failed: " + ex.Message); }
        return list;
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

    // id -> a pid currently resuming it. Catches local AND multiplex runs (both run the agent here).
    private Dictionary<string, int> GetRunningChats()
    {
        var map = new Dictionary<string, int>();
        var pids = new List<int>();
        foreach (var r in GetRunningSessions())
        {
            if (r.Pid > 0) pids.Add(r.Pid);
            if (!string.IsNullOrEmpty(r.SessionId) && !map.ContainsKey(r.SessionId))
                map[r.SessionId] = r.Pid;   // resumed sessions carry their id on the command line
        }
        // GROUND TRUTH for sessions whose id ISN'T on the command line — a chat forked/started locally
        // without --resume, or one sitting IDLE in the background. This is what stops two live copies of the
        // same session (a double-writer that loses progress). Two reliable sources, unioned:
        var livePids = new HashSet<int>(pids);
        //  • Claude keeps its OWN registry (~/.claude/sessions/<pid>.json) of every live session, idle or
        //    forked — claude open-append-closes its transcript so a file check can't see an idle one.
        try
        {
            foreach (var kv in CodexLocalRetrieval.Core.Remote.RunningSessions.ClaudeLiveSessionIds(livePids))
                if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
        }
        catch { }
        //  • Codex holds its rollout file OPEN for the whole session, so which transcript each agent has open
        //    is the ground truth there (also catches a claude that's mid-write).
        try
        {
            if (CodexLocalRetrieval.Core.Remote.RunningSessions.TryOpenTranscriptSessionIds(
                    pids,
                    out var openIds,
                    out _))
            {
                foreach (var kv in openIds)
                    if (!map.ContainsKey(kv.Key)) map[kv.Key] = kv.Value;
            }
        }
        catch { }
        return map;
    }

    // Kill the live agent for a session id (preferred) or an explicit pid — but ONLY if it resolves to one
    // of OUR scanned claude/codex agents. Never an arbitrary process kill from the web.
    private (bool ok, string detail) KillRunningSession(string? sessionId, int pid)
    {
        var sessions = GetRunningSessions();
        var match = pid > 0
            ? sessions.FirstOrDefault(s =>
                s.Pid == pid
                && (string.IsNullOrEmpty(sessionId)
                    || string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase)))
            : (!string.IsNullOrEmpty(sessionId)
                ? sessions.FirstOrDefault(s =>
                    string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                : null);
        // No live match. Report "already gone" ONLY when confirmable — a failed/empty WMI scan would
        // otherwise false-success every kill. Verify the pid directly; if alive, don't claim it's gone.
        if (match is null)
        {
            if (pid > 0)
            {
                try { using var _ = Process.GetProcessById(pid); return (false, "still running but not a tracked claude/codex agent — not killed"); }
                catch (ArgumentException) { return (true, "already gone"); }
            }
            return sessions.Count > 0 ? (true, "already gone") : (false, "couldn't verify — process scan returned nothing");
        }
        try
        {
            Process.GetProcessById(match.Pid).Kill(entireProcessTree: true);
            InvalidateRunningCache();
            Diag.Log($"Remote kill: {match.Tool} pid {match.Pid} (session {match.SessionId})");
            return (true, $"killed {match.Tool} pid {match.Pid}");
        }
        catch (ArgumentException) { return (true, "already gone"); }   // raced out between scan and kill
        catch (Exception ex) { return (false, $"kill failed: {ex.Message}"); }
    }

    private (bool ok, string detail) TryKillChat(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); InvalidateRunningCache(); return (true, "killed"); }
        catch (ArgumentException) { InvalidateRunningCache(); return (true, "already gone"); }
        catch (Exception ex) { Diag.Log($"Kill pid {pid} failed: " + ex.Message); return (false, ex.Message); }
    }

    private enum RelayMuxState { None, Hosted, Legacy }

    // What does the relay know about this name? The relay can report PC-hosted muxd sessions as well as
    // legacy tmux sessions, so parse the JSON instead of string-searching and never treat this as a
    // creation fallback. It is only a duplicate-run guard and cleanup path.
    private static async Task<RelayMuxState> RelayMuxStateAsync(string target, int port, string name)
    {
        try
        {
            // Hardened + hard-timeout via RunSshAsync (no -n/timeout here was another way to hang the app).
            var (_, outText) = await RunSshAsync(target, $"curl -s http://127.0.0.1:{port}/api/sessions");
            using var doc = JsonDocument.Parse(outText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return RelayMuxState.None;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (!s.TryGetProperty("name", out var n) || !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                // A DORMANT session (e.g. every tab after a reboot) is just a placeholder — NOT a running
                // agent. Treat it as none so resume doesn't false-warn "already running / kill it"; the
                // separate local-process check still fires if the chat is actually running on this PC.
                var alive = s.TryGetProperty("alive", out var a) && a.ValueKind == JsonValueKind.True;
                if (!alive) return RelayMuxState.None;
                if (s.TryGetProperty("hosted", out var hosted) && hosted.ValueKind == JsonValueKind.True)
                    return RelayMuxState.Hosted;
                if (s.TryGetProperty("legacy", out var legacy) && legacy.ValueKind == JsonValueKind.True)
                    return RelayMuxState.Legacy;
                return RelayMuxState.Legacy;
            }
        }
        catch { }
        return RelayMuxState.None;
    }

    // DELETE a relay-visible mux session. This is cleanup/duplicate prevention only; creation is local muxd.
    private static async Task<(bool ok, string detail)> DeleteRelayMuxSessionAsync(string target, int port, string name)
    {
        try
        {
            var (code, outText) = await RunSshAsync(target, $"curl -s -X DELETE http://127.0.0.1:{port}/api/sessions/{name}");
            if (code == 0 && outText.Contains("\"ok\":true", StringComparison.OrdinalIgnoreCase)) return (true, "deleted");
            return (false, $"relay delete failed rc={code} {outText.Trim()}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private enum RunGuard { Kill, Cancel }

    private async Task<RunGuard> ConfirmAlreadyRunningAsync(string title, string where)
    {
        var dialog = new ContentDialog
        {
            Title = "This chat is already running",
            Content = $"\"{title}\" looks like it's already running in {where}. Running it twice makes two " +
                      "processes write the same transcript and can corrupt it. Kill the running copy and take over here?",
            PrimaryButtonText = "Kill it & continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var r = await dialog.ShowAsync();
        return r == ContentDialogResult.Primary ? RunGuard.Kill
             : RunGuard.Cancel;
    }

    // Shared pre-launch guard for BOTH resume flows: for THIS chat, check local muxd first, then the
    // relay's explicit hosted/legacy state, plus a loose local agent process. If any are up, offer to
    // kill it or cancel. Returns false to abort the launch.
    private async Task<bool> ConfirmRunOrKillAsync(ArchiveSession session)
    {
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        var muxName = ArchiveService.MultiplexSessionName(session);

        var localMuxUp = await LocalMuxdSessionAliveAsync(muxName);
        var relayState = !localMuxUp && !string.IsNullOrEmpty(target)
            ? await RelayMuxStateAsync(target, settings.MultiplexApiPort, muxName)
            : RelayMuxState.None;
        var relayMuxUp = relayState != RelayMuxState.None;
        var muxUp = localMuxUp || relayMuxUp;
        Dictionary<string, HashSet<int>> running;
        try
        {
            var scan = await Task.Run(() =>
            {
                var ok = CodexLocalRetrieval.Core.Remote.RunningSessions.TryLiveSessionPids(
                    out var live,
                    out var detail);
                return (ok, live, detail);
            }).WaitAsync(TimeSpan.FromSeconds(6));
            if (!scan.ok)
            {
                SyncStatus.Text = scan.detail;
                Diag.Log("Mux launch guard refused: " + scan.detail);
                return false;
            }
            running = scan.live;
        }
        catch (TimeoutException)
        {
            const string detail = "live-session verification timed out; refusing to risk a second writer";
            SyncStatus.Text = detail;
            Diag.Log("Mux launch guard refused: " + detail);
            return false;
        }
        // Match on the session id OR any of its aliases (a fork/resume writes a lineage id) so a live copy
        // started under a different id — but the SAME transcript — is still caught.
        var ids = new HashSet<string>(session.Aliases, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(session.Id)) ids.Add(session.Id);
        var localPids = ids
            .Where(running.ContainsKey)
            .SelectMany(id => running[id])
            .Where(pid => pid > 0)
            .Distinct()
            .ToArray();
        // if a multiplex is up, the running process IS its agent (not a separate local one)
        var localUp = localPids.Length > 0 && !muxUp;

        if (!muxUp && !localUp) return true;   // nothing running -> proceed

        var where = localMuxUp ? "a local mux session"
                  : relayState == RelayMuxState.Hosted ? "a PC-hosted mux session reported by the relay"
                  : relayState == RelayMuxState.Legacy ? "a legacy relay-side session"
                  : "locally on this PC";
        var choice = await ConfirmAlreadyRunningAsync(Trim(session.DisplayTitle, 40), where);
        if (choice == RunGuard.Cancel) return false;
        if (choice == RunGuard.Kill)
        {
            if (localMuxUp)
            {
                var deleted = await DeleteLocalMuxdSessionAsync(muxName);
                if (!deleted.ok)
                {
                    SyncStatus.Text = "Could not kill the local mux session: " + deleted.detail;
                    return false;
                }
            }
            else if (relayMuxUp)
            {
                var deleted = await DeleteRelayMuxSessionAsync(target, settings.MultiplexApiPort, muxName);
                if (!deleted.ok) { SyncStatus.Text = "Could not kill the relay-visible mux session: " + deleted.detail; return false; }
            }
            foreach (var localPid in localPids)
            {
                var killed = CodexLocalRetrieval.Core.Remote.RunningSessions.Kill(null, localPid);
                if (!killed.ok) { SyncStatus.Text = "Could not kill the local running agent: " + killed.detail; return false; }
            }
            await Task.Delay(400);
        }
        return true;
    }
}
