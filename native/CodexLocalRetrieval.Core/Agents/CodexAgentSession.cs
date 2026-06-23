using System.Diagnostics;
using System.Threading.Channels;

namespace CodexLocalRetrieval.Core.Agents;

// Drives a live Codex session by running `codex exec --json` per turn (and `codex exec resume <id>`
// for follow-ups, which continues the same on-disk session). Each turn streams JSONL events that
// CodexEventMapper normalizes into the channel. Interrupt kills the current turn's process.
// NOTE: passes `-c service_tier=fast` because this codex build's parser rejects the `default` tier
// some configs carry; configurable via the ctor.
public sealed class CodexAgentSession : IAgentSession
{
    private readonly string _exe;
    private readonly string? _model;
    private readonly string _serviceTier;
    private readonly string _sandbox;
    private readonly Channel<AgentEvent> _channel = Channel.CreateUnbounded<AgentEvent>();
    private readonly object _lock = new();
    private Process? _proc;

    public CodexAgentSession(string exe, string workspace, string? model = null, string serviceTier = "fast", string sandbox = "workspace-write")
    {
        _exe = exe;
        Workspace = workspace;
        _model = model;
        _serviceTier = serviceTier;
        _sandbox = sandbox;
    }

    public string Agent => "codex";
    public string? SessionId { get; private set; }
    public string Workspace { get; }
    public bool Busy { get; private set; }
    public ChannelReader<AgentEvent> Events => _channel.Reader;

    // Lets us resume an existing on-disk chat: set its thread id before the first SendUser.
    public void AdoptSession(string sessionId) => SessionId = sessionId;

    public async Task SendUserAsync(string text, CancellationToken ct = default)
    {
        if (Busy) throw new InvalidOperationException("A turn is already running.");
        Busy = true;
        await _channel.Writer.WriteAsync(new AgentEvent { Kind = AgentEventKind.UserMessage, Text = text }, ct);

        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            WorkingDirectory = Workspace,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("exec");
        if (SessionId is not null) { psi.ArgumentList.Add("resume"); psi.ArgumentList.Add(SessionId); }
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("service_tier=" + _serviceTier);
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--sandbox"); psi.ArgumentList.Add(_sandbox);
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(Workspace);
        if (!string.IsNullOrWhiteSpace(_model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(_model!); }
        psi.ArgumentList.Add(text);

        var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
            lock (_lock) _proc = proc;
            proc.StandardInput.Close(); // EOF so `codex exec` doesn't block reading stdin
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);

            string? line;
            while ((line = await proc.StandardOutput.ReadLineAsync(ct)) is not null)
            {
                foreach (var ev in CodexEventMapper.Map(line))
                {
                    if (ev.Kind == AgentEventKind.SessionStarted && ev.SessionId is not null) SessionId = ev.SessionId;
                    await _channel.Writer.WriteAsync(ev, ct);
                }
            }
            await proc.WaitForExitAsync(ct);
            if (proc.ExitCode != 0)
            {
                var err = (await stderrTask).Trim();
                if (!string.IsNullOrEmpty(err)) await _channel.Writer.WriteAsync(AgentEvent.Err(Cap(err, 600)), ct);
            }
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            await _channel.Writer.WriteAsync(AgentEvent.Stat("interrupted"), CancellationToken.None);
        }
        catch (Exception ex)
        {
            await _channel.Writer.WriteAsync(AgentEvent.Err(ex.Message), CancellationToken.None);
        }
        finally
        {
            Busy = false;
            lock (_lock) _proc = null;
        }
    }

    public void Interrupt()
    {
        lock (_lock) { try { _proc?.Kill(entireProcessTree: true); } catch { } }
    }

    public ValueTask DisposeAsync()
    {
        Interrupt();
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private static string Cap(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
