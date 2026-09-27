using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

// The shell around the work: left = navigate, centre = work, right = inspect, top = the current session.
// This file owns which pane shows what, how the three chrome areas fold, and the per-user state that
// remembers it. The screen renderers stay in their own partials.
public sealed partial class MainPage
{
    private const double SidebarWidth = 300;
    private const double InspectorWidth = 320;

    // Shell state is per user, read once at load and written back when it changes.
    private bool _sidebarCollapsed;
    private bool _inspectorOpen;
    private bool _inspectorPinned;   // pinned = stay open even when the window is too narrow for both rails
    private bool _topCompact;
    private string _inspectorTab = "Integrity";
    private bool _toolsPaneOpen;

    // Archive scope: the lifecycle tier. "active" is what has been vetted - the chats worth looking at, so
    // that is what the app opens on; "archived" is retired; "unvetted" is the general populace everything
    // lands in. Separate from the text/tag filters: this chooses the tier, they narrow within it.
    private string _chatScope = "active";

    private static readonly string[] InspectorTabNames = { "Integrity", "Context", "Activity", "Actions" };

    private void InitShell()
    {
        var settings = _archive.Store.Settings;
        _sidebarCollapsed = settings.ShellSidebarCollapsed;
        _inspectorOpen = settings.ShellInspectorOpen;
        _inspectorPinned = settings.ShellInspectorPinned;
        _topCompact = settings.ShellTopCompact;
        _inspectorTab = NormalizeInspectorTab(settings.ShellInspectorTab);
    }

    private void PersistShell()
    {
        if (!_storeLoaded) return;
        var settings = _archive.Store.Settings;
        settings.ShellSidebarCollapsed = _sidebarCollapsed;
        settings.ShellInspectorOpen = _inspectorOpen;
        settings.ShellInspectorPinned = _inspectorPinned;
        settings.ShellTopCompact = _topCompact;
        settings.ShellInspectorTab = _inspectorTab;
        // Fire and forget: a failed shell-state write is not worth an error dialog, but it must not vanish.
        _ = _archive.SaveAsync().ContinueWith(
            task => Diag.Log("shell state save failed: " + task.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    // The single place that decides the frame. Called from UpdateChrome, so chrome and shell can never
    // disagree about what is on screen.
    private void ApplyShellLayout(bool sessionContext, bool readOnlySnapshot, bool showResume)
    {
        var showSidebar = !_sidebarCollapsed;
        LeftPaneBorder.Visibility = showSidebar ? Visibility.Visible : Visibility.Collapsed;
        LeftColumn.Width = showSidebar ? new GridLength(SidebarWidth) : new GridLength(0);

        // The inspector is closed by default, so a narrow window no longer forces it away - but an
        // unpinned inspector still folds rather than crushing the transcript column.
        var showInspector = _inspectorOpen
            && sessionContext
            && !readOnlySnapshot
            && (_inspectorPinned || !_narrowLayout);
        RightColumnBorder.Visibility = showInspector ? Visibility.Visible : Visibility.Collapsed;
        RightColumn.Width = showInspector ? new GridLength(InspectorWidth) : new GridLength(0);

        ChatsPane.Visibility = _toolsPaneOpen ? Visibility.Collapsed : Visibility.Visible;
        ToolsPane.Visibility = _toolsPaneOpen ? Visibility.Visible : Visibility.Collapsed;

        // Compact top bar: the breadcrumb label and the transcript toolbar step aside, the identity
        // buttons, status, primary action and inspector toggle stay.
        ScreenLabel.Visibility = _topCompact ? Visibility.Collapsed : Visibility.Visible;
        // The label and its "/" are one unit: leaving the separator behind reads as a broken breadcrumb.
        ScreenSeparator.Visibility = _topCompact ? Visibility.Collapsed : Visibility.Visible;
        TopBar.Padding = _topCompact ? new Thickness(14, 4, 14, 4) : new Thickness(14, 10, 14, 10);
        TopBarCollapseButton.Content = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 14,
            Glyph = _topCompact ? "\uE70D" : "\uE70E"
        };
        ToolTipService.SetToolTip(TopBarCollapseButton, _topCompact ? "Restore the full top bar" : "Compact the top bar");
        HeaderActions.Visibility = sessionContext && !readOnlySnapshot && !_topCompact
            ? Visibility.Visible
            : Visibility.Collapsed;

        ResumeTerminalButton.Visibility = showResume ? Visibility.Visible : Visibility.Collapsed;
        HeaderMoreButton.Visibility = showResume ? Visibility.Visible : Visibility.Collapsed;
        InspectorCloseButton.Foreground = MutedBrush();
        InspectorPinButton.Foreground = _inspectorPinned ? AccentBrush() : MutedBrush();
        ToolTipService.SetToolTip(InspectorPinButton, _inspectorPinned
            ? "Unpin: let the inspector fold away on a narrow window"
            : "Pin the inspector open, even on a narrow window");

        // Chat rows are borderless by default and only carry a fill while selected.
        UpdateScopeButtons();
        ApplyInspectorTab();
        SetActiveNav();
    }

    private static string NormalizeInspectorTab(string? tab) =>
        Array.Find(InspectorTabNames, t => string.Equals(t, tab, StringComparison.OrdinalIgnoreCase)) ?? "Integrity";

    // ---- left pane: chats <-> tools ------------------------------------------------------------

    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();

    private void ToggleSidebar_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleSidebar();
        args.Handled = true;
    }

    private void ToggleSidebar()
    {
        _sidebarCollapsed = !_sidebarCollapsed;
        UpdateChrome();
        PersistShell();
    }

    private void Tools_Click(object sender, RoutedEventArgs e)
    {
        _toolsPaneOpen = !_toolsPaneOpen;
        UpdateChrome();
    }

    private void ToolsBack_Click(object sender, RoutedEventArgs e)
    {
        _toolsPaneOpen = false;
        UpdateChrome();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        _toolsPaneOpen = true;
        Navigate("Settings");
    }

    private void SetActiveNav()
    {
        foreach (var button in new[] { NavSearch, NavAsk, NavWorkspaces, NavCollections, NavRunning, NavFleet, NavPhrases, NavCustody, NavSettings })
        {
            var active = button.Tag is string tag && string.Equals(tag, _screen, StringComparison.Ordinal);
            button.Background = active ? AccentVerySoftBrush() : null;
            button.Foreground = active ? StrongBrush() : MutedBrush();
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ---- left pane: archive scope --------------------------------------------------------------

    private void ChatScope_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string scope) return;
        if (string.Equals(scope, _chatScope, StringComparison.Ordinal)) return;
        _chatScope = scope;
        UpdateScopeButtons();
        ApplyFilters();
    }

    private void UpdateScopeButtons()
    {
        // Tier sizes next to the labels: the whole reason for the tiers is knowing at a glance that Active
        // is small and All is the pile, so the numbers belong on the tabs.
        var counts = _archive.ChatScopeCounts();
        ScopeAllText.Text = $"All {counts.Unvetted}";
        ScopeActiveText.Text = $"Active {counts.Active}";
        ScopeArchivedText.Text = $"Archived {counts.Archived}";

        foreach (var button in new[] { ScopeAllButton, ScopeActiveButton, ScopeArchivedButton })
        {
            var active = button.Tag is string tag && string.Equals(tag, _chatScope, StringComparison.Ordinal);
            button.Background = active ? AccentVerySoftBrush() : null;
            button.Foreground = active ? StrongBrush() : MutedBrush();
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    // ---- right pane: inspector ------------------------------------------------------------------

    private void InspectorToggle_Click(object sender, RoutedEventArgs e) => ToggleInspector();

    private void ToggleInspector_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        ToggleInspector();
        args.Handled = true;
    }

    private void ToggleInspector()
    {
        _inspectorOpen = !_inspectorOpen;
        // Asking for the inspector on a narrow window is an explicit instruction; honour it by pinning
        // rather than by silently doing nothing.
        if (_inspectorOpen && _narrowLayout) _inspectorPinned = true;
        UpdateChrome();
        PersistShell();
    }

    private void InspectorClose_Click(object sender, RoutedEventArgs e)
    {
        _inspectorOpen = false;
        UpdateChrome();
        PersistShell();
    }

    private void InspectorPin_Click(object sender, RoutedEventArgs e)
    {
        _inspectorPinned = !_inspectorPinned;
        UpdateChrome();
        PersistShell();
    }

    private void InspectorTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string tab) return;
        _inspectorTab = NormalizeInspectorTab(tab);
        ApplyInspectorTab();
        PersistShell();
    }

    private void ApplyInspectorTab()
    {
        var panels = new (string Tab, FrameworkElement Panel)[]
        {
            ("Integrity", InspectorIntegrityPanel),
            ("Context", InspectorContextPanel),
            ("Activity", InspectorActivityPanel),
            ("Actions", InspectorActionsPanel),
        };
        foreach (var (tab, panel) in panels)
            panel.Visibility = string.Equals(tab, _inspectorTab, StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;

        foreach (var button in new[] { InspectorTabIntegrity, InspectorTabContext, InspectorTabActivity, InspectorTabActions })
        {
            var active = button.Tag is string tag && string.Equals(NormalizeInspectorTab(tag), _inspectorTab, StringComparison.Ordinal);
            button.Background = active ? AccentVerySoftBrush() : null;
            button.Foreground = active ? StrongBrush() : MutedBrush();
            button.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }

        if (_inspectorTab == "Context") RenderContextTab();
        else if (_inspectorTab == "Activity") RenderActivityTab();
    }

    // The badge is the whole point of a closed inspector: the session can ask for attention without
    // occupying part of the window. Driven from PaintIntegrity, so it can never disagree with the tab.
    private void UpdateInspectorBadge(string? severity)
    {
        var color = severity?.ToLowerInvariant() switch
        {
            "danger" => IntegrityBrush("danger"),
            "warn" => IntegrityBrush("warn"),
            _ => null,
        };
        if (color is null)
        {
            InspectorBadge.Visibility = Visibility.Collapsed;
            return;
        }
        InspectorBadge.Background = color;
        InspectorBadge.Visibility = Visibility.Visible;
    }

    // ---- inspector: Context tab ------------------------------------------------------------------

    // What this chat IS: identity, where it lives, how big it is and where it sits in the archive. The
    // tags editor lives here too, because a tag is context, not an action.
    private void RenderContextTab()
    {
        ContextItems.Children.Clear();
        if (_selected is null)
        {
            ContextItems.Children.Add(new TextBlock { Text = "No chat selected", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }

        var session = _selected;
        AddFact("Agent", string.Equals(session.Tool, "claude", StringComparison.OrdinalIgnoreCase) ? "Claude" : "Codex");
        if (!string.IsNullOrWhiteSpace(session.WorkspaceName)) AddFact("Workspace", session.WorkspaceName);
        if (!string.IsNullOrWhiteSpace(session.SourcePath)) AddFact("Source", session.SourcePath);
        AddFact("Created", string.IsNullOrWhiteSpace(session.CreatedAt) ? "unknown" : session.CreatedAt);
        AddFact("Updated", string.IsNullOrWhiteSpace(session.UpdatedAt) ? "unknown" : session.UpdatedAt);
        AddFact("Messages", session.MessageCount + " total - " + session.UserMessageCount + " yours");

        var collections = _archive.Store.Collections.Values
            .Where(c => c.SessionIds.Any(id => string.Equals(id, session.Id, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (collections.Count > 0) AddFact("Collections", string.Join(", ", collections));

        if (session.IsBranch && !string.IsNullOrWhiteSpace(session.BranchOfId))
        {
            var parent = _archive.Store.Sessions.TryGetValue(session.BranchOfId, out var source) ? source.DisplayTitle : session.BranchOfId;
            AddFact("Branch of", parent);
        }
        if (!string.IsNullOrWhiteSpace(session.HandoffFromId))
            AddFact("Handoff from", session.HandoffFromId);
        if (session.IsReadOnlySnapshot) AddFact("Access", "read-only checkpoint");

        void AddFact(string label, string value)
        {
            var stack = new StackPanel { Spacing = 1 };
            stack.Children.Add(new TextBlock
            {
                Text = label.ToUpperInvariant(),
                Foreground = MutedBrush(),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            });
            stack.Children.Add(new TextBlock
            {
                Text = value,
                Foreground = StrongBrush(),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 16
            });
            ContextItems.Children.Add(stack);
        }
    }

    // ---- inspector: Activity tab -----------------------------------------------------------------

    // What this chat has been DOING: live owner, launch reservations, mux custody, filing intents and the
    // recent app events. The Integrity tab answers "is it safe to continue"; this answers "what happened".
    private void RenderActivityTab()
    {
        ActivityItems.Children.Clear();
        // In the fixture the oracle never runs, so every claim this tab makes would be invented - and
        // "Checking session state..." would be a spinner that can never finish. Say so instead.
        if (GuiVerificationFixture.Enabled)
        {
            ActivityItems.Children.Add(IntegrityChip("Isolated metadata fixture; runtime actions disabled"));
            return;
        }
        if (_selected is null)
        {
            ActivityItems.Children.Add(new TextBlock { Text = "No chat selected", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }

        var summary = _integrity.CurrentFor(IntegrityKey(_selected));
        if (summary is null)
        {
            ActivityItems.Children.Add(new TextBlock { Text = "Checking session state...", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }

        var chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        chips.Children.Add(IntegrityChip(summary.LiveVerified ? "live verified" : "not live"));
        chips.Children.Add(IntegrityChip(summary.SourceStatus));
        chips.Children.Add(IntegrityChip(summary.MuxTabs.Count == 0 ? "no mux custody" : summary.MuxTabs.Count + " mux tab" + (summary.MuxTabs.Count == 1 ? "" : "s")));
        ActivityItems.Children.Add(chips);

        if (summary.MuxTabs.Count > 0)
            ActivityItems.Children.Add(IntegrityEvidenceBlock("Mux custody", summary.MuxTabs.Take(3).Select(t =>
                $"{t.Name}: {(t.IsCurrent ? "current" : "history")}{(string.IsNullOrWhiteSpace(t.Kind) ? "" : " - " + t.Kind)}")));

        if (summary.LaunchClaims.Count > 0)
            ActivityItems.Children.Add(IntegrityEvidenceBlock("Launch claims", summary.LaunchClaims.Take(3).Select(c =>
                $"{(c.Expired ? "expired" : "active")} - {c.OwnerProcess} pid {c.OwnerPid}")));

        if (summary.PendingIntents.Count > 0)
            ActivityItems.Children.Add(IntegrityEvidenceBlock("Pending filing", summary.PendingIntents.Take(3).Select(p =>
                $"{p.Tool} - {p.Workspace}")));

        if (summary.RecentEvents.Count > 0)
            ActivityItems.Children.Add(IntegrityEvidenceBlock("Recent events", summary.RecentEvents.Take(6).Select(e =>
                $"{e.Kind}: {Trim(e.Summary, 92)}")));
        else
            ActivityItems.Children.Add(new TextBlock { Text = "No recorded events for this chat.", Foreground = MutedBrush(), FontSize = 12 });
    }

    // ---- top bar: compact --------------------------------------------------------------------------

    private void TopBarCollapse_Click(object sender, RoutedEventArgs e)
    {
        _topCompact = !_topCompact;
        UpdateChrome();
        PersistShell();
    }
}
