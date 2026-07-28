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
    private readonly Func<string?, string?, Task<(bool ok, ArchiveService.RemoteMuxLaunch? launch, string detail)>>? _resolveMuxLaunch;
    private readonly Func<Task<IReadOnlyList<ArchiveService.PendingMuxBinding>>>? _resolvePendingMuxBindings;
    private readonly IProcessContainment? _processContainment;
    private readonly string _commandLeaseOwner = RemoteCommandProtocol.LeaseOwner("headless");
    // At-most-once fence for intent-fenced polled commands — see MainPage.Remote.cs for the GUI twin.
    private readonly RemoteCommandProtocol.IntentLedger _commandIntents = new();

    private const string SshHardenOpts = "-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=3";
    private static readonly TimeSpan SshHardTimeout = TimeSpan.FromSeconds(30);

    public RemoteBridge(
        Func<Settings?> settings,
        Func<bool> guiPrimaryRunning,
        ClaudeSessionStore claude,
        string codexDbPath,
        Action<string>? log = null,
        Func<string?, string?, Task<(bool ok, ArchiveService.RemoteMuxLaunch? launch, string detail)>>? resolveMuxLaunch = null,
        Func<Task<IReadOnlyList<ArchiveService.PendingMuxBinding>>>? resolvePendingMuxBindings = null,
        IProcessContainment? processContainment = null)
    {
        _settings = settings;
        _guiPrimaryRunning = guiPrimaryRunning;
        _claude = claude;
        _codexDbPath = codexDbPath ?? "";
        _log = log ?? (_ => { });
        _resolveMuxLaunch = resolveMuxLaunch;
        _resolvePendingMuxBindings = resolvePendingMuxBindings;
        _processContainment = processContainment;
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        _log("remote bridge loop started (acts only while the desktop app is closed)");
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_guiPrimaryRunning())   // desktop app present => it pushes + drains; we do nothing
                {
                    var s = _settings();
                    if (s is not null && !string.IsNullOrWhiteSpace(s.Target))
                    {
                        await ReconcilePendingMuxBindingsAsync();
                        if (tick % 3 == 0) await PushRunningAsync(s);   // running heartbeat ~every 9s (keeps `live` + refreshes the list)
                        await PollAndProcessAsync(s);                    // drain commands ~every 3s (snappy kills); also re-pushes after a kill
                    }
                }
            }
            catch (Exception ex) { _log("bridge tick failed: " + ex.Message); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { }
        }
    }

    private async Task ReconcilePendingMuxBindingsAsync()
    {
        if (_resolvePendingMuxBindings is null) return;
        try
        {
            var listing = await LocalMuxdRequestAsync(new { t = "ls" });
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

        foreach (var binding in await _resolvePendingMuxBindings())
        {
            try
            {
                var launch = binding.Launch;
                var response = await LocalMuxdRequestAsync(new
                {
                    t = "bind",
                    s = binding.MuxName,
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
        var verified = RunningSessions.TryScan(out var scanned, out var verificationDetail);
        var running = scanned.Select(r => new
        {
            pid = r.Pid, tool = r.Tool, sessionId = r.SessionId, parent = r.Parent,
            startedAt = r.StartedAt,
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
        await RunSshAsync(s.Target, remote, json);
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

    private async Task PollAndProcessAsync(Settings s)
    {
        var leaseJson = JsonSerializer.Serialize(new { owner = _commandLeaseOwner, limit = 1 });
        var outText = (await RunSshAsync(
            s.Target,
            $"curl -s -X POST http://127.0.0.1:{s.Port}/api/app-commands/lease -H 'Content-Type: application/json' --data-binary @-",
            leaseJson)).outText;
        if (string.IsNullOrWhiteSpace(outText)) return;
        List<Cmd>? cmds;
        try { cmds = JsonSerializer.Deserialize<List<Cmd>>(outText); } catch { return; }
        if (cmds is null || cmds.Count == 0) return;

        var changed = false;
        foreach (var c in cmds)
        {
            if (string.IsNullOrEmpty(c.id)) continue;
            (bool ok, string detail) res;
            var onPc = false;
            // ONE gate, before any side effect: replay policy AND (for intent-fenced types) a live lease
            // token + a stable intent id that has not already been delivered.
            var admission = _commandIntents.Admit(c.type, c.replayPolicy, c.intentId, c.leaseToken, out var gated);
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
                    res = Rename(c.tool ?? "claude", c.sessionId ?? "", c.title ?? ""); changed |= res.ok; break;
                case "fetchfile":
                {
                    var transfer = await RemoteUploadTransfer.FetchAndInsertAsync(
                        s.Target,
                        c.uploadId ?? "",
                        c.filename ?? "",
                        c.keep,
                        c.muxName ?? c.sessionName,
                        c.insert,
                        c.intentId,
                        LocalMuxdRequestAsync);
                    res = (transfer.Ok, transfer.Detail);
                    onPc = transfer.OnPc;
                    break;
                }
                case "addtocollection":
                    res = (false, "desktop app required for collection changes"); break;
                case "startmux":
                    res = await StartMuxHeadlessAsync(
                        c.muxName ?? c.sessionName ?? "",
                        c.sessionId ?? "",
                        c.tool ?? "",
                        c.intentId,
                        c.takeover);
                    break;
                default:
                    res = (false, "unknown command"); break;
            }
            if (admission == RemoteCommandAdmission.Execute) _commandIntents.Record(c.intentId, res.ok, res.detail);
            var ackJson = JsonSerializer.Serialize(new { leaseToken = c.leaseToken, ok = res.ok, detail = res.detail, onPc });
            await AckCommandAsync(s, c.id, ackJson);
        }
        if (changed) { await Task.Delay(300); await PushRunningAsync(s); }   // reflect a kill/rename fast
    }

    private (bool ok, string detail) Rename(string tool, string id, string title)
    {
        title = (title ?? "").Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return (false, "id and title required");
        if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)) return (false, "Codex titles its own sessions");
        return _claude.RenameSession(id, title) ? (true, "renamed") : (false, "session not found");
    }

    private async Task<(bool ok, string detail)> StartMuxHeadlessAsync(
        string name,
        string requestedSessionId,
        string tool,
        string intentId,
        bool takeover = false)
    {
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

            var resolved = await _resolveMuxLaunch(requestedSessionId, tool);
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
            var text = await LocalMuxdRequestAsync(new
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
                intentId
            });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "created")
            {
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

    // Hardened ssh + hard timeout (see class note). `-n` (stdin from /dev/null) only when not feeding stdin.
    private async Task<(int code, string outText)> RunSshAsync(string target, string remoteCmd, string? stdin = null)
    {
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
                SshHardTimeout,
                stdin,
                maxStdoutChars: 4 * 1024 * 1024,
                maxStderrChars: 64 * 1024,
                containment: _processContainment);
            if (result.TimedOut)
            {
                _log($"ssh timed out ({SshHardTimeout.TotalSeconds:0}s), tree-killed: {remoteCmd}");
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
            var result = await RunSshAsync(
                settings.Target,
                $"curl -sS -X POST http://127.0.0.1:{settings.Port}/api/app-commands/{commandId}/ack -H 'Content-Type: application/json' --data-binary @-",
                ackJson);
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
        public string? tool { get; set; }
        public int pid { get; set; }
        public string? uploadId { get; set; }
        public string? filename { get; set; }
        public string? title { get; set; }
        public bool keep { get; set; }
        public string? muxName { get; set; }
        public string? sessionName { get; set; }
        public string? insert { get; set; }
        public bool takeover { get; set; }
    }
}
