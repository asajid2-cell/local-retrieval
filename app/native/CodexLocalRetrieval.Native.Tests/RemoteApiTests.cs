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

// The command pollers' admission behaviour. Both pollers (MainPage.Remote.cs PollCommandsAsync, the GUI
// twin, and RemoteBridge.cs PollAndProcessAsync, headless) run ONE gate — IntentLedger.Admit — before any
// handler, so driving that gate in the pollers' own loop shape IS driving their decision. The source-level
// tests below pin that equivalence so the simulation cannot drift away from the shipped pollers.
[TestClass]
public sealed class RemoteApiCommandPollerTests
{
    private sealed record PolledCommand(string id, string type, string replayPolicy, string intentId, string leaseToken);

    private sealed record PolledAck(string commandId, string leaseToken, bool ok, string detail);

    // The exact loop body both pollers share: gate -> (only if admitted) side effect -> record -> ack.
    private sealed class PollerHarness
    {
        private readonly RemoteCommandProtocol.IntentLedger _commandIntents = new();
        public List<string> SideEffects { get; } = new();
        public List<PolledAck> Acks { get; } = new();

        public void Deliver(params PolledCommand[] batch)
        {
            foreach (var c in batch)
            {
                if (string.IsNullOrEmpty(c.id)) continue;
                (bool ok, string detail) res;
                var admission = _commandIntents.Admit(c.type, c.replayPolicy, c.intentId, c.leaseToken, out var gated);
                if (admission != RemoteCommandAdmission.Execute)
                {
                    res = gated;
                }
                else
                {
                    // stand-ins for FetchAndInsertAsync / StartMuxHeadless*: reaching here IS the side effect
                    SideEffects.Add($"{c.type}:{c.intentId}");
                    res = (true, $"{c.type} executed");
                }
                if (admission == RemoteCommandAdmission.Execute) _commandIntents.Record(c.intentId, res.ok, res.detail);
                Acks.Add(new PolledAck(c.id, c.leaseToken, res.ok, res.detail));
            }
        }
    }

    private static PolledCommand Fenced(string id, string type, string intentId, string leaseToken)
        => new(id, type, "intent-fenced", intentId, leaseToken);

    [DataTestMethod]
    [DataRow("fetchfile")]
    [DataRow("startmux")]
    [DataRow("mirrorlocal")]
    public void Poller_RefusesAnIntentFencedCommandWithNoLeaseToken_NoSideEffect(string type)
    {
        var poller = new PollerHarness();

        poller.Deliver(Fenced("c1", type, "intent-1", ""));

        Assert.AreEqual(0, poller.SideEffects.Count, "a leaseless command must not reach the handler");
        Assert.AreEqual(1, poller.Acks.Count, "the relay still has to be told, or the command is stuck");
        Assert.IsFalse(poller.Acks[0].ok);
        StringAssert.Contains(poller.Acks[0].detail, "lease token");
    }

    [DataTestMethod]
    [DataRow("fetchfile")]
    [DataRow("startmux")]
    [DataRow("mirrorlocal")]
    public void Poller_RefusesAnIntentFencedCommandWithNoIntentId_NoSideEffect(string type)
    {
        var poller = new PollerHarness();

        // "" is what the DTOs deserialize to when the field is absent — an old relay or a hand-rolled body
        poller.Deliver(Fenced("c1", type, "", "lease-1"), Fenced("c2", type, "  ", "lease-1"), Fenced("c3", type, "bad intent", "lease-1"));

        Assert.AreEqual(0, poller.SideEffects.Count, "an intent-less command must not reach the handler");
        Assert.AreEqual(3, poller.Acks.Count);
        Assert.IsTrue(poller.Acks.TrueForAll(a => !a.ok), "every refusal must ack as failed");
        StringAssert.Contains(poller.Acks[0].detail, "intent id");
        StringAssert.Contains(poller.Acks[2].detail, "malformed");
    }

    [DataTestMethod]
    [DataRow("fetchfile")]
    [DataRow("startmux")]
    [DataRow("mirrorlocal")]
    public void Poller_ExecutesOnceForAValidEnvelope_AndDedupsARedelivery(string type)
    {
        var poller = new PollerHarness();

        // the relay re-leases an unacked command under a FRESH lease token but the SAME intent id
        poller.Deliver(Fenced("c1", type, "intent-1", "lease-1"));
        poller.Deliver(Fenced("c2", type, "intent-1", "lease-2"));

        CollectionAssert.AreEqual(new[] { $"{type}:intent-1" }, poller.SideEffects, "the redelivery must not run a second time");
        Assert.AreEqual(2, poller.Acks.Count, "the duplicate still gets acked, or the relay retries forever");
        Assert.IsTrue(poller.Acks[1].ok, "the dedup ack replays the original outcome");
        Assert.AreEqual(poller.Acks[0].detail, poller.Acks[1].detail);
        Assert.AreEqual("lease-2", poller.Acks[1].leaseToken, "the ack echoes THIS delivery's lease, not the original");
    }

    [TestMethod]
    public void Poller_KeepsRunningIdempotentCommandsAndRefusesAWrongPolicy()
    {
        var poller = new PollerHarness();

        poller.Deliver(
            new PolledCommand("c1", "rename", "idempotent", "", ""),          // no envelope needed
            new PolledCommand("c2", "rename", "idempotent", "", ""),          // and redelivery may re-run
            new PolledCommand("c3", "fetchfile", "idempotent", "i", "l"),     // fenced type, wrong policy
            new PolledCommand("c4", "future-mutation", "idempotent", "i", "l"));

        CollectionAssert.AreEqual(new[] { "rename:", "rename:" }, poller.SideEffects);
        Assert.IsFalse(poller.Acks[2].ok);
        StringAssert.Contains(poller.Acks[2].detail, "replay policy");
        Assert.IsFalse(poller.Acks[3].ok);
    }

    // ---- fetchfile contract regression -------------------------------------------------------------

    // RemoteUploadTransfer.InsertDownloadedPathAsync: an insert-mode-less fetchfile reports the download in
    // its status Detail and must NEVER push a prompt into a mux tab. The envelope gate sits in front of this
    // path, so it is exactly the behaviour a valid-envelope fetchfile is allowed to have.
    [TestMethod]
    public async Task FetchFile_WithoutAnInsertMode_ReportsTheDownloadAndInsertsNothing()
    {
        var muxCalls = 0;
        Task<string> MuxRequest(object _) { muxCalls++; return Task.FromResult("{\"ok\":true}"); }

        foreach (var insert in new string?[] { null, "", "   " })
        {
            var result = await RemoteUploadTransfer.InsertDownloadedPathAsync(
                Path.Combine(Path.GetTempPath(), "report.pdf"),
                "report.pdf",
                "mux-tab-a",
                insert,
                "intent-1",
                MuxRequest);

            Assert.IsTrue(result.Ok);
            Assert.AreEqual("downloaded to PC", result.Detail, "the Detail IS the user-facing status for a no-insert fetch");
            Assert.IsTrue(result.OnPc);
        }
        Assert.AreEqual(0, muxCalls, "a fetchfile with no insert mode must never write to a mux tab");
    }

    // ---- the shipped pollers really are gated this way ---------------------------------------------

    [DataTestMethod]
    [DataRow("native/CodexLocalRetrieval.Native/MainPage.Remote.cs", "private async Task PollCommandsAsync")]
    // Task<bool>: the drain reports whether it found work so the loop can back its cadence off when idle.
    [DataRow("native/CodexLocalRetrieval.Core/Remote/RemoteBridge.cs", "private async Task<bool> PollAndProcessAsync")]
    public void BothPollers_GateOnTheIntentLedgerBeforeAnySideEffect(string relativePath, string pollerSignature)
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var start = source.IndexOf(pollerSignature, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"{relativePath} no longer declares {pollerSignature}");
        var poller = source[start..];

        var admit = poller.IndexOf("_commandIntents.Admit(c.type, c.replayPolicy, c.intentId, c.leaseToken", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, admit, "the poller must run the full envelope gate, not just a policy check");

        foreach (var sideEffect in new[] { "RemoteUploadTransfer.", "StartMuxHeadless", "MirrorLocal" })
        {
            var handler = poller.IndexOf(sideEffect, StringComparison.Ordinal);
            Assert.IsGreaterThanOrEqualTo(0, handler, $"{sideEffect} handler is missing from the poller");
            Assert.IsTrue(handler > admit, $"{sideEffect} must run AFTER the envelope gate, never before it");
        }

        var record = poller.IndexOf("_commandIntents.Record(", StringComparison.Ordinal);
        var ack = poller.IndexOf("await AckCommandAsync(", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, record, "the outcome must be recorded so a redelivery can replay it");
        Assert.IsTrue(record > admit && ack > record, "record the outcome after execution and before the ack");
        StringAssert.Contains(poller[admit..ack], "RemoteCommandAdmission.Execute", "only an Execute admission may run a handler");

        // the bare policy check is no longer a gate on its own anywhere in the poller
        Assert.IsFalse(
            poller[..ack].Contains("RemoteCommandProtocol.IsReplaySafe", StringComparison.Ordinal),
            "IsReplaySafe alone must not gate a polled command — it proves semantics, not currency");
    }

    // A polled start must never substitute a freshly minted local intent: muxd would see a brand-new intent
    // on every redelivery and its dedup would be defeated.
    [TestMethod]
    public void RemoteStartMux_NeverMintsALocalIntent()
    {
        var remote = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"));

        StringAssert.Contains(remote, "allowLocalIntentMint: false", "the polled startmux path must opt out of minting");
        var guard = remote.IndexOf("if (!allowLocalIntentMint && !RemoteCommandProtocol.IsWellFormedEnvelopeToken(intentId))", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, guard, "the raw mux-create must refuse a mint-less start with no usable intent");

        var mint = remote.IndexOf("RemoteCommandProtocol.NewIntent(", guard, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, mint, "the GUI-originated start still mints its own intent");
        Assert.IsTrue(mint > guard, "the refusal must come BEFORE the mint, or the remote path mints anyway");
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
        throw new DirectoryNotFoundException("Could not find CodexLocalRetrieval.sln from " + AppContext.BaseDirectory);
    }
}
