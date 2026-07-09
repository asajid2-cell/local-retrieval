using System.Diagnostics;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

// Drives a live Claude turn: `claude --resume <id> -p <prompt> --output-format stream-json`, one
// process per turn (the session file persists across turns). Stdout lines are mapped to AgentEvents
// via ClaudeStreamMapper and pushed to onEvent. acceptEdits lets it run without an interactive TTY.
public sealed class ClaudeLiveDriver
{
    private readonly string _exe;
    private readonly SessionLaunchGovernor _launchGovernor;

    public ClaudeLiveDriver(
        string? exe = null,
        Func<string, bool>? isSessionLive = null,
        SessionLaunchClaims.Options? claimOptions = null,
        SessionLaunchGovernor? launchGovernor = null)
    {
        _exe = exe ?? Resolve();
        _launchGovernor = launchGovernor ?? new SessionLaunchGovernor(new SessionLaunchGovernorOptions(claimOptions, IsSessionLive: isSessionLive));
    }

    public bool Available => File.Exists(_exe) || _exe == "claude";

    public Process StartTurn(string? sessionId, string cwd, string prompt, Func<AgentEvent, Task> onEvent, CancellationToken ct, string permissionMode = "acceptEdits", IEnumerable<string>? aliases = null)
    {
        SessionLaunchLease? lease = null;
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            var request = LaunchRequest(sessionId, aliases, permissionMode);
            if (!_launchGovernor.TryAcquire(request, out lease, out var leaseDetail))
                throw new InvalidOperationException(leaseDetail);
        }

        // only the known-safe set; default acceptEdits. "bypassPermissions" is reachable only for an
        // owner-signed auto command (the WS gates it on CommandSigner.Verify), never for an unsigned one.
        if (permissionMode is not ("acceptEdits" or "bypassPermissions" or "default" or "plan")) permissionMode = "acceptEdits";
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Directory.Exists(cwd) ? cwd : Environment.CurrentDirectory,
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add(prompt);
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");
        psi.ArgumentList.Add("--permission-mode");
        psi.ArgumentList.Add(permissionMode);
        if (!string.IsNullOrEmpty(sessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(sessionId);
        }

        Process proc;
        try
        {
            proc = Process.Start(psi) ?? throw new InvalidOperationException("could not start Claude process");
        }
        catch
        {
            lease?.MarkFailed("Claude live turn process failed to start.");
            lease?.Dispose();
            throw;
        }
        proc.StandardInput.Close(); // we never write; closing avoids a blocked-stdin wait
        if (lease is not null)
        {
            lease.MarkStarted("Claude live turn process started.", new Dictionary<string, string> { ["pid"] = proc.Id.ToString(), ["permissionMode"] = permissionMode }, retainUntilExpiry: false);
            var liveLease = lease;
            _ = Task.Run(async () =>
            {
                try { await proc.WaitForExitAsync(CancellationToken.None); }
                catch { }
                finally
                {
                    RecordSessionEvent(sessionId, aliases, "claude.turn.exited", "Claude live turn process exited.", details: new Dictionary<string, string> { ["pid"] = proc.Id.ToString(), ["exitCode"] = SafeExitCode(proc) });
                    liveLease.Dispose();
                }
            });
        }
        else
        {
            RecordSessionEvent(sessionId, aliases, "claude.turn.started", "Claude live turn process started.", details: new Dictionary<string, string> { ["pid"] = proc.Id.ToString(), ["permissionMode"] = permissionMode });
        }

        _ = Task.Run(async () =>
        {
            try
            {
                string? line;
                while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
                    foreach (var ev in ClaudeStreamMapper.Map(line))
                        await onEvent(ev);
            }
            catch (Exception ex) { try { await onEvent(AgentEvent.Err("claude stream error: " + ex.Message)); } catch { } }
            finally { try { await onEvent(AgentEvent.Stat("idle")); } catch { } }
        }, ct);

        return proc;
    }

    private static string Resolve()
    {
        var env = Environment.GetEnvironmentVariable("CLR_CLAUDE_EXE");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "claude.exe");
        if (File.Exists(local)) return local;
        var prog = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude", "claude.exe");
        if (File.Exists(prog)) return prog;
        return "claude"; // last resort: rely on PATH
    }

    private static SessionLaunchRequest LaunchRequest(string? sessionId, IEnumerable<string>? aliases, string permissionMode)
        => new(
            sessionId,
            aliases,
            "claude",
            "server",
            "server Claude live turn",
            "claude.turn.refused.claim",
            "claude.turn.started",
            "claude.turn.failed",
            Details: new Dictionary<string, string> { ["permissionMode"] = permissionMode });

    private static void RecordSessionEvent(
        string? sessionId,
        IEnumerable<string>? aliases,
        string kind,
        string summary,
        string severity = "info",
        string? permissionMode = null,
        IReadOnlyDictionary<string, string>? details = null)
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
        Dictionary<string, string>? merged = null;
        if (details is not null) merged = new Dictionary<string, string>(details, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(permissionMode))
        {
            merged ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            merged["permissionMode"] = permissionMode!;
        }
        var ev = SessionEventLedger.Create(
            kind,
            summary,
            sessionId,
            "claude",
            source: "server",
            severity: severity,
            details: merged,
            sessionIds: ids);
        SessionEventLedger.AppendBestEffortQueued(ev);
    }

    private static string SafeExitCode(Process proc)
    {
        try { return proc.HasExited ? proc.ExitCode.ToString() : ""; }
        catch { return ""; }
    }
}
