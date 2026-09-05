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
var redactReads = Environment.GetEnvironmentVariable("CLR_REMOTE_REDACT_READS") == "1";
var allowLaunch = Environment.GetEnvironmentVariable("CLR_REMOTE_ALLOW_LAUNCH") == "1";

// ---- archive: load the store and index the live session folders so remote browsing is current ----
// CLR_REMOTE_STORE: point at a specific app-store.json (e.g. a synced copy). CLR_REMOTE_BUNDLED=1
// uses the repo's sanitized demo store (handy for a smoke test without touching real chats).
var storePath = Environment.GetEnvironmentVariable("CLR_REMOTE_STORE");
var useBundled = Environment.GetEnvironmentVariable("CLR_REMOTE_BUNDLED") == "1";
var archive = new ArchiveService(storePath: string.IsNullOrWhiteSpace(storePath) ? null : storePath, useBundledStore: useBundled);
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
app.MapGet("/healthz", () => Results.Ok(new { ok = true, service = "codex-local-retrieval", archiveLoaded = archiveRuntime.IsLoaded, chats = archiveRuntime.SessionCount }));
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
app.MapDiscovery(archiveRuntime);

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
            bridgeSettings = new RemoteBridge.Settings(st.MultiplexSshTarget.Trim(), st.MultiplexApiPort);
    }
    catch (Exception ex) { Console.Error.WriteLine("bridge settings read failed: " + ex.Message); }

    if (bridgeSettings is not null)
    {
        var codexDbPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "state_5.sqlite");
        bool DesktopAppRunning()
        {
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
            childProcessJob);
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
