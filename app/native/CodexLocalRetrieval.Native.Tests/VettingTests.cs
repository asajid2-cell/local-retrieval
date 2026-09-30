using System.Text.Json;
using System.Text.Json.Nodes;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The lifecycle: everything lands in the general populace (unvetted), vetting promotes a chat to Active
// with a name and a phrase of its own, and archiving retires it. These cover the three buckets, the vet
// gate itself, the phrase-scheme changeover, and the remote discovery API that must keep meaning what it
// meant before the tiers existed.
[TestClass]
public sealed class VettingTests
{
    // Each test gets its own directory: the store, its pre-detach backups and any scratch transcripts all
    // live under it, so cleanup is one recursive delete and nothing leaks into the shared temp directory.
    private static ArchiveService TempService(out string root)
    {
        root = Path.Combine(Path.GetTempPath(), "clr-vet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return new ArchiveService(storePath: Path.Combine(root, "app-store.json"), enableTranscriptSearchIndex: false);
    }

    private static ArchiveSession Chat(string id, bool vetted = false, bool archived = false) => new()
    {
        Id = id,
        Title = id,
        Tool = "claude",
        SourcePath = "",
        CreatedAt = "2026-08-01T00:00:00Z",
        UpdatedAt = "2026-08-01T00:00:00Z",
        UserMessageCount = 5,
        MessageCount = 20,
        Vetted = vetted,
        Archived = archived,
    };

    // The scope tabs set an explicit tier; nothing else does, so "" must keep its historical meaning
    // (the whole live store) rather than silently becoming "vetted only".
    private static string[] Scoped(ArchiveService svc, string scope) =>
        svc.FilterChats(new ChatFilter { Archived = scope, ShowHidden = true })
            .Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    [TestMethod]
    public void ScopeTiers_AreDisjointAndSumToTheWholeStore()
    {
        var svc = TempService(out var root);
        try
        {
            svc.Store.Sessions["pile"] = Chat("pile");
            svc.Store.Sessions["kept"] = Chat("kept", vetted: true);
            svc.Store.Sessions["old"] = Chat("old", vetted: true, archived: true);

            CollectionAssert.AreEqual(new[] { "pile" }, Scoped(svc, "unvetted"), "the general populace is what nobody has vetted");
            CollectionAssert.AreEqual(new[] { "kept" }, Scoped(svc, "active"), "Active is what has been vetted");
            CollectionAssert.AreEqual(new[] { "old" }, Scoped(svc, "archived"), "Archived is what was retired");
            CollectionAssert.AreEqual(new[] { "kept", "old", "pile" }, Scoped(svc, "all"), "all keeps every tier");

            var counts = svc.ChatScopeCounts();
            Assert.AreEqual((1, 1, 1), counts, "each chat is counted in exactly one tier");
            Assert.AreEqual(svc.Store.Sessions.Count, counts.Unvetted + counts.Active + counts.Archived,
                "the tiers sum to the whole store - that is the point of the tabs");
        }
        finally { Directory.Delete(root, true); }
    }

    // The number on a tab has to be the number of rows under it. The Archived scope listed NOTHING while
    // its label counted the retired chats, because the list itself discarded archived rows on the way in.
    [TestMethod]
    public async Task TheArchivedTabListsTheChatsItsNumberCounts()
    {
        var svc = TempService(out var root);
        try
        {
            var retired = Chat("retired");
            svc.Store.Sessions[retired.Id] = retired;
            await svc.ArchiveSessionAsync(retired);   // exactly what the Archive action does

            var counts = svc.ChatScopeCounts();
            Assert.AreEqual(1, counts.Archived, "the retired chat is counted on the Archived tab");

            var rows = svc.FilterChats(new ChatFilter { Archived = "archived" });
            Assert.AreEqual(counts.Archived, rows.Count, "the tab's number and its rows agree");

            svc.RefreshSessions(rows);   // the path the chat list takes when the scope changes
            CollectionAssert.AreEqual(new[] { "retired" }, svc.Sessions.Select(s => s.Id).ToArray(),
                "the retired chat reaches the bound list - the Archived tab is not a dead tab");
        }
        finally { Directory.Delete(root, true); }
    }

    // A vetted chat is a deliberate keep in its own right, not only through its phrase: the phrase can be
    // taken away (the old-scheme retirement does exactly that) while the chat stays promoted.
    [TestMethod]
    public void AVettedChatIsNeverAutoHidden()
    {
        var svc = TempService(out var root);
        try
        {
            ArchiveSession Probe(string id, bool vetted) => new()
            {
                Id = id,
                Title = "Identity Check",
                Tool = "claude",
                FirstUserMessage = "Reply with exactly GATEWAY_IDENTITY_OK.",
                UserMessageCount = 1,
                MessageCount = 2,
                Vetted = vetted,
            };
            var kept = Probe("kept", vetted: true);
            var probe = Probe("probe", vetted: false);
            svc.Store.Sessions[kept.Id] = kept;
            svc.Store.Sessions[probe.Id] = probe;

            Assert.IsTrue(ArchiveService.IsLowSignalChat(probe), "the same shape, unvetted, is still hidden");
            Assert.IsFalse(ArchiveService.IsLowSignalChat(kept), "vetting is a keep, whether or not it has a phrase");

            CollectionAssert.AreEqual(new[] { "kept" },
                svc.FilterChats(new ChatFilter { Archived = "active" }).Select(s => s.Id).ToArray(),
                "what the Active tab counts is what it lists");
            Assert.AreEqual(svc.ChatScopeCounts().Active,
                svc.FilterChats(new ChatFilter { Archived = "active" }).Count,
                "the number on the Active tab and its rows are the same chat");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void AVettedAutomationWorkerIsNotHiddenEither()
    {
        var svc = TempService(out var root);
        try
        {
            ArchiveSession Worker(string id, bool vetted) => new()
            {
                Id = id,
                Title = "Worker result",
                Tool = "claude",
                FirstUserMessage = "[ORCH-WORKER campaign=demo node=kept]",
                UserMessageCount = 3,
                MessageCount = 12,
                Vetted = vetted,
            };
            var kept = Worker("kept", vetted: true);
            var hidden = Worker("auto", vetted: false);
            svc.Store.Sessions[kept.Id] = kept;
            svc.Store.Sessions[hidden.Id] = hidden;

            Assert.IsTrue(ArchiveService.ShouldAutoHideAutomationWorker(hidden));
            Assert.IsFalse(ArchiveService.ShouldAutoHideAutomationWorker(kept), "a vetted worker was kept on purpose");
        }
        finally { Directory.Delete(root, true); }
    }

    // The name typed at the gate must be the name the chat lists under even when the tool's own transcript
    // cannot take it (a live or unverified session defers the write) - the tool's title is stale then, and a
    // stale title on screen reads as a vet that did not save.
    [TestMethod]
    public async Task Vet_WithNoTranscriptToWrite_StillListsTheTypedName()
    {
        var svc = TempService(out var root);
        try
        {
            var session = Chat("vet-nofile");
            session.Title = "auto derived title";
            svc.Store.Sessions[session.Id] = session;

            var (vetted, status) = await svc.VetSessionAsync(session, "Chosen Name", "brave green apple pudding");

            Assert.IsTrue(vetted);
            Assert.IsFalse(ArchiveService.NativeRenameSucceeded(status), "nothing was written to the tool");
            Assert.AreEqual("auto derived title", session.Title, "the tool's own name is not ours to invent");
            Assert.AreEqual("Chosen Name", session.CustomTitle, "the typed name is kept as the app name");
            Assert.AreEqual("Chosen Name", session.RowName, "and it is what the row - and the header - show");
            Assert.AreEqual("Chosen Name", session.ListTitle);
        }
        finally { Directory.Delete(root, true); }
    }

    // The default must not narrow: every caller that never named a tier (the tag-filter wrapper, the
    // remote API with no archived= parameter) has always meant "everything live".
    [TestMethod]
    public void DefaultScope_KeepsEveryLiveChatAndStillExcludesRetired()
    {
        var svc = TempService(out var root);
        try
        {
            svc.Store.Sessions["pile"] = Chat("pile");
            svc.Store.Sessions["kept"] = Chat("kept", vetted: true);
            svc.Store.Sessions["old"] = Chat("old", vetted: true, archived: true);

            CollectionAssert.AreEqual(new[] { "kept", "pile" },
                svc.FilterChats(new ChatFilter { ShowHidden = true }).Select(s => s.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                "no tier chosen still means everything live, vetted included");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Vet_WritesTheChatsOwnNameAndPhrase_AndPromotesItToActive()
    {
        var svc = TempService(out var root);
        var transcript = Path.Combine(root, "session.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\",\"entrypoint\":\"cli\",\"message\":{\"content\":\"hi\"}}\n");
        var previousSources = RunningSessions.ScanSourceOverride;
        try
        {
            var session = Chat("vet-1");
            session.SourcePath = transcript;
            session.Title = "untitled";
            svc.Store.Sessions[session.Id] = session;

            (bool Vetted, string? RenameStatus) result;
            try
            {
                RunningSessions.ScanSourceOverride = new(
                    Scan: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
                    ClaudeRegistry: _ => (true, new Dictionary<string, int>(), new HashSet<int>(), ""),
                    OpenTranscripts: _ => (true, new Dictionary<string, int>(), new HashSet<int>(), ""));
                result = await svc.VetSessionAsync(session, "  Mux Work Chat  ", "brave green apple pudding");
            }
            finally
            {
                RunningSessions.ScanSourceOverride = previousSources;
                RunningSessions.InvalidateScanCache();
            }

            Assert.IsTrue(result.Vetted, "a name and a phrase is all vetting asks for");
            Assert.AreEqual("Mux Work Chat", session.Title, "the typed name is the chat's OWN name");
            Assert.AreEqual("", session.CustomTitle, "the app name is left alone - vetting writes the native name");
            CollectionAssert.AreEqual(new[] { "brave green apple pudding" }, session.SpecialPhrases.ToArray());
            Assert.IsTrue(session.Vetted);
            Assert.IsFalse(session.Archived, "vetting promotes out of the populace, it does not retire");
            Assert.IsTrue(File.ReadAllText(transcript).Contains("\"customTitle\":\"Mux Work Chat\""),
                "the tool's own resume list reads the same name");
            CollectionAssert.AreEqual(new[] { "vet-1" }, Scoped(svc, "active"));
            CollectionAssert.AreEqual(Array.Empty<string>(), Scoped(svc, "unvetted"), "the chat left the general populace");
            Assert.IsTrue((result.RenameStatus ?? "").Contains("Claude", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            RunningSessions.ScanSourceOverride = previousSources;
            RunningSessions.InvalidateScanCache();
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task Vet_RefusesWithoutANameOrAPhrase()
    {
        var svc = TempService(out var root);
        try
        {
            var session = Chat("vet-2");
            session.Title = "untouched";
            svc.Store.Sessions[session.Id] = session;

            var noName = await svc.VetSessionAsync(session, "   ", "brave green apple pudding");
            Assert.IsFalse(noName.Vetted, "the gate is a name AND a phrase");
            Assert.IsFalse(session.Vetted, "a refused vet promotes nothing");
            CollectionAssert.AreEqual(Array.Empty<string>(), Scoped(svc, "active"));

            var noPhrase = await svc.VetSessionAsync(session, "A Name", "  ");
            Assert.IsFalse(noPhrase.Vetted);
            Assert.IsFalse(session.Vetted);
            Assert.AreEqual("untouched", session.Title, "a refused vet writes nothing to the chat either");
            Assert.AreEqual(0, session.SpecialPhrases.Count, "and stamps no phrase on it");
        }
        finally { Directory.Delete(root, true); }
    }

    // The tool's store can refuse a native rename (a live or unverified transcript defers it). The vet
    // must still land, and the typed name must not vanish - it is kept as the app name.
    [TestMethod]
    public async Task Vet_KeepsTheTypedNameAsTheAppName_WhenTheToolStoreRefuses()
    {
        var svc = TempService(out var root);
        var transcript = Path.Combine(root, "held.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}\n");
        try
        {
            var session = Chat("vet-3");
            session.SourcePath = transcript;
            session.Title = "native original";
            svc.Store.Sessions[session.Id] = session;

            (bool Vetted, string? RenameStatus) result;
            using (new FileStream(transcript, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
                result = await svc.VetSessionAsync(session, "Kept In The App", "shy red raisin bread");

            Assert.IsTrue(result.Vetted, "the vet lands even when the tool's side refuses the write");
            Assert.IsFalse(ArchiveService.NativeRenameSucceeded(result.RenameStatus), "the status says the write was refused");
            Assert.AreEqual("Kept In The App", session.CustomTitle, "the typed name is kept as the app name, never dropped");
            Assert.AreEqual("native original", session.Title, "the native name is untouched when the write failed");
            Assert.IsTrue(session.Vetted);
            Assert.AreEqual("shy red raisin bread", session.SpecialPhrases.Single());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task Unvet_SendsAChatBackToTheGeneralPopulace()
    {
        var svc = TempService(out var root);
        try
        {
            var session = Chat("back", vetted: true);
            svc.Store.Sessions[session.Id] = session;

            await svc.UnvetSessionAsync(session);

            Assert.IsFalse(session.Vetted);
            Assert.IsFalse(session.Archived, "unvetting a retired chat brings it back into play too");
            CollectionAssert.AreEqual(new[] { "back" }, Scoped(svc, "unvetted"), "a mis-vet is not a one-way door");
        }
        finally { Directory.Delete(root, true); }
    }

    // The app is not the only writer of the live store: the retrieval server keeps the same file and syncs
    // its own parse into it. A vet must not be lost - or worse, clobber that sync - just because one landed
    // between this process's load and its save, so the service re-reads and re-applies. (Seen live: the
    // first real vet in the app was refused with "the store changed in another process".)
    [TestMethod]
    public async Task Vet_SurvivesAStoreWriteFromAnotherWriter()
    {
        var svc = TempService(out var root);
        try
        {
            var session = Chat("mine");
            session.Title = "untitled";
            svc.Store.Sessions[session.Id] = session;
            await svc.SaveAsync();

            // Stand in for the server's sync: bump the generation and add a chat this process has not seen.
            var storePath = Path.Combine(root, "app-store.json");
            var json = JsonDocument.Parse(File.ReadAllText(storePath));
            var generation = json.RootElement.GetProperty("generation").GetInt64();
            var foreign = new ArchiveSession { Id = "theirs", Title = "theirs", Tool = "claude" };
            var rewritten = JsonNode.Parse(File.ReadAllText(storePath))!.AsObject();
            rewritten["generation"] = generation + 1;
            rewritten["sessions"]!.AsObject()["theirs"] = JsonSerializer.SerializeToNode(foreign);
            File.WriteAllText(storePath, rewritten.ToJsonString());

            var (vetted, _) = await svc.VetSessionAsync(session, "Kept Name", "brave green apple pudding");

            Assert.IsTrue(vetted, "the vet lands even though another writer saved the store underneath it");
            var live = svc.Store.Sessions["mine"];
            Assert.IsTrue(live.Vetted);
            // This chat has no transcript on disk, so the tool's own store cannot take the name. The typed
            // name is kept as the app name - and it must survive the re-read, i.e. the retry re-applied the
            // whole change to the FRESH instance rather than only bumping Vetted on it.
            Assert.AreEqual("Kept Name", live.CustomTitle, "the retry re-applies the change, name included");
            CollectionAssert.AreEqual(new[] { "brave green apple pudding" }, live.SpecialPhrases.ToArray());
            Assert.IsTrue(svc.Store.Sessions.ContainsKey("theirs"),
                "the other writer's work is re-read, never clobbered by the retry");
        }
        finally { Directory.Delete(root, true); }
    }

    // A vet is app-only state: nothing in the transcript records the tier or the phrase. The sync re-parses
    // every dirty transcript and hands the store a FRESH session for that id, so anything the re-parse does
    // not carry over is silently dropped. This is the live failure: the vet reported success, and the very
    // next sync of that chat - dirtied by the app writing its own name into the transcript - put it back in
    // the general populace with no phrase.
    [TestMethod]
    public async Task Vet_SurvivesTheNextRescanOfThatChat()
    {
        var svc = TempService(out var root);
        var transcript = Path.Combine(root, "rescan.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\",\"entrypoint\":\"cli\",\"message\":{\"content\":\"hi\"}}\n");
        var previousSources = RunningSessions.ScanSourceOverride;
        try
        {
            var session = Chat("rescan");
            session.SourcePath = transcript;
            session.Title = "untitled";
            svc.Store.Sessions[session.Id] = session;

            try
            {
                RunningSessions.ScanSourceOverride = new(
                    Scan: () => (true, new List<ArchiveService.RunningSessionInfo>(), ""),
                    ClaudeRegistry: _ => (true, new Dictionary<string, int>(), new HashSet<int>(), ""),
                    OpenTranscripts: _ => (true, new Dictionary<string, int>(), new HashSet<int>(), ""));
                var (vetted, _) = await svc.VetSessionAsync(session, "Kept Through Rescan", "shy red raisin bread");
                Assert.IsTrue(vetted);
            }
            finally { RunningSessions.ScanSourceOverride = previousSources; }

            // The re-parse: a brand-new instance for the same id and file, exactly as the scanner builds it.
            var reparsed = new ArchiveSession
            {
                Id = "rescan",
                Title = "Kept Through Rescan",   // the name the transcript now carries
                Tool = "claude",
                SourcePath = transcript,
                CreatedAt = "2026-08-01T00:00:00Z",
                UpdatedAt = "2026-08-01T00:00:00Z",
                UserMessageCount = 6,
                MessageCount = 24,
            };
            await svc.MergeScanAsync(new DiskScan(new List<ArchiveSession> { reparsed }, new List<ArchiveSession>()), refreshList: false);

            var live = svc.Store.Sessions["rescan"];
            Assert.IsTrue(live.Vetted, "a rescan of a vetted chat does not un-vet it");
            CollectionAssert.AreEqual(new[] { "shy red raisin bread" }, live.SpecialPhrases.ToArray(),
                "and does not drop its phrase");
            CollectionAssert.AreEqual(new[] { "rescan" }, Scoped(svc, "active"),
                "it is still the tier the user put it in");
            CollectionAssert.AreEqual(Array.Empty<string>(), Scoped(svc, "unvetted"),
                "a rescan never sends a vetted chat back to the general populace");
        }
        finally
        {
            RunningSessions.ScanSourceOverride = previousSources;
            RunningSessions.InvalidateScanCache();
            Directory.Delete(root, true);
        }
    }

    // Retiring straight from the pile still counts as the LAST tier only - a chat is in one bucket, never two.
    [TestMethod]
    public async Task Archive_FromTheGeneralPopulace_CountsAsRetiredNotBoth()
    {
        var svc = TempService(out var root);
        try
        {
            var session = Chat("straight-out");
            svc.Store.Sessions[session.Id] = session;

            await svc.ArchiveSessionAsync(session);

            Assert.IsTrue(session.Archived);
            Assert.IsTrue(session.Vetted, "retired is past vetted, so it cannot also be in the pile");
            CollectionAssert.AreEqual(new[] { "straight-out" }, Scoped(svc, "archived"));
            CollectionAssert.AreEqual(Array.Empty<string>(), Scoped(svc, "unvetted"));
            Assert.AreEqual((0, 0, 1), svc.ChatScopeCounts());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DetachLegacyPhrases_ParksTheOldHandles_AndLeavesGeneratedPhrasesAlone()
    {
        var svc = TempService(out var root);
        try
        {
            var a = Chat("a");
            a.SpecialPhrases.Add(".mux");
            var b = Chat("b");
            b.SpecialPhrases.Add(".mux");
            b.SpecialPhrases.Add("petunia");
            var c = Chat("c");
            c.SpecialPhrases.Add("brave green apple pudding");
            svc.Store.Sessions["a"] = a; svc.Store.Sessions["b"] = b; svc.Store.Sessions["c"] = c;
            await svc.SaveAsync();   // the live store always exists; the backup copies it

            var (phrases, chats, backup) = await svc.DetachLegacyPhrasesAsync();

            Assert.AreEqual(2, phrases, "the two old handles, counted once each");
            Assert.AreEqual(2, chats, "the chats that carried one came out clean");
            Assert.IsTrue(File.Exists(backup), "the store is copied before a bulk rewrite");
            CollectionAssert.AreEquivalent(new[] { ".mux", "petunia" },
                svc.Store.LegacyPhrases.Select(g => g.Phrase).ToArray());
            var mux = svc.Store.LegacyPhrases.Single(g => g.Phrase == ".mux");
            CollectionAssert.AreEquivalent(new[] { "a", "b" }, mux.SessionIds, "the kept list remembers which chats it covered");
            Assert.IsFalse(string.IsNullOrEmpty(mux.DetachedAt), "the group records when it was retired");

            CollectionAssert.AreEqual(Array.Empty<string>(), a.SpecialPhrases.ToArray(), "the old handle comes off the chat");
            CollectionAssert.AreEqual(Array.Empty<string>(), b.SpecialPhrases.ToArray(), "both of its old handles come off");
            CollectionAssert.AreEqual(new[] { "brave green apple pudding" }, c.SpecialPhrases.ToArray(),
                "a phrase the new scheme could have issued is left where it is");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DetachLegacyPhrases_MergesIntoGroupsARunAlreadyParked()
    {
        var svc = TempService(out var root);
        try
        {
            var a = Chat("a");
            a.SpecialPhrases.Add(".mux");
            svc.Store.Sessions["a"] = a;
            await svc.SaveAsync();
            Assert.AreEqual(1, (await svc.DetachLegacyPhrasesAsync()).Phrases);

            // A chat that was closed while the app was running still carries the old handle.
            var late = Chat("late");
            late.SpecialPhrases.Add(".mux");
            svc.Store.Sessions["late"] = late;

            var second = await svc.DetachLegacyPhrasesAsync();

            Assert.AreEqual(1, second.Phrases);
            Assert.AreEqual(1, svc.Store.LegacyPhrases.Count, "a second run merges rather than duplicating");
            CollectionAssert.AreEquivalent(new[] { "a", "late" }, svc.Store.LegacyPhrases.Single().SessionIds);
            CollectionAssert.AreEqual(Array.Empty<string>(), late.SpecialPhrases.ToArray());
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public async Task DetachLegacyPhrases_WithNothingOld_ReportsNothingAndTakesNoBackup()
    {
        var svc = TempService(out var root);
        try
        {
            var a = Chat("a");
            a.SpecialPhrases.Add("brave green apple pudding");
            svc.Store.Sessions["a"] = a;
            await svc.SaveAsync();

            var (phrases, chats, backup) = await svc.DetachLegacyPhrasesAsync();

            Assert.AreEqual((0, 0, ""), (phrases, chats, backup), "nothing old means nothing done, and no copy to explain");
            CollectionAssert.AreEqual(new[] { "brave green apple pudding" }, a.SpecialPhrases.ToArray());
            Assert.AreEqual(0, Directory.GetFiles(root, "*.pre-legacy-phrases-*.json").Length, "no backup for a no-op");
        }
        finally { Directory.Delete(root, true); }
    }

    // Seen live: the retirement ran while a sync had loaded a newer store underneath it, so the save hit a
    // generation conflict, the exception escaped the click handler, and the phrases stayed exactly where
    // they were - a confirmed action that did nothing and said nothing. It now takes the swap gate and
    // retries against the store that won.
    [TestMethod]
    public async Task DetachLegacyPhrases_ReloadsAndRetriesAfterGenerationConflict()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-vet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = Path.Combine(root, "app-store.json");
            var seed = new ArchiveService(storePath: store, enableTranscriptSearchIndex: false);
            var a = Chat("a");
            a.SpecialPhrases.Add(".mux");
            seed.Store.Sessions["a"] = a;
            await seed.SaveAsync();

            var desktop = new ArchiveService(storePath: store, enableTranscriptSearchIndex: false);
            var server = new ArchiveService(storePath: store, enableTranscriptSearchIndex: false);
            await desktop.LoadAsync();
            await server.LoadAsync();

            // The other writer advances the store: the retirement's first save is now stale.
            desktop.Store.Sessions["desktop-change"] = Chat("desktop-change");
            await desktop.SaveAsync();

            var (phrases, chats, _) = await server.DetachLegacyPhrasesAsync();

            Assert.AreEqual((1, 1), (phrases, chats), "the retry detaches the old handle the fresh store still carries");
            var reader = new ArchiveService(storePath: store, enableTranscriptSearchIndex: false);
            await reader.LoadAsync();
            Assert.AreEqual(".mux", reader.Store.LegacyPhrases.Single().Phrase);
            CollectionAssert.AreEquivalent(new[] { "a" }, reader.Store.LegacyPhrases.Single().SessionIds);
            CollectionAssert.AreEqual(Array.Empty<string>(), reader.Store.Sessions["a"].SpecialPhrases.ToArray());
            Assert.IsTrue(reader.Store.Sessions.ContainsKey("desktop-change"),
                "the reload the retry takes must not throw away the other writer's change");
        }
        finally { Directory.Delete(root, true); }
    }

    // The tier and the retired handles are store state: they must survive a save/reload, and a store
    // written before any of this existed must read every chat as part of the general populace.
    [TestMethod]
    public void Store_RoundTripsTheTierAndTheRetiredHandles()
    {
        var session = Chat("rt", vetted: true);
        session.SpecialPhrases.Add("brave green apple pudding");
        var data = new AppStoreData();
        data.Sessions["rt"] = session;
        data.LegacyPhrases.Add(new LegacyPhraseGroup { Phrase = ".mux", SessionIds = { "rt" }, DetachedAt = "2026-09-01T00:00:00Z" });
        data.PhraseCategories.Add(new PhraseCategory { Phrase = "brave green apple pudding", Note = "mux work", SavedAt = "2026-09-01T00:00:00Z" });

        var reloaded = JsonSerializer.Deserialize<AppStoreData>(JsonSerializer.Serialize(data))!;

        Assert.IsTrue(reloaded.Sessions["rt"].Vetted, "the tier survives a reload");
        CollectionAssert.AreEqual(new[] { "brave green apple pudding" }, reloaded.Sessions["rt"].SpecialPhrases.ToArray());
        Assert.AreEqual(".mux", reloaded.LegacyPhrases.Single().Phrase);
        CollectionAssert.AreEqual(new[] { "rt" }, reloaded.LegacyPhrases.Single().SessionIds);
        Assert.AreEqual("mux work", reloaded.PhraseCategories.Single().Note);

        var older = JsonSerializer.Deserialize<AppStoreData>("{\"sessions\":{\"legacy\":{\"id\":\"legacy\"}}}")!;
        Assert.IsFalse(older.Sessions["legacy"].Vetted, "a chat from a store written before the tiers is in the general populace");
        Assert.AreEqual(0, older.LegacyPhrases.Count);
    }

    // The remote discovery API's archived= vocabulary predates the tiers: "active" there has always meant
    // a LIVE chat. The tiers must not have silently narrowed it to vetted-only.
    [TestMethod]
    public void RemoteDiscovery_KeepsItsOwnScopeVocabulary()
    {
        var svc = TempService(out var root);
        try
        {
            svc.Store.Sessions["pile"] = Chat("pile");
            svc.Store.Sessions["kept"] = Chat("kept", vetted: true);
            svc.Store.Sessions["old"] = Chat("old", vetted: true, archived: true);
            var api = new DiscoveryApi(svc, _ => true);

            var byDefault = api.Chats(new DiscoveryQuery(ShowHidden: true));
            CollectionAssert.AreEquivalent(new[] { "pile", "kept" },
                byDefault.Rows.Select(r => r.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray(),
                "an unspecified archived= is still every live chat, vetted included");

            var active = api.Chats(new DiscoveryQuery(Archived: "active", ShowHidden: true));
            CollectionAssert.AreEquivalent(new[] { "pile", "kept" }, active.Rows.Select(r => r.Id).ToArray());

            var all = api.Chats(new DiscoveryQuery(Archived: "all", ShowHidden: true));
            CollectionAssert.AreEquivalent(new[] { "pile", "kept", "old" }, all.Rows.Select(r => r.Id).ToArray());

            var retired = api.Chats(new DiscoveryQuery(Archived: "archived", ShowHidden: true));
            CollectionAssert.AreEquivalent(new[] { "old" }, retired.Rows.Select(r => r.Id).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
}
