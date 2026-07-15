using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class BranchSessionTests
{
    // A Claude branch must be a resumable clone: a NEW <newId>.jsonl in the same folder with sessionId
    // rewritten throughout, a forkedFrom marker pointing at the parent, and a registered branch session
    // linked back to its original.
    [TestMethod]
    public async Task BranchSessionAsync_Claude_ClonesTranscriptAndLinksParent()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-branch-" + Guid.NewGuid().ToString("N"));
        var projDir = Path.Combine(root, "proj");
        Directory.CreateDirectory(projDir);
        try
        {
            var parentId = "parent-aaaa";
            var srcPath = Path.Combine(projDir, parentId + ".jsonl");
            await File.WriteAllTextAsync(srcPath, string.Join("\n", new[]
            {
                "{\"type\":\"user\",\"sessionId\":\"" + parentId + "\",\"uuid\":\"u1\",\"parentUuid\":null,\"message\":{\"role\":\"user\",\"content\":\"hi\"}}",
                "{\"type\":\"assistant\",\"sessionId\":\"" + parentId + "\",\"uuid\":\"u2\",\"parentUuid\":\"u1\",\"message\":{\"role\":\"assistant\",\"content\":\"yo\"}}",
            }) + "\n");

            var svc = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var parent = new ArchiveSession { Id = parentId, Tool = "claude", Title = "My chat", Workspace = "z:/proj", SourcePath = srcPath, MessageCount = 2 };
            svc.Store.Sessions[parentId] = parent;

            var result = await svc.BranchSessionAsync(parent);

            Assert.IsTrue(result.Ok, result.Message);
            Assert.IsNotNull(result.Branch);
            var branch = result.Branch!;
            Assert.AreNotEqual(parentId, branch.Id, "the branch needs its own resumable id");
            Assert.AreEqual(parentId, branch.BranchOfId, "the branch must point at its parent");
            Assert.IsTrue(branch.IsBranch);
            Assert.IsTrue(svc.Store.Sessions.ContainsKey(branch.Id), "the branch must be registered");
            Assert.IsTrue(branch.Aliases.Contains(parentId), "parent id resolves against the branch");

            var newPath = Path.Combine(projDir, branch.Id + ".jsonl");
            Assert.AreEqual(newPath, branch.SourcePath);
            Assert.IsTrue(File.Exists(newPath), "the cloned transcript must exist on disk");

            var lines = await File.ReadAllLinesAsync(newPath);
            Assert.AreEqual(2, lines.Length);
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                Assert.AreEqual(branch.Id, doc.RootElement.GetProperty("sessionId").GetString(), "every line's sessionId must be the new id");
            }
            using (var first = JsonDocument.Parse(lines[0]))
            {
                var fork = first.RootElement.GetProperty("forkedFrom");
                Assert.AreEqual(parentId, fork.GetProperty("sessionId").GetString());
                Assert.AreEqual("u2", fork.GetProperty("messageUuid").GetString(), "branch point is the tip of the parent history");
            }
            Assert.IsFalse(File.Exists(srcPath) && File.ReadAllText(srcPath).Contains(branch.Id), "the original transcript must be untouched");
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    // The Codex session_meta rewrite gives the clone its own id and stamps the parent link, without
    // touching the rest of the (possibly huge) rollout.
    [TestMethod]
    public void RewriteCodexSessionMeta_RewritesIdAndStampsFork()
    {
        var line = "{\"timestamp\":\"2026-07-08T22:44:11Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"old-id\",\"id\":\"old-id\",\"cwd\":\"z:/proj\"}}";
        var outLine = ArchiveService.RewriteCodexSessionMeta(line, "old-id", "new-id");

        using var doc = JsonDocument.Parse(outLine);
        var payload = doc.RootElement.GetProperty("payload");
        Assert.AreEqual("new-id", payload.GetProperty("id").GetString());
        Assert.AreEqual("new-id", payload.GetProperty("session_id").GetString());
        Assert.AreEqual("old-id", payload.GetProperty("forked_from_id").GetString(), "the parent link must be stamped for the app + Codex");
        Assert.AreEqual("session_meta", doc.RootElement.GetProperty("type").GetString(), "unrelated fields are preserved");
    }

    [TestMethod]
    public async Task BranchSessionAsync_MissingTranscript_FailsCleanly()
    {
        var svc = new ArchiveService(useBundledStore: true);
        var parent = new ArchiveSession { Id = "x", Tool = "claude", SourcePath = "z:/does/not/exist.jsonl" };
        var result = await svc.BranchSessionAsync(parent);
        Assert.IsFalse(result.Ok);
        Assert.IsNull(result.Branch);
    }
}
