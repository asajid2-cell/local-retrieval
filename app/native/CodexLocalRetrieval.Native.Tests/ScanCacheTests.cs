using System.Diagnostics;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// The burst scan cache inside RunningSessions.
//
// The cost these pin down: one remote refresh fires integrity + custody + claim checks back to back, and each
// used to pay for its own WMI world sweep (~100-300ms) plus its own walk of Claude's live-session registry.
// They ask the same question microseconds apart, so a short TTL collapses the burst into ONE sweep.
//
// WMI and Claude's registry are machine-global and cannot be arranged in-test without touching real user
// state, so both sweep sources are injected through RunningSessions.ScanSourceOverride (the same seam shape as
// KillSignals). Only the SCAN fake ticks PerfCounters.WmiSweep, mirroring the real code: TryScan is the WMI
// pass, TryClaudeLiveSessionIds is a file walk. An injected scan returning no sessions means no live pids,
// which makes the per-pid transcript probe a no-op — so these tests touch nothing on the machine.
[TestClass]
public class ScanCacheTests
{
    private long _scans;
    private long _registryReads;
    private volatile bool _scanFails;
    private volatile bool _registryFails;
    private List<ArchiveService.RunningSessionInfo> _scanRows = new();
    private Dictionary<string, int> _registryRows = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, int> _handleRows = new(StringComparer.OrdinalIgnoreCase);
    private HashSet<int> _handleUnverifiable = new();

    [TestInitialize]
    public void Init()
    {
        _scans = 0;
        _registryReads = 0;
        _scanFails = false;
        _registryFails = false;
        _scanRows = new();
        _registryRows = new(StringComparer.OrdinalIgnoreCase);
        _handleRows = new(StringComparer.OrdinalIgnoreCase);
        _handleUnverifiable = new();
        RunningSessions.ScanSourceOverride = new RunningSessions.ScanSources(
            Scan: FakeScan,
            ClaudeRegistry: FakeRegistry,
            OpenTranscripts: FakeOpenTranscripts);
        RunningSessions.InvalidateScanCache();
    }

    [TestCleanup]
    public void Cleanup()
    {
        RunningSessions.ScanSourceOverride = null;
        RunningSessions.InvalidateScanCache();
    }

    [TestMethod]
    public void NativeRename_RefusesUnknownAndRegistryOrHandleOwnersWithoutChangingTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-ownership-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(project, id + ".jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"Original transcript\"}}\n";
        File.WriteAllText(path, original);
        var store = new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(root);
        try
        {
            _scanFails = true;
            Assert.IsFalse(store.RenameSession(id, "refused scan"));
            Assert.AreEqual(original, File.ReadAllText(path));
            _scanFails = false;
            _registryFails = true;
            Assert.IsFalse(store.RenameSession(id, "refused registry"));
            Assert.AreEqual(original, File.ReadAllText(path));
            _registryFails = false;
            _registryRows[id] = 12345;
            Assert.IsFalse(store.RenameSession(id, "registry owner"));
            Assert.AreEqual(original, File.ReadAllText(path));
            _registryRows.Clear();
            _scanRows.Add(new ArchiveService.RunningSessionInfo(12345, "claude", "", "Terminal", "", ""));
            _handleRows[id] = 12345;
            Assert.IsFalse(store.RenameSession(id, "handle owner"));
            Assert.AreEqual(original, File.ReadAllText(path));
            _handleRows.Clear();
            Assert.IsTrue(store.RenameSession(id, "Verified idle title"));
            StringAssert.StartsWith(File.ReadAllText(path), original);
            StringAssert.Contains(File.ReadAllText(path), "Verified idle title");
            var once = File.ReadAllText(path);
            Assert.IsTrue(new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(root).RenameSession(id, "Verified idle title"));
            Assert.AreEqual(once, File.ReadAllText(path), "same-title replay after a new store instance must not append again");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_StaleBridgeCommandCannotOverwriteNewerTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-stale-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(project, id + ".jsonl");
        var original = "{\"type\":\"custom-title\",\"sessionId\":\"" + id + "\",\"customTitle\":\"Newer title\"}\n";
        File.WriteAllText(path, original);
        try
        {
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"), sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Newer title" };
            await archive.SaveAsync();
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var acknowledged = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var command = System.Text.Json.JsonSerializer.Serialize(new[] { new {
                id = "stale-command", type = "rename", tool = "claude", sessionId = id, title = "Older title",
                intentId = "stale-intent", leaseToken = "stale-lease", replayPolicy = "idempotent", expectedRevision = "stale-revision"
            }});
            var bridge = new RemoteBridge(() => new RemoteBridge.Settings("loopback", 1), () => false,
                new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(root), "",
                executeArchiveCommand: value => CodexLocalRetrieval.Server.ArchiveRemoteCommands.ExecuteAsync(archive, value),
                isolationFixture: true, runningSnapshot: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
                transport: (_, operation, body, _, _) =>
                {
                    if (operation == RemoteBridge.BridgeOperation.Lease) return Task.FromResult((0, command));
                    if (operation == RemoteBridge.BridgeOperation.Ack)
                    {
                        acknowledged.TrySetResult(System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body!).GetProperty("ok").GetBoolean());
                        cancellation.Cancel();
                    }
                    return Task.FromResult((0, "{}"));
                });
            await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.IsFalse(await acknowledged.Task.WaitAsync(TimeSpan.FromSeconds(1)), "stale rename must be refused");
            Assert.AreEqual(original, File.ReadAllText(path), "old delivery must preserve the newer native title");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_UnconfirmedReceiptIsRedeliveredWithoutTerminalAck()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-redelivery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        File.WriteAllText(path, "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n");
        try
        {
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"),
                sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var id = Guid.NewGuid().ToString();
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Original" };
            await archive.SaveAsync();
            var revision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            archive.StoreWriteFault = _ =>
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(source);
                if (reader.ReadToEnd().Contains("muxRenameIntent", StringComparison.Ordinal))
                    throw new IOException("injected applied receipt failure");
            };
            var command = System.Text.Json.JsonSerializer.Serialize(new[] { new {
                id = "rename-command", type = "rename", tool = "claude", sessionId = id, title = "Confirmed title",
                intentId = "rename-intent", leaseToken = "rename-lease", replayPolicy = "idempotent", expectedRevision = revision
            }});
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var executions = 0;
            var acknowledgements = 0;
            var ackAfterExecutions = 0;
            var ackOk = false;
            string? writtenSource = null;
            var bridge = new RemoteBridge(() => new RemoteBridge.Settings("loopback", 1), () => false,
                new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(root), "",
                executeArchiveCommand: async value =>
                {
                    executions++;
                    if (executions == 2)
                    {
                        writtenSource = File.ReadAllText(path);
                        archive.StoreWriteFault = null;
                    }
                    return await CodexLocalRetrieval.Server.ArchiveRemoteCommands.ExecuteAsync(archive, value);
                },
                isolationFixture: true, runningSnapshot: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
                transport: (_, operation, body, _, _) =>
                {
                    if (operation == RemoteBridge.BridgeOperation.Lease) return Task.FromResult((0, command));
                    if (operation == RemoteBridge.BridgeOperation.Ack)
                    {
                        acknowledgements++;
                        ackAfterExecutions = executions;
                        ackOk = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body!).GetProperty("ok").GetBoolean();
                        cancellation.Cancel();
                    }
                    return Task.FromResult((0, "{}"));
                });
            await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.AreEqual(2, ackAfterExecutions, "unknown transcript/store outcome must not receive a terminal ACK before reconciliation");
            Assert.AreEqual(1, acknowledgements);
            Assert.IsTrue(ackOk);
            Assert.AreEqual("applied", archive.Store.ManagementOperations["rename-intent"].State);
            Assert.AreEqual(writtenSource, File.ReadAllText(path), "redelivery must reconcile without reappending");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeRename_HoldsLaunchCustodyThroughAppliedReceiptCommit(bool recovery)
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-commit-custody-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        File.WriteAllText(path, "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n");
        try
        {
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"),
                sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var id = Guid.NewGuid().ToString();
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Original" };
            await archive.SaveAsync();
            var revision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            if (recovery)
            {
                archive.StoreWriteFault = stage =>
                {
                    using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(source);
                    if (reader.ReadToEnd().Contains("muxRenameIntent", StringComparison.Ordinal))
                        throw new IOException("injected receipt failure before recovery probe");
                };
                Assert.IsTrue((await archive.RenameNativeRemoteAsync("claude", id, "Desired", "custody", revision)).Uncertain);
            }
            var observedCommit = false;
            var competingLaunchAllowed = false;
            archive.StoreWriteFault = stage =>
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(source);
                if (!reader.ReadToEnd().Contains("muxRenameIntent", StringComparison.Ordinal)) return;
                observedCommit = true;
                if (new SessionLaunchGovernor().TryAcquire(
                    new(id, Array.Empty<string>(), "claude", "test", "competing fixture launch", "", "", ""),
                    out var competing, out _))
                {
                    competingLaunchAllowed = true;
                    competing?.Dispose();
                }
            };
            var result = await archive.RenameNativeRemoteAsync("claude", id, "Desired", "custody", revision);
            Assert.IsTrue(result.Ok, result.Detail);
            Assert.IsTrue(observedCommit, "fixture must probe the applied receipt durability boundary");
            Assert.IsFalse(competingLaunchAllowed, "launch custody must not end between transcript append and applied receipt commit");
            Assert.IsTrue(new SessionLaunchGovernor().TryAcquire(
                new(id, Array.Empty<string>(), "claude", "test", "post-commit fixture launch", "", "", ""),
                out var afterCommit, out var detail), detail);
            afterCommit?.Dispose();
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_MissingPreparedMarkerRemainsUnconfirmedWithoutWriting()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-missing-marker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n";
        File.WriteAllText(path, original);
        try
        {
            var storePath = Path.Combine(root, "store.json");
            var archive = new ArchiveService(storePath: storePath,
                sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var id = Guid.NewGuid().ToString();
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Original" };
            await archive.SaveAsync();
            var revision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            archive.StoreWriteFault = _ =>
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(source);
                if (reader.ReadToEnd().Contains("muxRenameIntent", StringComparison.Ordinal))
                    throw new IOException("injected applied receipt failure");
            };
            var unknown = await archive.RenameNativeRemoteAsync("claude", id, "Desired", "pending", revision);
            Assert.IsTrue(unknown.Uncertain);
            var committed = File.ReadAllText(path);
            File.WriteAllText(path, original);
            var restarted = new ArchiveService(storePath: storePath,
                sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var missing = await restarted.RenameNativeRemoteAsync("claude", id, "Desired", "pending", revision);
            Assert.IsFalse(missing.Ok);
            Assert.IsTrue(missing.Uncertain);
            Assert.AreEqual("prepared", restarted.Store.ManagementOperations["pending"].State);
            Assert.AreEqual(original, File.ReadAllText(path));
            File.WriteAllText(path, committed);
            var recovered = await restarted.RenameNativeRemoteAsync("claude", id, "Desired", "pending", revision);
            Assert.IsTrue(recovered.Ok, recovered.Detail);
            Assert.IsFalse(recovered.Uncertain);
            Assert.AreEqual(committed, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_DurableReplayDoesNotRestoreOlderTitle()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        File.WriteAllText(path, "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n");
        try
        {
            var storePath = Path.Combine(root, "store.json");
            ArchiveService Fresh() => new(storePath: storePath, sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var archive = Fresh();
            var id = Guid.NewGuid().ToString();
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Original" };
            await archive.SaveAsync();
            var firstRevision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            var first = await archive.RenameNativeRemoteAsync("claude", id, "First", "rename-first", firstRevision);
            Assert.IsTrue(first.Ok, first.Detail);
            var secondRevision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            var second = await archive.RenameNativeRemoteAsync("claude", id, "Second", "rename-second", secondRevision);
            Assert.IsTrue(second.Ok, second.Detail);
            var latest = File.ReadAllText(path);
            var restarted = Fresh();
            var replay = await restarted.RenameNativeRemoteAsync("claude", id, "First", "rename-first", firstRevision);
            Assert.IsTrue(replay.Ok, replay.Detail);
            Assert.AreEqual(latest, File.ReadAllText(path));
            Assert.AreEqual("Second", restarted.Store.Sessions[id].Title);
            var stale = await restarted.RenameNativeRemoteAsync("claude", id, "Stale", "rename-stale", firstRevision);
            Assert.IsFalse(stale.Ok);
            var collision = await restarted.RenameNativeRemoteAsync("claude", id, "Different", "rename-first", firstRevision);
            Assert.IsFalse(collision.Ok);
            Assert.AreEqual(latest, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeRename_RefusesCodexAndPendingWriteAfterRestart(bool malformedTail)
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n";
        File.WriteAllText(path, original);
        try
        {
            var storePath = Path.Combine(root, "store.json");
            var archive = new ArchiveService(storePath: storePath, sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var id = Guid.NewGuid().ToString();
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Original" };
            await archive.SaveAsync();
            var revision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            var refused = await archive.RenameNativeRemoteAsync("codex", id, "Forbidden", "wrong-tool", revision);
            Assert.IsFalse(refused.Ok);
            Assert.AreEqual(original, File.ReadAllText(path));
            archive.StoreWriteFault = _ =>
            {
                using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(source);
                if (reader.ReadToEnd().Contains("First", StringComparison.Ordinal))
                    throw new IOException("injected receipt persistence failure");
            };
            var unknown = await archive.RenameNativeRemoteAsync("claude", id, "First", "first", revision);
            Assert.IsFalse(unknown.Ok, "a native write without its applied receipt is not confirmed success");
            var once = File.ReadAllText(path);
            StringAssert.Contains(once, "First");
            var restarted = new ArchiveService(storePath: storePath, sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var later = await restarted.RenameNativeRemoteAsync("claude", id, "Later", "later", revision);
            Assert.IsFalse(later.Ok);
            File.AppendAllText(path, "{\"type\":\"custom-title\",\"customTitle\":\"Newer external title\"}\n");
            var validSource = File.ReadAllText(path);
            if (malformedTail)
            {
                File.AppendAllText(path, "{\"type\":\"custom-title\",\"customTitle\":\"Incomplete");
                var incompleteSource = File.ReadAllText(path);
                var incomplete = await restarted.RenameNativeRemoteAsync("claude", id, "First", "first", revision);
                Assert.IsFalse(incomplete.Ok, "a partial later title must not be skipped when reconciling the current title");
                Assert.AreEqual("prepared", restarted.Store.ManagementOperations["first"].State);
                Assert.AreEqual(incompleteSource, File.ReadAllText(path));
                File.WriteAllText(path, validSource);
            }
            var beforeReconcile = File.ReadAllText(path);
            var retry = await restarted.RenameNativeRemoteAsync("claude", id, "First", "first", revision);
            Assert.IsTrue(retry.Ok, retry.Detail);
            Assert.AreEqual("Newer external title", restarted.Store.Sessions[id].Title);
            Assert.AreEqual(beforeReconcile, File.ReadAllText(path), "reconciliation must confirm the marker without another transcript write");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeRename_RefusesExternalTitleChangedSinceArchiveRevision(bool titleOutsideTail)
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-external-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        const string original = "{\"type\":\"custom-title\",\"customTitle\":\"Original\"}\n";
        File.WriteAllText(path, original);
        try
        {
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"), sourceOverride: Array.Empty<CodexLocalRetrieval.Core.Models.SessionSource>());
            var id = Guid.NewGuid().ToString();
            archive.Store.Sessions[id] = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = id, Tool = "claude", SourcePath = path, Title = "Original" };
            await archive.SaveAsync();
            var revision = archive.RemoteManagementRevision(archive.Store.Sessions[id]);
            File.AppendAllText(path, "{\"type\":\"custom-title\",\"customTitle\":\"External new title\"}\n");
            if (titleOutsideTail)
                File.AppendAllText(path, "{\"type\":\"assistant\",\"message\":{\"content\":\"" + new string('x', 128 * 1024) + "\"}}\n");
            var latest = File.ReadAllText(path);
            var outcome = await archive.RenameNativeRemoteAsync("claude", id, "Stale browser title", "external-race", revision);
            Assert.IsFalse(outcome.Ok, "archive revision alone must not overwrite a newer native title");
            Assert.AreEqual(latest, File.ReadAllText(path));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_WrongToolCannotWriteIndexedTranscript()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-tool-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "source.jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"content\":\"Original prompt\"}}\n";
        File.WriteAllText(path, original);
        try
        {
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var session = new CodexLocalRetrieval.Core.Models.ArchiveSession
                { Id = Guid.NewGuid().ToString(), Tool = "claude", SourcePath = path, Title = "Original title" };
            archive.Store.Sessions[session.Id] = session;
            var status = await archive.RenameNativeByIdAsync("codex", session.Id, "Wrong tool title");
            Assert.IsFalse(ArchiveService.NativeRenameSucceeded(status));
            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.AreEqual("Original title", session.Title);
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_HeldLaunchClaimPreservesBothPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-claim-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(project, id + ".jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n";
        File.WriteAllText(path, original);
        try
        {
            Assert.IsTrue(SessionLaunchClaims.TryAcquire(id, null, "disposable rename test", out var claim, out var detail), detail);
            using (claim)
            {
                var sessions = new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(root);
                Assert.IsFalse(sessions.RenameSession(id, "must not write"));
                Assert.AreEqual(original, File.ReadAllText(path));
                var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"));
                var session = new CodexLocalRetrieval.Core.Models.ArchiveSession
                {
                    Id = id, Tool = "claude", SourcePath = path, Title = "Native original", CustomTitle = "App original"
                };
                archive.Store.Sessions[id] = session;
                Assert.IsFalse(ArchiveService.NativeRenameSucceeded(await archive.RenameNativeAsync(session, "must not write")));
                Assert.AreEqual("Native original", session.Title);
                Assert.AreEqual("App original", session.CustomTitle);
                Assert.AreEqual(original, File.ReadAllText(path));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task NativeRename_OpenWriterRefusesBothPathsAndReleasesReservation()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-writer-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "project");
        Directory.CreateDirectory(project);
        var id = Guid.NewGuid().ToString();
        var path = Path.Combine(project, id + ".jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n";
        File.WriteAllText(path, original);
        try
        {
            var sessions = new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(root);
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var session = new CodexLocalRetrieval.Core.Models.ArchiveSession
            {
                Id = id, Tool = "claude", SourcePath = path, Title = "Native original", CustomTitle = "App original"
            };
            archive.Store.Sessions[id] = session;
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                Assert.IsFalse(sessions.RenameSession(id, "must not write"));
                Assert.IsFalse(ArchiveService.NativeRenameSucceeded(await archive.RenameNativeAsync(session, "must not write")));
                Assert.AreEqual("Native original", session.Title);
                Assert.AreEqual("App original", session.CustomTitle);
                Assert.HasCount(0, SessionLaunchClaims.ReadClaimsForSession(id));
            }
            Assert.AreEqual(original, File.ReadAllText(path));
            Assert.IsTrue(ArchiveService.NativeRenameSucceeded(await archive.RenameNativeAsync(session, "Idle title")));
            var once = File.ReadAllText(path);
            Assert.IsTrue(ArchiveService.NativeRenameSucceeded(await archive.RenameNativeAsync(session, "Idle title")));
            Assert.AreEqual(once, File.ReadAllText(path));
            Assert.HasCount(0, SessionLaunchClaims.ReadClaimsForSession(id));
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task ArchiveNativeRename_UnknownOwnershipPreservesNativeAndAppTitles()
    {
        var root = Path.Combine(Path.GetTempPath(), "rename-archive-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "session.jsonl");
        const string original = "{\"type\":\"user\",\"message\":{\"content\":\"Original\"}}\n";
        File.WriteAllText(path, original);
        try
        {
            var archive = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var session = new CodexLocalRetrieval.Core.Models.ArchiveSession
            {
                Id = "rename-unknown", Tool = "claude", SourcePath = path,
                Title = "Native original", CustomTitle = "App original"
            };
            archive.Store.Sessions[session.Id] = session;
            foreach (var ownership in new[] { "scan-failure", "registry-failure", "registry-owner", "handle-owner" })
            {
                _scanFails = ownership == "scan-failure";
                _registryFails = ownership == "registry-failure";
                _registryRows.Clear(); _scanRows.Clear(); _handleRows.Clear();
                if (ownership == "registry-owner") _registryRows[session.Id] = 12345;
                if (ownership == "handle-owner")
                {
                    _scanRows.Add(new ArchiveService.RunningSessionInfo(12345, "claude", "", "Terminal", "", ""));
                    _handleRows[session.Id] = 12345;
                }
                var status = await archive.RenameNativeAsync(session, "Must not appear");
                Assert.AreEqual(original, File.ReadAllText(path), ownership);
                Assert.AreEqual("Native original", session.Title, ownership);
                Assert.AreEqual("App original", session.CustomTitle, ownership);
                StringAssert.Contains(status, "deferred");
                Assert.IsFalse(ArchiveService.NativeRenameSucceeded(status));
            }
        }
        finally { Directory.Delete(root, true); }
    }

    // Stands in for TryScan: counts itself against the same counter the real WMI pass ticks, so a test asserting
    // "one sweep" is asserting on the number the perf gates record.
    private (bool Ok, List<ArchiveService.RunningSessionInfo> Sessions, string Detail) FakeScan()
    {
        Interlocked.Increment(ref _scans);
        PerfCounters.WmiSweep();
        return _scanFails
            ? (false, new List<ArchiveService.RunningSessionInfo>(), "injected scan failure")
            : (true, new List<ArchiveService.RunningSessionInfo>(_scanRows), "");
    }

    // Stands in for TryClaudeLiveSessionIds. Not a WMI pass, so it does NOT tick wmiSweeps — it gets its own
    // counter, and the tests assert both collapse together.
    private (bool Ok, Dictionary<string, int> Map, HashSet<int> Unverifiable, string Detail) FakeRegistry(HashSet<int>? livePids)
    {
        Interlocked.Increment(ref _registryReads);
        return _registryFails
            ? (false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new HashSet<int> { 4242 }, "injected registry failure")
            : (true, new Dictionary<string, int>(_registryRows, StringComparer.OrdinalIgnoreCase), new HashSet<int>(), "");
    }

    private (bool Ok, Dictionary<string, int> Map, HashSet<int> Unverifiable, string Detail) FakeOpenTranscripts(HashSet<int> livePids)
        => (_handleUnverifiable.Count == 0,
            new Dictionary<string, int>(_handleRows, StringComparer.OrdinalIgnoreCase),
            new HashSet<int>(_handleUnverifiable),
            _handleUnverifiable.Count == 0 ? "" : "injected handle failure");

    private static long Sweeps() => PerfCounters.Snapshot()["wmiSweeps"];

    [TestMethod]
    public async Task RunningBridge_PreservesEnrichedIdentityAndVerificationFailure()
    {
        _scanRows = new() { new(500, "codex", "", "Terminal", "", "") };
        _handleRows["exact-handle-id"] = 500;
        _handleUnverifiable.Add(500);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        System.Text.Json.JsonElement observed = default;
        var bridge = new RemoteBridge(() => new RemoteBridge.Settings("loopback", 1), () => false,
            new CodexLocalRetrieval.Core.Agents.ClaudeSessionStore(Path.GetTempPath()), "",
            isolationFixture: true,
            transport: (_, operation, body, _, _) =>
            {
                if (operation == RemoteBridge.BridgeOperation.Running)
                {
                    observed = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(body!);
                    cancellation.Cancel();
                }
                return Task.FromResult((0, operation == RemoteBridge.BridgeOperation.Lease ? "[]" : "{}"));
            });
        await bridge.RunLoopAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.AreEqual(System.Text.Json.JsonValueKind.Object, observed.ValueKind);
        Assert.IsFalse(observed.GetProperty("runningVerified").GetBoolean());
        var row = observed.GetProperty("runningSessions")[0];
        Assert.AreEqual(500, row.GetProperty("pid").GetInt32());
        Assert.AreEqual("exact-handle-id", row.GetProperty("sessionId").GetString());
        Assert.AreEqual("unverifiable", row.GetProperty("identityStatus").GetString());
    }

    [TestMethod]
    public void EnrichedScan_UsesCommandLineRegistryAndOpenTranscriptIdentityWithoutGuessing()
    {
        _scanRows = new()
        {
            new(100, "claude", "command-id", "Terminal", "", ""),
            new(200, "claude", "", "VS Code", "", ""),
            new(300, "codex", "", "Terminal", "", ""),
            new(400, "claude", "", "Terminal", "", ""),
        };
        _registryRows["registry-id"] = 200;
        _handleRows["codex-id"] = 300;

        Assert.IsTrue(RunningSessions.TryScanEnriched(out var rows, out var detail), detail);
        Assert.AreEqual("command-id", rows.Single(r => r.Pid == 100).SessionId);
        Assert.AreEqual("command line", rows.Single(r => r.Pid == 100).IdentitySource);
        Assert.AreEqual("registry-id", rows.Single(r => r.Pid == 200).SessionId);
        Assert.AreEqual("Claude registry", rows.Single(r => r.Pid == 200).IdentitySource);
        Assert.AreEqual("codex-id", rows.Single(r => r.Pid == 300).SessionId);
        Assert.AreEqual("open transcript", rows.Single(r => r.Pid == 300).IdentitySource);
        Assert.AreEqual("", rows.Single(r => r.Pid == 400).SessionId);
        Assert.AreEqual("unresolved", rows.Single(r => r.Pid == 400).IdentityStatus);
    }

    [TestMethod]
    public void EnrichedScan_PreservesConflictingEvidenceAsAliases()
    {
        _scanRows = new() { new(100, "claude", "command-id", "Terminal", "", "") };
        _registryRows["registry-id"] = 100;
        _handleRows["handle-id"] = 100;

        Assert.IsTrue(RunningSessions.TryScanEnriched(out var rows, out var detail), detail);
        var row = rows.Single();
        Assert.AreEqual("command-id", row.SessionId);
        CollectionAssert.AreEquivalent(new[] { "registry-id", "handle-id" }, row.SessionAliases!.ToArray());
        CollectionAssert.AreEquivalent(new[] { "command-id", "registry-id", "handle-id" }, row.AllSessionIds.ToArray());
    }

    [TestMethod]
    public void EnrichedScan_ReportsUnverifiableIdentityButKeepsTheProcessVisible()
    {
        _scanRows = new() { new(500, "codex", "", "Terminal", "", "") };
        _handleUnverifiable.Add(500);

        Assert.IsFalse(RunningSessions.TryScanEnriched(out var rows, out var detail));
        Assert.AreEqual(1, rows.Count);
        Assert.AreEqual(500, rows[0].Pid);
        Assert.AreEqual("unverifiable", rows[0].IdentityStatus);
        StringAssert.Contains(detail, "handle failure");
    }

    // ---- 1. a burst shares one sweep ----------------------------------------------------------------

    // The whole point of the cache: the callers of one refresh cycle ask within milliseconds of each other, so
    // nine of these ten questions must be answered from the first one's sweep.
    [TestMethod]
    public void TenLivenessChecksInOneBurst_SweepOnce()
    {
        var before = Sweeps();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 10; i++)
            Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var detail), detail);
        sw.Stop();

        Assert.IsTrue(sw.Elapsed < TimeSpan.FromSeconds(1), "the burst took " + sw.ElapsedMilliseconds + "ms, longer than the TTL it is meant to fit inside");
        Assert.AreEqual(1, Sweeps() - before, "ten liveness checks inside one TTL should cost exactly one WMI sweep");
        Assert.AreEqual(1, Interlocked.Read(ref _scans));
        Assert.AreEqual(1, Interlocked.Read(ref _registryReads), "the Claude registry walk must collapse with the sweep, not survive it");
    }

    // ---- 2. [F#3] the post-claim re-check bypasses the cache -----------------------------------------

    // Once the reservation is HELD the question changes from "who is running" to "did the world change in the
    // last few milliseconds", and a cached answer cannot tell you that by construction. So with an already-warm
    // cache, acquiring a claim must still cost a sweep: the pre-check rides the burst, the re-check does not.
    [TestMethod]
    public void PostClaimRecheck_BypassesTheCacheAndSweepsAgain()
    {
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var warmDetail), warmDetail);   // warm it
        var before = Sweeps();
        var scansBefore = Interlocked.Read(ref _scans);

        var root = Path.Combine(Path.GetTempPath(), "scan-cache-claim-" + Guid.NewGuid().ToString("N"));
        SessionLaunchClaim? claim = null;
        try
        {
            var ok = SessionLaunchClaims.TryAcquire(
                Guid.NewGuid().ToString(),
                aliases: null,
                reason: "scan cache test",
                out claim,
                out var detail,
                isSessionLive: null,   // null => the check falls through to RunningSessions, which is the point
                options: new SessionLaunchClaims.Options(RootDirectory: root));

            Assert.IsTrue(ok, detail);
            Assert.AreEqual(1, Sweeps() - before, "the post-claim re-check must bypass the warm cache and sweep for itself");
            Assert.AreEqual(1, Interlocked.Read(ref _scans) - scansBefore, "exactly one of the two liveness checks may bypass: the post-claim one");
        }
        finally
        {
            try { claim?.Dispose(); } catch { }
            try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
        }
    }

    // ---- 3. [F#4] invalidation forces the next caller to re-sweep ------------------------------------

    // Every kill/create site calls this. If it did not actually drop the entry, the next liveness question would
    // report the process we just removed as still running for the rest of the TTL.
    [TestMethod]
    public void InvalidateScanCache_MakesTheNextCallSweep()
    {
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var d1), d1);
        var afterWarm = Sweeps();

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var d2), d2);
        Assert.AreEqual(0, Sweeps() - afterWarm, "precondition: a second call inside the TTL is served from cache");

        RunningSessions.InvalidateScanCache();

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var d3), d3);
        Assert.AreEqual(1, Sweeps() - afterWarm, "after invalidation the next caller must re-sweep");
        Assert.AreEqual(2, Interlocked.Read(ref _scans));
    }

    // ---- 4. [F#8] a failed sweep is never cached -----------------------------------------------------

    // A transient WMI hiccup must not fail-close every caller for a whole TTL: each retries for itself, and the
    // first success is what becomes shared.
    [TestMethod]
    public void FailedScan_IsNeverCachedAndTheNextCallRetries()
    {
        _scanFails = true;

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var first), "the injected failure should surface: " + first);
        var afterFailure = Sweeps();
        Assert.AreEqual(1, Interlocked.Read(ref _scans));

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var second), "the injected failure should still surface: " + second);
        Assert.AreEqual(1, Sweeps() - afterFailure, "a failed sweep must not be served from cache — the next caller retries");

        _scanFails = false;
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var third), third);
        Assert.AreEqual(2, Sweeps() - afterFailure, "the recovery call sweeps for itself");

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var fourth), fourth);
        Assert.AreEqual(2, Sweeps() - afterFailure, "...and only a SUCCESSFUL sweep becomes the shared answer");
    }

    // Same rule one layer down: an unverifiable registry read means a live owner may be HIDDEN from the answer,
    // and re-serving that for a whole TTL would turn one write race into a burst-wide blind spot.
    [TestMethod]
    public void FailedRegistryRead_IsNeverCachedAndTheNextCallRetries()
    {
        _registryFails = true;

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var unverifiable, out var first), first);
        CollectionAssert.Contains(unverifiable.ToList(), 4242, "the blocking pid should be reported as unverifiable, not silently dropped");
        Assert.AreEqual(1, Interlocked.Read(ref _registryReads));

        Assert.IsFalse(RunningSessions.TryAllLiveSessionIds(out _, out var second), second);
        Assert.AreEqual(2, Interlocked.Read(ref _registryReads), "a failed registry read must not be cached");

        _registryFails = false;
        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var third), third);
        Assert.AreEqual(3, Interlocked.Read(ref _registryReads));

        Assert.IsTrue(RunningSessions.TryAllLiveSessionIds(out _, out var fourth), fourth);
        Assert.AreEqual(3, Interlocked.Read(ref _registryReads), "only the clean read becomes the shared answer");
    }
}
