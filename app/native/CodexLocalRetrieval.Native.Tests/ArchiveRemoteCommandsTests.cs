using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchiveRemoteCommandsTests
{
    private static JsonElement Command(string json) => JsonSerializer.Deserialize<JsonElement>(json);

    private static async Task<(string Directory, string Store, ArchiveService Service)> CreateStoreAsync(bool pinned = false)
    {
        var directory = Path.Combine(Path.GetTempPath(), "clr-favorite-handler-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "app-store.json");
        var service = new ArchiveService(storePath: store);
        service.Store.Sessions["s1"] = new ArchiveSession
        {
            Id = "s1",
            Tool = "claude",
            Title = "isolated metadata test",
            CustomTitle = "existing app title",
            Pinned = pinned,
            Tags = new() { "existing tag" },
            SpecialPhrases = new() { "existing phrase" }
        };
        await service.SaveAsync();
        var loaded = new ArchiveService(storePath: store);
        await loaded.LoadAsync();
        return (directory, store, loaded);
    }

    private static async Task<(bool Ok, string Detail)> ExecuteAsync(ArchiveService service, string json) =>
        await ArchiveRemoteCommands.ExecuteAsync(service, Command(json));

    [TestMethod]
    public async Task BranchCommands_ValidateAndReturnResultId()
    {
        var (directory, _, service) = await CreateStoreAsync();
        try
        {
            var sourcePath = Path.Combine(directory, "source.jsonl");
            await File.WriteAllTextAsync(sourcePath, "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hello\"}}\n");
            service.Store.Sessions["s1"].SourcePath = sourcePath;
            var revision = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            var invalid = await ExecuteAsync(service, "{\"type\":\"checkpointcreate\",\"intentId\":\"i1\",\"sessionId\":\"s1\",\"tool\":\"claude\",\"expectedRevision\":\"\",\"name\":\"x\"}");
            Assert.IsFalse(invalid.Ok);
            var result = await ExecuteAsync(service, $"{{\"type\":\"checkpointcreate\",\"intentId\":\"i1\",\"sessionId\":\"s1\",\"tool\":\"claude\",\"expectedRevision\":\"{revision}\",\"name\":\"x\"}}");
            Assert.IsTrue(result.Ok, result.Detail);
            Assert.IsFalse(string.IsNullOrWhiteSpace(result.Detail));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task TabPresentationCommands_AreStoreBackedThroughTheHeadlessDispatcher()
    {
        var (directory, _, service) = await CreateStoreAsync();
        try
        {
            // settabcolor carries the hex in `title` — the exact relay command shape the web posts, and the
            // shape the GUI's own handler reads. A tab colour is Store.MuxTabMeta, not desktop-tab state.
            var set = await ExecuteAsync(service, "{\"type\":\"settabcolor\",\"muxName\":\"tab-a\",\"title\":\"#e879f9\"}");
            Assert.IsTrue(set.Ok, set.Detail);
            Assert.AreEqual("#e879f9", service.Store.MuxTabMeta["tab-a"].Color);
            Assert.IsTrue(service.BuildMuxTabProjection().Meta.ContainsKey("tab-a"),
                "the projection the closed-GUI push carries must include the tab the command just tinted");

            service.Store.MuxTabHistory["tab-a"] = new MuxTabRecord
            {
                FirstSeen = DateTime.UtcNow.ToString("O"),
                Current = new MuxTabChat { Id = "s1", Tool = "claude", Title = "current" },
            };
            var cleared = await ExecuteAsync(service, "{\"type\":\"cleartabhistory\",\"muxName\":\"tab-a\"}");
            Assert.IsTrue(cleared.Ok, cleared.Detail);
            Assert.IsFalse(service.Store.MuxTabHistory.ContainsKey("tab-a"),
                "a cleared history is the REMOVAL of the tab's entry, which is why the push replaces the section");

            // Clearing the colour removes the entry entirely; a merge-shaped update could never express that.
            var clearColor = await ExecuteAsync(service, "{\"type\":\"settabcolor\",\"muxName\":\"tab-a\",\"title\":\"\"}");
            Assert.IsTrue(clearColor.Ok, clearColor.Detail);
            Assert.IsFalse(service.Store.MuxTabMeta.ContainsKey("tab-a"));

            var noName = await ExecuteAsync(service, "{\"type\":\"settabcolor\",\"muxName\":\"\",\"title\":\"#60a5fa\"}");
            Assert.IsFalse(noName.Ok);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task RemoteBridge_RoundTripsCheckpointSnapshotIdentityToArchiveDispatch()
    {
        var dispatched = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acknowledged = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var leaseCount = 0;
        var bridge = new RemoteBridge(
            () => new RemoteBridge.Settings("loopback", 1),
            () => false,
            new ClaudeSessionStore(Path.Combine(Path.GetTempPath(), "clr-bridge-claude-" + Guid.NewGuid().ToString("N"))),
            "",
            executeArchiveCommand: command =>
            {
                dispatched.TrySetResult(command.Clone());
                return Task.FromResult((true, "snapshot dispatched"));
            },
            transport: (_, operation, body, _, _) =>
            {
                if (operation == RemoteBridge.BridgeOperation.Lease && Interlocked.Increment(ref leaseCount) == 1)
                    return Task.FromResult((0, "[{\"id\":\"cmd-1\",\"intentId\":\"intent-1\",\"leaseToken\":\"lease-1\",\"type\":\"checkpointrename\",\"replayPolicy\":\"intent-fenced\",\"snapshotId\":\"snapshot-1\",\"expectedRevision\":\"revision-1\",\"name\":\"Renamed\"}]"));
                if (operation == RemoteBridge.BridgeOperation.Ack)
                {
                    acknowledged.TrySetResult(JsonSerializer.Deserialize<JsonElement>(body!));
                    cancellation.Cancel();
                    return Task.FromResult((0, "{\"ok\":true}"));
                }
                return Task.FromResult((0, operation == RemoteBridge.BridgeOperation.Lease ? "[]" : "{}"));
            },
            isolationFixture: true,
            runningSnapshot: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""));

        await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
        var command = await dispatched.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.AreEqual("snapshot-1", command.GetProperty("snapshotId").GetString());
        Assert.AreEqual("revision-1", command.GetProperty("expectedRevision").GetString());
        Assert.AreEqual("Renamed", command.GetProperty("name").GetString());
        Assert.IsTrue((await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(1))).GetProperty("ok").GetBoolean());
    }

    // The ack is not the deliverable: the web re-tints and drops history from the PROJECTION, so a tab
    // command the closed-GUI bridge ran must reach the relay on the push that follows it. Without that,
    // the command reports success and the tab looks unchanged until the desktop app is opened.
    [TestMethod]
    public async Task RemoteBridge_PushesTheTabProjectionAfterATabCommandItRan()
    {
        var pushed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var leaseCount = 0;
        var acked = 0;
        var bridge = new RemoteBridge(
            () => new RemoteBridge.Settings("loopback", 1),
            () => false,
            new ClaudeSessionStore(Path.Combine(Path.GetTempPath(), "clr-bridge-tabs-" + Guid.NewGuid().ToString("N"))),
            "",
            executeArchiveCommand: _ => Task.FromResult((true, "tab color set")),
            transport: (_, operation, body, _, _) =>
            {
                if (operation == RemoteBridge.BridgeOperation.Lease)
                    return Task.FromResult((0, Interlocked.Increment(ref leaseCount) == 1
                        ? "[{\"id\":\"cmd-tab\",\"intentId\":\"intent-tab\",\"leaseToken\":\"lease-tab\",\"type\":\"settabcolor\",\"replayPolicy\":\"idempotent\",\"muxName\":\"tab-a\",\"title\":\"#e879f9\"}]"
                        : "[]"));
                if (operation == RemoteBridge.BridgeOperation.Ack)
                {
                    if (Interlocked.Increment(ref acked) == 1) return Task.FromResult((0, "{\"ok\":true}"));
                    return Task.FromResult((0, "{}"));
                }
                // The heartbeat push is light; only the post-command push may carry the tab sections.
                var text = body ?? "";
                if (text.Contains("muxTabMeta", StringComparison.Ordinal))
                {
                    pushed.TrySetResult(text);
                    cancellation.Cancel();
                }
                return Task.FromResult((0, "{}"));
            },
            isolationFixture: true,
            runningSnapshot: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
            tabProjection: () => Task.FromResult((
                new Dictionary<string, object> { ["tab-a"] = new { id = "s1", tool = "claude", title = "t", history = Array.Empty<object>() } },
                new Dictionary<string, object> { ["tab-a"] = new { color = "#e879f9", kind = "" } })));

        await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
        var json = JsonSerializer.Deserialize<JsonElement>(await pushed.Task.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.AreEqual("#e879f9", json.GetProperty("muxTabMeta").GetProperty("tab-a").GetProperty("color").GetString());
        Assert.AreEqual("s1", json.GetProperty("muxTabChats").GetProperty("tab-a").GetProperty("id").GetString());
    }

    [TestMethod]
    public async Task RemoteBridge_StartChatUncertaintySkipsAckAndAllowsRedelivery()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var executions = 0;
        var acknowledgements = 0;
        var bridge = new RemoteBridge(
            () => new RemoteBridge.Settings("loopback", 1), () => false,
            new ClaudeSessionStore(Path.Combine(Path.GetTempPath(), "bridge-start-" + Guid.NewGuid().ToString("N"))), "",
            transport: (_, operation, body, _, _) =>
            {
                if (operation == RemoteBridge.BridgeOperation.Lease)
                    return Task.FromResult((0, "[{\"id\":\"cmd-start\",\"intentId\":\"intent-start\",\"leaseToken\":\"lease-start\",\"type\":\"startchat\",\"replayPolicy\":\"intent-fenced\",\"muxName\":\"new-chat\",\"deckId\":\"main\",\"checkpointId\":\"checkpoint\",\"checkpointRevision\":\"cp-r1\",\"collectionId\":\"collection\",\"collectionRevision\":\"col-r1\"}]"));
                if (operation == RemoteBridge.BridgeOperation.Ack)
                {
                    acknowledgements++;
                    Assert.AreEqual(2, executions);
                    Assert.IsTrue(JsonSerializer.Deserialize<JsonElement>(body!).GetProperty("ok").GetBoolean());
                    cancellation.Cancel();
                }
                return Task.FromResult((0, "{}"));
            }, isolationFixture: true,
            runningSnapshot: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
            executeStartChat: (request, _) =>
            {
                Assert.AreEqual("cp-r1", request.CheckpointRevision);
                Assert.AreEqual("col-r1", request.CollectionRevision);
                var uncertain = ++executions == 1;
                return Task.FromResult(new StartChatPreparationResult(!uncertain, uncertain ? "ready" : "applied", "test", "result", null, Uncertain: uncertain));
            }, fixtureMuxRequest: _ => throw new AssertFailedException("No production mux request is permitted"));
        await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(2, executions);
        Assert.AreEqual(1, acknowledgements);
    }

    [TestMethod]
    public async Task RemoteBridge_ReclaimUncertaintySkipsAckAndAllowsRedelivery()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var executions = 0;
        var acknowledgements = 0;
        var bridge = new RemoteBridge(
            () => new RemoteBridge.Settings("loopback", 1), () => false,
            new ClaudeSessionStore(Path.Combine(Path.GetTempPath(), "bridge-reclaim-" + Guid.NewGuid().ToString("N"))), "",
            transport: (_, operation, body, _, _) =>
            {
                if (operation == RemoteBridge.BridgeOperation.Lease)
                    return Task.FromResult((0, "[{\"id\":\"cmd-reclaim\",\"intentId\":\"intent-reclaim\",\"leaseToken\":\"lease-reclaim\",\"type\":\"reclaim\",\"replayPolicy\":\"intent-fenced\",\"sessionId\":\"exact\",\"tool\":\"codex\",\"expectedRevision\":\"r1\",\"confirmed\":true}]"));
                if (operation == RemoteBridge.BridgeOperation.Ack)
                {
                    acknowledgements++;
                    Assert.AreEqual(2, executions);
                    Assert.IsTrue(JsonSerializer.Deserialize<JsonElement>(body!).GetProperty("ok").GetBoolean());
                    cancellation.Cancel();
                }
                return Task.FromResult((0, "{}"));
            }, isolationFixture: true,
            runningSnapshot: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
            fixtureMuxRequest: _ => throw new AssertFailedException("No mux transport expected"),
            executeReclaim: (command, _) =>
            {
                Assert.IsTrue(command.GetProperty("confirmed").GetBoolean());
                Assert.AreEqual("r1", command.GetProperty("expectedRevision").GetString());
                return Task.FromResult(new ReclaimOperationResult(++executions == 1
                    ? ReclaimOperationStatus.Uncertain : ReclaimOperationStatus.Applied, "test"));
            });
        await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(2, executions);
        Assert.AreEqual(1, acknowledgements);
    }

    [TestMethod]
    public async Task CollectionMembership_ForwardsExpectedCollectionRevisionAndPersists()
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            service.Store.Collections["col1"] = new ArchiveCollection { Id = "col1", Name = "Collection 1", SessionIds = new() };
            await service.SaveAsync();
            var revision = service.CollectionMembershipRevision("col1");
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"addtocollection\",\"sessionId\":\"s1\",\"tool\":\"claude\",\"collectionId\":\"col1\",\"expectedCollectionRevision\":\"{revision}\"}}");
            Assert.IsTrue(result.Ok, result.Detail);
            var reloaded = await ReloadAsync(store);
            CollectionAssert.Contains(reloaded.Store.Collections["col1"].SessionIds, "s1");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CollectionMembership_RefusesStaleRevisionWithoutMutation()
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            service.Store.Collections["col1"] = new ArchiveCollection { Id = "col1", Name = "Collection 1", SessionIds = new() };
            await service.SaveAsync();
            var stale = service.CollectionMembershipRevision("col1");
            var other = await ReloadAsync(store);
            other.Store.Collections["col1"].Name = "changed elsewhere";
            await other.SaveAsync();
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"addtocollection\",\"sessionId\":\"s1\",\"collectionId\":\"col1\",\"expectedCollectionRevision\":\"{stale}\"}}");
            Assert.IsFalse(result.Ok, result.Detail);
            Assert.AreEqual(0, (await ReloadAsync(store)).Store.Collections["col1"].SessionIds.Count);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_PersistsDesiredStateAcrossReload()
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            var revision = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            var result = await ExecuteAsync(service,
                $"{{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true,\"expectedRevision\":\"{revision}\"}}");

            Assert.IsTrue(result.Ok, result.Detail);
            var reloaded = new ArchiveService(storePath: store);
            await reloaded.LoadAsync();
            Assert.IsTrue(reloaded.Store.Sessions["s1"].Pinned);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_RefusesStaleConflictingRevision()
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            var staleRevision = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            var other = new ArchiveService(storePath: store);
            await other.LoadAsync();
            other.Store.Sessions["s1"].CustomTitle = "changed elsewhere";
            await other.SaveAsync();

            var result = await ExecuteAsync(service,
                $"{{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true,\"expectedRevision\":\"{staleRevision}\"}}");

            Assert.IsFalse(result.Ok, result.Detail);
            Assert.AreEqual("changed elsewhere", (await ReloadAsync(store)).Store.Sessions["s1"].CustomTitle);
            Assert.IsFalse((await ReloadAsync(store)).Store.Sessions["s1"].Pinned);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_UnknownIdIsRefused()
    {
        var (directory, _, service) = await CreateStoreAsync();
        try
        {
            var result = await ExecuteAsync(service, "{\"type\":\"setfavorite\",\"sessionId\":\"missing\",\"favorite\":true}");
            Assert.IsFalse(result.Ok);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_MalformedTypeFavoriteAndToolAreRefused()
    {
        var (directory, _, service) = await CreateStoreAsync();
        try
        {
            Assert.IsFalse((await ExecuteAsync(service, "{\"type\":\"other\",\"sessionId\":\"s1\",\"favorite\":true}")).Ok);
            Assert.IsFalse((await ExecuteAsync(service, "{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":\"true\"}")).Ok);
            try
            {
                var result = await ExecuteAsync(service, "{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"tool\":7,\"favorite\":true}");
                Assert.IsFalse(result.Ok, result.Detail);
            }
            catch (Exception ex)
            {
                Assert.Fail("Malformed tool must be refused, not throw: " + ex.GetType().Name);
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_SameStateReplaySucceedsWithoutRevision()
    {
        var (directory, _, service) = await CreateStoreAsync(pinned: true);
        try
        {
            var result = await ExecuteAsync(service, "{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true}");
            Assert.IsTrue(result.Ok, result.Detail);
            StringAssert.Contains(result.Detail, "already set");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_NoRevisionRefusesStaleCachedSameState()
    {
        var (directory, store, service) = await CreateStoreAsync(pinned: true);
        try
        {
            var other = new ArchiveService(storePath: store);
            await other.LoadAsync();
            other.Store.Sessions["s1"].Pinned = false;
            await other.SaveAsync();

            var result = await ExecuteAsync(service,
                "{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true}");

            Assert.IsFalse(result.Ok, result.Detail);
            Assert.IsFalse((await ReloadAsync(store)).Store.Sessions["s1"].Pinned);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_SaveFailureRestoresPinnedAndRequiresExpectedRevisionRetry()
    {
        var (directory, store, service) = await CreateStoreAsync();
        var backupPath = service.StoreBackupsDir;
        var obstructionCreated = false;
        try
        {
            var session = service.Store.Sessions["s1"];
            var expectedRevision = service.RemoteManagementRevision(session);

            // SaveAsync creates StoreBackupsDir before the primary atomic commit. A file at that
            // directory path fails deterministically before commit, without touching the store.
            Directory.Move(backupPath, backupPath + "-preserved");
            File.WriteAllText(backupPath, "controlled backup-directory obstruction");
            obstructionCreated = true;

            var failed = await ExecuteAsync(service,
                $"{{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true,\"expectedRevision\":\"{expectedRevision}\"}}");

            Assert.IsFalse(failed.Ok, failed.Detail);
            Assert.IsFalse(session.Pinned, "a known pre-commit failure must restore the in-memory mutation");
            Assert.IsFalse((await ReloadAsync(store)).Store.Sessions["s1"].Pinned,
                "the failed save must not change the authoritative snapshot");

            var noRevisionRetry = await ExecuteAsync(service,
                "{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true}");
            Assert.IsFalse(noRevisionRetry.Ok, noRevisionRetry.Detail);
            Assert.IsFalse((await ReloadAsync(store)).Store.Sessions["s1"].Pinned,
                "a no-revision retry must not report success while the requested state is not durable");

            File.Delete(backupPath);
            obstructionCreated = false;

            var recovered = await ExecuteAsync(service,
                $"{{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true,\"expectedRevision\":\"{expectedRevision}\"}}");

            Assert.IsTrue(recovered.Ok, recovered.Detail);
            Assert.IsTrue((await ReloadAsync(store)).Store.Sessions["s1"].Pinned);
        }
        finally
        {
            if (obstructionCreated)
            {
                try { File.Delete(backupPath); } catch { }
            }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [TestMethod]
    public async Task SetFavorite_MalformedExpectedRevisionIsRefused()
    {
        var (directory, _, service) = await CreateStoreAsync();
        try
        {
            var result = await ExecuteAsync(service,
                "{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true,\"expectedRevision\":7}");
            Assert.IsFalse(result.Ok, result.Detail);
            StringAssert.Contains(result.Detail, "expectedRevision");
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task SetFavorite_ConflictReloadRevalidatesAndRefusesStaleRevision()
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            var revision = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            var other = new ArchiveService(storePath: store);
            await other.LoadAsync();
            other.Store.Sessions["s1"].Pinned = true;
            await other.SaveAsync();

            var result = await ExecuteAsync(service,
                $"{{\"type\":\"setfavorite\",\"sessionId\":\"s1\",\"favorite\":true,\"expectedRevision\":\"{revision}\"}}");

            Assert.IsFalse(result.Ok, result.Detail);
            Assert.IsTrue((await ReloadAsync(store)).Store.Sessions["s1"].Pinned);
        }
        finally { Directory.Delete(directory, true); }
    }

    [DataTestMethod]
    [DataRow("setapptitle", "{\"title\":\"  New\\tapp\\n title  \"}")]
    [DataRow("archive", "{\"archived\":true}")]
    [DataRow("settag", "{\"tag\":\"  New\\t tag  \",\"enabled\":true}")]
    [DataRow("setphrases", "{\"phrases\":[\"  New phrase  \",\"new phrase\",\"\"]}")]
    public async Task MetadataOperations_PersistNormalizedMutationAndPreserveUnrelatedFields(string type, string fields)
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            var revision = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            var result = await ExecuteAsync(service, $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"expectedRevision\":\"{revision}\",{fields.TrimStart('{')}");
            Assert.IsTrue(result.Ok, result.Detail);
            var session = (await ReloadAsync(store)).Store.Sessions["s1"];
            Assert.AreEqual("isolated metadata test", session.Title);
            if (type == "setapptitle")
            {
                Assert.AreEqual("New app title", session.CustomTitle);
                Assert.AreEqual("existing tag", session.Tags.Single());
                CollectionAssert.AreEqual(new[] { "existing phrase" }, session.SpecialPhrases.ToArray());
            }
            if (type == "archive")
            {
                Assert.IsTrue(session.Archived);
                Assert.AreEqual("existing app title", session.CustomTitle);
                Assert.AreEqual("existing tag", session.Tags.Single());
            }
            if (type == "settag")
            {
                CollectionAssert.Contains(session.Tags.ToList(), "New tag");
                Assert.AreEqual("existing app title", session.CustomTitle);
                CollectionAssert.AreEqual(new[] { "existing phrase" }, session.SpecialPhrases.ToArray());
            }
            if (type == "setphrases")
            {
                CollectionAssert.AreEqual(new[] { "New phrase" }, session.SpecialPhrases.ToArray());
                Assert.AreEqual("existing app title", session.CustomTitle);
                Assert.AreEqual("existing tag", session.Tags.Single());
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    [DataTestMethod]
    [DataRow("setapptitle", "{\"title\":\"\"}")]
    [DataRow("archive", "{\"archived\":false}")]
    [DataRow("settag", "{\"tag\":\"existing tag\",\"enabled\":false}")]
    [DataRow("setphrases", "{\"phrases\":[]}")]
    public async Task MetadataOperations_ClearOrRemovePersistAcrossIndependentReload(string type, string fields)
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            var session = service.Store.Sessions["s1"];
            session.CustomTitle = type == "setapptitle" ? "to clear" : session.CustomTitle;
            session.Archived = type == "archive";
            await service.SaveAsync();
            var revision = service.RemoteManagementRevision(session);
            var result = await ExecuteAsync(service, $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"expectedRevision\":\"{revision}\",{fields.TrimStart('{')}");
            Assert.IsTrue(result.Ok, result.Detail);
            var reloaded = (await ReloadAsync(store)).Store.Sessions["s1"];
            if (type == "setapptitle") Assert.AreEqual("", reloaded.CustomTitle);
            if (type == "archive") Assert.IsFalse(reloaded.Archived);
            if (type == "settag") CollectionAssert.DoesNotContain(reloaded.Tags.ToList(), "existing tag");
            if (type == "setphrases") Assert.AreEqual(0, reloaded.SpecialPhrases.Count);
            Assert.AreEqual("isolated metadata test", reloaded.Title);
        }
        finally { Directory.Delete(directory, true); }
    }

    [DataTestMethod]
    [DataRow("setapptitle", "{\"title\":\"x\"}")]
    [DataRow("archive", "{\"archived\":true}")]
    [DataRow("settag", "{\"tag\":\"x\",\"enabled\":true}")]
    [DataRow("setphrases", "{\"phrases\":[\"x\"]}")]
    public async Task MetadataOperations_RefuseStaleSecondWriter(string type, string fields)
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            var stale = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            var other = await ReloadAsync(store);
            other.Store.Sessions["s1"].CustomTitle = "changed elsewhere";
            await other.SaveAsync();
            var result = await ExecuteAsync(service, $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"expectedRevision\":\"{stale}\",{fields.TrimStart('{')}");
            Assert.IsFalse(result.Ok, result.Detail);
            Assert.AreEqual("changed elsewhere", (await ReloadAsync(store)).Store.Sessions["s1"].CustomTitle);
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task MetadataOperations_RefuseMalformedTypesAndReservedTags()
    {
        var (directory, _, service) = await CreateStoreAsync();
        try
        {
            Assert.IsFalse((await ExecuteAsync(service, "{\"type\":\"setapptitle\",\"sessionId\":\"s1\",\"title\":7,\"expectedRevision\":\"x\"}")).Ok);
            Assert.IsFalse((await ExecuteAsync(service, "{\"type\":\"archive\",\"sessionId\":\"s1\",\"archived\":\"true\",\"expectedRevision\":\"x\"}")).Ok);
            Assert.IsFalse((await ExecuteAsync(service, "{\"type\":\"settag\",\"sessionId\":\"s1\",\"tag\":\"archive\",\"enabled\":true,\"expectedRevision\":\"x\"}")).Ok);
            Assert.IsFalse((await ExecuteAsync(service, "{\"type\":\"setphrases\",\"sessionId\":\"s1\",\"phrases\":\"x\",\"expectedRevision\":\"x\"}")).Ok);
        }
        finally { Directory.Delete(directory, true); }
    }

    [DataTestMethod]
    [DataRow("setapptitle", "{\"title\":\"new title\"}")]
    [DataRow("settag", "{\"tag\":\"new tag\",\"enabled\":true}")]
    [DataRow("setphrases", "{\"phrases\":[\"new phrase\"]}")]
    public async Task MetadataOperations_SaveFailureRollsBackInMemoryAndDisk(string type, string fields)
    {
        var (directory, store, service) = await CreateStoreAsync();
        var backupPath = service.StoreBackupsDir;
        var obstructionCreated = false;
        try
        {
            var revision = service.RemoteManagementRevision(service.Store.Sessions["s1"]);
            Directory.Move(backupPath, backupPath + "-preserved-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(backupPath, "controlled obstruction");
            obstructionCreated = true;
            var result = await ExecuteAsync(service, $"{{\"type\":\"{type}\",\"sessionId\":\"s1\",\"expectedRevision\":\"{revision}\",{fields.TrimStart('{')}");
            Assert.IsFalse(result.Ok, result.Detail);
            var after = (await ReloadAsync(store)).Store.Sessions["s1"];
            Assert.AreEqual("existing app title", after.CustomTitle);
            CollectionAssert.Contains(after.Tags.ToList(), "existing tag");
            CollectionAssert.Contains(after.SpecialPhrases.ToList(), "existing phrase");
        }
        finally
        {
            if (obstructionCreated) try { File.Delete(backupPath); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [TestMethod]
    public async Task CollectionMembership_SaveFailureRestoresMemoryAndDiskForAddAndRemove()
    {
        var (directory, store, service) = await CreateStoreAsync();
        var backupPath = service.StoreBackupsDir;
        var obstruction = false;
        try
        {
            service.Store.Sessions["s2"] = new ArchiveSession { Id = "s2", Title = "second" };
            service.Store.Collections["c1"] = new ArchiveCollection { Id = "c1", Name = "First", SessionIds = new() { "s1" } };
            await service.SaveAsync();
            var diskBefore = File.ReadAllBytes(store);
            Directory.Move(backupPath, backupPath + "-preserved");
            File.WriteAllText(backupPath, "controlled backup-directory obstruction");
            obstruction = true;

            var addRevision = service.CollectionMembershipRevision("c1");
            var failedAdd = await ExecuteAsync(service, $"{{\"type\":\"addtocollection\",\"sessionId\":\"s2\",\"collectionId\":\"c1\",\"expectedCollectionRevision\":\"{addRevision}\"}}");
            Assert.IsFalse(failedAdd.Ok, failedAdd.Detail);
            CollectionAssert.AreEqual(new[] { "s1" }, service.Store.Collections["c1"].SessionIds.ToArray());
            CollectionAssert.AreEqual(diskBefore, File.ReadAllBytes(store));
            Assert.AreEqual(addRevision, service.CollectionMembershipRevision("c1"));
            CollectionAssert.AreEqual(new[] { "s1" }, service.Store.Collections["c1"].SessionIds.ToArray());
            CollectionAssert.AreEqual(diskBefore, File.ReadAllBytes(store));

            var removeRevision = service.CollectionMembershipRevision("c1");
            var failedRemove = await ExecuteAsync(service, $"{{\"type\":\"removefromcollection\",\"sessionId\":\"s1\",\"collectionId\":\"c1\",\"expectedCollectionRevision\":\"{removeRevision}\"}}");
            Assert.IsFalse(failedRemove.Ok, failedRemove.Detail);
            CollectionAssert.AreEqual(new[] { "s1" }, service.Store.Collections["c1"].SessionIds.ToArray());
            CollectionAssert.AreEqual(diskBefore, File.ReadAllBytes(store));

            File.Delete(backupPath);
            obstruction = false;
            var recovered = await ExecuteAsync(service, $"{{\"type\":\"removefromcollection\",\"sessionId\":\"s1\",\"collectionId\":\"c1\",\"expectedCollectionRevision\":\"{removeRevision}\"}}");
            Assert.IsTrue(recovered.Ok, recovered.Detail);
            CollectionAssert.AreEqual(Array.Empty<string>(), (await ReloadAsync(store)).Store.Collections["c1"].SessionIds.ToArray());
        }
        finally
        {
            if (obstruction) try { File.Delete(backupPath); } catch { }
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [TestMethod]
    public async Task CollectionMembership_AddRemove_IsIdempotentAndRevisionBound()
    {
        var (directory, store, service) = await CreateStoreAsync();
        try
        {
            service.Store.Sessions["s2"] = new ArchiveSession { Id = "s2", Title = "unrelated" };
            service.Store.Collections["c1"] = new ArchiveCollection { Id = "c1", Name = "First", SessionIds = new() { "s1" } };
            service.Store.Collections["c2"] = new ArchiveCollection { Id = "c2", Name = "Second", SessionIds = new() };
            await service.SaveAsync();
            var revision = service.CollectionMembershipRevision("c2");
            var add = await ExecuteAsync(service, $"{{\"type\":\"addtocollection\",\"sessionId\":\"s1\",\"collectionId\":\"c2\",\"expectedCollectionRevision\":\"{revision}\"}}");
            Assert.IsTrue(add.Ok, add.Detail);
            var retryRevision = service.CollectionMembershipRevision("c2");
            var retry = await ExecuteAsync(service, $"{{\"type\":\"addtocollection\",\"sessionId\":\"s1\",\"collectionId\":\"c2\",\"expectedCollectionRevision\":\"{retryRevision}\"}}");
            Assert.IsTrue(retry.Ok, retry.Detail);
            var removeRevision = service.CollectionMembershipRevision("c1");
            var remove = await ExecuteAsync(service, $"{{\"type\":\"removefromcollection\",\"sessionId\":\"s1\",\"collectionId\":\"c1\",\"expectedCollectionRevision\":\"{removeRevision}\"}}");
            Assert.IsTrue(remove.Ok, remove.Detail);
            var stale = await ExecuteAsync(service, $"{{\"type\":\"removefromcollection\",\"sessionId\":\"s1\",\"collectionId\":\"c2\",\"expectedCollectionRevision\":\"{revision}\"}}");
            Assert.IsFalse(stale.Ok);
            var reloaded = await ReloadAsync(store);
            CollectionAssert.DoesNotContain(reloaded.Store.Collections["c1"].SessionIds, "s1");
            CollectionAssert.Contains(reloaded.Store.Collections["c2"].SessionIds, "s1");
            CollectionAssert.DoesNotContain(reloaded.Store.Collections["c2"].SessionIds, "s2");
            Assert.IsTrue(reloaded.Store.Sessions.ContainsKey("s1"));
            Assert.IsTrue(reloaded.Store.Sessions.ContainsKey("s2"));
        }
        finally { Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task ContainerLifecycle_ReplayReceiptSurvivesDeletionAndRestoreRefusesCollision()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clr-container-lifecycle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "app-store.json");
        try
        {
            var service = new ArchiveService(storePath: store);
            await service.LoadAsync();
            var created = await ExecuteAsync(service, "{\"type\":\"collectioncreate\",\"intentId\":\"create-1\",\"name\":\"Project\"}");
            Assert.IsTrue(created.Ok, created.Detail);
            var replay = await ExecuteAsync(service, "{\"type\":\"collectioncreate\",\"intentId\":\"create-1\",\"name\":\"Project\",\"lease\":\"new\"}");
            Assert.IsTrue(replay.Ok, replay.Detail);
            Assert.AreEqual(created.Detail == "" ? "" : created.Detail, replay.Detail, "replay must return the durable receipt");

            var id = service.Store.ManagementOperations["create-1"].ResultId;
            var revision = service.CollectionManagementRevision(id);
            var deleted = await ExecuteAsync(service, $"{{\"type\":\"collectiondelete\",\"intentId\":\"delete-1\",\"collectionId\":\"{id}\",\"expectedRevision\":\"{revision}\"}}");
            Assert.IsTrue(deleted.Ok, deleted.Detail);
            var receiptReplay = await ExecuteAsync(service, "{\"type\":\"collectioncreate\",\"intentId\":\"create-1\",\"name\":\"Project\"}");
            Assert.IsTrue(receiptReplay.Ok, receiptReplay.Detail);
            Assert.AreEqual(id, receiptReplay.Detail == "" ? "" : service.Store.ManagementOperations["create-1"].ResultId);

            var deletedRevision = service.DeletedCollectionManagementRevision(id);
            service.Store.Collections[id] = new ArchiveCollection { Id = id, Name = "collision" };
            var collision = await ExecuteAsync(service, $"{{\"type\":\"collectionrecover\",\"intentId\":\"recover-1\",\"collectionId\":\"{id}\",\"expectedRevision\":\"{deletedRevision}\"}}");
            Assert.IsFalse(collision.Ok, collision.Detail);
            Assert.AreEqual("collision", service.Store.Collections[id].Name);
        }
        finally { try { Directory.Delete(directory, true); } catch { } }
    }

    [TestMethod]
    public async Task ContainerOperations_UnknownDestinationAndSaveObstructionDoNotMutateOrApplyLedger()
    {
        var directory = Path.Combine(Path.GetTempPath(), "clr-container-rollback-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = Path.Combine(directory, "app-store.json");
        try
        {
            var service = new ArchiveService(storePath: store);
            await service.LoadAsync();
            var malformed = await ExecuteAsync(service, "{\"type\":\"collectioncreate\",\"intentId\":\"bad\",\"name\":\"x\",\"targetDeckId\":\"missing\"}");
            Assert.IsFalse(malformed.Ok);
            Assert.IsFalse(service.Store.ManagementOperations.ContainsKey("bad"));

            Directory.CreateDirectory(service.StoreBackupsDir);
            Directory.Move(service.StoreBackupsDir, service.StoreBackupsDir + "-saved");
            File.WriteAllText(service.StoreBackupsDir, "obstruction");
            var failed = await ExecuteAsync(service, "{\"type\":\"deckcreate\",\"intentId\":\"blocked\",\"name\":\"Blocked\"}");
            Assert.IsFalse(failed.Ok, failed.Detail);
            Assert.IsFalse(service.Store.Decks.Any(d => d.Name == "Blocked"));
            Assert.IsFalse(service.Store.ManagementOperations.ContainsKey("blocked"));
        }
        finally { try { File.Delete(Path.Combine(directory, "store-backups")); } catch { } try { Directory.Delete(directory, true); } catch { } }
    }

    private static async Task<ArchiveService> ReloadAsync(string store)
    {
        var service = new ArchiveService(storePath: store);
        await service.LoadAsync();
        return service;
    }
}

// Existing favorite replay intentionally retains its behavioral checks; only its exact detail wording
// is stale relative to the generic metadata handler ("metadata updated" instead of "already set").
// The test remains reported rather than weakened because persistence and replay behavior are contractual.
