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

    private async Task CreateCheckpointAsync(ArchiveSession session)
    {
        SyncStatus.Text = $"Creating checkpoint from \"{Trim(session.DisplayTitle, 40)}\"...";
        var result = await _archive.CreateTemplateSnapshotAsync(session);
        SyncStatus.Text = result.Message;
        RenderCurrent();
    }

    private async Task StartFromTemplateAsync(
        TemplateSnapshot template,
        string chatName,
        string phrase,
        string collectionName,
        string? deckId)
    {
        var result = await _archive.SpawnTemplateAsync(template);
        if (!result.Ok || result.Branch is null) { SyncStatus.Text = result.Message; return; }
        var branch = result.Branch;

        var name = (chatName ?? "").Trim();
        if (name.Length > 0)
            await _archive.RenameSessionAsync(branch, name);
        else
            await _archive.RenameSessionAsync(branch, template.SourceTitle);

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
        ResumeInTerminal(branch);
        var filed = string.IsNullOrWhiteSpace(collectionName) ? "" : $" (filed in \"{collectionName}\")";
        SyncStatus.Text = $"Started a new chat from checkpoint \"{template.DisplayName}\"{filed}.";
    }

    private async Task ManageCheckpointsAsync(ArchiveSession session)
    {
        while (true)
        {
            var snapshots = _archive.TemplateSnapshotsForSource(session.Id);
            var list = new ListView
            {
                MinWidth = 520,
                MaxHeight = 360,
                SelectionMode = ListViewSelectionMode.Single
            };
            foreach (var snapshot in snapshots)
            {
                var created = DateTime.TryParse(snapshot.CreatedAt, out var parsed)
                    ? parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
                    : snapshot.CreatedAt;
                list.Items.Add(new ListViewItem
                {
                    Tag = snapshot,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock { Text = snapshot.DisplayName, FontWeight = FontWeights.SemiBold },
                            new TextBlock
                            {
                                Text = $"{created} · source: {snapshot.SourceTitle}",
                                Foreground = MutedBrush(),
                                FontSize = 12
                            }
                        }
                    }
                });
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;

            var action = "";
            var spawn = new Button { Content = "Spawn chat", IsEnabled = list.Items.Count > 0 };
            var rename = new Button { Content = "Rename", IsEnabled = list.Items.Count > 0 };
            var delete = new Button { Content = "Delete", IsEnabled = list.Items.Count > 0 };
            var actions = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children = { spawn, rename, delete }
            };
            var content = new StackPanel { Spacing = 12, Children = { list, actions } };
            if (snapshots.Count == 0)
                content.Children.Insert(0, new TextBlock
                {
                    Text = "No checkpoints have been taken from this chat.",
                    Foreground = MutedBrush()
                });

            var dialog = new ContentDialog
            {
                Title = $"Checkpoints for \"{Trim(session.DisplayTitle, 48)}\"",
                Content = content,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            spawn.Click += (_, _) => { action = "spawn"; dialog.Hide(); };
            rename.Click += (_, _) => { action = "rename"; dialog.Hide(); };
            delete.Click += (_, _) => { action = "delete"; dialog.Hide(); };
            await dialog.ShowAsync();
            if (action.Length == 0) return;
            if ((list.SelectedItem as ListViewItem)?.Tag is not TemplateSnapshot selected) continue;

            if (action == "spawn")
            {
                await StartFromTemplateAsync(selected, "", "", "", null);
                return;
            }
            if (action == "rename")
            {
                var input = new TextBox
                {
                    Text = selected.DisplayName,
                    MinWidth = 420,
                    CornerRadius = ControlCornerRadius()
                };
                var renameDialog = new ContentDialog
                {
                    Title = "Rename checkpoint",
                    Content = input,
                    PrimaryButtonText = "Save",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await renameDialog.ShowAsync() == ContentDialogResult.Primary)
                    await _archive.RenameTemplateSnapshotAsync(selected.Id, input.Text);
                continue;
            }

            var confirm = new ContentDialog
            {
                Title = "Delete checkpoint?",
                Content = $"\"{selected.DisplayName}\" will no longer be available as a starting point.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
                await _archive.DeleteTemplateSnapshotAsync(selected.Id);
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
