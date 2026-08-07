using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

public static class AdoptedMuxLauncher
{
    public sealed record Request(
        string MuxName,
        int Pid,
        string SessionId,
        IReadOnlyList<string> Aliases,
        string Tool,
        string Command,
        string Workspace);

    public sealed record Result(bool Ok, string Detail);

    internal sealed record Dependencies(
        Func<(bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail)> ScanRunning,
        Func<Task<(bool Ok, string Xml, string Detail)>> ReadMuxdTask,
        Func<ProcessStartInfo, bool> StartProcess,
        Func<TimeSpan, Task> Delay,
        Func<string, bool> FileExists,
        Func<string, string?> ReadText,
        Func<string, bool> DirectoryExists);

    private static readonly Regex SafeMuxName = new(@"^[A-Za-z0-9._-]{1,80}$", RegexOptions.Compiled);

    public static Task<Result> MirrorAsync(
        Request request,
        Func<object, Task<string>> muxdRequest,
        Action<string>? log = null)
        => MirrorAsync(request, muxdRequest, DefaultDependencies(), log);

    internal static async Task<Result> MirrorAsync(
        Request request,
        Func<object, Task<string>> muxdRequest,
        Dependencies deps,
        Action<string>? log = null)
    {
        var valid = Validate(request, deps);
        if (!valid.Ok) return valid;

        var before = await ReadStateAsync(request, muxdRequest);
        if (before.Ok) return before;
        if (before.Detail.StartsWith("conflict:", StringComparison.Ordinal))
            return new Result(false, before.Detail["conflict:".Length..].Trim());

        var task = await deps.ReadMuxdTask();
        if (!task.Ok) return new Result(false, task.Detail);
        if (!MuxdTaskLaunch.TryGetAdopterAction(
                task.Xml,
                deps.FileExists,
                deps.ReadText,
                out var action)
            || action is null)
            return new Result(false, "could not resolve the trusted muxrun.py/pythonw.exe pair from MuxdSessionHost");

        var psi = new ProcessStartInfo
        {
            FileName = action.PythonwExe,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = request.Workspace,
        };
        psi.ArgumentList.Add(action.MuxrunPath);
        psi.ArgumentList.Add(request.MuxName);
        psi.ArgumentList.Add("--attach-pid");
        psi.ArgumentList.Add(request.Pid.ToString());
        psi.ArgumentList.Add("--cwd");
        psi.ArgumentList.Add(request.Workspace);
        psi.ArgumentList.Add("--cmd-b64");
        psi.ArgumentList.Add(Convert.ToBase64String(Encoding.UTF8.GetBytes(request.Command)));
        psi.ArgumentList.Add("--session-id");
        psi.ArgumentList.Add(request.SessionId);
        foreach (var alias in request.Aliases.Where(IsSafeIdentity))
        {
            psi.ArgumentList.Add("--alias");
            psi.ArgumentList.Add(alias);
        }

        try
        {
            if (!deps.StartProcess(psi))
                return new Result(false, "the adopted-terminal sidecar did not start");
        }
        catch (Exception ex)
        {
            return new Result(false, "could not start the adopted-terminal sidecar: " + ex.Message);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(12);
        string last = "muxd has not published the adopted session yet";
        while (DateTime.UtcNow < deadline)
        {
            await deps.Delay(TimeSpan.FromMilliseconds(200));
            var state = await ReadStateAsync(request, muxdRequest);
            if (state.Ok) return state;
            if (state.Detail.StartsWith("conflict:", StringComparison.Ordinal))
                return new Result(false, state.Detail["conflict:".Length..].Trim());
            last = state.Detail;
        }
        log?.Invoke($"adopted mux registration timed out for {request.MuxName}: {last}");
        return new Result(false, "the sidecar started, but muxd did not confirm the adopted session: " + last);
    }

    private static Result Validate(Request request, Dependencies deps)
    {
        if (!OperatingSystem.IsWindows())
            return new Result(false, "local terminal mirroring is Windows-only");
        if (!SafeMuxName.IsMatch((request.MuxName ?? "").Trim()))
            return new Result(false, "invalid mux session name");
        if (request.Pid <= 0)
            return new Result(false, "a live local agent pid is required");
        if (!IsSafeIdentity(request.SessionId))
            return new Result(false, "a verified local session identity is required");
        if (request.Tool is not ("claude" or "codex"))
            return new Result(false, "tool must be claude or codex");
        if (string.IsNullOrWhiteSpace(request.Command))
            return new Result(false, "the local archive has no trusted command for this session");
        if (string.IsNullOrWhiteSpace(request.Workspace) || !deps.DirectoryExists(request.Workspace))
            return new Result(false, "the trusted session workspace is unavailable");

        var scan = deps.ScanRunning();
        if (!scan.Ok)
            return new Result(false, "could not verify the local process: " + scan.Detail);
        var row = scan.Sessions.FirstOrDefault(item => item.Pid == request.Pid);
        if (row is null)
            return new Result(false, $"pid {request.Pid} is no longer a live Claude/Codex agent");
        if (!string.Equals(row.Tool, request.Tool, StringComparison.OrdinalIgnoreCase))
            return new Result(false, $"pid {request.Pid} is {row.Tool}, not {request.Tool}");
        var identities = new HashSet<string>(
            new[] { request.SessionId }.Concat(request.Aliases ?? Array.Empty<string>())
                .Where(IsSafeIdentity),
            StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(row.SessionId) || !identities.Contains(row.SessionId))
            return new Result(false, $"pid {request.Pid} does not own session {request.SessionId}");
        return new Result(true, "verified");
    }

    private static async Task<Result> ReadStateAsync(Request request, Func<object, Task<string>> muxdRequest)
    {
        try
        {
            var text = await muxdRequest(new { t = "ls" });
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
                return new Result(false, "muxd returned no session list");
            foreach (var row in list.EnumerateArray())
            {
                if (!row.TryGetProperty("name", out var name)
                    || !string.Equals(name.GetString(), request.MuxName, StringComparison.OrdinalIgnoreCase))
                    continue;
                var alive = row.TryGetProperty("alive", out var aliveNode) && aliveNode.ValueKind == JsonValueKind.True;
                var adopted = row.TryGetProperty("adopted", out var adoptedNode) && adoptedNode.ValueKind == JsonValueKind.True;
                var external = row.TryGetProperty("externalOwner", out var externalNode) && externalNode.ValueKind == JsonValueKind.True;
                var pid = row.TryGetProperty("childPid", out var pidNode) && pidNode.TryGetInt32(out var parsedPid)
                    ? parsedPid
                    : 0;
                var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (row.TryGetProperty("sessionId", out var sid) && !string.IsNullOrWhiteSpace(sid.GetString()))
                    ids.Add(sid.GetString()!);
                if (row.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                    foreach (var alias in aliases.EnumerateArray())
                        if (!string.IsNullOrWhiteSpace(alias.GetString())) ids.Add(alias.GetString()!);
                if (alive && adopted && external && pid == request.Pid && ids.Contains(request.SessionId))
                    return new Result(true, "mirroring local terminal as " + request.MuxName);
                if (alive)
                    return new Result(false, "conflict: mux session name is already owned by a different live terminal");
                return new Result(false, "matching mux row is dormant");
            }
            return new Result(false, "mux row is not registered");
        }
        catch (Exception ex)
        {
            return new Result(false, ex.Message);
        }
    }

    private static Dependencies DefaultDependencies()
        => new(
            () =>
            {
                var ok = RunningSessions.TryScan(out var sessions, out var detail);
                return (ok, sessions, detail);
            },
            ReadMuxdTaskAsync,
            psi =>
            {
                using var process = Process.Start(psi);
                return process is not null;
            },
            Task.Delay,
            File.Exists,
            path =>
            {
                try { return File.ReadAllText(path); }
                catch { return null; }
            },
            Directory.Exists);

    private static async Task<(bool Ok, string Xml, string Detail)> ReadMuxdTaskAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "schtasks",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var arg in new[] { "/Query", "/TN", "MuxdSessionHost", "/XML" })
            psi.ArgumentList.Add(arg);
        var result = await ContainedProcessRunner.RunAsync(
            psi,
            TimeSpan.FromSeconds(8),
            maxStdoutChars: 1024 * 1024,
            maxStderrChars: 64 * 1024);
        if (result.TimedOut) return (false, "", "MuxdSessionHost task query timed out");
        if (result.ExitCode != 0)
            return (false, "", "could not query MuxdSessionHost: " + (result.Stderr + " " + result.Stdout).Trim());
        return (true, result.Stdout, "");
    }

    private static bool IsSafeIdentity(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.Length <= 200
           && value is not "." and not ".."
           && value.All(ch => char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-');
}
