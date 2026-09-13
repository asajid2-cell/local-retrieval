using System.Diagnostics;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Server;

// Launches only descriptors resolved from trusted local session metadata. Browser input never reaches
// executable selection, working-directory selection, or command-line construction.
public sealed class SessionLauncher
{
    private readonly string _claudeExe;
    private readonly string _codexExe;
    private readonly string _gatewayCliScript;
    private readonly string _cmdExe;
    private readonly bool _allow;
    private readonly string? _wt;
    private readonly SessionLaunchGovernor _launchGovernor;
    private readonly Func<ProcessStartInfo, Process?> _startProcess;
    private readonly SessionOwnerRecords.Options? _ownerRecordOptions;
    private readonly string _codexAccountsRoot;

    public SessionLauncher(
        string claudeExe,
        string codexExe,
        bool allowLaunch,
        Func<string, bool>? isSessionLive = null,
        SessionLaunchClaims.Options? claimOptions = null,
        SessionLaunchGovernor? launchGovernor = null,
        Func<ProcessStartInfo, Process?>? startProcess = null,
        string? windowsTerminal = null,
        bool discoverWindowsTerminal = true,
        SessionOwnerRecords.Options? ownerRecordOptions = null,
        string? gatewayCliScript = null,
        string? cmdExe = null,
        string? codexAccountsRoot = null)
    {
        _ownerRecordOptions = ownerRecordOptions;
        _codexAccountsRoot = codexAccountsRoot ?? ArchiveService.CodexAccountsRoot;
        _claudeExe = claudeExe;
        _codexExe = codexExe;
        _gatewayCliScript = gatewayCliScript ?? ArchiveService.ResolveGatewayCliScript();
        _cmdExe = cmdExe ?? ArchiveService.ResolveCmdExe();
        _allow = allowLaunch;
        _wt = windowsTerminal ?? (discoverWindowsTerminal ? Which("wt.exe") : null);
        _launchGovernor = launchGovernor
            ?? new SessionLaunchGovernor(new SessionLaunchGovernorOptions(claimOptions, IsSessionLive: isSessionLive));
        _startProcess = startProcess ?? (psi =>
            Process.Start(psi) ?? throw new InvalidOperationException("process start returned no process"));
    }

    public bool Enabled => _allow;

    public (bool ok, string message) Open(TrustedSessionLaunch session, SessionOpenTarget target)
    {
        var id = (session.SessionId ?? "").Trim();
        var aliases = session.Aliases ?? Array.Empty<string>();
        var targetName = TargetName(target);
        if (!TryToolName(session.Tool, out var source))
        {
            _launchGovernor.RecordRefused(
                LaunchRequest("invalid", id, aliases, targetName),
                "Server reopen refused because the trusted tool was invalid.");
            return (false, "invalid trusted session tool");
        }
        if (!_allow)
        {
            _launchGovernor.RecordRefused(
                LaunchRequest(source, id, aliases, targetName),
                "Server reopen refused because launch is disabled.");
            return (false, "Reopening is disabled on this server (set CLR_REMOTE_ALLOW_LAUNCH=1).");
        }
        if (id.Length == 0)
        {
            _launchGovernor.RecordRefused(
                LaunchRequest(source, id, aliases, targetName),
                "Server reopen refused because the session id was missing.");
            return (false, "missing session id");
        }
        if (!ArchiveService.IsResumableId(id))
        {
            _launchGovernor.RecordRefused(
                LaunchRequest(source, id, aliases, targetName),
                "Server reopen refused because the trusted session id was invalid.");
            return (false, "invalid trusted session id");
        }
        if (!Enum.IsDefined(target))
        {
            _launchGovernor.RecordRefused(
                LaunchRequest(source, id, aliases, targetName),
                "Server reopen refused because the target was invalid.");
            return (false, "invalid launch target");
        }

        var gateway = ArchiveService.IsGatewayLaunchMode(session.LaunchMode);
        if (gateway && session.Tool != SessionTool.Claude)
        {
            _launchGovernor.RecordRefused(
                LaunchRequest(source, id, aliases, targetName),
                "Server reopen refused because Gateway (cc) can resume Claude transcripts only.");
            return (false, "Gateway (cc) can resume Claude transcripts only; use native Codex resume.");
        }

        var dir = (session.WorkingDirectory ?? "").Trim();
        if (!Path.IsPathRooted(dir) || !Directory.Exists(dir))
        {
            _launchGovernor.RecordRefused(
                LaunchRequest(source, id, aliases, targetName),
                "Server reopen refused because the trusted workspace was unavailable.");
            return (false, "trusted workspace is missing or unavailable");
        }
        dir = Path.GetFullPath(dir);

        try
        {
            if (target == SessionOpenTarget.VsCode)
                return OpenVsCode(session.Tool, source, id, aliases, dir, targetName);

            var request = LaunchRequest(
                source,
                id,
                aliases,
                targetName,
                "server terminal resume",
                "server.open.started.terminal");
            if (!_launchGovernor.TryAcquire(request, out var lease, out var leaseDetail))
                return (false, leaseDetail);

            var exe = gateway
                ? _cmdExe
                : session.Tool == SessionTool.Claude ? _claudeExe : _codexExe;
            var arguments = gateway
                ? new[] { "/c", _gatewayCliScript, "--resume", id }
                : session.Tool == SessionTool.Claude
                    ? new[] { "--resume", id }
                    : new[] { "resume", id };
            IReadOnlyDictionary<string, string>? environment =
                session.Tool == SessionTool.Codex
                    && ArchiveService.TryGetCodexAccountHome(
                        session.SourcePath,
                        _codexAccountsRoot,
                        out var accountHome)
                    ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["CODEX_HOME"] = accountHome,
                    }
                    : null;
            using (lease)
            {
                try
                {
                    var wrapper = OpenTerminal(dir, exe, arguments, environment);
                    lease?.MarkStarted("Opening terminal resume from server.");
                    RecordOwner(id, aliases, wrapper, "server");
                }
                catch (Exception ex)
                {
                    lease?.MarkFailed(ex.Message);
                    throw;
                }
            }
            return (true, "Opening a terminal...");
        }
        catch (Exception ex)
        {
            RecordSessionEvent(id, aliases, source, "server.open.failed", ex.Message, "error", targetName);
            return (false, "couldn't launch: " + ex.Message);
        }
    }

    private (bool ok, string message) OpenVsCode(
        SessionTool tool,
        string source,
        string id,
        IReadOnlyList<string> aliases,
        string dir,
        string targetName)
    {
        SessionLaunchLease? lease = null;
        if (tool == SessionTool.Claude)
        {
            var request = LaunchRequest(
                source,
                id,
                aliases,
                targetName,
                "server Claude VS Code deeplink",
                "server.open.started.vscode");
            if (!_launchGovernor.TryAcquire(request, out lease, out var leaseDetail))
                return (false, leaseDetail);
        }

        var uri = tool == SessionTool.Claude
            ? $"vscode://anthropic.claude-code/open?session={Uri.EscapeDataString(id)}"
            : WorkspaceUri(dir);
        using (lease)
        {
            try
            {
                OpenUri(uri);
                if (lease is not null)
                    lease.MarkStarted("Opening Claude chat in VS Code.");
                else
                    RecordSessionEvent(
                        id,
                        aliases,
                        source,
                        "server.open.started.workspace",
                        "Opening workspace in VS Code.",
                        target: targetName);
            }
            catch (Exception ex)
            {
                lease?.MarkFailed(ex.Message);
                throw;
            }
        }
        return (true, tool == SessionTool.Claude
            ? "Opening the chat in VS Code..."
            : "Opening the workspace in VS Code...");
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

    public (bool ok, string message) OpenDesktopApp()
    {
        if (!_allow)
            return (false, "Launching is disabled on this server (set CLR_REMOTE_ALLOW_LAUNCH=1).");
        var exe = Environment.GetEnvironmentVariable("CLR_DESKTOP_APP_EXE");
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            exe = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs",
                "MUX",
                "CodexLocalRetrieval.Native.exe");
        if (!File.Exists(exe))
            return (false, "Desktop app not found (install it, or set CLR_DESKTOP_APP_EXE).");
        try
        {
            if (Process.GetProcessesByName("CodexLocalRetrieval.Native").Length > 0)
                return (true, "The desktop app is already running on the PC.");
            _startProcess(new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true,
            });
            return (true, "Launching the desktop app on the PC...");
        }
        catch (Exception ex)
        {
            return (false, "couldn't launch the desktop app: " + ex.Message);
        }
    }

    // Best-effort note of the wrapper process this server started for a session, and the point where that
    // wrapper is put into a named job object [F#7] for a race-free later Kill. A wrapper is not the agent (see
    // SessionOwnerRecords); a failed write or job assignment is logged and never fails a launch.
    private void RecordOwner(string id, IReadOnlyList<string> aliases, Process? wrapper, string transport)
    {
        try
        {
            if (!SessionOwnerRecords.TryWriteForProcess(id, aliases, wrapper, transport, out var detail, _ownerRecordOptions))
                Console.Error.WriteLine($"owner record not written ({transport}): {detail}");
        }
        catch (Exception ex) { Console.Error.WriteLine("owner record write threw: " + ex.Message); }
    }

    private void OpenUri(string uri) =>
        _startProcess(new ProcessStartInfo { FileName = uri, UseShellExecute = true });

    private Process? OpenTerminal(
        string dir,
        string exe,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (_wt is not null)
        {
            var psi = new ProcessStartInfo
            {
                FileName = _wt,
                UseShellExecute = true,
            };
            ApplyLaunchEnvironment(psi, environment);
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(dir);
            psi.ArgumentList.Add(exe);
            foreach (var argument in arguments) psi.ArgumentList.Add(argument);
            return _startProcess(psi);
        }

        var fallback = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = dir,
            UseShellExecute = true,
        };
        ApplyLaunchEnvironment(fallback, environment);
        foreach (var argument in arguments) fallback.ArgumentList.Add(argument);
        return _startProcess(fallback);
    }

    private static void ApplyLaunchEnvironment(
        ProcessStartInfo psi,
        IReadOnlyDictionary<string, string>? environment)
    {
        if (environment is null) return;
        foreach (var (name, value) in environment)
            psi.Environment[name] = value;
    }

    private static string WorkspaceUri(string dir) =>
        "vscode://file" + new Uri(Path.GetFullPath(dir) + Path.DirectorySeparatorChar).AbsolutePath.TrimEnd('/');

    private static bool TryToolName(SessionTool tool, out string name)
    {
        switch (tool)
        {
            case SessionTool.Codex:
                name = "codex";
                return true;
            case SessionTool.Claude:
                name = "claude";
                return true;
            default:
                name = "";
                return false;
        }
    }

    private static string TargetName(SessionOpenTarget target) =>
        target switch
        {
            SessionOpenTarget.Terminal => "terminal",
            SessionOpenTarget.VsCode => "vscode",
            _ => "invalid",
        };

    private static string? Which(string exe)
    {
        try
        {
            foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(path)) continue;
                var full = Path.Combine(path, exe);
                if (File.Exists(full)) return full;
            }
        }
        catch
        {
        }
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

public enum SessionTool
{
    Codex,
    Claude,
}

public enum SessionOpenTarget
{
    Terminal,
    VsCode,
}

public sealed record TrustedSessionLaunch(
    string SessionId,
    SessionTool Tool,
    string WorkingDirectory,
    IReadOnlyList<string> Aliases,
    string LaunchMode = ArchiveService.NativeLaunchMode)
{
    // Absolute transcript path from trusted archive metadata. Used only to select the owning Codex home.
    public string? SourcePath { get; init; }
}
