using System;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml;

namespace CodexLocalRetrieval_Native;

// "Start remote session": resume a chat NOT in a local terminal but inside a multiplex session you can
// also drive from your phone. The session lives in tmux on the VPS, SSHes back into THIS PC, and runs
// claude/codex natively here — so the desk window, the new Windows-Terminal tab we open, and
// harmonizerlabs.cc/multiplex all attach the SAME session (a true mirror). The app creates the session
// over its own owner-only SSH to the VPS (the multiplex API trusts loopback), then opens the local tab.
public sealed partial class MainPage
{
    // All remote sessions share one Windows Terminal window so a busy day of resumes is tabs, not a
    // screenful of windows. Anchored by this window name; the first tab creates it, the rest join it.
    private const string MultiplexWtWindow = "multiplex";

    private async void StartRemoteSession(ArchiveSession session)
    {
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        if (string.IsNullOrEmpty(target))
        {
            SyncStatus.Text = "Set a multiplex SSH target in Settings to start remote sessions.";
            return;
        }

        var command = _archive.BuildMultiplexCommand(session);
        if (string.IsNullOrEmpty(command))
        {
            SyncStatus.Text = "Remote session refused: this chat's id is not a safe resume token.";
            return;
        }

        var name = ArchiveService.MultiplexSessionName(session);
        var title = Trim(session.DisplayTitle, 40);
        SyncStatus.Text = $"Starting remote session \"{title}\"...";

        try
        {
            var created = await CreateRemoteSessionAsync(target, settings.MultiplexApiPort, name, command);
            if (!created.ok)
            {
                Diag.Log($"Remote create FAILED ({created.detail}) name={name}");
                SyncStatus.Text = "Could not create the remote session - see log.";
                return;
            }

            OpenMultiplexTab(target, settings.MultiplexSessionLauncher, name);

            // Bump like a local resume so the chat is where you expect when you come back to the app.
            session.UpdatedAt = DateTime.UtcNow.ToString("O");
            _archive.RefreshSessions(_archive.Store.Sessions.Values);
            SessionList.SelectedItem = session;
            _ = _archive.SaveAsync();

            SyncStatus.Text = $"Remote \"{title}\" live as \"{name}\" - open it on your phone at /multiplex.";
        }
        catch (Exception ex)
        {
            Diag.Log("StartRemoteSession FAILED " + ex);
            SyncStatus.Text = "Could not start the remote session - see log.";
        }
    }

    // --- project sync: keep the web's Projects view (harmonizerlabs.cc/multiplex) in step with the app
    // while it's open, so you can see your collections + chats and resume any of them remotely. Pushes
    // the projection now + every 30s; the web shows "synced / app live" when these land. --------------
    private DispatcherTimer? _syncTimer;
    private bool _syncPushing;

    public void StartProjectSync()
    {
        _ = PushProjectsAsync();
        _syncTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _syncTimer.Tick -= OnSyncTick;
        _syncTimer.Tick += OnSyncTick;
        _syncTimer.Start();
    }
    private async void OnSyncTick(object? sender, object e) => await PushProjectsAsync();

    private async Task PushProjectsAsync()
    {
        if (_syncPushing) return;
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return;
        string json;
        try { json = _archive.BuildProjectsProjectionJson(); }
        catch (Exception ex) { Diag.Log("BuildProjects failed: " + ex.Message); return; }
        _syncPushing = true;
        try
        {
            // POST over our own owner-only SSH to the VPS loopback (the API trusts loopback); the JSON
            // rides ssh stdin into curl so its quotes/backslashes never touch a shell command line.
            var remote = $"curl -s -X POST http://127.0.0.1:{settings.MultiplexApiPort}/api/projects -H 'Content-Type: application/json' --data-binary @-";
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"{target} \"{remote}\"",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            await p.StandardInput.WriteAsync(json);
            p.StandardInput.Close();
            var outText = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            Diag.Log($"Projects sync rc={p.ExitCode} out={outText.Trim()}");
        }
        catch (Exception ex) { Diag.Log("PushProjects failed: " + ex.Message); }
        finally { _syncPushing = false; }
    }

    // Create (or reuse) the tmux session on the VPS and queue the resume command, by POSTing to the
    // multiplex API over our own SSH. The API binds to loopback and trusts direct-loopback callers, so
    // an SSH-tunnelled curl needs no SSO cookie. The JSON body is piped through ssh stdin (curl -d @-)
    // so neither the Windows path's backslashes nor the command's quotes have to survive shell-nesting.
    private static async Task<(bool ok, string detail)> CreateRemoteSessionAsync(string target, int port, string name, string command)
    {
        var json = JsonSerializer.Serialize(new { name, command });
        var remote = $"curl -s -X POST http://127.0.0.1:{port}/api/sessions -H 'Content-Type: application/json' -d @-";
        var psi = new ProcessStartInfo
        {
            FileName = "ssh",
            Arguments = $"{target} \"{remote}\"",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi);
        if (p is null) return (false, "ssh did not start");
        await p.StandardInput.WriteAsync(json);
        p.StandardInput.Close();
        var outText = await p.StandardOutput.ReadToEndAsync();
        var errText = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Diag.Log($"Remote create rc={p.ExitCode} out={outText.Trim()} err={errText.Trim()}");
        var ok = p.ExitCode == 0 && outText.Contains("\"ok\":true");
        return (ok, ok ? "ok" : $"rc={p.ExitCode} {errText.Trim()} {outText.Trim()}");
    }

    // Attach the local mirror as a TAB in the shared multiplex window (Windows Terminal). The tab runs
    // `ssh -t <vps> <launcher> <name>`, which does the tmux attach-or-create on the VPS. Falls back to a
    // plain PowerShell window if Windows Terminal isn't available.
    private void OpenMultiplexTab(string target, string launcher, string name)
    {
        var attach = $"ssh -t {target} {launcher} {name}";
        try
        {
            // Wrap the attach in `powershell -NoExit` so detaching tmux (or the session ending) leaves a
            // usable shell in the tab instead of the tab silently vanishing.
            var psi = new ProcessStartInfo
            {
                FileName = "wt",
                Arguments = $"-w {MultiplexWtWindow} new-tab --title \"{name}\" powershell -NoExit -Command {attach}",
                UseShellExecute = true,
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Diag.Log("Windows Terminal unavailable, falling back to a window: " + ex.Message);
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = $"-NoExit -Command {attach}",
                UseShellExecute = true,
            });
        }
    }
}
