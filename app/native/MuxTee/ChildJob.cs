using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace MuxTee;

// The child lives in an unnamed job with JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE, exactly as muxrun's
// ChildJob does. That is invariant 6: closing the tab (which drops our last handle) takes the whole
// descendant tree with it, with no process-tree snapshot to race against.
//
// JOB_OBJECT_LIMIT_BREAKAWAY_OK is OR-ed in additively. It keeps kill-on-close and only *permits* a
// descendant to leave: a process still lands in this job unless it is created with
// CREATE_BREAKAWAY_FROM_JOB, and the only spawner that asks for that is the gateway's breakaway leaf
// launcher. Without the flag that launcher is refused (Win32 err 5) or stays trapped ("breakaway
// process is still in a job"); being fail-closed it then kills the suspended leaf, so the leaf
// "just disappears". Everything else stays contained.
//
// It is the OPPOSITE of OwnerJobObjects in Core, which is named and deliberately NOT kill-on-close
// because that wrapper must outlive the app that started it. Here the job exists to die with muxtee.
internal sealed class ChildJob : IDisposable
{
    private SafeFileHandle? _handle;

    public ChildJob()
    {
        _handle = CreateJobObjectW(IntPtr.Zero, null);
        if (_handle.IsInvalid)
        {
            _handle.Dispose();
            _handle = null;
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
        }

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags =
            JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE | JOB_OBJECT_LIMIT_BREAKAWAY_OK;
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!SetInformationJobObject(_handle, JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                var err = Marshal.GetLastWin32Error();
                _handle.Dispose();
                _handle = null;
                throw new System.ComponentModel.Win32Exception(err, "SetInformationJobObject failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Assign(SafeProcessHandle process)
    {
        if (_handle is null || _handle.IsInvalid) return;
        if (!AssignProcessToJobObject(_handle, process))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
    }

    public void Dispose()
    {
        // Closing the last handle is what fires KILL_ON_JOB_CLOSE. Do it once.
        _handle?.Dispose();
        _handle = null;
    }

    // Test seam, mirroring owned-gateway-process.ps1's readback (:314-:323): report the limit flags the
    // OS actually holds for this job, so a test can assert BREAKAWAY_OK is really set rather than trust
    // that SetInformationJobObject did what we asked.
    internal uint QueryLimitFlags()
    {
        if (_handle is null || _handle.IsInvalid)
            throw new InvalidOperationException("job is not open");

        var size = (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryInformationJobObject(
                    _handle, JobObjectExtendedLimitInformation, buffer, size, IntPtr.Zero))
            {
                throw new System.ComponentModel.Win32Exception(
                    Marshal.GetLastWin32Error(), "QueryInformationJobObject failed");
            }
            var readback = Marshal.PtrToStructure<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(buffer);
            return readback.BasicLimitInformation.LimitFlags;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const uint JOB_OBJECT_LIMIT_BREAKAWAY_OK = 0x00000800;
    private const int JobObjectExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attrs, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job, int infoClass, IntPtr info, uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        SafeFileHandle job, int infoClass, IntPtr info, uint infoLength, IntPtr returnLength);
}
