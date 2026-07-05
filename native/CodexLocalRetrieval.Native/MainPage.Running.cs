using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

// The RUNNING page: one live view of every session that exists RIGHT NOW — multiplex sessions
// (PC-hosted via muxd + any legacy tmux ones) pulled from the relay, and loose local claude/codex
// processes (VS Code / terminals) from the process scan. Manage them here: open, add to a
// collection, kill — without hunting through the web UI or Task Manager.
public sealed partial class MainPage
{
    private sealed record MuxRow(string Name, string State, string AgentLabel, string AgentDetail, bool Hosted, bool Alive, bool Armed, long Activity, bool Attached);
    private int _runningSeq;

    private void RenderRunningPage()
    {
        var seq = ++_runningSeq;
        MainContent.Children.Clear();

        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Margin = new Thickness(0, 0, 0, 12) };
        var title = new StackPanel { Spacing = 2 };
        title.Children.Add(new TextBlock { Text = "Running now", Foreground = StrongBrush(), FontSize = 20, FontWeight = FontWeights.SemiBold });
        title.Children.Add(new TextBlock { Text = "Every live session — multiplex (PC-hosted) and loose local agents. Open, file into a collection, or kill.", Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        header.Children.Add(title);
        var refresh = new Button { Style = (Style)Resources["PillButtonStyle"], Content = new TextBlock { Text = "Refresh", FontSize = 12 } };
        refresh.Click += (_, _) => RenderRunningPage();
        Grid.SetColumn(refresh, 1);
        header.Children.Add(refresh);
        MainContent.Children.Add(header);

        var loading = new TextBlock { Text = "Scanning…", Foreground = MutedBrush(), FontSize = 13 };
        MainContent.Children.Add(loading);
        _ = LoadRunningAsync(seq, loading);
    }

    private async Task LoadRunningAsync(int seq, TextBlock loading)
    {
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();

        // both scans in parallel: relay session list (over our ssh) + local process sweep
        var muxTask = Task.Run(async () =>
        {
            var rows = new List<MuxRow>();
            if (string.IsNullOrEmpty(target)) return rows;
            try
            {
                var (_, outText) = await RunSshAsync(target, $"curl -s http://127.0.0.1:{settings.MultiplexApiPort}/api/sessions");
                using var doc = JsonDocument.Parse(outText);
                foreach (var s in doc.RootElement.EnumerateArray())
                {
                    rows.Add(new MuxRow(
                        s.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        s.TryGetProperty("state", out var st) ? st.GetString() ?? "white" : "white",
                        s.TryGetProperty("agentLabel", out var al) ? al.GetString() ?? "" : "",
                        s.TryGetProperty("agentDetail", out var ad) ? ad.GetString() ?? "" : "",
                        s.TryGetProperty("hosted", out var h) && h.GetBoolean(),
                        s.TryGetProperty("alive", out var alive) && alive.GetBoolean(),
                        s.TryGetProperty("autoheal", out var a) && a.GetBoolean(),
                        s.TryGetProperty("activity", out var ac) ? ac.GetInt64() : 0,
                        s.TryGetProperty("attached", out var at) && at.GetBoolean()));
                }
            }
            catch { }
            return rows;
        });
        var localTask = Task.Run(() => EnrichRunningSessionTitles(ResolveMissingSessionIds(GetRunningSessions())));
        await Task.WhenAll(muxTask, localTask);
        if (seq != _runningSeq || _screen != "Running") return;   // navigated away / re-rendered meanwhile

        var mux = muxTask.Result.Where(m => m.Name.Length > 0).OrderByDescending(m => m.Activity).ToList();
        var muxIds = new HashSet<string>(mux.Where(m => m.Alive).Select(m => m.Name), StringComparer.OrdinalIgnoreCase);
        // a local process whose session is a multiplex one is the SAME agent seen from the OS side — don't list it twice
        var local = localTask.Result
            .Where(r => string.IsNullOrEmpty(r.SessionId)
                        || FindArchiveSessionForRunning(r) is not { } s0
                        || !muxIds.Contains(ArchiveService.MultiplexSessionName(s0)))
            .ToList();

        MainContent.Children.Remove(loading);
        MainContent.Children.Add(SectionLabel($"Multiplex sessions ({mux.Count})"));
        if (mux.Count == 0) MainContent.Children.Add(new TextBlock { Text = string.IsNullOrEmpty(target) ? "No multiplex target configured (Settings)." : "None running.", Foreground = MutedBrush(), FontSize = 12, Margin = new Thickness(0, 0, 0, 10) });
        foreach (var m in mux) MainContent.Children.Add(MuxRowCard(m, target, settings.MultiplexApiPort));

        MainContent.Children.Add(SectionLabel($"Local agent processes ({local.Count})"));
        if (local.Count == 0) MainContent.Children.Add(new TextBlock { Text = "No loose local claude/codex processes.", Foreground = MutedBrush(), FontSize = 12 });
        foreach (var r in local) MainContent.Children.Add(LocalRowCard(r));
    }

    private TextBlock SectionLabel(string text) => new()
    { Text = text, Foreground = StrongBrush(), FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 6) };

    private static (string glyph, string label) StateBadge(string state, string agentLabel, string agentDetail)
    {
        var glyph = state switch
        {
            "green" or "yellow" or "red" or "detached" => "\u25CF",
            _ => "\u25CB",
        };
        var label = !string.IsNullOrWhiteSpace(agentLabel)
            ? agentLabel
            : state switch
            {
                "green" => "working",
                "yellow" => "waiting for you",
                "red" => "stopped/blocker",
                "detached" => "detached local agent",
                "dormant" => "dormant",
                _ => "plain shell",
            };
        if (!string.IsNullOrWhiteSpace(agentDetail)) label += " - " + agentDetail;
        return (glyph, label);
    }

    private Border RowCard(UIElement content) => new()
    {
        Background = PanelBrush(),
        BorderBrush = LineBrush(),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(14, 10, 14, 10),
        Margin = new Thickness(0, 0, 0, 8),
        Child = content
    };

    private Border MuxRowCard(MuxRow m, string target, int port)
    {
        var session = FindArchiveSessionByMuxName(m.Name);
        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var (glyph, stateLabel) = StateBadge(m.State, m.AgentLabel, m.AgentDetail);
        var dotColor = m.State switch
        {
            "green" => Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99),
            "yellow" => Windows.UI.Color.FromArgb(255, 0xFB, 0xBF, 0x24),
            "red" => Windows.UI.Color.FromArgb(255, 0xFB, 0x71, 0x85),
            _ => Windows.UI.Color.FromArgb(255, 0x8B, 0x8D, 0x96),
        };
        nameRow.Children.Add(new TextBlock { Text = glyph, Foreground = new SolidColorBrush(dotColor), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        nameRow.Children.Add(new TextBlock { Text = m.Name, Foreground = StrongBrush(), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (m.Hosted) nameRow.Children.Add(SmallChip("PC", "Runs on this PC (muxd) — survives network/VPS failures"));
        if (m.Armed) nameRow.Children.Add(SmallChip("auto", "Auto-resume is ON for this session"));
        left.Children.Add(nameRow);
        if (session is not null)
        {
            left.Children.Add(new TextBlock { Text = Trim(session.DisplayTitle, 110), Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });
        }
        var age = m.Activity > 0 ? $" · active {Ago(m.Activity)}" : "";
        left.Children.Add(new TextBlock { Text = stateLabel + age + (m.Attached ? " · viewer attached" : ""), Foreground = MutedBrush(), FontSize = 11 });
        grid.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        actions.Children.Add(SmallAction("Open", "Open this session in the multiplex web app", async () =>
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://harmonizerlabs.cc/multiplex/?s=" + Uri.EscapeDataString(m.Name)));
        }));
        if (session is not null)
        {
            var s = session;
            actions.Children.Add(SmallAction("Transcript", "Open this chat's transcript in the archive reader", () => { OpenSession(s); return Task.CompletedTask; }));
        }
        actions.Children.Add(SmallAction("Add to collection", "File this session's chat into a collection", () => { ShowMuxAddToCollectionFlyout(m.Name); return Task.CompletedTask; }));
        actions.Children.Add(SmallAction("Kill", "End this session (the agent stops)", async () =>
        {
            var dlg = new ContentDialog { Title = $"Kill \"{Trim(m.Name, 40)}\"?", Content = "Ends the session and stops its agent.", PrimaryButtonText = "Kill", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            await RunSshAsync(target, $"curl -s -X DELETE http://127.0.0.1:{port}/api/sessions/{m.Name}");
            SyncStatus.Text = $"Killed \"{m.Name}\".";
            RenderRunningPage();
        }));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return RowCard(grid);
    }

    private Border LocalRowCard(ArchiveService.RunningSessionInfo r)
    {
        var session = FindArchiveSessionForRunning(r);
        var title = session?.DisplayTitle ?? (string.IsNullOrEmpty(r.RealTitle) ? $"Unsaved {r.Tool} session" : r.RealTitle);

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var left = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
        var nameRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        nameRow.Children.Add(new TextBlock { Text = r.Tool == "codex" ? "CX" : "CL", Foreground = MutedBrush(), FontSize = 11, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var titleBlock = new TextBlock { Text = Trim(title, 64), Foreground = StrongBrush(), FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(titleBlock, session?.SourcePath ?? r.SessionId);
        nameRow.Children.Add(titleBlock);
        left.Children.Add(nameRow);
        if (!string.IsNullOrWhiteSpace(r.Preview))
        {
            left.Children.Add(new TextBlock { Text = Trim(r.Preview, 150), Foreground = MutedBrush(), FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }
        left.Children.Add(new TextBlock { Text = $"{r.Parent} · pid {r.Pid}" + (string.IsNullOrEmpty(r.SessionId) ? "" : $" · {r.SessionId[..Math.Min(8, r.SessionId.Length)]}"), Foreground = MutedBrush(), FontSize = 11 });
        grid.Children.Add(left);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (!string.IsNullOrWhiteSpace(r.SessionId))
        {
            var knownSession = session;
            actions.Children.Add(SmallAction("Transcript", "Open the transcript for this running process", async () => await OpenRunningTranscriptAsync(r, knownSession)));
        }
        if (session is not null)
        {
            var s = session;
            actions.Children.Add(SmallAction("Add to collection", "File this chat into a collection", () => { ShowSessionAddToCollectionFlyout(s); return Task.CompletedTask; }));
        }
        actions.Children.Add(SmallAction("Kill", "Kill this local agent process", async () =>
        {
            var dlg = new ContentDialog { Title = $"Kill {r.Tool} (pid {r.Pid})?", Content = "Ends that running agent on this PC.", PrimaryButtonText = "Kill", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close, XamlRoot = XamlRoot };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            TryKillChat(r.Pid);
            SyncStatus.Text = $"Killed pid {r.Pid}.";
            RenderRunningPage();
        }));
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);
        return RowCard(grid);
    }

    private ArchiveSession? FindArchiveSessionForRunning(ArchiveService.RunningSessionInfo running) =>
        FindArchiveSessionByIdOrAlias(running.SessionId, running.Tool);

    private ArchiveSession? FindArchiveSessionByIdOrAlias(string? id, string? tool)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return _archive.ResolveTargetSession(new AgentCommand { id = id.Trim(), tool = tool });
    }

    private ArchiveSession? FindArchiveSessionByMuxName(string muxName)
    {
        if (string.IsNullOrWhiteSpace(muxName)) return null;
        return _archive.Store.Sessions.Values.FirstOrDefault(s =>
            string.Equals(ArchiveService.MultiplexSessionName(s), muxName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task OpenRunningTranscriptAsync(ArchiveService.RunningSessionInfo running, ArchiveSession? knownSession)
    {
        var session = knownSession ?? BuildTransientRunningSession(running);
        if (session is null)
        {
            await ShowInfoAsync(
                "Transcript not found",
                string.IsNullOrWhiteSpace(running.SessionId)
                    ? "This running process did not expose a session id."
                    : $"No local transcript file was found for {running.Tool} session {running.SessionId}.");
            return;
        }

        OpenSession(session);
        SyncStatus.Text = $"Opened transcript for \"{Trim(session.DisplayTitle, 40)}\".";
    }

    private ArchiveSession? BuildTransientRunningSession(ArchiveService.RunningSessionInfo running)
    {
        if (string.IsNullOrWhiteSpace(running.SessionId)) return null;
        var path = RunningTranscriptPath(running.Tool, running.SessionId);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;

        var title = !string.IsNullOrWhiteSpace(running.RealTitle)
            ? running.RealTitle
            : $"{running.Tool} running session {ShortId(running.SessionId)}";
        var updated = DateTime.UtcNow.ToString("O");
        try { updated = File.GetLastWriteTimeUtc(path).ToString("O"); } catch { }

        return new ArchiveSession
        {
            Id = running.SessionId,
            Tool = string.Equals(running.Tool, "claude", StringComparison.OrdinalIgnoreCase) ? "claude" : "codex",
            Title = title,
            SourcePath = path,
            CreatedAt = updated,
            UpdatedAt = updated,
            Workspace = running.Cwd,
            WorkspaceName = string.IsNullOrWhiteSpace(running.Cwd) ? "Running now" : Path.GetFileName(running.Cwd.TrimEnd('\\', '/')),
        };
    }

    private static string? RunningTranscriptPath(string tool, string sessionId) =>
        string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)
            ? ArchiveService.ReadCodexRolloutPath(CodexDbPath, sessionId)
            : FindClaudeTranscript(sessionId);

    private static string ShortId(string id) => id.Length <= 8 ? id : id[..4] + id[^4..];

    private Border SmallChip(string text, string tip)
    {
        var chip = new Border
        {
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(90, 0x34, 0xD3, 0x99)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5, 1, 5, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = text, Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99)), FontSize = 9, FontWeight = FontWeights.Bold }
        };
        ToolTipService.SetToolTip(chip, tip);
        return chip;
    }

    private Button SmallAction(string text, string tip, Func<Task> onClick)
    {
        var b = new Button { Style = (Style)Resources["PillButtonStyle"], Padding = new Thickness(10, 4, 10, 4), MinHeight = 0, Content = new TextBlock { Text = text, FontSize = 11 } };
        ToolTipService.SetToolTip(b, tip);
        b.Click += async (_, _) => { try { await onClick(); } catch (Exception ex) { SyncStatus.Text = "Action failed: " + ex.Message; } };
        return b;
    }

    private static string Ago(long unixMs)
    {
        var dt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        var span = DateTimeOffset.UtcNow - dt;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours}h ago";
        return $"{(int)span.TotalDays}d ago";
    }

    // Add a MULTIPLEX session to a collection: resolves the chat by its multiplex name (same mapping the
    // remote queue uses), then files it — all in-app, no web round-trip.
    private void ShowMuxAddToCollectionFlyout(string muxName)
    {
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        foreach (var col in _archive.Store.Collections.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = col.Name;
            var item = new MenuFlyoutItem { Text = name };
            item.Click += async (_, _) =>
            {
                var (ok, detail) = await AddSessionToCollectionAsync(muxName, name);
                SyncStatus.Text = ok ? $"Added \"{muxName}\" to \"{name}\"." : "Add failed: " + detail;
            };
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var newCol = new MenuFlyoutItem { Text = "New collection…" };
        newCol.Click += async (_, _) =>
        {
            var input = new TextBox { PlaceholderText = "Collection name", MinWidth = 320, CornerRadius = ControlCornerRadius() };
            var dlg = new ContentDialog { Title = "Add to new collection", Content = input, PrimaryButtonText = "Create & add", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
            if (await dlg.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
            {
                var (ok, detail) = await AddSessionToCollectionAsync(muxName, input.Text.Trim());
                SyncStatus.Text = ok ? $"Added \"{muxName}\" to \"{input.Text.Trim()}\"." : "Add failed: " + detail;
            }
        };
        flyout.Items.Add(newCol);
        flyout.ShowAt(MainContent);
    }

    private void ShowSessionAddToCollectionFlyout(CodexLocalRetrieval.Core.Models.ArchiveSession session)
    {
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        foreach (var col in _archive.Store.Collections.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = col.Name;
            var item = new MenuFlyoutItem { Text = name };
            item.Click += async (_, _) => { await _archive.AddToCollectionAsync(session, name); SyncStatus.Text = $"Added to \"{name}\"."; };
            flyout.Items.Add(item);
        }
        flyout.Items.Add(new MenuFlyoutSeparator());
        var newCol = new MenuFlyoutItem { Text = "New collection…" };
        newCol.Click += async (_, _) => await AddSessionToNewCollectionAsync(session);
        flyout.Items.Add(newCol);
        flyout.ShowAt(MainContent);
    }
}
