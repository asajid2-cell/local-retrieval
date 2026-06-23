using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexLocalRetrieval.Core.Agents;

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

    public static async Task HandleAsync(WebSocket ws, CodexAgentHub hub, string defaultWorkspace, CancellationToken ct)
    {
        var send = new SemaphoreSlim(1, 1);
        string? openThreadId = null;
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
                        if (openThreadId is not null) hub.CloseThread(openThreadId);
                        try
                        {
                            await hub.OpenThreadAsync(id, Str(root, "cwd"), OnNote, OnReq, ct);
                            openThreadId = id;
                            await SendJson(ws, send, new { kind = "Opened", threadId = id }, ct);
                            foreach (var ev in await hub.ReadHistoryAsync(id, ct)) await SendJson(ws, send, ev, ct);
                        }
                        catch (Exception ex) { await SendJson(ws, send, AgentEvent.Err("open failed: " + ex.Message), ct); }
                        await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                        break;
                    }
                    case "new":
                    {
                        if (openThreadId is not null) hub.CloseThread(openThreadId);
                        openThreadId = await hub.NewThreadAsync(Str(root, "cwd") ?? defaultWorkspace, OnNote, OnReq, ct, Str(root, "approvalPolicy") ?? "on-request", Str(root, "sandbox") ?? "workspace-write");
                        await SendJson(ws, send, new { kind = "Opened", threadId = openThreadId, isNew = true }, ct);
                        await SendJson(ws, send, new { kind = "HistoryEnd" }, ct);
                        break;
                    }
                    case "send":
                    {
                        var text = Str(root, "text");
                        if (openThreadId is not null && !string.IsNullOrWhiteSpace(text))
                        {
                            var tid = openThreadId;
                            // fire-and-forget so the receive loop stays free for interrupt; surface failures.
                            _ = Task.Run(async () =>
                            {
                                try { await hub.StartTurnAsync(tid, text!, ct); }
                                catch (Exception ex) { await SendJson(ws, send, AgentEvent.Err("send failed: " + ex.Message), ct); }
                            }, ct);
                        }
                        break;
                    }
                    case "interrupt":
                        if (openThreadId is not null) await hub.InterruptAsync(openThreadId, ct);
                        break;
                    case "approve":
                        if (root.TryGetProperty("requestId", out var ridEl) && ridEl.TryGetInt32(out var rid))
                            await hub.RespondApprovalAsync(rid, Str(root, "decision") == "allow", ct);
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (openThreadId is not null) hub.CloseThread(openThreadId);
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
