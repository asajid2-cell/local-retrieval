using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

internal static class Program
{
    private static int Main(string[] argv)
    {
        // Passthrough must work even when a console call here would fail, so decide FIRST and only then
        // touch console state.
        var spec = CommandLine.Parse(argv);
        var hIn = GetStdHandle(ConsoleApi.STD_INPUT_HANDLE);
        var hOut = GetStdHandle(ConsoleApi.STD_OUTPUT_HANDLE);

        var stdinIsConsole = IsConsole(hIn);
        var stdoutIsConsole = IsConsole(hOut);

        var mode = PassthroughDecisions.Decide(
            stdinIsConsole, stdoutIsConsole, Environment.GetEnvironmentVariable);
        if (mode == LaunchMode.Passthrough)
        {
            Log.Write($"passthrough (stdin={stdinIsConsole} stdout={stdoutIsConsole}) " +
                      $"disabled={PassthroughDecisions.IsSet(Environment.GetEnvironmentVariable(PassthroughDecisions.DisableVar))} " +
                      $"active={PassthroughDecisions.IsSet(Environment.GetEnvironmentVariable(PassthroughDecisions.ActiveVar))}");
            return RunPassthrough(spec);
        }

        try
        {
            return RunTee(hIn, hOut, spec);
        }
        catch (Exception ex)
        {
            Log.Write("fatal: " + ex);
            return 3;
        }
    }

    // ---- passthrough ----------------------------------------------------------------------------

    private static int RunPassthrough(LaunchSpec spec)
    {
        // Straight exec with inherited handles, at the same cwd and environment we already have. This is
        // the path a Gateway leaf harness, the "no mux" profile and MUXTEE_DISABLE=1 all take, and it must
        // be indistinguishable from not having muxtee in the chain.
        try
        {
            var psi = new ProcessStartInfo(spec.Image)
            {
                UseShellExecute = false,
                WorkingDirectory = Environment.CurrentDirectory,
            };
            foreach (var a in spec.Args) psi.ArgumentList.Add(a);

            using var child = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for " + spec.Image);
            child.WaitForExit();
            return child.ExitCode;
        }
        catch (Win32Exception ex)
        {
            Log.Write("passthrough spawn failed: " + ex.Message);
            return 127;
        }
    }

    // ---- tee ------------------------------------------------------------------------------------

    private static int RunTee(IntPtr hInPtr, IntPtr hOutPtr, LaunchSpec spec)
    {
        var stdin = new SafeFileHandle(hInPtr, ownsHandle: false);
        var stdout = new SafeFileHandle(hOutPtr, ownsHandle: false);

        var modes = new ConsoleModes(stdin, stdout);
        modes.Capture();
        modes.InstallCtrlHandler();
        AppDomain.CurrentDomain.ProcessExit += (_, _) => modes.Restore();

        // Viewport size for the initial pty, read from srWindow (the buffer under a ConPTY is not the
        // user's window). A failure here means a truly broken console; use a sane default rather than die.
        short cols = 120, rows = 30;
        if (ConsoleApi.GetConsoleScreenBufferInfo(stdout, out var info))
        {
            cols = (short)Math.Max(1, info.srWindow.Right - info.srWindow.Left + 1);
            rows = (short)Math.Max(1, info.srWindow.Bottom - info.srWindow.Top + 1);
        }

        // Input pipe: the child reads from inRead, we (T3) write to inWrite. Output pipe: the child writes
        // to outWrite (ConPTY renders there), we (T1) read from outRead.
        PseudoConsole.OpenPair(out var inRead, out var inWrite);
        PseudoConsole.OpenPair(out var outRead, out var outWrite);

        // ConPTY takes ownership of the child-side ends; we keep the other two. A leaked copy of our ends
        // in the child would keep a pipe alive after the child exits, so the split is deliberate.
        using var pc = new PseudoConsole(new COORD(cols, rows), inRead, outWrite, inWrite, outRead, Log.Write);
        // Once the pseudoconsole has duplicated them, drop our copies of the child-side ends.
        inRead.Dispose();
        outWrite.Dispose();

        var input = new InputQueue(Log.Write);
        using var child = ChildProcess.Spawn(spec, pc.Handle, Environment.CurrentDirectory, Log.Write);

        using var engine = new TeeEngine(stdout, pc, input, child, modes, Log.Write);
        // The net link only exists when we intend to be mirrored. MUXTEE_NOLINK gives the "no mux" escape
        // a local-only tab, and is what the P1 acceptance suite runs under so it measures the tee alone.
        if (!PassthroughDecisions.IsSet(Environment.GetEnvironmentVariable("MUXTEE_NOLINK")))
        {
            var link = new MuxLink(OwnerIdentity.SessionName(), OwnerIdentity.OwnerKey(),
                cols, rows, MuxLink.DefaultUri(), input, Log.Write);
            engine.Link = link;
            // The child's first frames arrive before muxd has accepted the owner, so the link re-sends its
            // ring as a replace_history once it registers. Hand it a snapshot at that point.
            link.RegisteredChanged += () =>
            {
                if (link.Registered) link.ReplaceHistory(engine.Ring.Snapshot());
            };
            // While unwatched the link drops frames, so muxd's ring stopped at the last watched moment.
            // Push a fresh snapshot the instant a viewer attaches, so the attach's `sb` reads our history
            // and not a stale one (spec section 6.5).
            link.WatchChanged += () => link.ReplaceHistory(engine.Ring.Snapshot());
        }
        engine.Start();
        Log.Write($"tee: {spec.Image} cols={cols} rows={rows} bundledConpty={pc.UsedBundledDll} " +
                  $"link={(engine.Link is null ? "off" : "on")}");
        engine.WaitForExit();
        return engine.ExitCode;
    }

    private static bool IsConsole(IntPtr handle)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        return GetConsoleMode(handle, out _);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);
}
