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

    // B6: the passthrough handler must swallow Ctrl+C (0) so a ^C in the tab does not take muxtee with
    // it, and must NOT swallow Break (1) or Close (2) - there the tab really is going away.
    [TestMethod]
    public void Passthrough_SwallowsOnlyCtrlC()
    {
        Assert.IsTrue(PassthroughDecisions.SwallowsCtrl(0), "Ctrl+C must be swallowed");
        Assert.IsFalse(PassthroughDecisions.SwallowsCtrl(1), "Break must tear muxtee down");
        Assert.IsFalse(PassthroughDecisions.SwallowsCtrl(2), "Close must tear muxtee down");
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

    // Invariant 2, at the one point that enforces it: many producers (T2 keys, the net task) push
    // multi-byte sequences, and ONE consumer - the shape of T3 - writes them out. Every sequence must
    // arrive whole and in its own contiguous run, never sliced by another producer's bytes. Break the
    // "one consumer" half (drain with two threads) and the runs interleave, so this goes red.
    [TestMethod]
    public void InputQueue_OneConsumer_KeepsEachSequenceWhole()
    {
        var queue = new InputQueue(_ => { });
        const int producers = 4, perProducer = 200, seqLen = 6;
        // Each producer stamps a distinct byte value and a 0xAA terminator, so a torn run is visible.
        var expectedWhole = producers * perProducer;

        var writers = new List<System.Threading.Thread>();
        for (int p = 0; p < producers; p++)
        {
            int id = p + 1;
            writers.Add(new System.Threading.Thread(() =>
            {
                var seq = new byte[seqLen];
                for (int i = 0; i < seqLen - 1; i++) seq[i] = (byte)id;
                seq[seqLen - 1] = 0xAA;
                for (int n = 0; n < perProducer; n++) queue.Enqueue(seq);
            }) { IsBackground = true });
        }

        var outBuf = new List<byte>();
        var drain = new System.Threading.Thread(() =>
        {
            while (queue.TryTake(out var chunk)) outBuf.AddRange(chunk);
        }) { IsBackground = true };

        drain.Start();
        foreach (var w in writers) w.Start();
        foreach (var w in writers) w.Join();
        queue.Complete();
        drain.Join(5000);

        // Re-parse the flat stream into runs and check none is torn.
        int runs = 0; bool torn = false;
        for (int i = 0; i < outBuf.Count; )
        {
            byte id = outBuf[i];
            if (id == 0 || id > producers) { torn = true; break; }
            for (int j = 0; j < seqLen; j++)
                if (outBuf[i + j] != (j == seqLen - 1 ? (byte)0xAA : id)) { torn = true; break; }
            if (torn) break;
            i += seqLen; runs++;
        }
        Assert.IsFalse(torn, "a sequence was torn: bytes from two producers interleaved");
        Assert.AreEqual(expectedWhole, runs, "not every enqueued sequence came out whole");
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

    // The regression behind "up-arrow does nothing": T2 forwarded only UnicodeChar, so a key with no
    // character vanished. These pin the stock-terminal encoding for each key a terminal must send.
    [TestMethod]
    public void KeyTranslator_MapsNavigationKeysToVtSequences()
    {
        Assert.AreEqual("\x1b[A", KeyTranslator.ForKeyDown(0x26, '\0', 0), "Up (command history)");
        Assert.AreEqual("\x1b[B", KeyTranslator.ForKeyDown(0x28, '\0', 0), "Down");
        Assert.AreEqual("\x1b[C", KeyTranslator.ForKeyDown(0x27, '\0', 0), "Right");
        Assert.AreEqual("\x1b[D", KeyTranslator.ForKeyDown(0x25, '\0', 0), "Left");
        Assert.AreEqual("\x1b[H", KeyTranslator.ForKeyDown(0x24, '\0', 0), "Home");
        Assert.AreEqual("\x1b[F", KeyTranslator.ForKeyDown(0x23, '\0', 0), "End");
        Assert.AreEqual("\x1b[5~", KeyTranslator.ForKeyDown(0x21, '\0', 0), "PageUp");
        Assert.AreEqual("\x1b[6~", KeyTranslator.ForKeyDown(0x22, '\0', 0), "PageDown");
        Assert.AreEqual("\x1b[3~", KeyTranslator.ForKeyDown(0x2E, '\0', 0), "Delete");
        Assert.AreEqual("\x1b[2~", KeyTranslator.ForKeyDown(0x2D, '\0', 0), "Insert");
    }

    [TestMethod]
    public void KeyTranslator_CharacterKeysPassThrough()
    {
        Assert.AreEqual("a", KeyTranslator.ForKeyDown(0x41, 'a', 0));
        Assert.AreEqual("\t", KeyTranslator.ForKeyDown(0x09, '\t', 0));
        // Ctrl+C arrives as ETX in the character field, not as a navigation key.
        Assert.AreEqual("\x03", KeyTranslator.ForKeyDown(0x43, '\x03', KeyTranslator.LEFT_CTRL_PRESSED));
        // A key with no mapping at all must contribute nothing - never an invented byte.
        Assert.AreEqual(string.Empty, KeyTranslator.ForKeyDown(0xFF, '\0', 0), "unmapped VK");
    }

    // A terminal sends DEL for Backspace and BS for Ctrl+Backspace; the console hands us BS for both, so
    // the distinction has to be rebuilt here or Backspace deletes a whole word in a TUI.
    [TestMethod]
    public void KeyTranslator_BackspaceMatchesATerminal()
    {
        Assert.AreEqual("\x7f", KeyTranslator.ForKeyDown(0x08, '\x08', 0), "Backspace = DEL");
        Assert.AreEqual("\x7f", KeyTranslator.ForKeyDown(0x08, '\x08', KeyTranslator.SHIFT_PRESSED), "Shift+Backspace = DEL");
        Assert.AreEqual("\x08", KeyTranslator.ForKeyDown(0x08, '\x08', KeyTranslator.LEFT_CTRL_PRESSED), "Ctrl+Backspace = BS");
        Assert.AreEqual("\x7f", KeyTranslator.ForKeyDown(0x08, '\x08',
            KeyTranslator.LEFT_CTRL_PRESSED | KeyTranslator.SHIFT_PRESSED), "Ctrl+Shift+Backspace = DEL");
    }

    [TestMethod]
    public void KeyTranslator_MapsFunctionKeys()
    {
        Assert.AreEqual("\x1bOP", KeyTranslator.ForKeyDown(0x70, '\0', 0), "F1 (SS3)");
        Assert.AreEqual("\x1b[15~", KeyTranslator.ForKeyDown(0x74, '\0', 0), "F5");
        Assert.AreEqual("\x1b[24~", KeyTranslator.ForKeyDown(0x7B, '\0', 0), "F12");
        Assert.AreEqual("\x1b[15;5~", KeyTranslator.ForKeyDown(0x74, '\0', KeyTranslator.LEFT_CTRL_PRESSED), "Ctrl+F5");
    }

    // Ctrl+Arrow is word motion and Shift+Arrow is a selection in PSReadLine; sending the bare CSI form
    // would silently drop the modifier and move/select by one cell instead.
    [TestMethod]
    public void KeyTranslator_ModifiedNavigationKeys()
    {
        Assert.AreEqual("\x1b[1;5A", KeyTranslator.ForKeyDown(0x26, '\0', KeyTranslator.LEFT_CTRL_PRESSED), "Ctrl+Up");
        Assert.AreEqual("\x1b[1;2D", KeyTranslator.ForKeyDown(0x25, '\0', KeyTranslator.SHIFT_PRESSED), "Shift+Left");
        Assert.AreEqual("\x1b[1;6C", KeyTranslator.ForKeyDown(0x27, '\0',
            KeyTranslator.LEFT_CTRL_PRESSED | KeyTranslator.SHIFT_PRESSED), "Ctrl+Shift+Right");
        Assert.AreEqual("\x1b[1;3A", KeyTranslator.ForKeyDown(0x26, '\0', KeyTranslator.LEFT_ALT_PRESSED), "Alt+Up");
        Assert.AreEqual("\x1b[1;5H", KeyTranslator.ForKeyDown(0x24, '\0', KeyTranslator.LEFT_CTRL_PRESSED), "Ctrl+Home");
        Assert.AreEqual("\x1b[3;5~", KeyTranslator.ForKeyDown(0x2E, '\0', KeyTranslator.LEFT_CTRL_PRESSED), "Ctrl+Delete");
    }

    [TestMethod]
    public void KeyTranslator_ShiftTabIsBackTab()
        => Assert.AreEqual("\x1b[Z", KeyTranslator.ForKeyDown(0x09, '\t', KeyTranslator.SHIFT_PRESSED));

    [TestMethod]
    public void KeyTranslator_AltPrintableGetsEscapePrefix()
    {
        Assert.AreEqual("\x1b" + "a", KeyTranslator.ForKeyDown(0x41, 'a', KeyTranslator.LEFT_ALT_PRESSED));
        // AltGr is Ctrl+Alt and must stay a bare character, not an ESC-prefixed one.
        Assert.AreEqual("a", KeyTranslator.ForKeyDown(0x41, 'a',
            KeyTranslator.LEFT_ALT_PRESSED | KeyTranslator.LEFT_CTRL_PRESSED));
    }

    // Alt+numpad composition only: the composed character rides the ALT key-UP record.
    [TestMethod]
    public void KeyTranslator_AltNumpadKeyUpCarriesTheCharacter()
    {
        Assert.AreEqual("é", KeyTranslator.ForAltNumpadKeyUp(KeyTranslator.VK_MENU, 'é'));
        Assert.AreEqual(string.Empty, KeyTranslator.ForAltNumpadKeyUp(0x41, 'a'), "a normal key-up is a release");
        Assert.AreEqual(string.Empty, KeyTranslator.ForAltNumpadKeyUp(KeyTranslator.VK_MENU, '\0'));
    }

    // The tracker is the gate: only a child that turned mouse tracking on should have reports forwarded,
    // and the encoding mode decides the report's shape.
    [TestMethod]
    public void ScreenMode_TracksMouseModesFromChildOutput()
    {
        var t = new ScreenModeTracker();
        t.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[?1000h\x1b[?1006h"));
        Assert.IsTrue(t.MouseTracking, "1000h turns tracking on");
        Assert.IsTrue(t.Sgr, "1006h selects SGR");

        t.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[?1000l"));
        Assert.IsFalse(t.MouseTracking, "1000l turns tracking off");
        Assert.IsTrue(t.Sgr, "1006 is untouched by the 1000 reset");
    }

    [TestMethod]
    public void ScreenMode_HandlesMultiParamAndSplitSequences()
    {
        var t = new ScreenModeTracker();
        t.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[?1002;1006;1049h"));
        Assert.IsTrue(t.MouseTracking, "1002 is a tracking mode");
        Assert.IsTrue(t.Sgr);
        Assert.IsTrue(t.AlternateScreen, "1049 is the alternate screen");

        var split = new ScreenModeTracker();
        split.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[?10"));
        split.Feed(System.Text.Encoding.ASCII.GetBytes("15h"));
        Assert.IsTrue(split.Urxvt, "a sequence cut across two reads still lands");
    }

    [TestMethod]
    public void ScreenMode_IgnoresNonModeSequences()
    {
        var t = new ScreenModeTracker();
        t.Feed(System.Text.Encoding.ASCII.GetBytes("\x1b[31m\x1b[2J\x1b]0;title\x07\x1b[H"));
        Assert.IsFalse(t.MouseTracking);
        Assert.IsFalse(t.Sgr);
        Assert.IsFalse(t.AlternateScreen);
    }

    [TestMethod]
    public void MouseInput_EncodesSgrPressAndRelease()
    {
        // Left button (console bit 0x1) at (5,7) -> button 0, 'M' for press, 'm' for release.
        var press = MouseInput.Encode(0x0001, 0, 0, 5, 7, sgr: true, urxvt: false);
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes("\x1b[<0;5;7M"), press);
        var release = MouseInput.Encode(0x0000, 0, 0, 5, 7, sgr: true, urxvt: false);
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes("\x1b[<3;5;7m"), release);
    }

    [TestMethod]
    public void MouseInput_EncodesWheelDirection()
    {
        var up = MouseInput.Encode((uint)1 << 16, 0, ConsoleApi.MOUSE_WHEELED, 10, 4, sgr: true, urxvt: false);
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes("\x1b[<64;10;4M"), up);
        var down = MouseInput.Encode(0xFFFF0000, 0, ConsoleApi.MOUSE_WHEELED, 10, 4, sgr: true, urxvt: false);
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes("\x1b[<65;10;4M"), down);
    }

    [TestMethod]
    public void MouseInput_EncodesUrxvtAndX10()
    {
        var urxvt = MouseInput.Encode(0x0001, 0, 0, 5, 7, sgr: false, urxvt: true);
        CollectionAssert.AreEqual(System.Text.Encoding.ASCII.GetBytes("\x1b[32;5;7M"), urxvt);

        // X10 coordinate bytes are raw (32+c) and must not be UTF-8 doubled even past 0x7f.
        var x10 = MouseInput.Encode(0x0001, 0, 0, 200, 200, sgr: false, urxvt: false);
        CollectionAssert.AreEqual(new byte[] { 0x1b, (byte)'[', (byte)'M', 32, 232, 232 }, x10);
    }
}
