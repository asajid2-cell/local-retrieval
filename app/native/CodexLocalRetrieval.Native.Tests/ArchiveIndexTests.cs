using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The recent-chats archive index the app pushes to the VPS (POST /api/archive-index) so the web
// terminal can offer "reopen a recent chat". Mirrors the BuildProjectsProjectionJson_* contract
// tests: same resumable gate, same recency order, same 500 cap — but flat, and intent-only. The
// one deliberate divergence is `cwd`, which the projects projection forbids and this index carries
// as an accepted display-string disclosure.
[TestClass]
public sealed class ArchiveIndexTests
{
    private static string TempCwd() => Path.GetTempPath().TrimEnd('\\', '/');

    private static ArchiveSession Chat(string id, string tool, string title, string updatedAt = "")
    {
        var cwd = TempCwd();
        return new ArchiveSession
        {
            Id = id,
            Tool = tool,
            Title = title,
            UpdatedAt = updatedAt,
            Workspace = cwd,
            WorkspaceName = "Temp",
            SourcePath = Path.Combine(cwd, id + ".jsonl"),
        };
    }

    private static ArchiveService WithChats(params ArchiveSession[] sessions)
    {
        var svc = new ArchiveService(useBundledStore: true);
        foreach (var s in sessions) svc.Store.Sessions[s.Id] = s;
        return svc;
    }

    [TestMethod]
    public void BuildArchiveIndexJson_EmitsSchemaHostAndResumableChats()
    {
        var svc = WithChats(
            Chat("cl1", "claude", "Cortex Push", "2026-07-01T10:00:00Z"),
            Chat("cx2", "codex", "GPU gen", "2026-07-01T09:00:00Z"));

        var json = svc.BuildArchiveIndexJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.IsTrue(root.GetProperty("host").GetString()!.Length > 0);

        var chats = root.GetProperty("chats");
        Assert.AreEqual(2, chats.GetArrayLength());
        foreach (var ch in chats.EnumerateArray())
        {
            Assert.IsTrue(ch.GetProperty("id").GetString()!.Length > 0);
            Assert.IsTrue(ch.GetProperty("title").GetString()!.Length > 0);
            Assert.IsTrue(ch.GetProperty("tool").GetString()!.Length > 0);
            Assert.IsTrue(ch.GetProperty("muxName").GetString()!.Length > 0);
            Assert.IsTrue(ch.GetProperty("updatedAt").GetString()!.Length > 0);
            Assert.AreEqual("Temp", ch.GetProperty("workspaceLabel").GetString());
            Assert.IsTrue(ch.GetProperty("resumable").GetBoolean());
        }
        var cl1 = chats.EnumerateArray().First(c => c.GetProperty("id").GetString() == "cl1");
        Assert.AreEqual("Cortex Push", cl1.GetProperty("title").GetString());
        Assert.AreEqual("claude", cl1.GetProperty("tool").GetString());
    }

    // Intent only: the relay may render this list but must never receive anything executable, nor any
    // local path other than the cwd display string the contract explicitly allows.
    [TestMethod]
    public void BuildArchiveIndexJson_CarriesCwdDisplayStringButNoExecutableIntent()
    {
        var svc = WithChats(Chat("cx2", "codex", "GPU gen", "2026-07-01T09:00:00Z"));

        var json = svc.BuildArchiveIndexJson();
        Assert.IsFalse(json.Contains("\"muxCommand\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"command\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"exe\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"arguments\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"sourcePath\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"pcPath\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains("\"preview\"", StringComparison.Ordinal));
        Assert.IsFalse(json.Contains(".jsonl", StringComparison.Ordinal), "transcript paths must not reach the relay");

        using var doc = JsonDocument.Parse(json);
        var chat = doc.RootElement.GetProperty("chats")[0];
        Assert.AreEqual(TempCwd(), chat.GetProperty("cwd").GetString());
    }

    // Flat and archive-wide: unlike the projects projection there is no collection tree, so a chat that
    // was never filed still appears — and no collection/deck metadata leaks into the index.
    [TestMethod]
    public void BuildArchiveIndexJson_IncludesUnfiledChatsAndOmitsCollectionTree()
    {
        var svc = WithChats(
            Chat("filed", "claude", "Filed Chat", "2026-07-01T10:00:00Z"),
            Chat("old-chat", "codex", "Old Unfiled Chat", "2026-07-01T08:00:00Z"));
        svc.Store.Collections["col1"] = new ArchiveCollection { Id = "col1", Name = "Filed", SessionIds = new() { "filed" } };

        var json = svc.BuildArchiveIndexJson();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.IsFalse(root.TryGetProperty("collections", out _));
        Assert.IsFalse(root.TryGetProperty("decks", out _));
        Assert.IsFalse(root.TryGetProperty("allChats", out _));
        Assert.IsFalse(root.TryGetProperty("runningSessions", out _));

        var ids = root.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        CollectionAssert.Contains(ids, "old-chat");
        CollectionAssert.Contains(ids, "filed");
        var unfiled = root.GetProperty("chats").EnumerateArray().First(c => c.GetProperty("id").GetString() == "old-chat");
        Assert.AreEqual("Old Unfiled Chat", unfiled.GetProperty("title").GetString());
        Assert.IsFalse(unfiled.TryGetProperty("collection", out _));
    }

    // Same gate as the projection: a read-only checkpoint and an id that isn't a safe token can't be
    // resumed, so neither may be offered to the web.
    [TestMethod]
    public void BuildArchiveIndexJson_OmitsChatsWithNoTrustedResumeLaunch()
    {
        var snapshot = Chat("snap1", "codex", "Read-only Checkpoint", "2026-07-01T10:00:00Z");
        snapshot.IsReadOnlySnapshot = true;
        var unsafeId = Chat("bad id & calc", "codex", "Crafted Id", "2026-07-01T09:00:00Z");
        var good = Chat("ok1", "codex", "Resumable Chat", "2026-07-01T08:00:00Z");
        var svc = WithChats(snapshot, unsafeId, good);
        var archived = Chat("arch1", "codex", "Archived Chat", "2026-07-01T11:00:00Z");
        archived.Archived = true;
        svc.Store.Sessions[archived.Id] = archived;

        using var doc = JsonDocument.Parse(svc.BuildArchiveIndexJson());
        var ids = doc.RootElement.GetProperty("chats").EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();

        Assert.AreEqual(1, ids.Count, "only the resumable, unarchived chat may be offered");
        Assert.AreEqual("ok1", ids[0]);
    }

    // Recency-ordered and bounded exactly like the projection's allChats: newest first, hard cap 500.
    [TestMethod]
    public void BuildArchiveIndexJson_OrdersByRecencyAndCapsAtFiveHundred()
    {
        var svc = new ArchiveService(useBundledStore: true);
        for (var i = 0; i < 520; i++)
        {
            var s = Chat($"chat{i:D4}", "codex", $"Chat {i}", $"2026-07-01T00:00:{i:D4}Z");
            svc.Store.Sessions[s.Id] = s;
        }

        using var doc = JsonDocument.Parse(svc.BuildArchiveIndexJson());
        var chats = doc.RootElement.GetProperty("chats");

        Assert.AreEqual(ArchiveService.ArchiveIndexMaxChats, chats.GetArrayLength());
        Assert.AreEqual(500, chats.GetArrayLength());
        Assert.AreEqual("chat0519", chats[0].GetProperty("id").GetString(), "newest chat first");
        Assert.AreEqual("chat0518", chats[1].GetProperty("id").GetString());

        var updated = chats.EnumerateArray().Select(c => c.GetProperty("updatedAt").GetString()!).ToList();
        CollectionAssert.AreEqual(
            updated.OrderByDescending(u => u, StringComparer.Ordinal).ToList(),
            updated,
            "chats must be newest-first");
        // The 20 oldest fell off the cap.
        var ids = chats.EnumerateArray().Select(c => c.GetProperty("id").GetString()).ToList();
        CollectionAssert.DoesNotContain(ids, "chat0000");
        CollectionAssert.DoesNotContain(ids, "chat0019");
    }
}
