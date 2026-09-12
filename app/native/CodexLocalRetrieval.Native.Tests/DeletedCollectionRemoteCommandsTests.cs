using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class DeletedCollectionRemoteCommandsTests
{
    private static JsonElement Command(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static async Task<(string Directory, string Store, ArchiveService Service)> CreateDeletedStoreAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clr-deleted-collections-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "app-store.json");
        var service = new ArchiveService(storePath: store);
        service.Store.Sessions["s1"] = new ArchiveSession { Id = "s1", Tool = "claude", Title = "first" };
        service.Store.Sessions["s2"] = new ArchiveSession { Id = "s2", Tool = "codex", Title = "second" };
        service.Store.Decks.Add(new Deck { Id = "deck-a", Name = "A", CreatedAt = "2026-01-01T00:00:00Z" });
        service.Store.Decks.Add(new Deck { Id = "deck-b", Name = "B", CreatedAt = "2026-01-02T00:00:00Z" });
        service.Store.Collections["c1"] = new ArchiveCollection { Id = "c1", Name = "First", DeckId = "deck-a", SessionIds = new() { "s1" } };
        service.Store.Collections["c2"] = new ArchiveCollection { Id = "c2", Name = "Second", DeckId = "deck-b", SessionIds = new() { "s2" } };
        await service.SaveAsync();
        return (directory, store, service);
    }

    private static async Task<(bool Ok, string Detail)> ExecuteAsync(ArchiveService service, string json) =>
        await ArchiveRemoteCommands.ExecuteAsync(service, Command(json));

    private static async Task<ArchiveService> ReloadAsync(string store)
    {
        var service = new ArchiveService(storePath: store);
        await service.LoadAsync();
        return service;
    }

    private static async Task DeleteAsync(ArchiveService service, string id, string intent)
    {
        var revision = service.CollectionManagementRevision(id);
        var result = await ExecuteAsync(service,
            $"{{\"type\":\"collectiondelete\",\"intentId\":\"{intent}\",\"collectionId\":\"{id}\",\"expectedRevision\":\"{revision}\"}}");
        Assert.IsTrue(result.Ok, result.Detail);
    }

    [TestMethod]
    public async Task DeckOrder_RejectsStaleAndInvalidPermutationsAndReplaysAfterReload()
    {
        var (directory, store, service) = await CreateDeletedStoreAsync();
        try
        {
            service.EnsureDecks();
            await service.SaveAsync();
            var original = service.Decks.Select(d => d.Id).ToArray();
            var revision = service.DeckOrderRevision();
            var unrelated = JsonSerializer.Serialize(service.Store.Collections);
            foreach (var invalid in new[] { original.Skip(1).ToArray(), original.Select(_ => original[0]).ToArray(), original.Skip(1).Append("foreign").ToArray() })
            {
                var refused = await ArchiveRemoteCommands.ExecuteAsync(service, JsonSerializer.SerializeToElement(new { type = "deckreorder", intentId = Guid.NewGuid().ToString("N"), expectedRevision = revision, deckIds = invalid }));
                Assert.IsFalse(refused.ok);
                CollectionAssert.AreEqual(original, service.Decks.Select(d => d.Id).ToArray());
            }
            var order = original.AsEnumerable().Reverse().ToArray();
            var command = JsonSerializer.SerializeToElement(new { type = "deckreorder", intentId = "deck-order-proof", expectedRevision = revision, deckIds = order });
            var applied = await ArchiveRemoteCommands.ExecuteAsync(service, command);
            Assert.IsTrue(applied.ok, applied.detail);
            var loaded = await ReloadAsync(store);
            CollectionAssert.AreEqual(order, loaded.Decks.Select(d => d.Id).ToArray());
            Assert.IsTrue((await ArchiveRemoteCommands.ExecuteAsync(loaded, command)).ok);
            Assert.AreEqual(unrelated, JsonSerializer.Serialize(loaded.Store.Collections));
            var stale = await ArchiveRemoteCommands.ExecuteAsync(loaded, JsonSerializer.SerializeToElement(new { type = "deckreorder", intentId = "stale-deck-order", expectedRevision = revision, deckIds = original }));
            Assert.IsFalse(stale.ok);
            CollectionAssert.AreEqual(order, loaded.Decks.Select(d => d.Id).ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Organization_RejectsInvalidOrderAndPersistsTagsAndExactOrder()
    {
        var (directory, store, service) = await CreateDeletedStoreAsync();
        try
        {
            service.Store.Collections["c1"].SessionIds.Add("s2");
            await service.SaveAsync();
            var revision = service.CollectionManagementRevision("c1");
            foreach (var invalid in new[] { new[] { "s1", "s1" }, new[] { "s1" }, new[] { "s1", "foreign" } })
            {
                var result = await ArchiveRemoteCommands.ExecuteAsync(service, JsonSerializer.SerializeToElement(new { type = "collectionreorder", intentId = Guid.NewGuid().ToString("N"), collectionId = "c1", expectedRevision = revision, sessionIds = invalid }));
                Assert.IsFalse(result.ok);
                CollectionAssert.AreEqual(new[] { "s1", "s2" }, service.Store.Collections["c1"].SessionIds);
            }
            var ordered = JsonSerializer.SerializeToElement(new { type = "collectionreorder", intentId = "order-proof", collectionId = "c1", expectedRevision = revision, sessionIds = new[] { "s2", "s1" } });
            Assert.IsTrue((await ArchiveRemoteCommands.ExecuteAsync(service, ordered)).ok);
            Assert.IsTrue((await ArchiveRemoteCommands.ExecuteAsync(service, ordered)).ok);
            var stale = await ArchiveRemoteCommands.ExecuteAsync(service, JsonSerializer.SerializeToElement(new { type = "collectionsettag", intentId = "stale-tag", collectionId = "c1", expectedRevision = revision, tag = "proof", enabled = true }));
            Assert.IsFalse(stale.ok);
            foreach (var enabled in new[] { true, false })
            {
                var result = await ArchiveRemoteCommands.ExecuteAsync(service, JsonSerializer.SerializeToElement(new { type = "collectionsettag", intentId = "tag-" + enabled, collectionId = "c1", expectedRevision = service.CollectionManagementRevision("c1"), tag = "proof", enabled }));
                Assert.IsTrue(result.ok, result.detail);
                Assert.AreEqual(enabled, service.Store.Collections["c1"].Tags.Contains("proof"));
            }
            var reserved = await ArchiveRemoteCommands.ExecuteAsync(service, JsonSerializer.SerializeToElement(new { type = "collectionsettag", intentId = "reserved-tag", collectionId = "c1", expectedRevision = service.CollectionManagementRevision("c1"), tag = "archive", enabled = true }));
            Assert.IsFalse(reserved.ok);
            var loaded = await ReloadAsync(store);
            CollectionAssert.AreEqual(new[] { "s2", "s1" }, loaded.Store.Collections["c1"].SessionIds);
            CollectionAssert.AreEqual(new[] { "s1", "s2" }, loaded.Store.Sessions.Keys.ToArray());
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Purge_DeletesOnlySelectedTombstoneAndPreservesSessionsAndMemberships()
    {
        var (directory, store, service) = await CreateDeletedStoreAsync();
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            await DeleteAsync(service, "c2", "delete-c2");
            var expected = service.DeletedCollectionManagementRevision("c1");
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"collectionpurge\",\"intentId\":\"purge-c1\",\"collectionId\":\"c1\",\"expectedDeletedRevision\":\"{expected}\"}}");
            Assert.IsTrue(result.Ok, result.Detail);

            var reloaded = await ReloadAsync(store);
            CollectionAssert.AreEqual(new[] { "c2" }, reloaded.DeletedCollectionIds().ToArray());
            CollectionAssert.AreEqual(new[] { "s1", "s2" }, reloaded.Store.Sessions.Keys.ToArray());
            CollectionAssert.AreEqual(new[] { "s2" }, reloaded.Store.DeletedCollections.Single().Collection.SessionIds.ToArray());
            Assert.AreEqual("deck-b", reloaded.Store.DeletedCollections.Single().Collection.DeckId);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task Purge_RefusesStaleExactTombstoneRevisionWithoutMutation()
    {
        var (directory, _, service) = await CreateDeletedStoreAsync();
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            var stale = service.DeletedCollectionManagementRevision("c1");
            service.Store.DeletedCollections[0].Collection.Name = "changed elsewhere";
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"collectionpurge\",\"intentId\":\"purge-stale\",\"collectionId\":\"c1\",\"expectedDeletedRevision\":\"{stale}\"}}");
            Assert.IsFalse(result.Ok, result.Detail);
            Assert.IsTrue(service.HasDeletedCollection("c1"));
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task Empty_RefusesStaleAggregateRevisionAfterAnotherTombstoneIsAdded()
    {
        var (directory, _, service) = await CreateDeletedStoreAsync();
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            var stale = service.RecentlyDeletedManagementRevision();
            await DeleteAsync(service, "c2", "delete-c2");
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"collectionempty\",\"intentId\":\"empty-stale\",\"expectedRecentlyDeletedRevision\":\"{stale}\"}}");
            Assert.IsFalse(result.Ok, result.Detail);
            CollectionAssert.AreEqual(new[] { "c2", "c1" }, service.DeletedCollectionIds().ToArray());
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task Purge_ExactIntentReplayDoesNotPurgeLaterTombstone()
    {
        var (directory, _, service) = await CreateDeletedStoreAsync();
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            var expected = service.DeletedCollectionManagementRevision("c1");
            var command = $"{{\"type\":\"collectionpurge\",\"intentId\":\"purge-fixed\",\"collectionId\":\"c1\",\"expectedDeletedRevision\":\"{expected}\"}}";
            Assert.IsTrue((await ExecuteAsync(service, command)).Ok);
            await DeleteAsync(service, "c2", "delete-c2");
            var replay = await ExecuteAsync(service, command);
            Assert.IsTrue(replay.Ok, replay.Detail);
            CollectionAssert.AreEqual(new[] { "c2" }, service.DeletedCollectionIds().ToArray());
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task Purge_IntentCollisionIsRefused()
    {
        var (directory, _, service) = await CreateDeletedStoreAsync();
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            var expected = service.DeletedCollectionManagementRevision("c1");
            var first = await ExecuteAsync(service,
                $"{{\"type\":\"collectionpurge\",\"intentId\":\"same-intent\",\"collectionId\":\"c1\",\"expectedDeletedRevision\":\"{expected}\"}}");
            Assert.IsTrue(first.Ok, first.Detail);
            var collision = await ExecuteAsync(service,
                "{\"type\":\"collectionpurge\",\"intentId\":\"same-intent\",\"collectionId\":\"c1\",\"expectedDeletedRevision\":\"different\"}");
            Assert.IsFalse(collision.Ok, collision.Detail);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task Purge_SaveFailureRestoresMemoryDiskLedgerAndOrderedMembers()
    {
        var (directory, store, service) = await CreateDeletedStoreAsync();
        var backupPath = service.StoreBackupsDir;
        var obstructed = false;
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            await DeleteAsync(service, "c2", "delete-c2");
            var before = File.ReadAllBytes(store);
            var ids = service.DeletedCollectionIds().ToArray();
            var expected = service.DeletedCollectionManagementRevision("c1");
            Directory.CreateDirectory(backupPath);
            Directory.Move(backupPath, backupPath + "-preserved");
            File.WriteAllText(backupPath, "controlled backup-directory obstruction");
            obstructed = true;
            var failed = await ExecuteAsync(service,
                $"{{\"type\":\"collectionpurge\",\"intentId\":\"purge-blocked\",\"collectionId\":\"c1\",\"expectedDeletedRevision\":\"{expected}\"}}");
            Assert.IsFalse(failed.Ok, failed.Detail);
            CollectionAssert.AreEqual(ids, service.DeletedCollectionIds().ToArray());
            Assert.IsFalse(service.Store.ManagementOperations.ContainsKey("purge-blocked"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(store));
            var reloaded = await ReloadAsync(store);
            CollectionAssert.AreEqual(ids, reloaded.DeletedCollectionIds().ToArray());
            Assert.IsFalse(reloaded.Store.ManagementOperations.ContainsKey("purge-blocked"));
        }
        finally
        {
            if (obstructed) try { File.Delete(backupPath); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Empty_SaveFailureRestoresMemoryDiskLedgerAndOrderedMembers()
    {
        var (directory, store, service) = await CreateDeletedStoreAsync();
        var backupPath = service.StoreBackupsDir;
        var obstructed = false;
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            await DeleteAsync(service, "c2", "delete-c2");
            var before = File.ReadAllBytes(store);
            var ids = service.DeletedCollectionIds().ToArray();
            var expected = service.RecentlyDeletedManagementRevision();
            Directory.CreateDirectory(backupPath);
            Directory.Move(backupPath, backupPath + "-preserved");
            File.WriteAllText(backupPath, "controlled backup-directory obstruction");
            obstructed = true;
            var failed = await ExecuteAsync(service,
                $"{{\"type\":\"collectionempty\",\"intentId\":\"empty-blocked\",\"expectedRecentlyDeletedRevision\":\"{expected}\"}}");
            Assert.IsFalse(failed.Ok, failed.Detail);
            CollectionAssert.AreEqual(ids, service.DeletedCollectionIds().ToArray());
            Assert.IsFalse(service.Store.ManagementOperations.ContainsKey("empty-blocked"));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(store));
            var reloaded = await ReloadAsync(store);
            CollectionAssert.AreEqual(ids, reloaded.DeletedCollectionIds().ToArray());
            Assert.IsFalse(reloaded.Store.ManagementOperations.ContainsKey("empty-blocked"));
        }
        finally
        {
            if (obstructed) try { File.Delete(backupPath); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [TestMethod]
    public async Task Empty_PersistsAcrossReloadAndLeavesSessionsUntouched()
    {
        var (directory, store, service) = await CreateDeletedStoreAsync();
        try
        {
            await DeleteAsync(service, "c1", "delete-c1");
            await DeleteAsync(service, "c2", "delete-c2");
            var expected = service.RecentlyDeletedManagementRevision();
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"collectionempty\",\"intentId\":\"empty-all\",\"expectedRecentlyDeletedRevision\":\"{expected}\"}}");
            Assert.IsTrue(result.Ok, result.Detail);
            var reloaded = await ReloadAsync(store);
            Assert.AreEqual(0, reloaded.Store.DeletedCollections.Count);
            CollectionAssert.AreEqual(new[] { "s1", "s2" }, reloaded.Store.Sessions.Keys.ToArray());
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task CollectionCreate_EmptyTargetDeckIdUsesValidDeckId()
    {
        var (directory, _, service) = await CreateDeletedStoreAsync();
        try
        {
            var result = await ExecuteAsync(service,
                "{\"type\":\"collectioncreate\",\"intentId\":\"create-on-b\",\"name\":\"Created\",\"targetDeckId\":\"\",\"deckId\":\"deck-b\"}");
            Assert.IsTrue(result.Ok, result.Detail);
            Assert.AreEqual("deck-b", service.Store.Collections[result.Detail].DeckId);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }
}
