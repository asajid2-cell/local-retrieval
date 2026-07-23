using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    // 5s TTL, same window the old inline staleness check used.
    private readonly StaleGuardedRefresher<SessionIntegritySummary> _integrity = new(TimeSpan.FromSeconds(5));
    private int _reclaimSeq;
    private bool _reclaimRunning;

    private void RefreshIntegrity_Click(object sender, RoutedEventArgs e) => RenderIntegrity(force: true);

    // B4. The integrity oracle walks the store, the process table and the claim files; run on the UI thread
    // that is a freeze the user reads as the app hanging. So this paints the CACHED panel for this chat first
    // and never waits: the rebuild goes off-thread behind a monotonic sequence guard and repaints only if it
    // is still the newest answer to the newest question. Same shape as RenderCustodyPage/LoadCustodyAsync.
    private void RenderIntegrity(bool force = false)
    {
        if (_selected is null)
        {
            _integrity.Invalidate();
            PaintIntegrity(null, checking: false);
            return;
        }

        var session = _selected;
        var key = IntegrityKey(session);
        var store = _archive.Store;
        var checking = force || _integrity.NeedsRefresh(key);

        PaintIntegrity(_integrity.CurrentFor(key), checking);
        if (!checking) return;
        _ = RefreshIntegrityAsync(session, key, store, force);
    }

    private async Task RefreshIntegrityAsync(ArchiveSession session, string key, AppStoreData store, bool force)
    {
        var outcome = await _integrity.RefreshAsync(key, () => SessionIntegrity.Build(store, session), force);

        // Superseded by a newer refresh, or the selection moved on while we were building: either way this
        // answer is no longer about what is on screen, and painting it would be a lie with a fresh timestamp.
        if (!outcome.IsCurrent || !ReferenceEquals(_selected, session)) return;
        if (outcome.Error is not null) Diag.Log("RenderIntegrity failed: " + outcome.Error);
        PaintIntegrity(outcome.Value, checking: false);
    }

    private void PaintIntegrity(SessionIntegritySummary? summary, bool checking)
    {
        IntegrityItems.Children.Clear();
        if (_selected is null)
        {
            SetRiskySessionActionsEnabled(false);
            IntegrityItems.Children.Add(new TextBlock { Text = "No chat selected", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }

        // Start-class actions stay off until a COMPLETED build says this chat is clear. Unverified is not
        // clear, and neither is mid-verification — so they disable while checking instead of the click
        // blocking on a synchronous build.
        SetRiskySessionActionsEnabled(
            !checking
            && summary is not null
            && !string.Equals(summary.Severity, "danger", StringComparison.OrdinalIgnoreCase));

        if (summary is null)
        {
            IntegrityItems.Children.Add(checking
                ? IntegrityChip("Checking integrity...")
                : IntegrityHeadline("danger", "Integrity checks failed. Refusing to treat this chat as clear."));
            return;
        }

        IntegrityItems.Children.Add(IntegrityHeadline(summary.Severity, summary.Headline));
        if (checking) IntegrityItems.Children.Add(IntegrityChip("Checking integrity..."));
        if (CanReclaim(summary))
            IntegrityItems.Children.Add(IntegrityReclaimButton());
        IntegrityItems.Children.Add(IntegrityMeta(summary));

        // Branch linkage — a branch links back to its original; a parent lists the branches taken off it.
        if (_selected.IsBranch)
            IntegrityItems.Children.Add(BranchLinkBlock(_selected));
        else
        {
            var branches = BranchesOf(_selected);
            if (branches.Count > 0) IntegrityItems.Children.Add(BranchesOfBlock(branches));
        }

        foreach (var check in summary.Checks)
            IntegrityItems.Children.Add(IntegrityCheckRow(check));

        if (summary.MuxTabs.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Mux custody", summary.MuxTabs.Take(3).Select(t =>
                $"{t.Name}: {(t.IsCurrent ? "current" : "history")}{(string.IsNullOrWhiteSpace(t.Kind) ? "" : " - " + t.Kind)}")));

        if (summary.LaunchClaims.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Launch claims", summary.LaunchClaims.Take(3).Select(c =>
                $"{(c.Expired ? "expired" : "active")} - {c.OwnerProcess} pid {c.OwnerPid}")));

        if (summary.PendingIntents.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Pending filing", summary.PendingIntents.Take(3).Select(p =>
                $"{p.Tool} - {p.Workspace}")));

        if (summary.RecentEvents.Count > 0)
            IntegrityItems.Children.Add(IntegrityEvidenceBlock("Recent events", summary.RecentEvents.Take(4).Select(e =>
                $"{e.Kind}: {Trim(e.Summary, 92)}")));
    }

    // M7. Reclaim is the take-control op, so its affordance may never depend on the oracle that take-control
    // exists to work around. ANY danger blocker offers it — including "could not verify" and the error-event
    // case, which is precisely what the old exact-id + {Live owner, Launch claim} gating hid: one
    // `resume.failed.terminal` in the ledger used to lock the recovery UI permanently. It also stays offered
    // while any launch reservation is still on disk (expired or active) even after severity has dropped to
    // "warn": a Reclaim that kills the live owner but then FAILS to delete the expired claim file must not
    // remove the only affordance left to retry. The rule lives in Core (SessionIntegrity.ReclaimAvailable) so it
    // is unit-testable; this just delegates.
    private static bool CanReclaim(SessionIntegritySummary summary)
        => SessionIntegrity.ReclaimAvailable(summary);

    private Button IntegrityReclaimButton()
    {
        var button = new Button
        {
            Content = "Reclaim",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Style = (Style)Resources["PrimaryPillButtonStyle"]
        };
        ToolTipService.SetToolTip(button, "Take control: stop every owner of this chat, clear the reservations that are safe to clear, then relaunch it");
        button.Click += async (_, _) => await ReclaimSelectedSessionAsync();
        return button;
    }

    // M3/M4. The whole flow runs off the UI thread behind a sequence guard; only the dialogs and the final
    // render come back. It addresses the FULL candidate set (id + every alias) everywhere — owner matching,
    // claim reading, claim clearing, record reading — because the blocker logic matches aliases too, and an
    // alias-held claim used to block forever while Unblock reported clean.
    private async Task ReclaimSelectedSessionAsync()
    {
        if (_selected is null || _reclaimRunning) return;
        var session = _selected;
        var candidateIds = ReclaimCandidateIds(session);

        var dialog = new ContentDialog
        {
            Title = "Reclaim this chat?",
            Content = new TextBlock
            {
                Text = "The app will stop every process it can tie to this chat, clear the launch reservations it is allowed to clear, prune stale mux custody, and relaunch it in a terminal. "
                       + "A reservation held by a live owner is NOT force-cleared — you'll be asked what to do.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460
            },
            PrimaryButtonText = "Reclaim",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var seq = ++_reclaimSeq;
        _reclaimRunning = true;
        SyncStatus.Text = "Reclaiming...";

        ReclaimReport report;
        try
        {
            var options = new ReclaimOptions
            {
                CandidateIds = candidateIds,
                Progress = message => Report(seq, message),
                PruneMuxCurrent = ids => PruneMuxCustodyForReclaim(ids),
                LaunchRequest = new SessionLaunchRequest(
                    session.Id,
                    session.Aliases,
                    session.Tool,
                    "native",
                    "native terminal resume (reclaim)",
                    "resume.refused.claim",
                    "resume.started.terminal",
                    "resume.failed.terminal",
                    session.DisplayTitle,
                    session.Workspace,
                    new Dictionary<string, string> { ["trigger"] = "reclaim" }),
                Launch = () => Task.FromResult(LaunchResumeWrapper(session)),
                OnRefusal = refusal => AskReclaimRefusalAsync(session, refusal),
                OnOverride = refusal =>
                {
                    // Ledgered BEFORE anything is forced: the operator owns this double-writer risk and the
                    // record has to survive whatever happens next.
                    RecordSessionEvent(
                        session,
                        "reclaim.override.double-writer-risk",
                        "Operator forced a launch past a live launch reservation: " + refusal.EvidenceLine,
                        "error",
                        details: new Dictionary<string, string>
                        {
                            ["claim"] = refusal.Claim.Path,
                            ["ownerPid"] = refusal.Claim.OwnerPid.ToString()
                        });
                    return Task.CompletedTask;
                }
            };

            report = await Task.Run(() => SessionReclaim.ExecuteAsync(options));
        }
        catch (Exception ex)
        {
            Diag.Log("Reclaim FAILED " + ex);
            _reclaimRunning = false;
            if (seq == _reclaimSeq) SyncStatus.Text = "Reclaim failed - see log.";
            return;
        }

        _reclaimRunning = false;
        if (seq != _reclaimSeq || !ReferenceEquals(_selected, session)) return;

        RecordReclaimEvents(session, report);
        await SyncNowAsync(initial: false);
        RenderIntegrity(force: true);
        SyncStatus.Text = report.Headline;
    }

    private void Report(int seq, string message)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (seq == _reclaimSeq) SyncStatus.Text = message;
        });
    }

    private static List<string> ReclaimCandidateIds(ArchiveSession session)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0) return;
            if (!ids.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase))) ids.Add(id);
        }
        Add(session.Id);
        foreach (var alias in session.Aliases) Add(alias);
        return ids;
    }

    // The refusal dialog states exactly what is known and nothing more: a verified pid when there is one, and
    // an honest "owner unknown" when the scan could not answer. The DEFAULT path is waiting the reservation out.
    private async Task<ReclaimRefusalChoice> AskReclaimRefusalAsync(ArchiveSession session, ReclaimRefusal refusal)
    {
        var tcs = new TaskCompletionSource<ReclaimRefusalChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enqueued = DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                var dialog = new ContentDialog
                {
                    Title = "A live launch reservation holds this chat",
                    Content = new TextBlock
                    {
                        Text = refusal.EvidenceLine + ".\n\n"
                               + "Waiting the reservation out is safe: it expires within two minutes and the app then takes control. "
                               + "Launching anyway risks two writers on the same transcript and silent message loss — it is recorded as an error in this chat's event log.",
                        TextWrapping = TextWrapping.Wrap,
                        MaxWidth = 460
                    },
                    PrimaryButtonText = "Wait for it to expire",
                    SecondaryButtonText = "Launch anyway",
                    CloseButtonText = "Stop",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = XamlRoot
                };
                var result = await dialog.ShowAsync();
                tcs.TrySetResult(result switch
                {
                    ContentDialogResult.Primary => ReclaimRefusalChoice.WaitForExpiry,
                    ContentDialogResult.Secondary => ReclaimRefusalChoice.LaunchAnyway,
                    _ => ReclaimRefusalChoice.Abort
                });
            }
            catch (Exception ex)
            {
                Diag.Log("Reclaim refusal dialog failed: " + ex);
                tcs.TrySetResult(ReclaimRefusalChoice.Abort);
            }
        });
        // No dispatcher (window closing): refuse rather than silently force.
        if (!enqueued) return ReclaimRefusalChoice.Abort;
        RecordSessionEvent(
            session,
            "reclaim.refused.live-claim",
            "Reclaim refused to force a live launch reservation: " + refusal.Detail,
            "warn",
            details: new Dictionary<string, string> { ["claim"] = refusal.Claim.Path });
        return await tcs.Task;
    }

    private void RecordReclaimEvents(ArchiveSession session, ReclaimReport report)
    {
        RecordSessionEvent(
            session,
            "reclaim.kill",
            report.KillOk ? "Reclaim stopped this chat's owners: " + report.KillDetail : "Reclaim could not stop every owner: " + report.KillDetail,
            report.KillOk ? "info" : "warn");

        foreach (var claim in report.Claims)
        {
            switch (claim.Outcome)
            {
                case ReclaimClearOutcome.Cleared:
                    RecordSessionEvent(session, "reclaim.claim.cleared", claim.Detail, claim.Overridden ? "warn" : "info");
                    break;
                case ReclaimClearOutcome.LaunchInFlight:
                    RecordSessionEvent(session, "reclaim.launch-in-flight", claim.Detail, "warn");
                    break;
                case ReclaimClearOutcome.RefusedAliveOwner:
                    RecordSessionEvent(session, "reclaim.refused.live-claim", claim.Detail, "warn");
                    break;
                default:
                    RecordSessionEvent(session, "reclaim.claim.failed", claim.Detail, "warn");
                    break;
            }
        }

        if (report.PrunedMuxTabs.Count > 0)
            RecordSessionEvent(
                session,
                "reclaim.mux.pruned",
                "Pruned stale mux custody for: " + string.Join(", ", report.PrunedMuxTabs));

        if (report.Relaunched)
            RecordSessionEvent(session, "reclaim.relaunched", "Reclaim relaunched this chat: " + report.RelaunchDetail);
        else if (report.LostLaunchRace)
            RecordSessionEvent(session, "reclaim.relaunch.lost-race", report.RelaunchDetail, "warn");
    }

    // B4. Async-aware: the click never waits on the oracle. It answers from the last COMPLETED build and kicks
    // an off-thread re-verification, which disables the start-class buttons for its duration. With no
    // completed build for this chat the answer is "blocked" — fail closed, exactly as the synchronous version
    // did when Build threw.
    private bool RiskySessionActionBlocked()
    {
        if (_selected is null) return true;
        var summary = _integrity.CurrentFor(IntegrityKey(_selected));
        RenderIntegrity(force: true);
        return summary is null
               || string.Equals(summary.Severity, "danger", StringComparison.OrdinalIgnoreCase);
    }

    private void SetRiskySessionActionsEnabled(bool enabled)
    {
        ResumeTerminalButton.IsEnabled = enabled;
        ResumeMultiplexButton.IsEnabled = enabled;
        ResumeHeadlessMultiplexButton.IsEnabled = enabled;
        CopyCommandButton.IsEnabled = enabled;
    }

    private string IntegrityKey(ArchiveSession session)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length > 0) ids.Add(id);
        }
        Add(session.Id);
        foreach (var alias in session.Aliases) Add(alias);

        var collections = _archive.Store.Collections.Values
            .Where(c => c.SessionIds.Any(id => ids.Contains(id)))
            .Select(c => c.Id)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var pending = _archive.Store.PendingNewChats.Count(p =>
            string.Equals(p.Tool, session.Tool, StringComparison.OrdinalIgnoreCase)
            && string.Equals(NormalizeIntegrityPath(p.Cwd), NormalizeIntegrityPath(session.Workspace), StringComparison.OrdinalIgnoreCase));
        var mux = _archive.Store.MuxTabHistory
            .Where(kv => (kv.Value.Current is not null && ids.Contains(kv.Value.Current.Id))
                         || kv.Value.History.Any(h => ids.Contains(h.Id)))
            .Select(kv => kv.Key + ":" + (kv.Value.Current?.Id ?? "") + ":" + kv.Value.History.Count)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);

        return string.Join("|", new[]
        {
            session.Id,
            string.Join(",", session.Aliases.OrderBy(a => a, StringComparer.OrdinalIgnoreCase)),
            session.SourcePath,
            session.UpdatedAt,
            string.Join(",", collections),
            pending.ToString(),
            string.Join(",", mux)
        });
    }

    private static string NormalizeIntegrityPath(string? path)
    {
        path = (path ?? "").Trim();
        if (path.Length == 0) return "";
        try { path = System.IO.Path.GetFullPath(path); } catch { }
        return path.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }

    private Border IntegrityHeadline(string severity, string text)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Children =
            {
                IntegrityDot(severity),
                new TextBlock
                {
                    Text = IntegrityLabel(severity),
                    Foreground = IntegrityBrush(severity),
                    FontSize = 12,
                    FontWeight = FontWeights.SemiBold,
                    VerticalAlignment = VerticalAlignment.Center
                }
            }
        });
        stack.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = StrongBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        });

        return new Border
        {
            Background = IntegrityBackground(severity),
            BorderBrush = IntegrityBorderBrush(severity),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Child = stack
        };
    }

    private UIElement IntegrityMeta(SessionIntegritySummary summary)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(IntegrityChip(summary.Tool.Equals("claude", StringComparison.OrdinalIgnoreCase) ? "CL" : "CX"));
        row.Children.Add(IntegrityChip(summary.SourceStatus));
        row.Children.Add(IntegrityChip(summary.Collections.Count == 0 ? "unfiled" : summary.Collections.Count + " collection" + (summary.Collections.Count == 1 ? "" : "s")));
        return row;
    }

    private UIElement IntegrityCheckRow(SessionIntegrityCheck check)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
            },
            ColumnSpacing = 8
        };
        grid.Children.Add(IntegrityDot(check.Severity));
        var text = new StackPanel { Spacing = 1 };
        text.Children.Add(new TextBlock
        {
            Text = check.Name,
            Foreground = StrongBrush(),
            FontSize = 12,
            FontWeight = FontWeights.SemiBold
        });
        text.Children.Add(new TextBlock
        {
            Text = check.Summary,
            Foreground = MutedBrush(),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 15
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        return grid;
    }

    private UIElement IntegrityEvidenceBlock(string title, IEnumerable<string> lines)
    {
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontSize = 12, FontWeight = FontWeights.SemiBold });
        foreach (var line in lines)
        {
            stack.Children.Add(new TextBlock
            {
                Text = line,
                Foreground = MutedBrush(),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 15
            });
        }
        return new Border
        {
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 7, 9, 7),
            Child = stack
        };
    }

    private Border IntegrityChip(string text) => new()
    {
        BorderBrush = LineBrush(),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 2, 7, 2),
        Child = new TextBlock { Text = text, Foreground = MutedBrush(), FontSize = 10, FontWeight = FontWeights.SemiBold }
    };

    private UIElement IntegrityDot(string severity) => new FontIcon
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        Glyph = severity switch
        {
            "danger" => "\uE783",
            "warn" => "\uE7BA",
            "ok" => "\uE73E",
            _ => "\uE946"
        },
        FontSize = 12,
        Foreground = IntegrityBrush(severity),
        VerticalAlignment = VerticalAlignment.Center
    };

    private string IntegrityLabel(string severity) => severity switch
    {
        "danger" => "BLOCKED",
        "warn" => "REVIEW",
        "ok" => "CLEAR",
        _ => "UNKNOWN"
    };

    private SolidColorBrush IntegrityBrush(string severity) => severity switch
    {
        "danger" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0x71, 0x85)),
        "warn" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xFB, 0xBF, 0x24)),
        "ok" => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x34, 0xD3, 0x99)),
        _ => MutedBrush()
    };

    private SolidColorBrush IntegrityBorderBrush(string severity) => severity switch
    {
        "danger" => new SolidColorBrush(Windows.UI.Color.FromArgb(170, 0xFB, 0x71, 0x85)),
        "warn" => new SolidColorBrush(Windows.UI.Color.FromArgb(150, 0xFB, 0xBF, 0x24)),
        "ok" => new SolidColorBrush(Windows.UI.Color.FromArgb(130, 0x34, 0xD3, 0x99)),
        _ => LineBrush()
    };

    private SolidColorBrush IntegrityBackground(string severity) => severity switch
    {
        "danger" => new SolidColorBrush(Windows.UI.Color.FromArgb(26, 0xFB, 0x71, 0x85)),
        "warn" => new SolidColorBrush(Windows.UI.Color.FromArgb(24, 0xFB, 0xBF, 0x24)),
        "ok" => new SolidColorBrush(Windows.UI.Color.FromArgb(18, 0x34, 0xD3, 0x99)),
        _ => PanelBrush()
    };
}
