using System.Text.Json;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class MuxIdentityTransferTests
{
    [TestMethod]
    public async Task ExecuteAsync_RemovesEveryAliasEquivalentOldRow()
    {
        var killedRows = new List<string>();
        var result = await MuxIdentityTransfer.ExecuteAsync(
            "requested",
            "canonical",
            new[] { "alias" },
            message =>
            {
                using var doc = JsonDocument.Parse(JsonSerializer.Serialize(message));
                var type = doc.RootElement.GetProperty("t").GetString();
                if (type == "ls")
                    return Task.FromResult("""
                        {"list":[
                          {"name":"old-a","alive":false,"sessionId":"canonical","aliases":[]},
                          {"name":"old-b","alive":true,"sessionId":"other","aliases":["alias"]}
                        ]}
                        """);
                killedRows.Add(doc.RootElement.GetProperty("s").GetString() ?? "");
                return Task.FromResult("""{"t":"killed"}""");
            },
            () => (true, new(StringComparer.OrdinalIgnoreCase), ""),
            _ => (true, "killed"),
            (_, _) => (true, new(), ""));

        Assert.IsTrue(result.Ok, result.Detail);
        CollectionAssert.AreEquivalent(new[] { "old-a", "old-b" }, killedRows);
    }

    [TestMethod]
    public async Task ExecuteAsync_DesiredLivePreservesMuxOwnerAndKillsEveryExternalWriter()
    {
        var scans = 0;
        var killedPids = new List<int>();
        var result = await MuxIdentityTransfer.ExecuteAsync(
            "requested",
            "canonical",
            Array.Empty<string>(),
            _ => Task.FromResult("""
                {"list":[{"name":"requested","alive":true,"sessionId":"canonical","aliases":[]}]}
                """),
            () =>
            {
                scans++;
                var live = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase)
                {
                    ["canonical"] = scans == 1 ? new() { 10, 20, 30 } : new() { 10 }
                };
                return (true, live, "");
            },
            pid =>
            {
                killedPids.Add(pid);
                return (true, "killed");
            },
            (_, _) => (true, new() { 10 }, ""));

        Assert.IsTrue(result.Ok, result.Detail);
        Assert.IsTrue(result.AlreadyOwned);
        CollectionAssert.AreEquivalent(new[] { 20, 30 }, killedPids);
    }

    [TestMethod]
    public async Task ExecuteAsync_ReplayWindowDoesNotKillRequestedMuxOwner()
    {
        var killedPids = new List<int>();
        var result = await MuxIdentityTransfer.ExecuteAsync(
            "requested",
            "canonical",
            Array.Empty<string>(),
            _ => Task.FromResult("""
                {"list":[{"name":"requested","alive":false,"sessionId":"canonical","aliases":[]}]}
                """),
            () => (true, new(StringComparer.OrdinalIgnoreCase)
            {
                ["canonical"] = new() { 44 }
            }, ""),
            pid =>
            {
                killedPids.Add(pid);
                return (true, "killed");
            },
            (_, _) => (true, new() { 44 }, ""));

        Assert.IsTrue(result.Ok, result.Detail);
        Assert.IsFalse(result.AlreadyOwned);
        Assert.IsEmpty(killedPids);
    }

    [TestMethod]
    public async Task ExecuteAsync_FailsClosedWhenMuxCustodyCannotBeVerified()
    {
        var result = await MuxIdentityTransfer.ExecuteAsync(
            "requested",
            "canonical",
            Array.Empty<string>(),
            _ => Task.FromResult("""
                {"list":[{"name":"requested","alive":true,"sessionId":"canonical","aliases":[]}]}
                """),
            () => (true, new(StringComparer.OrdinalIgnoreCase)
            {
                ["canonical"] = new() { 55 }
            }, ""),
            _ => (true, "killed"),
            (_, _) => (false, new(), "custody unavailable"));

        Assert.IsFalse(result.Ok);
        Assert.Contains("custody unavailable", result.Detail);
    }
}
