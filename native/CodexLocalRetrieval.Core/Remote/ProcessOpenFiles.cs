using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace CodexLocalRetrieval.Core.Remote;

// PER-PID replacement for the O(whole machine) handle scan in OpenHandles.cs. Answers the same question —
// "which live agent holds which session transcript open?" — by asking each candidate pid for its OWN handle
// table (NtQueryInformationProcess / ProcessHandleInformation, KB-scale) instead of copying the system-wide
// table (GB-scale, which threw past its bound and made every resume BLOCKED).
//
// The safety contract is the DEAD-vs-DENIED distinction the old scan conflated. A pid we cannot inspect is
// never silently "not an owner":
//   * exited pid (ERROR_INVALID_PARAMETER, or an open that succeeds on a zombie whose exit code is set)
//     -> DEAD. Not an owner, vetoes nothing. This is the swarm-churn case that used to refuse whole scans.
//   * alive but uninspectable (access denied, or a class-51 query that fails after a good open)
//     -> UNVERIFIABLE. Reported as such so Start-class callers fail closed honestly.
// "Alive" is defined ONLY by GetExitCodeProcess == STILL_ACTIVE, never by OpenProcess succeeding: OpenProcess
// succeeds on a zombie (an exited process someone still holds a handle to, e.g. the cmd wrapper mid-wait).
public static class ProcessOpenFiles
{
    // Per-pid work is timeboxed so one wedged process marks ONLY ITSELF unverifiable — never the whole set.
    public static readonly TimeSpan DefaultPerPidTimeout = TimeSpan.FromSeconds(1);

    public enum PidOutcome
    {
        // Handle table read; Found is complete for this pid (possibly empty, which genuinely means "holds none").
        Resolved,
        // Process has exited. Not an owner. Drops out of the candidate set without vetoing anything.
        Dead,
        // Process is alive (exit-code rule) but we could not read its handles. Never treated as "no handles".
        Unverifiable,
    }

    public readonly record struct PidInspection(
        int Pid,
        PidOutcome Outcome,
        DateTime StartTimeUtc,
        Dictionary<string, int> Found,
        string Detail);

    // The scan result shape callers must branch on. A flat bool cannot express "pid 42 is unverifiable but
    // pid 43 holds nothing", which is exactly the distinction Start-class refusals need to be honest.
    public sealed class ScanResult
    {
        public Dictionary<string, int> Found { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<int> UnverifiablePids { get; } = new();
        public HashSet<int> DeadPids { get; } = new();
        // Human-readable reason per unverifiable pid, for the refusal message.
        public Dictionary<int, string> Details { get; } = new();

        public bool AllVerifiable => UnverifiablePids.Count == 0;

        public string UnverifiableDetail()
        {
            if (AllVerifiable) return "";
            var parts = new List<string>();
            foreach (var pid in UnverifiablePids)
                parts.Add(Details.TryGetValue(pid, out var d) ? $"pid {pid} ({d})" : "pid " + pid);
            return "could not verify transcript ownership for " + string.Join(", ", parts)
                 + "; the owner is alive but not inspectable (likely elevated), so this is unverified — not a confirmed live owner";
        }
    }

    // TEST SEAM: overrides only the PROCESS_DUP_HANDLE|PROCESS_QUERY_INFORMATION open, so a test can simulate
    // an access-denied (elevated) process while the aliveness probe still measures a REAL live process.
    // Returns (handle, lastError); a null return for a pid falls through to the real OpenProcess.
    internal static Func<int, (IntPtr Handle, int Error)?>? DupQueryOpenOverride;

    // TEST HELPER: proves the zombie precondition — that OpenProcess still SUCCEEDS on an exited pid whose
    // handle someone holds. Without it a zombie test would pass vacuously via the ERROR_INVALID_PARAMETER path.
    internal static bool CanOpenForDupQuery(int pid)
    {
        var h = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        CloseHandle(h);
        return true;
    }

    public static ScanResult Scan(IEnumerable<int> pids, TimeSpan? perPidTimeout = null)
    {
        var result = new ScanResult();
        var seen = new HashSet<int>();
        foreach (var pid in pids)
        {
            if (pid <= 0 || !seen.Add(pid)) continue;
            Merge(result, Inspect(pid, perPidTimeout ?? DefaultPerPidTimeout));
        }
        return result;
    }

    public static void Merge(ScanResult result, PidInspection inspection)
    {
        switch (inspection.Outcome)
        {
            case PidOutcome.Dead:
                result.DeadPids.Add(inspection.Pid);
                return;
            case PidOutcome.Unverifiable:
                result.UnverifiablePids.Add(inspection.Pid);
                result.Details[inspection.Pid] = inspection.Detail;
                return;
            default:
                foreach (var kv in inspection.Found)
                    if (!result.Found.ContainsKey(kv.Key)) result.Found[kv.Key] = kv.Value;
                return;
        }
    }

    // Alive-and-identified: a PROCESS_QUERY_LIMITED_INFORMATION open + the exit-code rule + the creation time
    // that makes (pid, startTime) a real identity (a reused pid is a different process, so a cached answer for
    // the old one must not be served for the new one).
    public static bool TryGetAliveIdentity(int pid, out DateTime startTimeUtc)
    {
        startTimeUtc = default;
        if (pid <= 0) return false;
        var h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return false;
        try
        {
            if (!GetExitCodeProcess(h, out var code) || code != STILL_ACTIVE) return false;
            startTimeUtc = StartTime(h);
            return true;
        }
        finally { CloseHandle(h); }
    }

    // Alive is exit-code defined [F#6]: a handle held to an exited process is NOT a live owner.
    public static bool IsAlive(int pid) => TryGetAliveIdentity(pid, out _);

    public static PidInspection Inspect(int pid, TimeSpan? timeout = null)
    {
        var budget = timeout ?? DefaultPerPidTimeout;
        // The Win32 calls below cannot be cancelled once entered, so the timebox runs them on a pool thread
        // and abandons a wedged one. The abandoned task owns and closes its own handle.
        var work = Task.Run(() => InspectCore(pid));
        if (work.Wait(budget)) return work.Result;
        return Unverifiable(pid, "handle enumeration exceeded its " + (int)budget.TotalMilliseconds + "ms timebox");
    }

    private static PidInspection InspectCore(int pid)
    {
        IntPtr h;
        int err;
        var forced = DupQueryOpenOverride?.Invoke(pid);
        if (forced is not null)
        {
            (h, err) = forced.Value;
        }
        else
        {
            h = OpenProcess(PROCESS_DUP_HANDLE | PROCESS_QUERY_INFORMATION, false, pid);
            err = h == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        }

        if (h == IntPtr.Zero)
        {
            // The pid exited between the process scan and now. This is the ordinary swarm-churn case: it is
            // not an owner and it vetoes NOTHING.
            if (err == ERROR_INVALID_PARAMETER) return Dead(pid);
            // Anything else (classically ERROR_ACCESS_DENIED on an elevated agent) is only "not an owner" if
            // the process is actually gone. Alive + uninspectable = unverifiable, never "holds no transcript".
            if (!TryGetAliveIdentity(pid, out var deniedStart)) return Dead(pid);
            return Unverifiable(
                pid,
                "OpenProcess failed with win32 error " + err + " while the process is still running",
                deniedStart);
        }

        try
        {
            // PROCESS_QUERY_INFORMATION implies LIMITED, so the exit-code rule applies to this same handle.
            // A successful open on a zombie must classify DEAD, not alive [F#6].
            if (!GetExitCodeProcess(h, out var code) || code != STILL_ACTIVE) return Dead(pid);
            var startedUtc = StartTime(h);

            if (!TryQueryHandles(h, out var entries, out var queryDetail))
            {
                // Never silently "no handles": an unexpected NTSTATUS after the retry loop is uncertainty.
                if (!IsAlive(pid)) return Dead(pid);
                return Unverifiable(pid, queryDetail, startedUtc);
            }

            var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var cur = GetCurrentProcess();
            foreach (var e in entries)
            {
                // 0x0012019F = a synchronous handle (often a named pipe) whose resolution can block — skip.
                if (e.GrantedAccess == 0x0012019F) continue;
                if (!DuplicateHandle(h, e.HandleValue, cur, out var dup, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                try
                {
                    if (GetFileType(dup) != FILE_TYPE_DISK) continue;
                    var path = OpenHandles.FinalPath(dup);
                    if (path is null || !OpenHandles.IsTranscriptPath(path)) continue;
                    var id = OpenHandles.SessionIdFromPath(path);
                    if (!string.IsNullOrEmpty(id) && !found.ContainsKey(id!)) found[id!] = pid;
                }
                catch { }
                finally { CloseHandle(dup); }
            }
            return new PidInspection(pid, PidOutcome.Resolved, startedUtc, found, "");
        }
        catch (Exception ex)
        {
            if (!IsAlive(pid)) return Dead(pid);
            return Unverifiable(pid, "handle enumeration failed: " + ex.Message);
        }
        finally { if (h != IntPtr.Zero) CloseHandle(h); }
    }

    private static PidInspection Dead(int pid) =>
        new(pid, PidOutcome.Dead, default, EmptyFound(), "process has exited");

    private static PidInspection Unverifiable(int pid, string detail, DateTime startedUtc = default) =>
        new(pid, PidOutcome.Unverifiable, startedUtc, EmptyFound(), detail);

    private static Dictionary<string, int> EmptyFound() => new(StringComparer.OrdinalIgnoreCase);

    // ---- ProcessHandleInformation (class 51) --------------------------------------------------------
    // PROCESS_HANDLE_SNAPSHOT_INFORMATION = { ULONG_PTR NumberOfHandles; ULONG_PTR Reserved;
    //                                         PROCESS_HANDLE_TABLE_ENTRY_INFO Handles[]; }
    private static bool TryQueryHandles(IntPtr process, out List<PROCESS_HANDLE_TABLE_ENTRY_INFO> entries, out string detail)
    {
        entries = new List<PROCESS_HANDLE_TABLE_ENTRY_INFO>();
        detail = "";
        var len = 64 * 1024;                 // KB scale, not the GB the world scan needed
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            var ok = false;
            var status = 0;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                status = NtQueryInformationProcess(process, ProcessHandleInformation, buf, len, out var need);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buf);
                    buf = IntPtr.Zero;
                    len = Math.Max(need, len * 2);
                    if (len > MaxProcessHandleTableBytes)
                    {
                        detail = "process handle table exceeds the bounded per-pid scan limit";
                        return false;
                    }
                    buf = Marshal.AllocHGlobal(len);
                    continue;
                }
                if (status != 0)
                {
                    detail = "NtQueryInformationProcess(ProcessHandleInformation) failed with status 0x" + status.ToString("X8");
                    return false;
                }
                ok = true;
                break;
            }
            if (!ok)
            {
                detail = "the process handle table kept growing during the scan";
                return false;
            }

            var count = Marshal.ReadIntPtr(buf).ToInt64();
            var entrySize = Marshal.SizeOf<PROCESS_HANDLE_TABLE_ENTRY_INFO>();
            var basePtr = IntPtr.Add(buf, IntPtr.Size * 2);   // skip NumberOfHandles + Reserved
            if (count < 0 || checked(count * entrySize) > len - IntPtr.Size * 2)
            {
                detail = "process handle table reported an implausible entry count";
                return false;
            }
            for (long i = 0; i < count; i++)
                entries.Add(Marshal.PtrToStructure<PROCESS_HANDLE_TABLE_ENTRY_INFO>(
                    IntPtr.Add(basePtr, checked((int)(i * entrySize)))));
            return true;
        }
        finally { if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf); }
    }

    private static DateTime StartTime(IntPtr process)
    {
        if (!GetProcessTimes(process, out var creation, out _, out _, out _)) return default;
        try { return DateTime.FromFileTimeUtc(((long)creation.dwHighDateTime << 32) | (uint)creation.dwLowDateTime); }
        catch { return default; }
    }

    // ---- P/Invoke -----------------------------------------------------------------------------------
    private const int ProcessHandleInformation = 51;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const int ERROR_INVALID_PARAMETER = 87;
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint DUPLICATE_SAME_ACCESS = 0x0002;
    private const uint FILE_TYPE_DISK = 0x0001;
    private const uint STILL_ACTIVE = 259;
    private const int MaxProcessHandleTableBytes = 64 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_HANDLE_TABLE_ENTRY_INFO
    {
        public IntPtr HandleValue;
        public IntPtr HandleCount;
        public IntPtr PointerCount;
        public uint GrantedAccess;
        public uint ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public int dwHighDateTime;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int cls, IntPtr info, int len, out int retLen);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DuplicateHandle(IntPtr srcProc, IntPtr srcHandle, IntPtr dstProc, out IntPtr dstHandle, uint access, bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr hFile);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr process, out FILETIME creation, out FILETIME exit, out FILETIME kernel, out FILETIME user);
}
