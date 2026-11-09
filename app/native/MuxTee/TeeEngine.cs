using System;
using System.Collections.Generic;
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

    // Watches the child's output for the DEC private modes that govern the mouse, so T2 can re-encode a
    // MOUSE_EVENT in the encoding the child negotiated. Fed from T1; read from T2.
    private readonly ScreenModeTracker _screen = new();

    // Whether the tab's console is currently capturing the mouse (toggled by T1 to match _screen).
    private bool _mouseCapture;

    // A reconnect/watch snapshot is sent as ONE websocket message, so this capacity must stay under the
    // local attach server's max message size (muxd LOCAL_MAX_MESSAGE, 8 MB) or the link is closed 1009 on
    // every register - which is a permanent reconnect loop, not a degraded mirror.
    public RingBuffer Ring { get; } = new(2 * 1024 * 1024);

    // Set when the child appeared to be resized behind our back and the net tier should not trust the
    // frames it would have queued. P1 never reads it; the field exists so P2's contract is unchanged.
    public bool NeedResync { get; private set; }
    public int ExitCode { get; private set; }

    private MuxLink? _link;
    // Setting the link wires it to this pty: a remote resize is applied HERE (the link has no pty), and
    // the pane's geometry is read live so every size frame and ack carries it.
    public MuxLink? Link
    {
        get => _link;
        set
        {
            _link = value;
            if (value is not null)
            {
                value.ApplyResize = ApplyRemoteResize;
                value.RestorePane = RestorePaneLocal;
                value.PaneSize = ReadPaneSizeTuple;
            }
        }
    }

    // Contract section 5a: true while a remote device owns the grid. The first local key or mouse event
    // restores the pane locally - the PTY, not just muxd's record - because TryResize compares the pane
    // against its own last seen size and would never fire after a remote resize.
    private bool _remoteOwned;
    // The pane as it was when the device took the grid, and the grid it took. ConPTY reports the
    // pseudoconsole's new size through the tee's own console after a remote resize, so a pane read that
    // merely echoes _remoteGrid is not the user's pane moving - it is our own resize coming back.
    private (int Cols, int Rows) _paneBeforeRemote;
    private (int Cols, int Rows) _remoteGrid;
    // The last pane size read at a moment we could trust: a backstop fire, a completed restore, or the
    // startup read. A device can take the grid again seconds after a release, while the console is still
    // echoing; taking the pane from that read records the OLD DEVICE GRID as "the pane", and the next
    // restore drags the user's pty back to it. This is the only source ApplyRemoteResize may use.
    private (int Cols, int Rows) _paneTrusted;
    private readonly List<byte> _localCarry = new();
    // conhost echoes a pseudoconsole resize back through the tee's own console a few milliseconds
    // later, and two device resizes in a row queue two echoes. For a short window after any resize the
    // pane read is not trustworthy, so it is not acted on; the next 250 ms backstop picks up a real
    // user resize once the stream has settled.
    private long _settleUntilTicks;
    private long _lastActivityAt;
    private readonly object _resizeLock = new();

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
        // Seed the trusted pane size before any device can take the grid: at launch the pty IS the pane,
        // so this read is the only one that is guaranteed to be the user's window and not an echo.
        _paneTrusted = ReadPaneSizeTuple();
        // Threads first, then the child sees input - spec §5.1: INHERIT_CURSOR makes ConPTY ask the
        // OUTER terminal for the cursor via ESC[6n, and that reply arrives on our stdin, so T2/T3 must
        // already be running when the child draws its first frame.
        _t1 = new Thread(OutputLoop) { IsBackground = true, Name = "muxtee-t1-out" };
        _t2 = new Thread(InputLoop) { IsBackground = true, Name = "muxtee-t2-in" };
        _t3 = new Thread(InputWriterLoop) { IsBackground = true, Name = "muxtee-t3-inw" };
        _t1.Start();
        _t2.Start();
        _t3.Start();
        Link?.Start();

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
                // A resize of OUR pseudoconsole makes conhost emit a window-resize request
                // (CSI 8 ; rows ; cols t) into the pty stream. Forwarded to the tab it makes the USER'S
                // pane adopt the phone's grid, so it is dropped from the LOCAL write only; the mirror
                // still receives the pty's bytes unchanged.
                WriteAll(_stdout, StripWindowResize(chunk));

                // Then the ring, for a reconnect replay, and only then the (P1: inert) net queue.
                Ring.Append(chunk);
                EnqueueNet(chunk);

                // Track the child's mouse modes for T2. The child's output is the only place the app says
                // which mouse encoding it wants, and T1 is the one thread that sees every byte of it.
                _screen.Feed(chunk);

                // Mirror the child's mouse intent onto the tab's console: capture only while the child
                // actually tracks the mouse, so a plain shell keeps native drag-select and scrollback
                // (muxctl does the same - wants_mouse_capture). Mode changes are rare, so this stays off
                // the hot path in practice.
                if (_screen.MouseTracking != _mouseCapture)
                {
                    _mouseCapture = _screen.MouseTracking;
                    _modes.SetMouseCapture(_mouseCapture);
                    _log($"mouse capture {(_mouseCapture ? "on" : "off")}");
                }
            }
        }
        catch (Exception ex)
        {
            _log("T1 output loop ended: " + ex.Message);
        }
    }

    // P2: hand the chunk to the link's bounded queue. The link drops its oldest frames under pressure and
    // never blocks this thread, so invariant 1 still holds with a dead or absent muxd.
    private void EnqueueNet(byte[] chunk) => Link?.Enqueue(chunk);

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
        // Keys become UTF-8 text; a mouse report is raw bytes (X10 coordinates can exceed 0x7f, so they
        // must not go through UTF-8). Both land here in record order and leave as one write.
        var raw = new List<byte>(256);
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
                raw.Clear();
                for (int i = 0; i < count; i++)
                {
                    // Contract 5a: the physical hand at the pane always wins. Keys, a click/drag and a
                    // wheel notch count; a bare pointer MOVE does not (MouseInput.IsIntent) - motion is
                    // not intent, and WT emits it whenever the pointer crosses the window. The restore is
                    // once, the activity report is rate-limited inside.
                    if (records[i].EventType == ConsoleApi.KEY_EVENT
                        || (records[i].EventType == ConsoleApi.MOUSE_EVENT && MouseInput.IsIntent(records[i].MouseEvent)))
                        NoteLocalInput();
                    if (records[i].EventType == ConsoleApi.KEY_EVENT)
                    {
                        var k = records[i].KeyEvent;
                        if (k.bKeyDown == 0)
                        {
                            // A key-up is a release and carries nothing - except Alt+numpad, whose
                            // composed character is delivered on VK_MENU's key-up record.
                            pending.Append(KeyTranslator.ForAltNumpadKeyUp(k.wVirtualKeyCode, k.UnicodeChar));
                            continue;
                        }
                        // Translate the record the way a stock terminal would. Forwarding UnicodeChar
                        // alone dropped every key with no character (arrows, Home/End, Insert/Delete,
                        // PageUp/PageDown, Shift+Tab) - up-arrow never reached the child, so command
                        // history was unreachable.
                        var text = KeyTranslator.ForKeyDown(k.wVirtualKeyCode, k.UnicodeChar, k.dwControlKeyState);
                        if (text.Length == 0) continue;
                        int repeat = Math.Max(1, (int)k.wRepeatCount);
                        for (int r = 0; r < repeat; r++) pending.Append(text);
                    }
                    else if (records[i].EventType == ConsoleApi.MOUSE_EVENT)
                    {
                        // WT only hands us a mouse report when the child asked to track the mouse, and the
                        // report's encoding is the child's. But only the WHEEL is forwarded: click and
                        // motion are dropped, because a flooded motion/click stream is what leaked mouse
                        // bytes into a shell that never asked (the 2026-10-13 ssh bug), exactly as muxctl
                        // and the web client avoid (muxd/muxctl.py translates the wheel alone). Flush the
                        // pending text first so key and wheel bytes keep their order in the pipe.
                        if (!_screen.MouseTracking) continue;
                        var m = records[i].MouseEvent;
                        if (!MouseInput.IsWheel(m.dwEventFlags)) continue;
                        AppendText(raw, pending);
                        raw.AddRange(MouseInput.Encode(m.dwButtonState, m.dwControlKeyState, m.dwEventFlags,
                            m.dwMousePosition.X + 1, m.dwMousePosition.Y + 1, _screen.Sgr, _screen.Urxvt));
                    }
                    else if (records[i].EventType == ConsoleApi.WINDOW_BUFFER_SIZE_EVENT)
                    {
                        if (TryResize(ref lastCols, ref lastRows))
                            lastSizeCheck = Environment.TickCount64;
                    }
                }

                AppendText(raw, pending);
                if (raw.Count > 0)
                    _input.Enqueue(raw.ToArray());

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

    // Turn the accumulated key text into UTF-8 bytes and fold it into the pending byte run, so a surrogate
    // pair split across two records still re-joins before it is encoded (never two lone halves).
    private static void AppendText(List<byte> raw, System.Text.StringBuilder pending)
    {
        if (pending.Length == 0) return;
        raw.AddRange(Utf.EncodeChunk(pending.ToString()));
        pending.Clear();
    }

    // Drops `ESC [ 8 ; <digits> ; <digits> t` (conhost's window-resize request) from the local write,
    // carrying a partial sequence into the next chunk. Nothing else is touched.
    private byte[] StripWindowResize(byte[] chunk)
    {
        _localCarry.AddRange(chunk);
        var src = _localCarry.ToArray();
        var keep = new List<byte>(src.Length);
        int i = 0, pending = -1;
        while (i < src.Length)
        {
            if (src[i] == 0x1B)
            {
                int j = i + 1;
                if (j < src.Length && src[j] == (byte)'[')
                {
                    j++;
                    int digits1 = 0;
                    while (j < src.Length && src[j] >= (byte)'0' && src[j] <= (byte)'9') { j++; digits1++; }
                    if (j >= src.Length) { pending = i; break; }
                    if (digits1 > 0 && src[j] == (byte)';')
                    {
                        j++;
                        int digits2 = 0;
                        while (j < src.Length && src[j] >= (byte)'0' && src[j] <= (byte)'9') { j++; digits2++; }
                        if (j >= src.Length) { pending = i; break; }
                        if (digits1 == 1 && src[i + 2] == (byte)'8'
                            && digits2 > 0 && src[j] == (byte)'t')
                        {
                            i = j + 1;          // drop the whole request
                            continue;
                        }
                    }
                }
            }
            keep.Add(src[i]);
            i++;
        }
        _localCarry.Clear();
        if (pending >= 0) _localCarry.AddRange(src[pending..]);
        return keep.ToArray();
    }

    // The WT viewport, read srWindow (NOT dwSize - under a ConPTY the buffer is viewport-sized and
    // dwSize is the maximum window, which is not the size the user sees).
    private bool TryReadPaneSize(out short cols, out short rows)
    {
        cols = rows = 0;
        if (!ConsoleApi.GetConsoleScreenBufferInfo(_modes.Stdout, out var info)) return false;
        cols = (short)(info.srWindow.Right - info.srWindow.Left + 1);
        rows = (short)(info.srWindow.Bottom - info.srWindow.Top + 1);
        return cols > 0 && rows > 0;
    }

    private (int Cols, int Rows) ReadPaneSizeTuple()
        => TryReadPaneSize(out var c, out var r) ? (c, r) : (0, 0);

    private bool TryResize(ref short lastCols, ref short lastRows)
    {
        // The local pane owns the grid by default. While a remote device owns it this still runs (the
        // window may be dragged underneath), and the pane's new size is simply reported as a local
        // statement - which is what reclaims the grid.
        short cols, rows;
        // The read and the decision are serialized with ApplyRemoteResize: a remote resize wakes this
        // loop (the pane stays put, but the console buffer reports the pseudoconsole) and the two must
        // not interleave, or this fires on the remote grid a millisecond after it landed.
        lock (_resizeLock)
        {
            if (!TryReadPaneSize(out cols, out rows)) return false;
            // Echo guard: while a device owns the grid, a read equal to the grid we applied is ConPTY
            // reporting our own pseudoconsole resize, not the local pane changing. Firing here would
            // send a `size` and reclaim the grid the device just took (contract G2/G5).
            if (_remoteOwned && (int)cols == _remoteGrid.Cols && (int)rows == _remoteGrid.Rows) return false;
            if (Environment.TickCount64 < _settleUntilTicks) return false;
            if (cols == lastCols && rows == lastRows) return false;
            _pc.Resize(cols, rows);
            _remoteOwned = false;
            // A backstop fire is the one moment the pane read is proven genuine: it differs from the
            // applied grid, from the last seen size, and it arrives outside the settle window.
            _paneTrusted = (cols, rows);
        }
        lastCols = cols;
        lastRows = rows;
        // The mirror must follow the window the user is actually looking at. The tab is the default
        // source of its geometry and it has to say so on every change - silence here means viewers keep
        // rendering at the size the tab happened to be at when muxtee started.
        Link?.SetSize(cols, rows);
        _log($"resize inner pty to {cols}x{rows}");
        return true;
    }

    // Contract 5a: the local pane reclaims the grid. Called from the receive half when muxd asks.
    private (bool Ok, int Cols, int Rows, int PaneCols, int PaneRows) ApplyRemoteResize(int cols, int rows, long epoch)
    {
        (int Cols, int Rows) pane;
        try
        {
            // Claim the grid BEFORE the resize so a pane-read racing this call sees _remoteOwned with the
            // applied grid already recorded and treats its own echo as our resize, not the user's.
            lock (_resizeLock)
            {
                pane = ReadPaneSizeTuple();
                // Only the local -> remote transition records the pane, and only from the trusted size:
                // a device can take the grid again while the console still echoes the last grid, so a
                // fresh read here can be the previous device grid, not the pane. Recording that would
                // make every later restore drag the user's pty back to a phone's size.
                if (!_remoteOwned && _paneTrusted.Cols > 0 && _paneTrusted.Rows > 0)
                    _paneBeforeRemote = _paneTrusted;
                else if (!_remoteOwned && pane.Cols > 0 && pane.Rows > 0
                         && !(pane.Cols == _remoteGrid.Cols && pane.Rows == _remoteGrid.Rows)
                         && Environment.TickCount64 >= _settleUntilTicks)
                    _paneBeforeRemote = pane;
                _remoteGrid = (cols, rows);
                _remoteOwned = true;
                _settleUntilTicks = Environment.TickCount64 + 400;
                _pc.Resize((short)cols, (short)rows);
            }
        }
        catch (Exception ex)
        {
            _log("remote resize failed: " + ex.Message);
            return (false, 0, 0, 0, 0);
        }
        // A remote resize never touches lastCols/lastRows: those track the pane, and moving them would
        // make the 250 ms backstop immediately undo the phone's grid. It does mean TryResize cannot
        // notice the pane changing back; _remoteOwned drives the restore instead (NoteLocalInput).
        // The ring's bytes are at the old width and cannot be honestly replayed: clear it, and the app
        // repaints itself at the new grid.
        Link?.ReplaceHistory(Array.Empty<byte>());
        _log($"remote resize pty to {cols}x{rows} epoch={epoch}");
        return (true, cols, rows, pane.Cols, pane.Rows);
    }

    // Contract 5a: restore the pane's pty and report it. Shared by the first local key and by an
    // explicit muxd release, which is why the release frame carries no geometry at all.
    private (bool Ok, int Cols, int Rows, int PaneCols, int PaneRows) RestorePaneLocal(long epoch)
    {
        // Use the pane read when the grid was taken, not a fresh one: ConPTY is currently echoing the
        // remote grid through the tee's own console, so a live read would "restore" to the phone's size.
        short cols, rows;
        if (_paneBeforeRemote.Cols > 0 && _paneBeforeRemote.Rows > 0)
        {
            cols = (short)_paneBeforeRemote.Cols;
            rows = (short)_paneBeforeRemote.Rows;
        }
        else if (!TryReadPaneSize(out cols, out rows))
            return (false, 0, 0, 0, 0);
        try
        {
            lock (_resizeLock)
            {
                _remoteOwned = false;
                _settleUntilTicks = Environment.TickCount64 + 800;
                _pc.Resize(cols, rows);
                _paneTrusted = (cols, rows);
            }
        }
        catch (Exception ex)
        {
            _log("pane restore resize failed: " + ex.Message);
            return (false, 0, 0, cols, rows);
        }
        Link?.SetSize(cols, rows);
        // The ring's bytes are at the remote width: clear it and let the app repaint at the pane grid.
        Link?.ReplaceHistory(Array.Empty<byte>());
        _log($"pane restored to {cols}x{rows} epoch={epoch}");
        return (true, cols, rows, cols, rows);
    }

    // A local key or mouse event. The first one after a remote resize restores the pane's PTY locally;
    // every one at most once a second tells muxd the physical hand is here (it reclaims ownership).
    private void NoteLocalInput()
    {
        if (_remoteOwned) RestorePaneLocal(0);
        long now = Environment.TickCount64;
        if (now - _lastActivityAt < 1000) return;      // rate-limited: a typing burst is one signal
        _lastActivityAt = now;
        Link?.SendActivity();
    }

    // ---- T3 -------------------------------------------------------------------------------------

    private void InputWriterLoop()
    {
        try
        {
            while (_input.TryTake(out var item))
            {
                bool ok = false;
                try
                {
                    WriteAll(_innerWrite, item.Data);
                    ok = true;
                }
                finally
                {
                    // The ack means "these bytes reached the child", which is the only claim muxd's
                    // write_confirmed can honestly make. Answering at enqueue time would let a remote
                    // sender believe a keystroke landed when the child had already gone away.
                    _input.NotifyWritten(item.Rid, ok);
                }
            }
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
        Link?.Dispose();
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
