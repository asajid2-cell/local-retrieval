using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class SessionEventLedgerTests
{
    [TestMethod]
    public void TryAppend_ReadRecent_ReturnsNewestFirstValidJson()
    {
        using var dir = NewTempDir();
        var options = Options(dir.Path, DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("old", "old summary", "s1", at: DateTimeOffset.Parse("2026-07-08T00:00:01Z")), out var d1, options), d1);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("new", "new summary", "s1", at: DateTimeOffset.Parse("2026-07-08T00:00:02Z")), out var d2, options), d2);

        var lines = File.ReadAllLines(EventFile(dir.Path, options.EffectiveNow));
        Assert.AreEqual(2, lines.Length);
        foreach (var line in lines)
            Assert.AreEqual(JsonValueKind.Object, JsonDocument.Parse(line).RootElement.ValueKind);

        var events = SessionEventLedger.ReadRecent(10, options);
        Assert.AreEqual(2, events.Count);
        Assert.AreEqual("new", events[0].Kind);
        Assert.AreEqual("old", events[1].Kind);
    }

    [TestMethod]
    public void ReadForSession_MatchesStructuredAliasesAcrossWholeLedger()
    {
        using var dir = NewTempDir();
        var options = Options(dir.Path);
        for (var i = 0; i < 12; i++)
            Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("noise." + i, "noise", "noise-" + i), out _, options));
        var target = SessionEventLedger.Create("mux.started", "started", "parent-id", sessionIds: new[] { "parent-id", "child-id" });
        Assert.IsTrue(SessionEventLedger.TryAppend(target, out var detail, options), detail);

        var matches = SessionEventLedger.ReadForSession("child-id", max: 5, options: options);

        Assert.AreEqual(1, matches.Count);
        Assert.AreEqual("mux.started", matches[0].Kind);
    }

    [TestMethod]
    public void Create_RedactsSecretsAndDropsRawPathCommandDetails()
    {
        var ev = SessionEventLedger.Create(
            "resume.started",
            @"started with api_key=sk-FAKEexampleKEYnotreal0000000 from C:\Users\Ahmed\secret\chat.jsonl",
            "s1",
            details: new Dictionary<string, string>
            {
                ["command"] = "codex resume s1",
                ["cwd"] = @"C:\Users\Ahmed\repo",
                ["sourcePath"] = @"C:\Users\Ahmed\.claude\projects\x\s1.jsonl",
                ["safe"] = "token=ghp_FAKEexampleKEYnotreal0000000"
            });

        Assert.IsFalse(ev.Details.ContainsKey("command"));
        Assert.IsFalse(ev.Details.ContainsKey("cwd"));
        Assert.IsFalse(ev.Details.ContainsKey("sourcePath"));
        StringAssert.DoesNotMatch(ev.Summary, new System.Text.RegularExpressions.Regex(@"C:\\Users\\Ahmed"));
        StringAssert.DoesNotMatch(ev.Details["safe"], new System.Text.RegularExpressions.Regex("ghp_"));
        StringAssert.Contains(ev.Summary, "[path]");
        StringAssert.Contains(ev.Details["safe"], "[redacted]");
    }

    [TestMethod]
    public void ReadRecent_RedactsLegacyRawLedgerLinesOnRead()
    {
        using var dir = NewTempDir();
        var now = DateTimeOffset.Parse("2026-07-08T00:00:00Z");
        var file = EventFile(dir.Path, now);
        Directory.CreateDirectory(dir.Path);
        File.WriteAllText(file, JsonSerializer.Serialize(new
        {
            id = "legacy",
            at = now.ToString("O"),
            kind = "resume.failed",
            severity = "error",
            source = "test",
            sessionId = "s1",
            summary = @"failed with token=ghp_FAKEexampleKEYnotreal0000000 at C:\Users\Ahmed\repo\chat.jsonl",
            details = new Dictionary<string, string>
            {
                ["command"] = "codex resume s1",
                ["sourcePath"] = @"C:\Users\Ahmed\.claude\projects\x\s1.jsonl",
                ["safe"] = "api_key=sk-FAKEexampleKEYnotreal0000000"
            }
        }) + "\n");

        var ev = SessionEventLedger.ReadRecent(5, Options(dir.Path, now)).Single();

        StringAssert.DoesNotMatch(ev.Summary, new System.Text.RegularExpressions.Regex(@"C:\\Users\\Ahmed"));
        Assert.IsFalse(ev.Details.ContainsKey("command"));
        Assert.IsFalse(ev.Details.ContainsKey("sourcePath"));
        StringAssert.Contains(ev.Summary, "[path]");
        StringAssert.Contains(ev.Details["safe"], "[redacted]");
    }

    [TestMethod]
    public void ReadRecent_CanReadWhileFileIsOpenForAppend()
    {
        using var dir = NewTempDir();
        var options = Options(dir.Path);
        Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("one", "one", "s1"), out var detail, options), detail);
        var file = EventFile(dir.Path, options.EffectiveNow);

        using var writer = File.Open(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var events = SessionEventLedger.ReadRecent(10, options);

        Assert.AreEqual(1, events.Count);
        Assert.AreEqual("one", events[0].Kind);
    }

    [TestMethod]
    public void TryAppend_RespectsNamedMutexTimeout()
    {
        using var dir = NewTempDir();
        var now = DateTimeOffset.Parse("2026-07-08T00:00:00Z");
        var options = Options(dir.Path, now) with { LockTimeout = TimeSpan.FromMilliseconds(30) };
        Directory.CreateDirectory(dir.Path);
        using var ready = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, MutexName(EventFile(dir.Path, now)));
            Assert.IsTrue(mutex.WaitOne(TimeSpan.FromSeconds(1)));
            ready.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            mutex.ReleaseMutex();
        });
        Assert.IsTrue(ready.Wait(TimeSpan.FromSeconds(1)));
        try
        {
            Assert.IsFalse(SessionEventLedger.TryAppend(SessionEventLedger.Create("blocked", "blocked"), out var detail, options));
            StringAssert.Contains(detail, "lock timed out");
        }
        finally
        {
            release.Set();
            holder.Wait(TimeSpan.FromSeconds(2));
        }
    }

    [TestMethod]
    public void TryAppend_ConcurrentCallersProduceUncorruptedLines()
    {
        using var dir = NewTempDir();
        var options = Options(dir.Path);

        Parallel.For(0, 64, i =>
        {
            Assert.IsTrue(SessionEventLedger.TryAppend(SessionEventLedger.Create("event." + i, "summary", "s-" + i), out var detail, options), detail);
        });

        var lines = File.ReadAllLines(EventFile(dir.Path, options.EffectiveNow));
        Assert.AreEqual(64, lines.Length);
        foreach (var line in lines)
            Assert.IsFalse(string.IsNullOrWhiteSpace(JsonDocument.Parse(line).RootElement.GetProperty("kind").GetString()));
    }

    [TestMethod]
    public async Task ArchiveService_SaveAsync_PreservesMuxTabHistoryAndMeta()
    {
        using var dir = NewTempDir();
        var storePath = Path.Combine(dir.Path, "store.json");
        var svc = new ArchiveService(storePath: storePath);
        svc.Store.MuxTabHistory["tab-a"] = new MuxTabRecord { Current = new MuxTabChat { Id = "s1", Tool = "codex", Title = "session one" } };
        svc.Store.MuxTabMeta["tab-a"] = new MuxTabMeta { Kind = "remote-resumed", Color = "#e879f9" };

        await svc.SaveAsync();
        var reloaded = new ArchiveService(storePath: storePath);
        await reloaded.LoadAsync();

        Assert.IsTrue(reloaded.Store.MuxTabHistory.ContainsKey("tab-a"));
        Assert.AreEqual("s1", reloaded.Store.MuxTabHistory["tab-a"].Current?.Id);
        Assert.IsTrue(reloaded.Store.MuxTabMeta.ContainsKey("tab-a"));
        Assert.AreEqual("remote-resumed", reloaded.Store.MuxTabMeta["tab-a"].Kind);
    }

    private static SessionEventLedger.Options Options(string root, DateTimeOffset? now = null)
        => new(root, now ?? DateTimeOffset.Parse("2026-07-08T00:00:00Z"));

    private static string EventFile(string root, DateTimeOffset now)
        => Path.Combine(root, "events-" + now.UtcDateTime.ToString("yyyy-MM") + ".jsonl");

    private static string MutexName(string path)
    {
        var full = Path.GetFullPath(path).ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)));
        return @"Local\CodexLocalRetrieval.SessionEventLedger." + hash[..32];
    }

    private static TempDir NewTempDir()
        => new(Path.Combine(Path.GetTempPath(), "clr-session-events-" + Guid.NewGuid().ToString("N")));

    private sealed class TempDir : IDisposable
    {
        public TempDir(string path)
        {
            Path = path;
            Directory.CreateDirectory(path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
