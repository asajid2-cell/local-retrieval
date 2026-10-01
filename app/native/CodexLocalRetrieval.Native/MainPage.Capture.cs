using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval_Native;

// In-app capture/automation harness. Enabled when CLR_CAPTURE=1. The app polls a
// JSON command file (request.json) in CLR_CAP_DIR; for each request it runs the
// steps (navigate / resize window / type / click / screenshot / dump layout) on the
// UI thread, self-captures via RenderTargetBitmap (no external window handle needed),
// dumps the visual tree with bounds, and writes result-<id>.json. This replaces the
// Playwright battery for this WinUI app and works with `dotnet watch` live reload.
public sealed partial class MainPage
{
    private static string CapDir =>
        Environment.GetEnvironmentVariable("CLR_CAP_DIR") ?? Path.Combine(Path.GetTempPath(), "clrcap");

    private long _lastCapReqId = -1;
    private bool _capBusy;
    // Held in a field so the timer isn't garbage-collected (a local ref would stop ticking).
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _capTimer;

    private void StartCaptureHarness()
    {
        if (Environment.GetEnvironmentVariable("CLR_CAPTURE") != "1" || _capTimer is not null) return;
        try { Directory.CreateDirectory(Path.Combine(CapDir, "out")); } catch { }
        Diag.Log("Capture harness ENABLED, dir=" + CapDir);
        _capTimer = DispatcherQueue.CreateTimer();
        _capTimer.Interval = TimeSpan.FromMilliseconds(350);
        _capTimer.Tick += async (_, _) => await PollCaptureAsync();
        _capTimer.Start();
        try { File.WriteAllText(Path.Combine(CapDir, "ready.txt"), DateTime.Now.ToString("o")); } catch { }
    }

    private void StopCaptureHarness()
    {
        _capTimer?.Stop();
        _capTimer = null;
        _capBusy = false;
    }

    private async Task PollCaptureAsync()
    {
        if (_capBusy) return;
        var reqPath = Path.Combine(CapDir, "request.json");
        if (!File.Exists(reqPath)) return;
        CapRequest? req;
        try { req = JsonSerializer.Deserialize<CapRequest>(File.ReadAllText(reqPath)); }
        catch { return; }
        if (req is null || req.id == _lastCapReqId) return;

        _capBusy = true;
        _lastCapReqId = req.id;
        var artifacts = new List<string>();
        var errors = new List<string>();
        try
        {
            Diag.Log($"Capture req {req.id} ({req.steps?.Count ?? 0} steps)");
            foreach (var step in req.steps ?? new List<CapStep>())
            {
                try { await RunStepAsync(step, artifacts); }
                catch (Exception ex)
                {
                    errors.Add(step + ": " + ex.Message);
                    Diag.Log("step error " + ex);
                    break;
                }
            }
        }
        finally
        {
            var res = JsonSerializer.Serialize(new { id = req.id, ok = errors.Count == 0, screen = _screen, artifacts, errors });
            try { File.WriteAllText(Path.Combine(CapDir, $"result-{req.id}.json"), res); } catch { }
            Diag.Log($"Capture req {req.id} DONE ({artifacts.Count} artifacts, {errors.Count} errors)");
            _capBusy = false;
        }
    }

    // The only clicks the isolated fixture may perform: pure frame toggles. They rearrange the shell
    // (pane visibility, inspector tab, chat scope) against disposable metadata - no navigation to a live
    // screen, no launch, no store mutation - so the fixture can still verify every chrome state.
    private static readonly string[] FixtureShellToggles =
    {
        "SidebarToggleButton", "ToolsButton", "ToolsBackButton", "SettingsButton", "TopBarCollapseButton",
        "InspectorButton", "InspectorPinButton", "InspectorCloseButton",
        "InspectorTabIntegrity", "InspectorTabContext", "InspectorTabActivity", "InspectorTabActions",
        "ScopeActiveButton", "ScopeArchivedButton", "ScopeAllButton",
    };

    private static bool IsFixtureShellToggle(string text) =>
        Array.Exists(FixtureShellToggles, name => string.Equals(name, text, StringComparison.Ordinal));

    private async Task RunStepAsync(CapStep step, List<string> artifacts)
    {
        if (GuiVerificationFixture.Enabled)
        {
            if (step.nav is not (null or "Archive") || step.clipboard is true
                || step.vet is not null || step.vetClose is true || step.vetMany is not null || step.unvetMany is not null
                || step.confirm is not null
                || (step.click is not null && !IsFixtureShellToggle(step.click)))
                throw new InvalidOperationException("GUI metadata fixture permits read-only capture steps only");
            foreach (var name in new[] { step.shot, step.dump, step.snapshot, step.bulkMenu, step.dumpList })
                if (name is not null && !System.Text.RegularExpressions.Regex.IsMatch(name, @"\A[A-Za-z0-9_-]{1,80}\z"))
                    throw new InvalidOperationException("GUI fixture artifact name refused");
        }
        if (step.nav is not null) Navigate(step.nav);
        if (step.resize is { Length: 2 }) MainWindow.Instance?.AppWindow.Resize(new Windows.Graphics.SizeInt32(step.resize[0], step.resize[1]));
        if (step.type is not null) { SearchBox.Text = step.type; ApplySearch(step.type); }

        // Dismiss an open popup. CaptureSubject prefers the topmost popup, so while the filter flyout is up a
        // shot renders the flyout and never the page behind it; a step that wants to photograph the list it
        // just changed has to put the flyout away first.
        if (step.closeFlyout is true)
        {
            _filterFlyout?.Hide();
            _collFilterFlyout?.Hide();
            await SettleAsync(3);
        }

        // Open the vet dialog for a chat and LEAVE it open, so the next shot can render it. A later
        // vetClose step dismisses it. Nothing is committed unless vetName is given - and then the dialog's
        // REAL fields are filled and its REAL primary button pressed, so the run goes through the same
        // gate a user's typing and click does (validate -> VetSessionAsync -> rename -> tier move).
        if (step.vet is not null)
        {
            var target = _archive.Store.Sessions.Values.FirstOrDefault(s =>
                string.Equals(s.Id, step.vet, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Session '{step.vet}' was not found.");
            _selected = target;
            SelectSessionRow(target);
            var pending = VetDialogAsync(target);
            await SettleAsync(4);
            if (step.vetName is not null || step.vetPhrase is not null)
            {
                SetPopupBoxText("VetNameBox", step.vetName);
                SetPopupBoxText("VetPhraseBox", step.vetPhrase);
                await SettleAsync(3);
                InvokePopupButton(_openVetDialog?.PrimaryButtonText ?? "Vet");
                // The commit saves the store and rewrites the transcript's own title, so it needs longer
                // than a layout settle. Awaiting the dialog's own task covers ShowAsync returning; the
                // fixed tail covers the store write that follows it.
                await pending;
                await Task.Delay(1200);
            }
            else
            {
                _ = pending;   // the dialog stays open on purpose; a later vetClose step dismisses it
            }
        }
        // Prove the name box adopts the hint the way pressing Tab does. A KeyRoutedEventArgs cannot be
        // constructed from outside the framework, so the run drives the OTHER real hook on the same
        // gesture: focusing the field and then moving focus off it is what Tab does, and the box's own
        // LostFocus handler runs. The two boxes are then recorded as they stand.
        if (step.vetBlur is true)
        {
            TextBox? PopupBox(string name)
            {
                foreach (var root in PopupRoots())
                {
                    var box = FindNamed<TextBox>(root, name);
                    if (box is not null) return box;
                }
                return null;
            }

            var nameBox = PopupBox("VetNameBox")
                ?? throw new InvalidOperationException("No open dialog has a 'VetNameBox' field.");
            var phraseBox = PopupBox("VetPhraseBox");
            nameBox.Focus(FocusState.Programmatic);
            await SettleAsync(2);
            phraseBox?.Focus(FocusState.Programmatic);
            await SettleAsync(3);
            if (step.vetFields is not null)
            {
                var path = Path.Combine(CapDir, "out", step.vetFields + ".json");
                File.WriteAllText(path, JsonSerializer.Serialize(new
                {
                    nameHint = nameBox.PlaceholderText,
                    name = nameBox.Text,
                    phrase = phraseBox?.Text ?? "",
                    primaryEnabled = _openVetDialog?.IsPrimaryButtonEnabled
                }));
                artifacts.Add(path);
            }
        }
        if (step.vetClose is true)
        {
            _openVetDialog?.Hide();
            await SettleAsync(2);
        }

        // A batch vet: the SAME gate as `vet`, but opened once per chat by the app itself, so the harness
        // waits for each dialog to appear instead of settling a fixed number of frames - the app commits
        // one chat before opening the next gate.
        if (step.vetMany is { Length: > 0 })
        {
            var batch = new List<ArchiveSession>();
            foreach (var id in step.vetMany)
                batch.Add(_archive.Store.Sessions.Values.FirstOrDefault(s =>
                    string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Session '{id}' was not found."));

            SessionList.SelectedItems.Clear();
            foreach (var target in batch) AddDisplayedSessionToSelection(target.Id);

            var pending = VetManyAsync(batch);
            ContentDialog? previous = null;
            for (var i = 0; i < batch.Count; i++)
            {
                var dialog = await NextVetDialogAsync(previous)
                    ?? throw new InvalidOperationException($"The gate for chat {i + 1} of {batch.Count} never opened.");
                previous = dialog;
                // The dialog object exists before its popup content is laid out, so let it open first: the
                // named fields are not in the tree yet when NextVetDialogAsync hands it over.
                await SettleAsync(4);
                if (step.vetManyNames is { Length: > 0 } names && i < names.Length) SetPopupBoxText("VetNameBox", names[i]);
                if (step.vetManyPhrases is { Length: > 0 } phrases && i < phrases.Length) SetPopupBoxText("VetPhraseBox", phrases[i]);
                await SettleAsync(6);
                // Shot of the gate itself, filled and still up: the per-chat screen a batch shows
                // ("Vet chat i of N") is the thing being proven, and it exists for a second or two only.
                if (i == 0 && step.vetManyShot is not null) artifacts.Add(await CaptureAsync(step.vetManyShot));
                InvokePopupButton(dialog.PrimaryButtonText ?? "Vet");
                await SettleAsync(2);
            }
            await pending;
            await SettleAsync(2);
        }

        // A bulk "send back to the general populace", driven through the same method the menu item's click
        // calls. A MenuFlyoutItem cannot be clicked from outside (its click is raised by the framework), so
        // the menu's labels are recorded by WriteMenuDump and the ACTION is proven here, against the same
        // selection the menu was built from.
        if (step.unvetMany is { Length: > 0 })
        {
            var back = new List<ArchiveSession>();
            foreach (var id in step.unvetMany)
                back.Add(_archive.Store.Sessions.Values.FirstOrDefault(s =>
                    string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
                    ?? throw new InvalidOperationException($"Session '{id}' was not found."));

            SessionList.SelectedItems.Clear();
            foreach (var target in back) AddDisplayedSessionToSelection(target.Id);
            await UnvetManyAsync(back);
            await SettleAsync(2);
        }

        // Press a button inside whatever dialog is open (a confirm step: the button is in the popup layer,
        // which InvokeByText - a walk from the page - cannot reach).
        if (step.confirm is not null)
        {
            InvokePopupButton(step.confirm);
            await SettleAsync(4);
            await Task.Delay(900);   // the action behind a confirm dialog writes before the next step reads
        }
        if (step.click is not null)
        {
            InvokeByText(step.click, step.expectedSessionId);
        }
        else if (step.expectedSessionId is not null)
        {
            RequireExpectedSelection(step.expectedSessionId, null);
        }
        if (step.clipboard is true
            && step.click is not ("CopyGatewayCommandButton" or "Copy Gateway command"))
            throw new InvalidOperationException("Clipboard snapshot requires a CopyGatewayCommandButton click in the same step.");
        if (step.selectSessionIds is { Length: > 0 })
        {
            SessionList.SelectedItems.Clear();
            foreach (var id in step.selectSessionIds) AddDisplayedSessionToSelection(id);
        }
        // The multi-selection menu is a popup, which the visual-tree dump cannot reach, so it is recorded
        // from the menu object itself - the same one a right-click shows, built from the same selection.
        // A step that wants the menu must select first (selectSessionIds), because the menu is built from
        // the selection, exactly as the pointer handler builds it.
        if (step.bulkMenu is not null)
        {
            var selection = SessionList.SelectedItems.OfType<ArchiveSession>().ToList();
            if (selection.Count == 0) throw new InvalidOperationException("bulkMenu needs a selection to act on.");
            var menu = BuildBulkMenu(RightClickTargets(selection[0]));
            artifacts.Add(WriteMenuDump(step.bulkMenu, menu));
            menu.ShowAt(SessionList, new Windows.Foundation.Point(40, 40));
            await SettleAsync(3);
            try { artifacts.Add(await CaptureAsync(step.bulkMenu + "-shot")); }
            finally { menu.Hide(); await SettleAsync(2); }
        }
        if (step.selectSessionId is not null) SelectDisplayedSession(step.selectSessionId);
        if (step.familyBadge is not null) ToggleFamilyRow(DisplayedRow(step.familyBadge, nameof(step.familyBadge)));
        if (step.leafGlyph is not null) GoToPatriarch(DisplayedRow(step.leafGlyph, nameof(step.leafGlyph)));
        if (step.waitMs is not null)
        {
            if (step.waitMs is < 0 or > 10000) throw new ArgumentOutOfRangeException(nameof(step.waitMs), "waitMs must be between 0 and 10000 milliseconds.");
            await Task.Delay(step.waitMs.Value);
        }
        await SettleAsync(step.wait ?? 3);
        if (step.shot is not null) artifacts.Add(await CaptureAsync(step.shot));
        if (step.dump is not null) artifacts.Add(DumpTree(step.dump));
        if (step.dumpList is not null) artifacts.Add(WriteListDump(step.dumpList));
        if (step.snapshot is not null) artifacts.Add(WriteStateSnapshot(step.snapshot));
        if (step.clipboard is true) artifacts.Add(await WriteClipboardSnapshotAsync());
        if (step.scroll is { Length: 1 })
        {
            // Move the reader's viewport. This is a programmatic ChangeView, so it deliberately does NOT
            // page: loading older messages is driven by a real wheel-up (or the "load earlier" button),
            // and a synthetic offset change must not be mistaken for that gesture.
            var target = Math.Clamp(MainScroller.VerticalOffset + step.scroll[0], 0, MainScroller.ScrollableHeight);
            MainScroller.ChangeView(null, target, null, disableAnimation: true);
        }
    }

    // The row a family-badge or leaf-glyph step acts on: it has to be a row of the list AS IT IS NOW, which
    // is what makes the step a test of the collapse - a folded member is not a row, so naming one fails
    // loudly instead of quietly acting on a chat that is not on screen.
    private ArchiveSession DisplayedRow(string id, string field)
    {
        foreach (var item in SessionList.Items)
            if (item is ArchiveSession session && string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase))
                return session;
        throw new InvalidOperationException($"{field} names '{id}', which is not a row in the current list.");
    }

    private void SelectDisplayedSession(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("selectSessionId must not be empty.", nameof(id));

        for (var index = 0; index < SessionList.Items.Count; index++)
        {
            if (SessionList.Items[index] is not ArchiveSession session
                || !string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase)) continue;

            var container = SessionList.ContainerFromIndex(index) as ListViewItem;
            if (container is null) throw new InvalidOperationException($"Session '{id}' is not displayed.");
            var peer = new ListViewItemDataAutomationPeer(session, new ListViewAutomationPeer(SessionList));
            if (peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
                throw new InvalidOperationException("Displayed session item has no selection automation pattern.");

            selection.Select();
            if (SessionList.SelectedItem is not ArchiveSession selected
                || !string.Equals(selected.Id, id, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Session selection did not settle on '{id}'.");
            return;
        }

        throw new InvalidOperationException($"Displayed session '{id}' was not found.");
    }

    // Grow the selection the way Ctrl-click does, so a bulk step acts on a real multi-selection; Select()
    // would collapse the selection back down to the one row.
    private void AddDisplayedSessionToSelection(string id)
    {
        for (var index = 0; index < SessionList.Items.Count; index++)
        {
            if (SessionList.Items[index] is not ArchiveSession session
                || !string.Equals(session.Id, id, StringComparison.OrdinalIgnoreCase)) continue;

            var peer = new ListViewItemDataAutomationPeer(session, new ListViewAutomationPeer(SessionList));
            if (peer.GetPattern(PatternInterface.SelectionItem) is not ISelectionItemProvider selection)
                throw new InvalidOperationException("Displayed session item has no selection automation pattern.");
            selection.AddToSelection();
            return;
        }

        throw new InvalidOperationException($"Displayed session '{id}' was not found.");
    }

    // A batch vet commits one chat before opening the next gate, so a fixed frame settle cannot know when
    // that happened: wait for the dialog on top to be a different one. Bounded by TIME, not frames: the
    // commit between two gates rewrites the chat's native title and saves the store (tens of seconds on a
    // large one), and a frame count would time out on exactly the stores this batch is for.
    private async Task<ContentDialog?> NextVetDialogAsync(ContentDialog? previous)
    {
        var deadline = DateTime.UtcNow.AddSeconds(150);
        while (DateTime.UtcNow < deadline)
        {
            if (_openVetDialog is not null && !ReferenceEquals(_openVetDialog, previous)) return _openVetDialog;
            await SettleAsync(1);
        }
        return null;
    }

    // The menu's own items, as an artifact: a flyout lives in the popup layer, so the labels a user sees are
    // recorded here rather than inferred from the tree.
    private string WriteMenuDump(string name, MenuFlyout menu)
    {
        var items = new List<object>();
        void Walk(IEnumerable<MenuFlyoutItemBase> entries)
        {
            foreach (var entry in entries)
            {
                if (entry is MenuFlyoutSubItem sub)
                {
                    items.Add(new { type = "sub", text = sub.Text, enabled = sub.IsEnabled });
                    Walk(sub.Items);
                }
                else if (entry is MenuFlyoutItem item) items.Add(new { type = "item", text = item.Text, enabled = item.IsEnabled });
                else if (entry is MenuFlyoutSeparator) items.Add(new { type = "separator", text = "", enabled = true });
            }
        }
        Walk(menu.Items);
        var path = Path.Combine(CapDir, "out", name + ".json");
        try { File.WriteAllText(path, JsonSerializer.Serialize(new { items })); } catch { }
        return path;
    }

    // The chat list's BOUND rows, in order: the tree dump carries no text, so a step that must name a row
    // (the bulk menu needs a real selection) reads it from here. Bounded by the list cap, so a huge scope
    // still writes a few hundred rows rather than the whole store.
    private string WriteListDump(string name)
    {
        var rows = new List<object>();
        foreach (var item in SessionList.Items)
        {
            if (item is not ArchiveSession session) continue;
            rows.Add(new
            {
                id = session.Id,
                title = session.Title,
                // The name the row actually shows, so a roster line can be checked against what the list
                // calls that chat rather than against the raw fields behind it.
                listTitle = session.ListTitle,
                customTitle = session.CustomTitle,
                vetted = session.Vetted,
                archived = session.Archived,
                phrases = session.SpecialPhrases.ToList(),
                updatedAt = session.UpdatedAt,
                // A grouped row is the family's row: it says so here, and the folded members are simply not
                // in this list at all, which is the whole claim the grouping has to prove.
                familyHead = session.IsFamilyHead,
                familyBadge = session.FamilyBadge,
                // Whether the family is opened, and the roster it shows while it is - the two things a click
                // on the badge changes.
                familyExpanded = session.FamilyExpanded,
                familyRoster = session.FamilyRoster.Select(m => m.RosterLine).ToList(),
                // A flat row that is a leaf of a family, and the glyph that takes it to its patriarch.
                isLeaf = session.IsLeaf,
                leafGlyph = session.LeafGlyph
            });
        }
        var path = Path.Combine(CapDir, "out", name + ".json");
        try { File.WriteAllText(path, JsonSerializer.Serialize(new { count = rows.Count, rows })); } catch { }
        return path;
    }

    private string WriteStateSnapshot(string name)
    {
        var selectedItemId = (SessionList.SelectedItem as ArchiveSession)?.Id;
        var path = Path.Combine(CapDir, "out", name + ".json");
        // Reader geometry + paging state: the transcript's centring and its scroll-up paging are both
        // invisible in a screenshot, so the snapshot carries the numbers the fixes are judged against.
        double mainX = 0, mainW = 0;
        try { mainX = MainContent.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0)).X; } catch { }
        try { mainW = MainContent.ActualWidth; } catch { }
        var scopes = _archive.ChatScopeCounts();
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            selectedId = _selected?.Id,
            selectedItemId,
            selectedVetted = _selected?.Vetted,
            selectedPhrases = _selected?.SpecialPhrases.ToList(),
            chatScope = _chatScope,
            scopeUnvetted = scopes.Unvetted,
            scopeActive = scopes.Active,
            scopeArchived = scopes.Archived,
            // Family grouping is derived state, so a run has to be able to tell "the switch is off" apart from
            // "the switch is on and there is nothing to fold" - both look like a flat list otherwise.
            groupFamilies = _groupFamilies,
            drawableFamilies = _archive.DrawableFamilyCount,
            leafFamiliesOnDisk = _archive.LeafFamilies.Families.Count,
            leafFamilyMembersOnDisk = _archive.LeafFamilies.MemberCount,
            // The tab LABELS as they are on screen, next to the tier sizes they came from: a run can assert
            // that what a tab says and what it lists are the same number.
            labelAll = ScopeAllText.Text,
            labelActive = ScopeActiveText.Text,
            labelArchived = ScopeArchivedText.Text,
            renderedTitle = TitleText.Text,
            syncStatus = SyncStatus.Text,
            screen = _screen,
            syncInProgress = _syncing,
            count = _archive.Sessions.Count,
            msgFilter = _msgFilter,
            readerCount = _selected?.ContentLoaded == true ? CurrentReaderMessages().Count : 0,
            archiveShown = _archiveShown,
            pageSize = ArchivePageSize,
            verticalOffset = MainScroller.VerticalOffset,
            extentHeight = MainScroller.ExtentHeight,
            viewportHeight = MainScroller.ViewportHeight,
            viewportWidth = MainScroller.ViewportWidth,
            mainX,
            mainW,
            mainChildren = MainContent.Children.Count
        }));
        return path;
    }

    private async Task<string> WriteClipboardSnapshotAsync()
    {
        var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
        if (content is null || !content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
            throw new InvalidOperationException("Clipboard does not contain text after the copy action.");

        var text = await content.GetTextAsync();
        if (string.IsNullOrEmpty(text))
            throw new InvalidOperationException("Clipboard text is absent after the copy action.");

        var path = Path.Combine(CapDir, "out", "clipboard-command.txt");
        File.WriteAllText(path, text);
        return path;
    }

    // Yield enough UI-thread frames for layout/render to settle before capturing.
    private async Task SettleAsync(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            var tcs = new TaskCompletionSource();
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => tcs.SetResult());
            await tcs.Task;
        }
        await Task.Delay(120);
    }

    // A dialog lives in the root's popup layer, so rendering the page would show the page behind it
    // (dimmed by the dialog's smoke layer) and never the dialog itself. The subject is therefore the
    // topmost open popup's child - the template root that carries the dialog's background. Rendering the
    // dialog's CONTENT instead composites over nothing, which washes out every themed brush in it.
    private UIElement CaptureSubject()
    {
        var popups = OpenPopups();
        // A ContentDialog is hosted alongside its own full-window smoke layer, and the smoke layer is the
        // topmost popup: rendering whatever is on top captures a flat dim frame instead of the dialog. So a
        // dialog is preferred first, and only then the plain topmost child (a flyout, whose presenter IS
        // the topmost child, is unaffected).
        for (var i = popups.Count - 1; i >= 0; i--)
            if (popups[i].Child is ContentDialog dialog) return dialog;
        for (var i = popups.Count - 1; i >= 0; i--)
            if (popups[i].Child is UIElement child) return child;
        return this;
    }

    private IReadOnlyList<Microsoft.UI.Xaml.Controls.Primitives.Popup> OpenPopups()
    {
        try { return XamlRoot is null ? Array.Empty<Microsoft.UI.Xaml.Controls.Primitives.Popup>() : VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot); }
        catch { return Array.Empty<Microsoft.UI.Xaml.Controls.Primitives.Popup>(); }
    }

    private static T? FindNamed<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T match && string.Equals(match.Name, name, StringComparison.Ordinal)) return match;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindNamed<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }
        return null;
    }

    private static T? FindByContent<T>(DependencyObject root, string content) where T : ContentControl
    {
        if (root is T match && match.Content is string text && string.Equals(text, content, StringComparison.Ordinal)) return match;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindByContent<T>(VisualTreeHelper.GetChild(root, i), content);
            if (found is not null) return found;
        }
        return null;
    }

    // Every open popup's child, for reaching INTO a dialog: a ContentDialog's controls are in the popup
    // layer, which a walk from the page never enters.
    private IEnumerable<DependencyObject> PopupRoots()
    {
        foreach (var popup in OpenPopups())
            if (popup.Child is DependencyObject child) yield return child;
    }

    // Press a dialog's button the way a pointer would: through its automation peer, so the click runs the
    // real handler (and the ContentDialog's own ShowAsync result plumbing) rather than a shortcut.
    private void InvokePopupButton(string content)
    {
        Button? button = null;
        foreach (var root in PopupRoots())
        {
            button = FindByContent<Button>(root, content);
            if (button is not null) break;
        }
        if (button is null) throw new InvalidOperationException($"No open dialog has a '{content}' button.");
        if (!button.IsEnabled) throw new InvalidOperationException($"The dialog's '{content}' button is disabled.");
        if (new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke) is not IInvokeProvider invoke)
            throw new InvalidOperationException($"The dialog's '{content}' button cannot be invoked.");
        invoke.Invoke();
    }

    private void SetPopupBoxText(string name, string? text)
    {
        if (text is null) return;
        foreach (var root in PopupRoots())
        {
            var box = FindNamed<TextBox>(root, name);
            if (box is null) continue;
            box.Text = text;   // fires TextChanged, so the dialog's own validation gate runs
            return;
        }
        throw new InvalidOperationException($"No open dialog has a '{name}' field.");
    }

    private async Task<string> CaptureAsync(string name)
    {
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(CaptureSubject());
        var pixels = await rtb.GetPixelsAsync();
        var path = Path.Combine(CapDir, "out", name + ".png");
        using var fs = new FileStream(path, FileMode.Create);
        var enc = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, fs.AsRandomAccessStream());
        enc.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, pixels.ToArray());
        await enc.FlushAsync();
        return path;
    }

    // Walk the visual tree and record each element's type/name/bounds/visibility,
    // so the external harness can run ui-craft layout checks (offscreen / zero-size /
    // overlap / clipped) without an external automation client.
    private string DumpTree(string name)
    {
        var elements = new List<object>();
        void Walk(DependencyObject d, int depth)
        {
            if (d is FrameworkElement fe)
            {
                try
                {
                    var p = fe.TransformToVisual(this).TransformPoint(new Windows.Foundation.Point(0, 0));
                    elements.Add(new
                    {
                        type = d.GetType().Name,
                        name = fe.Name,
                        x = Math.Round(p.X, 1),
                        y = Math.Round(p.Y, 1),
                        w = Math.Round(fe.ActualWidth, 1),
                        h = Math.Round(fe.ActualHeight, 1),
                        vis = fe.Visibility.ToString(),
                        depth
                    });
                }
                catch { }
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) Walk(VisualTreeHelper.GetChild(d, i), depth + 1);
        }
        Walk(this, 0);
        var path = Path.Combine(CapDir, "out", name + ".json");
        try { File.WriteAllText(path, JsonSerializer.Serialize(new { window = new { w = ActualWidth, h = ActualHeight }, screen = _screen, elements })); } catch { }
        return path;
    }

    private void RequireExpectedSelection(string? expectedSessionId, string? action)
    {
        var isReclaim = action?.Contains("reclaim", StringComparison.OrdinalIgnoreCase) == true;
        if (isReclaim && string.IsNullOrWhiteSpace(expectedSessionId))
            throw new InvalidOperationException("Reclaim requires expectedSessionId.");
        if (expectedSessionId is null) return;
        if (string.IsNullOrWhiteSpace(expectedSessionId))
            throw new InvalidOperationException("expectedSessionId must not be empty.");
        var selectedId = _selected?.Id;
        var selectedItemId = (SessionList.SelectedItem as ArchiveSession)?.Id;
        if (!string.Equals(selectedId, expectedSessionId, StringComparison.Ordinal)
            || !string.Equals(selectedItemId, expectedSessionId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Expected selected session '{expectedSessionId}', actual _selected='{selectedId}', SelectedItem='{selectedItemId}'.");
    }

    private void InvokeByText(string text, string? expectedSessionId)
    {
        RequireExpectedSelection(expectedSessionId, text);
        if (text.Contains("launch", StringComparison.OrdinalIgnoreCase)
            || text.Contains("resume", StringComparison.OrdinalIgnoreCase)
            || text.Contains("delete", StringComparison.OrdinalIgnoreCase)
            || text.Contains("terminate", StringComparison.OrdinalIgnoreCase)
            || text.Contains("kill", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Destructive capture action '{text}' is not allowlisted.");

        // A filter switch is not a Button, so it needs its own match. Flipping IsOn drives the same Toggled
        // handler a pointer tap does - the harness has no pointer injection.
        ToggleSwitch? toggle = null;
        void FindToggle(DependencyObject d)
        {
            if (toggle is not null) return;
            if (d is ToggleSwitch t && t.Name == text
                && t.IsEnabled && t.Visibility == Visibility.Visible && t.ActualWidth > 0 && t.ActualHeight > 0)
            {
                toggle = t;
                return;
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) FindToggle(VisualTreeHelper.GetChild(d, i));
        }

        Button? unavailable = null;
        Button? Find(DependencyObject d)
        {
            if (d is Button b)
            {
                var caption = b.Content as string ?? ButtonText(b);
                var matches = b.Name == text || caption == text
                    || (text.Contains("reclaim", StringComparison.OrdinalIgnoreCase)
                        && (b.Name.Contains("reclaim", StringComparison.OrdinalIgnoreCase)
                            || caption?.Contains("reclaim", StringComparison.OrdinalIgnoreCase) == true));
                if (matches)
                {
                    if (b.IsEnabled && b.Visibility == Visibility.Visible && b.ActualWidth > 0 && b.ActualHeight > 0)
                        return b;
                    unavailable ??= b;
                }
            }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
            {
                var result = Find(VisualTreeHelper.GetChild(d, i));
                if (result is not null) return result;
            }
            return null;
        }

        // The filter flyout is a popup, so the controls inside it are NOT under MainPage's visual tree and
        // have to be reached through the flyout's own content.
        var roots = new List<DependencyObject> { this };
        if (_filterFlyout?.Content is DependencyObject flyoutContent) roots.Add(flyoutContent);

        foreach (var root in roots) FindToggle(root);
        if (toggle is not null)
        {
            toggle.IsOn = !toggle.IsOn;
            return;
        }

        Button? btn = null;
        foreach (var root in roots)
        {
            btn = Find(root);
            if (btn is not null) break;
        }
        if (btn is null)
        {
            if (unavailable is not null) throw new InvalidOperationException($"Button '{text}' is unavailable.");
            throw new InvalidOperationException($"Button '{text}' was not found.");
        }
        if (new ButtonAutomationPeer(btn).GetPattern(PatternInterface.Invoke) is not IInvokeProvider inv)
            throw new InvalidOperationException($"Button '{text}' has no invoke automation pattern.");
        inv.Invoke();
    }


    private static string? ButtonText(Button b)
    {
        string? found = null;
        void W(DependencyObject d)
        {
            if (found is not null) return;
            if (d is TextBlock t) { found = t.Text; return; }
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++) W(VisualTreeHelper.GetChild(d, i));
        }
        if (b.Content is DependencyObject dobj) W(dobj);
        return found;
    }
}

internal sealed class CapRequest
{
    public long id { get; set; }
    public List<CapStep>? steps { get; set; }
}

internal sealed class CapStep
{
    public string? nav { get; set; }
    public int[]? resize { get; set; }
    public string? type { get; set; }
    public string? click { get; set; }
    public string? expectedSessionId { get; set; }
    public string? selectSessionId { get; set; }
    public string[]? selectSessionIds { get; set; }
    public string? bulkMenu { get; set; }
    public string? vet { get; set; }
    public string? vetName { get; set; }
    public string? vetPhrase { get; set; }
    public bool? vetClose { get; set; }
    public string[]? vetMany { get; set; }
    public string[]? vetManyNames { get; set; }
    public string[]? vetManyPhrases { get; set; }
    public string? vetManyShot { get; set; }
    public string[]? unvetMany { get; set; }
    public string? confirm { get; set; }
    public string? shot { get; set; }
    public string? dump { get; set; }
    public string? snapshot { get; set; }
    public string? dumpList { get; set; }
    // The family badge and the leaf glyph live inside the row template, where InvokeByText cannot reach them
    // (they are neither buttons with captions nor toggles). These name the ROW to act on and drive the same
    // handler the tap does, so the run proves the behaviour without a pointer.
    public string? familyBadge { get; set; }
    public string? leafGlyph { get; set; }
    // Whether to drive the vet dialog's name-autofill (focus the field, then move focus off it, which is
    // what pressing Tab does) and, if named, the artifact the two fields are recorded into.
    public bool? vetBlur { get; set; }
    public string? vetFields { get; set; }
    public bool? closeFlyout { get; set; }
    public bool? clipboard { get; set; }
    public int[]? scroll { get; set; }
    public int? wait { get; set; }
    public int? waitMs { get; set; }
    public override string ToString() => JsonSerializer.Serialize(this);
}
