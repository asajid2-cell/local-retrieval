using System.Globalization;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchiveServiceTests
{
    private static string WriteRollout(string dir, string fileName, string id, string isoTimestamp, string userText)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllLines(path, new[]
        {
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"type\":\"session_meta\",\"cwd\":\"z:/proj\"}}",
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"" + userText + "\"}}",
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"on it\"}}",
        });
        return path;
    }

    // L1: indexing the rollout store resurfaces every session on disk, old and new alike.
    [TestMethod]
    public async Task IndexRoot_ResurfacesOldAndNewRollouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-idx-" + Guid.NewGuid().ToString("N"));
        WriteRollout(root, "rollout-old.jsonl", "old-1", "2026-01-02T00:00:00Z", "old work from january");
        WriteRollout(root, "rollout-new.jsonl", "new-1", "2026-06-14T00:00:00Z", "recent work");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var indexed = await service.IndexRootAsync(root);

            Assert.AreEqual(2, indexed);
            Assert.IsTrue(service.Store.Sessions.ContainsKey("old-1"), "the months-old january session must resurface");
            Assert.IsTrue(service.Store.Sessions.ContainsKey("new-1"));
        }
        finally { Directory.Delete(root, true); }
    }

    // L1 (machine proof): the real ~/.codex store resurfaces, including chats older than 30 days.
    [TestMethod]
    [TestCategory("RealStore")]
    public async Task SyncFromDisk_ResurfacesRealStoreIncludingMonthsOld()
    {
        if (!Directory.Exists(ArchiveService.DefaultCodexSessionsRoot))
        {
            Assert.Inconclusive("No ~/.codex/sessions on this machine.");
            return;
        }
        var store = Path.Combine(Path.GetTempPath(), "clr-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new ArchiveService(storePath: store);
            var indexed = await service.SyncFromDiskAsync();

            Assert.IsTrue(indexed >= 400, $"expected >=400 on-disk sessions resurfaced, got {indexed}");

            // "Months ago" proof, robust to date drift: the OLDEST codex rollout file on disk must
            // resurface as a session. (We assert on the file, not UpdatedAt, because
            // EnrichTitlesFromLocalState legitimately overrides UpdatedAt with Codex's own index dates.)
            var oldestFile = Directory.EnumerateFiles(ArchiveService.DefaultCodexSessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .First();
            var oldestAgeDays = (DateTime.UtcNow - oldestFile.LastWriteTimeUtc).TotalDays;
            Assert.IsTrue(oldestAgeDays >= 30, $"sanity: oldest on-disk chat should be months old (got {oldestAgeDays:F0}d)");
            Assert.IsTrue(
                service.Store.Sessions.Values.Any(s => string.Equals(s.SourcePath, oldestFile.FullName, StringComparison.OrdinalIgnoreCase)),
                $"the oldest on-disk chat ({oldestAgeDays:F0} days old) should resurface");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // L2: a chat resumes via `codex resume <id> --include-non-interactive` at its workspace cwd.
    [TestMethod]
    public void BuildResumeLaunch_TargetsCodexResumeAtWorkspace()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "abc-123", Workspace = cwd, SourcePath = Path.Combine(cwd, "x.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\codex.exe");

        Assert.AreEqual("C:\\codex.exe", launch.Exe);
        // Options before the positional session id so codex's [SESSION_ID] [PROMPT] never mis-parses.
        Assert.AreEqual("resume --include-non-interactive abc-123", launch.Arguments);
        Assert.AreEqual(cwd, launch.WorkingDirectory);
        Assert.AreEqual("\"C:\\codex.exe\" resume --include-non-interactive abc-123", launch.DisplayCommand);
    }

    // Review #4: a crafted/unsafe session id (e.g. a malicious filename) is refused, never injected.
    [TestMethod]
    public void BuildResumeLaunch_RefusesUnsafeSessionId()
    {
        var service = new ArchiveService(useBundledStore: true);
        var bad = service.BuildResumeLaunch(new ArchiveSession { Id = "x & calc", Tool = "codex", Workspace = Path.GetTempPath() });

        Assert.AreEqual("", bad.Exe, "unsafe id must be refused (empty Exe)");
        Assert.IsFalse(ArchiveService.IsResumableId("x & calc.jsonl"));
        Assert.IsFalse(ArchiveService.IsResumableId("a\" & start calc \""));
        Assert.IsTrue(ArchiveService.IsResumableId("019eb8f6-0f43-7f31-a284-f9da9cd5b3fa"));
        Assert.IsTrue(ArchiveService.IsResumableId("rollout-2026-06-15T22-39-19-019eceba"));
    }

    // Review #2: a parser-version migration prunes an orphan whose file now parses to a new id.
    [TestMethod]
    public async Task FullRescan_PrunesOrphanFromOldIdScheme()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-prune-" + Guid.NewGuid().ToString("N"));
        var path = WriteRollout(dir, "rollout-x.jsonl", "newid", "2026-06-14T00:00:00Z", "a chat");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "codex", Root = dir });
            // seed an orphan: same file, but an old-scheme id that the new parse no longer produces
            service.Store.Sessions["oldid"] = new ArchiveSession { Id = "oldid", SourcePath = path, Tool = "codex" };
            service.Store.Settings.IndexVersion = 0; // force a full rescan

            await service.SyncFromDiskAsync(refreshList: false);

            Assert.IsTrue(service.Store.Sessions.ContainsKey("newid"), "the file's current id is present");
            Assert.IsFalse(service.Store.Sessions.ContainsKey("oldid"), "the old-scheme orphan is pruned");
        }
        finally { Directory.Delete(dir, true); }
    }

    // N1: a Claude chat resumes with `claude --resume <id>`, not the codex form.
    [TestMethod]
    public void BuildResumeLaunch_RoutesClaudeSessionsToClaudeCli()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cl-9", Tool = "claude", Workspace = cwd, SourcePath = Path.Combine(cwd, "cl-9.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\claude.exe");

        Assert.AreEqual("C:\\claude.exe", launch.Exe);
        Assert.AreEqual("--resume cl-9", launch.Arguments);
        Assert.AreEqual(cwd, launch.WorkingDirectory);
    }

    private static string WriteClaudeSession(string dir, string fileName, string sessionId, string cwd, string isoTs, string userText)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var esc = cwd.Replace("\\", "\\\\");
        File.WriteAllLines(path, new[]
        {
            "{\"type\":\"user\",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"" + esc + "\",\"timestamp\":\"" + isoTs + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + userText + "\"}]}}",
            "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"" + esc + "\",\"timestamp\":\"" + isoTs + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"on it, here is the plan\"}]}}",
        });
        return path;
    }

    // N1/N2: a Claude source is parsed with the Claude shape and tagged tool=claude.
    [TestMethod]
    public async Task Sync_ParsesClaudeSource_AndTagsTool()
    {
        var claudeDir = Path.Combine(Path.GetTempPath(), "clr-claude-" + Guid.NewGuid().ToString("N"));
        WriteClaudeSession(claudeDir, "cl-1.jsonl", "cl-1", "z:\\proj", "2026-06-10T00:00:00Z", "help me ship the claude feature");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(claudeDir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "claude", Root = claudeDir });

            await service.SyncFromDiskAsync(refreshList: false);

            var s = service.Store.Sessions["cl-1"];
            Assert.AreEqual("claude", s.Tool);
            Assert.AreEqual("z:\\proj", s.Workspace);
            Assert.IsTrue(s.Messages.Count >= 2, "both claude messages should parse");
            StringAssert.Contains(s.Text, "ship the claude feature");
        }
        finally { Directory.Delete(claudeDir, true); }
    }

    // N3: a second scan with no file changes parses nothing (incremental skip).
    [TestMethod]
    public async Task Scan_IsIncremental_SkipsUnchangedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-incr-" + Guid.NewGuid().ToString("N"));
        WriteRollout(dir, "rollout-a.jsonl", "a-1", "2026-06-14T00:00:00Z", "first chat");
        WriteRollout(dir, "rollout-b.jsonl", "b-1", "2026-06-14T00:00:01Z", "second chat");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "codex", Root = dir });

            var first = await service.ScanDiskAsync();
            Assert.AreEqual(2, first.Disk.Count, "first scan parses both files");
            await service.MergeScanAsync(first, refreshList: false);

            var second = await service.ScanDiskAsync();
            Assert.AreEqual(0, second.Disk.Count, "second scan must skip unchanged files");
        }
        finally { Directory.Delete(dir, true); }
    }

    // L1 correctness (tandem review HIGH): a re-sync refreshes disk content but must NOT wipe the
    // user's organization (pin / archive / rename / review / tags).
    [TestMethod]
    public async Task MergeScan_RefreshesContentButPreservesOrganization()
    {
        var service = new ArchiveService(storePath: Path.Combine(Path.GetTempPath(), "clr-merge-" + Guid.NewGuid().ToString("N") + ".json"));
        var existing = new ArchiveSession { Id = "s1", Title = "old title", Pinned = true, Archived = true, Reviewed = true, CustomTitle = "My renamed chat" };
        existing.Tags.Add("squeezebox");
        service.Store.Sessions["s1"] = existing;

        var fromDisk = new ArchiveSession { Id = "s1", Title = "fresh title from disk" };
        await service.MergeScanAsync(new DiskScan(new List<ArchiveSession> { fromDisk }, new List<ArchiveSession>()), refreshList: false);

        var merged = service.Store.Sessions["s1"];
        Assert.AreEqual("fresh title from disk", merged.Title, "disk content should refresh");
        Assert.IsTrue(merged.Pinned, "pin must survive a re-sync");
        Assert.IsTrue(merged.Archived, "archive must survive a re-sync");
        Assert.IsTrue(merged.Reviewed, "review must survive a re-sync");
        Assert.AreEqual("My renamed chat", merged.CustomTitle, "rename must survive a re-sync");
        Assert.IsTrue(merged.Tags.Contains("squeezebox"), "user tags must survive a re-sync");
    }

    // L1 correctness (tandem review #5): a live rollout with a partial trailing line still indexes,
    // including the good lines above the partial one.
    [TestMethod]
    public async Task IndexRoot_ToleratesPartialTrailingLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-partial-" + Guid.NewGuid().ToString("N"));
        var path = WriteRollout(root, "rollout-live.jsonl", "live-1", "2026-06-15T00:00:00Z", "active session");
        File.AppendAllText(path, "\n{\"timestamp\":\"2026-06-15T00:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_m");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var indexed = await service.IndexRootAsync(root);

            Assert.AreEqual(1, indexed, "the file must still index despite the partial tail line");
            Assert.IsTrue(service.Store.Sessions.ContainsKey("live-1"));
            Assert.IsTrue(service.Store.Sessions["live-1"].Messages.Count >= 1, "good lines above the partial must survive");
        }
        finally { Directory.Delete(root, true); }
    }

    // L5: reading-mode cleanup strips machine-context noise and code, keeps the human text.
    [TestMethod]
    public void ForReading_StripsNoiseKeepsConversation()
    {
        var raw = "# Context from my IDE setup: stuff\n"
                + "<environment_context>os=win</environment_context>\n"
                + "<goal_context>do the thing</goal_context>\n"
                + "Please fix the parser bug.\n\n```cs\nvar x = 1;\n```";

        var cleaned = ArchiveService.ForReading(raw);

        Assert.AreEqual("Please fix the parser bug.", cleaned);
        Assert.IsFalse(cleaned.Contains("environment_context"));
        Assert.IsFalse(cleaned.Contains("var x = 1"));
        Assert.AreEqual("", ArchiveService.ForReading("<environment_context>only noise</environment_context>"));
        // Claude IDE-context tags are stripped too, so titles read like real prompts.
        Assert.AreEqual("Fix the null check in foo.",
            ArchiveService.ForReading("<ide_opened_file>opened foo.cs</ide_opened_file>\nFix the null check in foo."));
    }

    // L3: adding a chat to a project (collection) survives an app restart.
    [TestMethod]
    public async Task AddToCollection_PersistsAcrossReload()
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-coll-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = new ArchiveSession { Id = "chat-42", Title = "Resume me later" };
            var writer = new ArchiveService(storePath: store);
            writer.Store.Sessions[session.Id] = session;
            await writer.AddToCollectionAsync(session, "Squeezebox");

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();

            Assert.IsTrue(
                reader.Store.Collections.Values.Any(c => c.Name == "Squeezebox" && c.SessionIds.Contains("chat-42")),
                "collection membership must survive a reload");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    private static ArchiveService TempService(out string store)
    {
        store = Path.Combine(Path.GetTempPath(), "clr-agent-" + Guid.NewGuid().ToString("N") + ".json");
        return new ArchiveService(storePath: store);
    }

    // The control panel can create an empty collection (no chat needed); creating the same name
    // twice (by slug) returns the existing one so a later agent self-file lands in the same place.
    [TestMethod]
    public async Task CreateCollection_MakesEmptyAndDedupesBySlug()
    {
        var svc = TempService(out var store);
        try
        {
            var a = await svc.CreateCollectionAsync("Renderer Work");
            Assert.IsTrue(svc.Store.Collections.ContainsKey(a.Id), "the empty collection exists");
            Assert.AreEqual(0, a.SessionIds.Count, "it starts with no chats");

            // Same display name (same slug) -> same collection, not a duplicate.
            var b = await svc.CreateCollectionAsync("renderer work");
            Assert.AreEqual(a.Id, b.Id, "same slug returns the same collection");
            Assert.AreEqual(1, svc.Store.Collections.Count, "no duplicate collection is created");

            // An agent self-filing into the same name lands in that collection.
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Renderer Work");
            Assert.AreEqual(1, svc.Store.Collections.Count, "still one collection");
            Assert.IsTrue(svc.Store.Collections[a.Id].SessionIds.Contains("s1"), "the chat joins the existing collection");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // "self" must resolve to the chat that's LIVE (its transcript was just written), not the chat
    // with the newest in-transcript timestamp - that mismatch filed a random sibling before.
    [TestMethod]
    public void ResolveSelf_PicksMostRecentlyWrittenTranscript_NotNewestTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var live = Path.Combine(root, "live.jsonl");
        var old = Path.Combine(root, "old.jsonl");
        File.WriteAllText(live, "{}");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));   // sibling's file is old on disk...
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);              // ...the live chat is being written now

        var svc = TempService(out var store);
        try
        {
            const string ws = @"C:\proj\app";
            // The live chat has a STALE parsed timestamp; the sibling has a NEWER one. Old code picked the sibling.
            svc.Store.Sessions["live"] = new ArchiveSession { Id = "live", Workspace = ws, SourcePath = live, Tool = "claude", UpdatedAt = "2026-01-01T00:00:00Z" };
            svc.Store.Sessions["old"] = new ArchiveSession { Id = "old", Workspace = ws, SourcePath = old, Tool = "claude", UpdatedAt = "2026-06-27T00:00:00Z" };

            var target = svc.ResolveTargetSession(new AgentCommand { op = "addToCollection", project = "X", target = "self", cwd = ws });

            Assert.IsNotNull(target);
            Assert.AreEqual("live", target!.Id, "resolves to the freshly-written transcript, not the newest timestamp");
        }
        finally
        {
            if (File.Exists(store)) File.Delete(store);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Deleting a project removes only the grouping; the chats stay in the archive.
    [TestMethod]
    public async Task RemoveCollection_DropsGroupingKeepsChats()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Temp");
            var id = svc.Store.Collections.Keys.First();

            await svc.RemoveCollectionAsync(id);

            Assert.IsFalse(svc.Store.Collections.ContainsKey(id), "the collection is removed");
            Assert.IsTrue(svc.Store.Sessions.ContainsKey("s1"), "the chat itself is kept");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A deleted collection lands in Recently Deleted and can be restored exactly as it was.
    [TestMethod]
    public async Task DeleteCollection_GoesToRecentlyDeleted_AndRestores()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Renderer");
            var id = svc.Store.Collections.Keys.First();

            await svc.RemoveCollectionAsync(id);
            Assert.IsFalse(svc.Store.Collections.ContainsKey(id), "removed from active");
            Assert.AreEqual(1, svc.Store.DeletedCollections.Count, "moved to recently deleted");

            await svc.RestoreDeletedCollectionAsync(id);
            Assert.IsTrue(svc.Store.Collections.ContainsKey(id), "restored to active");
            Assert.AreEqual(0, svc.Store.DeletedCollections.Count, "no longer in recently deleted");
            Assert.IsTrue(svc.Store.Collections[id].SessionIds.Contains("s1"), "its chats came back");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Export then import rebuilds collections from a lightweight backup; re-import is non-destructive.
    [TestMethod]
    public async Task ExportImport_RoundTripsCollections()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            svc.Store.Sessions["s2"] = new ArchiveSession { Id = "s2" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Alpha");
            await svc.AddToCollectionAsync(svc.Store.Sessions["s2"], "Beta");
            var json = svc.ExportCollectionsJson();

            svc.Store.Collections.Clear();   // simulate accidental loss / a fresh machine

            var added = await svc.ImportCollectionsJsonAsync(json);
            Assert.AreEqual(2, added, "both collections restored from backup");
            Assert.IsTrue(svc.Store.Collections.Values.Any(c => c.Name == "Alpha" && c.SessionIds.Contains("s1")));
            Assert.IsTrue(svc.Store.Collections.Values.Any(c => c.Name == "Beta" && c.SessionIds.Contains("s2")));

            var again = await svc.ImportCollectionsJsonAsync(json);
            Assert.AreEqual(0, again, "re-importing an existing backup adds nothing");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A malformed backup file is rejected, never partially applied.
    [TestMethod]
    public async Task ImportCollections_RejectsInvalidJson()
    {
        var svc = TempService(out var store);
        try
        {
            Assert.AreEqual(-1, await svc.ImportCollectionsJsonAsync("this is not json"));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // The agent resume prompt must point a fresh agent at the transcript as the source of truth and
    // frame the snippets as a preview only - so it reconstructs the real task instead of acting on an excerpt.
    [TestMethod]
    public void ResumePrompt_TreatsTranscriptAsAuthoritative()
    {
        var svc = TempService(out var store);
        try
        {
            var s = new ArchiveSession
            {
                Id = "x",
                Title = "Check current date",
                SourcePath = @"C:\Users\Ahmed\.claude\projects\p\abc.jsonl",
                Workspace = @"z:\proj",
                ContentLoaded = true   // skip the file load; we set the preview messages directly
            };
            s.Messages.Add(new ArchiveMessage { Role = "user", Text = "do the actual thing" });
            s.Messages.Add(new ArchiveMessage { Role = "assistant", Text = "finished step one" });
            svc.Store.Sessions["x"] = s;

            var prompt = svc.CopyPayload(s, "resume");

            Assert.IsTrue(prompt.Contains(s.SourcePath), "includes the source transcript path");
            Assert.IsTrue(prompt.Contains("Read the archived transcript"), "tells the agent to read the file first");
            Assert.IsTrue(prompt.Contains("authoritative"), "marks the transcript as authoritative");
            Assert.IsTrue(prompt.Contains("preview"), "labels the snippets as only a preview");
            Assert.IsTrue(prompt.Contains("do the actual thing"), "includes a recent message preview");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4: an agent favorites "self" — resolved as the newest session in its workspace.
    [TestMethod]
    public async Task AgentCommand_FavoriteSelf_ResolvesNewestByCwd()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["old"] = new ArchiveSession { Id = "old", Workspace = "z:\\proj", UpdatedAt = "2026-06-01T00:00:00Z", Tool = "claude" };
            svc.Store.Sessions["new"] = new ArchiveSession { Id = "new", Workspace = "z:\\proj", UpdatedAt = "2026-06-15T00:00:00Z", Tool = "claude" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "favorite", target = "self", cwd = "z:/proj/" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.Store.Sessions["new"].Pinned, "newest session in the cwd is 'self'");
            Assert.IsFalse(svc.Store.Sessions["old"].Pinned);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4: an agent files itself into a project by cwd.
    [TestMethod]
    public async Task AgentCommand_AddToProject_FilesSelfByCwd()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s"] = new ArchiveSession { Id = "s", Workspace = "z:\\proj", UpdatedAt = "2026-06-15T00:00:00Z" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToProject", project = "VENPOD", cwd = "z:\\proj" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.Store.Collections.Values.Any(c => c.Name == "VENPOD" && c.SessionIds.Contains("s")));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4/N2: an agent registers a non-default source root; defaults are kept.
    [TestMethod]
    public async Task AgentCommand_AddSource_RegistersCustomRootKeepsDefaults()
    {
        var svc = TempService(out var store);
        try
        {
            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSource", tool = "claude", root = "d:\\custom\\claude" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.EffectiveSources().Any(s => s.Tool == "claude" && s.Root == "d:\\custom\\claude"));
            Assert.IsTrue(svc.EffectiveSources().Any(s => s.Tool == "codex"), "default codex source kept");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4/N7: an agent renames itself (app title), canonical write-back is attempted.
    [TestMethod]
    public async Task AgentCommand_Rename_SetsLocalTitle()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s"] = new ArchiveSession { Id = "s", Title = "orig", Workspace = "z:\\proj", UpdatedAt = "2026-06-15T00:00:00Z" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "rename", target = "self", cwd = "z:\\proj", localName = "My cool chat" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual("My cool chat", svc.Store.Sessions["s"].DisplayTitle);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // L4: the Source inspector reads the real rollout event timeline, not a placeholder.
    [TestMethod]
    public void ReadEvents_ReturnsRealTimelineFromRollout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-evt-" + Guid.NewGuid().ToString("N"));
        var file = WriteRollout(dir, "rollout-evt.jsonl", "sess-1", "2026-03-01T00:00:00Z", "hello old chat");
        try
        {
            var service = new ArchiveService(useBundledStore: true);
            var events = service.ReadEvents(new ArchiveSession { Id = "sess-1", SourcePath = file });

            Assert.IsTrue(events.Count >= 3, $"expected >=3 events, got {events.Count}");
            Assert.IsTrue(events.Any(e => e.Kind == "user_message" && e.Preview.Contains("hello old chat")));
            Assert.IsTrue(events.Any(e => e.Kind == "agent_message"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task LoadAsync_UsesRoseAsNativeDefault()
    {
        var service = new ArchiveService(useBundledStore: true);

        await service.LoadAsync();

        Assert.AreNotEqual("codex-blue", service.Store.Settings.Accent);
        Assert.StartsWith("#", service.Store.Settings.AccentHex);
        Assert.AreEqual("compact", service.Store.Settings.Radius);
        Assert.IsTrue(service.Store.Settings.ReadOnlySourceMode);
        Assert.AreNotEqual(0, service.Sessions.Count);
    }

    [TestMethod]
    public async Task Search_FindsFixtureBySessionId()
    {
        var service = new ArchiveService(useBundledStore: true);
        await service.LoadAsync();

        var results = service.Search("fixture-b");

        Assert.IsTrue(results.Any(session => session.Id == "fixture-b"));
    }

    [TestMethod]
    public async Task CopyPayload_BuildsRestorePacketAndCodePayload()
    {
        var service = new ArchiveService(useBundledStore: true);
        await service.LoadAsync();
        var fixture = service.Search("fixture-b").First();

        var restore = service.CopyPayload(fixture, "restore");
        var code = service.CopyPayload(fixture, "code");

        StringAssert.Contains(restore, "Restore Packet");
        StringAssert.Contains(code, "export function score");
    }

    [TestMethod]
    public async Task DeepSearch_ReturnsContentSnippetsAndPathPayload()
    {
        var service = new ArchiveService(useBundledStore: true);
        await service.LoadAsync();

        // DeepSearch now matches the capped, in-memory Text (full transcripts lazy-load on open), so
        // search terms that live in the stored text.
        var hits = service.DeepSearch("restore packet");
        var fixture = service.Search("fixture-b").First();
        var path = service.CopyPayload(fixture, "path");

        Assert.IsTrue(hits.Any(hit => hit.Session.Id == "fixture-b" && hit.Snippet.Contains("restore", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(fixture.SourcePath, path);
    }
}
