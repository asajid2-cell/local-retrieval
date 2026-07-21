using System.Text.Json;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The remote API surface (transport-agnostic) + the bearer gate. These are what the VPS-reachable
// server exposes, so the auth and the redaction-on-the-wire are security-critical.
[TestClass]
public sealed class RemoteApiTests
{
    // ---- auth gate ----

    [TestMethod]
    public void Auth_Matches_OnlyForExactToken()
    {
        var token = "this-is-a-long-enough-secret-token";
        Assert.IsTrue(RemoteAuth.Matches(token, token));
        Assert.IsFalse(RemoteAuth.Matches("wrong", token));
        Assert.IsFalse(RemoteAuth.Matches(token + "x", token));
        Assert.IsFalse(RemoteAuth.Matches("", token));
        Assert.IsFalse(RemoteAuth.Matches(null, token));
        Assert.IsFalse(RemoteAuth.Matches(token, null));
    }

    [TestMethod]
    public void Auth_RejectsShortConfiguredToken()
    {
        Assert.IsFalse(RemoteAuth.IsValidConfiguredToken(null));
        Assert.IsFalse(RemoteAuth.IsValidConfiguredToken("short"));
        Assert.IsTrue(RemoteAuth.IsValidConfiguredToken(new string('k', RemoteAuth.MinTokenLength)));
    }

    [TestMethod]
    public void Auth_ExtractsBearerAndHeaderToken()
    {
        Assert.AreEqual("abc123", RemoteAuth.Extract("Bearer abc123", null));
        Assert.AreEqual("abc123", RemoteAuth.Extract("bearer abc123", null)); // case-insensitive scheme
        Assert.AreEqual("hdr", RemoteAuth.Extract(null, "hdr"));               // X-Auth-Token fallback
        Assert.IsNull(RemoteAuth.Extract(null, null));
        Assert.IsNull(RemoteAuth.Extract("Basic xyz", null));                  // not a bearer
    }

    // ---- api ----

    private sealed class FakeBackend : IChatBackend
    {
        private readonly Queue<BackendReply> _replies;
        public FakeBackend(params BackendReply[] replies) => _replies = new(replies);
        public string Name => "fake";
        public bool SupportsTools => true;
        public List<IReadOnlyList<ChatToolSpec>> ToolsSeen { get; } = new();
        public Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatToolSpec> t, CancellationToken ct)
        {
            ToolsSeen.Add(t);
            return Task.FromResult(_replies.Dequeue());
        }
    }

    private static ArchiveService StoreWith(params ArchiveSession[] sessions)
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-remote-" + Guid.NewGuid().ToString("N") + ".json");
        var svc = new ArchiveService(storePath: store);
        foreach (var s in sessions) svc.Store.Sessions[s.Id] = s;
        return svc;
    }

    private static string Json(object? o) => JsonSerializer.Serialize(o);

    private static ResumeLaunch FakeResume(ArchiveSession s)
        => new("codex.exe", "resume " + s.Id, Path.GetTempPath(), "codex resume " + s.Id);

    private static SessionIntegrity.Options IntegrityOptions(
        bool liveVerified = true,
        IEnumerable<string>? liveIds = null,
        string? claimRoot = null)
        => new(
            Now: DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            EventRootDirectory: Path.Combine(Path.GetTempPath(), "clr-empty-events-" + Guid.NewGuid().ToString("N")),
            ClaimRootDirectory: claimRoot ?? Path.Combine(Path.GetTempPath(), "clr-empty-claims-" + Guid.NewGuid().ToString("N")),
            LiveIdsProvider: () => (liveVerified, new HashSet<string>(liveIds ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase), liveVerified ? "verified" : "scan failed"),
            FileExists: _ => true);

    [TestMethod]
    public void Search_RedactsTitleAndSnippet()
    {
        var fakeKey = "sk-" + "FAKEexampleKEYnotreal0000000";
        var s = new ArchiveSession { Id = "s1", Title = $"deploy {fakeKey} notes", Tool = "codex", WorkspaceName = "cortex" };
        s.Messages.Add(new ArchiveMessage { Role = "user", Text = "fix the cortex renderer" });
        var api = new RemoteApi(StoreWith(s), () => null);

        var json = Json(api.Search("cortex", 10));

        StringAssert.Contains(json, "s1");
        StringAssert.Contains(json, SecretRedactor.Mask);
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(fakeKey)));
    }

    [TestMethod]
    public void Read_UnknownId_IsNull_KnownId_ReturnsMessages()
    {
        var s = new ArchiveSession { Id = "s1", Title = "hi" };
        s.Messages.Add(new ArchiveMessage { Role = "user", Text = "hello there" });
        var api = new RemoteApi(StoreWith(s), () => null);

        Assert.IsNull(api.Read("nope", 0, 20));
        var json = Json(api.Read("s1", 0, 20));
        StringAssert.Contains(json, "totalMessages");
        StringAssert.Contains(json, "hello there");
    }

    [TestMethod]
    public void Read_RedactsWhenConfigured()
    {
        var fakeKey = "sk-" + "FAKEexampleKEYnotreal0000000";
        var s = new ArchiveSession { Id = "s1", Title = "t" };
        s.Messages.Add(new ArchiveMessage { Role = "user", Text = $"the key is {fakeKey} ok" });
        var redacting = new RemoteApi(StoreWith(s), () => null, redactReads: true);
        var plain = new RemoteApi(StoreWith(s), () => null, redactReads: false);

        StringAssert.DoesNotMatch(Json(redacting.Read("s1", 0, 20)), new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(fakeKey)));
        StringAssert.Contains(Json(plain.Read("s1", 0, 20)), fakeKey); // reads are NOT redacted by default
    }

    [TestMethod]
    public void Stats_CountsByTool()
    {
        var api = new RemoteApi(StoreWith(
            new ArchiveSession { Id = "a", Tool = "codex", Pinned = true },
            new ArchiveSession { Id = "b", Tool = "claude" }), () => null);
        var json = Json(api.Stats());
        StringAssert.Contains(json, "\"totalChats\":2");
        StringAssert.Contains(json, "\"favorites\":1");
    }

    [TestMethod]
    public void Custody_ReturnsSanitizedOverview()
    {
        var fakeKey = "sk-" + "FAKEexampleKEYnotreal0000000";
        var s = new ArchiveSession
        {
            Id = "s1",
            Title = "deploy " + fakeKey,
            Tool = "codex",
            SourcePath = @"C:\Users\Ahmed\.claude\projects\secret\s1.jsonl",
            Workspace = @"C:\Users\Ahmed\private-repo",
            WorkspaceName = ""
        };
        var api = new RemoteApi(StoreWith(s), () => null);

        var json = Json(api.Custody(new SessionCustody.Options(
            Now: DateTimeOffset.Parse("2026-07-08T12:00:00Z"),
            EventRootDirectory: Path.Combine(Path.GetTempPath(), "clr-empty-events-" + Guid.NewGuid().ToString("N")),
            ClaimRootDirectory: Path.Combine(Path.GetTempPath(), "clr-empty-claims-" + Guid.NewGuid().ToString("N")),
            LiveIdsProvider: () => (true, new HashSet<string>(StringComparer.OrdinalIgnoreCase), "verified"),
            FileExists: _ => false)));

        StringAssert.Contains(json, "private-repo");
        StringAssert.Contains(json, SecretRedactor.Mask);
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(fakeKey)));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(@"C:\\Users\\Ahmed"));
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume"));
    }

    [TestMethod]
    public async Task Copilot_NoKey_ReturnsError()
    {
        var api = new RemoteApi(StoreWith(), () => null);
        var json = Json(await api.CopilotAsync("hi", null));
        StringAssert.Contains(json, "No model key");
    }

    [TestMethod]
    public async Task Copilot_RunsReadOnly_AndNeverAdvertisesMutations()
    {
        var s = new ArchiveSession { Id = "s1", Title = "cortex notes" };
        s.Messages.Add(new ArchiveMessage { Role = "user", Text = "cortex" });
        var backend = new FakeBackend(new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "Found it." } });
        var api = new RemoteApi(StoreWith(s), () => backend);

        var json = Json(await api.CopilotAsync("find cortex", null));

        StringAssert.Contains(json, "Found it.");
        // the advertised tool set must contain NO mutating tools (no favorite/rename/add_to_project/resume)
        var advertised = backend.ToolsSeen[0].Select(t => t.Name).ToList();
        CollectionAssert.DoesNotContain(advertised, "set_favorite");
        CollectionAssert.DoesNotContain(advertised, "rename_local");
        CollectionAssert.DoesNotContain(advertised, "resume_chat");
        Assert.IsTrue(advertised.Contains("search_chats"), "read tools are available");
    }

    [TestMethod]
    public async Task Favorite_Pins()
    {
        var svc = StoreWith(new ArchiveSession { Id = "s1" });
        var api = new RemoteApi(svc, () => null);
        var json = Json(await api.FavoriteAsync("s1", true));
        StringAssert.Contains(json, "\"ok\":true");
        Assert.IsTrue(svc.Store.Sessions["s1"].Pinned);
    }

    [TestMethod]
    public void Resume_ReturnsCommandOrRefusal_NeverThrows()
    {
        var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath(), SourcePath = "present" };
        var api = new RemoteApi(StoreWith(s), () => null, allowLaunch: false, resumeLaunchFactory: FakeResume);
        var json = Json(api.ResumeCommand("s1", launch: true, IntegrityOptions()));
        // launch disabled => not launched, structured response (command or refusal note)
        StringAssert.Contains(json, "\"launched\":false");
        StringAssert.Contains(json, "codex resume s1");
        StringAssert.Contains(Json(api.ResumeCommand("missing", true)), "No chat with that id");
    }

    [TestMethod]
    public void Resume_OmitsCommandWhenLiveOwnerScanIsUnverified()
    {
        var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath(), SourcePath = "present" };
        var api = new RemoteApi(StoreWith(s), () => null, allowLaunch: false, resumeLaunchFactory: FakeResume);

        var json = Json(api.ResumeCommand("s1", launch: false, IntegrityOptions(liveVerified: false)));

        StringAssert.Contains(json, "\"command\":\"\"");
        StringAssert.Contains(json, "\"workingDirectory\":\"\"");
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume s1"));
        StringAssert.Contains(json, "scan failed");
    }

    [TestMethod]
    public void Resume_OmitsCommandWhenAliasIsLive()
    {
        var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath(), SourcePath = "present" };
        s.Aliases.Add("fork-live");
        var api = new RemoteApi(StoreWith(s), () => null, allowLaunch: false, resumeLaunchFactory: FakeResume);

        var json = Json(api.ResumeCommand("s1", launch: false, IntegrityOptions(liveIds: new[] { "fork-live" })));

        StringAssert.Contains(json, "\"command\":\"\"");
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume s1"));
        StringAssert.Contains(json, "live owner");
    }

    [TestMethod]
    public void Resume_OmitsCommandWhenActiveLaunchClaimExists()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-remote-claim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var now = DateTimeOffset.Parse("2026-07-08T12:00:00Z");
            var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath(), SourcePath = "present" };
            Assert.IsTrue(SessionLaunchClaims.TryAcquire(s.Id, s.Aliases, "test", out var claim, out var detail, _ => false, new SessionLaunchClaims.Options(dir, TimeSpan.FromMinutes(2), now)), detail);
            using (claim)
            {
                var api = new RemoteApi(StoreWith(s), () => null, allowLaunch: false, resumeLaunchFactory: FakeResume);

                var json = Json(api.ResumeCommand("s1", launch: false, IntegrityOptions(claimRoot: dir)));

                StringAssert.Contains(json, "\"command\":\"\"");
                StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume s1"));
                StringAssert.Contains(json, "launch reservation");
            }
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void Resume_OmitsCommandWhenMuxCurrentlyClaimsSession()
    {
        var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath(), SourcePath = "present" };
        var svc = StoreWith(s);
        svc.Store.MuxTabHistory["tab-a"] = new MuxTabRecord { Current = new MuxTabChat { Id = "s1", Tool = "codex", Title = "Session" } };
        var api = new RemoteApi(svc, () => null, allowLaunch: false, resumeLaunchFactory: FakeResume);

        var json = Json(api.ResumeCommand("s1", launch: false, IntegrityOptions()));

        StringAssert.Contains(json, "\"command\":\"\"");
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume s1"));
        StringAssert.Contains(json, "mux tab");
    }

    [TestMethod]
    public void Resume_LaunchTrueOmittedCommandWhenGovernorClaimRefuses()
    {
        var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath(), SourcePath = "present" };
        var governor = new SessionLaunchGovernor(new SessionLaunchGovernorOptions(IsSessionLive: id => id == "s1"));
        var api = new RemoteApi(StoreWith(s), () => null, allowLaunch: true, resumeLaunchFactory: FakeResume, launchGovernor: governor);

        var json = Json(api.ResumeCommand("s1", launch: true, IntegrityOptions()));

        StringAssert.Contains(json, "\"command\":\"\"");
        StringAssert.Contains(json, "\"workingDirectory\":\"\"");
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex("codex resume s1"));
        StringAssert.Contains(json, "already running");
    }
}
