using System;
using System.Collections.Generic;
using System.Linq;
using System.Management;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace CodexLocalRetrieval_Native;

// Don't double-run a chat. Resuming a chat that's ALREADY running â€” in a local terminal or inside a
// multiplex (which runs the agent on this PC via SSH-back) â€” makes two processes append to the same
// transcript/rollout and corrupts it. We scan live claude/codex processes for the session id each is
// resuming (off their command lines) and guard the resume flows: attach instead of re-injecting, or
// ask to kill the running copy before taking over.
public sealed partial class MainPage
{
    // Every live claude/codex agent process on this PC, with enough context to identify it on the web:
    // which chat it's resuming (SessionId), where it lives (parent: VS Code / Terminal / Multiplex / app),
    // its pid and start time. This is the source of truth for "what's actually running" â€” including
    // forgotten background VS Code sessions the user can't otherwise see.
    // PERF: two WMI process sweeps per call (~100-300ms each) and several callers per poll cycle
    // (resume guards, remote pushes, the Running page). A 4s cache collapses a burst into ONE sweep;
    // anything mutating processes (kill/launch) can call InvalidateRunningCache() for a fresh view.
    private List<ArchiveService.RunningSessionInfo>? _runningCache;
    private DateTime _runningCacheAt;
    private readonly object _runningCacheLock = new();
    // [F#4] The GUI's own cache and Core's burst scan cache answer the same question from two layers, so they
    // must go stale together: dropping only this one leaves the resume guards and claim checks still reading a
    // pre-kill world through Core.
    private void InvalidateRunningCache()
    {
        lock (_runningCacheLock) _runningCache = null;
        RunningSessions.InvalidateScanCache();
    }

    private List<ArchiveService.RunningSessionInfo> GetRunningSessions()
    {
        lock (_runningCacheLock)
        {
            if (_runningCache is not null && (DateTime.UtcNow - _runningCacheAt).TotalSeconds < 4)
                return _runningCache;
        }
        var (ok, fresh, _) = GetRunningSessionsUncached();
        if (ok)
            lock (_runningCacheLock) { _runningCache = fresh; _runningCacheAt = DateTime.UtcNow; }
        return fresh;
    }

    private (bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail) GetRunningSessionsUncached()
    {
        if (!CodexLocalRetrieval.Core.Remote.RunningSessions.TryScanEnriched(out var list, out var scanDetail))
        {
            Diag.Log("Running session scan unavailable: " + scanDetail);
            return (false, list, scanDetail);
        }
        return (true, list, "");
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
        {
            foreach (var id in r.AllSessionIds)
                if (!map.ContainsKey(id)) map[id] = r.Pid;
        }
        return map;
    }

    private enum RelayMuxState { Unavailable, None, Hosted, Legacy }

    // What does the relay know about this name? The relay can report PC-hosted muxd sessions as well as
    // legacy tmux sessions. Unavailable is distinct from a verified-empty response because launch and
    // takeover decisions must fail closed when the remote owner cannot be checked.
    private static async Task<RelayMuxState> RelayMuxStateAsync(string target, int port, string name)
    {
        try
        {
            var response = await RunSshAsync(target, $"curl -sS -f http://127.0.0.1:{port}/api/sessions");
            if (response.code != 0) return RelayMuxState.Unavailable;
            using var doc = JsonDocument.Parse(response.outText);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return RelayMuxState.Unavailable;
            foreach (var s in doc.RootElement.EnumerateArray())
            {
                if (!s.TryGetProperty("name", out var n) || !string.Equals(n.GetString(), name, StringComparison.OrdinalIgnoreCase))
                    continue;
                var alive = s.TryGetProperty("alive", out var a) && a.ValueKind == JsonValueKind.True;
                if (!alive) return RelayMuxState.None;
                if (s.TryGetProperty("hosted", out var hosted) && hosted.ValueKind == JsonValueKind.True)
                    return RelayMuxState.Hosted;
                return RelayMuxState.Legacy;
            }
            return RelayMuxState.None;
        }
        catch { return RelayMuxState.Unavailable; }
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
    // kill it or cancel.
    //
    // Returns WHY it stopped, not just that it did. A bare bool made scan-failure indistinguishable from
    // a confirmed live owner, and callers wrote the latter into the ledger for both â€” see RunGuardOutcome.
    // The decision itself lives in Core's RunGuardClassifier; this method only does the I/O around it.
    // The guard's verdict PLUS the pids it killed and watched exit by identity. The pids matter because a
    // takeover the operator just confirmed may have stopped one of OUR launches, whose claim is retained for
    // two minutes â€” [F#5] needs that concrete evidence to release the reservation instead of refusing the
    // takeover it just performed.
    private readonly record struct GuardResult(RunGuardDecision Decision, IReadOnlyList<int>? Killed = null)
    {
        public RunGuardOutcome Outcome => Decision.Outcome;
        public string Detail => Decision.Detail;
        public IReadOnlyList<int> KilledPids => Killed ?? Array.Empty<int>();
    }

    private async Task<GuardResult> ConfirmRunOrKillAsync(ArchiveSession session)
    {
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        var muxName = ArchiveService.MultiplexSessionName(session);

        var localMuxUp = await LocalMuxdSessionAliveAsync(muxName);
        var relayState = !localMuxUp && !string.IsNullOrEmpty(target)
            ? await RelayMuxStateAsync(target, settings.MultiplexApiPort, muxName)
            : RelayMuxState.None;
        if (!localMuxUp && relayState == RelayMuxState.Unavailable)
        {
            const string detail = "couldn't verify the relay multiplex session; refusing to risk a second writer";
            SyncStatus.Text = detail;
            Diag.Log("Launch guard could not verify relay mux state: " + muxName);
            return new GuardResult(new RunGuardDecision(RunGuardOutcome.Unverifiable, detail));
        }
        var relayMuxUp = relayState is RelayMuxState.Hosted or RelayMuxState.Legacy;
        var muxUp = localMuxUp || relayMuxUp;
        Dictionary<string, HashSet<int>> running;
        try
        {
            // Threading is deliberate and load-bearing: Task.Run off the UI thread with a hard 6s cap.
            var scan = await Task.Run(() =>
            {
                // STRUCTURED overload: which pids blocked verification, so the refusal can name them
                // instead of claiming a live owner the scan never found.
                var ok = CodexLocalRetrieval.Core.Remote.RunningSessions.TryLiveSessionPids(
                    out var live,
                    out var unverifiablePids,
                    out var detail);
                return (ok, live, unverifiablePids, detail);
            }).WaitAsync(TimeSpan.FromSeconds(6));
            var scanVerdict = RunGuardClassifier.ClassifyScan(scan.ok, timedOut: false, scan.unverifiablePids, scan.detail);
            if (scanVerdict is not null)
            {
                SyncStatus.Text = scanVerdict.Value.Detail;
                Diag.Log("Launch guard could not verify: " + scanVerdict.Value.Detail);
                return new GuardResult(scanVerdict.Value);
            }
            running = scan.live;
        }
        catch (TimeoutException)
        {
            var verdict = RunGuardClassifier.ClassifyScan(false, timedOut: true, null, null)!.Value;
            SyncStatus.Text = verdict.Detail;
            Diag.Log("Launch guard could not verify: " + verdict.Detail);
            return new GuardResult(verdict);
        }
        // Match on the session id OR any of its aliases (a fork/resume writes a lineage id) so a live copy
        // started under a different id â€” but the SAME transcript â€” is still caught.
        var ids = new HashSet<string>(session.Aliases, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(session.Id)) ids.Add(session.Id);
        var localPids = ids
            .Where(running.ContainsKey)
            .SelectMany(id => running[id])
            .Where(pid => pid > 0)
            .Distinct()
            .ToArray();
        // nothing running -> proceed. (If a multiplex is up, the running process IS its agent rather than
        // a separate local copy, but either signal alone still means there's an owner to take over.)
        if (!RunGuardClassifier.NeedsTakeoverPrompt(muxUp, localPids))
            return new GuardResult(new RunGuardDecision(RunGuardOutcome.Proceed, ""));

        var where = localMuxUp ? "a local mux session"
                  : relayState == RelayMuxState.Hosted ? "a PC-hosted mux session reported by the relay"
                  : relayState == RelayMuxState.Legacy ? "a legacy relay-side session"
                  : "locally on this PC";
        var choice = await ConfirmAlreadyRunningAsync(Trim(session.DisplayTitle, 40), where);
        var killOk = true;
        var killDetail = "";
        var killedPids = new List<int>();
        if (choice == RunGuard.Kill)
        {
            if (localMuxUp)
            {
                var deleted = await DeleteLocalMuxdSessionAsync(muxName);
                if (!deleted.ok) { killOk = false; killDetail = "Could not kill the local mux session: " + deleted.detail; }
            }
            else if (relayMuxUp)
            {
                var deleted = await DeleteRelayMuxSessionAsync(target, settings.MultiplexApiPort, muxName);
                if (!deleted.ok) { killOk = false; killDetail = "Could not kill the relay-visible mux session: " + deleted.detail; }
            }
            foreach (var localPid in localPids)
            {
                if (!killOk) break;
                // Empty id set, not null: the pid IS the target here, and `null` is ambiguous between the two
                // Kill overloads (single session id vs. the alias-aware candidate set).
                var killed = CodexLocalRetrieval.Core.Remote.RunningSessions.KillWithEvidence(Array.Empty<string>(), localPid);
                if (!killed.Ok) { killOk = false; killDetail = "Could not kill the local running agent: " + killed.Detail; }
                // Only pids Kill watched OUT by identity count as evidence â€” never a pid we merely asked to die.
                else killedPids.AddRange(killed.ConfirmedExited.Select(k => k.Pid));
            }
            // The owner is still there â€” this is a FAILED takeover of a confirmed live owner, not a cancel.
            if (!killOk) SyncStatus.Text = killDetail;
            else await Task.Delay(400);
        }

        return new GuardResult(RunGuardClassifier.Classify(
            scanOk: true,
            timedOut: false,
            unverifiablePids: null,
            scanDetail: null,
            muxUp: muxUp,
            localPids: localPids,
            dialogChoice: choice == RunGuard.Kill ? RunGuardChoice.Kill : RunGuardChoice.Cancel,
            killOk: killOk,
            killDetail: killDetail), killedPids);
    }
}
