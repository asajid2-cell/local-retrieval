using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

// The inner ConPTY. Spec §5: prefer the bundled conpty.dll (the current WT ConPTY, which passes OSC 8,
// OSC 52, ?2026 and OSC 133 through), and fall back to inbox kernel32 only if the bundled one refuses to
// load - loudly, because the inbox ConPTY varies by OS build in what VT it relays.
internal sealed class PseudoConsole : IDisposable
{
    public SafeFileHandle InputWrite { get; private set; }      // we write child input here
    public SafeFileHandle OutputRead { get; private set; }      // we read child output here
    public IntPtr Handle { get; private set; }
    public bool UsedBundledDll { get; private set; }
    // conpty.dll launches a console host for each pty and picks it by scanning its own directory for
    // `x64\OpenConsole.exe` (then `arm64\`, `x86\`, then the inbox `\conhost.exe`). UsedBundledDll is
    // true even when that host is the inbox one, so it is NOT the answer to "are we on the bundled
    // host". This records the host the pty actually got, so a host-less install shows up in the log
    // instead of passing silently (spec section 5).
    public string PtyHost { get; private set; } = "unknown";

    private readonly Action<string> _log;

    // `childInputRead` and `childOutputWrite` are the ends ConPTY takes ownership of; `ourInputWrite`
    // and `ourOutputRead` are the ends muxtee keeps (T3 writes input, T1 reads output). Passing them in
    // rather than opening them here keeps the ownership obvious at the call site.
    public PseudoConsole(
        COORD size,
        SafeFileHandle childInputRead,
        SafeFileHandle childOutputWrite,
        SafeFileHandle ourInputWrite,
        SafeFileHandle ourOutputRead,
        Action<string> log)
    {
        _log = log;
        InputWrite = ourInputWrite;
        OutputRead = ourOutputRead;
        Handle = IntPtr.Zero;

        const uint flags = ConsoleApi.PSEUDOCONSOLE_INHERIT_CURSOR
                         | ConsoleApi.PSEUDOCONSOLE_WIN32_INPUT_MODE;

        var hr = TryBundled(size, childInputRead, childOutputWrite, flags);
        if (hr < 0)
        {
            _log($"bundled conpty.dll CreatePseudoConsole failed hr=0x{hr:X8}; falling back to kernel32");
            hr = ConsoleApi.CreatePseudoConsole(size, childInputRead, childOutputWrite, flags, out var hpc);
            if (hr < 0)
                throw new InvalidOperationException($"CreatePseudoConsole failed hr=0x{hr:X8}");
            Handle = hpc;
            UsedBundledDll = false;
            PtyHost = "inbox kernel32 (bundled DLL did not load)";
        }
    }

    private int TryBundled(COORD size, SafeFileHandle inputRead, SafeFileHandle outputWrite, uint flags)
    {
        try
        {
            var hr = ConsoleApi.ConptyCreatePseudoConsole(size, inputRead, outputWrite, flags, out var hpc);
            if (hr >= 0)
            {
                Handle = hpc;
                UsedBundledDll = true;
                PtyHost = ProbePtyHost();
            }
            return hr;
        }
        catch (DllNotFoundException ex)
        {
            _log("bundled conpty.dll not found: " + ex.Message);
            return unchecked((int)0x8007007E); // ERROR_MOD_NOT_FOUND as HRESULT
        }
        catch (EntryPointNotFoundException ex)
        {
            _log("bundled conpty.dll missing ConptyCreatePseudoConsole: " + ex.Message);
            return unchecked((int)0x8007007F); // ERROR_PROC_NOT_FOUND as HRESULT
        }
    }

    // Which console host conpty.dll will hand this pty. conpty.dll embeds the search it does - its own
    // directory, then `x64\OpenConsole.exe`, `arm64\`, `x86\`, then the inbox `\conhost.exe` - so the
    // best we can do is look where it looks and report. Paths are keyed to the loaded conpty.dll, which
    // for us is the copy beside muxtee.exe; the DllImport search finds the exe directory first.
    private string ProbePtyHost() => ProbePtyHost(AppContext.BaseDirectory);

    // Split out so a test can point it at a staged directory instead of the running exe's own.
    internal static string ProbePtyHost(string dir)
    {
        try
        {
            foreach (var arch in new[] { "x64", "arm64", "x86" })
            {
                if (File.Exists(Path.Combine(dir, arch, "OpenConsole.exe")))
                    return $"OpenConsole.exe ({arch}, bundled)";
            }
            return "conhost.exe (inbox - no bundled OpenConsole.exe beside conpty.dll)";
        }
        catch (Exception ex)
        {
            return "unknown (" + ex.GetType().Name + ")";
        }
    }

    // Open the two ends of the pipes the pseudoconsole needs. The child gets the other ends; we keep
    // these. Pipe handles must NOT be inheritable - passing them into the child would let it hold the
    // console open after we close.
    public static void OpenPair(out SafeFileHandle read, out SafeFileHandle write)
    {
        var sa = new SECURITY_ATTRIBUTES();
        sa.nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>();
        sa.bInheritHandle = false;

        if (!CreatePipe(out var r, out var w, ref sa, 0))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        read = r;
        write = w;
    }

    public void Resize(short cols, short rows)
    {
        if (Handle == IntPtr.Zero) return;
        var size = new COORD(cols, rows);
        var hr = UsedBundledDll
            ? ConsoleApi.ConptyResizePseudoConsole(Handle, size)
            : ConsoleApi.ResizePseudoConsole(Handle, size);
        if (hr < 0) _log($"ResizePseudoConsole failed hr=0x{hr:X8}");
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero)
        {
            if (UsedBundledDll) ConsoleApi.ConptyClosePseudoConsole(Handle);
            else ConsoleApi.ClosePseudoConsole(Handle);
            Handle = IntPtr.Zero;
        }
        InputWrite.Dispose();
        OutputRead.Dispose();
    }

    private static SafeFileHandle Invalid()
        => new SafeFileHandle(new IntPtr(-1), ownsHandle: true);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool bInheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SECURITY_ATTRIBUTES attrs,
        uint size);
}
