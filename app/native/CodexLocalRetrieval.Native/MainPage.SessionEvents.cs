using System.IO;
using System.Linq;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    private static void RecordSessionEvent(
        ArchiveSession? session,
        string kind,
        string summary,
        string severity = "info",
        string source = "native",
        IReadOnlyDictionary<string, string>? details = null)
    {
        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (details is not null)
        {
            foreach (var kv in details)
                merged[kv.Key] = kv.Value;
        }

        var sessionIds = session is null
            ? Array.Empty<string>()
            : new[] { session.Id }.Concat(session.Aliases).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        var ev = SessionEventLedger.Create(
            kind,
            summary,
            session?.Id,
            session?.Tool,
            session?.DisplayTitle,
            WorkspaceLabel(session?.Workspace),
            source,
            severity,
            merged,
            sessionIds: sessionIds);
        SessionEventLedger.AppendBestEffortQueued(ev, m => Diag.Log(m));
    }

    private static void RecordAppEvent(
        string kind,
        string summary,
        string severity = "info",
        string source = "native",
        string? tool = null,
        string? workspace = null,
        IReadOnlyDictionary<string, string>? details = null)
    {
        var ev = SessionEventLedger.Create(
            kind,
            summary,
            tool: tool,
            workspace: WorkspaceLabel(workspace),
            source: source,
            severity: severity,
            details: details);
        SessionEventLedger.AppendBestEffortQueued(ev, m => Diag.Log(m));
    }

    private static string WorkspaceLabel(string? workspace)
    {
        workspace = (workspace ?? "").Trim();
        if (workspace.Length == 0) return "";
        try
        {
            var trimmed = workspace.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(trimmed);
            return string.IsNullOrWhiteSpace(name) ? trimmed : name;
        }
        catch { return ""; }
    }
}
