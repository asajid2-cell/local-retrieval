using CodexLocalRetrieval.Core.Memory;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// Memory Bank / Project Brain — Phase A (source layer) verifiers.
[TestClass]
public sealed class BrainTests
{
    private static ArchiveService TempService(out string store)
    {
        store = Path.Combine(Path.GetTempPath(), "clr-brain-" + Guid.NewGuid().ToString("N") + ".json");
        return new ArchiveService(storePath: store);
    }

    // Writes a Codex rollout with `pairs` user/assistant turns (2*pairs messages), each with unique
    // text so the parser's dedup keeps them all. Returns (dir, id).
    private static (string dir, string id) WriteBigCodexRollout(int pairs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-bigcodex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var id = "019f0d3e-" + Guid.NewGuid().ToString("N")[..12];
        var lines = new List<string>
        {
            "{\"timestamp\":\"2026-06-28T02:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + id + "\",\"id\":\"" + id + "\",\"cwd\":\"z:/proj\"}}"
        };
        for (var i = 0; i < pairs; i++)
        {
            lines.Add("{\"timestamp\":\"2026-06-28T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"user turn number " + i + "\"}}");
            lines.Add("{\"timestamp\":\"2026-06-28T02:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"assistant reply number " + i + "\"}}");
        }
        File.WriteAllLines(Path.Combine(dir, "rollout-2026-06-28T02-00-00-" + id + ".jsonl"), lines);
        return (dir, id);
    }

    private static (string dir, string id) WriteBigClaudeTranscript(int pairs)
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-bigclaude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var id = Guid.NewGuid().ToString();
        var lines = new List<string>();
        // Real Claude transcripts carry a sessionId on every line — DetectTool keys on it. Without it
        // the file would be misclassified as a Codex rollout.
        for (var i = 0; i < pairs; i++)
        {
            lines.Add("{\"type\":\"user\",\"sessionId\":\"" + id + "\",\"timestamp\":\"2026-06-28T02:00:00Z\",\"cwd\":\"z:/proj\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"user turn number " + i + "\"}]}}");
            lines.Add("{\"type\":\"assistant\",\"sessionId\":\"" + id + "\",\"timestamp\":\"2026-06-28T02:00:00Z\",\"cwd\":\"z:/proj\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"assistant reply number " + i + "\"}]}}");
        }
        File.WriteAllLines(Path.Combine(dir, id + ".jsonl"), lines);
        return (dir, id);
    }

    // GG1 DRIFT GUARD: the full-history parse must contain MORE than the windowed reader parse, and
    // its last-N tail must be byte-identical (role+text+timestamp) to the windowed messages. If the
    // shared extraction core ever drifts between the two paths, this fails.
    [TestMethod]
    public async Task ParseFull_TailEqualsWindowedParse_Codex()
    {
        var (dir, id) = WriteBigCodexRollout(700);  // 1400 messages > 600 window
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);
            var session = svc.Store.Sessions[id];
            await svc.EnsureContentAsync(session);

            var full = await svc.ParseFullAsync(session);

            Assert.AreEqual(1400, full.Count, "full parse keeps every message");
            Assert.AreEqual(session.MessageCount, full.Count, "MessageCount is the true total");
            Assert.IsTrue(full.Count > session.Messages.Count, "windowed reader is a strict subset");
            Assert.AreEqual(600, session.Messages.Count, "reader window is 600");

            var tail = full.Skip(full.Count - session.Messages.Count).ToList();
            for (var i = 0; i < session.Messages.Count; i++)
            {
                Assert.AreEqual(session.Messages[i].Role, tail[i].Role, $"role drift at {i}");
                Assert.AreEqual(session.Messages[i].Text, tail[i].Text, $"text drift at {i}");
                Assert.AreEqual(session.Messages[i].Timestamp, tail[i].Timestamp, $"ts drift at {i}");
            }
            // The first message of the FULL list is the real opening prompt the window dropped.
            Assert.AreEqual("user turn number 0", full[0].Text);
        }
        finally { Cleanup(dir, store); }
    }

    [TestMethod]
    public async Task ParseFull_TailEqualsWindowedParse_Claude()
    {
        var (dir, id) = WriteBigClaudeTranscript(700);
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);
            var session = svc.Store.Sessions[id];
            await svc.EnsureContentAsync(session);

            var full = await svc.ParseFullAsync(session);

            Assert.AreEqual(1400, full.Count);
            Assert.AreEqual(session.MessageCount, full.Count);
            Assert.AreEqual(600, session.Messages.Count);

            var tail = full.Skip(full.Count - session.Messages.Count).ToList();
            for (var i = 0; i < session.Messages.Count; i++)
            {
                Assert.AreEqual(session.Messages[i].Role, tail[i].Role, $"role drift at {i}");
                Assert.AreEqual(session.Messages[i].Text, tail[i].Text, $"text drift at {i}");
            }
            // The first full message is the opening prompt the window dropped. Claude accumulates
            // content blocks with AppendLine, so its message text carries a trailing newline (the
            // windowed and full paths agree on this — the tail loop above proves it); Trim to compare
            // the content, not that incidental artifact.
            Assert.AreEqual("user turn number 0", full[0].Text.Trim());
        }
        finally { Cleanup(dir, store); }
    }

    // GG2: blocking is deterministic AND contiguous — same input ⇒ identical ids/ranges/hashes, and
    // the blocks tile the whole transcript with no gaps or overlaps.
    [TestMethod]
    public async Task SourceBlocker_DeterministicAndContiguous()
    {
        var (dir, id) = WriteBigCodexRollout(700);
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);
            var session = svc.Store.Sessions[id];
            var full = await svc.ParseFullAsync(session);

            var a = SourceBlocker.Build(session.Id, session.SourcePath, session.Tool, full);
            var b = SourceBlocker.Build(session.Id, session.SourcePath, session.Tool, full);

            Assert.IsTrue(a.Count > 1, "a 1400-message chat splits into several blocks");
            Assert.AreEqual(a.Count, b.Count, "block count is deterministic");
            for (var i = 0; i < a.Count; i++)
            {
                Assert.AreEqual(a[i].Id, b[i].Id);
                Assert.AreEqual(a[i].SourceHash, b[i].SourceHash);
                Assert.AreEqual(a[i].MsgStartIndex, b[i].MsgStartIndex);
                Assert.AreEqual(a[i].MsgEndIndex, b[i].MsgEndIndex);
            }
            // contiguous tiling
            Assert.AreEqual(0, a[0].MsgStartIndex);
            Assert.AreEqual(full.Count - 1, a[^1].MsgEndIndex);
            for (var i = 1; i < a.Count; i++)
                Assert.AreEqual(a[i - 1].MsgEndIndex + 1, a[i].MsgStartIndex, $"gap before block {i}");
            // every block carries a non-empty hash that doubles as its anchor quote hash
            foreach (var blk in a)
            {
                Assert.IsFalse(string.IsNullOrEmpty(blk.SourceHash));
                Assert.AreEqual(blk.SourceHash, blk.ToAnchor().QuoteHash);
            }
        }
        finally { Cleanup(dir, store); }
    }

    // GG2: anchors survive a re-parse of the unchanged file (hash bound to content, not parse run).
    [TestMethod]
    public async Task SourceBlocker_AnchorsStableAcrossReparse()
    {
        var (dir, id) = WriteBigCodexRollout(300);
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);
            var session = svc.Store.Sessions[id];

            var first = SourceBlocker.Build(session.Id, session.SourcePath, session.Tool, await svc.ParseFullAsync(session));
            var second = SourceBlocker.Build(session.Id, session.SourcePath, session.Tool, await svc.ParseFullAsync(session));

            Assert.AreEqual(first.Count, second.Count);
            for (var i = 0; i < first.Count; i++)
            {
                Assert.AreEqual(first[i].SourceHash, second[i].SourceHash, $"hash changed on re-parse at block {i}");
                Assert.AreEqual(first[i].FileStamp, second[i].FileStamp, "file stamp stable while file unchanged");
            }
        }
        finally { Cleanup(dir, store); }
    }

    // GG8 precondition: any secret in a transcript is redacted before it lands in a block excerpt.
    [TestMethod]
    public void SourceBlocker_RedactsSecretsInExcerpts()
    {
        const string secret = "sk-livedeadbeef0123456789abcdef";
        var messages = new List<FullMessage>
        {
            new(0, "user", "here is my key " + secret + " please use it", "2026-06-28T02:00:00Z", null),
            new(1, "assistant", "got it, will not echo the key", "2026-06-28T02:00:01Z", null),
        };
        Assert.IsTrue(SecretRedactor.ContainsSecret(messages[0].Text), "raw text really does contain a secret");

        var blocks = SourceBlocker.Build("sid", "z:/x.jsonl", "codex", messages, "stamp");
        Assert.AreEqual(1, blocks.Count);
        Assert.IsFalse(blocks[0].Excerpt.Contains(secret), "excerpt must not leak the raw key");
        Assert.IsTrue(blocks[0].Excerpt.Contains("[redacted-key]"), "excerpt shows the redaction marker");
    }

    [TestMethod]
    public void SecretRedactor_MasksCommonShapes()
    {
        Assert.AreEqual("[redacted-key]", SecretRedactor.Redact("sk-abcdefghijklmnop123456"));
        Assert.IsFalse(SecretRedactor.Redact("AKIAIOSFODNN7EXAMPLE").Contains("AKIA"));
        Assert.IsFalse(SecretRedactor.Redact("Bearer abcdef012345678901234").Contains("abcdef012345678901234"));
        Assert.AreEqual("nothing here", SecretRedactor.Redact("nothing here"));
    }

    // ---- BL2: vault + git + index ----

    private static string TempBrains() => Path.Combine(Path.GetTempPath(), "clr-brains-" + Guid.NewGuid().ToString("N"));

    private static MemoryCard SampleCard(string id, string title, string lane, string body, bool withAnchor)
    {
        var card = new MemoryCard
        {
            Id = id, Title = title, Lane = lane, Type = CardTypes.Decision, Status = CardStatuses.Active,
            TruthEvidence = TruthEvidence.ResultBacked, ExtractionConfidence = ExtractionConfidence.High,
            Importance = 4, Durability = 4, Actionability = 3, CreatedBy = CreatedBy.Codex,
            CreatedAt = "2026-06-28T00:00:00Z", DerivedFromPatchId = "patch_001",
            SourceCoverage = withAnchor ? SourceCoverage.ExactSpan : SourceCoverage.ManualUnverified,
            Topics = new() { "t6-modding", "servant-fx" }, Related = new() { "other-card" }, Body = body,
        };
        if (withAnchor)
            card.Sources.Add(new SourceAnchor
            {
                SessionId = "chat_004", SourcePath = "z:/proj/x.jsonl", BlockId = "chat_004:block_2",
                MsgStartIndex = 182, MsgEndIndex = 197, TsStart = "2026-06-28T01:00:00Z",
                TsEnd = "2026-06-28T01:05:00Z", QuoteHash = "abc123def456", FileStamp = "111:222",
            });
        return card;
    }

    // GG3 / lockstep: every card field survives a serialize -> parse round-trip. This is what makes
    // "Markdown is canonical" safe — the index reader and the writer agree on the format exactly.
    [TestMethod]
    public void CardMarkdown_RoundTripsAllFields()
    {
        var card = SampleCard("decision-use-method-x", "Use method X for the servant FX", Lanes.Canonical,
            "We chose method X after Y failed. It produced a working servant FX.", withAnchor: true);
        card.LedTo = new() { "win-servant-fx-renders" };
        card.CausedBy = new() { "rejected-method-y" };

        var parsed = CardMarkdown.Parse(CardMarkdown.Serialize(card));

        Assert.AreEqual(card.Id, parsed.Id);
        Assert.AreEqual(card.Title, parsed.Title);
        Assert.AreEqual(Lanes.Canonical, parsed.Lane);
        Assert.AreEqual(CardTypes.Decision, parsed.Type);
        Assert.AreEqual(TruthEvidence.ResultBacked, parsed.TruthEvidence);
        Assert.AreEqual(ExtractionConfidence.High, parsed.ExtractionConfidence);
        Assert.AreEqual(4, parsed.Importance);
        Assert.AreEqual("patch_001", parsed.DerivedFromPatchId);
        CollectionAssert.AreEqual(card.Topics, parsed.Topics);
        CollectionAssert.AreEqual(card.LedTo, parsed.LedTo);
        CollectionAssert.AreEqual(card.CausedBy, parsed.CausedBy);
        Assert.AreEqual(1, parsed.Sources.Count);
        Assert.AreEqual("chat_004", parsed.Sources[0].SessionId);
        Assert.AreEqual(182, parsed.Sources[0].MsgStartIndex);
        Assert.AreEqual(197, parsed.Sources[0].MsgEndIndex);
        Assert.AreEqual("abc123def456", parsed.Sources[0].QuoteHash);
        Assert.IsTrue(parsed.Body.Contains("working servant FX"));
    }

    // GG3: a written vault is a real Obsidian vault — scaffold + card file with frontmatter, wikilinks,
    // and an auto Sources section; ReadAllCards recovers the cards.
    [TestMethod]
    public void Vault_ScaffoldAndCardRoundtrip()
    {
        var brains = TempBrains();
        try
        {
            var paths = new BrainPaths(brains, "col-graphics");
            var manifest = new BrainManifest { CollectionId = "col-graphics", CollectionName = "Graphics" };
            var vw = new VaultWriter();
            vw.EnsureScaffold(paths, manifest);

            var canon = SampleCard("decision-x", "Method X for servant FX", Lanes.Canonical, "Chose X.", true);
            var work = SampleCard("hyp-y", "Maybe Y could work", Lanes.Working, "Unverified hunch.", false);
            vw.WriteCards(paths, new[] { canon, work });
            vw.WriteOverviews(paths, new[] { canon, work }, manifest);

            Assert.IsTrue(Directory.Exists(paths.CardsCanonical));
            Assert.IsTrue(File.Exists(Path.Combine(paths.Vault, "00 Atlas.md")));
            Assert.IsTrue(File.Exists(paths.GitIgnore));

            var cardMd = File.ReadAllText(Path.Combine(paths.CardsCanonical, "decision-x.md"));
            Assert.IsTrue(cardMd.Contains("## Sources"), "card has a Sources section");
            Assert.IsTrue(cardMd.Contains("collections://source?"), "card carries the open-source-span link");
            Assert.IsTrue(File.ReadAllText(Path.Combine(paths.Vault, "00 Atlas.md")).Contains("[[02 Top Canonical Truths]]"));

            var read = vw.ReadAllCards(paths);
            Assert.AreEqual(2, read.Count);
            var rc = read.First(c => c.Id == "decision-x");
            Assert.AreEqual(Lanes.Canonical, rc.Lane);
            Assert.AreEqual("Method X for servant FX", rc.Title);
            Assert.AreEqual(1, rc.Sources.Count);
            Assert.AreEqual(182, rc.Sources[0].MsgStartIndex);
        }
        finally { TryDeleteDir(brains); }
    }

    // GG4 + GG8: git init + commit creates history and tracks the vault files, and NO raw transcript /
    // secret is ever committed. Degrades gracefully when git is not installed.
    [TestMethod]
    public void Git_CommitsVaultAndNeverCommitsRawSecrets()
    {
        var brains = TempBrains();
        try
        {
            var paths = new BrainPaths(brains, "col-secrets");
            var manifest = new BrainManifest { CollectionId = "col-secrets" };
            var vw = new VaultWriter();
            vw.EnsureScaffold(paths, manifest);

            // a card whose source excerpt came from a chat that contained a live key — redacted on the way in
            const string secret = "sk-livedeadbeef0123456789abcdef";
            var msgs = new List<FullMessage>
            {
                new(0, "user", "my key is " + secret + " use method X", "t0", null),
                new(1, "assistant", "method X gave a working servant fx", "t1", null),
            };
            var blocks = SourceBlocker.Build("chat_004", "z:/proj/x.jsonl", "codex", msgs, "111:222");
            var card = SampleCard("decision-x", "Method X", Lanes.Canonical,
                "From the chat: " + blocks[0].Excerpt, true);
            vw.WriteCards(paths, new[] { card });
            vw.WriteOverviews(paths, new[] { card }, manifest);

            var git = new GitHistory();
            if (!git.IsAvailable())
            {
                Assert.AreEqual("", git.Commit(paths.Vault, "x"), "no git => no commit, but vault still written");
                Assert.IsTrue(Directory.Exists(paths.CardsCanonical), "build still produced a usable vault");
                return; // GG4: git optional — build works without history
            }

            var commit = git.Commit(paths.Vault, "Build brain for col-secrets");
            Assert.IsTrue(commit.Length >= 7, "commit produced a hash");
            Assert.IsTrue(git.Log(paths.Vault).Count >= 1, "history exists");

            var tracked = git.LsFiles(paths.Vault);
            Assert.IsTrue(tracked.Count > 0, "vault files are tracked");
            foreach (var rel in tracked)
            {
                var full = Path.Combine(paths.Vault, rel.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(full)) continue;
                var content = File.ReadAllText(full);
                Assert.IsFalse(content.Contains(secret), $"tracked file {rel} leaked a raw secret");
                Assert.IsFalse(SecretRedactor.ContainsSecret(content), $"tracked file {rel} contains a secret shape");
            }
        }
        finally { TryDeleteDir(brains); }
    }

    // GG5: the derived index builds from vault cards + chat blocks, counts line up, and FTS finds a card.
    [TestMethod]
    public void Index_BuildsFromVaultAndSearches()
    {
        var brains = TempBrains();
        try
        {
            var paths = new BrainPaths(brains, "col-idx");
            var manifest = new BrainManifest { CollectionId = "col-idx" };
            manifest.IncorporatedChats["chat_004"] = "111:222";
            var vw = new VaultWriter();
            vw.EnsureScaffold(paths, manifest);

            var c1 = SampleCard("decision-ram", "VS Code RAM usage decision", Lanes.Canonical,
                "We capped the VS Code RAM budget to stop the indexer thrashing.", true);
            var c2 = SampleCard("hyp-gpu", "Maybe move to GPU", Lanes.Working, "Unverified GPU idea.", false);
            vw.WriteCards(paths, new[] { c1, c2 });

            var msgs = new List<FullMessage> { new(0, "user", "ram talk", "t0", null), new(1, "assistant", "ok", "t1", null) };
            var blocks = SourceBlocker.Build("chat_004", "z:/proj/x.jsonl", "codex", msgs, "111:222");

            var idx = new BrainIndex(paths.Db);
            idx.Rebuild(vw.ReadAllCards(paths), blocks, manifest);

            Assert.AreEqual(2, idx.CardCount());
            Assert.IsTrue(idx.BlockCount() >= 1);
            Assert.IsTrue(idx.SourceCount() >= 1);
            var hits = idx.Search("RAM");
            CollectionAssert.Contains(hits, "decision-ram");
            Assert.IsFalse(hits.Contains("hyp-gpu"));
            // the canonical card's anchor is queryable for trace-to-source
            Assert.AreEqual(1, idx.CardSources("decision-ram").Count);
        }
        finally { TryDeleteDir(brains); }
    }

    // SACRED GG6: Markdown is canonical. A hand-edit to a card .md is reflected after reindex — proving
    // the SQLite index is genuinely derived, not a hidden second source of truth.
    [TestMethod]
    public void Index_MarkdownIsCanonical_HandEditReflectedAfterReindex()
    {
        var brains = TempBrains();
        try
        {
            var paths = new BrainPaths(brains, "col-canon");
            var manifest = new BrainManifest { CollectionId = "col-canon" };
            var vw = new VaultWriter();
            vw.EnsureScaffold(paths, manifest);
            vw.WriteCards(paths, new[] { SampleCard("decision-x", "OriginalTitleAlpha", Lanes.Canonical, "body", true) });

            var idx = new BrainIndex(paths.Db);
            idx.Rebuild(vw.ReadAllCards(paths), Array.Empty<SourceBlock>(), manifest);
            Assert.AreEqual("OriginalTitleAlpha", idx.GetCardTitle("decision-x"));

            // hand-edit the .md (as a human would in Obsidian) — change the title only
            var file = Path.Combine(paths.CardsCanonical, "decision-x.md");
            var edited = File.ReadAllText(file).Replace("OriginalTitleAlpha", "HandEditedTitleBeta");
            File.WriteAllText(file, edited);

            // reindex straight from the vault
            idx.Rebuild(vw.ReadAllCards(paths), Array.Empty<SourceBlock>(), manifest);
            Assert.AreEqual("HandEditedTitleBeta", idx.GetCardTitle("decision-x"), "the index reflects the hand edit");
        }
        finally { TryDeleteDir(brains); }
    }

    // SACRED GG7: the index is disposable. Delete brain.db, rebuild from the vault, everything works.
    [TestMethod]
    public void Index_IsDisposable_DeleteThenRebuild()
    {
        var brains = TempBrains();
        try
        {
            var paths = new BrainPaths(brains, "col-disp");
            var manifest = new BrainManifest { CollectionId = "col-disp" };
            var vw = new VaultWriter();
            vw.EnsureScaffold(paths, manifest);
            vw.WriteCards(paths, new[]
            {
                SampleCard("a", "Alpha card about meshes", Lanes.Canonical, "mesh body", true),
                SampleCard("b", "Beta card about shaders", Lanes.Working, "shader body", false),
            });

            var idx = new BrainIndex(paths.Db);
            idx.Rebuild(vw.ReadAllCards(paths), Array.Empty<SourceBlock>(), manifest);
            Assert.AreEqual(2, idx.CardCount());

            idx.Delete();
            Assert.IsFalse(File.Exists(paths.Db), "db file is gone");

            // rebuild purely from the canonical vault
            var idx2 = new BrainIndex(paths.Db);
            idx2.Rebuild(vw.ReadAllCards(paths), Array.Empty<SourceBlock>(), manifest);
            Assert.AreEqual(2, idx2.CardCount(), "rebuilt from the vault alone");
            CollectionAssert.Contains(idx2.Search("shaders"), "b");
        }
        finally { TryDeleteDir(brains); }
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }

    private static void Cleanup(string dir, string store)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        try { if (File.Exists(store)) File.Delete(store); } catch { }
    }
}
