using System;
using System.Collections.Generic;
using System.IO;
using MuxTee;

namespace MuxTee.Tests;

// The pieces that do not need a console: the pure decisions and the data structures the hot path rides
// on. These run anywhere; the console-dependent P1 acceptance lives in TeeAcceptanceTests.
[TestClass]
public class UnitTests
{
    [TestMethod]
    public void Passthrough_WhenNotAConsole_RegardlessOfEnv()
    {
        var env = new Dictionary<string, string>();
        Assert.AreEqual(LaunchMode.Passthrough,
            PassthroughDecisions.Decide(stdinIsConsole: false, stdoutIsConsole: true, env.GetValueOrDefault));
        Assert.AreEqual(LaunchMode.Passthrough,
            PassthroughDecisions.Decide(stdinIsConsole: true, stdoutIsConsole: false, env.GetValueOrDefault));
    }

    [TestMethod]
    public void Passthrough_WhenDisabledOrNested()
    {
        Assert.AreEqual(LaunchMode.Passthrough,
            PassthroughDecisions.Decide(true, true,
                new Dictionary<string, string> { ["MUXTEE_DISABLE"] = "1" }.GetValueOrDefault));
        Assert.AreEqual(LaunchMode.Passthrough,
            PassthroughDecisions.Decide(true, true,
                new Dictionary<string, string> { ["MUXTEE_ACTIVE"] = "1" }.GetValueOrDefault));
    }

    [TestMethod]
    public void Tee_WhenConsoleAndNoEscapeHatch()
    {
        Assert.AreEqual(LaunchMode.Tee, PassthroughDecisions.Decide(true, true, _ => null));
        // A falsey value must NOT disable - a stray MUXTEE_DISABLE=0 in a shell is not an opt-out.
        Assert.AreEqual(LaunchMode.Tee,
            PassthroughDecisions.Decide(true, true,
                new Dictionary<string, string> { ["MUXTEE_DISABLE"] = "0" }.GetValueOrDefault));
    }

    [TestMethod]
    [DataRow("1", true)]
    [DataRow("true", true)]
    [DataRow("TRUE", true)]
    [DataRow("yes", true)]
    [DataRow("on", true)]
    [DataRow(" 1 ", true)]
    [DataRow("0", false)]
    [DataRow("false", false)]
    [DataRow("", false)]
    [DataRow(null, false)]
    public void IsSet_Truthiness(string? value, bool expected)
        => Assert.AreEqual(expected, PassthroughDecisions.IsSet(value));

    [TestMethod]
    public void Ring_AppendAndSnapshot_RoundTrips()
    {
        var ring = new RingBuffer(16);
        ring.Append(new byte[] { 1, 2, 3 });
        ring.Append(new byte[] { 4, 5 });
        CollectionAssert.AreEqual(new byte[] { 1, 2, 3, 4, 5 }, ring.Snapshot());
    }

    [TestMethod]
    public void Ring_DropsOldestOnOverflow()
    {
        var ring = new RingBuffer(8);
        ring.Append(new byte[] { 1, 2, 3, 4, 5, 6 });
        ring.Append(new byte[] { 7, 8, 9, 10 });   // 10 bytes into an 8-byte ring
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6, 7, 8, 9, 10 }, ring.Snapshot());
    }

    [TestMethod]
    public void Ring_OversizedChunk_KeepsTail()
    {
        var ring = new RingBuffer(4);
        ring.Append(new byte[] { 1, 2, 3, 4, 5, 6 });   // whole chunk bigger than the ring
        CollectionAssert.AreEqual(new byte[] { 3, 4, 5, 6 }, ring.Snapshot());
    }

    [TestMethod]
    public void QuoteArg_LeavesSimpleArgsAlone()
        => Assert.AreEqual("hello", ChildProcess.QuoteArg("hello"));

    [TestMethod]
    public void QuoteArg_QuotesArgsWithSpaces()
        => Assert.AreEqual("\"a b\"", ChildProcess.QuoteArg("a b"));

    [TestMethod]
    public void QuoteArg_EscapesQuotes()
        => Assert.AreEqual("\"a\\\"b\"", ChildProcess.QuoteArg("a\"b"));

    [TestMethod]
    public void BuildCommandLine_JoinsImageAndArgs()
    {
        var spec = new LaunchSpec { Image = "pwsh", Args = new[] { "-NoLogo", "-Command", "echo hi" } };
        Assert.AreEqual("pwsh -NoLogo -Command \"echo hi\"", ChildProcess.BuildCommandLine(spec));
    }

    [TestMethod]
    public void Parse_NoArgs_UsesDefaultShell()
    {
        var spec = CommandLine.Parse(Array.Empty<string>());
        Assert.IsFalse(string.IsNullOrEmpty(spec.Image));
        Assert.IsEmpty(spec.Args);
    }

    [TestMethod]
    public void Parse_Separator_KeepsChildArgsVerbatim()
    {
        var spec = CommandLine.Parse(new[] { "--", "cmd.exe", "-c", "--weird" });
        Assert.AreEqual("cmd.exe", spec.Image);
        CollectionAssert.AreEqual(new[] { "-c", "--weird" }, (System.Collections.ICollection)spec.Args);
    }

    // P4 regression: the ptyHost diagnostic must report the bundled host only when conpty.dll can find
    // its OpenConsole.exe, and must say "inbox" when it cannot. bundledConpty=True is true either way
    // (the DLL's entry point resolved), so this is the flag that actually catches a host-less install.
    [TestMethod]
    public void PseudoConsole_ProbePtyHost_ReportsBundledWhenHostStaged()
    {
        var root = Path.Combine(Path.GetTempPath(), "muxtee-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "x64"));
        try
        {
            File.WriteAllText(Path.Combine(root, "x64", "OpenConsole.exe"), "stub");
            StringAssert.Contains(PseudoConsole.ProbePtyHost(root), "bundled");
            StringAssert.Contains(PseudoConsole.ProbePtyHost(root), "x64");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public void PseudoConsole_ProbePtyHost_ReportsInboxWhenHostMissing()
    {
        var root = Path.Combine(Path.GetTempPath(), "muxtee-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var reported = PseudoConsole.ProbePtyHost(root);
            // The message does say "no bundled OpenConsole.exe", so assert on the host it names first,
            // which is what the log reader keys on.
            Assert.IsTrue(reported.StartsWith("conhost.exe (inbox"), "expected the inbox host first: " + reported);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
