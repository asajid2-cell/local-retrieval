using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public class ServerSingleOwnerGateTests
{
    [TestMethod]
    public void SessionLauncher_RefusesTerminalResumeWhenSessionIsLive()
    {
        var launcher = new SessionLauncher("claude-do-not-launch.exe", "codex-do-not-launch.exe", allowLaunch: true, isSessionLive: id => id == "live-id");

        var result = launcher.Open("codex", "live-id", "terminal", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "already running");
    }

    [TestMethod]
    public void SessionLauncher_RefusesTerminalResumeWhenAliasIsLive()
    {
        var launcher = new SessionLauncher("claude-do-not-launch.exe", "codex-do-not-launch.exe", allowLaunch: true, isSessionLive: id => id == "child-id");

        var result = launcher.Open("codex", "parent-id", "terminal", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), new[] { "child-id" });

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "already running");
    }

    [TestMethod]
    public void SessionLauncher_RefusesClaudeVsCodeDeepLinkWhenSessionIsLive()
    {
        var launcher = new SessionLauncher("claude-do-not-launch.exe", "codex-do-not-launch.exe", allowLaunch: true, isSessionLive: id => id == "live-claude");

        var result = launcher.Open("claude", "live-claude", "vscode", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        Assert.IsFalse(result.ok);
        StringAssert.Contains(result.message, "already running");
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
    public async Task CodexAgentSession_RefusesAdoptedResumeWhenGovernorDenies()
    {
        var governor = new SessionLaunchGovernor(new SessionLaunchGovernorOptions(IsSessionLive: id => id == "live-codex"));
        var session = new CodexAgentSession("codex-do-not-launch.exe", Path.GetTempPath(), launchGovernor: governor);
        session.AdoptSession("live-codex");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => session.SendUserAsync("hello"));
        Assert.IsFalse(session.Busy);
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
}
