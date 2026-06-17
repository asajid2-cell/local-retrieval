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
        var s = new ArchiveSession { Id = "s1", Tool = "codex", Workspace = Path.GetTempPath() };
        var api = new RemoteApi(StoreWith(s), () => null, allowLaunch: false);
        var json = Json(api.ResumeCommand("s1", launch: true));
        // launch disabled => not launched, structured response (command or refusal note)
        StringAssert.Contains(json, "\"launched\":false");
        StringAssert.Contains(Json(api.ResumeCommand("missing", true)), "No chat with that id");
    }
}
