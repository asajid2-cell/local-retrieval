using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace CodexLocalRetrieval_Native;

// Vetting is the door between the general populace and Active: a chat is not promoted until it has a name
// of its own and a phrase. That is what makes the middle tier worth searching, so the dialog is
// deliberately a gate - the primary button stays disabled until both fields are filled. The phrase starts
// as a random [trait][color][fruit][food] combination, which can be rerolled, taken apart word by word,
// or replaced with a phrase saved earlier for this kind of work.
public sealed partial class MainPage
{
    // The dialog is held while it is up so a capture-harness step can dismiss it (a ContentDialog lives in
    // the root's popup layer, which a plain RenderAsync(this) does not include).
    private ContentDialog? _openVetDialog;

    private string VetMenuLabel(ArchiveSession session) =>
        session.Vetted || session.Archived ? "Vetted: change name / phrase..." : "Vet chat...";

    private async Task VetDialogAsync(ArchiveSession session)
    {
        if (session is null) return;

        var panel = new StackPanel { Spacing = 10, MinWidth = 470 };
        panel.Children.Add(new TextBlock
        {
            Text = session.Vetted
                ? "This chat is already vetted. Changing its name or phrase keeps it in Active."
                : "Vetting moves this chat out of the general populace and into Active. Give it a name of its own and a phrase of its own, so it is recognisable when you go looking for it.",
            Foreground = MutedBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap
        });

        // -- name: the chat's OWN name, the one the list and the tool's resume list both show --
        // A chat entering the gate starts EMPTY: vetting is the moment it earns a name of its own, so the
        // gate is a typed name, not a rubber stamp on the auto-derived title. The old title is offered as
        // the placeholder hint so nothing is hidden. An already-vetted chat is being EDITED, not vetted,
        // so there its current name is prefilled.
        panel.Children.Add(FieldLabel("Name"));
        var nameBox = new TextBox
        {
            Name = "VetNameBox",
            Text = session.Vetted ? session.NativeTitle ?? "" : "",
            PlaceholderText = string.IsNullOrWhiteSpace(session.NativeTitle) ? "A short, recognisable name" : session.NativeTitle,
            CornerRadius = ControlCornerRadius()
        };
        ToolTipService.SetToolTip(nameBox,
            "Written to the chat's own name in Claude/Codex, so it shows in the list and in their resume list.");
        panel.Children.Add(nameBox);

        // -- phrase: four words, autofilled, yours to change --
        panel.Children.Add(FieldLabel("Phrase"));
        var phraseBox = new TextBox
        {
            Name = "VetPhraseBox",
            Text = PhraseGenerator.Next(),
            CornerRadius = ControlCornerRadius()
        };
        ToolTipService.SetToolTip(phraseBox, "Four words: a trait, a colour, a fruit and a food. Edit it, or pick each word below.");
        panel.Children.Add(phraseBox);

        var slotRow = new Grid
        {
            ColumnSpacing = 6,
            ColumnDefinitions =
            {
                new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition(), new ColumnDefinition()
            }
        };
        var slotWords = new[]
        {
            PhraseGenerator.Traits, PhraseGenerator.Colors, PhraseGenerator.Fruits, PhraseGenerator.Foods
        };
        var slotLabels = new[] { "trait", "colour", "fruit", "food" };
        var slotBoxes = new ComboBox[4];
        var syncing = false;

        var reroll = new Button { Content = "Reroll", Style = (Style)Resources["PillButtonStyle"], MinHeight = 32 };
        var rerollRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }
        };
        rerollRow.Children.Add(slotRow);
        Grid.SetColumn(reroll, 1);
        rerollRow.Children.Add(reroll);
        panel.Children.Add(rerollRow);

        void SyncSlotsFromBox()
        {
            var parts = PhraseGenerator.Parts(phraseBox.Text);
            for (var i = 0; i < 4; i++)
            {
                var word = i < parts.Length ? parts[i] : "";
                slotBoxes[i].SelectedItem = slotWords[i].FirstOrDefault(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase));
            }
        }

        // Any picked word replaces its slot in the phrase and leaves the other three alone, so the four
        // pickers and the text box are two views of one phrase rather than competing inputs.
        void RebuildFromSlots()
        {
            if (syncing) return;
            syncing = true;
            var current = PhraseGenerator.Parts(phraseBox.Text);
            var words = new string[4];
            for (var i = 0; i < 4; i++)
                words[i] = slotBoxes[i].SelectedItem as string ?? (i < current.Length ? current[i] : "");
            phraseBox.Text = PhraseGenerator.Combine(words[0], words[1], words[2], words[3]);
            SyncSlotsFromBox();
            syncing = false;
        }

        for (var i = 0; i < 4; i++)
        {
            var index = i;
            var box = new ComboBox
            {
                ItemsSource = slotWords[index],
                PlaceholderText = slotLabels[index],
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };
            ToolTipService.SetToolTip(box, $"Pick the phrase's {slotLabels[index]}");
            box.SelectionChanged += (_, _) => RebuildFromSlots();
            slotBoxes[index] = box;
            Grid.SetColumn(box, index);
            slotRow.Children.Add(box);
        }
        SyncSlotsFromBox();
        reroll.Click += (_, _) =>
        {
            syncing = true;
            phraseBox.Text = PhraseGenerator.Next();
            SyncSlotsFromBox();
            syncing = false;
        };

        // -- reuse: a phrase saved earlier for this kind of work ("apples" for mux work) --
        panel.Children.Add(new Border { Height = 1, Background = LineBrush(), Margin = new Thickness(0, 2, 0, 2) });
        panel.Children.Add(FieldLabel("Saved phrases"));
        var savedBox = new ComboBox
        {
            PlaceholderText = "Use a saved phrase...",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 0
        };
        void FillSaved()
        {
            savedBox.Items.Clear();
            foreach (var category in _archive.SavedPhraseCategories())
            {
                var label = string.IsNullOrWhiteSpace(category.Note) ? category.Phrase : $"{category.Phrase} — {category.Note}";
                savedBox.Items.Add(new ComboBoxItem { Content = label, Tag = category.Phrase });
            }
            savedBox.IsEnabled = savedBox.Items.Count > 0;
        }
        savedBox.SelectionChanged += (_, _) =>
        {
            if (savedBox.SelectedItem is not ComboBoxItem item || item.Tag is not string savedPhrase) return;
            syncing = true;
            phraseBox.Text = savedPhrase;
            SyncSlotsFromBox();
            syncing = false;
            savedBox.SelectedIndex = -1;   // ready to pick again without a dead first click
        };
        FillSaved();
        panel.Children.Add(savedBox);

        var noteBox = new TextBox
        {
            PlaceholderText = "Keep this phrase for... (e.g. mux work)",
            CornerRadius = ControlCornerRadius()
        };
        var savePhrase = new Button { Content = "Save phrase", Style = (Style)Resources["PillButtonStyle"], MinHeight = 32 };
        savePhrase.Click += async (_, _) =>
        {
            var saved = await _archive.SavePhraseCategoryAsync(phraseBox.Text, noteBox.Text);
            if (saved is null) return;
            noteBox.Text = "";
            FillSaved();
            SyncStatus.Text = $"Saved phrase \"{saved.Phrase}\" for reuse.";
        };
        var saveRow = new Grid
        {
            ColumnSpacing = 8,
            ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }
        };
        saveRow.Children.Add(noteBox);
        Grid.SetColumn(savePhrase, 1);
        saveRow.Children.Add(savePhrase);
        panel.Children.Add(saveRow);

        var content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 560
        };
        var dialog = new ContentDialog
        {
            Title = session.Vetted ? "Name and phrase" : "Vet this chat",
            Content = content,
            PrimaryButtonText = session.Vetted ? "Save" : "Vet",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            XamlRoot = XamlRoot
        };

        void Validate() => dialog.IsPrimaryButtonEnabled =
            !string.IsNullOrWhiteSpace(nameBox.Text) && !string.IsNullOrWhiteSpace(phraseBox.Text);
        nameBox.TextChanged += (_, _) => Validate();
        phraseBox.TextChanged += (_, _) =>
        {
            if (!syncing)
            {
                syncing = true;
                SyncSlotsFromBox();
                syncing = false;
            }
            Validate();
        };
        Validate();

        _openVetDialog = dialog;
        var result = ContentDialogResult.None;
        try
        {
            result = await dialog.ShowAsync();
        }
        finally
        {
            _openVetDialog = null;
        }
        if (result != ContentDialogResult.Primary) return;

        (bool Vetted, string? RenameStatus) vetted;
        try
        {
            vetted = await _archive.VetSessionAsync(session, nameBox.Text, phraseBox.Text);
        }
        catch (Exception error)
        {
            // The store is shared with the retrieval server, and its generation fence can refuse a save
            // even after the service's own retry. Report it here rather than letting it escape a click
            // handler, where it would take the app down with no explanation.
            Diag.Log("vet failed: " + error.Message);
            SyncStatus.Text = "Could not vet this chat: " + error.Message;
            return;
        }
        if (!vetted.Vetted)
        {
            SyncStatus.Text = vetted.RenameStatus ?? "Chat was not vetted.";
            return;
        }
        var status = vetted.RenameStatus;
        var shown = Trim(nameBox.Text, 40);
        SyncStatus.Text = ArchiveService.NativeRenameSucceeded(status)
            ? $"Vetted \"{shown}\" into Active as {phraseBox.Text.Trim()}."
            : $"Vetted \"{shown}\" into Active as {phraseBox.Text.Trim()}; the tool's own name could not be written, so it is kept as the app name ({status}).";
        UpdateChrome();
        RenderCurrent();
    }
}
