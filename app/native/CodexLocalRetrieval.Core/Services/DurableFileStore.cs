using System.Buffers;
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
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const int CopyBufferBytes = 256 * 1024;

    /// The replace was refused by the filesystem before it swapped anything, so the destination still
    /// holds the previous generation. Distinguishing this from a generic IO failure is what lets the
    /// caller skip a recovery attempt that would re-issue the identical refusal.
    private sealed class DestinationRefusedException(string message, Exception innerException)
        : IOException(message, innerException);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

    /// `contents` is a ReadOnlyMemory rather than a byte[] so the caller can hand over a slice of a
    /// reusable serialization buffer. The store is tens of MB and it is written twice per save (primary
    /// plus the redundant committed snapshot), so a `ToArray()` to satisfy a byte[] parameter was a whole
    /// extra store-sized allocation and copy per save, on the large object heap.
    public static async Task WriteAtomicAsync(
        string destination,
        ReadOnlyMemory<byte> contents,
        string? backupPath = null,
        Action<DurableWriteStage>? fault = null)
    {
        var directory = Path.GetDirectoryName(destination)
            ?? throw new ArgumentException("Destination must have a parent directory.", nameof(destination));
        Directory.CreateDirectory(directory);

        // The backup IS the rollback source. Keeping a byte[] of the previous generation in memory served
        // only to be written back on a mismatch, and the file we just wrote holds exactly those bytes - so
        // the copy is both the backup and the thing a rollback restores from, with no store-sized buffer
        // held across the whole operation.
        var hadPrevious = File.Exists(destination);
        if (hadPrevious)
        {
            if (string.IsNullOrWhiteSpace(backupPath))
                throw new IOException("Replacing a durable file requires a backup path.");
            try
            {
                await CommitFileCopyAsync(destination, backupPath, fault: null);
            }
            catch (DestinationRefusedException refused)
            {
                throw new IOException(
                    "The previous generation could not be snapshotted; the durable file was left untouched: "
                    + backupPath,
                    refused);
            }
        }

        try
        {
            await CommitBytesAsync(destination, contents, fault);
        }
        catch (DestinationRefusedException refused)
        {
            // Nothing was swapped, so the destination still holds the previous generation AND the backup
            // written above holds those same bytes. Taking the shared failure path below would re-read the
            // destination, find the old bytes, and roll back by re-issuing the identical refused replace -
            // a second full-store rewrite plus a second retry budget, for a write that cannot succeed.
            // Against a store held open by another process that ran on every sync attempt, which is where
            // the sustained burn came from. The previous generation is provably intact, so say so.
            throw new DurableWriteException(
                "The durable file replacement was refused; the previous generation is unchanged: " + destination,
                refused,
                committed: false,
                recovered: true);
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
            if (comparison == DurableFileComparison.Mismatch && hadPrevious)
            {
                try
                {
                    await CommitFileCopyAsync(backupPath!, destination, fault: null);
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
        ReadOnlyMemory<byte> contents,
        Action<DurableWriteStage>? fault)
    {
        var tmp = TempPathFor(destination);
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
            FlushDirectory(Path.GetDirectoryName(destination)!);
            fault?.Invoke(DurableWriteStage.BeforeReadBack);
            if (!await BytesMatchFileAsync(destination, contents))
                throw new IOException("The committed file did not read back identically: " + destination);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    // Byte-identical replacement of `destination` with the current contents of `source`, through the same
    // temp + atomic-replace + verify machinery as CommitBytesAsync. Used for the pre-replace snapshot and
    // for rolling back to it, so neither has to exist in memory first.
    //
    // The copy is bounded to the source length read up front and verified against that same prefix BEFORE
    // the replace. A transcript is append-only and may be growing while we copy it, so "copy then compare
    // whole files" would either fail spuriously or, worse, be compared against a later, longer source.
    // Verifying the temp before the move also means the check cannot be defeated by the temp ceasing to
    // exist at its path.
    private static async Task CommitFileCopyAsync(
        string source,
        string destination,
        Action<DurableWriteStage>? fault)
    {
        var tmp = TempPathFor(destination);
        try
        {
            fault?.Invoke(DurableWriteStage.BeforeWrite);
            long length;
            await using (var from = OpenForVerify(source))
            {
                length = from.Length;
                await using var to = new FileStream(
                    tmp,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    CopyBufferBytes,
                    FileOptions.WriteThrough);
                var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
                try
                {
                    for (long copied = 0; copied < length;)
                    {
                        var want = (int)Math.Min(buffer.Length, length - copied);
                        var read = await from.ReadAsync(buffer.AsMemory(0, want));
                        if (read <= 0) break;
                        await to.WriteAsync(buffer.AsMemory(0, read));
                        copied += read;
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }

                fault?.Invoke(DurableWriteStage.BeforeFileFlush);
                await to.FlushAsync();
                to.Flush(flushToDisk: true);
            }

            fault?.Invoke(DurableWriteStage.BeforeVerificationRead);
            if (!await FilesMatchAsync(tmp, source, length))
                throw new IOException("The durable file copy did not match its source: " + source);

            fault?.Invoke(DurableWriteStage.BeforeReplace);
            await ReplaceFileWithRetryAsync(tmp, destination);

            fault?.Invoke(DurableWriteStage.BeforeDirectoryFlush);
            FlushDirectory(Path.GetDirectoryName(destination)!);
        }
        finally
        {
            try { File.Delete(tmp); } catch { }
        }
    }

    private static string TempPathFor(string destination) =>
        Path.Combine(
            Path.GetDirectoryName(destination) ?? Path.GetTempPath(),
            Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");

    private static async Task<DurableFileComparison> CompareFileAsync(
        string path,
        ReadOnlyMemory<byte> expected,
        Action<DurableWriteStage>? fault)
    {
        try
        {
            fault?.Invoke(DurableWriteStage.BeforeVerificationRead);
            if (!File.Exists(path)) return DurableFileComparison.Mismatch;
            return await BytesMatchFileAsync(path, expected)
                ? DurableFileComparison.Match
                : DurableFileComparison.Mismatch;
        }
        catch
        {
            return DurableFileComparison.Unknown;
        }
    }

    // Streamed rather than `ReadAllBytesAsync` + `SequenceEqual`. Verification runs on every durable
    // write and the store is tens of MB, so materializing a copy purely to compare it put a whole store
    // on the large object heap several times per save; a pooled window answers the same question without
    // the allocation or the copy. The share mode is permissive on purpose: a verifier must never be the
    // handle that stops somebody else from replacing the file it is checking.
    private static async Task<bool> BytesMatchFileAsync(string path, ReadOnlyMemory<byte> expected)
    {
        await using var stream = OpenForVerify(path);
        if (stream.Length != expected.Length) return false;

        var buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            for (var offset = 0; offset < expected.Length;)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(0, Math.Min(buffer.Length, expected.Length - offset)));
                if (read <= 0) return false;
                if (!buffer.AsSpan(0, read).SequenceEqual(expected.Span.Slice(offset, read))) return false;
                offset += read;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // Compare the first `length` bytes of both files. `left` is a file we just wrote and closed, so it is
    // exactly `length` bytes; `right` may be longer if it is still being appended to, which is why the
    // comparison is bounded rather than whole-file.
    private static async Task<bool> FilesMatchAsync(string left, string right, long length)
    {
        await using var a = OpenForVerify(left);
        await using var b = OpenForVerify(right);
        if (a.Length != length || b.Length < length) return false;

        var bufferA = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        var bufferB = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        try
        {
            for (long offset = 0; offset < length;)
            {
                var want = (int)Math.Min(bufferA.Length, length - offset);
                if (!await TryReadFullyAsync(a, bufferA, want)) return false;
                if (!await TryReadFullyAsync(b, bufferB, want)) return false;
                if (!bufferA.AsSpan(0, want).SequenceEqual(bufferB.AsSpan(0, want))) return false;
                offset += want;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    // A single ReadAsync is allowed to return short; comparing two files needs the same number of bytes
    // from each per round, so fill the window before comparing.
    private static async Task<bool> TryReadFullyAsync(FileStream stream, byte[] buffer, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, count - total));
            if (read <= 0) return false;
            total += read;
        }
        return true;
    }

    private static FileStream OpenForVerify(string path) =>
        new(path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static async Task ReplaceFileWithRetryAsync(string source, string destination)
    {
        Exception? last = null;
        var refused = false;
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
                    {
                        var code = Marshal.GetLastWin32Error();
                        refused = code is ErrorAccessDenied or ErrorSharingViolation;
                        throw new Win32Exception(code);
                    }
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

        if (refused && last is not null)
            throw new DestinationRefusedException(
                "The atomic replace was refused by the filesystem (the destination is locked or not "
                + "replaceable). The previous generation was not touched: " + destination,
                last);
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
