using System.Threading.Channels;

namespace CodexLocalRetrieval.Core.Agents;

// A live agent session (codex or claude) in a workspace. One interface, two adapters, so the server +
// UI are agent-agnostic. Events flow out on a Channel; user turns / interrupts flow in via methods.
public interface IAgentSession : IAsyncDisposable
{
    string Agent { get; }                          // "codex" | "claude"
    string? SessionId { get; }                     // the resumable id, known after the first turn
    string Workspace { get; }
    bool Busy { get; }                             // a turn is currently running
    ChannelReader<AgentEvent> Events { get; }      // normalized event stream to forward to the browser

    Task SendUserAsync(string text, CancellationToken ct = default); // run one user turn
    void Interrupt();                              // stop the current turn
}
