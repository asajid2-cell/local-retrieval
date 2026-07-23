using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Remote;

public static class RemoteCommandProtocol
{
    public static string LeaseOwner(string role)
    {
        var clean = new StringBuilder();
        foreach (var ch in Environment.MachineName)
            clean.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '-');
        return $"{role}-{clean}-{Environment.ProcessId}";
    }

    public static string NewIntent(string prefix)
        => $"{prefix}-{Guid.NewGuid():N}";

    public static bool IsReplaySafe(string? type, string? replayPolicy)
    {
        var expected = (type ?? "").Trim().ToLowerInvariant() switch
        {
            "kill" => "refused",
            "transcript" => "read-only",
            "transcriptfetch" => "read-only",
            "fetchfile" => "intent-fenced",
            "rename" => "idempotent",
            "setapptitle" => "idempotent",
            "addtocollection" => "idempotent",
            "startmux" => "intent-fenced",
            "cleartabhistory" => "idempotent",
            "settabcolor" => "idempotent",
            _ => "",
        };
        return expected.Length > 0
            && string.Equals(expected, replayPolicy, StringComparison.Ordinal);
    }

    public static bool AckSucceeded(string? response)
    {
        if (string.IsNullOrWhiteSpace(response)) return false;
        try
        {
            using var document = JsonDocument.Parse(response);
            return document.RootElement.TryGetProperty("ok", out var ok) && ok.GetBoolean();
        }
        catch
        {
            return false;
        }
    }
}
