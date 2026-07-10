using System.Text;

namespace CodexLocalRetrieval.Core.Agents;

public sealed record BoundedTextCaptureResult(string Text, bool Truncated);

public static class BoundedTextCapture
{
    public static async Task<BoundedTextCaptureResult> ReadToEndAsync(
        TextReader reader,
        int maxCapturedChars,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        if (maxCapturedChars < 0) throw new ArgumentOutOfRangeException(nameof(maxCapturedChars));

        var captured = new StringBuilder(Math.Min(maxCapturedChars, 4096));
        var buffer = new char[8192];
        var truncated = false;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            var remaining = maxCapturedChars - captured.Length;
            if (remaining > 0)
                captured.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining)
                truncated = true;
        }

        return new BoundedTextCaptureResult(captured.ToString(), truncated);
    }
}
