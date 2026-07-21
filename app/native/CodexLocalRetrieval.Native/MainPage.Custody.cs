using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    private int _custodySeq;

    private void RenderCustodyPage()
    {
        var seq = ++_custodySeq;
        ScreenLabel.Text = "Session custody";
        TitleText.Text = "Custody";
        MainContent.Children.Clear();

        var header = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8,
            Margin = new Thickness(0, 0, 0, 12)
        };
        var title = new StackPanel { Spacing = 3 };
        title.Children.Add(new TextBlock
        {
            Text = "Launch safety and recovery",
            Foreground = StrongBrush(),
            FontSize = 20,
            FontWeight = FontWeights.SemiBold
        });
        title.Children.Add(new TextBlock
        {
            Text = "A snapshot of live owners, launch reservations, mux claims, missing sources, pending filings, and recent refused operations.",
            Foreground = MutedBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });
        header.Children.Add(title);

        var running = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Running" };
        running.Click += (_, _) => Navigate("Running");
        Grid.SetColumn(running, 1);
        header.Children.Add(running);

        var refresh = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Refresh" };
        refresh.Click += (_, _) => RenderCustodyPage();
        Grid.SetColumn(refresh, 2);
        header.Children.Add(refresh);
        MainContent.Children.Add(header);

        var loading = new TextBlock { Text = "Checking custody...", Foreground = MutedBrush(), FontSize = 13 };
        MainContent.Children.Add(loading);
        _ = LoadCustodyAsync(seq, loading);
    }

    private async Task LoadCustodyAsync(int seq, TextBlock loading)
    {
        CustodyOverview overview;
        try
        {
            var snapshot = SnapshotStoreForCustody();
            overview = await Task.Run(() => SessionCustody.BuildOverview(snapshot));
        }
        catch (Exception ex)
        {
            Diag.Log("Custody overview failed: " + ex);
            overview = new CustodyOverview
            {
                Severity = "danger",
                Headline = "Custody checks failed; unsafe launch actions must stay blocked.",
                GeneratedAt = DateTimeOffset.UtcNow.ToString("O"),
                LiveVerified = false,
                LiveStatus = "failed",
                Checks = new[] { new CustodyOverviewCheck("Custody check", "danger", "Could not build the custody snapshot.") }
            };
        }

        if (seq != _custodySeq || _screen != "Custody") return;
        MainContent.Children.Remove(loading);
        MainContent.Children.Add(CustodySummaryCard(overview));

        var metrics = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition(),
                new ColumnDefinition(),
                new ColumnDefinition()
            },
            ColumnSpacing = 8
        };
        metrics.Children.Add(CustodyMetric("Risks", overview.DangerCount.ToString(), "danger"));
        metrics.Children.Add(CustodyMetric("Warnings", overview.WarnCount.ToString(), overview.WarnCount > 0 ? "warn" : "ok"));
        metrics.Children.Add(CustodyMetric("Live scan", overview.LiveVerified ? "verified" : "blocked", overview.LiveVerified ? "ok" : "danger"));
        metrics.Children.Add(CustodyMetric("Sessions", overview.TotalSessions.ToString(), "ok"));
        for (var i = 0; i < metrics.Children.Count; i++)
            if (metrics.Children[i] is FrameworkElement child) Grid.SetColumn(child, i);
        MainContent.Children.Add(metrics);

        MainContent.Children.Add(CustodyChecksPanel(overview));

        MainContent.Children.Add(SectionLabel("Action queue (" + overview.Items.Count + ")"));
        if (overview.Items.Count == 0)
        {
            MainContent.Children.Add(new TextBlock
            {
                Text = overview.LiveVerified ? "No custody risks in this snapshot." : "No per-chat risks shown because live-owner verification is unavailable.",
                Foreground = MutedBrush(),
                FontSize = 12
            });
            return;
        }

        foreach (var item in overview.Items)
            MainContent.Children.Add(CustodyItemRow(item));
    }

    private AppStoreData SnapshotStoreForCustody()
    {
        var store = _archive.Store;
        return new AppStoreData
        {
            Sessions = new Dictionary<string, ArchiveSession>(store.Sessions, StringComparer.OrdinalIgnoreCase),
            Settings = store.Settings,
            Collections = new Dictionary<string, ArchiveCollection>(store.Collections, StringComparer.OrdinalIgnoreCase),
            Decks = new List<Deck>(store.Decks),
            DeletedCollections = new List<DeletedCollection>(store.DeletedCollections),
            FileStamps = new Dictionary<string, string>(store.FileStamps, StringComparer.OrdinalIgnoreCase),
            TagColors = new Dictionary<string, string>(store.TagColors, StringComparer.OrdinalIgnoreCase),
            TagLayers = new Dictionary<string, int>(store.TagLayers, StringComparer.OrdinalIgnoreCase),
            PendingNewChats = new List<PendingNewChat>(store.PendingNewChats),
            MuxTabHistory = new Dictionary<string, MuxTabRecord>(store.MuxTabHistory, StringComparer.OrdinalIgnoreCase),
            MuxTabMeta = new Dictionary<string, MuxTabMeta>(store.MuxTabMeta, StringComparer.OrdinalIgnoreCase)
        };
    }

    private Border CustodySummaryCard(CustodyOverview overview)
    {
        var stack = new StackPanel { Spacing = 8 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(IntegrityDot(overview.Severity));
        row.Children.Add(new TextBlock
        {
            Text = IntegrityLabel(overview.Severity),
            Foreground = IntegrityBrush(overview.Severity),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = overview.LiveStatus,
            Foreground = MutedBrush(),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center
        });
        stack.Children.Add(row);
        stack.Children.Add(new TextBlock
        {
            Text = overview.Headline,
            Foreground = StrongBrush(),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = "Snapshot " + FormatTime(overview.GeneratedAt),
            Foreground = MutedBrush(),
            FontSize = 11
        });
        return new Border
        {
            Background = IntegrityBackground(overview.Severity),
            BorderBrush = IntegrityBorderBrush(overview.Severity),
            BorderThickness = new Thickness(1),
            CornerRadius = PanelCornerRadius(),
            Padding = new Thickness(14, 12, 14, 12),
            Child = stack
        };
    }

    private Border CustodyMetric(string label, string value, string severity)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush(), FontSize = 11 });
        stack.Children.Add(new TextBlock { Text = value, Foreground = IntegrityBrush(severity), FontSize = 20, FontWeight = FontWeights.SemiBold });
        return new Border
        {
            Background = PanelBrush(),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = PanelCornerRadius(),
            Padding = new Thickness(12, 9, 12, 9),
            Child = stack
        };
    }

    private Border CustodyChecksPanel(CustodyOverview overview)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = "Signals", Foreground = StrongBrush(), FontSize = 14, FontWeight = FontWeights.SemiBold });
        foreach (var check in overview.Checks)
        {
            var row = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition()
                },
                ColumnSpacing = 8
            };
            row.Children.Add(IntegrityDot(check.Severity));
            var text = new StackPanel { Spacing = 1 };
            text.Children.Add(new TextBlock { Text = check.Name, Foreground = StrongBrush(), FontSize = 12, FontWeight = FontWeights.SemiBold });
            text.Children.Add(new TextBlock { Text = check.Summary, Foreground = MutedBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            stack.Children.Add(row);
        }
        return RowCard(stack);
    }

    private Border CustodyItemRow(CustodyOverviewItem item)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 12
        };
        var left = new StackPanel { Spacing = 5 };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        titleRow.Children.Add(IntegrityDot(item.Severity));
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Tool.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "CL" : "CX",
            Foreground = MutedBrush(),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        titleRow.Children.Add(new TextBlock
        {
            Text = item.Title,
            Foreground = StrongBrush(),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });
        left.Children.Add(titleRow);
        left.Children.Add(new TextBlock { Text = item.Reason, Foreground = StrongBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        left.Children.Add(new TextBlock { Text = item.NextAction, Foreground = MutedBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var signal in item.Signals.Take(5))
            chips.Children.Add(IntegrityChip(signal));
        if (!string.IsNullOrWhiteSpace(item.Workspace))
            chips.Children.Add(IntegrityChip(item.Workspace));
        left.Children.Add(chips);
        grid.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(SmallAction("Open", "Open this chat in the archive reader", () =>
        {
            if (_archive.Store.Sessions.TryGetValue(item.SessionId, out var session)) OpenSession(session);
            return Task.CompletedTask;
        }));
        actions.Children.Add(SmallAction("Running", "Open the live process and mux session view", () =>
        {
            Navigate("Running");
            return Task.CompletedTask;
        }));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return RowCard(grid);
    }
}
