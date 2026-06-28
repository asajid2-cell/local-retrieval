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

    private static void Cleanup(string dir, string store)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        try { if (File.Exists(store)) File.Delete(store); } catch { }
    }
}
