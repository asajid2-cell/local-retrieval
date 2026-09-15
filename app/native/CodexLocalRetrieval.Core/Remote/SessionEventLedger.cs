using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Services;

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
    private const long DefaultMaxFileBytes = 8L * 1024 * 1024;
    private const long DefaultMaxTotalBytes = 64L * 1024 * 1024;
    private static readonly TimeSpan DefaultRetention = TimeSpan.FromDays(180);

    // Per-session sidecar index. events-index/<session-id>.jsonl holds one {"f","o"} line per ledger event
    // that mentions that id, so ReadForSession costs O(events-of-that-session) instead of an all-time scan.
    private const string IndexDirectoryName = "events-index";
    private const string IndexCompleteMarker = ".complete";
    private const string IndexDirtyMarker = ".dirty";
    private const int MaxIndexKeyLength = 120;
    private const int BackfillFlushChars = 8_000_000;

    public sealed record Options(
        string? RootDirectory = null,
        DateTimeOffset? Now = null,
        TimeSpan? LockTimeout = null,
        long? MaxFileBytes = null,
        long? MaxTotalBytes = null,
        TimeSpan? Retention = null)
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
        public long EffectiveMaxFileBytes => Math.Max(1024, MaxFileBytes ?? DefaultMaxFileBytes);
        public long EffectiveMaxTotalBytes => Math.Max(EffectiveMaxFileBytes, MaxTotalBytes ?? DefaultMaxTotalBytes);
        public TimeSpan EffectiveRetention => Retention ?? DefaultRetention;
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
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            detail = "couldn't create session event directory: " + ex.Message;
            return false;
        }

        var line = JsonSerializer.Serialize(ev, JsonOptions) + "\n";
        var bytes = Encoding.UTF8.GetBytes(line);
        var mutexName = MutexName(root);
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

            var path = WritableEventFile(root, options.EffectiveNow, bytes.Length, options.EffectiveMaxFileBytes);
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);

            // Index FIRST, at the offset the event is about to occupy. A crash between the two writes then
            // leaves a DANGLING index entry (the reader re-verifies every hit, so it is discarded) rather
            // than a MISSING one, which would silently shrink a session's history. If the sidecar write
            // fails - typically a bounded-timeout loss against a backfill holding the index mutex - the
            // index is invalidated only AFTER the event bytes are on disk, so the sticky dirty sentinel
            // never becomes visible before the bytes it exists to protect.
            var offset = fs.Position;
            var indexed = TryWriteIndexEntries(root, IndexKeysForEvent(ev), Path.GetFileName(path), offset, options.EffectiveLockTimeout);

            fs.Write(bytes, 0, bytes.Length);
            fs.Flush(flushToDisk: true);
            if (!indexed)
                InvalidateIndex(root);
            EnforceRetention(root, path, options);
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
        options ??= new Options();
        if (TryReadViaIndex(options.EffectiveRootDirectory, ids, max, options, out var indexed)) return indexed;
        return ScanForIds(ids, max, options);
    }

    /// The exhaustive reader ReadForSession falls back to when the sidecar index cannot answer. Exposed for
    /// the equivalence tests, which assert the two paths agree line for line.
    internal static IReadOnlyList<SessionEvent> ReadForSessionScanOnly(string? sessionId, IEnumerable<string>? aliases = null, int max = 200, Options? options = null)
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
        return ScanForIds(ids, Math.Clamp(max, 1, 5000), options ?? new Options());
    }

    private static IReadOnlyList<SessionEvent> ScanForIds(HashSet<string> ids, int max, Options options)
    {
        // The screen is what makes the fallback affordable: a line that cannot contain any candidate id is
        // rejected on raw bytes, before UTF-8 decode, JSON deserialize and the regex redaction in Normalize.
        var screen = BuildIdScreen(ids);
        var outEvents = new List<SessionEvent>();
        foreach (var file in EventFilesNewestFirst(options.EffectiveRootDirectory))
        {
            foreach (var ev in ReadFileNewestFirst(file, screen))
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

    private static string WritableEventFile(string root, DateTimeOffset now, int appendBytes, long maxFileBytes)
    {
        var basePath = EventFile(root, now);
        var stem = Path.GetFileNameWithoutExtension(basePath);
        var highestSegment = Directory.EnumerateFiles(root, stem + "*.jsonl")
            .Select(path => EventFileOrder(path).Segment)
            .DefaultIfEmpty(0)
            .Max();
        var current = highestSegment == 0
            ? basePath
            : Path.Combine(root, $"{stem}-{highestSegment:0000}.jsonl");
        if (Fits(current, appendBytes, maxFileBytes)) return current;
        return Path.Combine(root, $"{stem}-{highestSegment + 1:0000}.jsonl");
    }

    private static bool Fits(string path, int appendBytes, long maxFileBytes)
    {
        try
        {
            if (!File.Exists(path)) return true;
            return new FileInfo(path).Length + appendBytes <= maxFileBytes;
        }
        catch
        {
            return false;
        }
    }

    private static void EnforceRetention(string root, string currentPath, Options options)
    {
        try
        {
            var files = EventFilesNewestFirst(root)
                .Select(path => new FileInfo(path))
                .Where(info => info.Exists)
                .OrderByDescending(info => EventFileOrder(info.FullName).Month, StringComparer.Ordinal)
                .ThenByDescending(info => EventFileOrder(info.FullName).Segment)
                .ToList();
            var cutoff = options.EffectiveNow - options.EffectiveRetention;
            var total = files.Sum(info => info.Length);
            var removed = false;

            foreach (var info in files
                         .OrderBy(info => EventFileOrder(info.FullName).Month, StringComparer.Ordinal)
                         .ThenBy(info => EventFileOrder(info.FullName).Segment))
            {
                if (string.Equals(info.FullName, currentPath, StringComparison.OrdinalIgnoreCase)) continue;
                var tooOld = info.LastWriteTimeUtc < cutoff.UtcDateTime;
                var tooLarge = total > options.EffectiveMaxTotalBytes;
                if (!tooOld && !tooLarge) continue;
                var length = info.Length;
                try
                {
                    info.Delete();
                    total -= length;
                    removed = true;
                }
                catch { }
            }

            if (removed) ResetIndex(root, options.EffectiveLockTimeout);
        }
        catch { }
    }

    private static IEnumerable<string> EventFilesNewestFirst(string root)
    {
        if (!Directory.Exists(root)) yield break;
        foreach (var path in Directory.EnumerateFiles(root, "events-*.jsonl")
                     .OrderByDescending(path => EventFileOrder(path).Month, StringComparer.Ordinal)
                     .ThenByDescending(path => EventFileOrder(path).Segment))
            yield return path;
    }

    private static (string Month, int Segment) EventFileOrder(string path)
    {
        var name = Path.GetFileName(path);
        if (!name.StartsWith("events-", StringComparison.OrdinalIgnoreCase)
            || !name.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)
            || name.Length < 20)
            return ("", -1);
        var month = name.Substring(7, 7);
        var segmentText = name.Substring(14, name.Length - 14 - ".jsonl".Length);
        if (segmentText.Length == 0) return (month, 0);
        return segmentText[0] == '-'
               && int.TryParse(segmentText.AsSpan(1), out var segment)
            ? (month, segment)
            : (month, -1);
    }

    private static IEnumerable<SessionEvent> ReadFileNewestFirst(string path, byte[][]? screen = null)
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
                PerfCounters.LedgerBytesRead(read);

                for (var i = read - 1; i >= 0; i--)
                {
                    var b = readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        if (!oversized && TryParseReversedLine(lineBuffer, lineLength, screen, out var ev))
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

            if (!oversized && TryParseReversedLine(lineBuffer, lineLength, screen, out var first))
                yield return first;
        }
    }

    private static bool TryParseReversedLine(byte[] buffer, int length, byte[][]? screen, out SessionEvent ev)
    {
        ev = null!;
        if (length == 0) return false;
        Array.Reverse(buffer, 0, length);
        try
        {
            if (screen is not null && !ContainsAnyNeedle(buffer.AsSpan(0, length), screen)) return false;
            return TryParseLineBytes(buffer.AsSpan(0, length), out ev);
        }
        finally
        {
            Array.Reverse(buffer, 0, length);
        }
    }

    private static bool TryParseLineBytes(ReadOnlySpan<byte> line, out SessionEvent ev)
    {
        ev = null!;
        try
        {
            var text = Encoding.UTF8.GetString(line);
            if (text.Length > 0 && text[0] == '\uFEFF') text = text[1..];
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parsed = JsonSerializer.Deserialize<SessionEvent>(text);
            if (parsed is null || string.IsNullOrWhiteSpace(parsed.Kind)) return false;
            ev = Normalize(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---- pre-parse screen -------------------------------------------------------------------------------
    //
    // Needles are matched against the raw ledger bytes, so they must be spelled the way the writer spelled
    // them: both the verbatim UTF-8 id AND its JSON-encoded form, because JavaScriptEncoder.Default escapes
    // '+', '<', '&' and everything non-ASCII as \uXXXX. Comparison is ASCII-case-folded on both sides.
    //
    // INVARIANT: the screen is engaged ONLY when every candidate id is pure ASCII \u2014 BuildIdScreen returns
    // null for any non-ASCII candidate, which the caller reads as "no screening" and runs the exact legacy
    // unscreened scan. Within ASCII its folding is exactly OrdinalIgnoreCase, the same fold EventMatchesAnyId
    // uses, so it can never wrongly reject a line the real filter would keep. The non-ASCII bail-out closes
    // two ways that guarantee would otherwise fail: (1) an id only recoverable after Clean() strips an
    // embedded control character from a longer raw value (control characters are JSON-escaped on the way out,
    // so this cannot arise from anything this ledger wrote); and (2) an id that matches only via non-ASCII
    // case folding, e.g. an uppercase accented id probed against its lowercase-accented stored form \u2014
    // LowerAscii folds only 'A'-'Z', so the raw needle and its \uXXXX form both differ from the hay in bytes
    // ASCII folding never equates (the escaped hex digits, not just ASCII case).
    private static byte[][]? BuildIdScreen(IEnumerable<string> ids)
    {
        var needles = new List<byte[]>();
        foreach (var id in ids)
        {
            if (string.IsNullOrEmpty(id)) continue;
            // Any non-ASCII candidate would need Unicode case folding to match; the screen folds ASCII only,
            // so it would silently drop such an id. Disable screening for the whole read and fall back to the
            // exact legacy unscreened scan, restoring equivalence with EventMatchesAnyId by construction.
            foreach (var ch in id)
                if (ch > (char)0x7F) return null;
            AddNeedle(needles, Encoding.UTF8.GetBytes(id));
            var encoded = JsonSerializer.Serialize(id, JsonOptions);
            if (encoded.Length > 2) AddNeedle(needles, Encoding.UTF8.GetBytes(encoded[1..^1]));
        }
        return needles.Count == 0 ? null : needles.ToArray();
    }

    private static void AddNeedle(List<byte[]> needles, byte[] candidate)
    {
        if (candidate.Length == 0) return;
        for (var i = 0; i < candidate.Length; i++) candidate[i] = LowerAscii(candidate[i]);
        foreach (var existing in needles)
            if (existing.AsSpan().SequenceEqual(candidate)) return;
        needles.Add(candidate);
    }

    private static byte LowerAscii(byte b) => b >= (byte)'A' && b <= (byte)'Z' ? (byte)(b + 32) : b;

    private static bool ContainsAnyNeedle(ReadOnlySpan<byte> hay, byte[][] needles)
    {
        foreach (var needle in needles)
        {
            if (needle.Length > hay.Length) continue;
            var lower = needle[0];
            var upper = lower >= (byte)'a' && lower <= (byte)'z' ? (byte)(lower - 32) : lower;
            var from = 0;
            var last = hay.Length - needle.Length;
            while (from <= last)
            {
                var window = hay.Slice(from, last - from + 1);
                var rel = lower == upper ? window.IndexOf(lower) : window.IndexOfAny(lower, upper);
                if (rel < 0) break;
                var start = from + rel;
                var j = 1;
                while (j < needle.Length && LowerAscii(hay[start + j]) == needle[j]) j++;
                if (j == needle.Length) return true;
                from = start + 1;
            }
        }
        return false;
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

    // ---- per-session sidecar index ----------------------------------------------------------------------
    //
    // events-index/<key>.jsonl is an append-ordered list of {"f":<monthly file>,"o":<byte offset>} pointing at
    // every event that mentions <key>. Keys come from the SAME normalized event TryAppend writes, so the key
    // set is exactly what EventMatchesAnyId would later match on. The index is a cache with one hard rule: it
    // may never be missing an entry the ledger has. events-index/.complete asserts history has been swept;
    // any write that could not be completed deletes it, and the next read rebuilds.

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.Ordinal)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9"
    };

    internal static string IndexDirectory(string root) => Path.Combine(root, IndexDirectoryName);

    /// An id is indexable only if it is safe as a bare file name. Anything else (spaces, separators, unicode,
    /// over-long, a DOS device name) simply is not indexed, and a read asking for it takes the scan path.
    private static bool TryIndexKey(string? id, out string key)
    {
        key = "";
        id = (id ?? "").Trim();
        if (id.Length == 0 || id.Length > MaxIndexKeyLength) return false;
        foreach (var ch in id)
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9')
                     || ch == '-' || ch == '_' || ch == '.' || ch == '~';
            if (!ok) return false;
        }
        if (id[0] == '.') return false;
        var lower = id.ToLowerInvariant();
        var stem = lower.Split('.')[0];
        if (ReservedDeviceNames.Contains(stem)) return false;
        key = lower;
        return true;
    }

    private static HashSet<string> IndexKeysForEvent(SessionEvent ev)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        void Add(string? value)
        {
            if (TryIndexKey(value, out var key)) keys.Add(key);
        }
        Add(ev.SessionId);
        foreach (var id in ev.SessionIds ?? []) Add(id);
        foreach (var value in ev.Details.Values)
        {
            Add(value);
            foreach (var part in value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                Add(part);
        }
        return keys;
    }

    private static string IndexLine(string fileName, long offset)
        => "{\"f\":\"" + fileName + "\",\"o\":" + offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}\n";

    // Sticky invalidation. Deleting the completeness marker alone is not enough: while a backfill holds the
    // index mutex the marker does not exist yet, so the delete is a no-op and the sweep stamps it AFTER this
    // append's bytes landed in a file the sweep had already passed - hiding the event behind a marker forever.
    // The dirty sentinel survives that stamp: it is only ever cleared under the index mutex immediately before
    // a fresh full sweep, and a marker accompanied by the sentinel is never trusted.
    private static void InvalidateIndex(string root)
    {
        var dir = IndexDirectory(root);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, IndexDirtyMarker), DateTimeOffset.UtcNow.UtcDateTime.ToString("O") + "\n");
        }
        catch { }
        try { File.Delete(Path.Combine(dir, IndexCompleteMarker)); } catch { }
    }

    private static void ResetIndex(string root, TimeSpan timeout)
    {
        var dir = IndexDirectory(root);
        if (!Directory.Exists(dir)) return;
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            mutex = new Mutex(false, MutexName(dir));
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                InvalidateIndex(root);
                return;
            }
            foreach (var path in Directory.EnumerateFiles(dir))
                try { File.Delete(path); } catch { }
        }
        catch { InvalidateIndex(root); }
        finally
        {
            if (acquired)
                try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    private static bool TryWriteIndexEntries(string root, HashSet<string> keys, string fileName, long offset, TimeSpan timeout)
    {
        if (keys.Count == 0) return true;
        var dir = IndexDirectory(root);
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            Directory.CreateDirectory(dir);
            mutex = new Mutex(false, MutexName(dir));
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return false;

            var bytes = Encoding.UTF8.GetBytes(IndexLine(fileName, offset));
            foreach (var key in keys)
            {
                using var fs = new FileStream(Path.Combine(dir, key + ".jsonl"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.WriteThrough);
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(flushToDisk: true);
            }
            return true;
        }
        catch { return false; }
        finally
        {
            if (acquired)
                try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    // A stamped marker is trustworthy only while no dirty sentinel sits beside it: an append whose index
    // write timed out may have landed event bytes a finished sweep never saw.
    private static bool IndexLooksComplete(string dir)
        => File.Exists(Path.Combine(dir, IndexCompleteMarker)) && !File.Exists(Path.Combine(dir, IndexDirtyMarker));

    private static bool EnsureIndexComplete(string root, TimeSpan timeout)
    {
        var dir = IndexDirectory(root);
        if (IndexLooksComplete(dir)) return true;
        Mutex? mutex = null;
        var acquired = false;
        try
        {
            Directory.CreateDirectory(dir);
            mutex = new Mutex(false, MutexName(dir));
            try { acquired = mutex.WaitOne(timeout); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) return false;
            // The fast path raced: a concurrent reader may have just finished a sweep and stamped .complete,
            // or an append may have marked the index dirty after that check.
            if (IndexLooksComplete(dir)) return true;
            var marker = Path.Combine(dir, IndexCompleteMarker);
            // Consume the sentinel only here, under the mutex and immediately before the full sweep: the sweep
            // is about to re-read the whole ledger, so it picks up any append whose index write had failed.
            try { File.Delete(Path.Combine(dir, IndexDirtyMarker)); } catch { }
            Backfill(root, dir);
            File.WriteAllText(marker, DateTimeOffset.UtcNow.UtcDateTime.ToString("O") + "\n");
            return true;
        }
        catch { return false; }
        finally
        {
            if (acquired)
                try { mutex?.ReleaseMutex(); } catch { }
            mutex?.Dispose();
        }
    }

    // One-time sweep of every monthly file, forward, recording each line's start offset. Runs under the index
    // mutex, so a concurrent TryAppend blocks before writing its own entry: the worst outcome is that both the
    // sweep and the append record the same (file, offset), and the reader dedupes.
    private static void Backfill(string root, string dir)
    {
        foreach (var stale in Directory.EnumerateFiles(dir, "*.jsonl"))
            try { File.Delete(stale); } catch { }

        var buffers = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
        var buffered = 0;
        foreach (var path in EventFilesNewestFirst(root))
        {
            var name = Path.GetFileName(path);
            foreach (var (offset, ev) in ReadFileForwardWithOffsets(path))
            {
                var keys = IndexKeysForEvent(ev);
                if (keys.Count == 0) continue;
                var line = IndexLine(name, offset);
                foreach (var key in keys)
                {
                    if (!buffers.TryGetValue(key, out var sb)) buffers[key] = sb = new StringBuilder();
                    sb.Append(line);
                    buffered += line.Length;
                }
                if (buffered >= BackfillFlushChars)
                {
                    FlushIndexBuffers(dir, buffers);
                    buffered = 0;
                }
            }
        }
        FlushIndexBuffers(dir, buffers);
    }

    private static void FlushIndexBuffers(string dir, Dictionary<string, StringBuilder> buffers)
    {
        foreach (var kv in buffers)
            File.AppendAllText(Path.Combine(dir, kv.Key + ".jsonl"), kv.Value.ToString());
        buffers.Clear();
    }

    private static IEnumerable<(long Offset, SessionEvent Event)> ReadFileForwardWithOffsets(string path)
    {
        FileStream fs;
        try { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
        catch { yield break; }

        using (fs)
        {
            var readBuffer = new byte[ReverseReadBufferBytes];
            var lineBuffer = new byte[MaxLedgerLineBytes];
            var lineLength = 0;
            var oversized = false;
            long consumed = 0;
            long lineStart = 0;

            while (true)
            {
                int read;
                try { read = fs.Read(readBuffer, 0, readBuffer.Length); }
                catch { yield break; }
                if (read <= 0) break;
                PerfCounters.LedgerBytesRead(read);

                for (var i = 0; i < read; i++)
                {
                    var b = readBuffer[i];
                    if (b == (byte)'\n')
                    {
                        if (!oversized && TryParseLineBytes(lineBuffer.AsSpan(0, lineLength), out var ev))
                            yield return (lineStart, ev);
                        lineLength = 0;
                        oversized = false;
                        lineStart = consumed + i + 1;
                    }
                    else if (b != (byte)'\r')
                    {
                        if (lineLength < lineBuffer.Length) lineBuffer[lineLength++] = b;
                        else oversized = true;
                    }
                }
                consumed += read;
            }

            if (!oversized && lineLength > 0 && TryParseLineBytes(lineBuffer.AsSpan(0, lineLength), out var last))
                yield return (lineStart, last);
        }
    }

    private static bool TryReadViaIndex(string root, HashSet<string> ids, int max, Options options, out IReadOnlyList<SessionEvent> events)
    {
        events = Array.Empty<SessionEvent>();
        if (!Directory.Exists(root)) return false;

        var keys = new List<string>();
        foreach (var id in ids)
        {
            if (!TryIndexKey(id, out var key)) return false;
            if (!keys.Contains(key, StringComparer.Ordinal)) keys.Add(key);
        }
        if (!EnsureIndexComplete(root, options.EffectiveLockTimeout)) return false;

        var dir = IndexDirectory(root);
        var entries = new HashSet<(string File, long Offset)>();
        foreach (var key in keys)
        {
            var path = Path.Combine(dir, key + ".jsonl");
            byte[] raw;
            try
            {
                if (!File.Exists(path)) continue;
                raw = File.ReadAllBytes(path);
            }
            catch { return false; }
            PerfCounters.LedgerBytesRead(raw.Length);
            foreach (var entry in ParseIndexEntries(raw)) entries.Add(entry);
        }

        // (file name descending, offset descending) is exactly the order EventFilesNewestFirst plus the
        // reverse in-file reader produces, so limit and ordering semantics are unchanged.
        var ordered = entries
            .OrderByDescending(e => EventFileOrder(e.File).Month, StringComparer.Ordinal)
            .ThenByDescending(e => EventFileOrder(e.File).Segment)
            .ThenByDescending(e => e.Offset);

        var outEvents = new List<SessionEvent>();
        var lineBuffer = new byte[MaxLedgerLineBytes];
        var chunk = new byte[4096];
        FileStream? open = null;
        var openPath = "";
        try
        {
            foreach (var entry in ordered)
            {
                var path = Path.Combine(root, entry.File);
                if (!string.Equals(path, openPath, StringComparison.OrdinalIgnoreCase))
                {
                    open?.Dispose();
                    open = null;
                    openPath = path;
                    try { open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete); }
                    catch { open = null; }
                }
                if (open is null) continue;
                if (!TryReadEventAt(open, entry.Offset, lineBuffer, chunk, out var ev)) continue;
                if (!EventMatchesAnyId(ev, ids)) continue;
                outEvents.Add(ev);
                if (outEvents.Count >= max) break;
            }
        }
        finally { open?.Dispose(); }

        events = outEvents;
        return true;
    }

    private static IEnumerable<(string File, long Offset)> ParseIndexEntries(byte[] raw)
    {
        var text = Encoding.UTF8.GetString(raw);
        foreach (var line in text.Split('\n'))
        {
            if (line.Length == 0) continue;
            string? file = null;
            long offset;
            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("f", out var f) || !doc.RootElement.TryGetProperty("o", out var o)) continue;
                file = f.GetString();
                if (!o.TryGetInt64(out offset)) continue;
            }
            catch { continue; }
            if (string.IsNullOrEmpty(file) || offset < 0) continue;
            // Entries name a bare monthly ledger file and nothing else; never let one address a path.
            if (file != Path.GetFileName(file) || !file.StartsWith("events-", StringComparison.OrdinalIgnoreCase) || !file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (file, offset);
        }
    }

    private static bool TryReadEventAt(FileStream fs, long offset, byte[] lineBuffer, byte[] chunk, out SessionEvent ev)
    {
        ev = null!;
        var lineLength = 0;
        try
        {
            if (offset >= fs.Length) return false;
            fs.Position = offset;
            while (true)
            {
                var read = fs.Read(chunk, 0, chunk.Length);
                if (read <= 0) break;
                PerfCounters.LedgerBytesRead(read);
                for (var i = 0; i < read; i++)
                {
                    var b = chunk[i];
                    if (b == (byte)'\n') return TryParseLineBytes(lineBuffer.AsSpan(0, lineLength), out ev);
                    if (b == (byte)'\r') continue;
                    if (lineLength >= lineBuffer.Length) return false;
                    lineBuffer[lineLength++] = b;
                }
            }
        }
        catch { return false; }
        return lineLength > 0 && TryParseLineBytes(lineBuffer.AsSpan(0, lineLength), out ev);
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
