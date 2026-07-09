using System.Diagnostics;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

// Reopens a stored chat where the user actually works — in VS Code (the same editor session) or a fresh
// terminal — so the app is a home base you return to, not 15 tabs you're afraid to close. Everything is
// launched ON this PC (that's where the editor/CLI live); gated by CLR_REMOTE_ALLOW_LAUNCH.
public sealed class SessionLauncher
{
    private readonly string _claudeExe;
    private readonly string _codexExe;
    private readonly bool _allow;
    private readonly string? _wt;
    private readonly SessionLaunchGovernor _launchGovernor;

    public SessionLauncher(
        string claudeExe,
        string codexExe,
        bool allowLaunch,
        Func<string, bool>? isSessionLive = null,
        SessionLaunchClaims.Options? claimOptions = null,
        SessionLaunchGovernor? launchGovernor = null)
    {
        _claudeExe = claudeExe;
        _codexExe = codexExe;
        _allow = allowLaunch;
        _wt = Which("wt.exe");
        _launchGovernor = launchGovernor ?? new SessionLaunchGovernor(new SessionLaunchGovernorOptions(claimOptions, IsSessionLive: isSessionLive));
    }

    public bool Enabled => _allow;

    public (bool ok, string message) Open(string source, string id, string target, string? cwd, IEnumerable<string>? aliases = null)
    {
        if (!_allow)
        {
            _launchGovernor.RecordRefused(LaunchRequest(source, id, aliases, target), "Server reopen refused because launch is disabled.");
            return (false, "Reopening is disabled on this server (set CLR_REMOTE_ALLOW_LAUNCH=1).");
        }
        if (string.IsNullOrWhiteSpace(id))
        {
            _launchGovernor.RecordRefused(LaunchRequest(source, id, aliases, target), "Server reopen refused because the session id was missing.");
            return (false, "missing session id");
        }
        var dir = !string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd)
            ? cwd!
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        try
        {
            if (target == "vscode")
            {
                // Claude has a deep-link straight to the session; for Codex, open the workspace folder so
                // its sidebar lists the session.
                SessionLaunchLease? lease = null;
                if (source == "claude")
                {
                    var request = LaunchRequest(source, id, aliases, target, "server Claude VS Code deeplink", "server.open.started.vscode");
                    if (!_launchGovernor.TryAcquire(request, out lease, out var leaseDetail))
                        return (false, leaseDetail);
                }
                var uri = source == "claude"
                    ? $"vscode://anthropic.claude-code/open?session={Uri.EscapeDataString(id)}"
                    : $"vscode://file/{dir.Replace('\\', '/')}";
                using (lease)
                {
                    try
                    {
                        OpenUri(uri);
                        if (lease is not null)
                            lease.MarkStarted("Opening Claude chat in VS Code.");
                        else
                            RecordSessionEvent(id, aliases, source, "server.open.started.workspace", "Opening workspace in VS Code.", target: target);
                    }
                    catch (Exception ex)
                    {
                        lease?.MarkFailed(ex.Message);
                        throw;
                    }
                }
                return (true, source == "claude" ? "Opening the chat in VS Code…" : "Opening the workspace in VS Code…");
            }
            // terminal
            var terminalRequest = LaunchRequest(source, id, aliases, target, "server terminal resume", "server.open.started.terminal");
            if (!_launchGovernor.TryAcquire(terminalRequest, out var terminalLease, out var terminalLeaseDetail))
                return (false, terminalLeaseDetail);
            var exe = source == "claude" ? _claudeExe : _codexExe;
            var args = source == "claude" ? $"--resume {id}" : $"resume {id}";
            using (terminalLease)
            {
                try
                {
                    OpenTerminal(dir, exe, args);
                    terminalLease?.MarkStarted("Opening terminal resume from server.");
                }
                catch (Exception ex)
                {
                    terminalLease?.MarkFailed(ex.Message);
                    throw;
                }
            }
            return (true, "Opening a terminal…");
        }
        catch (Exception ex)
        {
            RecordSessionEvent(id, aliases, source, "server.open.failed", ex.Message, "error", target);
            return (false, "couldn't launch: " + ex.Message);
        }
    }

    private static SessionLaunchRequest LaunchRequest(
        string? source,
        string? id,
        IEnumerable<string>? aliases,
        string? target,
        string reason = "server session open",
        string startedKind = "server.open.started",
        string failedKind = "server.open.failed")
        => new(
            id,
            aliases,
            string.IsNullOrWhiteSpace(source) ? "codex" : source!,
            "server",
            reason,
            "server.open.refused",
            startedKind,
            failedKind,
            Details: string.IsNullOrWhiteSpace(target)
                ? null
                : new Dictionary<string, string> { ["target"] = target! });

    // Spin up the FULL desktop app on this PC on demand (the headless server stays light by default).
    // Idempotent: if it's already running, just say so. Path: CLR_DESKTOP_APP_EXE or the install default.
    public (bool ok, string message) OpenDesktopApp()
    {
        if (!_allow) return (false, "Launching is disabled on this server (set CLR_REMOTE_ALLOW_LAUNCH=1).");
        var exe = Environment.GetEnvironmentVariable("CLR_DESKTOP_APP_EXE");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                               "Programs", "CodexLocalRetrieval", "CodexLocalRetrieval.Native.exe");
        if (!File.Exists(exe)) return (false, "Desktop app not found (install it, or set CLR_DESKTOP_APP_EXE).");
        try
        {
            if (Process.GetProcessesByName("CodexLocalRetrieval.Native").Length > 0)
                return (true, "The desktop app is already running on the PC.");
            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,   // so its relative asset/icon loads resolve
                UseShellExecute = true
            });
            return (true, "Launching the desktop app on the PC…");
        }
        catch (Exception ex) { return (false, "couldn't launch the desktop app: " + ex.Message); }
    }

    private static void OpenUri(string uri) =>
        Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });

    private void OpenTerminal(string dir, string exe, string args)
    {
        if (_wt is not null)
        {
            // Windows Terminal opens in the folder running the resume command directly.
            Process.Start(new ProcessStartInfo
            {
                FileName = _wt,
                Arguments = $"-d \"{dir}\" \"{exe}\" {args}",
                UseShellExecute = true
            });
            return;
        }
        // Fallback: a PowerShell window in the folder, kept open.
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoExit -NoProfile -Command \"Set-Location -LiteralPath '{dir}'; & '{exe}' {args}\"",
            UseShellExecute = true
        });
    }

    private static string? Which(string exe)
    {
        try
        {
            foreach (var p in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(p)) continue;
                var full = Path.Combine(p, exe);
                if (File.Exists(full)) return full;
            }
        }
        catch { }
        return null;
    }

    private static void RecordSessionEvent(
        string? sessionId,
        IEnumerable<string>? aliases,
        string? tool,
        string kind,
        string summary,
        string severity = "info",
        string? target = null)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0) return;
            if (!ids.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) ids.Add(id);
        }
        Add(sessionId);
        if (aliases is not null)
            foreach (var alias in aliases) Add(alias);
        var details = string.IsNullOrWhiteSpace(target)
            ? null
            : new Dictionary<string, string> { ["target"] = target! };
        var ev = SessionEventLedger.Create(
            kind,
            summary,
            sessionId,
            tool,
            source: "server",
            severity: severity,
            details: details,
            sessionIds: ids);
        SessionEventLedger.AppendBestEffortQueued(ev);
    }
}
