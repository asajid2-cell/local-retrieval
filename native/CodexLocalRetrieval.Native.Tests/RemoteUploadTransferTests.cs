using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class RemoteUploadTransferTests
{
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
            request =>
            {
                requestJson = JsonSerializer.Serialize(request);
                return Task.FromResult("""{"t":"input-ok","s":"tab-one"}""");
            });

        Assert.IsTrue(result.Ok);
        Assert.IsTrue(result.OnPc);
        Assert.IsFalse(result.Detail.Contains(localPath, StringComparison.OrdinalIgnoreCase));
        using var request = JsonDocument.Parse(requestJson!);
        Assert.AreEqual("input", request.RootElement.GetProperty("t").GetString());
        Assert.AreEqual("tab-one", request.RootElement.GetProperty("s").GetString());
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
            request =>
            {
                requestJson = JsonSerializer.Serialize(request);
                return Task.FromResult("""{"t":"input-ok","s":"tab-one"}""");
            });

        Assert.IsTrue(result.Ok);
        Assert.IsFalse(result.Detail.Contains(localPath, StringComparison.OrdinalIgnoreCase));
        using var request = JsonDocument.Parse(requestJson!);
        var inserted = Encoding.UTF8.GetString(
            Convert.FromBase64String(request.RootElement.GetProperty("d").GetString()!));
        Assert.AreEqual($"![input.png]({localPath})", inserted);
    }
}
