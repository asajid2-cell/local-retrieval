using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Server;

// Bridges one browser WebSocket <-> one live agent session. Client sends {op:start|send|interrupt};
// the agent's normalized events stream back as JSON. One session per connection (P0); the session is
// disposed when the socket closes.
public static class AgentWebSocket
{
    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }, // AgentEventKind as "AssistantText", not a number
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static async Task HandleAsync(WebSocket ws, ArchiveService archive, string codexExe, string defaultWorkspace, CancellationToken ct)
    {
        IAgentSession? session = null;
        Task? forward = null;
        var send = new SemaphoreSlim(1, 1);
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var msg = await ReceiveText(ws, buf, ct);
                if (msg is null) break;
                JsonElement root;
                try { using var doc = JsonDocument.Parse(msg); root = doc.RootElement.Clone(); }
                catch { continue; }
                switch (Str(root, "op"))
                {
                    case "start":
                        if (session is not null) break;
                        var (workspace, resumeId) = ResolveTarget(root, archive, defaultWorkspace);
                        var cs = new CodexAgentSession(codexExe, workspace);
                        if (resumeId is not null) cs.AdoptSession(resumeId);
                        session = cs;
                        forward = ForwardEvents(session, ws, send, ct);
                        await SendJson(ws, send, new { kind = "Ready", agent = "codex", workspace, resume = resumeId }, ct);
                        break;

                    case "send":
                        var text = Str(root, "text");
                        if (session is not null && !string.IsNullOrWhiteSpace(text) && !session.Busy)
                            _ = Task.Run(() => session.SendUserAsync(text!, ct), ct);
                        break;

                    case "interrupt":
                        session?.Interrupt();
                        break;
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            if (session is not null) await session.DisposeAsync();
            try { if (forward is not null) await forward; } catch { }
            try { if (ws.State == WebSocketState.Open) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None); } catch { }
        }
    }

    // chatId -> resume that on-disk codex session in its workspace; else a fresh session in {workspace|default}.
    private static (string workspace, string? resumeId) ResolveTarget(JsonElement root, ArchiveService archive, string def)
    {
        var chatId = Str(root, "chatId");
        if (!string.IsNullOrWhiteSpace(chatId))
        {
            var s = archive.GetSession(chatId!);
            if (s is not null && string.Equals(s.Tool, "codex", StringComparison.OrdinalIgnoreCase))
                return (string.IsNullOrWhiteSpace(s.Workspace) ? def : s.Workspace, s.Id);
        }
        var w = Str(root, "workspace");
        return (string.IsNullOrWhiteSpace(w) ? def : w!, null);
    }

    private static async Task ForwardEvents(IAgentSession session, WebSocket ws, SemaphoreSlim send, CancellationToken ct)
    {
        try { await foreach (var ev in session.Events.ReadAllAsync(ct)) await SendJson(ws, send, ev, ct); }
        catch (OperationCanceledException) { }
        catch (Exception) { }
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
