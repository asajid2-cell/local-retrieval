using System.Text.Json;

namespace CodexLocalRetrieval.Core.Chat;

// Owns the multi-turn tool loop (kept out of the HTTP backend). Sends messages + tool schemas to the
// backend; when the assistant returns tool_calls, it validates and runs each, feeds the results back
// as role:tool messages, and repeats until a final answer or a guardrail trips. Read-only tools run
// directly; mutations route through an optional confirmation gate (added with the first write tool).
public sealed class ChatOrchestrator
{
    public const int MaxToolRounds = 6;
    public const int MaxToolCallsTotal = 20;
    public const int MaxToolOutputChars = 16_000;
    public const int MaxSameCallRepeats = 2;

    private readonly IChatBackend _backend;
    private readonly Dictionary<string, ChatTool> _tools;

    // Returns true to proceed with a mutation, false to refuse. Null => mutations are not allowed.
    private readonly Func<ChatTool, JsonElement, Task<bool>>? _confirm;

    public ChatOrchestrator(IChatBackend backend, IEnumerable<ChatTool> tools, Func<ChatTool, JsonElement, Task<bool>>? confirm = null)
    {
        _backend = backend;
        _tools = tools.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        _confirm = confirm;
    }

    public IReadOnlyList<ChatToolSpec> ToolSpecs => _tools.Values.Select(t => t.Spec).ToList();

    public async Task<ChatRunResult> RunAsync(List<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        var result = new ChatRunResult();
        var specs = _backend.SupportsTools ? ToolSpecs : Array.Empty<ChatToolSpec>();
        var totalCalls = 0;
        var callSignatureCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var perToolCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        for (var round = 0; round < MaxToolRounds; round++)
        {
            BackendReply reply;
            try { reply = await _backend.CompleteAsync(messages, specs, cancellationToken); }
            catch (Exception ex) { result.Error = ex.Message; return result; }

            var assistant = reply.Message;
            messages.Add(assistant);

            if (assistant.ToolCalls is null || assistant.ToolCalls.Count == 0)
            {
                result.Answer = assistant.Content ?? "";
                return result;
            }

            foreach (var call in assistant.ToolCalls)
            {
                if (++totalCalls > MaxToolCallsTotal)
                {
                    result.Stopped = true;
                    result.Error = "Too many tool calls in one turn.";
                    return result;
                }

                // Per-tool quota for side-effecting tools (a model shouldn't open 20 windows or
                // launch many terminals in one message).
                perToolCounts.TryGetValue(call.Function.Name, out var toolUsed);
                perToolCounts[call.Function.Name] = toolUsed + 1;
                if (SideEffectLimit(call.Function.Name) is int limit && toolUsed + 1 > limit)
                {
                    result.Activity.Add(new ToolActivity(call.Function.Name, call.Function.Arguments, "rate limited", false));
                    messages.Add(ChatMessage.Tool(call.Id, Err($"'{call.Function.Name}' may run at most {limit} time(s) per message. Ask the user.")));
                    continue;
                }

                // Repeat guard keyed on canonicalized arguments (so whitespace/order can't bypass it).
                var signature = call.Function.Name + "|" + Canonicalize(call.Function.Arguments);
                callSignatureCounts.TryGetValue(signature, out var seen);
                callSignatureCounts[signature] = seen + 1;
                if (seen + 1 > MaxSameCallRepeats)
                {
                    result.Stopped = true;
                    result.Error = $"The assistant repeated the same '{call.Function.Name}' call too many times.";
                    return result;
                }

                var (ok, summary, json) = await ExecuteToolAsync(call, cancellationToken);
                result.Activity.Add(new ToolActivity(call.Function.Name, call.Function.Arguments, summary, ok));
                messages.Add(ChatMessage.Tool(call.Id, Cap(json, MaxToolOutputChars)));
            }
        }

        result.Stopped = true;
        result.Error = "Reached the tool-round limit before a final answer.";
        return result;
    }

    private async Task<(bool ok, string summary, string json)> ExecuteToolAsync(ToolCall call, CancellationToken cancellationToken)
    {
        var name = call.Function.Name;
        if (!_tools.TryGetValue(name, out var tool))
            return (false, "unknown tool", Err($"Unknown tool '{name}'. Use only the provided tools."));

        JsonElement args;
        try
        {
            var raw = string.IsNullOrWhiteSpace(call.Function.Arguments) ? "{}" : call.Function.Arguments;
            args = JsonSerializer.Deserialize<JsonElement>(raw);
        }
        catch (Exception ex)
        {
            return (false, "invalid arguments", Err("Arguments were not valid JSON: " + ex.Message));
        }

        if (tool.IsMutation)
        {
            if (_confirm is null) return (false, "not allowed", Err("This action is not available."));
            bool approved;
            try { approved = await _confirm(tool, args); }
            catch { approved = false; }
            if (!approved) return (false, "declined", Err("The user declined this action."));
        }

        try
        {
            var output = await tool.Execute(args, cancellationToken);
            return (true, name, JsonSerializer.Serialize(output));
        }
        catch (Exception ex)
        {
            return (false, "tool error", Err(ex.Message));
        }
    }

    // Side-effecting tools get a per-message quota; read-only tools are unlimited (only the global cap).
    private static int? SideEffectLimit(string name) => name switch
    {
        "resume_chat" => 1,
        "show_chat" => 3,
        _ => null
    };

    private static string Canonicalize(string argsJson)
    {
        try { return JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson)); }
        catch { return argsJson; }
    }

    private static string Err(string message) => JsonSerializer.Serialize(new { error = message });
    private static string Cap(string s, int max) => s.Length <= max ? s : s[..max] + "...(truncated)";
}
