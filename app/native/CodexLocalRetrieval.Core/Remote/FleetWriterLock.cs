using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodexLocalRetrieval.Core.Remote;

public static class FleetWriterLock
{
    public sealed record Options(string? RootDirectory = null);

    public sealed record HolderInfo(
        [property: JsonPropertyName("pid")] int Pid,
        [property: JsonPropertyName("host")] string Host,
        [property: JsonPropertyName("acquiredAtUtc")] string AcquiredAtUtc);

    public static bool TryAcquire(
        string host,
        out IDisposable? handle,
        out string detail,
        Options? options = null)
    {
        handle = null;
        detail = string.Empty;

        FileStream? stream = null;
        string? lockFilePath = null;

        try
        {
            lockFilePath = GetLockFilePath(options);
            Directory.CreateDirectory(Path.GetDirectoryName(lockFilePath)!);

            stream = new FileStream(
                lockFilePath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.Read);

            stream.SetLength(0);
            stream.Position = 0;

            var holder = new HolderInfo(
                Environment.ProcessId,
                host,
                DateTime.UtcNow.ToString("O"));

            JsonSerializer.Serialize(stream, holder);
            stream.Flush(flushToDisk: true);

            handle = new WriterLockHandle(stream, lockFilePath);
            stream = null;
            detail = $"Acquired writer lock at '{lockFilePath}'.";
            return true;
        }
        catch (Exception exception)
        {
            detail = $"Could not acquire writer lock"
                + (lockFilePath is null ? string.Empty : $" at '{lockFilePath}'")
                + $": {exception.Message}";
            return false;
        }
        finally
        {
            if (stream is not null)
            {
                try
                {
                    stream.Dispose();
                }
                catch
                {
                }

                if (lockFilePath is not null)
                {
                    TryDelete(lockFilePath);
                }
            }
        }
    }

    public static bool TryReadHolder(
        out HolderInfo? holder,
        out string detail,
        Options? options = null)
    {
        holder = null;
        detail = string.Empty;

        string lockFilePath;
        try
        {
            lockFilePath = GetLockFilePath(options);

            using var stream = new FileStream(
                lockFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length == 0)
            {
                detail = $"Writer lock file '{lockFilePath}' is empty.";
                return true;
            }

            holder = JsonSerializer.Deserialize<HolderInfo>(stream);
            if (holder is null)
            {
                detail = $"Writer lock file '{lockFilePath}' contained no holder information.";
                return true;
            }

            detail = $"Read writer lock holder from '{lockFilePath}'.";
            return true;
        }
        catch (FileNotFoundException)
        {
            detail = "Writer lock file does not exist; nobody holds the lock.";
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            detail = "Writer lock directory does not exist; nobody holds the lock.";
            return true;
        }
        catch (JsonException exception)
        {
            detail = $"Writer lock file contained empty or unparseable holder information: {exception.Message}";
            holder = null;
            return true;
        }
        catch (Exception exception)
        {
            detail = $"Could not read writer lock holder: {exception.Message}";
            holder = null;
            return false;
        }
    }

    private static string GetLockFilePath(Options? options)
    {
        var rootDirectory = options?.RootDirectory;
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            rootDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(rootDirectory))
            {
                rootDirectory = Path.GetTempPath();
            }

            rootDirectory = Path.Combine(rootDirectory, "CodexLocalRetrieval", "fleet");
        }

        return Path.Combine(rootDirectory, "writer.lock");
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private sealed class WriterLockHandle : IDisposable
    {
        private readonly string _lockFilePath;
        private FileStream? _stream;

        public WriterLockHandle(FileStream stream, string lockFilePath)
        {
            _stream = stream;
            _lockFilePath = lockFilePath;
        }

        public void Dispose()
        {
            var stream = Interlocked.Exchange(ref _stream, null);
            if (stream is null)
            {
                return;
            }

            try
            {
                stream.Dispose();
            }
            catch
            {
            }

            TryDelete(_lockFilePath);
        }
    }
}
