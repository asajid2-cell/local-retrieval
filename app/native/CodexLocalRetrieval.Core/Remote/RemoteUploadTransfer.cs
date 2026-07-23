using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record RemoteUploadResult(bool Ok, string Detail, bool OnPc);

public static class RemoteUploadTransfer
{
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);

    // The upload id arrives over the relay, so the executing end treats it as untrusted input:
    // a strict token charset, the relay's own minting shape, and a resolved-path containment
    // check all have to agree before the id reaches Path.Combine or the scp remote path.
    private static readonly Regex UploadIdCharset =
        new(@"^[A-Za-z0-9._-]{1,64}$", RegexOptions.CultureInvariant);

    // Mirrors relay/server.js uploadIdPattern; ids are minted there as 'u' + base36 + '-' + base36.
    private static readonly Regex RelayUploadIdShape =
        new(@"^u[a-z0-9]+-[a-z0-9]+$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>
    /// Resolves the per-upload directory for <paramref name="uploadId"/> under
    /// <paramref name="destRoot"/> without touching the filesystem. Returns false with a
    /// user-facing <paramref name="detail"/> when the id is not a legal token or when it would
    /// resolve outside the upload root.
    /// </summary>
    public static bool TryResolveUploadDirectory(
        string destRoot,
        string? uploadId,
        out string destDir,
        out string detail)
    {
        destDir = "";
        if (string.IsNullOrWhiteSpace(uploadId))
        {
            detail = "file download refused: missing upload id";
            return false;
        }

        if (!UploadIdCharset.IsMatch(uploadId))
        {
            detail = "file download refused: upload id is not a valid token";
            return false;
        }

        // Containment runs on the broadest set that can reach it — before the narrower format
        // rules below — so the security-critical invariant is exercised rather than shadowed.
        string rootFull;
        string candidateFull;
        try
        {
            rootFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destRoot));
            candidateFull = Path.GetFullPath(Path.Combine(rootFull, uploadId));
        }
        catch
        {
            detail = "file download refused: upload id does not resolve to a usable folder";
            return false;
        }

        var prefix = rootFull + Path.DirectorySeparatorChar;
        if (candidateFull.Length <= prefix.Length
            || !candidateFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            detail = "file download refused: upload id resolves outside the upload folder";
            return false;
        }

        if (uploadId[0] == '.')
        {
            detail = "file download refused: upload id is not a valid token";
            return false;
        }

        if (!RelayUploadIdShape.IsMatch(uploadId))
        {
            detail = "file download refused: upload id does not match the expected upload id format";
            return false;
        }

        destDir = candidateFull;
        detail = "";
        return true;
    }

    public static async Task<RemoteUploadResult> FetchAndInsertAsync(
        string target,
        string uploadId,
        string filename,
        bool keep,
        string? muxName,
        string? insert,
        string intentId,
        Func<object, Task<string>> muxRequest)
    {
        var safe = SanitizeFilename(filename);
        var destRoot = keep
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMultiplexUploads")
            : Path.Combine(Path.GetTempPath(), "multiplex-uploads");

        // Validate before any filesystem or scp side effect.
        if (!TryResolveUploadDirectory(destRoot, uploadId, out var destDir, out var refusal))
            return new RemoteUploadResult(false, refusal, false);

        try
        {
            Directory.CreateDirectory(destDir);
        }
        catch
        {
            return new RemoteUploadResult(false, "file download failed: local destination is unavailable", false);
        }

        var dest = Path.Combine(destDir, safe);

        var remote = $"{target}:multiplex-app/uploads/{uploadId}/{safe}";
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "scp",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("-q");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("BatchMode=yes");
            start.ArgumentList.Add("-o");
            start.ArgumentList.Add("ConnectTimeout=10");
            start.ArgumentList.Add(remote);
            start.ArgumentList.Add(dest);
            var result = await ContainedProcessRunner.RunAsync(
                start,
                TransferTimeout,
                maxStdoutChars: 64 * 1024,
                maxStderrChars: 64 * 1024);
            if (result.TimedOut)
                return new RemoteUploadResult(false, "file download timed out", false);

            if (result.ExitCode != 0 || !File.Exists(dest))
                return new RemoteUploadResult(false, $"file download failed (scp exit {result.ExitCode})", false);
        }
        catch
        {
            return new RemoteUploadResult(false, "file download failed", false);
        }

        return await InsertDownloadedPathAsync(dest, filename, muxName, insert, intentId, muxRequest);
    }

    public static async Task<RemoteUploadResult> InsertDownloadedPathAsync(
        string localPath,
        string filename,
        string? muxName,
        string? insert,
        string intentId,
        Func<object, Task<string>> muxRequest)
    {
        var insertion = (insert ?? "").Trim().ToLowerInvariant();
        if (insertion.Length == 0)
            return new RemoteUploadResult(true, "downloaded to PC", true);
        if (insertion is not ("path" or "element"))
            return new RemoteUploadResult(false, "file downloaded but the requested insertion mode is invalid", true);
        if (string.IsNullOrWhiteSpace(muxName))
            return new RemoteUploadResult(false, "file downloaded but no mux tab was selected for insertion", true);

        var input = insertion == "element"
            ? BuildElement(filename, localPath)
            : localPath;
        try
        {
            var response = await muxRequest(new
            {
                t = "input",
                s = muxName,
                d = Convert.ToBase64String(Encoding.UTF8.GetBytes(input)),
                intentId
            });
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            if (root.TryGetProperty("t", out var type) && type.GetString() == "input-ok")
                return new RemoteUploadResult(true, "downloaded to PC and inserted into the mux prompt", true);
            return new RemoteUploadResult(false, "file downloaded but prompt insertion failed", true);
        }
        catch
        {
            return new RemoteUploadResult(false, "file downloaded but prompt insertion failed", true);
        }
    }

    private static string BuildElement(string filename, string path)
    {
        var label = Path.GetFileName(filename ?? "file")
            .Replace("[", "\\[", StringComparison.Ordinal)
            .Replace("]", "\\]", StringComparison.Ordinal);
        var prefix = IsImage(filename ?? "") ? "!" : "";
        return $"{prefix}[{label}]({path})";
    }

    private static bool IsImage(string filename)
    {
        var extension = Path.GetExtension(filename ?? "");
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".gif", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".webp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".bmp", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".svg", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".heic", StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFilename(string name)
    {
        name = Path.GetFileName(name ?? "file");
        var clean = new StringBuilder();
        foreach (var ch in name)
            clean.Append(char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_' ? ch : '_');
        var value = clean.ToString();
        if (string.IsNullOrEmpty(value)) return "file";
        return value.Length > 120 ? value[..120] : value;
    }
}
