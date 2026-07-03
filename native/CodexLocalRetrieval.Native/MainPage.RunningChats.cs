using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Management;
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
                var sid = ArchiveService.ParseResumedSessionId(cl) ?? "";
                var name = (mo["Name"]?.ToString() ?? "").ToLowerInvariant();
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
        foreach (var r in GetRunningSessions())
            if (!string.IsNullOrEmpty(r.SessionId) && !map.ContainsKey(r.SessionId))
                map[r.SessionId] = r.Pid;
        return map;
    }

    // Kill the live agent for a session id (preferred) or an explicit pid — but ONLY if it resolves to one
    // of OUR scanned claude/codex agents. Never an arbitrary process kill from the web.
    private (bool ok, string detail) KillRunningSession(string? sessionId, int pid)
    {
        var sessions = GetRunningSessions();
        var match = (!string.IsNullOrEmpty(sessionId)
                        ? sessions.FirstOrDefault(s => string.Equals(s.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                        : null)
                    ?? (pid > 0 ? sessions.FirstOrDefault(s => s.Pid == pid) : null);
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

    private void TryKillChat(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); InvalidateRunningCache(); }
        catch (Exception ex) { Diag.Log($"Kill pid {pid} failed: " + ex.Message); }
    }

    // Does a multiplex session of this name already exist on the VPS? Lets "Resume in multiplex" ATTACH
    // an already-running session instead of injecting a second resume into it.
    private static async Task<bool> RemoteSessionExistsAsync(string target, int port, string name)
    {
        try
        {
            // Hardened + hard-timeout via RunSshAsync (no -n/timeout here was another way to hang the app).
            var (_, outText) = await RunSshAsync(target, $"curl -s http://127.0.0.1:{port}/api/sessions");
            return outText.Contains($"\"name\":\"{name}\"");
        }
        catch { return false; }
    }

    // DELETE the multiplex session on the VPS (ends its tmux session + the agent running inside it).
    private static async Task DeleteRemoteSessionAsync(string target, int port, string name)
    {
        try
        {
            await RunSshAsync(target, $"curl -s -X DELETE http://127.0.0.1:{port}/api/sessions/{name}");
        }
        catch { }
    }

    private enum RunGuard { Proceed, Kill, Cancel }

    private async Task<RunGuard> ConfirmAlreadyRunningAsync(string title, string where)
    {
        var dialog = new ContentDialog
        {
            Title = "This chat is already running",
            Content = $"\"{title}\" looks like it's already running in {where}. Running it twice makes two " +
                      "processes write the same transcript and can corrupt it. Kill the running copy and take over here?",
            PrimaryButtonText = "Kill it & continue",
            SecondaryButtonText = "Start anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var r = await dialog.ShowAsync();
        return r == ContentDialogResult.Primary ? RunGuard.Kill
             : r == ContentDialogResult.Secondary ? RunGuard.Proceed
             : RunGuard.Cancel;
    }

    // Shared pre-launch guard for BOTH resume flows: for THIS chat, check a multiplex session on the
    // VPS AND a local agent process, and if either is up, offer to kill it (or cancel and deal with it
    // later). Returns false to abort the launch.
    private async Task<bool> ConfirmRunOrKillAsync(ArchiveSession session)
    {
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        var muxName = ArchiveService.MultiplexSessionName(session);

        var muxUp = !string.IsNullOrEmpty(target) && await RemoteSessionExistsAsync(target, settings.MultiplexApiPort, muxName);
        var running = await Task.Run(GetRunningChats);
        var localPid = (!string.IsNullOrEmpty(session.Id) && running.TryGetValue(session.Id, out var pid)) ? pid : 0;
        // if a multiplex is up, the running process IS its agent (not a separate local one)
        var localUp = localPid != 0 && !muxUp;

        if (!muxUp && !localUp) return true;   // nothing running -> proceed

        var where = muxUp ? "a multiplex on the VPS" : "locally on this PC";
        var choice = await ConfirmAlreadyRunningAsync(Trim(session.DisplayTitle, 40), where);
        if (choice == RunGuard.Cancel) return false;
        if (choice == RunGuard.Kill)
        {
            if (muxUp) await DeleteRemoteSessionAsync(target, settings.MultiplexApiPort, muxName);
            if (localPid != 0) TryKillChat(localPid);
            await Task.Delay(400);
        }
        return true;
    }
}
