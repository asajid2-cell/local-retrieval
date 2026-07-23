using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml;

namespace CodexLocalRetrieval_Native;

// "Start in multiplexer": resume a chat in a PC-local muxd session. muxd owns the PTY on this
// machine; harmonizerlabs.cc/multiplex and local `mux <name>` attach to that same session.
public sealed partial class MainPage
{
    private void ResumeInMultiplex_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) StartRemoteSession(_selected, openLocalAttach: true);
    }

    private void ResumeInHeadlessMultiplex_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) StartRemoteSession(_selected, openLocalAttach: false);
    }

    private async void StartRemoteSession(ArchiveSession session, bool openLocalAttach = false)
    {
        var command = _archive.BuildMultiplexCommand(session);
        if (string.IsNullOrEmpty(command))
        {
            RecordSessionEvent(session, "mux.refused.invalid", "Mux session refused because this chat has no safe resume command.", "warn");
            SyncStatus.Text = "Mux session refused: this chat's id is not a safe resume token.";
            if (ReferenceEquals(_selected, session)) RenderIntegrity(force: true);
            return;
        }

        var name = ArchiveService.MultiplexSessionName(session);
        var title = Trim(session.DisplayTitle, 40);
        var muxStarted = false;
        SyncStatus.Text = $"Starting mux session \"{title}\"...";
        Diag.Log($"Mux start requested name={name} openLocalAttach={openLocalAttach}");

        try
        {
            // Already in muxd? Do not inject a SECOND resume into the same transcript.
            // Attach locally if this was the foreground/local action.
            if (await LocalMuxdSessionAliveAsync(name))
            {
                RecordSessionEvent(
                    session,
                    "mux.already.live",
                    "Mux start reused an already-live muxd session instead of injecting another resume.",
                    details: new Dictionary<string, string>
                    {
                        ["muxName"] = name,
                        ["openLocalAttach"] = openLocalAttach.ToString()
                    });
                if (openLocalAttach)
                {
                    var opened = OpenLocalMuxAttach(name);
                    SyncStatus.Text = opened.ok
                        ? $"\"{title}\" is already in multiplex as \"{name}\" - opening local terminal..."
                        : $"\"{title}\" is already in multiplex as \"{name}\" - attach with mux {name}.";
                }
                else
                {
                    SyncStatus.Text = $"\"{title}\" is already in headless multiplex as \"{name}\" - attach with mux {name}.";
                }
                return;
            }
            // Running locally (or elsewhere) right now? Don't let two copies fight over the transcript.
            var guard = await ConfirmRunOrKillAsync(session);
            if (guard.Outcome == CodexLocalRetrieval.Core.Remote.RunGuardOutcome.Unverifiable)
            {
                // NOT "already running" - the scan never got an answer. Same honesty as tomux.refused.live-scan.
                RecordSessionEvent(
                    session,
                    "mux.refused.unverified",
                    "Mux start refused because live-owner verification failed: " + guard.Detail,
                    "warn",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                SyncStatus.Text = "Refused - couldn't verify whether this chat is already running.";
                return;
            }
            if (guard.Outcome == CodexLocalRetrieval.Core.Remote.RunGuardOutcome.Cancelled
                || guard.Outcome == CodexLocalRetrieval.Core.Remote.RunGuardOutcome.Live)
            {
                var takeoverFailed = guard.Outcome == CodexLocalRetrieval.Core.Remote.RunGuardOutcome.Live;
                RecordSessionEvent(
                    session,
                    "mux.refused.running",
                    takeoverFailed
                        ? "Mux start refused because the live owner could not be stopped: " + guard.Detail
                        : "Mux start cancelled because the session already had a live owner.",
                    "warn",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                // On Live the guard already put the kill failure in SyncStatus - don't stomp it.
                if (!takeoverFailed) SyncStatus.Text = "Cancelled - already running.";
                return;
            }

            // [F#5] The guard may have just killed and verified out this chat's owner; if that owner was one of
            // OUR launches, its claim is still retained. Clear it on that evidence before asking for a lease,
            // or the governor refuses the very takeover the operator just confirmed.
            if (guard.KilledPids.Count > 0)
                ClearClaimsAfterVerifiedKill(session, session.Id, session.Aliases, guard.KilledPids, name);

            var created = await GovernedCreateLocalMuxdSessionAsync(
                new SessionLaunchRequest(
                    session.Id,
                    session.Aliases,
                    session.Tool,
                    "native",
                    $"native mux start ({name})",
                    "mux.refused.claim",
                    "mux.started.local",
                    "mux.failed",
                    session.DisplayTitle,
                    session.Workspace,
                    new Dictionary<string, string> { ["muxName"] = name }),
                name,
                command,
                session.Id,
                session.Aliases);
            if (!created.ok)
            {
                Diag.Log($"Mux create FAILED ({created.detail}) name={name}");
                RecordSessionEvent(
                    session,
                    "mux.refused.create",
                    created.detail,
                    "warn",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                SyncStatus.Text = "Could not create the mux session: " + created.detail;
                return;
            }
            muxStarted = true;

            // Bump like a local resume so the chat is where you expect when you come back to the app.
            session.UpdatedAt = DateTime.UtcNow.ToString("O");
            ReapplyActiveFilter();   // respects the active filter + spam-hide instead of dumping the whole store
            SelectSessionRow(session);
            var metadataPersisted = true;
            try
            {
                await _archive.SaveAsync();
            }
            catch (Exception ex)
            {
                metadataPersisted = false;
                Diag.Log("Mux metadata persistence FAILED after session start " + ex);
                RecordSessionEvent(
                    session,
                    "mux.started.metadata-failed",
                    ex.Message,
                    "error",
                    details: new Dictionary<string, string> { ["muxName"] = name });
            }
            var metadataSuffix = metadataPersisted
                ? ""
                : " Session is live, but its recent-session metadata was not persisted.";

            if (openLocalAttach)
            {
                var opened = OpenLocalMuxAttach(name);
                RecordSessionEvent(
                    session,
                    "mux.started.local",
                    "Started PC-local mux session and opened a local attach terminal.",
                    details: new Dictionary<string, string>
                    {
                        ["muxName"] = name,
                        ["attachOpened"] = opened.ok.ToString()
                    });
                SyncStatus.Text = opened.ok
                    ? $"Mux \"{title}\" live as \"{name}\" - opening local terminal; web can attach at /multiplex.{metadataSuffix}"
                    : $"Mux \"{title}\" live as \"{name}\" - web can attach at /multiplex; local attach failed, run mux {name}.{metadataSuffix}";
            }
            else
            {
                RecordSessionEvent(
                    session,
                    "mux.started.headless",
                    "Started headless PC-local mux session.",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                SyncStatus.Text = $"Headless mux \"{title}\" live as \"{name}\" - open it on your phone at /multiplex or attach with mux {name}.{metadataSuffix}";
            }
        }
        catch (Exception ex)
        {
            if (muxStarted)
            {
                Diag.Log("StartRemoteSession post-start step FAILED " + ex);
                RecordSessionEvent(
                    session,
                    "mux.started.post-start-failed",
                    ex.Message,
                    "error",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                SyncStatus.Text = $"Mux \"{title}\" is live as \"{name}\", but a post-start step failed - see log.";
            }
            else
            {
                Diag.Log("StartRemoteSession FAILED " + ex);
                RecordSessionEvent(
                    session,
                    "mux.failed",
                    ex.Message,
                    "error",
                    details: new Dictionary<string, string> { ["muxName"] = name });
                SyncStatus.Text = "Could not start the mux session: " + ex.Message;
            }
        }
        finally
        {
            if (ReferenceEquals(_selected, session)) RenderIntegrity(force: true);
        }
    }

    private static (bool ok, string detail) OpenLocalMuxAttach(string name)
    {
        try
        {
            var mux = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "mux.cmd");
            if (!File.Exists(mux)) mux = "mux";
            var command = $"{QuoteCmd(mux)} {QuoteCmd(name)}";
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/k \"{command}\"",
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                UseShellExecute = true
            };
            Process.Start(psi);
            return (true, command);
        }
        catch (Exception ex)
        {
            Diag.Log("OpenLocalMuxAttach FAILED " + ex);
            return (false, ex.Message);
        }
    }

    private static string QuoteCmd(string value) => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

    // --- project sync: keep the web's Projects view (harmonizerlabs.cc/multiplex) in step with the app
    // while it's open, so you can see your collections + chats and resume any of them remotely. Pushes
    // the projection now + every 30s; the web shows "synced / app live" when these land. --------------
    private DispatcherTimer? _syncTimer;
    private DispatcherTimer? _cmdTimer;
    private DispatcherTimer? _tabTimer;
    private bool _syncPushing;
    private bool _cmdPolling;
    private readonly string _commandLeaseOwner = RemoteCommandProtocol.LeaseOwner("gui");
    private bool _tabTracking;

    public void StartProjectSync()
    {
        _ = PushProjectsAsync();
        _syncTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _syncTimer.Tick -= OnSyncTick;
        _syncTimer.Tick += OnSyncTick;
        _syncTimer.Start();
        // A faster, lighter loop pulls remote commands (kill / open transcript / fetch an uploaded file)
        // so a tap on the web is actioned within a few seconds, while the heavier full-projection push
        // stays at 30s. 3s keeps kills snappy without hammering SSH (each call is hardened + timeout-capped).
        _cmdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _cmdTimer.Tick -= OnCmdTick;
        _cmdTimer.Tick += OnCmdTick;
        _cmdTimer.Start();
        // Track which chat each mux tab is hosting on a fast loop (off the UI thread) so a brief
        // `claude` → `codex` → exit is caught into the tab's session history even between 30s pushes.
        _tabTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _tabTimer.Tick -= OnTabTick;
        _tabTimer.Tick += OnTabTick;
        _tabTimer.Start();
    }
    private async void OnSyncTick(object? sender, object e) => await PushProjectsAsync();
    private async void OnCmdTick(object? sender, object e) => await PollCommandsAsync();
    private async void OnTabTick(object? sender, object e)
    {
        if (_tabTracking) return;
        _tabTracking = true;
        try
        {
            var bindings = await Task.Run(() => _archive.ResolvePendingMuxBindings());
            foreach (var binding in bindings)
                await BindPendingMuxIdentityAsync(binding);
        }
        catch { }
        finally { _tabTracking = false; }
    }

    private async Task BindPendingMuxIdentityAsync(ArchiveService.PendingMuxBinding binding)
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
            if (!doc.RootElement.TryGetProperty("t", out var type) || type.GetString() != "bind-ok")
                Diag.Log($"Mux identity bind deferred for {binding.MuxName}: {response}");
        }
        catch (Exception ex) { Diag.Log($"Mux identity bind deferred for {binding.MuxName}: {ex.Message}"); }
    }

    private async Task PushProjectsAsync()
    {
        if (_syncPushing) return;
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return;
        string json;
        try
        {
            var scan = await Task.Run(() =>
            {
                var verified = RunningSessions.TryScan(out var list, out var detail);
                return (verified, detail, sessions: EnrichRunningSessionTitles(ResolveMissingSessionIds(list)));
            });
            var sessions = scan.sessions;
            var running = new HashSet<string>(
                sessions.Where(s => !string.IsNullOrEmpty(s.SessionId)).Select(s => s.SessionId),
                StringComparer.OrdinalIgnoreCase);
            json = _archive.BuildProjectsProjectionJson(running, sessions, scan.verified, scan.detail);
            await _archive.SaveMuxHistoryIfDirtyAsync();   // persist any tab-session-history rotation the projection detected
        }
        catch (Exception ex) { Diag.Log("BuildProjects failed: " + ex.Message); return; }
        _syncPushing = true;
        try
        {
            // POST over our own owner-only SSH to the VPS loopback (the API trusts loopback); the JSON
            // rides ssh stdin into curl so its quotes/backslashes never touch a shell command line.
            // Hardened + hard-timeout via RunSshAsync so a stalled push can't wedge _syncPushing either.
            var remote = $"curl -s -X POST http://127.0.0.1:{settings.MultiplexApiPort}/api/projects -H 'Content-Type: application/json' --data-binary @-";
            var (code, outText) = await RunSshAsync(target, remote, json);
            Diag.Log($"Projects sync rc={code} out={outText.Trim()}");
        }
        catch (Exception ex) { Diag.Log("PushProjects failed: " + ex.Message); }
        finally { _syncPushing = false; }
    }

    // Pull pending owner commands from the VPS (the web enqueues them) and action them on this PC. Only
    // "kill <session>" today: kill the live agent for that session (scoped to OUR claude/codex processes),
    // ack the result, and immediately re-push the projection so the web reflects the kill within seconds.
    private async Task PollCommandsAsync()
    {
        if (_cmdPolling) return;
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return;
        _cmdPolling = true;
        try
        {
            var port = settings.MultiplexApiPort;
            var leaseJson = JsonSerializer.Serialize(new { owner = _commandLeaseOwner, limit = 1 });
            var outText = (await RunSshAsync(
                target,
                $"curl -s -X POST http://127.0.0.1:{port}/api/app-commands/lease -H 'Content-Type: application/json' --data-binary @-",
                leaseJson)).outText;
            if (string.IsNullOrWhiteSpace(outText)) return;
            List<AppCommand>? cmds;
            try { cmds = JsonSerializer.Deserialize<List<AppCommand>>(outText); } catch { return; }
            if (cmds is null || cmds.Count == 0) return;

            var killed = false;
            var renamed = false;
            var added = false;
            foreach (var c in cmds)
            {
                if (string.IsNullOrEmpty(c.id)) continue;
                (bool ok, string detail) res;
                var onPc = false;
                if (!RemoteCommandProtocol.IsReplaySafe(c.type, c.replayPolicy))
                {
                    res = (false, "command replay policy is missing or invalid");
                }
                else if (string.Equals(c.type, "kill", StringComparison.OrdinalIgnoreCase))
                {
                    // DISABLED: autonomous remote kill was the #1 cause of lost work. Relay-queued "kill"
                    // commands were executed here every 3s with NO user intent — and when the ack curl
                    // timed out the command wasn't dequeued, so it re-fired every poll, terminating live
                    // local claude/codex sessions (see "Remote kill (session )" bursts in the log). The app
                    // must NEVER kill a local agent from a polled command. Ack it as refused so the relay
                    // clears it; killing is a deliberate LOCAL action only (or re-add behind an approval gate).
                    Diag.Log($"Remote kill REFUSED (autonomous kill disabled): session='{c.sessionId}' pid={c.pid}");
                    RecordSessionEvent(
                        null,
                        "remote.kill.refused",
                        "Autonomous remote kill command refused.",
                        "warn",
                        details: new Dictionary<string, string>
                        {
                            ["requestedSessionId"] = c.sessionId ?? "",
                            ["requestedTool"] = c.tool ?? "",
                            ["pid"] = c.pid.ToString()
                        });
                    res = (false, "autonomous remote kill is disabled (it was terminating live sessions); kill locally instead");
                }
                else if (string.Equals(c.type, "transcript", StringComparison.OrdinalIgnoreCase))
                {
                    var text = await Task.Run(() => ReadTranscriptTail(c.tool ?? "claude", c.sessionId ?? "", 7000));
                    res = (true, text);
                }
                else if (string.Equals(c.type, "transcriptfetch", StringComparison.OrdinalIgnoreCase))
                {
                    res = await PushTranscriptPagesAsync(target, port, c);
                }
                else if (string.Equals(c.type, "rename", StringComparison.OrdinalIgnoreCase))
                {
                    var status = await _archive.RenameNativeByIdAsync(c.tool ?? "claude", c.sessionId ?? "", c.title ?? "");
                    var ok = status is not null && !status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                                && !status.Contains("needs", StringComparison.OrdinalIgnoreCase)
                                                && !status.Contains("not found", StringComparison.OrdinalIgnoreCase);
                    res = (ok, status ?? "renamed");
                    renamed |= ok;
                }
                else if (string.Equals(c.type, "setapptitle", StringComparison.OrdinalIgnoreCase))
                {
                    var ok = await _archive.RenameAppTitleByIdAsync(c.sessionId ?? "", c.title ?? "");
                    res = (ok, ok ? "app name set" : "chat not in this app's archive");
                    renamed |= ok;
                }
                else if (string.Equals(c.type, "fetchfile", StringComparison.OrdinalIgnoreCase))
                {
                    var transfer = await RemoteUploadTransfer.FetchAndInsertAsync(
                        target,
                        c.uploadId ?? "",
                        c.filename ?? "",
                        c.keep,
                        c.muxName ?? c.sessionName,
                        c.insert,
                        c.intentId,
                        message => LocalMuxdRequestAsync(message));
                    res = (transfer.Ok, transfer.Detail);
                    onPc = transfer.OnPc;
                }
                else if (string.Equals(c.type, "addtocollection", StringComparison.OrdinalIgnoreCase))
                {
                    res = await AddSessionToCollectionAsync(c.muxName ?? c.sessionName ?? "", c.collection ?? "", c.collectionId ?? "", c.deckId ?? "", c.deckName ?? c.deck ?? "", c.tool ?? "", c.sessionId ?? "");
                    added |= res.ok;
                }
                else if (string.Equals(c.type, "startmux", StringComparison.OrdinalIgnoreCase))
                {
                    res = await StartMuxHeadlessFromIntentAsync(
                        c.muxName ?? c.sessionName ?? "",
                        c.sessionId ?? "",
                        c.tool ?? "",
                        c.intentId,
                        c.takeover);
                }
                else if (string.Equals(c.type, "cleartabhistory", StringComparison.OrdinalIgnoreCase))
                {
                    var n = await _archive.ClearMuxTabHistoryAsync(c.muxName);   // one tab, or ALL when muxName is empty
                    res = (true, n > 0 ? $"cleared session history for {n} tab(s)" : "no tab history to clear");
                    added |= n > 0;   // trigger a fast re-push so the web reflects the cleared history
                }
                else if (string.Equals(c.type, "settabcolor", StringComparison.OrdinalIgnoreCase))
                {
                    await _archive.SetTabColorAsync(c.muxName ?? c.sessionName ?? "", c.title);   // title carries the hex color ("" clears)
                    res = (true, "tab color set");
                    added = true;   // re-push so the web re-tints
                }
                else res = (false, "unknown command");
                var ackJson = JsonSerializer.Serialize(new { leaseToken = c.leaseToken, ok = res.ok, detail = res.detail, onPc });
                await AckCommandAsync(target, port, c.id, ackJson);
            }
            if (killed || renamed || added) { await Task.Delay(300); await PushProjectsAsync(); }   // reflect a kill/rename/add fast
        }
        catch (Exception ex) { Diag.Log("PollCommands failed: " + ex.Message); }
        finally { _cmdPolling = false; }
    }

    // transcriptfetch — an archive.read for ONE explicitly named chat. The relay has already verified
    // the signed client principal envelope and that principal's current membership of the resource;
    // what this side enforces is the rest of the fence: the id must be opaque, the leased command must
    // carry a scoped bridge credential and a bounded fetch TTL, and we ship the clean transcript in
    // capped pages. Never a bulk mirror of history — one id, one chat, one bounded window.
    //
    // The pages go to the relay's own store over our owner-only ssh; the ack carries only a count,
    // because the relay discards app-supplied ack detail (server.js:1491-1497).
    private async Task<(bool ok, string detail)> PushTranscriptPagesAsync(string target, int port, AppCommand c)
    {
        var sessionId = (c.sessionId ?? "").Trim();
        var admission = TranscriptFetchProjection.AdmitFetch(sessionId, c.bridgeToken, c.ttlMs);
        if (!admission.Allowed) return (false, admission.Reason);

        var session = _archive.ResolveSessionByIdOrAlias(sessionId, c.tool);
        if (session is null) return (false, "chat not in this app's archive");

        var user = await _archive.ExtractReaderMessagesAsync(session, "user");
        var assistant = await _archive.ExtractReaderMessagesAsync(session, "assistant");
        var merged = TranscriptFetchProjection.MergeChronological(user, assistant);
        var redact = TranscriptFetchProjection.RedactReadsEnabled();
        var pages = TranscriptFetchProjection.BuildPages(sessionId, merged, redact);
        if (pages.Count == 0) return (true, "no readable messages in that chat");

        string remote;
        try { remote = TranscriptFetchProjection.PushCommand(port, sessionId, c.bridgeToken!); }
        catch (ArgumentException ex) { return (false, ex.Message); }

        // The TTL bounds the whole fetch, not each hop: once it lapses we stop pushing rather than
        // keep writing history the requester is no longer entitled to.
        var deadline = Stopwatch.StartNew();
        var pushed = 0;
        foreach (var page in pages)
        {
            if (deadline.ElapsedMilliseconds > c.ttlMs)
                return (false, $"transcript fetch TTL lapsed after {pushed}/{pages.Count} page(s)");
            var (code, outText) = await RunSshAsync(target, remote, page.Json);
            if (code != 0) return (false, $"page {page.Page}/{page.Pages} push failed rc={code} {outText.Trim()}");
            pushed++;
        }
        Diag.Log($"Transcript fetch pushed {pushed} page(s) for session={sessionId} redact={redact}");
        return (true, $"pushed {pushed} page(s){(redact ? " (redacted)" : "")}");
    }

    private static async Task AckCommandAsync(string target, int port, string commandId, string ackJson)
    {
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await RunSshAsync(
                target,
                $"curl -sS -X POST http://127.0.0.1:{port}/api/app-commands/{commandId}/ack -H 'Content-Type: application/json' --data-binary @-",
                ackJson);
            if (result.code == 0 && RemoteCommandProtocol.AckSucceeded(result.outText)) return;
            if (attempt < 3) await Task.Delay(attempt * 500);
        }
        Diag.Log($"Remote command acknowledgement remained unconfirmed: id={commandId}");
    }

    private sealed class AppCommand
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
        public string? collection { get; set; }
        public string? collectionId { get; set; }
        public string? deckId { get; set; }
        public string? deck { get; set; }
        public string? deckName { get; set; }
        public bool takeover { get; set; }

        // transcriptfetch only: the relay mints a scoped, short-lived credential when it queues the
        // command and bounds the fetch with a TTL. Neither is stored in AppSettings — the app holds
        // no standing authority to write transcript history, only what a single lease grants it.
        public string? bridgeToken { get; set; }
        public int ttlMs { get; set; }
    }

    private async Task<(bool ok, string detail)> StartMuxHeadlessFromIntentAsync(
        string name,
        string sessionId,
        string tool,
        string intentId,
        bool takeover = false)
    {
        name = (name ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return (false, "missing mux session name");
        if (!_archive.TryBuildRemoteMuxLaunch(sessionId, tool, out var launch, out var detail) || launch is null)
        {
            RecordSessionEvent(
                string.IsNullOrWhiteSpace(sessionId) ? null : _archive.ResolveSessionByIdOrAlias(sessionId, tool),
                "mux.refused.remote-command",
                detail,
                "warn",
                details: new Dictionary<string, string>
                {
                    ["muxName"] = name,
                    ["requestedSessionId"] = sessionId ?? ""
                });
            return (false, detail);
        }
        var session = _archive.ResolveSessionByIdOrAlias(launch.SessionId, launch.Tool);
        if (takeover)
        {
            var transferred = await CodexLocalRetrieval.Core.Remote.MuxIdentityTransfer.ExecuteAsync(
                name,
                launch.SessionId,
                launch.Aliases,
                message => LocalMuxdRequestAsync(message));
            if (!transferred.Ok)
            {
                RecordSessionEvent(
                    session,
                    "mux.refused.takeover",
                    transferred.Detail,
                    "warn",
                    details: new Dictionary<string, string>
                    {
                        ["muxName"] = name,
                        ["sessionId"] = launch.SessionId
                    });
                return (false, transferred.Detail);
            }
            // [F#5] verified kill -> clear the tied (retained) claim before the lease is requested.
            if (transferred.ExitedPids.Count > 0)
                ClearClaimsAfterVerifiedKill(session, launch.SessionId, launch.Aliases, transferred.ExitedPids, name);
        }
        var created = await GovernedCreateLocalMuxdSessionAsync(
            new SessionLaunchRequest(
                launch.SessionId,
                launch.Aliases,
                launch.Tool,
                "native",
                $"headless mux start ({name})",
                "mux.refused.claim",
                "mux.started.remote-command",
                "mux.failed",
                session?.DisplayTitle,
                session?.Workspace,
                new Dictionary<string, string> { ["muxName"] = name }),
            name,
            launch.Command,
            launch.SessionId,
            launch.Aliases,
            intentId,
            relaunch: takeover);
        RecordSessionEvent(
            session,
            created.ok ? "mux.started.remote-command" : "mux.refused.remote-command",
            created.ok ? "Started PC-local mux session from remote command." : created.detail,
            created.ok ? "info" : "warn",
            details: new Dictionary<string, string>
            {
                ["muxName"] = name,
                ["sessionId"] = launch.SessionId
            });
        return created.ok ? (true, "started PC-local mux session: " + name) : created;
    }

    // /tomux handoff: resolve the caller's own session, stop the local owner, verify the
    // transcript is no longer live, then start muxd as the new owner. Starting mux first creates
    // the double-writer race that loses Claude/Codex rollouts.
    private async Task<CodexLocalRetrieval.Core.Models.AgentCommandResult> HandleToMuxAsync(CodexLocalRetrieval.Core.Models.AgentCommand c)
    {
        CodexLocalRetrieval.Core.Models.ArchiveSession? session = null;
        try { session = await _archive.ResolveOrIndexTargetAsync(c); } catch { }
        if (session is null)
        {
            RecordSessionEvent(
                null,
                "tomux.refused.unresolved",
                "Tomux handoff refused because the caller session could not be resolved.",
                "warn",
                details: new Dictionary<string, string>
                {
                    ["requestedId"] = c.id ?? c.target ?? "",
                    ["requestedTool"] = c.tool ?? ""
                });
            return new CodexLocalRetrieval.Core.Models.AgentCommandResult(false, "couldn't resolve this session — pass id + tool (e.g. $env:CLAUDE_CODE_SESSION_ID).");

        }

        var name = !string.IsNullOrWhiteSpace(c.name) ? c.name!.Trim() : ArchiveService.MultiplexSessionName(session);
        var command = _archive.BuildMultiplexCommand(session);
        if (string.IsNullOrWhiteSpace(command))
        {
            RecordSessionEvent(
                session,
                "tomux.refused.invalid",
                "Tomux handoff refused because this chat has no safe resume command.",
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return new CodexLocalRetrieval.Core.Models.AgentCommandResult(false, "no resume command for this session.");
        }

        // Only consult the live scan when the caller gave us no pid. `sessionLive` is initialized
        // because the && short-circuits when a pid IS supplied, leaving the out-param unassigned.
        var sessionLive = false;
        if (c.pid <= 0
            && !CodexLocalRetrieval.Core.Remote.RunningSessions.TryIsSessionLive(
                session.Id,
                session.Aliases,
                out sessionLive,
                out var liveDetail))
        {
            RecordSessionEvent(
                session,
                "tomux.refused.live-scan",
                liveDetail,
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return new CodexLocalRetrieval.Core.Models.AgentCommandResult(false,
                "couldn't verify whether this chat is already live: " + liveDetail,
                ResolvedSessionId: session.Id);
        }

        if (c.pid <= 0 && sessionLive)
        {
            RecordSessionEvent(
                session,
                "tomux.refused.missing-pid",
                "Tomux handoff refused because the live caller did not provide a host pid.",
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return new CodexLocalRetrieval.Core.Models.AgentCommandResult(false,
                "this chat is live but /tomux did not provide a host pid; refusing to start a second writer.",
                ResolvedSessionId: session.Id);
        }

        var transferred = await CodexLocalRetrieval.Core.Remote.MuxIdentityTransfer.ExecuteAsync(
            name,
            session.Id,
            session.Aliases,
            message => LocalMuxdRequestAsync(message));
        if (!transferred.Ok)
        {
            RecordSessionEvent(
                session,
                "tomux.refused.consolidation",
                transferred.Detail,
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return new CodexLocalRetrieval.Core.Models.AgentCommandResult(false,
                "couldn't consolidate the existing mux tab for this chat: " + transferred.Detail,
                ResolvedSessionId: session.Id);
        }

        // [F#5] The transfer stopped the local caller and watched it exit. That is the tier-2 evidence that lets
        // the previous launch's retained claim go, so the governed create below is not refused by our own
        // reservation from two minutes ago. The pid-handoff logic above is untouched.
        if (transferred.ExitedPids.Count > 0)
            ClearClaimsAfterVerifiedKill(session, session.Id, session.Aliases, transferred.ExitedPids, name);

        var created = transferred.AlreadyOwned
            ? (ok: true, detail: "mux session already owns this identity")
            : await GovernedCreateLocalMuxdSessionAsync(
                new SessionLaunchRequest(
                    session.Id,
                    session.Aliases,
                    session.Tool,
                    "native",
                    $"tomux handoff ({name})",
                    "tomux.refused.claim",
                    "tomux.completed",
                    "tomux.failed",
                    session.DisplayTitle,
                    session.Workspace,
                    new Dictionary<string, string> { ["muxName"] = name }),
                name,
                command,
                session.Id,
                session.Aliases);
        if (!created.ok)
        {
            RecordSessionEvent(
                session,
                "tomux.refused.create",
                created.detail,
                "warn",
                details: new Dictionary<string, string> { ["muxName"] = name });
            return new CodexLocalRetrieval.Core.Models.AgentCommandResult(false, "multiplex start failed: " + created.detail);
        }

        _archive.SetTabKind(name, "remote-resumed", "#e879f9");   // default tint so you can tell it's a resumed-remote

        string metadataWarning = "";
        try
        {
            await _archive.SaveAsync();
            await PushProjectsAsync();
        }
        catch (Exception ex)
        {
            metadataWarning = " The mux handoff succeeded, but app metadata synchronization failed.";
            Diag.Log("Tomux metadata persistence FAILED after ownership handoff " + ex);
            RecordSessionEvent(
                session,
                "tomux.completed.metadata-failed",
                ex.Message,
                "error",
                details: new Dictionary<string, string>
                {
                    ["muxName"] = name,
                    ["pid"] = c.pid.ToString()
                });
        }
        RecordSessionEvent(
            session,
            "tomux.completed",
            transferred.AlreadyOwned
                ? "Tomux request was already satisfied by the existing mux owner."
                : "Tomux handoff completed: local owner stopped and mux became the owner.",
            details: new Dictionary<string, string>
            {
                ["muxName"] = name,
                ["pid"] = c.pid.ToString()
            });
        return new CodexLocalRetrieval.Core.Models.AgentCommandResult(true,
            transferred.AlreadyOwned
                ? $"Already running in multiplex as '{name}'; the existing mux owner was preserved.{metadataWarning}"
                : $"Handed off to multiplex as '{name}' (resumed-remote); local session stopped. Open the multiplex site to drive it.{metadataWarning}",
            ResolvedSessionId: session.Id);
    }

    // Web "Add to collection": file a multiplex session's chat into a (new or existing) collection. The web
    // only allows this while the app is live (the app OWNS collections, so this can't drift out of sync). We
    // resolve the mux session name back to its ArchiveSession, then reuse the tested AddToCollectionAsync and
    // re-push the projection so the web reflects it.
    private async Task<(bool ok, string detail)> AddSessionToCollectionAsync(string muxName, string collection, string collectionId = "", string deckId = "", string deckName = "", string tool = "", string sessionId = "")
    {
        muxName = (muxName ?? "").Trim(); collection = (collection ?? "").Trim(); collectionId = (collectionId ?? "").Trim();
        deckId = (deckId ?? "").Trim(); deckName = (deckName ?? "").Trim(); sessionId = (sessionId ?? "").Trim();
        if (string.IsNullOrEmpty(muxName) || (string.IsNullOrEmpty(collection) && string.IsNullOrEmpty(collectionId))) return (false, "missing session or collection");
        ArchiveSession? session = null;
        // Prefer the resolved REAL chat id (the mux-tab resolver linked this shell tab to its live agent chat)
        // so a shell-launched tab files its actual, relaunchable chat rather than a name-only placeholder.
        if (!string.IsNullOrEmpty(sessionId))
            session = _archive.Store.Sessions.Values.FirstOrDefault(s =>
                string.Equals(s.Id, sessionId, StringComparison.OrdinalIgnoreCase) ||
                s.Aliases.Any(a => string.Equals(a, sessionId, StringComparison.OrdinalIgnoreCase)));
        if (session is null)
            foreach (var s in _archive.Store.Sessions.Values)
                if (string.Equals(ArchiveService.MultiplexSessionName(s), muxName, StringComparison.OrdinalIgnoreCase)) { session = s; break; }
        // Still nothing (an un-linkable shell) → a named placeholder so "add ALL tabs" still captures it.
        if (session is null) session = _archive.EnsureMuxTabPlaceholder(muxName, tool);
        try
        {
            if (!string.IsNullOrEmpty(collectionId) && _archive.Store.Collections.ContainsKey(collectionId))
            {
                await _archive.AddToCollectionByIdAsync(session, collectionId);
                var existing = _archive.Store.Collections[collectionId];
                return (true, "added to " + existing.Name);
            }
            var resolvedDeck = _archive.ResolveDeckId(deckId);
            if (string.IsNullOrWhiteSpace(deckId) && !string.IsNullOrWhiteSpace(deckName))
            {
                var existingDeck = _archive.Decks.FirstOrDefault(d => string.Equals(d.Name, deckName, StringComparison.OrdinalIgnoreCase));
                resolvedDeck = existingDeck?.Id ?? (await _archive.CreateDeckAsync(deckName)).Id;
            }
            else if (!string.IsNullOrWhiteSpace(deckId) && !_archive.Decks.Any(d => string.Equals(d.Id, deckId, StringComparison.OrdinalIgnoreCase)))
            {
                return (false, "unknown deck: " + deckId);
            }
            await _archive.AddToCollectionAsync(session, collection, resolvedDeck);
            var deckLabel = _archive.Decks.FirstOrDefault(d => string.Equals(d.Id, resolvedDeck, StringComparison.OrdinalIgnoreCase))?.Name ?? "Main";
            return (true, "added to " + collection + " on " + deckLabel);
        }
        catch (Exception ex) { return (false, "add failed: " + ex.Message); }
    }

    // SSH hardening so a single stalled connection can NEVER wedge the bridge again (the bug that left
    // _cmdPolling stuck true and the kill/transcript queue undrained for hours):
    //  - BatchMode=yes      never block on a prompt (host-key / password) — key-only, fail fast.
    //  - ConnectTimeout     cap the TCP/handshake wait.
    //  - ServerAlive*       drop a connection that goes silent mid-session (~45s).
    // Plus a HARD wall-clock timeout in C# that tree-kills the process if it overruns, so WaitForExit can
    // never hang forever (and no orphan ssh.exe piles up). `-n` (stdin from /dev/null) is added for calls
    // with no stdin — without it, `ssh host "cmd"` waits forever for EOF on the GUI app's never-closing
    // inherited stdin, which is exactly what hung the poll while the stdin-piped push kept working.
    private const string SshHardenOpts = "-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=3";
    private static readonly TimeSpan SshHardTimeout = TimeSpan.FromSeconds(30);

    // Run ssh with the hardened options + a hard timeout. Returns (exitCode, stdout); -2 = timed out (tree-killed).
    private static async Task<(int code, string outText)> RunSshAsync(string target, string remoteCmd, string? stdin = null)
    {
        try
        {
            var stdinFlag = stdin is null ? "-n " : "";   // -n only when we're NOT feeding stdin
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
                maxStderrChars: 64 * 1024);
            if (result.TimedOut)
            {
                Diag.Log($"ssh timed out ({SshHardTimeout.TotalSeconds:0}s), tree-killed: {remoteCmd}");
                return (-2, "");
            }
            if (result.StdoutTruncated)
            {
                Diag.Log($"ssh output exceeded the 4 MiB capture limit: {remoteCmd}");
                return (-3, "");
            }
            return (result.ExitCode, result.Stdout);
        }
        catch (Exception ex) { Diag.Log("RunSsh failed: " + ex.Message); return (-1, ""); }
    }

    // Run a remote command over our owner-only SSH and capture stdout.
    private static async Task<string> SshCaptureAsync(string target, string remoteCmd)
        => (await RunSshAsync(target, remoteCmd)).outText;

    // Run a remote command and feed JSON on stdin (so quotes/backslashes never touch a shell command line).
    private static async Task SshSendAsync(string target, string remoteCmd, string stdin)
        => await RunSshAsync(target, remoteCmd, stdin);

    private const string MuxdTaskName = "MuxdSessionHost";

    private static async Task<string> LocalMuxdRequestAsync(object message, bool retryStart = true)
    {
        try { return await LocalMuxdRequestOnceAsync(message); }
        catch (Exception ex) when (retryStart)
        {
            Diag.Log("muxd local request failed; checking scheduled task: " + ex.Message);
            var task = await EnsureMuxdScheduledTaskRunningAsync();
            if (!task.ok) throw new InvalidOperationException("muxd unavailable; " + task.detail, ex);
            await Task.Delay(1200);
            return await LocalMuxdRequestOnceAsync(message);
        }
    }

    private static async Task<string> LocalMuxdRequestOnceAsync(object message)
    {
        using var ws = new ClientWebSocket();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
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

    private static async Task<(bool ok, string detail)> EnsureMuxdScheduledTaskRunningAsync()
    {
        var query = await RunProcessCaptureAsync("schtasks", "/Query", "/TN", MuxdTaskName, "/FO", "CSV", "/NH");
        if (query.code != 0)
            return (false, "scheduled task query failed: " + (query.errText + " " + query.outText).Trim());
        await EnsureMuxdScheduledTaskHiddenAsync();
        if (query.outText.Contains("Running", StringComparison.OrdinalIgnoreCase))
            return (true, "scheduled task already running");

        var run = await RunProcessCaptureAsync("schtasks", "/Run", "/TN", MuxdTaskName);
        if (run.code == 0) return (true, "scheduled task started");
        return (false, "scheduled task start failed: " + (run.errText + " " + run.outText).Trim());
    }

    private static async Task EnsureMuxdScheduledTaskHiddenAsync()
    {
        try
        {
            var xml = await RunProcessCaptureAsync("schtasks", "/Query", "/TN", MuxdTaskName, "/XML");
            if (xml.code != 0) return;
            if (!MuxdTaskLaunch.TryGetHiddenPythonAction(xml.outText, File.Exists, out var exe, out var args)) return;
            var taskRun = QuoteTaskArg(exe) + (string.IsNullOrWhiteSpace(args) ? "" : " " + args);
            var change = await RunProcessCaptureAsync("schtasks", "/Change", "/TN", MuxdTaskName, "/TR", taskRun);
            if (change.code != 0)
                Diag.Log("Muxd task hidden-launch update failed: " + (change.errText + " " + change.outText).Trim());
        }
        catch (Exception ex) { Diag.Log("Muxd task hidden-launch check failed: " + ex.Message); }
    }

    private static string QuoteTaskArg(string value)
        => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

    private static async Task<(int code, string outText, string errText)> RunProcessCaptureAsync(string file, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            var result = await ContainedProcessRunner.RunAsync(
                psi,
                TimeSpan.FromSeconds(6),
                maxStdoutChars: 1024 * 1024,
                maxStderrChars: 64 * 1024);
            if (result.TimedOut)
                return (-2, "", "process timed out");
            if (result.StdoutTruncated || result.StderrTruncated)
                return (-3, "", "process output exceeded the capture limit");
            return (result.ExitCode, result.Stdout, result.Stderr);
        }
        catch (Exception ex) { return (-1, "", ex.Message); }
    }

    private static async Task<bool> LocalMuxdSessionAliveAsync(string name)
    {
        try
        {
            var text = await LocalMuxdRequestAsync(new { t = "ls" });
            using var doc = JsonDocument.Parse(text);
            foreach (var s in doc.RootElement.GetProperty("list").EnumerateArray())
                if (string.Equals(s.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    return s.TryGetProperty("alive", out var alive) && alive.ValueKind == JsonValueKind.True;
        }
        catch { }
        return false;
    }

    // H1: every writer-creation path goes through the governor, not just the terminal resume. The claim's own
    // comment ("closes the check-then-spawn race across the GUI, headless server, and remote bridge") was
    // aspirational while the GUI's own mux path never touched it — ConfirmRunOrKillAsync is a check, not a
    // reservation, so two mux creates could still race each other into the same transcript.
    private async Task<(bool ok, string detail)> GovernedCreateLocalMuxdSessionAsync(
        SessionLaunchRequest request,
        string name,
        string command,
        string? sessionId = null,
        IEnumerable<string>? aliases = null,
        string? intentId = null,
        bool relaunch = false)
    {
        if (!_launchGovernor.TryAcquire(request, out var lease, out var claimDetail))
            return (false, claimDetail);
        using (lease)
        {
            var created = await CreateLocalMuxdSessionAsync(name, command, sessionId, aliases, intentId, relaunch);
            if (created.ok) lease?.MarkStarted("Started mux-hosted session writer.");
            else lease?.MarkFailed(created.detail);
            return created;
        }
    }

    // [F#5]. An app-launched session retains its claim for ~2 minutes, so once the handoff paths above are
    // governed, a /tomux or a kill-and-takeover inside that window would be refused by the PREVIOUS launch's own
    // leftover reservation — turning a working flow into a two-minute wait. A kill we verified by identity is
    // exactly the tier-2 evidence the reclaim tiers accept, so the tied claim is cleared through that same
    // mechanism. Nothing here force-clears a claim no verified kill is tied to.
    private static void ClearClaimsAfterVerifiedKill(
        ArchiveSession? session,
        string? sessionId,
        IEnumerable<string>? aliases,
        IEnumerable<int> verifiedExitedPids,
        string muxName)
    {
        try
        {
            var results = SessionReclaim.ClearClaimsTiedToVerifiedKills(sessionId, aliases, verifiedExitedPids);
            foreach (var result in results)
            {
                if (result.Outcome == ReclaimClearOutcome.Cleared)
                    RecordSessionEvent(
                        session,
                        "reclaim.claim.cleared",
                        "Cleared the retained launch reservation tied to a verified kill: " + result.Detail,
                        details: new Dictionary<string, string> { ["muxName"] = muxName });
                else if (result.Outcome == ReclaimClearOutcome.LaunchInFlight)
                    RecordSessionEvent(
                        session,
                        "reclaim.launch-in-flight",
                        result.Detail,
                        "warn",
                        details: new Dictionary<string, string> { ["muxName"] = muxName });
            }
        }
        catch (Exception ex) { Diag.Log("Tied-claim clear after verified kill failed: " + ex.Message); }
    }

    // Reclaim's stale-mux-custody step. A Current pointer is pruned ONLY when muxd itself confirms the tab is
    // gone; if muxd can't be asked, nothing is pruned — "we couldn't reach muxd" is not "the tab is absent".
    private IReadOnlyList<string> PruneMuxCustodyForReclaim(IReadOnlyList<string> candidateIds)
    {
        IReadOnlyCollection<string>? live = null;
        try
        {
            var text = LocalMuxdRequestAsync(new { t = "ls" }).GetAwaiter().GetResult();
            using var doc = JsonDocument.Parse(text);
            var names = new List<string>();
            foreach (var row in doc.RootElement.GetProperty("list").EnumerateArray())
            {
                var rowName = row.TryGetProperty("name", out var n) ? n.GetString() : null;
                if (!string.IsNullOrWhiteSpace(rowName)) names.Add(rowName!);
            }
            live = names;
        }
        catch { live = null; }

        var pruned = SessionReclaim.PruneStaleMuxCurrent(_archive.Store.MuxTabHistory, candidateIds, live);
        if (pruned.Count > 0)
        {
            try { _archive.SaveAsync().GetAwaiter().GetResult(); }
            catch (Exception ex) { Diag.Log("Mux custody prune could not be persisted: " + ex.Message); }
        }
        return pruned;
    }

    private async Task<(bool ok, string detail)> CreateLocalMuxdSessionAsync(
        string name,
        string command,
        string? sessionId = null,
        IEnumerable<string>? aliases = null,
        string? intentId = null,
        bool relaunch = false)
    {
        try
        {
            var cap = await EnsureLocalMuxdCapabilityAsync("create");
            if (!cap.ok) return cap;
            var canonicalId = (sessionId ?? "").Trim();
            if (string.IsNullOrWhiteSpace(canonicalId))
                canonicalId = ArchiveService.ParseResumedSessionId(command) ?? "";
            var identityAliases = (aliases ?? Array.Empty<string>())
                .Where(a => !string.IsNullOrWhiteSpace(a)
                            && !string.Equals(a, canonicalId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var text = await LocalMuxdRequestAsync(new
            {
                t = "create",
                s = name,
                cmd = command,
                cols = 140,
                rows = 40,
                sessionId = canonicalId,
                aliases = identityAliases,
                identityPending = string.IsNullOrWhiteSpace(canonicalId),
                relaunch,
                intentId = string.IsNullOrWhiteSpace(intentId)
                    ? RemoteCommandProtocol.NewIntent("local-create")
                    : intentId
            });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "err")
            {
                var detail = doc.RootElement.TryGetProperty("m", out var m) ? m.GetString() ?? "muxd error" : "muxd error";
                return (false, detail);
            }
            if (doc.RootElement.TryGetProperty("t", out t) && t.GetString() == "created")
            {
                RecordMuxSessionOwner(canonicalId, identityAliases, name);
                return (true, text);
            }
            return (false, "unexpected muxd create response: " + Trim(text, 160));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<(bool ok, string detail)> EnsureLocalMuxdCapabilityAsync(string cap)
    {
        try
        {
            var text = await LocalMuxdRequestAsync(new { t = "info" });
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("t", out var t) && t.GetString() == "err")
            {
                var msg = root.TryGetProperty("m", out var m) ? m.GetString() : null;
                return (false, "running muxd does not advertise protocol info"
                    + (string.IsNullOrWhiteSpace(msg) ? "" : " (" + msg + ")")
                    + "; restart MuxdSessionHost to load the current muxd");
            }
            if (!root.TryGetProperty("t", out t) || t.GetString() != "info")
                return (false, "running muxd returned an unexpected protocol response; restart MuxdSessionHost to load the current muxd");
            if (root.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in caps.EnumerateArray())
                    if (string.Equals(c.GetString(), cap, StringComparison.OrdinalIgnoreCase))
                        return (true, "ok");
            }
            var protocol = root.TryGetProperty("protocol", out var p) ? p.ToString() : "?";
            return (false, $"running muxd protocol {protocol} does not advertise '{cap}'; restart MuxdSessionHost to load the current muxd");
        }
        catch (JsonException ex) { return (false, "muxd protocol response was not JSON: " + ex.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<(bool ok, string detail)> DeleteLocalMuxdSessionAsync(string name)
    {
        try
        {
            var cap = await EnsureLocalMuxdCapabilityAsync("kill");
            if (!cap.ok) return cap;
            var text = await LocalMuxdRequestAsync(new { t = "kill", s = name });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "killed")
                return (true, "killed");
            if (doc.RootElement.TryGetProperty("t", out t) && t.GetString() == "err")
                return (false, doc.RootElement.TryGetProperty("m", out var m) ? m.GetString() ?? "muxd error" : "muxd error");
            return (false, "unexpected muxd kill response: " + Trim(text, 160));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

}
