using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Server;

public sealed record SessionOpenResult(bool Ok, string Message);
public sealed record SessionResolutionResult(bool Ok, TrustedSessionLaunch? Session, string Message);

public sealed class CanonicalSessionResolver
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<ArchiveSession>>> _loadSessions;

    public CanonicalSessionResolver(Func<CancellationToken, Task<IReadOnlyList<ArchiveSession>>> loadSessions) =>
        _loadSessions = loadSessions;

    public async Task<SessionResolutionResult> ResolveAsync(string? requestedId, CancellationToken cancellationToken)
    {
        var id = (requestedId ?? "").Trim();
        if (id.Length == 0)
            return new SessionResolutionResult(false, null, "session id is required");

        var sessions = await _loadSessions(cancellationToken);
        var matches = sessions
            .Where(s => MatchesStrongIdentity(s, id))
            .ToList();
        if (matches.Count == 0)
            return new SessionResolutionResult(false, null, "session id is not in this app's archive");
        if (matches.Count != 1)
            return new SessionResolutionResult(false, null, "session identity is ambiguous in this app's archive");

        var session = matches[0];
        if (!TryParseTool(session.Tool, out var tool))
            return new SessionResolutionResult(false, null, "archived session has an unsupported tool");

        var canonicalId = (session.Id ?? "").Trim();
        if (canonicalId.Length == 0)
            return new SessionResolutionResult(false, null, "archived session has no canonical id");
        if (!CodexLocalRetrieval.Core.Services.ArchiveService.IsResumableId(canonicalId))
            return new SessionResolutionResult(false, null, "archived session has an invalid canonical id");

        var workspace = (session.Workspace ?? "").Trim();
        if (!Path.IsPathRooted(workspace) || !Directory.Exists(workspace))
            return new SessionResolutionResult(false, null, "archived session workspace is missing or unavailable");
        workspace = Path.GetFullPath(workspace);

        var aliases = new[] { canonicalId }
            .Concat(session.Aliases ?? new())
            .Select(alias => (alias ?? "").Trim())
            .Where(alias => alias.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new SessionResolutionResult(
            true,
            new TrustedSessionLaunch(
                canonicalId,
                tool,
                workspace,
                aliases,
                CodexLocalRetrieval.Core.Services.ArchiveService.NormalizeLaunchMode(session.LaunchMode))
            {
                SourcePath = string.IsNullOrWhiteSpace(session.SourcePath) || !Path.IsPathRooted(session.SourcePath)
                    ? null
                    : Path.GetFullPath(session.SourcePath),
            },
            "ok");
    }

    private static bool MatchesStrongIdentity(ArchiveSession session, string id) =>
        string.Equals((session.Id ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase)
        || (session.Aliases ?? new()).Any(alias =>
            string.Equals((alias ?? "").Trim(), id, StringComparison.OrdinalIgnoreCase));

    private static bool TryParseTool(string? value, out SessionTool tool)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "codex":
                tool = SessionTool.Codex;
                return true;
            case "claude":
                tool = SessionTool.Claude;
                return true;
            default:
                tool = default;
                return false;
        }
    }
}

public sealed class SessionOpenService
{
    private readonly CanonicalSessionResolver _resolver;
    private readonly SessionLauncher _launcher;

    public SessionOpenService(CanonicalSessionResolver resolver, SessionLauncher launcher)
    {
        _resolver = resolver;
        _launcher = launcher;
    }

    public async Task<SessionOpenResult> OpenAsync(
        string? requestedId,
        OpenRequest? request,
        CancellationToken cancellationToken)
    {
        if (!TryParseTarget(request?.Target, out var target))
            return new SessionOpenResult(false, "target must be terminal or vscode");

        var resolution = await _resolver.ResolveAsync(requestedId, cancellationToken);
        if (!resolution.Ok || resolution.Session is null)
            return new SessionOpenResult(false, resolution.Message);

        var (ok, message) = _launcher.Open(resolution.Session, target);
        return new SessionOpenResult(ok, message);
    }

    private static bool TryParseTarget(string? value, out SessionOpenTarget target)
    {
        switch (string.IsNullOrWhiteSpace(value) ? "terminal" : value.Trim().ToLowerInvariant())
        {
            case "terminal":
                target = SessionOpenTarget.Terminal;
                return true;
            case "vscode":
                target = SessionOpenTarget.VsCode;
                return true;
            default:
                target = default;
                return false;
        }
    }
}
