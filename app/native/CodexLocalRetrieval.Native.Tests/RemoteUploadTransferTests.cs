using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class RemoteUploadTransferTests
{
    [TestMethod]
    public async Task IsolatedScp_DownloadsThroughCoreAndFencesInsertion()
    {
        var config = Environment.GetEnvironmentVariable("MUX_SCP_FIXTURE_CONFIG");
        if (string.IsNullOrEmpty(config)) Assert.Inconclusive("Run through verify-scp-isolation.py --core");
        var root = Path.GetDirectoryName(config)!;
        var frames = new List<string>();
        Task<string> Request(object frame)
        {
            var json = JsonSerializer.Serialize(frame);
            using var parsed = JsonDocument.Parse(json);
            if (parsed.RootElement.GetProperty("t").GetString() == "info")
                return Task.FromResult("""{"t":"info","caps":["inputFence","inputDurable"]}""");
            frames.Add(json);
            return Task.FromResult("""{"t":"input-ok"}""");
        }
        var destination = Path.Combine(root, "core-download");
        var result = await RemoteUploadTransfer.FetchAndInsertAsync("fixture", "ufixture-1", "fixture.txt", false,
            "fixture-tab", "path", "fixture-input", Request, "fixture-chat", "fixture-generation",
            destinationRoot: destination, sshConfigPath: config);
        Assert.IsTrue(result.Ok, result.Detail);
        var downloaded = Path.Combine(destination, "ufixture-1", "fixture.txt");
        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(Path.Combine(root, "served", "multiplex-app", "uploads", "ufixture-1", "fixture.txt")),
            await File.ReadAllBytesAsync(downloaded));
        Assert.HasCount(1, frames);
        using var input = JsonDocument.Parse(frames.Single());
        Assert.AreEqual("fixture-chat", input.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("fixture-generation", input.RootElement.GetProperty("generationId").GetString());
        Assert.AreEqual("fixture-input", input.RootElement.GetProperty("intentId").GetString());
        Assert.AreEqual(downloaded, Encoding.UTF8.GetString(Convert.FromBase64String(input.RootElement.GetProperty("d").GetString()!)));
        frames.Clear();
        foreach (var target in new[] { "wrong-user", "wrong-key", "fixture" })
        {
            var uploadId = target == "fixture" ? "umissing-1" : "ufixture-1";
            var refused = await RemoteUploadTransfer.FetchAndInsertAsync(target, uploadId, "fixture.txt", false,
                "fixture-tab", "path", "refused-input", Request, "fixture-chat", "fixture-generation",
                destinationRoot: destination, sshConfigPath: config);
            Assert.IsFalse(refused.Ok);
            Assert.IsFalse(refused.OnPc);
            Assert.HasCount(0, frames);
        }
    }

    [TestMethod]
    public async Task LostInputResponse_RemainsUncertainAndReusesExactIntent()
    {
        var inputs = new List<string>();
        Task<string> Request(object frame)
        {
            var json = JsonSerializer.Serialize(frame);
            if (json.Contains("\"info\"", StringComparison.Ordinal))
                return Task.FromResult("""{"t":"info","caps":["inputFence","inputDurable"]}""");
            inputs.Add(json);
            if (inputs.Count == 1) throw new IOException("lost response");
            return Task.FromResult("""{"t":"input-ok"}""");
        }
        var first = await RemoteUploadTransfer.InsertDownloadedPathAsync("fixture.txt", "fixture.txt", "tab", "path", "same-intent", Request, "chat", "generation");
        Assert.IsFalse(first.Ok);
        Assert.IsTrue(first.Uncertain);
        var replay = await RemoteUploadTransfer.InsertDownloadedPathAsync("fixture.txt", "fixture.txt", "tab", "path", "same-intent", Request, "chat", "generation");
        Assert.IsTrue(replay.Ok);
        Assert.AreEqual(inputs[0], inputs[1]);
    }

    [TestMethod]
    public async Task UnsettledDurableInput_RemainsUncertainOnRepeatedReconciliation()
    {
        var inputs = new List<string>();
        Task<string> Request(object frame)
        {
            var json = JsonSerializer.Serialize(frame);
            if (json.Contains("\"info\"", StringComparison.Ordinal))
                return Task.FromResult("""{"t":"info","caps":["inputFence","inputDurable"]}""");
            inputs.Add(json);
            return Task.FromResult("""{"t":"err","m":"input outcome is uncertain; the PTY write was not replayed"}""");
        }
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var result = await RemoteUploadTransfer.InsertDownloadedPathAsync("fixture.txt", "fixture.txt", "tab", "path", "same-intent", Request, "chat", "generation");
            Assert.IsFalse(result.Ok);
            Assert.IsTrue(result.Uncertain);
            Assert.IsTrue(result.OnPc);
        }
        Assert.AreEqual(inputs[0], inputs[1]);
    }

    [TestMethod]
    public async Task InsertDownloadedPathAsync_SendsPathOnlyToLocalMuxdAndReturnsOpaqueDetail()
    {
        const string localPath = @"C:\Users\Ahmed\AppData\Local\secret\input.png";
        string? requestJson = null;

        var result = await RemoteUploadTransfer.InsertDownloadedPathAsync(
            localPath,
            "input.png",
            "tab-one",
            "path",
            "fetch-intent-1",
            request =>
            {
                var json = JsonSerializer.Serialize(request);
                if (json.Contains("\"info\"", StringComparison.Ordinal))
                    return Task.FromResult("""{"t":"info","caps":["inputFence","inputDurable"]}""");
                requestJson = json;
                return Task.FromResult("""{"t":"input-ok","s":"tab-one"}""");
            }, "chat-one", "generation-one");

        Assert.IsTrue(result.Ok);
        Assert.IsTrue(result.OnPc);
        Assert.IsFalse(result.Detail.Contains(localPath, StringComparison.OrdinalIgnoreCase));
        using var request = JsonDocument.Parse(requestJson!);
        Assert.AreEqual("input", request.RootElement.GetProperty("t").GetString());
        Assert.AreEqual("tab-one", request.RootElement.GetProperty("s").GetString());
        Assert.AreEqual("fetch-intent-1", request.RootElement.GetProperty("intentId").GetString());
        Assert.AreEqual("chat-one", request.RootElement.GetProperty("sessionId").GetString());
        Assert.AreEqual("generation-one", request.RootElement.GetProperty("generationId").GetString());
        var inserted = Encoding.UTF8.GetString(
            Convert.FromBase64String(request.RootElement.GetProperty("d").GetString()!));
        Assert.AreEqual(localPath, inserted);
    }

    [TestMethod]
    public async Task InsertDownloadedPathAsync_ElementModeBuildsLocalMarkdownWithoutLeakingItInResult()
    {
        const string localPath = @"C:\Users\Ahmed\AppData\Local\secret\input.png";
        string? requestJson = null;

        var result = await RemoteUploadTransfer.InsertDownloadedPathAsync(
            localPath,
            "input.png",
            "tab-one",
            "element",
            "fetch-intent-2",
            request =>
            {
                var json = JsonSerializer.Serialize(request);
                if (json.Contains("\"info\"", StringComparison.Ordinal))
                    return Task.FromResult("""{"t":"info","caps":["inputFence","inputDurable"]}""");
                requestJson = json;
                return Task.FromResult("""{"t":"input-ok","s":"tab-one"}""");
            }, "chat-one", "generation-one");

        Assert.IsTrue(result.Ok);
        Assert.IsFalse(result.Detail.Contains(localPath, StringComparison.OrdinalIgnoreCase));
        using var request = JsonDocument.Parse(requestJson!);
        var inserted = Encoding.UTF8.GetString(
            Convert.FromBase64String(request.RootElement.GetProperty("d").GetString()!));
        Assert.AreEqual($"![input.png]({localPath})", inserted);
    }

    [TestMethod]
    public async Task InsertDownloadedPathAsync_RefusesMissingIdentityWithoutDispatch()
    {
        var result = await RemoteUploadTransfer.InsertDownloadedPathAsync("fixture.txt", "fixture.txt", "tab-one", "path", "intent",
            _ => throw new AssertFailedException("unfenced insertion must not dispatch"));
        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.OnPc);
    }

    [TestMethod]
    public async Task InsertDownloadedPathAsync_RefusesOldDaemonWithoutSendingInput()
    {
        var calls = 0;
        var result = await RemoteUploadTransfer.InsertDownloadedPathAsync("fixture.txt", "fixture.txt", "tab", "path", "intent",
            frame =>
            {
                calls++;
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(frame));
                Assert.AreEqual("info", json.RootElement.GetProperty("t").GetString());
                return Task.FromResult("""{"t":"info","caps":["input","inputDurable"]}""");
            }, "chat", "generation");
        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.OnPc);
        Assert.AreEqual(1, calls);
    }

    private const string ValidUploadId = "u2abc3-1f";

    private static string UploadRoot => Path.Combine(Path.GetTempPath(), "multiplex-uploads");

    private static IEnumerable<object[]> HostileUploadIds =>
    [
        [".."],
        ["."],
        [@"..\outside"],
        ["../outside"],
        [@"u2abc3-1f\..\..\outside"],
        [@"C:\Windows\Temp"],
        ["C:x"],
        [@"\\server\share"],
        ["//server/share"],
        [".hidden"],
        [new string('u', 260)],
        ["   "],
        [""],
        ["u2abc3 1f"],
        ["u2abc3-1f;rm -rf ~"],
        ["u2abc3-1f\u0000"],
    ];

    [TestMethod]
    public void TryResolveUploadDirectory_AcceptsPlainTokenAndResolvesInsideRoot()
    {
        var root = UploadRoot;
        Assert.IsTrue(
            RemoteUploadTransfer.TryResolveUploadDirectory(root, ValidUploadId, out var destDir, out var detail),
            $"expected a relay-minted upload id to be accepted, got: {detail}");
        Assert.AreEqual("", detail);

        var expected = Path.Combine(Path.GetFullPath(root), ValidUploadId);
        Assert.AreEqual(expected, destDir);
        Assert.IsTrue(
            destDir.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
            $"resolved path escaped the upload root: {destDir}");
        Assert.AreEqual(ValidUploadId, Path.GetFileName(destDir));
    }

    [TestMethod]
    [DynamicData(nameof(HostileUploadIds))]
    public void TryResolveUploadDirectory_RefusesTraversalAndMalformedIds(string uploadId)
    {
        var root = UploadRoot;
        Assert.IsFalse(
            RemoteUploadTransfer.TryResolveUploadDirectory(root, uploadId, out var destDir, out var detail),
            $"upload id was accepted but must be refused: '{uploadId}' -> '{destDir}'");
        Assert.AreEqual("", destDir);
        Assert.IsTrue(detail.StartsWith("file download refused:", StringComparison.Ordinal),
            $"refusal detail is not a clear refusal: '{detail}'");
    }

    [TestMethod]
    public void TryResolveUploadDirectory_ParentAndCurrentDirectoryFailContainmentNotJustGrammar()
    {
        // '..' and '.' clear the token charset, so they prove the resolved-path containment
        // check itself — not the cheaper syntactic gate in front of it.
        foreach (var uploadId in new[] { "..", "." })
        {
            Assert.IsFalse(
                RemoteUploadTransfer.TryResolveUploadDirectory(UploadRoot, uploadId, out _, out var detail));
            Assert.AreEqual("file download refused: upload id resolves outside the upload folder", detail);
        }
    }

    [TestMethod]
    public async Task FetchAndInsertAsync_RefusesTraversalIdBeforeAnyFilesystemOrScpSideEffect()
    {
        var escapeRoot = Path.Combine(Path.GetTempPath(), "r19-escape-probe");
        if (Directory.Exists(escapeRoot)) Directory.Delete(escapeRoot, recursive: true);

        foreach (var uploadId in new[] { "..", @"..\r19-escape-probe", @"C:\Windows\Temp", "   " })
        {
            var muxCalled = false;
            var result = await RemoteUploadTransfer.FetchAndInsertAsync(
                "user@host",
                uploadId,
                "input.png",
                keep: false,
                "tab-one",
                "path",
                "fetch-intent-3",
                _ =>
                {
                    muxCalled = true;
                    return Task.FromResult("""{"t":"input-ok","s":"tab-one"}""");
                });

            Assert.IsFalse(result.Ok, $"traversal upload id was accepted: '{uploadId}'");
            Assert.IsFalse(result.OnPc, $"traversal upload id reported an on-PC file: '{uploadId}'");
            Assert.IsTrue(result.Detail.StartsWith("file download refused:", StringComparison.Ordinal),
                $"refusal detail is not a clear refusal: '{result.Detail}'");
            Assert.IsFalse(muxCalled, $"muxd was contacted for a refused upload id: '{uploadId}'");
            Assert.IsFalse(Directory.Exists(escapeRoot),
                $"a directory was created outside the upload root for '{uploadId}'");
        }
    }

    [TestMethod]
    public async Task FetchAndInsertAsync_AcceptedIdNeverEscapesTheUploadRootOnScpFailure()
    {
        // A well-formed id gets past validation; scp then fails (no such host, BatchMode), and the
        // failure must be the transfer's, not a containment breach.
        var result = await RemoteUploadTransfer.FetchAndInsertAsync(
            "invalid-host.invalid",
            ValidUploadId,
            "input.png",
            keep: false,
            "tab-one",
            "path",
            "fetch-intent-4",
            _ => Task.FromResult("""{"t":"input-ok","s":"tab-one"}"""));

        var created = Path.Combine(Path.GetFullPath(UploadRoot), ValidUploadId);
        try
        {
            Assert.IsFalse(result.Ok);
            Assert.IsFalse(result.Detail.StartsWith("file download refused:", StringComparison.Ordinal),
                $"a relay-minted upload id was rejected by validation: '{result.Detail}'");
            Assert.IsTrue(Directory.Exists(created),
                $"the accepted upload id did not land inside the upload root: {created}");
        }
        finally
        {
            if (Directory.Exists(created)) Directory.Delete(created, recursive: true);
        }
    }
}
