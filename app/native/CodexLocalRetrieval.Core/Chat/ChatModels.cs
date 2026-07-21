using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexLocalRetrieval.Core.Chat;

// OpenAI-compatible chat message (works for DeepSeek). role = system|user|assistant|tool.
public sealed class ChatMessage
{
    [JsonPropertyName("role")] public string Role { get; set; } = "user";
    [JsonPropertyName("content")] public string? Content { get; set; }
    [JsonPropertyName("tool_calls")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public List<ToolCall>? ToolCalls { get; set; }
    [JsonPropertyName("tool_call_id")][JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ToolCallId { get; set; }

    public static ChatMessage System(string text) => new() { Role = "system", Content = text };
    public static ChatMessage User(string text) => new() { Role = "user", Content = text };
    public static ChatMessage Tool(string toolCallId, string json) => new() { Role = "tool", ToolCallId = toolCallId, Content = json };
}

public sealed class ToolCall
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "function";
    [JsonPropertyName("function")] public ToolCallFunction Function { get; set; } = new();
}

public sealed class ToolCallFunction
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("arguments")] public string Arguments { get; set; } = "{}"; // JSON string per the API
}

// The schema half of a tool — what the model is told it can call.
public sealed record ChatToolSpec(string Name, string Description, object ParametersSchema);

// A registered tool: its schema + whether it mutates (needs confirmation) + how to run it.
// Execute receives the validated arguments and returns a structured result (serialized to JSON for
// the model). Tool results are DATA, never instructions.
public sealed class ChatTool
{
    public required ChatToolSpec Spec { get; init; }
    public bool IsMutation { get; init; }
    public required Func<JsonElement, CancellationToken, Task<object>> Execute { get; init; }
    public string Name => Spec.Name;
}

// What the backend returns for one completion: the assistant message (possibly with tool_calls).
public sealed class BackendReply
{
    public ChatMessage Message { get; init; } = new() { Role = "assistant" };
    public string? FinishReason { get; init; } // stop | tool_calls | length | ...
}

// One backend (DeepSeek/OpenAI-compatible, later Claude). Owns only the HTTP round-trip, not the loop.
public interface IChatBackend
{
    string Name { get; }
    bool SupportsTools { get; }
    Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatToolSpec> tools, CancellationToken cancellationToken);
}

// The visible outcome of a run: the final answer plus the tool activity (for UI chips) and any error.
public sealed class ChatRunResult
{
    public string Answer { get; set; } = "";
    public List<ToolActivity> Activity { get; } = new();
    public string? Error { get; set; }
    public bool Stopped { get; set; } // hit a guardrail
}

public sealed record ToolActivity(string Tool, string ArgumentsJson, string ResultSummary, bool Ok);
