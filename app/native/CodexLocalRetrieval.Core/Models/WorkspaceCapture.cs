namespace CodexLocalRetrieval.Core.Models;

public sealed record WorkspaceCaptureTab(
    string Name,
    string GenerationId,
    string SessionId,
    string Tool,
    bool Alive,
    bool IdentityPending,
    bool ShellOnly);
