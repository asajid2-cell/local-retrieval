using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

// Spawn the child attached to our inner pseudoconsole, in a kill-on-close job, with the tab's cwd and a
// copy of our environment plus MUXTEE_ACTIVE=1 (invariant 5: an inner muxtee must see this and pass
// through instead of nesting another ConPTY).
//
// The engine builds a STARTUPINFOEX whose only attribute is PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, which
// is exactly what `CreateProcess` needs to hand a real console to the child.
internal sealed class ChildProcess : IDisposable
{
    public SafeProcessHandle ProcessHandle { get; private set; } = new(new IntPtr(-1), ownsHandle: true);
    public uint Pid { get; private set; }
    public ChildJob? Job { get; private set; }

    public static ChildProcess Spawn(LaunchSpec spec, IntPtr pseudoConsole, string cwd, Action<string> log)
    {
        var result = new ChildProcess();
        IntPtr attrListPtr = IntPtr.Zero;
        IntPtr startupInfoPtr = IntPtr.Zero;
        var job = new ChildJob();

        try
        {
            // Size the attribute list first with a null buffer, then allocate and initialise it.
            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attrListPtr = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attrListPtr, 1, 0, ref size))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");

            if (!UpdateProcThreadAttribute(
                    attrListPtr, 0, PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
                    pseudoConsole, new IntPtr(IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            // No STARTF_USESTDHANDLES: the pty hands the child its console through the attribute, and
            // leaving the standard slots unset is what stops the tab's own handles leaking into the child.
            si.StartupInfo.dwFlags = 0;
            si.lpAttributeList = attrListPtr;
            startupInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<STARTUPINFOEX>());
            Marshal.StructureToPtr(si, startupInfoPtr, false);

            var commandLine = BuildCommandLine(spec);
            var envBlock = BuildEnvironmentBlock();

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT,
                    envBlock,
                    string.IsNullOrWhiteSpace(cwd) ? null : cwd,
                    startupInfoPtr,
                    out var pi))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed for " + spec.Image);
            }

            result.ProcessHandle.Dispose();
            result.ProcessHandle = new SafeProcessHandle(pi.hProcess, ownsHandle: true);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            result.Pid = pi.dwProcessId;
            job.Assign(result.ProcessHandle);
            result.Job = job;
            log($"child pid={result.Pid} in kill-on-close job");
            return result;
        }
        catch
        {
            job.Dispose();
            result.ProcessHandle.Dispose();
            throw;
        }
        finally
        {
            if (attrListPtr != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attrListPtr);
                Marshal.FreeHGlobal(attrListPtr);
            }
            if (startupInfoPtr != IntPtr.Zero) Marshal.FreeHGlobal(startupInfoPtr);
        }
    }

    // Windows wants a single command-line string, so quote the image only when it needs it and let the
    // child's own argv carry the rest. The raw form is what CreateProcess's CRT parses back.
    internal static string BuildCommandLine(LaunchSpec spec)
    {
        var sb = new StringBuilder();
        sb.Append(QuoteArg(spec.Image));
        foreach (var a in spec.Args)
        {
            sb.Append(' ');
            sb.Append(QuoteArg(a));
        }
        return sb.ToString();
    }

    // Standard MSVCRT quoting: wrap in quotes and backslash-escape the run of backslashes that precedes a
    // quote (and the trailing run, if the argument ends in one, so the closing quote stays a quote).
    internal static string QuoteArg(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
            return arg;

        var sb = new StringBuilder();
        sb.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1);
                sb.Append('"');
                backslashes = 0;
                continue;
            }
            sb.Append('\\', backslashes);
            backslashes = 0;
            sb.Append(c);
        }
        sb.Append('\\', backslashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    // Our environment with MUXTEE_ACTIVE=1 forced in, as a sorted UTF-16 double-null-terminated block.
    private static IntPtr BuildEnvironmentBlock()
    {
        var env = new System.Collections.Generic.SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            var key = e.Key as string;
            if (string.IsNullOrEmpty(key) || key.StartsWith("=", StringComparison.Ordinal)) continue;
            env[key] = e.Value as string ?? "";
        }
        env[PassthroughDecisions.ActiveVar] = "1";

        var sb = new StringBuilder();
        foreach (var kv in env)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');

        var bytes = Encoding.Unicode.GetBytes(sb.ToString());
        var ptr = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, ptr, bytes.Length);
        return ptr;
    }

    public void Dispose()
    {
        Job?.Dispose();
        Job = null;
        ProcessHandle.Dispose();
    }

    private const uint PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE = 0x00020016;
    private const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr list, uint flags, uint attribute, IntPtr value, IntPtr size, IntPtr prev, IntPtr ret);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        IntPtr startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
