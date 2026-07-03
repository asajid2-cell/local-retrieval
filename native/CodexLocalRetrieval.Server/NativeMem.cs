using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodexLocalRetrieval.Server;

// After dropping the archive + a compacting GC, the managed heap is small but the runtime keeps a lot of
// committed pages in the working set. EmptyWorkingSet asks Windows to trim the process working set to its
// minimum (pages go to standby / pagefile, reclaimed instantly if the process is idle), so an idle
// background server reports its true small footprint. Re-faulted on demand on the next access.
internal static class NativeMem
{
    [DllImport("psapi.dll")]
    private static extern bool EmptyWorkingSet(nint hProcess);

    public static void TrimWorkingSet()
    {
        try { EmptyWorkingSet(Process.GetCurrentProcess().Handle); }
        catch { /* best-effort; not available everywhere */ }
    }
}
