using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace CodexLocalRetrieval.Core.Terminal;

// A process running under a Windows pseudo-console (ConPTY, Win10 1809+). This is the unit a remote
// session mirrors: one real interactive program (claude/codex/pwsh) whose terminal output is a byte
// stream we can fan out to many viewers, and whose input is a byte stream any viewer can write to.
// Output is VT/ANSI exactly as a real console would receive it, so xterm.js renders it faithfully.
//
// Windows-only by nature. The Server target is net8.0 (cross), so callers guard OperatingSystem.IsWindows().
[SupportedOSPlatform("windows")]
public sealed class PtySession : IDisposable
{
    private IntPtr _hPC;                       // the pseudo console handle
    private IntPtr _attrList;                  // PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE list (must outlive CreateProcess)
    private IntPtr _hProcess;
    private IntPtr _hThread;
    private bool _disposed;

    public Stream Input { get; }               // write here -> goes to the program's stdin
    public Stream Output { get; }              // read here  -> the program's terminal output (VT bytes)
    public int ProcessId { get; }

    private PtySession(Stream input, Stream output, IntPtr hPC, IntPtr attrList, IntPtr hProcess, IntPtr hThread, int pid)
    {
        Input = input; Output = output;
        _hPC = hPC; _attrList = attrList; _hProcess = hProcess; _hThread = hThread; ProcessId = pid;
    }

    // Spawn `commandLine` (e.g. "pwsh.exe" or "claude.exe --resume <id>") under a fresh ConPTY.
    public static PtySession Start(string commandLine, string? workingDir = null, short cols = 120, short rows = 30)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("PtySession requires Windows (ConPTY).");
        if (string.IsNullOrWhiteSpace(commandLine)) throw new ArgumentException("commandLine is required.");

        SafeFileHandle? inRead = null, inWrite = null, outRead = null, outWrite = null;
        var hPC = IntPtr.Zero; var attrList = IntPtr.Zero;
        try
        {
            // Two anonymous pipes. We keep inWrite (to type) + outRead (to read); ConPTY gets inRead + outWrite.
            if (!CreatePipe(out inRead, out inWrite, IntPtr.Zero, 0)) throw Win32("CreatePipe(in)");
            if (!CreatePipe(out outRead, out outWrite, IntPtr.Zero, 0)) throw Win32("CreatePipe(out)");

            var size = new COORD { X = cols, Y = rows };
            var hr = CreatePseudoConsole(size, inRead, outWrite, 0, out hPC);
            if (hr != 0) throw new InvalidOperationException($"CreatePseudoConsole failed (0x{hr:X8}).");

            // ConPTY duplicated the ends it needs; release our copies so EOF propagates correctly.
            inRead.Dispose(); inRead = null;
            outWrite.Dispose(); outWrite = null;

            // Build a STARTUPINFOEX whose attribute list carries the pseudo console.
            var lpSize = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize); // query size
            attrList = Marshal.AllocHGlobal(lpSize);
            if (!InitializeProcThreadAttributeList(attrList, 1, 0, ref lpSize)) throw Win32("InitializeProcThreadAttributeList");
            if (!UpdateProcThreadAttribute(attrList, 0, (IntPtr)PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, hPC, (IntPtr)IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
                throw Win32("UpdateProcThreadAttribute");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.lpAttributeList = attrList;

            var ok = CreateProcess(null, commandLine, IntPtr.Zero, IntPtr.Zero, false,
                EXTENDED_STARTUPINFO_PRESENT, IntPtr.Zero,
                string.IsNullOrWhiteSpace(workingDir) ? null : workingDir,
                ref si, out var pi);
            if (!ok) throw Win32("CreateProcess");

            // Anonymous pipes are synchronous (no overlapped I/O) -> open the streams non-async; the broker
            // reads on a dedicated thread. Streams take ownership of the kept handles.
            var input = new FileStream(inWrite!, FileAccess.Write, 4096, isAsync: false);
            var output = new FileStream(outRead!, FileAccess.Read, 4096, isAsync: false);
            inWrite = null; outRead = null; // ownership transferred to the FileStreams

            return new PtySession(input, output, hPC, attrList, pi.hProcess, pi.hThread, pi.dwProcessId);
        }
        catch
        {
            if (hPC != IntPtr.Zero) ClosePseudoConsole(hPC);
            if (attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(attrList); Marshal.FreeHGlobal(attrList); }
            inRead?.Dispose(); inWrite?.Dispose(); outRead?.Dispose(); outWrite?.Dispose();
            throw;
        }
    }

    public void Resize(short cols, short rows)
    {
        if (_disposed || _hPC == IntPtr.Zero) return;
        ResizePseudoConsole(_hPC, new COORD { X = cols, Y = rows });
    }

    // Blocks up to timeoutMs (-1 = infinite). Returns true if the process exited.
    public bool WaitForExit(int timeoutMs = -1)
    {
        if (_hProcess == IntPtr.Zero) return true;
        return WaitForSingleObject(_hProcess, timeoutMs < 0 ? INFINITE : (uint)timeoutMs) == 0;
    }

    public bool HasExited => _hProcess != IntPtr.Zero && WaitForSingleObject(_hProcess, 0) == 0;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Closing the pseudo console signals the child's console is gone; then tear down handles.
        if (_hPC != IntPtr.Zero) { ClosePseudoConsole(_hPC); _hPC = IntPtr.Zero; }
        try { Input.Dispose(); } catch { }
        try { Output.Dispose(); } catch { }
        if (_hProcess != IntPtr.Zero)
        {
            if (WaitForSingleObject(_hProcess, 0) != 0) { try { TerminateProcess(_hProcess, 0); } catch { } }
            CloseHandle(_hProcess); _hProcess = IntPtr.Zero;
        }
        if (_hThread != IntPtr.Zero) { CloseHandle(_hThread); _hThread = IntPtr.Zero; }
        if (_attrList != IntPtr.Zero) { DeleteProcThreadAttributeList(_attrList); Marshal.FreeHGlobal(_attrList); _attrList = IntPtr.Zero; }
    }

    private static Exception Win32(string what) => new InvalidOperationException($"{what} failed (Win32 {Marshal.GetLastWin32Error()}).");

    // ---- Win32 interop ----
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const int PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint INFINITE = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct COORD { public short X; public short Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb; public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX { public STARTUPINFO StartupInfo; public IntPtr lpAttributeList; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(out SafeFileHandle hReadPipe, out SafeFileHandle hWritePipe, IntPtr lpPipeAttributes, int nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int CreatePseudoConsole(COORD size, SafeFileHandle hInput, SafeFileHandle hOutput, uint dwFlags, out IntPtr phPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResizePseudoConsole(IntPtr hPC, COORD size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void ClosePseudoConsole(IntPtr hPC);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr lpAttributeList, int dwAttributeCount, int dwFlags, ref IntPtr lpSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(IntPtr lpAttributeList, uint dwFlags, IntPtr Attribute, IntPtr lpValue, IntPtr cbSize, IntPtr lpPreviousValue, IntPtr lpReturnSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr lpAttributeList);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(string? lpApplicationName, string lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
        bool bInheritHandles, uint dwCreationFlags, IntPtr lpEnvironment, string? lpCurrentDirectory, ref STARTUPINFOEX lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
