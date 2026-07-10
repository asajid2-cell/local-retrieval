using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

// Bridges one browser WebSocket to the codex app-server (via the hub). The browser opens a session
// (resume + history), goes live (turn/start), interrupts, and answers approvals; the thread's events
// stream back as AgentEvents. One open thread per socket.
public static class AgentWebSocket
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task HandleAsync(
        WebSocket ws,
        CodexAgentHub hub,
        ClaudeSessionStore claude,
        ClaudeLiveDriver claudeDriver,
        CommandSigner signer,
        string defaultWorkspace,
        CancellationToken ct,
        CancellationToken ownerStopping,
        Func<string, CancellationToken, Task<SessionResolutionResult>> resolveSession)
    {
        var send = new SemaphoreSlim(1, 1);
        ThreadRouteBinding? openThread = null;
        var openSource = "codex";
        string? openedId = null;           // the id the client opened (stable; used to bind signatures)
        string? claudeSid = null;          // current Claude session id (updated each turn)
        var claudeCwd = defaultWorkspace;
        IReadOnlyList<string> openAliases = Array.Empty<string>();
        System.Diagnostics.Process? claudeProc = null;
        Task? codexTurnStart = null;
        long openGeneration = 0;
        var buf = new byte[16 * 1024];

        void ClearOpenSession()
        {
            Interlocked.Increment(ref openGeneration);
            if (openThread is not null) hub.CloseThread(openThread);
            openThread = null;
            claudeProc = null;
            openedId = null;
            claudeSid = null;
            claudeCwd = defaultWorkspace;
            openAliases = Array.Empty<string>();
            openSource = "codex";
            codexTurnStart = null;
        }

        bool IsClaudeTurnActive()
        {
            try { return claudeProc is not null && !claudeProc.HasExited; }
            catch { return false; }
        }

        bool HasActiveTurn() =>
            IsClaudeTurnActive()
            || codexTurnStart is { IsCompleted: false }
            || openThread is not null && hub.IsTurnActive(openThread.ThreadId);

        async Task OnNote(JsonElement note)
        {
            var method = note.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
            var p = note.TryGetProperty("params", out var pp) ? pp : default;
            foreach (var ev in CodexItemMapper.MapNotification(method, p)) await SendJson(ws, send, ev, ct);
        }
        async Task OnReq(JsonElement req)
        {
            if (!req.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) return;
            var method = req.TryGetProperty("method", out var mEl) ? mEl.GetString() ?? "" : "";
            var p = req.TryGetProperty("params", out var pp) ? pp : default;
            await SendJson(ws, send, new { kind = "PermissionRequest", requestId = id, tool = method, text = ApprovalText(method, p) }, ct);
        }

        // Owner-signature gate for execution-causing ops (send, approve). When a signing key is configured,
        // EVERY such command must carry a valid HMAC bound to the open session — closes injection on any hop
        // (incl. the plaintext LAN leg): forged/tampered/replayed/cross-session commands are refused. With no
        // key configured the server accepts unsigned commands (safe mode only; auto is gated separately).
        async Task<bool> RequireSig(JsonElement m, string op, string field5, string field8)
        {
            if (!signer.Enabled) return true;
            var nonce = Str(m, "nonce") ?? "";
            var sig = Str(m, "sig") ?? "";
            var ts = m.TryGetProperty("ts", out var e) && e.TryGetInt64(out var v) ? v : 0L;
            var msgThread = Str(m, "threadId") ?? "";
            var msgSource = Str(m, "source") ?? openSource;
            if (msgThread != (openedId ?? "") || msgSource != openSource)
            { await SendJson(ws, send, AgentEvent.Err("rejected: command is not bound to the open session."), ct); return false; }
            var canonical = CommandSigner.Canonical(op, msgSource, msgThread, field5, ts, nonce, field8);
            if (!signer.Verify(canonical, nonce, ts, sig, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var reason))
            { await SendJson(ws, send, AgentEvent.Err("rejected: " + reason + ". Check your owner key."), ct); return false; }
            return true;
        }

        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReceiveText(ws, buf, ct);
                if (msg is null) break;
                JsonElement root;
                try { using var d = JsonDocument.Parse(msg); root = d.RootElement.Clone(); } catch { continue; }

                switch (Str(root, "op"))
                {
                    case "open":
                    {
                        var id = Str(root, "id");
                        if (id is null)
                        {
                            await SendJson(ws, send, new { kind = "OpenFailed", text = "open refused: session id is required" }, ct);
                            await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                            break;
                        }
                        var committed = false;
                        try
                        {
                            var resolution = await resolveSession(id, ct);
                            if (!resolution.Ok || resolution.Session is null)
                            {
                                await SendJson(ws, send, new { kind = "OpenFailed", text = "open refused: " + resolution.Message }, ct);
                                await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                                break;
                            }
                            var trusted = resolution.Session;
                            if (HasActiveTurn())
                            {
                                await SendJson(ws, send, new { kind = "OpenFailed", text = "open refused: interrupt or finish the active turn before switching sessions." }, ct);
                                await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                                break;
                            }
                            id = trusted.SessionId;
                            var nextSource = trusted.Tool == SessionTool.Claude ? "claude" : "codex";
                            var nextGeneration = Interlocked.Read(ref openGeneration) + 1;
                            ThreadRouteBinding? nextOpenThread = null;
                            IReadOnlyList<AgentEvent> history;
                            if (nextSource == "claude")
                            {
                                history = claude.ReadHistory(id);
                            }
                            else
                            {
                                var prepared = await hub.PrepareThreadOpenAsync(
                                    id,
                                    trusted.WorkingDirectory,
                                    note => nextGeneration == Interlocked.Read(ref openGeneration) ? OnNote(note) : Task.CompletedTask,
                                    req => nextGeneration == Interlocked.Read(ref openGeneration) ? OnReq(req) : Task.CompletedTask,
                                    ct);
                                nextOpenThread = prepared.Binding;
                                history = prepared.History;
                            }

                            ClearOpenSession();
                            committed = true;
                            openThread = nextOpenThread;
                            openSource = nextSource;
                            openedId = id;
                            openAliases = trusted.Aliases;
                            claudeSid = nextSource == "claude" ? id : null;
                            claudeCwd = nextSource == "claude" ? trusted.WorkingDirectory : defaultWorkspace;
                            await SendJson(ws, send, new
                            {
                                kind = "Opened",
                                threadId = id,
                                source = openSource,
                                cwd = trusted.WorkingDirectory,
                                live = nextSource == "claude" ? claudeDriver.Available : true
                            }, ct);
                            foreach (var ev in history) await SendJson(ws, send, ev, ct);
                        }
                        catch (Exception ex)
                        {
                            if (committed) ClearOpenSession();
                            await SendJson(ws, send, new { kind = "OpenFailed", text = "open failed: " + ex.Message }, ct);
                        }
                        await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                        break;
                    }
                    case "new":
                    {
                        if (HasActiveTurn())
                        {
                            await SendJson(ws, send, new { kind = "OpenFailed", text = "new session refused: interrupt or finish the active turn before switching sessions." }, ct);
                            await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                            break;
                        }
                        var committed = false;
                        try
                        {
                            var nextGeneration = Interlocked.Read(ref openGeneration) + 1;
                            var nextOpenThread = await hub.NewThreadAsync(
                                Str(root, "cwd") ?? defaultWorkspace,
                                note => nextGeneration == Interlocked.Read(ref openGeneration) ? OnNote(note) : Task.CompletedTask,
                                req => nextGeneration == Interlocked.Read(ref openGeneration) ? OnReq(req) : Task.CompletedTask,
                                ct,
                                Str(root, "approvalPolicy") ?? "on-request",
                                Str(root, "sandbox") ?? "workspace-write");
                            ClearOpenSession();
                            committed = true;
                            openThread = nextOpenThread;
                            openedId = openThread.ThreadId;
                            openAliases = new[] { openThread.ThreadId };
                            await SendJson(ws, send, new { kind = "Opened", threadId = openThread.ThreadId, isNew = true, live = true }, ct);
                        }
                        catch (Exception ex)
                        {
                            if (committed) ClearOpenSession();
                            await SendJson(ws, send, new { kind = "OpenFailed", text = "new session failed: " + ex.Message }, ct);
                        }
                        await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                        break;
                    }
                    case "send":
                    {
                        var text = Str(root, "text");
                        if (string.IsNullOrWhiteSpace(text)) break;
                        if (openedId is null)
                        {
                            await SendJson(ws, send, AgentEvent.Err("refused: no session is open."), ct);
                            break;
                        }

                        // mode controls the AGENT's autonomy: "auto" = no per-command approval, "safe" = approvals
                        // (codex) / edits-only (claude). When a signing key is configured BOTH require a valid owner
                        // signature (the canonical binds the mode, so a safe signature can't be reused as auto).
                        // auto is only available when signing is configured.
                        var auto = Str(root, "mode") == "auto";
                        if (auto && !signer.Enabled)
                        {
                            await SendJson(ws, send, AgentEvent.Err("rejected: auto mode needs a signing key configured on the server."), ct);
                            break;
                        }
                        if (!await RequireSig(root, "send", auto ? "auto" : "safe", text!)) break;

                        if (openSource == "claude")
                        {
                            if (IsClaudeTurnActive())
                            {
                                await SendJson(ws, send, AgentEvent.Err("refused: this Claude turn is still running; wait or interrupt it before sending another."), ct);
                                break;
                            }
                            await SendJson(ws, send, AgentEvent.Stat("turn-start"), ct);
                            try
                            {
                                var generation = Interlocked.Read(ref openGeneration);
                                claudeProc = claudeDriver.StartTurn(claudeSid, claudeCwd, text!, async ev =>
                                {
                                    if (generation != Interlocked.Read(ref openGeneration)) return;
                                    if (ev.Kind == AgentEventKind.SessionStarted)
                                    {
                                        claudeSid = ev.SessionId;
                                        if (!string.IsNullOrWhiteSpace(claudeSid))
                                            openAliases = openAliases
                                                .Append(claudeSid)
                                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                                .ToArray();
                                        return;
                                    }
                                    await SendJson(ws, send, ev, ct);
                                }, ownerStopping, auto ? "bypassPermissions" : "acceptEdits", openAliases);
                            }
                            catch (InvalidOperationException ex)
                            {
                                await SendJson(ws, send, AgentEvent.Err("refused: " + ex.Message), ct);
                                await SendJson(ws, send, AgentEvent.Stat("idle"), ct);
                            }
                        }
                        else if (openThread is not null)
                        {
                            var tid = openThread.ThreadId;
                            var aliases = openAliases;
                            var policy = auto ? "never" : null; // never = autonomous (owner-signed); else inherit session policy (approvals)
                            // fire-and-forget so the receive loop stays free for interrupt; surface failures.
                            codexTurnStart = Task.Run(async () =>
                            {
                                try { await hub.StartTurnAsync(tid, text!, ct, policy, aliases); }
                                catch (Exception ex) { await SendJson(ws, send, AgentEvent.Err("send failed: " + ex.Message), ct); }
                            }, ct);
                        }
                        break;
                    }
                    case "interrupt":
                        if (openSource == "claude") { try { claudeProc?.Kill(entireProcessTree: true); } catch { } await SendJson(ws, send, AgentEvent.Stat("idle"), ct); }
                        else if (openThread is not null) await hub.InterruptAsync(openThread.ThreadId, ct);
                        break;
                    case "approve":
                        if (root.TryGetProperty("requestId", out var ridEl) && ridEl.TryGetInt32(out var rid))
                        {
                            var decision = Str(root, "decision") ?? "deny";
                            // an approval also bypasses the human gate, so it must be owner-signed too.
                            if (!await RequireSig(root, "approve", decision, rid.ToString())) break;
                            await hub.RespondApprovalAsync(rid, decision == "allow", ct);
                        }
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            ClearOpenSession();
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    private static string ApprovalText(string method, JsonElement p)
    {
        var cmd = Str(p, "command");
        if (!string.IsNullOrEmpty(cmd)) return "run: " + cmd;
        if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("changes", out _)) return "apply file changes";
        return method;
    }

    private static async Task SendJson(WebSocket ws, SemaphoreSlim send, object payload, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Wire);
        await send.WaitAsync(ct);
        try { if (ws.State == WebSocketState.Open) await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { send.Release(); }
    }

    private static async Task<string?> ReceiveText(WebSocket ws, byte[] buf, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        WebSocketReceiveResult r;
        do
        {
            r = await ws.ReceiveAsync(buf, ct);
            if (r.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buf, 0, r.Count);
        } while (!r.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
