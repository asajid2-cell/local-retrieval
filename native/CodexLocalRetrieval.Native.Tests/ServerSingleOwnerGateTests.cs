using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public class ServerSingleOwnerGateTests
{
    [TestMethod]
    public void ThreadRouteRegistry_StaleSocketCannotRemoveNewerOwner()
    {
        var routes = new ThreadRouteRegistry();
        var first = routes.Register("thread-1", _ => Task.CompletedTask, _ => Task.CompletedTask);
        var second = routes.Register("thread-1", _ => Task.CompletedTask, _ => Task.CompletedTask);

        routes.Close(first);

        Assert.IsTrue(routes.TryGet("thread-1", out var current));
        Assert.AreEqual(second.OwnerToken, current.OwnerToken);
    }

    [TestMethod]
    public async Task ThreadRouteRegistry_FailedTakeoverPreservesExistingOwner()
    {
        var routes = new ThreadRouteRegistry();
        var first = routes.Register("thread-1", _ => Task.CompletedTask, _ => Task.CompletedTask);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            routes.ReplaceAfterAsync(
                "thread-1",
                _ => Task.CompletedTask,
                _ => Task.CompletedTask,
                () => Task.FromException(new InvalidOperationException("resume failed")),
                CancellationToken.None));

        Assert.IsTrue(routes.TryGet("thread-1", out var current));
        Assert.AreEqual(first.OwnerToken, current.OwnerToken);
    }

    [TestMethod]
    public void SessionLauncher_RefusesTerminalResumeWhenSessionIsLive()
    {
        var launcher = new SessionLauncher("claude-do-not-launch.exe", "codex-do-not-launch.exe", allowLaunch: true, isSessionLive: id => id == "live-id");

        var result = launcher.Open(
            new TrustedSessionLaunch(
                "live-id",
                SessionTool.Codex,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                new[] { "live-id" }),
            SessionOpenTarget.Terminal);

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "already running");
    }

    [TestMethod]
    public void SessionLauncher_RefusesTerminalResumeWhenAliasIsLive()
    {
        var launcher = new SessionLauncher("claude-do-not-launch.exe", "codex-do-not-launch.exe", allowLaunch: true, isSessionLive: id => id == "child-id");

        var result = launcher.Open(
            new TrustedSessionLaunch(
                "parent-id",
                SessionTool.Codex,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                new[] { "child-id" }),
            SessionOpenTarget.Terminal);

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "already running");
    }

    [TestMethod]
    public void SessionLauncher_RefusesClaudeVsCodeDeepLinkWhenSessionIsLive()
    {
        var launcher = new SessionLauncher("claude-do-not-launch.exe", "codex-do-not-launch.exe", allowLaunch: true, isSessionLive: id => id == "live-claude");

        var result = launcher.Open(
            new TrustedSessionLaunch(
                "live-claude",
                SessionTool.Claude,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                new[] { "live-claude" }),
            SessionOpenTarget.VsCode);

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "already running");
    }

    [TestMethod]
    public void SessionLauncher_RejectsInvalidTrustedCanonicalId()
    {
        var launcher = new SessionLauncher(
            "claude-do-not-launch.exe",
            "codex-do-not-launch.exe",
            allowLaunch: true,
            isSessionLive: _ => false);

        var result = launcher.Open(
            new TrustedSessionLaunch(
                "--dangerously-skip-permissions",
                SessionTool.Claude,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                new[] { "--dangerously-skip-permissions" }),
            SessionOpenTarget.Terminal);

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "invalid");
    }

    [TestMethod]
    public void ClaudeLiveDriver_RefusesStartTurnWhenSessionIsLive()
    {
        var driver = new ClaudeLiveDriver("claude-do-not-launch.exe", isSessionLive: id => id == "live-claude");

        try
        {
            driver.StartTurn(
                "live-claude",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "hello",
                _ => Task.CompletedTask,
                CancellationToken.None);
            Assert.Fail("StartTurn should refuse a live session before launching Claude.");
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "already running");
        }
    }

    [TestMethod]
    public void ClaudeLiveDriver_RefusesStartTurnWhenAliasIsLive()
    {
        var driver = new ClaudeLiveDriver("claude-do-not-launch.exe", isSessionLive: id => id == "child-claude");

        try
        {
            driver.StartTurn(
                "parent-claude",
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "hello",
                _ => Task.CompletedTask,
                CancellationToken.None,
                aliases: new[] { "child-claude" });
            Assert.Fail("StartTurn should refuse a live alias before launching Claude.");
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "already running");
        }
    }

    [TestMethod]
    public async Task ClaudeLiveDriver_OutputPumpSurvivesDetachedConsumerAndDrainsStderr()
    {
        var stdout = new StringReader(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-1\"}\n"
            + "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\"}\n");
        var stderr = new TrackingTextReader(new string('x', 32 * 1024));
        var callbacks = 0;
        var delivered = new List<AgentEventKind>();

        await ClaudeLiveDriver.PumpOutputAsync(stdout, stderr, ev =>
        {
            callbacks++;
            if (callbacks == 1)
                throw new IOException("socket detached");
            delivered.Add(ev.Kind);
            return Task.CompletedTask;
        });

        Assert.IsTrue(stderr.FullyDrained);
        CollectionAssert.Contains(delivered, AgentEventKind.TurnResult);
        CollectionAssert.Contains(delivered, AgentEventKind.Status);
    }

    [TestMethod]
    public async Task CodexAgentHub_RefusesStartTurnWhenSessionIsLive()
    {
        var hub = new CodexAgentHub("codex-do-not-launch.exe", isSessionLive: id => id == "live-codex");

        try
        {
            await hub.StartTurnAsync("live-codex", "hello", CancellationToken.None);
            Assert.Fail("StartTurnAsync should refuse a live session before launching codex app-server.");
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "already running");
        }
    }

    [TestMethod]
    public async Task CodexAgentHub_RefusesStartTurnWhenAliasIsLiveBeforeStartingAppServer()
    {
        var hub = new CodexAgentHub("codex-do-not-launch.exe", isSessionLive: id => id == "child-codex");

        try
        {
            await hub.StartTurnAsync("parent-codex", "hello", CancellationToken.None, aliases: new[] { "child-codex" });
            Assert.Fail("StartTurnAsync should refuse a live alias before launching codex app-server.");
        }
        catch (InvalidOperationException ex)
        {
            StringAssert.Contains(ex.Message, "already running");
        }
    }

    [TestMethod]
    public async Task CodexAgentHub_FailedServerStartDoesNotLeakActiveTurnClaim()
    {
        var hub = new CodexAgentHub(
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing-codex.exe"),
            isSessionLive: _ => false);

        try
        {
            await hub.StartTurnAsync("turn-start-failure", "hello", CancellationToken.None);
            Assert.Fail("StartTurnAsync should fail when the app-server executable is missing.");
        }
        catch
        {
        }

        Assert.IsFalse(hub.IsTurnActive("turn-start-failure"));
    }

    [TestMethod]
    public void CodexAgentHub_EnsuresServerBeforePublishingActiveTurnClaim()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "CodexLocalRetrieval.Server", "CodexAgentHub.cs"));
        var methodStart = source.IndexOf("public async Task StartTurnAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task InterruptAsync", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.IsGreaterThanOrEqualTo(0, methodStart);
        Assert.IsGreaterThan(method.IndexOf("EnsureAsync(ct)", StringComparison.Ordinal),
            method.IndexOf("_activeTurnClaims.TryAdd", StringComparison.Ordinal));
    }

    [TestMethod]
    public void CodexAgentHub_OldNotificationPumpReleasesOnlyItsServerGeneration()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "CodexLocalRetrieval.Server", "CodexAgentHub.cs"));
        var methodStart = source.IndexOf("private async Task PumpNotifications", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private async Task PumpServerRequests", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        StringAssert.Contains(method, "ReleaseTurnClaims(s");
        Assert.IsFalse(
            method.Contains("ReleaseAllTurnClaims", StringComparison.Ordinal),
            "A late pump from an old app-server must not release claims owned by its replacement.");
    }

    [TestMethod]
    public void CodexAgentHub_ReleasesLeaseBeforeRemovingClaimBookkeeping()
    {
        var root = FindRepoRoot();
        var source = File.ReadAllText(
            Path.Combine(root, "native", "CodexLocalRetrieval.Server", "CodexAgentHub.cs"));
        var methodStart = source.IndexOf("private void ReleaseClaim(", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("private static SessionLaunchRequest", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.IsGreaterThanOrEqualTo(0, methodStart);
        Assert.IsGreaterThan(
            method.IndexOf("pair.Value.Lease.Dispose()", StringComparison.Ordinal),
            method.IndexOf(".Remove(pair)", StringComparison.Ordinal),
            "Claim files must be released before the active-claim entry becomes invisible to competing cleanup paths.");
    }

    [TestMethod]
    public void ResumeCommandConflicts_UsesParsedResumeIdAgainstLiveSet()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abc-123" };

        var conflicts = CodexLocalRetrieval.Core.Remote.RunningSessions.ResumeCommandConflicts("codex resume --include-non-interactive ABC-123", live, out var sessionId);

        Assert.IsTrue(conflicts);
        Assert.AreEqual("ABC-123", sessionId);
    }

    [TestMethod]
    public void ResumeCommandConflicts_IgnoresFreshShellCommands()
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "abc-123" };

        var conflicts = CodexLocalRetrieval.Core.Remote.RunningSessions.ResumeCommandConflicts("pwsh.exe", live, out var sessionId);

        Assert.IsFalse(conflicts);
        Assert.AreEqual("", sessionId);
    }

    private sealed class TrackingTextReader(string text) : TextReader
    {
        private int _offset;

        public bool FullyDrained { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= text.Length)
            {
                FullyDrained = true;
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(buffer.Length, text.Length - _offset);
            text.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not find CodexLocalRetrieval.sln from " + AppContext.BaseDirectory);
    }
}
