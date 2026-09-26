using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace CodexLocalRetrieval_Native;

// On/off control for the headless /remote server (CodexLocalRetrieval.Server) from the GUI. The server
// auto-starts at logon (the "CodexArchiveRemote" task) and is light by default (it loads your archive
// only when you open Chats remotely — idle ≈ 40 MB). This panel lets you START it or STOP it to reclaim
// RAM when you don't need remote access at all.
public sealed partial class MainPage
{
    private const string RemoteServerProcName = "CodexLocalRetrieval.Server";

    private static string RemoteServerDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexArchiveRemote");

    private static string RemoteServerLauncher => Path.Combine(RemoteServerDir, "run-remote.ps1");

    // A watchdog keeps the server alive on its own (so the site never depends on this app being open),
    // which means a deliberate Stop has to be recorded or the watchdog would bring it straight back.
    // Start clears it; the watchdog ignores it once the machine has rebooted.
    private static string RemoteServerPauseMarker => Path.Combine(RemoteServerDir, "watchdog.pause");

    private static Process? RemoteServerProcess()
    {
        try { return Process.GetProcessesByName(RemoteServerProcName).FirstOrDefault(); }
        catch { return null; }
    }

    private Border RemoteServerPanel()
    {
        var proc = RemoteServerProcess();
        var running = proc is not null;
        string status;
        try { status = running ? $"Running — ~{Math.Round(proc!.WorkingSet64 / 1048576.0)} MB" : "Stopped (RAM reclaimed)"; }
        catch { status = running ? "Running" : "Stopped"; }

        var statusText = new TextBlock
        {
            Text = status,
            Foreground = running ? StrongBrush() : MutedBrush(),
            FontSize = 15,
            VerticalAlignment = VerticalAlignment.Center
        };

        var btn = new Button
        {
            Style = (Style)Resources["PillButtonStyle"],
            Content = running ? "Stop server" : "Start server"
        };
        ToolTipService.SetToolTip(btn, running
            ? "Stop the MUX remote server and free its RAM. It stays stopped until you press Start, or until the next login."
            : "Start the MUX remote server so you can reach your workspace at harmonizerlabs.cc/remote.");
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            if (running) StopRemoteServer(); else StartRemoteServer();
            await Task.Delay(running ? 700 : 1600);   // let it die / boot, then refresh the panel
            RenderSettings();
        };

        var installed = File.Exists(RemoteServerLauncher);
        if (!installed) { btn.IsEnabled = false; btn.Content = "Not installed"; }

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(statusText);
        row.Children.Add(btn);

        var body = installed
            ? "harmonizerlabs.cc/remote — browse and drive your Claude/Codex chats from any browser through the MUX remote server. It " +
              "auto-starts at login, stays light (loads your archive only when you open Chats; idle ≈ 40 MB), and is kept alive by a " +
              "watchdog, so the site works whether or not this app is open. " +
              "Stop it here to reclaim memory when you don't need remote access — it comes back when you press Start, or at next login."
            : "The MUX remote server isn't installed on this PC (run-remote.ps1 not found in %LocalAppData%\\CodexArchiveRemote).";

        return SettingsPanel("MUX remote server", body, row);
    }

    private void StartRemoteServer()
    {
        try
        {
            if (RemoteServerProcess() is not null) { SyncStatus.Text = "MUX remote server is already running."; return; }
            if (!File.Exists(RemoteServerLauncher)) { SyncStatus.Text = "MUX remote server isn't installed (run-remote.ps1 missing)."; return; }
            // Lifting the stop marker FIRST, so the watchdog cannot race us and treat a manual start as stopped.
            try { File.Delete(RemoteServerPauseMarker); } catch { }
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-WindowStyle Hidden -ExecutionPolicy Bypass -NonInteractive -File \"{RemoteServerLauncher}\"",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                CreateNoWindow = true
            });
            SyncStatus.Text = "Starting the MUX remote server…";
        }
        catch (Exception ex) { Diag.Log("StartRemoteServer FAILED " + ex); SyncStatus.Text = "Couldn't start the MUX remote server — see log."; }
    }

    private void StopRemoteServer()
    {
        try
        {
            var any = false;
            foreach (var p in Process.GetProcessesByName(RemoteServerProcName))
            {
                try { p.Kill(entireProcessTree: true); any = true; } catch { }
            }
            // Record the stop BEFORE the watchdog's next pass, or it would restart what the user just stopped.
            try { File.WriteAllText(RemoteServerPauseMarker, DateTime.UtcNow.ToString("o")); } catch { }
            SyncStatus.Text = any ? "Stopped the MUX remote server (RAM reclaimed)." : "MUX remote server wasn't running.";
        }
        catch (Exception ex) { Diag.Log("StopRemoteServer FAILED " + ex); }
    }
}
