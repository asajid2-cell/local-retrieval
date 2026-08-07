using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The owner-signing gate for auto (no-approval) commands: only a correctly HMAC-signed, fresh,
// non-replayed command is authorized. These mirror the browser's signCommand() exactly.
[TestClass]
public sealed class CommandSignerTests
{
    private const string Key = "0123456789abcdef0123456789abcdef"; // 32 chars

    private static (string canon, string nonce, long ts, string sig) Sign(string key, string text, long? tsOverride = null, string? nonce = null)
    {
        var ts = tsOverride ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        nonce ??= "n-" + Guid.NewGuid().ToString("N");
        var canon = CommandSigner.Canonical("send", "claude", "tid-1", "auto", ts, nonce, text);
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        var sig = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(canon))).ToLowerInvariant();
        return (canon, nonce, ts, sig);
    }

    [TestMethod]
    public void GoodSignature_Passes()
    {
        var s = new CommandSigner(Key);
        var (c, n, t, sig) = Sign(Key, "deploy the thing");
        Assert.IsTrue(s.Verify(c, n, t, sig, t, out _));
    }

    [TestMethod]
    public void WrongKey_Fails()
    {
        var s = new CommandSigner(Key);
        var (c, n, t, sig) = Sign("a-different-key-aaaaaaaaaaaaaaaaaa", "deploy");
        Assert.IsFalse(s.Verify(c, n, t, sig, t, out var why));
        StringAssert.Contains(why, "bad signature");
    }

    [TestMethod]
    public void TamperedCommand_Fails()
    {
        var s = new CommandSigner(Key);
        var (_, n, t, sig) = Sign(Key, "echo safe");
        // attacker swaps the text but reuses the signature
        var tampered = CommandSigner.Canonical("send", "claude", "tid-1", "auto", t, n, "rm -rf /");
        Assert.IsFalse(s.Verify(tampered, n, t, sig, t, out _));
    }

    [TestMethod]
    public void Replay_Fails()
    {
        var s = new CommandSigner(Key);
        var (c, n, t, sig) = Sign(Key, "run once");
        Assert.IsTrue(s.Verify(c, n, t, sig, t, out _));
        Assert.IsFalse(s.Verify(c, n, t, sig, t, out var why)); // same nonce again
        StringAssert.Contains(why, "replay");
    }

    [TestMethod]
    public void StaleTimestamp_Fails()
    {
        var s = new CommandSigner(Key);
        var old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10 * 60_000;
        var (c, n, t, sig) = Sign(Key, "late", tsOverride: old);
        Assert.IsFalse(s.Verify(c, n, t, sig, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), out var why));
        StringAssert.Contains(why, "stale");
    }

    [TestMethod]
    public void NoKey_DisablesAuto()
    {
        var s = new CommandSigner(null);
        Assert.IsFalse(s.Enabled);
        Assert.IsFalse(s.Verify("x", "n", 1, "sig", 1, out var why));
        StringAssert.Contains(why, "not configured");
    }

    [TestMethod]
    public void ModeSwap_SafeSignatureCannotRunAsAuto()
    {
        var s = new CommandSigner(Key);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = "n-mode";
        // owner signs a "safe" command; attacker flips the mode field to "auto" and reuses the sig.
        var safeCanon = CommandSigner.Canonical("send", "claude", "tid-1", "safe", ts, nonce, "edit a file");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Key));
        var sig = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(safeCanon))).ToLowerInvariant();
        var autoCanon = CommandSigner.Canonical("send", "claude", "tid-1", "auto", ts, nonce, "edit a file");
        Assert.IsFalse(s.Verify(autoCanon, nonce, ts, sig, ts, out _)); // safe sig != auto canonical (MAC fails, nonce not burned)
        Assert.IsTrue(s.Verify(safeCanon, nonce, ts, sig, ts, out _));  // the legitimate safe command verifies
    }

    [TestMethod]
    public void CrossOp_SendSignatureCannotApprove()
    {
        var s = new CommandSigner(Key);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = "n-cross";
        var sendCanon = CommandSigner.Canonical("send", "codex", "tid-1", "safe", ts, nonce, "echo");
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(Key));
        var sig = Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(sendCanon))).ToLowerInvariant();
        // attacker reuses that signature on an approve canonical
        var approveCanon = CommandSigner.Canonical("approve", "codex", "tid-1", "allow", ts, nonce, "42");
        Assert.IsFalse(s.Verify(approveCanon, nonce, ts, sig, ts, out _));
        // and the legitimate send still verifies
        Assert.IsTrue(s.Verify(sendCanon, nonce, ts, sig, ts, out _));
    }

    [TestMethod]
    public void Boundary_JustInsideWindowPasses_JustOutsideFails()
    {
        var s = new CommandSigner(Key);
        var (c1, n1, t1, sig1) = Sign(Key, "edge-in", nonce: "n-in");
        Assert.IsTrue(s.Verify(c1, n1, t1, sig1, t1 + 119_000, out _));   // 119s later: inside ±120s
        var (c2, n2, t2, sig2) = Sign(Key, "edge-out", nonce: "n-out");
        Assert.IsFalse(s.Verify(c2, n2, t2, sig2, t2 + 121_000, out var why)); // 121s later: outside
        StringAssert.Contains(why, "stale");
    }

    [TestMethod]
    public void LoadOrCreate_DoesNotLogKey_AndProtectsPersistedFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "command-signer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "signing.key");
            var logs = new List<string>();
            var signer = CommandSigner.LoadOrCreate(null, path, logs.Add);

            Assert.IsTrue(signer.Enabled);
            var key = File.ReadAllText(path).Trim();
            Assert.AreEqual(48, key.Length);
            Assert.IsFalse(logs.Any(line => line.Contains(key, StringComparison.Ordinal)));

            if (OperatingSystem.IsWindows())
            {
                var acl = new FileInfo(path).GetAccessControl();
                Assert.IsTrue(acl.AreAccessRulesProtected);
                var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    WindowsIdentity.GetCurrent().User!.Value,
                    new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
                    new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                };
                var rules = acl.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                    .Cast<FileSystemAccessRule>()
                    .ToArray();
                Assert.IsGreaterThanOrEqualTo(3, rules.Length);
                Assert.IsTrue(rules.All(rule =>
                    !rule.IsInherited
                    && rule.AccessControlType == AccessControlType.Allow
                    && allowed.Contains(((SecurityIdentifier)rule.IdentityReference).Value)));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
