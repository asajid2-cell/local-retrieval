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
// as a random [trait][color][fruit][food] combination, which can be rerolled or rebuilt word by word from
// the four slots. There is no second, older place to pick a phrase from - the slots ARE the picker.
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

        var entered = await ShowVetDialogAsync(session, null);
        if (entered is null) return;   // cancelled - nothing changes

        SyncStatus.Text = (await CommitVetAsync(session, entered.Value.Name, entered.Value.Phrase)).Line;
        UpdateChrome();
        RenderCurrent();
    }

    // Vet a multi-selection: the gate is the SAME screen, opened once per chat with the position in the run
    // as its title, so a batch is vetted one decision at a time - each chat still earns its own name and its
    // own phrase, rather than one decision being stamped across all of them. Cancelling stops the run where
    // it stands; the chats already vetted in it keep their names and phrases.
    private async Task VetManyAsync(IReadOnlyList<ArchiveSession> targets)
    {
        if (targets.Count == 0) return;

        var vetted = 0;
        for (var i = 0; i < targets.Count; i++)
        {
            var entered = await ShowVetDialogAsync(targets[i], $"Vet chat {i + 1} of {targets.Count}");
            if (entered is null) break;
            if ((await CommitVetAsync(targets[i], entered.Value.Name, entered.Value.Phrase)).Ok) vetted++;
        }

        SessionList.SelectedItems.Clear();
        UpdateChrome();
        RenderCurrent();
        SyncStatus.Text = vetted == 0
            ? "No chats were vetted."
            : vetted == targets.Count
                ? $"Vetted {vetted} chat{(vetted == 1 ? "" : "s")} into Active."
                : $"Vetted {vetted} of {targets.Count} chats into Active.";
    }

    // The gate itself: the fields, the phrase pickers and the validation, shown once. Returns the name and
    // phrase the user entered, or null when they cancelled, so the caller decides what committing means.
    private async Task<(string Name, string Phrase)?> ShowVetDialogAsync(ArchiveSession session, string? title)
    {
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
        // so there its current name is prefilled — DisplayTitle, the name this chat is actually listed
        // under, rather than the tool's own title, which is stale whenever the native write was deferred.
        panel.Children.Add(FieldLabel("Name"));
        var nameBox = new TextBox
        {
            Name = "VetNameBox",
            Text = session.Vetted ? session.DisplayTitle : "",
            PlaceholderText = string.IsNullOrWhiteSpace(session.NativeTitle) ? "A short, recognisable name" : session.NativeTitle,
            CornerRadius = ControlCornerRadius()
        };
        ToolTipService.SetToolTip(nameBox,
            "Written to the chat's own name in Claude/Codex, so it shows in the list and in their resume list.");
        panel.Children.Add(nameBox);

        // -- phrase: four words, autofilled, yours to change --
        // A chat that is being EDITED keeps the phrase it already has: opening the gate to fix a name must
        // not silently reroll the phrase, which would change the handle the chat is grouped under.
        panel.Children.Add(FieldLabel("Phrase"));
        var phraseBox = new TextBox
        {
            Name = "VetPhraseBox",
            Text = session.SpecialPhrases.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p))?.Trim() ?? PhraseGenerator.Next(),
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

        var content = new ScrollViewer
        {
            Content = panel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 560
        };
        var dialog = new ContentDialog
        {
            Title = title ?? (session.Vetted ? "Name and phrase" : "Vet this chat"),
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
        if (result != ContentDialogResult.Primary) return null;
        return (nameBox.Text, phraseBox.Text);
    }

    // Apply ONE vetting decision, and say what to show for it. Shared by the single vet and the batch so a
    // chat is promoted by the same code either way; a failure (a save the store's fence refuses even after
    // the service's retry) is reported rather than thrown, because both callers are click handlers.
    private async Task<(bool Ok, string Line)> CommitVetAsync(ArchiveSession session, string name, string phrase)
    {
        (bool Vetted, string? RenameStatus) vetted;
        try
        {
            vetted = await _archive.VetSessionAsync(session, name, phrase);
        }
        catch (Exception error)
        {
            Diag.Log("vet failed: " + error.Message);
            return (false, "Could not vet this chat: " + error.Message);
        }
        if (!vetted.Vetted) return (false, vetted.RenameStatus ?? "Chat was not vetted.");

        var shown = Trim(name, 40);
        var status = vetted.RenameStatus;
        return (true, ArchiveService.NativeRenameSucceeded(status)
            ? $"Vetted \"{shown}\" into Active as {phrase.Trim()}."
            : $"Vetted \"{shown}\" into Active as {phrase.Trim()}; the tool's own name could not be written, so it is kept as the app name ({status}).");
    }
}
