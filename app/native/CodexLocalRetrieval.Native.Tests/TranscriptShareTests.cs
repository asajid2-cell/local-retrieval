using System;
using System.IO;
using CodexLocalRetrieval.Core.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

// The app is a transcript INDEXER — it reads files that live claude/codex agents are appending to. A read
// opened with the default FileShare.Read DENIES the agent's append; the agent then does NOT error, it
// SILENTLY DROPS the turn (verified empirically). These tests lock in that every app read of an agent file
// uses ReadWrite|Delete sharing, so the app can never cause that silent loss.
[TestClass]
public class TranscriptShareTests
{
    [TestMethod]
    public void SafeReadAllText_DoesNotLockOutALiveAppender()
    {
        var path = Path.Combine(Path.GetTempPath(), "share-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "{\"a\":1}\n{\"a\":2}\n");
        try
        {
            // a live agent holds the transcript open for append the whole time we index it
            using var agent = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(agent);

            var text = ArchiveService.SafeReadAllText(path);
            StringAssert.Contains(text, "\"a\":1");

            // and the agent can STILL append during/after our read — no lock-out, so no dropped turn
            w.Write("{\"a\":3}\n"); w.Flush();
            StringAssert.Contains(ArchiveService.SafeReadAllText(path), "\"a\":3");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void SafeReadLines_DoesNotLockOutALiveAppender()
    {
        var path = Path.Combine(Path.GetTempPath(), "share-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "l1\nl2\n");
        try
        {
            using var agent = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            var count = 0;
            foreach (var _ in ArchiveService.SafeReadLines(path)) count++;
            Assert.AreEqual(2, count);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void DefaultDenyWriteRead_WouldBlockTheAgent_butSharedReadDoesNot()
    {
        var path = Path.Combine(Path.GetTempPath(), "share-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(path, "x\n");
        try
        {
            // Prove the hazard is real: while a deny-write read (what File.ReadAllText uses) is open, the
            // agent's append gets a sharing violation — the exact condition that makes claude drop a turn.
            using (var denyWrite = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var blocked = false;
                try { using var _ = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite); }
                catch (IOException) { blocked = true; }
                Assert.IsTrue(blocked, "a deny-write read must block the agent's append (that's the hazard)");
            }

            // With our shared read open instead, the very same agent append succeeds (no violation).
            using (var shared = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var agent = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                using var w = new StreamWriter(agent);
                w.Write("y\n"); w.Flush();
            }
            StringAssert.Contains(File.ReadAllText(path), "y");
        }
        finally { File.Delete(path); }
    }
}
