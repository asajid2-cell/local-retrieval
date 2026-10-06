using System.Text;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class TranscriptSearchIndexTests
{
    [TestMethod]
    public async Task DecodedText_IsSearchable_AndHitCarriesRawByteOffset()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "decoded",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"decoded","cwd":"C:\\repo"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"quoted \"alpha\" line\nsecond beta sentinel"}}
            """);
        var session = fixture.Session("decoded", path);

        await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"));
        var result = await fixture.Index.SearchAsync(
            "alpha second beta sentinel",
            10,
            id => id == session.Id ? session : null);

        var hit = result.Hits.Single();
        Assert.AreEqual("decoded", hit.Session.Id);
        Assert.AreEqual("user", hit.Provenance);
        Assert.IsTrue(hit.Navigable);
        Assert.IsTrue(hit.ByteOffset > 0);
        using var source = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        source.Position = hit.ByteOffset;
        var bytes = new byte[checked((int)hit.ByteLength)];
        _ = source.Read(bytes);
        var raw = Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(raw, "\\\"alpha\\\"");
        StringAssert.Contains(raw, "\\nsecond beta sentinel");
    }

    [TestMethod]
    public async Task UserTurn_RanksAheadOfSamePhraseInToolOutput()
    {
        using var fixture = new SearchFixture();
        var userPath = fixture.WriteCodex(
            "user-target",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"user-target"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"cobalt aperture ranking sentinel"}}
            """);
        var toolPath = fixture.WriteCodex(
            "tool-only",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"tool-only"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"response_item","payload":{"type":"function_call_output","output":"cobalt aperture ranking sentinel"}}
            """);
        var user = fixture.Session("user-target", userPath);
        var tool = fixture.Session("tool-only", toolPath);
        var sessions = new[] { user, tool };

        await fixture.Index.SyncAsync(sessions, fixture.Sources("codex"));
        var result = await fixture.Index.SearchAsync(
            "cobalt aperture ranking sentinel",
            10,
            id => sessions.FirstOrDefault(session => session.Id == id));

        Assert.AreEqual(2, result.Hits.Count);
        Assert.AreEqual("user-target", result.Hits[0].Session.Id);
        Assert.AreEqual("user", result.Hits[0].Provenance);
        Assert.AreEqual("tool", result.Hits[1].Provenance);
    }

    [TestMethod]
    public async Task AppendOnlyGrowth_IndexesOnlyDelta()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "append",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"append"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"original turn sentinel"}}
            """);
        var session = fixture.Session("append", path);
        var first = await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"));
        Assert.AreEqual(1, first.RebuiltFiles);

        await File.AppendAllTextAsync(
            path,
            """
            {"timestamp":"2026-08-03T00:00:02Z","type":"event_msg","payload":{"type":"agent_message","message":"appended delta sentinel"}}

            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        var second = await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"));

        Assert.AreEqual(1, second.AppendedFiles);
        Assert.AreEqual(0, second.RebuiltFiles);
        var result = await fixture.Index.SearchAsync(
            "appended delta sentinel",
            10,
            id => id == session.Id ? session : null);
        Assert.AreEqual("append", result.Hits.Single().Session.Id);
    }

    [TestMethod]
    public async Task SameSizeMutation_RebuildsOnlyChangedFile()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "mutation",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"mutation"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"alpha mutation sentinel"}}
            """);
        var session = fixture.Session("mutation", path);
        await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"));
        var original = await File.ReadAllTextAsync(path);
        var changed = original.Replace("alpha", "omega", StringComparison.Ordinal);
        Assert.AreEqual(original.Length, changed.Length);
        await File.WriteAllTextAsync(path, changed);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));

        var sync = await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"));
        var oldResult = await fixture.Index.SearchAsync(
            "alpha mutation sentinel",
            10,
            id => id == session.Id ? session : null);
        var newResult = await fixture.Index.SearchAsync(
            "omega mutation sentinel",
            10,
            id => id == session.Id ? session : null);

        Assert.AreEqual(1, sync.RebuiltFiles);
        Assert.AreEqual(0, sync.AppendedFiles);
        Assert.AreEqual(0, oldResult.Hits.Count);
        Assert.AreEqual("mutation", newResult.Hits.Single().Session.Id);
    }

    [TestMethod]
    public async Task LiveSharedWriter_RemainsWritableDuringIndex()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "live",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"live"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"shared writer sentinel"}}
            """);
        var session = fixture.Session("live", path);
        await using var writer = new FileStream(
            path,
            FileMode.Append,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete);

        await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"));
        var extra = Encoding.UTF8.GetBytes(
            "{\"timestamp\":\"2026-08-03T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"writer survived\"}}\n");
        await writer.WriteAsync(extra);
        await writer.FlushAsync();

        Assert.IsTrue(new FileInfo(path).Length > extra.Length);
    }

    [TestMethod]
    public async Task DirtySetSync_IndexesOnlyReportedFile()
    {
        using var fixture = new SearchFixture();
        var reported = fixture.WriteCodex(
            "reported",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"reported"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"reported baseline sentinel"}}
            """);
        var missed = fixture.WriteCodex(
            "missed",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"missed"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"missed baseline sentinel"}}
            """);
        var sessions = new[] { fixture.Session("reported", reported), fixture.Session("missed", missed) };
        await fixture.Index.SyncAsync(sessions, fixture.Sources("codex"));

        // Only the missed file changes, but the dirty set names the reported file: the walk is skipped and
        // the changed file is not touched.
        await File.AppendAllTextAsync(
            missed,
            """
            {"timestamp":"2026-08-03T00:00:02Z","type":"event_msg","payload":{"type":"agent_message","message":"unreported delta sentinel"}}

            """);
        File.SetLastWriteTimeUtc(missed, DateTime.UtcNow.AddSeconds(1));
        var sync = await fixture.Index.SyncAsync(
            sessions,
            fixture.Sources("codex"),
            changedPaths: new[] { reported });

        Assert.AreEqual(0, sync.AppendedFiles);
        Assert.AreEqual(0, sync.RebuiltFiles);
        var absent = await fixture.Index.SearchAsync(
            "unreported delta sentinel",
            10,
            id => sessions.FirstOrDefault(session => session.Id == id));
        Assert.AreEqual(0, absent.Hits.Count, "a file outside the dirty set must not be indexed");
    }

    [TestMethod]
    public async Task DirtySetSync_MakesAppendedTextSearchable()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "dirty-append",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"dirty-append"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"dirty baseline sentinel"}}
            """);
        var session = fixture.Session("dirty-append", path);
        await fixture.Index.SyncAsync(new[] { session }, fixture.Sources("codex"));

        await File.AppendAllTextAsync(
            path,
            """
            {"timestamp":"2026-08-03T00:00:02Z","type":"event_msg","payload":{"type":"agent_message","message":"dirty appended sentinel"}}

            """);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        var sync = await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"),
            changedPaths: new[] { path });

        Assert.AreEqual(1, sync.AppendedFiles);
        var result = await fixture.Index.SearchAsync(
            "dirty appended sentinel",
            10,
            id => id == session.Id ? session : null);
        Assert.AreEqual("dirty-append", result.Hits.Single().Session.Id);
    }

    [TestMethod]
    public async Task FullSync_PicksUpFileTheDirtySetMissed()
    {
        using var fixture = new SearchFixture();
        var known = fixture.WriteCodex(
            "known",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"known"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"known baseline sentinel"}}
            """);
        var session = fixture.Session("known", known);
        await fixture.Index.SyncAsync(new[] { session }, fixture.Sources("codex"));

        // A file the watcher never reported: only the reconciliation walk can find it.
        var late = fixture.WriteCodex(
            "late",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"late"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"late arrival sentinel"}}
            """);
        var lateSession = fixture.Session("late", late);
        await fixture.Index.SyncAsync(
            new[] { session, lateSession },
            fixture.Sources("codex"),
            changedPaths: new[] { known });
        var beforeWalk = await fixture.Index.SearchAsync(
            "late arrival sentinel",
            10,
            id => id == lateSession.Id ? lateSession : null);
        Assert.AreEqual(0, beforeWalk.Hits.Count);

        await fixture.Index.SyncAsync(new[] { session, lateSession }, fixture.Sources("codex"));
        var afterWalk = await fixture.Index.SearchAsync(
            "late arrival sentinel",
            10,
            id => id == lateSession.Id ? lateSession : null);
        Assert.AreEqual("late", afterWalk.Hits.Single().Session.Id);
    }

    [TestMethod]
    public async Task EmptyDirtySet_WritesNothing()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "quiet",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"quiet"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"quiet sentinel"}}
            """);
        var session = fixture.Session("quiet", path);
        await fixture.Index.SyncAsync(new[] { session }, fixture.Sources("codex"));

        var dbPath = fixture.Index.DatabasePath;
        var before = File.GetLastWriteTimeUtc(dbPath);
        var sync = await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"),
            changedPaths: Array.Empty<string>());
        var after = File.GetLastWriteTimeUtc(dbPath);

        Assert.AreEqual(0, sync.IndexedFiles);
        Assert.AreEqual(0, sync.RebuiltFiles);
        Assert.AreEqual(0, sync.AppendedFiles);
        Assert.AreEqual(before, after, "a cycle with no changes must not write the index");
    }

    // The gate for the in-memory file-state map: the `files` table (~9.4k rows, ~3 MB live) must be read
    // once per process, not on every sync. A dirty-set pass in steady state may read no row of it.
    [TestMethod]
    public async Task IncrementalSync_DoesNotReloadTheFileTable()
    {
        using var fixture = new SearchFixture();
        const int count = 64;
        var sessions = new List<ArchiveSession>();
        for (var i = 0; i < count; i++)
        {
            var id = $"bulk-{i:D2}";
            var path = fixture.WriteCodex(
                id,
                "{\"timestamp\":\"2026-08-03T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\"}}\n"
                + "{\"timestamp\":\"2026-08-03T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"baseline sentinel " + id + "\"}}");
            sessions.Add(fixture.Session(id, path));
        }

        await fixture.Index.SyncAsync(sessions, fixture.Sources("codex"));

        // A fresh instance over the populated db models a process restart: the table is read once, at
        // startup, and every later pass works from the map.
        using var warm = new TranscriptSearchIndex(fixture.Index.DatabasePath);
        await warm.SyncAsync(sessions, fixture.Sources("codex"));
        Assert.AreEqual(count, warm.FileRowsRead, "the file table is materialised exactly once");

        var target = sessions[0].SourcePath!;
        await File.AppendAllTextAsync(
            target,
            """
            {"timestamp":"2026-08-03T00:00:02Z","type":"event_msg","payload":{"type":"agent_message","message":"appended sentinel"}}

            """);
        File.SetLastWriteTimeUtc(target, DateTime.UtcNow.AddSeconds(1));

        var rowsBefore = warm.FileRowsRead;
        var bytesBefore = warm.FileStateBytesRead;
        var sync = await warm.SyncAsync(
            sessions,
            fixture.Sources("codex"),
            changedPaths: new[] { target });

        Assert.AreEqual(1, sync.AppendedFiles, "the dirty-set pass must do real work");
        Assert.AreEqual(rowsBefore, warm.FileRowsRead, "an incremental pass must not reload the file table");
        Assert.AreEqual(bytesBefore, warm.FileStateBytesRead, "an incremental pass must read no file rows");
        Assert.AreEqual(count, warm.Status.TotalFiles, "the running file count must stay correct");
        Assert.AreEqual(
            sessions.Sum(session => new FileInfo(session.SourcePath!).Length),
            warm.Status.TotalBytes,
            "the running byte total must stay correct");

        // A full pass reconciles: a removed file leaves the count, still without reading the table.
        File.Delete(sessions[1].SourcePath!);
        var remaining = sessions.Where((_, index) => index != 1).ToList();
        await warm.SyncAsync(remaining, fixture.Sources("codex"));
        Assert.AreEqual(count - 1, warm.Status.TotalFiles);
        Assert.AreEqual(rowsBefore, warm.FileRowsRead, "a reconciliation pass reads no file rows once warm");
    }

    // The gate for the cached next-turn ordinal: repeated appends to one file must run the `turns`
    // ordinal query once (the first append seeds the cache), not once per append.
    [TestMethod]
    public async Task RepeatedAppends_RunTheOrdinalQueryOnce()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "ordinal",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"ordinal"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"ordinal baseline sentinel"}}
            """);
        var session = fixture.Session("ordinal", path);
        await fixture.Index.SyncAsync(new[] { session }, fixture.Sources("codex"));

        // A fresh instance over the populated db models a process restart: the file state it loads has no
        // cached ordinal, so the first append seeds it and every later append reads the cache.
        using var warm = new TranscriptSearchIndex(fixture.Index.DatabasePath);
        await warm.SyncAsync(new[] { session }, fixture.Sources("codex"));
        Assert.AreEqual(0, warm.OrdinalQueries, "a pass with no appends runs no ordinal query");

        const int appends = 6;
        for (var i = 0; i < appends; i++)
        {
            await File.AppendAllTextAsync(
                path,
                "{\"timestamp\":\"2026-08-03T00:00:0" + (i + 2)
                + "Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"ordinal append "
                + i + "\"}}\n\n");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(i + 1));
            var sync = await warm.SyncAsync(
                new[] { session },
                fixture.Sources("codex"),
                changedPaths: new[] { path });
            Assert.AreEqual(1, sync.AppendedFiles, $"append {i} must append");
        }

        Assert.AreEqual(
            1,
            warm.OrdinalQueries,
            "the ordinal query runs once per file per process, not once per append");
    }

    // The cached ordinal must survive the real index transitions: appends keep the ordinals contiguous,
    // and a rebuild restarts them at 0 so the next append continues from the rebuilt count, not the stale
    // cache.
    [TestMethod]
    public async Task TurnOrdinals_StayContiguous_AcrossAppendAndRebuild()
    {
        using var fixture = new SearchFixture();
        var path = fixture.WriteCodex(
            "contiguous",
            """
            {"timestamp":"2026-08-03T00:00:00Z","type":"session_meta","payload":{"id":"contiguous"}}
            {"timestamp":"2026-08-03T00:00:01Z","type":"event_msg","payload":{"type":"user_message","message":"baseline sentinel"}}
            """);
        var session = fixture.Session("contiguous", path);
        await fixture.Index.SyncAsync(new[] { session }, fixture.Sources("codex"));

        await File.AppendAllTextAsync(
            path,
            "{\"timestamp\":\"2026-08-03T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"beta append one\"}}\n\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(1));
        await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"),
            changedPaths: new[] { path });
        await File.AppendAllTextAsync(
            path,
            "{\"timestamp\":\"2026-08-03T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"gamma append two\"}}\n\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(2));
        await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"),
            changedPaths: new[] { path });

        var afterAppends = Ordinals(fixture.Index.DatabasePath, path);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, afterAppends.Count).ToArray(),
            afterAppends,
            "appends must leave the ordinals contiguous");

        // A same-size mutation in the tail is not an append: the file rebuilds, its ordinals restart at 0,
        // and the cached next ordinal must be refreshed rather than reused.
        var original = await File.ReadAllTextAsync(path);
        var changed = original.Replace("gamma", "gammx", StringComparison.Ordinal);
        Assert.AreEqual(original.Length, changed.Length);
        await File.WriteAllTextAsync(path, changed);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(3));
        var sync = await fixture.Index.SyncAsync(new[] { session }, fixture.Sources("codex"));
        Assert.AreEqual(1, sync.RebuiltFiles);

        var afterRebuild = Ordinals(fixture.Index.DatabasePath, path);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, afterRebuild.Count).ToArray(),
            afterRebuild,
            "a rebuild must restart the ordinals at 0, contiguous");

        await File.AppendAllTextAsync(
            path,
            "{\"timestamp\":\"2026-08-03T00:00:04Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"delta append three\"}}\n\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddSeconds(4));
        await fixture.Index.SyncAsync(
            new[] { session },
            fixture.Sources("codex"),
            changedPaths: new[] { path });

        var afterRebuildAppend = Ordinals(fixture.Index.DatabasePath, path);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, afterRebuildAppend.Count).ToArray(),
            afterRebuildAppend,
            "an append after a rebuild must continue from the rebuilt count");
        Assert.AreEqual(afterRebuild.Count + 1, afterRebuildAppend.Count);
    }

    private static List<int> Ordinals(string dbPath, string sourcePath)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT turn_ordinal FROM turns WHERE source_path = $path ORDER BY turn_ordinal;";
        command.Parameters.AddWithValue("$path", sourcePath);
        using var reader = command.ExecuteReader();
        var ordinals = new List<int>();
        while (reader.Read()) ordinals.Add(reader.GetInt32(0));
        return ordinals;
    }

    private sealed class SearchFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "clr-search-index-" + Guid.NewGuid().ToString("N"));

        public SearchFixture()
        {
            Directory.CreateDirectory(_root);
            Index = new TranscriptSearchIndex(Path.Combine(_root, "search.sqlite"));
        }

        public TranscriptSearchIndex Index { get; }

        public string WriteCodex(string id, string content)
        {
            var path = Path.Combine(_root, $"rollout-2026-08-03T00-00-00-{id}.jsonl");
            File.WriteAllText(path, content.Trim() + "\n", new UTF8Encoding(false));
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-30));
            return path;
        }

        public ArchiveSession Session(string id, string path) => new()
        {
            Id = id,
            Title = id,
            Tool = "codex",
            SourcePath = path,
            UpdatedAt = File.GetLastWriteTimeUtc(path).ToString("O"),
        };

        public IReadOnlyList<SessionSource> Sources(string tool) =>
            new[] { new SessionSource { Tool = tool, Root = _root } };

        public void Dispose()
        {
            Index.Dispose();
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }
    }
}
