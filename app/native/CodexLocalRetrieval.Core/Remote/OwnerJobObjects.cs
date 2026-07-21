using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodexLocalRetrieval.Core.Remote;

// A NAMED job object per app-started wrapper, so a later kill can take down the whole launch atomically.
//
// Why a job at all: `Kill(entireProcessTree: true)` walks a SNAPSHOT of the process tree, so a child forked at
// the instant of the snapshot is never in the list and survives the kill. Job membership is inherited by every
// descendant at creation time with no enumeration window, so `TerminateJobObject` has no such race.
//
// This is NOT WindowsProcessJob (Core/Agents). That class is turn-containment for server-owned agent processes:
// it is kill-on-close, unnamed, and its whole point is that dropping the handle kills the children. Here the
// opposite is required — the wrapper must OUTLIVE the app that started it (the `cmd /k -> claude` tree is
// launched with UseShellExecute and is expected to keep running after the app closes), so:
//   * JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE is deliberately NOT set - this job never kills anything on its own;
//     it exists purely as a kill HANDLE that some later process can find by name.
//   * the name is derived from the wrapper's (pid, start time) identity, so a REUSED pid can never resolve to
//     the old launch's job - terminating by name can only ever hit the launch it was minted for.
//
// Everything here is best-effort: a launch is never failed, delayed, or altered because a job could not be
// created or assigned. A null job name simply means the Kill path falls back to snapshot tree-kill.
public static class OwnerJobObjects
{
    // A job with no open handles dies with its last member. Holding the creating handle for the app's lifetime
    // keeps the name resolvable (and lets a same-process kill skip OpenJobObject entirely). Reopen-by-name is
    // the cross-process path - the server killing what the GUI started, or either after a restart.
    private static readonly ConcurrentDictionary<string, SafeFileHandle> Created = new(StringComparer.Ordinal);

    public static string? TryAssign(Process? wrapper)
    {
        if (wrapper is null || !OperatingSystem.IsWindows()) return null;

        string name;
        try
        {
            var pid = wrapper.Id;
            if (pid <= 0) return null;
            // Both Id and StartTime throw once the wrapper has exited; that is a normal outcome for a launch
            // that failed instantly, and it means there is nothing to contain.
            var startTicks = wrapper.StartTime.ToUniversalTime().Ticks;
            name = JobName(pid, startTicks);
        }
        catch { return null; }

        SafeFileHandle? job = null;
        try
        {
            job = CreateJobObjectW(IntPtr.Zero, name);
            if (job.IsInvalid) { job.Dispose(); return null; }

            // No SetInformationJobObject call: the default limit flags are what we want. Setting
            // KILL_ON_JOB_CLOSE here would kill every app-started terminal when the app exits.
            if (!AssignProcessToJobObject(job, wrapper.SafeHandle))
            {
                job.Dispose();
                return null;
            }
        }
        catch
        {
            try { job?.Dispose(); } catch { }
            return null;
        }

        // Same name twice would mean the same (pid, start time) twice - impossible in practice, but if it
        // happens the newer handle wins and the older one is closed rather than leaked.
        if (Created.TryRemove(name, out var previous))
            try { previous.Dispose(); } catch { }
        Created[name] = job;
        return name;
    }

    // Kills every process still in the job. Returns true for "the job's members are gone", which includes the
    // case where the job itself no longer exists - that only happens once its last member has exited AND no
    // handle is held, i.e. exactly the already-gone end state a kill is trying to reach.
    public static bool TryTerminate(string? jobName, out string detail)
    {
        detail = "";
        if (string.IsNullOrWhiteSpace(jobName)) { detail = "no job recorded for this launch"; return false; }
        if (!OperatingSystem.IsWindows()) { detail = "job objects are Windows-only"; return false; }

        var name = jobName!.Trim();
        var opened = false;
        SafeFileHandle? job;
        if (Created.TryGetValue(name, out var held) && !held.IsInvalid && !held.IsClosed)
        {
            job = held;
        }
        else
        {
            job = OpenJobObjectW(JOB_OBJECT_TERMINATE | JOB_OBJECT_QUERY, false, name);
            opened = true;
            if (job.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                job.Dispose();
                if (error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND)
                {
                    detail = "job no longer exists - every process in this launch had already exited";
                    return true;
                }
                detail = $"couldn't open job '{name}' (win32 error {error})";
                return false;
            }
        }

        try
        {
            var members = TryCountAssignedProcesses(job);
            if (!TerminateJobObject(job, 1))
            {
                detail = $"TerminateJobObject failed on '{name}' (win32 error {Marshal.GetLastWin32Error()})";
                return false;
            }
            detail = members switch
            {
                0 => "job had no live members - already gone",
                < 0 => "terminated job (member count unavailable)",
                _ => $"terminated job ({members} process(es))",
            };
            return true;
        }
        catch (Exception ex)
        {
            detail = "job termination failed: " + ex.Message;
            return false;
        }
        finally
        {
            if (opened) try { job.Dispose(); } catch { }
        }
    }

    // Test/diagnostic seam: does this app still hold the creating handle for that name?
    internal static bool HoldsHandle(string? jobName)
        => !string.IsNullOrWhiteSpace(jobName)
           && Created.TryGetValue(jobName!.Trim(), out var h)
           && !h.IsInvalid && !h.IsClosed;

    internal static void ReleaseHandle(string? jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName)) return;
        if (Created.TryRemove(jobName!.Trim(), out var h))
            try { h.Dispose(); } catch { }
    }

    // `Local\` keeps the name in the caller's session namespace: two logged-in users running the app never
    // collide, and no elevation is needed to create it.
    internal static string JobName(int pid, long startTicksUtc)
        => $@"Local\CodexLocalRetrieval-owner-{pid}-{startTicksUtc}";

    // -1 = unknown (the query failed for a reason other than a short buffer).
    private static int TryCountAssignedProcesses(SafeFileHandle job)
    {
        // Only the leading NumberOfAssignedProcesses DWORD is read, so a short buffer (ERROR_MORE_DATA, which
        // still fills the counts) is as good as a complete one.
        var length = 4096;
        var buffer = Marshal.AllocHGlobal(length);
        try
        {
            var ok = QueryInformationJobObject(
                job, JobObjectBasicProcessIdList, buffer, (uint)length, IntPtr.Zero);
            if (!ok && Marshal.GetLastWin32Error() != ERROR_MORE_DATA) return -1;
            return Marshal.ReadInt32(buffer);
        }
        catch { return -1; }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint JOB_OBJECT_TERMINATE = 0x0008;
    private const uint JOB_OBJECT_QUERY = 0x0004;
    private const int JobObjectBasicProcessIdList = 3;
    private const int ERROR_FILE_NOT_FOUND = 2;
    private const int ERROR_PATH_NOT_FOUND = 3;
    private const int ERROR_MORE_DATA = 234;

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", EntryPoint = "OpenJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(SafeFileHandle job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job,
        int infoClass,
        IntPtr info,
        uint infoLength,
        IntPtr returnLength);
}
