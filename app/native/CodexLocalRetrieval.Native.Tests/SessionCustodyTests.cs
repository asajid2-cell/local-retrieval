using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using System.Text.Json;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class SessionCustodyTests
{
    [TestMethod]
    public void BuildOverview_DangerWhenLiveOwnerScanIsUnverified()
    {
        var store = Store(Session("s1"));

        var overview = Build(store, liveVerified: false);

        Assert.AreEqual("danger", overview.Severity);
        Assert.IsFalse(overview.LiveVerified);
        Assert.AreEqual(1, overview.DangerCount);
        Assert.AreEqual(0, overview.Items.Count, "unverified scan is a global block, not a fake per-chat match");
        StringAssert.Contains(overview.Headline, "Live-owner verification");
    }

    [TestMethod]
    public void BuildOverview_DangerItemWhenAliasIsLive()
    {
        var session = Session("s1");
        session.Aliases.Add("fork-live");
        var store = Store(session);

        var overview = Build(store, liveIds: new[] { "fork-live" });

        Assert.AreEqual("danger", overview.Severity);
        Assert.AreEqual(1, overview.LiveOwnerCount);
        Assert.AreEqual(1, overview.Items.Count);
        Assert.AreEqual("danger", overview.Items[0].Severity);
        CollectionAssert.Contains(overview.Items[0].Signals.ToList(), "live owner");
        StringAssert.Contains(overview.Items[0].NextAction, "Attach");
    }

    [TestMethod]
    public void BuildOverview_UsesOneLiveScanForManySessions()
    {
        var store = Store(Session("s1"), Session("s2"), Session("s3"));
        var calls = 0;

        var overview = SessionCustody.BuildOverview(store, new SessionCustody.Options(
            Now: DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            EventRootDirectory: Path.Combine(Path.GetTempPath(), "clr-empty-events-" + Guid.NewGuid().ToString("N")),
            ClaimRootDirectory: Path.Combine(Path.GetTempPath(), "clr-empty-claims-" + Guid.NewGuid().ToString("N")),
            LiveIdsProvider: () =>
            {
                calls++;
                return (true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "verified");
            },
            FileExists: _ => true));

        Assert.AreEqual("ok", overview.Severity);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public void BuildOverview_DangerItemForActiveLaunchClaim()
    {
        using var claims = TempDir("custody-claims");
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var session = Session("s1");
        var store = Store(session);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            session.Id,
            session.Aliases,
            "test claim",
            out var claim,
            out var detail,
            _ => false,
            new SessionLaunchClaims.Options(claims.Path, TimeSpan.FromMinutes(2), now)), detail);
        using (claim)
        {
            var overview = Build(store, claimRoot: claims.Path, now: now);

            Assert.AreEqual("danger", overview.Severity);
            Assert.AreEqual(1, overview.ActiveClaimCount);
            CollectionAssert.Contains(overview.Items.Single().Signals.ToList(), "active launch claim");
        }
    }

    [TestMethod]
    public void BuildOverview_WarnsForExpiredLaunchClaim()
    {
        using var claims = TempDir("custody-claims");
        var created = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var session = Session("s1");
        var store = Store(session);

        Assert.IsTrue(SessionLaunchClaims.TryAcquire(
            session.Id,
            null,
            "expired claim",
            out var claim,
            out var detail,
            _ => false,
            new SessionLaunchClaims.Options(claims.Path, TimeSpan.FromMinutes(1), created)), detail);
        claim!.RetainUntilExpiry();
        claim.Dispose();

        var overview = Build(store, claimRoot: claims.Path, now: created.AddMinutes(3));

        Assert.AreEqual("warn", overview.Severity);
        Assert.AreEqual(1, overview.ExpiredClaimCount);
        Assert.AreEqual("warn", overview.Items.Single().Severity);
    }

    [TestMethod]
    public void BuildOverview_DangerWhenMuxTabCurrentlyClaimsSession()
    {
        var session = Session("s1");
        var store = Store(session);
        store.MuxTabHistory["tab-a"] = new MuxTabRecord
        {
            Current = new MuxTabChat { Id = "s1", Tool = "codex", Title = "Session One" }
        };

        var overview = Build(store);

        Assert.AreEqual("danger", overview.Severity);
        Assert.AreEqual(1, overview.CurrentMuxCount);
        CollectionAssert.Contains(overview.Items.Single().Signals.ToList(), "current mux owner");
    }

    [TestMethod]
    public void BuildOverview_WarnsForPendingFilingIntentInSameWorkspace()
    {
        var session = Session("s1");
        var store = Store(session);
        store.PendingNewChats.Add(new PendingNewChat
        {
            Tool = "codex",
            Cwd = session.Workspace,
            CollectionId = "c1",
            CreatedAt = DateTimeOffset.Parse("2026-07-08T12:00:00Z").ToString("O")
        });

        var overview = Build(store);

        Assert.AreEqual("warn", overview.Severity);
        Assert.AreEqual(1, overview.PendingFilingCount);
        CollectionAssert.Contains(overview.Items.Single().Signals.ToList(), "pending filing");
    }

    [TestMethod]
    public void BuildOverview_RecentIncidentMatchesAlias()
    {
        using var events = TempDir("custody-events");
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var session = Session("s1");
        session.Aliases.Add("child-id");
        var store = Store(session);
        Assert.IsTrue(SessionEventLedger.TryAppend(
            SessionEventLedger.Create("mux.refused.create", @"refused from C:\Users\Ahmed\private\chat.jsonl", "child-id", severity: "warn", at: now),
            out var detail,
            new SessionEventLedger.Options(events.Path, now)), detail);

        var overview = Build(store, eventRoot: events.Path, now: now);

        Assert.AreEqual("warn", overview.Severity);
        Assert.AreEqual(1, overview.RecentIncidentCount);
        var item = overview.Items.Single();
        CollectionAssert.Contains(item.Signals.ToList(), "recent refusal");
        StringAssert.DoesNotMatch(item.Reason, new System.Text.RegularExpressions.Regex(@"C:\\Users\\Ahmed"));
    }

    [TestMethod]
    public void BuildOverview_MissingSourceDoesNotExposeRawSourcePath()
    {
        var session = Session("s1");
        session.SourcePath = @"C:\Users\Ahmed\.claude\projects\secret\s1.jsonl";
        session.Workspace = @"C:\Users\Ahmed\private-repo";
        session.WorkspaceName = "";
        var store = Store(session);

        var overview = Build(store, fileExists: _ => false);

        Assert.AreEqual("danger", overview.Severity);
        var item = overview.Items.Single();
        StringAssert.DoesNotMatch(item.Reason, new System.Text.RegularExpressions.Regex(@"C:\\Users\\Ahmed"));
        Assert.AreEqual("private-repo", item.Workspace);
    }

    [TestMethod]
    public void BuildOverview_DtoDoesNotSerializeRawPathsOrCommands()
    {
        using var events = TempDir("custody-events");
        var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
        var session = Session("s1");
        session.SourcePath = @"C:\Users\Ahmed\.claude\projects\secret\s1.jsonl";
        session.Workspace = @"C:\Users\Ahmed\private-repo";
        session.WorkspaceName = "";
        var store = Store(session);
        Assert.IsTrue(SessionEventLedger.TryAppend(
            SessionEventLedger.Create(
                "resume.refused.claim",
                @"refused command codex resume s1 from C:\Users\Ahmed\repo",
                "s1",
                severity: "warn",
                at: now,
                details: new Dictionary<string, string> { ["command"] = "codex resume s1" }),
            out var detail,
            new SessionEventLedger.Options(events.Path, now)), detail);

        var overview = Build(store, eventRoot: events.Path, now: now, fileExists: _ => false);
        var json = JsonSerializer.Serialize(overview);

        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(@"C:\\Users\\Ahmed"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("secret"));
    }

    [TestMethod]
    public void BuildOverview_RedactsSecretsInDisplayLabels()
    {
        var fakeKey = "sk-" + "FAKEexampleKEYnotreal0000000";
        var session = Session("s1");
        session.Title = "deploy " + fakeKey;
        var store = Store(session);

        var overview = Build(store, fileExists: _ => false);
        var json = JsonSerializer.Serialize(overview);

        StringAssert.Contains(json, "[redacted-secret]");
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(fakeKey)));
    }

    [TestMethod]
    public void BuildOverview_HealthyUnfiledSessionDoesNotCreateNoiseItem()
    {
        var store = Store(Session("s1"));

        var overview = Build(store);

        Assert.AreEqual("ok", overview.Severity);
        Assert.AreEqual(0, overview.Items.Count);
        Assert.AreEqual(1, overview.TotalSessions);
    }

    private static ArchiveSession Session(string id) => new()
    {
        Id = id,
        Title = "Session " + id,
        Tool = "codex",
        SourcePath = "present",
        Workspace = @"C:\work\repo",
        WorkspaceName = "repo",
        UpdatedAt = "2026-07-08T12:00:00Z"
    };

    private static AppStoreData Store(params ArchiveSession[] sessions)
    {
        var store = new AppStoreData();
        foreach (var session in sessions) store.Sessions[session.Id] = session;
        return store;
    }

    private static CustodyOverview Build(
        AppStoreData store,
        bool liveVerified = true,
        IEnumerable<string>? liveIds = null,
        string? eventRoot = null,
        string? claimRoot = null,
        DateTimeOffset? now = null,
        Func<string, bool>? fileExists = null)
        => SessionCustody.BuildOverview(store, new SessionCustody.Options(
            Now: now ?? DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            EventRootDirectory: eventRoot ?? Path.Combine(Path.GetTempPath(), "clr-empty-events-" + Guid.NewGuid().ToString("N")),
            ClaimRootDirectory: claimRoot ?? Path.Combine(Path.GetTempPath(), "clr-empty-claims-" + Guid.NewGuid().ToString("N")),
            LiveIdsProvider: () => (liveVerified, new HashSet<string>(liveIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase), liveVerified ? "verified" : "scan failed"),
            FileExists: fileExists ?? (_ => true)));

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
