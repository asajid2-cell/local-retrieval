using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace CodexLocalRetrieval_Native;

// Backup + recover for collections. Collections are just lightweight metadata (a name + a list of
// chat ids), so they're cheap to snapshot. The app keeps automatic backups on every change, a
// deleted collection lands in Recently Deleted instead of vanishing, and you can export/import a
// backup file to move your projects between machines or recover after a reset.
public sealed partial class MainPage
{
    // The "Backup" button + menu in the Collections control bar.
    private Button BackupMenuButton()
    {
        var button = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Backup" };
        var flyout = new MenuFlyout { AreOpenCloseAnimationsEnabled = false };

        var export = new MenuFlyoutItem { Text = "Export collections to file..." };
        export.Click += async (_, _) => await ExportCollectionsToFileAsync();
        flyout.Items.Add(export);

        var import = new MenuFlyoutItem { Text = "Restore from a backup file..." };
        import.Click += async (_, _) => await ImportCollectionsFromFileAsync();
        flyout.Items.Add(import);

        flyout.Items.Add(new MenuFlyoutSeparator());

        var appBackups = new MenuFlyoutItem { Text = "Restore from an app backup..." };
        appBackups.Click += async (_, _) => await ShowAppBackupsAsync();
        flyout.Items.Add(appBackups);

        var openFolder = new MenuFlyoutItem { Text = "Open backups folder" };
        openFolder.Click += (_, _) => OpenBackupsFolder();
        flyout.Items.Add(openFolder);

        button.Flyout = flyout;
        return button;
    }

    private IntPtr WindowHandle()
        => MainWindow.Instance is null ? IntPtr.Zero : WinRT.Interop.WindowNative.GetWindowHandle(MainWindow.Instance);

    private async Task ExportCollectionsToFileAsync()
    {
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeChoices.Add("Collections backup", new List<string> { ".json" });
            picker.SuggestedFileName = "codex-collections-" + DateTime.Now.ToString("yyyy-MM-dd");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            await FileIO.WriteTextAsync(file, _archive.ExportCollectionsJson());
            SyncStatus.Text = $"Exported {_archive.Store.Collections.Count} collection(s) to {file.Name}.";
        }
        catch (Exception ex) { Diag.Log("Export failed " + ex); SyncStatus.Text = "Export failed - see log."; }
    }

    private async Task ImportCollectionsFromFileAsync()
    {
        try
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
            picker.FileTypeFilter.Add(".json");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WindowHandle());
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            var json = await FileIO.ReadTextAsync(file);
            var added = await _archive.ImportCollectionsJsonAsync(json);
            if (added < 0) { await ShowInfoAsync("Restore failed", "That file isn't a valid collections backup."); return; }
            RenderCollections();
            SyncStatus.Text = added == 0 ? "Restore complete - everything was already present." : $"Restored {added} collection(s) from backup.";
        }
        catch (Exception ex) { Diag.Log("Import failed " + ex); SyncStatus.Text = "Restore failed - see log."; }
    }

    private void OpenBackupsFolder()
    {
        try
        {
            Directory.CreateDirectory(_archive.CollectionBackupsDir);
            Process.Start(new ProcessStartInfo { FileName = _archive.CollectionBackupsDir, UseShellExecute = true });
        }
        catch (Exception ex) { Diag.Log("Open backups folder failed " + ex); }
    }

    // Pick one of the app's automatic backups to restore from.
    private async Task ShowAppBackupsAsync()
    {
        var backups = _archive.ListAppBackups();
        var panel = new StackPanel { Spacing = 8, MinWidth = 460 };
        if (backups.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "No app backups yet. They're written automatically when you create or delete a collection.", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            panel.Children.Add(new TextBlock { Text = "Restoring merges a backup's collections back in - it never deletes anything you have now.", Foreground = MutedBrush(), TextWrapping = TextWrapping.Wrap });
            foreach (var (path, when, count) in backups.Take(30))
            {
                var row = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
                row.Children.Add(new TextBlock
                {
                    Text = $"{when:MMM d, h:mm tt}  -  {count} collection{(count == 1 ? "" : "s")}",
                    Foreground = StrongBrush(),
                    VerticalAlignment = VerticalAlignment.Center
                });
                var restore = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Restore", MinHeight = 34, Padding = new Thickness(12, 0, 12, 0) };
                var p = path;
                restore.Click += async (_, _) =>
                {
                    var added = await _archive.RestoreAppBackupAsync(p);
                    RenderCollections();
                    SyncStatus.Text = added < 0 ? "That backup couldn't be read." : added == 0 ? "Already up to date." : $"Restored {added} collection(s).";
                };
                Grid.SetColumn(restore, 1);
                row.Children.Add(restore);
                panel.Children.Add(row);
            }
        }

        var dialog = new ContentDialog
        {
            Title = "App backups",
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 420 },
            CloseButtonText = "Close",
            XamlRoot = XamlRoot
        };
        await dialog.ShowAsync();
    }

    // The "Recently deleted" section appended to the Collections screen. Empty -> renders nothing.
    private void RenderRecentlyDeleted()
    {
        var deleted = _archive.Store.DeletedCollections;
        if (deleted.Count == 0) return;

        var stack = new StackPanel { Spacing = 6 };
        var header = new Grid { ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        header.Children.Add(new TextBlock
        {
            Text = $"Recently deleted ({deleted.Count})",
            Foreground = StrongBrush(),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center
        });
        var empty = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Empty", MinHeight = 32, Padding = new Thickness(12, 0, 12, 0) };
        empty.Click += async (_, _) => { await _archive.EmptyRecentlyDeletedAsync(); RenderCollections(); };
        Grid.SetColumn(empty, 1);
        header.Children.Add(empty);
        stack.Children.Add(header);
        stack.Children.Add(new TextBlock { Text = "Restore a project you removed by mistake. Chats themselves were never deleted.", Foreground = MutedBrush(), FontSize = 12, TextWrapping = TextWrapping.Wrap });

        foreach (var d in deleted.ToList())
        {
            var col = d.Collection;
            var when = DateTime.TryParse(d.DeletedAt, out var dt) ? dt.ToLocalTime().ToString("MMM d, h:mm tt") : "";
            var row = new Grid
            {
                MinHeight = 40,
                BorderBrush = LineBrush(),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Padding = new Thickness(0, 8, 0, 8),
                ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } }
            };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = col.Name, Foreground = StrongBrush(), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 },
                    new TextBlock { Text = $"{col.SessionIds.Count} chat{(col.SessionIds.Count == 1 ? "" : "s")} - deleted {when}", Foreground = MutedBrush(), FontSize = 12 }
                }
            });
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
            var restore = new Button { Style = (Style)Resources["PrimaryPillButtonStyle"], Content = "Restore", MinHeight = 34, Padding = new Thickness(12, 0, 12, 0) };
            var cid = col.Id;
            restore.Click += async (_, _) => { await _archive.RestoreDeletedCollectionAsync(cid); RenderCollections(); SyncStatus.Text = $"Restored \"{col.Name}\"."; };
            var forget = new Button { Style = (Style)Resources["PillButtonStyle"], Content = "Delete forever", MinHeight = 34, Padding = new Thickness(12, 0, 12, 0) };
            forget.Click += async (_, _) => { await _archive.PurgeDeletedCollectionAsync(cid); RenderCollections(); };
            actions.Children.Add(restore);
            actions.Children.Add(forget);
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);
            stack.Children.Add(row);
        }

        MainContent.Children.Add(Card(stack));
    }
}
