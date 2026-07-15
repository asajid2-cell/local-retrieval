using System.Collections.Concurrent;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public class SessionLaunchClaimTests
{
    [TestMethod]
    public void ClaimFileName_MatchesMuxdContract()
    {
        var root = Path.Combine(Path.GetTempPath(), "claim-name-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expected = Path.Combine(root, "Parent-ID_123-76d41cbf4b150c76.claim.json");
            File.WriteAllText(expected, "{}");

            var claims = SessionLaunchClaims.ReadClaimsForSession(
                "Parent-ID_123",
                options: new SessionLaunchClaims.Options(RootDirectory: root));

            Assert.AreEqual(1, claims.Count);
            Assert.AreEqual(expected, claims[0].Path);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void TryAcquire_RejectsNonAsciiSessionIdentity()
    {
        using var dir = TempClaimDir();

        Assert.IsFalse(SessionLaunchClaims.TryAcquire(
            "ünicode-id",
            null,
            "test",
            out var claim,
            out var detail,
            _ => false,
            Options(dir.Path)));

        Assert.IsNull(claim);
        StringAssert.Contains(detail, "missing session id");
        Assert.AreEqual(0, Directory.EnumerateFiles(dir.Path).Count());
    }

    [TestMethod]
    public void TryAcquire_BlocksSecondClaimUntilReleased()
    {
        using var dir = TempClaimDir();
        var options = Options(dir.Path);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-a", null, "test", out var first, out var firstDetail, _ => false, options), firstDetail);
        using (first)
        {
            Assert.IsFalse(SessionLaunchClaims.TryAcquire("session-a", null, "test", out var second, out var secondDetail, _ => false, options));
            Assert.IsNull(second);
            StringAssert.Contains(secondDetail, "launch already pending");
        }

        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-a", null, "test", out var afterRelease, out var afterDetail, _ => false, options), afterDetail);
        afterRelease!.Dispose();
    }

    [TestMethod]
    public void TryAcquire_BlocksAliasOverlap()
    {
        using var dir = TempClaimDir();
        var options = Options(dir.Path);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire("parent-id", new[] { "child-id" }, "test", out var first, out var detail, _ => false, options), detail);
        using (first)
        {
            Assert.IsFalse(SessionLaunchClaims.TryAcquire("child-id", null, "test", out _, out var secondDetail, _ => false, options));
            StringAssert.Contains(secondDetail, "launch already pending");
        }
    }

    [TestMethod]
    public void TryAcquire_RetainedClaimBlocksDuringStartupGraceThenExpires()
    {
        using var dir = TempClaimDir();
        var now = DateTimeOffset.UtcNow;
        var options = Options(dir.Path, now);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-b", null, "test", out var claim, out var detail, _ => false, options), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();

        Assert.IsFalse(SessionLaunchClaims.TryAcquire("session-b", null, "test", out _, out var blockedDetail, _ => false, Options(dir.Path, now.AddSeconds(30))));
        StringAssert.Contains(blockedDetail, "launch already pending");

        Assert.IsTrue(SessionLaunchClaims.TryAcquire("session-b", null, "test", out var afterExpiry, out var afterDetail, _ => false, Options(dir.Path, now.AddMinutes(3))), afterDetail);
        afterExpiry!.Dispose();
    }

    [TestMethod]
    public void TryClearAbandonedClaim_RefusesLiveUnexpiredOwner()
    {
        using var dir = TempClaimDir();
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            "session-live-owner", null, "test", out var claim, out var detail, _ => false, Options(dir.Path, now)), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();
        var info = SessionLaunchClaims.ReadClaimsForSession(
            "session-live-owner", options: Options(dir.Path, now)).Single();

        Assert.IsFalse(SessionLaunchClaims.TryClearAbandonedClaim(info, out var clearDetail, now, _ => true));
        StringAssert.Contains(clearDetail, "still owned");
        Assert.IsTrue(File.Exists(info.Path));
    }

    [TestMethod]
    public void TryClearAbandonedClaim_ClearsOwnerlessUnexpiredClaim()
    {
        using var dir = TempClaimDir();
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            "session-dead-owner", null, "test", out var claim, out var detail, _ => false, Options(dir.Path, now)), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();
        var info = SessionLaunchClaims.ReadClaimsForSession(
            "session-dead-owner", options: Options(dir.Path, now)).Single();

        Assert.IsTrue(SessionLaunchClaims.TryClearAbandonedClaim(info, out _, now, _ => false));
        Assert.IsFalse(File.Exists(info.Path));
    }

    [TestMethod]
    public void TryClearAbandonedClaim_DoesNotDeleteReplacedClaim()
    {
        using var dir = TempClaimDir();
        var now = DateTimeOffset.UtcNow;
        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            "session-replaced", null, "old", out var claim, out var detail, _ => false, Options(dir.Path, now)), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();
        var reviewed = SessionLaunchClaims.ReadClaimsForSession(
            "session-replaced", options: Options(dir.Path, now)).Single();
        File.WriteAllText(reviewed.Path,
            $$"""{"SessionId":"session-replaced","CandidateIds":["session-replaced"],"OwnerPid":99999,"OwnerProcess":"new-owner","CreatedUtc":"{{now.AddSeconds(1):O}}","ExpiresUtc":"{{now.AddMinutes(5):O}}","Reason":"new"}""");

        Assert.IsFalse(SessionLaunchClaims.TryClearAbandonedClaim(reviewed, out var clearDetail, now, _ => false));
        StringAssert.Contains(clearDetail, "changed during cleanup");
        Assert.IsTrue(File.Exists(reviewed.Path));
        StringAssert.Contains(File.ReadAllText(reviewed.Path), "new-owner");
    }

    [TestMethod]
    public void TryAcquire_OnlyOneConcurrentCallerWins()
    {
        using var dir = TempClaimDir();
        var options = Options(dir.Path);
        var winners = new ConcurrentBag<SessionLaunchClaim>();
        var failures = 0;

        Parallel.For(0, 24, _ =>
        {
            if (SessionLaunchClaims.TryAcquire("session-race", null, "race test", out var claim, out var ignoredDetail, _ => false, options))
                winners.Add(claim!);
            else
                Interlocked.Increment(ref failures);
        });

        Assert.AreEqual(1, winners.Count);
        Assert.AreEqual(23, failures);
        foreach (var claim in winners) claim.Dispose();
    }

    [TestMethod]
    public void TryAcquire_FailsClosedWhenLiveCheckThrows()
    {
        using var dir = TempClaimDir();
        var options = Options(dir.Path);

        Assert.IsFalse(SessionLaunchClaims.TryAcquire(
            "session-unknown",
            null,
            "test",
            out var claim,
            out var detail,
            _ => throw new InvalidOperationException("WMI unavailable"),
            options));
        Assert.IsNull(claim);
        StringAssert.Contains(detail, "couldn't verify whether session session-unknown is live");
        StringAssert.Contains(detail, "refusing to risk a second writer");
    }

    [TestMethod]
    public void TryAcquireForResumeCommand_NoopsForFreshShellCommand()
    {
        using var dir = TempClaimDir();

        Assert.IsTrue(SessionLaunchClaims.TryAcquireForResumeCommand("pwsh.exe", "test", out var sessionId, out var claim, out var detail, _ => false, Options(dir.Path)), detail);
        Assert.AreEqual("", sessionId);
        Assert.IsNull(claim);
    }

    [TestMethod]
    public void TryAcquireForResumeCommand_BlocksAliasOverlapWhenAliasesProvided()
    {
        using var dir = TempClaimDir();
        var options = Options(dir.Path);

        Assert.IsTrue(SessionLaunchClaims.TryAcquireForResumeCommand(
            "codex resume parent-id",
            "test",
            out var sessionId,
            out var claim,
            out var detail,
            new[] { "child-id" },
            _ => false,
            options), detail);
        Assert.AreEqual("parent-id", sessionId);
        using (claim)
        {
            Assert.IsFalse(SessionLaunchClaims.TryAcquire("child-id", null, "test", out _, out var blockedDetail, _ => false, options));
            StringAssert.Contains(blockedDetail, "launch already pending");
        }
    }

    [TestMethod]
    public void TryAcquireForResumeCommand_ParsesQuotedResumeId()
    {
        using var dir = TempClaimDir();

        Assert.IsTrue(SessionLaunchClaims.TryAcquireForResumeCommand(
            "codex resume \"quoted-id\"",
            "test",
            out var sessionId,
            out var claim,
            out var detail,
            _ => false,
            Options(dir.Path)), detail);

        Assert.AreEqual("quoted-id", sessionId);
        claim!.Dispose();
    }

    [TestMethod]
    public void TryAcquireForResumeCommand_RejectsMultipleResumeIds()
    {
        using var dir = TempClaimDir();

        Assert.IsFalse(SessionLaunchClaims.TryAcquireForResumeCommand(
            "codex resume idle-id codex resume live-id",
            "test",
            out var sessionId,
            out var claim,
            out var detail,
            _ => false,
            Options(dir.Path)));

        Assert.AreEqual("", sessionId);
        Assert.IsNull(claim);
        StringAssert.Contains(detail, "multiple resume ids");
        Assert.AreEqual(0, Directory.EnumerateFiles(dir.Path).Count());
    }

    [TestMethod]
    public void TryAcquireForResumeCommand_RejectsShellChainedResumeCommand()
    {
        using var dir = TempClaimDir();

        Assert.IsFalse(SessionLaunchClaims.TryAcquireForResumeCommand(
            "codex resume idle-id && codex resume live-id",
            "test",
            out var sessionId,
            out var claim,
            out var detail,
            _ => false,
            Options(dir.Path)));

        Assert.AreEqual("", sessionId);
        Assert.IsNull(claim);
        StringAssert.Contains(detail, "shell control");
        Assert.AreEqual(0, Directory.EnumerateFiles(dir.Path).Count());
    }

    private static SessionLaunchClaims.Options Options(string root, DateTimeOffset? now = null)
        => new(root, TimeSpan.FromMinutes(2), now);

    private static TempDir TempClaimDir()
        => new(Path.Combine(Path.GetTempPath(), "clr-claims-" + Guid.NewGuid().ToString("N")));

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
