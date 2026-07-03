using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// The remote command bridge, runnable from the ALWAYS-ON headless server so remote "running sessions"
// (see / Open transcript / Kill / rename) keep working when the heavy desktop app is closed. It mirrors
// the GUI app's MainPage.Remote.cs loop, but only acts while the GUI app is NOT running — the GUI is the
// primary bridge when open (it pushes the full enriched projection), so the two never double-process.
//
// Reliability: every ssh is hardened (-n / BatchMode / ConnectTimeout / ServerAlive) AND carries a hard
// wall-clock timeout that tree-kills on overrun, so a stalled connection can never wedge the loop or pile
// up orphan ssh.exe (the exact bug that silently killed the GUI app's command poll).
public sealed class RemoteBridge
{
    public sealed record Settings(string Target, int Port);

    private readonly Func<Settings?> _settings;      // cheap, per-tick (SSH target + multiplex API port)
    private readonly Func<bool> _guiPrimaryRunning;  // true => the desktop app owns the bridge; we stand down
    private readonly ClaudeSessionStore _claude;
    private readonly string _codexDbPath;
    private readonly Action<string> _log;

    private const string SshHardenOpts = "-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=3";
    private static readonly TimeSpan SshHardTimeout = TimeSpan.FromSeconds(30);
    private const string MultiplexWtWindow = "multiplex";

    public RemoteBridge(Func<Settings?> settings, Func<bool> guiPrimaryRunning, ClaudeSessionStore claude, string codexDbPath, Action<string>? log = null)
    {
        _settings = settings;
        _guiPrimaryRunning = guiPrimaryRunning;
        _claude = claude;
        _codexDbPath = codexDbPath ?? "";
        _log = log ?? (_ => { });
    }

    public async Task RunLoopAsync(CancellationToken ct)
    {
        _log("remote bridge loop started (acts only while the desktop app is closed)");
        var tick = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (!_guiPrimaryRunning())   // desktop app present => it pushes + drains; we do nothing
                {
                    var s = _settings();
                    if (s is not null && !string.IsNullOrWhiteSpace(s.Target))
                    {
                        if (tick % 3 == 0) await PushRunningAsync(s);   // running heartbeat ~every 9s (keeps `live` + refreshes the list)
                        await PollAndProcessAsync(s);                    // drain commands ~every 3s (snappy kills); also re-pushes after a kill
                    }
                }
            }
            catch (Exception ex) { _log("bridge tick failed: " + ex.Message); }
            tick++;
            try { await Task.Delay(TimeSpan.FromSeconds(3), ct); } catch { }
        }
    }

    // Keep the web's running list + `live` flag fresh via a LIGHT partial update (only runningSessions),
    // so the collections projection the desktop app last pushed is left intact.
    private async Task PushRunningAsync(Settings s)
    {
        var running = RunningSessions.Scan().Select(r => new
        {
            pid = r.Pid, tool = r.Tool, sessionId = r.SessionId, parent = r.Parent,
            startedAt = r.StartedAt, cwd = r.Cwd,
            title = (string?)null, collection = (string?)null,
            realTitle = RealTitle(r.Tool, r.SessionId), preview = (string?)null,
        }).OrderByDescending(r => r.startedAt, StringComparer.Ordinal).ToList();
        var json = JsonSerializer.Serialize(new { host = Environment.MachineName, runningSessions = running });
        var remote = $"curl -s -X POST http://127.0.0.1:{s.Port}/api/running -H 'Content-Type: application/json' --data-binary @-";
        await RunSshAsync(s.Target, remote, json);
    }

    // The tool's OWN name where it's a cheap point-lookup (codex thread title). Claude's custom title needs
    // a file read we skip on the light path — those show the desktop-app-less fallback label until it opens.
    private string? RealTitle(string tool, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase))
        {
            try { var (t, _) = ArchiveService.ReadCodexThreadTitle(_codexDbPath, id); return string.IsNullOrWhiteSpace(t) ? null : t; }
            catch { return null; }
        }
        return null;
    }

    private async Task PollAndProcessAsync(Settings s)
    {
        var outText = (await RunSshAsync(s.Target, $"curl -s http://127.0.0.1:{s.Port}/api/app-commands")).outText;
        if (string.IsNullOrWhiteSpace(outText)) return;
        List<Cmd>? cmds;
        try { cmds = JsonSerializer.Deserialize<List<Cmd>>(outText); } catch { return; }
        if (cmds is null || cmds.Count == 0) return;

        var changed = false;
        foreach (var c in cmds)
        {
            if (string.IsNullOrEmpty(c.id)) continue;
            (bool ok, string detail) res;
            switch ((c.type ?? "").ToLowerInvariant())
            {
                case "kill":
                    res = RunningSessions.Kill(c.sessionId, c.pid); changed |= res.ok; break;
                case "transcript":
                    res = (true, ReadTranscriptTail(c.tool ?? "claude", c.sessionId ?? "")); break;
                case "rename":
                    res = Rename(c.tool ?? "claude", c.sessionId ?? "", c.title ?? ""); changed |= res.ok; break;
                case "fetchfile":
                    res = await FetchUploadedFileAsync(s.Target, c.uploadId ?? "", c.filename ?? "", c.keep); break;
                case "addtocollection":
                    res = (false, "desktop app required for collection changes"); break;
                case "startmux":
                    res = StartMuxOwner(c.muxName ?? c.sessionName ?? "", c.muxCommand ?? ""); break;
                default:
                    res = (false, "unknown command"); break;
            }
            var ackJson = JsonSerializer.Serialize(new { ok = res.ok, detail = res.detail });
            await RunSshAsync(s.Target, $"curl -s -X POST http://127.0.0.1:{s.Port}/api/app-commands/{c.id}/ack -H 'Content-Type: application/json' --data-binary @-", ackJson);
        }
        if (changed) { await Task.Delay(300); await PushRunningAsync(s); }   // reflect a kill/rename fast
    }

    private (bool ok, string detail) Rename(string tool, string id, string title)
    {
        title = (title ?? "").Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) return (false, "id and title required");
        if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase)) return (false, "Codex titles its own sessions");
        return _claude.RenameSession(id, title) ? (true, "renamed") : (false, "session not found");
    }

    private (bool ok, string detail) StartMuxOwner(string name, string command)
    {
        name = (name ?? "").Trim();
        command = (command ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return (false, "missing mux session name");
        try
        {
            var muxrun = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "muxrun.cmd");
            if (!File.Exists(muxrun)) muxrun = "muxrun";
            var cmdB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(command));
            try
            {
                var wt = new ProcessStartInfo { FileName = "wt", UseShellExecute = false };
                wt.ArgumentList.Add("-w");
                wt.ArgumentList.Add(MultiplexWtWindow);
                wt.ArgumentList.Add("new-tab");
                wt.ArgumentList.Add("--title");
                wt.ArgumentList.Add(name);
                wt.ArgumentList.Add(muxrun);
                wt.ArgumentList.Add(name);
                wt.ArgumentList.Add("--cmd-b64");
                wt.ArgumentList.Add(cmdB64);
                Process.Start(wt);
            }
            catch (Exception ex)
            {
                _log("Windows Terminal launch failed, falling back to cmd.exe: " + ex.Message);
                var ownerCommand = $"\"{muxrun}\" \"{name}\" --cmd-b64 {cmdB64}";
                var psi = new ProcessStartInfo { FileName = "cmd.exe", UseShellExecute = true, Arguments = "/k " + ownerCommand };
                Process.Start(psi);
            }
            return (true, "started visible local mux owner: " + name);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private string ReadTranscriptTail(string tool, string sessionId, int maxChars = 7000)
    {
        try
        {
            List<AgentEvent> evs;
            if (string.Equals(tool, "codex", StringComparison.OrdinalIgnoreCase))
            {
                var path = ArchiveService.ReadCodexRolloutPath(_codexDbPath, sessionId);
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "(transcript not found on this PC)";
                evs = RolloutToEvents.Parse(path, 600, 6000);
            }
            else
            {
                evs = _claude.ReadHistory(sessionId, 600, 6000);
            }
            var msgs = evs.Where(e => (e.Kind == AgentEventKind.UserMessage || e.Kind == AgentEventKind.AssistantText)
                                      && !e.Delta && !string.IsNullOrWhiteSpace(e.Text)).ToList();
            if (msgs.Count == 0) return "(no messages yet)";
            var sb = new StringBuilder();
            foreach (var e in msgs.TakeLast(20))
            {
                sb.Append(e.Kind == AgentEventKind.UserMessage ? "› you:\n" : "• agent:\n");
                var t = e.Text!.Trim();
                sb.Append(t.Length > 800 ? t[..800] + "…" : t).Append("\n\n");
            }
            var s = sb.ToString().Trim();
            return s.Length <= maxChars ? s : "…\n" + s[^maxChars..];
        }
        catch (Exception ex) { return "(error reading transcript: " + ex.Message + ")"; }
    }

    // Pull an uploaded file from the VPS down to THIS PC (scp). keep -> persistent app folder; else %TEMP%.
    private static async Task<(bool ok, string detail)> FetchUploadedFileAsync(string target, string uploadId, string filename, bool keep)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) return (false, "no uploadId");
        var safe = SanitizeFilename(filename);
        var destDir = keep
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMultiplexUploads")
            : Path.Combine(Path.GetTempPath(), "multiplex-uploads");
        try { Directory.CreateDirectory(destDir); } catch { }
        var dest = Path.Combine(destDir, safe);
        if (File.Exists(dest)) dest = Path.Combine(destDir, uploadId + "_" + safe);   // no clobber
        var remote = $"{target}:multiplex-app/uploads/{uploadId}/{safe}";
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo { FileName = "scp", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-q");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("BatchMode=yes");
            psi.ArgumentList.Add("-o"); psi.ArgumentList.Add("ConnectTimeout=10");
            psi.ArgumentList.Add(remote); psi.ArgumentList.Add(dest);
            p = Process.Start(psi);
            if (p is null) return (false, "scp failed to start");
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(SshHardTimeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException) { try { p.Kill(entireProcessTree: true); } catch { } return (false, "scp timeout"); }
            var err = await errTask;
            if (p.ExitCode == 0 && File.Exists(dest)) return (true, dest);
            return (false, "scp exit " + p.ExitCode + (string.IsNullOrWhiteSpace(err) ? "" : " — " + err.Trim()));
        }
        catch (Exception ex) { try { p?.Kill(entireProcessTree: true); } catch { } return (false, "scp error: " + ex.Message); }
        finally { p?.Dispose(); }
    }

    private static string SanitizeFilename(string name)
    {
        name = Path.GetFileName(name ?? "file");
        var sb = new StringBuilder();
        foreach (var ch in name) sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' ? ch : '_');
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "file" : (s.Length > 120 ? s[..120] : s);
    }

    // Hardened ssh + hard timeout (see class note). `-n` (stdin from /dev/null) only when not feeding stdin.
    private async Task<(int code, string outText)> RunSshAsync(string target, string remoteCmd, string? stdin = null)
    {
        Process? p = null;
        try
        {
            var stdinFlag = stdin is null ? "-n " : "";
            var psi = new ProcessStartInfo
            {
                FileName = "ssh",
                Arguments = $"{stdinFlag}{SshHardenOpts} {target} \"{remoteCmd}\"",
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            p = Process.Start(psi);
            if (p is null) return (-1, "");
            if (stdin is not null) { await p.StandardInput.WriteAsync(stdin); p.StandardInput.Close(); }
            var outTask = p.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(SshHardTimeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                _log($"ssh timed out ({SshHardTimeout.TotalSeconds:0}s), tree-killed: {remoteCmd}");
                return (-2, "");
            }
            return (p.ExitCode, await outTask);
        }
        catch (Exception ex) { try { p?.Kill(entireProcessTree: true); } catch { } _log("RunSsh failed: " + ex.Message); return (-1, ""); }
        finally { p?.Dispose(); }
    }

    private sealed class Cmd
    {
        public string id { get; set; } = "";
        public string type { get; set; } = "";
        public string? sessionId { get; set; }
        public string? tool { get; set; }
        public int pid { get; set; }
        public string? uploadId { get; set; }
        public string? filename { get; set; }
        public string? title { get; set; }
        public bool keep { get; set; }
        public string? muxName { get; set; }
        public string? sessionName { get; set; }
        public string? muxCommand { get; set; }
    }
}
