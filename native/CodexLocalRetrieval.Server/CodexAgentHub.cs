using System.Collections.Concurrent;
using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Server;

// Owns the single `codex app-server` process for the whole web server and multiplexes it: lists all
// sessions, resumes any, runs turns, and routes the app-server's notifications + approval-requests to
// whichever WebSocket opened that thread. One app-server backs many browser tabs/threads.
public sealed class CodexAgentHub : IAsyncDisposable
{
    private readonly string _exe;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private CodexAppServer? _srv;

    // threadId -> consumer that receives this thread's notifications / approval-requests.
    private readonly ConcurrentDictionary<string, Func<JsonElement, Task>> _noteRoutes = new();
    private readonly ConcurrentDictionary<string, Func<JsonElement, Task>> _reqRoutes = new();

    public CodexAgentHub(string exe) => _exe = exe;

    private async Task<CodexAppServer> EnsureAsync(CancellationToken ct)
    {
        if (_srv is { HasExited: false }) return _srv;
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
        await foreach (var note in s.Notifications.ReadAllAsync())
            if (ThreadIdOf(note) is { } tid && _noteRoutes.TryGetValue(tid, out var f))
                try { await f(note); } catch { }
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
        var res = await s.RequestAsync("thread/start", new { cwd, sandbox, approvalPolicy }, ct);
        var id = res.TryGetProperty("thread", out var t) && t.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
        if (id.Length > 0) { _noteRoutes[id] = onNote; _reqRoutes[id] = onReq; }
        return id;
    }

    public async Task StartTurnAsync(string threadId, string text, CancellationToken ct)
    {
        var s = await EnsureAsync(ct);
        // turn/start takes UserInput[]; the text variant is {type:"text", text, text_elements:[]}.
        var input = new object[] { new { type = "text", text, text_elements = Array.Empty<object>() } };
        await s.RequestAsync("turn/start", new { threadId, input }, ct);
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
        if (_srv is not null) await _srv.DisposeAsync();
    }
}

// The unified row the sidebar renders, merged across tools. Source is "codex" (drivable live) or "claude" (history).
public sealed record AgentSessionDto(string Id, string Name, string Preview, string Cwd, string Source, long UpdatedAt);

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
