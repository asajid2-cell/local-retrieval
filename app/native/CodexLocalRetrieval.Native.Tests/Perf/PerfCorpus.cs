using System.Globalization;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests.Perf;

// Fabricates the inputs the perf gates measure against: multi-thousand-session stores and real .jsonl
// transcripts on disk, up to the 30-50 MB monsters a long-lived chat actually produces.
//
// Everything lands under %TEMP%\clr-perf-corpus\<deterministic key>. Disk corpora are CACHED behind a
// ".complete" marker: fabricating 30+ MB of transcripts on every suite run would cost more than the thing
// being measured, and these probes also run inside the ordinary (non-Perf) filter. Corpus content is
// seeded, so the same key always means the same bytes.
internal static class PerfCorpus
{
    internal const string Category = "Perf";

    internal static string Root => Path.Combine(Path.GetTempPath(), "clr-perf-corpus");

    // Pools kept small and repetitive on purpose: real archives are full of near-duplicate engineering
    // prose, which is what makes the naive substring search expensive rather than selective.
    private static readonly string[] Topics =
    {
        "renderer", "archive", "session", "transcript", "projection", "relay", "muxd", "pty",
        "custody", "ledger", "reclaim", "integrity", "handshake", "principal", "worktree",
    };

    private static readonly string[] Words =
    {
        "the", "store", "load", "path", "resolve", "index", "parse", "commit", "generation", "atomic",
        "backup", "snapshot", "gate", "owner", "claim", "socket", "frame", "buffer", "resize", "cursor",
        "timeout", "retry", "cancel", "queue", "thread", "await", "dispatch", "measure", "baseline",
    };

    // ---------------------------------------------------------------- in-memory stores

    // A service pointed at a private temp store. Caller disposes with TryDeleteDirectory(storeDir).
    internal static ArchiveService NewStoreService(out string storeDir, string? codexSessionsRoot = null)
    {
        storeDir = Path.Combine(Root, "store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDir);
        return new ArchiveService(
            storePath: Path.Combine(storeDir, "app-store.json"),
            codexSessionsRoot: codexSessionsRoot ?? Path.Combine(storeDir, "no-such-codex-root"),
            claudeSessionsRoot: Path.Combine(storeDir, "no-such-claude-root"));
    }

    // Populate Store.Sessions the way a loaded archive looks in memory: capped Text, no lazily-loaded
    // Messages, a spread of tags/codenames/pins. Returns the session count actually added.
    internal static int FillSessions(ArchiveService svc, int sessionCount, int seed = 1337)
    {
        var rng = new Random(seed);
        for (var i = 0; i < sessionCount; i++)
        {
            var id = SessionId(seed, i);
            var topic = Topics[i % Topics.Length];
            var day = 1 + (i % 28);
            var stamp = string.Format(CultureInfo.InvariantCulture,
                "2026-{0:D2}-{1:D2}T{2:D2}:{3:D2}:00Z", 1 + (i % 12), day, i % 24, i % 60);
            var session = new ArchiveSession
            {
                Id = id,
                Title = $"{topic} pass {i} — {Words[i % Words.Length]} {Words[(i * 7) % Words.Length]}",
                SourcePath = Path.Combine(Root, "sources", topic, $"rollout-{stamp.Replace(':', '-')}-{id}.jsonl"),
                CreatedAt = stamp,
                UpdatedAt = stamp,
                Workspace = $"z:/proj/{topic}",
                WorkspaceName = topic,
                Model = i % 3 == 0 ? "gpt-5-codex" : "claude-opus-4-8",
                Tool = i % 4 == 0 ? "claude" : "codex",
                MessageCount = 12 + (i % 240),
                ContentLoaded = false,
                Text = Body(rng, 2400),
                Pinned = i % 97 == 0,
                Starred = i % 53 == 0,
                Archived = i % 79 == 0,
            };
            session.Tags.Add("archive");
            session.Tags.Add(topic);
            if (i % 5 == 0) session.Tags.Add("code");
            if (i % 31 == 0) session.SpecialPhrases.Add($"{topic}-{i}");
            svc.Store.Sessions[id] = session;
        }
        return sessionCount;
    }

    // ---------------------------------------------------------------- on-disk transcripts

    // The idiom from ArchiveServiceTests.cs:16 — a session_meta line plus a user/agent exchange, written
    // exactly as Codex emits it. Kept here verbatim so perf corpora and the functional suite agree on shape.
    internal static string WriteRollout(string dir, string fileName, string id, string isoTimestamp, string userText)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllLines(path, new[]
        {
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"type\":\"session_meta\",\"cwd\":\"z:/proj\"}}",
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"" + userText + "\"}}",
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"on it\"}}",
        });
        return path;
    }

    // A multi-turn rollout of `turns` exchanges. Message bodies go through the JSON serializer (generated
    // prose contains quotes/newlines), unlike the fixed-text WriteRollout idiom above.
    internal static string WriteTranscript(string dir, string id, string isoTimestamp, int turns, int seed)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-{isoTimestamp.Replace(':', '-')}-{id}.jsonl");
        var rng = new Random(seed);
        using var writer = new StreamWriter(path, append: false, Encoding.UTF8);
        writer.WriteLine(MetaLine(id, isoTimestamp));
        for (var t = 0; t < turns; t++)
        {
            writer.WriteLine(EventLine(isoTimestamp, "user_message", Body(rng, 180)));
            writer.WriteLine(EventLine(isoTimestamp, "agent_message", Body(rng, 900)));
        }
        return path;
    }

    // A single transcript grown to at least `targetBytes` (use 30-50 MB to reproduce the long-chat case).
    // Streamed, never materialized in memory. Returns the path; its length is >= targetBytes.
    internal static string WriteLargeTranscript(string dir, string id, long targetBytes, int seed = 4242)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"rollout-2026-03-01T00-00-00-{id}.jsonl");
        var rng = new Random(seed);
        const string stamp = "2026-03-01T00:00:00Z";
        using (var writer = new StreamWriter(path, append: false, Encoding.UTF8, bufferSize: 1 << 20))
        {
            writer.WriteLine(MetaLine(id, stamp));
            long written = 0;
            var turn = 0;
            while (written < targetBytes)
            {
                var user = EventLine(stamp, "user_message", Body(rng, 220));
                var agent = EventLine(stamp, "agent_message", Body(rng, 1600));
                writer.WriteLine(user);
                writer.WriteLine(agent);
                written += user.Length + agent.Length + 4;
                turn++;
                if (turn % 512 == 0) writer.Flush();
            }
        }
        return path;
    }

    // A cached transcript directory: `sessions` ordinary rollouts plus one large transcript of
    // `largeBytes` (0 to skip). Same arguments => same directory, fabricated once per machine.
    internal static string EnsureTranscriptCorpus(string label, int sessions, int turnsPerSession, long largeBytes, int seed = 7)
    {
        var key = $"{label}-{sessions}x{turnsPerSession}-{largeBytes / (1024 * 1024)}mb-s{seed}-v1";
        var dir = Path.Combine(Root, key);
        var marker = Path.Combine(dir, ".complete");
        if (File.Exists(marker)) return dir;

        TryDeleteDirectory(dir);
        Directory.CreateDirectory(dir);
        for (var i = 0; i < sessions; i++)
        {
            var stamp = string.Format(CultureInfo.InvariantCulture,
                "2026-{0:D2}-{1:D2}T{2:D2}:00:00Z", 1 + (i % 12), 1 + (i % 28), i % 24);
            WriteTranscript(dir, SessionId(seed, i), stamp, turnsPerSession, seed + i);
        }
        if (largeBytes > 0) WriteLargeTranscript(dir, SessionId(seed, 999_001), largeBytes, seed);
        File.WriteAllText(marker, key);
        return dir;
    }

    internal static long DirectoryBytes(string dir) =>
        Directory.Exists(dir)
            ? new DirectoryInfo(dir).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).Sum(f => f.Length)
            : 0;

    internal static void TryDeleteDirectory(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ---------------------------------------------------------------- internals

    private static string SessionId(int seed, int i) =>
        string.Format(CultureInfo.InvariantCulture, "019f{0:x4}-perf-4{1:x3}-9{2:x3}-{3:x12}", seed & 0xFFFF, i & 0xFFF, (i >> 12) & 0xFFF, (long)i * 0x9E3779B1L & 0xFFFFFFFFFFFF);

    private static string MetaLine(string id, string stamp) =>
        "{\"timestamp\":\"" + stamp + "\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + id
        + "\",\"id\":\"" + id + "\",\"type\":\"session_meta\",\"cwd\":\"z:/proj/perf\"}}";

    private static string EventLine(string stamp, string kind, string message) =>
        "{\"timestamp\":\"" + stamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"" + kind
        + "\",\"message\":" + JsonSerializer.Serialize(message) + "}}";

    private static string Body(Random rng, int approxChars)
    {
        var sb = new StringBuilder(approxChars + 32);
        while (sb.Length < approxChars)
        {
            sb.Append(Words[rng.Next(Words.Length)]).Append(' ');
            if (rng.Next(11) == 0) sb.Append(Topics[rng.Next(Topics.Length)]).Append(' ');
        }
        return sb.ToString();
    }
}

// Append-only sink the perf probes record named numbers into. perf_gates.ps1 points CLR_PERF_RESULTS at a
// per-tag file and folds the lines into results.json; with the variable unset (an ordinary suite run) the
// lines still land in %TEMP% and on stdout, so a number is never silently lost.
internal static class PerfRecord
{
    private static readonly object Gate = new();

    internal static string SinkPath =>
        Environment.GetEnvironmentVariable("CLR_PERF_RESULTS") is { Length: > 0 } p
            ? p
            : Path.Combine(Path.GetTempPath(), "clr-perf-measurements.jsonl");

    internal static void Measure(string name, double value, string unit) => Write("measure", name, value, unit);

    // Snapshot every PerfCounters value under "<probe>.<counterName>". Counters read 0 until the consuming
    // leaves insert call sites; recording them now fixes the shape of the before-picture.
    internal static void Counters(string probe)
    {
        foreach (var kv in PerfCounters.Snapshot()) Write("counter", probe + "." + kv.Key, kv.Value, "count");
    }

    private static void Write(string kind, string name, double value, string unit)
    {
        var line = JsonSerializer.Serialize(new
        {
            kind,
            name,
            value,
            unit,
            at = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
        });
        Console.WriteLine("[perf] " + line);
        try
        {
            lock (Gate)
            {
                var dir = Path.GetDirectoryName(SinkPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(SinkPath, line + Environment.NewLine, Encoding.UTF8);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
