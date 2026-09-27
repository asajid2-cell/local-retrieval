using System.Diagnostics;
using System.Text;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Core.Chat;

// One-shot text backend over the local Codex CLI (`codex exec --json`). Used as a brain analyst so
// extraction can run on Codex with no API key. The prompt is passed as a command-line ARG (codex exec
// reads its prompt there, not stdin), so the caller keeps prompts well under the Windows arg limit.
// Runs read-only in a neutral temp dir so the agent has nothing to touch; hard timeout + cancellation.
public sealed class CodexCliBackend : IChatBackend
{
    private readonly string _exe;
    private readonly string? _model;
    private readonly string _serviceTier;
    private readonly int _timeoutMs;

    public CodexCliBackend(string exe, string? model = null, string serviceTier = "fast", int timeoutMs = 180_000)
    {
        _exe = exe;
        _model = model;
        _serviceTier = serviceTier;
        _timeoutMs = timeoutMs;
    }

    public string Name => "codex-cli";
    public bool SupportsTools => false;

    // codex exec passes the prompt as an argv element; Windows command lines cap near 32k chars, so
    // keep the whole prompt comfortably under that.
    private const int MaxPromptChars = 28_000;
    private const int MaxOutputChars = 24_000;
    private const int MaxErrorChars = 16_000;

    // stdout here is the --json EVENT STREAM, not the reply: reasoning and tool events dwarf the answer,
    // and the answer arrives at the end. So this bounds the transport and is deliberately generous - the
    // reply itself is still capped at MaxOutputChars below.
    private const int MaxStreamChars = 4 * 1024 * 1024;

    public async Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatToolSpec> tools, CancellationToken cancellationToken)
    {
        var prompt = ClaudexBackend.FlattenPrompt(messages);
        if (prompt.Length > MaxPromptChars) prompt = prompt[..MaxPromptChars];

        var workdir = Path.GetTempPath(); // neutral, read-only sandbox -> nothing to mutate
        var psi = new ProcessStartInfo
        {
            FileName = _exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workdir,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--json");
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("service_tier=" + _serviceTier);
        psi.ArgumentList.Add("--skip-git-repo-check");
        psi.ArgumentList.Add("--sandbox"); psi.ArgumentList.Add("read-only");
        psi.ArgumentList.Add("-C"); psi.ArgumentList.Add(workdir);
        if (!string.IsNullOrWhiteSpace(_model)) { psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(_model!); }
        psi.ArgumentList.Add(prompt);

        // Same surface as the other CLI backends: a job-contained process with both pipes bounded. stdin
        // is handed a closed pipe (no input) so codex exec does not wait on it.
        var result = await ContainedProcessRunner.RunAsync(
            psi,
            TimeSpan.FromMilliseconds(_timeoutMs),
            stdin: null,
            maxStdoutChars: MaxStreamChars,
            maxStderrChars: MaxErrorChars,
            cancellationToken: cancellationToken);
        if (result.TimedOut)
            throw new InvalidOperationException("The Codex CLI did not respond in time.");
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Codex CLI failed ({result.ExitCode}): {Trim(result.Stderr)}");

        // Pull the final assistant text out of the JSONL event stream (ignores reasoning/tools).
        var sb = new StringBuilder();
        foreach (var line in result.Stdout.Split('\n'))
            foreach (var ev in CodexEventMapper.Map(line))
                if (ev.Kind == AgentEventKind.AssistantText && !string.IsNullOrEmpty(ev.Text))
                    sb.Append(ev.Text);

        var answer = sb.ToString().Trim();
        if (answer.Length > MaxOutputChars) answer = answer[..MaxOutputChars] + "...(truncated)";
        return new BackendReply
        {
            Message = new ChatMessage { Role = "assistant", Content = string.IsNullOrWhiteSpace(answer) ? "(no response)" : answer },
            FinishReason = "stop",
        };
    }

    private static string Trim(string s)
    {
        var clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean[..Math.Min(300, clean.Length)];
    }
}
