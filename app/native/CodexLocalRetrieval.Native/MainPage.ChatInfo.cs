using CodexLocalRetrieval.Core.Models;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    // One dialog to VIEW and EDIT everything about a chat. The app name and its special phrases
    // (codenames) are editable; native name / tool / folder / collections / id are shown as facts.
    // Saving routes through the real store paths (RenameSessionAsync + SetSpecialPhrasesAsync) so the
    // change persists and every view — including the Phrases screen — reflects it immediately.
    private async Task ChatInfoDialogAsync(ArchiveSession session)
    {
        if (session is null) return;

        var panel = new StackPanel { Spacing = 12, MinWidth = 460 };

        // -- App display name (editable) --
        panel.Children.Add(FieldLabel("App name"));
        var nameBox = new TextBox { Text = session.DisplayTitle ?? "", CornerRadius = ControlCornerRadius() };
        ToolTipService.SetToolTip(nameBox, "The name shown in this app only (does not change the tool's own name).");
        panel.Children.Add(nameBox);

        // -- Special phrases / codenames (editable: add + remove) --
        panel.Children.Add(FieldLabel("Special phrases (codenames)"));
        var phrases = session.SpecialPhrases.ToList();
        var chips = new StackPanel { Spacing = 6 };

        void RenderChips()
        {
            chips.Children.Clear();
            if (phrases.Count == 0)
            {
                chips.Children.Add(new TextBlock { Text = "None yet.", Foreground = MutedBrush(), FontSize = 12 });
                return;
            }
            foreach (var phrase in phrases.ToList())
            {
                var row = new Grid
                {
                    ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } },
                    ColumnSpacing = 8
                };
                var pill = new Border
                {
                    Background = AccentVerySoftBrush(),
                    CornerRadius = new CornerRadius(6),
                    Padding = new Thickness(8, 3, 8, 3),
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Center,
                    Child = new TextBlock { Text = phrase, Foreground = StrongBrush(), FontSize = 12, FontWeight = FontWeights.SemiBold }
                };
                row.Children.Add(pill);

                var remove = new Button { Content = "Remove", Style = (Style)Resources["PillButtonStyle"], MinHeight = 30 };
                var captured = phrase;
                remove.Click += (_, _) =>
                {
                    phrases.RemoveAll(x => string.Equals(x, captured, StringComparison.OrdinalIgnoreCase));
                    RenderChips();
                };
                Grid.SetColumn(remove, 1);
                row.Children.Add(remove);
                chips.Children.Add(row);
            }
        }
        RenderChips();
        panel.Children.Add(chips);

        var addBox = new TextBox { PlaceholderText = "Add a phrase…", CornerRadius = ControlCornerRadius() };
        var addBtn = new Button { Content = "Add", Style = (Style)Resources["PillButtonStyle"], MinHeight = 32 };
        void DoAdd()
        {
            var value = (addBox.Text ?? "").Trim();
            if (value.Length == 0) return;
            if (!phrases.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))) phrases.Add(value);
            addBox.Text = "";
            RenderChips();
        }
        addBtn.Click += (_, _) => DoAdd();
        addBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { e.Handled = true; DoAdd(); } };
        var addRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }
        };
        addRow.Children.Add(addBox);
        Grid.SetColumn(addBtn, 1);
        addRow.Children.Add(addBtn);
        panel.Children.Add(addRow);

        // -- Read-only facts --
        panel.Children.Add(new Border { Height = 1, Background = LineBrush(), Margin = new Thickness(0, 4, 0, 4) });
        panel.Children.Add(InfoLine("Native name", string.IsNullOrWhiteSpace(session.Title) ? "(none yet)" : session.Title));
        panel.Children.Add(InfoLine("Tool", string.IsNullOrWhiteSpace(session.Tool) ? "-" : session.Tool));
        panel.Children.Add(InfoLine("Updated", string.IsNullOrWhiteSpace(session.DisplayDate) ? "-" : session.DisplayDate));
        if (!string.IsNullOrWhiteSpace(session.Workspace)) panel.Children.Add(InfoLine("Folder", session.Workspace));
        var cols = SessionCollectionNames(session).ToList();
        panel.Children.Add(InfoLine("Collections", cols.Count == 0 ? "Unfiled" : string.Join(" · ", cols)));
        panel.Children.Add(InfoLine("Id", session.Id));

        var dialog = new ContentDialog
        {
            Title = "Chat info",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 540 },
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var changes = new List<string>();
        var newName = (nameBox.Text ?? "").Trim();
        if (!string.Equals(newName, (session.DisplayTitle ?? "").Trim(), StringComparison.Ordinal))
        {
            await _archive.RenameSessionAsync(session, newName);
            changes.Add("name");
        }
        if (await _archive.SetSpecialPhrasesAsync(session, phrases)) changes.Add("phrases");

        if (changes.Count > 0)
        {
            SyncStatus.Text = $"Updated {string.Join(" + ", changes)} for \"{Trim(session.DisplayTitle, 40)}\".";
            RenderCurrent();
        }
        else
        {
            SyncStatus.Text = "No changes.";
        }
    }

    private TextBlock FieldLabel(string text) => new()
    {
        Text = text,
        Foreground = StrongBrush(),
        FontSize = 12,
        FontWeight = FontWeights.SemiBold
    };

    private UIElement InfoLine(string label, string value)
    {
        var grid = new Grid
        {
            ColumnSpacing = 12,
            ColumnDefinitions = { new ColumnDefinition { Width = new GridLength(120) }, new ColumnDefinition() }
        };
        grid.Children.Add(new TextBlock { Text = label, Foreground = MutedBrush(), FontSize = 12 });
        var value2 = new TextBlock
        {
            Text = value ?? "",
            Foreground = StrongBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true
        };
        Grid.SetColumn(value2, 1);
        grid.Children.Add(value2);
        return grid;
    }
}
