using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class BranchSessionTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task BranchSessionAsync_Claude_ClonesTranscriptAndLinksParent()
    {
        using var fixture = new TemplateFixture("claude");
        var result = await fixture.Service.BranchSessionAsync(fixture.Source);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        Assert.AreEqual(fixture.Source.Id, result.Branch.BranchOfId);
        Assert.IsTrue(File.Exists(result.Branch.SourcePath));
        var text = await File.ReadAllTextAsync(result.Branch.SourcePath);
        StringAssert.Contains(text, result.Branch.Id);
        StringAssert.Contains(text, fixture.Source.Id);
        StringAssert.Contains(text, "snapshot-tip");
        Assert.AreEqual(ArchiveService.NativeLaunchMode, result.Branch.LaunchMode);
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    public async Task ForkSessionAsync_Gateway_ClonesNativeTranscriptAndPersistsLaunchMode(string tool)
    {
        using var fixture = new TemplateFixture(tool);
        var initialThreadCount = tool == "codex" ? fixture.ThreadCount() : 0;

        var result = await fixture.Service.ForkSessionAsync(
            fixture.Source,
            ArchiveService.GatewayLaunchMode);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        Assert.AreEqual(tool, result.Branch.Tool);
        Assert.AreEqual(ArchiveService.GatewayLaunchMode, result.Branch.LaunchMode);
        Assert.IsTrue(result.Branch.IsGatewayBranch);
        Assert.AreEqual("GW", result.Branch.GatewayGlyph);
        StringAssert.Contains(result.Branch.ListMarks, "GW");
        StringAssert.Contains(result.Branch.DisplayTitle, "(branch)");
        Assert.IsFalse(result.Branch.DisplayTitle.Contains("Gateway", StringComparison.OrdinalIgnoreCase));
        Assert.IsTrue(File.Exists(result.Branch.SourcePath));
        StringAssert.Contains(await File.ReadAllTextAsync(result.Branch.SourcePath), fixture.Source.Id);

        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.AreEqual(
            ArchiveService.GatewayLaunchMode,
            reloaded.Store.Sessions[result.Branch.Id].LaunchMode);

        if (tool == "codex")
        {
            Assert.AreEqual(initialThreadCount + 1, fixture.ThreadCount());
            Assert.AreEqual(result.Branch.SourcePath, fixture.RolloutPath(result.Branch.Id));
        }
    }

    [TestMethod]
    [DataRow(ArchiveService.DeepSeekLaunchMode, "claude")]
    [DataRow(ArchiveService.LunaLaunchMode, "claude")]
    [DataRow(ArchiveService.DeepSeekLaunchMode, "codex")]
    [DataRow(ArchiveService.LunaLaunchMode, "codex")]
    public async Task ForkSessionAsync_LegacyDeepSeekOrLuna_ClonesAsGatewayBranchAndUsesValidContinuation(string legacyMode, string tool)
    {
        using var fixture = new TemplateFixture(tool);

        var result = await fixture.Service.ForkSessionAsync(fixture.Source, legacyMode);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        Assert.AreEqual(ArchiveService.GatewayLaunchMode, result.Branch.LaunchMode);
        Assert.IsTrue(result.Branch.IsGatewayBranch);
        StringAssert.Contains(result.Branch.ListMarks, "GW");
        StringAssert.Contains(await File.ReadAllTextAsync(result.Branch.SourcePath), fixture.Source.Id);

        var launch = fixture.Service.BuildResumeLaunch(result.Branch, exeOverride: "C:\\cmd.exe");
        if (tool == "claude")
        {
            Assert.AreEqual("C:\\cmd.exe", launch.Exe);
            StringAssert.Contains(launch.Arguments, "cc.cmd");
            StringAssert.Contains(launch.Arguments, $"--resume {result.Branch.Id}");
        }
        else
        {
            Assert.AreEqual("", launch.Exe);
            StringAssert.Contains(launch.DisplayCommand, "Claude transcripts only");

            var handoff = fixture.Service.BuildGatewayHandoffLaunch(
                result.Branch,
                exeOverride: "C:\\cmd.exe");
            Assert.AreEqual("C:\\cmd.exe", handoff.Exe);
            StringAssert.Contains(handoff.Arguments, "cc.cmd");
            Assert.IsFalse(handoff.Arguments.Contains("--resume", StringComparison.OrdinalIgnoreCase));
        }

        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        Assert.IsTrue(reloaded.Store.Sessions[result.Branch.Id].IsGatewayBranch);
    }

    // A branch must report the CHECKPOINT it came from, not the source chat's current name.
    //
    // Measured on the owner's store: three branches spawned from checkpoint "cleanmusic" carried
    // cleanmusic's transcript exactly (1310 user messages, tip uuid 1167d9f7) while the app described
    // them as "branch of iosnmusic-pixel" - the name the SOURCE chat had been renamed to an hour after
    // the checkpoint was taken. The spawn was correct and the label said it was not, which reads as
    // having branched from the wrong checkpoint. BranchOfId points at a moving target; FromSnapshotId
    // does not, and it was already recorded correctly.
    [TestMethod]
    public async Task DescribeSession_NamesTheCheckpoint_NotTheRenamedSource()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "cleanmusic");
        Assert.IsTrue(snapshot.Ok, snapshot.Message);
        Assert.IsNotNull(snapshot.Snapshot);

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot.Snapshot);
        Assert.IsTrue(spawned.Ok, spawned.Message);
        Assert.IsNotNull(spawned.Branch);

        // The source chat keeps living and gets renamed - this is what made the old label lie.
        fixture.Source.CustomTitle = "iosnmusic-pixel";

        var described = fixture.Service.DescribeSession(spawned.Branch);
        StringAssert.Contains(described, "cleanmusic", "the branch must name the checkpoint it came from");
        Assert.IsFalse(
            described.Contains("branch of: \"iosnmusic-pixel\""),
            "the branch must not be attributed to the source chat's post-checkpoint name: " + described);
    }

    [TestMethod]
    public void RewriteCodexSessionMeta_RewritesIdAndStampsFork()
    {
        var line = "{\"timestamp\":\"2026-07-08T22:44:11Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"old-id\",\"id\":\"old-id\",\"cwd\":\"z:/proj\"}}";
        var output = ArchiveService.RewriteCodexSessionMeta(line, "old-id", "new-id");

        using var document = JsonDocument.Parse(output);
        var payload = document.RootElement.GetProperty("payload");
        Assert.AreEqual("new-id", payload.GetProperty("id").GetString());
        Assert.AreEqual("new-id", payload.GetProperty("session_id").GetString());
        Assert.AreEqual("old-id", payload.GetProperty("forked_from_id").GetString());
    }

    [TestMethod]
    public async Task Claude_TemplateSnapshot_DoesNotChangeWhenSourceAppends()
    {
        using var fixture = new TemplateFixture("claude");
        var created = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source);
        Assert.IsTrue(created.Ok, created.Message);
        var before = await File.ReadAllTextAsync(created.Snapshot!.SnapshotPath);

        await File.AppendAllTextAsync(fixture.Source.SourcePath, ClaudeLine(fixture.Source.Id, "u3", "later-source-tip"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(created.Snapshot.SnapshotPath));
        Assert.DoesNotContain("later-source-tip", before);
    }

    [TestMethod]
    public async Task Codex_TemplateSnapshot_DoesNotChangeWhenSourceAppends()
    {
        using var fixture = new TemplateFixture("codex");
        var created = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source);
        Assert.IsTrue(created.Ok, created.Message);
        var before = await File.ReadAllTextAsync(created.Snapshot!.SnapshotPath);

        await File.AppendAllTextAsync(fixture.Source.SourcePath, CodexMessage("later-source-tip"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(created.Snapshot.SnapshotPath));
        Assert.DoesNotContain("later-source-tip", before);
    }

    [TestMethod]
    public async Task SpawnedClaudeChat_UsesSnapshotTip_NotCurrentSourceTip()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        await File.AppendAllTextAsync(fixture.Source.SourcePath, ClaudeLine(fixture.Source.Id, "u3", "current-source-tip"));

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(spawned.Ok, spawned.Message);
        var text = await File.ReadAllTextAsync(spawned.Branch!.SourcePath);
        StringAssert.Contains(text, "snapshot-tip");
        Assert.DoesNotContain("current-source-tip", text);
    }

    [TestMethod]
    public async Task SpawnedCodexChat_UsesSnapshotTip_NotCurrentSourceTip()
    {
        using var fixture = new TemplateFixture("codex");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        await File.AppendAllTextAsync(fixture.Source.SourcePath, CodexMessage("current-source-tip"));

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(spawned.Ok, spawned.Message);
        var text = await File.ReadAllTextAsync(spawned.Branch!.SourcePath);
        StringAssert.Contains(text, "snapshot-tip");
        Assert.DoesNotContain("current-source-tip", text);
    }

    [TestMethod]
    public async Task SpawnTemplateAsync_RecordsExactSnapshotLineage()
    {
        using var fixture = new TemplateFixture("claude");
        var first = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "same name")).Snapshot!;
        await fixture.AppendTurnAsync(fixture.Source, "after-first");
        var second = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "same name")).Snapshot!;

        var fromFirst = (await fixture.Service.SpawnTemplateAsync(first)).Branch!;
        var fromSecond = (await fixture.Service.SpawnTemplateAsync(second)).Branch!;

        Assert.AreEqual(fixture.Source.Id, fromFirst.BranchOfId);
        Assert.AreEqual(fixture.Source.Id, fromSecond.BranchOfId);
        Assert.AreEqual(first.Id, fromFirst.FromSnapshotId);
        Assert.AreEqual(second.Id, fromSecond.FromSnapshotId);
        Assert.AreNotEqual(fromFirst.FromSnapshotId, fromSecond.FromSnapshotId);
    }

    [TestMethod]
    public async Task BranchesForSnapshot_ExcludesOtherSnapshotsAndLegacyBranches()
    {
        using var fixture = new TemplateFixture("claude");
        var first = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "T1")).Snapshot!;
        await fixture.AppendTurnAsync(fixture.Source, "after-first");
        var second = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "T2")).Snapshot!;
        var fromFirst = (await fixture.Service.SpawnTemplateAsync(first)).Branch!;
        var fromSecond = (await fixture.Service.SpawnTemplateAsync(second)).Branch!;
        var legacy = (await fixture.Service.BranchSessionAsync(fixture.Source)).Branch!;

        CollectionAssert.AreEqual(
            new[] { fromFirst.Id },
            fixture.Service.BranchesForSnapshot(first.Id).Select(branch => branch.Id).ToArray());
        CollectionAssert.AreEqual(
            new[] { fromSecond.Id },
            fixture.Service.BranchesForSnapshot(second.Id).Select(branch => branch.Id).ToArray());
        Assert.AreEqual("", legacy.FromSnapshotId);
        Assert.IsFalse(fixture.Service.BranchesForSnapshot(first.Id).Any(branch => branch.Id == legacy.Id));
        Assert.IsFalse(fixture.Service.BranchesForSnapshot(second.Id).Any(branch => branch.Id == legacy.Id));
    }

    [TestMethod]
    public async Task SnapshotDisplayLabels_AreDistinctAndIncludeSecondsAndCounts()
    {
        using var fixture = new TemplateFixture("claude");
        var first = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "same name")).Snapshot!;
        await fixture.AppendTurnAsync(fixture.Source, "one-more-message");
        var second = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "same name")).Snapshot!;

        var firstLabel = ArchiveService.TemplateSnapshotDisplayLabel(first);
        var secondLabel = ArchiveService.TemplateSnapshotDisplayLabel(second);

        Assert.AreNotEqual(firstLabel, secondLabel);
        StringAssert.Matches(firstLabel, new System.Text.RegularExpressions.Regex(@"\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}"));
        StringAssert.Contains(firstLabel, $"{first.MessageCount} messages");
        StringAssert.Contains(secondLabel, $"{second.MessageCount} messages");
        Assert.IsTrue(second.MessageCount > first.MessageCount);
        Assert.IsTrue(first.LineCount > 0);
        Assert.IsTrue(second.LineCount > first.LineCount);
    }

    [TestMethod]
    public async Task OpenTemplateSnapshotAsync_ParsesFrozenTranscriptReadOnly()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source, "frozen")).Snapshot!;
        var frozenText = await File.ReadAllTextAsync(snapshot.SnapshotPath);
        await fixture.AppendTurnAsync(fixture.Source, "source-changed-later");

        var opened = await fixture.Service.OpenTemplateSnapshotAsync(snapshot.Id);

        Assert.IsTrue(opened.Ok, opened.Message);
        Assert.IsNotNull(opened.Reader);
        Assert.IsTrue(opened.Reader.IsReadOnlySnapshot);
        Assert.AreEqual(snapshot.Id, opened.Reader.ReadOnlySnapshotId);
        Assert.AreEqual(snapshot.SnapshotPath, opened.Reader.SourcePath);
        Assert.AreEqual(snapshot.MessageCount, opened.Reader.MessageCount);
        Assert.IsTrue(opened.Reader.ContentLoaded);
        Assert.IsFalse(fixture.Service.Store.Sessions.ContainsKey(opened.Reader.Id));
        var resume = fixture.Service.BuildResumeLaunch(opened.Reader, exeOverride: "C:\\claude.exe");
        Assert.AreEqual("", resume.Exe);
        StringAssert.Contains(resume.DisplayCommand, "read-only");
        Assert.AreEqual(frozenText, await File.ReadAllTextAsync(snapshot.SnapshotPath));
        Assert.IsFalse(opened.Reader.Messages.Any(message =>
            message.Text.Contains("source-changed-later", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task AppendingToSpawnedClaudeChat_DoesNotModifySnapshot()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);
        var before = await File.ReadAllTextAsync(snapshot.SnapshotPath);

        await File.AppendAllTextAsync(spawned.Branch!.SourcePath, ClaudeLine(spawned.Branch.Id, "spawn-u3", "spawn-append"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(snapshot.SnapshotPath));
    }

    [TestMethod]
    public async Task AppendingToSpawnedCodexChat_DoesNotModifySnapshot()
    {
        using var fixture = new TemplateFixture("codex");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);
        var before = await File.ReadAllTextAsync(snapshot.SnapshotPath);

        await File.AppendAllTextAsync(spawned.Branch!.SourcePath, CodexMessage("spawn-append"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(snapshot.SnapshotPath));
    }

    [TestMethod]
    public async Task TemplateSnapshot_IsAbsentFromSessionsSearchCollectionsProjectionAndAliasResolution()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(
            fixture.Source,
            "unique-checkpoint-only-name")).Snapshot!;

        Assert.IsFalse(fixture.Service.Store.Sessions.ContainsKey(snapshot.Id));
        Assert.IsFalse(fixture.Service.Search("unique-checkpoint-only-name").Any());
        Assert.IsFalse(fixture.Service.Store.Collections.Values.Any(collection => collection.SessionIds.Contains(snapshot.Id)));
        Assert.DoesNotContain(snapshot.Id, fixture.Service.BuildProjectsProjectionJson());
        Assert.IsNull(fixture.Service.ResolveSessionByIdOrAlias(snapshot.Id));
    }

    [TestMethod]
    public async Task DeletingTemplate_CannotResurrectAfterDiskSync()
    {
        using var fixture = new TemplateFixture("claude");
        fixture.Service.Store.Settings.Sources =
        [
            new SessionSource { Tool = "claude", Root = fixture.NativeRoot }
        ];
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;

        Assert.IsTrue(await fixture.Service.DeleteTemplateSnapshotAsync(snapshot.Id));
        await fixture.Service.SyncFromDiskAsync();

        Assert.IsFalse(fixture.Service.Store.TemplateSnapshots.ContainsKey(snapshot.Id));
        Assert.IsFalse(File.Exists(snapshot.SnapshotPath));
    }

    [TestMethod]
    public async Task Codex_SnapshotCreation_DoesNotInsertThreadRow()
    {
        using var fixture = new TemplateFixture("codex");
        var before = fixture.ThreadCount();

        var created = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source);

        Assert.IsTrue(created.Ok, created.Message);
        Assert.AreEqual(before, fixture.ThreadCount());
    }

    [TestMethod]
    public async Task Codex_Spawn_InsertsExactlyOneThreadWithCorrectRolloutPath()
    {
        using var fixture = new TemplateFixture("codex");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var before = fixture.ThreadCount();

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(spawned.Ok, spawned.Message);
        Assert.AreEqual(before + 1, fixture.ThreadCount());
        Assert.AreEqual(spawned.Branch!.SourcePath, fixture.RolloutPath(spawned.Branch.Id));
    }

    [TestMethod]
    [DataRow("claude")]
    [DataRow("codex")]
    public async Task CheckpointsRemainPristineAndSpawnIndependentResumableChats(string tool)
    {
        using var fixture = new TemplateFixture(tool);
        var source = fixture.Source;
        var initialTurns = UserTurns(tool, source.SourcePath);
        var initialThreadCount = tool == "codex" ? fixture.ThreadCount() : 0;

        var t1 = (await fixture.Service.CreateTemplateSnapshotAsync(source, "T1")).Snapshot!;
        var t1Bytes = await File.ReadAllBytesAsync(t1.SnapshotPath);

        await fixture.AppendTurnAsync(source, "continued-after-t1");
        var continuedTurns = UserTurns(tool, source.SourcePath);
        var sourceAtT2 = await File.ReadAllBytesAsync(source.SourcePath);
        var t2 = (await fixture.Service.CreateTemplateSnapshotAsync(source, "T2")).Snapshot!;
        var t2Bytes = await File.ReadAllBytesAsync(t2.SnapshotPath);

        var fromT1 = (await fixture.Service.SpawnTemplateAsync(t1)).Branch!;
        var fromT2 = (await fixture.Service.SpawnTemplateAsync(t2)).Branch!;

        CollectionAssert.AreEqual(initialTurns, UserTurns(tool, fromT1.SourcePath));
        CollectionAssert.AreEqual(continuedTurns, UserTurns(tool, fromT2.SourcePath));
        CollectionAssert.AreEqual(t1Bytes, await File.ReadAllBytesAsync(t1.SnapshotPath));
        CollectionAssert.AreEqual(t2Bytes, await File.ReadAllBytesAsync(t2.SnapshotPath));
        CollectionAssert.AreEqual(sourceAtT2, await File.ReadAllBytesAsync(source.SourcePath));
        Assert.AreNotEqual(source.Id, fromT1.Id);
        Assert.AreNotEqual(source.Id, fromT2.Id);
        Assert.AreNotEqual(fromT1.Id, fromT2.Id);
        Assert.AreNotEqual(fromT1.SourcePath, fromT2.SourcePath);
        Assert.IsFalse(fromT1.Aliases.Contains(source.Id), "lineage must not be an identity alias");
        Assert.IsFalse(fromT2.Aliases.Contains(source.Id), "lineage must not be an identity alias");

        var exe = tool == "claude" ? "C:\\claude.exe" : "C:\\codex.exe";
        var sourceLaunch = fixture.Service.BuildResumeLaunch(source, exeOverride: exe);
        var t1Launch = fixture.Service.BuildResumeLaunch(fromT1, exeOverride: exe);
        var t2Launch = fixture.Service.BuildResumeLaunch(fromT2, exeOverride: exe);
        StringAssert.Contains(sourceLaunch.Arguments, source.Id);
        StringAssert.Contains(t1Launch.Arguments, fromT1.Id);
        StringAssert.Contains(t2Launch.Arguments, fromT2.Id);

        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { source.Id };
        Assert.IsFalse(RunningSessions.IsSessionLive(fromT1.Id, fromT1.Aliases, live));
        Assert.IsFalse(RunningSessions.IsSessionLive(fromT2.Id, fromT2.Aliases, live));
        Assert.IsTrue(RunningSessions.IsSessionLive(source.Id, source.Aliases, live));

        var claimRoot = Path.Combine(fixture.Root, "claims");
        var claimOptions = new SessionLaunchClaims.Options(claimRoot);
        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            fromT1.Id, fromT1.Aliases, "checkpoint T1 launch", out var t1Claim, out var t1Detail,
            id => live.Contains(id), claimOptions), t1Detail);
        using (t1Claim)
        {
            Assert.IsTrue(SessionLaunchClaims.TryAcquire(
                fromT2.Id, fromT2.Aliases, "checkpoint T2 launch", out var t2Claim, out var t2Detail,
                id => live.Contains(id), claimOptions), t2Detail);
            using (t2Claim)
            {
                Assert.AreEqual(2, Directory.GetFiles(claimRoot, "*.json").Length);
            }
        }

        live.Clear();
        live.Add(fromT1.Id);
        Assert.IsFalse(RunningSessions.IsSessionLive(source.Id, source.Aliases, live));
        Assert.IsFalse(RunningSessions.IsSessionLive(fromT2.Id, fromT2.Aliases, live));
        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            source.Id, source.Aliases, "original launch", out var sourceClaim, out var sourceDetail,
            id => live.Contains(id), claimOptions), sourceDetail);
        using (sourceClaim)
        {
            Assert.IsTrue(SessionLaunchClaims.TryAcquire(
                fromT2.Id, fromT2.Aliases, "other checkpoint launch", out var otherClaim, out var otherDetail,
                id => live.Contains(id), claimOptions), otherDetail);
            otherClaim?.Dispose();
        }

        var remoteApi = new RemoteApi(
            fixture.Service,
            () => null,
            allowLaunch: false,
            resumeLaunchFactory: session => fixture.Service.BuildResumeLaunch(session, exeOverride: exe));
        var remoteT2Json = JsonSerializer.Serialize(remoteApi.ResumeCommand(
            fromT2.Id,
            launch: true,
            IntegrityOptions(live)));
        using var remoteT2 = JsonDocument.Parse(remoteT2Json);
        Assert.AreEqual(t2Launch.DisplayCommand, remoteT2.RootElement.GetProperty("command").GetString());
        StringAssert.Contains(remoteT2.RootElement.GetProperty("note").GetString() ?? "", "Launch is disabled");

        await fixture.AppendTurnAsync(source, "original-continued-after-spawns");
        CollectionAssert.AreEqual(
            continuedTurns.Concat(new[] { "original-continued-after-spawns" }).ToArray(),
            UserTurns(tool, source.SourcePath));
        CollectionAssert.AreEqual(initialTurns, UserTurns(tool, fromT1.SourcePath));
        CollectionAssert.AreEqual(continuedTurns, UserTurns(tool, fromT2.SourcePath));
        CollectionAssert.AreEqual(t1Bytes, await File.ReadAllBytesAsync(t1.SnapshotPath));
        CollectionAssert.AreEqual(t2Bytes, await File.ReadAllBytesAsync(t2.SnapshotPath));

        if (tool == "codex")
        {
            Assert.AreEqual(initialThreadCount + 2, fixture.ThreadCount());
            Assert.AreEqual(fromT1.SourcePath, fixture.RolloutPath(fromT1.Id));
            Assert.AreEqual(fromT2.SourcePath, fixture.RolloutPath(fromT2.Id));
        }
    }

    [TestMethod]
    public async Task Load_RemovesLegacyBranchParentIdentityAlias()
    {
        using var fixture = new TemplateFixture("claude");
        var branch = (await fixture.Service.BranchSessionAsync(fixture.Source)).Branch!;
        branch.Aliases.Add(fixture.Source.Id);
        var json = JsonSerializer.Serialize(fixture.Service.Store);
        await File.WriteAllTextAsync(fixture.StorePath, json);

        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();

        Assert.IsFalse(reloaded.Store.Sessions[branch.Id].Aliases.Contains(fixture.Source.Id));
        Assert.AreEqual(fixture.Source.Id, reloaded.Store.Sessions[branch.Id].BranchOfId);
    }

    [TestMethod]
    public async Task Load_PreservesSnapshotLineageAcrossTranscriptReparse()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var branch = (await fixture.Service.SpawnTemplateAsync(snapshot)).Branch!;
        await fixture.Service.SaveAsync();

        var reloaded = fixture.NewService();
        await reloaded.LoadStoreStateAsync();
        reloaded.Store.Settings.Sources =
        [
            new SessionSource { Tool = "claude", Root = fixture.NativeRoot }
        ];
        await reloaded.SyncFromDiskAsync();

        Assert.AreEqual(snapshot.Id, reloaded.Store.Sessions[branch.Id].FromSnapshotId);
        Assert.AreEqual(fixture.Source.Id, reloaded.Store.Sessions[branch.Id].BranchOfId);
    }

    [TestMethod]
    public async Task LegacyMigration_IsIdempotentAcrossRestartAndSaveConflict()
    {
        using var fixture = new TemplateFixture("claude", seedStore: false);
        fixture.Source.IsTemplate = true;
        fixture.Service.Store.Sessions[fixture.Source.Id] = fixture.Source;
        await fixture.Service.SaveAsync();

        var migrator = fixture.NewService();
        var competingWriter = fixture.NewService();
        await migrator.LoadStoreStateAsync();
        await competingWriter.LoadStoreStateAsync();
        competingWriter.Store.Settings.MultiplexApiPort = 8127;
        await competingWriter.SaveAsync();

        await migrator.MigrateLegacyTemplatesAsync();
        var restarted = fixture.NewService();
        await restarted.LoadAsync();

        Assert.AreEqual(1, restarted.TemplateSnapshotsForSource(fixture.Source.Id).Count);
        Assert.IsFalse(restarted.Store.Sessions[fixture.Source.Id].IsTemplate);
        Assert.AreEqual(8127, restarted.Store.Settings.MultiplexApiPort);
    }

    [TestMethod]
    public async Task LegacyMigration_MissingTranscript_DoesNotClearOrHideTemplate()
    {
        using var fixture = new TemplateFixture("claude", seedStore: false);
        fixture.Source.SourcePath = Path.Combine(fixture.Root, "missing.jsonl");
        fixture.Source.IsTemplate = true;
        fixture.Service.Store.Sessions[fixture.Source.Id] = fixture.Source;
        await fixture.Service.SaveAsync();

        var restarted = fixture.NewService();
        await restarted.LoadAsync();

        Assert.IsTrue(restarted.Store.Sessions[fixture.Source.Id].IsTemplate);
        Assert.AreEqual("!", restarted.Store.Sessions[fixture.Source.Id].ListMarks);
        Assert.AreEqual(0, restarted.TemplateSnapshotsForSource(fixture.Source.Id).Count);
    }

    [TestMethod]
    public async Task JsonlClone_DropsIncompleteTailAtCapturedBoundary()
    {
        using var fixture = new TemplateFixture("claude");
        await File.AppendAllTextAsync(fixture.Source.SourcePath, "{\"type\":\"user\",\"sessionId\":\"incomplete");

        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var text = await File.ReadAllTextAsync(snapshot.SnapshotPath);

        StringAssert.Contains(text, "snapshot-tip");
        Assert.DoesNotContain("incomplete", text);
        Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BranchSessionAsync_Claude_RecoversFromNulCorruptedLine()
    {
        using var fixture = new TemplateFixture("claude");
        // Reproduce crash corruption: an unclean shutdown flushes the file SIZE but loses in-flight
        // write-back pages, leaving a run of NUL (0x00) bytes at the start of a line. Strict parsing
        // then throws "'0x00' is an invalid start of a value. LineNumber: 0 | BytePositionInLine: 0"
        // and the whole branch fails. The clone must strip the NUL and best-effort recover instead.
        await File.AppendAllTextAsync(fixture.Source.SourcePath, "\0\0\0\0\0\0\0\0\n"); // an all-NUL (fully lost) line
        await File.AppendAllTextAsync(
            fixture.Source.SourcePath,
            "\0\0" + ClaudeLine(fixture.Source.Id, "u3", "post-corruption-tip")); // NUL-prefixed, otherwise-valid line

        var result = await fixture.Service.BranchSessionAsync(fixture.Source);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        var bytes = await File.ReadAllBytesAsync(result.Branch!.SourcePath);
        Assert.IsTrue(Array.IndexOf(bytes, (byte)0) < 0, "branch transcript must not carry NUL corruption forward");
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        StringAssert.Contains(text, "post-corruption-tip"); // the recoverable line survived the NUL strip
        StringAssert.Contains(text, result.Branch.Id);
    }

    [TestMethod]
    public void RewriteCodexSessionMeta_NulCorruptedLine_DoesNotThrow()
    {
        // The codex line-0 rewrite must also tolerate a NUL/garbage line without throwing.
        var output = ArchiveService.RewriteCodexSessionMeta("\0\0\0not json\0", "old-id", "new-id");
        Assert.IsNotNull(output);
    }

    [TestMethod]
    public async Task BranchSessionAsync_MissingTranscript_FailsCleanly()
    {
        var service = new ArchiveService(useBundledStore: true);
        var parent = new ArchiveSession { Id = "x", Tool = "claude", SourcePath = "z:/does/not/exist.jsonl" };
        var result = await service.BranchSessionAsync(parent);
        Assert.IsFalse(result.Ok);
        Assert.IsNull(result.Branch);
    }

    [TestMethod]
    public async Task BranchFailure_IsFindableBySessionWithTheExactVisibleError()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-branch-telemetry-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var sourcePath = Path.Combine(root, "source.jsonl");
            await File.WriteAllTextAsync(sourcePath, ClaudeLine("telemetry-source", "u1", "hello"));
            var eventsRoot = Path.Combine(root, "events");
            var service = new ArchiveService(
                storePath: Path.Combine(root, "store.json"),
                templatesRoot: Path.Combine(root, "templates"));
            var parent = new ArchiveSession
            {
                Id = "telemetry-source",
                Tool = "broken-tool",
                Title = "Telemetry source",
                SourcePath = sourcePath,
                Workspace = root
            };
            var eventOptions = new SessionEventLedger.Options(eventsRoot, DateTimeOffset.Parse("2026-08-02T12:00:00Z"));

            var result = await service.BranchSessionAsync(parent, eventOptions);

            Assert.IsFalse(result.Ok);
            Assert.AreEqual("Branch failed: branching isn't supported for tool 'broken-tool'.", result.Message);
            var ev = SessionEventLedger.ReadForSession(parent.Id, max: 10, options: eventOptions).Single();
            Assert.AreEqual("branch.failed", ev.Kind);
            Assert.AreEqual("error", ev.Severity);
            Assert.AreEqual(result.Message, ev.Summary);
            Assert.AreEqual("failed", ev.Details["outcome"]);
            Assert.AreEqual("branch", ev.Details["operation"]);
            TestContext.WriteLine(JsonSerializer.Serialize(ev));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string ClaudeLine(string sessionId, string uuid, string text) =>
        $"{{\"type\":\"user\",\"sessionId\":\"{sessionId}\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}\n";

    private static string CodexMessage(string text) =>
        $"{{\"timestamp\":\"2026-07-16T12:00:00Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"user_message\",\"message\":\"{text}\"}}}}\n";

    private static string[] UserTurns(string tool, string path)
    {
        var turns = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (tool == "claude"
                && root.TryGetProperty("type", out var claudeType)
                && claudeType.GetString() == "user"
                && root.TryGetProperty("message", out var message)
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                turns.Add(content.GetString() ?? "");
            }
            else if (tool == "codex"
                     && root.TryGetProperty("type", out var rootType)
                     && rootType.GetString() == "event_msg"
                     && root.TryGetProperty("payload", out var payload)
                     && payload.TryGetProperty("type", out var payloadType)
                     && payloadType.GetString() == "user_message"
                     && payload.TryGetProperty("message", out var codexMessage))
            {
                turns.Add(codexMessage.GetString() ?? "");
            }
        }
        return turns.ToArray();
    }

    private static SessionIntegrity.Options IntegrityOptions(ISet<string> liveIds) =>
        new(
            LiveIdsProvider: () => (
                true,
                new HashSet<string>(liveIds, StringComparer.OrdinalIgnoreCase),
                "test live ids"),
            FileExists: _ => true);

    private sealed class TemplateFixture : IDisposable
    {
        public TemplateFixture(string tool, bool seedStore = true)
        {
            Tool = tool;
            Root = Path.Combine(Path.GetTempPath(), "clr-template-" + Guid.NewGuid().ToString("N"));
            NativeRoot = Path.Combine(Root, tool + "-native");
            TemplatesRoot = Path.Combine(Root, "templates");
            StorePath = Path.Combine(Root, "store.json");
            DbPath = Path.Combine(Root, "state.sqlite");
            Directory.CreateDirectory(NativeRoot);

            var id = tool + "-source-11111111";
            var sourcePath = tool == "claude"
                ? Path.Combine(NativeRoot, id + ".jsonl")
                : Path.Combine(NativeRoot, "2026", "07", "16", $"rollout-2026-07-16T12-00-00-{id}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(
                sourcePath,
                tool == "claude"
                    ? ClaudeLine(id, "u1", "first") + ClaudeLine(id, "u2", "snapshot-tip")
                    : $"{{\"timestamp\":\"2026-07-16T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"session_id\":\"{id}\",\"cwd\":\"{Escape(Root)}\"}}}}\n"
                      + CodexMessage("snapshot-tip"));

            Source = new ArchiveSession
            {
                Id = id,
                Tool = tool,
                Title = "Source chat",
                Workspace = Root,
                WorkspaceName = "fixture",
                SourcePath = sourcePath
            };
            if (tool == "codex") CreateCodexDatabase(id, sourcePath);
            Service = NewService();
            if (seedStore) Service.Store.Sessions[id] = Source;
        }

        public string Tool { get; }
        public string Root { get; }
        public string NativeRoot { get; }
        public string TemplatesRoot { get; }
        public string StorePath { get; }
        public string DbPath { get; }
        public ArchiveSession Source { get; }
        public ArchiveService Service { get; }

        public Task AppendTurnAsync(ArchiveSession session, string text) =>
            File.AppendAllTextAsync(
                session.SourcePath,
                Tool == "claude"
                    ? ClaudeLine(session.Id, Guid.NewGuid().ToString("N"), text)
                    : CodexMessage(text));

        public ArchiveService NewService() => new(
            storePath: StorePath,
            codexSessionsRoot: NativeRoot,
            claudeSessionsRoot: NativeRoot,
            codexStateDbPath: DbPath,
            templatesRoot: TemplatesRoot);

        public int ThreadCount()
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "select count(*) from threads";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public string? RolloutPath(string id)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "select rollout_path from threads where id = $id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string;
        }

        private void CreateCodexDatabase(string id, string rolloutPath)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                create table threads (
                    id text primary key,
                    rollout_path text not null,
                    title text not null,
                    cwd text not null,
                    created_at integer not null,
                    updated_at integer not null,
                    archived integer not null default 0
                );
                insert into threads (id, rollout_path, title, cwd, created_at, updated_at, archived)
                values ($id, $path, 'Source chat', $cwd, 1, 1, 0);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$path", rolloutPath);
            command.Parameters.AddWithValue("$cwd", Root);
            command.ExecuteNonQuery();
        }

        private SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\");
    }
}
