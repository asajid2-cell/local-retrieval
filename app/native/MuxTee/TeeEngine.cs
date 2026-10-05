using System;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

// The three-thread core from spec §5.2.
//
//   T1  inner stdout -> stdout FIRST, then the local ring, then the net queue
//   T2  ReadConsoleInputW -> key chunks queued to T3, resize drives ResizePseudoConsole
//   T3  one writer into the inner input pipe, draining the InputQueue
//
// P1 is local-only: the net queue is present as a no-op sink so the ordering T1 must obey is real and
// testable before P2 wires muxd behind it. Invariant 1 (local write before anything network-shaped) is
// therefore enforced from the first commit rather than retrofitted.
internal sealed class TeeEngine : IDisposable
{
    private readonly SafeFileHandle _stdout;
    private readonly SafeFileHandle _innerRead;
    private readonly SafeFileHandle _innerWrite;
    private readonly PseudoConsole _pc;
    private readonly InputQueue _input;
    private readonly ChildProcess _child;
    private readonly ConsoleModes _modes;
    private readonly Action<string> _log;

    public RingBuffer Ring { get; } = new(2 * 1024 * 1024);

    // Set when the child appeared to be resized behind our back and the net tier should not trust the
    // frames it would have queued. P1 never reads it; the field exists so P2's contract is unchanged.
    public bool NeedResync { get; private set; }
    public int ExitCode { get; private set; }

    private readonly ManualResetEventSlim _childExited = new(false);
    private Thread? _t1, _t2, _t3;

    public TeeEngine(
        SafeFileHandle stdout,
        PseudoConsole pc,
        InputQueue input,
        ChildProcess child,
        ConsoleModes modes,
        Action<string> log)
    {
        _stdout = stdout;
        _pc = pc;
        _innerRead = pc.OutputRead;
        _innerWrite = pc.InputWrite;
        _input = input;
        _child = child;
        _modes = modes;
        _log = log;
    }

    public void Start()
    {
        // Threads first, then the child sees input - spec §5.1: INHERIT_CURSOR makes ConPTY ask the
        // OUTER terminal for the cursor via ESC[6n, and that reply arrives on our stdin, so T2/T3 must
        // already be running when the child draws its first frame.
        _t1 = new Thread(OutputLoop) { IsBackground = true, Name = "muxtee-t1-out" };
        _t2 = new Thread(InputLoop) { IsBackground = true, Name = "muxtee-t2-in" };
        _t3 = new Thread(InputWriterLoop) { IsBackground = true, Name = "muxtee-t3-inw" };
        _t1.Start();
        _t2.Start();
        _t3.Start();

        var waiter = new Thread(WaitLoop) { IsBackground = true, Name = "muxtee-waiter" };
        waiter.Start();
    }

    // ---- T1 -------------------------------------------------------------------------------------

    private void OutputLoop()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ReadFileNative(_innerRead, buffer, (uint)buffer.Length, out var read, IntPtr.Zero) && read > 0)
            {
                var chunk = buffer.AsSpan(0, (int)read).ToArray();

                // INVARIANT 1: the local terminal is served first, unconditionally, before anything that
                // could touch the network. T1 never waits on muxd and never sees its absence.
                WriteAll(_stdout, chunk);

                // Then the ring, for a reconnect replay, and only then the (P1: inert) net queue.
                Ring.Append(chunk);
                EnqueueNet(chunk);
            }
        }
        catch (Exception ex)
        {
            _log("T1 output loop ended: " + ex.Message);
        }
    }

    // P1: the net queue is deliberately a stub. P2 replaces the body with a bounded queue and a wake.
    private void EnqueueNet(byte[] chunk)
    {
        _ = chunk;
    }

    // Keep writing until the whole buffer is out; a console write may take only part of it.
    internal static void WriteAll(SafeFileHandle handle, ReadOnlySpan<byte> data)
    {
        while (!data.IsEmpty)
        {
            int written = WriteFile(handle, data);
            if (written <= 0) throw new System.IO.IOException("WriteFile made no progress on stdout");
            data = data[written..];
        }
    }

    private static int WriteFile(SafeFileHandle handle, ReadOnlySpan<byte> data)
    {
        unsafe
        {
            fixed (byte* p = data)
            {
                if (!WriteFileNative(handle, (IntPtr)p, (uint)data.Length, out var written, IntPtr.Zero))
                    return -1;
                return (int)written;
            }
        }
    }

    // ---- T2 -------------------------------------------------------------------------------------

    private void InputLoop()
    {
        var records = new INPUT_RECORD[128];
        var pending = new System.Text.StringBuilder(256);
        short lastCols = -1, lastRows = -1;
        long lastSizeCheck = Environment.TickCount64;

        try
        {
            while (true)
            {
                if (!ConsoleApi.ReadConsoleInputW(_modes.Stdin, records, (uint)records.Length, out var count))
                {
                    // A failed read means our console went away (the tab closed). Nothing left to do.
                    _log("ReadConsoleInputW failed; input loop ending");
                    break;
                }

                pending.Clear();
                for (int i = 0; i < count; i++)
                {
                    if (records[i].EventType == ConsoleApi.KEY_EVENT)
                    {
                        var k = records[i].KeyEvent;
                        // Only key-down carries the payload; key-up would double every character.
                        if (k.bKeyDown == 0) continue;
                        pending.Append(k.UnicodeChar);
                    }
                    else if (records[i].EventType == ConsoleApi.WINDOW_BUFFER_SIZE_EVENT)
                    {
                        if (TryResize(ref lastCols, ref lastRows))
                            lastSizeCheck = Environment.TickCount64;
                    }
                }

                if (pending.Length > 0)
                    _input.Enqueue(Utf.EncodeChunk(pending.ToString()));

                // Backstop (§5.2): WT does not always deliver WINDOW_BUFFER_SIZE_EVENT, so re-read the
                // viewport on a slow timer as well.
                if (Environment.TickCount64 - lastSizeCheck >= 250)
                {
                    TryResize(ref lastCols, ref lastRows);
                    lastSizeCheck = Environment.TickCount64;
                }
            }
        }
        catch (Exception ex)
        {
            _log("T2 input loop ended: " + ex.Message);
        }
    }

    private bool TryResize(ref short lastCols, ref short lastRows)
    {
        // Invariant 3: the local terminal owns size; the inner pty always matches the WT viewport. No
        // remote path may call this. Read srWindow, NOT dwSize - under a ConPTY the buffer is
        // viewport-sized and dwSize is the maximum window, which is not the size the user sees.
        if (!ConsoleApi.GetConsoleScreenBufferInfo(_modes.Stdout, out var info)) return false;
        var cols = (short)(info.srWindow.Right - info.srWindow.Left + 1);
        var rows = (short)(info.srWindow.Bottom - info.srWindow.Top + 1);
        if (cols <= 0 || rows <= 0) return false;
        if (cols == lastCols && rows == lastRows) return false;
        lastCols = cols;
        lastRows = rows;
        _pc.Resize(cols, rows);
        _log($"resize inner pty to {cols}x{rows}");
        return true;
    }

    // ---- T3 -------------------------------------------------------------------------------------

    private void InputWriterLoop()
    {
        try
        {
            while (_input.TryTake(out var chunk))
                WriteAll(_innerWrite, chunk);
        }
        catch (Exception ex)
        {
            _log("T3 input writer ended: " + ex.Message);
        }
    }

    // ---- waiter ---------------------------------------------------------------------------------

    private void WaitLoop()
    {
        // WaitForSingleObject on the child, then KEYS TO ORDER (§5.2): drain T1 to EOF, close the pty
        // while T1 is still draining (an older ConPTY deadlocks if you close first), then restore modes.
        WaitForSingleObject(_child.ProcessHandle, INFINITE);
        GetExitCodeProcess(_child.ProcessHandle, out var code);
        ExitCode = unchecked((int)code);
        _childExited.Set();

        _input.Complete();
        _t1?.Join(3000);
        _pc.Dispose();
        _modes.Restore();
    }

    public void WaitForExit() => _childExited.Wait();

    public void Dispose()
    {
        _modes.Restore();
    }

    private const uint INFINITE = 0xFFFFFFFF;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "WriteFile", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool WriteFileNative(
        SafeFileHandle h, IntPtr buffer, uint toWrite, out uint written, IntPtr overlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ReadFileNative(
        SafeFileHandle h,
        [System.Runtime.InteropServices.Out] byte[] buffer,
        uint toRead,
        out uint read,
        IntPtr overlapped);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeProcessHandle handle, out uint exitCode);
}
