using System.Collections.Concurrent;
using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

// Owns the single `codex app-server` process for the whole web server and multiplexes it: lists all
// sessions, resumes any, runs turns, and routes the app-server's notifications + approval-requests to
// whichever WebSocket opened that thread. One app-server backs many browser tabs/threads.
public sealed class CodexAgentHub : IAsyncDisposable
{
    private readonly string _exe;
    private readonly SessionLaunchGovernor _launchGovernor;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CodexAppServer? _srv;

    // threadId -> consumer that receives this thread's notifications / approval-requests.
    private readonly ConcurrentDictionary<string, Func<JsonElement, Task>> _noteRoutes = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement, Task>> _reqRoutes = new();
    private readonly ConcurrentDictionary<string, SessionLaunchLease> _activeTurnClaims = new(StringComparer.OrdinalIgnoreCase);

    public CodexAgentHub(
        string exe,
        Func<string, bool>? isSessionLive = null,
        SessionLaunchClaims.Options? claimOptions = null,
        SessionLaunchGovernor? launchGovernor = null)
    {
        _exe = exe;
        _launchGovernor = launchGovernor ?? new SessionLaunchGovernor(new SessionLaunchGovernorOptions(claimOptions, IsSessionLive: isSessionLive));
    }

    private async Task<CodexAppServer> EnsureAsync(CancellationToken ct)
    {
        if (_srv is { HasExited: false }) return _srv;
        ReleaseAllTurnClaims();
        await _initLock.WaitAsync(ct);
        try
        {
            if (_srv is { HasExited: false }) return _srv;
            var s = CodexAppServer.Start(_exe);
            await s.InitializeAsync(ct);
            _ = Task.Run(() => PumpNotifications(s));
            _ = Task.Run(() => PumpServerRequests(s));
            _srv = s;
            return s;
        }
        finally { _initLock.Release(); }
    }

    private async Task PumpNotifications(CodexAppServer s)
    {
        try
        {
            await foreach (var note in s.Notifications.ReadAllAsync())
            {
                RetireTurnClaimIfTerminal(note);
                if (ThreadIdOf(note) is { } tid && _noteRoutes.TryGetValue(tid, out var f))
                    try { await f(note); } catch { }
            }
        }
        finally { ReleaseAllTurnClaims(); }
    }

    private async Task PumpServerRequests(CodexAppServer s)
    {
        await foreach (var req in s.ServerRequests.ReadAllAsync())
        {
            // route approval requests to the owning thread; auto-deny if no owner is listening.
            if (ThreadIdOf(req) is { } tid && _reqRoutes.TryGetValue(tid, out var f))
                try { await f(req); continue; } catch { }
            if (req.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                await s.RespondAsync(idEl.GetInt32(), new { decision = "decline" });
        }
    }

    // Notifications/requests carry the thread id under params.threadId (or .thread_id / .conversationId).
    private static string? ThreadIdOf(JsonElement msg)
    {
        if (!msg.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object) return null;
        foreach (var key in new[] { "threadId", "thread_id", "conversationId", "conversation_id" })
            if (p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString();
        return null;
    }

    public async Task<List<SessionInfo>> ListSessionsAsync(string? cursor, int pageSize, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        var res = await s.ListThreadsAsync(pageSize, cursor, ct);
        var list = new List<SessionInfo>();
        if (res.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            foreach (var t in data.EnumerateArray()) list.Add(SessionInfo.From(t));
        return list;
    }

    // Resume a thread (load from disk) and register `onNote`/`onReq` to receive its events + approvals.
    public async Task<CodexAppServer> OpenThreadAsync(string threadId, string? cwd, Func<JsonElement, Task> onNote, Func<JsonElement, Task> onReq, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        _noteRoutes[threadId] = onNote;
        _reqRoutes[threadId] = onReq;
        await s.ResumeThreadAsync(threadId, cwd, ct);
        return s;
    }

    // Read a thread's full history as normalized events. thread/read loads items lazily (it won't pull
    // a 96 MB rollout), so we take the rollout `path` it reports and parse the .jsonl directly.
    public async Task<List<AgentEvent>> ReadHistoryAsync(string threadId, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        var read = await s.ReadThreadAsync(threadId, ct);
        var path = read.TryGetProperty("thread", out var thread) && thread.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String
            ? pe.GetString() : null;
        return string.IsNullOrEmpty(path) ? new List<AgentEvent>() : RolloutToEvents.Parse(path!);
    }

    // Start a brand-new session in a workspace; returns its threadId.
    public async Task<string> NewThreadAsync(string cwd, Func<JsonElement, Task> onNote, Func<JsonElement, Task> onReq, CancellationToken ct, string approvalPolicy = "on-request", string sandbox = "workspace-write")
    {
        var s = await EnsureAsync(ct);
        var freshRequest = new SessionLaunchRequest(
            null,
            null,
            "codex",
            "server",
            "codex app-server thread/start",
            "codex.thread.refused",
            "codex.thread.started",
            "codex.thread.failed",
            Workspace: cwd,
            Details: new Dictionary<string, string>
            {
                ["approvalPolicy"] = approvalPolicy,
                ["sandbox"] = sandbox
            });
        using var lease = _launchGovernor.BeginFresh(freshRequest);
        try
        {
            var res = await s.RequestAsync("thread/start", new { cwd, sandbox, approvalPolicy }, ct);
            var id = res.TryGetProperty("thread", out var t) && t.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            if (id.Length > 0) { _noteRoutes[id] = onNote; _reqRoutes[id] = onReq; }
            using var startedLease = _launchGovernor.BeginFresh(freshRequest with { SessionId = id });
            startedLease.MarkStarted("Codex app-server fresh thread started.", retainUntilExpiry: false);
            return id;
        }
        catch
        {
            lease.MarkFailed("Codex app-server fresh thread failed to start.");
            throw;
        }
    }

    public async Task StartTurnAsync(string threadId, string text, CancellationToken ct, string? approvalPolicy = null, IEnumerable<string>? aliases = null)
    {
        if (_activeTurnClaims.ContainsKey(threadId))
        {
            RecordSessionEvent(threadId, aliases, "codex.turn.refused.active", "Codex turn refused because this server already has a turn running.", "warn", approvalPolicy);
            throw new InvalidOperationException("That Codex chat already has a turn running in this server. Wait for it to finish or interrupt it before sending another.");
        }
        var request = LaunchRequest(threadId, aliases, approvalPolicy);
        if (!_launchGovernor.TryAcquire(request, out var lease, out var leaseDetail))
            throw new InvalidOperationException(leaseDetail);
        if (!_activeTurnClaims.TryAdd(threadId, lease!))
        {
            lease?.Dispose();
            RecordSessionEvent(threadId, aliases, "codex.turn.refused.active", "Codex turn refused because this server already has a turn running.", "warn", approvalPolicy);
            throw new InvalidOperationException("That Codex chat already has a turn running in this server. Wait for it to finish or interrupt it before sending another.");
        }
        var s = await EnsureAsync(ct);
        // turn/start takes UserInput[]; the text variant is {type:"text", text, text_elements:[]}.
        var input = new object[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        object prms = approvalPolicy is null
            ? new { threadId, input }
            : new { threadId, input, approvalPolicy }; // "never" = autonomous (owner-signed auto turn)
        try
        {
            await s.RequestAsync("turn/start", prms, ct);
            lease?.MarkStarted("Codex app-server turn started.", retainUntilExpiry: false);
        }
        catch
        {
            lease?.MarkFailed("Codex app-server turn failed to start.");
            if (_activeTurnClaims.TryRemove(threadId, out var active)) active.Dispose();
            throw;
        }
    }

    public async Task InterruptAsync(string threadId, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        try { await s.RequestAsync("turn/interrupt", new { threadId }, ct); } catch { }
    }

    public async Task RespondApprovalAsync(int requestId, bool allow, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        // CommandExecution/FileChange approval decision is "accept" | "decline" (not approved/denied).
        await s.RespondAsync(requestId, new { decision = allow ? "accept" : "decline" }, ct);
    }

    public void CloseThread(string threadId)
    {
        _noteRoutes.TryRemove(threadId, out _);
        _reqRoutes.TryRemove(threadId, out _);
    }

    public async ValueTask DisposeAsync()
    {
        ReleaseAllTurnClaims();
        if (_srv is not null) await _srv.DisposeAsync();
    }

    private void RetireTurnClaimIfTerminal(JsonElement note)
    {
        var method = note.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
        if (method is not ("turn/completed" or "turn/failed" or "turn/cancelled" or "turn/interrupted")) return;
        if (ThreadIdOf(note) is { } tid && _activeTurnClaims.TryRemove(tid, out var claim))
        {
            RecordSessionEvent(tid, null, "codex." + method.Replace('/', '.'), "Codex app-server turn reached terminal state.");
            claim.Dispose();
        }
    }

    private void ReleaseAllTurnClaims()
    {
        foreach (var key in _activeTurnClaims.Keys)
            if (_activeTurnClaims.TryRemove(key, out var claim))
                claim.Dispose();
    }

    private static SessionLaunchRequest LaunchRequest(string threadId, IEnumerable<string>? aliases, string? approvalPolicy)
        => new(
            threadId,
            aliases,
            "codex",
            "server",
            "codex app-server turn/start",
            "codex.turn.refused.claim",
            "codex.turn.started",
            "codex.turn.failed",
            Details: string.IsNullOrWhiteSpace(approvalPolicy)
                ? null
                : new Dictionary<string, string> { ["approvalPolicy"] = approvalPolicy! });

    private static void RecordSessionEvent(
        string? sessionId,
        IEnumerable<string>? aliases,
        string kind,
        string summary,
        string severity = "info",
        string? approvalPolicy = null)
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
        var details = string.IsNullOrWhiteSpace(approvalPolicy)
            ? null
            : new Dictionary<string, string> { ["approvalPolicy"] = approvalPolicy! };
        var ev = SessionEventLedger.Create(
            kind,
            summary,
            sessionId,
            "codex",
            source: "server",
            severity: severity,
            details: details,
            sessionIds: ids);
        SessionEventLedger.AppendBestEffortQueued(ev);
    }
}

// The unified row the sidebar renders, merged across tools. Source is "codex" (drivable live) or "claude" (history).
public sealed record AgentSessionDto(string Id, string Name, string Preview, string Cwd, string Source, long UpdatedAt);

public sealed record RenameRequest(string? Title, string? Source);
public sealed record OpenRequest(string? Target, string? Source, string? Cwd);

// What the sidebar renders for each session (from thread/list).
public sealed record SessionInfo(string Id, string Name, string Preview, string Cwd, string Source, long UpdatedAt)
{
    public static SessionInfo From(JsonElement t)
    {
        string S(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        long L(string k) => t.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
        return new SessionInfo(S("id"), S("name"), S("preview"), S("cwd"), S("source"), L("updatedAt"));
    }
}
