using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class SessionLaunchGovernorTests
{
    [TestMethod]
    public void TryAcquire_BlocksLiveAliasAndRecordsRefusal()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, id => id == "child-live");
        var request = Request("parent-id", new[] { "child-live" });

        var ok = governor.TryAcquire(request, out var lease, out var detail);

        Assert.IsFalse(ok);
        Assert.IsNull(lease);
        StringAssert.Contains(detail, "already running");
        var ev = SingleEvent(events.Path);
        Assert.AreEqual("test.refused", ev.Kind);
        Assert.AreEqual("warn", ev.Severity);
        CollectionAssert.Contains(ev.SessionIds, "parent-id");
        CollectionAssert.Contains(ev.SessionIds, "child-live");
    }

    [TestMethod]
    public void MarkStarted_RetainsLaunchClaimUntilStartupGraceExpires()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var governor = Governor(claims.Path, events.Path, _ => false, now);
        var request = Request("session-a");

        Assert.IsTrue(governor.TryAcquire(request, out var lease, out var detail), detail);
        using (lease)
            lease!.MarkStarted("started");

        var duringGrace = Governor(claims.Path, events.Path, _ => false, now.AddSeconds(30));
        Assert.IsFalse(duringGrace.TryAcquire(request, out _, out var blocked));
        StringAssert.Contains(blocked, "launch already pending");

        var afterGrace = Governor(claims.Path, events.Path, _ => false, now.AddMinutes(3));
        Assert.IsTrue(afterGrace.TryAcquire(request, out var after, out var afterDetail), afterDetail);
        after!.Dispose();
    }

    [TestMethod]
    public void MarkFailed_ReleasesDefinitiveFailureClaimForImmediateRetry()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, _ => false);
        var request = Request("failed-create");

        Assert.IsTrue(governor.TryAcquire(request, out var failed, out var detail), detail);
        using (failed)
            failed!.MarkFailed("muxd create failed");

        Assert.IsTrue(governor.TryAcquire(request, out var retry, out var retryDetail), retryDetail);
        retry!.Dispose();
    }

    [TestMethod]
    public void MarkFailed_RetainsUncertainFailureClaimAndStillBlocksRetry()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, _ => false);
        var request = Request("uncertain-create");

        Assert.IsTrue(governor.TryAcquire(request, out var failed, out var detail), detail);
        using (failed)
            failed!.MarkFailed("could not confirm process exit", retainUntilExpiry: true);

        Assert.IsFalse(governor.TryAcquire(request, out _, out var blocked));
        StringAssert.Contains(blocked, "launch already pending");
    }

    [TestMethod]
    public void BeginFresh_RecordsStartedEventWithoutSessionClaim()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, _ => false);
        var request = new SessionLaunchRequest(
            null,
            null,
            "codex",
            "test",
            "fresh writer",
            "fresh.refused",
            "fresh.started",
            "fresh.failed",
            Workspace: @"C:\workspace");

        using (var lease = governor.BeginFresh(request))
            lease.MarkStarted("fresh started", retainUntilExpiry: false);

        var ev = SingleEvent(events.Path);
        Assert.AreEqual("fresh.started", ev.Kind);
        Assert.AreEqual("codex", ev.Tool);
        Assert.AreEqual("workspace", ev.Workspace);
        Assert.AreEqual(0, Directory.EnumerateFiles(claims.Path).Count());
    }

    [TestMethod]
    public void TryAcquireRequiredResumeCommand_RefusesCommandWithoutResumeId()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, _ => false);
        var request = Request("", null, "mux.required.refused");

        var ok = governor.TryAcquireRequiredResumeCommand("pwsh.exe", request, out var sessionId, out var lease, out var detail);

        Assert.IsFalse(ok);
        Assert.AreEqual("", sessionId);
        Assert.IsNull(lease);
        StringAssert.Contains(detail, "does not resume a stored session");
        Assert.AreEqual("mux.required.refused", SingleEvent(events.Path).Kind);
        Assert.AreEqual(0, Directory.EnumerateFiles(claims.Path).Count());
    }

    [TestMethod]
    public void TryAcquireRequiredResumeCommand_ClaimsParsedResumeIdAndAliases()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, _ => false);
        var request = Request("", new[] { "child-id" }, "mux.refused");

        Assert.IsTrue(governor.TryAcquireRequiredResumeCommand("codex resume parent-id", request, out var sessionId, out var lease, out var detail), detail);
        Assert.AreEqual("parent-id", sessionId);
        using (lease)
        {
            Assert.IsFalse(SessionLaunchClaims.TryAcquire("child-id", null, "test", out _, out var blocked, _ => false, new SessionLaunchClaims.Options(claims.Path)));
            StringAssert.Contains(blocked, "launch already pending");
        }
    }

    [TestMethod]
    public void TryAcquireRequiredResumeCommand_RefusesAmbiguousResumeCommandAndRecordsEvent()
    {
        using var claims = NewTempDir("claims");
        using var events = NewTempDir("events");
        var governor = Governor(claims.Path, events.Path, _ => false);
        var request = Request("", null, "mux.refused.ambiguous");

        var ok = governor.TryAcquireRequiredResumeCommand(
            "codex resume idle-id && codex resume live-id",
            request,
            out var sessionId,
            out var lease,
            out var detail);

        Assert.IsFalse(ok);
        Assert.AreEqual("", sessionId);
        Assert.IsNull(lease);
        StringAssert.Contains(detail, "shell control");
        Assert.AreEqual("mux.refused.ambiguous", SingleEvent(events.Path).Kind);
        Assert.AreEqual(0, Directory.EnumerateFiles(claims.Path).Count());
    }

    private static SessionLaunchGovernor Governor(string claimRoot, string eventRoot, Func<string, bool> isLive, DateTimeOffset? now = null)
        => new(new SessionLaunchGovernorOptions(
            new SessionLaunchClaims.Options(claimRoot, TimeSpan.FromMinutes(2), now),
            new SessionEventLedger.Options(eventRoot, now),
            isLive));

    private static SessionLaunchRequest Request(string? id, IEnumerable<string>? aliases = null, string refusedKind = "test.refused")
        => new(
            id,
            aliases,
            "codex",
            "test",
            "test launch",
            refusedKind,
            "test.started",
            "test.failed");

    private static SessionEvent SingleEvent(string eventRoot)
    {
        var events = SessionEventLedger.ReadRecent(10, new SessionEventLedger.Options(eventRoot));
        Assert.AreEqual(1, events.Count);
        return events[0];
    }

    private static TempDir NewTempDir(string prefix)
        => new(Path.Combine(Path.GetTempPath(), "clr-" + prefix + "-" + Guid.NewGuid().ToString("N")));

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
