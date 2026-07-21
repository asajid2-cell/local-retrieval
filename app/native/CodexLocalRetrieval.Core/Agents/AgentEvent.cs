namespace CodexLocalRetrieval.Core.Agents;

// The unified event model. BOTH live agent adapters (codex/claude) and the stored-rollout parser
// normalize into this, so one renderer shows history and live identically. Serialized to JSON and
// streamed to the browser over the WebSocket; the frontend switches on `kind`.
public enum AgentEventKind
{
    SessionStarted,   // a session/thread began (carries SessionId)
    Status,           // running | idle | turn-start | turn-end (Text = the status)
    UserMessage,      // a user turn (echoed back so history + live render the same)
    AssistantText,    // assistant message text (full or a streamed delta — Delta marks which)
    Thinking,         // reasoning / chain-of-thought (full or delta)
    ToolCall,         // the agent invoked a tool: a shell command, file edit, or skill/MCP call
    ToolOutput,       // output/result for a ToolCall (correlate by ItemId)
    PermissionRequest,// the agent is asking to run something (Approve/Deny) — for app-server later
    TurnResult,       // a turn finished (Usage carries token counts)
    Error,            // something went wrong (Text = message)
    SessionEnded      // the agent process exited
}

// One normalized event. Most fields are null depending on Kind; the wire JSON only carries what's set.
public sealed record AgentEvent
{
    public required AgentEventKind Kind { get; init; }
    public string? Text { get; init; }        // assistant/thinking/user/error/status text
    public bool Delta { get; init; }          // true when Text is a streaming fragment, not the whole
    public string? SessionId { get; init; }   // SessionStarted: the resumable session/thread id
    public string? ItemId { get; init; }      // correlates ToolCall <-> ToolOutput
    public string? ToolName { get; init; }    // ToolCall: "command" | "edit" | skill/MCP name
    public string? ToolInput { get; init; }   // ToolCall: the command line / args / file
    public string? Output { get; init; }      // ToolOutput: aggregated output
    public int? ExitCode { get; init; }       // ToolOutput: process exit code
    public string? State { get; init; }       // in_progress | completed | failed
    public object? Usage { get; init; }       // TurnResult: token usage object

    public static AgentEvent Session(string id) => new() { Kind = AgentEventKind.SessionStarted, SessionId = id };
    public static AgentEvent Assistant(string text, bool delta = false) => new() { Kind = AgentEventKind.AssistantText, Text = text, Delta = delta };
    public static AgentEvent Think(string text, bool delta = false) => new() { Kind = AgentEventKind.Thinking, Text = text, Delta = delta };
    public static AgentEvent Stat(string status) => new() { Kind = AgentEventKind.Status, Text = status };
    public static AgentEvent Err(string message) => new() { Kind = AgentEventKind.Error, Text = message };
    public static AgentEvent Ended() => new() { Kind = AgentEventKind.SessionEnded };
}
