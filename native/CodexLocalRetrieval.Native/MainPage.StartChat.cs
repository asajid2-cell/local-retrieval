using System.Diagnostics;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CodexLocalRetrieval_Native;

public sealed partial class MainPage
{
    private void StartChat_Click(object sender, RoutedEventArgs e) => _ = StartChatAsync();

    private async Task StartChatAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var tool = new ComboBox { MinWidth = 220, SelectedIndex = 0, Items = { "Claude", "Codex", "Shell only" } };
        var chatName = Field("Name this chat", "Optional app name");
        var phrase = Field("Special phrase", "Optional phrase, searchable as [phrase]");
        var location = Field("Working folder", home);
        location.Text = home;
        location.MinWidth = 390;
        var newFolder = Field("New subfolder", "Optional subfolder name");

        var browse = new Button { Content = "Browse...", CornerRadius = ControlCornerRadius() };
        browse.Click += async (_, _) =>
        {
            try
            {
                var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.ComputerFolder };
                picker.FileTypeFilter.Add("*");
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());
                var folder = await picker.PickSingleFolderAsync();
                if (folder is not null) location.Text = folder.Path;
            }
            catch (Exception ex) { Diag.Log("Folder pick failed " + ex); }
        };
        var locationRow = new Grid
        {
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            },
            ColumnSpacing = 8
        };
        locationRow.Children.Add(location);
        Grid.SetColumn(browse, 1);
        locationRow.Children.Add(browse);

        var deck = new ComboBox { MinWidth = 220, DisplayMemberPath = nameof(Deck.Name) };
        foreach (var item in _archive.Decks.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            deck.Items.Add(item);
        deck.SelectedItem = _archive.Decks.FirstOrDefault(d =>
            string.Equals(d.Id, _archive.ActiveDeckId, StringComparison.OrdinalIgnoreCase)) ?? deck.Items.FirstOrDefault();

        var collection = new ComboBox { MinWidth = 280, IsEditable = true, PlaceholderText = "Optional collection" };
        void LoadCollections()
        {
            var typed = collection.Text;
            collection.Items.Clear();
            collection.Items.Add("(no collection)");
            if (deck.SelectedItem is Deck selectedDeck)
                foreach (var item in _archive.CollectionsInDeck(selectedDeck.Id).OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
                    collection.Items.Add(item.Name);
            collection.SelectedIndex = 0;
            if (!string.IsNullOrWhiteSpace(typed) && typed != "(no collection)") collection.Text = typed;
        }
        deck.SelectionChanged += (_, _) => LoadCollections();
        LoadCollections();

        var durableFields = new StackPanel { Spacing = 12 };
        durableFields.Children.Add(Labeled("Chat name", chatName));
        durableFields.Children.Add(Labeled("Deck", deck));
        durableFields.Children.Add(Labeled("Collection", collection));
        durableFields.Children.Add(Labeled("Special phrase", phrase));

        var shellNotice = new TextBlock
        {
            Text = "Shell-only sessions are ephemeral and are not added to the archive.",
            Foreground = MutedBrush(),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };
        void UpdateToolMode()
        {
            var shell = string.Equals(tool.SelectedItem as string, "Shell only", StringComparison.Ordinal);
            chatName.IsEnabled = !shell;
            phrase.IsEnabled = !shell;
            deck.IsEnabled = !shell;
            collection.IsEnabled = !shell;
            shellNotice.Visibility = shell ? Visibility.Visible : Visibility.Collapsed;
        }
        tool.SelectionChanged += (_, _) => UpdateToolMode();

        var panel = new StackPanel { Spacing = 14, MinWidth = 500 };
        panel.Children.Add(Labeled("Tool", tool));
        panel.Children.Add(Labeled("Working folder", locationRow));
        panel.Children.Add(Labeled("New subfolder", newFolder));
        panel.Children.Add(durableFields);
        panel.Children.Add(shellNotice);

        var dialog = new ContentDialog
        {
            Title = "Start chat",
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            PrimaryButtonText = "Start chat",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var toolKey = (tool.SelectedItem as string) switch { "Codex" => "codex", "Shell only" => "shell", _ => "claude" };
        var baseDir = string.IsNullOrWhiteSpace(location.Text) ? home : location.Text.Trim();
        var sub = (newFolder.Text ?? "").Trim();
        string cwd;
        try
        {
            Directory.CreateDirectory(baseDir);
            cwd = baseDir;
            if (sub.Length > 0)
            {
                if (sub.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                {
                    SyncStatus.Text = "That folder name has invalid characters.";
                    return;
                }
                cwd = Path.Combine(baseDir, sub);
                Directory.CreateDirectory(cwd);
            }
        }
        catch (Exception ex)
        {
            Diag.Log("Start chat: folder setup failed " + ex);
            SyncStatus.Text = "Couldn't create that folder. Check the path.";
            return;
        }

        var selectedDeck = deck.SelectedItem as Deck;
        var collectionName = (collection.Text ?? collection.SelectedItem as string ?? "").Trim();
        if (collectionName == "(no collection)") collectionName = "";
        Diag.Log($"Start chat requested tool={toolKey} cwd={cwd} deck={selectedDeck?.Id ?? ""} collection={collectionName}");
        ArchiveCollection? targetCollection = null;
        var pendingIntentId = "";
        if (toolKey != "shell")
        {
            try
            {
                if (collectionName.Length > 0)
                    targetCollection = await _archive.CreateCollectionAsync(collectionName, selectedDeck?.Id);
                pendingIntentId = await _archive.QueuePendingNewChatAsync(
                    toolKey,
                    cwd,
                    targetCollection?.Id ?? "",
                    chatName.Text,
                    phrase.Text);
                if (pendingIntentId.Length == 0)
                    throw new InvalidOperationException("The new-chat filing intent could not be persisted.");
                Diag.Log($"Start chat intent queued id={pendingIntentId} collection={targetCollection?.Id ?? ""}");
            }
            catch (Exception ex)
            {
                SyncStatus.Text = ex.Message;
                return;
            }
        }

        var launch = _archive.BuildStartLaunch(toolKey, cwd);
        if (toolKey != "shell" && string.IsNullOrEmpty(launch.Exe))
        {
            if (pendingIntentId.Length > 0) await _archive.CancelPendingNewChatAsync(pendingIntentId);
            SyncStatus.Text = launch.DisplayCommand;
            return;
        }

        SessionLaunchLease? lease = null;
        if (toolKey != "shell")
        {
            lease = _launchGovernor.BeginFresh(new SessionLaunchRequest(
                null, null, toolKey, "native", "native deliberate chat start",
                "start.refused.native", "start.started.native", "start.failed.native", Workspace: cwd));
        }
        try
        {
            var args = string.IsNullOrEmpty(launch.DisplayCommand) ? "/k" : $"/k \"{launch.DisplayCommand}\"";
            Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = args,
                WorkingDirectory = launch.WorkingDirectory,
                UseShellExecute = true
            });
            Diag.Log($"Start chat process spawned tool={toolKey} cwd={launch.WorkingDirectory} intent={pendingIntentId}");
            lease?.MarkStarted("Started deliberate chat terminal.");
        }
        catch (Exception ex)
        {
            lease?.MarkFailed(ex.Message);
            if (pendingIntentId.Length > 0) await _archive.CancelPendingNewChatAsync(pendingIntentId);
            Diag.Log("Start chat launch failed " + ex);
            SyncStatus.Text = "Could not open a terminal. No filing intent was left behind.";
            return;
        }
        finally
        {
            lease?.Dispose();
        }

        if (toolKey == "shell")
        {
            SyncStatus.Text = $"Started an ephemeral shell in {cwd}.";
            return;
        }

        var filing = targetCollection is null ? "" : $" in \"{targetCollection.Name}\"";
        SyncStatus.Text = $"Started {ToolLabel(toolKey)} in {cwd}. It will be named and filed{filing} after its first transcript is indexed.";
        PollFileNewChatAsync(pendingIntentId, targetCollection?.Name ?? "");
    }

    private TextBox Field(string automationName, string placeholder)
    {
        var field = new TextBox
        {
            MinWidth = 360,
            CornerRadius = ControlCornerRadius(),
            PlaceholderText = placeholder
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(field, automationName);
        return field;
    }

    private UIElement Labeled(string label, UIElement control) => new StackPanel
    {
        Spacing = 4,
        Children =
        {
            new TextBlock { Text = label, Foreground = MutedBrush(), FontSize = 12.5 },
            control
        }
    };

    private async void PollFileNewChatAsync(string intentId, string collectionName)
    {
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(5000);
            try { await SyncNowAsync(initial: false); } catch (Exception ex) { Diag.Log("Pending chat sync poll: " + ex.Message); }
            var pending = _archive.Store.PendingNewChats.Any(p =>
                string.Equals(p.IntentId, intentId, StringComparison.Ordinal));
            if (!pending)
            {
                RenderCurrent();
                SyncStatus.Text = collectionName.Length > 0
                    ? $"The new chat is named and filed in \"{collectionName}\"."
                    : "The new chat is named and indexed.";
                return;
            }
        }
    }

    private static string ToolLabel(string toolKey) => toolKey == "codex" ? "Codex" : "Claude";
}
