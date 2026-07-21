using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Native.Tests;

// Opt-in integration test: drives the REAL `codex app-server` (spawns the process, needs codex logged
// in). Excluded from the normal suite. Run with:  dotnet test --filter "TestCategory=LiveCodex"
[TestClass]
public sealed class CodexAppServerLiveTests
{
    private static string CodexExe =>
        Environment.GetEnvironmentVariable("CLR_CODEX_EXE")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin", "codex.exe");

    [TestMethod]
    [TestCategory("LiveCodex")]
    public async Task Initialize_And_ListThreads_ReturnsRealSessions()
    {
        using var job = WindowsProcessJob.CreateKillOnClose();
        await using var srv = CodexAppServer.Start(CodexExe, processContainment: job);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var init = await srv.InitializeAsync(cts.Token);
        StringAssert.Contains(init.GetRawText(), "codexHome"); // initialize handshake came back

        var list = await srv.ListThreadsAsync(pageSize: 5, ct: cts.Token);
        Assert.IsTrue(list.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array, "thread/list returns a data array");
        Assert.IsTrue(data.GetArrayLength() > 0, "there are real sessions on this machine");

        var first = data[0];
        Assert.IsTrue(first.TryGetProperty("id", out _), "a thread has an id");
        Assert.IsTrue(first.TryGetProperty("path", out _), "a thread points at its rollout file");
        // name/preview/cwd are what the sidebar renders
        Assert.IsTrue(first.TryGetProperty("name", out _) || first.TryGetProperty("preview", out _));
    }
}
