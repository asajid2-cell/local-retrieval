using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    private async Task ForkChatAsync(ArchiveSession session, string launchMode)
    {
        if (session is null) return;
        var modeLabel = ArchiveService.NormalizeLaunchMode(launchMode) == ArchiveService.NativeLaunchMode
            ? ""
            : $" with {ArchiveService.LaunchModeLabel(launchMode)} marker";
        SyncStatus.Text = $"Forking \"{Trim(session.DisplayTitle, 40)}\"{modeLabel}...";
        var result = await _archive.ForkSessionAsync(session, launchMode);
        SyncStatus.Text = result.Message;
        RenderIntegrity(force: true);
        if (result.Ok && result.Branch is not null)
        {
            RenderCurrent();
            OpenSession(result.Branch);
        }
    }

    private MenuFlyoutSubItem ForkAsMenu(ArchiveSession session)
    {
        var menu = new MenuFlyoutSubItem { Text = "Fork as..." };

        var native = new MenuFlyoutItem { Text = "Native branch" };
        native.Click += async (_, _) => await ForkChatAsync(session, ArchiveService.NativeLaunchMode);
        menu.Items.Add(native);
        return menu;
    }

    private UIElement? GatewayBadge(ArchiveSession session)
    {
        if (!session.IsGatewayBranch) return null;
        var badge = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(45, 45, 190, 160)),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = session.GatewayGlyph,
                Foreground = StrongBrush(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            }
        };
        ToolTipService.SetToolTip(
            badge,
            "Gateway branch. Use Resume as Gateway to launch it through the cc build.");
        return badge;
    }

    private async Task BranchChatAsync(ArchiveSession session)
    {
        if (session is null) return;
        SyncStatus.Text = $"Branching \"{Trim(session.DisplayTitle, 40)}\"...";
        var result = await _archive.BranchSessionAsync(session);
        SyncStatus.Text = result.Message;
        RenderIntegrity(force: true);
        if (result.Ok && result.Branch is not null)
        {
            RenderCurrent();
            OpenSession(result.Branch);
        }
    }

    private async Task CreateCheckpointAsync(ArchiveSession session)
    {
        SyncStatus.Text = $"Creating checkpoint from \"{Trim(session.DisplayTitle, 40)}\"...";
        var result = await _archive.CreateTemplateSnapshotAsync(session);
        SyncStatus.Text = result.Message;
        RenderCurrent();
        RenderIntegrity(force: true);
    }

    private async Task StartFromTemplateAsync(
        TemplateSnapshot template,
        string chatName,
        string phrase,
        string collectionName,
        string? deckId)
    {
        var result = await _archive.SpawnTemplateAsync(template);
        if (!result.Ok || result.Branch is null)
        {
            SyncStatus.Text = result.Message;
            return;
        }
        var branch = result.Branch;

        try
        {
            var name = (chatName ?? "").Trim();
            var finalName = name.Length > 0 ? name : template.SourceTitle;
            await _archive.RenameSessionAsync(branch, finalName);
            RecordSessionEvent(
                branch,
                "rename.succeeded",
                $"Renamed spawned chat to \"{finalName}\".",
                details: new Dictionary<string, string> { ["operation"] = "checkpoint.spawn.rename" });

            var ph = (phrase ?? "").Trim();
            if (ph.Length > 0)
            {
                var list = branch.SpecialPhrases.ToList();
                list.Add(ph);
                await _archive.SetSpecialPhrasesAsync(branch, list);
            }
            if (!string.IsNullOrWhiteSpace(collectionName))
            {
                await _archive.AddToCollectionAsync(branch, collectionName, deckId);
                RecordSessionEvent(
                    branch,
                    "collection.file.succeeded",
                    $"Filed spawned chat in \"{collectionName}\".",
                    details: new Dictionary<string, string> { ["operation"] = "checkpoint.spawn.file" });
            }
        }
        catch (Exception ex)
        {
            RecordSessionEvent(
                branch,
                "checkpoint.spawn.configure.failed",
                ex.Message,
                "error",
                details: new Dictionary<string, string>
                {
                    ["operation"] = "checkpoint.spawn.configure",
                    ["checkpointId"] = template.Id
                });
            SyncStatus.Text = "The chat was spawned, but its name or filing failed: " + ex.Message;
            RenderIntegrity(force: true);
            return;
        }

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
            var snapshotIds = snapshots.Select(snapshot => snapshot.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unknownBranches = BranchesOf(session)
                .Where(branch =>
                    string.IsNullOrWhiteSpace(branch.FromSnapshotId)
                    || !snapshotIds.Contains(branch.FromSnapshotId))
                .ToList();
            var list = new ListView
            {
                MinWidth = 720,
                MaxHeight = 500,
                SelectionMode = ListViewSelectionMode.None
            };

            var action = "";
            TemplateSnapshot? selectedSnapshot = null;
            ArchiveSession? selectedBranch = null;
            ContentDialog? dialog = null;

            Button ActionButton(
                string text,
                string requestedAction,
                TemplateSnapshot? snapshot = null,
                ArchiveSession? branch = null)
            {
                var button = new Button
                {
                    Content = text,
                    Style = (Style)Resources["PillButtonStyle"],
                    MinHeight = 30,
                    Padding = new Thickness(10, 3, 10, 3)
                };
                button.Click += (_, _) =>
                {
                    action = requestedAction;
                    selectedSnapshot = snapshot;
                    selectedBranch = branch;
                    dialog?.Hide();
                };
                return button;
            }

            ListViewItem SnapshotRow(TemplateSnapshot snapshot)
            {
                var grid = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    ColumnSpacing = 14
                };
                grid.Children.Add(new StackPanel
                {
                    Spacing = 3,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Checkpoint",
                            Foreground = MutedBrush(),
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = ArchiveService.TemplateSnapshotDisplayLabel(snapshot),
                            FontWeight = FontWeights.SemiBold,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                });
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        ActionButton("View", "view-snapshot", snapshot),
                        ActionButton("Spawn new chat", "spawn", snapshot),
                        ActionButton("Rename", "rename-snapshot", snapshot),
                        ActionButton("Delete", "delete-snapshot", snapshot)
                    }
                };
                Grid.SetColumn(actions, 1);
                grid.Children.Add(actions);
                return new ListViewItem { Content = grid, IsTabStop = false };
            }

            ListViewItem BranchRow(ArchiveSession branch)
            {
                var grid = new Grid
                {
                    Margin = new Thickness(28, 0, 0, 0),
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto }
                    },
                    ColumnSpacing = 14
                };
                grid.Children.Add(new StackPanel
                {
                    Spacing = 2,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Branch",
                            Foreground = MutedBrush(),
                            FontSize = 11,
                            FontWeight = FontWeights.SemiBold
                        },
                        new TextBlock
                        {
                            Text = branch.DisplayTitle,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                });
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 6,
                    VerticalAlignment = VerticalAlignment.Center,
                    Children =
                    {
                        ActionButton("View", "view-branch", branch: branch),
                        ActionButton("Rename", "rename-branch", branch: branch),
                        ActionButton("Delete", "delete-branch", branch: branch)
                    }
                };
                Grid.SetColumn(actions, 1);
                grid.Children.Add(actions);
                return new ListViewItem { Content = grid, IsTabStop = false };
            }

            foreach (var snapshot in snapshots)
            {
                list.Items.Add(SnapshotRow(snapshot));
                foreach (var branch in _archive.BranchesForSnapshot(snapshot.Id))
                    list.Items.Add(BranchRow(branch));
            }

            if (unknownBranches.Count > 0)
            {
                list.Items.Add(new ListViewItem
                {
                    IsTabStop = false,
                    Content = new StackPanel
                    {
                        Spacing = 2,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "Unknown checkpoint",
                                FontWeight = FontWeights.SemiBold
                            },
                            new TextBlock
                            {
                                Text = "Legacy branches and branches whose checkpoint was deleted.",
                                Foreground = MutedBrush(),
                                FontSize = 12
                            }
                        }
                    }
                });
                foreach (var branch in unknownBranches)
                    list.Items.Add(BranchRow(branch));
            }

            var content = new StackPanel { Spacing = 12, Children = { list } };
            if (snapshots.Count == 0 && unknownBranches.Count == 0)
            {
                content.Children.Insert(0, new TextBlock
                {
                    Text = "No checkpoints or branches have been created from this chat.",
                    Foreground = MutedBrush()
                });
            }

            dialog = new ContentDialog
            {
                Title = $"Checkpoints & Branches - \"{Trim(session.DisplayTitle, 48)}\"",
                Content = content,
                CloseButtonText = "Close",
                XamlRoot = XamlRoot
            };
            await dialog.ShowAsync();
            if (action.Length == 0) return;

            if (action == "view-snapshot" && selectedSnapshot is not null)
            {
                await OpenSnapshotAsync(selectedSnapshot);
                return;
            }
            if (action == "view-branch" && selectedBranch is not null)
            {
                OpenSession(selectedBranch);
                return;
            }
            if (action == "spawn" && selectedSnapshot is not null)
            {
                await StartFromTemplateAsync(selectedSnapshot, "", "", "", null);
                return;
            }
            if (action is "rename-snapshot" or "rename-branch")
            {
                var input = new TextBox
                {
                    Text = selectedSnapshot?.DisplayName ?? selectedBranch?.DisplayTitle ?? "",
                    MinWidth = 420,
                    CornerRadius = ControlCornerRadius()
                };
                var renameDialog = new ContentDialog
                {
                    Title = selectedSnapshot is not null ? "Rename checkpoint" : "Rename branch",
                    Content = input,
                    PrimaryButtonText = "Save",
                    CloseButtonText = "Cancel",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                if (await renameDialog.ShowAsync() == ContentDialogResult.Primary)
                {
                    try
                    {
                        if (selectedSnapshot is not null)
                        {
                            var renamed = await _archive.RenameTemplateSnapshotAsync(selectedSnapshot.Id, input.Text);
                            RecordSessionEvent(
                                session,
                                renamed ? "checkpoint.rename.succeeded" : "checkpoint.rename.refused",
                                renamed ? $"Renamed checkpoint to \"{input.Text.Trim()}\"." : "Checkpoint rename made no change.",
                                renamed ? "info" : "warn",
                                details: new Dictionary<string, string> { ["checkpointId"] = selectedSnapshot.Id });
                        }
                        else if (selectedBranch is not null)
                        {
                            await _archive.RenameSessionAsync(selectedBranch, input.Text);
                            RecordSessionEvent(selectedBranch, "rename.succeeded", $"Renamed branch to \"{input.Text.Trim()}\".");
                        }
                    }
                    catch (Exception ex)
                    {
                        RecordSessionEvent(
                            selectedBranch ?? session,
                            selectedSnapshot is not null ? "checkpoint.rename.failed" : "rename.failed",
                            ex.Message,
                            "error",
                            details: selectedSnapshot is null
                                ? null
                                : new Dictionary<string, string> { ["checkpointId"] = selectedSnapshot.Id });
                        SyncStatus.Text = "Rename failed: " + ex.Message;
                    }
                }
                continue;
            }

            var confirm = new ContentDialog
            {
                Title = selectedSnapshot is not null ? "Delete checkpoint?" : "Delete branch?",
                Content = selectedSnapshot is not null
                    ? $"\"{selectedSnapshot.DisplayName}\" will no longer be available as a starting point. Existing branches remain visible under Unknown checkpoint."
                    : $"\"{selectedBranch?.DisplayTitle}\" will be removed from active chat lists. Its transcript file is not deleted.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot
            };
            if (await confirm.ShowAsync() == ContentDialogResult.Primary)
            {
                try
                {
                    if (selectedSnapshot is not null)
                    {
                        var deleted = await _archive.DeleteTemplateSnapshotAsync(selectedSnapshot.Id);
                        RecordSessionEvent(
                            session,
                            deleted ? "checkpoint.delete.succeeded" : "checkpoint.delete.refused",
                            deleted ? $"Deleted checkpoint \"{selectedSnapshot.DisplayName}\"." : "Checkpoint delete made no change.",
                            deleted ? "info" : "warn",
                            details: new Dictionary<string, string> { ["checkpointId"] = selectedSnapshot.Id });
                    }
                    else if (selectedBranch is not null)
                    {
                        await _archive.ArchiveSessionAsync(selectedBranch);
                        RecordSessionEvent(selectedBranch, "archive.succeeded", "Archived branch.");
                    }
                }
                catch (Exception ex)
                {
                    RecordSessionEvent(
                        selectedBranch ?? session,
                        selectedSnapshot is not null ? "checkpoint.delete.failed" : "archive.failed",
                        ex.Message,
                        "error",
                        details: selectedSnapshot is null
                            ? null
                            : new Dictionary<string, string> { ["checkpointId"] = selectedSnapshot.Id });
                    SyncStatus.Text = "Delete failed: " + ex.Message;
                }
                RenderCurrent();
                RenderIntegrity(force: true);
            }
        }
    }

    private async Task OpenSnapshotAsync(TemplateSnapshot snapshot)
    {
        var result = await _archive.OpenTemplateSnapshotAsync(snapshot.Id);
        SyncStatus.Text = result.Message;
        if (!result.Ok || result.Reader is null) return;
        _selected = result.Reader;
        SelectSessionRow(null);
        Navigate("Archive");
    }

    private void OpenParentOf(ArchiveSession branch)
    {
        if (branch is null || string.IsNullOrWhiteSpace(branch.BranchOfId)) return;
        if (_archive.Store.Sessions.TryGetValue(branch.BranchOfId, out var parent))
            OpenSession(parent);
        else
            SyncStatus.Text = "The original chat isn't in the index.";
    }

    // The checkpoint a branch came from, or "" when it was branched off a live chat directly. Named
    // separately because the source chat's title keeps moving and the checkpoint's does not: renaming a
    // chat after taking a checkpoint made every branch report the chat's newest name, which reads as the
    // spawn having used the wrong checkpoint even when the transcript was correct.
    private string CheckpointNameOf(ArchiveSession session)
        => !string.IsNullOrWhiteSpace(session?.FromSnapshotId)
           && _archive.Store.TemplateSnapshots.TryGetValue(session.FromSnapshotId, out var snapshot)
            ? snapshot.DisplayName
            : "";

    private UIElement? BranchBadge(ArchiveSession session)
    {
        if (session is null || !session.IsBranch) return null;
        var parentTitle = _archive.Store.Sessions.TryGetValue(session.BranchOfId, out var parent)
            ? parent.DisplayTitle
            : session.BranchOfId;
        var checkpointName = CheckpointNameOf(session);
        var button = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Padding = new Thickness(8, 0, 8, 0),
            MinHeight = 24,
            Content = new TextBlock
            {
                Text = "\u2442 branch",
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        };
        ToolTipService.SetToolTip(
            button,
            checkpointName.Length > 0
                ? $"Branch of checkpoint \"{checkpointName}\" (from \"{parentTitle}\") - click to view the original"
                : $"Branch of \"{parentTitle}\" - click to view the original");
        button.Click += (_, _) => OpenParentOf(session);
        return button;
    }

    private int BranchCountOf(ArchiveSession session)
    {
        if (session is null) return 0;
        return BranchesOf(session).Count;
    }

    private List<ArchiveSession> BranchesOf(ArchiveSession session) =>
        _archive.Store.Sessions.Values
            .Where(candidate =>
                !candidate.Archived
                && string.Equals(candidate.BranchOfId, session.Id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.UpdatedAt, StringComparer.Ordinal)
            .ToList();

    private UIElement BranchLinkBlock(ArchiveSession branch)
    {
        var parentTitle = _archive.Store.Sessions.TryGetValue(branch.BranchOfId, out var parent)
            ? parent.DisplayTitle
            : branch.BranchOfId;
        var panel = new StackPanel { Spacing = 2, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = "Branch",
            Foreground = StrongBrush(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        var link = new HyperlinkButton
        {
            Content = $"Branch of \"{parentTitle}\" - view original{SnapshotOriginLabel(branch)}",
            FontSize = 12,
            Padding = new Thickness(0)
        };
        link.Click += (_, _) => OpenParentOf(branch);
        panel.Children.Add(link);
        return panel;
    }

    private UIElement BranchesOfBlock(IReadOnlyList<ArchiveSession> branches)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = $"Branches ({branches.Count})",
            Foreground = StrongBrush(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        foreach (var branch in branches.Take(6))
        {
            var captured = branch;
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                },
                ColumnSpacing = 8
            };
            row.Children.Add(new TextBlock
            {
                Text = branch.DisplayTitle + SnapshotOriginLabel(branch),
                FontSize = 12,
                Foreground = MutedBrush(),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            });
            var view = new Button
            {
                Content = "View",
                Style = (Style)Resources["PillButtonStyle"],
                MinHeight = 28,
                Padding = new Thickness(9, 2, 9, 2)
            };
            view.Click += (_, _) => OpenSession(captured);
            Grid.SetColumn(view, 1);
            row.Children.Add(view);
            panel.Children.Add(row);
        }
        return panel;
    }

    private string SnapshotOriginLabel(ArchiveSession branch)
    {
        if (string.IsNullOrWhiteSpace(branch.FromSnapshotId)) return " - checkpoint unknown";
        return _archive.Store.TemplateSnapshots.TryGetValue(branch.FromSnapshotId, out var snapshot)
            ? " - " + ArchiveService.TemplateSnapshotDisplayLabel(snapshot)
            : " - checkpoint unavailable";
    }
}
