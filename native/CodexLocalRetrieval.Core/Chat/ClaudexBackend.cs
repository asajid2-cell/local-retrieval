using System.Diagnostics;
using System.Text;

namespace CodexLocalRetrieval.Core.Chat;

// Degraded text-only fallback that drives the local Claude CLI (claudex) via `-p --output-format
// text`. It does NOT speak OpenAI tool-calling, so SupportsTools is false and the orchestrator sends
// no tools — this is plain chat over the conversation, not archive actions. Runs as a sandboxed
// subprocess: no shell, neutral working directory, captured stderr, hard timeout, cancellation.
public sealed class ClaudexBackend : IChatBackend
{
    private readonly string _exe;
    private readonly string? _model;
    private readonly int _timeoutMs;

    public ClaudexBackend(string exe, string? model = null, int timeoutMs = 120_000)
    {
        _exe = exe;
        _model = model;
        _timeoutMs = timeoutMs;
    }

    public string Name => "claude-cli";
    public bool SupportsTools => false;

    public async Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatToolSpec> tools, CancellationToken cancellationToken)
    {
        var prompt = FlattenPrompt(messages);

        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetTempPath() // neutral cwd so a coding agent has nothing to touch
        };
        psi.ArgumentList.Add("-p");
        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("text");
        if (!string.IsNullOrWhiteSpace(_model)) { psi.ArgumentList.Add("--model"); psi.ArgumentList.Add(_model!); }

        using var process = new Process { StartInfo = psi };
        if (!process.Start()) throw new InvalidOperationException("Could not start the Claude CLI.");

        try { await process.StandardInput.WriteAsync(prompt); }
        finally { process.StandardInput.Close(); }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeoutMs);
        try { await process.WaitForExitAsync(timeoutCts.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new InvalidOperationException("The Claude CLI did not respond in time.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Claude CLI failed ({process.ExitCode}): {Trim(stderr)}");

        var answer = stdout.Trim();
        return new BackendReply
        {
            Message = new ChatMessage { Role = "assistant", Content = string.IsNullOrWhiteSpace(answer) ? "(no response)" : answer },
            FinishReason = "stop"
        };
    }

    // Flatten the conversation into a single prompt and steer the CLI toward answering, not acting.
    public static string FlattenPrompt(IReadOnlyList<ChatMessage> messages)
    {
        var builder = new StringBuilder();
        builder.AppendLine("You are answering a question about the user's local chat archive. Do not run commands or edit files; just reply in text.");
        builder.AppendLine();
        foreach (var m in messages)
        {
            if (m.Role == "system" && !string.IsNullOrWhiteSpace(m.Content)) { builder.AppendLine(m.Content); builder.AppendLine(); continue; }
            if (m.Role == "user" && !string.IsNullOrWhiteSpace(m.Content)) builder.AppendLine("User: " + m.Content);
            else if (m.Role == "assistant" && !string.IsNullOrWhiteSpace(m.Content)) builder.AppendLine("Assistant: " + m.Content);
        }
        builder.AppendLine();
        builder.Append("Assistant:");
        return builder.ToString();
    }

    private static string Trim(string s)
    {
        var clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean[..Math.Min(300, clean.Length)];
    }
}
