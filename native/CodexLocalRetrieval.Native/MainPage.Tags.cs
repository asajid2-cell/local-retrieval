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

    private ChatFilter CurrentChatFilter() => new()
    {
        Query = SearchBox.Text,
        IncludeTags = _includeTags.ToList(),
        ExcludeTags = _excludeTags.ToList(),
        MatchAllIncludes = _matchAllIncludes,
        CollectionId = _filterCollectionId
    };

    private string? FilterCollectionName() =>
        _filterCollectionId is not null && _archive.Store.Collections.TryGetValue(_filterCollectionId, out var c) ? c.Name : null;

    // ---- Chat list: unified text + compound tag filter ---------------------------------------
    private void ApplyFilters()
    {
        var results = _archive.FilterChats(CurrentChatFilter());
        _archive.RefreshSessions(results);
        SelectFirstSession();
        RenderCurrent();
        RenderTagFilterBar();
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
        foreach (var tc in all) list.Children.Add(FilterTagRow(tc));
        root.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 320, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });

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

        if (_includeTags.Count > 0 || _excludeTags.Count > 0 || _filterCollectionId is not null)
        {
            var clear = new Button { Style = (Style)Resources["PillButtonStyle"], HorizontalAlignment = HorizontalAlignment.Stretch, Content = new TextBlock { Text = "Clear filters", FontSize = 12 } };
            clear.Click += (_, _) => { _includeTags.Clear(); _excludeTags.Clear(); _filterCollectionId = null; RefreshFilterFlyout(); ApplyFilters(); };
            root.Children.Add(clear);
        }
        return root;
    }

    // One filter row: dot + name + count, then include/exclude toggle buttons.
    private FrameworkElement FilterTagRow(TagCount tc)
    {
        var tag = tc.Tag;
        var inc = _includeTags.Contains(tag);
        var exc = _excludeTags.Contains(tag);

        var grid = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }, Padding = new Thickness(2, 1, 2, 1) };
        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(ColorFromHex(_archive.TagColor(tag))), VerticalAlignment = VerticalAlignment.Center });
        left.Children.Add(new TextBlock { Text = tag, Foreground = inc ? StrongBrush() : exc ? MutedBrush() : new SolidColorBrush(ChipText), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextDecorations = exc ? Windows.UI.Text.TextDecorations.Strikethrough : Windows.UI.Text.TextDecorations.None });
        left.Children.Add(new TextBlock { Text = $"({tc.Count})", Foreground = MutedBrush(), FontSize = 11, VerticalAlignment = VerticalAlignment.Center });
        grid.Children.Add(left);

        var toggles = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        toggles.Children.Add(FilterToggle("Include", "", inc, () => new SolidColorBrush(_accentColor), () => { if (!_includeTags.Remove(tag)) { _excludeTags.Remove(tag); _includeTags.Add(tag); } RefreshFilterFlyout(); ApplyFilters(); }));
        toggles.Children.Add(FilterToggle("Exclude", "", exc, () => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xF8, 0x71, 0x71)), () => { if (!_excludeTags.Remove(tag)) { _includeTags.Remove(tag); _excludeTags.Add(tag); } RefreshFilterFlyout(); ApplyFilters(); }));
        Grid.SetColumn(toggles, 1);
        grid.Children.Add(toggles);
        return grid;
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
        if (_includeTags.Count == 0 && _excludeTags.Count == 0 && _filterCollectionId is null)
        {
            TagFilterScroller.Visibility = Visibility.Collapsed;
            return;
        }
        TagFilterScroller.Visibility = Visibility.Visible;

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
        TagFilterBar.Children.Add(AddChip("clear", () => { _includeTags.Clear(); _excludeTags.Clear(); _filterCollectionId = null; ApplyFilters(); }));
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

    // ---- Collection tags (Collections screen) ------------------------------------------------
    private readonly HashSet<string> _activeCollectionTagFilters = new(StringComparer.OrdinalIgnoreCase);

    // A collection card's tag row: unified chips (click to filter, × to remove) + an "+ tag" chip.
    private UIElement CollectionTagsRow(IReadOnlyList<string> tags, Action onAddTag, Action<string>? onRemoveTag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tag in tags)
        {
            var t = tag;
            row.Children.Add(TagChip(t, active: _activeCollectionTagFilters.Contains(t),
                onTap: () => ToggleCollectionFilter(t),
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

    private void ToggleCollectionFilter(string tag)
    {
        if (!_activeCollectionTagFilters.Remove(tag)) _activeCollectionTagFilters.Add(tag);
        RenderCollections();
    }

    // The filter strip shown above the collection list (null when no collection tags exist yet).
    private UIElement? CollectionTagFilterBar()
    {
        var all = _archive.AllCollectionTags();
        _activeCollectionTagFilters.RemoveWhere(t => !all.Any(x => string.Equals(x.Tag, t, StringComparison.OrdinalIgnoreCase)));
        if (all.Count == 0) return null;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(new TextBlock { Text = "Filter", Foreground = MutedBrush(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        foreach (var tc in all)
        {
            var tag = tc.Tag;
            row.Children.Add(TagChip(tag, active: _activeCollectionTagFilters.Contains(tag),
                onTap: () => ToggleCollectionFilter(tag), onRemove: null, suffix: tc.Count.ToString(),
                dotColor: _archive.TagColor(tag)));
        }
        if (_activeCollectionTagFilters.Count > 0)
            row.Children.Add(AddChip("clear", () => { _activeCollectionTagFilters.Clear(); RenderCollections(); }));
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = row,
            Margin = new Thickness(0, 10, 0, 0)
        };
    }

    private async Task RemoveCollectionTagAndRefreshAsync(string collectionId, string tag)
    {
        await _archive.RemoveCollectionTagAsync(collectionId, tag);
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
