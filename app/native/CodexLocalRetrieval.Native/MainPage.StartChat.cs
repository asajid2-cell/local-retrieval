using System.Diagnostics;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
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
        var tool = new ComboBox { MinWidth = 220, SelectedIndex = 0, Items = { "Claude", "Gateway (cc)", "Codex", "Shell only" } };
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

        // Start from an immutable checkpoint. Tool + folder come from its source chat.
        var templates = _archive.Templates();
        var templatePicker = new ComboBox { MinWidth = 320 };
        templatePicker.Items.Add(new ComboBoxItem { Content = "(none — start a blank chat)", Tag = null });
        foreach (var t in templates)
        {
            templatePicker.Items.Add(new ComboBoxItem
            {
                Content = ArchiveService.TemplateSnapshotDisplayLabel(t) + $" · source: {t.SourceTitle}",
                Tag = t
            });
        }
        templatePicker.SelectedIndex = 0;
        TemplateSnapshot? SelectedTemplate() => (templatePicker.SelectedItem as ComboBoxItem)?.Tag as TemplateSnapshot;
        var templateNote = new TextBlock
        {
            Text = "Tool and folder come from the template.",
            Foreground = MutedBrush(),
            FontSize = 12,
            Visibility = Visibility.Collapsed
        };
        void UpdateTemplateMode()
        {
            var usingTemplate = SelectedTemplate() is not null;
            tool.IsEnabled = !usingTemplate;
            location.IsEnabled = !usingTemplate;
            newFolder.IsEnabled = !usingTemplate;
            browse.IsEnabled = !usingTemplate;
            templateNote.Visibility = usingTemplate ? Visibility.Visible : Visibility.Collapsed;
        }
        templatePicker.SelectionChanged += (_, _) => UpdateTemplateMode();

        var panel = new StackPanel { Spacing = 14, MinWidth = 500 };
        if (templates.Count > 0)
        {
            panel.Children.Add(Labeled("Start from checkpoint", templatePicker));
            panel.Children.Add(templateNote);
        }
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

        // Spawn from the immutable checkpoint, then apply this chat's filing metadata.
        var chosenTemplate = SelectedTemplate();
        if (chosenTemplate is not null)
        {
            var templateCollection = (collection.Text ?? collection.SelectedItem as string ?? "").Trim();
            if (templateCollection == "(no collection)") templateCollection = "";
            await StartFromTemplateAsync(chosenTemplate, chatName.Text, phrase.Text, templateCollection, (deck.SelectedItem as Deck)?.Id);
            return;
        }

        var selectedTool = tool.SelectedItem as string;
        var toolKey = selectedTool switch { "Codex" => "codex", "Shell only" => "shell", _ => "claude" };
        var launchMode = selectedTool == "Gateway (cc)"
            ? ArchiveService.GatewayLaunchMode
            : ArchiveService.NativeLaunchMode;
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
                    RecordAppEvent(
                        "start.refused.folder-name",
                        "Start chat refused because the requested subfolder name contains invalid characters.",
                        "warn",
                        tool: toolKey,
                        workspace: baseDir);
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
            RecordAppEvent(
                "start.failed.folder-setup",
                ex.Message,
                "error",
                tool: toolKey,
                workspace: baseDir,
                details: new Dictionary<string, string> { ["operation"] = "start-chat.folder-setup" });
            SyncStatus.Text = "Couldn't create that folder: " + ex.Message;
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
                    phrase.Text,
                    launchMode);
                if (pendingIntentId.Length == 0)
                    throw new InvalidOperationException("The new-chat filing intent could not be persisted.");
                Diag.Log($"Start chat intent queued id={pendingIntentId} collection={targetCollection?.Id ?? ""}");
                RecordAppEvent(
                    "start.intent.queued",
                    "Queued the new-chat filing intent.",
                    tool: toolKey,
                    workspace: cwd,
                    details: new Dictionary<string, string>
                    {
                        ["intentId"] = pendingIntentId,
                        ["collectionId"] = targetCollection?.Id ?? ""
                    });
            }
            catch (Exception ex)
            {
                RecordAppEvent(
                    "start.intent.failed",
                    ex.Message,
                    "error",
                    tool: toolKey,
                    workspace: cwd,
                    details: new Dictionary<string, string> { ["operation"] = "start-chat.intent" });
                SyncStatus.Text = ex.Message;
                return;
            }
        }

        var launch = _archive.BuildStartLaunch(toolKey, cwd, launchModeOverride: launchMode);
        if (toolKey != "shell" && string.IsNullOrEmpty(launch.Exe))
        {
            if (pendingIntentId.Length > 0) await _archive.CancelPendingNewChatAsync(pendingIntentId);
            RecordAppEvent(
                "start.refused.invalid-launch",
                launch.DisplayCommand,
                "warn",
                tool: toolKey,
                workspace: cwd,
                details: new Dictionary<string, string> { ["intentId"] = pendingIntentId });
            SyncStatus.Text = launch.DisplayCommand;
            return;
        }

        SessionLaunchLease? lease = null;
        if (toolKey != "shell")
        {
            lease = _launchGovernor.BeginFresh(new SessionLaunchRequest(
                null, null, toolKey, launchMode, $"{launchMode} deliberate chat start",
                "start.refused.native", "start.started.native", "start.failed.native", Workspace: cwd));
        }
        try
        {
            var process = launchMode == ArchiveService.GatewayLaunchMode
                ? BuildGatewayTerminalStartInfo(launch)
                : new ProcessStartInfo
                {
                    FileName = ArchiveService.ResolveCmdExe(),
                    Arguments = string.IsNullOrEmpty(launch.DisplayCommand) ? "/k" : $"/k \"{launch.DisplayCommand}\"",
                    WorkingDirectory = launch.WorkingDirectory,
                    UseShellExecute = true
                };
            Process.Start(process);
            Diag.Log($"Start chat process spawned tool={toolKey} cwd={launch.WorkingDirectory} intent={pendingIntentId}");
            lease?.MarkStarted("Started deliberate chat terminal.");
            if (toolKey == "shell")
                RecordAppEvent(
                    "start.started.shell",
                    "Started an ephemeral shell.",
                    tool: toolKey,
                    workspace: launch.WorkingDirectory);
        }
        catch (Exception ex)
        {
            lease?.MarkFailed(ex.Message);
            if (pendingIntentId.Length > 0) await _archive.CancelPendingNewChatAsync(pendingIntentId);
            Diag.Log("Start chat launch failed " + ex);
            if (toolKey == "shell")
                RecordAppEvent(
                    "start.failed.shell",
                    ex.Message,
                    "error",
                    tool: toolKey,
                    workspace: cwd);
            SyncStatus.Text = "Could not open a terminal: " + ex.Message + " No filing intent was left behind.";
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
        PollFileNewChatAsync(pendingIntentId, targetCollection?.Name ?? "", toolKey);
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

    // A new chat used to be hunted by a blind full SyncNowAsync every 5 s for two minutes — 24 rescans
    // of the whole archive to notice one file appearing. The transcript lands in the tool's own session
    // root, so watch THAT and sync only when a .jsonl actually shows up there. The wait is still capped
    // so a root we could not watch (or a tool that writes somewhere unexpected) still converges.
    private async void PollFileNewChatAsync(string intentId, string collectionName, string toolKey)
    {
        var signal = new SemaphoreSlim(0);
        var watches = new List<IFileWatchRegistration>();
        try
        {
            foreach (var src in _archive.EffectiveSources())
            {
                if (toolKey.Length > 0 && !string.Equals(src.Tool, toolKey, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.IsNullOrWhiteSpace(src.Root) || !Directory.Exists(src.Root)) continue;
                try { watches.Add(FileWatch.WatchDirectory(src.Root, "*.jsonl", recurse: true, () => { try { signal.Release(); } catch { } })); }
                catch (Exception ex) { Diag.Log("Pending chat watch " + src.Root + ": " + ex.Message); }
            }
        }
        catch (Exception ex) { Diag.Log("Pending chat roots: " + ex.Message); }

        // With a watcher the wake is the event and this is only a backstop; with none it IS the poll.
        var wake = watches.Count > 0 ? TimeSpan.FromSeconds(30) : TimeSpan.FromSeconds(5);
        var deadline = DateTime.UtcNow.AddSeconds(120);
        try
        {
            while (DateTime.UtcNow < deadline)
            {
                await signal.WaitAsync(wake);
                while (signal.Wait(0)) { }        // a burst of writes is one reason to sync, not many

                try { await SyncNowAsync(initial: false); } catch (Exception ex) { Diag.Log("Pending chat sync poll: " + ex.Message); }
                var pending = _archive.Store.PendingNewChats.Any(p =>
                    string.Equals(p.IntentId, intentId, StringComparison.Ordinal));
                if (!pending)
                {
                    RecordAppEvent(
                        "start.intent.filed",
                        collectionName.Length > 0
                            ? $"The new chat was indexed and filed in \"{collectionName}\"."
                            : "The new chat was indexed and its pending filing intent completed.",
                        tool: toolKey,
                        details: new Dictionary<string, string> { ["intentId"] = intentId });
                    RenderCurrent();
                    SyncStatus.Text = collectionName.Length > 0
                        ? $"The new chat is named and filed in \"{collectionName}\"."
                        : "The new chat is named and indexed.";
                    return;
                }
            }
            RecordAppEvent(
                "start.intent.timed-out",
                "The new chat did not appear in the archive before the two-minute filing deadline.",
                "warn",
                tool: toolKey,
                details: new Dictionary<string, string> { ["intentId"] = intentId });
            SyncStatus.Text = "The terminal started, but the new chat was not indexed within two minutes. Its filing intent is still pending.";
        }
        finally
        {
            foreach (var w in watches) { try { w.Dispose(); } catch { } }
            signal.Dispose();
        }
    }

    private static string ToolLabel(string toolKey) => toolKey == "codex" ? "Codex" : "Claude";
}
