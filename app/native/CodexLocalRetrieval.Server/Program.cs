using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

// ---- config (env only; nothing secret is ever read from disk or args) ----
var token = Environment.GetEnvironmentVariable("CLR_REMOTE_TOKEN")?.Trim();

// hl-auth (SSO) mode: gate by the harmonizerlabs.cc account system instead of the bearer token.
var hlAuthOn = Environment.GetEnvironmentVariable("CLR_REMOTE_HLAUTH") == "1";
var hlBase = (Environment.GetEnvironmentVariable("CLR_REMOTE_HLAUTH_BASE") ?? "").TrimEnd('/');
var hlPage = Environment.GetEnvironmentVariable("CLR_REMOTE_HLAUTH_PAGE");          // page id to require (empty = any signed-in account)
var hlCookie = Environment.GetEnvironmentVariable("CLR_REMOTE_HLAUTH_COOKIE") ?? "hl_session";
var hlReturn = Environment.GetEnvironmentVariable("CLR_REMOTE_PUBLIC_PATH") ?? "/";  // where /auth/login sends you back

if (hlAuthOn)
{
    if (string.IsNullOrWhiteSpace(hlBase))
    {
        Console.Error.WriteLine("refusing to start: CLR_REMOTE_HLAUTH=1 requires CLR_REMOTE_HLAUTH_BASE (e.g. https://harmonizerlabs.cc).");
        return 1;
    }
}
else if (!RemoteAuth.IsValidConfiguredToken(token))
{
    Console.Error.WriteLine($"refusing to start: set CLR_REMOTE_TOKEN to a secret of at least {RemoteAuth.MinTokenLength} chars (or enable CLR_REMOTE_HLAUTH).");
    return 1;
}

var port = int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_PORT"), out var p) ? p : 8765;
var bind = Environment.GetEnvironmentVariable("CLR_REMOTE_BIND") ?? "127.0.0.1"; // localhost only; nginx is the edge
var storePath = Environment.GetEnvironmentVariable("CLR_REMOTE_STORE");
var useBundled = Environment.GetEnvironmentVariable("CLR_REMOTE_BUNDLED") == "1";
var isolatedProfile = Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_PROFILE") == "1";
if (isolatedProfile)
{
    var fixtureError = LoopbackRelayTransport.ValidateFixtureProfile(storePath, Environment.GetEnvironmentVariable("CLR_CLAUDE_PROJECTS"), bind, useBundled);
    if (fixtureError.Length != 0)
    {
        Console.Error.WriteLine("refusing isolated remote test profile: " + fixtureError);
        return 2;
    }
}
var instance = ServerSingleInstanceGuard.Identify(bind, port, storePath, useBundled);
var instanceResult = ServerSingleInstanceGuard.TryAcquire(bind, port, storePath, useBundled);
if (!instanceResult.Acquired)
{
    Console.Error.WriteLine(instanceResult.Message);
    return instanceResult.ExistingHealthy ? 0 : 2;
}
using var instanceLease = instanceResult.Lease;
var redactReads = Environment.GetEnvironmentVariable("CLR_REMOTE_REDACT_READS") == "1";
var allowLaunch = Environment.GetEnvironmentVariable("CLR_REMOTE_ALLOW_LAUNCH") == "1";

// ---- archive: load the store and index the live session folders so remote browsing is current ----
// CLR_REMOTE_STORE: point at a specific app-store.json (e.g. a synced copy). CLR_REMOTE_BUNDLED=1
// uses the repo's sanitized demo store (handy for a smoke test without touching real chats).
var isolatedArchiveRoot = isolatedProfile ? Path.GetDirectoryName(storePath!)! : null;
var archive = new ArchiveService(
    storePath: string.IsNullOrWhiteSpace(storePath) ? null : storePath,
    useBundledStore: useBundled,
    codexSessionsRoot: isolatedProfile ? Path.Combine(isolatedArchiveRoot!, "codex-sources") : null,
    claudeSessionsRoot: isolatedProfile ? Environment.GetEnvironmentVariable("CLR_CLAUDE_PROJECTS") : null,
    codexStateDbPath: isolatedProfile ? Path.Combine(isolatedArchiveRoot!, "codex-state.sqlite") : null,
    transcriptSearchIndexPath: isolatedProfile ? Path.Combine(isolatedArchiveRoot!, "transcript-search.sqlite") : null,
    enableTranscriptSearchIndex: isolatedProfile ? true : null,
    sourceOverride: isolatedProfile ? new[]
    {
        new SessionSource { Tool = "codex", Root = Path.Combine(isolatedArchiveRoot!, "codex-sources") },
        new SessionSource { Tool = "claude", Root = Environment.GetEnvironmentVariable("CLR_CLAUDE_PROJECTS")! }
    } : null);
// LAZY: the archive (store + disk index) is the heavy part (~150-200MB), but only the Chats tab and the
// co-pilot use it — the Agent tab lists live sessions straight from the app-server / Claude store. So we
// DON'T load it at startup; the first archive-backed request triggers a one-time load (+ disk sync). An
// idle server (Agent-only use, or just sitting there) stays light until you actually browse your archive.
var syncOnLoad = Environment.GetEnvironmentVariable("CLR_REMOTE_SYNC") != "0";
var archiveRuntime = new ArchiveRuntime(archive, syncOnLoad, Console.Error.WriteLine);

IChatBackend? BackendFactory()
{
    var provider = archive.ActiveAiProvider()
                   ?? archive.EnsureAiProvider("DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash");
    var key = Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")
              ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
    return string.IsNullOrWhiteSpace(key) ? null : new DeepSeekBackend(provider.BaseUrl, provider.Model, key);
}

var api = new RemoteApi(archive, BackendFactory, redactReads, allowLaunch);

// Resolve wwwroot robustly: next to the binary when published, else the project source when running
// from bin during development. Content root = binary dir so this works regardless of launch cwd.
var contentRoot = AppContext.BaseDirectory;
var webRoot = Path.Combine(contentRoot, "wwwroot");
if (!Directory.Exists(webRoot))
{
    var srcWwwroot = Path.GetFullPath(Path.Combine(contentRoot, "..", "..", "..", "wwwroot"));
    if (Directory.Exists(srcWwwroot)) webRoot = srcWwwroot;
}
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot, WebRootPath = webRoot });
builder.WebHost.UseUrls($"http://{bind}:{port}");
builder.Logging.AddSimpleConsole(o => o.SingleLine = true);
var app = builder.Build();

app.UseWebSockets(); // live agent sessions stream over /api/agent

if (hlAuthOn)
{
    // hl-auth SSO gate: every request (except /healthz) must carry an hl_session cookie that the
    // hl-auth account system says can open this page. Not signed in -> bounce to the hl-auth login
    // page; signed in but not allowed -> 403. Decision cached ~30s per cookie. Fails closed.
    var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    var gate = new HlAuthGate(async (cookieVal, ct) =>
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{hlBase}/auth/api/access");
        req.Headers.TryAddWithoutValidation("Cookie", $"{hlCookie}={cookieVal}");
        using var resp = await http.SendAsync(req, ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
    }, hlPage);
    var cache = new HlAuthOutcomeCache(capacity: 1024, ttl: TimeSpan.FromSeconds(30));

    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/healthz")) { await next(); return; }
        var cookie = ctx.Request.Cookies[hlCookie];
        GateOutcome outcome;
        if (string.IsNullOrEmpty(cookie)) outcome = GateOutcome.Login;
        else if (cache.TryGet(cookie, out var hit)) outcome = hit;
        else { outcome = await gate.CheckAsync(cookie, ctx.RequestAborted); cache.Set(cookie, outcome); }

        if (outcome == GateOutcome.Allow) { await next(); return; }
        if (outcome == GateOutcome.Login)
        {
            ctx.Response.Redirect($"{hlBase}/auth/login?next={Uri.EscapeDataString(hlReturn)}");
            return;
        }
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsync("Forbidden — your account isn't allowed to open this page.");
    });
}
else
{
    // Bearer-token gate on everything under /api. The static SPA shell (/, /index.html) is open — it
    // carries no data and asks for the token itself, then sends it as a Bearer header on API calls.
    app.Use(async (ctx, next) =>
    {
        if (ctx.Request.Path.StartsWithSegments("/api"))
        {
            // Browsers can't set an Authorization header on a WebSocket, so the WS upgrade may carry
            // the token as ?token= instead. (hl-auth mode uses the cookie and never hits this branch.)
            var presented = RemoteAuth.Extract(ctx.Request.Headers.Authorization, ctx.Request.Headers["X-Auth-Token"])
                            ?? (ctx.WebSockets.IsWebSocketRequest ? ctx.Request.Query["token"].ToString() : null);
            if (!RemoteAuth.Matches(presented, token))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new { error = "unauthorized" });
                return;
            }
        }
        await next();
    });
}

async Task<(bool ok, ArchiveService.RemoteMuxLaunch? launch, string detail)> ResolveRemoteMuxLaunchAsync(
    string? sessionId,
    string? tool,
    string? launchMode)
{
    return await archiveRuntime.UseAsync((loadedArchive, _) =>
    {
        var result = loadedArchive.TryBuildRemoteMuxLaunch(
                sessionId,
                tool,
                out var launch,
                out var detail,
                multiplexCommandFactory: isolatedProfile ? session =>
                    System.Text.RegularExpressions.Regex.IsMatch(session.Id, "\\A[A-Za-z0-9_-]{1,200}\\z")
                        ? (session.Tool == "claude"
                            ? (launchMode == ArchiveService.GatewayLaunchMode ? "# cc --resume " : "# claude --resume ")
                            : "# codex resume ") + session.Id : "" : null,
                launchMode: launchMode)
            ? (true, launch, detail)
            : (false, null, detail);
        return Task.FromResult(result);
    });
}

async Task<IReadOnlyList<ArchiveService.PendingMuxBinding>> ResolvePendingMuxBindingsAsync()
{
    return await archiveRuntime.UseAsync(
        (loadedArchive, _) => Task.FromResult(loadedArchive.ResolvePendingMuxBindings()));
}

app.UseDefaultFiles();
app.UseStaticFiles();

// healthz stays light — it must NOT trigger the archive load (it's a liveness probe).
app.MapGet("/healthz", () => Results.Ok(new { ok = true, service = "codex-local-retrieval", instance = instance.Value, archiveLoaded = archiveRuntime.IsLoaded, chats = archiveRuntime.SessionCount }));
app.MapGet("/api/stats", async (CancellationToken ct) =>
    Results.Json(await archiveRuntime.UseAsync((_, _) => Task.FromResult(api.Stats()), ct)));
app.MapGet("/api/custody", async (CancellationToken ct) =>
    Results.Json(await archiveRuntime.UseAsync((_, innerCt) => Task.Run(() => api.Custody(), innerCt), ct)));
app.MapGet("/api/chats", async (string? q, int? limit, CancellationToken ct) =>
    Results.Json(await archiveRuntime.UseAsync((_, _) => Task.FromResult(api.Search(q, limit ?? 20)), ct)));
app.MapGet("/api/chats/{id}", async (string id, int? page, int? pageSize, CancellationToken ct) =>
{
    var result = await archiveRuntime.UseAsync((_, _) => Task.FromResult(api.Read(id, page ?? 0, pageSize ?? 20)), ct);
    return result is not null ? Results.Json(result) : Results.NotFound(new { error = "no chat with that id" });
});
app.MapGet("/api/chats/{id}/events", async (string id, int? limit, CancellationToken ct) =>
{
    var result = await archiveRuntime.UseAsync((_, _) => Task.FromResult(api.Events(id, limit ?? 400)), ct);
    return result is not null ? Results.Json(result) : Results.NotFound(new { error = "no chat with that id" });
});
app.MapPost("/api/copilot", async (CopilotRequest req, CancellationToken ct) =>
    Results.Json(await archiveRuntime.UseAsync((_, innerCt) => api.CopilotAsync(req.Message, req.History, innerCt), ct)));
app.MapPost("/api/chats/{id}/resume", async (string id, ResumeRequest? req, CancellationToken ct) =>
    Results.Json(await archiveRuntime.UseAsync((_, _) => Task.FromResult(api.ResumeCommand(id, req?.Launch ?? false)), ct)));
app.MapPost("/api/chats/{id}/favorite", async (string id, FavoriteRequest? req, CancellationToken ct) =>
    Results.Json(await archiveRuntime.UseAsync((_, _) => api.FavoriteAsync(id, req?.Favorite ?? true), ct)));

// Live agent: our server is a client of `codex app-server` (the desktop-app protocol). The hub owns
// the one app-server and multiplexes it. /api/agent/sessions lists ALL sessions; the WS opens/drives one.
var codexExe = Environment.GetEnvironmentVariable("CLR_CODEX_EXE") ?? ArchiveService.ResolveCodexExe();
var defaultWs = Environment.GetEnvironmentVariable("CLR_AGENT_DEFAULT_WS") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
using var childProcessJob = WindowsProcessJob.CreateKillOnClose();
var agentHub = new CodexAgentHub(codexExe, processContainment: childProcessJob);
var claudeStore = new ClaudeSessionStore(Environment.GetEnvironmentVariable("CLR_CLAUDE_PROJECTS"));
var claudeDriver = new ClaudeLiveDriver(
    Environment.GetEnvironmentVariable("CLR_CLAUDE_EXE"),
    processContainment: childProcessJob);
// Owner-signing for "auto" (no-approval) turns: an owner-held key authorizes each auto command (HMAC,
// fresh, non-replayed). Auto-generated + saved on the PC if unset; the owner reads it off the machine
// (out-of-band) and enters it in the browser. Never transmitted.
var signingKeyFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexArchiveRemote", "signing.key");
var commandSigner = CommandSigner.LoadOrCreate(Environment.GetEnvironmentVariable("CLR_REMOTE_SIGNING_KEY"), signingKeyFile, m => Console.WriteLine("[signing] " + m));

// One merged, time-sorted list of BOTH tools' sessions: source "codex" (drivable) or "claude" (history).
app.MapGet("/api/agent/sessions", async (string? cursor, int? pageSize, CancellationToken ct) =>
{
    var codex = (await agentHub.ListSessionsAsync(cursor, pageSize ?? 200, ct))
        .Select(c => new AgentSessionDto(c.Id, string.IsNullOrEmpty(c.Name) ? c.Preview : c.Name, c.Preview, c.Cwd, "codex", c.UpdatedAt));
    var claude = claudeStore.List(250)
        .Select(c => new AgentSessionDto(c.Id, c.Title, "", c.Cwd, "claude", c.UpdatedAt));
    return Results.Json(codex.Concat(claude).OrderByDescending(x => x.UpdatedAt).ToList());
});

// Whether owner-signed "auto" mode is available (a signing key is configured on this server).
app.MapGet("/api/agent/config", () => Results.Json(new { autoAvailable = commandSigner.Enabled }));

// Rename a session. For Claude this appends a custom-title to its rollout (same mechanism + shared with
// the Claude Code sidebar). Codex titles itself; renaming codex sessions isn't supported here.
app.MapPost("/api/agent/sessions/{id}/rename", (string id, RenameRequest req) =>
{
    var title = (req?.Title ?? "").Trim();
    if (title.Length == 0) return Results.BadRequest(new { error = "title required" });
    if (title.Length > 120) title = title[..120];
    if (req?.Source == "codex") return Results.BadRequest(new { error = "Codex titles its own sessions; rename is available for Claude sessions." });
    return claudeStore.RenameSession(id, title)
        ? Results.Json(new { ok = true, title })
        : Results.NotFound(new { error = "session not found" });
});

// Reopen a chat where you work: in VS Code (the same session) or a fresh terminal. Launches on this PC.
var claudeExeForLaunch = Environment.GetEnvironmentVariable("CLR_CLAUDE_EXE")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
var launcher = new SessionLauncher(claudeExeForLaunch, codexExe, allowLaunch);
var canonicalSessionResolver = new CanonicalSessionResolver(async ct =>
{
    return await archiveRuntime.UseAsync(
        (loadedArchive, _) => Task.FromResult<IReadOnlyList<ArchiveSession>>(
            loadedArchive.Store.Sessions.Values
                .Select(s => new ArchiveSession
                {
                    Id = s.Id,
                    Tool = s.Tool,
                    LaunchMode = s.LaunchMode,
                    Workspace = s.Workspace,
                    Aliases = new(s.Aliases.ToArray()),
                })
                .ToArray()),
        ct,
        refreshBeforeUse: true);
});
var sessionOpenService = new SessionOpenService(canonicalSessionResolver, launcher);
app.MapPost("/api/agent/sessions/{id}/open", async (string id, OpenRequest? req, CancellationToken ct) =>
{
    var result = await sessionOpenService.OpenAsync(id, req, ct);
    return result.Ok
        ? Results.Json(new { ok = true, message = result.Message })
        : Results.BadRequest(new { error = result.Message });
});

// The web archive browser (relay/public/chats.html) reads its rows straight from here through the
// reverse tunnel — the PC filters, the browser only renders.
app.MapDiscovery(archiveRuntime, redactReads);

// ---- fleet: always-on recorder of what's running, so a crash/reboot leaves a restorable record ----
// The server is the natural writer (it's back ~a minute after boot); if the desktop app ever hosts a
// recorder too, FleetWriterLock elects exactly one and the loser serves reads. CLR_FLEET=0 disables.
var fleetInterval = int.TryParse(Environment.GetEnvironmentVariable("CLR_FLEET_INTERVAL_SEC"), out var fleetSec) && fleetSec > 0
    ? TimeSpan.FromSeconds(fleetSec) : (TimeSpan?)null;
var fleetService = new FleetSnapshotService("server", new FleetSnapshotService.Options(
    Interval: fleetInterval,
    OnTransition: t => SessionEventLedger.AppendBestEffortQueued(SessionEventLedger.Create(
        kind: "fleet-session-" + t.Kind,
        summary: $"{t.Tool} session {t.SessionId} {t.Kind} (fleet recorder)",
        sessionId: t.SessionId,
        tool: t.Tool,
        source: "fleet")),
    Log: m => Console.WriteLine("[fleet] " + m)));
if (Environment.GetEnvironmentVariable("CLR_FLEET") != "0")
    _ = Task.Run(() => fleetService.RunAsync(app.Lifetime.ApplicationStopping));
app.MapFleet(fleetService, sessionOpenService);

// "Open the full desktop app on the PC" — light headless server by default, heavy app on demand.
// Owner-gated by the global auth middleware; honours the same CLR_REMOTE_ALLOW_LAUNCH switch.
app.MapGet("/api/desktop-app", () => Results.Json(new { available = launcher.Enabled, running = System.Diagnostics.Process.GetProcessesByName("CodexLocalRetrieval.Native").Length > 0 }));
app.MapPost("/api/desktop-app/open", () =>
{
    var (ok, msg) = launcher.OpenDesktopApp();
    return ok ? Results.Json(new { ok = true, message = msg }) : Results.BadRequest(new { error = msg });
});

app.Map("/api/agent", async (HttpContext ctx) =>
{
    if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
    using var sock = await ctx.WebSockets.AcceptWebSocketAsync();
    await AgentWebSocket.HandleAsync(
        sock,
        agentHub,
        claudeStore,
        claudeDriver,
        commandSigner,
        defaultWs,
        ctx.RequestAborted,
        app.Lifetime.ApplicationStopping,
        canonicalSessionResolver.ResolveAsync);
});

// Idle eviction: keep loaded only while in use. After CLR_REMOTE_IDLE_UNLOAD_SEC (default 300s) with no
// archive-backed request, drop the in-memory store and force a compacting GC so the pages go back to the
// OS — the server falls back to its ~40MB idle weight. The next browse reloads it on demand. 0 = never.
var idleUnloadSec = int.TryParse(Environment.GetEnvironmentVariable("CLR_REMOTE_IDLE_UNLOAD_SEC"), out var iu) ? iu : 300;
if (idleUnloadSec > 0)
{
    _ = Task.Run(async () =>
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            try
            {
                if (await archiveRuntime.TryUnloadIfIdleAsync(TimeSpan.FromSeconds(idleUnloadSec)))
                {
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    GC.WaitForPendingFinalizers();
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    NativeMem.TrimWorkingSet();   // return the committed pages to the OS, not just the managed heap
                    Console.WriteLine($"archive unloaded after {idleUnloadSec}s idle — RAM released");
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("idle-unload warning: " + ex.Message); }
        }
    });
}

// ---- remote command bridge: keep the web's "running on PC" view + Open/Kill/transcript/rename working
// even when the heavy desktop app is CLOSED. The bridge pushes a light running-sessions heartbeat + drains
// the multiplex command queue over the SAME owner-only SSH the desktop app uses — but ONLY while the
// desktop app is NOT running (the app is the primary bridge when open, and pushes the full enriched
// projection, so the two never double-process). SSH target/port come from the app's settings, read once
// without loading the heavy archive. Disable with CLR_REMOTE_BRIDGE=0.
if (Environment.GetEnvironmentVariable("CLR_REMOTE_BRIDGE") != "0")
{
    RemoteBridge.Settings? bridgeSettings = null;
    try
    {
        var st = archive.ReadSettingsOnly();
        if (!string.IsNullOrWhiteSpace(st.MultiplexSshTarget))
        {
            if (isolatedProfile)
            {
                var portError = LoopbackRelayTransport.ValidateFixturePort(st.MultiplexApiPort);
                if (portError.Length != 0)
                {
                    Console.Error.WriteLine("refusing isolated remote test profile: " + portError);
                    return 2;
                }
            }
            bridgeSettings = new RemoteBridge.Settings(st.MultiplexSshTarget.Trim(), st.MultiplexApiPort);
        }
    }
    catch (Exception ex) { Console.Error.WriteLine("bridge settings read failed: " + ex.Message); }

    if (bridgeSettings is not null)
    {
        var codexDbPath = isolatedProfile
            ? Path.Combine(Path.GetDirectoryName(storePath!)!, "fixture-codex-state.sqlite")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
        var emptyRunningSnapshot = isolatedProfile
            ? new Func<(bool verified, List<ArchiveService.RunningSessionInfo> sessions, string detail)>(() => (true, new List<ArchiveService.RunningSessionInfo>(), "fixture snapshot; production process absence not asserted"))
            : null;
        bool DesktopAppRunning()
        {
            // An explicitly isolated fixture profile owns its temporary store and loopback relay;
            // it must not be suppressed by the production desktop process on the same machine.
            if (isolatedProfile) return LoopbackRelayTransport.IsGuiFixtureRunning(storePath!);
            try { return System.Diagnostics.Process.GetProcessesByName("CodexLocalRetrieval.Native").Length > 0; }
            catch { return false; }
        }
        var bridge = new RemoteBridge(
            () => bridgeSettings,
            DesktopAppRunning,
            claudeStore,
            codexDbPath,
            m => Console.WriteLine("[bridge] " + m),
            ResolveRemoteMuxLaunchAsync,
            ResolvePendingMuxBindingsAsync,
            executeArchiveCommand: command => archiveRuntime.UseAsync(
                (loadedArchive, _) => ArchiveRemoteCommands.ExecuteAsync(loadedArchive, command)),
            processContainment: childProcessJob,
            transport: isolatedProfile
                ? LoopbackRelayTransport.Create()
                : null,
            isolationFixture: isolatedProfile,
            runningSnapshot: emptyRunningSnapshot,
            captureWorkspace: (command, listing) => archiveRuntime.UseAsync(
                (loadedArchive, _) => ArchiveRemoteCommands.CaptureWorkspaceAsync(loadedArchive, command, listing)),
            fixtureWorkspaceListing: isolatedProfile ? async () =>
            {
                var fixtureListing = Path.Combine(Path.GetDirectoryName(storePath!)!, "workspace-listing.json");
                return File.Exists(fixtureListing) ? await File.ReadAllTextAsync(fixtureListing) : "{\"list\":[]}";
            } : null,
            executeStartChat: (request, muxRequest) => StartChatCoordinator.ExecuteAsync(request,
                () => archiveRuntime.UseAsync((loadedArchive, _) => loadedArchive.PrepareStartChatAsync(request,
                    isolatedProfile && request.HandoffFromId.Length > 0
                        ? (tool, cwd, mode) => tool == "claude" && mode == ArchiveService.GatewayLaunchMode ? "# isolated Gateway handoff" : ""
                        : null)),
                (ok, detail, generation) => archiveRuntime.UseAsync((loadedArchive, _) => ok
                    ? loadedArchive.MarkStartChatAppliedAsync(request, detail, generation)
                    : loadedArchive.MarkStartChatFailedAsync(request, detail)),
                muxRequest),
            fixtureMuxRequest: isolatedProfile ? LoopbackRelayTransport.CreateMuxFixture() : null,
            fixtureUploadRoot: isolatedProfile ? Path.Combine(Path.GetDirectoryName(storePath!)!, "downloaded-uploads") : null,
            fixtureSshConfigPath: isolatedProfile ? Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_SSH_CONFIG") : null,
            fixtureDownload: isolatedProfile && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_SSH_CONFIG")) ? (id, filename, destination) => LoopbackRelayTransport.DownloadUploadAsync(
                bridgeSettings, Path.GetDirectoryName(storePath!)!, id, destination) : null,
            reconcileStartChatBindings: listing => archiveRuntime.UseAsync((loadedArchive, _) =>
                loadedArchive.ReconcileStartChatBindingsAsync(listing)),
            executeReclaim: (command, muxRequest) => archiveRuntime.UseAsync(async (loadedArchive, _) =>
            {
                var intent = command.GetProperty("intentId").GetString() ?? "";
                var sessionId = command.GetProperty("sessionId").GetString() ?? "";
                var tool = command.GetProperty("tool").GetString() ?? "";
                var revision = command.GetProperty("expectedRevision").GetString() ?? "";
                if (!command.TryGetProperty("confirmed", out var confirmed) || confirmed.ValueKind != System.Text.Json.JsonValueKind.True)
                    return new CodexLocalRetrieval.Core.Models.ReclaimOperationResult(
                        CodexLocalRetrieval.Core.Models.ReclaimOperationStatus.Refused, "explicit cleanup confirmation required");
                var fixtureRoot = isolatedProfile ? Path.GetDirectoryName(storePath!)! : null;
                return await loadedArchive.ExecuteReclaimOperationAsync(intent, sessionId, tool, revision,
                    () =>
                    {
                        if (isolatedProfile && Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_RECLAIM_PAUSE_BEFORE_PRUNE") == "1"
                            && loadedArchive.Store.ManagementOperations.TryGetValue(intent, out var receipt)
                            && receipt.State == "started")
                        {
                            var payload = System.Text.Json.JsonSerializer.Deserialize<CodexLocalRetrieval.Core.Models.ReclaimOperationReceiptPayload>(receipt.ResultId);
                            if (payload?.Report is not null) return Task.FromResult("fixture paused before prune");
                        }
                        return muxRequest(new { t = "ls" });
                    },
                    async (name, id, generation) =>
                    {
                        using var reply = System.Text.Json.JsonDocument.Parse(await muxRequest(new
                        {
                            t = "kill", s = name, sessionId = id, generationId = generation
                        }));
                        var root = reply.RootElement;
                        var killed = root.TryGetProperty("t", out var type) && type.GetString() == "killed"
                            && root.TryGetProperty("s", out var returnedName) && returnedName.GetString() == name;
                        return (killed, killed ? "fenced mux owner removed" : "mux teardown was not confirmed");
                    },
                    processKill: isolatedProfile ? _ => new RunningSessions.KillResult(true,
                        "fixture cleanup is restricted to generation-fenced mux teardown", Array.Empty<ReclaimKilledPid>()) : null,
                    claimOptions: isolatedProfile ? new SessionLaunchClaims.Options(RootDirectory: Path.Combine(fixtureRoot!, "claims")) : null,
                    recordOptions: isolatedProfile ? new SessionOwnerRecords.Options(RootDirectory: Path.Combine(fixtureRoot!, "owners")) : null,
                    cancellationToken: app.Lifetime.ApplicationStopping);
            }),
            fetchTranscript: async command =>
            {
                var id = command.GetProperty("sessionId").GetString() ?? "";
                var credential = command.GetProperty("bridgeToken").GetString();
                var ttl = command.GetProperty("ttlMs").GetInt32();
                var admission = TranscriptFetchProjection.AdmitFetch(id, credential, ttl);
                if (!admission.Allowed) return (false, admission.Reason);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var pages = await archiveRuntime.UseAsync(async (loaded, _) =>
                    {
                        var session = loaded.ResolveSessionByIdOrAlias(id);
                        if (session is null) return null;
                        var remaining = ttl - clock.ElapsedMilliseconds;
                        if (remaining <= 0) return null;
                        using var captureDeadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(remaining));
                        var messages = await loaded.ExtractReaderMessagesAsync(session, "all", captureDeadline.Token);
                        return TranscriptFetchProjection.BuildPages(id, messages, redactReads);
                    });
                    if (pages is null || pages.Count == 0) return (false, "no readable transcript for this chat");
                    foreach (var page in pages)
                    {
                        var remaining = ttl - clock.ElapsedMilliseconds;
                        if (remaining <= 0) return (false, "transcript fetch authorization expired");
                        if (!await LoopbackRelayTransport.PushTranscriptAsync(bridgeSettings, id, credential!, page.Json,
                            TimeSpan.FromMilliseconds(Math.Min(30000, remaining)), isolatedProfile))
                            return (false, "transcript page delivery was not confirmed");
                    }
                    return (true, $"{pages.Count} transcript pages delivered");
                }
                catch { return (false, "transcript fetch failed"); }
            });
        _ = bridge.RunLoopAsync(app.Lifetime.ApplicationStopping);
        Console.WriteLine($"MUX remote bridge armed (target {bridgeSettings.Target}:{bridgeSettings.Port}; active only while the desktop app is closed)");
    }
    else Console.WriteLine("MUX remote bridge OFF — no multiplex SSH target in settings.");
}

var authMode = hlAuthOn ? $"hl-auth ({hlBase}, page:{hlPage ?? "any"})" : "bearer token";
Console.WriteLine($"MUX remote server on http://{bind}:{port}  (archive: lazy (loads on first browse), auth: {authMode}, launch: {(allowLaunch ? "on" : "off")}, redact-reads: {(redactReads ? "on" : "off")})");
try
{
    app.Run();
}
finally
{
    archiveRuntime.Dispose();
    try
    {
        await agentHub.DisposeAsync();
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("codex agent hub shutdown failed: " + ex);
    }
}
return 0;
