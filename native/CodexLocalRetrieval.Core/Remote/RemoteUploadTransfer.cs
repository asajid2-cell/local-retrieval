using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record RemoteUploadResult(bool Ok, string Detail, bool OnPc);

public static class RemoteUploadTransfer
{
    private static readonly TimeSpan TransferTimeout = TimeSpan.FromSeconds(30);

    public static async Task<RemoteUploadResult> FetchAndInsertAsync(
        string target,
        string uploadId,
        string filename,
        bool keep,
        string? muxName,
        string? insert,
        Func<object, Task<string>> muxRequest)
    {
        if (string.IsNullOrWhiteSpace(uploadId))
            return new RemoteUploadResult(false, "file download refused: missing upload id", false);

        var safe = SanitizeFilename(filename);
        var destDir = keep
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMultiplexUploads")
            : Path.Combine(Path.GetTempPath(), "multiplex-uploads");
        try
        {
            Directory.CreateDirectory(destDir);
        }
        catch
        {
            return new RemoteUploadResult(false, "file download failed: local destination is unavailable", false);
        }

        var dest = Path.Combine(destDir, safe);
        if (File.Exists(dest))
            dest = Path.Combine(destDir, uploadId + "_" + safe);

        var remote = $"{target}:multiplex-app/uploads/{uploadId}/{safe}";
        Process? process = null;
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
            process = Process.Start(start);
            if (process is null)
                return new RemoteUploadResult(false, "file download failed to start", false);

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TransferTimeout);
            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new RemoteUploadResult(false, "file download timed out", false);
            }
            await Task.WhenAll(stdoutTask, stderrTask);

            if (process.ExitCode != 0 || !File.Exists(dest))
                return new RemoteUploadResult(false, $"file download failed (scp exit {process.ExitCode})", false);
        }
        catch
        {
            try { process?.Kill(entireProcessTree: true); } catch { }
            return new RemoteUploadResult(false, "file download failed", false);
        }
        finally
        {
            process?.Dispose();
        }

        return await InsertDownloadedPathAsync(dest, filename, muxName, insert, muxRequest);
    }

    public static async Task<RemoteUploadResult> InsertDownloadedPathAsync(
        string localPath,
        string filename,
        string? muxName,
        string? insert,
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
                d = Convert.ToBase64String(Encoding.UTF8.GetBytes(input))
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
