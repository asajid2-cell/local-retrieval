using System.Collections.Concurrent;
using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

public sealed record ThreadRouteBinding(
    string ThreadId,
    Guid OwnerToken,
    Func<JsonElement, Task> OnNote,
    Func<JsonElement, Task> OnRequest);

public sealed record PreparedThreadOpen(
    ThreadRouteBinding Binding,
    IReadOnlyList<AgentEvent> History);

public sealed class ThreadRouteRegistry
{
    private readonly ConcurrentDictionary<string, ThreadRouteBinding> _routes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _replaceGate = new(1, 1);

    public ThreadRouteBinding Register(
        string threadId,
        Func<JsonElement, Task> onNote,
        Func<JsonElement, Task> onRequest)
    {
        var binding = new ThreadRouteBinding(threadId, Guid.NewGuid(), onNote, onRequest);
        _routes[threadId] = binding;
        return binding;
    }

    public bool TryGet(string threadId, out ThreadRouteBinding binding) =>
        _routes.TryGetValue(threadId, out binding!);

    public async Task<ThreadRouteBinding> ReplaceAfterAsync(
        string threadId,
        Func<JsonElement, Task> onNote,
        Func<JsonElement, Task> onRequest,
        Func<Task> prepare,
        CancellationToken cancellationToken)
    {
        await _replaceGate.WaitAsync(cancellationToken);
        try
        {
            await prepare();
            return Register(threadId, onNote, onRequest);
        }
        finally
        {
            _replaceGate.Release();
        }
    }

    public void Close(ThreadRouteBinding binding)
    {
        if (!_routes.TryGetValue(binding.ThreadId, out var current)
            || current.OwnerToken != binding.OwnerToken)
            return;

        ((ICollection<KeyValuePair<string, ThreadRouteBinding>>)_routes)
            .Remove(new KeyValuePair<string, ThreadRouteBinding>(binding.ThreadId, current));
    }
}

// Owns the single `codex app-server` process for the whole web server and multiplexes it: lists all
// sessions, resumes any, runs turns, and routes the app-server's notifications + approval-requests to
// whichever WebSocket opened that thread. One app-server backs many browser tabs/threads.
public sealed class CodexAgentHub : IAsyncDisposable
{
    private readonly string _exe;
    private readonly SessionLaunchGovernor _launchGovernor;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CodexAppServer? _srv;

    // The newest socket owns a thread's event route. Tokenized close prevents an older socket from
    // disconnecting that newer owner when the old tab navigates away or closes.
    private readonly ThreadRouteRegistry _routes = new();
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
                if (ThreadIdOf(note) is { } tid && _routes.TryGet(tid, out var route))
                    try { await route.OnNote(note); } catch { }
            }
        }
        finally { ReleaseAllTurnClaims(); }
    }

    private async Task PumpServerRequests(CodexAppServer s)
    {
        await foreach (var req in s.ServerRequests.ReadAllAsync())
        {
            // route approval requests to the owning thread; auto-deny if no owner is listening.
            if (ThreadIdOf(req) is { } tid && _routes.TryGet(tid, out var route))
                try { await route.OnRequest(req); continue; } catch { }
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

    // Prepare the complete open before publishing route ownership. A failed resume or history read
    // leaves the prior tab/session route intact instead of exposing a half-open replacement.
    public async Task<PreparedThreadOpen> PrepareThreadOpenAsync(
        string threadId,
        string? cwd,
        Func<JsonElement, Task> onNote,
        Func<JsonElement, Task> onReq,
        CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        List<AgentEvent>? history = null;
        var binding = await _routes.ReplaceAfterAsync(
            threadId,
            onNote,
            onReq,
            async () =>
            {
                await s.ResumeThreadAsync(threadId, cwd, ct);
                history = await ReadHistoryAsync(s, threadId, ct);
            },
            ct);
        return new PreparedThreadOpen(
            binding,
            (IReadOnlyList<AgentEvent>?)history ?? Array.Empty<AgentEvent>());
    }

    // Read a thread's full history as normalized events. thread/read loads items lazily (it won't pull
    // a 96 MB rollout), so we take the rollout `path` it reports and parse the .jsonl directly.
    public async Task<List<AgentEvent>> ReadHistoryAsync(string threadId, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        return await ReadHistoryAsync(s, threadId, ct);
    }

    private static async Task<List<AgentEvent>> ReadHistoryAsync(
        CodexAppServer s,
        string threadId,
        CancellationToken ct)
    {
        var read = await s.ReadThreadAsync(threadId, ct);
        var path = read.TryGetProperty("thread", out var thread) && thread.TryGetProperty("path", out var pe) && pe.ValueKind == JsonValueKind.String
            ? pe.GetString() : null;
        return string.IsNullOrEmpty(path) ? new List<AgentEvent>() : RolloutToEvents.Parse(path!);
    }

    // Start a brand-new session in a workspace; returns its threadId.
    public async Task<ThreadRouteBinding> NewThreadAsync(string cwd, Func<JsonElement, Task> onNote, Func<JsonElement, Task> onReq, CancellationToken ct, string approvalPolicy = "on-request", string sandbox = "workspace-write")
    {
        var s = await EnsureAsync(ct);
        ThreadRouteBinding? binding = null;
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
            if (id.Length == 0) throw new InvalidDataException("codex app-server returned no thread id");
            binding = _routes.Register(id, onNote, onReq);
            using var startedLease = _launchGovernor.BeginFresh(freshRequest with { SessionId = id });
            startedLease.MarkStarted("Codex app-server fresh thread started.", retainUntilExpiry: false);
            return binding;
        }
        catch
        {
            if (binding is not null) _routes.Close(binding);
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
        CodexAppServer s;
        try
        {
            // Ensure may restart the shared app-server and clear stale published turn claims.
            // Hold the cross-process lease now, but publish this turn only after Ensure completes.
            s = await EnsureAsync(ct);
        }
        catch
        {
            lease?.MarkFailed("Codex app-server failed before the turn claim was published.");
            lease?.Dispose();
            throw;
        }
        if (!_activeTurnClaims.TryAdd(threadId, lease!))
        {
            lease?.Dispose();
            RecordSessionEvent(threadId, aliases, "codex.turn.refused.active", "Codex turn refused because this server already has a turn running.", "warn", approvalPolicy);
            throw new InvalidOperationException("That Codex chat already has a turn running in this server. Wait for it to finish or interrupt it before sending another.");
        }
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

    public void CloseThread(ThreadRouteBinding binding) => _routes.Close(binding);
    public bool IsTurnActive(string threadId) => _activeTurnClaims.ContainsKey(threadId);

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
public sealed record OpenRequest(string? Target);

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
