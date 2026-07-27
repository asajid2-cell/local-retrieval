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
    // Per-pid transcript cache keyed by (pid, process start time) — a reused pid is a different process and
    // must never be served the old one's answer. Callers with DIFFERENT pid sets compose from these entries
    // instead of sharing one global scan, so they can no longer refuse each other ("busy verifying another
    // process set" is gone along with the single-flight gate and its 5s give-up).
    // [F#8] ONLY positive resolutions are stored. A failed or unverifiable pid is never cached — a transient
    // hiccup must not fail-close every caller for a whole TTL.
    private static readonly object PerPidTranscriptGate = new();
    private static readonly Dictionary<int, (DateTime StartTimeUtc, DateTime CachedAt, Dictionary<string, int> Sids)>
        _perPidTranscripts = new();
    private static readonly TimeSpan PerPidTranscriptCacheLifetime = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan PerPidHandleTimeout = TimeSpan.FromSeconds(1);

    // ---- burst scan cache -----------------------------------------------------------------------------
    // One remote refresh fires integrity + custody + claim checks back to back, and each one used to pay for
    // its own WMI world sweep plus its own walk of Claude's live-session registry. They ask the same question
    // microseconds apart, so within one burst they now share ONE answer. Same discipline as the per-pid
    // transcript cache above: short TTL, explicit invalidation, and [F#8] a FAILED sweep is never stored — a
    // transient WMI hiccup must not fail-close every caller for a whole TTL.
    //
    // The cache is deliberately PRIVATE and reachable only through TryLiveSessionPids. TryScan and
    // TryClaudeLiveSessionIds stay raw, so Kill (which calls them directly) keeps its oracle-free promise:
    // take-control must work precisely when the shared view is stale or broken.
    private static readonly object ScanCacheGate = new();
    private static readonly TimeSpan ScanCacheLifetime = TimeSpan.FromSeconds(3);
    private static (DateTime CachedAt, List<ArchiveService.RunningSessionInfo> Sessions, string Detail)? _scanCache;
    private static (DateTime CachedAt, HashSet<int>? Key, Dictionary<string, int> Map, string Detail)? _registryCache;
    // Bumped by InvalidateScanCache. A sweep that started BEFORE an invalidation describes the pre-kill world,
    // so its result is discarded rather than stored — otherwise a kill could be undone by an in-flight read.
    private static long _scanCacheEpoch;

    // TEST SEAM (mirrors KillSignals): WMI and Claude's registry are machine-global and cannot be arranged
    // in-test without touching real user state, so the two sweep sources are injectable. Null = the real thing.
    internal sealed record ScanSources(
        Func<(bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail)>? Scan = null,
        Func<HashSet<int>?, (bool Ok, Dictionary<string, int> Map, HashSet<int> Unverifiable, string Detail)>? ClaudeRegistry = null);

    internal static ScanSources? ScanSourceOverride;

    /// Drop the burst scan cache. [F#4] Every site that kills or creates an agent calls this, so the next
    /// liveness question re-sweeps instead of reporting the world as it was before the mutation.
    public static void InvalidateScanCache()
    {
        Interlocked.Increment(ref _scanCacheEpoch);
        lock (ScanCacheGate)
        {
            _scanCache = null;
            _registryCache = null;
        }
    }

    // The WMI world sweep, cached. `bypassCache` forces a fresh sweep AND refreshes the entry for everyone
    // else: [F#3] the post-claim re-check is the one caller whose whole job is to see the newest possible world.
    private static (bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail) CachedScan(bool bypassCache)
    {
        if (!bypassCache)
            lock (ScanCacheGate)
                if (_scanCache is { } hit && DateTime.UtcNow - hit.CachedAt <= ScanCacheLifetime)
                    return (true, new List<ArchiveService.RunningSessionInfo>(hit.Sessions), hit.Detail);

        var epoch = Interlocked.Read(ref _scanCacheEpoch);
        var source = ScanSourceOverride?.Scan;
        var (ok, sessions, detail) = source is not null
            ? source()
            : (TryScan(out var scanned, out var scanDetail), scanned, scanDetail);
        sessions ??= new List<ArchiveService.RunningSessionInfo>();
        if (!ok) return (false, sessions, detail);   // [F#8] a failed sweep is NEVER cached

        lock (ScanCacheGate)
            if (Interlocked.Read(ref _scanCacheEpoch) == epoch)
                _scanCache = (DateTime.UtcNow, new List<ArchiveService.RunningSessionInfo>(sessions), detail);
        return (true, sessions, detail);
    }

    // Claude's live-session registry, cached. The entry is keyed by the pid set it was filtered against, so a
    // caller asking about a DIFFERENT set of pids re-reads rather than inheriting someone else's filter.
    private static (bool Ok, Dictionary<string, int> Map, HashSet<int> Unverifiable, string Detail) CachedClaudeRegistry(
        HashSet<int>? livePids,
        bool bypassCache)
    {
        if (!bypassCache)
            lock (ScanCacheGate)
                if (_registryCache is { } hit
                    && DateTime.UtcNow - hit.CachedAt <= ScanCacheLifetime
                    && SamePidKey(hit.Key, livePids))
                    return (true, new Dictionary<string, int>(hit.Map, StringComparer.OrdinalIgnoreCase), new HashSet<int>(), hit.Detail);

        var epoch = Interlocked.Read(ref _scanCacheEpoch);
        var source = ScanSourceOverride?.ClaudeRegistry;
        var (ok, map, unverifiable, detail) = source is not null
            ? source(livePids)
            : (TryClaudeLiveSessionIds(livePids, out var rm, out var ru, out var rd), rm, ru, rd);
        map ??= new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        unverifiable ??= new HashSet<int>();
        // [F#8] Only a clean read is cached. An unverifiable pid means a live owner may be HIDDEN from this
        // answer, and re-serving that for a whole TTL would turn one write race into a burst-wide blind spot.
        if (!ok) return (false, map, unverifiable, detail);

        lock (ScanCacheGate)
            if (Interlocked.Read(ref _scanCacheEpoch) == epoch)
                _registryCache = (
                    DateTime.UtcNow,
                    livePids is null ? null : new HashSet<int>(livePids),
                    new Dictionary<string, int>(map, StringComparer.OrdinalIgnoreCase),
                    detail);
        return (true, map, unverifiable, detail);
    }

    private static bool SamePidKey(HashSet<int>? cached, HashSet<int>? asked)
        => cached is null ? asked is null : asked is not null && cached.SetEquals(asked);

    // The world scan stays reachable for one release: CODEXLOCAL_LEGACY_HANDLE_SCAN=1 restores it.
    private static bool LegacyHandleScanEnabled
    {
        get
        {
            var v = (Environment.GetEnvironmentVariable("CODEXLOCAL_LEGACY_HANDLE_SCAN") ?? "").Trim();
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
        }
    }
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
        // One world sweep, counted once whether it costs one CIM query or two. This is the number the burst
        // cache exists to hold down, so it must tick per SWEEP, not per query.
        PerfCounters.WmiSweep();

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
        => TryClaudeLiveSessionIds(livePids, out map, out _, out detail);

    public static bool TryClaudeLiveSessionIds(
        HashSet<int>? livePids,
        out Dictionary<string, int> map,
        out HashSet<int> unverifiablePids,
        out string detail)
    {
        map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        unverifiablePids = new HashSet<int>();
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

        return TryReadClaudeRegistryFiles(files, livePids, out map, out unverifiablePids, out detail);
    }

    // An idle fresh claude (no --resume, no open transcript handle) is visible ONLY here, so an unreadable
    // registry file may be HIDING a live owner and must not be silently skipped. But claude rewrites these
    // files live, so most unreadable moments are a transient write race, not a real blocker: retry, and only
    // then decide by the file's pid. Alive pid -> unverifiable (Start-class fails closed); dead pid -> skip.
    // One unreadable file no longer fails the whole check unconditionally.
    internal static bool TryReadClaudeRegistryFiles(
        IEnumerable<string> files,
        HashSet<int>? livePids,
        out Dictionary<string, int> map,
        out HashSet<int> unverifiablePids,
        out string detail)
    {
        map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        unverifiablePids = new HashSet<int>();
        detail = "";
        var unreadable = new List<string>();

        foreach (var f in files)
        {
            var namePid = int.TryParse(Path.GetFileNameWithoutExtension(f), out var fp) ? fp : 0;
            if (livePids is not null && namePid > 0 && !livePids.Contains(namePid)) continue;

            if (!TryReadRegistryFileWithRetry(f, out var json, out var readError))
            {
                // The file's pid is the only identity we have when we cannot read the contents.
                if (namePid > 0 && ProcessOpenFiles.IsAlive(namePid))
                {
                    unverifiablePids.Add(namePid);
                    unreadable.Add(Path.GetFileName(f) + " (" + readError + ")");
                }
                continue;   // dead (or unidentifiable) pid: stale registry leftover, safe to skip
            }

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var pid = root.TryGetProperty("pid", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                var sid = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
                if (pid <= 0 || string.IsNullOrEmpty(sid)) continue;
                if (livePids is not null && !livePids.Contains(pid)) continue;
                if (!map.ContainsKey(sid!)) map[sid!] = pid;
            }
            catch
            {
                // Readable but not parseable: same rule — only a LIVE pid makes it uncertainty.
                if (namePid > 0 && ProcessOpenFiles.IsAlive(namePid))
                {
                    unverifiablePids.Add(namePid);
                    unreadable.Add(Path.GetFileName(f) + " (unparseable)");
                }
            }
        }

        if (unverifiablePids.Count > 0)
        {
            detail = "Claude live-session registry is unreadable for still-running pids: "
                   + string.Join(", ", unreadable)
                   + "; ownership is unverified — this is not a confirmed live owner";
            return false;
        }
        return true;
    }

    private static bool TryReadRegistryFileWithRetry(string path, out string json, out string error)
    {
        json = "";
        error = "";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (attempt > 0) Thread.Sleep(150);
            try
            {
                // shared read (ReadWrite|Delete): claude writes these registry files live — the default
                // deny-write share could block its update. Read without ever locking out the writer.
                using var rfs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var rsr = new StreamReader(rfs);
                json = rsr.ReadToEnd();
                return true;
            }
            catch (FileNotFoundException) { error = "file disappeared"; return false; }   // claude cleaned up; not a retry case
            catch (DirectoryNotFoundException) { error = "directory disappeared"; return false; }
            catch (Exception ex) { error = ex.Message; }
        }
        return false;
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

    // `bypassCache: true` forces a fresh sweep instead of reusing this burst's shared answer. [F#3] The ONLY
    // caller entitled to it is the post-claim re-check in SessionLaunchClaims: it holds the reservation and is
    // asking whether the world changed underneath it, which a cached answer cannot tell it by construction.
    public static bool TryAllLiveSessionIds(out HashSet<string> live, out string detail, bool bypassCache = false)
        => TryAllLiveSessionIds(out live, out _, out detail, bypassCache);

    public static bool TryAllLiveSessionIds(
        out HashSet<string> live,
        out HashSet<int> unverifiablePids,
        out string detail,
        bool bypassCache = false)
    {
        live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = TryLiveSessionPids(out var livePids, out unverifiablePids, out detail, bypassCache);
        foreach (var id in livePids.Keys) live.Add(id);
        return ok;
    }

    public static bool TryLiveSessionPids(out Dictionary<string, HashSet<int>> live, out string detail)
        => TryLiveSessionPids(out live, out _, out detail);

    // Structured variant: which pids blocked verification, so a refusal can say "unverified" instead of
    // claiming a live owner it never found.
    public static bool TryLiveSessionPids(
        out Dictionary<string, HashSet<int>> live,
        out HashSet<int> unverifiablePids,
        out string detail,
        bool bypassCache = false)
    {
        live = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        unverifiablePids = new HashSet<int>();
        detail = "";
        var pids = new HashSet<int>();
        var (scanOk, sessions, scanDetail) = CachedScan(bypassCache);
        detail = scanDetail;
        if (!scanOk) return false;
        foreach (var s in sessions)
        {
            AddLivePid(live, s.SessionId, s.Pid);
            if (s.Pid > 0) pids.Add(s.Pid);
        }
        var ok = true;
        var (registryOk, claudeLiveIds, registryUnverifiable, registryDetail) = CachedClaudeRegistry(pids, bypassCache);
        if (!registryOk)
        {
            unverifiablePids.UnionWith(registryUnverifiable);
            detail = registryDetail;
            ok = false;
        }
        foreach (var kv in claudeLiveIds)
            AddLivePid(live, kv.Key, kv.Value);
        if (!TryOpenTranscriptSessionIdsBounded(pids, out var openTranscriptIds, out var handleUnverifiable, out var handleDetail))
        {
            unverifiablePids.UnionWith(handleUnverifiable);
            detail = string.IsNullOrEmpty(detail) ? handleDetail : detail + "; " + handleDetail;
            ok = false;
        }
        foreach (var kv in openTranscriptIds)
            AddLivePid(live, kv.Key, kv.Value);
        return ok;
    }

    public static bool TryOpenTranscriptSessionIds(
        IEnumerable<int> processIds,
        out Dictionary<string, int> ids,
        out string detail)
        => TryOpenTranscriptSessionIdsBounded(
            new HashSet<int>(processIds.Where(pid => pid > 0)),
            out ids,
            out detail);

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

    // Structured per-pid result: WHICH pids are unverifiable is distinguishable from "nobody holds a
    // transcript". Callers that must fail closed (Start/Resume/mux-create) read UnverifiablePids; the
    // outcome-enum work threads it through as `Unverifiable` rather than a mislabelled "already running".
    public static bool TryOpenTranscriptSessionIds(
        IEnumerable<int> processIds,
        out Dictionary<string, int> ids,
        out HashSet<int> unverifiablePids,
        out string detail)
        => TryOpenTranscriptSessionIdsBounded(
            new HashSet<int>(processIds.Where(pid => pid > 0)),
            out ids,
            out unverifiablePids,
            out detail);

    private static bool TryOpenTranscriptSessionIdsBounded(HashSet<int> pids, out Dictionary<string, int> ids, out string detail)
        => TryOpenTranscriptSessionIdsBounded(pids, out ids, out _, out detail);

    private static bool TryOpenTranscriptSessionIdsBounded(
        HashSet<int> pids,
        out Dictionary<string, int> ids,
        out HashSet<int> unverifiablePids,
        out string detail)
    {
        ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        unverifiablePids = new HashSet<int>();
        detail = "";
        if (pids.Count == 0) return true;

        if (LegacyHandleScanEnabled)
        {
            if (OpenHandles.TryOpenTranscriptSessionIds(pids, out var legacyFound, out var legacyDetail))
            {
                ids = FilterOpenHandleIds(legacyFound, pids);
                return true;
            }
            // The world scan can only fail wholesale, so it cannot attribute the failure to a pid.
            unverifiablePids = new HashSet<int>(pids);
            detail = legacyDetail;
            return false;
        }

        try
        {
            var result = new ProcessOpenFiles.ScanResult();
            foreach (var pid in pids)
            {
                // A pid that isn't alive by the exit-code rule is DEAD: it drops out silently instead of
                // vetoing the set. This is the swarm-churn case the old scan failed closed on.
                if (!ProcessOpenFiles.TryGetAliveIdentity(pid, out var startedUtc))
                {
                    result.DeadPids.Add(pid);
                    continue;
                }
                if (TryGetCachedTranscripts(pid, startedUtc, out var cached))
                {
                    foreach (var kv in cached)
                        if (!result.Found.ContainsKey(kv.Key)) result.Found[kv.Key] = kv.Value;
                    continue;
                }
                var inspection = ProcessOpenFiles.Inspect(pid, PerPidHandleTimeout);
                ProcessOpenFiles.Merge(result, inspection);
                if (inspection.Outcome == ProcessOpenFiles.PidOutcome.Resolved)
                    CachePositiveTranscripts(pid, inspection.StartTimeUtc, inspection.Found);   // [F#8] positives only
            }

            ids = FilterOpenHandleIds(result.Found, pids);
            unverifiablePids = result.UnverifiablePids;
            if (!result.AllVerifiable)
            {
                detail = result.UnverifiableDetail();
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            unverifiablePids = new HashSet<int>(pids);
            detail = "per-pid transcript handle check could not complete (" + ex.Message + "); ownership is unverified";
            return false;
        }
    }

    private static bool TryGetCachedTranscripts(int pid, DateTime startedUtc, out Dictionary<string, int> sids)
    {
        lock (PerPidTranscriptGate)
        {
            if (_perPidTranscripts.TryGetValue(pid, out var entry)
                && entry.StartTimeUtc == startedUtc
                && DateTime.UtcNow - entry.CachedAt <= PerPidTranscriptCacheLifetime)
            {
                sids = entry.Sids;
                return true;
            }
        }
        sids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        return false;
    }

    private static void CachePositiveTranscripts(int pid, DateTime startedUtc, Dictionary<string, int> sids)
    {
        lock (PerPidTranscriptGate)
        {
            _perPidTranscripts[pid] = (startedUtc, DateTime.UtcNow, sids);
            if (_perPidTranscripts.Count > 512)
            {
                var stale = _perPidTranscripts
                    .Where(kv => DateTime.UtcNow - kv.Value.CachedAt > PerPidTranscriptCacheLifetime)
                    .Select(kv => kv.Key)
                    .ToArray();
                foreach (var key in stale) _perPidTranscripts.Remove(key);
            }
        }
    }

    internal static void ResetPerPidTranscriptCache()
    {
        lock (PerPidTranscriptGate) _perPidTranscripts.Clear();
    }

    private static Dictionary<string, int> FilterOpenHandleIds(
        Dictionary<string, int> found,
        HashSet<int> pids)
        => found
            .Where(kv => pids.Contains(kv.Value))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

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
    public static bool TryIsSessionLive(
        string? sessionId,
        IEnumerable<string>? aliases,
        out bool isLive,
        out string detail)
    {
        isLive = false;
        if (!TryAllLiveSessionIds(out var live, out detail)) return false;
        isLive = IsSessionLive(sessionId, aliases, live);
        return true;
    }

    public static bool IsSessionLive(
        string? sessionId,
        IEnumerable<string>? aliases,
        ISet<string> liveIds)
    {
        var ids = new List<string?> { sessionId };
        if (aliases != null) ids.AddRange(aliases);
        return AnyLive(ids, liveIds);
    }

    public static bool IsSessionLive(string? sessionId, IEnumerable<string>? aliases = null)
        => !TryIsSessionLive(sessionId, aliases, out var isLive, out _) || isLive;

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

    // ---- Kill --------------------------------------------------------------------------------------
    //
    // The one real kill. Three things were broken and are fixed here:
    //
    //  1. TARGET RESOLUTION was the WMI command-line scan and nothing else, so an owner visible only through
    //     Claude's registry or through an open transcript handle was INVISIBLE to Kill - and a sessionId-only
    //     kill with an empty scan gave up ("process scan returned nothing"). Targets are now the UNION of four
    //     signals, matched against the full candidate-id set (session id + aliases) rather than one id.
    //  2. VERIFICATION was `IsProcessAlive(pid)` over a tree snapshot, so ONE reused pid anywhere in the tree
    //     made a perfectly successful kill report failure forever. Exit is now decided by (pid, start time)
    //     identity: a pid that came back as a different process means the one we killed is dead.
    //  3. ALL-OR-NOTHING: a stray surviving descendant failed the whole kill. Success is now defined on the
    //     PRIMARY targets (the agents/wrappers we resolved); surviving descendants are reported in the detail.
    //
    // Kill deliberately does NOT call the global liveness oracle (TryLiveSessionPids / TryAllLiveSessionIds).
    // Take-control has to work precisely when the oracle is broken, so both resolution and verification use
    // only the signals below and the specific target pids.
    // What Kill actually proved. `ConfirmedExited` is the primary targets it watched out BY IDENTITY - the only
    // thing Reclaim's tier-2 force-clear is allowed to treat as "a process tied to this claim is really gone".
    public sealed record KillResult(bool Ok, string Detail, IReadOnlyList<ReclaimKilledPid> ConfirmedExited)
    {
        // Callers that only care whether the kill worked keep reading it as the (ok, detail) pair it always was.
        public void Deconstruct(out bool ok, out string detail)
        {
            ok = Ok;
            detail = Detail;
        }
    }

    public static (bool ok, string detail) Kill(string? sessionId, int pid, string? expectedStartedUtc = null)
        => Kill(
            string.IsNullOrWhiteSpace(sessionId) ? Array.Empty<string>() : new[] { sessionId! },
            pid,
            expectedStartedUtc);

    public static (bool ok, string detail) Kill(
        IReadOnlyCollection<string> candidateIds,
        int pid = 0,
        string? expectedStartedUtc = null)
    {
        var result = Kill(candidateIds, pid, expectedStartedUtc, signals: null);
        return (result.Ok, result.Detail);
    }

    // `ownerRecords` exists so a caller with its own record root (Reclaim, tests) resolves the wrapper it
    // actually recorded rather than whatever is under %LOCALAPPDATA%.
    public static KillResult KillWithEvidence(
        IReadOnlyCollection<string> candidateIds,
        int pid = 0,
        string? expectedStartedUtc = null,
        SessionOwnerRecords.Options? ownerRecords = null)
        => Kill(
            candidateIds,
            pid,
            expectedStartedUtc,
            ownerRecords is null ? null : new KillSignals(OwnerRecords: ownerRecords));

    // TEST SEAM: the WMI scan and the Claude registry are machine-global and cannot be arranged in-test
    // without touching real user state, so they are injectable. Everything else - the per-pid handle probe,
    // the owner records, the identity checks, the killing itself - runs for real.
    internal sealed record KillSignals(
        Func<(bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail)>? Scan = null,
        Func<(bool Ok, Dictionary<string, int> Map, HashSet<int> Unverifiable, string Detail)>? ClaudeRegistry = null,
        SessionOwnerRecords.Options? OwnerRecords = null);

    // [F#4] Every success path — "already gone" as much as a confirmed tree kill — ends with the burst cache
    // dropped, so the next liveness question cannot report the process we just removed as still running. The
    // invalidation happens AFTER the kill decided, never inside it: Kill's own signals stay uncached (:674).
    internal static KillResult Kill(
        IReadOnlyCollection<string>? candidateIds,
        int pid,
        string? expectedStartedUtc,
        KillSignals? signals)
    {
        var result = KillCore(candidateIds, pid, expectedStartedUtc, signals);
        if (result.Ok) InvalidateScanCache();
        return result;
    }

    private static KillResult KillCore(
        IReadOnlyCollection<string>? candidateIds,
        int pid,
        string? expectedStartedUtc,
        KillSignals? signals)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in candidateIds ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id.Trim());

        // Targets carry the label that tied them to the session, so the detail can say WHY something was killed.
        var targets = new Dictionary<int, string>();
        // ...and the session ids each target was matched on, so a later reclaim can tell whether a confirmed
        // exit is tied to a specific claim rather than merely contemporaneous with it.
        var targetSids = new Dictionary<int, HashSet<string>>();
        var noEvidence = (IReadOnlyList<ReclaimKilledPid>)Array.Empty<ReclaimKilledPid>();
        var unanswered = new List<string>();   // signals that could not answer at all -> never report "already gone"
        var notes = new List<string>();        // reportable-but-not-fatal observations

        // ---- signal 1: live command lines (WMI) -----------------------------------------------------
        var (scanOk, sessions, scanDetail) = signals?.Scan is not null
            ? signals.Scan()
            : (TryScan(out var scanned, out var sd), scanned, sd);
        if (!scanOk) unanswered.Add("the process scan could not complete (" + scanDetail + ")");
        var scanRows = sessions.Where(s => s.Pid > 0).ToList();
        var scanSidMatches = scanRows
            .Where(s => !string.IsNullOrEmpty(s.SessionId) && ids.Contains(s.SessionId))
            .ToList();
        foreach (var s in scanSidMatches)
            AddTarget(targets, targetSids, s.Pid, s.Tool + " pid " + s.Pid + " (command line)", new[] { s.SessionId });
        // A pid-addressed kill with no session id at all (the mux handoff path) targets the scanned agent itself.
        if (pid > 0 && ids.Count == 0 && scanRows.Any(s => s.Pid == pid))
            AddTarget(targets, targetSids, pid, "agent pid " + pid + " (tracked agent)", null);

        // ---- signal 4a: the owner record we wrote at launch -----------------------------------------
        // Read early even on the fast path: it is a cheap file read, and it carries both the wrapper pid the
        // probe below needs and the job name the kill mechanics prefer.
        var records = ReadOwnerRecords(ids, signals?.OwnerRecords);
        var verifiedWrappers = new Dictionary<int, SessionOwnerRecords.OwnerRecordInfo>();
        foreach (var record in records)
        {
            if (record.WrapperPid <= 0) continue;
            if (!ProcessOpenFiles.TryGetAliveIdentity(record.WrapperPid, out var actualStart))
                continue;   // wrapper already exited - answered, and not a target
            if (record.WrapperStartTimeUtc is null)
            {
                // A live pid we cannot pin to the launch that recorded it. Killing it could kill an unrelated
                // process that inherited the pid, so it is NOT a target - and we must not claim "already gone".
                unanswered.Add($"the recorded wrapper pid {record.WrapperPid} is alive but the record has no start time to identify it by");
                continue;
            }
            if (Math.Abs((actualStart - record.WrapperStartTimeUtc.Value.UtcDateTime).TotalSeconds) > 1)
                continue;   // the pid was REUSED: our wrapper is dead. Answered, and emphatically not a target.
            verifiedWrappers[record.WrapperPid] = record;
            AddTarget(targets, targetSids, record.WrapperPid, "launch wrapper pid " + record.WrapperPid + " (owner record)", record.CandidateIds);
        }

        // ---- signals 2 + 3: Claude's registry and the per-pid transcript probe ----------------------
        // Skipped when a pid-addressed kill is already tied by a cheaper signal: the stop button must not pay
        // for a registry sweep plus a handle probe to kill a process the scan already identified.
        var registryAnswered = true;
        if (pid <= 0 || !targets.ContainsKey(pid))
        {
            var (registryOk, registryMap, registryUnverifiable, registryDetail) = signals?.ClaudeRegistry is not null
                ? signals.ClaudeRegistry()
                : (TryClaudeLiveSessionIds(null, out var rm, out var ru, out var rd), rm, ru, rd);
            registryAnswered = registryOk;
            if (!registryOk) unanswered.Add("Claude's live-session registry could not be read (" + registryDetail + ")");
            foreach (var p in registryUnverifiable)
                notes.Add($"pid {p} alive but not inspectable - close it manually / elevated");

            var registryPids = new HashSet<int>();
            var registrySids = new Dictionary<int, HashSet<string>>();
            foreach (var kv in registryMap)
            {
                if (!ids.Contains(kv.Key) || kv.Value <= 0) continue;
                registryPids.Add(kv.Value);
                if (!registrySids.TryGetValue(kv.Value, out var set))
                    registrySids[kv.Value] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                set.Add(kv.Key);
            }
            foreach (var p in registryPids)
            {
                // A registry file outlives its process; only a still-live pid is a target.
                if (!ProcessOpenFiles.IsAlive(p)) continue;
                AddTarget(targets, targetSids, p, "claude pid " + p + " (live-session registry)", registrySids.GetValueOrDefault(p));
            }

            // Signal 3 runs over the BOUNDED pid set already in hand - never the whole machine. It is what
            // catches an owner running under a DIFFERENT session id that nonetheless holds OUR transcript open.
            var probePids = new HashSet<int>(scanRows.Select(s => s.Pid));
            probePids.UnionWith(registryPids);
            probePids.UnionWith(records.Where(r => r.WrapperPid > 0).Select(r => r.WrapperPid));
            if (pid > 0) probePids.Add(pid);
            if (ids.Count > 0 && probePids.Count > 0)
            {
                var probe = ProcessOpenFiles.Scan(probePids);
                foreach (var kv in probe.Found)
                    if (ids.Contains(kv.Key) && kv.Value > 0 && ProcessOpenFiles.IsAlive(kv.Value))
                        AddTarget(targets, targetSids, kv.Value, "pid " + kv.Value + " (holds this transcript open)", new[] { kv.Key });
                foreach (var p in probe.UnverifiablePids)
                {
                    // Alive but uninspectable: never killed on a guess, always reported.
                    notes.Add($"pid {p} alive but not inspectable - close it manually / elevated");
                    unanswered.Add($"pid {p} is alive but its open files could not be read");
                }
            }
        }

        // ---- pid-addressed kills: that exact process is the target -----------------------------------
        if (pid > 0)
        {
            if (!targets.TryGetValue(pid, out var why))
            {
                // A live pid tied to this session by NO signal stays untouched - Kill has never been a
                // general-purpose process killer and still isn't.
                if (ProcessOpenFiles.IsAlive(pid) || IsProcessAlive(pid))
                    return new KillResult(false, "still running but not a tracked claude/codex agent — not killed", noEvidence);
                return new KillResult(true, "already gone", noEvidence);
            }
            targets = new Dictionary<int, string> { [pid] = why };
        }

        if (targets.Count == 0)
        {
            if (unanswered.Count > 0)
                return new KillResult(false, "couldn't verify — " + string.Join("; ", unanswered.Distinct()), noEvidence);
            // Every signal answered, all of them empty: nothing is running this session.
            return new KillResult(true, Join("already gone", notes), noEvidence);
        }

        // ---- identity gate ---------------------------------------------------------------------------
        if (!string.IsNullOrWhiteSpace(expectedStartedUtc)
            && DateTimeOffset.TryParse(expectedStartedUtc, out var expectedStart))
        {
            foreach (var target in targets.Keys)
            {
                if (!ProcessOpenFiles.TryGetAliveIdentity(target, out var actual)) continue;   // already gone
                if (Math.Abs((actual - expectedStart.UtcDateTime).TotalSeconds) > 1)
                    return new KillResult(false, "process identity changed before stop; refusing to kill a reused pid", noEvidence);
            }
        }

        try
        {
            // ---- snapshot: pid AND identity for every tree member -----------------------------------
            // Capturing the start time here is what makes the 12s verify below immune to pid reuse. A member
            // whose identity cannot be read at snapshot time is watched by bare pid, exactly as before.
            var watch = new Dictionary<int, DateTime?>();
            foreach (var target in targets.Keys)
                foreach (var member in TrySnapshotProcessTree(target))
                    if (!watch.ContainsKey(member))
                        watch[member] = ProcessOpenFiles.TryGetAliveIdentity(member, out var st) ? st : null;

            // ---- job kill first [F#7] ---------------------------------------------------------------
            // The tree snapshot above has an unavoidable race: a child forked during the enumeration is not in
            // it and survives a tree kill. Job membership has no such window, so when the launch was assigned
            // to a job we take that route first and let the tree kill mop up whatever is left.
            foreach (var record in verifiedWrappers.Values
                         .Where(r => !string.IsNullOrWhiteSpace(r.JobName))
                         .GroupBy(r => r.JobName!, StringComparer.Ordinal)
                         .Select(g => g.First()))
            {
                notes.Add(OwnerJobObjects.TryTerminate(record.JobName, out var jobDetail)
                    ? "job kill: " + jobDetail
                    : "job kill did not apply (" + jobDetail + ")");
            }

            foreach (var target in targets.Keys)
            {
                if (!ProcessOpenFiles.TryGetAliveIdentity(target, out _)) continue;   // job kill got it, or it raced out
                try
                {
                    using var process = Process.GetProcessById(target);
                    process.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }   // exited between the identity check and the kill
                catch (Exception ex) { notes.Add($"pid {target} could not be killed directly ({ex.Message})"); }
            }

            // ---- identity-checked verification (same 12s shape) --------------------------------------
            var primaries = new HashSet<int>(targets.Keys);
            var deadline = DateTime.UtcNow.AddSeconds(12);
            List<int> livePrimaries;
            List<int> liveDescendants;
            while (true)
            {
                livePrimaries = primaries.Where(p => !HasExitedByIdentity(p, watch.GetValueOrDefault(p))).OrderBy(p => p).ToList();
                liveDescendants = watch.Keys
                    .Where(p => !primaries.Contains(p) && !HasExitedByIdentity(p, watch[p]))
                    .OrderBy(p => p)
                    .ToList();
                if (livePrimaries.Count == 0 && liveDescendants.Count == 0) break;
                if (DateTime.UtcNow >= deadline) break;
                Thread.Sleep(50);
            }

            // Evidence is the primaries we watched OUT by identity - never a pid we merely asked to die. This is
            // what Reclaim's tier-2 clear consumes, so a partial kill yields partial evidence, not a blanket
            // "everything died".
            var exited = targets.Keys
                .Where(p => !livePrimaries.Contains(p))
                .Select(p => new ReclaimKilledPid(
                    p,
                    watch.GetValueOrDefault(p),
                    (IReadOnlyList<string>)(targetSids.TryGetValue(p, out var sids)
                        ? sids.ToArray()
                        : Array.Empty<string>())))
                .ToArray();

            if (livePrimaries.Count > 0)
                return new KillResult(false, Join(
                    "kill requested, but " + string.Join(", ", livePrimaries.Select(p => targets[p])) + " did not exit",
                    notes), exited);

            // Partial success is success. The things we set out to kill are confirmed dead by identity; a
            // descendant that outlived them (a detached editor, a spawned shell) is worth saying out loud but
            // is not a failed kill.
            if (liveDescendants.Count > 0)
                notes.Add("descendant pids still alive: " + string.Join(", ", liveDescendants));
            return new KillResult(true, Join("killed " + string.Join(", ", targets.Values), notes), exited);
        }
        catch (Exception ex) { return new KillResult(false, $"kill failed: {ex.Message}", noEvidence); }
    }

    private static void AddTarget(
        Dictionary<int, string> targets,
        Dictionary<int, HashSet<string>> targetSids,
        int pid,
        string label,
        IEnumerable<string>? sessionIds)
    {
        if (pid <= 0 || pid == Environment.ProcessId) return;
        if (!targets.ContainsKey(pid)) targets[pid] = label;
        if (sessionIds is null) return;
        if (!targetSids.TryGetValue(pid, out var set))
            targetSids[pid] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in sessionIds)
            if (!string.IsNullOrWhiteSpace(id)) set.Add(id.Trim());
    }

    private static string Join(string head, List<string> notes)
    {
        var extra = notes.Distinct().ToList();
        return extra.Count == 0 ? head : head + "; note: " + string.Join("; ", extra);
    }

    private static IReadOnlyList<SessionOwnerRecords.OwnerRecordInfo> ReadOwnerRecords(
        HashSet<string> ids,
        SessionOwnerRecords.Options? options)
    {
        if (ids.Count == 0) return Array.Empty<SessionOwnerRecords.OwnerRecordInfo>();
        try
        {
            var ordered = ids.ToList();
            return SessionOwnerRecords.ReadRecordsForSession(ordered[0], ordered.Skip(1), options);
        }
        catch { return Array.Empty<SessionOwnerRecords.OwnerRecordInfo>(); }
    }

    // THE reused-pid fix. A pid alone cannot say whether the process we killed is gone: Windows hands pids out
    // again within seconds under load, and the old check called a recycled pid "still alive" forever.
    // Snapshot identity known -> gone when the pid is dead OR now belongs to a different process.
    // Snapshot identity unknown -> fall back to the bare-pid check this used to do everywhere.
    internal static bool HasExitedByIdentity(int pid, DateTime? snapshotStartUtc)
    {
        if (snapshotStartUtc is null) return !IsProcessAlive(pid);
        if (!ProcessOpenFiles.TryGetAliveIdentity(pid, out var current)) return true;
        return Math.Abs((current - snapshotStartUtc.Value).TotalSeconds) > 1;
    }

    private static HashSet<int> TrySnapshotProcessTree(int rootPid)
    {
        try { return SnapshotProcessTree(rootPid); }
        catch { return new HashSet<int> { rootPid }; }   // WMI hiccup: watch at least the target itself
    }

    private static HashSet<int> SnapshotProcessTree(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        using var searcher = new ManagementObjectSearcher(
            new ManagementScope(@"\\.\root\cimv2"),
            new ObjectQuery("SELECT ProcessId, ParentProcessId FROM Win32_Process"),
            BoundedWmiOptions);
        foreach (ManagementObject mo in searcher.Get())
        {
            var child = Convert.ToInt32(mo["ProcessId"]);
            var parent = Convert.ToInt32(mo["ParentProcessId"]);
            if (!children.TryGetValue(parent, out var list))
                children[parent] = list = new List<int>();
            list.Add(child);
        }

        var tree = new HashSet<int> { rootPid };
        var pending = new Stack<int>();
        pending.Push(rootPid);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            if (!children.TryGetValue(parent, out var direct)) continue;
            foreach (var child in direct)
                if (tree.Add(child)) pending.Push(child);
        }
        return tree;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
