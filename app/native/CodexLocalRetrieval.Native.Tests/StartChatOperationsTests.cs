using System.Security.Cryptography;
using System.Text;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class StartChatOperationsTests
{
    [TestMethod]
    public async Task HandoffRequiresExactCodexSourceAndPreservesLinkAcrossReplay()
    {
        using var f = new Fixture();
        f.Service.Store.Sessions["codex-source"] = new ArchiveSession { Id = "codex-source", Tool = "codex", Workspace = f.Root };
        await f.Service.SaveAsync();
        var request = f.Blank("handoff", "claude") with { LaunchMode = ArchiveService.GatewayLaunchMode, HandoffFromId = "codex-source" };
        foreach (var invalid in new[] { request with { Tool = "codex" }, request with { LaunchMode = "native" }, request with { HandoffFromId = "missing" }, request with { HandoffFromId = "source" } })
        {
            var refused = await f.Service.PrepareStartChatAsync(invalid);
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual(0, f.Service.Store.PendingNewChats.Count);
        }
        var first = await f.Service.PrepareStartChatAsync(request);
        Assert.IsTrue(first.Ok, first.Detail);
        Assert.AreEqual("codex-source", f.Service.Store.PendingNewChats.Single().HandoffFromId);
        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        var replay = await restarted.PrepareStartChatAsync(request);
        Assert.IsTrue(replay.Ok, replay.Detail);
        Assert.AreEqual(first.ResultId, replay.ResultId);
        Assert.AreEqual("codex-source", restarted.Store.PendingNewChats.Single().HandoffFromId);
        Assert.AreEqual("codex", restarted.Store.Sessions["codex-source"].Tool);
        await restarted.MarkStartChatAppliedAsync(request, "created", "handoff-generation");
        restarted.Store.Sessions["new-claude"] = new ArchiveSession { Id = "new-claude", Tool = "claude", Workspace = f.Root };
        await restarted.SaveAsync();
        var listing = System.Text.Json.JsonSerializer.Serialize(new { list = new[] { new { name = request.MuxName, generationId = "handoff-generation", identityPending = false, sessionId = "new-claude" } } });
        Assert.IsTrue(await restarted.ReconcileStartChatBindingsAsync(listing));
        Assert.AreEqual("codex-source", restarted.Store.Sessions["new-claude"].HandoffFromId);
        Assert.AreEqual(ArchiveService.GatewayLaunchMode, restarted.Store.Sessions["new-claude"].LaunchMode);
        Assert.AreEqual("codex", restarted.Store.Sessions["codex-source"].Tool);
    }

    [TestMethod]
    public async Task BlankRestartReplayKeepsOnePendingAndFrozenDescriptor()
    {
        using var f = new Fixture();
        var request = f.Blank("blank-replay");
        var first = await f.Service.PrepareStartChatAsync(request);
        Assert.IsTrue(first.Ok, first.Detail);
        Assert.AreEqual("ready", first.State);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
        var frozenCommand = first.Launch!.Command;
        var frozenCwd = first.Launch.WorkingDirectory;

        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        restarted.Store.Settings.CodexLaunchArgs = "--changed-after-ready";
        await restarted.SaveAsync();
        var replay = await restarted.PrepareStartChatAsync(request);

        Assert.IsTrue(replay.Ok, replay.Detail);
        Assert.IsTrue(replay.Replay);
        Assert.AreEqual(1, restarted.Store.PendingNewChats.Count);
        Assert.AreEqual(first.ResultId, replay.ResultId);
        Assert.AreEqual(frozenCommand, replay.Launch!.Command);
        Assert.AreEqual(frozenCwd, replay.Launch.WorkingDirectory);
    }

    [TestMethod]
    public async Task CheckpointAndCollectionRestartReplayDoNotDuplicateArtifacts()
    {
        using var f = new Fixture();
        var checkpoint = await f.CreateCheckpointAsync();
        var request = new StartChatPreparationRequest(
            "checkpoint-replay", "mux-checkpoint", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: f.Service.TemplateSnapshotManagementRevision(checkpoint),
            CollectionName: "Prepared collection",
            Title: "Prepared branch",
            Phrase: "orchid");

        var first = await f.Service.PrepareStartChatAsync(request);
        Assert.IsTrue(first.Ok, first.Detail);
        var branchCount = f.Service.Store.Sessions.Values.Count(s => s.FromSnapshotId == checkpoint);
        var collectionCount = f.Service.Store.Collections.Values.Count(c => c.Name == "Prepared collection");

        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        var replay = await restarted.PrepareStartChatAsync(request);

        Assert.IsTrue(replay.Ok, replay.Detail);
        Assert.AreEqual(first.ResultId, replay.ResultId);
        Assert.AreEqual(branchCount, restarted.Store.Sessions.Values.Count(s => s.FromSnapshotId == checkpoint));
        Assert.AreEqual(collectionCount, restarted.Store.Collections.Values.Count(c => c.Name == "Prepared collection"));
        Assert.AreEqual("Prepared branch", restarted.Store.Sessions[first.ResultId].CustomTitle);
        CollectionAssert.Contains(restarted.Store.Sessions[first.ResultId].SpecialPhrases.ToList(), "orchid");
    }

    [TestMethod]
    public async Task ReplayAndCollisionPrecedeStaleSourceValidation()
    {
        using var f = new Fixture();
        var checkpoint = await f.CreateCheckpointAsync();
        var request = new StartChatPreparationRequest(
            "receipt-first", "mux-receipt", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: f.Service.TemplateSnapshotManagementRevision(checkpoint));
        var first = await f.Service.PrepareStartChatAsync(request);
        Assert.IsTrue(first.Ok, first.Detail);

        f.Service.Store.TemplateSnapshots.Remove(checkpoint);
        var replay = await f.Service.PrepareStartChatAsync(request);
        Assert.IsTrue(replay.Ok, replay.Detail);
        Assert.IsTrue(replay.Replay);

        var collision = await f.Service.PrepareStartChatAsync(request with { MuxName = "different" });
        Assert.IsFalse(collision.Ok);
        StringAssert.Contains(collision.Detail, "collision");
    }

    [TestMethod]
    public async Task StaleRevisionRefusesBeforeSpawnOrCollectionCreation()
    {
        using var f = new Fixture();
        var checkpoint = await f.CreateCheckpointAsync();
        var beforeSessions = f.Service.Store.Sessions.Count;
        var request = new StartChatPreparationRequest(
            "stale", "mux-stale", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: "stale",
            CollectionName: "must-not-exist");

        var result = await f.Service.PrepareStartChatAsync(request);

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(beforeSessions, f.Service.Store.Sessions.Count);
        Assert.IsFalse(f.Service.Store.Collections.Values.Any(c => c.Name == "must-not-exist"));
        Assert.IsFalse(f.Service.Store.ManagementOperations.ContainsKey("stale"));
    }

    [TestMethod]
    public async Task PreparedParentResumesAfterCheckpointBecomesStale()
    {
        using var f = new Fixture();
        var checkpoint = await f.CreateCheckpointAsync();
        var request = new StartChatPreparationRequest(
            "prepared-resume", "mux-prepared", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: f.Service.TemplateSnapshotManagementRevision(checkpoint));
        var replacements = 0;
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace && ++replacements == 3)
                throw new IOException("stop after child receipt");
        };

        var interrupted = await f.Service.PrepareStartChatAsync(request);
        Assert.IsFalse(interrupted.Ok);
        Assert.AreEqual("prepared", f.Service.Store.ManagementOperations[request.IntentId].State);
        f.Service.Store.TemplateSnapshots.Remove(checkpoint);
        await f.Service.SaveAsync();

        var resumed = await f.Service.PrepareStartChatAsync(request);

        Assert.IsTrue(resumed.Ok, resumed.Detail);
        Assert.AreEqual("ready", resumed.State);
        Assert.AreEqual(1, f.Service.Store.Sessions.Values.Count(s => s.FromSnapshotId == checkpoint));
    }

    [TestMethod]
    public async Task BusyBlankPreservesOlderPendingExactly()
    {
        using var f = new Fixture();
        var old = new PendingNewChat
        {
            IntentId = "older",
            Tool = "claude",
            Cwd = f.Root,
            CustomTitle = "keep me",
            CreatedAt = DateTime.UtcNow.ToString("O"),
        };
        f.Service.Store.PendingNewChats.Add(old);
        await f.Service.SaveAsync();

        var result = await f.Service.PrepareStartChatAsync(f.Blank("busy", tool: "claude"));

        Assert.IsFalse(result.Ok);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
        Assert.AreEqual("older", f.Service.Store.PendingNewChats[0].IntentId);
        Assert.AreEqual("keep me", f.Service.Store.PendingNewChats[0].CustomTitle);
        Assert.IsFalse(f.Service.Store.ManagementOperations.ContainsKey("busy"));
    }

    [TestMethod]
    public async Task EarlyRefusalAfterCheckpointMetadataCannotLeakIntoLaterSave()
    {
        using var f = new Fixture();
        var checkpoint = await f.CreateCheckpointAsync();
        f.Service.Store.Collections["target"] = new ArchiveCollection
        {
            Id = "target", Name = "Target", DeckId = "main"
        };
        await f.Service.SaveAsync();
        var request = new StartChatPreparationRequest(
            "metadata-abort", "mux-metadata-abort", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: f.Service.TemplateSnapshotManagementRevision(checkpoint),
            CollectionId: "target",
            CollectionRevision: f.Service.CollectionManagementRevision("target"),
            Title: "must not persist",
            Phrase: "must-not-persist");
        var poisonAfterPrepared = true;
        f.Fault = stage =>
        {
            if (poisonAfterPrepared && stage == DurableWriteStage.BeforeReadBack)
            {
                poisonAfterPrepared = false;
                f.Service.Store.Settings.ClaudeLaunchArgs = "--resume another-session";
            }
        };

        var refused = await f.Service.PrepareStartChatAsync(request);

        Assert.IsFalse(refused.Ok);
        Assert.IsTrue(refused.Uncertain);
        var branch = f.Service.Store.Sessions.Values.Single(s => s.FromSnapshotId == checkpoint);
        Assert.AreNotEqual("must not persist", branch.CustomTitle);
        Assert.IsFalse(branch.SpecialPhrases.Contains("must-not-persist"));
        Assert.IsFalse(f.Service.Store.Collections["target"].SessionIds.Contains(branch.Id));

        f.Fault = null;
        f.Service.Store.Settings.ClaudeLaunchArgs = "";
        f.Service.Store.Settings.Theme = "later-unrelated-save";
        await f.Service.SaveAsync();
        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        var persistedBranch = restarted.Store.Sessions[branch.Id];
        Assert.AreNotEqual("must not persist", persistedBranch.CustomTitle);
        Assert.IsFalse(persistedBranch.SpecialPhrases.Contains("must-not-persist"));
        Assert.IsFalse(restarted.Store.Collections["target"].SessionIds.Contains(branch.Id));
    }

    [TestMethod]
    public async Task NonIoFailureAfterCheckpointMetadataCannotLeakIntoLaterSave()
    {
        using var f = new Fixture();
        var checkpoint = await f.CreateCheckpointAsync();
        var request = new StartChatPreparationRequest(
            "non-io-metadata-abort", "mux-non-io", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: f.Service.TemplateSnapshotManagementRevision(checkpoint),
            Title: "must not persist",
            Phrase: "must-not-persist");
        var readBacks = 0;
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReadBack && ++readBacks == 2)
            {
                var spawned = f.Service.Store.Sessions.Values.Single(s => s.FromSnapshotId == checkpoint);
                spawned.SpecialPhrases.CollectionChanged += (_, _) =>
                    throw new InvalidOperationException("non-IO metadata failure");
            }
        };

        var refused = await f.Service.PrepareStartChatAsync(request);

        Assert.IsFalse(refused.Ok);
        Assert.IsFalse(refused.Uncertain);
        StringAssert.Contains(refused.Detail, "rolled back before commit");
        var branch = f.Service.Store.Sessions.Values.Single(s => s.FromSnapshotId == checkpoint);
        Assert.AreNotEqual("must not persist", branch.CustomTitle);
        Assert.IsFalse(branch.SpecialPhrases.Contains("must-not-persist"));

        f.Fault = null;
        f.Service.Store.Settings.Theme = "later-unrelated-save";
        await f.Service.SaveAsync();
        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        var persistedBranch = restarted.Store.Sessions[branch.Id];
        Assert.AreNotEqual("must not persist", persistedBranch.CustomTitle);
        Assert.IsFalse(persistedBranch.SpecialPhrases.Contains("must-not-persist"));
    }

    [TestMethod]
    public async Task EarlyRefusalAfterDirectoryCreationCannotLeakIntoLaterSave()
    {
        using var f = new Fixture();
        var request = f.Blank("directory-abort") with { Subfolder = "must-be-removed" };
        var cwd = Path.Combine(f.Root, request.Subfolder);
        var addCompetitorAfterPrepared = true;
        f.Fault = stage =>
        {
            if (addCompetitorAfterPrepared && stage == DurableWriteStage.BeforeReadBack)
            {
                addCompetitorAfterPrepared = false;
                f.Service.Store.PendingNewChats.Add(new PendingNewChat
                {
                    IntentId = "competitor",
                    Tool = request.Tool,
                    Cwd = cwd,
                    CreatedAt = DateTime.UtcNow.ToString("O"),
                });
            }
        };

        var refused = await f.Service.PrepareStartChatAsync(request);

        Assert.IsFalse(refused.Ok);
        Assert.IsTrue(refused.Uncertain);
        Assert.IsFalse(Directory.Exists(cwd));
        Assert.IsFalse(f.Service.Store.PendingNewChats.Any(p => p.IntentId != "competitor"));

        f.Fault = null;
        f.Service.Store.Settings.Theme = "later-unrelated-save";
        await f.Service.SaveAsync();
        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        Assert.IsFalse(Directory.Exists(cwd));
        Assert.IsFalse(restarted.Store.PendingNewChats.Any(p => p.IntentId != "competitor"));
        Assert.AreEqual("prepared", restarted.Store.ManagementOperations[request.IntentId].State);
    }

    [TestMethod]
    public async Task KnownSaveFailureRollsBackReadyAndPending()
    {
        using var f = new Fixture();
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace) throw new IOException("known failure");
        };

        var result = await f.Service.PrepareStartChatAsync(f.Blank("known-failure"));

        Assert.IsFalse(result.Ok);
        Assert.IsFalse(result.Uncertain);
        Assert.AreEqual(0, f.Service.Store.PendingNewChats.Count);
        Assert.IsFalse(f.Service.Store.ManagementOperations.ContainsKey("known-failure"));
        var restarted = f.NewService();
        await restarted.LoadStoreStateAsync();
        Assert.AreEqual(0, restarted.Store.PendingNewChats.Count);
        Assert.IsFalse(restarted.Store.ManagementOperations.ContainsKey("known-failure"));
    }

    [TestMethod]
    public async Task KnownReadySaveFailureRemovesNewSubfolderAndKeepsPreparedReceipt()
    {
        using var f = new Fixture();
        var subfolder = "prepared-subfolder";
        var request = f.Blank("known-ready-subfolder") with { Subfolder = subfolder };
        var replacements = 0;
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace && ++replacements == 2)
                throw new IOException("known ready failure");
        };

        var result = await f.Service.PrepareStartChatAsync(request);

        Assert.IsFalse(result.Ok);
        Assert.IsFalse(result.Uncertain);
        Assert.IsFalse(Directory.Exists(Path.Combine(f.Root, subfolder)));
        Assert.AreEqual(0, f.Service.Store.PendingNewChats.Count);
        Assert.AreEqual("prepared", f.Service.Store.ManagementOperations[request.IntentId].State);
    }

    [TestMethod]
    public async Task KnownReadySaveFailurePreservesPreexistingEmptySubfolder()
    {
        using var f = new Fixture();
        var request = f.Blank("existing-directory") with { Subfolder = "existing" };
        var directory = Path.Combine(f.Root, request.Subfolder);
        Directory.CreateDirectory(directory);
        var replacements = 0;
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace && ++replacements == 2)
                throw new IOException("known ready failure");
        };

        var result = await f.Service.PrepareStartChatAsync(request);

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(Directory.Exists(directory));
        Assert.AreEqual(0, f.Service.Store.PendingNewChats.Count);
        Assert.AreEqual("prepared", f.Service.Store.ManagementOperations[request.IntentId].State);
    }

    [TestMethod]
    public async Task UnknownSaveReloadsAndAcceptsOnlyAuthoritativeReadyReceipt()
    {
        using var f = new Fixture();
        var request = f.Blank("unknown-ready") with { Subfolder = "unknown-ready-subfolder" };
        var directoryFlushes = 0;
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeDirectoryFlush && ++directoryFlushes == 2)
                throw new IOException("unknown after ready replace");
        };

        var result = await f.Service.PrepareStartChatAsync(request);

        Assert.IsTrue(result.Ok, result.Detail);
        Assert.AreEqual("ready", result.State);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
        Assert.AreEqual("ready", f.Service.Store.ManagementOperations[request.IntentId].State);
        Assert.IsTrue(Directory.Exists(Path.Combine(f.Root, request.Subfolder)));
    }

    [TestMethod]
    public async Task FailedFinalizationRemovesOnlyExactPendingAndNeverDeletesSpawnedBranch()
    {
        using var f = new Fixture();
        var blank = f.Blank("finalize-blank");
        var prepared = await f.Service.PrepareStartChatAsync(blank);
        f.Service.Store.PendingNewChats.Add(new PendingNewChat
        {
            IntentId = "unrelated", Tool = "codex", Cwd = Path.Combine(f.Root, "other"), CreatedAt = DateTime.UtcNow.ToString("O")
        });
        await f.Service.SaveAsync();
        var failed = await f.Service.MarkStartChatFailedAsync(blank, "definitive mux refusal");
        Assert.AreEqual("failed", failed.State);
        Assert.IsFalse(f.Service.Store.PendingNewChats.Any(p => p.IntentId == prepared.ResultId));
        Assert.IsTrue(f.Service.Store.PendingNewChats.Any(p => p.IntentId == "unrelated"));

        var checkpoint = await f.CreateCheckpointAsync();
        var checkpointRequest = new StartChatPreparationRequest(
            "finalize-checkpoint", "mux-final-checkpoint", "main",
            CheckpointId: checkpoint,
            CheckpointRevision: f.Service.TemplateSnapshotManagementRevision(checkpoint));
        var checkpointPrepared = await f.Service.PrepareStartChatAsync(checkpointRequest);
        Assert.IsTrue(checkpointPrepared.Ok, checkpointPrepared.Detail);
        var sourcePath = f.Service.Store.Sessions[checkpointPrepared.ResultId].SourcePath;
        var checkpointFailed = await f.Service.MarkStartChatFailedAsync(checkpointRequest, "definitive mux refusal");
        Assert.AreEqual("failed", checkpointFailed.State);
        Assert.IsTrue(f.Service.Store.Sessions.ContainsKey(checkpointPrepared.ResultId));
        Assert.IsTrue(File.Exists(sourcePath));
    }

    [TestMethod]
    public async Task LostLaunchResponseRetainsReadyAndReplaysExactPayload()
    {
        using var f = new Fixture();
        var request = f.Blank("lost-response");
        string? firstPayload = null;
        var calls = 0;
        Task<string> Mux(object frame)
        {
            var payload = System.Text.Json.JsonSerializer.Serialize(frame);
            if (++calls == 1)
            {
                firstPayload = payload;
                throw new IOException("response lost after create");
            }
            Assert.AreEqual(firstPayload, payload);
            return Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new { t = "created", s = request.MuxName, created = false, generationId = "original-generation" }));
        }
        Task<StartChatPreparationResult> Run() => StartChatCoordinator.ExecuteAsync(request,
            () => f.Service.PrepareStartChatAsync(request),
            (ok, detail, generation) => ok ? f.Service.MarkStartChatAppliedAsync(request, detail, generation) : f.Service.MarkStartChatFailedAsync(request, detail), Mux);

        var lost = await Run();
        Assert.IsTrue(lost.Uncertain);
        Assert.AreEqual("ready", f.Service.Store.ManagementOperations[request.IntentId].State);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
        var replay = await Run();
        Assert.IsTrue(replay.Ok);
        Assert.AreEqual("applied", replay.State);
        Assert.AreEqual("original-generation", f.Service.Store.ManagementOperations[request.IntentId].StartChatMuxGeneration);
        var reloaded = f.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual("original-generation", reloaded.Store.ManagementOperations[request.IntentId].StartChatMuxGeneration);
        await Run();
        Assert.AreEqual(2, calls, "Terminal receipt must not dispatch again");
    }

    [DataTestMethod]
    [DataRow("{\"t\":\"err\",\"retryable\":true}", true)]
    [DataRow("{\"t\":\"err\"}", true)]
    [DataRow("{\"t\":\"err\",\"retryable\":false}", false)]
    public async Task LaunchRefusalFinalizesOnlyExplicitDefinitiveFailure(string response, bool uncertain)
    {
        using var f = new Fixture();
        var request = f.Blank("refusal-classification");
        var result = await StartChatCoordinator.ExecuteAsync(request,
            () => f.Service.PrepareStartChatAsync(request),
            (ok, detail, generation) => ok ? f.Service.MarkStartChatAppliedAsync(request, detail, generation) : f.Service.MarkStartChatFailedAsync(request, detail),
            _ => Task.FromResult(response));
        Assert.IsFalse(result.Ok);
        Assert.AreEqual(uncertain, result.Uncertain);
        Assert.AreEqual(uncertain ? "ready" : "failed", f.Service.Store.ManagementOperations[request.IntentId].State);
        Assert.AreEqual(uncertain ? 1 : 0, f.Service.Store.PendingNewChats.Count);
    }

    [DataTestMethod]
    [DataRow("original-generation", false, true)]
    [DataRow("replacement-generation", false, false)]
    [DataRow("original-generation", true, false)]
    public async Task FilingRequiresExactBoundGeneration(string generation, bool pending, bool expected)
    {
        using var f = new Fixture();
        var request = f.Blank("exact-filing");
        await f.Service.PrepareStartChatAsync(request);
        await f.Service.MarkStartChatAppliedAsync(request, "created", "original-generation");
        f.Service.Store.Sessions["exact-chat"] = new ArchiveSession
        {
            Id = "exact-chat", Tool = "codex", Workspace = f.Root, CustomTitle = "Original"
        };
        await f.Service.SaveAsync();
        var listing = System.Text.Json.JsonSerializer.Serialize(new
        {
            list = new[] { new { name = request.MuxName, generationId = generation,
                identityPending = pending, sessionId = "exact-chat" } }
        });
        Assert.AreEqual(expected, await f.Service.ReconcileStartChatBindingsAsync(listing));
        var reloaded = f.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual(expected ? "New chat" : "Original", reloaded.Store.Sessions["exact-chat"].CustomTitle);
        Assert.AreEqual(expected ? 0 : 1, reloaded.Store.PendingNewChats.Count);
        Assert.IsFalse(await reloaded.ReconcileStartChatBindingsAsync(listing));
    }

    [TestMethod]
    public async Task FilingSaveFailurePreservesPendingAndOriginalMetadata()
    {
        using var f = new Fixture();
        var request = f.Blank("filing-save-failure");
        await f.Service.PrepareStartChatAsync(request);
        await f.Service.MarkStartChatAppliedAsync(request, "created", "generation");
        f.Service.Store.Sessions["exact-chat"] = new ArchiveSession
        {
            Id = "exact-chat", Tool = "codex", CustomTitle = "Original"
        };
        await f.Service.SaveAsync();
        var listing = System.Text.Json.JsonSerializer.Serialize(new
        {
            list = new[] { new { name = request.MuxName, generationId = "generation",
                identityPending = false, sessionId = "exact-chat" } }
        });
        f.Fault = stage =>
        {
            if (stage == DurableWriteStage.BeforeReplace) throw new IOException("filing write refused");
        };
        try
        {
            await f.Service.ReconcileStartChatBindingsAsync(listing);
            Assert.Fail("Expected durable write failure");
        }
        catch (IOException) { }
        Assert.AreEqual("Original", f.Service.Store.Sessions["exact-chat"].CustomTitle);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
        f.Fault = null;
        var reloaded = f.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual("Original", reloaded.Store.Sessions["exact-chat"].CustomTitle);
        Assert.AreEqual(1, reloaded.Store.PendingNewChats.Count);
        Assert.IsTrue(await reloaded.ReconcileStartChatBindingsAsync(listing));
    }

    [DataTestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public async Task CreatedWithoutGenerationRemainsReady(string? generation)
    {
        using var f = new Fixture();
        var request = f.Blank("unverified-generation");
        var result = await StartChatCoordinator.ExecuteAsync(request,
            () => f.Service.PrepareStartChatAsync(request),
            (_, _, _) => throw new AssertFailedException("Unverified generation must not finalize"),
            _ => Task.FromResult(System.Text.Json.JsonSerializer.Serialize(new
            {
                t = "created", s = request.MuxName, generationId = generation
            })));
        Assert.IsTrue(result.Uncertain);
        Assert.AreEqual("ready", f.Service.Store.ManagementOperations[request.IntentId].State);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
    }

    [TestMethod]
    public async Task DurableBlankStartNeverFilesUnrelatedSingleWorkspaceCandidate()
    {
        using var f = new Fixture();
        var request = f.Blank("no-heuristic-filing");
        var prepared = await f.Service.PrepareStartChatAsync(request);
        Assert.IsTrue(prepared.Ok);
        f.Service.Store.Sessions["unrelated-new"] = new ArchiveSession
        {
            Id = "unrelated-new", Tool = "codex", Workspace = f.Root,
            CreatedAt = DateTime.UtcNow.ToString("O"), CustomTitle = "Keep original"
        };
        await f.Service.SaveAsync();
        await f.Service.ReconcilePendingNewChatsAsync();
        Assert.AreEqual("Keep original", f.Service.Store.Sessions["unrelated-new"].CustomTitle);
        Assert.AreEqual(1, f.Service.Store.PendingNewChats.Count);
        Assert.AreEqual(prepared.ResultId, f.Service.Store.PendingNewChats[0].IntentId);
    }

    private sealed class Fixture : IDisposable
    {
        private Action<DurableWriteStage>? _fault;

        public Fixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "startchat-ops-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Path.Combine(Root, "claude"));
            var sourcePath = Path.Combine(Root, "claude", "source.jsonl");
            File.WriteAllText(sourcePath, "{\"type\":\"user\",\"sessionId\":\"source\",\"uuid\":\"u1\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}\n");
            Service = NewService();
            Service.LoadStoreStateAsync().GetAwaiter().GetResult();
            Service.Store.Sessions["source"] = new ArchiveSession
            {
                Id = "source", Tool = "claude", Title = "Source", Workspace = Root, SourcePath = sourcePath,
            };
            Service.SaveAsync().GetAwaiter().GetResult();
        }

        public string Root { get; }
        public ArchiveService Service { get; }
        public Action<DurableWriteStage>? Fault { get => _fault; set => _fault = value; }

        public StartChatPreparationRequest Blank(string intent, string tool = "codex") => new(
            intent, "mux-" + intent, "main", tool, WorkspaceId(Root), Title: "New chat", Phrase: "violet");

        public async Task<string> CreateCheckpointAsync()
        {
            var source = Service.Store.Sessions["source"];
            var created = await Service.ExecuteBranchOperationAsync(
                "checkpointcreate", "make-checkpoint-" + Guid.NewGuid().ToString("N"), source.Id, source.Tool,
                Service.RemoteManagementRevision(source), "checkpoint");
            Assert.IsTrue(created.Ok, created.Detail);
            return created.ResultId;
        }

        public ArchiveService NewService()
        {
            var service = new ArchiveService(
                Path.Combine(Root, "store.json"),
                templatesRoot: Path.Combine(Root, "templates"),
                claudeSessionsRoot: Path.Combine(Root, "claude"),
                enableTranscriptSearchIndex: false);
            var property = typeof(ArchiveService).GetProperty(
                "StoreWriteFault", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            property!.SetValue(service, (Action<DurableWriteStage>)(stage => _fault?.Invoke(stage)));
            return service;
        }

        private static string WorkspaceId(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var identity = full.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar).ToUpperInvariant();
            return "ws-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch { } }
    }
}
