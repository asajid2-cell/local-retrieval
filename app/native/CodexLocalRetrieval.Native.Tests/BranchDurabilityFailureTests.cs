using System.Text;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class BranchDurabilityFailureTests
{
    [TestMethod]
    public async Task DurableWriter_KnownPrecommitFailure_RestoresPreviousGenerationAndCleansTemp()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-branch-durable-precommit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "store.json");
            var backup = Path.Combine(root, "store.bak.json");
            var original = Encoding.UTF8.GetBytes("previous");
            await File.WriteAllBytesAsync(destination, original);

            var error = await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
                DurableFileStore.WriteAtomicAsync(destination, Encoding.UTF8.GetBytes("candidate"), backup, stage =>
                {
                    if (stage == DurableWriteStage.BeforeReplace)
                        throw new IOException("injected known precommit failure");
                }));

            Assert.IsFalse(error.Committed);
            Assert.IsTrue(error.Recovered);
            CollectionAssert.AreEqual(original, await File.ReadAllBytesAsync(destination));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [TestMethod]
    public async Task DurableWriter_PostReplaceFailure_LeavesCommittedBytesAndReportsCommitted()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-branch-durable-committed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "store.json");
            var backup = Path.Combine(root, "store.bak.json");
            var committed = Encoding.UTF8.GetBytes("committed");
            await File.WriteAllBytesAsync(destination, Encoding.UTF8.GetBytes("previous"));

            var error = await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
                DurableFileStore.WriteAtomicAsync(destination, committed, backup, stage =>
                {
                    if (stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                        throw new IOException("injected post-replace uncertainty");
                }));

            Assert.IsFalse(error.Recovered);
            Assert.IsTrue(error.Committed || error.VerificationUnknown);
            CollectionAssert.AreEqual(committed, await File.ReadAllBytesAsync(destination));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [TestMethod]
    public async Task DurableWriter_UnknownVerification_NeverRollsCommittedBytesBack()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-branch-durable-unknown-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var destination = Path.Combine(root, "store.json");
            var backup = Path.Combine(root, "store.bak.json");
            var committed = Encoding.UTF8.GetBytes("committed");
            await File.WriteAllBytesAsync(destination, Encoding.UTF8.GetBytes("previous"));

            var error = await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
                DurableFileStore.WriteAtomicAsync(destination, committed, backup, stage =>
                {
                    if (stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                        throw new IOException("injected verification uncertainty");
                }));

            Assert.IsTrue(error.VerificationUnknown);
            Assert.IsFalse(error.Recovered);
            CollectionAssert.AreEqual(committed, await File.ReadAllBytesAsync(destination));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [TestMethod]
    public async Task CreateTemplateSnapshotAsync_UnknownSave_PreservesSnapshotFileAndMetadata()
    {
        using var fixture = new ClaudeFixture();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.Source.SourcePath);
        var armed = false;
        fixture.Fault = stage =>
        {
            if (armed && stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                throw new IOException("injected snapshot save uncertainty");
        };
        armed = true;

        var result = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "uncertain snapshot");

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Snapshot);
        Assert.IsTrue(File.Exists(result.Snapshot!.SnapshotPath));
        var snapshotBytes = await File.ReadAllBytesAsync(result.Snapshot.SnapshotPath);
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(fixture.Source.SourcePath));
        CollectionAssert.AreEqual(sourceBytes, snapshotBytes);
        Assert.IsTrue(fixture.Service.Store.TemplateSnapshots.ContainsKey(result.Snapshot.Id));
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.IsTrue(reloaded.Store.TemplateSnapshots.ContainsKey(result.Snapshot.Id));
        Assert.IsTrue(File.Exists(reloaded.Store.TemplateSnapshots[result.Snapshot.Id].SnapshotPath));
        CollectionAssert.AreEqual(snapshotBytes, await File.ReadAllBytesAsync(reloaded.Store.TemplateSnapshots[result.Snapshot.Id].SnapshotPath));
    }

    [TestMethod]
    public async Task BranchSessionAsync_UnknownSave_PreservesBranchFileAndReference()
    {
        using var fixture = new ClaudeFixture();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.Source.SourcePath);
        var existingFiles = Directory.GetFiles(fixture.ClaudeRoot, "*.jsonl", SearchOption.AllDirectories);
        var armed = false;
        fixture.Fault = stage =>
        {
            if (armed && stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                throw new IOException("injected branch save uncertainty");
        };
        armed = true;

        var result = await fixture.Service.BranchSessionAsync(fixture.Source);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        Assert.IsTrue(File.Exists(result.Branch!.SourcePath));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(fixture.Source.SourcePath));
        var branchBytes = await File.ReadAllBytesAsync(result.Branch.SourcePath);
        CollectionAssert.AreNotEqual(sourceBytes, branchBytes);
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.IsTrue(reloaded.Store.Sessions.ContainsKey(result.Branch.Id));
        Assert.IsTrue(File.Exists(reloaded.Store.Sessions[result.Branch.Id].SourcePath));
        CollectionAssert.AreEqual(branchBytes, await File.ReadAllBytesAsync(reloaded.Store.Sessions[result.Branch.Id].SourcePath));
        Assert.IsFalse(reloaded.Store.Sessions.Values.Any(session =>
            !string.IsNullOrWhiteSpace(session.SourcePath) && !File.Exists(session.SourcePath)));
        CollectionAssert.AreEquivalent(existingFiles.Append(result.Branch.SourcePath).ToArray(),
            Directory.GetFiles(fixture.ClaudeRoot, "*.jsonl", SearchOption.AllDirectories));
    }

    [TestMethod]
    public async Task SpawnTemplateAsync_UnknownSave_PreservesSpawnedBranchAndSnapshotReference()
    {
        using var fixture = new ClaudeFixture();
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var snapshotBytes = await File.ReadAllBytesAsync(snapshot.SnapshotPath);
        var sourceBytes = await File.ReadAllBytesAsync(fixture.Source.SourcePath);
        var armed = false;
        fixture.Fault = stage =>
        {
            if (armed && stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                throw new IOException("injected spawn save uncertainty");
        };
        armed = true;

        var result = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        Assert.AreEqual(snapshot.Id, result.Branch!.FromSnapshotId);
        Assert.IsTrue(File.Exists(result.Branch.SourcePath));
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(fixture.Source.SourcePath));
        var branchBytes = await File.ReadAllBytesAsync(result.Branch.SourcePath);
        Assert.IsTrue(branchBytes.Length > 0);
        CollectionAssert.AreEqual(snapshotBytes, await File.ReadAllBytesAsync(snapshot.SnapshotPath));
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual(snapshot.Id, reloaded.Store.Sessions[result.Branch.Id].FromSnapshotId);
        Assert.IsTrue(File.Exists(reloaded.Store.Sessions[result.Branch.Id].SourcePath));
        CollectionAssert.AreEqual(branchBytes, await File.ReadAllBytesAsync(reloaded.Store.Sessions[result.Branch.Id].SourcePath));
        CollectionAssert.AreEqual(snapshotBytes, await File.ReadAllBytesAsync(reloaded.Store.TemplateSnapshots[snapshot.Id].SnapshotPath));
    }

    [TestMethod]
    public async Task CreateTemplateSnapshotAsync_BeforeReplace_CreatesNoMetadataOrSnapshotFile()
    {
        using var fixture = new ClaudeFixture();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.Source.SourcePath);
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known precommit failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
            fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "must not commit"));

        Assert.AreEqual(0, fixture.Service.Store.TemplateSnapshots.Count);
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(fixture.Source.SourcePath));
        Assert.AreEqual(0, Directory.GetFiles(fixture.TemplatesRoot, "*.jsonl").Length);
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual(0, reloaded.Store.TemplateSnapshots.Count);
    }

    [TestMethod]
    public async Task BranchSessionAsync_BeforeReplace_CleansBranchAndPreservesSource()
    {
        using var fixture = new ClaudeFixture();
        var sourceBytes = await File.ReadAllBytesAsync(fixture.Source.SourcePath);
        var sessionIds = fixture.Service.Store.Sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known branch failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
            fixture.Service.BranchSessionAsync(fixture.Source));

        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(fixture.Source.SourcePath));
        Assert.IsTrue(fixture.Service.Store.Sessions.Keys.All(sessionIds.Contains));
        Assert.AreEqual(sessionIds.Count, fixture.Service.Store.Sessions.Count);
        Assert.AreEqual(1, Directory.GetFiles(fixture.ClaudeRoot, "*.jsonl", SearchOption.AllDirectories).Length);
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual(sessionIds.Count, reloaded.Store.Sessions.Count);
        CollectionAssert.AreEqual(sourceBytes, await File.ReadAllBytesAsync(fixture.Source.SourcePath));
        Assert.IsFalse(reloaded.Store.Sessions.Values.Any(session =>
            !string.IsNullOrWhiteSpace(session.SourcePath) && !File.Exists(session.SourcePath)));
    }

    [TestMethod]
    public async Task SpawnTemplateAsync_BeforeReplace_CleansBranchAndPreservesSnapshot()
    {
        using var fixture = new ClaudeFixture();
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "spawn source")).Snapshot!;
        var snapshotBytes = await File.ReadAllBytesAsync(snapshot.SnapshotPath);
        var sessionIds = fixture.Service.Store.Sessions.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known spawn failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
            fixture.Service.SpawnTemplateAsync(snapshot));

        CollectionAssert.AreEqual(snapshotBytes, await File.ReadAllBytesAsync(snapshot.SnapshotPath));
        Assert.IsTrue(fixture.Service.Store.Sessions.Keys.All(sessionIds.Contains));
        Assert.AreEqual(sessionIds.Count, fixture.Service.Store.Sessions.Count);
        Assert.AreEqual(1, Directory.GetFiles(fixture.ClaudeRoot, "*.jsonl", SearchOption.AllDirectories).Length);
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual(sessionIds.Count, reloaded.Store.Sessions.Count);
        CollectionAssert.AreEqual(snapshotBytes, await File.ReadAllBytesAsync(
            reloaded.Store.TemplateSnapshots[snapshot.Id].SnapshotPath));
        Assert.IsFalse(reloaded.Store.Sessions.Values.Any(session =>
            !string.IsNullOrWhiteSpace(session.SourcePath) && !File.Exists(session.SourcePath)));
    }

    [TestMethod]
    public async Task RenameTemplateSnapshotAsync_BeforeReplace_RestoresOldName()
    {
        using var fixture = new ClaudeFixture();
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "old name")).Snapshot!;
        fixture.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace)
                throw new IOException("injected known rename failure");
        };

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
            fixture.Service.RenameTemplateSnapshotAsync(snapshot.Id, "new name"));

        Assert.AreEqual("old name", fixture.Service.Store.TemplateSnapshots[snapshot.Id].Name);
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual("old name", reloaded.Store.TemplateSnapshots[snapshot.Id].Name);
    }

    [TestMethod]
    public async Task DeleteTemplateSnapshotAsync_UnknownSave_DoesNotResurrectMetadata()
    {
        using var fixture = new ClaudeFixture();
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "to delete")).Snapshot!;
        var armed = false;
        fixture.Fault = stage =>
        {
            if (armed && stage is DurableWriteStage.BeforeDirectoryFlush or DurableWriteStage.BeforeVerificationRead)
                throw new IOException("injected delete save uncertainty");
        };
        armed = true;

        await Assert.ThrowsExactlyAsync<DurableWriteException>(() =>
            fixture.Service.DeleteTemplateSnapshotAsync(snapshot.Id));

        Assert.IsFalse(fixture.Service.Store.TemplateSnapshots.ContainsKey(snapshot.Id));
        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.IsFalse(reloaded.Store.TemplateSnapshots.ContainsKey(snapshot.Id));
    }

    private sealed class ClaudeFixture : IDisposable
    {
        public ClaudeFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "clr-branch-service-durable-" + Guid.NewGuid().ToString("N"));
            TemplatesRoot = Path.Combine(Root, "templates");
            ClaudeRoot = Path.Combine(Root, "claude");
            CodexRoot = Path.Combine(Root, "codex");
            StorePath = Path.Combine(Root, "store.json");
            Directory.CreateDirectory(ClaudeRoot);
            var id = "claude-source-11111111";
            var sourcePath = Path.Combine(ClaudeRoot, id + ".jsonl");
            File.WriteAllText(sourcePath,
                $"{{\"type\":\"user\",\"sessionId\":\"{id}\",\"uuid\":\"u1\",\"message\":{{\"role\":\"user\",\"content\":\"first\"}}}}\n" +
                $"{{\"type\":\"assistant\",\"sessionId\":\"{id}\",\"uuid\":\"u2\"}}\n");
            Source = new ArchiveSession
            {
                Id = id, Tool = "claude", Title = "Source chat", Workspace = Root,
                SourcePath = sourcePath
            };
            Service = NewService();
            Service.Store.Sessions[id] = Source;
            Service.SaveAsync().GetAwaiter().GetResult();
        }

        public string Root { get; }
        public string TemplatesRoot { get; }
        public string ClaudeRoot { get; }
        public string CodexRoot { get; }
        public string StorePath { get; }
        public ArchiveSession Source { get; }
        public ArchiveService Service { get; }
        public Action<DurableWriteStage>? Fault { set => _fault = value; }
        private Action<DurableWriteStage>? _fault;

        public ArchiveService NewService()
        {
            var service = new ArchiveService(
                storePath: StorePath,
                templatesRoot: TemplatesRoot,
                claudeSessionsRoot: ClaudeRoot,
                codexSessionsRoot: CodexRoot,
                enableTranscriptSearchIndex: false);
            var property = typeof(ArchiveService).GetProperty(
                "StoreWriteFault",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            property!.SetValue(service, (Action<DurableWriteStage>)(stage => _fault?.Invoke(stage)));
            return service;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch { }
        }
    }
}
