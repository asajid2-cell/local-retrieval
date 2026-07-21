using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class SessionIntegrityTests
{
    [TestMethod]
    public void Build_DangerWhenLiveOwnerScanIsUnverified()
    {
        var (store, session) = Seed(filed: true);

        var summary = Build(store, session, liveVerified: false, liveIds: Array.Empty<string>());

        Assert.AreEqual("danger", summary.Severity);
        StringAssert.Contains(Check(summary, "Live owner").Summary, "scan failed");
    }

    [TestMethod]
    public void Build_DangerWhenSessionAliasIsLive()
    {
        var (store, session) = Seed(filed: true);
        session.Aliases.Add("alias-live");

        var summary = Build(store, session, liveIds: new[] { "alias-live" });

        Assert.AreEqual("danger", summary.Severity);
        CollectionAssert.Contains(summary.LiveSessionIds.ToList(), "alias-live");
        Assert.AreEqual("danger", Check(summary, "Live owner").Severity);
    }

    [TestMethod]
    public void Build_DangerWhenActiveLaunchClaimExists()
    {
        using var claims = TempDir("integrity-claims");
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var (store, session) = Seed(filed: true);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            session.Id,
            session.Aliases,
            "test active claim",
            out var claim,
            out var detail,
            _ => false,
            new SessionLaunchClaims.Options(claims.Path, TimeSpan.FromMinutes(2), now)), detail);
        using (claim)
        {
            var summary = Build(store, session, claimRoot: claims.Path, now: now);

            Assert.AreEqual("danger", summary.Severity);
            Assert.AreEqual("danger", Check(summary, "Launch claim").Severity);
            Assert.AreEqual(1, summary.LaunchClaims.Count);
            Assert.IsFalse(summary.LaunchClaims[0].Expired);
        }
    }

    [TestMethod]
    public void Build_WarnsWhenExpiredLaunchClaimRemains()
    {
        using var claims = TempDir("integrity-claims");
        var created = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var now = created.AddMinutes(3);
        var (store, session) = Seed(filed: true);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            session.Id,
            null,
            "test expired claim",
            out var claim,
            out var detail,
            _ => false,
            new SessionLaunchClaims.Options(claims.Path, TimeSpan.FromMinutes(1), created)), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();

        var summary = Build(store, session, claimRoot: claims.Path, now: now);

        Assert.AreEqual("warn", summary.Severity);
        Assert.AreEqual("warn", Check(summary, "Launch claim").Severity);
        Assert.IsTrue(summary.LaunchClaims[0].Expired);
    }

    // The reclaim-strand repro: Reclaim killed the live owner (danger cleared) but its claim-file cleanup FAILED,
    // so the only remaining launch-claim state is an EXPIRED reservation still on disk and overall severity has
    // dropped to "warn". The take-control affordance must survive that drop — otherwise the panel keeps showing
    // "Expired launch reservation files are still present" with no button left to retry.
    [TestMethod]
    public void ReclaimAvailable_StaysTrue_WhileAnExpiredLaunchReservationRemains()
    {
        using var claims = TempDir("integrity-claims");
        var created = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var now = created.AddMinutes(3);
        var (store, session) = Seed(filed: true);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            session.Id,
            null,
            "test expired claim",
            out var claim,
            out var detail,
            _ => false,
            new SessionLaunchClaims.Options(claims.Path, TimeSpan.FromMinutes(1), created)), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();

        var summary = Build(store, session, claimRoot: claims.Path, now: now);

        Assert.AreEqual("warn", summary.Severity, "an expired-only reservation scores warn, not danger");
        Assert.IsTrue(summary.LaunchClaims[0].Expired);
        Assert.IsTrue(SessionIntegrity.ReclaimAvailable(summary),
            "Reclaim must stay available while a launch reservation is still on disk, even at warn severity");
    }

    // ...but the affordance must NOT become permanent noise: a warn with no launch reservation at all (here, an
    // unfiled chat) offers no Reclaim, because there is nothing for take-control to take.
    [TestMethod]
    public void ReclaimAvailable_IsFalse_ForAWarnWithNoLaunchReservation()
    {
        var (store, session) = Seed(filed: false);

        var summary = Build(store, session);

        Assert.AreEqual("warn", summary.Severity, "an unfiled chat scores warn on Filing alone");
        Assert.AreEqual(0, summary.LaunchClaims.Count);
        Assert.IsFalse(SessionIntegrity.ReclaimAvailable(summary),
            "with no danger and no reservation on disk there is nothing to reclaim");
    }

    [TestMethod]
    public void Build_DangerWhenSourceFileIsMissing()
    {
        var (store, session) = Seed(filed: true);

        var summary = Build(store, session, fileExists: _ => false);

        Assert.AreEqual("danger", summary.Severity);
        Assert.AreEqual("danger", Check(summary, "Source file").Severity);
    }

    [TestMethod]
    public void Build_CollectionMembershipMatchesAlias()
    {
        var (store, session) = Seed(filed: false);
        session.Aliases.Add("alias-filed");
        store.Collections["c1"] = new ArchiveCollection { Id = "c1", Name = "Project One", SessionIds = { "alias-filed" } };

        var summary = Build(store, session);

        Assert.AreEqual("ok", Check(summary, "Filing").Severity);
        CollectionAssert.Contains(summary.Collections.ToList(), "Project One");
    }

    [TestMethod]
    public void Build_WarnsForPendingNewChatIntentInSameWorkspace()
    {
        var (store, session) = Seed(filed: true);
        store.PendingNewChats.Add(new PendingNewChat
        {
            Tool = session.Tool,
            Cwd = session.Workspace,
            CollectionId = "c1",
            CreatedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z").ToString("O")
        });

        var summary = Build(store, session);

        Assert.AreEqual("warn", summary.Severity);
        Assert.AreEqual("warn", Check(summary, "Pending filing").Severity);
        Assert.AreEqual(1, summary.PendingIntents.Count);
    }

    [TestMethod]
    public void Build_DangerWhenMuxTabCurrentlyClaimsSession()
    {
        var (store, session) = Seed(filed: true);
        store.MuxTabHistory["tab-a"] = new MuxTabRecord
        {
            Current = new MuxTabChat { Id = session.Id, Tool = session.Tool, Title = "chat", At = "2026-07-08T12:00:00Z" }
        };

        var summary = Build(store, session);

        Assert.AreEqual("danger", summary.Severity);
        Assert.AreEqual("danger", Check(summary, "Mux custody").Severity);
        Assert.IsTrue(summary.MuxTabs[0].IsCurrent);
    }

    [TestMethod]
    public void Build_MuxHistoryOnlyIsRecoverableOk()
    {
        var (store, session) = Seed(filed: true);
        store.MuxTabHistory["tab-a"] = new MuxTabRecord
        {
            History = { new MuxTabChat { Id = session.Id, Tool = session.Tool, Title = "chat", At = "2026-07-08T12:00:00Z" } }
        };

        var summary = Build(store, session);

        Assert.AreEqual("ok", summary.Severity);
        Assert.AreEqual("ok", Check(summary, "Mux custody").Severity);
        Assert.IsFalse(summary.MuxTabs[0].IsCurrent);
    }

    [TestMethod]
    public void Build_DangerForRecentFailedOwnershipEvent()
    {
        using var events = TempDir("integrity-events");
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var (store, session) = Seed(filed: true);
        Assert.IsTrue(SessionEventLedger.TryAppend(
            SessionEventLedger.Create("mux.failed", "mux failed", session.Id, severity: "error", at: now),
            out var detail,
            new SessionEventLedger.Options(events.Path, now)), detail);

        var summary = Build(store, session, eventRoot: events.Path, now: now);

        Assert.AreEqual("danger", summary.Severity);
        Assert.AreEqual("danger", Check(summary, "Recent events").Severity);
        Assert.AreEqual(1, summary.RecentEvents.Count);
    }

    private static (AppStoreData store, ArchiveSession session) Seed(bool filed)
    {
        var session = new ArchiveSession
        {
            Id = "session-1",
            Title = "Session One",
            Tool = "codex",
            SourcePath = "present",
            Workspace = @"C:\work\repo",
            WorkspaceName = "repo"
        };
        var store = new AppStoreData();
        store.Sessions[session.Id] = session;
        if (filed)
            store.Collections["c1"] = new ArchiveCollection { Id = "c1", Name = "Project One", SessionIds = { session.Id } };
        return (store, session);
    }

    private static SessionIntegritySummary Build(
        AppStoreData store,
        ArchiveSession session,
        bool liveVerified = true,
        IEnumerable<string>? liveIds = null,
        string? eventRoot = null,
        string? claimRoot = null,
        DateTimeOffset? now = null,
        Func<string, bool>? fileExists = null)
        => SessionIntegrity.Build(store, session, new SessionIntegrity.Options(
            Now: now ?? DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            EventRootDirectory: eventRoot,
            ClaimRootDirectory: claimRoot,
            LiveIdsProvider: () => (liveVerified, new HashSet<string>(liveIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase), liveVerified ? "verified" : "scan failed"),
            FileExists: fileExists ?? (_ => true)));

    private static SessionIntegrityCheck Check(SessionIntegritySummary summary, string name)
        => summary.Checks.Single(c => c.Name == name);

    private static TempScope TempDir(string prefix)
        => new(Path.Combine(Path.GetTempPath(), "clr-" + prefix + "-" + Guid.NewGuid().ToString("N")));

    private sealed class TempScope : IDisposable
    {
        public TempScope(string path)
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
