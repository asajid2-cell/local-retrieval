using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace CodexLocalRetrieval_Native;

// Don't double-run a chat. Resuming a chat that's ALREADY running — in a local terminal or inside a
// multiplex (which runs the agent on this PC via SSH-back) — makes two processes append to the same
// transcript/rollout and corrupts it. We scan live claude/codex processes for the session id each is
// resuming (off their command lines) and guard the resume flows: attach instead of re-injecting, or
// ask to kill the running copy before taking over.
public sealed partial class MainPage
{
    // id -> a pid currently resuming it. Catches local AND multiplex runs (both run the agent here).
    private Dictionary<string, int> GetRunningChats()
    {
        var map = new Dictionary<string, int>();
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='claude.exe' OR Name='codex.exe'");
            foreach (ManagementObject mo in searcher.Get())
            {
                var id = ArchiveService.ParseResumedSessionId(mo["CommandLine"]?.ToString() ?? "");
                if (!string.IsNullOrEmpty(id) && !map.ContainsKey(id))
                    map[id] = Convert.ToInt32(mo["ProcessId"]);
            }
        }
        catch (Exception ex) { Diag.Log("GetRunningChats failed: " + ex.Message); }
        return map;
    }

    private void TryKillChat(int pid)
    {
        try { Process.GetProcessById(pid).Kill(entireProcessTree: true); }
        catch (Exception ex) { Diag.Log($"Kill pid {pid} failed: " + ex.Message); }
    }

    // Does a multiplex session of this name already exist on the VPS? Lets "Resume in multiplex" ATTACH
    // an already-running session instead of injecting a second resume into it.
    private static async Task<bool> RemoteSessionExistsAsync(string target, int port, string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"{target} \"curl -s http://127.0.0.1:{port}/api/sessions\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return false;
            var outText = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return outText.Contains($"\"name\":\"{name}\"");
        }
        catch { return false; }
    }

    // DELETE the multiplex session on the VPS (ends its tmux session + the agent running inside it).
    private static async Task DeleteRemoteSessionAsync(string target, int port, string name)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"{target} \"curl -s -X DELETE http://127.0.0.1:{port}/api/sessions/{name}\"",
                UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return;
            await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
        }
        catch { }
    }

    private enum RunGuard { Proceed, Kill, Cancel }

    private async Task<RunGuard> ConfirmAlreadyRunningAsync(string title, string where)
    {
        var dialog = new ContentDialog
        {
            Title = "This chat is already running",
            Content = $"\"{title}\" looks like it's already running in {where}. Running it twice makes two " +
                      "processes write the same transcript and can corrupt it. Kill the running copy and take over here?",
            PrimaryButtonText = "Kill it & continue",
            SecondaryButtonText = "Start anyway",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        var r = await dialog.ShowAsync();
        return r == ContentDialogResult.Primary ? RunGuard.Kill
             : r == ContentDialogResult.Secondary ? RunGuard.Proceed
             : RunGuard.Cancel;
    }

    // Shared pre-launch guard for BOTH resume flows: for THIS chat, check a multiplex session on the
    // VPS AND a local agent process, and if either is up, offer to kill it (or cancel and deal with it
    // later). Returns false to abort the launch.
    private async Task<bool> ConfirmRunOrKillAsync(ArchiveSession session)
    {
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        var muxName = ArchiveService.MultiplexSessionName(session);

        var muxUp = !string.IsNullOrEmpty(target) && await RemoteSessionExistsAsync(target, settings.MultiplexApiPort, muxName);
        var running = await Task.Run(GetRunningChats);
        var localPid = (!string.IsNullOrEmpty(session.Id) && running.TryGetValue(session.Id, out var pid)) ? pid : 0;
        // if a multiplex is up, the running process IS its agent (not a separate local one)
        var localUp = localPid != 0 && !muxUp;

        if (!muxUp && !localUp) return true;   // nothing running -> proceed

        var where = muxUp ? "a multiplex on the VPS" : "locally on this PC";
        var choice = await ConfirmAlreadyRunningAsync(Trim(session.DisplayTitle, 40), where);
        if (choice == RunGuard.Cancel) return false;
        if (choice == RunGuard.Kill)
        {
            if (muxUp) await DeleteRemoteSessionAsync(target, settings.MultiplexApiPort, muxName);
            if (localPid != 0) TryKillChat(localPid);
            await Task.Delay(400);
        }
        return true;
    }
}
