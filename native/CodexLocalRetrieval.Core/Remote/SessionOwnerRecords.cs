using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Remote;

// What the app knows about the process IT started for a session: the wrapper (cmd.exe / muxd shell), not
// the agent. This is a HINT and a kill-target index, never an oracle:
//   * wrapper-alive != session-alive - `cmd /k` sits there long after the agent inside it exited;
//   * wrapper-dead != session-dead - the agent is a grandchild launched with UseShellExecute and outlives it.
// Authoritative liveness stays with the layered live-process probe. No caller may read a record as proof
// that a session is running or finished; it only says "if you need to kill this launch, here is the process
// concretely tied to it".
//
// Records are written one file PER candidate id (session id + each alias), in the same sorted order and with
// the same file-name derivation as launch claims, so a launcher addressing the transcript by a different
// alias subset lands on the same files instead of on a private canonical-sid record.
public static class SessionOwnerRecords
{
    public sealed record Options(string? RootDirectory = null, DateTimeOffset? Now = null)
    {
        public string EffectiveRootDirectory
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(RootDirectory)) return RootDirectory!;
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(local)) local = Path.GetTempPath();
                return Path.Combine(local, "CodexLocalRetrieval", "owners");
            }
        }

        public DateTimeOffset EffectiveNow => Now ?? DateTimeOffset.UtcNow;
    }

    public sealed record OwnerRecordInfo(
        string SessionId,
        IReadOnlyList<string> CandidateIds,
        int WrapperPid,
        DateTimeOffset? WrapperStartTimeUtc,
        string Transport,
        DateTimeOffset ClaimedAt,
        string? MuxName,
        string Path,
        string? JobName = null);

    // Never throws and never fails a launch: returns false with a detail the caller logs.
    public static bool TryWrite(
        string? sessionId,
        IEnumerable<string>? aliases,
        int wrapperPid,
        DateTimeOffset? wrapperStartTimeUtc,
        string transport,
        out string detail,
        string? muxName = null,
        Options? options = null,
        string? jobName = null)
    {
        detail = "";
        var ids = CandidateIds(sessionId, aliases);
        if (ids.Count == 0)
        {
            detail = "missing session id";
            return false;
        }

        options ??= new Options();
        var root = options.EffectiveRootDirectory;
        try { Directory.CreateDirectory(root); }
        catch (Exception ex)
        {
            detail = "couldn't create owner-record directory: " + ex.Message;
            return false;
        }

        var record = new OwnerRecordData
        {
            SessionId = ids[0],
            CandidateIds = ids,
            WrapperPid = wrapperPid > 0 ? wrapperPid : 0,
            WrapperStartTimeUtc = wrapperStartTimeUtc,
            Transport = string.IsNullOrWhiteSpace(transport) ? "unknown" : transport.Trim(),
            ClaimedAt = options.EffectiveNow,
            MuxName = string.IsNullOrWhiteSpace(muxName) ? null : muxName!.Trim(),
            JobName = string.IsNullOrWhiteSpace(jobName) ? null : jobName!.Trim()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, new JsonSerializerOptions { WriteIndented = true });

        var paths = ids
            .Select(id => Path.Combine(root, RecordFileName(id)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Overwrite-on-relaunch is fine here: these are hints, not reservations. CreateNew race semantics
        // only start to matter when records replace claim files.
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        if (failures.Count == 0) return true;
        detail = "couldn't write owner record(s) - " + string.Join("; ", failures);
        return false;
    }

    // Terminal/server launches hand us the started Process; a wrapper that exits instantly makes both Id and
    // StartTime throw, so the record degrades to "no wrapper known" rather than blowing up the launch.
    //
    // This is also the single point where the wrapper is put into a named job object [F#7], because it is the
    // only place with a real Process in hand (the muxd path goes through TryWrite and gets no job - muxd owns
    // its own shells). Assignment is best-effort and its failure is recorded as `jobName: null`, which simply
    // sends Kill down the snapshot tree-kill path; the launch itself is never affected.
    public static bool TryWriteForProcess(
        string? sessionId,
        IEnumerable<string>? aliases,
        Process? process,
        string transport,
        out string detail,
        Options? options = null,
        bool assignJobObject = true)
    {
        var pid = 0;
        DateTimeOffset? startedUtc = null;
        if (process is not null)
        {
            try { pid = process.Id; }
            catch { pid = 0; }
            try { startedUtc = process.StartTime.ToUniversalTime(); }
            catch { startedUtc = null; }
        }
        string? jobName = null;
        if (assignJobObject && process is not null)
        {
            try { jobName = OwnerJobObjects.TryAssign(process); }
            catch { jobName = null; }
        }
        return TryWrite(sessionId, aliases, pid, startedUtc, transport, out detail, options: options, jobName: jobName);
    }

    // muxd writes live-tabs.json asynchronously, so the shell pid is usually NOT there yet at record time.
    // Absence is expected and is not an error: we take the pid if it happens to be readable and move on.
    // Never polls, never waits.
    public static int TryReadMuxShellPid(string? muxName)
    {
        if (string.IsNullOrWhiteSpace(muxName)) return 0;
        try
        {
            var liveTabs = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "muxd",
                "live-tabs.json");
            if (!File.Exists(liveTabs)) return 0;
            using var doc = JsonDocument.Parse(File.ReadAllText(liveTabs));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return 0;
            if (!doc.RootElement.TryGetProperty(muxName!, out var row)
                || row.ValueKind != JsonValueKind.Object
                || !row.TryGetProperty("pid", out var pidElement)
                || !pidElement.TryGetInt32(out var pid)
                || pid <= 0)
                return 0;
            return pid;
        }
        catch { return 0; }
    }

    // Read by RunningSessions.Kill (target resolution signal 4) and by tests. Nothing may treat what it reads
    // as a liveness verdict (see the type comment) - Kill only uses the wrapper pid AFTER proving, by start
    // time, that the pid is still the process this record was written for.
    public static IReadOnlyList<OwnerRecordInfo> ReadRecordsForSession(
        string? sessionId,
        IEnumerable<string>? aliases = null,
        Options? options = null)
    {
        options ??= new Options();
        var root = options.EffectiveRootDirectory;
        var ids = CandidateIds(sessionId, aliases);
        if (ids.Count == 0 || !Directory.Exists(root)) return Array.Empty<OwnerRecordInfo>();

        var records = new List<OwnerRecordInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            var path = Path.Combine(root, RecordFileName(id));
            if (!File.Exists(path) || !seen.Add(path)) continue;
            var data = ReadRecordData(path);
            if (data is null) continue;
            records.Add(new OwnerRecordInfo(
                data.SessionId,
                data.CandidateIds,
                data.WrapperPid,
                data.WrapperStartTimeUtc,
                data.Transport,
                data.ClaimedAt,
                data.MuxName,
                path,
                data.JobName));
        }
        return records;
    }

    private static OwnerRecordData? ReadRecordData(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize<OwnerRecordData>(fs);
        }
        catch { return null; }
    }

    private static List<string> CandidateIds(string? sessionId, IEnumerable<string>? aliases)
    {
        var ids = new List<string>();
        void Add(string? id)
        {
            id = (id ?? "").Trim();
            if (id.Length == 0 || id.Any(ch => !(ch <= 0x7f && (char.IsLetterOrDigit(ch) || ch is '.' or '-' or '_')))) return;
            if (!ids.Any(existing => string.Equals(existing, id, StringComparison.OrdinalIgnoreCase)))
                ids.Add(id);
        }

        Add(sessionId);
        if (aliases is not null)
            foreach (var alias in aliases) Add(alias);
        ids.Sort(StringComparer.OrdinalIgnoreCase);
        return ids;
    }

    // Same derivation as SessionLaunchClaims.ClaimFileName (readable prefix + a hash of the lowercased id),
    // with an .owner.json suffix, so a record and its claim sit side by side under the same identity.
    private static string RecordFileName(string id)
    {
        var cleaned = new string(id.Where(ch => ch <= 0x7f && (char.IsLetterOrDigit(ch) || ch is '-' or '_')).Take(36).ToArray());
        if (string.IsNullOrWhiteSpace(cleaned)) cleaned = "session";
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id.ToLowerInvariant()))).ToLowerInvariant();
        return $"{cleaned}-{hash[..16]}.owner.json";
    }

    private sealed class OwnerRecordData
    {
        public string SessionId { get; set; } = "";
        public List<string> CandidateIds { get; set; } = new();
        public int WrapperPid { get; set; }
        public DateTimeOffset? WrapperStartTimeUtc { get; set; }
        public string Transport { get; set; } = "";
        public DateTimeOffset ClaimedAt { get; set; }
        public string? MuxName { get; set; }
        // The named job the wrapper was assigned to at launch [F#7]; null when assignment failed or the launch
        // path had no Process to assign (muxd). Kill uses it for a race-free tied kill.
        public string? JobName { get; set; }
    }
}
