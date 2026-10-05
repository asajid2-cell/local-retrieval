// The kernel32 console surface muxtee rides on, plus the conpty.dll entry points. We declare the
// Conpty* names the bundled header exports rather than the kernel32 CreatePseudoConsole, because the
// bundled DLL is the current WT ConPTY and the inbox one is not necessarily the same VT dialect.
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

[StructLayout(LayoutKind.Sequential)]
internal struct COORD
{
    public short X;
    public short Y;
    public COORD(short x, short y) { X = x; Y = y; }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SMALL_RECT
{
    public short Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CONSOLE_SCREEN_BUFFER_INFO
{
    public COORD dwSize;
    public COORD dwCursorPosition;
    public ushort wAttributes;
    public SMALL_RECT srWindow;
    public COORD dwMaximumWindowSize;
}

[StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
internal struct KEY_EVENT_RECORD
{
    [FieldOffset(0)] public int bKeyDown;
    [FieldOffset(4)] public ushort wRepeatCount;
    [FieldOffset(6)] public ushort wVirtualKeyCode;
    [FieldOffset(8)] public ushort wVirtualScanCode;
    [FieldOffset(10)] public char UnicodeChar;
    [FieldOffset(12)] public uint dwControlKeyState;
}

[StructLayout(LayoutKind.Sequential)]
internal struct WINDOW_BUFFER_SIZE_RECORD
{
    public COORD dwSize;
}

[StructLayout(LayoutKind.Explicit)]
internal struct INPUT_RECORD
{
    [FieldOffset(0)] public ushort EventType;
    [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    [FieldOffset(4)] public WINDOW_BUFFER_SIZE_RECORD WindowBufferSizeEvent;
}

internal static class ConsoleApi
{
    public const ushort KEY_EVENT = 0x0001;
    public const ushort WINDOW_BUFFER_SIZE_EVENT = 0x0004;

    public const int STD_INPUT_HANDLE = -10;
    public const int STD_OUTPUT_HANDLE = -11;
    public const int STD_ERROR_HANDLE = -12;

    public const uint ENABLE_PROCESSED_INPUT = 0x0001;
    public const uint ENABLE_LINE_INPUT = 0x0002;
    public const uint ENABLE_ECHO_INPUT = 0x0004;
    public const uint ENABLE_WINDOW_INPUT = 0x0008;
    public const uint ENABLE_MOUSE_INPUT = 0x0010;
    public const uint ENABLE_QUICK_EDIT_MODE = 0x0040;
    public const uint ENABLE_EXTENDED_FLAGS = 0x0080;
    public const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

    public const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    public const uint ENABLE_WRAP_AT_EOL_OUTPUT = 0x0002;
    public const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;
    public const uint DISABLE_NEWLINE_AUTO_RETURN = 0x0008;

    // CreatePseudoConsole flags (from the bundled conpty.h, which does not ship a managed side).
    public const uint PSEUDOCONSOLE_INHERIT_CURSOR = 0x1;
    public const uint PSEUDOCONSOLE_WIN32_INPUT_MODE = 0x4;
    public const uint PSEUDOCONSOLE_RESIZE_QUIRK = 0x2;
    public const uint PSEUDOCONSOLE_GLYPH_WIDTH_WCSWIDTH = 0x10;

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetConsoleMode(SafeFileHandle h, out uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetConsoleMode(SafeFileHandle h, uint mode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetConsoleScreenBufferInfo(SafeFileHandle h, out CONSOLE_SCREEN_BUFFER_INFO info);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReadConsoleInputW(
        SafeFileHandle h, [Out] INPUT_RECORD[] buffer, uint length, out uint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate? handler, [MarshalAs(UnmanagedType.Bool)] bool add);

    public delegate bool ConsoleCtrlDelegate(uint ctrlType);

    // ---- conpty.dll (bundled). The header exports the symbols under these Conpty* names. ----------

    [DllImport("conpty.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int ConptyCreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);

    [DllImport("conpty.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int ConptyResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("conpty.dll", ExactSpelling = true, SetLastError = false)]
    public static extern void ConptyClosePseudoConsole(IntPtr hPC);

    // The inbox fallback, only asked for when the bundled DLL will not load (spec §5: log it loudly).
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int CreatePseudoConsole(
        COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint flags, out IntPtr phPC);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    public static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = false)]
    public static extern void ClosePseudoConsole(IntPtr hPC);
}
