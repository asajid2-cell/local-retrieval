using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

// Resurface + resume: the features that make a chat reusable instead of lost after a month.
// Startup re-scans the live ~/.codex store; "Resume in terminal" actually reopens the agent
// session; "Add to project" files a chat into a persisted collection; the Source inspector
// shows the real rollout event timeline.
public sealed partial class MainPage
{
    private bool _syncing;

    // After the cached store has painted, resurface the live store so months-old chats reappear
    // and persist forward. The manual Sync button reuses the same path.
    private async Task StartupResurfaceAsync()
    {
        try { await _archive.EnrichTitlesFromLocalStateAsync(); } catch { }
        await SyncNowAsync(initial: true);
        try { StartProjectSync(); } catch { }   // keep the web's Projects view synced while the app is open
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await SyncNowAsync(initial: false);

    private async Task SyncNowAsync(bool initial)
    {
        if (_syncing) return;
        _syncing = true;
        var before = _archive.Store.Sessions.Count;
        var keepId = _selected?.Id;
        try
        {
            SyncButton.IsEnabled = false;
            SyncStatus.Text = initial ? "Resurfacing your chat history..." : "Syncing sessions...";
            // Progress<T> marshals callbacks to this (UI) thread. The heavy file parse runs on a
            // worker and touches no shared state; the store mutation + list refresh happen back on
            // the UI thread, so nothing races the renders (and launch never looks like it hung).
            var progress = new Progress<string>(s => SyncStatus.Text = s);
            var scan = await Task.Run(() => _archive.ScanDiskAsync(progress));
            var indexed = await _archive.MergeScanAsync(scan, refreshList: true);
            var added = _archive.Store.Sessions.Count - before;
            Diag.Log($"Sync: indexed {indexed}, store now {_archive.Store.Sessions.Count} (was {before}, +{added})");

            // MergeScanAsync's refresh now routes through OnReapplyFilter (ReapplyActiveFilter), which keeps
            // the active filter/sort AND the current selection — so a background sync no longer drops filters.
            _selected = (keepId is not null ? _archive.Sessions.FirstOrDefault(s => s.Id == keepId) : null)
                        ?? _archive.Sessions.FirstOrDefault();
            SelectSessionRow(_selected);
            RenderCurrent();
            SyncStatus.Text = $"{_archive.Sessions.Count} chats - synced {DateTime.Now:h:mm tt}"
                              + (added > 0 ? $" - +{added} new" : "");
        }
        catch (Exception ex)
        {
            Diag.Log("Sync error " + ex);
            SyncStatus.Text = "Sync failed - see log.";
        }
        finally
        {
            SyncButton.IsEnabled = true;
            _syncing = false;
        }
    }

    private void ResumeInTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) ResumeInTerminal(_selected);
    }

    // Open a real terminal and run `codex resume <id>` in the chat's original workspace, so the
    // agent session continues with the right cwd. This is the difference between an archive you
    // read and one you can pick back up.
    private async void ResumeInTerminal(ArchiveSession session, string trigger = "user")
    {
        var failureRecordedByGovernor = false;
        var launchStarted = false;
        try
        {
            // Guard against resuming a chat that's already running (locally or in a multiplex) - two
            // runs corrupt the transcript. Offers to kill the running copy first.
            if (!await ConfirmRunOrKillAsync(session))
            {
                RecordSessionEvent(
                    session,
                    "resume.refused.running",
                    "Terminal resume cancelled because the session already had a live owner.",
                    "warn",
                    details: new Dictionary<string, string> { ["trigger"] = trigger });
                SyncStatus.Text = "Cancelled - already running.";
                return;
            }

            var launch = _archive.BuildResumeLaunch(session);
            if (string.IsNullOrEmpty(launch.Exe))
            {
                Diag.Log("Resume refused: " + launch.DisplayCommand);
                RecordSessionEvent(
                    session,
                    "resume.refused.invalid",
                    launch.DisplayCommand,
                    "warn",
                    details: new Dictionary<string, string> { ["trigger"] = trigger });
                SyncStatus.Text = launch.DisplayCommand;
                return;
            }
            var cwd = Directory.Exists(launch.WorkingDirectory)
                ? launch.WorkingDirectory
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // HARD GATE (belt-and-suspenders, AFTER the dialog, regardless of what triggered this resume):
            // never start a 2nd OS process for a session that is live or already being launched. The claim
            // closes the check-then-spawn race across the GUI, headless server, and remote bridge.
            var request = new SessionLaunchRequest(
                session.Id,
                session.Aliases,
                session.Tool,
                "native",
                $"native terminal resume ({trigger})",
                "resume.refused.claim",
                "resume.started.terminal",
                "resume.failed.terminal",
                session.DisplayTitle,
                session.Workspace,
                new Dictionary<string, string> { ["trigger"] = trigger });
            if (!_launchGovernor.TryAcquire(request, out var lease, out var claimDetail))
            {
                Diag.Log($"Resume ABORTED - {claimDetail} (trigger={trigger}): {session.Id}");
                SyncStatus.Text = $"\"{Trim(session.DisplayTitle, 40)}\" is not being launched: {claimDetail}.";
                return;
            }
            using (lease)
            {
                try
                {
                    Diag.Log($"Resume launch [trigger={trigger}]: {launch.DisplayCommand} (cwd={cwd})");
                    var psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = $"/k \"{launch.DisplayCommand}\"",
                        WorkingDirectory = cwd,
                        UseShellExecute = true
                    };
                    var wrapper = Process.Start(psi);
                    launchStarted = true;
                    lease?.MarkStarted("Started terminal resume.");
                    RecordSessionOwner(session.Id, session.Aliases, wrapper, "terminal");
                }
                catch (Exception ex)
                {
                    failureRecordedByGovernor = true;
                    lease?.MarkFailed(ex.Message);
                    throw;
                }
            }
            // Bump to the top now so it's where you expect when you come back; a real message in the
            // resumed terminal will keep it there (and surface it in claude/codex's own picker too).
            session.UpdatedAt = DateTime.UtcNow.ToString("O");
            ReapplyActiveFilter();   // respects the active filter + spam-hide instead of dumping the whole store
            SelectSessionRow(session);
            await _archive.SaveAsync();
            SyncStatus.Text = $"Resuming \"{Trim(session.DisplayTitle, 40)}\" in a terminal...";
        }
        catch (Exception ex)
        {
            if (launchStarted)
            {
                Diag.Log("Resume metadata persistence FAILED after terminal launch " + ex);
                RecordSessionEvent(
                    session,
                    "resume.started.metadata-failed",
                    ex.Message,
                    "error",
                    details: new Dictionary<string, string> { ["trigger"] = trigger });
                SyncStatus.Text = "Terminal opened, but its recent-session metadata was not persisted - see log.";
            }
            else
            {
                Diag.Log("Resume launch FAILED " + ex);
                if (!failureRecordedByGovernor)
                    RecordSessionEvent(
                        session,
                        "resume.failed.terminal",
                        ex.Message,
                        "error",
                        details: new Dictionary<string, string> { ["trigger"] = trigger });
                SyncStatus.Text = "Could not open terminal - see log.";
            }
        }
        finally
        {
            if (ReferenceEquals(_selected, session)) RenderIntegrity(force: true);
        }
    }

    // Best-effort note of the wrapper process this app started for a session. Nothing consumes it yet, and
    // a wrapper is not the agent (see SessionOwnerRecords) - a failed write is logged and never blocks a launch.
    private static void RecordSessionOwner(string? sessionId, IEnumerable<string>? aliases, Process? wrapper, string transport)
    {
        try
        {
            if (!SessionOwnerRecords.TryWriteForProcess(sessionId, aliases, wrapper, transport, out var detail))
                Diag.Log($"Owner record not written ({transport}): {detail}");
        }
        catch (Exception ex) { Diag.Log("Owner record write threw " + ex); }
    }

    private static void RecordMuxSessionOwner(string? sessionId, IEnumerable<string>? aliases, string muxName)
    {
        try
        {
            // muxd publishes the shell pid to live-tabs.json asynchronously; if it isn't there yet we record
            // pid 0 and move on rather than polling for it.
            var shellPid = SessionOwnerRecords.TryReadMuxShellPid(muxName);
            if (!SessionOwnerRecords.TryWrite(sessionId, aliases, shellPid, null, "muxd", out var detail, muxName: muxName))
                Diag.Log($"Owner record not written (muxd {muxName}): {detail}");
        }
        catch (Exception ex) { Diag.Log("Owner record write threw " + ex); }
    }

    private static string BumpTooltip(ArchiveSession session) =>
        string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase)
            ? "Move this chat to the top of `codex resume` (no message sent)."
            : "Move this chat to the top of Claude's recent chats (no message sent).";

    // "Bump": float a chat to the top of Codex/Claude's OWN resume picker without sending a message.
    // ArchiveService refreshes the recency signal each picker reads (Codex threads.updated_at_ms /
    // Claude transcript mtime) and floats it in our list too.
    private async Task BumpSession(ArchiveSession session)
    {
        try
        {
            var (native, recovered) = await _archive.BumpSessionAsync(session);
            SelectSessionRow(session);
            RenderCurrent();
            var where = string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase) ? "codex resume" : "Claude's recent chats";
            SyncStatus.Text = !native
                ? $"Bumped \"{Trim(session.DisplayTitle, 40)}\" here - couldn't find it in {where}."
                : recovered
                    ? $"Recovered \"{Trim(session.DisplayTitle, 40)}\" (was hidden) and bumped it to the top of {where}."
                    : $"Bumped \"{Trim(session.DisplayTitle, 40)}\" to the top of {where}.";
        }
        catch (Exception ex)
        {
            Diag.Log("Bump FAILED " + ex);
            SyncStatus.Text = "Could not bump chat - see log.";
        }
    }

    private void AddToProject_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || sender is not FrameworkElement anchor) return;
        ShowAddToProjectFlyout(anchor, _selected);
    }

    private void ShowAddToProjectFlyout(FrameworkElement anchor, ArchiveSession session)
    {
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        BuildAddToCollectionItems(flyout.Items, new[] { session }, () => RenderCurrent());
        flyout.ShowAt(anchor);
    }

    // L4: the Source inspector, rebuilt as a real event timeline read straight from the rollout.
    private UIElement SourceEventsPanel(ArchiveSession session, IReadOnlyList<RawEvent> events)
    {
        var stack = new StackPanel { Spacing = 0 };
        stack.Children.Add(new TextBlock
        {
            Text = $"{events.Count} events - {session.MessageCount} messages - {session.CodeBlocks.Count} code blocks",
            Foreground = MutedBrush(),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 12)
        });

        if (events.Count == 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = "No events could be read from the source file.",
                Foreground = MutedBrush(),
                TextWrapping = TextWrapping.Wrap
            });
            return Card(stack);
        }

        foreach (var ev in events)
        {
            stack.Children.Add(EventRow(ev));
        }
        return Card(stack);
    }

    private UIElement EventRow(RawEvent ev)
    {
        var grid = new Grid
        {
            MinHeight = 30,
            Padding = new Thickness(0, 7, 0, 7),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(0, 1, 0, 0),
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(132) },
                new ColumnDefinition()
            }
        };
        grid.Children.Add(new Border
        {
            Background = EventTint(ev.Kind),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = EventLabel(ev.Kind),
                Foreground = StrongBrush(),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold
            }
        });
        var right = new StackPanel { Spacing = 2 };
        if (!string.IsNullOrWhiteSpace(ev.Preview))
        {
            right.Children.Add(new TextBlock
            {
                Text = ev.Preview,
                Foreground = StrongBrush(),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 3,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
        }
        var when = FormatTime(ev.Timestamp);
        if (!string.IsNullOrWhiteSpace(when))
        {
            right.Children.Add(new TextBlock { Text = when, Foreground = MutedBrush(), FontSize = 11 });
        }
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }

    private static string EventLabel(string kind) => kind switch
    {
        "message:user" => "you",
        "message:assistant" => "assistant",
        "user_message" => "you",
        "agent_message" => "assistant",
        "agent_reasoning" => "thinking",
        "reasoning" => "thinking",
        "function_call" => "tool call",
        "function_call_output" => "tool result",
        "exec_command_begin" => "command",
        "exec_command_end" => "command done",
        "token_count" => "tokens",
        "task_started" => "task start",
        "task_complete" => "task done",
        "session_meta" => "session",
        _ => kind.Replace('_', ' ')
    };

    private SolidColorBrush EventTint(string kind)
    {
        if (kind is "message:user" or "user_message") return AccentVerySoftBrush();
        if (kind is "function_call" or "function_call_output" or "exec_command_begin" or "exec_command_end")
            return new SolidColorBrush(Windows.UI.Color.FromArgb(36, 120, 170, 255));
        return new SolidColorBrush(Windows.UI.Color.FromArgb(28, 160, 160, 160));
    }

    private static string FormatTime(string value) =>
        DateTime.TryParse(value, out var d) ? d.ToLocalTime().ToString("MMM d, h:mm:ss tt") : "";

    private static string Trim(string value, int max) =>
        string.IsNullOrEmpty(value) || value.Length <= max ? value : value[..max] + "...";
}
