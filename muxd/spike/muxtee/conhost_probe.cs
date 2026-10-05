// conhost_probe — read one console's geometry and history state for spike S1, and emit
// WINDOW_BUFFER_SIZE_EVENTs for spike S2.
//
// The important context: this probe must be RUN AS THE ConPTY CHILD to have a console at all.
// Windows Terminal (and pywinpty) create a ConPTY, hand the child a console, and render its output
// into WT's own buffer. A helper process launched from a console-less parent can neither
// AttachConsole nor AllocConsole its way to a usable console in this environment, so the probe
// always reports the console it was given.
//
//   conhost_probe --geometry           report dwSize (buffer) vs srWindow (viewport) after 500 lines
//   conhost_probe --watch [ms]         print S2 events for each WINDOW_BUFFER_SIZE_EVENT until killed
//   conhost_probe --keys <ms>          print every KEY_EVENT_RECORD for S3 (run the same probe
//                                      directly in WT and under muxtee and diff the two logs)
//
// Output is one JSON object per line on stdout, and also appended to
// %LOCALAPPDATA%\muxtee\spikes\conhost_probe.jsonl so a ConPTY host that eats the stream still
// leaves evidence behind.
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class ConhostProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct SMALL_RECT { public short Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CONSOLE_SCREEN_BUFFER_INFO
    {
        public COORD dwSize;
        public COORD dwCursorPosition;
        public ushort wAttributes;
        public SMALL_RECT srWindow;
        public COORD dwMaximumWindowSize;
    }

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    private struct KEY_EVENT_RECORD
    {
        [FieldOffset(0)] public int bKeyDown;
        [FieldOffset(4)] public ushort wRepeatCount;
        [FieldOffset(6)] public ushort wVirtualKeyCode;
        [FieldOffset(8)] public ushort wVirtualScanCode;
        [FieldOffset(10)] public char UnicodeChar;
        [FieldOffset(12)] public uint dwControlKeyState;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOW_BUFFER_SIZE_RECORD { public COORD dwSize; }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
        [FieldOffset(4)] public WINDOW_BUFFER_SIZE_RECORD WindowBufferSizeEvent;
    }

    private const ushort KEY_EVENT = 0x0001;
    private const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;
    private const int STD_INPUT_HANDLE = -10;
    private const int STD_OUTPUT_HANDLE = -11;
    private const uint ENABLE_WINDOW_INPUT = 0x0008;
    private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleScreenBufferInfo(IntPtr h, out CONSOLE_SCREEN_BUFFER_INFO info);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GetStdHandle(int which);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool WriteConsoleW(IntPtr h, string s, uint n, out uint written, IntPtr reserved);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool ReadConsoleInputW(IntPtr h, [Out] INPUT_RECORD[] buf, uint len, out uint read);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(IntPtr h, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(IntPtr h, uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleOutputCP(uint cp);

    private static string? _sink;
    private static void Say(string line)
    {
        if (_sink == null)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "muxtee", "spikes");
            Directory.CreateDirectory(dir);
            _sink = Path.Combine(dir, "conhost_probe.jsonl");
        }
        File.AppendAllText(_sink, line + Environment.NewLine);
        Console.Error.WriteLine(line);
    }

    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "--geometry";
        // This probe is meant to be the ConPTY child. If it is not, the console calls below fail
        // with ERROR_INVALID_HANDLE and the run is worthless — say so plainly.
        IntPtr hOut = GetStdHandle(STD_OUTPUT_HANDLE);
        if (!GetConsoleScreenBufferInfo(hOut, out _))
        {
            Console.Error.WriteLine("no-console: run conhost_probe as the ConPTY child, not from a console-less parent");
            return 3;
        }
        SetConsoleOutputCP(65001);
        return mode switch
        {
            "--geometry" => Geometry(hOut),
            "--watch" => Watch(args),
            "--keys" => Keys(args),
            "--winevent" => WinEvent(args),
            _ => 2,
        };
    }

    private static int Geometry(IntPtr hOut)
    {
        // Report the console before any output, then push 500 lines through it and report again.
        // If dwSize.Y grew with the writes the console keeps history; if it stayed equal to the
        // viewport, it is viewport-sized and the Mirror tier can never carry scrollback.
        GetConsoleScreenBufferInfo(hOut, out var before);
        var sb = new StringBuilder();
        for (int i = 0; i < 500; i++) sb.Append($"selftest line {i:D3}\r\n");
        WriteConsoleW(hOut, sb.ToString(), (uint)sb.Length, out _, IntPtr.Zero);
        GetConsoleScreenBufferInfo(hOut, out var after);
        string Fmt(string tag, in CONSOLE_SCREEN_BUFFER_INFO i)
        {
            int viewH = i.srWindow.Bottom - i.srWindow.Top + 1;
            int viewW = i.srWindow.Right - i.srWindow.Left + 1;
            return $"{{\"tag\":\"{tag}\",\"dwSize\":[{i.dwSize.X},{i.dwSize.Y}],\"srWindow\":[{viewW},{viewH}],\"historyRows\":{i.dwSize.Y - viewH},\"viewportSized\":{(i.dwSize.Y == viewH ? "true" : "false")}}}";
        }
        Say(Fmt("before", before));
        Say(Fmt("after", after));
        return 0;
    }

    private static int Watch(string[] args)
    {
        int ms = args.Length > 1 && int.TryParse(args[1], out var v) ? v : 8000;
        IntPtr hIn = GetStdHandle(STD_INPUT_HANDLE);
        GetConsoleMode(hIn, out uint saved);
        SetConsoleMode(hIn, (saved | ENABLE_WINDOW_INPUT) & ~ENABLE_VIRTUAL_TERMINAL_INPUT);
        Say($"{{\"event\":\"watch-begin\",\"ms\":{ms},\"mode\":{saved}}}");
        var buf = new INPUT_RECORD[64];
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadConsoleInputW(hIn, buf, (uint)buf.Length, out uint n)) break;
            for (int i = 0; i < n; i++)
            {
                if (buf[i].EventType == WINDOW_BUFFER_SIZE_EVENT)
                    Say($"{{\"event\":\"resize\",\"t\":{Environment.TickCount64},\"size\":[{buf[i].WindowBufferSizeEvent.dwSize.X},{buf[i].WindowBufferSizeEvent.dwSize.Y}]}}");
            }
        }
        Say("{\"event\":\"watch-end\"}");
        SetConsoleMode(hIn, saved);
        return 0;
    }

    private static int Keys(string[] args)
    {
        int ms = args.Length > 1 && int.TryParse(args[1], out var v) ? v : 12000;
        IntPtr hIn = GetStdHandle(STD_INPUT_HANDLE);
        var buf = new INPUT_RECORD[64];
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            if (!ReadConsoleInputW(hIn, buf, (uint)buf.Length, out uint n)) break;
            for (int i = 0; i < n; i++)
            {
                if (buf[i].EventType != KEY_EVENT) continue;
                var k = buf[i].KeyEvent;
                // Emit only key-down: the char is the payload Claude Code's Shift+Enter depends on.
                if (k.bKeyDown == 0) continue;
                Say($"{{\"event\":\"key\",\"t\":{Environment.TickCount64},\"vk\":{k.wVirtualKeyCode},\"sc\":{k.wVirtualScanCode},\"uc\":{(int)k.UnicodeChar},\"cs\":{k.dwControlKeyState},\"rc\":{k.wRepeatCount}}}");
            }
        }
        Say("{\"event\":\"keys-end\"}");
        return 0;
    }

    // ---- S4: does EVENT_CONSOLE_UPDATE_* fire for a ConPTY console? -------------------------------

    private const uint EVENT_CONSOLE_CARET = 0x4001;
    private const uint EVENT_CONSOLE_UPDATE_REGION = 0x4002;
    private const uint EVENT_CONSOLE_UPDATE_SIMPLE = 0x4003;
    private const uint EVENT_CONSOLE_UPDATE_SCROLL = 0x4004;
    private const uint EVENT_CONSOLE_LAYOUT = 0x4005;
    private const uint EVENT_CONSOLE_START_APPLICATION = 0x4006;
    private const uint EVENT_CONSOLE_END_APPLICATION = 0x4007;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    private delegate void WinEventProc(IntPtr hHook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc cb, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetConsoleWindow();

    private static int WinEvent(string[] args)
    {
        int ms = args.Length > 1 && int.TryParse(args[1], out var v) ? v : 5000;
        IntPtr hwnd = GetConsoleWindow();
        Say($"{{\"event\":\"winevent-hwnd\",\"hwnd\":{(long)hwnd}}}");
        int[] counts = new int[5];
        // OUTOFCONTEXT: the callback is delivered on this thread's message queue, which we pump
        // with a plain sleep loop below. A single-process hook (pid 0) covers every console.
        WinEventProc cb = (hook, evt, h, idObj, idChild, thread, t) =>
        {
            int idx = (int)(evt - EVENT_CONSOLE_UPDATE_REGION);
            if (idx >= 0 && idx < counts.Length) counts[idx]++;
            Say($"{{\"event\":\"winevent\",\"code\":{evt},\"t\":{t},\"obj\":{idObj},\"child\":{idChild}}}");
        };
        IntPtr hook = SetWinEventHook(EVENT_CONSOLE_CARET, EVENT_CONSOLE_END_APPLICATION, IntPtr.Zero, cb, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        Say($"{{\"event\":\"winevent-hook\",\"hook\":{(long)hook},\"err\":{Marshal.GetLastWin32Error()}}}");
        long deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            // Pump the message queue so OUTOFCONTEXT callbacks fire.
            while (PeekMessage(out MSG _, IntPtr.Zero, 0, 0, PM_REMOVE)) { TranslateMessage(out _); DispatchMessage(out _); }
            Thread.Sleep(20);
        }
        UnhookWinEvent(hook);
        Say($"{{\"event\":\"winevent-end\",\"counts\":[{string.Join(",", counts)}]}}");
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }
    private const uint PM_REMOVE = 0x0001;
    [DllImport("user32.dll")] private static extern bool PeekMessage(out MSG m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(out MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(out MSG m);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetConsoleTitleW(StringBuilder title, uint size);
}
