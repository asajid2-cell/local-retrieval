using CodexLocalRetrieval.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    // Branch a chat from the app: clone its exact history into a new, independently-resumable session
    // tagged as a branch and linked to the original, then open the new branch so it's obvious it exists.
    private async Task BranchChatAsync(ArchiveSession session)
    {
        if (session is null) return;
        SyncStatus.Text = $"Branching \"{Trim(session.DisplayTitle, 40)}\"…";
        var result = await _archive.BranchSessionAsync(session);
        SyncStatus.Text = result.Message;
        if (result.Ok && result.Branch is not null)
        {
            RenderCurrent();
            OpenSession(result.Branch);   // show the branch immediately so it never feels like nothing happened
        }
    }

    private void OpenParentOf(ArchiveSession branch)
    {
        if (branch is null || string.IsNullOrWhiteSpace(branch.BranchOfId)) return;
        if (_archive.Store.Sessions.TryGetValue(branch.BranchOfId, out var parent)) OpenSession(parent);
        else SyncStatus.Text = "The original chat isn't in the index.";
    }

    // "⑂ branch" chip for a session row — its tooltip names the original and clicking it opens it, so a
    // branch is never confused with the chat it came from.
    private UIElement? BranchBadge(ArchiveSession session)
    {
        if (session is null || !session.IsBranch) return null;
        var parentTitle = _archive.Store.Sessions.TryGetValue(session.BranchOfId, out var p) ? p.DisplayTitle : session.BranchOfId;
        var btn = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Padding = new Thickness(8, 0, 8, 0),
            MinHeight = 24,
            Content = new TextBlock { Text = "⑂ branch", FontSize = 11, FontWeight = FontWeights.SemiBold }
        };
        ToolTipService.SetToolTip(btn, $"Branch of \"{parentTitle}\" — click to open the original");
        btn.Click += (_, _) => OpenParentOf(session);
        return btn;
    }

    // How many branches point back at this chat (shown on the parent so the relationship is visible both ways).
    private int BranchCountOf(ArchiveSession session)
    {
        if (session is null) return 0;
        return _archive.Store.Sessions.Values.Count(s =>
            !s.Archived && string.Equals(s.BranchOfId, session.Id, StringComparison.OrdinalIgnoreCase));
    }
}
