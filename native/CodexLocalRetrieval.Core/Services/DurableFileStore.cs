using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CodexLocalRetrieval.Core.Services;

internal enum DurableWriteStage
{
    BeforeWrite,
    BeforeFileFlush,
    BeforeReplace,
    BeforeDirectoryFlush,
    BeforeReadBack,
    BeforeVerificationRead,
}

internal enum DurableFileComparison
{
    Match,
    Mismatch,
    Unknown,
}

internal sealed class DurableWriteException(
    string message,
    Exception innerException,
    bool committed,
    bool recovered,
    bool verificationUnknown = false,
    Exception? recoveryError = null)
    : IOException(message, innerException)
{
    public bool Committed { get; } = committed;
    public bool Recovered { get; } = recovered;
    public bool VerificationUnknown { get; } = verificationUnknown;
    public Exception? RecoveryError { get; } = recoveryError;
}

internal static class DurableFileStore
{
    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    public static async Task WriteAtomicAsync(
        string destination,
        byte[] contents,
        string? backupPath = null,
        Action<DurableWriteStage>? fault = null)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destination));
        Directory.CreateDirectory(directory);
        byte[]? previous = null;
        if (File.Exists(destination))
        {
            if (string.IsNullOrWhiteSpace(backupPath))
                throw new IOException("Replacing a durable file requires a backup path.");
            previous = await File.ReadAllBytesAsync(destination);
            await CommitBytesAsync(backupPath, previous, fault: null);
        }

        try
        {
            await CommitBytesAsync(destination, contents, fault);
        }
        catch (Exception error) when (error is not DurableWriteException)
        {
            var comparison = await CompareFileAsync(destination, contents, fault);
            if (comparison == DurableFileComparison.Match)
            {
                try
                {
                    await CommitBytesAsync(destination, contents, fault: null);
                    return;
                }
                catch (Exception retryError)
                {
                    var retryComparison = await CompareFileAsync(destination, contents, fault: null);
                    throw new DurableWriteException(
                        retryComparison == DurableFileComparison.Match
                            ? "The durable file replacement committed but its durability could not be confirmed: " + destination
                            : "The durable file replacement retry left the authoritative generation uncertain: " + destination,
                        retryError,
                        committed: retryComparison == DurableFileComparison.Match,
                        recovered: false,
                        verificationUnknown: retryComparison == DurableFileComparison.Unknown);
                }
            }

            Exception? recoveryError = null;
            var recovered = false;
            if (comparison == DurableFileComparison.Mismatch && previous is not null)
            {
                try
                {
                    await CommitBytesAsync(destination, previous, fault: null);
                    recovered = true;
                }
                catch (Exception restoreError)
                {
                    recoveryError = restoreError;
                }
            }

            throw new DurableWriteException(
                comparison == DurableFileComparison.Unknown
                    ? "The durable file commit outcome is unknown; the primary was not rolled back: " + destination
                    : recovered
                        ? "The durable file commit failed and the previous generation was restored: " + destination
                        : "The durable file commit failed and the authoritative generation is uncertain: " + destination,
                error,
                committed: false,
                recovered,
                verificationUnknown: comparison == DurableFileComparison.Unknown,
                recoveryError: recoveryError);
        }
    }

    private static async Task CommitBytesAsync(
        string destination,
        byte[] contents,
        Action<DurableWriteStage>? fault)
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
            await ReplaceFileWithRetryAsync(tmp, destination);

            fault?.Invoke(DurableWriteStage.BeforeDirectoryFlush);
            FlushDirectory(directory);
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

    private static async Task<DurableFileComparison> CompareFileAsync(
        string path,
        byte[] expected,
        Action<DurableWriteStage>? fault)
    {
        try
        {
            fault?.Invoke(DurableWriteStage.BeforeVerificationRead);
            var actual = await File.ReadAllBytesAsync(path);
            return actual.AsSpan().SequenceEqual(expected)
                ? DurableFileComparison.Match
                : DurableFileComparison.Mismatch;
        }
        catch (FileNotFoundException)
        {
            return DurableFileComparison.Mismatch;
        }
        catch (DirectoryNotFoundException)
        {
            return DurableFileComparison.Mismatch;
        }
        catch
        {
            return DurableFileComparison.Unknown;
        }
    }

    private static async Task ReplaceFileWithRetryAsync(string source, string destination)
    {
        Exception? last = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!MoveFileEx(
                            source,
                            destination,
                            MoveFileReplaceExisting | MoveFileWriteThrough))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
                else
                {
                    File.Move(source, destination, overwrite: true);
                }
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
            {
                last = ex;
                await Task.Delay(25 * (attempt + 1));
            }
        }

        throw new IOException(
            "Could not atomically replace durable file after retries.",
            last);
    }

    private static void FlushDirectory(string directory)
    {
        if (OperatingSystem.IsWindows())
            return; // MoveFileEx(MOVEFILE_WRITE_THROUGH) flushes the replacement operation.

        using var handle = File.OpenHandle(
            directory,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        RandomAccess.FlushToDisk(handle);
    }
}
