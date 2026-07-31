using System.Text;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

/// Proves the two properties the event-driven readers rest on: a change reaches the consumer fast
/// WITHOUT a blind tick, and an idle target costs almost nothing. The idle case is driven by an
/// injected clock, so "60 seconds of silence" costs no wall-clock time.
[TestClass]
public sealed class FileWatchTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-filewatch", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [TestMethod]
    public void Append_FiresCallback_WithinTwoSeconds()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "transcript.jsonl");
            File.WriteAllText(file, "{\"seed\":1}\n");

            using var service = new FileWatchService();
            var fired = new ManualResetEventSlim(false);
            using var reg = service.WatchFile(file, () => fired.Set());

            // The fallback poll for a fresh registration is 5 s away, so anything observed inside the
            // 2 s window can ONLY have come from the FileSystemWatcher event path.
            File.AppendAllText(file, "{\"seed\":2}\n");

            Assert.IsTrue(fired.Wait(TimeSpan.FromSeconds(2)),
                "FileWatchService did not deliver an append within 2 s (events: " + service.Stats.Events +
                ", fallback polls: " + service.Stats.FallbackPolls + ")");
            Assert.IsTrue(service.Stats.Events > 0, "callback arrived but no FileSystemWatcher event was recorded");
        }
        finally { Cleanup(dir); }
    }

    [TestMethod]
    public void SixtySecondsIdle_CostsAtMostOneFallbackPoll()
    {
        var dir = NewTempDir();
        try
        {
            var file = Path.Combine(dir, "quiet.jsonl");
            File.WriteAllText(file, "{\"seed\":1}\n");

            var clock = new ManualFileWatchClock();
            using var service = new FileWatchService(new FileWatchOptions
            {
                ManualPump = true,
                Clock = clock,
                ActiveFallbackInterval = TimeSpan.FromSeconds(5),
                IdleFallbackInterval = TimeSpan.FromSeconds(60),
            });

            var callbacks = 0;
            using var reg = service.WatchFile(file, () => Interlocked.Increment(ref callbacks));

            // 60 simulated seconds, evaluated every simulated second. No file touched, no real sleep.
            for (var second = 0; second < 60; second++)
            {
                clock.Advance(TimeSpan.FromSeconds(1));
                service.Pump();
            }

            Assert.IsTrue(service.Stats.FallbackPolls <= 1,
                "idle target polled " + service.Stats.FallbackPolls + " times in 60 s; expected <= 1");
            Assert.AreEqual(0, Volatile.Read(ref callbacks), "an untouched file must not produce callbacks");
        }
        finally { Cleanup(dir); }
    }

    [TestMethod]
    public void LargeInboxAtCursorEnd_ReadsOnlyTheAppendedBytes()
    {
        var dir = NewTempDir();
        try
        {
            var inbox = Path.Combine(dir, "agent-inbox.jsonl");
            WriteFabricatedInbox(inbox, targetBytes: 50L * 1024 * 1024);
            var sizeBefore = new FileInfo(inbox).Length;
            Assert.IsTrue(sizeBefore >= 50L * 1024 * 1024, "fixture should be at least 50 MB, was " + sizeBefore);

            // Cursor parked at end-of-file, exactly where a running app's cursor sits.
            var cursor = new AppendCursorState(sizeBefore, 0);
            var append = "{\"op\":\"favorite\",\"pad\":\"" + new string('z', 900) + "\"}\n";
            File.AppendAllText(inbox, append, new UTF8Encoding(false));

            var result = AppendCursor.ReadNewLines(inbox, cursor);

            Assert.AreEqual(1, result.Lines.Count, "expected exactly the appended line");
            Assert.IsTrue(result.BytesRead <= 64 * 1024,
                "read " + result.BytesRead + " bytes off a 50 MB inbox for a 1 KB append; expected <= 65536");
            Assert.AreEqual(new FileInfo(inbox).Length, result.NextOffset);
            Assert.IsFalse(result.Restarted);
        }
        finally { Cleanup(dir); }
    }

    [TestMethod]
    public void LineCountCursor_MigratesToByteOffset_WithoutReplayOrSkip()
    {
        var dir = NewTempDir();
        try
        {
            var inbox = Path.Combine(dir, "agent-inbox.jsonl");
            var cursorPath = Path.Combine(dir, "agent-inbox.cursor");

            // 12 CRLF-terminated commands; the first 10 were processed and acked by the old build.
            var sb = new StringBuilder();
            for (var i = 1; i <= 12; i++) sb.Append("{\"op\":\"favorite\",\"requestId\":\"cmd-" + i + "\"}\r\n");
            File.WriteAllText(inbox, sb.ToString(), new UTF8Encoding(false));
            File.WriteAllText(cursorPath, "10");                     // v1 format: a bare line count

            var migrated = AppendCursor.Load(cursorPath, inbox);
            Assert.AreEqual(10, migrated.Line, "migration must carry the processed-line count forward");
            Assert.IsTrue(migrated.Offset > 0 && migrated.Offset < new FileInfo(inbox).Length,
                "migrated offset should land between the 10th and 12th line, was " + migrated.Offset);

            var persisted = File.ReadAllText(cursorPath);
            StringAssert.StartsWith(persisted, "offset:", "the cursor file should have been rewritten in v2 form");

            // Re-loading must be a no-op: no second migration, no drift.
            Assert.AreEqual(migrated, AppendCursor.Load(cursorPath, inbox));

            var result = AppendCursor.ReadNewLines(inbox, migrated);
            Assert.AreEqual(2, result.Lines.Count, "exactly the 2 unprocessed lines should surface");
            StringAssert.Contains(result.Lines[0], "cmd-11", "line 11 was skipped or replayed");
            StringAssert.Contains(result.Lines[1], "cmd-12", "line 12 was skipped or replayed");
            Assert.IsFalse(result.Lines[0].EndsWith('\r'), "CRLF terminator leaked into the parsed line");
            Assert.AreEqual(12, result.NextLine, "line numbering must stay absolute for acks");
            Assert.AreEqual(new FileInfo(inbox).Length, result.NextOffset);

            // And nothing is replayed on the next poll.
            var again = AppendCursor.ReadNewLines(inbox, new AppendCursorState(result.NextOffset, result.NextLine));
            Assert.AreEqual(0, again.Lines.Count);
        }
        finally { Cleanup(dir); }
    }

    [TestMethod]
    public void TruncatedInbox_RestartsFromZero_AndSignalsReset()
    {
        var dir = NewTempDir();
        try
        {
            var inbox = Path.Combine(dir, "agent-inbox.jsonl");
            File.WriteAllText(inbox, "{\"op\":\"a\"}\n{\"op\":\"b\"}\n", new UTF8Encoding(false));
            var stale = new AppendCursorState(new FileInfo(inbox).Length + 4096, 99);

            File.WriteAllText(inbox, "{\"op\":\"fresh\"}\n", new UTF8Encoding(false));
            var result = AppendCursor.ReadNewLines(inbox, stale);

            Assert.IsTrue(result.Restarted, "a cursor past EOF means the inbox was reset");
            Assert.AreEqual(1, result.Lines.Count);
            StringAssert.Contains(result.Lines[0], "fresh");
            Assert.AreEqual(1, result.NextLine, "the line counter must reset with the offset");
        }
        finally { Cleanup(dir); }
    }

    private static void WriteFabricatedInbox(string path, long targetBytes)
    {
        var line = Encoding.UTF8.GetBytes("{\"op\":\"noop\",\"pad\":\"" + new string('x', 2000) + "\"}\n");
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan);
        long written = 0;
        while (written < targetBytes)
        {
            fs.Write(line, 0, line.Length);
            written += line.Length;
        }
        fs.Flush(true);
    }
}
