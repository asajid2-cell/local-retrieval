using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.UI.Xaml;

namespace CodexLocalRetrieval_Native;

// "Start in multiplexer": resume a chat in a PC-local muxd session. muxd owns the PTY on this
// machine; harmonizerlabs.cc/multiplex and local `mux <name>` attach to that same headless session.
public sealed partial class MainPage
{
    private void ResumeInMultiplex_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) StartRemoteSession(_selected);
    }

    private async void StartRemoteSession(ArchiveSession session)
    {
        var command = _archive.BuildMultiplexCommand(session);
        if (string.IsNullOrEmpty(command))
        {
            SyncStatus.Text = "Mux session refused: this chat's id is not a safe resume token.";
            return;
        }

        var name = ArchiveService.MultiplexSessionName(session);
        var title = Trim(session.DisplayTitle, 40);
        SyncStatus.Text = $"Starting mux session \"{title}\"...";

        try
        {
            // Already in muxd? Do not inject a SECOND resume into the same transcript.
            // The visible terminal/web tab that owns it is already the place to continue.
            if (await LocalMuxdSessionAliveAsync(name))
            {
                SyncStatus.Text = $"\"{title}\" is already in multiplexer as \"{name}\".";
                return;
            }
            // Running locally (or elsewhere) right now? Don't let two copies fight over the transcript.
            if (!await ConfirmRunOrKillAsync(session)) { SyncStatus.Text = "Cancelled - already running."; return; }

            var created = await CreateLocalMuxdSessionAsync(name, command);
            if (!created.ok)
            {
                Diag.Log($"Mux create FAILED ({created.detail}) name={name}");
                SyncStatus.Text = "Could not create the mux session - see log.";
                return;
            }

            // Bump like a local resume so the chat is where you expect when you come back to the app.
            session.UpdatedAt = DateTime.UtcNow.ToString("O");
            _archive.RefreshSessions(_archive.Store.Sessions.Values);
            SessionList.SelectedItem = session;
            _ = _archive.SaveAsync();

            SyncStatus.Text = $"Mux \"{title}\" live as \"{name}\" - open it on your phone at /multiplex or attach with mux {name}.";
        }
        catch (Exception ex)
        {
            Diag.Log("StartRemoteSession FAILED " + ex);
            SyncStatus.Text = "Could not start the mux session - see log.";
        }
    }

    // --- project sync: keep the web's Projects view (harmonizerlabs.cc/multiplex) in step with the app
    // while it's open, so you can see your collections + chats and resume any of them remotely. Pushes
    // the projection now + every 30s; the web shows "synced / app live" when these land. --------------
    private DispatcherTimer? _syncTimer;
    private DispatcherTimer? _cmdTimer;
    private bool _syncPushing;
    private bool _cmdPolling;

    public void StartProjectSync()
    {
        _ = PushProjectsAsync();
        _syncTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _syncTimer.Tick -= OnSyncTick;
        _syncTimer.Tick += OnSyncTick;
        _syncTimer.Start();
        // A faster, lighter loop pulls remote commands (kill / open transcript / fetch an uploaded file)
        // so a tap on the web is actioned within a few seconds, while the heavier full-projection push
        // stays at 30s. 3s keeps kills snappy without hammering SSH (each call is hardened + timeout-capped).
        _cmdTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _cmdTimer.Tick -= OnCmdTick;
        _cmdTimer.Tick += OnCmdTick;
        _cmdTimer.Start();
    }
    private async void OnSyncTick(object? sender, object e) => await PushProjectsAsync();
    private async void OnCmdTick(object? sender, object e) => await PollCommandsAsync();

    private async Task PushProjectsAsync()
    {
        if (_syncPushing) return;
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return;
        string json;
        try
        {
            var sessions = await Task.Run(() => EnrichRunningSessionTitles(ResolveMissingSessionIds(GetRunningSessions())));
            var running = new HashSet<string>(
                sessions.Where(s => !string.IsNullOrEmpty(s.SessionId)).Select(s => s.SessionId),
                StringComparer.OrdinalIgnoreCase);
            json = _archive.BuildProjectsProjectionJson(running, sessions);
        }
        catch (Exception ex) { Diag.Log("BuildProjects failed: " + ex.Message); return; }
        _syncPushing = true;
        try
        {
            // POST over our own owner-only SSH to the VPS loopback (the API trusts loopback); the JSON
            // rides ssh stdin into curl so its quotes/backslashes never touch a shell command line.
            // Hardened + hard-timeout via RunSshAsync so a stalled push can't wedge _syncPushing either.
            var remote = $"curl -s -X POST http://127.0.0.1:{settings.MultiplexApiPort}/api/projects -H 'Content-Type: application/json' --data-binary @-";
            var (code, outText) = await RunSshAsync(target, remote, json);
            Diag.Log($"Projects sync rc={code} out={outText.Trim()}");
        }
        catch (Exception ex) { Diag.Log("PushProjects failed: " + ex.Message); }
        finally { _syncPushing = false; }
    }

    // Pull pending owner commands from the VPS (the web enqueues them) and action them on this PC. Only
    // "kill <session>" today: kill the live agent for that session (scoped to OUR claude/codex processes),
    // ack the result, and immediately re-push the projection so the web reflects the kill within seconds.
    private async Task PollCommandsAsync()
    {
        if (_cmdPolling) return;
        var settings = _archive.Store.Settings;
        var target = (settings.MultiplexSshTarget ?? "").Trim();
        if (string.IsNullOrEmpty(target)) return;
        _cmdPolling = true;
        try
        {
            var port = settings.MultiplexApiPort;
            var outText = await SshCaptureAsync(target, $"curl -s http://127.0.0.1:{port}/api/app-commands");
            if (string.IsNullOrWhiteSpace(outText)) return;
            List<AppCommand>? cmds;
            try { cmds = JsonSerializer.Deserialize<List<AppCommand>>(outText); } catch { return; }
            if (cmds is null || cmds.Count == 0) return;

            var killed = false;
            var renamed = false;
            var added = false;
            foreach (var c in cmds)
            {
                if (string.IsNullOrEmpty(c.id)) continue;
                (bool ok, string detail) res;
                if (string.Equals(c.type, "kill", StringComparison.OrdinalIgnoreCase))
                {
                    res = await Task.Run(() => KillRunningSession(c.sessionId, c.pid));
                    killed |= res.ok;
                }
                else if (string.Equals(c.type, "transcript", StringComparison.OrdinalIgnoreCase))
                {
                    var text = await Task.Run(() => ReadTranscriptTail(c.tool ?? "claude", c.sessionId ?? "", 7000));
                    res = (true, text);
                }
                else if (string.Equals(c.type, "rename", StringComparison.OrdinalIgnoreCase))
                {
                    var status = await _archive.RenameNativeByIdAsync(c.tool ?? "claude", c.sessionId ?? "", c.title ?? "");
                    var ok = status is not null && !status.Contains("failed", StringComparison.OrdinalIgnoreCase)
                                                && !status.Contains("needs", StringComparison.OrdinalIgnoreCase)
                                                && !status.Contains("not found", StringComparison.OrdinalIgnoreCase);
                    res = (ok, status ?? "renamed");
                    renamed |= ok;
                }
                else if (string.Equals(c.type, "fetchfile", StringComparison.OrdinalIgnoreCase))
                {
                    res = await FetchUploadedFileAsync(target, c.uploadId ?? "", c.filename ?? "", c.keep);
                }
                else if (string.Equals(c.type, "addtocollection", StringComparison.OrdinalIgnoreCase))
                {
                    res = await AddSessionToCollectionAsync(c.muxName ?? c.sessionName ?? "", c.collection ?? "");
                    added |= res.ok;
                }
                else if (string.Equals(c.type, "startmux", StringComparison.OrdinalIgnoreCase))
                {
                    res = await StartMuxHeadlessFromCommandAsync(c.muxName ?? c.sessionName ?? "", c.muxCommand ?? "");
                }
                else res = (false, "unknown command");
                var ackJson = JsonSerializer.Serialize(new { ok = res.ok, detail = res.detail });
                await SshSendAsync(target, $"curl -s -X POST http://127.0.0.1:{port}/api/app-commands/{c.id}/ack -H 'Content-Type: application/json' --data-binary @-", ackJson);
            }
            if (killed || renamed || added) { await Task.Delay(300); await PushProjectsAsync(); }   // reflect a kill/rename/add fast
        }
        catch (Exception ex) { Diag.Log("PollCommands failed: " + ex.Message); }
        finally { _cmdPolling = false; }
    }

    private sealed class AppCommand
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
        public string? collection { get; set; }
    }

    private async Task<(bool ok, string detail)> StartMuxHeadlessFromCommandAsync(string name, string command)
    {
        name = (name ?? "").Trim();
        command = (command ?? "").Trim();
        if (string.IsNullOrEmpty(name)) return (false, "missing mux session name");
        var created = await CreateLocalMuxdSessionAsync(name, command);
        return created.ok ? (true, "started PC-local mux session: " + name) : created;
    }

    // Web "Add to collection": file a multiplex session's chat into a (new or existing) collection. The web
    // only allows this while the app is live (the app OWNS collections, so this can't drift out of sync). We
    // resolve the mux session name back to its ArchiveSession, then reuse the tested AddToCollectionAsync and
    // re-push the projection so the web reflects it.
    private async Task<(bool ok, string detail)> AddSessionToCollectionAsync(string muxName, string collection)
    {
        muxName = (muxName ?? "").Trim(); collection = (collection ?? "").Trim();
        if (string.IsNullOrEmpty(muxName) || string.IsNullOrEmpty(collection)) return (false, "missing session or collection");
        ArchiveSession? session = null;
        foreach (var s in _archive.Store.Sessions.Values)
            if (string.Equals(ArchiveService.MultiplexSessionName(s), muxName, StringComparison.OrdinalIgnoreCase)) { session = s; break; }
        if (session is null) return (false, "no chat matches “" + muxName + "”");
        try { await _archive.AddToCollectionAsync(session, collection); return (true, "added to " + collection); }
        catch (Exception ex) { return (false, "add failed: " + ex.Message); }
    }

    // Pull an uploaded file from the VPS down to THIS PC (where the agent runs), so the web can attach its
    // local path to a prompt. scp handles binary cleanly. `keep` files go to a persistent app folder; the
    // rest go to %TEMP% (Windows clears it, so they "disappear" on restart — the "leave" default).
    private static async Task<(bool ok, string detail)> FetchUploadedFileAsync(string target, string uploadId, string filename, bool keep = false)
    {
        if (string.IsNullOrWhiteSpace(uploadId)) return (false, "no uploadId");
        var safe = SanitizeFilename(filename);
        var destDir = keep
            ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMultiplexUploads")
            : System.IO.Path.Combine(System.IO.Path.GetTempPath(), "multiplex-uploads");
        try { System.IO.Directory.CreateDirectory(destDir); } catch { }
        var dest = System.IO.Path.Combine(destDir, safe);
        if (System.IO.File.Exists(dest)) dest = System.IO.Path.Combine(destDir, uploadId + "_" + safe);   // no clobber
        var remote = $"{target}:multiplex-app/uploads/{uploadId}/{safe}";
        try
        {
            var psi = new ProcessStartInfo { FileName = "scp", UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            psi.ArgumentList.Add("-q"); psi.ArgumentList.Add(remote); psi.ArgumentList.Add(dest);
            using var p = Process.Start(psi);
            if (p is null) return (false, "scp failed to start");
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0 && System.IO.File.Exists(dest)) return (true, dest);
            return (false, "scp exit " + p.ExitCode + (string.IsNullOrWhiteSpace(err) ? "" : " — " + err.Trim()));
        }
        catch (Exception ex) { return (false, "scp error: " + ex.Message); }
    }

    private static string SanitizeFilename(string name)
    {
        name = System.IO.Path.GetFileName(name ?? "file");
        var sb = new StringBuilder();
        foreach (var ch in name) sb.Append(char.IsLetterOrDigit(ch) || ch == '.' || ch == '-' || ch == '_' ? ch : '_');
        var s = sb.ToString();
        return string.IsNullOrEmpty(s) ? "file" : (s.Length > 120 ? s.Substring(0, 120) : s);
    }

    // SSH hardening so a single stalled connection can NEVER wedge the bridge again (the bug that left
    // _cmdPolling stuck true and the kill/transcript queue undrained for hours):
    //  - BatchMode=yes      never block on a prompt (host-key / password) — key-only, fail fast.
    //  - ConnectTimeout     cap the TCP/handshake wait.
    //  - ServerAlive*       drop a connection that goes silent mid-session (~45s).
    // Plus a HARD wall-clock timeout in C# that tree-kills the process if it overruns, so WaitForExit can
    // never hang forever (and no orphan ssh.exe piles up). `-n` (stdin from /dev/null) is added for calls
    // with no stdin — without it, `ssh host "cmd"` waits forever for EOF on the GUI app's never-closing
    // inherited stdin, which is exactly what hung the poll while the stdin-piped push kept working.
    private const string SshHardenOpts = "-o BatchMode=yes -o ConnectTimeout=10 -o ServerAliveInterval=15 -o ServerAliveCountMax=3";
    private static readonly TimeSpan SshHardTimeout = TimeSpan.FromSeconds(30);

    // Run ssh with the hardened options + a hard timeout. Returns (exitCode, stdout); -2 = timed out (tree-killed).
    private static async Task<(int code, string outText)> RunSshAsync(string target, string remoteCmd, string? stdin = null)
    {
        Process? p = null;
        try
        {
            var stdinFlag = stdin is null ? "-n " : "";   // -n only when we're NOT feeding stdin
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
            using var cts = new System.Threading.CancellationTokenSource(SshHardTimeout);
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                Diag.Log($"ssh timed out ({SshHardTimeout.TotalSeconds:0}s), tree-killed: {remoteCmd}");
                return (-2, "");
            }
            var o = await outTask;
            return (p.ExitCode, o);
        }
        catch (Exception ex) { try { p?.Kill(entireProcessTree: true); } catch { } Diag.Log("RunSsh failed: " + ex.Message); return (-1, ""); }
        finally { p?.Dispose(); }
    }

    // Run a remote command over our owner-only SSH and capture stdout.
    private static async Task<string> SshCaptureAsync(string target, string remoteCmd)
        => (await RunSshAsync(target, remoteCmd)).outText;

    // Run a remote command and feed JSON on stdin (so quotes/backslashes never touch a shell command line).
    private static async Task SshSendAsync(string target, string remoteCmd, string stdin)
        => await RunSshAsync(target, remoteCmd, stdin);

    private const string MuxdTaskName = "MuxdSessionHost";

    private static async Task<string> LocalMuxdRequestAsync(object message, bool retryStart = true)
    {
        try { return await LocalMuxdRequestOnceAsync(message); }
        catch (Exception ex) when (retryStart)
        {
            Diag.Log("muxd local request failed; checking scheduled task: " + ex.Message);
            var task = await EnsureMuxdScheduledTaskRunningAsync();
            if (!task.ok) throw new InvalidOperationException("muxd unavailable; " + task.detail, ex);
            await Task.Delay(1200);
            return await LocalMuxdRequestOnceAsync(message);
        }
    }

    private static async Task<string> LocalMuxdRequestOnceAsync(object message)
    {
        using var ws = new ClientWebSocket();
        using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
        await ws.ConnectAsync(new Uri("ws://127.0.0.1:7699"), cts.Token);

        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message));
        await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, cts.Token);

        using var ms = new MemoryStream();
        var buffer = new byte[16 * 1024];
        WebSocketReceiveResult result;
        do
        {
            result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) break;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static async Task<(bool ok, string detail)> EnsureMuxdScheduledTaskRunningAsync()
    {
        var query = await RunProcessCaptureAsync("schtasks", "/Query", "/TN", MuxdTaskName, "/FO", "CSV", "/NH");
        if (query.code != 0)
            return (false, "scheduled task query failed: " + (query.errText + " " + query.outText).Trim());
        await EnsureMuxdScheduledTaskHiddenAsync();
        if (query.outText.Contains("Running", StringComparison.OrdinalIgnoreCase))
            return (true, "scheduled task already running");

        var run = await RunProcessCaptureAsync("schtasks", "/Run", "/TN", MuxdTaskName);
        if (run.code == 0) return (true, "scheduled task started");
        return (false, "scheduled task start failed: " + (run.errText + " " + run.outText).Trim());
    }

    private static async Task EnsureMuxdScheduledTaskHiddenAsync()
    {
        try
        {
            var xml = await RunProcessCaptureAsync("schtasks", "/Query", "/TN", MuxdTaskName, "/XML");
            if (xml.code != 0) return;
            if (!MuxdTaskLaunch.TryGetHiddenPythonAction(xml.outText, File.Exists, out var exe, out var args)) return;
            var taskRun = QuoteTaskArg(exe) + (string.IsNullOrWhiteSpace(args) ? "" : " " + args);
            var change = await RunProcessCaptureAsync("schtasks", "/Change", "/TN", MuxdTaskName, "/TR", taskRun);
            if (change.code != 0)
                Diag.Log("Muxd task hidden-launch update failed: " + (change.errText + " " + change.outText).Trim());
        }
        catch (Exception ex) { Diag.Log("Muxd task hidden-launch check failed: " + ex.Message); }
    }

    private static string QuoteTaskArg(string value)
        => "\"" + (value ?? "").Replace("\"", "\\\"") + "\"";

    private static async Task<(int code, string outText, string errText)> RunProcessCaptureAsync(string file, params string[] args)
    {
        Process? p = null;
        try
        {
            var psi = new ProcessStartInfo { FileName = file, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            p = Process.Start(psi);
            if (p is null) return (-1, "", "process did not start");
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return (-2, "", "process timed out");
            }
            return (p.ExitCode, await outTask, await errTask);
        }
        catch (Exception ex) { try { p?.Kill(entireProcessTree: true); } catch { } return (-1, "", ex.Message); }
        finally { p?.Dispose(); }
    }

    private static async Task<bool> LocalMuxdSessionAliveAsync(string name)
    {
        try
        {
            var text = await LocalMuxdRequestAsync(new { t = "ls" });
            using var doc = JsonDocument.Parse(text);
            foreach (var s in doc.RootElement.GetProperty("list").EnumerateArray())
                if (string.Equals(s.GetProperty("name").GetString(), name, StringComparison.OrdinalIgnoreCase))
                    return s.TryGetProperty("alive", out var alive) && alive.ValueKind == JsonValueKind.True;
        }
        catch { }
        return false;
    }

    private static async Task<(bool ok, string detail)> CreateLocalMuxdSessionAsync(string name, string command)
    {
        try
        {
            var cap = await EnsureLocalMuxdCapabilityAsync("create");
            if (!cap.ok) return cap;
            var text = await LocalMuxdRequestAsync(new { t = "create", s = name, cmd = command, cols = 140, rows = 40 });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "err")
                return (false, doc.RootElement.TryGetProperty("m", out var m) ? m.GetString() ?? "muxd error" : "muxd error");
            if (doc.RootElement.TryGetProperty("t", out t) && t.GetString() == "created")
                return (true, text);
            return (false, "unexpected muxd create response: " + Trim(text, 160));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<(bool ok, string detail)> EnsureLocalMuxdCapabilityAsync(string cap)
    {
        try
        {
            var text = await LocalMuxdRequestAsync(new { t = "info" });
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("t", out var t) && t.GetString() == "err")
            {
                var msg = root.TryGetProperty("m", out var m) ? m.GetString() : null;
                return (false, "running muxd does not advertise protocol info"
                    + (string.IsNullOrWhiteSpace(msg) ? "" : " (" + msg + ")")
                    + "; restart MuxdSessionHost to load the current muxd");
            }
            if (!root.TryGetProperty("t", out t) || t.GetString() != "info")
                return (false, "running muxd returned an unexpected protocol response; restart MuxdSessionHost to load the current muxd");
            if (root.TryGetProperty("caps", out var caps) && caps.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in caps.EnumerateArray())
                    if (string.Equals(c.GetString(), cap, StringComparison.OrdinalIgnoreCase))
                        return (true, "ok");
            }
            var protocol = root.TryGetProperty("protocol", out var p) ? p.ToString() : "?";
            return (false, $"running muxd protocol {protocol} does not advertise '{cap}'; restart MuxdSessionHost to load the current muxd");
        }
        catch (JsonException ex) { return (false, "muxd protocol response was not JSON: " + ex.Message); }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private static async Task<(bool ok, string detail)> DeleteLocalMuxdSessionAsync(string name)
    {
        try
        {
            var cap = await EnsureLocalMuxdCapabilityAsync("kill");
            if (!cap.ok) return cap;
            var text = await LocalMuxdRequestAsync(new { t = "kill", s = name });
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("t", out var t) && t.GetString() == "killed")
                return (true, "killed");
            if (doc.RootElement.TryGetProperty("t", out t) && t.GetString() == "err")
                return (false, doc.RootElement.TryGetProperty("m", out var m) ? m.GetString() ?? "muxd error" : "muxd error");
            return (false, "unexpected muxd kill response: " + Trim(text, 160));
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

}
