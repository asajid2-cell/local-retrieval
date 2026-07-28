using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Core.Remote;

// Native (non-WMI) process enumeration via Toolhelp32 + per-pid NtQueryInformationProcess for command lines.
// The WMI-based world sweeps in RunningSessions are hot and unbounded; this replaces them with a ~single-digit-ms
// Toolhelp32 snapshot for the pid/name/ppid list, then resolves command lines only for claude.exe/codex.exe
// candidates via NtQueryInformationProcess(ProcessCommandLineInformation=60).
//
// Safety: per-pid NtQueryInformationProcess can fail on x64/WOW64 mismatches or access-rights walls. The
// TryGetCommandLine method falls back to WMI for individual pids when the P/Invoke fails, so the world list
// always comes from Toolhelp but cmdline resolution degrades gracefully.
public static class ProcessSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PROCESS_VM_READ = 0x0010;
    private const int ProcessCommandLineInformation = 60;
    private const uint STATUS_SUCCESS = 0;
    private const uint STATUS_INFO_LENGTH_MISMATCH = 0xC0000004;
    private const uint STATUS_BUFFER_OVERFLOW = 0x80000005;
    private const uint STATUS_BUFFER_TOO_SMALL = 0xC0000023;
    private const int InitialBufferSize = 8192;
    private const int MaxBufferSize = 65536;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UNICODE_STRING
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("ntdll.dll")]
    private static extern uint NtQueryInformationProcess(
        IntPtr ProcessHandle,
        int ProcessInformationClass,
        IntPtr ProcessInformation,
        int ProcessInformationLength,
        out int ReturnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr hProcess,
        IntPtr lpBaseAddress,
        IntPtr lpBuffer,
        int dwSize,
        out int lpNumberOfBytesRead);

    // Resolve the command line for a single pid. Tries NtQueryInformationProcess(ProcessCommandLineInformation)
    // first; falls back to a targeted WMI query on failure. Returns "" when unresolvable.
    internal static string TryGetCommandLine(int pid)
    {
        var cl = TryGetCommandLineNative(pid);
        if (!string.IsNullOrEmpty(cl)) return cl;
        return TryGetCommandLineWmi(pid);
    }

    private static string TryGetCommandLineNative(int pid)
    {
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION | PROCESS_VM_READ, false, pid);
        if (h == IntPtr.Zero) return "";
        try
        {
            var size = InitialBufferSize;
            while (size <= MaxBufferSize)
            {
                var buf = Marshal.AllocHGlobal(size);
                try
                {
                    var nt = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out var retLen);
                    if (nt == STATUS_INFO_LENGTH_MISMATCH || nt == STATUS_BUFFER_OVERFLOW || nt == STATUS_BUFFER_TOO_SMALL)
                    {
                        size = Math.Max(retLen, size * 2);
                        continue;
                    }
                    if (nt != STATUS_SUCCESS) return "";
                    if (retLen < Marshal.SizeOf<UNICODE_STRING>()) return "";

                    var us = Marshal.PtrToStructure<UNICODE_STRING>(buf);
                    if (us.Length == 0 || us.Buffer == IntPtr.Zero) return "";

                    var clSize = us.Length / 2;
                    if (IsPointerInBuffer(us.Buffer, buf, size))
                    {
                        return Marshal.PtrToStringUni(us.Buffer, clSize) ?? "";
                    }

                    var clBuf = Marshal.AllocHGlobal(us.Length + 2);
                    try
                    {
                        if (!ReadProcessMemory(h, us.Buffer, clBuf, us.Length, out _))
                            return "";
                        return Marshal.PtrToStringUni(clBuf, clSize) ?? "";
                    }
                    finally { Marshal.FreeHGlobal(clBuf); }
                }
                finally { Marshal.FreeHGlobal(buf); }
            }
            return "";
        }
        finally { CloseHandle(h); }
    }

    private static bool IsPointerInBuffer(IntPtr ptr, IntPtr bufStart, int bufSize)
    {
        var p = ptr.ToInt64();
        var s = bufStart.ToInt64();
        return p >= s && p < s + bufSize;
    }

    private static string TryGetCommandLineWmi(int pid)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                new ManagementScope(@"\\.\root\cimv2"),
                new ObjectQuery($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}"),
                new System.Management.EnumerationOptions { ReturnImmediately = true, Timeout = TimeSpan.FromSeconds(2) });
            foreach (ManagementObject mo in searcher.Get())
                return mo["CommandLine"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }

    internal static Dictionary<int, (string Name, int Ppid)> SnapshotAllProcesses()
    {
        var map = new Dictionary<int, (string, int)>();
        var h = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (h == IntPtr.Zero || h == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(h, ref entry))
            {
                do
                {
                    var pid = (int)entry.th32ProcessID;
                    var ppid = (int)entry.th32ParentProcessID;
                    var name = entry.szExeFile ?? "";
                    if (pid > 0) map[pid] = (name, ppid);
                } while (Process32NextW(h, ref entry));
            }
        }
        finally { CloseHandle(h); }
        return map;
    }

    public static bool TryScan(out List<ArchiveService.RunningSessionInfo> list, out string detail)
    {
        list = new List<ArchiveService.RunningSessionInfo>();
        detail = "";
        try
        {
            var all = SnapshotAllProcesses();

            var names = new Dictionary<int, string>();
            foreach (var kv in all) names[kv.Key] = kv.Value.Name;

            foreach (var kv in all)
            {
                var (name, ppid) = kv.Value;
                var pid = kv.Key;
                var lower = name.ToLowerInvariant();
                if (lower != "claude.exe" && lower != "codex.exe") continue;

                var cl = TryGetCommandLine(pid);
                if (!RunningSessions.IsLiveAgentProcess(name, cl)) continue;

                var sid = ArchiveService.ParseResumedSessionId(cl) ?? "";
                var tool = lower.Contains("codex") ? "codex" : "claude";
                var parent = LabelParent(names.TryGetValue(ppid, out var pn) ? pn : "");
                var started = "";
                try
                {
                    using var process = Process.GetProcessById(pid);
                    started = process.StartTime.ToUniversalTime().ToString("O");
                }
                catch { }

                list.Add(new ArchiveService.RunningSessionInfo(pid, tool, sid, parent, started, ""));
            }
            return true;
        }
        catch (Exception ex)
        {
            detail = "couldn't verify live claude/codex processes (" + ex.Message + "); refusing to risk a second writer";
            return false;
        }
    }

    public static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var all = SnapshotAllProcesses();
        foreach (var kv in all) map[kv.Key] = kv.Value.Ppid;
        return map;
    }

    public static List<(int Pid, int Ppid, string Tool, string SessionId, DateTime StartedUtc)> AgentsWithPpid()
    {
        var list = new List<(int, int, string, string, DateTime)>();
        var all = SnapshotAllProcesses();
        foreach (var kv in all)
        {
            var (name, ppid) = kv.Value;
            var pid = kv.Key;
            var lower = name.ToLowerInvariant();
            if (lower != "claude.exe" && lower != "codex.exe") continue;

            var cl = TryGetCommandLine(pid);
            if (!RunningSessions.IsLiveAgentProcess(name, cl)) continue;

            DateTime started = default;
            try
            {
                using var process = Process.GetProcessById(pid);
                started = process.StartTime.ToUniversalTime();
            }
            catch { }

            var tool = lower.Contains("codex") ? "codex" : "claude";
            var sid = ArchiveService.ParseResumedSessionId(cl) ?? "";
            if (pid > 0) list.Add((pid, ppid, tool, sid, started));
        }
        return list;
    }

    private static string LabelParent(string name)
    {
        var n = (name ?? "").ToLowerInvariant();
        if (n.StartsWith("code")) return "VS Code";
        if (n is "windowsterminal.exe" or "wt.exe" or "openconsole.exe" or "conhost.exe"
              or "cmd.exe" or "powershell.exe" or "pwsh.exe" or "bash.exe" or "sh.exe") return "Terminal";
        if (n.StartsWith("ssh")) return "Multiplex (SSH)";
        if (n.StartsWith("codexlocalretrieval")) return "This app";
        return string.IsNullOrEmpty(name) ? "Unknown" : name.Replace(".exe", "");
    }

    public static HashSet<int> Tree(int rootPid)
    {
        var children = new Dictionary<int, List<int>>();
        var all = SnapshotAllProcesses();
        foreach (var kv in all)
        {
            var ppid = kv.Value.Ppid;
            if (!children.TryGetValue(ppid, out var list))
                children[ppid] = list = new List<int>();
            list.Add(kv.Key);
        }

        var tree = new HashSet<int> { rootPid };
        var pending = new Stack<int>();
        pending.Push(rootPid);
        while (pending.Count > 0)
        {
            var parent = pending.Pop();
            if (!children.TryGetValue(parent, out var direct)) continue;
            foreach (var child in direct)
                if (tree.Add(child)) pending.Push(child);
        }
        return tree;
    }
}