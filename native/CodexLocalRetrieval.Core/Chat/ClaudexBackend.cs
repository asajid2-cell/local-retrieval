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

    private const int MaxPromptChars = 48_000;
    private const int MaxOutputChars = 24_000;

    public async Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatToolSpec> tools, CancellationToken cancellationToken)
    {
        var prompt = FlattenPrompt(messages);
        if (prompt.Length > MaxPromptChars) prompt = prompt[..MaxPromptChars];

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
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_timeoutMs);
        var token = timeoutCts.Token;

        if (!process.Start()) throw new InvalidOperationException("Could not start the Claude CLI.");

        // Start draining stdout/stderr BEFORE writing stdin: if the child fills its output pipe while
        // we're still writing the prompt, an unread pipe would deadlock. The whole exchange is under
        // the timeout so a child that never reads stdin can't hang us.
        var stdoutTask = process.StandardOutput.ReadToEndAsync(token);
        var stderrTask = process.StandardError.ReadToEndAsync(token);
        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), token);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token);
        }
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
        if (answer.Length > MaxOutputChars) answer = answer[..MaxOutputChars] + "...(truncated)";
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
