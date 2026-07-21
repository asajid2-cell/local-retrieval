using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchiveRuntimeTests
{
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
