using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// The remote command bridge, runnable from the ALWAYS-ON headless server so remote "running sessions"
// (see / Open transcript / Kill / rename) keep working when the heavy desktop app is closed. It mirrors
// the GUI app's MainPage.Remote.cs loop, but only acts while the GUI app is NOT running — the GUI is the
// primary bridge when open (it pushes the full enriched projection), so the two never double-process.
//
// Reliability: every ssh is hardened (-n / BatchMode / ConnectTimeout / ServerAlive) AND carries a hard
// wall-clock timeout that tree-kills on overrun, so a stalled connection can never wedge the loop or pile
// up orphan ssh.exe (the exact bug that silently killed the GUI app's command poll).
public sealed class RemoteBridge
{
    public sealed record Settings(string Target, int Port);

    private readonly Func<Settings?> _settings;      // cheap, per-tick (SSH target + multiplex API port)
    private readonly Func<bool> _guiPrimaryRunning;  // true => the desktop app owns the bridge; we stand down
    private readonly ClaudeSessionStore _claude;
    private readonly string _codexDbPath;
    private readonly Action<string> _log;
    private readonly Func<string?, string?, string?, Task<(bool ok, ArchiveService.RemoteMuxLaunch? launch, string detail)>>? _resolveMuxLaunch;
    private readonly Func<Task<IReadOnlyList<ArchiveService.PendingMuxBinding>>>? _resolvePendingMuxBindings;
    private readonly Func<JsonElement, Task<(bool ok, string detail)>>? _executeArchiveCommand;
    private readonly Func<StartChatPreparationRequest, Func<object, Task<string>>, Task<StartChatPreparationResult>>? _executeStartChat;
    private readonly Func<JsonElement, Task<(bool ok, string detail)>>? _fetchTranscript;
    private readonly Func<string, string, string, Task<bool>>? _fixtureDownload;
    private readonly string? _fixtureSshConfigPath;
    private readonly string? _fixtureUploadRoot;
    // Test-only loopback transport. Call sites name the operation; the production path remains SSH.
    private readonly Func<Settings, BridgeOperation, string?, string?, TimeSpan?, Task<(int code, string outText)>>? _transport;
    private readonly IProcessContainment? _processContainment;
    private readonly bool _isolationFixture;
    private readonly Func<JsonElement, string, Task<(bool ok, string detail)>>? _captureWorkspace;
    private readonly Func<Task<string>>? _fixtureWorkspaceListing;
    private readonly Func<object, Task<string>>? _fixtureMuxRequest;
    private readonly Func<(bool verified, List<ArchiveService.RunningSessionInfo> sessions, string detail)>? _runningSnapshot;
    public bool IsolationFixture => _isolationFixture;

    internal static string ValidateIsolationPort(int port) =>
        int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_RELAY_PORT"), out var expected) && expected == port
            ? ""
            : "isolated remote test profile requires CLR_REMOTE_TEST_RELAY_PORT to match the archive relay port";

    public enum BridgeOperation { Running, Lease, Ack }
    private readonly string _commandLeaseOwner = RemoteCommandProtocol.LeaseOwner("headless");
    // At-most-once fence for intent-fenced polled commands — see MainPage.Remote.cs for the GUI twin.
    private readonly RemoteCommandProtocol.IntentLedger _commandIntents = new();

    private const string SshHardenOpts = "-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=3";
    private static readonly TimeSpan SshHardTimeout = TimeSpan.FromSeconds(30);
    // How long the relay may hold an empty lease poll open (its cap is 25s; ServerAlive keepalives
    // carry the quiet connection through the hold). Also the GUI twin's value — keep in step.
    public static readonly TimeSpan LeaseHoldWait = TimeSpan.FromSeconds(25);

    public RemoteBridge(
        Func<Settings?> settings,
        Func<bool> guiPrimaryRunning,
        ClaudeSessionStore claude,
        string codexDbPath,
        Action<string>? log = null,
        Func<string?, string?, string?, Task<(bool ok, ArchiveService.RemoteMuxLaunch? launch, string detail)>>? resolveMuxLaunch = null,
        Func<Task<IReadOnlyList<ArchiveService.PendingMuxBinding>>>? resolvePendingMuxBindings = null,
        Func<JsonElement, Task<(bool ok, string detail)>>? executeArchiveCommand = null,
        IProcessContainment? processContainment = null,
        Func<Settings, BridgeOperation, string?, string?, TimeSpan?, Task<(int code, string outText)>>? transport = null,
        bool isolationFixture = false,
        Func<(bool verified, List<ArchiveService.RunningSessionInfo> sessions, string detail)>? runningSnapshot = null,
        Func<JsonElement, string, Task<(bool ok, string detail)>>? captureWorkspace = null,
        Func<Task<string>>? fixtureWorkspaceListing = null,
        Func<StartChatPreparationRequest, Func<object, Task<string>>, Task<StartChatPreparationResult>>? executeStartChat = null,
        Func<object, Task<string>>? fixtureMuxRequest = null,
        Func<string, Task<bool>>? reconcileStartChatBindings = null,
        Func<JsonElement, Func<object, Task<string>>, Task<ReclaimOperationResult>>? executeReclaim = null,
        Func<JsonElement, Task<(bool ok, string detail)>>? fetchTranscript = null,
        Func<string, string, string, Task<bool>>? fixtureDownload = null,
        string? fixtureUploadRoot = null,
        string? fixtureSshConfigPath = null)
    {
        if (isolationFixture && transport is null)
            throw new ArgumentException("isolated bridge requires an injected relay transport", nameof(transport));
        if ((fixtureDownload is not null || fixtureUploadRoot is not null || fixtureSshConfigPath is not null) && !isolationFixture)
            throw new ArgumentException("fixture upload transport requires isolation");
        _fixtureSshConfigPath = fixtureSshConfigPath;
        _fixtureDownload = fixtureDownload;
        _fixtureUploadRoot = fixtureUploadRoot;
        _isolationFixture = isolationFixture;
        _runningSnapshot = runningSnapshot;
        if (fixtureWorkspaceListing is not null && !isolationFixture)
            throw new ArgumentException("fixture workspace listing requires isolation", nameof(fixtureWorkspaceListing));
        _captureWorkspace = captureWorkspace;
        _fixtureWorkspaceListing = fixtureWorkspaceListing;
        if (fixtureMuxRequest is not null && !isolationFixture)
            throw new ArgumentException("fixture mux transport requires isolation", nameof(fixtureMuxRequest));
        _fixtureMuxRequest = fixtureMuxRequest;

        _settings = settings;
        _guiPrimaryRunning = guiPrimaryRunning;
        _claude = claude;
        _codexDbPath = codexDbPath ?? "";
        _log = log ?? (_ => { });
        _resolveMuxLaunch = resolveMuxLaunch;
        _resolvePendingMuxBindings = resolvePendingMuxBindings;
        _executeArchiveCommand = executeArchiveCommand;
        _executeStartChat = executeStartChat;
        _fetchTranscript = fetchTranscript;
        _reconcileStartChatBindings = reconcileStartChatBindings;
        _executeReclaim = executeReclaim;
        _processContainment = processContainment;
        _transport = transport;
    }

    // The running heartbeat and the command drain used to share one 3s tick, with the push taken every 3rd
    // pass. They are now scheduled independently, because they want opposite things: the heartbeat has a
    // deadline it must keep (the relay ages a projection out at 45s, and calls an agent stale at 25s), while
    // the command drain is pure polling whose cost is one ssh handshake against a usually-empty queue. Tying
    // the drain's cadence to the tick counter would have dragged the heartbeat out with it.
    public static readonly TimeSpan RunningPushInterval = TimeSpan.FromSeconds(9);

    public async Task RunLoopAsync(CancellationToken ct)
    {
        _log("remote bridge loop started (acts only while the desktop app is closed)");
        var backoff = new RemotePollBackoff();
        var now = DateTimeOffset.UtcNow;
        var nextPushAt = now;
        var nextPollAt = now;
        // The command poll long-polls the relay (waitMs): the ssh call itself is held open up to
        // ~25s and answered the instant work lands. It therefore runs as an IN-FLIGHT TASK the
        // scheduler observes, never awaits inline — the heartbeat has a 9s cadence against the
        // relay's 25s staleness clock, and a held poll must not be allowed to starve it.
        Task<bool>? inflightPoll = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (inflightPoll is { IsCompleted: true })
                {
                    var sawWork = false;
                    try { sawWork = await inflightPoll; }
                    catch (Exception ex) { _log("command poll failed: " + ex.Message); }
                    inflightPoll = null;
                    nextPollAt = DateTimeOffset.UtcNow
                        + (sawWork ? backoff.OnCommandsReceived() : backoff.OnEmptyPoll());
                }

                if (!_guiPrimaryRunning())   // desktop app present => it pushes + drains; we do nothing
                {
                    var s = _settings();
                    if (s is not null && !string.IsNullOrWhiteSpace(s.Target))
                    {
                        if (!_isolationFixture || _fixtureMuxRequest is not null)
                            await ReconcilePendingMuxBindingsAsync();

                        now = DateTimeOffset.UtcNow;
                        if (now >= nextPushAt)
                        {
                            await PushRunningAsync(s);   // running heartbeat (keeps `live` + refreshes the list)
                            nextPushAt = DateTimeOffset.UtcNow + RunningPushInterval;
                        }
                        // A poll started here may still be in flight when the GUI takes over; its
                        // lease keeps that overlap at-most-once (the GUI leases under its own
                        // owner and can never claim the same command), so it is left to finish.
                        if (inflightPoll is null && DateTimeOffset.UtcNow >= nextPollAt)
                            inflightPoll = PollAndProcessAsync(s);   // drain commands; also re-pushes after a kill
                    }
                    else
                    {
                        // No target configured: nothing to poll, so don't spin at the fast cadence either.
                        nextPollAt = DateTimeOffset.UtcNow + backoff.Current;
                    }
                }
            }
            catch (Exception ex) { _log("bridge tick failed: " + ex.Message); }

            // Sleep until whichever job comes due first, so backing the drain off never delays a
            // heartbeat — and let a completing held poll wake the loop immediately, so a command
            // delivered mid-hold is executed now, not a tick later.
            var wakeAt = nextPushAt < nextPollAt ? nextPushAt : nextPollAt;
            var delay = wakeAt - DateTimeOffset.UtcNow;
            if (delay < MinLoopDelay) delay = MinLoopDelay;   // also the idle cadence while the GUI owns the bridge
            try
            {
                var sleep = Task.Delay(delay, ct);
                await (inflightPoll is null ? sleep : Task.WhenAny(sleep, inflightPoll));
            }
            catch { }
        }
    }

    private static readonly TimeSpan MinLoopDelay = TimeSpan.FromSeconds(3);

    private readonly Func<string, Task<bool>>? _reconcileStartChatBindings;
    private readonly Func<JsonElement, Func<object, Task<string>>, Task<ReclaimOperationResult>>? _executeReclaim;

    private async Task ReconcilePendingMuxBindingsAsync()
    {
        if (_isolationFixture && _fixtureMuxRequest is null) return;
        if (_resolvePendingMuxBindings is null && _reconcileStartChatBindings is null) return;
        Task<string> Request(object frame) => _fixtureMuxRequest is not null
            ? _fixtureMuxRequest(frame) : LocalMuxdRequestAsync(frame);
        try
        {
            var listing = await Request(new { t = "ls" });
            if (_reconcileStartChatBindings is not null) await _reconcileStartChatBindings(listing);
            using var listDoc = JsonDocument.Parse(listing);
            if (!listDoc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return;
            var needsResolution = list.EnumerateArray().Any(session =>
                (session.TryGetProperty("identityPending", out var pending) && pending.ValueKind == JsonValueKind.True)
                || (session.TryGetProperty("alive", out var alive) && alive.ValueKind == JsonValueKind.True
                    && session.TryGetProperty("hasCommand", out var hasCommand) && hasCommand.ValueKind == JsonValueKind.False));
            if (!needsResolution) return;
        }
        catch { return; }

        if (_isolationFixture || _resolvePendingMuxBindings is null) return;
        foreach (var binding in await _resolvePendingMuxBindings())
        {
            try
            {
                var launch = binding.Launch;
                var response = await LocalMuxdRequestAsync(new
                {
                    t = "bind",
                    s = binding.MuxName,
                    generationId = binding.Generation,
                    cmd = launch.Command,
                    sessionId = launch.SessionId,
                    aliases = launch.Aliases
                });
                using var doc = JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("t", out var type) && type.GetString() == "bind-ok")
                    continue;
                _log($"mux identity bind deferred for {binding.MuxName}: {response}");
            }
            catch (Exception ex) { _log($"mux identity bind deferred for {binding.MuxName}: {ex.Message}"); }
        }
    }

    // Keep the web's running list + `live` flag fresh via a LIGHT partial update (only runningSessions),
    // so the collections projection the desktop app last pushed is left intact.
    private async Task PushRunningAsync(Settings s)
    {
        bool verified;
        List<ArchiveService.RunningSessionInfo> scanned;
        string verificationDetail;
        if (_runningSnapshot is not null)
            (verified, scanned, verificationDetail) = _runningSnapshot();
        else
        {
            verified = RunningSessions.TryScanEnriched(out scanned, out verificationDetail);
        }
        var running = (scanned ?? new List<ArchiveService.RunningSessionInfo>()).Select(r => new
        {
            pid = r.Pid, tool = r.Tool, sessionId = r.SessionId, parent = r.Parent,
            startedAt = r.StartedAt,
            sessionAliases = r.SessionAliases ?? Array.Empty<string>(),
            identityStatus = r.IdentityStatus, identitySource = r.IdentitySource,
            title = (string?)null, collection = (string?)null,
            realTitle = RealTitle(r.Tool, r.SessionId),
        }).OrderByDescending(r => r.startedAt, StringComparer.Ordinal).ToList();
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 3,
            host = Environment.MachineName,
            runningSessions = running,
            runningVerified = verified,
            runningVerificationDetail = verified ? "" : verificationDetail,
        });
        var remote = $"curl -s -X POST http://127.0.0.1:{s.Port}/api/running -H 'Content-Type: application/json' --data-binary @-";
        await RunTransportAsync(s, BridgeOperation.Running, json, null);
    }

    // The tool's OWN name where it's a cheap point-lookup (codex thread title). Claude's custom title needs
    // a file read we skip on the light path — those show the desktop-app-less fallback label until it opens.
    private string? RealTitle(string tool, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase))
        {
            try { var (t, _) = ArchiveService.ReadCodexThreadTitle(_codexDbPath, id); return string.IsNullOrWhiteSpace(t) ? null : t; }
            catch { return null; }
        }
        return null;
    }

    /// Returns true when this poll actually carried commands, so the caller can keep the loop fast while
    /// there is work and let it coast once the queue has gone quiet.
    private async Task<bool> PollAndProcessAsync(Settings s)
    {
        // waitMs long-polls the relay: an empty queue holds the answer open and fulfils it the
        // moment a command is enqueued, so delivery is instant while the ssh spawn rate stays at
        // the backoff's idle cadence. A relay predating waitMs ignores the field and answers
        // immediately — the backoff alone then paces the polling, exactly as before. The ssh
        // timeout for this one call must sit well above the hold (the relay caps the hold at 25s).
        var leaseJson = JsonSerializer.Serialize(new { owner = _commandLeaseOwner, limit = 1, waitMs = (int)LeaseHoldWait.TotalMilliseconds });
        var lease = await RunTransportAsync(
            s,
            BridgeOperation.Lease,
            leaseJson,
            LeaseHoldWait + SshHardTimeout);
        if (lease.code != 0) return false;
        var outText = lease.outText;
        if (string.IsNullOrWhiteSpace(outText)) return false;
        List<Cmd>? cmds;
        try { cmds = JsonSerializer.Deserialize<List<Cmd>>(outText); } catch { return false; }
        if (cmds is null || cmds.Count == 0) return false;

        var changed = false;
        foreach (var c in cmds)
        {
            if (string.IsNullOrEmpty(c.id)) continue;
            (bool ok, string detail) res;
            var onPc = false;
            // ONE gate, before any side effect: replay policy AND (for intent-fenced types) a live lease
            // token + a stable intent id that has not already been delivered.
            var admission = _commandIntents.Admit(c.type, c.replayPolicy, c.intentId, c.leaseToken, out var gated);
            if (admission == RemoteCommandAdmission.Busy) continue;
            if (admission != RemoteCommandAdmission.Execute)
            {
                if (admission == RemoteCommandAdmission.Refused)
                    _log($"Remote command REFUSED (envelope): type='{c.type}' id={c.id} — {gated.detail}");
                res = gated;
            }
            else switch ((c.type ?? "").ToLowerInvariant())
            {
                case "kill":
                    // DISABLED: autonomous remote kill (this headless server executed relay-queued "kill"
                    // commands with no user intent, terminating live local sessions — a top cause of lost
                    // work, and it kept running even when the desktop app was closed). Never kill a local
                    // agent from a polled command. Ack as refused so the relay clears it.
                    _log($"Remote kill REFUSED (autonomous kill disabled): session='{c.sessionId}' pid={c.pid}");
                    RecordSessionEvent(
                        c.sessionId,
                        null,
                        "remote.kill.refused",
                        "Autonomous remote kill command refused.",
                        "warn",
                        details: new Dictionary<string, string>
                        {
                            ["requestedTool"] = c.tool ?? "",
                            ["pid"] = c.pid.ToString()
                        });
                    res = (false, "autonomous remote kill is disabled (it was terminating live sessions)"); break;
                case "transcript":
                    res = (true, ReadTranscriptTail(c.tool ?? "claude", c.sessionId ?? "")); break;
                case "rename":
                {
                    if (_executeArchiveCommand is null) { res = (false, "native rename authority unavailable"); break; }
                    using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                    try { res = await _executeArchiveCommand(command.RootElement); }
                    catch (RemoteCommandUnconfirmedException)
                    {
                        _commandIntents.Release(c.intentId);
                        continue;
                    }
                    changed |= res.ok;
                    break;
                }
                case "fetchfile":
                {
                    if (_isolationFixture && (_fixtureMuxRequest is null || (_fixtureDownload is null && _fixtureSshConfigPath is null) || _fixtureUploadRoot is null))
                    { res = (false, "isolated upload transport unavailable"); break; }
                    var transfer = await RemoteUploadTransfer.FetchAndInsertAsync(
                        s.Target,
                        c.uploadId ?? "",
                        c.filename ?? "",
                        c.keep,
                        c.muxName ?? c.sessionName,
                        c.insert,
                        c.intentId,
                        _isolationFixture ? _fixtureMuxRequest! : LocalMuxdRequestAsync,
                        c.sessionId, c.generationId, _fixtureDownload, _fixtureUploadRoot, _fixtureSshConfigPath);
                    if (transfer.Uncertain)
                    {
                        _commandIntents.Release(c.intentId);
                        continue;
                    }
                    res = (transfer.Ok, transfer.Detail);
                    onPc = transfer.OnPc;
                    break;
                }
                case "transcriptfetch":
                {
                    if (_fetchTranscript is null) { res = (false, "transcript authority unavailable"); break; }
                    using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                    res = await _fetchTranscript(command.RootElement);
                    break;
                }
                case "setfavorite":
                case "setapptitle":
                case "archive":
                case "setphrases":
                case "settag":
                {
                    if (_executeArchiveCommand is null)
                    {
                        res = (false, "archive command dispatch unavailable");
                        break;
                    }
                    using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                    res = await _executeArchiveCommand(command.RootElement);
                    break;
                }
                case "checkpointcreate":
                case "checkpointrename":
                case "checkpointdelete":
                case "checkpointspawn":
                case "branchcreate":
                {
                    if (_executeArchiveCommand is null)
                    {
                        res = (false, "archive command dispatch unavailable");
                        break;
                    }
                    using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                    res = await _executeArchiveCommand(command.RootElement);
                    break;
                }
                case "captureworkspace":
                {
                    if (_captureWorkspace is null || (_isolationFixture && _fixtureWorkspaceListing is null))
                    {
                        res = (false, "workspace authority unavailable");
                        break;
                    }
                    var listing = _isolationFixture ? await _fixtureWorkspaceListing!() : await LocalMuxdRequestAsync(new { t = "ls" });
                    using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                    res = await _captureWorkspace(command.RootElement, listing);
                    break;
                }
                case "addtocollection":
                case "removefromcollection":
                case "deckcreate":
                case "collectioncreate":
                case "deckrename":
                case "collectionrename":
                case "collectionmove":
                case "deckdelete":
                case "collectiondelete":
                case "collectionrecover":
                case "collectionpurge":
                case "collectionempty":
                case "collectionsettag":
                case "collectionreorder":
                case "deckreorder":
                {
                    if (_executeArchiveCommand is null)
                    {
                        res = (false, "archive command dispatch unavailable");
                        break;
                    }
                    using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                    res = await _executeArchiveCommand(command.RootElement);
                    break;
                }
                case "reclaim":
                {
                    if (!c.confirmed || _executeReclaim is null || (_isolationFixture && _fixtureMuxRequest is null))
                    {
                        res = (false, "confirmed reclaim authority unavailable");
                        break;
                    }
                    ReclaimOperationResult outcome;
                    try
                    {
                        using var command = JsonDocument.Parse(JsonSerializer.Serialize(c));
                        outcome = await _executeReclaim(command.RootElement, _isolationFixture ? _fixtureMuxRequest! : LocalMuxdRequestAsync);
                    }
                    catch
                    {
                        _commandIntents.Release(c.intentId);
                        continue;
                    }
                    var reply = ReclaimCommandOutcome.Reply(outcome.Status);
                    if (!reply.Acknowledge)
                    {
                        _commandIntents.Release(c.intentId);
                        continue;
                    }
                    res = (reply.Ok, outcome.Detail);
                    break;
                }
                case "startchat":
                {
                    if ((_isolationFixture && _fixtureMuxRequest is null) || _executeStartChat is null)
                    {
                        res = (false, "startchat launch authority unavailable in this profile");
                        break;
                    }
                    var request = new StartChatPreparationRequest(c.intentId, c.muxName ?? c.sessionName ?? "",
                        c.deckId ?? "", c.tool ?? "", c.workspaceId ?? "", c.subfolder ?? "",
                        c.checkpointId ?? "", c.checkpointRevision ?? "", c.collectionId ?? "",
                        c.collectionRevision ?? "", c.collection ?? "", c.title ?? "", c.phrase ?? "",
                        c.launchMode ?? ArchiveService.NativeLaunchMode, c.handoffFromId ?? "");
                    StartChatPreparationResult outcome;
                    try { outcome = await _executeStartChat(request, _isolationFixture ? _fixtureMuxRequest! : LocalMuxdRequestAsync); }
                    catch
                    {
                        _commandIntents.Release(c.intentId);
                        continue;
                    }
                    if (outcome.Uncertain)
                    {
                        _commandIntents.Release(c.intentId);
                        continue;
                    }
                    res = (outcome.Ok, outcome.Detail);
                    break;
                }
                case "startmux":
                    res = await StartMuxHeadlessAsync(
                        c.muxName ?? c.sessionName ?? "",
                        c.sessionId ?? "",
                        c.tool ?? "",
                        c.intentId,
                        c.takeover,
                        c.launchMode);
                    break;
                case "mirrorlocal":
                    res = await MirrorLocalAsync(
                        c.muxName ?? c.sessionName ?? "",
                        c.sessionId ?? "",
                        c.tool ?? "",
                        c.pid);
                    break;
                default:
                    res = (false, "unknown command"); break;
            }
            if (admission == RemoteCommandAdmission.Execute) _commandIntents.Record(c.intentId, res.ok, res.detail);
            var resultId = res.ok && (c.type is "deckcreate" or "collectioncreate") ? res.detail : null;
            var ackJson = JsonSerializer.Serialize(new { leaseToken = c.leaseToken, ok = res.ok, detail = res.detail, resultId, onPc });
            await AckCommandAsync(s, c.id, ackJson);
        }
        if (changed) { await Task.Delay(300); await PushRunningAsync(s); }   // reflect a kill/rename fast
        return true;
    }

    private async Task<(bool ok, string detail)> StartMuxHeadlessAsync(
        string name,
        string requestedSessionId,
        string tool,
        string intentId,
        bool takeover = false,
        string? launchMode = null)
    {
        if (_isolationFixture && (_fixtureMuxRequest is null || takeover || string.IsNullOrWhiteSpace(requestedSessionId)))
            return (false, "isolated fixture resume requires an existing identity and forbids takeover");
        Task<string> Request(object frame) => _isolationFixture ? _fixtureMuxRequest!(frame) : LocalMuxdRequestAsync(frame);
        name = (name ?? "").Trim();
        var eventSessionId = (requestedSessionId ?? "").Trim();
        if (string.IsNullOrEmpty(name))
        {
            RecordSessionEvent(
                eventSessionId,
                null,
                "mux.refused.remote-command",
                "Headless mux start refused because the command had no mux session name.",
                "warn");
            return (false, "missing mux session name");
        }
        try
        {
            if (_resolveMuxLaunch is null)
            {
                const string resolverMissing = "headless remote mux start refused: no local archive resolver is configured";
                RecordSessionEvent(eventSessionId, null, "mux.refused.remote-command", resolverMissing, "warn", details: new Dictionary<string, string> { ["muxName"] = name });
                return (false, resolverMissing);
            }

            var resolved = await _resolveMuxLaunch(requestedSessionId, tool, launchMode);
            if (!resolved.ok || resolved.launch is null)
            {
                RecordSessionEvent(
                    eventSessionId,
                    null,
                    "mux.refused.remote-command",
                    resolved.detail,
                    "warn",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                return (false, resolved.detail);
            }
            var launch = resolved.launch;
            using var capability = JsonDocument.Parse(await Request(new { t = "info" }));
            if (!capability.RootElement.TryGetProperty("caps", out var caps) || caps.ValueKind != JsonValueKind.Array
                || !caps.EnumerateArray().Any(cap => cap.ValueKind == JsonValueKind.String && cap.GetString() == "resumeOnly"))
                return (false, "mux daemon does not support protected resume; update it before resuming remotely");
            if (takeover)
            {
                var transferred = await MuxIdentityTransfer.ExecuteAsync(
                    name,
                    launch.SessionId,
                    launch.Aliases,
                    LocalMuxdRequestAsync);
                if (!transferred.Ok)
                {
                    RecordSessionEvent(
                        launch.SessionId,
                        launch.Aliases,
                        "mux.refused.takeover",
                        transferred.Detail,
                        "warn",
                        details: new Dictionary<string, string> { ["muxName"] = name });
                    return (false, transferred.Detail);
                }
            }
            var text = await Request(new
            {
                t = "create",
                s = name,
                cmd = launch.Command,
                cols = 140,
                rows = 40,
                sessionId = launch.SessionId,
                aliases = launch.Aliases,
                identityPending = string.IsNullOrWhiteSpace(launch.SessionId),
                relaunch = takeover,
                resumeOnly = true,
                intentId
            });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "created")
            {
                var reply = doc.RootElement;
                if (!reply.TryGetProperty("s", out var returnedName) || returnedName.GetString() != name
                    || !reply.TryGetProperty("generationId", out var generation) || string.IsNullOrWhiteSpace(generation.GetString()))
                    return (false, "mux resume response did not identify the requested generation");
                var rows = SessionReclaim.ParseMuxRowsStrict(await Request(new { t = "ls" }), out _);
                if (rows is null || !rows.Any(row => row.Name == name && row.GenerationId == generation.GetString()
                    && row.SessionId == launch.SessionId && row.Alive))
                    return (false, "mux resume could not verify the exact live chat generation");
                RecordSessionEvent(
                    launch.SessionId,
                    launch.Aliases,
                    "mux.started.remote-command",
                    "Started PC-local mux session from headless remote command.",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                return (true, "started PC-local mux session: " + name);
            }
            var detail = doc.RootElement.TryGetProperty("m", out var m)
                ? m.GetString() ?? "muxd error"
                : "unexpected muxd response: " + text;
            RecordSessionEvent(
                launch.SessionId,
                launch.Aliases,
                "mux.failed.remote-command",
                detail,
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return (false, detail);
        }
        catch (Exception ex)
        {
            RecordSessionEvent(
                eventSessionId,
                null,
                "mux.refused.remote-command",
                ex.Message,
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return (false, ex.Message);
        }
    }

    private async Task<(bool ok, string detail)> MirrorLocalAsync(
        string name,
        string requestedSessionId,
        string tool,
        int pid)
    {
        if (_isolationFixture) return (false, "isolated fixture bridge disables local mirror");
        name = (name ?? "").Trim();
        var eventSessionId = (requestedSessionId ?? "").Trim();
        if (string.IsNullOrWhiteSpace(name)) return (false, "missing mux session name");
        if (_resolveMuxLaunch is null)
            return (false, "headless local mirror refused: no local archive resolver is configured");
        try
        {
            var resolved = await _resolveMuxLaunch(requestedSessionId, tool, null);
            if (!resolved.ok || resolved.launch is null) return (false, resolved.detail);
            var launch = resolved.launch;
            var result = await AdoptedMuxLauncher.MirrorAsync(
                new AdoptedMuxLauncher.Request(
                    name,
                    pid,
                    launch.SessionId,
                    launch.Aliases,
                    launch.Tool.ToLowerInvariant(),
                    launch.Command,
                    launch.Workspace),
                LocalMuxdRequestAsync,
                _log);
            RecordSessionEvent(
                launch.SessionId,
                launch.Aliases,
                result.Ok ? "mux.mirror.started" : "mux.mirror.refused",
                result.Detail,
                result.Ok ? "info" : "warn",
                new Dictionary<string, string>
                {
                    ["muxName"] = name,
                    ["pid"] = pid.ToString(),
                });
            return (result.Ok, result.Detail);
        }
        catch (Exception ex)
        {
            RecordSessionEvent(
                eventSessionId,
                null,
                "mux.mirror.refused",
                ex.Message,
                "warn",
                new Dictionary<string, string>
                {
                    ["muxName"] = name,
                    ["pid"] = pid.ToString(),
                });
            return (false, ex.Message);
        }
    }

    private void RecordSessionEvent(
        string? sessionId,
        IEnumerable<string>? aliases,
        string kind,
        string summary,
        string severity = "info",
        IReadOnlyDictionary<string, string>? details = null)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0) return;
            if (!ids.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) ids.Add(id);
        }
        Add(sessionId);
        if (aliases is not null)
            foreach (var alias in aliases) Add(alias);
        var ev = SessionEventLedger.Create(
            kind,
            summary,
            sessionId,
            source: "remote-bridge",
            severity: severity,
            details: details,
            sessionIds: ids);
        SessionEventLedger.AppendBestEffortQueued(ev, _log);
    }

    private static async Task<string> LocalMuxdRequestAsync(object message)
    {
        using var ws = new ClientWebSocket();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:7699"), cts.Token);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);
        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            if (ms.Length + result.Count > 4 * 1024 * 1024)
                throw new InvalidDataException("muxd response exceeded the 4 MiB limit");
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private string ReadTranscriptTail(string tool, string sessionId, int maxChars = 7000)
    {
        try
        {
            List<AgentEvent> evs;
            if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase))
            {
                var path = ArchiveService.ReadCodexRolloutPath(_codexDbPath, sessionId);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(transcript not found on this PC)";
                evs = RolloutToEvents.Parse(path, 600, 6000);
            }
            else
            {
                evs = _claude.ReadHistory(sessionId, 600, 6000);
            }
            var msgs = evs.Where(e => (e.Kind == AgentEventKind.UserMessage || e.Kind == AgentEventKind.AssistantText)
                                      && !e.Delta && !string.IsNullOrWhiteSpace(e.Text)).ToList();
            if (msgs.Count == 0) return "(no messages yet)";
            var sb = new StringBuilder();
            foreach (var e in msgs.TakeLast(20))
            {
                sb.Append(e.Kind == AgentEventKind.UserMessage ? "› you:\n" : "• agent:\n");
                var t = e.Text!.Trim();
                sb.Append(t.Length > 800 ? t[..800] + "…" : t).Append("\n\n");
            }
            var s = sb.ToString().Trim();
            return s.Length <= maxChars ? s : "…\n" + s[^maxChars..];
        }
        catch (Exception ex) { return "(error reading transcript: " + ex.Message + ")"; }
    }

    private Task<(int code, string outText)> RunTransportAsync(Settings settings, BridgeOperation operation, string? stdin, TimeSpan? timeout, string? commandId = null)
    {
        if (operation == BridgeOperation.Ack && !RemoteCommandProtocol.IsWellFormedEnvelopeToken(commandId))
            return Task.FromResult((-1, ""));
        if (_transport is not null) return _transport(settings, operation, stdin, commandId, timeout);
        return RunSshAsync(settings.Target, settings.Port, operation, stdin, timeout, commandId); /* production retains the typed route */
    }

    // Hardened ssh + hard timeout (see class note). `-n` (stdin from /dev/null) only when not feeding
    // stdin. `timeout` widens the wall clock for calls that are held open ON PURPOSE (the long-poll
    // lease); everything else keeps the tight default.
    private async Task<(int code, string outText)> RunSshAsync(string target, int port, BridgeOperation operation, string? stdin = null, TimeSpan? timeout = null, string? commandId = null)
    {
        const string commandBridgeHeader = "h=\"$HOME/.config/mux/command-bridge.header\"; [ -f \"$h\" ] && [ ! -L \"$h\" ] && [ -s \"$h\" ] && [ -r \"$h\" ] && [ $(stat -c %u -- \"$h\") -eq $(id -u) ] || exit 77; p=$(stat -c %A -- \"$h\") || exit 77; case $p in ?r??------) ;; *) exit 77;; esac; ";
        var remoteCmd = operation switch
        {
            BridgeOperation.Running => $"curl -s -X POST http://127.0.0.1:{port}/api/running -H 'Content-Type: application/json' --data-binary @-",
            BridgeOperation.Lease => $"{commandBridgeHeader}curl --fail --silent --show-error -X POST http://127.0.0.1:{port}/api/app-commands/lease -H 'Content-Type: application/json' --header \"@$h\" --data-binary @-",
            BridgeOperation.Ack => $"{commandBridgeHeader}curl --fail --silent --show-error -X POST http://127.0.0.1:{port}/api/app-commands/{commandId}/ack -H 'Content-Type: application/json' --header \"@$h\" --data-binary @-",
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        return await RunSshCommandAsync(target, remoteCmd, stdin, timeout);
    }

    private async Task<(int code, string outText)> RunSshCommandAsync(string target, string remoteCmd, string? stdin = null, TimeSpan? timeout = null)
    {
        var hardTimeout = timeout ?? SshHardTimeout;
        try
        {
            var stdinFlag = stdin is null ? "-n " : "";
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"{stdinFlag}{SshHardenOpts} {target} \"{remoteCmd}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            var result = await ContainedProcessRunner.RunAsync(
                psi,
                hardTimeout,
                stdin,
                maxStdoutChars: 4 * 1024 * 1024,
                maxStderrChars: 64 * 1024,
                containment: _processContainment);
            if (result.TimedOut)
            {
                _log($"ssh timed out ({hardTimeout.TotalSeconds:0}s), tree-killed: {remoteCmd}");
                return (-2, "");
            }
            if (result.StdoutTruncated)
            {
                _log($"ssh output exceeded the 4 MiB capture limit: {remoteCmd}");
                return (-3, "");
            }
            return (result.ExitCode, result.Stdout);
        }
        catch (Exception ex) { _log("RunSsh failed: " + ex.Message); return (-1, ""); }
    }

    private async Task AckCommandAsync(Settings settings, string commandId, string ackJson)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await RunTransportAsync(settings, BridgeOperation.Ack, ackJson, null, commandId);
            if (result.code == 0 && RemoteCommandProtocol.AckSucceeded(result.outText)) return;
            if (attempt < 3) await Task.Delay(attempt * 500);
        }
        _log($"Remote command acknowledgement remained unconfirmed: id={commandId}");
    }

    private sealed class Cmd
    {
        public string id { get; set; } = "";
        public string intentId { get; set; } = "";
        public string leaseToken { get; set; } = "";
        public string type { get; set; } = "";
        public string replayPolicy { get; set; } = "";
        public string? sessionId { get; set; }
        public string? snapshotId { get; set; }
        public string? checkpointId { get; set; }
        public string? checkpointRevision { get; set; }
        public string? collectionRevision { get; set; }
        public string? workspaceId { get; set; }
        public string? subfolder { get; set; }
        public string? phrase { get; set; }
        public string? tool { get; set; }
        public string? launchMode { get; set; }
        public string? handoffFromId { get; set; }
        public int pid { get; set; }
        public string? generationId { get; set; }
        public string? uploadId { get; set; }
        public string? filename { get; set; }
        public string? title { get; set; }
        public string? expectedRevision { get; set; }
        public bool confirmed { get; set; }
        public string? bridgeToken { get; set; }
        public int ttlMs { get; set; }
        public string? expectedCollectionRevision { get; set; }
        public string? collectionId { get; set; }
        public string? collection { get; set; }
        public string? name { get; set; }
        public string? deckId { get; set; }
        public string? targetDeckId { get; set; }
        public string? expectedDeletedRevision { get; set; }
        public string? expectedRecentlyDeletedRevision { get; set; }
        public bool favorite { get; set; }
        public bool archived { get; set; }
        public bool enabled { get; set; }
        public string[]? phrases { get; set; }
        public string? tag { get; set; }
        public string[]? sessionIds { get; set; }
        public string[]? deckIds { get; set; }
        public JsonElement? tabs { get; set; }
        public bool keep { get; set; }

        public string? muxName { get; set; }
        public string? sessionName { get; set; }
        public string? insert { get; set; }
        public bool takeover { get; set; }
    }
}
