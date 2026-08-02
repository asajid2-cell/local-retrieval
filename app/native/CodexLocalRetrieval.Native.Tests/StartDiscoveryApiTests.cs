using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class StartDiscoveryApiTests
{
    [TestMethod]
    public void StartDecks_SortsRowsAndFallsBackToMainForUnknownActiveDeck()
    {
        var archive = NewArchive();
        archive.Store.Decks.Clear();
        archive.Store.Decks.Add(new Deck { Id = "z", Name = "Zulu" });
        archive.Store.Decks.Add(new Deck { Id = "a", Name = "Alpha" });
        archive.Store.Decks.Add(new Deck { Id = "main", Name = "Main" });
        archive.Store.Settings.ActiveDeckId = "missing";

        var projection = new DiscoveryApi(archive).StartDecks();

        CollectionAssert.AreEqual(
            new[] { "a", "main", "z" },
            projection.Rows.Select(row => row.Id).ToArray());
        Assert.AreEqual("main", projection.ActiveDeckId);
    }

    [TestMethod]
    public void StartCollections_IsScopedToTheExactDeckAndSortsRows()
    {
        var archive = NewArchive();
        archive.Store.Decks.Clear();
        archive.Store.Decks.Add(new Deck { Id = "main", Name = "Main" });
        archive.Store.Decks.Add(new Deck { Id = "deck-z", Name = "Zulu" });
        archive.Store.Collections["z"] = new ArchiveCollection
        {
            Id = "z",
            Name = "Zulu collection",
            DeckId = "deck-z"
        };
        archive.Store.Collections["a"] = new ArchiveCollection
        {
            Id = "a",
            Name = "Alpha collection",
            DeckId = "deck-z"
        };
        archive.Store.Collections["main-item"] = new ArchiveCollection
        {
            Id = "main-item",
            Name = "Main collection",
            DeckId = ""
        };

        var scoped = new DiscoveryApi(archive).StartCollections("deck-z");
        var missing = new DiscoveryApi(archive).StartCollections("missing");

        Assert.AreEqual("deck-z", scoped.DeckId);
        CollectionAssert.AreEqual(
            new[] { "a", "z" },
            scoped.Rows.Select(row => row.Id).ToArray());
        Assert.AreEqual("missing", missing.DeckId);
        Assert.AreEqual(0, missing.Rows.Count);
    }

    [TestMethod]
    public void StartCheckpoints_PreservesTemplateOrderAndOmitsLocalPaths()
    {
        var archive = NewArchive();
        var sourcePath = Path.Combine(Path.GetTempPath(), "private-source.jsonl");
        var snapshotPath = Path.Combine(Path.GetTempPath(), "private-snapshot.jsonl");
        var workspacePath = Path.Combine(Path.GetTempPath(), "private-workspace");
        archive.Store.TemplateSnapshots["older"] = new TemplateSnapshot
        {
            Id = "older",
            Name = "Older checkpoint",
            SourceTitle = "Older source",
            SourcePath = sourcePath,
            SnapshotPath = snapshotPath,
            Tool = "claude",
            Workspace = workspacePath,
            WorkspaceName = "private-workspace",
            CreatedAt = "2026-08-01T12:00:00Z",
            MessageCount = 3
        };
        archive.Store.TemplateSnapshots["newer"] = new TemplateSnapshot
        {
            Id = "newer",
            Name = "Newer checkpoint",
            SourceTitle = "Newer source",
            SourcePath = sourcePath,
            SnapshotPath = snapshotPath,
            Tool = "codex",
            Workspace = workspacePath,
            WorkspaceName = "private-workspace",
            CreatedAt = "2026-08-02T12:00:00Z",
            MessageCount = 7
        };

        var projection = new DiscoveryApi(archive).StartCheckpoints();
        var json = JsonSerializer.Serialize(projection);

        CollectionAssert.AreEqual(
            new[] { "newer", "older" },
            projection.Rows.Select(row => row.Id).ToArray());
        Assert.AreEqual("Newer source", projection.Rows[0].SourceTitle);
        Assert.AreEqual("codex", projection.Rows[0].Tool);
        Assert.AreEqual("private-workspace", projection.Rows[0].WorkspaceLabel);
        Assert.AreEqual(7, projection.Rows[0].MessageCount);
        Assert.DoesNotContain(sourcePath, json);
        Assert.DoesNotContain(snapshotPath, json);
        Assert.DoesNotContain(workspacePath, json);
        Assert.DoesNotContain("sourcePath", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("snapshotPath", json, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void StartWorkspaces_DeduplicatesPathsAggregatesToolsAndResolvesTheSameOpaqueId()
    {
        var root = NewDirectory();
        var workspace = Path.Combine(root, "project");
        Directory.CreateDirectory(workspace);
        var archive = NewArchive();
        archive.Store.Sessions["codex"] = new ArchiveSession
        {
            Id = "codex",
            Tool = "Codex",
            Workspace = workspace
        };
        archive.Store.Sessions["claude"] = new ArchiveSession
        {
            Id = "claude",
            Tool = "claude",
            Workspace = workspace + Path.DirectorySeparatorChar
        };

        var api = new DiscoveryApi(archive);
        var first = api.StartWorkspaces();
        var second = api.StartWorkspaces();
        var row = first.Rows.Single(candidate => candidate.Label == "project");
        var duplicateRows = first.Rows.Count(candidate => candidate.Label == "project");

        Assert.AreEqual(1, duplicateRows);
        CollectionAssert.AreEqual(new[] { "claude", "codex" }, row.Tools.ToArray());
        Assert.IsTrue(row.Id.StartsWith("ws-", StringComparison.Ordinal));
        Assert.AreEqual(row.Id, second.Rows.Single(candidate => candidate.Label == "project").Id);
        Assert.DoesNotContain(workspace, JsonSerializer.Serialize(first));
        Assert.IsTrue(api.TryResolveWorkspace(row.Id, out var resolved));
        Assert.AreEqual(
            Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        Directory.Delete(workspace);
        Assert.IsFalse(api.TryResolveWorkspace(row.Id, out var stale));
        Assert.AreEqual("", stale);
        Assert.IsFalse(api.TryResolveWorkspace("ws-missing", out var missing));
        Assert.AreEqual("", missing);
    }

    private static ArchiveService NewArchive()
        => new(storePath: Path.Combine(Path.GetTempPath(), "clr-start-discovery-" + Guid.NewGuid().ToString("N") + ".json"));

    private static string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "clr-start-discovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
