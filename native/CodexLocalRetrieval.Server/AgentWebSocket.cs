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

    public static async Task HandleAsync(WebSocket ws, CodexAgentHub hub, ClaudeSessionStore claude, ClaudeLiveDriver claudeDriver, CommandSigner signer, string defaultWorkspace, CancellationToken ct, Func<string, Task<IEnumerable<string>>>? aliasesForSessionId = null)
    {
        var send = new SemaphoreSlim(1, 1);
        string? openThreadId = null;
        var openSource = "codex";
        string? openedId = null;           // the id the client opened (stable; used to bind signatures)
        string? claudeSid = null;          // current Claude session id (updated each turn)
        var claudeCwd = defaultWorkspace;
        System.Diagnostics.Process? claudeProc = null;
        var buf = new byte[16 * 1024];

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

        async Task<(bool ok, IEnumerable<string>? aliases)> TryResolveAliasesAsync(string? sessionId)
        {
            if (string.IsNullOrWhiteSpace(sessionId) || aliasesForSessionId is null) return (true, null);
            try { return (true, await aliasesForSessionId(sessionId)); }
            catch (Exception ex)
            {
                await SendJson(ws, send, AgentEvent.Err("refused: could not verify session aliases (" + ex.Message + "); refusing to risk a second writer."), ct);
                await SendJson(ws, send, AgentEvent.Stat("idle"), ct);
                return (false, null);
            }
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
                        if (id is null) break;
                        openSource = Str(root, "source") ?? "codex";
                        openedId = id;
                        if (openThreadId is not null) { hub.CloseThread(openThreadId); openThreadId = null; }
                        try
                        {
                            if (openSource == "claude")
                            {
                                claudeSid = id;
                                claudeCwd = Str(root, "cwd") is { Length: > 0 } cc ? cc : defaultWorkspace;
                                await SendJson(ws, send, new { kind = "Opened", threadId = id, live = claudeDriver.Available }, ct);
                                foreach (var ev in claude.ReadHistory(id)) await SendJson(ws, send, ev, ct);
                            }
                            else
                            {
                                await hub.OpenThreadAsync(id, Str(root, "cwd"), OnNote, OnReq, ct);
                                openThreadId = id;
                                await SendJson(ws, send, new { kind = "Opened", threadId = id, live = true }, ct);
                                foreach (var ev in await hub.ReadHistoryAsync(id, ct)) await SendJson(ws, send, ev, ct);
                            }
                        }
                        catch (Exception ex) { await SendJson(ws, send, AgentEvent.Err("open failed: " + ex.Message), ct); }
                        await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                        break;
                    }
                    case "new":
                    {
                        openSource = "codex";
                        if (openThreadId is not null) hub.CloseThread(openThreadId);
                        openThreadId = await hub.NewThreadAsync(Str(root, "cwd") ?? defaultWorkspace, OnNote, OnReq, ct, Str(root, "approvalPolicy") ?? "on-request", Str(root, "sandbox") ?? "workspace-write");
                        openedId = openThreadId;
                        await SendJson(ws, send, new { kind = "Opened", threadId = openThreadId, isNew = true, live = true }, ct);
                        await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                        break;
                    }
                    case "send":
                    {
                        var text = Str(root, "text");
                        if (string.IsNullOrWhiteSpace(text)) break;

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
                            if (claudeProc is { HasExited: false })
                            {
                                await SendJson(ws, send, AgentEvent.Err("refused: this Claude turn is still running; wait or interrupt it before sending another."), ct);
                                break;
                            }
                            await SendJson(ws, send, AgentEvent.Stat("turn-start"), ct);
                            try
                            {
                                var aliasResult = await TryResolveAliasesAsync(claudeSid);
                                if (!aliasResult.ok) break;
                                claudeProc = claudeDriver.StartTurn(claudeSid, claudeCwd, text!, async ev =>
                                {
                                    if (ev.Kind == AgentEventKind.SessionStarted) { claudeSid = ev.SessionId; return; } // track id, don't render
                                    await SendJson(ws, send, ev, ct);
                                }, ct, auto ? "bypassPermissions" : "acceptEdits", aliasResult.aliases);
                            }
                            catch (InvalidOperationException ex)
                            {
                                await SendJson(ws, send, AgentEvent.Err("refused: " + ex.Message), ct);
                                await SendJson(ws, send, AgentEvent.Stat("idle"), ct);
                            }
                        }
                        else if (openThreadId is not null)
                        {
                            var tid = openThreadId;
                            var policy = auto ? "never" : null; // never = autonomous (owner-signed); else inherit session policy (approvals)
                            var aliasResult = await TryResolveAliasesAsync(tid);
                            if (!aliasResult.ok) break;
                            // fire-and-forget so the receive loop stays free for interrupt; surface failures.
                            _ = Task.Run(async () =>
                            {
                                try { await hub.StartTurnAsync(tid, text!, ct, policy, aliasResult.aliases); }
                                catch (Exception ex) { await SendJson(ws, send, AgentEvent.Err("send failed: " + ex.Message), ct); }
                            }, ct);
                        }
                        break;
                    }
                    case "interrupt":
                        if (openSource == "claude") { try { claudeProc?.Kill(true); } catch { } await SendJson(ws, send, AgentEvent.Stat("idle"), ct); }
                        else if (openThreadId is not null) await hub.InterruptAsync(openThreadId, ct);
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
            if (openThreadId is not null) hub.CloseThread(openThreadId);
            try { if (claudeProc is { HasExited: false }) claudeProc.Kill(true); } catch { }
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
