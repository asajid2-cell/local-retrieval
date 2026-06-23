using System.Text.Json;

namespace CodexLocalRetrieval.Core.Agents;

// Parses a codex rollout `.jsonl` (the on-disk session log) into our AgentEvent model, so stored
// sessions render as real conversations identical to live ones. Each line is {timestamp, type,
// payload}: event_msg/{user_message,agent_message} carry the clean turn text; response_item/
// {function_call, function_call_output} carry shell commands + their output (correlated by call_id);
// reasoning is encrypted on disk, so it's omitted from history (live reasoning streams in plaintext).
public static class RolloutToEvents
{
    public static List<AgentEvent> Parse(string path, int maxEvents = 500, int maxOutputChars = 6000)
    {
        var events = new List<AgentEvent>();
        if (!File.Exists(path)) return events;

        // FileShare.ReadWrite because codex usually has the active session's rollout open for writing.
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var sr = new StreamReader(fs);
        string? line;
        while ((line = sr.ReadLine()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonElement root;
            try { using var d = JsonDocument.Parse(line); root = d.RootElement.Clone(); }
            catch { continue; }
            if (!root.TryGetProperty("payload", out var pay) || pay.ValueKind != JsonValueKind.Object) continue;
            var type = Str(root, "type");
            var pt = Str(pay, "type");

            if (type == "event_msg")
            {
                if (pt == "user_message" && Str(pay, "message") is { Length: > 0 } um)
                    events.Add(new AgentEvent { Kind = AgentEventKind.UserMessage, Text = um });
                else if (pt == "agent_message" && Str(pay, "message") is { Length: > 0 } am)
                    events.Add(new AgentEvent { Kind = AgentEventKind.AssistantText, Text = am });
            }
            else if (type == "response_item")
            {
                switch (pt)
                {
                    case "function_call":
                    case "custom_tool_call":
                        events.Add(new AgentEvent
                        {
                            Kind = AgentEventKind.ToolCall,
                            ItemId = Str(pay, "call_id") ?? Str(pay, "id"),
                            ToolName = "command",
                            ToolInput = ExtractCommand(Str(pay, "arguments") ?? Str(pay, "input")),
                            State = "completed"
                        });
                        break;
                    case "function_call_output":
                    case "custom_tool_call_output":
                        events.Add(new AgentEvent
                        {
                            Kind = AgentEventKind.ToolOutput,
                            ItemId = Str(pay, "call_id"),
                            Output = Cap(ExtractOutput(pay), maxOutputChars),
                            State = "completed"
                        });
                        break;
                }
            }
        }

        if (events.Count > maxEvents) events.RemoveRange(0, events.Count - maxEvents);
        return events;
    }

    private static string ExtractCommand(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "";
        try
        {
            using var d = JsonDocument.Parse(argsJson);
            var root = d.RootElement;
            // codex shell tools use "cmd" or "command"; either a string or an argv array.
            foreach (var key in new[] { "cmd", "command" })
                if (root.TryGetProperty(key, out var c))
                {
                    if (c.ValueKind == JsonValueKind.String) return c.GetString() ?? "";
                    if (c.ValueKind == JsonValueKind.Array)
                        return string.Join(" ", c.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()));
                }
            return argsJson;
        }
        catch { return argsJson; }
    }

    private static string ExtractOutput(JsonElement pay)
    {
        if (pay.TryGetProperty("output", out var o))
        {
            if (o.ValueKind == JsonValueKind.String) return o.GetString() ?? "";
            // output can be an object {output, metadata} or content array
            if (o.ValueKind == JsonValueKind.Object && o.TryGetProperty("output", out var inner) && inner.ValueKind == JsonValueKind.String)
                return inner.GetString() ?? "";
            return o.GetRawText();
        }
        return "";
    }

    private static string? Str(JsonElement e, string p) => e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n] + "\n…(truncated)";
}
