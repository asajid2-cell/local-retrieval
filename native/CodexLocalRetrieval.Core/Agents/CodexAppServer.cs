using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexLocalRetrieval.Core.Agents;

// A client of `codex app-server` — the rich JSON-RPC protocol the Codex desktop app / IDE use, over
// newline-delimited stdio. ONE app-server process backs many threads. It exposes everything needed for
// a full session experience: thread/list (all sessions), thread/read (history), thread/resume +
// turn/start (go live), turn/steer / turn/interrupt, streaming item/* notifications, and execCommand/
// applyPatch approvals (server -> us requests we answer). Pure transport; mapping to AgentEvent lives
// elsewhere. NOTE: passes `-c service_tier=fast` (this codex build rejects the `default` tier).
public sealed class CodexAppServer : IAsyncDisposable
{
    private readonly Process _proc;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _notifications = Channel.CreateUnbounded<JsonElement>();
    private readonly Channel<JsonElement> _serverRequests = Channel.CreateUnbounded<JsonElement>();
    private int _nextId;

    // {method, params} — streaming events (item/*, turn/*, thread/*, ...).
    public ChannelReader<JsonElement> Notifications => _notifications.Reader;
    // {id, method, params} — approvals/elicitations the agent asks us; answer with RespondAsync(id, ...).
    public ChannelReader<JsonElement> ServerRequests => _serverRequests.Reader;
    public bool HasExited => _proc.HasExited;

    private CodexAppServer(Process proc) { _proc = proc; _stdin = proc.StandardInput; }

    public static CodexAppServer Start(string exe, string serviceTier = "fast", string? cwd = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            WorkingDirectory = string.IsNullOrWhiteSpace(cwd) ? Environment.CurrentDirectory : cwd,
        };
        psi.ArgumentList.Add("app-server");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("service_tier=" + serviceTier);
        var proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start codex app-server");
        var s = new CodexAppServer(proc);
        _ = Task.Run(s.ReadLoopAsync);
        return s;
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            string? line;
            while ((line = await _proc.StandardOutput.ReadLineAsync()) is not null)
            {
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(line); msg = doc.RootElement.Clone(); }
                catch { continue; } // skip non-JSON noise

                var hasId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number;
                var hasMethod = msg.TryGetProperty("method", out _);

                if (hasId && !hasMethod) // a response to one of our requests
                {
                    var id = idEl.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        if (msg.TryGetProperty("error", out var err)) tcs.TrySetException(new InvalidOperationException(err.ToString()));
                        else tcs.TrySetResult(msg.TryGetProperty("result", out var r) ? r.Clone() : default);
                    }
                }
                else if (hasId && hasMethod) await _serverRequests.Writer.WriteAsync(msg); // server request (approval)
                else if (hasMethod) await _notifications.Writer.WriteAsync(msg);            // notification
            }
        }
        catch { /* process died */ }
        finally
        {
            _notifications.Writer.TryComplete();
            _serverRequests.Writer.TryComplete();
            foreach (var kv in _pending) kv.Value.TrySetException(new InvalidOperationException("app-server closed"));
            _pending.Clear();
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? prms = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        await WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = prms ?? new { } }), ct);
        using (ct.Register(() => tcs.TrySetCanceled()))
            return await tcs.Task;
    }

    public Task NotifyAsync(string method, object? prms = null, CancellationToken ct = default) =>
        WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = prms ?? new { } }), ct);

    public Task RespondAsync(int id, object result, CancellationToken ct = default) =>
        WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), ct);

    private async Task WriteLineAsync(string json, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct);
        try { await _stdin.WriteAsync(json.AsMemory(), ct); await _stdin.WriteAsync("\n".AsMemory(), ct); await _stdin.FlushAsync(ct); }
        finally { _writeLock.Release(); }
    }

    // ---- typed helpers over the protocol ----

    public Task<JsonElement> InitializeAsync(CancellationToken ct = default) =>
        RequestAsync("initialize", new { clientInfo = new { name = "codex-local-retrieval", title = "Codex Archive", version = "0.1" }, capabilities = (object?)null }, ct);

    public Task<JsonElement> ListThreadsAsync(int pageSize = 50, string? cursor = null, CancellationToken ct = default) =>
        RequestAsync("thread/list", new { pageSize, cursor }, ct);

    public Task<JsonElement> ReadThreadAsync(string threadId, CancellationToken ct = default) =>
        RequestAsync("thread/read", new { threadId }, ct);

    public Task<JsonElement> ResumeThreadAsync(string threadId, string? cwd = null, CancellationToken ct = default) =>
        RequestAsync("thread/resume", new { threadId, cwd }, ct);

    public async ValueTask DisposeAsync()
    {
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
        await Task.CompletedTask;
    }
}
