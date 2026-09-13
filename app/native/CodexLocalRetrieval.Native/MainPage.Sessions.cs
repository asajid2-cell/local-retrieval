using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
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
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private static readonly TimeSpan StartupSyncBudget = TimeSpan.FromSeconds(90);

    private void StartArchiveSourceWatches()
    {
        StopArchiveSourceWatches();
        foreach (var source in _archive.EffectiveSources())
        {
            if (!source.Enabled || string.IsNullOrWhiteSpace(source.Root)) continue;
            try
            {
                _archiveSourceWatches.Add(FileWatch.WatchDirectory(
                    source.Root,
                    "*.jsonl",
                    recurse: true,
                    () =>
                    {
                        Interlocked.Exchange(ref _archiveSyncPending, 1);
                        ScheduleArchiveSourceSync();
                    }));
            }
            catch (Exception ex) { Diag.Log("Archive source watch failed " + source.Root + ": " + ex.Message); }
        }
        Diag.Log("Archive source watches armed: " + _archiveSourceWatches.Count);
    }

    private void StopArchiveSourceWatches()
    {
        foreach (var watch in _archiveSourceWatches) { try { watch.Dispose(); } catch { } }
        _archiveSourceWatches.Clear();
    }

    private void ScheduleArchiveSourceSync()
    {
        if (Interlocked.CompareExchange(ref _archiveSyncWorkerActive, 1, 0) != 0) return;
        if (DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                while (Volatile.Read(ref _archiveSyncPending) == 1)
                {
                    if (_syncing)
                    {
                        await Task.Delay(100);
                        continue;
                    }
                    Interlocked.Exchange(ref _archiveSyncPending, 0);
                    await SyncNowAsync(initial: false);
                }
            }
            finally
            {
                Volatile.Write(ref _archiveSyncWorkerActive, 0);
                if (Volatile.Read(ref _archiveSyncPending) == 1) ScheduleArchiveSourceSync();
            }
        })) return;

        Volatile.Write(ref _archiveSyncWorkerActive, 0);
        Diag.Log("Archive source sync could not be queued on the UI dispatcher");
    }

    // After the cached store has painted, resurface the live store so months-old chats reappear
    // and persist forward. The manual Sync button reuses the same path.
    private async Task StartupResurfaceAsync()
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Diag.Log("Startup resurface: start");
        using var cancellation = new CancellationTokenSource(StartupSyncBudget);
        try
        {
            try { await _archive.EnrichTitlesFromLocalStateAsync(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            catch (Exception ex) { Diag.Log("Startup title enrichment failed: " + ex.Message); }
            await SyncNowAsync(initial: true, suppliedCancellationToken: cancellation.Token);
            try { StartProjectSync(); } catch (Exception ex) { Diag.Log("Project sync start failed: " + ex.Message); }   // keep the web's Projects view synced while the app is open
        }
        finally
        {
            Diag.Log("Startup resurface: end (" + stopwatch.ElapsedMilliseconds + " ms)");
        }
    }

    private async void Sync_Click(object sender, RoutedEventArgs e) => await SyncNowAsync(initial: false);

    private async Task SyncNowAsync(
        bool initial,
        CancellationToken suppliedCancellationToken = default,
        bool waitForActive = false)
    {
        if (!_pageInitialized && !initial) return;
        if (!waitForActive && _syncing) return;
        await _syncGate.WaitAsync();
        if (!waitForActive && _syncing)
        {
            _syncGate.Release();
            return;
        }
        _syncing = true;
        using var ownedCancellation = initial && !suppliedCancellationToken.CanBeCanceled
            ? new CancellationTokenSource(StartupSyncBudget)
            : null;
        var cancellationToken = suppliedCancellationToken.CanBeCanceled
            ? suppliedCancellationToken
            : ownedCancellation?.Token ?? CancellationToken.None;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var before = _archive.Store.Sessions.Count;
        var keepId = _selected?.Id;
        var selectionRevision = SelectionRevision;
        ArchiveSession? RestoreSelectionIfUnchanged()
        {
            var id = SelectionRaceGuard.Resolve(
                keepId,
                selectionRevision,
                SelectionRevision,
                _selected?.Id,
                _archive.Sessions.Select(s => s.Id));
            return id is not null
                ? _archive.Sessions.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                : null;
        }
        bool CanRestoreSelection() => SelectionRevision == selectionRevision;

        void RestoreSelectionIfStillAuthoritative()
        {
            if (!CanRestoreSelection()) return;
            var restored = RestoreSelectionIfUnchanged();
            RunSessionListRefresh(() =>
            {
                _selected = restored;
                SelectSessionRow(restored);
            });
            RenderCurrent();
        }
        try
        {
            SyncButton.IsEnabled = false;
            SyncStatus.Text = initial ? "Resurfacing your chat history..." : "Syncing sessions...";
            if (_archiveLoadFailed)
            {
                Diag.Log("Sync: retrying archive load before disk merge");
                await _archive.LoadAsync(cancellationToken);
                _archiveLoadFailed = false;
                if (CanRestoreSelection())
                {
                    var retryRestored = RestoreSelectionIfUnchanged();
                    RunSessionListRefresh(() =>
                    {
                        _selected = retryRestored;
                        SelectSessionRow(retryRestored);
                    });
                    RenderCurrent();
                }
                Diag.Log("Sync: archive load retry succeeded (" + _archive.Sessions.Count + " sessions)");
            }
            var progress = new Progress<string>(s => SyncStatus.Text = s);
            Diag.Log("Sync: scan start");
            var scan = await Task.Run(() => _archive.ScanDiskAsync(progress, cancellationToken), cancellationToken);
            Diag.Log("Sync: scan end (" + stopwatch.ElapsedMilliseconds + " ms)");
            var indexed = await _archive.MergeScanAsync(scan, refreshList: true, cancellationToken: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Diag.Log("Sync: merge end (" + stopwatch.ElapsedMilliseconds + " ms)");
            var added = _archive.Store.Sessions.Count - before;
            Diag.Log($"Sync: indexed {indexed}, store now {_archive.Store.Sessions.Count} (was {before}, +{added}, {stopwatch.ElapsedMilliseconds} ms)");
            RecordAppEvent(
                "sync.succeeded",
                $"Session sync indexed {indexed} chats; the store now has {_archive.Store.Sessions.Count} chats.",
                details: new Dictionary<string, string>
                {
                    ["operation"] = initial ? "startup.resurface" : "sync",
                    ["indexed"] = indexed.ToString(),
                    ["added"] = added.ToString()
                });

            // MergeScanAsync's refresh now routes through OnReapplyFilter (ReapplyActiveFilter), which keeps
            // the active filter/sort AND the current selection. Only restore the pre-scan selection when no
            // newer user click happened while the scan/merge was awaiting; otherwise that click is authoritative.
            RestoreSelectionIfStillAuthoritative();
            // If the selected chat disappeared, leave selection empty rather than navigating to an unrelated row.
            SyncStatus.Text = $"{_archive.Sessions.Count} chats - synced {DateTime.Now:h:mm tt}"
                              + (added > 0 ? $" - +{added} new" : "");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Diag.Log("Sync timed out/cancelled after " + stopwatch.ElapsedMilliseconds + " ms");
            SyncStatus.Text = initial
                ? "Initial resurface timed out; cached chats remain available. Click Sync sessions to retry."
                : "Sync cancelled; cached chats remain available.";
        }
        catch (Exception ex)
        {
            Diag.Log("Sync error after " + stopwatch.ElapsedMilliseconds + " ms: " + ex);
            RecordAppEvent(
                "sync.failed",
                ex.Message,
                "error",
                details: new Dictionary<string, string> { ["operation"] = initial ? "startup.resurface" : "sync" });
            SyncStatus.Text = "Sync failed: " + ex.Message;
        }
        finally
        {
            SyncButton.IsEnabled = true;
            _syncing = false;
            _syncGate.Release();
        }
    }

    private void ResumeInTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null)
            ResumeInTerminal(
                _selected,
                trigger: "user-native",
                launchModeOverride: ArchiveService.NativeLaunchMode);
    }

    private static string NativeResumeLabel(ArchiveSession session) =>
        string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase)
            ? "Resume as Codex"
            : "Resume as Claude";

    private static string GatewayResumeLabel(ArchiveSession session) =>
        string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase)
            ? "Continue in Gateway"
            : "Resume as Gateway";

    private static string GatewayResumeTooltip(ArchiveSession session) =>
        string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase)
            ? "Start a new Gateway chat and copy a handoff prompt for this Codex chat."
            : "Resume this Claude transcript through the custom cc Gateway.";

    private void ResumeAsGateway(ArchiveSession session)
    {
        if (ArchiveService.CanResumeThroughGateway(session.Tool))
        {
            ResumeInTerminal(session, trigger: "user-gateway", launchModeOverride: ArchiveService.GatewayLaunchMode);
            return;
        }

        if (string.Equals(session.Tool, "codex", StringComparison.OrdinalIgnoreCase))
        {
            _ = StartCodexGatewayHandoffAsync(session);
            return;
        }

        SyncStatus.Text = "Gateway continuation is unavailable for this chat.";
    }

    private void ResumeAsGateway_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) ResumeAsGateway(_selected);
    }

    private async Task StartCodexGatewayHandoffAsync(ArchiveSession source)
    {
        var prompt = "";
        try
        {
            prompt = await _archive.CopyPayloadAsync(source, "resume");
            if (string.IsNullOrWhiteSpace(prompt))
                throw new InvalidOperationException("The handoff prompt was empty.");
        }
        catch (Exception ex)
        {
            Diag.Log("Codex Gateway handoff prompt failed " + ex);
            SyncStatus.Text = "Couldn't build the Gateway handoff prompt: " + ex.Message;
            return;
        }

        var launch = _archive.BuildGatewayHandoffLaunch(source);
        if (string.IsNullOrWhiteSpace(launch.Exe))
        {
            SyncStatus.Text = launch.DisplayCommand;
            return;
        }

        var pendingIntentId = "";
        try
        {
            pendingIntentId = await _archive.QueuePendingNewChatAsync(
                "claude",
                launch.WorkingDirectory,
                customTitle: $"Gateway handoff: {source.DisplayTitle}",
                launchMode: ArchiveService.GatewayLaunchMode,
                handoffFromId: source.Id);
            if (pendingIntentId.Length == 0)
                throw new InvalidOperationException("The Gateway handoff filing intent could not be persisted.");

            SetClipboardText(prompt);
            var lease = _launchGovernor.BeginFresh(new SessionLaunchRequest(
                null,
                null,
                "claude",
                ArchiveService.GatewayLaunchMode,
                "gateway Codex handoff",
                "resume.refused.claim",
                "resume.started.terminal",
                "resume.failed.terminal",
                $"Gateway handoff: {source.DisplayTitle}",
                launch.WorkingDirectory,
                new Dictionary<string, string>
                {
                    ["sourceSessionId"] = source.Id,
                    ["pendingIntentId"] = pendingIntentId
                }));
            try
            {
                Process.Start(BuildGatewayTerminalStartInfo(launch));
                lease.MarkStarted("Started a fresh Gateway handoff terminal.");
            }
            catch
            {
                lease.MarkFailed("Could not open the Gateway handoff terminal.");
                throw;
            }
            finally
            {
                lease.Dispose();
            }

            RecordSessionEvent(
                source,
                "handoff.started.gateway",
                "Started a fresh Gateway chat; the handoff prompt was copied to the clipboard.",
                details: new Dictionary<string, string>
                {
                    ["pendingIntentId"] = pendingIntentId,
                    ["sourceSessionId"] = source.Id,
                    ["launchMode"] = ArchiveService.GatewayLaunchMode
                });
            SyncStatus.Text = "Started a new Gateway chat. The handoff prompt is on the clipboard; paste it into the terminal.";
            PollFileNewChatAsync(pendingIntentId, "", "claude");
        }
        catch (Exception ex)
        {
            if (pendingIntentId.Length > 0)
            {
                try { await _archive.CancelPendingNewChatAsync(pendingIntentId); } catch { }
            }
            Diag.Log("Codex Gateway handoff launch failed " + ex);
            SyncStatus.Text = "Could not open the Gateway handoff terminal: " + ex.Message;
        }
    }

    // Open a real terminal and run `codex resume <id>` in the chat's original workspace, so the
    // agent session continues with the right cwd. This is the difference between an archive you
    // read and one you can pick back up.
    private async void ResumeInTerminal(
        ArchiveSession session,
        string trigger = "user",
        string? launchModeOverride = null)
    {
        var failureRecordedByGovernor = false;
        var launchStarted = false;
        try
        {
            // Guard against resuming a chat that's already running (locally or in a multiplex) - two
            // runs corrupt the transcript. Offers to kill the running copy first.
            var guard = await ConfirmRunOrKillAsync(session);
            if (guard.KilledPids.Count > 0)
            {
                RecordSessionEvent(
                    session,
                    "kill.succeeded",
                    $"Stopped {guard.KilledPids.Count} running process{(guard.KilledPids.Count == 1 ? "" : "es")} before resume.",
                    details: new Dictionary<string, string>
                    {
                        ["operation"] = "resume.takeover",
                        ["processCount"] = guard.KilledPids.Count.ToString()
                    });
            }
            if (guard.Outcome == RunGuardOutcome.Unverifiable)
            {
                // NOT "already running" - the scan never got an answer. Recording this as a live owner
                // would put a statement into the ledger that nothing ever verified.
                RecordSessionEvent(
                    session,
                    "resume.refused.unverified",
                    "Terminal resume refused because live-owner verification failed: " + guard.Detail,
                    "warn",
                    details: new Dictionary<string, string> { ["trigger"] = trigger });
                SyncStatus.Text = "Refused - couldn't verify whether this chat is already running.";
                return;
            }
            if (guard.Outcome == RunGuardOutcome.Cancelled || guard.Outcome == RunGuardOutcome.Live)
            {
                var takeoverFailed = guard.Outcome == RunGuardOutcome.Live;
                if (takeoverFailed)
                    RecordSessionEvent(
                        session,
                        "kill.failed",
                        guard.Detail,
                        "error",
                        details: new Dictionary<string, string> { ["operation"] = "resume.takeover" });
                RecordSessionEvent(
                    session,
                    "resume.refused.running",
                    takeoverFailed
                        ? "Terminal resume refused because the live owner could not be stopped: " + guard.Detail
                        : "Terminal resume cancelled because the session already had a live owner.",
                    "warn",
                    details: new Dictionary<string, string> { ["trigger"] = trigger });
                // On Live the guard already put the kill failure in SyncStatus - don't stomp it.
                if (!takeoverFailed) SyncStatus.Text = "Cancelled - already running.";
                return;
            }

            if (guard.KilledPids.Count > 0)
                ClearClaimsAfterVerifiedKill(session, session.Id, session.Aliases, guard.KilledPids, "terminal");

            var launch = _archive.BuildResumeLaunch(session, launchModeOverride: launchModeOverride);
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
                ArchiveService.NormalizeLaunchMode(launchModeOverride ?? session.LaunchMode),
                $"{ArchiveService.NormalizeLaunchMode(launchModeOverride ?? session.LaunchMode)} terminal resume ({trigger})",
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
                    var started = LaunchResumeWrapper(session, launchModeOverride);
                    if (!started.Ok) throw new InvalidOperationException(started.Detail);
                    launchStarted = true;
                    lease?.MarkStarted("Started terminal resume.");
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
            if (IsSelectedSession(session)) RenderIntegrity(force: true);
        }
    }

    // The terminal-start core of ResumeInTerminal, factored out so Reclaim's relaunch is literally the SAME
    // launch — same wrapper shape, same owner record, same job-object assignment — instead of a second copy
    // that would drift. The CALLER owns the reservation; this only starts the process. Safe off the UI thread:
    // .NET runs a UseShellExecute start on its own STA thread when the caller isn't one.
    private (bool Ok, string Detail) LaunchResumeWrapper(
        ArchiveSession session,
        string? launchModeOverride = null)
    {
        var launch = _archive.BuildResumeLaunch(session, launchModeOverride: launchModeOverride);
        if (string.IsNullOrEmpty(launch.Exe)) return (false, launch.DisplayCommand);
        var cwd = Directory.Exists(launch.WorkingDirectory)
            ? launch.WorkingDirectory
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var gateway = ArchiveService.IsGatewayLaunchMode(launchModeOverride ?? session.LaunchMode);
        var psi = gateway
            ? BuildGatewayTerminalStartInfo(launch)
            : new ProcessStartInfo
            {
                FileName = ArchiveService.ResolveCmdExe(),
                Arguments = $"/k \"{launch.DisplayCommand}\"",
                WorkingDirectory = cwd,
                UseShellExecute = true
            };
        if (launch.Environment is not null)
            foreach (var (name, value) in launch.Environment)
                psi.Environment[name] = value;
        var wrapper = Process.Start(psi);
        RecordSessionOwner(session.Id, session.Aliases, wrapper, "terminal");
        return (true, launch.DisplayCommand);
    }

    private static ProcessStartInfo BuildGatewayTerminalStartInfo(ResumeLaunch launch)
    {
        var arguments = ArchiveService.BuildGatewayTerminalArgumentList(launch);
        if (arguments.Count == 0)
            throw new InvalidOperationException("Gateway launch did not provide a structured command-shell argument vector.");

        var psi = new ProcessStartInfo
        {
            FileName = launch.Exe,
            WorkingDirectory = launch.WorkingDirectory,
            UseShellExecute = true
        };
        foreach (var argument in arguments) psi.ArgumentList.Add(argument);
        return psi;
    }

    // Best-effort note of the wrapper process this app started for a session, and the point where that wrapper
    // is put into a named job object [F#7] so a later Kill can take the launch down without a tree-enumeration
    // race. A wrapper is not the agent (see SessionOwnerRecords); a failed write or a failed job assignment is
    // logged and never blocks a launch - Kill just falls back to the snapshot tree kill.
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
            RecordSessionEvent(
                session,
                "recency.bump.succeeded",
                native
                    ? "Updated the chat's native and app recency."
                    : "Updated app recency, but the native picker record was not found.",
                native ? "info" : "warn",
                details: new Dictionary<string, string>
                {
                    ["nativeUpdated"] = native.ToString(),
                    ["recovered"] = recovered.ToString()
                });
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
            RecordSessionEvent(session, "recency.bump.failed", ex.Message, "error");
            SyncStatus.Text = "Could not bump chat: " + ex.Message;
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
