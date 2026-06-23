using System.Diagnostics;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Server;

// Drives a live Claude turn: `claude --resume <id> -p <prompt> --output-format stream-json`, one
// process per turn (the session file persists across turns). Stdout lines are mapped to AgentEvents
// via ClaudeStreamMapper and pushed to onEvent. acceptEdits lets it run without an interactive TTY.
public sealed class ClaudeLiveDriver
{
    private readonly string _exe;
    public ClaudeLiveDriver(string? exe = null) => _exe = exe ?? Resolve();

    public bool Available => File.Exists(_exe) || _exe == "claude";

    public Process StartTurn(string? sessionId, string cwd, string prompt, Func<AgentEvent, Task> onEvent, CancellationToken ct)
    {
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
        psi.ArgumentList.Add("acceptEdits");
        if (!string.IsNullOrEmpty(sessionId))
        {
            psi.ArgumentList.Add("--resume");
            psi.ArgumentList.Add(sessionId);
        }

        var proc = Process.Start(psi)!;
        proc.StandardInput.Close(); // we never write; closing avoids a blocked-stdin wait

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
}
