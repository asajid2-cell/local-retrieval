using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// transcriptfetch: one explicitly named archived chat -> paged clean transcript pushed to the relay.
// These lock the wire contract the relay side is pinned to (r.2 child 7), the size/page caps that stop
// a fetch from becoming a bulk history mirror, the redaction switch from app/REMOTE.md, and the
// tolerance for a half-written live tail that the headless reader has to survive.
[TestClass]
public sealed class TranscriptFetchTests
{
    private const string Sid = "1f2e3d4c-5b6a-7980-abcd-ef0123456789";
    // The relay command id travels with every page so a redelivered fetch is filed under the SAME
    // fetch, never mistaken for a newer one; it is an opaque envelope token like any other.
    private const string FetchId = "cmd-1f2e3d4c-5b6a-7980-abcd-ef0123456789";

    private static string WriteJsonl(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "clr-tfetch-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static ArchiveService NewArchive()
        => new(storePath: Path.Combine(Path.GetTempPath(), "clr-tfetch-store-" + Guid.NewGuid().ToString("N") + ".json"));

    private static ArchiveMessage Msg(string role, string text, string ts)
        => new() { Role = role, Kind = role, Text = text, Timestamp = ts };

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // A page must be self-describing: a reader that receives page 2 of 3 out of order can still place
    // it, and the sessionId travels with every page so a page can never be filed under the wrong chat.
    [TestMethod]
    public void BuildPages_EmitsPinnedEnvelopeWithChronologicalMessages()
    {
        var msgs = new List<ArchiveMessage>
        {
            Msg("user", "first ask", "2026-07-01T00:00:00Z"),
            Msg("assistant", "first answer", "2026-07-01T00:00:01Z"),
            Msg("user", "second ask", "2026-07-01T00:00:02Z"),
        };

        var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, redact: false);

        Assert.AreEqual(1, pages.Count);
        var root = Parse(pages[0].Json);
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(Sid, root.GetProperty("sessionId").GetString());
        Assert.AreEqual(FetchId, root.GetProperty("fetchId").GetString());
        Assert.AreEqual(1, root.GetProperty("page").GetInt32());
        Assert.AreEqual(1, root.GetProperty("pages").GetInt32());

        var wire = root.GetProperty("messages").EnumerateArray().ToArray();
        Assert.AreEqual(3, wire.Length);
        CollectionAssert.AreEqual(
            new[] { "user", "assistant", "user" },
            wire.Select(m => m.GetProperty("role").GetString()).ToArray());
        CollectionAssert.AreEqual(
            new[] { "first ask", "first answer", "second ask" },
            wire.Select(m => m.GetProperty("text").GetString()).ToArray());
        Assert.AreEqual(DateTimeOffset.Parse("2026-07-01T00:00:00Z").ToUnixTimeMilliseconds(), wire[0].GetProperty("ts").GetInt64());
    }

    // An empty or blank transcript is a DEFINITIVE capture: the relay stores it so an older parked
    // transcript for the same chat is replaced rather than left stale. One page, zero messages.
    [TestMethod]
    public void BuildPages_OnEmptyOrBlankTranscript_EmitsAuthoritativeEmptyCapture()
    {
        foreach (var (msgs, label) in new (IReadOnlyList<ArchiveMessage>?, string)[]
        {
            (new List<ArchiveMessage>(), "empty"),
            (null, "null"),
            (new List<ArchiveMessage> { Msg("user", "   ", "t"), Msg("assistant", "", "t") }, "blank"),
        })
        {
            var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, false);
            Assert.AreEqual(1, pages.Count, label);
            var root = Parse(pages[0].Json);
            Assert.AreEqual(Sid, root.GetProperty("sessionId").GetString());
            Assert.AreEqual(FetchId, root.GetProperty("fetchId").GetString());
            Assert.AreEqual(1, root.GetProperty("pages").GetInt32());
            Assert.AreEqual(0, root.GetProperty("messages").GetArrayLength(), label);
        }
    }

    // Page 1 is the NEWEST slice (the web wants the end of the conversation first) but a page reads
    // forwards inside itself, so rendering one page is never backwards.
    [TestMethod]
    public void BuildPages_PageOneIsNewestSlice_AndEachPageStaysUnderTheByteCap()
    {
        var body = new string('x', 40 * 1024);
        var msgs = Enumerable.Range(0, 40)
            .Select(i => Msg(i % 2 == 0 ? "user" : "assistant", $"m{i:D3} {body}", $"2026-07-01T00:{i:D2}:00Z"))
            .ToList();

        var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, redact: false);

        Assert.IsTrue(pages.Count > 1, "40 x 40KB messages must span multiple pages");
        foreach (var p in pages)
        {
            Assert.IsTrue(
                Encoding.UTF8.GetByteCount(p.Json) <= TranscriptFetchProjection.MaxPageBytes,
                $"page {p.Page} was {Encoding.UTF8.GetByteCount(p.Json)} bytes");
            Assert.AreEqual(pages.Count, p.Pages);
        }
        CollectionAssert.AreEqual(Enumerable.Range(1, pages.Count).ToArray(), pages.Select(p => p.Page).ToArray());

        var first = Parse(pages[0].Json).GetProperty("messages").EnumerateArray().ToArray();
        StringAssert.StartsWith(first[^1].GetProperty("text").GetString(), "m039");   // newest message overall
        var firstTexts = first.Select(m => m.GetProperty("text").GetString()!).ToArray();
        CollectionAssert.AreEqual(firstTexts.OrderBy(t => t, StringComparer.Ordinal).ToArray(), firstTexts);

        // ...and page 2 holds strictly older messages than page 1.
        var second = Parse(pages[1].Json).GetProperty("messages").EnumerateArray().ToArray();
        Assert.IsTrue(string.CompareOrdinal(
            second[^1].GetProperty("text").GetString(), first[0].GetProperty("text").GetString()) < 0);
    }

    // The cap is the fence against a full-history mirror: an enormous chat is clipped to its newest
    // MaxPages, and the OLDEST overflow is what gets dropped.
    [TestMethod]
    public void BuildPages_ClipsToMaxPages_DroppingOldestOverflow()
    {
        var body = new string('y', 150 * 1024);
        var msgs = Enumerable.Range(0, TranscriptFetchProjection.MaxPages + 12)
            .Select(i => Msg("assistant", $"m{i:D3} {body}", $"2026-07-01T{i / 60:D2}:{i % 60:D2}:00Z"))
            .ToList();

        var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, redact: false);

        Assert.AreEqual(TranscriptFetchProjection.MaxPages, pages.Count);
        var newest = Parse(pages[0].Json).GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString();
        StringAssert.StartsWith(newest, $"m{msgs.Count - 1:D3}");        // newest survived
        var oldestKept = Parse(pages[^1].Json).GetProperty("messages").EnumerateArray().First().GetProperty("text").GetString();
        Assert.IsFalse(oldestKept!.StartsWith("m000", StringComparison.Ordinal), "oldest overflow must be dropped");
    }

    // One giant paste must not stall the walk — it is truncated so a page can always close.
    [TestMethod]
    public void BuildPages_TruncatesASingleOversizeMessageRatherThanStalling()
    {
        var msgs = new List<ArchiveMessage> { Msg("user", new string('z', 900 * 1024), "2026-07-01T00:00:00Z") };

        var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, redact: false);

        Assert.AreEqual(1, pages.Count);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(pages[0].Json) <= TranscriptFetchProjection.MaxPageBytes);
        var text = Parse(pages[0].Json).GetProperty("messages").EnumerateArray().Single().GetProperty("text").GetString()!;
        StringAssert.Contains(text, "truncated for transfer");
        Assert.IsTrue(text.Length < 900 * 1024);
    }

    // app/REMOTE.md: CLR_REMOTE_REDACT_READS=1 scrubs secret shapes out of message BODIES.
    [TestMethod]
    public void BuildPages_RedactionOn_ScrubsSecretsFromMessageBodies()
    {
        const string key = "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var msgs = new List<ArchiveMessage> { Msg("user", $"here is my key {key} use it", "2026-07-01T00:00:00Z") };

        var raw = Parse(TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, redact: false)[0].Json)
            .GetProperty("messages").EnumerateArray().Single().GetProperty("text").GetString()!;
        StringAssert.Contains(raw, key);

        var scrubbed = Parse(TranscriptFetchProjection.BuildPages(Sid, FetchId, msgs, redact: true)[0].Json)
            .GetProperty("messages").EnumerateArray().Single().GetProperty("text").GetString()!;
        Assert.IsFalse(scrubbed.Contains(key, StringComparison.Ordinal), "the key must not leave the machine");
        StringAssert.Contains(scrubbed, "[redacted-secret]");
        StringAssert.Contains(scrubbed, "here is my key");   // surrounding prose survives
    }

    [TestMethod]
    public void RedactReadsEnabled_TracksTheDocumentedEnvVar()
    {
        var prior = Environment.GetEnvironmentVariable("CLR_REMOTE_REDACT_READS");
        try
        {
            Environment.SetEnvironmentVariable("CLR_REMOTE_REDACT_READS", "1");
            Assert.IsTrue(TranscriptFetchProjection.RedactReadsEnabled());
            Environment.SetEnvironmentVariable("CLR_REMOTE_REDACT_READS", "0");
            Assert.IsFalse(TranscriptFetchProjection.RedactReadsEnabled());
            Environment.SetEnvironmentVariable("CLR_REMOTE_REDACT_READS", null);
            Assert.IsFalse(TranscriptFetchProjection.RedactReadsEnabled());
        }
        finally { Environment.SetEnvironmentVariable("CLR_REMOTE_REDACT_READS", prior); }
    }

    // Opaque ids only (PLAN items 5-6). The id lands in a URL path inside a shell command line on the
    // far side of ssh; anything that could break out of it is refused, not escaped.
    [DataTestMethod]
    [DataRow("1f2e3d4c-5b6a-7980-abcd-ef0123456789", true)]
    [DataRow("rollout-2026-07-01T00-00-00.jsonl", true)]
    [DataRow("", false)]
    [DataRow("../../etc/passwd", false)]
    [DataRow("id with space", false)]
    [DataRow("id'; rm -rf /; echo '", false)]
    [DataRow("id\nnewline", false)]
    [DataRow("a/b", false)]
    public void IsOpaqueId_RefusesAnythingThatCouldEscapeAUrlOrShellWord(string id, bool ok)
        => Assert.AreEqual(ok, TranscriptFetchProjection.IsOpaqueId(id));

    [DataTestMethod]
    [DataRow("abcdefgh12345678", true)]
    [DataRow("eyJhbGci.eyJzdWIi.c2ln-_~+/=", true)]
    [DataRow("short", false)]
    [DataRow("has space here", false)]
    [DataRow("tok'en", false)]
    [DataRow("tok\r\nX-Injected: 1", false)]
    public void IsCredential_RefusesHeaderInjectionShapes(string token, bool ok)
        => Assert.AreEqual(ok, TranscriptFetchProjection.IsCredential(token));

    [TestMethod]
    public void PushCommand_TargetsTheScopedRelayEndpointOverStdin()
    {
        var cmd = TranscriptFetchProjection.PushCommand(7411, Sid, "bridge-token-abcdefgh12345678");

        StringAssert.Contains(cmd, $"http://127.0.0.1:7411/api/transcripts/{Sid}");
        StringAssert.Contains(cmd, "--data-binary @-");                 // the page rides stdin, never argv
        StringAssert.Contains(cmd, "X-Mux-Transcript-Bridge: bridge-token-abcdefgh12345678");
        StringAssert.Contains(cmd, "--fail");
        StringAssert.Contains(cmd, "Content-Type: application/json");
    }

    [TestMethod]
    public void PushCommand_RefusesNonOpaqueIdOrMalformedCredential()
    {
        Assert.ThrowsExactly<ArgumentException>(() => TranscriptFetchProjection.PushCommand(7411, "a b", "abcdefgh12345678"));
        Assert.ThrowsExactly<ArgumentException>(() => TranscriptFetchProjection.PushCommand(7411, Sid, "tok"));
        Assert.ThrowsExactly<ArgumentException>(() => TranscriptFetchProjection.PushCommand(7411, Sid, "tok\nX: 1"));
    }

    // ExtractReaderMessagesAsync filters to one role per call, so a whole chat is two reads merged.
    // OrderBy is stable, so equal timestamps keep per-role file order instead of shuffling.
    [TestMethod]
    public void MergeChronological_InterleavesBothRolesByTimestamp()
    {
        var merged = TranscriptFetchProjection.MergeChronological(
            new[] { Msg("user", "u1", "2026-07-01T00:00:00Z"), Msg("user", "u2", "2026-07-01T00:00:02Z") },
            new[] { Msg("assistant", "a1", "2026-07-01T00:00:01Z"), Msg("assistant", "a2", "2026-07-01T00:00:03Z") });

        CollectionAssert.AreEqual(new[] { "u1", "a1", "u2", "a2" }, merged.Select(m => m.Text).ToArray());
    }

    [TestMethod]
    public async Task ReaderAllRoles_PreservesFileOrderWithMissingOrEqualTimestamps()
    {
        var path = WriteJsonl(
            "{\"type\":\"user\",\"message\":{\"content\":\"first prompt\"}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":\"first answer\"}}",
            "{\"type\":\"user\",\"message\":{\"content\":\"second prompt\"}}",
            "{\"type\":\"assistant\",\"message\":{\"content\":\"second answer\"}}");
        try
        {
            var session = new ArchiveSession { Id = Sid, Tool = "claude", SourcePath = path };
            var messages = await NewArchive().ExtractReaderMessagesAsync(session, "all");
            var wire = Parse(TranscriptFetchProjection.BuildPages(Sid, FetchId, messages, false)[0].Json)
                .GetProperty("messages").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(new[] { "first prompt", "first answer", "second prompt", "second answer" },
                wire.Select(m => m.GetProperty("text").GetString()).ToArray());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ReaderCapture_StopsAtOpeningLengthWithoutBlockingAppend()
    {
        var first = "{\"type\":\"user\",\"message\":{\"content\":\"first prompt\"}}";
        var path = WriteJsonl(first, first);
        try
        {
            using var capture = ArchiveService.SafeReadLines(path, captureOpeningLength: true).GetEnumerator();
            Assert.IsTrue(capture.MoveNext());
            using (var writer = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var text = new StreamWriter(writer))
                text.WriteLine("{\"type\":\"assistant\",\"message\":{\"content\":\"later answer\"}}");
            var lines = new List<string> { capture.Current };
            while (capture.MoveNext()) lines.Add(capture.Current);
            Assert.HasCount(2, lines, "capture must not chase writes appended after it opened");
            Assert.HasCount(3, ArchiveService.SafeReadLines(path).ToList(), "next refresh sees the append");
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void ReaderCapture_PathReplacementKeepsOneOpenedSource()
    {
        var path = WriteJsonl("original first", "original second");
        var replacement = WriteJsonl("replacement first", "replacement second");
        try
        {
            using var capture = ArchiveService.SafeReadLines(path, captureOpeningLength: true).GetEnumerator();
            Assert.IsTrue(capture.MoveNext());
            File.Replace(replacement, path, null);
            var lines = new List<string> { capture.Current };
            while (capture.MoveNext()) lines.Add(capture.Current);
            CollectionAssert.AreEqual(new[] { "original first", "original second" }, lines);
            CollectionAssert.AreEqual(new[] { "replacement first", "replacement second" }, ArchiveService.SafeReadLines(path).ToList());
        }
        finally { File.Delete(path); File.Delete(replacement); }
    }

    [TestMethod]
    public void ReaderCapture_TruncationRefusesPartialSuccess()
    {
        var path = WriteJsonl("first", new string('x', 64 * 1024));
        try
        {
            using var capture = ArchiveService.SafeReadLines(path, captureOpeningLength: true).GetEnumerator();
            Assert.IsTrue(capture.MoveNext());
            using (var writer = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                writer.SetLength(0);
            Assert.ThrowsAsync<IOException>(() => Task.Run(() => { while (capture.MoveNext()) { } })).GetAwaiter().GetResult();
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ReaderAllRoles_UnreadableSourceFailsRatherThanReturningSuccess()
    {
        var path = WriteJsonl("{\"type\":\"user\",\"message\":{\"content\":\"preserve this prompt\"}}");
        try
        {
            using var owner = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var session = new ArchiveSession { Id = Sid, Tool = "claude", SourcePath = path };
            await Assert.ThrowsAsync<IOException>(() => NewArchive().ExtractReaderMessagesAsync(session, "all"));
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task ReaderAllRoles_CancellationDoesNotPublishPartialCapture()
    {
        var path = WriteJsonl("{\"type\":\"user\",\"message\":{\"content\":\"preserve this prompt\"}}");
        try
        {
            using var deadline = new CancellationTokenSource();
            deadline.Cancel();
            var session = new ArchiveSession { Id = Sid, Tool = "claude", SourcePath = path };
            await Assert.ThrowsAsync<OperationCanceledException>(() =>
                NewArchive().ExtractReaderMessagesAsync(session, "all", deadline.Token));
        }
        finally { File.Delete(path); }
    }

    // Half-written live tail: a chat still being appended to ends in a partial JSON line. The reader
    // must skip it and still return everything before it, and both roles must survive the fetch.
    [TestMethod]
    public async Task ClaudeJsonl_WithTruncatedInFlightLastLine_ProjectsEverythingBeforeIt()
    {
        var path = WriteJsonl(
            "{\"type\":\"user\",\"timestamp\":\"2026-07-01T00:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"build the thing\"}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-07-01T00:00:01Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"building it now\"}]}}",
            "{\"type\":\"user\",\"timestamp\":\"2026-07-01T00:00:02Z\",\"message\":{\"role\":\"user\",\"content\":\"ship it\"}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-07-01T00:00:03Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"tex");
        try
        {
            var archive = NewArchive();
            var session = new ArchiveSession { Id = Sid, Tool = "claude", SourcePath = path };

            var merged = await archive.ExtractReaderMessagesAsync(session, "all");
            var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, merged, redact: false);

            Assert.AreEqual(1, pages.Count);
            var wire = Parse(pages[0].Json).GetProperty("messages").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(
                new[] { "build the thing", "building it now", "ship it" },
                wire.Select(m => m.GetProperty("text").GetString()).ToArray());
            CollectionAssert.AreEqual(
                new[] { "user", "assistant", "user" },
                wire.Select(m => m.GetProperty("role").GetString()).ToArray());
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public async Task CodexRollout_WithTruncatedInFlightLastLine_ProjectsEverythingBeforeIt()
    {
        var path = WriteJsonl(
            "{\"timestamp\":\"2026-07-01T00:00:00Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"build the thing\"}}",
            "{\"timestamp\":\"2026-07-01T00:00:01Z\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"encrypted_content\":\"gAAAA-opaque\"}}",
            "{\"timestamp\":\"2026-07-01T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"building it now\"}}",
            "{\"timestamp\":\"2026-07-01T00:00:03Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_mess");
        try
        {
            var archive = NewArchive();
            var session = new ArchiveSession { Id = Sid, Tool = "codex", SourcePath = path };

            var merged = await archive.ExtractReaderMessagesAsync(session, "all");
            var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, merged, redact: false);

            Assert.AreEqual(1, pages.Count);
            var wire = Parse(pages[0].Json).GetProperty("messages").EnumerateArray().ToArray();
            CollectionAssert.AreEqual(
                new[] { "build the thing", "building it now" },
                wire.Select(m => m.GetProperty("text").GetString()).ToArray());   // opaque reasoning never ships
        }
        finally { File.Delete(path); }
    }

    // End to end with redaction ON: a secret pasted into a real Claude chat does not leave the box.
    [TestMethod]
    public async Task ClaudeJsonl_WithRedactionOn_ScrubsSecretsOutOfThePushedPage()
    {
        const string key = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
        var path = WriteJsonl(
            "{\"type\":\"user\",\"timestamp\":\"2026-07-01T00:00:00Z\",\"message\":{\"role\":\"user\",\"content\":\"token is " + key + " ok\"}}",
            "{\"type\":\"assistant\",\"timestamp\":\"2026-07-01T00:00:01Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"got it\"}]}}");
        try
        {
            var archive = NewArchive();
            var session = new ArchiveSession { Id = Sid, Tool = "claude", SourcePath = path };

            var merged = await archive.ExtractReaderMessagesAsync(session, "all");
            var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, merged, redact: true);

            Assert.AreEqual(1, pages.Count);
            Assert.IsFalse(pages[0].Json.Contains(key, StringComparison.Ordinal));
            StringAssert.Contains(pages[0].Json, "[redacted-secret]");
            StringAssert.Contains(pages[0].Json, "got it");
        }
        finally { File.Delete(path); }
    }

    // A missing source path is a normal outcome (the chat was deleted under us), not a crash. It still
    // publishes the authoritative empty capture so a stale parked transcript cannot survive.
    [TestMethod]
    public async Task MissingSourcePath_YieldsEmptyCaptureRatherThanThrowing()
    {
        var archive = NewArchive();
        var session = new ArchiveSession { Id = Sid, Tool = "claude", SourcePath = Path.Combine(Path.GetTempPath(), "clr-tfetch-absent.jsonl") };

        var merged = TranscriptFetchProjection.MergeChronological(
            await archive.ExtractReaderMessagesAsync(session, "user"),
            await archive.ExtractReaderMessagesAsync(session, "assistant"));

        var pages = TranscriptFetchProjection.BuildPages(Sid, FetchId, merged, redact: false);
        Assert.AreEqual(1, pages.Count);
        Assert.AreEqual(0, Parse(pages[0].Json).GetProperty("messages").GetArrayLength());
    }

    // transcriptfetch is an archive.read, so replay must be safe — the closed policy switch has to
    // know the type at all, and it must not classify it as a mutation.
    [TestMethod]
    public void TranscriptFetch_IsDeclaredReadOnlyInTheReplayPolicy()
    {
        Assert.IsTrue(RemoteCommandProtocol.IsReplaySafe("transcriptfetch", "read-only"));
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe("transcriptfetch", "idempotent"));
    }

    // ---- AdmitFetch: the trust fence itself ------------------------------------------------
    // MainPage is pure I/O wiring; the decision lives here, so these are the tests that actually
    // hold the amendment "one explicit opaque session id and a bounded fetch TTL; never bulk-mirror".

    private const string GoodToken = "brdg_ABCDEFGHIJKLMNOP0123456789";

    [TestMethod]
    public void AdmitFetch_AdmitsAWellFormedRequest()
    {
        var a = TranscriptFetchProjection.AdmitFetch(Sid, FetchId, GoodToken, 30_000);
        Assert.IsTrue(a.Allowed);
        Assert.AreEqual("", a.Reason);
    }

    [TestMethod]
    public void AdmitFetch_TrimsSurroundingWhitespaceOnTheId()
    {
        Assert.IsTrue(TranscriptFetchProjection.AdmitFetch("  " + Sid + "  ", FetchId, GoodToken, 30_000).Allowed);
    }

    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("a b")]           // space
    [DataRow("a\"b")]          // quote
    [DataRow("a'b")]
    [DataRow("../etc/passwd")] // slash + traversal
    [DataRow("a\rb")]
    [DataRow("a\nb")]
    [DataRow("a;rm -rf /")]
    [DataTestMethod]
    public void AdmitFetch_RejectsAnIdThatIsNotOpaque(string? id)
    {
        var a = TranscriptFetchProjection.AdmitFetch(id, FetchId, GoodToken, 30_000);
        Assert.IsFalse(a.Allowed);
        Assert.AreEqual("transcript fetch needs one explicit opaque session id", a.Reason);
    }

    // The fetch id is a second opaque value on the same wire: it names WHICH fetch a page belongs to, so
    // it is held to the identical alphabet as the session id and refused before any capture work.
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("a b")]
    [DataRow("a\"b")]
    [DataRow("../etc/passwd")]
    [DataRow("a\rb")]
    [DataRow("a\nb")]
    [DataRow("a;rm -rf /")]
    [DataTestMethod]
    public void AdmitFetch_RejectsAFetchIdThatIsNotOpaque(string? fetchId)
    {
        var a = TranscriptFetchProjection.AdmitFetch(Sid, fetchId, GoodToken, 30_000);
        Assert.IsFalse(a.Allowed);
        Assert.AreEqual("transcript fetch needs one explicit opaque fetch id", a.Reason);
    }

    [DataRow(null)]
    [DataRow("")]
    [DataRow("short")]                 // under the 8-char floor
    [DataRow("has spaces in it")]
    [DataRow("bad\ntoken-value-here")]
    [DataTestMethod]
    public void AdmitFetch_RejectsACredentialThatIsNotScoped(string? token)
    {
        var a = TranscriptFetchProjection.AdmitFetch(Sid, FetchId, token, 30_000);
        Assert.IsFalse(a.Allowed);
        Assert.AreEqual("transcript fetch needs a scoped bridge credential", a.Reason);
    }

    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(TranscriptFetchProjection.MaxFetchTtlMs + 1)]
    [DataRow(int.MaxValue)]
    [DataTestMethod]
    public void AdmitFetch_RejectsAnUnboundedTtl(int ttlMs)
    {
        var a = TranscriptFetchProjection.AdmitFetch(Sid, FetchId, GoodToken, ttlMs);
        Assert.IsFalse(a.Allowed);
        Assert.AreEqual(
            $"transcript fetch needs a bounded ttlMs in 1..{TranscriptFetchProjection.MaxFetchTtlMs}",
            a.Reason);
    }

    [DataRow(1)]
    [DataRow(TranscriptFetchProjection.MaxFetchTtlMs)]
    [DataTestMethod]
    public void AdmitFetch_TtlBoundsAreInclusive(int ttlMs)
    {
        Assert.IsTrue(TranscriptFetchProjection.AdmitFetch(Sid, FetchId, GoodToken, ttlMs).Allowed);
    }

    // Order matters: an operator debugging a bad request should be told about the id first, because
    // a bad id is the failure that means "this is not a scoped single-chat read at all".
    [TestMethod]
    public void AdmitFetch_ReportsTheSessionIdReasonFirstWhenEverythingIsWrong()
    {
        var a = TranscriptFetchProjection.AdmitFetch("bad id/../", FetchId, "x", 0);
        Assert.IsFalse(a.Allowed);
        Assert.AreEqual("transcript fetch needs one explicit opaque session id", a.Reason);
    }

    [TestMethod]
    public void AdmitFetch_ReportsTheCredentialReasonBeforeTheTtlReason()
    {
        var a = TranscriptFetchProjection.AdmitFetch(Sid, FetchId, "x", 0);
        Assert.IsFalse(a.Allowed);
        Assert.AreEqual("transcript fetch needs a scoped bridge credential", a.Reason);
    }
}
