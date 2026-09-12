using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class BranchRemoteOperationsTests
{
    [TestMethod]
    public async Task MissingTranscriptRefusesCheckpointWithoutPersistingPreparedReceipt()
    {
        using var fixture = new Fixture();
        var revision = fixture.Service.RemoteManagementRevision(fixture.Source);
        File.Delete(fixture.Source.SourcePath);

        var result = await fixture.Service.ExecuteBranchOperationAsync(
            "checkpointcreate", "missing-1", fixture.Source.Id, "claude", revision, "missing");
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(fixture.Service.Store.ManagementOperations.ContainsKey("missing-1"));

        await fixture.Service.SaveAsync();
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.IsFalse(reloaded.Store.ManagementOperations.ContainsKey("missing-1"));
    }

    [TestMethod]
    public async Task EmptyTranscriptRefusesCheckpointWithoutPersistingPreparedReceipt()
    {
        using var fixture = new Fixture();
        var revision = fixture.Service.RemoteManagementRevision(fixture.Source);
        await File.WriteAllTextAsync(fixture.Source.SourcePath, "");

        var result = await fixture.Service.ExecuteBranchOperationAsync(
            "checkpointcreate", "empty-1", fixture.Source.Id, "claude", revision, "empty");
        Assert.IsFalse(result.Ok);
        Assert.IsFalse(fixture.Service.Store.ManagementOperations.ContainsKey("empty-1"));
    }

    [TestMethod]
    public async Task CheckpointCreate_BeforeReplaceLeavesNoReceiptOrSnapshotFile()
    {
        using var fixture = new Fixture();
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known remote checkpoint failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() => fixture.Service.ExecuteBranchOperationAsync(
            "checkpointcreate", "known-failure-1", fixture.Source.Id, "claude",
            fixture.Service.RemoteManagementRevision(fixture.Source), "known failure"));
        fixture.Fault = null;

        Assert.IsFalse(fixture.Service.Store.ManagementOperations.ContainsKey("known-failure-1"));
        Assert.AreEqual(0, Directory.GetFiles(fixture.TemplatesRoot, "*.jsonl").Length);
        await fixture.Service.SaveAsync();
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.IsFalse(reloaded.Store.ManagementOperations.ContainsKey("known-failure-1"));
        Assert.AreEqual(0, reloaded.Store.TemplateSnapshots.Count);
    }

    [TestMethod]
    public async Task RenameKnownFailurePreservesOriginalDataAndReceipt()
    {
        using var fixture = new Fixture();
        var created = await fixture.Service.ExecuteBranchOperationAsync(
            "checkpointcreate", "rename-source", fixture.Source.Id, "claude",
            fixture.Service.RemoteManagementRevision(fixture.Source), "original");
        var snapshotId = created.ResultId;
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known rename failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() => fixture.Service.ExecuteBranchOperationAsync(
            "checkpointrename", "rename-failure", snapshotId, "claude",
            fixture.Service.TemplateSnapshotManagementRevision(snapshotId), "changed"));
        fixture.Fault = null;

        Assert.AreEqual("original", fixture.Service.Store.TemplateSnapshots[snapshotId].Name);
        Assert.IsFalse(fixture.Service.Store.ManagementOperations.ContainsKey("rename-failure"));
        await fixture.Service.SaveAsync();
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual("original", reloaded.Store.TemplateSnapshots[snapshotId].Name);
        Assert.IsFalse(reloaded.Store.ManagementOperations.ContainsKey("rename-failure"));
    }

    [TestMethod]
    public async Task DeleteKnownFailurePreservesOriginalDataAndReceipt()
    {
        using var fixture = new Fixture();
        var created = await fixture.Service.ExecuteBranchOperationAsync(
            "checkpointcreate", "delete-source", fixture.Source.Id, "claude",
            fixture.Service.RemoteManagementRevision(fixture.Source), "original");
        var snapshotId = created.ResultId;
        var snapshotPath = fixture.Service.Store.TemplateSnapshots[snapshotId].SnapshotPath;
        var snapshotBytes = await File.ReadAllBytesAsync(snapshotPath);
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known delete failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() => fixture.Service.ExecuteBranchOperationAsync(
            "checkpointdelete", "delete-failure", snapshotId, "claude",
            fixture.Service.TemplateSnapshotManagementRevision(snapshotId)));
        fixture.Fault = null;

        Assert.IsTrue(fixture.Service.Store.TemplateSnapshots.ContainsKey(snapshotId));
        CollectionAssert.AreEqual(snapshotBytes, await File.ReadAllBytesAsync(snapshotPath));
        Assert.IsFalse(fixture.Service.Store.ManagementOperations.ContainsKey("delete-failure"));
    }

    [TestMethod]
    public async Task UnknownCheckpointSaveReloadsAuthoritativeReceiptAndPreservesSnapshotBytes()
    {
        using var fixture = new Fixture();
        var armed = false;
        fixture.Fault = stage =>
        {
            if (armed && stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                throw new IOException("injected unknown checkpoint save");
        };
        armed = true;

        var result = await fixture.Service.ExecuteBranchOperationAsync(
            "checkpointcreate", "unknown-1", fixture.Source.Id, "claude",
            fixture.Service.RemoteManagementRevision(fixture.Source), "unknown");

        Assert.IsTrue(result.Ok, result.Detail);
        var snapshotPath = fixture.Service.Store.TemplateSnapshots[result.ResultId].SnapshotPath;
        Assert.IsTrue(File.Exists(snapshotPath));
        Assert.IsTrue(fixture.Service.Store.ManagementOperations["unknown-1"].State == "applied");
        Assert.IsTrue((await File.ReadAllBytesAsync(snapshotPath)).Length > 0);
    }

    [TestMethod]
    public async Task CheckpointCreateRenameSpawnDelete_ReplaysDurably()
    {
        using var fixture = new Fixture();
        var revision = fixture.Service.RemoteManagementRevision(fixture.Source);
        var created = await fixture.Service.ExecuteBranchOperationAsync("checkpointcreate", "create-1", fixture.Source.Id, "claude", revision, "checkpoint");
        Assert.IsTrue(created.Ok, created.Detail);
        var snapshotId = created.ResultId;
        var snapshotRevision = fixture.Service.TemplateSnapshotManagementRevision(snapshotId);
        var renamed = await fixture.Service.ExecuteBranchOperationAsync("checkpointrename", "rename-1", snapshotId, "claude", snapshotRevision, "renamed");
        Assert.IsTrue(renamed.Ok, renamed.Detail);
        var spawned = await fixture.Service.ExecuteBranchOperationAsync("checkpointspawn", "spawn-1", snapshotId, "claude", fixture.Service.TemplateSnapshotManagementRevision(snapshotId));
        Assert.IsTrue(spawned.Ok, spawned.Detail);
        Assert.AreEqual(snapshotId, fixture.Service.Store.Sessions[spawned.ResultId].FromSnapshotId);
        var replay = await fixture.Service.ExecuteBranchOperationAsync("checkpointspawn", "spawn-1", snapshotId, "claude", fixture.Service.TemplateSnapshotManagementRevision(snapshotId));
        Assert.IsTrue(replay.Ok, replay.Detail);
        Assert.AreEqual(spawned.ResultId, replay.ResultId);
        var deleted = await fixture.Service.ExecuteBranchOperationAsync("checkpointdelete", "delete-1", snapshotId, "claude", fixture.Service.TemplateSnapshotManagementRevision(snapshotId));
        Assert.IsTrue(deleted.Ok, deleted.Detail);
        Assert.IsFalse(fixture.Service.Store.TemplateSnapshots.ContainsKey(snapshotId));
    }

    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "branch-remote-" + Guid.NewGuid().ToString("N"));
            var claude = Path.Combine(Root, "claude");
            Directory.CreateDirectory(claude);
            var path = Path.Combine(claude, "source.jsonl");
            File.WriteAllText(path, "{\"type\":\"user\",\"sessionId\":\"source\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}\n");
            Source = new ArchiveSession { Id = "source", Tool = "claude", Title = "Source", Workspace = Root, SourcePath = path };
            Service = NewService();
            Service.Store.Sessions[Source.Id] = Source;
            Service.SaveAsync().GetAwaiter().GetResult();
        }
        public string Root { get; }
        public string TemplatesRoot => Path.Combine(Root, "templates");
        public ArchiveService Service { get; }
        public ArchiveSession Source { get; }
        public Action<DurableWriteStage>? Fault { get => _fault; set => _fault = value; }
        private Action<DurableWriteStage>? _fault;

        public ArchiveService NewService()
        {
            var service = new ArchiveService(
                Path.Combine(Root, "store.json"),
                templatesRoot: TemplatesRoot,
                claudeSessionsRoot: Path.Combine(Root, "claude"),
                enableTranscriptSearchIndex: false);
            var property = typeof(ArchiveService).GetProperty(
                "StoreWriteFault",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            property!.SetValue(service, (Action<DurableWriteStage>)(stage => _fault?.Invoke(stage)));
            return service;
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
