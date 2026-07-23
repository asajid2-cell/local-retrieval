using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Native.Tests.Perf;

namespace CodexLocalRetrieval.Native.Tests;

// ReadForSession used to cost an all-time scan: every line in every monthly file was UTF-8 decoded, JSON
// deserialized and run through Normalize's four compiled redaction regexes BEFORE the id filter got a look.
// For a quiet session that is the entire ledger to find two events. These tests pin the two things that fix
// it — the per-session sidecar index and the raw-byte pre-parse screen — and, more importantly, pin that
// neither one changed a single result.
[TestClass]
public sealed class LedgerReadTests
{
    private const string TargetSession = "target-session-7f3a";
    private const int NoiseSessions = 500;
    private const int TotalEvents = 100_000;

    [TestMethod]
    [TestCategory(PerfCorpus.Category)]
    [Timeout(180_000)]
    public void ReadForSession_QuietSessionInHundredThousandEventLedger_IsBounded()
    {
        using var dir = NewTempDir();
        var months = new[] { "2026-05", "2026-06", "2026-07" };
        WriteBulkLedger(dir.Path, months);
        var options = new SessionEventLedger.Options(dir.Path, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        // First read pays the one-time backfill (and the JIT). Steady state is what the app experiences and
        // what this test gates; the sweep cost is recorded separately rather than hidden.
        var backfillWatch = Stopwatch.StartNew();
        PerfCounters.Reset();
        var warm = SessionEventLedger.ReadForSession(TargetSession, max: 12, options: options);
        backfillWatch.Stop();
        var backfillBytes = PerfCounters.Snapshot()["ledgerBytesRead"];
        Assert.HasCount(2, warm);

        PerfCounters.Reset();
        var watch = Stopwatch.StartNew();
        var events = SessionEventLedger.ReadForSession(TargetSession, max: 12, options: options);
        watch.Stop();
        var bytesRead = PerfCounters.Snapshot()["ledgerBytesRead"];

        PerfRecord.Measure("ledger.readForSession.quiet.ms", watch.Elapsed.TotalMilliseconds, "ms");
        PerfRecord.Measure("ledger.readForSession.quiet.bytesRead", bytesRead, "bytes");
        PerfRecord.Measure("ledger.readForSession.backfill.ms", backfillWatch.Elapsed.TotalMilliseconds, "ms");
        PerfRecord.Measure("ledger.readForSession.backfill.bytesRead", backfillBytes, "bytes");

        Assert.HasCount(2, events);
        Assert.AreEqual("target.newer", events[0].Kind);
        Assert.AreEqual("target.older", events[1].Kind);
        Assert.IsLessThan(100d, watch.Elapsed.TotalMilliseconds, $"ReadForSession took {watch.Elapsed.TotalMilliseconds:N1} ms.");
        Assert.IsLessThan(256L * 1024, bytesRead, $"ReadForSession read {bytesRead:N0} ledger bytes.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public void ReadForSession_IndexedAndScannedResultsAreIdentical()
    {
        using var dir = NewTempDir();
        WriteMixedLedger(dir.Path);
        var options = new SessionEventLedger.Options(dir.Path, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        foreach (var probe in MixedProbes())
        {
            var scanned = SessionEventLedger.ReadForSessionScanOnly(probe.Id, probe.Aliases, probe.Max, options);
            var indexed = SessionEventLedger.ReadForSession(probe.Id, probe.Aliases, probe.Max, options);
            AssertSameEvents(scanned, indexed, probe.Name);
        }

        // The scan path is the one that runs the byte screen, so a probe that finds nothing has to be proven
        // to find nothing for the right reason: at least one probe must return events on both paths.
        Assert.IsGreaterThan(0, SessionEventLedger.ReadForSession("alias-child", max: 50, options: options).Count);

        // Non-vacuity for the non-ASCII probes — these are exactly the ids the ASCII-only screen used to drop.
        // Each count would be 0 (empty on BOTH paths, so AssertSameEvents alone stays green) before the guard.
        // The mixed set must return BOTH the ASCII primary's event and the non-ASCII alias's, not just one.
        Assert.IsGreaterThan(0, SessionEventLedger.ReadForSession("SESSION-É", max: 50, options: options).Count);
        Assert.IsGreaterThan(0, SessionEventLedger.ReadForSession("café-session", max: 50, options: options).Count);
        Assert.HasCount(2, SessionEventLedger.ReadForSession("uni-parent", ["SÉANCE"], 50, options));
    }

    [TestMethod]
    [Timeout(120_000)]
    public void ReadForSession_MissingIndexBackfillsToIdenticalResults()
    {
        using var dir = NewTempDir();
        WriteMixedLedger(dir.Path);
        var options = new SessionEventLedger.Options(dir.Path, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        var before = MixedProbes().ToDictionary(p => p.Name, p => SessionEventLedger.ReadForSession(p.Id, p.Aliases, p.Max, options));
        var indexDir = SessionEventLedger.IndexDirectory(dir.Path);
        Assert.IsTrue(Directory.Exists(indexDir), "index directory was never created");

        Directory.Delete(indexDir, recursive: true);
        Assert.IsFalse(Directory.Exists(indexDir));

        foreach (var probe in MixedProbes())
            AssertSameEvents(before[probe.Name], SessionEventLedger.ReadForSession(probe.Id, probe.Aliases, probe.Max, options), probe.Name + " after backfill");

        Assert.IsTrue(File.Exists(Path.Combine(indexDir, ".complete")), "backfill did not stamp the completeness marker");

        // An append after a rebuild must land in the rebuilt index, not be stranded by it.
        Assert.IsTrue(SessionEventLedger.TryAppend(
            SessionEventLedger.Create("post.backfill", "appended after rebuild", "alias-parent"), out var detail, options), detail);
        var after = SessionEventLedger.ReadForSession("alias-parent", max: 50, options: options);
        Assert.AreEqual("post.backfill", after[0].Kind);
        AssertSameEvents(SessionEventLedger.ReadForSessionScanOnly("alias-parent", max: 50, options: options), after, "post-append");
    }

    // TryAppend records the sidecar index entry BEFORE writing the event bytes, all under one per-file mutex.
    // The existing SessionEventLedgerTests only proves the ledger FILE ends uncorrupted; nothing proves the
    // index offsets are right afterwards. A wrong offset recorded under contention makes TryReadEventAt's
    // re-verification (EventMatchesAnyId) silently DROP the hit, so the index read returns fewer events than
    // the scan while every file-level test stays green. This is the assertion the scan-vs-index equivalence
    // catches: the scan path never consults the index, so identity between the two pins every offset.
    [TestMethod]
    [Timeout(120_000)]
    public void TryAppend_ConcurrentAppendsKeepSidecarIndexConsistent()
    {
        using var dir = NewTempDir();
        // One month => one monthly file => one mutex, so all 64 appends genuinely contend.
        var options = new SessionEventLedger.Options(dir.Path, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        Parallel.For(0, 64, i =>
        {
            var appended = SessionEventLedger.TryAppend(
                SessionEventLedger.Create("event." + i, "summary " + i, "s-" + i, sessionIds: ["s-" + i, "shared-alias"]),
                out var detail, options);
            Assert.IsTrue(appended, detail);
        });

        // (a) Every per-session id resolves to exactly its own single event.
        for (var i = 0; i < 64; i++)
        {
            var one = SessionEventLedger.ReadForSession("s-" + i, max: 50, options: options);
            Assert.HasCount(1, one);
            Assert.AreEqual("event." + i, one[0].Kind);
        }

        // (b) The shared alias sees all 64, each event exactly once.
        var shared = SessionEventLedger.ReadForSession("shared-alias", max: 200, options: options);
        Assert.HasCount(64, shared);
        Assert.HasCount(64, shared.Select(e => e.Kind).Distinct().ToList());

        // (c) A sample of per-session ids: the index-backed read must equal the scan (which ignores the index
        // entirely). This is what a wrong recorded offset breaks — the index read would drop or misplace it.
        for (var i = 0; i < 64; i += 8)
        {
            var id = "s-" + i;
            AssertSameEvents(
                SessionEventLedger.ReadForSessionScanOnly(id, max: 50, options: options),
                SessionEventLedger.ReadForSession(id, max: 50, options: options),
                id);
        }

        // (d) The shared-alias read must also match its scan — pinning membership AND newest-first ordering.
        AssertSameEvents(
            SessionEventLedger.ReadForSessionScanOnly("shared-alias", max: 200, options: options),
            shared,
            "shared-alias");
    }

    private sealed record Probe(string Name, string? Id, string[]? Aliases, int Max);

    private static IEnumerable<Probe> MixedProbes() =>
    [
        new("plain", "quiet-one", null, 50),
        new("alias-only", "alias-child", null, 50),
        new("alias-pair", "alias-parent", ["alias-child"], 50),
        new("detail-value", "detail-linked-9", null, 50),
        new("detail-csv-part", "csv-b", null, 50),
        new("legacy-raw-line", "legacy-session", null, 50),
        new("limit-of-one", "chatty", null, 1),
        new("limit-truncates", "chatty", null, 3),
        // Not file-name safe, so the sidecar cannot answer it and the read must fall back to the screened scan.
        new("unindexable-id", "sess:42+beta", null, 50),
        new("unindexable-mixed", "quiet-one", ["sess:42+beta"], 50),
        new("device-name-id", "nul", null, 50),
        new("absent", "no-such-session", null, 50),
        new("case-insensitive", "QUIET-ONE", null, 50),
        // Non-ASCII ids: TryIndexKey admits only [A-Za-z0-9._~-], so every non-ASCII id is unindexable and is
        // forced down the screened scan. Before the BuildIdScreen non-ASCII guard the ASCII-only fold silently
        // dropped an id that matched only via Unicode case folding, so ReadForSession returned FEWER events
        // than the unscreened reader. Each probe still equals its scan; the count assertions in the identity
        // test prove they are not passing vacuously (empty == empty).
        new("nonascii-casefold", "SESSION-É", null, 50),   // stored "session-é": matches only via non-ASCII case fold
        new("nonascii-verbatim", "café-session", null, 50),
        new("nonascii-alias", "uni-parent", ["SÉANCE"], 50), // ASCII primary + non-ASCII (case-differing) alias
    ];

    private static void AssertSameEvents(IReadOnlyList<SessionEvent> expected, IReadOnlyList<SessionEvent> actual, string probe)
    {
        Assert.AreEqual(expected.Count, actual.Count, $"{probe}: count differs");
        for (var i = 0; i < expected.Count; i++)
        {
            Assert.AreEqual(expected[i].Id, actual[i].Id, $"{probe}[{i}]: id differs");
            Assert.AreEqual(expected[i].Kind, actual[i].Kind, $"{probe}[{i}]: kind differs");
            Assert.AreEqual(expected[i].At, actual[i].At, $"{probe}[{i}]: timestamp differs");
            Assert.AreEqual(expected[i].Summary, actual[i].Summary, $"{probe}[{i}]: summary differs");
        }
    }

    // 100k events over three monthly files: 500 chatty sessions plus exactly two for the target. Written
    // straight to disk rather than through TryAppend, both because 100k flush-to-disk appends would dominate
    // the run and because it is the honest shape of a ledger that predates the index.
    private static void WriteBulkLedger(string root, string[] months)
    {
        Directory.CreateDirectory(root);
        var perMonth = TotalEvents / months.Length;
        var noise = 0;
        for (var m = 0; m < months.Length; m++)
        {
            var path = Path.Combine(root, "events-" + months[m] + ".jsonl");
            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            for (var i = 0; i < perMonth; i++)
            {
                writer.WriteLine(RawEvent(
                    id: "n" + noise,
                    kind: "noise." + noise,
                    at: months[m] + "-10T00:00:00.0000000Z",
                    sessionId: "noise-" + (noise % NoiseSessions),
                    summary: "routine ledger traffic " + noise));
                noise++;
            }
            if (m == months.Length - 1)
            {
                writer.WriteLine(RawEvent("t1", "target.older", months[m] + "-11T00:00:00.0000000Z", TargetSession, "quiet session first event"));
                writer.WriteLine(RawEvent("t2", "target.newer", months[m] + "-11T00:00:01.0000000Z", TargetSession, "quiet session second event"));
            }
        }
    }

    // Deliberately heterogeneous: two months, both write paths, aliases, detail-carried ids, a comma-joined
    // detail value, a hand-written legacy line that only Normalize can clean, and a trailing line with no
    // newline terminator.
    private static void WriteMixedLedger(string root)
    {
        Directory.CreateDirectory(root);
        var may = new SessionEventLedger.Options(root, DateTimeOffset.Parse("2026-05-04T00:00:00Z"));
        var july = new SessionEventLedger.Options(root, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("quiet.may", "may event", "quiet-one"), out var d1, may), d1);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("mux.started", "aliased", "alias-parent", sessionIds: ["alias-parent", "alias-child"]), out var d2, july), d2);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("quiet.july", "july event", "quiet-one"), out var d3, july), d3);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("odd.id", "unindexable id", "sess:42+beta"), out var d4, july), d4);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("device.id", "dos device name as id", "nul"), out var d5, july), d5);
        Assert.IsTrue(SessionEventLedger.TryAppend(
            SessionEventLedger.Create("linked", "detail linked", "holder-1", details: new Dictionary<string, string> { ["peer"] = "detail-linked-9" }), out var d6, july), d6);
        Assert.IsTrue(SessionEventLedger.TryAppend(
            SessionEventLedger.Create("linked.csv", "detail csv", "holder-2", details: new Dictionary<string, string> { ["peers"] = "csv-a, csv-b, csv-c" }), out var d7, july), d7);
        for (var i = 0; i < 9; i++)
            Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("chatty." + i, "chatter " + i, "chatty"), out var dc, july), dc);

        // Non-ASCII session ids. Unindexable (TryIndexKey rejects them), so answerable only via the screened
        // scan. "session-é" and the "SÉANCE" alias are probed in the OTHER case, which matches only
        // through Unicode case folding the ASCII-only byte screen cannot do; "café-session" is probed
        // verbatim.
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("accent.lower", "accented stored id", "session-é"), out var u1, july), u1);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("accent.verbatim", "accented stored id", "café-session"), out var u2, july), u2);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("uni.parent", "ascii primary of a mixed alias set", "uni-parent"), out var u3, july), u3);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("uni.alias", "non-ascii alias member", "séance"), out var u4, july), u4);

        // Written behind the ledger's back, so only a backfill (or the scan) can see them.
        var julyFile = Path.Combine(root, "events-2026-07.jsonl");
        using (var writer = new StreamWriter(julyFile, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            writer.WriteLine(JsonSerializer.Serialize(new
            {
                id = "legacy",
                at = "2026-07-08T00:00:00.0000000Z",
                kind = "resume.failed",
                severity = "error",
                source = "test",
                sessionId = "legacy-session",
                summary = @"failed with token=ghp_FAKEexampleKEYnotreal0000000 at C:\Users\Ahmed\repo\chat.jsonl",
                details = new Dictionary<string, string> { ["command"] = "codex resume x", ["safe"] = "quiet-one" }
            }));
            writer.WriteLine("{ not json at all");
        }

        // An unterminated final line, parked in the month that takes no further appends. TryAppend would
        // otherwise glue its next event onto this one and corrupt both for any line-oriented reader — a
        // pre-existing hazard of hand-editing a ledger, not something this node changes.
        var mayFile = Path.Combine(root, "events-2026-05.jsonl");
        using var tail = new StreamWriter(mayFile, append: true, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        tail.Write(RawEvent("tail", "tail.event", "2026-05-04T00:00:09.0000000Z", "quiet-one", "final unterminated line"));
    }

    private static string RawEvent(string id, string kind, string at, string sessionId, string summary)
        => JsonSerializer.Serialize(new { id, at, kind, severity = "info", source = "test", sessionId, summary });

    private static TempDir NewTempDir()
        => new(Path.Combine(Path.GetTempPath(), "clr-ledger-read-" + Guid.NewGuid().ToString("N")));

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
