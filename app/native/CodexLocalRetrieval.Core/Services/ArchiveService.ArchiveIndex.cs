using System.Text.Json;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Services;

public sealed partial class ArchiveService
{
    // The recent-chats archive index the app pushes to the VPS (POST /api/archive-index) so the web
    // terminal can offer "reopen a recent chat" without the relay ever holding an executable command.
    //
    // A narrower reshape of the same pipeline BuildProjectsProjectionJson uses: the SAME resumable
    // gate (CanBuildTrustedResumeLaunch), the SAME recency ordering (OrderedVisibleSessions), the SAME
    // 500-chat cap — but flat (no decks/collections/running) and carrying INTENT ONLY: opaque ids plus
    // a cwd display string. No command, no exe, no transcript path ever crosses to the relay; the PC
    // rebuilds the trusted launch from its own archive when an intent comes back, and any start/open
    // originating from this index must carry an authorized-principal proof.
    public const int ArchiveIndexSchemaVersion = 1;
    public const int ArchiveIndexMaxChats = 500;

    public string BuildArchiveIndexJson()
    {
        var chats = OrderedVisibleSessions(Store.Sessions.Values)
            .Where(CanBuildTrustedResumeLaunch)
            .Take(ArchiveIndexMaxChats)
            .Select(s => new
            {
                id = s.Id,
                title = s.DisplayTitle,
                tool = s.Tool,
                cwd = ArchiveIndexCwd(s),
                workspaceLabel = s.WorkspaceName,
                updatedAt = s.UpdatedAt,
                muxName = MultiplexSessionName(s),
                // Every row is gated on CanBuildTrustedResumeLaunch above, so this is always true — it
                // is emitted so the relay contract stays explicit if the gate ever widens.
                resumable = true,
            })
            .ToList();

        return JsonSerializer.Serialize(new
        {
            schemaVersion = ArchiveIndexSchemaVersion,
            host = Environment.MachineName,
            chats,
        });
    }

    // The directory this chat would actually resume in — routed exactly like BuildResumeLaunch, so the
    // label the web shows matches where the PC would really land. A display string only.
    private static string ArchiveIndexCwd(ArchiveSession session) =>
        string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase)
            ? ResolveClaudeResumeDirectory(session)
            : ResolveWorkingDirectory(session);
}
