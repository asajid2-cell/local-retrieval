using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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

    private void TagFilter_Click(object sender, RoutedEventArgs e)
    {
        var all = _archive.AllChatTags();
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };
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
            TagFilterBar.Children.Add(ActiveFilterChip(tag));

        var clear = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Padding = new Thickness(10, 3, 10, 3),
            Content = new TextBlock { Text = "Clear", FontSize = 12 }
        };
        clear.Click += (_, _) => { _activeTagFilters.Clear(); ApplyFilters(); };
        TagFilterBar.Children.Add(clear);
    }

    // A pill showing an active filter; clicking it removes that filter.
    private Button ActiveFilterChip(string tag)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new TextBlock { Text = tag, FontSize = 12, Foreground = StrongBrush(), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = "×", FontSize = 13, Foreground = MutedBrush(), VerticalAlignment = VerticalAlignment.Center });
        var b = new Button
        {
            Padding = new Thickness(10, 3, 10, 3),
            CornerRadius = new CornerRadius(14),
            Background = AccentVerySoftBrush(),
            BorderBrush = AccentSoftBrush(),
            BorderThickness = new Thickness(1),
            Content = content
        };
        ToolTipService.SetToolTip(b, $"Remove filter: {tag}");
        b.Click += (_, _) => { _activeTagFilters.Remove(tag); ApplyFilters(); };
        return b;
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
            TagsItems.Children.Add(new TextBlock { Text = "No tags yet - use + Tag", Foreground = MutedBrush(), FontSize = 12 });
            return;
        }
        foreach (var tag in tags) TagsItems.Children.Add(EditableTagRow(_selected, tag));
    }

    // One tag row: click the label to filter the chat list by it; × removes it from the chat.
    private Border EditableTagRow(ArchiveSession session, string tag)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new Button
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Content = new TextBlock { Text = "# " + tag, Foreground = StrongBrush(), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis }
        };
        ToolTipService.SetToolTip(label, $"Filter chats tagged \"{tag}\"");
        label.Click += (_, _) =>
        {
            _activeTagFilters.Clear();
            _activeTagFilters.Add(tag);
            Navigate("Archive");
            ApplyFilters();
        };
        grid.Children.Add(label);

        var remove = new Button
        {
            Style = (Style)Resources["IconButtonStyle"],
            Width = 26,
            Height = 26,
            MinWidth = 26,
            MinHeight = 26,
            Content = new TextBlock { Text = "×", FontSize = 14, Foreground = MutedBrush() }
        };
        ToolTipService.SetToolTip(remove, "Remove tag");
        remove.Click += async (_, _) =>
        {
            await _archive.RemoveChatTagAsync(session, tag);
            RenderTags();
            RenderTagFilterBar();
        };
        Grid.SetColumn(remove, 1);
        grid.Children.Add(remove);

        return new Border
        {
            BorderBrush = AccentSoftBrush(),
            BorderThickness = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(8, 1, 2, 1),
            Child = grid
        };
    }

    private async void AddTag_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) await ShowAddTagDialogAsync(_selected);
    }

    // ---- Collection tags (Collections screen) ------------------------------------------------
    private readonly HashSet<string> _activeCollectionTagFilters = new(StringComparer.OrdinalIgnoreCase);

    // A collection card's tag row: chips (click to filter, × to remove) + an "+ tag" button.
    private UIElement CollectionTagsRow(IReadOnlyList<string> tags, Action onAddTag, Action<string>? onRemoveTag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        foreach (var tag in tags) row.Children.Add(CollectionTagChip(tag, onRemoveTag));
        var add = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Padding = new Thickness(8, 1, 8, 1),
            MinHeight = 26,
            Height = 26,
            Content = new TextBlock { Text = tags.Count == 0 ? "+ tag" : "+", FontSize = 11 }
        };
        ToolTipService.SetToolTip(add, "Add a tag to this collection");
        add.Click += (_, _) => onAddTag();
        row.Children.Add(add);
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
            Content = row
        };
    }

    private Border CollectionTagChip(string tag, Action<string>? onRemoveTag)
    {
        var active = _activeCollectionTagFilters.Contains(tag);
        var inner = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        var label = new Button
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            Content = new TextBlock { Text = tag, FontSize = 11, Foreground = StrongBrush() }
        };
        ToolTipService.SetToolTip(label, active ? "Filtering by this tag - click to clear" : $"Show only collections tagged \"{tag}\"");
        label.Click += (_, _) => ToggleCollectionFilter(tag);
        inner.Children.Add(label);
        if (onRemoveTag is not null)
        {
            var x = new Button
            {
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0),
                MinWidth = 0,
                Content = new TextBlock { Text = "×", FontSize = 12, Foreground = MutedBrush() }
            };
            ToolTipService.SetToolTip(x, "Remove tag from collection");
            x.Click += (_, _) => onRemoveTag(tag);
            inner.Children.Add(x);
        }
        return new Border
        {
            Background = active ? AccentSoftBrush() : AccentVerySoftBrush(),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(9, 2, 7, 2),
            Child = inner
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
        row.Children.Add(new TextBlock { Text = "Filter:", Foreground = MutedBrush(), FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0) });
        foreach (var tc in all)
        {
            var tag = tc.Tag;
            var active = _activeCollectionTagFilters.Contains(tag);
            var chip = new Button
            {
                Padding = new Thickness(10, 3, 10, 3),
                CornerRadius = new CornerRadius(13),
                Background = active ? AccentSoftBrush() : AccentVerySoftBrush(),
                Content = new TextBlock { Text = $"{tag} ({tc.Count})", FontSize = 12, Foreground = StrongBrush() }
            };
            chip.Click += (_, _) => ToggleCollectionFilter(tag);
            row.Children.Add(chip);
        }
        if (_activeCollectionTagFilters.Count > 0)
        {
            var clear = new Button { Style = (Style)Resources["PillButtonStyle"], Padding = new Thickness(10, 3, 10, 3), Content = new TextBlock { Text = "Clear", FontSize = 12 } };
            clear.Click += (_, _) => { _activeCollectionTagFilters.Clear(); RenderCollections(); };
            row.Children.Add(clear);
        }
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
