using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

// Tagging & filtering. Chat tags live on ArchiveSession.Tags (app-only, persisted + backed up);
// collection tags live on ArchiveCollection.Tags. The chat list filters by an ANY-match set of
// active tags combined with the text search. See ArchiveService tag API.
public sealed partial class MainPage
{
    // Compound chat-list filter: tags you must have (include) + tags you must NOT have (exclude) +
    // ANY/ALL for the includes. Empty => no tag filter. This is the "active but not cpp" machinery.
    private readonly HashSet<string> _includeTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _excludeTags = new(StringComparer.OrdinalIgnoreCase);
    private bool _matchAllIncludes;
    private string? _filterCollectionId;   // scope the chat list to one project (collection), or null
    private string _dateMode = "";          // SORT: "" recent-activity | created-newest/oldest | last-user | first-user
    private string _dateRange = "";         // RANGE filter (combines with sort): "" any | today | week | month
    private string _toolFilter = "";        // "" both | "codex" | "claude"

    // SORT of the list (single choice). Combines with the date-range + agent + tag filters.
    private static readonly (string Value, string Label)[] DateModes =
    {
        ("", "Recent activity (default)"),
        ("created-newest", "Created · newest first"),
        ("created-oldest", "Created · oldest first"),
        ("last-user", "Last user message"),
        ("first-user", "First user message"),
    };
    private string DateModeLabel() => DateModes.FirstOrDefault(m => m.Value == _dateMode).Label ?? "Recent activity (default)";

    // DATE-RANGE filter (single choice, COMBINES with the sort).
    private static readonly (string Value, string Label)[] DateRanges =
    {
        ("", "Any time"),
        ("today", "Created today"),
        ("week", "Created · last 7 days"),
        ("month", "Created · last 30 days"),
    };
    private string DateRangeLabel() => DateRanges.FirstOrDefault(r => r.Value == _dateRange).Label ?? "Any time";

    private static readonly (string Value, string Label)[] ToolFilters =
    {
        ("", "Any agent"),
        ("codex", "Codex"),
        ("claude", "Claude"),
    };
    private string ToolFilterLabel() => ToolFilters.FirstOrDefault(t => t.Value == _toolFilter).Label ?? "Any agent";

    private bool _showHidden;   // reveal the auto-hidden one-off / spam chats (default off = they're hidden)
    private bool _showAutomationWorkers;   // reveal tandem/orchestration workers (default off = they're hidden)
    private int _minUserMsgs;   // hide chats with fewer than this many real user prompts (0 = off)
    private static readonly (int Value, string Label)[] MinUserMsgOptions =
    {
        (0, "Any"),
        (2, "2+ your messages"),
        (3, "3+ your messages"),
        (5, "5+ your messages"),
        (10, "10+ your messages"),
        (25, "25+ your messages"),
    };
    private string MinUserMsgLabel() => MinUserMsgOptions.FirstOrDefault(m => m.Value == _minUserMsgs).Label ?? "Any";

    private ChatFilter CurrentChatFilter() => new()
    {
        Query = SearchBox.Text,
        IncludeTags = _includeTags.ToList(),
        ExcludeTags = _excludeTags.ToList(),
        MatchAllIncludes = _matchAllIncludes,
        CollectionId = _filterCollectionId,
        DateMode = _dateMode,
        DateRange = _dateRange,
        Tool = _toolFilter,
        MinUserMessages = _minUserMsgs,
        ShowHidden = _showHidden,
        ShowAutomationWorkers = _showAutomationWorkers
    };

    // The funnel filters WITHOUT the text query — used to constrain the Deep-search results to the same
    // agent / date / min-messages / tags / project the main list is filtered by.
    private ChatFilter CurrentFiltersNoQuery() => new()
    {
        IncludeTags = _includeTags.ToList(),
        ExcludeTags = _excludeTags.ToList(),
        MatchAllIncludes = _matchAllIncludes,
        CollectionId = _filterCollectionId,
        DateMode = _dateMode,
        DateRange = _dateRange,
        Tool = _toolFilter,
        MinUserMessages = _minUserMsgs,
        ShowHidden = _showHidden,
        ShowAutomationWorkers = _showAutomationWorkers,
    };

    // Session ids that pass the active funnel filters (no text query). Empty filters -> null (no restriction).
    internal HashSet<string>? ActiveFilterAllowedIds()
    {
        var f = CurrentFiltersNoQuery();
        if (f.IsEmpty) return null;
        return new HashSet<string>(_archive.FilterChats(f).Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
    }

    private string? FilterCollectionName() =>
        _filterCollectionId is not null && _archive.Store.Collections.TryGetValue(_filterCollectionId, out var c) ? c.Name : null;

    // ---- Chat list: unified text + compound tag filter ---------------------------------------

    // Every keystroke used to run ApplyFilters synchronously; this collapses a burst of typing into one
    // filter pass 150 ms after you stop. The dispatcher hop is injected (the debouncer's timer fires on
    // the thread pool, but every UI touch below must happen on the UI thread).
    private TrailingDebouncer<(string Query, long SelectionRevision)>? _searchDebouncer;
    internal TrailingDebouncer<(string Query, long SelectionRevision)> SearchDebouncer => _searchDebouncer ??= new TrailingDebouncer<(string Query, long SelectionRevision)>(
        // Capture the selection revision when the keystroke is posted. If a click happens before the
        // trailing callback runs, the callback must not select the first filtered row over that click.
        posted => ApplyFilters(posted.SelectionRevision),
        TrailingDebouncer<(string Query, long SelectionRevision)>.DefaultDelay,
        run => DispatcherQueue.TryEnqueue(() => run()));

    private void ApplyFilters(long? postedSelectionRevision = null)
    {
        var previousId = _selected?.Id;
        var preserveSelection = postedSelectionRevision is not null && SelectionRevision != postedSelectionRevision.Value;
        var results = _archive.FilterChats(CurrentChatFilter());
        var currentId = _selected?.Id;
        var selection = preserveSelection && currentId is not null
            ? results.FirstOrDefault(s => string.Equals(s.Id, currentId, StringComparison.OrdinalIgnoreCase))
            : null;
        // A newer click owns selection even when filtering removed that row; never replace it with an unrelated first result.
        // last-user / first-user sorts flip each visible row's title to what YOU said.
        var titleMode = (_dateMode == "last-user" || _dateMode == "first-user") ? _dateMode : "";
        foreach (var s in results) s.RowTitleMode = titleMode;
        // A sort OR a search picks the order; preserve it (don't let RefreshSessions re-sort by recent).
        var preserve = _dateMode.Length > 0 || !string.IsNullOrWhiteSpace(SearchBox.Text);
        RunSessionListRefresh(() =>
        {
            _archive.RefreshSessions(results, preserveOrder: preserve);
            if (selection is not null)
            {
                ApplySelection(selection);
            }
            else
            {
                SelectFirstSession();
            }
        });
        RenderTagFilterBar();
        // NARROW RENDER: filtering touches the session list and the tag strip, nothing else. A full-screen
        // rebuild here re-ran whatever page you were on for every keystroke — on Running that meant a fresh
        // host probe/SSH spawn per key. The transcript pane is only stale if the selection actually moved.
        if (!string.Equals(previousId, _selected?.Id, StringComparison.OrdinalIgnoreCase)) RenderSelectedSessionPane();
        // The search page IS a view of the filter, so it stays live — one pane, not the whole screen.
        if (_screen == "Search") RenderSearch(string.IsNullOrWhiteSpace(_deepSearchQuery) ? SearchBox.Text : _deepSearchQuery);
    }

    // The only screens whose body is a view of the SELECTED chat. Re-rendering one of these is the
    // narrow equivalent of RenderCurrent() for a selection change — no nav chrome, no page switch,
    // and nothing at all on the screens (Running, Ask, Collections…) that don't follow the selection.
    private void RenderSelectedSessionPane()
    {
        switch (_screen)
        {
            case "Archive": RenderArchive(); break;
            case "Source": RenderSource(); break;
            case "Restore": RenderRestore(); break;
            default: return;
        }
        UpdateChrome();   // the right rail / header actions hide for a read-only snapshot, so they follow selection
    }

    // Re-run the ACTIVE filter WITHOUT changing your selection — used after a mutation (add-to-collection,
    // rename, tag, pin, archive) or a background sync, so the list stays filtered instead of snapping back
    // to everything. (ApplyFilters, by contrast, re-selects the first row — right for a fresh filter change.)
    private void ReapplyActiveFilter()
    {
        var keep = _selected?.Id;
        var results = _archive.FilterChats(CurrentChatFilter());
        var titleMode = (_dateMode == "last-user" || _dateMode == "first-user") ? _dateMode : "";
        foreach (var s in results) s.RowTitleMode = titleMode;
        var preserve = _dateMode.Length > 0 || !string.IsNullOrWhiteSpace(SearchBox.Text);
        RunSessionListRefresh(() =>
        {
            _archive.RefreshSessions(results, preserveOrder: preserve);
            var restored = !string.IsNullOrEmpty(keep)
                ? _archive.Sessions.FirstOrDefault(x => string.Equals(x.Id, keep, StringComparison.OrdinalIgnoreCase))
                : null;
            ApplySelection(restored);
        });
        RenderTagFilterBar();
    }

    // ENTER in the search box: scan the full transcript FILES (fuzzy word-overlap) and append any chats the
    // fast in-memory search missed — so pasting a specific turn (even one from mid-chat, past the 6000-char
    // cap, with a word or two off) surfaces the chat it came from. Heavy, so it's Enter-only, not per-keystroke.
    private int _diskSearchGen;
    private async void DeepSearchCurrentQuery()
    {
        var q = (SearchBox.Text ?? "").Trim();
        var gen = ++_diskSearchGen;
        if (q.Length < 8) return;   // deep search is for a phrase, not a one-word keyword
        SyncStatus.Text = "Searching full transcripts…";
        IReadOnlyList<ArchiveSession> extra;
        try { extra = await _archive.SearchDiskPhraseAsync(q, 40); }
        catch { SyncStatus.Text = "Full-transcript search failed."; return; }
        if (gen != _diskSearchGen || !string.Equals((SearchBox.Text ?? "").Trim(), q, StringComparison.Ordinal)) return;   // a newer search superseded this
        var have = new HashSet<string>(_archive.Sessions.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        var titleMode = (_dateMode == "last-user" || _dateMode == "first-user") ? _dateMode : "";
        var added = 0;
        foreach (var s in extra)
        {
            if (have.Contains(s.Id) || s.Archived) continue;
            if (!_showHidden && ArchiveService.IsLowSignalChat(s)) continue;   // keep one-offs hidden unless revealed
            if (!_showAutomationWorkers && ArchiveService.ShouldAutoHideAutomationWorker(s)) continue;
            s.RowTitleMode = titleMode;
            _archive.Sessions.Add(s);
            added++;
        }
        if (_archive.Sessions.Count > 0 && SessionList.SelectedItem is null) SelectFirstSession();
        RenderCurrent();
        SyncStatus.Text = added > 0
            ? $"+{added} chat{(added == 1 ? "" : "s")} matched this phrase in the full transcript."
            : (_archive.Sessions.Count > 0 ? $"{_archive.Sessions.Count} match{(_archive.Sessions.Count == 1 ? "" : "es")}." : "No chat contains that phrase.");
    }

    // Cycle a tag through the filter: none -> include -> exclude -> none (include & exclude are exclusive).
    private void CycleTagFilter(string tag)
    {
        if (_includeTags.Remove(tag)) { _excludeTags.Add(tag); }
        else if (_excludeTags.Remove(tag)) { /* -> none */ }
        else { _includeTags.Add(tag); }
        ApplyFilters();
    }
    private void SetTagInclude(string tag) { _excludeTags.Remove(tag); _includeTags.Add(tag); }

    // ---- Unified tag chip --------------------------------------------------------------------
    // ONE chip for every tag context (chat tags, collection tags, filter pills). Operational/AMOLED:
    // tags read as METADATA, not buttons - a quiet raised label carrying a small colored DOT for
    // identity (auto-assigned per name, user-overridable), NOT a colored fill (which looks noisy on
    // black). Rose tint is reserved for the active-filter state. Body tap = onTap (filter); the x =
    // onRemove (null hides it) with a small quiet glyph + hover. Converged with an independent design pass.
    private static readonly Windows.UI.Color ChipText = Windows.UI.Color.FromArgb(255, 0xD9, 0xDA, 0xE0);
    private static readonly Windows.UI.Color ChipXIdle = Windows.UI.Color.FromArgb(255, 0x8B, 0x8D, 0x96);
    private SolidColorBrush ChipNeutralHover() => new(Windows.UI.Color.FromArgb(255, 0x18, 0x1A, 0x1F));

    private FrameworkElement TagChip(string text, bool active, Action? onTap, Action? onRemove, string? suffix = null, string? dotColor = null, Action<FrameworkElement>? onColorPick = null)
    {
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        if (dotColor is not null)
            inner.Children.Add(new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(ColorFromHex(dotColor)),
                VerticalAlignment = VerticalAlignment.Center
            });
        inner.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            Foreground = active ? StrongBrush() : new SolidColorBrush(ChipText),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 188
        });
        if (suffix is not null)
            inner.Children.Add(new TextBlock { Text = $"({suffix})", FontSize = 11, Foreground = MutedBrush(), VerticalAlignment = VerticalAlignment.Center });
        if (onRemove is not null)
        {
            // 22px hit target, but a small quiet glyph (Codex: the x must not dominate a tiny chip).
            var glyph = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = "", FontSize = 10, Foreground = new SolidColorBrush(ChipXIdle) };
            var x = new Button
            {
                Width = 22,
                Height = 22,
                MinWidth = 22,
                MinHeight = 22,
                Padding = new Thickness(0),
                Margin = new Thickness(2, 0, -3, 0),
                CornerRadius = new CornerRadius(11),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Content = glyph
            };
            x.PointerEntered += (_, _) => { glyph.Foreground = StrongBrush(); x.Background = LineBrush(); };
            x.PointerExited += (_, _) => { glyph.Foreground = new SolidColorBrush(ChipXIdle); x.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)); };
            ToolTipService.SetToolTip(x, "Remove");
            x.Click += (_, _) => onRemove();
            inner.Children.Add(x);
        }

        var neutral = RaisedBrush();
        var activeBg = AccentVerySoftBrush();
        var activeBorder = new SolidColorBrush(Windows.UI.Color.FromArgb(180, _accentColor.R, _accentColor.G, _accentColor.B));
        var chip = new Border
        {
            Background = active ? activeBg : neutral,
            BorderBrush = active ? activeBorder : LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = onRemove is null ? new Thickness(9, 4, 9, 4) : new Thickness(9, 4, 4, 4),
            MinHeight = 27,
            VerticalAlignment = VerticalAlignment.Center,
            Child = inner
        };
        if (onTap is not null)
        {
            chip.Tapped += (_, e) => { e.Handled = true; onTap(); };
            chip.PointerEntered += (_, _) => chip.Background = active ? AccentSoftBrush() : ChipNeutralHover();
            chip.PointerExited += (_, _) => chip.Background = active ? activeBg : neutral;
        }
        if (onColorPick is not null)
            chip.RightTapped += (s, e) => { e.Handled = true; onColorPick((FrameworkElement)s); };
        return chip;
    }

    // A compact strip of a chat's tag colors for a list row (dots only, capped). Null when no tags,
    // so callers can skip adding it. Hover/tooltip names them.
    private FrameworkElement? TagDots(ArchiveSession session)
    {
        var tags = ArchiveService.UserTags(session);
        if (tags.Count == 0) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tag in tags.Take(6))
            row.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ColorFromHex(_archive.TagColor(tag))), VerticalAlignment = VerticalAlignment.Center });
        ToolTipService.SetToolTip(row, string.Join(", ", tags));
        return row;
    }

    // Create a new collection (prompt for a name) and add this chat to it - used from any chat's menu.
    private async Task AddSessionToNewCollectionAsync(ArchiveSession session)
    {
        var input = new TextBox { PlaceholderText = "e.g. Renderer work, Job search", MinWidth = 360, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog
        {
            Title = "Add to new collection",
            Content = input,
            PrimaryButtonText = "Create & add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            var name = input.Text.Trim();
            await _archive.AddToCollectionAsync(session, name);
            SyncStatus.Text = $"Added to new collection \"{name}\".";
            RenderCurrent();
        }
    }

    // Right-click a tag chip -> a curated swatch menu to recolor it (or reset to auto). Per the
    // independent design pass: manual colors come from a fixed palette, not a freeform picker.
    private void ShowTagColorFlyout(FrameworkElement anchor, string tag, Action after)
    {
        var current = _archive.TagColor(tag);
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(2) };
        panel.Children.Add(new TextBlock { Text = $"Color · {tag}", Foreground = StrongBrush(), FontSize = 13, FontWeight = FontWeights.SemiBold });

        var grid = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var hex in ArchiveService.TagPalette)
        {
            var selected = string.Equals(hex, current, StringComparison.OrdinalIgnoreCase);
            var sw = new Button
            {
                Width = 26,
                Height = 26,
                MinWidth = 26,
                MinHeight = 26,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(ColorFromHex(hex)),
                BorderBrush = StrongBrush(),
                BorderThickness = new Thickness(selected ? 2 : 0)
            };
            var h = hex;
            sw.Click += async (_, _) => { flyout.Hide(); await _archive.SetTagColorAsync(tag, h); after(); };
            grid.Children.Add(sw);
        }
        panel.Children.Add(grid);

        var auto = new Button { Style = (Style)Resources["PillButtonStyle"], Content = new TextBlock { Text = "Reset to auto", FontSize = 12 }, HorizontalAlignment = HorizontalAlignment.Stretch };
        auto.Click += async (_, _) => { flyout.Hide(); await _archive.SetTagColorAsync(tag, null); after(); };
        panel.Children.Add(auto);

        // Layer rule: where this tag's chats sit in a collection. Lower = higher; presets cover the
        // common cases (active -> Top, context -> Bottom), with a number for fine control.
        panel.Children.Add(new Border { Height = 1, Background = LineBrush() });
        panel.Children.Add(new TextBlock { Text = "List layer (lower = higher)", Foreground = new SolidColorBrush(ChipText), FontSize = 12 });
        var layerBox = new NumberBox { Value = _archive.TagLayer(tag), Minimum = 1, Maximum = 999, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, SmallChange = 1, MinWidth = 130, HorizontalAlignment = HorizontalAlignment.Left };
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        void Preset(string label, int value)
        {
            var b = new Button { Style = (Style)Resources["PillButtonStyle"], Padding = new Thickness(10, 2, 10, 2), MinHeight = 0, Content = new TextBlock { Text = label, FontSize = 11 } };
            b.Click += async (_, _) => { layerBox.Value = value; await _archive.SetTagLayerAsync(tag, value == ArchiveService.DefaultLayer ? (int?)null : value); after(); };
            presets.Children.Add(b);
        }
        Preset("Top", 1);
        Preset("Normal", ArchiveService.DefaultLayer);
        Preset("Bottom", 900);
        layerBox.ValueChanged += async (_, args) =>
        {
            if (double.IsNaN(args.NewValue)) return;
            var v = (int)args.NewValue;
            await _archive.SetTagLayerAsync(tag, v == ArchiveService.DefaultLayer ? (int?)null : v);
            after();
        };
        panel.Children.Add(presets);
        panel.Children.Add(layerBox);

        flyout.Content = panel;
        flyout.ShowAt(anchor);
    }

    // An outlined "add" chip matching the tag chips (lowercase, transparent, hairline border).
    private FrameworkElement AddChip(string text, Action onClick)
    {
        var b = new Button
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(11, 4, 11, 4),
            MinHeight = 28,
            MinWidth = 0,
            Content = new TextBlock { Text = text, FontSize = 12, Foreground = MutedBrush() }
        };
        b.Click += (_, _) => onClick();
        return b;
    }

    // Greedy wrap: pack chips into rows by measured width so they flow within a narrow panel.
    private StackPanel WrapChips(IEnumerable<FrameworkElement> chips, double maxWidth, double spacing = 6)
    {
        var outer = new StackPanel { Spacing = spacing };
        StackPanel? row = null;
        double used = 0;
        foreach (var chip in chips)
        {
            chip.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = chip.DesiredSize.Width;
            if (row is null || (used > 0 && used + w > maxWidth))
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing };
                outer.Children.Add(row);
                used = 0;
            }
            row.Children.Add(chip);
            used += w + spacing;
        }
        return outer;
    }

    private Flyout? _filterFlyout;

    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        _filterFlyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        _filterFlyout.Content = BuildFilterFlyout();
        _filterFlyout.ShowAt(TagFilterButton);
    }

    // A tri-state filter menu: each tag is none / include (✓ accent) / exclude (⊘). Rebuilt in place
    // after every toggle so the row states + the result stay live. "Match all/any" governs includes.
    private FrameworkElement BuildFilterFlyout()
    {
        var all = _archive.AllChatTags();
        var root = new StackPanel { Spacing = 10, Padding = new Thickness(2), MinWidth = 268 };
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        header.Children.Add(new TextBlock { Text = "Filter by tags", Foreground = StrongBrush(), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (_includeTags.Count > 1)
        {
            var mode = new Button
            {
                Style = (Style)Resources["PillButtonStyle"],
                Padding = new Thickness(10, 2, 10, 2),
                MinHeight = 0,
                Content = new TextBlock { Text = _matchAllIncludes ? "Match: all" : "Match: any", FontSize = 11 }
            };
            mode.Click += (_, _) => { _matchAllIncludes = !_matchAllIncludes; RefreshFilterFlyout(); ApplyFilters(); };
            Grid.SetColumn(mode, 1);
            header.Children.Add(mode);
        }
        root.Children.Add(header);

        if (all.Count == 0)
        {
            root.Children.Add(new TextBlock { Text = "No tags yet - add tags from a chat's Tags panel.", Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });
            return root;
        }

        var list = new StackPanel { Spacing = 4 };
        foreach (var tc in all) list.Children.Add(TriStateTagRow(tc, _includeTags, _excludeTags, () => { RefreshFilterFlyout(); ApplyFilters(); }));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

        // SORT + DATE-RANGE + AGENT all combine (e.g. "Claude chats created today, sorted by last message").
        root.Children.Add(new Border { Height = 1, Background = LineBrush(), Margin = new Thickness(0, 2, 0, 2) });
        var dateRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        dateRow.Children.Add(new TextBlock { Text = "Sort", Foreground = new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var dateBtn = new DropDownButton { Content = new TextBlock { Text = DateModeLabel(), FontSize = 12 }, MinHeight = 30 };
        var dateFlyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        foreach (var (value, label) in DateModes)
        {
            var v = value;
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { _dateMode = v; RefreshFilterFlyout(); ApplyFilters(); };
            dateFlyout.Items.Add(item);
        }
        dateBtn.Flyout = dateFlyout;
        Grid.SetColumn(dateBtn, 1);
        dateRow.Children.Add(dateBtn);
        root.Children.Add(dateRow);

        // Date-range filter — combines with the sort above.
        var rangeRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        rangeRow.Children.Add(new TextBlock { Text = "Date", Foreground = new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var rangeBtn = new DropDownButton { Content = new TextBlock { Text = DateRangeLabel(), FontSize = 12 }, MinHeight = 30 };
        var rangeFlyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        foreach (var (value, label) in DateRanges)
        {
            var v = value;
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { _dateRange = v; RefreshFilterFlyout(); ApplyFilters(); };
            rangeFlyout.Items.Add(item);
        }
        rangeBtn.Flyout = rangeFlyout;
        Grid.SetColumn(rangeBtn, 1);
        rangeRow.Children.Add(rangeBtn);
        root.Children.Add(rangeRow);

        // Agent (tool) scope: show only Codex or only Claude chats.
        var toolRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        toolRow.Children.Add(new TextBlock { Text = "Agent", Foreground = new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var toolBtn = new DropDownButton { Content = new TextBlock { Text = ToolFilterLabel(), FontSize = 12 }, MinHeight = 30 };
        var toolFlyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        foreach (var (value, label) in ToolFilters)
        {
            var v = value;
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { _toolFilter = v; RefreshFilterFlyout(); ApplyFilters(); };
            toolFlyout.Items.Add(item);
        }
        toolBtn.Flyout = toolFlyout;
        Grid.SetColumn(toolBtn, 1);
        toolRow.Children.Add(toolBtn);
        root.Children.Add(toolRow);

        // Min user-messages: hide one-off / low-substance chats (e.g. 5+ = "real working sessions only").
        var umRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        umRow.Children.Add(new TextBlock { Text = "Your messages", Foreground = new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        var umBtn = new DropDownButton { Content = new TextBlock { Text = MinUserMsgLabel(), FontSize = 12 }, MinHeight = 30 };
        var umFlyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
        foreach (var (value, label) in MinUserMsgOptions)
        {
            var v = value;
            var item = new MenuFlyoutItem { Text = label };
            item.Click += (_, _) => { _minUserMsgs = v; RefreshFilterFlyout(); ApplyFilters(); };
            umFlyout.Items.Add(item);
        }
        umBtn.Flyout = umFlyout;
        Grid.SetColumn(umBtn, 1);
        umRow.Children.Add(umBtn);
        root.Children.Add(umRow);

        // Show-hidden toggle: reveal the auto-hidden one-off / spam chats (single prompt, tiny transcript).
        // Off by default so those hundreds of spawned judge/probe sessions never clutter the list or search.
        var hiddenN = _archive.HiddenChatCount();
        var shRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var shLabel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        shLabel.Children.Add(new TextBlock { Text = "Show hidden chats", Foreground = new SolidColorBrush(ChipText), FontSize = 12 });
        shLabel.Children.Add(new TextBlock { Text = $"{hiddenN} one-off chat{(hiddenN == 1 ? "" : "s")} auto-hidden", Foreground = MutedBrush(), FontSize = 11 });
        shRow.Children.Add(shLabel);
        var shToggle = new ToggleSwitch { IsOn = _showHidden, OnContent = "On", OffContent = "Off", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        shToggle.Toggled += (_, _) => { if (_showHidden != shToggle.IsOn) { _showHidden = shToggle.IsOn; RefreshFilterFlyout(); ApplyFilters(); } };
        Grid.SetColumn(shToggle, 1);
        shRow.Children.Add(shToggle);
        root.Children.Add(shRow);

        // Automation toggle: reveal tandem/orchestration worker sessions, while apex/controller chats
        // remain visible by default because the core classifier explicitly retains them.
        var automationN = _archive.AutomationWorkerCount();
        var awRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        var awLabel = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        awLabel.Children.Add(new TextBlock { Text = "Show automation workers", Foreground = new SolidColorBrush(ChipText), FontSize = 12 });
        awLabel.Children.Add(new TextBlock { Text = $"{automationN} tandem/orchestration worker{(automationN == 1 ? "" : "s")} auto-hidden", Foreground = MutedBrush(), FontSize = 11 });
        awRow.Children.Add(awLabel);
        var awToggle = new ToggleSwitch { IsOn = _showAutomationWorkers, OnContent = "On", OffContent = "Off", MinWidth = 0, HorizontalAlignment = HorizontalAlignment.Right };
        awToggle.Toggled += (_, _) => { if (_showAutomationWorkers != awToggle.IsOn) { _showAutomationWorkers = awToggle.IsOn; RefreshFilterFlyout(); ApplyFilters(); } };
        Grid.SetColumn(awToggle, 1);
        awRow.Children.Add(awToggle);
        root.Children.Add(awRow);

        // Project (collection) scope: restrict the whole filter to one project's chats.
        if (_archive.Store.Collections.Count > 0)
        {
            root.Children.Add(new Border { Height = 1, Background = LineBrush(), Margin = new Thickness(0, 2, 0, 2) });
            var projRow = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            projRow.Children.Add(new TextBlock { Text = "Project", Foreground = new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            var projBtn = new DropDownButton { Content = new TextBlock { Text = FilterCollectionName() ?? "Any", FontSize = 12 }, MinHeight = 30 };
            var projFlyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
            var anyItem = new MenuFlyoutItem { Text = "Any project" };
            anyItem.Click += (_, _) => { _filterCollectionId = null; RefreshFilterFlyout(); ApplyFilters(); };
            projFlyout.Items.Add(anyItem);
            projFlyout.Items.Add(new MenuFlyoutSeparator());
            foreach (var col in _archive.Store.Collections.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var id = col.Id;
                var item = new MenuFlyoutItem { Text = col.Name };
                item.Click += (_, _) => { _filterCollectionId = id; RefreshFilterFlyout(); ApplyFilters(); };
                projFlyout.Items.Add(item);
            }
            projBtn.Flyout = projFlyout;
            Grid.SetColumn(projBtn, 1);
            projRow.Children.Add(projBtn);
            root.Children.Add(projRow);
        }

        if (_includeTags.Count > 0 || _excludeTags.Count > 0 || _filterCollectionId is not null || _dateMode.Length > 0 || _dateRange.Length > 0 || _toolFilter.Length > 0 || _minUserMsgs > 0 || _showHidden || _showAutomationWorkers)
        {
            var clear = new Button { Style = (Style)Resources["PillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = new TextBlock { Text = "Clear filters", FontSize = 12 } };
            clear.Click += (_, _) => { _includeTags.Clear(); _excludeTags.Clear(); _filterCollectionId = null; _dateMode = ""; _dateRange = ""; _toolFilter = ""; _minUserMsgs = 0; _showHidden = false; _showAutomationWorkers = false; RefreshFilterFlyout(); ApplyFilters(); };
            root.Children.Add(clear);
        }
        return root;
    }

    private Button FilterToggle(string tip, string glyph, bool on, Func<Brush> onBrush, Action click)
    {
        var b = new Button
        {
            Width = 28,
            Height = 26,
            MinWidth = 28,
            MinHeight = 26,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(6),
            Background = on ? AccentVerySoftBrush() : new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = on ? onBrush() : LineBrush(),
            BorderThickness = new Thickness(1),
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = glyph, FontSize = 12, Foreground = on ? onBrush() : MutedBrush() }
        };
        ToolTipService.SetToolTip(b, tip);
        b.Click += (_, _) => click();
        return b;
    }

    // Generic tri-state filter row used by both the chat funnel and the collections funnel: dot +
    // name + count, then include/exclude toggles operating on the GIVEN sets (none/include/exclude
    // are mutually exclusive). onChange rebuilds the flyout + applies.
    private FrameworkElement TriStateTagRow(TagCount tc, HashSet<string> include, HashSet<string> exclude, Action onChange)
    {
        var tag = tc.Tag;
        var inc = include.Contains(tag);
        var exc = exclude.Contains(tag);

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Padding = new Thickness(2, 1, 2, 1) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ColorFromHex(_archive.TagColor(tag))), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = tag, Foreground = inc ? StrongBrush() : exc ? MutedBrush() : new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextDecorations = exc ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None });
        left.Children.Add(new TextBlock { Text = $"({tc.Count})", Foreground = MutedBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(left);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        toggles.Children.Add(FilterToggle("Include", "", inc, () => new SolidColorBrush(_accentColor), () => { if (!include.Remove(tag)) { exclude.Remove(tag); include.Add(tag); } onChange(); }));
        toggles.Children.Add(FilterToggle("Exclude", "", exc, () => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0x71, 0x71)), () => { if (!exclude.Remove(tag)) { include.Remove(tag); exclude.Add(tag); } onChange(); }));
        Grid.SetColumn(toggles, 1);
        grid.Children.Add(toggles);
        return grid;
    }

    private void RefreshFilterFlyout()
    {
        if (_filterFlyout is not null) _filterFlyout.Content = BuildFilterFlyout();
    }

    // The strip of active-filter pills under the search box: include pills (accent dot) + exclude
    // pills (struck "not tag"). Clicking a pill removes that filter. Hidden when nothing is filtered.
    private void RenderTagFilterBar()
    {
        var known = new HashSet<string>(_archive.AllChatTags().Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
        _includeTags.RemoveWhere(t => !known.Contains(t));
        _excludeTags.RemoveWhere(t => !known.Contains(t));

        if (_filterCollectionId is not null && !_archive.Store.Collections.ContainsKey(_filterCollectionId))
            _filterCollectionId = null;   // collection was deleted

        TagFilterBar.Children.Clear();
        if (_includeTags.Count == 0 && _excludeTags.Count == 0 && _filterCollectionId is null && _dateMode.Length == 0 && _dateRange.Length == 0 && _toolFilter.Length == 0 && _minUserMsgs == 0 && !_showHidden && !_showAutomationWorkers)
        {
            TagFilterScroller.Visibility = Visibility.Collapsed;
            return;
        }
        TagFilterScroller.Visibility = Visibility.Visible;

        if (_showHidden)
            TagFilterBar.Children.Add(TagChip("showing hidden", active: true,
                onTap: () => { _showHidden = false; ApplyFilters(); },
                onRemove: () => { _showHidden = false; ApplyFilters(); }));

        if (_showAutomationWorkers)
            TagFilterBar.Children.Add(TagChip("showing automation", active: true,
                onTap: () => { _showAutomationWorkers = false; ApplyFilters(); },
                onRemove: () => { _showAutomationWorkers = false; ApplyFilters(); }));

        if (_toolFilter.Length > 0)
            TagFilterBar.Children.Add(TagChip(ToolFilterLabel(), active: true,
                onTap: () => { _toolFilter = ""; ApplyFilters(); },
                onRemove: () => { _toolFilter = ""; ApplyFilters(); }));

        if (_dateMode.Length > 0)
            TagFilterBar.Children.Add(TagChip(DateModeLabel().Replace(" (default)", ""), active: true,
                onTap: () => { _dateMode = ""; ApplyFilters(); },
                onRemove: () => { _dateMode = ""; ApplyFilters(); }));

        if (_dateRange.Length > 0)
            TagFilterBar.Children.Add(TagChip(DateRangeLabel(), active: true,
                onTap: () => { _dateRange = ""; ApplyFilters(); },
                onRemove: () => { _dateRange = ""; ApplyFilters(); }));

        if (_minUserMsgs > 0)
            TagFilterBar.Children.Add(TagChip("≥ " + _minUserMsgs + " msgs", active: true,
                onTap: () => { _minUserMsgs = 0; ApplyFilters(); },
                onRemove: () => { _minUserMsgs = 0; ApplyFilters(); }));

        if (FilterCollectionName() is { } projName)
            TagFilterBar.Children.Add(TagChip("in: " + projName, active: true,
                onTap: () => { _filterCollectionId = null; ApplyFilters(); },
                onRemove: () => { _filterCollectionId = null; ApplyFilters(); }));

        if (_includeTags.Count > 1)
            TagFilterBar.Children.Add(AddChip(_matchAllIncludes ? "all of" : "any of", () => { _matchAllIncludes = !_matchAllIncludes; ApplyFilters(); }));

        foreach (var tag in _includeTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var t = tag;
            TagFilterBar.Children.Add(TagChip(t, active: true,
                onTap: () => { _includeTags.Remove(t); ApplyFilters(); },
                onRemove: () => { _includeTags.Remove(t); ApplyFilters(); },
                dotColor: _archive.TagColor(t)));
        }
        foreach (var tag in _excludeTags.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var t = tag;
            TagFilterBar.Children.Add(TagChip("not " + t, active: false,
                onTap: () => { _excludeTags.Remove(t); ApplyFilters(); },
                onRemove: () => { _excludeTags.Remove(t); ApplyFilters(); }));
        }
        TagFilterBar.Children.Add(AddChip("clear", () => { _includeTags.Clear(); _excludeTags.Clear(); _filterCollectionId = null; _dateMode = ""; _dateRange = ""; _toolFilter = ""; _minUserMsgs = 0; _showHidden = false; _showAutomationWorkers = false; ApplyFilters(); }));
    }

    // ---- Per-chat tag editor (right panel) ---------------------------------------------------
    private void RenderTags()
    {
        TagsItems.Children.Clear();
        if (AddTagButton is not null) AddTagButton.IsEnabled = _selected is not null;
        if (_selected is null) return;

        var tags = ArchiveService.UserTags(_selected);
        if (tags.Count == 0)
        {
            TagsItems.Children.Add(new TextBlock { Text = "No tags yet", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }
        var sel = _selected;
        var chips = tags.Select(tag => TagChip(tag, active: false,
            onTap: () => { _includeTags.Clear(); _excludeTags.Clear(); _includeTags.Add(tag); Navigate("Archive"); ApplyFilters(); },
            onRemove: async () => { await _archive.RemoveChatTagAsync(sel, tag); RenderTags(); RenderTagFilterBar(); },
            dotColor: _archive.TagColor(tag),
            onColorPick: anchor => ShowTagColorFlyout(anchor, tag, () => { RenderTags(); RenderTagFilterBar(); })));
        TagsItems.Children.Add(WrapChips(chips, 244));
    }

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) await ShowAddTagDialogAsync(_selected);
    }

    // ---- Collections screen: compound filter (project tags + chat tags) ----------------------
    // Project tags decide WHICH collections show; chat tags filter the chats WITHIN each. Both are
    // tri-state include/exclude, so "active + graphics, not web-dev" works at the project level too.
    private readonly HashSet<string> _collInclude = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collExclude = new(StringComparer.OrdinalIgnoreCase);
    private bool _collMatchAll;
    private readonly HashSet<string> _collChatInclude = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _collChatExclude = new(StringComparer.OrdinalIgnoreCase);
    private Flyout? _collFilterFlyout;

    // Collections that pass the project-tag filter (compound).
    public IReadOnlyList<ArchiveCollection> FilteredCollections() =>
        _archive.FilterCollections(_collInclude.ToList(), _collExclude.ToList(), _collMatchAll);

    // A collection's chats after the chat-tag filter, ordered with demoted ("background") chats last.
    public IReadOnlyList<ArchiveSession> CollectionChatsFiltered(ArchiveCollection col)
    {
        var sessions = col.SessionIds
            .Select(id => _archive.Store.Sessions.TryGetValue(id, out var s) ? s : null)
            .OfType<ArchiveSession>()
            .Where(s => _collChatInclude.Count == 0 || _collChatInclude.Any(t => s.Tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))))
            .Where(s => _collChatExclude.Count == 0 || !_collChatExclude.Any(t => s.Tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase))));
        return _archive.OrderCollectionChats(col, sessions);
    }

    private bool AnyCollectionFilterActive() =>
        _collInclude.Count > 0 || _collExclude.Count > 0 || _collChatInclude.Count > 0 || _collChatExclude.Count > 0;

    // ---- Decks (Task-View-style picker) ------------------------------------------------------
    // A horizontal row of deck cards; click to switch the active deck, right-click to rename/delete,
    // plus a "+ New deck" card. Decks are top-level groupings of collections (virtual desktops).
    private UIElement DeckPickerBar()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
        foreach (var deck in _archive.Decks)
        {
            var d = deck;
            var active = string.Equals(d.Id, _archive.ActiveDeckId, StringComparison.OrdinalIgnoreCase);
            var count = _archive.CollectionCountInDeck(d.Id);
            var card = new Button
            {
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16, 9, 16, 9),
                MinWidth = 120,
                Background = active ? AccentVerySoftBrush() : PanelBrush(),
                BorderBrush = active ? new SolidColorBrush(_accentColor) : LineBrush(),
                BorderThickness = new Thickness(active ? 2 : 1),
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock { Text = d.Name, Foreground = StrongBrush(), FontSize = 14, FontWeight = FontWeights.SemiBold },
                        new TextBlock { Text = $"{count} project{(count == 1 ? "" : "s")}", Foreground = MutedBrush(), FontSize = 11 }
                    }
                }
            };
            card.Click += async (_, _) => { await _archive.SetActiveDeckAsync(d.Id); RenderCollections(); };

            var menu = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
            var rename = new MenuFlyoutItem { Text = "Rename deck" };
            rename.Click += async (_, _) => await RenameDeckByAsync(d.Id, d.Name);
            menu.Items.Add(rename);
            if (!string.Equals(d.Id, CodexLocalRetrieval.Core.Services.ArchiveService.MainDeckId, StringComparison.OrdinalIgnoreCase))
            {
                var del = new MenuFlyoutItem { Text = "Delete deck (projects move to Main)" };
                del.Click += async (_, _) => { await _archive.DeleteDeckAsync(d.Id); RenderCollections(); };
                menu.Items.Add(del);
            }
            card.ContextFlyout = menu;
            row.Children.Add(card);
        }

        var add = new Button
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 9, 16, 9),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderBrush = LineBrush(),
            BorderThickness = new Thickness(1),
            Content = new TextBlock { Text = "+ New deck", Foreground = MutedBrush(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center }
        };
        ToolTipService.SetToolTip(add, "Create a new deck (a separate set of collections)");
        add.Click += async (_, _) => await NewDeckAsync();
        row.Children.Add(add);

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = row,
            Margin = new Thickness(0, 0, 0, 6)
        };
    }

    private async Task NewDeckAsync()
    {
        var input = new TextBox { PlaceholderText = "e.g. Context archives, Agent loop", MinWidth = 360, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog { Title = "New deck", Content = input, PrimaryButtonText = "Create", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            var d = await _archive.CreateDeckAsync(input.Text.Trim());
            await _archive.SetActiveDeckAsync(d.Id);
            RenderCollections();
        }
    }

    private async Task RenameDeckByAsync(string deckId, string current)
    {
        var input = new TextBox { Text = current, MinWidth = 360, CornerRadius = ControlCornerRadius() };
        var dialog = new ContentDialog { Title = "Rename deck", Content = input, PrimaryButtonText = "Save", CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Primary, XamlRoot = XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(input.Text))
        {
            await _archive.RenameDeckAsync(deckId, input.Text.Trim());
            RenderCollections();
        }
    }

    // A collection card's tag row: chips (click cycles the project filter) + × remove + "+ tag".
    private UIElement CollectionTagsRow(IReadOnlyList<string> tags, Action onAddTag, Action<string>? onRemoveTag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tag in tags)
        {
            var t = tag;
            row.Children.Add(TagChip(t, active: _collInclude.Contains(t),
                onTap: () => { if (!_collInclude.Remove(t)) { _collExclude.Remove(t); _collInclude.Add(t); } RenderCollections(); },
                onRemove: onRemoveTag is null ? null : () => onRemoveTag(t),
                dotColor: _archive.TagColor(t),
                onColorPick: anchor => ShowTagColorFlyout(anchor, t, RenderCollections)));
        }
        row.Children.Add(AddChip("+ tag", onAddTag));
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = row
        };
    }

    private void CollectionFilter_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement anchor) return;
        _collFilterFlyout = new Flyout { Placement = FlyoutPlacementMode.Bottom };
        _collFilterFlyout.Content = BuildCollectionFilterFlyout();
        _collFilterFlyout.ShowAt(anchor);
    }

    private void RefreshCollFilterFlyout()
    {
        if (_collFilterFlyout is not null) _collFilterFlyout.Content = BuildCollectionFilterFlyout();
    }

    private FrameworkElement BuildCollectionFilterFlyout()
    {
        var projTags = _archive.AllCollectionTags();
        var chatTags = _archive.AllChatTags();
        var root = new StackPanel { Spacing = 8, Padding = new Thickness(2), MinWidth = 280 };

        void Section(string title, IReadOnlyList<TagCount> tags, HashSet<string> inc, HashSet<string> exc, bool showMatch)
        {
            var head = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
            head.Children.Add(new TextBlock { Text = title, Foreground = StrongBrush(), FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            if (showMatch && inc.Count > 1)
            {
                var mode = new Button { Style = (Style)Resources["PillButtonStyle"], Padding = new Thickness(10, 2, 10, 2), MinHeight = 0, Content = new TextBlock { Text = _collMatchAll ? "Match: all" : "Match: any", FontSize = 11 } };
                mode.Click += (_, _) => { _collMatchAll = !_collMatchAll; RefreshCollFilterFlyout(); RenderCollections(); };
                Grid.SetColumn(mode, 1);
                head.Children.Add(mode);
            }
            root.Children.Add(head);
            if (tags.Count == 0)
            {
                root.Children.Add(new TextBlock { Text = "None yet.", Foreground = MutedBrush(), FontSize = 12 });
                return;
            }
            var list = new StackPanel { Spacing = 4 };
            foreach (var tc in tags) list.Children.Add(TriStateTagRow(tc, inc, exc, () => { RefreshCollFilterFlyout(); RenderCollections(); }));
            root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 200, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        }

        Section("Project tags", projTags, _collInclude, _collExclude, showMatch: true);
        root.Children.Add(new Border { Height = 1, Background = LineBrush(), Margin = new Thickness(0, 2, 0, 2) });
        Section("Chat tags (within projects)", chatTags, _collChatInclude, _collChatExclude, showMatch: false);

        if (AnyCollectionFilterActive())
        {
            var clear = new Button { Style = (Style)Resources["PillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = new TextBlock { Text = "Clear filters", FontSize = 12 } };
            clear.Click += (_, _) => { _collInclude.Clear(); _collExclude.Clear(); _collChatInclude.Clear(); _collChatExclude.Clear(); RefreshCollFilterFlyout(); RenderCollections(); };
            root.Children.Add(clear);
        }
        return root;
    }

    // Active-filter pills for the Collections screen (project includes/excludes + chat includes/excludes).
    private UIElement? CollectionFilterPills()
    {
        var known = new HashSet<string>(_archive.AllCollectionTags().Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
        _collInclude.RemoveWhere(t => !known.Contains(t));
        _collExclude.RemoveWhere(t => !known.Contains(t));
        var chatKnown = new HashSet<string>(_archive.AllChatTags().Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
        _collChatInclude.RemoveWhere(t => !chatKnown.Contains(t));
        _collChatExclude.RemoveWhere(t => !chatKnown.Contains(t));
        if (!AnyCollectionFilterActive()) return null;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tag in _collInclude.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        { var t = tag; row.Children.Add(TagChip(t, active: true, onTap: () => { _collInclude.Remove(t); RenderCollections(); }, onRemove: () => { _collInclude.Remove(t); RenderCollections(); }, dotColor: _archive.TagColor(t))); }
        foreach (var tag in _collExclude.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        { var t = tag; row.Children.Add(TagChip("not " + t, active: false, onTap: () => { _collExclude.Remove(t); RenderCollections(); }, onRemove: () => { _collExclude.Remove(t); RenderCollections(); })); }
        foreach (var tag in _collChatInclude.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        { var t = tag; row.Children.Add(TagChip("chat: " + t, active: true, onTap: () => { _collChatInclude.Remove(t); RenderCollections(); }, onRemove: () => { _collChatInclude.Remove(t); RenderCollections(); }, dotColor: _archive.TagColor(t))); }
        foreach (var tag in _collChatExclude.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        { var t = tag; row.Children.Add(TagChip("chat: not " + t, active: false, onTap: () => { _collChatExclude.Remove(t); RenderCollections(); }, onRemove: () => { _collChatExclude.Remove(t); RenderCollections(); })); }
        row.Children.Add(AddChip("clear", () => { _collInclude.Clear(); _collExclude.Clear(); _collChatInclude.Clear(); _collChatExclude.Clear(); RenderCollections(); }));
        return new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = row, Margin = new Thickness(0, 10, 0, 0) };
    }

    private async Task RemoveCollectionTagAndRefreshAsync(string collectionId, string tag)
    {
        await _archive.RemoveCollectionTagAsync(collectionId, tag);
        RenderCollections();
    }

    private async Task ReorderInCollectionAndRefreshAsync(string collectionId, string sessionId, int delta, bool toEnd)
    {
        await _archive.ReorderInCollectionAsync(collectionId, sessionId, delta, toEnd);
        RenderCollections();
    }

    private async Task MoveCollectionToDeckAndRefreshAsync(string collectionId, string deckId)
    {
        await _archive.MoveCollectionToDeckAsync(collectionId, deckId);
        SyncStatus.Text = "Moved project to another deck.";
        RenderCollections();
    }

    private async Task ShowAddCollectionTagDialogAsync(string collectionId, string collectionName)
    {
        var existing = _archive.AllCollectionTags().Select(t => t.Tag).ToList();
        var box = new AutoSuggestBox
        {
            PlaceholderText = "e.g. graphics, active, archived",
            MinWidth = 360,
            ItemsSource = existing,
            QueryIcon = new SymbolIcon(Symbol.Tag)
        };
        box.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                box.ItemsSource = existing.Where(t => t.Contains(box.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        var dialog = new ContentDialog
        {
            Title = $"Tag collection \"{Trim(collectionName, 40)}\"",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            await _archive.AddCollectionTagAsync(collectionId, box.Text);
            RenderCollections();
        }
    }

    private async Task ShowAddTagDialogAsync(ArchiveSession session)
    {
        var existing = _archive.AllChatTags().Select(t => t.Tag)
            .Where(t => !session.Tags.Any(x => string.Equals(x, t, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        var box = new AutoSuggestBox
        {
            PlaceholderText = "e.g. bug, idea, urgent",
            MinWidth = 360,
            ItemsSource = existing,
            QueryIcon = new SymbolIcon(Symbol.Tag)
        };
        box.TextChanged += (_, args) =>
        {
            if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
                box.ItemsSource = existing.Where(t => t.Contains(box.Text, StringComparison.OrdinalIgnoreCase)).ToList();
        };
        var dialog = new ContentDialog
        {
            Title = $"Add tag to \"{Trim(session.DisplayTitle, 40)}\"",
            Content = box,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary && !string.IsNullOrWhiteSpace(box.Text))
        {
            var added = await _archive.AddChatTagAsync(session, box.Text);
            RenderTags();
            RenderTagFilterBar();
            SyncStatus.Text = added ? $"Tagged \"{box.Text.Trim()}\"." : "Tag already present or reserved.";
        }
    }
}
