using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Security.Credentials;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage : Page
{
    private readonly ArchiveService _archive = new();
    private readonly AiChatService _ai = new();
    private readonly Stack<string> _backStack = new();
    private ArchiveSession? _selected;
    private string _screen = "Archive";
    private Windows.UI.Color _accentColor = Windows.UI.Color.FromArgb(255, 251, 113, 133);
    private int _panelRadius = 12;
    private int _controlRadius = 12;
    private bool _storeLoaded;
    private string _deepSearchQuery = "";
    private string _askQuestion = "";
    private string _askAnswer = "";
    private string _askStatus = "Configure a provider in Settings, then ask a question about your archive.";

    public MainPage()
    {
        Diag.Log("MP.ctor: before InitializeComponent");
        InitializeComponent();
        Diag.Log("MP.ctor: after InitializeComponent");
        Loaded += MainPage_Loaded;
        // Responsive: below this width the right rail + chat reader can't both fit, so the right
        // panel (secondary actions, all reachable from the header + ... menu) folds away.
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth > 0 && ActualWidth < RightPanelMinWidth;
            if (narrow == _narrowLayout) return;
            _narrowLayout = narrow;
            UpdateChrome();
        };
    }

    private const double RightPanelMinWidth = 1120;
    private bool _narrowLayout;

    private async void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        Diag.Log("MP.Loaded: start");
        try
        {
            await _archive.LoadAsync();
            Diag.Log("MP.Loaded: archive loaded (" + _archive.Sessions.Count + " sessions)");
            ApplyThemeAndShape();
            _storeLoaded = true;
            SessionList.ItemsSource = _archive.Sessions;
            Diag.Log("MP.Loaded: list bound");
            SelectFirstSession();
            Diag.Log("MP.Loaded: first selected");
            RenderCurrent();
            Diag.Log("MP.Loaded: render done");
            StartCaptureHarness();
            StartAgentBridge();
            StartLiveReader();
            Diag.Log("DeepSeek key source: " + ApiKeySource("deepseek"));
            _ = StartupResurfaceAsync();
        }
        catch (Exception ex)
        {
            Diag.Log("MP.Loaded: EXCEPTION " + ex);
        }
    }

    // Live reader: while a chat is open, poll its source transcript and tail new turns as the agent
    // writes them - so you watch a rollout fill in. We only auto-follow when you're at the bottom; if
    // you scroll up to read history, we leave you there until you return to the latest.
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _liveTimer;
    private DateTime _liveMtime = DateTime.MinValue;
    private ArchiveSession? _liveSession;
    private bool _liveBusy;

    private void StartLiveReader()
    {
        _liveTimer = DispatcherQueue.CreateTimer();
        _liveTimer.Interval = TimeSpan.FromMilliseconds(1500);
        _liveTimer.Tick += async (_, _) => await LiveTickAsync();
        _liveTimer.Start();
    }

    private async Task LiveTickAsync()
    {
        if (_liveBusy || _screen != "Archive" || _selected is null || !_selected.ContentLoaded) return;

        // First sight of this chat -> set the baseline, don't repaint.
        if (!ReferenceEquals(_selected, _liveSession))
        {
            _liveSession = _selected;
            _liveMtime = _archive.SourceWriteTimeUtc(_selected);
            return;
        }

        var mtime = _archive.SourceWriteTimeUtc(_selected);
        if (mtime <= _liveMtime) return;                                   // nothing new on disk
        if (MainScroller.ScrollableHeight - MainScroller.VerticalOffset > 120) return;  // reading history; don't yank

        _liveBusy = true;
        try
        {
            _liveMtime = mtime;
            await _archive.ReloadContentAsync(_selected);
            if (_screen == "Archive" && ReferenceEquals(_selected, _liveSession))
            {
                _scrollArchiveToBottom = true;
                RenderArchive();
            }
        }
        catch (Exception ex) { Diag.Log("Live tick: " + ex.Message); }
        finally { _liveBusy = false; }
    }

    private async Task RefreshTitlesAfterFirstPaint()
    {
        try
        {
            if (await _archive.EnrichTitlesFromLocalStateAsync())
            {
                SelectFirstSession();
                RenderCurrent();
            }
        }
        catch
        {
            // Local title indexes are optional; startup should not depend on them.
        }
    }

    private void SelectFirstSession()
    {
        _selected = _archive.Sessions.FirstOrDefault();
        SessionList.SelectedItem = _selected;
    }

    private void SessionList_ItemClick(object sender, ItemClickEventArgs e)
    {
        _selected = e.ClickedItem as ArchiveSession;
        Navigate("Archive");
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            ApplyFilters();
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (SearchBox.Text.Length == 0)
        {
            ApplyFilters();
        }
    }

    // Kept for callers that pre-set SearchBox.Text (e.g. capture replay); routes through the unified
    // text + tag filter so an active tag filter is always respected.
    private void ApplySearch(string query) => ApplyFilters();

    private void Nav_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string target) return;
        Navigate(target);
    }

    private void Navigate(string target, bool pushHistory = true)
    {
        if (pushHistory && _screen != target)
        {
            _backStack.Push(_screen);
        }

        _screen = target;
        RenderCurrent();
    }

    private void RenderCurrent()
    {
        switch (_screen)
        {
            case "Search":
                RenderSearch(SearchBox.Text);
                break;
            case "Ask":
                RenderCopilot();
                break;
            case "Workspaces":
                RenderWorkspaces();
                break;
            case "Collections":
                RenderCollections();
                break;
            case "Settings":
                RenderSettings();
                break;
            case "Source":
                RenderSource();
                break;
            case "Restore":
                RenderRestore();
                break;
            case "Brain":
                RenderBrainPage();
                break;
            default:
                RenderArchive();
                break;
        }
        UpdateChrome();
        UpdateBackButton();
    }

    // The right panel (quick actions / tags) and the header actions (copy context /
    // build restore packet) are session-specific - they only belong on screens tied to
    // the selected chat. Hide them elsewhere and reclaim the space so each screen shows
    // only what's relevant.
    private void UpdateChrome()
    {
        bool sessionContext = _screen is "Archive" or "Source" or "Restore";
        bool showRight = sessionContext && !_narrowLayout;   // fold the right rail when too narrow to fit
        RightColumnBorder.Visibility = showRight ? Visibility.Visible : Visibility.Collapsed;
        RightColumn.Width = showRight ? new GridLength(292) : new GridLength(0);
        HeaderActions.Visibility = sessionContext ? Visibility.Visible : Visibility.Collapsed;
    }

    // Header overflow menu: the same secondary actions as the right "Quick actions" rail, reachable
    // at ALL widths - critical once the right rail folds away on a narrow window.
    private void HeaderMore_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        void Add(string text, Action act) { var item = new MenuFlyoutItem { Text = text }; item.Click += (_, _) => act(); flyout.Items.Add(item); }
        Add("Add to project", () => ShowAddToProjectFlyout(HeaderMoreButton, _selected!));
        Add("Bump to top of resume list", () => _ = BumpSession(_selected!));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Copy resume prompt", () => Copy("resume"));
        Add("Copy chat path", () => Copy("path"));
        Add("Copy all code", () => Copy("code"));
        flyout.Items.Add(new MenuFlyoutSeparator());
        Add("Build restore packet", () => Navigate("Restore"));
        Add("Inspect raw events", () => Navigate("Source"));
        flyout.ShowAt(HeaderMoreButton);
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_backStack.Count == 0) return;
        Navigate(_backStack.Pop(), pushHistory: false);
    }

    private void UpdateBackButton()
    {
        BackButton.IsEnabled = _backStack.Count > 0;
        BackButton.Opacity = _backStack.Count > 0 ? 1 : 0.45;
    }

    private int _archiveShown;
    private ArchiveSession? _lastArchiveSession;
    private const int ArchivePageSize = 25;
    private bool _scrollArchiveToBottom;   // jump to newest after the first render of a chat
    private bool _loadingOlder;            // re-entrancy guard while prepending older messages on scroll-up
    private ArchiveSession? _contentLoadingSession;

    private int _searchShown;
    private string _lastSearchQuery = "";
    private const int SearchPageSize = 25;

    private void RenderArchive()
    {
        ScreenLabel.Text = _selected is null
            ? "Archive reader"
            : $"{(string.Equals(_selected.Tool, "claude", StringComparison.OrdinalIgnoreCase) ? "Claude" : "Codex")} chat - archive reader";
        TitleText.Text = _selected?.DisplayTitle ?? "No chat selected";
        MainContent.Children.Clear();
        RenderTags();

        if (_selected is null)
        {
            MainContent.Children.Add(EmptyBlock("No chats indexed", "Import a sessions folder to begin."));
            return;
        }

        // New chat selected -> start fresh and jump to the newest messages once it renders.
        if (!ReferenceEquals(_selected, _lastArchiveSession)) { _archiveShown = 0; _lastArchiveSession = _selected; _scrollArchiveToBottom = true; }

        // Content lazy-loads from the source file the first time you open a chat (the store holds only
        // metadata). The reader shows the MOST RECENT messages at the bottom; scrolling up auto-loads
        // older ones, so a long chat opens where the conversation actually is.
        if (!_selected.ContentLoaded)
        {
            MainContent.Children.Add(EmptyBlock("Loading conversation...", _selected.WorkspaceName));
            if (!ReferenceEquals(_contentLoadingSession, _selected))
            {
                _contentLoadingSession = _selected;
                _ = EnsureContentThenRenderAsync(_selected);
            }
            return;
        }

        var messages = _selected.Messages;
        if (messages.Count == 0)
        {
            MainContent.Children.Add(EmptyBlock("No conversation messages parsed", _selected.SourcePath));
            return;
        }
        var shown = Math.Min(_archiveShown <= 0 ? ArchivePageSize : _archiveShown, messages.Count);
        var start = messages.Count - shown;   // render the last `shown` messages, oldest-of-page first

        if (start > 0)
        {
            MainContent.Children.Add(new TextBlock
            {
                Text = $"Scroll up to load {start} earlier message{(start == 1 ? "" : "s")}",
                Foreground = MutedBrush(),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 8)
            });
        }
        for (var i = start; i < messages.Count; i++) MainContent.Children.Add(MessageBubble(messages[i]));

        if (_scrollArchiveToBottom && messages.Count > 0)
        {
            _scrollArchiveToBottom = false;
            // Force layout so ScrollableHeight is known, then jump to the newest message.
            MainScroller.UpdateLayout();
            MainScroller.ChangeView(null, MainScroller.ScrollableHeight, null, disableAnimation: true);
        }
    }

    // Auto-paginate older messages when the user scrolls near the top - no manual "load more".
    private void MainScroller_ViewChanged(object sender, Microsoft.UI.Xaml.Controls.ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate || _loadingOlder) return;
        if (_screen != "Archive" || _selected is null || !_selected.ContentLoaded) return;
        if (MainScroller.VerticalOffset > 48) return;   // only fire when near the top

        var count = _selected.Messages.Count;
        var shown = Math.Min(_archiveShown <= 0 ? ArchivePageSize : _archiveShown, count);
        if (shown >= count) return;                       // nothing older to load

        _loadingOlder = true;
        try
        {
            var oldExtent = MainScroller.ExtentHeight;
            var oldOffset = MainScroller.VerticalOffset;
            _archiveShown = shown + ArchivePageSize;
            RenderArchive();                              // re-renders with more older messages at the top
            MainScroller.UpdateLayout();
            // Keep the user's view anchored on the same message by absorbing the height added above.
            var delta = MainScroller.ExtentHeight - oldExtent;
            MainScroller.ChangeView(null, oldOffset + delta, null, disableAnimation: true);
        }
        finally { _loadingOlder = false; }
    }

    private async Task EnsureContentThenRenderAsync(ArchiveSession session)
    {
        try { await _archive.EnsureContentAsync(session); }
        catch (Exception ex) { Diag.Log("EnsureContent: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_contentLoadingSession, session)) _contentLoadingSession = null;
        }
        if (ReferenceEquals(_selected, session) && _screen == "Archive")
        {
            _archiveShown = ArchivePageSize;
            RenderArchive();
        }
    }

    private void RenderSearch(string query)
    {
        ScreenLabel.Text = "Deep content retrieval";
        TitleText.Text = "Global search";
        MainContent.Children.Clear();

        MainContent.Children.Add(DeepSearchPanel());
        var hits = _archive.DeepSearch(_deepSearchQuery);
        if (hits.Count == 0)
        {
            MainContent.Children.Add(EmptyBlock("No deep matches", "Try fewer words, a rough phrase, a file name, or a path fragment."));
            return;
        }

        // New query -> start paging fresh (Show more keeps the same query, so it won't reset).
        if (!string.Equals(_deepSearchQuery, _lastSearchQuery, StringComparison.Ordinal)) { _searchShown = 0; _lastSearchQuery = _deepSearchQuery; }
        var shown = Math.Min(_searchShown <= 0 ? SearchPageSize : _searchShown, hits.Count);
        for (var i = 0; i < shown; i++) MainContent.Children.Add(SearchHitResult(hits[i]));

        if (shown < hits.Count)
        {
            var more = new Button
            {
                Content = $"Show {Math.Min(SearchPageSize, hits.Count - shown)} more  ({hits.Count - shown} left)",
                Margin = new Thickness(0, 8, 0, 16),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            more.Click += (_, _) => { _searchShown = shown + SearchPageSize; RenderSearch(query); };
            MainContent.Children.Add(more);
        }
    }

    private void RenderWorkspaces()
    {
        ScreenLabel.Text = "Grouped by local project path";
        TitleText.Text = "Workspaces";
        MainContent.Children.Clear();
        // Snapshot: a background sync may still be writing Store.Sessions on launch.
        var groups = _archive.Store.Sessions.Values.ToList()
            .GroupBy(s => string.IsNullOrWhiteSpace(s.WorkspaceName) ? "Unknown" : s.WorkspaceName)
            .OrderByDescending(g => g.Max(s => s.UpdatedAt));
        foreach (var group in groups.Take(80))
        {
            MainContent.Children.Add(ExpandableSessionGroup(
                group.Key,
                $"{group.Count()} chats",
                group.OrderByDescending(session => session.Pinned).ThenByDescending(session => session.UpdatedAt),
                pathLabel: group.First().Workspace));
        }
    }

    private void RenderCollections()
    {
        ScreenLabel.Text = "Your hub - group chats into projects you can reopen";
        TitleText.Text = "Collections";
        MainContent.Children.Clear();

        MainContent.Children.Add(DeckPickerBar());
        MainContent.Children.Add(CollectionsControlBar());

        var pills = CollectionFilterPills();
        if (pills is not null) MainContent.Children.Add(pills);

        // Only the active deck's collections (scoped), then the compound tag filter.
        var activeDeck = _archive.ActiveDeckId;
        var collections = FilteredCollections().Where(c => string.Equals(ArchiveService.CollectionDeck(c), activeDeck, StringComparison.OrdinalIgnoreCase)).ToList();
        if (collections.Count == 0)
        {
            var filtering = AnyCollectionFilterActive();
            MainContent.Children.Add(EmptyBlock(
                filtering ? "No projects match this filter" : "No collections yet",
                filtering
                    ? "Clear the filter (top-right funnel), or tag a collection with the selected tags."
                    : "Make one with \"+ New collection\" above. Then add chats from a chat's right-click menu, or use a collection's \"Agent cmd\" button and paste it into any Claude/Codex chat to have it file itself in."));
        }
        else
        {
            foreach (var collection in collections)
            {
                var sessions = CollectionChatsFiltered(collection);   // chat-tag filter + layer sort
                var id = collection.Id;
                var name = collection.Name;
                // Count only chats that actually resolve to a stored session (stale ids are ignored),
                // and only show "N of M" when a chat-tag filter is hiding some.
                var resolvedTotal = collection.SessionIds.Count(sid => _archive.Store.Sessions.ContainsKey(sid));
                var chatFilterActive = _collChatInclude.Count > 0 || _collChatExclude.Count > 0;
                var subtitle = chatFilterActive ? $"{sessions.Count} of {resolvedTotal} chats" : $"{sessions.Count} chats";
                var deckName = _archive.Decks.FirstOrDefault(d => string.Equals(d.Id, ArchiveService.CollectionDeck(collection), StringComparison.OrdinalIgnoreCase))?.Name ?? "Main";
                MainContent.Children.Add(ExpandableSessionGroup(name, subtitle, sessions,
                    onDelete: () => _ = DeleteCollectionAsync(id, name),
                    onCopyAgentCommand: () => CopyCollectionAgentCommand(name, deckName),
                    onRemoveSession: s => _ = RemoveSessionFromCollectionAsync(id, s.Id),
                    tags: collection.Tags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase).ToList(),
                    onAddTag: () => _ = ShowAddCollectionTagDialogAsync(id, name),
                    onRemoveTag: t => _ = RemoveCollectionTagAndRefreshAsync(id, t),
                    onMoveSession: (s, delta, toEnd) => _ = ReorderInCollectionAndRefreshAsync(id, s.Id, delta, toEnd),
                    moveTargets: _archive.Decks.Where(d => !string.Equals(d.Id, ArchiveService.CollectionDeck(collection), StringComparison.OrdinalIgnoreCase)).Select(d => (d.Id, d.Name)).ToList(),
                    onMoveToDeck: deckId => _ = MoveCollectionToDeckAndRefreshAsync(id, deckId)));
            }
        }

        RenderRecentlyDeleted();   // a safety net for accidental deletes (renders nothing when empty)
    }

    // The control panel header for Collections: create a project + explain the two ways chats get in.
    private Border CollectionsControlBar()
    {
        var newButton = new Button
        {
            Style = (Style)Resources["PrimaryPillButtonStyle"],
            Content = "+ New collection"
        };
        newButton.Click += async (_, _) => await NewCollectionAsync();

        // Project Brain entry point — opens the per-collection memory screen.
        var brainButton = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Brain" };
        ToolTipService.SetToolTip(brainButton, "Build a durable, source-linked memory brain for a project");
        brainButton.Click += (_, _) => Navigate("Brain");

        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        header.Children.Add(new TextBlock
        {
            Text = "Projects",
            Foreground = StrongBrush(),
            FontSize = 16,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var filterButton = new Button
        {
            Style = (Style)Resources["IconButtonStyle"],
            Width = 40,
            Height = 40,
            MinWidth = 40,
            MinHeight = 40,
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16, Glyph = "" }
        };
        ToolTipService.SetToolTip(filterButton, "Filter projects by tag, and chats within them");
        filterButton.Click += CollectionFilter_Click;

        var headerActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        headerActions.Children.Add(filterButton);
        headerActions.Children.Add(BackupMenuButton());
        headerActions.Children.Add(brainButton);
        headerActions.Children.Add(newButton);
        Grid.SetColumn(headerActions, 1);
        header.Children.Add(headerActions);

        var explain = new TextBlock
        {
            Foreground = MutedBrush(),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Text = "A collection is a project folder for chats. Add chats from their right-click menu, or hit \"Agent cmd\" " +
                   "on a collection and paste it into a chat so it files itself in. Expand a collection to open any chat or " +
                   "resume it in a fresh terminal in its original folder - so you can close VS Code and pick work back up from here."
        };

        return Card(new StackPanel { Spacing = 12, Children = { header, explain } });
    }

    private async Task NewCollectionAsync()
    {
        var input = new TextBox
        {
            PlaceholderText = "e.g. Renderer work, Job search, VENPOD",
            MinWidth = 420,
            CornerRadius = ControlCornerRadius()
        };
        var dialog = new ContentDialog
        {
            Title = "New collection",
            Content = input,
            PrimaryButtonText = "Create",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            var name = input.Text.Trim();
            await _archive.CreateCollectionAsync(name);
            RenderCollections();
            SyncStatus.Text = $"Created collection \"{name}\".";
        }
    }

    private async Task RemoveSessionFromCollectionAsync(string collectionId, string sessionId)
    {
        await _archive.RemoveFromCollectionAsync(collectionId, sessionId);
        RenderCollections();
    }

    private void CopyCollectionAgentCommand(string name, string deckName)
    {
        var package = new DataPackage();
        package.SetText(CollectionAgentInstruction(name, deckName));
        Clipboard.SetContent(package);
        SyncStatus.Text = $"Copied the agent command for \"{name}\" (deck: {deckName}) - paste it into a chat.";
    }

    // The self-file instruction a user pastes into any Claude/Codex chat. The agent appends one line
    // to the inbox the app already polls; the app resolves the runtime id to its stored chat key.
    private static string CollectionAgentInstruction(string projectName, string deckName)
    {
        var inbox = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexLocalRetrieval", "agent-inbox.jsonl");
        return
            $"Add THIS chat to my \"Codex Local Retrieval\" app under the project \"{projectName}\" on deck \"{deckName}\".\n" +
            "If I gave this chat a name (e.g. \"add yourself as codex-claude-local\"), set that as its " +
            "in-app name too. The name is app-only - it does NOT change your global/native title.\n\n" +
            "1. Get YOUR exact session id from your environment (you already have it):\n" +
            "   - Codex:  the CODEX_THREAD_ID environment variable\n" +
            "   - Claude: the CLAUDE_CODE_SESSION_ID environment variable\n" +
            "2. Generate a requestId, then append exactly one line (then a newline) to this file:\n" +
            $"   {inbox}\n" +
            "   The line (put your real id, tool, requestId, and optional name in):\n" +
            $"   {{\"op\":\"addSelfToProject\",\"project\":\"{projectName}\",\"deck\":\"{deckName}\",\"name\":\"<optional in-app name, omit to keep the auto title>\",\"id\":\"<your session id>\",\"tool\":\"codex|claude\",\"requestId\":\"<uuid>\"}}\n\n" +
            "The app resolves your runtime id to its stored chat key, files it into the project, sets the " +
            "optional in-app name, and acks with the same requestId plus resolvedSessionId and persisted. " +
            "If the id cannot be resolved it returns an error rather than adding a different chat. Only if " +
            "your runtime truly has no session-id variable, fall back to \"target\":\"self\" with your real " +
            "\"cwd\" and \"tool\". (To only rename, omit project and use \"op\":\"setName\".) " +
            "Full protocol: AGENTS.md next to the inbox file.";
    }

    private async Task DeleteCollectionAsync(string id, string name)
    {
        var dialog = new ContentDialog
        {
            Title = "Delete project",
            Content = $"Remove the \"{name}\" project? The chats themselves stay in your archive - only the grouping is removed.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.RemoveCollectionAsync(id);
            RenderCollections();
        }
    }

    private void RenderSettings()
    {
        ScreenLabel.Text = "Preferences and safety";
        TitleText.Text = "Settings";
        MainContent.Children.Clear();
        MainContent.Children.Add(SettingsPanel(
            "Theme picker",
            "Choose the palette, accent, shape, and density used by the app.",
            SettingControlRow("Palette", ThemeCombo()),
            SettingControlRow("Accent preset", AccentCombo()),
            SettingControlRow("Custom accent", AccentColorPicker()),
            SettingControlRow("Shape", ShapeCombo()),
            SettingControlRow("Density", DensityCombo())));
        MainContent.Children.Add(AgentAccessPanel());
        MainContent.Children.Add(AiProviderPanel());
        MainContent.Children.Add(SettingRow("Read-only source mode", _archive.Store.Settings.ReadOnlySourceMode ? "On" : "Off"));
    }

    private void RenderAsk()
    {
        ScreenLabel.Text = "Retrieval assisted answers";
        TitleText.Text = "Ask archive";
        MainContent.Children.Clear();
        MainContent.Children.Add(AskPanel());
        if (!string.IsNullOrWhiteSpace(_askAnswer))
        {
            MainContent.Children.Add(TextPanel(_askAnswer));
        }
    }

    private void RenderSource()
    {
        ScreenLabel.Text = "Raw rollout timeline";
        TitleText.Text = "Source inspector";
        MainContent.Children.Clear();
        if (_selected is null)
        {
            MainContent.Children.Add(EmptyBlock("No chat selected", "Pick a chat to inspect its raw events."));
            return;
        }
        MainContent.Children.Add(InfoPanel("Source file", _selected.SourcePath));
        // Read + parse the rollout OFF the UI thread (up to 400 lines) so opening Source never hitches.
        MainContent.Children.Add(EmptyBlock("Reading source...", _selected.WorkspaceName));
        _ = LoadSourceThenRenderAsync(_selected);
    }

    private async Task LoadSourceThenRenderAsync(ArchiveSession session)
    {
        IReadOnlyList<RawEvent> events;
        try { events = await _archive.ReadEventsAsync(session); }
        catch (Exception ex) { Diag.Log("ReadEvents: " + ex.Message); events = Array.Empty<RawEvent>(); }
        if (!ReferenceEquals(_selected, session) || _screen != "Source") return;
        MainContent.Children.Clear();
        MainContent.Children.Add(InfoPanel("Source file", session.SourcePath));
        MainContent.Children.Add(SourceEventsPanel(session, events));
    }

    private void RenderRestore()
    {
        ScreenLabel.Text = "New chat handoff";
        TitleText.Text = "Restore packet";
        MainContent.Children.Clear();
        if (_selected is null) return;
        MainContent.Children.Add(TextPanel(_archive.RestorePacket(_selected)));
    }

    private static string CapDisplay(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "\n\n...(truncated - reopen the chat to see the full message)";

    private UIElement MessageBubble(ArchiveMessage message)
    {
        if (message.EffectiveKind == "tool") return ToolStepView(message);

        var isUser = message.EffectiveKind == "user";
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock
        {
            Text = (isUser ? "You" : "Assistant") + "  -  " + FormatDate(message.Timestamp),
            Foreground = isUser ? AccentBrush() : MutedBrush(),
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        stack.Children.Add(new TextBlock
        {
            // A single message can carry a 500KB tool dump; laying that out in a wrapping TextBlock is what
            // made opening a chat hitch. Cap the DISPLAYED text (full content stays in the source file -
            // "Resume in terminal" / "Open in VS Code" shows it all).
            Text = CapDisplay(CleanReadingText(message.Text), 4000),
            TextWrapping = TextWrapping.Wrap,
            Foreground = StrongBrush(),
            LineHeight = 22,
            FontSize = 14
        });
        foreach (var block in message.CodeBlocks)
        {
            stack.Children.Add(CodeBlockPanel(block));
        }

        return new Border
        {
            Background = isUser ? AccentVerySoftBrush() : PanelBrush(),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = PanelCornerRadius(),
            Padding = new Thickness(18),
            MaxWidth = 900,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Child = stack
        };
    }

    // A tool call rendered as a single compact, collapsible step: badge + command on one line; expand
    // for the full command and its output. Keeps the transcript reading like a chat, not a data dump.
    private UIElement ToolStepView(ArchiveMessage m)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(40, 120, 170, 255)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(7, 1, 7, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = m.ToolName, Foreground = StrongBrush(), FontSize = 11, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
        });
        header.Children.Add(new TextBlock
        {
            Text = FirstLine(m.Text),
            Foreground = MutedBrush(),
            FontSize = 12,
            FontFamily = new FontFamily("Consolas"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center
        });

        var body = new StackPanel { Spacing = 8 };
        if (!string.IsNullOrWhiteSpace(m.Text)) body.Children.Add(MonoBlock(m.Text, muted: false));
        if (!string.IsNullOrWhiteSpace(m.ToolOutput)) body.Children.Add(MonoBlock(m.ToolOutput, muted: true));

        var expander = new Expander
        {
            Header = header,
            Content = body,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = ControlCornerRadius(),
            Padding = new Thickness(10, 2, 10, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 900
        };
        return expander;
    }

    private UIElement MonoBlock(string text, bool muted) => new Border
    {
        Background = new SolidColorBrush(Colors.Black),
        BorderBrush = LineBrush(),
        BorderThickness = new Thickness(1),
        CornerRadius = ControlCornerRadius(),
        Padding = new Thickness(10),
        Child = new TextBlock
        {
            Text = CapDisplay(text, 4000),
            FontFamily = new FontFamily("Consolas"),
            FontSize = 12,
            Foreground = muted ? MutedBrush() : StrongBrush(),
            TextWrapping = TextWrapping.Wrap
        }
    };

    private static string FirstLine(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var i = s.IndexOfAny(new[] { '\n', '\r' });
        var line = i < 0 ? s : s[..i];
        return line.Length > 200 ? line[..200] : line;
    }

    private UIElement CodeBlockPanel(CodeBlock block)
    {
        return new Border
        {
            Background = new SolidColorBrush(Colors.Black),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = ControlCornerRadius(),
            Padding = new Thickness(14),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock { Text = block.Language, Foreground = MutedBrush(), FontSize = 12 },
                    new TextBlock { Text = CapDisplay(block.Code, 4000), Foreground = StrongBrush(), FontFamily = new FontFamily("Consolas"), TextWrapping = TextWrapping.Wrap }
                }
            }
        };
    }

    private Border SessionResult(ArchiveSession session)
    {
        var button = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Content = "Open",
            HorizontalAlignment = HorizontalAlignment.Left,
            Tag = session
        };
        button.Click += (_, _) =>
        {
            OpenSession(session);
        };

        return Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = session.DisplayTitle, Foreground = StrongBrush(), FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{session.WorkspaceName} - {session.SourcePath}", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap },
                button
            }
        });
    }

    private Border SearchHitResult(ArchiveSearchHit hit)
    {
        var openButton = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Content = "Open chat",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        openButton.Click += (_, _) => OpenSession(hit.Session);

        var copyPathButton = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Content = "Copy path",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        copyPathButton.Click += (_, _) => CopyPath(hit.Session);

        return Card(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = hit.Session.DisplayTitle, Foreground = StrongBrush(), FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = $"{hit.SourceLabel} - score {hit.Score} - {hit.MatchedTerms}", Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = hit.Snippet, Foreground = StrongBrush(), TextWrapping = TextWrapping.Wrap, LineHeight = 21 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { openButton, copyPathButton }
                }
            }
        });
    }

    private Border DeepSearchPanel()
    {
        var input = new TextBox
        {
            Text = _deepSearchQuery,
            PlaceholderText = "Search inside every conversation with loose matching",
            CornerRadius = ControlCornerRadius(),
            MinWidth = 360
        };
        input.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            _deepSearchQuery = input.Text;
            RenderSearch(_deepSearchQuery);
        };
        input.TextChanged += (_, _) =>
        {
            if (input.Text.Length != 0) return;
            _deepSearchQuery = "";
            RenderSearch("");
        };

        var button = new Button
        {
            Style = (Style)Resources["PrimaryPillButtonStyle"],
            Content = "Search content"
        };
        button.Click += (_, _) =>
        {
            _deepSearchQuery = input.Text;
            RenderSearch(_deepSearchQuery);
        };

        var clearButton = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Content = "Clear"
        };
        clearButton.Click += (_, _) =>
        {
            _deepSearchQuery = "";
            RenderSearch("");
        };

        var inputGrid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        Grid.SetColumn(button, 1);
        Grid.SetColumn(clearButton, 2);
        inputGrid.Children.Add(input);
        inputGrid.Children.Add(button);
        inputGrid.Children.Add(clearButton);

        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Fuzzy search scans titles, paths, message text, and code blocks. It tolerates partial words and small typos.", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap },
                inputGrid
            }
        });
    }

    private Border AskPanel()
    {
        var provider = _archive.ActiveAiProvider();
        var input = new TextBox
        {
            Text = _askQuestion,
            PlaceholderText = "Ask about a task, file, decision, error, or code snippet",
            CornerRadius = ControlCornerRadius(),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 86
        };
        input.TextChanged += (_, _) => _askQuestion = input.Text;

        var askButton = new Button
        {
            Style = (Style)Resources["PrimaryPillButtonStyle"],
            Content = "Ask",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        askButton.Click += async (_, _) =>
        {
            _askQuestion = input.Text;
            await AskArchiveAsync();
        };

        var settingsButton = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Content = "Provider settings",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        settingsButton.Click += (_, _) => Navigate("Settings");

        return Card(new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = provider is null ? "No AI provider configured" : $"Provider: {provider.Name} / {provider.Model}", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap },
                input,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { askButton, settingsButton }
                },
                new TextBlock { Text = _askStatus, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap }
            }
        });
    }

    private async Task AskArchiveAsync()
    {
        var provider = _archive.ActiveAiProvider();
        if (provider is null)
        {
            _askStatus = "No provider configured.";
            RenderAsk();
            return;
        }
        if (string.IsNullOrWhiteSpace(_askQuestion))
        {
            _askStatus = "Enter a question first.";
            RenderAsk();
            return;
        }

        var key = LoadApiKey(provider.Id);
        if (string.IsNullOrWhiteSpace(key))
        {
            _askStatus = $"No API key saved for {provider.Name}.";
            RenderAsk();
            return;
        }

        _askStatus = "Retrieving local context and asking provider...";
        _askAnswer = "";
        RenderAsk();

        try
        {
            var hits = _archive.RetrieveForQuestion(_askQuestion, 10);
            _askAnswer = await _ai.AskArchiveAsync(provider, key, _askQuestion, hits);
            _askStatus = $"Answered using {hits.Count} local excerpts. Full archive was not sent.";
        }
        catch (Exception ex)
        {
            _askStatus = $"Ask failed: {ex.Message}";
        }
        RenderAsk();
    }

    private UIElement ExpandableSessionGroup(string title, string subtitle, IEnumerable<ArchiveSession> sessions,
        Action? onDelete = null, Action? onCopyAgentCommand = null, Action<ArchiveSession>? onRemoveSession = null,
        IReadOnlyList<string>? tags = null, Action? onAddTag = null, Action<string>? onRemoveTag = null,
        string? pathLabel = null, Action<ArchiveSession, int, bool>? onMoveSession = null,
        IReadOnlyList<(string Id, string Name)>? moveTargets = null, Action<string>? onMoveToDeck = null)
    {
        var sessionList = sessions.Take(120).ToList();
        var stack = new StackPanel { Spacing = 0 };
        if (sessionList.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "No chats yet - add some from a chat's right-click menu, or paste this collection's Agent cmd into a chat.", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) });
        }
        foreach (var session in sessionList)
            stack.Children.Add(SessionRow(session,
                onRemoveSession is null ? null : () => onRemoveSession(session),
                onMoveSession is null ? null : (delta, toEnd) => onMoveSession(session, delta, toEnd)));

        // Dense header: name + count pill on one line, path muted underneath. No oversized type.
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var titleArea = new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        titleRow.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        titleArea.Children.Add(titleRow);
        if (!string.IsNullOrWhiteSpace(pathLabel))
            titleArea.Children.Add(new TextBlock { Text = pathLabel, Foreground = MutedBrush(), FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        if (onAddTag is not null) titleArea.Children.Add(CollectionTagsRow(tags ?? Array.Empty<string>(), onAddTag, onRemoveTag));
        header.Children.Add(titleArea);

        var rightActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        rightActions.Children.Add(new Border
        {
            Background = AccentVerySoftBrush(),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 1, 9, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = subtitle, Foreground = MutedBrush(), FontSize = 11 }
        });
        if (onCopyAgentCommand is not null)
        {
            var agentCmd = new Button
            {
                Style = (Style)Resources["PillButtonStyle"],
                Padding = new Thickness(10, 0, 10, 0),
                MinHeight = 32,
                Height = 32,
                Content = new TextBlock { Text = "Agent cmd", FontSize = 12 }
            };
            ToolTipService.SetToolTip(agentCmd, "Copy a command to paste into a chat so it files itself into this collection");
            agentCmd.Click += (_, _) => onCopyAgentCommand();
            rightActions.Children.Add(agentCmd);
        }
        if (onMoveToDeck is not null && moveTargets is { Count: > 0 })
        {
            var moveBtn = new Button
            {
                Style = (Style)Resources["IconButtonStyle"],
                Width = 32,
                Height = 32,
                MinWidth = 32,
                MinHeight = 32,
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Glyph = "" } // Move to folder
            };
            ToolTipService.SetToolTip(moveBtn, "Move this project to another deck");
            var moveFlyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
            foreach (var (tid, tname) in moveTargets)
            {
                var id = tid;
                var item = new MenuFlyoutItem { Text = "Move to " + tname };
                item.Click += (_, _) => onMoveToDeck(id);
                moveFlyout.Items.Add(item);
            }
            moveBtn.Flyout = moveFlyout;
            rightActions.Children.Add(moveBtn);
        }
        if (onDelete is not null)
        {
            var delete = new Button
            {
                Style = (Style)Resources["IconButtonStyle"],
                Width = 32,
                Height = 32,
                MinWidth = 32,
                MinHeight = 32,
                Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 14, Glyph = "\uE74D" }
            };
            ToolTipService.SetToolTip(delete, "Delete project (keeps the chats)");
            delete.Click += (_, _) => onDelete();
            rightActions.Children.Add(delete);
        }
        Grid.SetColumn(rightActions, 1);
        header.Children.Add(rightActions);

        var expander = new Expander
        {
            Background = new SolidColorBrush(Colors.Transparent),
            BorderBrush = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Header = header,
            Content = stack
        };
        // Kill the Expander's own grey header/content chrome (the inner box that held the title) so the
        // black panel container shows through cleanly. Expander.Background doesn't cover these - they're
        // driven by theme resources, overridden per-instance here.
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (var key in new[]
                 {
                     "ExpanderHeaderBackground", "ExpanderHeaderBorderBrush",
                     "ExpanderHeaderPointerOverBackground", "ExpanderHeaderPressedBackground",
                     "ExpanderHeaderDisabledBackground", "ExpanderContentBackground", "ExpanderContentBorderBrush"
                 })
            expander.Resources[key] = clear;
        expander.Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);

        // Keep the black panel container; only the inner grey box was the problem.
        return new Border
        {
            Background = PanelBrush(),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = PanelCornerRadius(),
            Padding = new Thickness(14, 6, 14, 6),
            Child = expander
        };
    }

    // A compact colored badge naming the agent that produced a chat (claude / codex).
    private Border ToolBadge(string tool)
    {
        var claude = string.Equals(tool, "claude", StringComparison.OrdinalIgnoreCase);
        var tint = claude
            ? Windows.UI.Color.FromArgb(40, 214, 153, 92)   // warm = claude
            : Windows.UI.Color.FromArgb(40, 120, 170, 255);  // cool = codex
        return new Border
        {
            Background = new SolidColorBrush(tint),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = claude ? "claude" : "codex", Foreground = StrongBrush(), FontSize = 10, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold }
        };
    }

    private UIElement SessionRow(ArchiveSession session, Action? onRemove = null, Action<int, bool>? onMove = null)
    {
        var resumeButton = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Padding = new Thickness(12, 0, 12, 0),
            MinHeight = 34,
            Content = "Resume"
        };
        resumeButton.Click += (_, _) => ResumeInTerminal(session);

        var openButton = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Padding = new Thickness(12, 0, 12, 0),
            MinHeight = 34,
            Content = "Open"
        };
        openButton.Click += (_, _) => OpenSession(session);

        var grid = new Grid
        {
            MinHeight = 40,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };

        var meta = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        meta.Children.Add(ToolBadge(session.Tool));
        meta.Children.Add(new TextBlock { Text = session.DisplayDate, Foreground = MutedBrush(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var dots = TagDots(session);
        if (dots is not null) meta.Children.Add(dots);

        grid.Children.Add(new StackPanel
        {
            Spacing = 3,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = session.DisplayTitle, Foreground = StrongBrush(), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
                meta
            }
        });
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { resumeButton, openButton, SessionMoreMenu(session, onRemove, onMove) }
        };
        Grid.SetColumn(actions, 1);
        grid.Children.Add(actions);

        return new Border
        {
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Background = new SolidColorBrush(Colors.Transparent),
            Padding = new Thickness(0, 8, 0, 8),
            Child = grid
        };
    }

    // The per-chat "..." menu on a Workspaces/Collections row. Replaces the bare remove button (which
    // read like a more-actions affordance yet deleted on a single click) with an explicit menu.
    // onMove (collections only): (delta, toEnd) manual reorder within the collection.
    private Button SessionMoreMenu(ArchiveSession session, Action? onRemove, Action<int, bool>? onMove = null)
    {
        var button = new Button
        {
            Style = (Style)Resources["IconButtonStyle"],
            Width = 34,
            Height = 34,
            MinWidth = 34,
            MinHeight = 34,
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), FontSize = 16, Glyph = "\uE712" } // More (...)
        };
        ToolTipService.SetToolTip(button, "More actions");

        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };

        var open = new MenuFlyoutItem { Text = "Open" };
        open.Click += (_, _) => OpenSession(session);
        flyout.Items.Add(open);

        var resume = new MenuFlyoutItem { Text = "Resume in terminal" };
        resume.Click += (_, _) => ResumeInTerminal(session);
        flyout.Items.Add(resume);

        var bump = new MenuFlyoutItem
        {
            Text = "Bump to top of resume list",
        };
        ToolTipService.SetToolTip(bump, BumpTooltip(session));
        bump.Click += async (_, _) => await BumpSession(session);
        flyout.Items.Add(bump);

        if (onMove is not null)
        {
            var move = new MenuFlyoutSubItem { Text = "Move in collection" };
            void MoveItem(string text, int delta, bool toEnd) { var it = new MenuFlyoutItem { Text = text }; it.Click += (_, _) => onMove(delta, toEnd); move.Items.Add(it); }
            MoveItem("Move to top", -1, true);
            MoveItem("Move up", -1, false);
            MoveItem("Move down", +1, false);
            MoveItem("Move to bottom", +1, true);
            flyout.Items.Add(move);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());

        // Add to collection (multi-membership: a chat can live in several collections at once).
        var addToCol = new MenuFlyoutSubItem { Text = "Add to collection" };
        foreach (var col in _archive.Store.Collections.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
        {
            var name = col.Name;
            var already = col.SessionIds.Contains(session.Id);
            var ci = new MenuFlyoutItem { Text = already ? "✓  " + name : name, IsEnabled = !already };
            ci.Click += async (_, _) => { await _archive.AddToCollectionAsync(session, name); SyncStatus.Text = $"Added to \"{name}\"."; RenderCurrent(); };
            addToCol.Items.Add(ci);
        }
        if (addToCol.Items.Count > 0) addToCol.Items.Add(new MenuFlyoutSeparator());
        var newCol = new MenuFlyoutItem { Text = "New collection..." };
        newCol.Click += async (_, _) => await AddSessionToNewCollectionAsync(session);
        addToCol.Items.Add(newCol);
        flyout.Items.Add(addToCol);

        // Tags: toggle the chat's current tags off, or add a new one - taggable from inside a collection.
        var tagsSub = new MenuFlyoutSubItem { Text = "Tags" };
        var userTags = ArchiveService.UserTags(session);
        foreach (var t in userTags)
        {
            var tag = t;
            var ti = new ToggleMenuFlyoutItem { Text = tag, IsChecked = true };
            ti.Click += async (_, _) => { await _archive.RemoveChatTagAsync(session, tag); RenderCurrent(); };
            tagsSub.Items.Add(ti);
        }
        if (userTags.Count > 0) tagsSub.Items.Add(new MenuFlyoutSeparator());
        var addTag = new MenuFlyoutItem { Text = "Add tag..." };
        addTag.Click += async (_, _) => { await ShowAddTagDialogAsync(session); RenderCurrent(); };
        tagsSub.Items.Add(addTag);
        flyout.Items.Add(tagsSub);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var rename = new MenuFlyoutItem { Text = "Rename" };
        rename.Click += async (_, _) => await RenameSessionByAsync(session);
        flyout.Items.Add(rename);

        var pin = new MenuFlyoutItem { Text = session.Pinned ? "Unpin" : "Pin to top" };
        pin.Click += async (_, _) => { await _archive.TogglePinAsync(session); RenderCurrent(); };
        flyout.Items.Add(pin);

        var copyPath = new MenuFlyoutItem { Text = "Copy chat path" };
        copyPath.Click += (_, _) => { SetClipboardText(session.SourcePath); SyncStatus.Text = "Copied chat path."; };
        flyout.Items.Add(copyPath);

        var copyResume = new MenuFlyoutItem { Text = "Copy agent resume prompt" };
        copyResume.Click += async (_, _) =>
        {
            await _archive.EnsureContentAsync(session);
            SetClipboardText(_archive.CopyPayload(session, "resume"));
            SyncStatus.Text = "Copied a resume prompt - paste it into a fresh Claude/Codex agent.";
        };
        flyout.Items.Add(copyResume);

        if (onRemove is not null)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var remove = new MenuFlyoutItem { Text = "Remove from collection" };
            remove.Click += (_, _) => onRemove();
            flyout.Items.Add(remove);
        }

        flyout.Items.Add(new MenuFlyoutSeparator());
        var archive = new MenuFlyoutItem { Text = "Archive (hide from lists)" };
        archive.Click += async (_, _) => { await _archive.ArchiveSessionAsync(session); RenderCurrent(); };
        flyout.Items.Add(archive);

        button.Flyout = flyout;
        return button;
    }

    // Rename any chat (not just the selected one) - app-local custom title, persisted to the store.
    private async Task RenameSessionByAsync(ArchiveSession session)
    {
        var input = new TextBox { Text = session.DisplayTitle, MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog
        {
            Title = "Rename chat",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            await _archive.RenameSessionAsync(session, input.Text);
            RenderCurrent();
        }
    }

    private static void SetClipboardText(string text)
    {
        var package = new DataPackage();
        package.SetText(text ?? "");
        Clipboard.SetContent(package);
    }

    private Border InfoPanel(string title, string body)
    {
        return Card(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, Foreground = StrongBrush(), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = body, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap }
            }
        });
    }

    private Border SettingRow(string label, string value)
    {
        var grid = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        grid.Children.Add(new TextBlock { Text = label, Foreground = StrongBrush(), FontSize = 16, TextWrapping = TextWrapping.Wrap });
        var valueBlock = new TextBlock
        {
            Text = value,
            Foreground = MutedBrush(),
            Margin = new Thickness(18, 0, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetColumn(valueBlock, 1);
        grid.Children.Add(valueBlock);

        return Card(grid);
    }

    private Border SettingsPanel(string title, string body, params UIElement[] controls)
    {
        // Cap the measure so label/control pairs stay visually associated instead of being
        // flung to opposite edges of a wide panel (the "far-right gap" the dashboard would have).
        var stack = new StackPanel
        {
            Spacing = _archive.Store.Settings.Density == "compact" ? 10 : 14,
            MaxWidth = 660,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        stack.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = body, Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap });
        foreach (var control in controls) stack.Children.Add(control);
        return Card(stack);
    }

    private UIElement SettingControlRow(string label, Control control)
    {
        control.HorizontalAlignment = HorizontalAlignment.Right;
        if (control is ComboBox combo) combo.CornerRadius = ControlCornerRadius();
        var grid = new Grid
        {
            MinHeight = 44,
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Foreground = StrongBrush(),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private ComboBox ThemeCombo()
    {
        var combo = new ComboBox { Width = 220, SelectedValuePath = "Tag" };
        combo.Items.Add(new ComboBoxItem { Content = "AMOLED Black", Tag = "amoled" });
        combo.Items.Add(new ComboBoxItem { Content = "Graphite", Tag = "graphite" });
        combo.Items.Add(new ComboBoxItem { Content = "Ink", Tag = "ink" });
        SelectComboItem(combo, _archive.Store.Settings.Theme);
        combo.SelectionChanged += async (_, _) =>
        {
            if (!_storeLoaded || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string theme) return;
            _archive.Store.Settings.Theme = theme;
            ApplyThemeAndShape();
            await _archive.SaveAsync();
        };
        return combo;
    }

    private ComboBox AccentCombo()
    {
        var combo = new ComboBox { Width = 220, SelectedValuePath = "Tag" };
        foreach (var preset in AccentPresets())
        {
            combo.Items.Add(new ComboBoxItem { Content = preset.Name, Tag = preset.Hex });
        }
        SelectComboItem(combo, _archive.Store.Settings.AccentHex);
        combo.SelectionChanged += async (_, _) =>
        {
            if (!_storeLoaded || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string hex) return;
            _archive.Store.Settings.Accent = item.Content?.ToString()?.ToLowerInvariant() ?? "custom";
            _archive.Store.Settings.AccentHex = hex;
            SetAccent(hex);
            await _archive.SaveAsync();
            RenderCurrent();
        };
        return combo;
    }

    private ColorPicker AccentColorPicker()
    {
        // Compact: hide the bulky RGB/HSV text inputs (the "More" toggle clipped to "Mo"), keeping the
        // spectrum + sliders + hex so the picker stays short and nothing clips in the Settings viewport.
        var picker = new ColorPicker
        {
            Width = 280,
            Color = _accentColor,
            IsAlphaEnabled = false,
            IsAlphaSliderVisible = false,
            IsAlphaTextInputVisible = false,
            IsColorChannelTextInputVisible = false,
            IsHexInputVisible = true
        };
        picker.ColorChanged += async (_, args) =>
        {
            if (!_storeLoaded) return;
            _archive.Store.Settings.Accent = "custom";
            _archive.Store.Settings.AccentHex = HexFromColor(args.NewColor);
            SetAccent(_archive.Store.Settings.AccentHex);
            await _archive.SaveAsync();
        };
        return picker;
    }

    private ComboBox ShapeCombo()
    {
        var combo = new ComboBox { Width = 220, SelectedValuePath = "Tag" };
        combo.Items.Add(new ComboBoxItem { Content = "Pill", Tag = "pill" });
        combo.Items.Add(new ComboBoxItem { Content = "Rounded", Tag = "rounded" });
        combo.Items.Add(new ComboBoxItem { Content = "Compact", Tag = "compact" });
        SelectComboItem(combo, _archive.Store.Settings.Radius);
        combo.SelectionChanged += async (_, _) =>
        {
            if (!_storeLoaded || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string shape) return;
            _archive.Store.Settings.Radius = shape;
            SetShape(shape);
            ApplyThemeAndShape();
            await _archive.SaveAsync();
            RenderCurrent();
        };
        return combo;
    }

    private ComboBox DensityCombo()
    {
        var combo = new ComboBox { Width = 220, SelectedValuePath = "Tag" };
        combo.Items.Add(new ComboBoxItem { Content = "Comfortable", Tag = "comfortable" });
        combo.Items.Add(new ComboBoxItem { Content = "Compact", Tag = "compact" });
        combo.Items.Add(new ComboBoxItem { Content = "Spacious", Tag = "spacious" });
        SelectComboItem(combo, _archive.Store.Settings.Density);
        combo.SelectionChanged += async (_, _) =>
        {
            if (!_storeLoaded || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string density) return;
            _archive.Store.Settings.Density = density;
            await _archive.SaveAsync();
            RenderCurrent();
        };
        return combo;
    }

    private Border AiProviderPanel()
    {
        var provider = _archive.ActiveAiProvider();
        var stack = new StackPanel { Spacing = _archive.Store.Settings.Density == "compact" ? 10 : 14 };
        stack.Children.Add(new TextBlock { Text = "AI providers", Foreground = StrongBrush(), FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = "Manage OpenAI-compatible providers. API keys are stored in Windows credentials, not app JSON.", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap });
        stack.Children.Add(SettingControlRow("Active provider", ProviderCombo()));

        if (provider is not null)
        {
            stack.Children.Add(ProviderEditor(provider));
        }

        var addDeepSeek = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Add DeepSeek preset" };
        addDeepSeek.Click += async (_, _) =>
        {
            _archive.EnsureAiProvider("DeepSeek", "https://api.deepseek.com", "deepseek-v4-flash");
            await _archive.SaveAsync();
            RenderSettings();
        };

        var addOpenAi = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Add OpenAI preset" };
        addOpenAi.Click += async (_, _) =>
        {
            _archive.EnsureAiProvider("OpenAI", "https://api.openai.com/v1", "gpt-4.1-mini");
            await _archive.SaveAsync();
            RenderSettings();
        };

        var addCustom = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Add custom compatible" };
        addCustom.Click += async (_, _) => await AddCustomProviderAsync();

        stack.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { addDeepSeek, addOpenAi, addCustom }
        });

        return Card(stack);
    }

    private ComboBox ProviderCombo()
    {
        var combo = new ComboBox { Width = 260, SelectedValuePath = "Tag" };
        foreach (var provider in _archive.Store.Settings.AiProviders.OrderBy(provider => provider.Name))
        {
            combo.Items.Add(new ComboBoxItem { Content = provider.Name, Tag = provider.Id });
        }
        SelectComboItem(combo, _archive.Store.Settings.ActiveAiProviderId);
        combo.SelectionChanged += async (_, _) =>
        {
            if (!_storeLoaded || combo.SelectedItem is not ComboBoxItem item || item.Tag is not string id) return;
            _archive.Store.Settings.ActiveAiProviderId = id;
            await _archive.SaveAsync();
            RenderSettings();
        };
        return combo;
    }

    private UIElement ProviderEditor(AiProviderSettings provider)
    {
        var nameBox = ProviderTextBox(provider.Name);
        var baseUrlBox = ProviderTextBox(provider.BaseUrl);
        var modelCombo = ProviderModelCombo(provider);
        var keyBox = new PasswordBox
        {
            Width = 360,
            PlaceholderText = HasApiKey(provider.Id) ? "Saved. Enter a new key to replace." : "Paste API key",
            CornerRadius = ControlCornerRadius()
        };

        var saveButton = new Button { Style = (Style)Resources["PrimaryPillButtonStyle"], Content = "Save provider" };
        saveButton.Click += async (_, _) =>
        {
            provider.Name = string.IsNullOrWhiteSpace(nameBox.Text) ? provider.Name : nameBox.Text.Trim();
            provider.BaseUrl = baseUrlBox.Text.Trim().TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(keyBox.Password)) SaveApiKey(provider.Id, keyBox.Password);
            provider.Model = SelectedModel(modelCombo) ?? provider.Model;
            await RefreshProviderModelsAsync(provider, modelCombo, showDialog: false);
            await _archive.SaveAsync();
            RenderSettings();
        };

        var refreshModelsButton = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Detect models" };
        refreshModelsButton.Click += async (_, _) => await RefreshProviderModelsAsync(provider, modelCombo, showDialog: true);

        var manualModelButton = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Manual model" };
        manualModelButton.Click += async (_, _) => await SetManualModelAsync(provider);

        var testButton = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Test" };
        testButton.Click += async (_, _) => await TestProviderAsync(provider);

        var deleteKeyButton = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Delete key" };
        deleteKeyButton.Click += (_, _) =>
        {
            DeleteApiKey(provider.Id);
            RenderSettings();
        };

        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                SettingControlRow("Name", nameBox),
                SettingControlRow("Base URL", baseUrlBox),
                SettingControlRow("Model", modelCombo),
                SettingControlRow("API key", keyBox),
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { saveButton, refreshModelsButton, manualModelButton, testButton, deleteKeyButton }
                }
            }
        };
    }

    private ComboBox ProviderModelCombo(AiProviderSettings provider)
    {
        var combo = new ComboBox { Width = 360, SelectedValuePath = "Tag" };
        var models = provider.Models
            .Append(provider.Model)
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(model => model, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (models.Count == 0)
        {
            combo.Items.Add(new ComboBoxItem { Content = "Detect models after saving a key", Tag = "" });
        }
        else
        {
            foreach (var model in models)
            {
                combo.Items.Add(new ComboBoxItem { Content = model, Tag = model });
            }
        }
        SelectComboItem(combo, provider.Model);
        return combo;
    }

    private static string? SelectedModel(ComboBox combo)
    {
        return combo.SelectedItem is ComboBoxItem item && item.Tag is string model && !string.IsNullOrWhiteSpace(model)
            ? model
            : null;
    }

    private TextBox ProviderTextBox(string value)
    {
        return new TextBox
        {
            Text = value,
            Width = 360,
            CornerRadius = ControlCornerRadius()
        };
    }

    private async Task AddCustomProviderAsync()
    {
        var name = new TextBox { Text = "Custom provider", MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var baseUrl = new TextBox { Text = "https://api.example.com/v1", MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var model = new TextBox { Text = "model-name", MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Name", Foreground = StrongBrush() },
                name,
                new TextBlock { Text = "Base URL", Foreground = StrongBrush() },
                baseUrl,
                new TextBlock { Text = "Model", Foreground = StrongBrush() },
                model
            }
        };
        var dialog = new ContentDialog
        {
            Title = "Add provider",
            Content = content,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            _archive.EnsureAiProvider(name.Text, baseUrl.Text, model.Text);
            await _archive.SaveAsync();
            RenderSettings();
        }
    }

    private async Task RefreshProviderModelsAsync(AiProviderSettings provider, ComboBox modelCombo, bool showDialog)
    {
        var key = LoadApiKey(provider.Id);
        if (string.IsNullOrWhiteSpace(key))
        {
            if (showDialog) await ShowInfoAsync("Detect models", $"Save an API key for {provider.Name} first.");
            return;
        }

        try
        {
            var models = await _ai.ListModelsAsync(provider, key);
            provider.Models = models.ToList();
            if (string.IsNullOrWhiteSpace(provider.Model) || provider.Models.All(model => model != provider.Model))
            {
                provider.Model = provider.Models.FirstOrDefault() ?? provider.Model;
            }
            await _archive.SaveAsync();
            if (showDialog)
            {
                await ShowInfoAsync("Detect models", $"Found {provider.Models.Count} models for {provider.Name}.");
            }
            RenderSettings();
        }
        catch (Exception ex)
        {
            if (showDialog) await ShowInfoAsync("Detect models", ex.Message);
            modelCombo.Items.Clear();
            modelCombo.Items.Add(new ComboBoxItem { Content = provider.Model, Tag = provider.Model });
            modelCombo.SelectedIndex = 0;
        }
    }

    private async Task SetManualModelAsync(AiProviderSettings provider)
    {
        var input = new TextBox { Text = provider.Model, MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog
        {
            Title = "Manual model",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            provider.Model = input.Text.Trim();
            if (!provider.Models.Contains(provider.Model, StringComparer.OrdinalIgnoreCase))
            {
                provider.Models.Add(provider.Model);
            }
            await _archive.SaveAsync();
            RenderSettings();
        }
    }

    private async Task TestProviderAsync(AiProviderSettings provider)
    {
        var key = LoadApiKey(provider.Id);
        if (string.IsNullOrWhiteSpace(key))
        {
            await ShowInfoAsync("Provider test", $"No API key is saved for {provider.Name}.");
            return;
        }

        try
        {
            var result = await _ai.TestAsync(provider, key);
            await ShowInfoAsync("Provider test", $"Success: {result}");
        }
        catch (Exception ex)
        {
            await ShowInfoAsync("Provider test", ex.Message);
        }
    }

    private Border EmptyBlock(string title, string body) => InfoPanel(title, body);

    private Border TextPanel(string text)
    {
        return Card(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = StrongBrush(),
            FontFamily = new FontFamily("Consolas"),
            LineHeight = 21
        });
    }

    private Border Card(UIElement child)
    {
        return new Border
        {
            Background = PanelBrush(),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = PanelCornerRadius(),
            Padding = new Thickness(18),
            Child = child
        };
    }

    private void CopyContext_Click(object sender, RoutedEventArgs e) => Copy("resume");
    private void CopyCode_Click(object sender, RoutedEventArgs e) => Copy("code");
    private void CopyPath_Click(object sender, RoutedEventArgs e) => Copy("path");

    private async void Copy(string mode)
    {
        if (_selected is null) return;
        var session = _selected;
        if (mode is not ("path" or "paths")) await _archive.EnsureContentAsync(session); // off-thread, no UI block
        var package = new DataPackage();
        package.SetText(_archive.CopyPayload(session, mode));
        Clipboard.SetContent(package);
    }

    private void CopyPath(ArchiveSession session)
    {
        var package = new DataPackage();
        package.SetText(_archive.CopyPayload(session, "path"));
        Clipboard.SetContent(package);
    }

    private void OpenSession(ArchiveSession session)
    {
        _selected = session;
        SessionList.SelectedItem = session;
        Navigate("Archive");
    }

    private void Source_Click(object sender, RoutedEventArgs e) => Navigate("Source");
    private void Restore_Click(object sender, RoutedEventArgs e) => Navigate("Restore");

    private void Review_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null && !_selected.Tags.Contains("review-later"))
        {
            _selected.Tags.Add("review-later");
            RenderTags();
        }
    }

    private void SessionList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var session = FindSessionFromElement(e.OriginalSource as DependencyObject) ?? SessionList.SelectedItem as ArchiveSession;
        if (session is null) return;

        _selected = session;
        SessionList.SelectedItem = session;

        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false }; // snap open instantly (no fade-in lag)
        var pinItem = new MenuFlyoutItem { Text = session.Pinned ? "Unpin chat" : "Pin chat" };
        pinItem.Click += async (_, _) => await TogglePinSelected();
        flyout.Items.Add(pinItem);

        var bumpItem = new MenuFlyoutItem { Text = "Bump to top of resume list" };
        ToolTipService.SetToolTip(bumpItem, BumpTooltip(session));
        bumpItem.Click += async (_, _) => await BumpSession(session);
        flyout.Items.Add(bumpItem);

        var renameItem = new MenuFlyoutItem { Text = "Rename chat" };
        renameItem.Click += async (_, _) => await RenameSelected();
        flyout.Items.Add(renameItem);

        var addToCollection = new MenuFlyoutSubItem { Text = "Add to collection" };
        foreach (var collection in _archive.Store.Collections.Values.OrderBy(c => c.Name))
        {
            var collectionItem = new MenuFlyoutItem { Text = collection.Name, Tag = collection.Name };
            collectionItem.Click += async (menuSender, _) =>
            {
                if ((menuSender as MenuFlyoutItem)?.Tag is string name && _selected is not null)
                {
                    await _archive.AddToCollectionAsync(_selected, name);
                    RenderCurrent();
                }
            };
            addToCollection.Items.Add(collectionItem);
        }
        var newCollectionItem = new MenuFlyoutItem { Text = "New collection..." };
        newCollectionItem.Click += async (_, _) => await AddSelectedToNewCollection();
        addToCollection.Items.Add(newCollectionItem);
        flyout.Items.Add(addToCollection);

        var copyItem = new MenuFlyoutItem { Text = "Copy restore packet" };
        copyItem.Click += (_, _) => Copy("restore");
        flyout.Items.Add(copyItem);

        var copyPathItem = new MenuFlyoutItem { Text = "Copy chat path" };
        copyPathItem.Click += (_, _) => Copy("path");
        flyout.Items.Add(copyPathItem);

        var archiveItem = new MenuFlyoutItem { Text = "Archive chat" };
        archiveItem.Click += async (_, _) => await ArchiveSelected();
        flyout.Items.Add(archiveItem);

        flyout.ShowAt(SessionList, e.GetPosition(SessionList));
    }

    private void SetAccent(string hex)
    {
        _accentColor = Windows.UI.Color.FromArgb(
            255,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
        SetBrush("AccentBrush", _accentColor);
        RenderTags();
    }

    private void ApplyThemeAndShape()
    {
        SetShape(_archive.Store.Settings.Radius);
        switch (_archive.Store.Settings.Theme)
        {
            case "graphite":
                SetBrush("AmoledBrush", Windows.UI.Color.FromArgb(255, 6, 7, 8));
                SetBrush("PanelBrush", Windows.UI.Color.FromArgb(255, 14, 15, 18));
                SetBrush("RaisedBrush", Windows.UI.Color.FromArgb(255, 24, 25, 29));
                SetBrush("LineBrush", Windows.UI.Color.FromArgb(255, 44, 46, 54));
                break;
            case "ink":
                SetBrush("AmoledBrush", Windows.UI.Color.FromArgb(255, 0, 0, 0));
                SetBrush("PanelBrush", Windows.UI.Color.FromArgb(255, 4, 4, 6));
                SetBrush("RaisedBrush", Windows.UI.Color.FromArgb(255, 11, 11, 14));
                SetBrush("LineBrush", Windows.UI.Color.FromArgb(255, 33, 34, 40));
                break;
            default:
                SetBrush("AmoledBrush", Windows.UI.Color.FromArgb(255, 0, 0, 0));
                SetBrush("PanelBrush", Windows.UI.Color.FromArgb(255, 7, 8, 10));
                SetBrush("RaisedBrush", Windows.UI.Color.FromArgb(255, 16, 17, 20));
                SetBrush("LineBrush", Windows.UI.Color.FromArgb(255, 36, 38, 45));
                break;
        }

        SetAccent(_archive.Store.Settings.AccentHex);
        ApplyShapeToTree(this);
    }

    private void SetShape(string shape)
    {
        (_panelRadius, _controlRadius) = shape switch
        {
            "compact" => (12, 12),
            "rounded" => (18, 16),
            _ => (24, 20)
        };
        _archive.Store.Settings.PanelRadius = _panelRadius;
        _archive.Store.Settings.ControlRadius = _controlRadius;
    }

    private void ApplyShapeToTree(DependencyObject root)
    {
        if (root is Button button) button.CornerRadius = ControlCornerRadius();
        if (root is TextBox textBox) textBox.CornerRadius = ControlCornerRadius();
        if (root is ComboBox comboBox) comboBox.CornerRadius = ControlCornerRadius();
        if (root is Border border && border.CornerRadius.TopLeft > 0)
        {
            border.CornerRadius = border.ActualWidth <= 48 && border.ActualHeight <= 48
                ? new CornerRadius(Math.Min(border.ActualWidth, border.ActualHeight) / 2)
                : PanelCornerRadius();
        }
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) ApplyShapeToTree(VisualTreeHelper.GetChild(root, i));
    }

    private static void SetBrush(string key, Windows.UI.Color color)
    {
        if (Application.Current.Resources[key] is SolidColorBrush brush) brush.Color = color;
        else Application.Current.Resources[key] = new SolidColorBrush(color);
    }

    private static void SelectComboItem(ComboBox combo, string value)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
        combo.SelectedIndex = 0;
    }

    private static IReadOnlyList<AccentPreset> AccentPresets() =>
    [
        new("Rose", "#fb7185"),
        new("Mint", "#43d6a4"),
        new("Violet", "#a78bfa"),
        new("Amber", "#f6b44b"),
        new("Blue", "#7aa2ff")
    ];

    private async Task TogglePinSelected()
    {
        if (_selected is null) return;
        await _archive.TogglePinAsync(_selected);
        SelectFirstSession();
        RenderCurrent();
    }

    private async Task RenameSelected()
    {
        if (_selected is null) return;
        var input = new TextBox { Text = _selected.DisplayTitle, MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog
        {
            Title = "Rename chat",
            Content = input,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await _archive.RenameSessionAsync(_selected, input.Text);
            RenderCurrent();
        }
    }

    private async Task AddSelectedToNewCollection()
    {
        if (_selected is null) return;
        var input = new TextBox { Text = "Saved", MinWidth = 420, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog
        {
            Title = "New collection",
            Content = input,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            await _archive.AddToCollectionAsync(_selected, input.Text);
            RenderCurrent();
        }
    }

    private async Task ArchiveSelected()
    {
        if (_selected is null) return;
        await _archive.ArchiveSessionAsync(_selected);
        SelectFirstSession();
        RenderCurrent();
    }

    private async Task ShowInfoAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    private static string CredentialResource(string providerId) => $"CodexLocalRetrieval.ApiKey.{providerId}";

    private static bool HasApiKey(string providerId) => !string.IsNullOrWhiteSpace(LoadApiKey(providerId));

    private static string LoadApiKey(string providerId)
    {
        // Autograb from the environment FIRST (the "set it once" canonical source the user asked
        // for); fall back to a key saved in the app's credential vault if no env var is set.
        var env = EnvApiKey(providerId);
        if (!string.IsNullOrWhiteSpace(env)) return env;
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource(providerId), Environment.UserName);
            credential.RetrievePassword();
            return credential.Password;
        }
        catch
        {
            return "";
        }
    }

    // Autograb: read the key from a standard env var so it can be set once
    // (DEEPSEEK_API_KEY / OPENAI_API_KEY / else CLR_<ID>_API_KEY) and never pasted into the app or
    // written to the store. A key saved in the credential vault still takes precedence.
    private static string EnvApiKey(string providerId)
    {
        string[] names = providerId.ToLowerInvariant() switch
        {
            "deepseek" => new[] { "DEEPSEEK_API_KEY" },
            "openai" => new[] { "OPENAI_API_KEY" },
            _ => new[] { $"CLR_{providerId.ToUpperInvariant()}_API_KEY" }
        };
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return "";
    }

    public static string ApiKeySource(string providerId)
    {
        if (!string.IsNullOrWhiteSpace(EnvApiKey(providerId))) return "environment variable";
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource(providerId), Environment.UserName);
            credential.RetrievePassword();
            if (!string.IsNullOrWhiteSpace(credential.Password)) return "saved in app";
        }
        catch { }
        return "none";
    }

    private static void SaveApiKey(string providerId, string apiKey)
    {
        DeleteApiKey(providerId);
        var vault = new PasswordVault();
        vault.Add(new PasswordCredential(CredentialResource(providerId), Environment.UserName, apiKey));
    }

    private static void DeleteApiKey(string providerId)
    {
        try
        {
            var vault = new PasswordVault();
            var credential = vault.Retrieve(CredentialResource(providerId), Environment.UserName);
            vault.Remove(credential);
        }
        catch
        {
            // Missing credentials are fine.
        }
    }

    private static ArchiveSession? FindSessionFromElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is FrameworkElement { DataContext: ArchiveSession session }) return session;
            source = VisualTreeHelper.GetParent(source);
        }
        return null;
    }

    private static string StripCode(string text) => Regex.Replace(text, "```[\\s\\S]*?```", "").Trim();

    // Reading-mode cleanup (logic lives in Core.ArchiveService.ForReading so it's unit-tested):
    // drop fenced code + machine-context noise; show a marker when a message was pure context.
    private static string CleanReadingText(string text)
    {
        var value = ArchiveService.ForReading(text);
        return value.Length == 0 ? "(IDE / environment context)" : value;
    }
    private static string FormatDate(string value) => DateTime.TryParse(value, out var date) ? date.ToString("MMM d") : "";
    private static string HexFromColor(Windows.UI.Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}".ToLowerInvariant();

    private static Windows.UI.Color ColorFromHex(string hex)
    {
        try
        {
            var h = hex.TrimStart('#');
            return Windows.UI.Color.FromArgb(255,
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }
        catch { return Windows.UI.Color.FromArgb(255, 0x60, 0xA5, 0xFA); }
    }
    private SolidColorBrush StrongBrush() => (SolidColorBrush)Application.Current.Resources["TextStrongBrush"];
    private SolidColorBrush MutedBrush() => (SolidColorBrush)Application.Current.Resources["TextMutedBrush"];
    private SolidColorBrush PanelBrush() => (SolidColorBrush)Application.Current.Resources["PanelBrush"];
    private SolidColorBrush RaisedBrush() => (SolidColorBrush)Application.Current.Resources["RaisedBrush"];
    private SolidColorBrush LineBrush() => (SolidColorBrush)Application.Current.Resources["LineBrush"];
    private SolidColorBrush AccentSoftBrush() => new(Windows.UI.Color.FromArgb(90, _accentColor.R, _accentColor.G, _accentColor.B));
    private SolidColorBrush AccentVerySoftBrush() => new(Windows.UI.Color.FromArgb(30, _accentColor.R, _accentColor.G, _accentColor.B));
    private SolidColorBrush AccentBrush() => new(_accentColor);
    private CornerRadius PanelCornerRadius() => new(_panelRadius);
    private CornerRadius ControlCornerRadius() => new(_controlRadius);
    private sealed record AccentPreset(string Name, string Hex);
}
