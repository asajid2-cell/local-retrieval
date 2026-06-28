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
    // Active chat-list tag filter (ANY-match). Empty => no tag filter applied.
    private readonly HashSet<string> _activeTagFilters = new(StringComparer.OrdinalIgnoreCase);

    // ---- Chat list: unified text + tag filter ------------------------------------------------
    private void ApplyFilters()
    {
        var results = _archive.Filter(SearchBox.Text, _activeTagFilters.ToList());
        _archive.RefreshSessions(results);
        SelectFirstSession();
        RenderCurrent();
        RenderTagFilterBar();
    }

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

    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        var all = _archive.AllChatTags();
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false, Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        if (all.Count == 0)
        {
            flyout.Items.Add(new MenuFlyoutItem { Text = "No tags yet - add one from a chat's Tags panel", IsEnabled = false });
        }
        else
        {
            foreach (var tc in all)
            {
                var tag = tc.Tag;
                var item = new ToggleMenuFlyoutItem { Text = $"{tag}  ({tc.Count})", IsChecked = _activeTagFilters.Contains(tag) };
                item.Click += (_, _) =>
                {
                    if (!_activeTagFilters.Remove(tag)) _activeTagFilters.Add(tag);
                    ApplyFilters();
                };
                flyout.Items.Add(item);
            }
            if (_activeTagFilters.Count > 0)
            {
                flyout.Items.Add(new MenuFlyoutSeparator());
                var clear = new MenuFlyoutItem { Text = "Clear all filters" };
                clear.Click += (_, _) => { _activeTagFilters.Clear(); ApplyFilters(); };
                flyout.Items.Add(clear);
            }
        }
        flyout.ShowAt(TagFilterButton);
    }

    // The strip of active-filter pills under the search box (hidden when nothing is filtered).
    private void RenderTagFilterBar()
    {
        var known = new HashSet<string>(_archive.AllChatTags().Select(t => t.Tag), StringComparer.OrdinalIgnoreCase);
        _activeTagFilters.RemoveWhere(t => !known.Contains(t));   // drop filters whose tag is gone

        TagFilterBar.Children.Clear();
        if (_activeTagFilters.Count == 0)
        {
            TagFilterScroller.Visibility = Visibility.Collapsed;
            return;
        }
        TagFilterScroller.Visibility = Visibility.Visible;
        foreach (var tag in _activeTagFilters.OrderBy(t => t, StringComparer.OrdinalIgnoreCase))
        {
            var t = tag;
            TagFilterBar.Children.Add(TagChip(t, active: true,
                onTap: () => { _activeTagFilters.Remove(t); ApplyFilters(); },
                onRemove: () => { _activeTagFilters.Remove(t); ApplyFilters(); },
                dotColor: _archive.TagColor(t)));
        }
        TagFilterBar.Children.Add(AddChip("clear", () => { _activeTagFilters.Clear(); ApplyFilters(); }));
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
            onTap: () => { _activeTagFilters.Clear(); _activeTagFilters.Add(tag); Navigate("Archive"); ApplyFilters(); },
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
