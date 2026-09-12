using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class WorkspaceCaptureTests
{
    private static async Task<(string Dir, string Store, ArchiveService Service)> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "workspace-capture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = Path.Combine(dir, "app-store.json");
        var service = new ArchiveService(storePath: store);
        service.Store.Sessions["real-1"] = new ArchiveSession { Id = "real-1", Tool = "claude", Title = "real" };
        service.Store.Collections["col-1"] = new ArchiveCollection { Id = "col-1", Name = "Collection" };
        await service.SaveAsync();
        return (dir, store, service);
    }

    private static (WorkspaceCaptureTab Real, WorkspaceCaptureTab Shell) Tabs() =>
        (new("tab-real", "gen-1", "real-1", "claude", true, false, false),
         new("tab-shell", "gen-2", "", "codex", true, false, true));

    [TestMethod]
    public async Task CapturesRealAndShellAtomicallyAndReloads()
    {
        var (dir, store, service) = await CreateAsync();
        try
        {
            var (real, shell) = Tabs();
            var revision = service.CollectionMembershipRevision("col-1");
            var result = await service.ExecuteWorkspaceCaptureAsync("intent-1", "col-1", revision,
                new[] { real, shell }, new[] { real, shell });
            Assert.IsTrue(result.Ok, result.Detail);
            var loaded = new ArchiveService(storePath: store);
            await loaded.LoadAsync();
            CollectionAssert.AreEquivalent(new[] { "real-1", "muxtab:tab-shell" }, loaded.Store.Collections["col-1"].SessionIds);
            Assert.AreEqual("applied", loaded.Store.ManagementOperations["intent-1"].State);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task ReplayUsesDurableReceiptAfterAuthoritativeTabsDisappear()
    {
        var (dir, _, service) = await CreateAsync();
        try
        {
            var (real, shell) = Tabs();
            var revision = service.CollectionMembershipRevision("col-1");
            var first = await service.ExecuteWorkspaceCaptureAsync("intent-1", "col-1", revision, new[] { real, shell }, new[] { real, shell });
            var replay = await service.ExecuteWorkspaceCaptureAsync("intent-1", "col-1", revision, new[] { real, shell }, Array.Empty<WorkspaceCaptureTab>());
            Assert.IsTrue(first.Ok);
            Assert.IsTrue(replay.Ok, replay.Detail);
            Assert.AreEqual(first.ResultId, replay.ResultId);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task RejectsStaleGenerationSessionCollectionAndDuplicatesWithoutMutation()
    {
        var (dir, _, service) = await CreateAsync();
        try
        {
            var (real, shell) = Tabs();
            var revision = service.CollectionMembershipRevision("col-1");
            var stale = await service.ExecuteWorkspaceCaptureAsync("stale", "col-1", revision,
                new[] { real with { GenerationId = "old" } }, new[] { real, shell });
            var duplicate = await service.ExecuteWorkspaceCaptureAsync("dup", "col-1", revision,
                new[] { real, real }, new[] { real });
            var badId = await service.ExecuteWorkspaceCaptureAsync("bad", "col-1", revision,
                new[] { real with { SessionId = "missing" } }, new[] { real with { SessionId = "missing" } });
            Assert.IsFalse(stale.Ok);
            Assert.IsFalse(duplicate.Ok);
            Assert.IsFalse(badId.Ok);
            Assert.AreEqual(0, service.Store.Collections["col-1"].SessionIds.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task BackupObstructionRollsBackPlaceholderLedgerAndMembership()
    {
        var (dir, _, service) = await CreateAsync();
        try
        {
            Directory.Delete(service.StoreBackupsDir, true);
            File.WriteAllText(service.StoreBackupsDir, "obstruction");
            var (_, shell) = Tabs();
            var revision = service.CollectionMembershipRevision("col-1");
            var result = await service.ExecuteWorkspaceCaptureAsync("blocked", "col-1", revision,
                new[] { shell }, new[] { shell });
            Assert.IsFalse(result.Ok);
            Assert.AreEqual(0, service.Store.Collections["col-1"].SessionIds.Count);
            Assert.IsFalse(service.Store.Sessions.ContainsKey("muxtab:tab-shell"));
            Assert.IsFalse(service.Store.ManagementOperations.ContainsKey("blocked"));
        }
        finally { Directory.Delete(dir, true); }
    }
}
