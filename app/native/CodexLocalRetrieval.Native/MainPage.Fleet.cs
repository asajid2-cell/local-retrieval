using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexLocalRetrieval_Native;

// The FLEET page: what the always-on recorder knows was running (current.json), every saved fleet
// state (user-named snapshots + automatic pre-reboot promotions), and claim-gated bulk restore.
// Restore goes through the SAME per-session path as "Resume in terminal" (run guard + launch
// claims) — this page only does selection; it never launches anything itself.
public sealed partial class MainPage
{
    private int _fleetSeq;

    private void RenderFleetPage()
    {
        var seq = ++_fleetSeq;
        MainContent.Children.Clear();

        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Margin = new Thickness(0, 0, 0, 12) };
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock { Text = "Fleet", Foreground = StrongBrush(), FontSize = 20, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock
        {
            Text = "The always-on record of what's running — survives a crash or reboot. Save the current fleet as a named state, or reopen a saved one.",
            Foreground = MutedBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        header.Children.Add(title);
        var refresh = new Button { Style = (Style)Resources["PillButtonStyle"], Content = new TextBlock { Text = "Refresh", FontSize = 12 } };
        refresh.Click += (_, _) => RenderFleetPage();
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        MainContent.Children.Add(header);

        // Save row: name a snapshot of RIGHT NOW (probe runs off-thread on click).
        var saveRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Margin = new Thickness(0, 0, 0, 14) };
        var nameBox = new TextBox { PlaceholderText = "State name (e.g. venpod, before-reboot)", CornerRadius = new CornerRadius(10), Height = 40 };
        saveRow.Children.Add(nameBox);
        var saveBtn = new Button { Style = (Style)Resources["PrimaryPillButtonStyle"], Margin = new Thickness(8, 0, 0, 0), Content = new TextBlock { Text = "Save current fleet" } };
        saveBtn.Click += async (_, _) =>
        {
            var name = (nameBox.Text ?? "").Trim();
            if (name.Length == 0) { SyncStatus.Text = "Give the state a name first."; return; }
            saveBtn.IsEnabled = false;
            var (ok, detail) = await Task.Run(() => new FleetSnapshotService("native").SaveNamed(name));
            saveBtn.IsEnabled = true;
            SyncStatus.Text = ok ? $"Saved fleet state '{name}'." : "Save failed: " + detail;
            if (ok) RenderFleetPage();
        };
        Grid.SetColumn(saveBtn, 1);
        saveRow.Children.Add(saveBtn);
        MainContent.Children.Add(saveRow);

        var loading = new TextBlock { Text = "Reading fleet records…", Foreground = MutedBrush(), FontSize = 13 };
        MainContent.Children.Add(loading);
        _ = LoadFleetAsync(seq, loading);
    }

    private async Task LoadFleetAsync(int seq, TextBlock loading)
    {
        var data = await Task.Run(() =>
        {
            FleetStore.TryReadCurrent(out var current, out _);
            FleetStore.TryListStates(out var states, out var listDetail);
            FleetWriterLock.TryReadHolder(out var holder, out _);
            RunningSessions.TryAllLiveSessionIds(out var liveIds, out _);
            return (current, states, listDetail, holder, liveIds);
        });
        if (seq != _fleetSeq || _screen != "Fleet") return;

        MainContent.Children.Remove(loading);
        var bootStamp = FleetStore.CurrentBootStampUtc();

        // --- recorder status + the rolling current record ---
        MainContent.Children.Add(SectionLabel("Recording now"));
        var recorderText = data.holder is { } h
            ? $"Recorder active: {h.Host} (pid {h.Pid}, since {FriendlyTime(h.AcquiredAtUtc)})."
            : "No recorder is running — the headless server usually owns this. Fleet states can still be saved and restored.";
        MainContent.Children.Add(new TextBlock { Text = recorderText, Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        if (data.current is { } cur)
            MainContent.Children.Add(FleetStateCard(cur, isCurrent: true, bootStamp, data.liveIds));
        else
            MainContent.Children.Add(new TextBlock { Text = "No rolling record yet.", Foreground = MutedBrush(), FontSize = 12, Margin = new Thickness(0, 0, 0, 8) });

        // --- saved states (pre-reboot promotions first-class) ---
        var states = data.states;
        MainContent.Children.Add(SectionLabel($"Saved states ({states.Count})"));
        if (!string.IsNullOrEmpty(data.listDetail))
            MainContent.Children.Add(new TextBlock { Text = data.listDetail, Foreground = MutedBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        if (states.Count == 0)
            MainContent.Children.Add(new TextBlock { Text = "None yet. Save one above, or let a reboot leave one behind.", Foreground = MutedBrush(), FontSize = 12 });
        foreach (var s in states)
            MainContent.Children.Add(FleetStateCard(s, isCurrent: false, bootStamp, data.liveIds));
    }

    private Border FleetStateCard(FleetState state, bool isCurrent, string bootStamp, HashSet<string> liveIds)
    {
        var outer = new StackPanel { Spacing = 6 };
        var head = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var left = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

        var displayName = isCurrent ? "Current fleet" : state.Name;
        nameRow.Children.Add(new TextBlock { Text = displayName, Foreground = StrongBrush(), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var isPreReboot = string.Equals(state.Kind, "boot", StringComparison.OrdinalIgnoreCase);
        if (isPreReboot)
            nameRow.Children.Add(Badge("pre-reboot", accent: true));
        else if (!isCurrent)
            nameRow.Children.Add(Badge(state.Kind, accent: false));
        var sameBoot = FleetStore.SameBoot(state.BootStampUtc, bootStamp);
        if (!isCurrent && !sameBoot && !isPreReboot)
            nameRow.Children.Add(Badge("older boot", accent: false));
        left.Children.Add(nameRow);

        var liveCount = state.Sessions.Count(s => liveIds.Contains(s.SessionId));
        var deadCount = state.Sessions.Count - liveCount;
        left.Children.Add(new TextBlock
        {
            Text = $"{state.Sessions.Count} session(s) — {liveCount} live now, {deadCount} closed · saved {FriendlyTime(state.SavedAtUtc)}",
            Foreground = MutedBrush(),
            FontSize = 12,
        });
        head.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        if (deadCount > 0)
        {
            var restore = new Button { Style = (Style)Resources["PrimaryPillButtonStyle"], Content = new TextBlock { Text = deadCount == state.Sessions.Count ? "Restore…" : $"Restore {deadCount} closed…", FontSize = 12 } };
            restore.Click += async (_, _) => await ShowRestoreDialogAsync(state);
            actions.Children.Add(restore);
        }
        if (!isCurrent)
        {
            var del = new Button { Style = (Style)Resources["PillButtonStyle"], Content = new TextBlock { Text = "Delete", FontSize = 12 } };
            del.Click += async (_, _) =>
            {
                var confirm = new ContentDialog
                {
                    Title = $"Delete fleet state '{state.Name}'?",
                    Content = "Only the saved list is deleted — the chats themselves stay in the archive.",
                    PrimaryButtonText = "Delete",
                    CloseButtonText = "Cancel",
                    XamlRoot = XamlRoot,
                };
                if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;
                SyncStatus.Text = FleetStore.TryDeleteState(state.Name, out var detail) ? $"Deleted '{state.Name}'." : "Delete failed: " + detail;
                RenderFleetPage();
            };
            actions.Children.Add(del);
        }
        Grid.SetColumn(actions, 1);
        head.Children.Add(actions);
        outer.Children.Add(head);

        // Per-session rows (enriched from the archive; the fleet record itself is deliberately raw).
        foreach (var s in state.Sessions.Take(30))
        {
            var archived = FindArchiveSessionByIdOrAlias(s.SessionId, s.Tool);
            var row = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Margin = new Thickness(2, 0, 0, 0) };
            var live = liveIds.Contains(s.SessionId);
            row.Children.Add(new TextBlock
            {
                Text = live ? "●" : "○",
                Foreground = live ? AccentBrush() : MutedBrush(),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
            });
            var label = archived?.DisplayTitle is { Length: > 0 } t ? t : s.SessionId;
            var text = new TextBlock { Text = $"{label}  ·  {s.Tool} {ShortId(s.SessionId)}", Foreground = MutedBrush(), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(text, 1);
            row.Children.Add(text);
            if (!live && archived is not null)
            {
                var open = new Button { Style = (Style)Resources["PillButtonStyle"], Padding = new Thickness(10, 2, 10, 2), Content = new TextBlock { Text = "Open", FontSize = 11 } };
                open.Click += (_, _) => ResumeInTerminal(archived, trigger: "fleet");
                Grid.SetColumn(open, 2);
                row.Children.Add(open);
            }
            outer.Children.Add(row);
        }
        if (state.Sessions.Count > 30)
            outer.Children.Add(new TextBlock { Text = $"…and {state.Sessions.Count - 30} more.", Foreground = MutedBrush(), FontSize = 11 });

        return RowCard(outer);
    }

    // Selection dialog → sequential guarded opens. Live sessions are shown but locked out; each
    // open still passes the run guard + launch claim on its own (a session may go live between
    // the dialog and its turn — the claim refuses it, never double-opens).
    private async Task ShowRestoreDialogAsync(FleetState state)
    {
        await Task.Run(() => RunningSessions.InvalidateScanCache());
        RunningSessions.TryAllLiveSessionIds(out var liveIds, out _);

        var picks = new List<(CheckBox Box, ArchiveSession Session)>();
        var listPanel = new StackPanel { Spacing = 4 };
        var missing = 0;
        foreach (var s in state.Sessions)
        {
            var archived = FindArchiveSessionByIdOrAlias(s.SessionId, s.Tool);
            if (archived is null) { missing++; continue; }
            var live = liveIds.Contains(s.SessionId);
            var box = new CheckBox
            {
                Content = new TextBlock
                {
                    Text = (archived.DisplayTitle is { Length: > 0 } t ? t : s.SessionId) + (live ? "   (already live)" : ""),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxLines = 1,
                },
                IsChecked = !live,
                IsEnabled = !live,
            };
            listPanel.Children.Add(box);
            if (!live) picks.Add((box, archived));
        }
        if (missing > 0)
            listPanel.Children.Add(new TextBlock { Text = $"{missing} session(s) aren't in the archive yet — sync sessions first to restore them.", Foreground = MutedBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap });

        var dialog = new ContentDialog
        {
            Title = $"Restore '{(state.Name.Length > 0 ? state.Name : "current fleet")}'",
            Content = new ScrollViewer { Content = listPanel, MaxHeight = 420 },
            PrimaryButtonText = "Open selected",
            CloseButtonText = "Cancel",
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var chosen = picks.Where(p => p.Box.IsChecked == true).Select(p => p.Session).ToList();
        if (chosen.Count == 0) { SyncStatus.Text = "Nothing selected."; return; }
        SyncStatus.Text = $"Restoring {chosen.Count} session(s)…";
        foreach (var session in chosen)
        {
            ResumeInTerminal(session, trigger: "fleet-restore");
            await Task.Delay(600);   // space out terminal spawns; each open is claim-gated on its own
        }
        SyncStatus.Text = $"Restore dispatched for {chosen.Count} session(s).";
    }

    private Border Badge(string text, bool accent) => new()
    {
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(8, 1, 8, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Background = accent ? AccentBrush() : PanelBrush(),
        BorderBrush = LineBrush(),
        BorderThickness = new Thickness(1),
        Child = new TextBlock { Text = text, FontSize = 10, Foreground = accent ? new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Black) : MutedBrush() },
    };

    private static string FriendlyTime(string iso)
    {
        if (!DateTimeOffset.TryParse(iso, out var t)) return "unknown time";
        var delta = DateTimeOffset.UtcNow - t.ToUniversalTime();
        if (delta < TimeSpan.FromMinutes(2)) return "just now";
        if (delta < TimeSpan.FromHours(1)) return $"{(int)delta.TotalMinutes} min ago";
        if (delta < TimeSpan.FromHours(48)) return $"{(int)delta.TotalHours} h ago";
        return t.ToLocalTime().ToString("MMM d, HH:mm");
    }
}
