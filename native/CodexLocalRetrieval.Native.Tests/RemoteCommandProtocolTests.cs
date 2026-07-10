using CodexLocalRetrieval.Core.Remote;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class RemoteCommandProtocolTests
{
    [DataTestMethod]
    [DataRow("rename", "idempotent")]
    [DataRow("addtocollection", "idempotent")]
    [DataRow("fetchfile", "intent-fenced")]
    [DataRow("startmux", "intent-fenced")]
    [DataRow("transcript", "read-only")]
    [DataRow("kill", "refused")]
    public void IsReplaySafe_AcceptsOnlyDeclaredCommandPolicyPairs(string type, string policy)
    {
        Assert.IsTrue(RemoteCommandProtocol.IsReplaySafe(type, policy));
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe(type, "idempotent-but-wrong"));
    }

    [TestMethod]
    public void IsReplaySafe_RefusesUnknownCommandsAndMissingPolicies()
    {
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe("future-mutation", "idempotent"));
        Assert.IsFalse(RemoteCommandProtocol.IsReplaySafe("rename", ""));
    }

    [TestMethod]
    public void AckSucceeded_RequiresAnExplicitSuccessfulJsonAcknowledgement()
    {
        Assert.IsTrue(RemoteCommandProtocol.AckSucceeded("""{"ok":true,"deduplicated":true}"""));
        Assert.IsFalse(RemoteCommandProtocol.AckSucceeded("""{"error":"lease expired"}"""));
        Assert.IsFalse(RemoteCommandProtocol.AckSucceeded(""));
        Assert.IsFalse(RemoteCommandProtocol.AckSucceeded("not json"));
    }
}
