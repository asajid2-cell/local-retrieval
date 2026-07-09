using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;

namespace CodexLocalRetrieval_Native;

// "Start chat": the missing front door. Pick a tool + a working folder (optionally make a new one),
// optionally target a collection, and launch a fresh claude/codex/shell in a terminal there. The chat's
// session id doesn't exist until the agent writes its transcript, so the collection link is recorded as
// a pending intent (by tool + cwd) and the next index pass files the real session in.
public sealed partial class MainPage
{
    private async Task StartChatAsync()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var tool = new ComboBox
        {
            MinWidth = 200,
            SelectedIndex = 0,
            Items = { "Claude", "Codex", "Shell only" }
        };

        var location = new TextBox
        {
            Text = home,
            MinWidth = 360,
            CornerRadius = ControlCornerRadius(),
            PlaceholderText = "Folder the chat runs in"
        };
        var browse = new Button { Content = "Browse…", CornerRadius = ControlCornerRadius() };
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
        var locationRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        locationRow.Children.Add(location);
        locationRow.Children.Add(browse);

        var newFolder = new TextBox
        {
            MinWidth = 360,
            CornerRadius = ControlCornerRadius(),
            PlaceholderText = "(optional) make a new sub-folder named… e.g. renderer-spike"
        };

        // Collection target: "(none)" + existing collections in the active deck. Editable so a brand-new
        // name can be typed (we create that collection on launch).
        var collection = new ComboBox { MinWidth = 280, IsEditable = true, PlaceholderText = "(no collection)" };
        collection.Items.Add("(no collection)");
        foreach (var c in _archive.Store.Collections.Values
                     .Where(c => string.Equals(c.DeckId, _archive.ActiveDeckId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(c.DeckId))
                     .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            collection.Items.Add(c.Name);
        collection.SelectedIndex = 0;

        UIElement Labeled(string label, UIElement control) => new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = label, Foreground = MutedBrush(), FontSize = 12.5 },
                control
            }
        };

        var panel = new StackPanel { Spacing = 14, MinWidth = 460 };
        panel.Children.Add(Labeled("Tool", tool));
        panel.Children.Add(Labeled("Location", locationRow));
        panel.Children.Add(Labeled("New folder (optional)", newFolder));
        panel.Children.Add(Labeled("Add to collection (optional)", collection));

        var dialog = new ContentDialog
        {
            Title = "Start a new chat",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Start",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var toolKey = (tool.SelectedItem as string) switch { "Codex" => "codex", "Shell only" => "shell", _ => "claude" };

        // Resolve the working directory (optionally creating a sub-folder).
        var baseDir = (location.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(baseDir)) baseDir = home;
        var sub = (newFolder.Text ?? "").Trim();
        string cwd;
        try
        {
            if (!Directory.Exists(baseDir)) { Directory.CreateDirectory(baseDir); }
            cwd = baseDir;
            if (sub.Length > 0)
            {
                if (sub.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                { SyncStatus.Text = "That folder name has invalid characters."; return; }
                cwd = Path.Combine(baseDir, sub);
                Directory.CreateDirectory(cwd);
            }
        }
        catch (Exception ex)
        {
            Diag.Log("Start chat: folder setup failed " + ex);
            SyncStatus.Text = "Couldn't create that folder - check the path.";
            return;
        }

        // Build a trusted launch and open it in a terminal.
        var launch = _archive.BuildStartLaunch(toolKey, cwd);
        if (toolKey != "shell" && string.IsNullOrEmpty(launch.Exe))
        {
            SyncStatus.Text = launch.DisplayCommand;   // e.g. "The claude CLI was not found at a trusted path."
            return;
        }
        SessionLaunchLease? lease = null;
        if (toolKey != "shell")
        {
            var request = new SessionLaunchRequest(
                null,
                null,
                toolKey,
                "native",
                "native fresh chat start",
                "start.refused.native",
                "start.started.native",
                "start.failed.native",
                Workspace: cwd);
            lease = _launchGovernor.BeginFresh(request);
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
            lease?.MarkStarted("Started fresh chat terminal.");
        }
        catch (Exception ex)
        {
            lease?.MarkFailed(ex.Message);
            Diag.Log("Start chat launch failed " + ex);
            SyncStatus.Text = "Could not open a terminal - see log.";
            return;
        }
        finally
        {
            lease?.Dispose();
        }

        // Optionally remember to file the new chat into a collection once it's indexed (not for shell -
        // a shell has no agent session).
        var colName = (collection.SelectedItem as string ?? collection.Text ?? "").Trim();
        if (toolKey != "shell" && colName.Length > 0 && colName != "(no collection)")
        {
            try
            {
                var col = _archive.Store.Collections.Values.FirstOrDefault(c =>
                    string.Equals(c.Name, colName, StringComparison.OrdinalIgnoreCase)
                    && (string.Equals(c.DeckId, _archive.ActiveDeckId, StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(c.DeckId)));
                if (col is null) col = await _archive.CreateCollectionAsync(colName);
                await _archive.QueuePendingNewChatAsync(toolKey, cwd, col.Id);
                SyncStatus.Text = $"Started {ToolLabel(toolKey)} in {cwd} - filing it into \"{colName}\" as soon as it saves...";
                RenderCollections();
                PollFileNewChatAsync(col.Id, colName);   // auto-file once the new transcript appears (no manual Sync needed)
            }
            catch (Exception ex) { Diag.Log("Queue pending new chat failed " + ex); SyncStatus.Text = $"Started {ToolLabel(toolKey)} in {cwd}."; }
        }
        else
        {
            SyncStatus.Text = $"Started {ToolLabel(toolKey)} in {cwd}.";
        }
    }

    // After a Start-chat with a target collection, watch for the new transcript and file it — without
    // making the user hit Sync. Cheap folder-diff poll (~2 min); once it files, one full index brings the
    // session into the store so it renders. The pending lives 24h, so even a slow first message is caught
    // on the next normal Sync.
    private async void PollFileNewChatAsync(string collectionId, string colName)
    {
        for (var i = 0; i < 24; i++)
        {
            await Task.Delay(5000);
            try { await SyncNowAsync(initial: false); } catch (Exception ex) { Diag.Log("Pending chat sync poll: " + ex.Message); }
            if (!_archive.Store.PendingNewChats.Any(p => string.Equals(p.CollectionId, collectionId, StringComparison.Ordinal)))
            {
                RenderCollections();
                SyncStatus.Text = $"Filed the new chat into \"{colName}\".";
                return;   // consumed by the sync pass above, or elsewhere
            }
            var filed = false;
            try { filed = await _archive.ReconcilePendingNewChatsAsync(); } catch (Exception ex) { Diag.Log("Reconcile poll: " + ex.Message); }
            if (filed)
            {
                RenderCollections();
                SyncStatus.Text = $"Filed the new chat into \"{colName}\".";
                return;
            }
        }
    }

    private static string ToolLabel(string toolKey) => toolKey switch { "codex" => "Codex", "shell" => "a shell", _ => "Claude" };
}
