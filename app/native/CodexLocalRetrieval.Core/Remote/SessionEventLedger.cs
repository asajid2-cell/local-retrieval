using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace CodexLocalRetrieval.Core.Remote;

public sealed record SessionEvent
{
    [JsonPropertyName("id")] public string Id { get; init; } = "";
    [JsonPropertyName("at")] public string At { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("severity")] public string Severity { get; init; } = "info";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = "";
    [JsonPropertyName("sessionIds")] public List<string> SessionIds { get; init; } = new();
    [JsonPropertyName("tool")] public string Tool { get; init; } = "";
    [JsonPropertyName("title")] public string Title { get; init; } = "";
    [JsonPropertyName("workspace")] public string Workspace { get; init; } = "";
    [JsonPropertyName("summary")] public string Summary { get; init; } = "";
    [JsonPropertyName("details")] public Dictionary<string, string> Details { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public static class SessionEventLedger
{
    private const int ReverseReadBufferBytes = 64 * 1024;
    private const int MaxLedgerLineBytes = 256 * 1024;

    public sealed record Options(string? RootDirectory = null, DateTimeOffset? Now = null, TimeSpan? LockTimeout = null)
    {
        public string EffectiveRootDirectory
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RootDirectory)) return RootDirectory!;
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
                return Path.Combine(local, "CodexLocalRetrieval", "session-events");
            }
        }

        public DateTimeOffset EffectiveNow => Now ?? DateTimeOffset.UtcNow;
        public TimeSpan EffectiveLockTimeout => LockTimeout ?? TimeSpan.FromSeconds(1);
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault
    };
    private static readonly Regex SecretAssignment = new(@"\b(api[_-]?key|token|secret|password)\b\s*[:=]\s*['""]?[^'""\s,;]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex CommonSecret = new(@"\b(?:sk|ghp|gho|github_pat|glpat)-[A-Za-z0-9_\-]{16,}\b", RegexOptions.Compiled);
    private static readonly Regex WindowsUserPath = new(@"\b[A-Z]:\\Users\\[^\\\s""']+(?:\\[^\s""']*)?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex WindowsPath = new(@"\b[A-Z]:\\[^\s""']+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static SessionEvent Create(
        string kind,
        string summary,
        string? sessionId = null,
        string? tool = null,
        string? title = null,
        string? workspace = null,
        string source = "app",
        string severity = "info",
        IReadOnlyDictionary<string, string>? details = null,
        DateTimeOffset? at = null,
        IEnumerable<string>? sessionIds = null)
    {
        var when = at ?? DateTimeOffset.UtcNow;
        var cleanSessionIds = CleanSessionIds(sessionId, sessionIds);
        return new SessionEvent
        {
            Id = Guid.NewGuid().ToString("N"),
            At = when.UtcDateTime.ToString("O"),
            Kind = Clean(kind, 96),
            Severity = Clean(string.IsNullOrWhiteSpace(severity) ? "info" : severity, 32),
            Source = Clean(source, 64),
            SessionId = Clean(sessionId, 160),
            SessionIds = cleanSessionIds,
            Tool = Clean(tool, 32),
            Title = Clean(title, 220),
            Workspace = Clean(workspace, 360),
            Summary = Clean(summary, 600),
            Details = CleanDetails(details)
        };
    }

    public static void AppendBestEffortQueued(SessionEvent ev, Action<string>? log = null, Options? options = null)
    {
        try { _ = Task.Run(() => AppendBestEffort(ev, log, options)); }
        catch (Exception ex) { log?.Invoke("session event append queue failed: " + ex.Message); }
    }

    public static void AppendBestEffort(SessionEvent ev, Action<string>? log = null, Options? options = null)
    {
        try
        {
            if (!TryAppend(ev, out var detail, options))
                log?.Invoke("session event append skipped: " + detail);
        }
        catch (Exception ex)
        {
            log?.Invoke("session event append failed: " + ex.Message);
        }
    }

    public static bool TryAppend(SessionEvent ev, out string detail, Options? options = null)
    {
        options ??= new Options();
        detail = "";
        ev = Normalize(ev);
        if (string.IsNullOrWhiteSpace(ev.Kind))
        {
            detail = "event kind is required";
            return false;
        }

        var root = options.EffectiveRootDirectory;
        var path = EventFile(root, options.EffectiveNow);
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            detail = "couldn't create session event directory: " + ex.Message;
            return false;
        }

        var line = JsonSerializer.Serialize(ev, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        var mutexName = MutexName(path);
        var acquired = false;
        Mutex? mutex = null;
        try
        {
            mutex = new Mutex(false, mutexName);
            try { acquired = mutex.WaitOne(options.EffectiveLockTimeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                detail = "event file lock timed out";
                return false;
            }

            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
        finally
        {
            if (acquired)
                try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    public static IReadOnlyList<SessionEvent> ReadRecent(int max = 200, Options? options = null)
    {
        options ??= new Options();
        max = Math.Clamp(max, 1, 5000);
        var outEvents = new List<SessionEvent>();
        foreach (var file in EventFilesNewestFirst(options.EffectiveRootDirectory))
        {
            foreach (var ev in ReadFileNewestFirst(file))
            {
                outEvents.Add(ev);
                if (outEvents.Count >= max) return outEvents;
            }
        }
        return outEvents;
    }

    public static IReadOnlyList<SessionEvent> ReadForSession(string? sessionId, IEnumerable<string>? aliases = null, int max = 200, Options? options = null)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length > 0) ids.Add(id);
        }
        Add(sessionId);
        if (aliases is not null)
            foreach (var alias in aliases) Add(alias);
        if (ids.Count == 0) return Array.Empty<SessionEvent>();
        max = Math.Clamp(max, 1, 5000);
        var outEvents = new List<SessionEvent>();
        options ??= new Options();
        foreach (var file in EventFilesNewestFirst(options.EffectiveRootDirectory))
        {
            foreach (var ev in ReadFileNewestFirst(file))
            {
                if (!EventMatchesAnyId(ev, ids)) continue;
                outEvents.Add(ev);
                if (outEvents.Count >= max) return outEvents;
            }
        }
        return outEvents;
    }

    internal static string EventFile(string root, DateTimeOffset now)
        => Path.Combine(root, "events-" + now.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    private static IEnumerable<string> EventFilesNewestFirst(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "events-*.jsonl").OrderByDescending(p => p, StringComparer.OrdinalIgnoreCase))
            yield return path;
    }

    private static IEnumerable<SessionEvent> ReadFileNewestFirst(string path)
    {
        FileStream fs;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch { yield break; }

        using (fs)
        {
            var position = fs.Length;
            var readBuffer = new byte[ReverseReadBufferBytes];
            var lineBuffer = new byte[MaxLedgerLineBytes];
            var lineLength = 0;
            var oversized = false;

            while (position > 0)
            {
                var requested = (int)Math.Min(readBuffer.Length, position);
                position -= requested;
                int read;
                try
                {
                    fs.Position = position;
                    read = 0;
                    while (read < requested)
                    {
                        var count = fs.Read(readBuffer, read, requested - read);
                        if (count == 0) break;
                        read += count;
                    }
                }
                catch { yield break; }
                if (read <= 0) yield break;

                for (var i = read - 1; i >= 0; i--)
                {
                    var b = readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        if (!oversized && TryParseReversedLine(lineBuffer, lineLength, out var ev))
                            yield return ev;
                        lineLength = 0;
                        oversized = false;
                    }
                    else if (b != (byte)'\r')
                    {
                        if (lineLength < lineBuffer.Length) lineBuffer[lineLength++] = b;
                        else oversized = true;
                    }
                }
            }

            if (!oversized && TryParseReversedLine(lineBuffer, lineLength, out var first))
                yield return first;
        }
    }

    private static bool TryParseReversedLine(byte[] buffer, int length, out SessionEvent ev)
    {
        ev = null!;
        if (length == 0) return false;
        Array.Reverse(buffer, 0, length);
        try
        {
            var line = Encoding.UTF8.GetString(buffer, 0, length);
            if (line.Length > 0 && line[0] == '\uFEFF') line = line[1..];
            if (string.IsNullOrWhiteSpace(line)) return false;
            var parsed = JsonSerializer.Deserialize<SessionEvent>(line);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Kind)) return false;
            ev = Normalize(parsed);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            Array.Reverse(buffer, 0, length);
        }
    }

    private static bool EventMatchesAnyId(SessionEvent ev, HashSet<string> ids)
    {
        if (ids.Contains(ev.SessionId)) return true;
        foreach (var id in ev.SessionIds ?? [])
            if (ids.Contains(id)) return true;
        foreach (var value in ev.Details.Values)
        {
            if (ids.Contains(value)) return true;
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                if (ids.Contains(part)) return true;
        }
        return false;
    }

    private static Dictionary<string, string> CleanDetails(IReadOnlyDictionary<string, string>? details)
    {
        var clean = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (details is null) return clean;
        foreach (var kv in details.Take(24))
        {
            var key = Clean(kv.Key, 80);
            if (key.Length == 0) continue;
            if (IsSensitiveDetailKey(key)) continue;
            clean[key] = Clean(kv.Value, 500);
        }
        return clean;
    }

    private static List<string> CleanSessionIds(string? primary, IEnumerable<string>? ids)
    {
        var clean = new List<string>();
        void Add(string? id)
        {
            id = Clean(id, 160);
            if (id.Length == 0) return;
            if (!clean.Any(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase))) clean.Add(id);
        }
        Add(primary);
        if (ids is not null)
            foreach (var id in ids.Take(48)) Add(id);
        return clean;
    }

    private static SessionEvent Normalize(SessionEvent ev)
    {
        var ids = CleanSessionIds(ev.SessionId, ev.SessionIds ?? []);
        return ev with
        {
            Id = Clean(ev.Id, 64),
            At = Clean(ev.At, 64),
            Kind = Clean(ev.Kind, 96),
            Severity = Clean(string.IsNullOrWhiteSpace(ev.Severity) ? "info" : ev.Severity, 32),
            Source = Clean(ev.Source, 64),
            SessionId = Clean(ev.SessionId, 160),
            SessionIds = ids,
            Tool = Clean(ev.Tool, 32),
            Title = Clean(ev.Title, 220),
            Workspace = Clean(ev.Workspace, 360),
            Summary = Clean(ev.Summary, 600),
            Details = CleanDetails(ev.Details)
        };
    }

    private static bool IsSensitiveDetailKey(string key)
    {
        key = key.ToLowerInvariant();
        return key.Contains("command")
            || key.Contains("path")
            || key.Contains("cwd")
            || key.Contains("prompt")
            || key.Contains("transcript")
            || key.Contains("stdout")
            || key.Contains("stderr")
            || key.Contains("output")
            || key.Contains("secret")
            || key.Contains("token")
            || key.Contains("password")
            || key.Contains("apikey")
            || key.Contains("api_key");
    }

    private static string Clean(string? value, int max)
    {
        value = (value ?? "").Trim();
        if (value.Length == 0) return "";
        value = Redact(value);
        var sb = new StringBuilder(Math.Min(value.Length, max));
        foreach (var ch in value)
        {
            if (ch == '\r' || ch == '\n' || ch == '\t') sb.Append(' ');
            else if (!char.IsControl(ch)) sb.Append(ch);
            if (sb.Length >= max) break;
        }
        return sb.ToString().Trim();
    }

    private static string Redact(string value)
    {
        value = SecretAssignment.Replace(value, m => m.Groups[1].Value + "=[redacted]");
        value = CommonSecret.Replace(value, "[secret]");
        value = WindowsUserPath.Replace(value, "[path]");
        value = WindowsPath.Replace(value, "[path]");
        return value;
    }

    private static string MutexName(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        return @"Local\CodexLocalRetrieval.SessionEventLedger." + hash[..32];
    }
}
