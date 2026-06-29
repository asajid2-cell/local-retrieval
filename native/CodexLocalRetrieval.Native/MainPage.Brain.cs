using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Memory;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace CodexLocalRetrieval_Native;

// The Project Brain screen: pick a collection, see what's wired (connections preflight), build/update
// its durable source-linked memory through a reviewable patch, and preview the result. A build NEVER
// writes the canonical vault — only Apply does, after the user sees "what changed, why, and from where".
public sealed partial class MainPage
{
    private string? _brainCollectionId;
    // A built-but-not-applied patch, held so the review renders IN-PAGE (visual tree, not a modal
    // popup) - the user sees "what changed, why, from where" alongside the preview and Applies or Discards.
    private MemoryPatch? _brainPendingPatch;
    private BrainBuildResult? _brainPendingBuild;
    private string? _brainPendingFor;
    private string? _brainPendingNow;

    private List<ArchiveSession> BrainChatsOf(ArchiveCollection col)
        => col.SessionIds.Where(id => _archive.Store.Sessions.ContainsKey(id))
                         .Select(id => _archive.Store.Sessions[id]).ToList();

    private void RenderBrainPage()
    {
        ScreenLabel.Text = "Project Brain - durable, source-linked memory per collection";
        TitleText.Text = "Brain";
        MainContent.Children.Clear();

        var collections = _archive.Store.Collections.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (collections.Count == 0)
        {
            MainContent.Children.Add(EmptyBlock("No collections yet",
                "Create a collection first (Collections screen), then come back to build its brain."));
            return;
        }
        if (_brainCollectionId is null || !_archive.Store.Collections.ContainsKey(_brainCollectionId))
            _brainCollectionId = collections[0].Id;

        MainContent.Children.Add(BrainPickerCard(collections));
        var col = _archive.Store.Collections[_brainCollectionId];
        MainContent.Children.Add(BrainConnectionsCard(col));
        MainContent.Children.Add(BrainStatusActionsCard(col));
        if (_brainPendingPatch is not null && _brainPendingFor == col.Id)
            MainContent.Children.Add(BrainReviewCard(col, _brainPendingPatch));
        MainContent.Children.Add(BrainPreviewCard(col));
    }

    private Border BrainPickerCard(List<ArchiveCollection> collections)
    {
        var combo = new ComboBox { MinWidth = 320, CornerRadius = ControlCornerRadius() };
        foreach (var c in collections)
        {
            var item = new ComboBoxItem { Content = c.Name, Tag = c.Id };
            combo.Items.Add(item);
            if (c.Id == _brainCollectionId) combo.SelectedItem = item;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is ComboBoxItem ci && ci.Tag is string id)
            {
                _brainCollectionId = id;
                RenderBrainPage();
            }
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "Collection", Foreground = MutedBrush(), VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(combo);

        var explain = new TextBlock
        {
            Foreground = MutedBrush(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Text = "A brain is an external memory over this project's chats: raw transcripts stay the source " +
                   "of truth, the brain holds durable typed cards (decisions, reversals, breakthroughs, open " +
                   "loops) each linked back to the exact messages. It lives as an Obsidian Markdown vault + a " +
                   "local git history. Summaries are navigation - never a replacement for the source."
        };
        return Card(new StackPanel { Spacing = 12, Children = { row, explain } });
    }

    private Border BrainConnectionsCard(ArchiveCollection col)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(SectionHeader("Connections"));

        var provider = _archive.ActiveAiProvider();
        var hasKey = provider is not null && HasApiKey(provider.Id);
        var gitOk = new GitHistory().IsAvailable();
        var claudeExe = ArchiveService.ResolveClaudeExe();
        var codexExe = ArchiveService.ResolveCodexExe();
        var vault = new BrainService(_archive).PathsFor(col.Id).Vault;

        stack.Children.Add(BrainStatusRow("Build agent (AI key)",
            hasKey ? $"{provider!.Name} - key found; real extraction available" : "No key - builds use the deterministic mock (working-lane placeholders)",
            hasKey));
        stack.Children.Add(BrainStatusRow("Git history",
            gitOk ? "git found - every build/apply is committed locally" : "git not found - the vault still builds, just without history", gitOk));
        stack.Children.Add(BrainStatusRow("Claude CLI", IsRootedExisting(claudeExe) ? claudeExe : "not found (optional)", IsRootedExisting(claudeExe)));
        stack.Children.Add(BrainStatusRow("Codex CLI", IsRootedExisting(codexExe) ? codexExe : "not found (optional)", IsRootedExisting(codexExe)));
        stack.Children.Add(BrainStatusRow("Vault folder", vault, true));
        stack.Children.Add(new TextBlock
        {
            Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = "Secrets (keys/tokens) are redacted from every excerpt before it is written. Raw transcripts are never committed."
        });
        return Card(stack);
    }

    private Border BrainStatusActionsCard(ArchiveCollection col)
    {
        var brain = new BrainService(_archive);
        var chats = BrainChatsOf(col);
        var status = brain.Status(col.Id, chats);

        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(SectionHeader(status.Built ? "Status" : "Not built yet"));

        var summary = status.Built
            ? $"{status.CanonicalCount} canonical truths · {status.WorkingCount} working · {status.IncorporatedChats} chats incorporated" +
              (status.StaleChats > 0 ? $" · {status.StaleChats} changed since last build" : " · up to date")
            : $"{chats.Count} chats ready to build into a brain.";
        stack.Children.Add(new TextBlock { Text = summary, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap });
        if (status.Built && !string.IsNullOrEmpty(status.LastCommit))
            stack.Children.Add(new TextBlock { Text = $"Last commit {Shorten(status.LastCommit, 10)} · built {status.BuiltAt}", Foreground = MutedBrush(), FontSize = 12 });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var buildBtn = new Button
        {
            Style = (Style)Resources["PrimaryPillButtonStyle"],
            Content = status.Built ? (status.StaleChats > 0 ? "Update brain (review patch)" : "Rebuild (review patch)") : "Build brain (review patch)"
        };
        buildBtn.Click += async (_, _) => await BuildBrainAsync(col);
        actions.Children.Add(buildBtn);

        if (status.Built)
        {
            var openVault = PillButton("Open vault");
            openVault.Click += (_, _) => OpenBrainVault(col);
            actions.Children.Add(openVault);

            var handoff = PillButton("Copy handoff");
            handoff.Click += (_, _) => CopyBrainHandoff(col);
            actions.Children.Add(handoff);

            var history = PillButton("History");
            history.Click += async (_, _) => await ShowBrainHistoryAsync(col);
            actions.Children.Add(history);
        }
        stack.Children.Add(actions);
        if (chats.Count == 0)
            stack.Children.Add(new TextBlock { Foreground = MutedBrush(), FontSize = 12, Text = "Add chats to this collection first - the brain is built from them." });
        return Card(stack);
    }

    private Border BrainPreviewCard(ArchiveCollection col)
    {
        var brain = new BrainService(_archive);
        var cards = brain.ReadCards(col.Id).Where(c => c.Status == CardStatuses.Active).ToList();
        var stack = new StackPanel { Spacing = 12 };
        stack.Children.Add(SectionHeader("Preview"));
        if (cards.Count == 0)
        {
            stack.Children.Add(new TextBlock { Foreground = MutedBrush(), Text = "Nothing yet - build the brain to see its memory here." });
            return Card(stack);
        }

        void Group(string title, IEnumerable<MemoryCard> sel)
        {
            var list = sel.OrderByDescending(c => c.Importance).ToList();
            if (list.Count == 0) return;
            stack.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontWeight = FontWeights.SemiBold, FontSize = 14 });
            foreach (var c in list.Take(12)) stack.Children.Add(BrainCardRow(col, c));
        }
        Group("Current state", cards.Where(c => c.Type == CardTypes.CurrentState));
        Group("Top canonical truths", cards.Where(c => c.Lane == Lanes.Canonical && c.Type != CardTypes.CurrentState));
        Group("Working / exploratory (not settled)", cards.Where(c => c.Lane == Lanes.Working && c.Type is not (CardTypes.OpenLoop or CardTypes.QuestionForHuman) && c.Type != CardTypes.CurrentState));
        Group("Open loops & questions", cards.Where(c => c.Type is CardTypes.OpenLoop or CardTypes.QuestionForHuman));
        return Card(stack);
    }

    private UIElement BrainCardRow(ArchiveCollection col, MemoryCard c)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock { Text = c.Title, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap, FontSize = 13, MaxWidth = 560 });
        head.Children.Add(MiniChip(c.Lane == Lanes.Canonical ? "canonical" : "working"));
        head.Children.Add(MiniChip(c.TruthEvidence));

        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(head);

        // "trace to source" - open the exact transcript span behind the first anchor
        if (c.Sources.Count > 0)
        {
            var a = c.Sources[0];
            var link = new HyperlinkButton { Content = $"open source: {a.SessionId} msg {a.MsgStartIndex}-{a.MsgEndIndex}", Padding = new Thickness(0) };
            link.Click += async (_, _) => await OpenSourceSpanAsync(a);
            panel.Children.Add(link);
        }
        return new Border { Padding = new Thickness(0, 4, 0, 8), Child = panel };
    }

    private async Task BuildBrainAsync(ArchiveCollection col)
    {
        var chats = BrainChatsOf(col);
        if (chats.Count == 0) { await InfoAsync("No chats", "Add chats to this collection first."); return; }

        SyncStatus.Text = "Building brain (reading full transcripts)...";
        var brain = new BrainService(_archive);
        var backend = BuildCopilotBackend();
        IReadOnlyList<IBrainAnalyst> analysts = backend is not null
            ? new IBrainAnalyst[] { new BackendAnalyst(backend) }
            : new IBrainAnalyst[] { new MockAnalyst() };
        var now = DateTime.UtcNow.ToString("O");

        var agentLabel = backend is not null ? backend.Name : "mock";
        Diag.Log($"Brain build start: collection={col.Id} chats={chats.Count} agent={agentLabel}");
        BrainBuildResult build;
        try { build = await brain.BuildAsync(col.Id, chats, analysts, new BrainBuildOptions(), now); }
        catch (Exception ex) { SyncStatus.Text = "Build failed: " + ex.Message; Diag.Log("Brain build EX: " + ex); return; }

        Diag.Log($"Brain build done: blocks={build.Blocks.Count} adds={build.Patch.Adds.Count} updates={build.Patch.Updates.Count}");

        // Resilience: if a real backend produced nothing (e.g. an invalid/expired key or a quota error),
        // fall back to the offline deterministic mock so the user still gets a starting brain rather than
        // a dead end. The cards are honestly created_by=mock / working-lane.
        var usedMockFallback = false;
        if (build.Patch.Adds.Count == 0 && build.Patch.Updates.Count == 0 && backend is not null && build.Blocks.Count > 0)
        {
            Diag.Log("Brain build: backend returned no cards -> falling back to offline mock");
            build = await brain.BuildAsync(col.Id, chats, new IBrainAnalyst[] { new MockAnalyst() }, new BrainBuildOptions(), now);
            usedMockFallback = true;
        }

        if (build.Patch.Adds.Count == 0 && build.Patch.Updates.Count == 0)
        {
            SyncStatus.Text = build.Blocks.Count == 0
                ? "No content parsed from this project's chats."
                : $"Built {build.Blocks.Count} source blocks but no cards were produced.";
            return;
        }
        if (usedMockFallback)
            SyncStatus.Text = $"The {agentLabel} agent returned nothing (check the API key) - used the offline mock instead. Review below.";

        // Hold the patch and render the review IN-PAGE (not a modal) so it sits with the preview and
        // can be Applied/Discarded from the visual tree. Nothing is written to the vault until Apply.
        _brainPendingPatch = build.Patch;
        _brainPendingBuild = build;
        _brainPendingFor = col.Id;
        _brainPendingNow = now;
        SyncStatus.Text = $"Built a patch from {patch_count(build.Patch)} card(s) - review it below, then Apply.";
        RenderBrainPage();
    }

    private static int patch_count(MemoryPatch p) => p.Adds.Count + p.Updates.Count + p.Supersedes.Count;

    // The centerpiece, rendered in-page: "what changed, why, and from where" before anything is written.
    private Border BrainReviewCard(ArchiveCollection col, MemoryPatch patch)
    {
        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(SectionHeader("Review patch - nothing is written until you Apply"));
        stack.Children.Add(new TextBlock { Text = patch.Summary, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(new TextBlock { Text = $"Built by {patch.CreatedBy} from {patch.SourceChatIds.Count} chat(s).", Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });

        void Section(string title, List<MemoryCard> cards)
        {
            if (cards.Count == 0) return;
            stack.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var c in cards)
            {
                var sp = new StackPanel { Spacing = 2, Margin = new Thickness(0, 0, 0, 6) };
                var h = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                h.Children.Add(new TextBlock { Text = c.Title, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap, MaxWidth = 560, FontSize = 13 });
                h.Children.Add(MiniChip(c.Lane));
                h.Children.Add(MiniChip(c.Type));
                sp.Children.Add(h);
                sp.Children.Add(new TextBlock
                {
                    Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
                    Text = $"truth: {c.TruthEvidence} · extraction: {c.ExtractionConfidence} · " +
                           (c.Sources.Count > 0 ? $"from {c.Sources[0].SessionId} msg {c.Sources[0].MsgStartIndex}-{c.Sources[0].MsgEndIndex}" : "no source anchor (stays working)")
                });
                stack.Children.Add(sp);
            }
        }
        Section($"Add ({patch.Adds.Count})", patch.Adds);
        Section($"Update ({patch.Updates.Count})", patch.Updates);
        Section($"Supersede ({patch.Supersedes.Count})", patch.Supersedes);
        if (patch.Conflicts.Count > 0)
        {
            stack.Children.Add(new TextBlock { Text = "Disputed", Foreground = StrongBrush(), FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            foreach (var x in patch.Conflicts) stack.Children.Add(new TextBlock { Text = "· " + x, Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var applyBtn = new Button { Style = (Style)Resources["PrimaryPillButtonStyle"], Content = "Apply patch" };
        applyBtn.Click += (_, _) => ApplyPendingPatch(col);
        var discardBtn = PillButton("Discard");
        discardBtn.Click += (_, _) => { ClearPending(); SyncStatus.Text = "Build discarded - nothing was written."; RenderBrainPage(); };
        actions.Children.Add(applyBtn);
        actions.Children.Add(discardBtn);
        stack.Children.Add(actions);

        return Card(stack);
    }

    private async void ApplyPendingPatch(ArchiveCollection col)
    {
        if (_brainPendingPatch is null || _brainPendingBuild is null || _brainPendingNow is null) return;
        var patch = _brainPendingPatch;
        var build = _brainPendingBuild;
        var now = _brainPendingNow;
        SyncStatus.Text = "Applying patch (writing the vault, committing, indexing)...";
        try
        {
            // File I/O + a git subprocess + a SQLite rebuild - keep it OFF the UI thread so the app
            // never freezes (the git step in particular can block).
            var brain = new BrainService(_archive);
            var res = await Task.Run(() => brain.ApplyPatch(col.Id, col.Name, patch, build.Blocks, build.ChatStamps, now));
            SyncStatus.Text = $"Brain updated: {res.CanonicalCount} canonical, {res.WorkingCount} working" +
                              (string.IsNullOrEmpty(res.Commit) ? " (git not available - no history)" : $" - committed {Shorten(res.Commit, 8)}");
        }
        catch (Exception ex) { SyncStatus.Text = "Apply failed: " + ex.Message; }
        ClearPending();
        RenderBrainPage();
    }

    private void ClearPending() { _brainPendingPatch = null; _brainPendingBuild = null; _brainPendingFor = null; _brainPendingNow = null; }

    private async Task OpenSourceSpanAsync(SourceAnchor a)
    {
        if (!_archive.Store.Sessions.TryGetValue(a.SessionId, out var session))
        {
            await InfoAsync("Source not available", $"The chat {a.SessionId} is not in the index right now.");
            return;
        }
        var span = await _archive.ParseFullRangeAsync(session, a.MsgStartIndex, a.MsgEndIndex, context: 3);
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap,
            Text = $"{session.DisplayTitle} · messages {a.MsgStartIndex}-{a.MsgEndIndex} (+/-3 context)"
        });
        foreach (var m in span)
        {
            var card = new StackPanel { Spacing = 2 };
            card.Children.Add(new TextBlock { Text = $"[{m.Index}] {m.Role}" + (m.ToolName is { Length: > 0 } ? $" · {m.ToolName}" : ""), Foreground = MutedBrush(), FontSize = 11 });
            card.Children.Add(new TextBlock { Text = m.Text, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap, FontSize = 12 });
            panel.Children.Add(new Border { Padding = new Thickness(0, 2, 0, 6), Child = card });
        }
        var dialog = new ContentDialog
        {
            Title = "Source span",
            Content = new ScrollViewer { Content = panel, MaxHeight = 480, HorizontalScrollMode = ScrollMode.Disabled },
            PrimaryButtonText = "Copy reference",
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            SetClipboard($"{a.SessionId} msg {a.MsgStartIndex}-{a.MsgEndIndex}");
    }

    private void OpenBrainVault(ArchiveCollection col)
    {
        var vault = new BrainService(_archive).PathsFor(col.Id).Vault;
        try
        {
            if (!Directory.Exists(vault)) { SyncStatus.Text = "No vault yet - build the brain first."; return; }
            Process.Start(new ProcessStartInfo { FileName = vault, UseShellExecute = true });
            SyncStatus.Text = "Opened the vault folder. Open it in Obsidian to graph the memory.";
        }
        catch (Exception ex) { SyncStatus.Text = "Could not open the vault: " + ex.Message; }
    }

    private void CopyBrainHandoff(ArchiveCollection col)
    {
        var bundle = new BrainService(_archive).GetAgentContext(col.Id, "");
        SetClipboard(bundle);
        SyncStatus.Text = "Copied a paste-ready agent handoff for this project.";
    }

    private async Task ShowBrainHistoryAsync(ArchiveCollection col)
    {
        var brain = new BrainService(_archive);
        var vault = brain.PathsFor(col.Id).Vault;
        var log = brain.Git.Log(vault, 50);
        var panel = new StackPanel { Spacing = 4 };
        if (log.Count == 0) panel.Children.Add(new TextBlock { Text = "No git history (git not available, or nothing committed yet).", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap });
        foreach (var line in log) panel.Children.Add(new TextBlock { Text = line, Foreground = StrongBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        await new ContentDialog
        {
            Title = $"Brain history - {col.Name}",
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        }.ShowAsync();
    }

    // --- small UI helpers (local to the Brain page) ---
    private TextBlock SectionHeader(string text) => new() { Text = text, Foreground = StrongBrush(), FontSize = 18, FontWeight = FontWeights.SemiBold };

    private Button PillButton(string text) => new() { Style = (Style)Resources["PillButtonStyle"], Content = text };

    private UIElement BrainStatusRow(string label, string value, bool ok)
    {
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(170) }, new ColumnDefinition() } };
        var dot = new TextBlock { Text = ok ? "●" : "○", Foreground = ok ? StrongBrush() : MutedBrush(), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 8, 0) };
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        left.Children.Add(dot);
        left.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush() });
        Grid.SetColumn(left, 0);
        var right = new TextBlock { Text = value, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private Border MiniChip(string text) => new()
    {
        Background = LineBrush(),
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(7, 1, 7, 1),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, Foreground = MutedBrush(), FontSize = 11 }
    };

    private static bool IsRootedExisting(string exe) => Path.IsPathRooted(exe) && File.Exists(exe);

    private static string Shorten(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s[..n]);

    private void SetClipboard(string text)
    {
        var dp = new DataPackage();
        dp.SetText(text);
        Clipboard.SetContent(dp);
    }

    private async Task InfoAsync(string title, string message)
    {
        await new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        }.ShowAsync();
    }
}
