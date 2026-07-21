using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace CodexLocalRetrieval.Core.Agents;

public sealed class CodexAppServerResponseException(string message)
    : InvalidOperationException(message);

// A client of `codex app-server` — the rich JSON-RPC protocol the Codex desktop app / IDE use, over
// newline-delimited stdio. ONE app-server process backs many threads. It exposes everything needed for
// a full session experience: thread/list (all sessions), thread/read (history), thread/resume +
// turn/start (go live), turn/steer / turn/interrupt, streaming item/* notifications, and execCommand/
// applyPatch approvals (server -> us requests we answer). Pure transport; mapping to AgentEvent lives
// elsewhere. NOTE: passes `-c service_tier=fast` (this codex build rejects the `default` tier).
public sealed class CodexAppServer : IAsyncDisposable
{
    private const int MaxProtocolLineChars = 2 * 1024 * 1024;
    private const int NotificationCapacity = 128;
    private const int ServerRequestCapacity = 16;
    private const int PendingRequestCapacity = 256;
    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FrameWriteTimeout = TimeSpan.FromSeconds(15);
    private readonly ContainedProcess _proc;
    private readonly StreamWriter _stdin;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _pendingSlots = new(PendingRequestCapacity, PendingRequestCapacity);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly Channel<JsonElement> _notifications = CreateBoundedChannel(NotificationCapacity);
    private readonly Channel<JsonElement> _serverRequests = CreateBoundedChannel(ServerRequestCapacity);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _disposeGate = new();
    private Task? _readLoopTask;
    private Task? _stderrDrainTask;
    private Task? _disposeTask;
    private int _transportCompleted;
    private int _terminationConfirmed;
    private int _nextId;

    // {method, params} — streaming events (item/*, turn/*, thread/*, ...).
    public ChannelReader<JsonElement> Notifications => _notifications.Reader;
    // {id, method, params} — approvals/elicitations the agent asks us; answer with RespondAsync(id, ...).
    public ChannelReader<JsonElement> ServerRequests => _serverRequests.Reader;
    public bool HasExited => _proc.HasExited;
    public bool TerminationConfirmed => Volatile.Read(ref _terminationConfirmed) != 0;

    private CodexAppServer(ContainedProcess proc) { _proc = proc; _stdin = proc.StandardInput; }

    private static Channel<JsonElement> CreateBoundedChannel(int capacity) =>
        Channel.CreateBounded<JsonElement>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });

    public static CodexAppServer Start(
        string exe,
        string serviceTier = "fast",
        string? cwd = null,
        IProcessContainment? processContainment = null,
        Func<ProcessStartInfo, Process?>? processStarter = null)
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
        if (processContainment is not null && processStarter is not null)
            throw new ArgumentException("A test process starter cannot be combined with creation-time containment.");
        if (processContainment is null && processStarter is null)
            throw new InvalidOperationException("Codex app-server launch requires creation-time process containment.");
        var proc = processContainment is null
            ? ContainedProcess.Start(psi, processStarter)
            : processContainment.StartContained(psi);
        var s = new CodexAppServer(proc);
        s._readLoopTask = Task.Run(s.ReadLoopAsync);
        s._stderrDrainTask = Task.Run(s.DrainStderrAsync);
        return s;
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new char[4096];
        try
        {
            while (await _proc.StandardError.ReadAsync(buffer.AsMemory(), _shutdown.Token) > 0)
            {
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch
        {
        }
    }

    private async Task ReadLoopAsync()
    {
        Exception? terminalError = null;
        try
        {
            var output = new BoundedTextLineReader(_proc.StandardOutput, MaxProtocolLineChars);
            string? line;
            while ((line = await output.ReadLineAsync(_shutdown.Token)) is not null)
            {
                JsonElement msg;
                try { using var doc = JsonDocument.Parse(line); msg = doc.RootElement.Clone(); }
                catch { continue; } // skip non-JSON noise
                if (msg.ValueKind != JsonValueKind.Object) continue;

                var hasId = msg.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number;
                var hasMethod = msg.TryGetProperty("method", out _);

                if (hasId && !hasMethod) // a response to one of our requests
                {
                    var id = idEl.GetInt32();
                    if (_pending.TryRemove(id, out var tcs))
                    {
                        _pendingSlots.Release();
                        if (msg.TryGetProperty("error", out var err)) tcs.TrySetException(new CodexAppServerResponseException(err.ToString()));
                        else tcs.TrySetResult(msg.TryGetProperty("result", out var r) ? r.Clone() : default);
                    }
                }
                else if (hasId && hasMethod) await _serverRequests.Writer.WriteAsync(msg, _shutdown.Token);
                else if (hasMethod) await _notifications.Writer.WriteAsync(msg, _shutdown.Token);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            terminalError = ex;
        }
        finally
        {
            var terminationConfirmed = await StopProcessAsync();
            if (terminationConfirmed) Volatile.Write(ref _terminationConfirmed, 1);
            if (!terminationConfirmed)
            {
                terminalError = new ProcessContainmentException(
                    "Codex app-server transport stopped but process termination was not confirmed.",
                    terminationConfirmed: false,
                    terminalError);
            }
            CompleteTransport(terminalError);
        }
    }

    public async Task<JsonElement> RequestAsync(string method, object? prms = null, CancellationToken ct = default)
    {
        ThrowIfTransportClosed();
        using var requestLifetime = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _pendingSlots.WaitAsync(requestLifetime.Token);
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = tcs.Task.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _pending[id] = tcs;
        if (Volatile.Read(ref _transportCompleted) != 0)
        {
            if (_pending.TryRemove(id, out _))
            {
                _pendingSlots.Release();
                throw new ObjectDisposedException(nameof(CodexAppServer), "app-server transport is closed");
            }
            return await tcs.Task;
        }
        try
        {
            await WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = prms ?? new { } }), ct);
            using var registration = ct.Register(() =>
            {
                if (_pending.TryRemove(id, out var pending))
                {
                    _pendingSlots.Release();
                    pending.TrySetCanceled(ct);
                }
            });
            return await tcs.Task;
        }
        catch
        {
            if (_pending.TryRemove(id, out _)) _pendingSlots.Release();
            throw;
        }
    }

    public Task NotifyAsync(string method, object? prms = null, CancellationToken ct = default) =>
        WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", method, @params = prms ?? new { } }), ct);

    public Task RespondAsync(int id, object result, CancellationToken ct = default) =>
        WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }), ct);

    private async Task WriteLineAsync(string json, CancellationToken ct)
    {
        ThrowIfTransportClosed();
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        await _writeLock.WaitAsync(acquisition.Token);
        try
        {
            // Caller cancellation may abandon its response wait, but it must never split a shared
            // newline-delimited frame. Once the lock is held, only transport shutdown or the bounded
            // write deadline can interrupt the frame. Any interrupted write is fatal to this transport.
            using var frameLifetime = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            frameLifetime.CancelAfter(FrameWriteTimeout);
            var frame = json + "\n";
            try
            {
                await _stdin.WriteAsync(frame.AsMemory(), frameLifetime.Token);
                await _stdin.FlushAsync(frameLifetime.Token);
            }
            catch (Exception ex)
            {
                FailTransport(ex);
                throw;
            }
        }
        finally { _writeLock.Release(); }
    }

    private void FailTransport(Exception error)
    {
        try { _shutdown.Cancel(); } catch { }
        CompleteTransport(error);
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { }
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

    public ValueTask DisposeAsync()
    {
        lock (_disposeGate)
            return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        _shutdown.Cancel();
        Exception? cleanupError = null;
        try
        {
            if (_readLoopTask is not null)
                await _readLoopTask.WaitAsync(ShutdownTimeout);
        }
        catch (Exception ex)
        {
            cleanupError = ex;
        }

        if (!TerminationConfirmed && await StopProcessAsync())
            Volatile.Write(ref _terminationConfirmed, 1);
        CompleteTransport(cleanupError);

        try { _proc.Dispose(); } catch (Exception ex) { cleanupError ??= ex; }
        if (_stderrDrainTask is not null)
        {
            try { await _stderrDrainTask.WaitAsync(ShutdownTimeout); }
            catch (Exception ex) { cleanupError ??= ex; }
        }

        if (_readLoopTask is null || _readLoopTask.IsCompleted)
            _shutdown.Dispose();
        if (!TerminationConfirmed)
        {
            throw new ProcessContainmentException(
                "Codex app-server disposal finished without confirmed process termination.",
                terminationConfirmed: false,
                cleanupError);
        }
        if (cleanupError is TimeoutException)
            throw new TimeoutException("Codex app-server transport did not stop before the shutdown deadline.", cleanupError);
    }

    private async Task<bool> StopProcessAsync()
    {
        try
        {
            if (!_proc.HasExited) _proc.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            await _proc.WaitForExitAsync(CancellationToken.None).WaitAsync(ShutdownTimeout);
            return true;
        }
        catch
        {
            try { return _proc.HasExited; } catch { return false; }
        }
    }

    private void CompleteTransport(Exception? error)
    {
        if (Interlocked.Exchange(ref _transportCompleted, 1) != 0) return;
        _notifications.Writer.TryComplete(error);
        _serverRequests.Writer.TryComplete(error);
        var pendingError = error ?? new InvalidOperationException("app-server closed");
        foreach (var kv in _pending)
            if (_pending.TryRemove(kv.Key, out var pending))
            {
                _pendingSlots.Release();
                pending.TrySetException(pendingError);
            }
    }

    private void ThrowIfTransportClosed()
    {
        if (Volatile.Read(ref _transportCompleted) != 0 || _shutdown.IsCancellationRequested)
            throw new ObjectDisposedException(nameof(CodexAppServer));
    }
}
