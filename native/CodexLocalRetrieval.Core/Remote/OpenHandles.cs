using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace CodexLocalRetrieval.Core.Remote;

// GROUND-TRUTH "is this session live?": which live claude/codex process (by pid) currently holds which
// session TRANSCRIPT (.jsonl under ~/.claude/projects or ~/.codex/sessions) OPEN. An agent keeps its
// transcript handle open for the WHOLE session regardless of activity, so this catches sessions the
// command-line check misses (forked/started without --resume) AND idle sessions the mtime check misses.
// Windows-only, best-effort (skips protected/unreadable handles). Uses DuplicateHandle +
// GetFinalPathNameByHandle (safer than NtQueryObject, which can hang on pipe handles).
public static class OpenHandles
{
    // sessionId -> pid for every transcript a live agent process (in `agentPids`) has open.
    public static Dictionary<string, int> OpenTranscriptSessionIds(IEnumerable<int> agentPids)
    {
        return TryOpenTranscriptSessionIds(agentPids, out var result, out _) ? result : result;
    }

    // Verified variant for launch guards: failure to enumerate/open handles is uncertainty, not
    // "no live owner". Callers that create writers must fail closed when this returns false.
    public static bool TryOpenTranscriptSessionIds(IEnumerable<int> agentPids, out Dictionary<string, int> result, out string detail)
    {
        result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        detail = "";
        var pids = new HashSet<int>(agentPids);
        if (pids.Count == 0) return true;
        var found = result;

        var procHandles = new Dictionary<int, IntPtr>();
        try
        {
            var cur = GetCurrentProcess();
            foreach (var pid in pids)
            {
                var h = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
                if (h != IntPtr.Zero) procHandles[pid] = h;
            }
            if (procHandles.Count < pids.Count)
            {
                detail = "couldn't inspect transcript handles for every live agent process; refusing to risk a second writer";
                return false;
            }

            QueryAllHandles(e =>
            {
                var pid = (int)(long)e.UniqueProcessId;
                if (!procHandles.TryGetValue(pid, out var src)) return;
                // 0x0012019F = a synchronous handle (often a named pipe) whose resolution can block — skip.
                if (e.GrantedAccess == 0x0012019F) return;

                if (!DuplicateHandle(src, e.HandleValue, cur, out var dup, 0, false, DUPLICATE_SAME_ACCESS)) return;
                try
                {
                    if (GetFileType(dup) != FILE_TYPE_DISK) return;
                    var path = FinalPath(dup);
                    if (path is null || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
                    var low = path.Replace('/', '\\').ToLowerInvariant();
                    if (!(low.Contains("\\.claude\\projects\\") || low.Contains("\\.codex\\sessions\\"))) return;
                    var id = SessionIdFromPath(path);
                    if (!string.IsNullOrEmpty(id) && !found.ContainsKey(id)) found[id] = pid;
                }
                catch { }
                finally { CloseHandle(dup); }
            });
            return true;
        }
        catch (Exception ex)
        {
            detail = "open transcript handle scan failed (" + ex.Message + "); refusing to risk a second writer";
            return false;
        }
        finally { foreach (var h in procHandles.Values) if (h != IntPtr.Zero) CloseHandle(h); }
    }

    // The session id encoded in a transcript path: claude = the filename (a uuid); codex = the uuid suffix
    // of `rollout-<ts>-<uuid>.jsonl`.
    private static string? SessionIdFromPath(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var m = Regex.Match(name, "([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})$");
        return m.Success ? m.Groups[1].Value : name;
    }

    // ---- P/Invoke -----------------------------------------------------------------------------------
    private const int SystemExtendedHandleInformation = 64;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x0002;
    private const uint FILE_TYPE_DISK = 0x0001;
    // The full system handle table on a busy dev box (many agents/processes) can exceed a few hundred MB;
    // a 256 MB ceiling made the scan throw -> the live-owner check failed closed -> resume was BLOCKED for
    // every session. Give it real headroom (transient, freed immediately). Kept well under int.MaxValue.
    private const int MaxHandleTableBytes = 1024 * 1024 * 1024;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_HANDLE_ENTRY
    {
        public IntPtr Object;
        public IntPtr UniqueProcessId;
        public IntPtr HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int cls, IntPtr info, int len, out int retLen);
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
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern int GetFinalPathNameByHandle(IntPtr hFile, StringBuilder path, int cch, int flags);

    private static string? FinalPath(IntPtr h)
    {
        var sb = new StringBuilder(600);
        var n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0);
        if (n == 0) return null;
        if (n >= sb.Capacity) { sb = new StringBuilder(n + 1); n = GetFinalPathNameByHandle(h, sb, sb.Capacity, 0); if (n == 0) return null; }
        var p = sb.ToString();
        return p.StartsWith("\\\\?\\", StringComparison.Ordinal) ? p.Substring(4) : p;
    }

    private static void QueryAllHandles(Action<SYSTEM_HANDLE_ENTRY> visit)
    {
        var len = 0x200000;
        var buf = Marshal.AllocHGlobal(len);
        try
        {
            var status = 0;
            var ok = false;
            for (var attempt = 0; attempt < 8; attempt++)
            {
                status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out var need);
                if (status == STATUS_INFO_LENGTH_MISMATCH)
                {
                    Marshal.FreeHGlobal(buf);
                    len = Math.Max(need, len * 2);
                    if (len > MaxHandleTableBytes)
                        throw new InvalidOperationException("system handle table exceeds the bounded scan limit");
                    buf = Marshal.AllocHGlobal(len);
                    continue;
                }
                if (status != 0) throw new InvalidOperationException("NtQuerySystemInformation failed with status 0x" + status.ToString("X8"));
                ok = true;
                break;
            }
            if (!ok) throw new InvalidOperationException("handle table kept growing during scan");
            var count = Marshal.ReadIntPtr(buf).ToInt64();
            var entrySize = Marshal.SizeOf<SYSTEM_HANDLE_ENTRY>();
            var basePtr = IntPtr.Add(buf, IntPtr.Size * 2);   // skip NumberOfHandles + Reserved
            for (long i = 0; i < count; i++)
                visit(Marshal.PtrToStructure<SYSTEM_HANDLE_ENTRY>(IntPtr.Add(basePtr, checked((int)(i * entrySize)))));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
