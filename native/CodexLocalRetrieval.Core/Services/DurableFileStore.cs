namespace CodexLocalRetrieval.Core.Services;

internal enum DurableWriteStage
{
    BeforeWrite,
    BeforeFileFlush,
    BeforeReplace,
    BeforeReadBack,
}

internal static class DurableFileStore
{
    public static async Task WriteAtomicAsync(
        string destination,
        byte[] contents,
        string? backupPath = null,
        Action<DurableWriteStage>? fault = null)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destination));
        Directory.CreateDirectory(directory);
        var tmp = Path.Combine(
            directory,
            Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            fault?.Invoke(DurableWriteStage.BeforeWrite);
            await using (var stream = new FileStream(
                             tmp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents);
                fault?.Invoke(DurableWriteStage.BeforeFileFlush);
                await stream.FlushAsync();
                stream.Flush(flushToDisk: true);
            }

            fault?.Invoke(DurableWriteStage.BeforeReplace);
            if (File.Exists(destination))
            {
                if (string.IsNullOrWhiteSpace(backupPath))
                    throw new IOException("Replacing a durable file requires a backup path.");
                Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
                await ReplaceFileWithRetryAsync(tmp, destination, backupPath);
            }
            else
            {
                File.Move(tmp, destination);
            }

            fault?.Invoke(DurableWriteStage.BeforeReadBack);
            var committed = await File.ReadAllBytesAsync(destination);
            if (!committed.AsSpan().SequenceEqual(contents))
                throw new IOException("The committed file did not read back identically: " + destination);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static async Task ReplaceFileWithRetryAsync(string source, string destination, string backup)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                File.Replace(source, destination, backup, ignoreMetadataErrors: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                last = ex;
                await Task.Delay(25 * (attempt + 1));
            }
        }

        throw new IOException(
            "Could not atomically replace durable file after retries; existing file was left untouched.",
            last);
    }
}
