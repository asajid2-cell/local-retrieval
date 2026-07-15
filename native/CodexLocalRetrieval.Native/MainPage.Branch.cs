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

    // Start a fresh chat FROM a template: branch the template (so the new chat begins with its full
    // context), apply the new chat's name / phrase / collection, then resume it in a terminal so the agent
    // opens already carrying that context. Can be done any number of times, whenever.
    private async Task StartFromTemplateAsync(ArchiveSession template, string chatName, string phrase, string collectionName, string? deckId)
    {
        var result = await _archive.BranchSessionAsync(template);
        if (!result.Ok || result.Branch is null) { SyncStatus.Text = result.Message; return; }
        var branch = result.Branch;

        var name = (chatName ?? "").Trim();
        if (name.Length > 0)
            await _archive.RenameSessionAsync(branch, name);
        else
            await _archive.RenameSessionAsync(branch, template.DisplayTitle);   // no "(branch)" suffix for a template spawn

        var ph = (phrase ?? "").Trim();
        if (ph.Length > 0)
        {
            var list = branch.SpecialPhrases.ToList();
            list.Add(ph);
            await _archive.SetSpecialPhrasesAsync(branch, list);
        }
        if (!string.IsNullOrWhiteSpace(collectionName))
            await _archive.AddToCollectionAsync(branch, collectionName, deckId);

        RenderCurrent();
        ResumeInTerminal(branch);   // open the agent with the template's context loaded
        var filed = string.IsNullOrWhiteSpace(collectionName) ? "" : $" (filed in \"{collectionName}\")";
        SyncStatus.Text = $"Started a new chat from template \"{template.DisplayTitle}\"{filed}.";
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
        return BranchesOf(session).Count;
    }

    private List<ArchiveSession> BranchesOf(ArchiveSession session) =>
        _archive.Store.Sessions.Values
            .Where(s => !s.Archived && string.Equals(s.BranchOfId, session.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.UpdatedAt, StringComparer.Ordinal)
            .ToList();

    // Integrity-panel block shown when the open chat IS a branch: names its original and links to it.
    private UIElement BranchLinkBlock(ArchiveSession branch)
    {
        var parentTitle = _archive.Store.Sessions.TryGetValue(branch.BranchOfId, out var p) ? p.DisplayTitle : branch.BranchOfId;
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = "Branch", Foreground = StrongBrush(), FontSize = 12, FontWeight = FontWeights.SemiBold });
        var link = new HyperlinkButton { Content = $"⑂ Branch of \"{parentTitle}\" — open the original", FontSize = 12, Padding = new Thickness(0) };
        link.Click += (_, _) => OpenParentOf(branch);
        panel.Children.Add(link);
        return panel;
    }

    // Integrity-panel block shown on a parent: lists the branches taken off it, each opening on click.
    private UIElement BranchesOfBlock(IReadOnlyList<ArchiveSession> branches)
    {
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock { Text = $"Branches ({branches.Count})", Foreground = StrongBrush(), FontSize = 12, FontWeight = FontWeights.SemiBold });
        foreach (var b in branches.Take(6))
        {
            var captured = b;
            var link = new HyperlinkButton { Content = "⑂ " + b.DisplayTitle, FontSize = 12, Padding = new Thickness(0) };
            link.Click += (_, _) => OpenSession(captured);
            panel.Children.Add(link);
        }
        return panel;
    }
}
