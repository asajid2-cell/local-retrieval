using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchiveRuntimeTests
{
    [TestMethod]
    public async Task WatcherRefresh_MakesNewRolloutVisibleToWarmRuntime_AndPreservesOrganization()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        var existing = fixture.Archive.Store.Sessions["existing-1"];
        existing.CustomTitle = "Keep this name";
        existing.Pinned = true;
        await fixture.Archive.SaveAsync();

        var path = fixture.WriteRollout("rollout-new.jsonl", "new-1", "new work");
        await WaitUntilAsync(() => fixture.Archive.Store.Sessions.ContainsKey("new-1"));

        var count = await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        Assert.AreEqual(2, count);
        Assert.AreEqual("Keep this name", fixture.Archive.Store.Sessions["existing-1"].CustomTitle);
        Assert.IsTrue(fixture.Archive.Store.Sessions["existing-1"].Pinned);
        Assert.IsTrue(fixture.Archive.Store.FileStamps.ContainsKey(path));
    }

    [TestMethod]
    public async Task WatcherRefresh_DoesNotStampPartialRollout_AndConvergesAfterCompletion()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        var path = Path.Combine(fixture.Root, "rollout-partial.jsonl");
        await File.WriteAllTextAsync(path,
            "{\"timestamp\":\"2026-08-27T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"partial-1\",\"cwd\":\"z:/proj\"}}\n"
            + "{\"timestamp\":\"2026-08-27T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"fin");
        await WaitUntilAsync(() => !fixture.Archive.Store.FileStamps.ContainsKey(path));

        await File.AppendAllTextAsync(path, "ished\"}}\n");
        await WaitUntilAsync(() => fixture.Archive.Store.Sessions.ContainsKey("partial-1"));
        await WaitUntilAsync(() => fixture.Archive.Store.FileStamps.ContainsKey(path));
        Assert.AreEqual(1, fixture.Archive.Store.Sessions["partial-1"].UserMessageCount);
        Assert.AreEqual("finished", fixture.Archive.Store.Sessions["partial-1"].LastUserMessage);

        // The completed file was stamped only after the retry parsed the completed tail.
    }

    [TestMethod]
    public async Task UseAsync_DoesNotRescanWarmRuntimeUntilWatcherMarksRefresh()
    {
        using var fixture = new RuntimeFixture();
        await fixture.Runtime.UseAsync((archive, _) => Task.FromResult(archive.Store.Sessions.Count));
        fixture.WriteRollout("rollout-late.jsonl", "late-1", "late work");

        await Task.Delay(100);
        Assert.IsFalse(fixture.Archive.Store.Sessions.ContainsKey("late-1"), "a warm read must not perform a recursive scan on every request");

        fixture.Runtime.MarkRefreshPending();
        await WaitUntilAsync(() => fixture.Archive.Store.Sessions.ContainsKey("late-1"));
    }

    [TestMethod]
    public async Task UseAsync_SerializesEveryArchiveReaderAndWriter()
    {
        using var store = new TempStore();
        var archive = new ArchiveService(storePath: store.Path);
        var runtime = new ArchiveRuntime(archive, syncOnLoad: false);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = false;

        var first = runtime.UseAsync(async (_, _) =>
        {
            firstEntered.SetResult();
            await releaseFirst.Task;
            return 1;
        });
        await firstEntered.Task;

        var second = runtime.UseAsync((_, _) =>
        {
            secondEntered = true;
            return Task.FromResult(2);
        });

        await Task.Delay(100);
        Assert.IsFalse(secondEntered, "a second archive operation entered while the first still owned the runtime");

        releaseFirst.SetResult();
        Assert.AreEqual(1, await first);
        Assert.AreEqual(2, await second);
    }

    [TestMethod]
    public async Task TryUnloadIfIdleAsync_WaitsForActiveArchiveOperation()
    {
        using var store = new TempStore();
        var archive = new ArchiveService(storePath: store.Path);
        var runtime = new ArchiveRuntime(archive, syncOnLoad: false);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var active = runtime.UseAsync(async (_, _) =>
        {
            entered.SetResult();
            await release.Task;
            return true;
        });
        await entered.Task;

        var unload = runtime.TryUnloadIfIdleAsync(TimeSpan.Zero);
        await Task.Delay(100);
        Assert.IsFalse(unload.IsCompleted, "idle unload raced an active archive reader");
        Assert.IsTrue(runtime.IsLoaded);

        release.SetResult();
        await active;
        Assert.IsTrue(await unload);
        Assert.IsFalse(runtime.IsLoaded);
        Assert.AreEqual(-1, runtime.SessionCount);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("condition did not become true before the 10 second timeout");
    }

    private sealed class RuntimeFixture : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "clr-archive-runtime-watch-" + Guid.NewGuid().ToString("N"));

        public RuntimeFixture()
        {
            Directory.CreateDirectory(_directory);
            Root = System.IO.Path.Combine(_directory, "sessions");
            Directory.CreateDirectory(Root);
            StorePath = System.IO.Path.Combine(_directory, "store.json");
            File.WriteAllText(StorePath, "{\"sessions\":{},\"settings\":{\"sources\":[{\"tool\":\"codex\",\"root\":\"" + Root.Replace("\\", "\\\\") + "\"}]},\"collections\":{}}");
            Archive = new ArchiveService(storePath: StorePath, codexSessionsRoot: Root);
            Archive.Store.Settings.Sources.Clear();
            Archive.Store.Settings.Sources.Add(new CodexLocalRetrieval.Core.Models.SessionSource { Tool = "codex", Root = Root });
            WriteRollout("rollout-existing.jsonl", "existing-1", "existing work");
            Runtime = new ArchiveRuntime(Archive, syncOnLoad: true);
        }

        public string Root { get; }
        public string StorePath { get; }
        public ArchiveService Archive { get; }
        public ArchiveRuntime Runtime { get; }

        public string WriteRollout(string fileName, string id, string userText)
        {
            var path = System.IO.Path.Combine(Root, fileName);
            File.WriteAllLines(path, new[]
            {
                "{\"timestamp\":\"2026-08-27T00:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"cwd\":\"z:/proj\"}}",
                "{\"timestamp\":\"2026-08-27T00:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"" + userText + "\"}}",
                "{\"timestamp\":\"2026-08-27T00:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"on it\"}}",
            });
            return path;
        }

        public void Dispose()
        {
            Runtime.Dispose();
            try { Directory.Delete(_directory, recursive: true); } catch { }
        }
    }

    private sealed class TempStore : IDisposable
    {
        private readonly string _directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "clr-archive-runtime-" + Guid.NewGuid().ToString("N"));

        public TempStore()
        {
            Directory.CreateDirectory(_directory);
            Path = System.IO.Path.Combine(_directory, "store.json");
            File.WriteAllText(Path, """{"sessions":{},"settings":{},"collections":{}}""");
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(_directory, recursive: true); }
            catch { }
        }
    }
}
