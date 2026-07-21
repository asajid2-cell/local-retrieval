using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// /stashme: one call that names a chat, files it into a collection/deck, and tags it with a searchable
// CODENAME ("special phrase"). The core promise: searching the codename surfaces EVERY chat stashed under
// it, so many chats can live under one codename (e.g. "petunia").
[TestClass]
public class StashSkillTests
{
    [TestMethod]
    public void NormalizeAgentOp_MapsStashAliases()
    {
        Assert.AreEqual("stash", ArchiveService.NormalizeAgentOp("stash"));
        Assert.AreEqual("stash", ArchiveService.NormalizeAgentOp("stashme"));
        Assert.AreEqual("stash", ArchiveService.NormalizeAgentOp("StashMe"));
    }

    [TestMethod]
    public void SpecialPhrase_SearchSurfacesEveryChatUnderTheCodename()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-stash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            // three chats: two carry codename "petunia", one doesn't
            foreach (var (id, phrase) in new[] { ("aaaa1", "petunia"), ("bbbb2", "petunia"), ("cccc3", (string?)null) })
            {
                var s = new ArchiveSession { Id = id, Tool = "claude", Title = "chat " + id, Text = "some body" };
                if (phrase != null) s.SpecialPhrases.Add(phrase);
                svc.Store.Sessions[id] = s;
            }
            var hits = svc.Search("petunia").Select(h => h.Id).ToHashSet();
            Assert.IsTrue(hits.Contains("aaaa1") && hits.Contains("bbbb2"), "both petunia chats surface");
            Assert.IsFalse(hits.Contains("cccc3"), "a chat without the codename does not surface");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Stash_NamesFilesAndCodenames_InOneCall()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-stash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var id = "11111111-2222-3333-4444-555555555555";
            var path = Path.Combine(dir, id + ".jsonl");
            File.WriteAllText(path, "{\"sessionId\":\"" + id + "\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
            var svc = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            svc.Store.Sessions[id] = new ArchiveSession { Id = id, SourcePath = path, Tool = "claude", Title = "orig" };

            var res = await svc.ApplyAgentCommandAsync(new AgentCommand
            {
                op = "stashme", id = id, tool = "claude",
                name = "Archiver HTV work", collection = "HarmonizerLabs", deck = "Work", phrase = "petunia"
            });

            Assert.IsTrue(res.Ok, res.Message);
            var s = svc.Store.Sessions[id];
            Assert.AreEqual("Archiver HTV work", s.CustomTitle, "app-only name set");
            Assert.IsTrue(s.SpecialPhrases.Any(p => p.Equals("petunia", StringComparison.OrdinalIgnoreCase)), "codename set");
            Assert.IsTrue(svc.Store.Collections.Values.Any(c =>
                c.Name.Equals("HarmonizerLabs", StringComparison.OrdinalIgnoreCase) && c.SessionIds.Contains(id)),
                "filed into the collection");
            Assert.IsTrue(svc.Search("petunia").Any(h => h.Id == id), "searchable by codename after stashing");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Stash_BareCodename_NoCollectionRequired()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-stash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var id = "99999999-8888-7777-6666-555555555555";
            var path = Path.Combine(dir, id + ".jsonl");
            File.WriteAllText(path, "{\"sessionId\":\"" + id + "\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
            var svc = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            svc.Store.Sessions[id] = new ArchiveSession { Id = id, SourcePath = path, Tool = "claude", Title = "orig" };

            var res = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "stash", id = id, tool = "claude", phrase = "petunia" });
            Assert.IsTrue(res.Ok, res.Message);
            Assert.IsTrue(svc.Search("petunia").Any(h => h.Id == id), "a bare codename (no collection) still stashes + is searchable");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void BracketSearch_IsPreciseToTheCodename_NotTextMentions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-stash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            svc.Store.Settings.BundledHistoryAbsorbed = true;
            var coded = new ArchiveSession { Id = "coded", Tool = "claude", Title = "unrelated title", Text = "body" };
            coded.SpecialPhrases.Add("petunia");
            var mention = new ArchiveSession { Id = "mention", Tool = "claude", Title = "talks about petunia", Text = "petunia petunia" };
            svc.Store.Sessions["coded"] = coded;
            svc.Store.Sessions["mention"] = mention;

            var bracket = svc.Search("[petunia]").Select(h => h.Id).ToHashSet();
            Assert.IsTrue(bracket.Contains("coded"), "[petunia] returns the codenamed chat");
            Assert.IsFalse(bracket.Contains("mention"), "[petunia] is precise — a plain text mention does NOT match");

            var plain = svc.Search("petunia").Select(h => h.Id).ToHashSet();
            Assert.IsTrue(plain.Contains("coded") && plain.Contains("mention"), "plain search still matches codename AND text");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Stash_OnlyCollection_FilesIntoMainDeck_NoNameNoPhrase()
    {
        // <>AOC<><> : just file into AOC (created on the default deck); no rename, no codename
        var (svc, id, dir) = SeededService();
        try
        {
            var res = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "stash", id = id, tool = "claude", collection = "AOC" });
            Assert.IsTrue(res.Ok, res.Message);
            var s = svc.Store.Sessions[id];
            Assert.AreEqual("", s.CustomTitle, "no name given => not renamed");
            Assert.AreEqual(0, s.SpecialPhrases.Count, "no phrase given => no codename");
            Assert.IsTrue(svc.Store.Collections.Values.Any(c =>
                c.Name.Equals("AOC", StringComparison.OrdinalIgnoreCase)
                && c.DeckId == ArchiveService.MainDeckId && c.SessionIds.Contains(id)), "filed into AOC on the Main deck");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task Stash_OnlyDeck_CreatesDeck_AndDefaultsCollectionToDeckName()
    {
        // <mux><><fable><fable> : name=mux, collection blank, deck=fable, phrase=fable
        var (svc, id, dir) = SeededService();
        try
        {
            var res = await svc.ApplyAgentCommandAsync(new AgentCommand
            { op = "stash", id = id, tool = "claude", name = "mux", deck = "fable", phrase = "fable" });
            Assert.IsTrue(res.Ok, res.Message);
            var s = svc.Store.Sessions[id];
            Assert.AreEqual("mux", s.CustomTitle);
            Assert.IsTrue(s.SpecialPhrases.Any(p => p.Equals("fable", StringComparison.OrdinalIgnoreCase)));
            var fable = svc.Decks.FirstOrDefault(d => d.Name.Equals("fable", StringComparison.OrdinalIgnoreCase));
            Assert.IsNotNull(fable, "the 'fable' deck was created (not silently dropped to Main)");
            Assert.IsTrue(svc.Store.Collections.Values.Any(c =>
                c.Name.Equals("fable", StringComparison.OrdinalIgnoreCase)
                && c.DeckId == fable!.Id && c.SessionIds.Contains(id)),
                "collection defaulted to the deck name, on the fable deck");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public void NormalizeAgentOp_MapsInfoAndTemplateAliases()
    {
        Assert.AreEqual("info", ArchiveService.NormalizeAgentOp("info"));
        Assert.AreEqual("info", ArchiveService.NormalizeAgentOp("whoami"));
        Assert.AreEqual("template", ArchiveService.NormalizeAgentOp("template"));
        Assert.AreEqual("template", ArchiveService.NormalizeAgentOp("maketemplate"));
    }

    [TestMethod]
    public async Task Stash_WithTemplateFlag_CreatesOneCheckpointOutsideSessions()
    {
        var (svc, id, dir) = SeededService();
        try
        {
            var res = await svc.ApplyAgentCommandAsync(new AgentCommand
            { op = "stash", id = id, tool = "claude", collection = "Corpus", template = true, requestId = "stash-template-once" });
            Assert.IsTrue(res.Ok, res.Message);
            Assert.AreEqual(1, svc.TemplateSnapshotsForSource(id).Count);
            Assert.IsFalse(svc.Store.Sessions[id].IsTemplate, "the legacy flag is not the new storage mechanism");
            Assert.IsFalse(svc.Store.Sessions.ContainsKey(svc.Templates().Single().Id));
            StringAssert.Contains(res.Message, "checkpoint");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task TemplateFalse_RemovesAllCheckpointsAndReportsCount()
    {
        var (svc, id, dir) = SeededService();
        try
        {
            var on = await svc.ApplyAgentCommandAsync(new AgentCommand
                { op = "template", id = id, tool = "claude", requestId = "first" });
            Assert.IsTrue(on.Ok);
            var second = await svc.ApplyAgentCommandAsync(new AgentCommand
                { op = "template", id = id, tool = "claude", requestId = "second" });
            Assert.IsTrue(second.Ok);
            Assert.AreEqual(2, svc.TemplateSnapshotsForSource(id).Count);

            var off = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "template", id = id, tool = "claude", template = false });
            Assert.IsTrue(off.Ok);
            Assert.AreEqual(0, svc.TemplateSnapshotsForSource(id).Count);
            StringAssert.Contains(off.Message, "Removed 2 checkpoints");
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task InfoOp_ReportsNameCollectionsPhrasesAndCheckpointCount()
    {
        var (svc, id, dir) = SeededService();
        try
        {
            await svc.ApplyAgentCommandAsync(new AgentCommand
            { op = "stash", id = id, tool = "claude", name = "Context primer", collection = "Corpus", phrase = "mux", template = true });

            var res = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "info", id = id, tool = "claude" });
            Assert.IsTrue(res.Ok, res.Message);
            StringAssert.Contains(res.Message, "Context primer");
            StringAssert.Contains(res.Message, "Corpus");
            StringAssert.Contains(res.Message, "mux");
            StringAssert.Contains(res.Message, "checkpoints: 1");
            StringAssert.Contains(res.Message, id);
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task StashTemplateTrue_CreatesExactlyOneDurableCheckpointDespiteRetry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-stash-retry-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var id = "11111111-2222-3333-4444-555555555555";
            var source = Path.Combine(dir, id + ".jsonl");
            await File.WriteAllTextAsync(
                source,
                $"{{\"sessionId\":\"{id}\",\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"snapshot tip\"}}}}\n");
            var store = Path.Combine(dir, "store.json");
            var templates = Path.Combine(dir, "templates");
            var seed = new ArchiveService(storePath: store, templatesRoot: templates);
            seed.Store.Sessions[id] = new ArchiveSession
                { Id = id, Tool = "claude", Title = "source", SourcePath = source };
            await seed.SaveAsync();

            var desktop = new ArchiveService(storePath: store, templatesRoot: templates);
            var bridge = new ArchiveService(storePath: store, templatesRoot: templates);
            await desktop.LoadAsync();
            await bridge.LoadAsync();
            desktop.Store.Settings.MultiplexApiPort = 8129;
            await desktop.SaveAsync();

            var result = await bridge.ApplyAgentCommandAsync(new AgentCommand
            {
                op = "stash",
                id = id,
                tool = "claude",
                template = true,
                requestId = "same-request-through-retry"
            });

            Assert.IsTrue(result.Ok, result.Message);
            var reloaded = new ArchiveService(storePath: store, templatesRoot: templates);
            await reloaded.LoadAsync();
            Assert.AreEqual(1, reloaded.TemplateSnapshotsForSource(id).Count);
            Assert.AreEqual(1, Directory.GetFiles(templates, "*.jsonl").Length);
            Assert.AreEqual(8129, reloaded.Store.Settings.MultiplexApiPort);
        }
        finally { Directory.Delete(dir, true); }
    }

    private static (ArchiveService svc, string id, string dir) SeededService()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-stash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var id = "11111111-2222-3333-4444-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        var path = Path.Combine(dir, id + ".jsonl");
        File.WriteAllText(path, "{\"sessionId\":\"" + id + "\",\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
        var svc = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
        svc.Store.Settings.BundledHistoryAbsorbed = true;
        svc.Store.Sessions[id] = new ArchiveSession { Id = id, SourcePath = path, Tool = "claude", Title = "orig" };
        return (svc, id, dir);
    }
}
