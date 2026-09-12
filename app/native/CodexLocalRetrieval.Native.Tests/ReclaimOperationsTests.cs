using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ReclaimOperationsTests
{
    private static async Task<(string Dir, string Store, ArchiveService Service, ArchiveSession Target)> CreateAsync()
    {
        var dir = Path.Combine(Path.GetTempPath(), "reclaim-operation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = Path.Combine(dir, "app-store.json");
        var service = new ArchiveService(storePath: store);
        var target = new ArchiveSession { Id = "target", Tool = "claude", Title = "Target", Aliases = { "old-target" } };
        service.Store.Sessions[target.Id] = target;
        service.Store.Sessions["other"] = new ArchiveSession { Id = "other", Tool = "claude", Title = "Other" };
        service.Store.MuxTabHistory["target-tab"] = new MuxTabRecord { Current = new MuxTabChat { Id = target.Id }, History = { new MuxTabChat { Id = "historic" } } };
        service.Store.MuxTabHistory["other-tab"] = new MuxTabRecord { Current = new MuxTabChat { Id = "other" }, History = { new MuxTabChat { Id = "other-old" } } };
        await service.SaveAsync();
        return (dir, store, service, target);
    }

    private static string Listing(string generation = "gen-1") =>
        "{\"list\":[{\"name\":\"target-tab\",\"alive\":true,\"sessionId\":\"target\",\"aliases\":[\"old-target\"],\"generationId\":\"" + generation + "\"},{\"name\":\"other-tab\",\"alive\":true,\"sessionId\":\"other\",\"aliases\":[],\"generationId\":\"other-gen\"}]}";

    [TestMethod]
    public async Task AppliesCleanupOnly_PersistsReceiptAndPrune_PreservesUnrelatedState_AndReplays()
    {
        var f = await CreateAsync();
        try
        {
            var queue = new Queue<string>(new[] { Listing(), "{\"list\":[{\"name\":\"other-tab\",\"alive\":true,\"sessionId\":\"other\",\"aliases\":[],\"generationId\":\"other-gen\"}]}", "{\"list\":[]}" });
            var removes = 0; var kills = 0;
            var revision = f.Service.RemoteManagementRevision(f.Target);
            var first = await f.Service.ExecuteReclaimOperationAsync("intent-1", "old-target", "claude", revision,
                () => Task.FromResult(queue.Dequeue()),
                (name, sid, generation) => { removes++; Assert.AreEqual(("target-tab", "target", "gen-1"), (name, sid, generation)); return Task.FromResult((true, "killed")); },
                ids => { kills++; CollectionAssert.Contains(ids.ToArray(), "old-target"); return new RunningSessions.KillResult(true, "none", Array.Empty<ReclaimKilledPid>()); });
            var replay = await f.Service.ExecuteReclaimOperationAsync("intent-1", "old-target", "claude", revision,
                () => throw new AssertFailedException("replay must not probe"), (_, _, _) => throw new AssertFailedException("replay must not remove"));

            Assert.AreEqual(ReclaimOperationStatus.Applied, first.Status, first.Detail);
            Assert.AreEqual(ReclaimOperationStatus.Applied, replay.Status, replay.Detail);
            Assert.IsTrue(replay.Replay);
            Assert.AreEqual(1, removes); Assert.AreEqual(1, kills);
            Assert.IsFalse(first.Report!.Relaunched, "reclaim coordinator is cleanup-only");
            Assert.AreEqual(first.Report.Headline, replay.Report!.Headline, "durable replay returns the original report");
            Assert.AreEqual(first.Report.MuxDetail, replay.Report.MuxDetail);
            Assert.AreEqual(first.Report.KillDetail, replay.Report.KillDetail);
            Assert.AreEqual(first.Report.Relaunched, replay.Report.Relaunched);
            var receiptPayload = System.Text.Json.JsonSerializer.Deserialize<ReclaimOperationReceiptPayload>(
                f.Service.Store.ManagementOperations["intent-1"].ResultId)!;
            Assert.AreEqual(new ReclaimMuxFence("target-tab", "target", "gen-1"), receiptPayload.MuxFence);
            Assert.IsNull(f.Service.Store.MuxTabHistory["target-tab"].Current);
            Assert.AreEqual("historic", f.Service.Store.MuxTabHistory["target-tab"].History.Single().Id);
            Assert.AreEqual("other", f.Service.Store.MuxTabHistory["other-tab"].Current!.Id);
            Assert.IsTrue(f.Service.Store.Sessions.ContainsKey("target"));
            Assert.IsTrue(f.Service.Store.Sessions.ContainsKey("other"));
            var loaded = new ArchiveService(storePath: f.Store); await loaded.LoadAsync();
            Assert.AreEqual("applied", loaded.Store.ManagementOperations["intent-1"].State);
            Assert.IsNull(loaded.Store.MuxTabHistory["target-tab"].Current);
            Assert.AreEqual("other", loaded.Store.MuxTabHistory["other-tab"].Current!.Id);
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [TestMethod]
    public async Task CancellationBeforeReceiptDoesNotAuthorizeCleanup()
    {
        var f = await CreateAsync();
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var result = await f.Service.ExecuteReclaimOperationAsync("cancelled", "target", "claude",
                f.Service.RemoteManagementRevision(f.Target), () => Task.FromResult(Listing()),
                (_, _, _) => throw new AssertFailedException("Cancelled receipt must not permit removal"),
                _ => throw new AssertFailedException("Cancelled receipt must not permit process cleanup"),
                cancellationToken: cancellation.Token);
            Assert.AreNotEqual(ReclaimOperationStatus.Applied, result.Status);
            Assert.IsFalse(f.Service.Store.ManagementOperations.ContainsKey("cancelled"));
            Assert.AreEqual("target", f.Service.Store.MuxTabHistory["target-tab"].Current!.Id);
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [DataTestMethod]
    [DataRow(false, "{\"list\":[]}")]
    [DataRow(true, "invalid listing")]
    [DataRow(true, "{\"list\":[{\"name\":\"target-tab\",\"alive\":true,\"sessionId\":\"target\",\"aliases\":[],\"generationId\":\"replacement\"}]}")]
    public async Task IncompleteCleanupNeverCommitsAppliedReceipt(bool killOk, string finalListing)
    {
        var f = await CreateAsync();
        try
        {
            var queue = new Queue<string>(new[] { Listing(), "{\"list\":[]}", finalListing });
            var result = await f.Service.ExecuteReclaimOperationAsync("incomplete", "target", "claude",
                f.Service.RemoteManagementRevision(f.Target), () => Task.FromResult(queue.Dequeue()),
                (_, _, _) => Task.FromResult((true, "killed")),
                _ => new RunningSessions.KillResult(killOk, "cleanup result", Array.Empty<ReclaimKilledPid>()),
                claimOptions: new SessionLaunchClaims.Options(RootDirectory: Path.Combine(f.Dir, "claims")));
            Assert.AreEqual(ReclaimOperationStatus.Uncertain, result.Status);
            Assert.AreEqual("started", f.Service.Store.ManagementOperations["incomplete"].State);
            Assert.IsNotNull(f.Service.Store.MuxTabHistory["target-tab"].Current);
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task VerifiedCleanupReceiptResumesPruneWithoutRepeatingDestruction(bool replacement)
    {
        var f = await CreateAsync();
        try
        {
            var revision = f.Service.RemoteManagementRevision(f.Target);
            var listings = new Queue<string>(new[] { Listing(), "{\"list\":[]}", "invalid" });
            var first = await f.Service.ExecuteReclaimOperationAsync("cleanup-verified", "target", "claude", revision,
                () => Task.FromResult(listings.Dequeue()), (_, _, _) => Task.FromResult((true, "killed")),
                _ => new RunningSessions.KillResult(true, "verified", Array.Empty<ReclaimKilledPid>()),
                claimOptions: new SessionLaunchClaims.Options(RootDirectory: Path.Combine(f.Dir, "claims")));
            Assert.AreEqual(ReclaimOperationStatus.Uncertain, first.Status);
            var loaded = new ArchiveService(storePath: f.Store);
            await loaded.LoadAsync();
            var retry = await loaded.ExecuteReclaimOperationAsync("cleanup-verified", "target", "claude", revision,
                () => Task.FromResult(replacement ? Listing("replacement") : "{\"list\":[]}"),
                (_, _, _) => throw new AssertFailedException("recovery must not remove a mux owner"),
                _ => throw new AssertFailedException("recovery must not repeat process cleanup"));
            Assert.AreEqual(replacement ? ReclaimOperationStatus.Uncertain : ReclaimOperationStatus.Applied, retry.Status, retry.Detail);
            Assert.AreEqual(replacement, loaded.Store.MuxTabHistory["target-tab"].Current is not null);
            Assert.AreEqual("other", loaded.Store.MuxTabHistory["other-tab"].Current!.Id);
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [TestMethod]
    public async Task StaleRevisionRefusesBeforeEffects()
    {
        var f = await CreateAsync();
        try
        {
            var calls = 0;
            var result = await f.Service.ExecuteReclaimOperationAsync("stale", "target", "claude", "wrong",
                () => { calls++; return Task.FromResult(Listing()); }, (_, _, _) => { calls++; return Task.FromResult((true, "")); },
                _ => { calls++; return new RunningSessions.KillResult(true, "", Array.Empty<ReclaimKilledPid>()); });
            Assert.AreEqual(ReclaimOperationStatus.Refused, result.Status);
            Assert.AreEqual(0, calls);
            Assert.IsFalse(f.Service.Store.ManagementOperations.ContainsKey("stale"));
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [TestMethod]
    public async Task StartedReceiptNeverKillsReplacementGenerationOrRunsProcessCleanup()
    {
        var f = await CreateAsync();
        try
        {
            var revision = f.Service.RemoteManagementRevision(f.Target);
            var first = await f.Service.ExecuteReclaimOperationAsync("replace", "target", "claude", revision,
                () => Task.FromResult(Listing()),
                (_, _, _) => Task.FromResult((false, "transport uncertain")));
            Assert.AreEqual(ReclaimOperationStatus.Uncertain, first.Status);
            Assert.AreEqual("started", f.Service.Store.ManagementOperations["replace"].State);

            var removes = 0; var kills = 0;
            var retry = await f.Service.ExecuteReclaimOperationAsync("replace", "target", "claude", revision,
                () => Task.FromResult(Listing("gen-2")),
                (_, _, _) => { removes++; return Task.FromResult((true, "killed")); },
                _ => { kills++; return new RunningSessions.KillResult(true, "", Array.Empty<ReclaimKilledPid>()); });
            Assert.AreEqual(ReclaimOperationStatus.Uncertain, retry.Status);
            StringAssert.Contains(retry.Detail, "frozen fence");
            Assert.AreEqual(0, removes);
            Assert.AreEqual(0, kills);
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [TestMethod]
    public async Task PreservesUnrelatedLaunchClaim()
    {
        var f = await CreateAsync();
        var claims = Path.Combine(f.Dir, "claims");
        Directory.CreateDirectory(claims);
        try
        {
            var options = new SessionLaunchClaims.Options(RootDirectory: claims);
            Assert.IsTrue(SessionLaunchClaims.TryAcquire("other", null, "test", out var unrelated, out var detail, _ => false, options), detail);
            unrelated!.RetainUntilExpiry();
            unrelated.Dispose();
            var unrelatedPaths = SessionLaunchClaims.ReadClaimsForSession("other", null, options).Select(c => c.Path).ToArray();
            Assert.IsNotEmpty(unrelatedPaths);
            var queue = new Queue<string>(new[] { Listing(), "{\"list\":[{\"name\":\"other-tab\",\"alive\":true,\"sessionId\":\"other\",\"aliases\":[],\"generationId\":\"other-gen\"}]}", "{\"list\":[]}" });
            var result = await f.Service.ExecuteReclaimOperationAsync("claim-preserve", "target", "claude",
                f.Service.RemoteManagementRevision(f.Target), () => Task.FromResult(queue.Dequeue()),
                (_, _, _) => Task.FromResult((true, "killed")),
                _ => new RunningSessions.KillResult(true, "none", Array.Empty<ReclaimKilledPid>()), claimOptions: options);
            Assert.AreEqual(ReclaimOperationStatus.Applied, result.Status, result.Detail);
            Assert.IsTrue(unrelatedPaths.All(File.Exists), "a different session's claim must remain untouched");
        }
        finally { Directory.Delete(f.Dir, true); }
    }

    [TestMethod]
    public async Task FinalSaveFailureRestoresPruneInMemoryAndReturnsUncertain()
    {
        var f = await CreateAsync();
        try
        {
            var queue = new Queue<string>(new[] { Listing(), "{\"list\":[{\"name\":\"other-tab\",\"alive\":true,\"sessionId\":\"other\",\"aliases\":[],\"generationId\":\"other-gen\"}]}", "{\"list\":[]}" });
            var revision = f.Service.RemoteManagementRevision(f.Target);
            var calls = 0;
            var result = await f.Service.ExecuteReclaimOperationAsync("save-fail", "target", "claude", revision,
                () => Task.FromResult(queue.Dequeue()),
                (_, _, _) => { calls++; if (calls == 1) { Directory.Delete(f.Service.StoreBackupsDir, true); File.WriteAllText(f.Service.StoreBackupsDir, "blocked"); } return Task.FromResult((true, "killed")); });
            Assert.AreEqual(ReclaimOperationStatus.Uncertain, result.Status);
            Assert.IsNotNull(f.Service.Store.MuxTabHistory["target-tab"].Current, "failed final save must restore dirty prune");
            Assert.AreEqual("started", f.Service.Store.ManagementOperations["save-fail"].State);
            var loaded = new ArchiveService(storePath: f.Store); await loaded.LoadAsync();
            Assert.IsNotNull(loaded.Store.MuxTabHistory["target-tab"].Current);
            Assert.AreEqual("started", loaded.Store.ManagementOperations["save-fail"].State);
        }
        finally { Directory.Delete(f.Dir, true); }
    }
}
