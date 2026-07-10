using System.Diagnostics;

namespace CodexLocalRetrieval.Core.Agents;

public sealed record ContainedProcessRunResult(
    int ExitCode,
    string Stdout,
    string Stderr,
    bool StdoutTruncated,
    bool StderrTruncated,
    bool TimedOut);

public static class ContainedProcessRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public static async Task<ContainedProcessRunResult> RunAsync(
        ProcessStartInfo startInfo,
        TimeSpan timeout,
        string? stdin = null,
        int maxStdoutChars = 1024 * 1024,
        int maxStderrChars = 64 * 1024,
        IProcessContainment? containment = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        if (timeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (!startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
            throw new ArgumentException("Contained process capture requires redirected stdout and stderr.", nameof(startInfo));
        // Creation-time containment passes an explicit inherited-handle allowlist. Normalize stdin
        // here so no-input helpers still receive a closed pipe instead of inheriting the owner's
        // console handle or failing the containment contract.
        startInfo.RedirectStandardInput = true;

        WindowsProcessJob? ownedContainment = null;
        containment ??= ownedContainment = WindowsProcessJob.CreateKillOnClose();
        ContainedProcess? process = null;
        try
        {
            process = containment.StartContained(startInfo);
            var stdoutTask = BoundedTextCapture.ReadToEndAsync(
                process.StandardOutput,
                maxStdoutChars,
                CancellationToken.None);
            var stderrTask = BoundedTextCapture.ReadToEndAsync(
                process.StandardError,
                maxStderrChars,
                CancellationToken.None);

            using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operation.CancelAfter(timeout);
            var timedOut = false;
            try
            {
                if (startInfo.RedirectStandardInput)
                {
                    if (stdin is not null)
                        await process.StandardInput.WriteAsync(stdin.AsMemory(), operation.Token).ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(operation.Token).ConfigureAwait(false);
                    process.CloseStandardInputPipe();
                }
                await process.WaitForExitAsync(operation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException ex)
            {
                timedOut = !cancellationToken.IsCancellationRequested;
                if (!await TerminateAndConfirmAsync(process).ConfigureAwait(false))
                    throw new ProcessContainmentException(
                        "Contained process cancellation did not confirm process termination.",
                        terminationConfirmed: false,
                        ex);
                if (!timedOut) throw;
            }

            var stdout = await stdoutTask.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            var stderr = await stderrTask.WaitAsync(CleanupTimeout).ConfigureAwait(false);
            return new ContainedProcessRunResult(
                timedOut ? -2 : process.ExitCode,
                stdout.Text,
                stderr.Text,
                stdout.Truncated,
                stderr.Truncated,
                timedOut);
        }
        finally
        {
            process?.Dispose();
            ownedContainment?.Dispose();
        }
    }

    private static async Task<bool> TerminateAndConfirmAsync(ContainedProcess process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        try
        {
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(CleanupTimeout).ConfigureAwait(false);
            return true;
        }
        catch
        {
            try { return process.HasExited; } catch { return false; }
        }
    }
}
