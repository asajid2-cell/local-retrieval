using System.Text.RegularExpressions;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The app reaches the VPS by spawning ssh.exe, and it does so on a timer for as long as it is open. A
// console subsystem process started WITHOUT CreateNoWindow allocates a visible console — so a single
// regression here does not cost one stray window, it strobes one onto the user's screen every few seconds
// for the life of the session. That failure mode is invisible in a headless test run and obvious (and
// infuriating) on a real desktop, which is exactly the kind of thing that needs a source-level lock.
//
// Both spawn sites are correct today; these tests exist to keep them that way.
[TestClass]
public sealed class SshConsoleWindowTests
{
    // Every `FileName = "ssh"` ProcessStartInfo in production code, with the block it belongs to.
    private static IEnumerable<(string label, string block)> SshStartBlocks()
    {
        var root = FindRepoRoot();
        var productionRoots = new[]
        {
            Path.Combine(root, "native", "CodexLocalRetrieval.Core"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Server"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Native"),
        };

        foreach (var dir in productionRoots)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = File.ReadAllText(file);
                // Brace-MATCH the initialiser rather than regex to its first '}'. Argument strings here are
                // interpolated ($"{stdinFlag}..."), so a naive scan stops inside the interpolation and reports
                // a site as missing flags that are in fact set a line below — a false failure, not a finding.
                // Interpolation braces are themselves balanced, so plain depth counting is correct.
                foreach (Match m in Regex.Matches(
                    text,
                    @"new\s+(?:System\.Diagnostics\.)?ProcessStartInfo\s*\{",
                    RegexOptions.Singleline))
                {
                    var open = text.IndexOf('{', m.Index);
                    var depth = 0;
                    var end = -1;
                    for (var i = open; i < text.Length; i++)
                    {
                        if (text[i] == '{') depth++;
                        else if (text[i] == '}')
                        {
                            depth--;
                            if (depth == 0) { end = i; break; }
                        }
                    }
                    if (end < 0) continue;

                    var body = text.Substring(open + 1, end - open - 1);
                    // Two sanctioned spellings: bare "ssh" (PATH-resolved, the GUI site) and the explicit
                    // system OpenSSH path (the headless bridge, which must not be hijackable via PATH).
                    var isSshSpawn = Regex.IsMatch(body, @"FileName\s*=\s*""ssh""")
                        || body.Contains("\"OpenSSH\", \"ssh.exe\"", StringComparison.Ordinal);
                    if (!isSshSpawn) continue;
                    yield return (Path.GetRelativePath(root, file), body);
                }
            }
        }
    }

    [TestMethod]
    public void EverySshSpawnSiteIsFoundBySourceScan()
    {
        // Guards the two tests below against silently passing on an empty set: if the regex or the layout
        // drifts, "no violations" would otherwise be indistinguishable from "nothing was inspected".
        var blocks = SshStartBlocks().ToList();
        Assert.IsTrue(
            blocks.Count >= 2,
            "expected to find at least the GUI (MainPage.Remote.cs) and headless (RemoteBridge.cs) ssh spawn "
            + $"sites, but the source scan found {blocks.Count}. The scan, not the code, is probably broken.");
    }

    [TestMethod]
    public void EverySshSpawnSuppressesItsConsoleWindow()
    {
        var violations = SshStartBlocks()
            .Where(b => !Regex.IsMatch(b.block, @"CreateNoWindow\s*=\s*true"))
            .Select(b => b.label)
            .ToList();

        Assert.AreEqual(
            0,
            violations.Count,
            "ssh runs on a repeating timer; a spawn site without CreateNoWindow = true pops a console window "
            + "onto the user's desktop on every poll: " + string.Join(", ", violations));
    }

    [TestMethod]
    public void EverySshSpawnDisablesShellExecuteSoCreateNoWindowIsHonoured()
    {
        // CreateNoWindow is only observed when UseShellExecute is false; with ShellExecute the flag is
        // silently ignored and the window comes back. The pair has to hold together to mean anything.
        var violations = SshStartBlocks()
            .Where(b => !Regex.IsMatch(b.block, @"UseShellExecute\s*=\s*false"))
            .Select(b => b.label)
            .ToList();

        Assert.AreEqual(
            0,
            violations.Count,
            "CreateNoWindow is ignored unless UseShellExecute = false: " + string.Join(", ", violations));
    }

    [TestMethod]
    public void CommandConsumerSshCallsUseOwnerOnlyHeaderFileAndFailingCurl()
    {
        var root = FindRepoRoot();
        var sources = new[]
        {
            Path.Combine(root, "native", "CodexLocalRetrieval.Core", "Remote", "RemoteBridge.cs"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"),
        };

        foreach (var source in sources)
        {
            var text = File.ReadAllText(source);
            StringAssert.Contains(text, "$HOME/.config/mux/command-bridge.header", source);
            StringAssert.Contains(text, "[ -f \\\"$h\\\" ]", source);
            StringAssert.Contains(text, "[ ! -L \\\"$h\\\" ]", source);
            StringAssert.Contains(text, "[ -r \\\"$h\\\" ]", source);
            StringAssert.Contains(text, "stat -c %u -- \\\"$h\\\"", source);
            StringAssert.Contains(text, "?r??------", source);
            StringAssert.Contains(text, "curl --fail --silent --show-error", source);
            StringAssert.Contains(text, "--header \\\"@$h\\\"", source);
            if (source.EndsWith("MainPage.Remote.cs", StringComparison.Ordinal))
                StringAssert.Contains(text, "if (lease.code != 0) return;", source);
            else
            {
                StringAssert.Contains(text, "if (lease.code != 0) return false;", source);
                StringAssert.Contains(text, "RunSshAsync(settings.Target, settings.Port", source);
                Assert.DoesNotContain("_settings()?.Port", text, source + " must use the settings snapshot captured by RunTransportAsync");
            }
            StringAssert.Contains(text, "IsWellFormedEnvelopeToken(commandId)", source);
            Assert.DoesNotContain("MUX_COMMAND_BRIDGE_TOKEN", text, source + " must never put the token in a command string");
        }
    }

    [TestMethod]
    public void RelayCommandIdValidationMatchesRelayAlphabetAndLength()
    {
        Assert.IsTrue(RemoteCommandProtocol.IsWellFormedEnvelopeToken("cabc123-1"));
        Assert.IsTrue(RemoteCommandProtocol.IsWellFormedEnvelopeToken(new string('A', 128)));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken(new string('A', 129)));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken("cabc/ack"));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken("cabc;touch-pwned"));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken("cabc $(id)"));
        Assert.IsFalse(RemoteCommandProtocol.IsWellFormedEnvelopeToken("cabc\"quoted"));
    }

    [TestMethod]
    public void FixtureCommandConsumerTransportUsesDedicatedHeaderToken()
    {
        var root = FindRepoRoot();
        var source = Path.Combine(root, "native", "CodexLocalRetrieval.Server", "LoopbackRelayTransport.cs");
        var text = File.ReadAllText(source);

        StringAssert.Contains(text, "CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN", source);
        StringAssert.Contains(text, "X-Mux-Command-Bridge", source);
        StringAssert.Contains(text, "string.IsNullOrWhiteSpace(token)", source);
        StringAssert.Contains(text, "IsWellFormedEnvelopeToken(commandId)", source);
        StringAssert.Contains(text, "AllowAutoRedirect = false", source);
        Assert.DoesNotContain("ex.Message", text, source + " must not return exception text that could include request headers");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find CodexLocalRetrieval.sln from " + AppContext.BaseDirectory);
    }
}
