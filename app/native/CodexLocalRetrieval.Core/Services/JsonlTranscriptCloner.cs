using System.Text;

namespace CodexLocalRetrieval.Core.Services;

internal readonly record struct JsonlCloneResult(long CapturedSourceLength, long WrittenLength, int LineCount);

internal static class JsonlTranscriptCloner
{
    public static async Task<JsonlCloneResult> CloneAsync(
        string sourcePath,
        string destinationPath,
        Func<int, string, string>? transform = null)
    {
        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destinationPath));
        Directory.CreateDirectory(destinationDirectory);

        var temporaryPath = Path.Combine(
            destinationDirectory,
            Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            long capturedLength;
            long writtenLength;
            var lineCount = 0;
            await using (var source = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                capturedLength = source.Length;
                var remaining = capturedLength;
                var readBuffer = new byte[64 * 1024];
                using var pending = new MemoryStream();

                while (remaining > 0)
                {
                    var requested = (int)Math.Min(readBuffer.Length, remaining);
                    var read = await source.ReadAsync(readBuffer.AsMemory(0, requested));
                    if (read == 0) break;
                    remaining -= read;

                    var segmentStart = 0;
                    for (var i = 0; i < read; i++)
                    {
                        if (readBuffer[i] != (byte)'\n') continue;
                        pending.Write(readBuffer, segmentStart, i - segmentStart);
                        var lineBytes = pending.ToArray();
                        pending.SetLength(0);
                        if (lineBytes.Length > 0 && lineBytes[^1] == (byte)'\r')
                            Array.Resize(ref lineBytes, lineBytes.Length - 1);

                        var line = Encoding.UTF8.GetString(lineBytes);
                        if (transform is not null) line = transform(lineCount, line);
                        var output = Encoding.UTF8.GetBytes(line);
                        await destination.WriteAsync(output);
                        await destination.WriteAsync("\n"u8.ToArray());
                        lineCount++;
                        segmentStart = i + 1;
                    }

                    if (segmentStart < read)
                        pending.Write(readBuffer, segmentStart, read - segmentStart);
                }

                await destination.FlushAsync();
                destination.Flush(flushToDisk: true);
                writtenLength = destination.Length;
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            return new JsonlCloneResult(capturedLength, writtenLength, lineCount);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }
    }
}
