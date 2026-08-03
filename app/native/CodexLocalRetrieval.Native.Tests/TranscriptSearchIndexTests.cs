using System.Text;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

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
