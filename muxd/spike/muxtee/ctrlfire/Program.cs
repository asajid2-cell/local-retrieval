using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// B6 Ctrl+C proof: the standard automated Ctrl+C, per the reviewer's recipe. A pty cannot produce a
// console CONTROL event (a raw ^C byte or GenerateConsoleCtrlEvent(CTRL_BREAK) is not CTRL_C_EVENT),
// so this helper does it the way the OS does:
//
//   * launch muxtee in a NEW CONSOLE with CREATE_NEW_CONSOLE and NOT CREATE_NEW_PROCESS_GROUP, so muxtee
//     and everything it spawns PROCESS Ctrl+C. The "ignore Ctrl+C" flag is inherited, so the launcher
//     must not set it either - we explicitly clear it with SetConsoleCtrlHandler(NULL, FALSE) before
//     spawning;
//   * from this helper: FreeConsole, AttachConsole(muxtee_pid), SetConsoleCtrlHandler(NULL, TRUE) so the
//     helper survives, GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0).
//
//   args: <mode once|keep|raw> <muxteeExe> <reportPath> <tag>
//     once -> muxtee -- cmd.exe /c ping -t 127.0.0.1   (tab closes when the command finishes)
//     keep -> muxtee -- cmd.exe /k ping -t 127.0.0.1   (tab stays, prompt returns)
//     raw  -> cmd.exe /c ping -t 127.0.0.1             (no muxtee; reads cmd's own interrupted exit code)
//
// The console process list before/after the event says exactly who died. For `once`: ping, cmd and muxtee
// all gone, and muxtee's exit code is cmd's (NOT 0xC000013A STATUS_CONTROL_C_EXIT - that would mean
// muxtee itself was killed by the ^C). For `keep`: ping gone, muxtee and cmd both still alive.
//
// Run:  dotnet build muxd/spike/muxtee/ctrlfire/ctrlfire.csproj -c Release
//       <ctrlfire.exe> keep <path-to-muxtee.exe> <report.txt> green-keep
// RED:  comment out the ConsoleApi.SetConsoleCtrlHandler(PassthroughCtrl, true) line in
//       app/native/MuxTee/Program.cs, rebuild muxtee, and run `keep` again - muxtee must then die on ^C
//       (exit 0xC000013A, muxteeAlive=False).
static class CtrlFire
{
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit,
        uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GenerateConsoleCtrlEvent(uint evt, uint group);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr h, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true)] static extern uint GetConsoleProcessList(uint[] list, uint count);

    const uint CREATE_NEW_CONSOLE = 0x00000010;
    const uint STATUS_CONTROL_C_EXIT = 0xC000013A;
    const uint WAIT_TIMEOUT = 0x102;

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    static string ConsolePids()
    {
        var buf = new uint[256];
        uint n = GetConsoleProcessList(buf, (uint)buf.Length);
        var sb = new StringBuilder();
        for (uint i = 0; i < n && i < buf.Length; i++) { sb.Append(buf[i]); sb.Append(' '); }
        return "[" + sb.ToString().Trim() + "]";
    }

    static bool Alive(IntPtr h) => WaitForSingleObject(h, 0) == WAIT_TIMEOUT;

    static int CountPing() { try { return Process.GetProcessesByName("ping").Length; } catch { return -1; } }

    static int Main(string[] args)
    {
        if (args.Length < 4) { Console.Error.WriteLine("usage: ctrlfire <once|keep|raw> <exe> <report> <tag>"); return 2; }
        string mode = args[0], exe = args[1], report = args[2], tag = args[3];
        string switchFlag = mode == "keep" ? "/k" : "/c";

        var log = new StringBuilder();
        try
        {
            // Clear any inherited "ignore Ctrl+C" so muxtee (and its children) PROCESS Ctrl+C.
            SetConsoleCtrlHandler(IntPtr.Zero, false);

            // Force the PASSTHROUGH path (a real passthrough tab sets this), or muxtee would own its own
            // ConPTY and the child would not be on this console at all.
            Environment.SetEnvironmentVariable("MUXTEE_DISABLE", "1");

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            // raw = no muxtee at all, to read cmd's own exit code for an interrupted /c command.
            var cmd = new StringBuilder(mode == "raw"
                ? "cmd.exe /c ping -t 127.0.0.1"
                : "\"" + exe + "\" -- cmd.exe " + switchFlag + " ping -t 127.0.0.1");
            if (!CreateProcessW(null, cmd, IntPtr.Zero, IntPtr.Zero, false, CREATE_NEW_CONSOLE,
                                IntPtr.Zero, null, ref si, out var pi))
            {
                log.AppendLine("CreateProcess failed err=" + Marshal.GetLastWin32Error());
                File.WriteAllText(report, log.ToString());
                return 1;
            }
            log.AppendLine("launched muxtee pid=" + pi.dwProcessId + " mode=" + mode + " flags=CREATE_NEW_CONSOLE");

            Thread.Sleep(3500);
            int pingBefore = CountPing();
            log.AppendLine("pre-fire ping=" + pingBefore + " muxteeAlive=" + Alive(pi.hProcess));

            // The helper must survive the event it fires.
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            FreeConsole();
            bool attached = AttachConsole((uint)pi.dwProcessId);
            int attachErr = Marshal.GetLastWin32Error();
            string before = attached ? ConsolePids() : "attach-failed-" + attachErr;
            bool ok = attached && GenerateConsoleCtrlEvent(0, 0);
            int fireErr = Marshal.GetLastWin32Error();
            FreeConsole();
            log.AppendLine("attach=" + attached + "(" + attachErr + ") fire CTRL_C_EVENT(0,0) ok=" + ok + " err=" + fireErr);
            log.AppendLine("consolePids before=" + before);

            Thread.Sleep(3500);
            int pingAfter = CountPing();
            bool muxteeAlive = Alive(pi.hProcess);
            uint exit = 0; if (!muxteeAlive) GetExitCodeProcess(pi.hProcess, out exit);
            log.AppendLine("post-fire ping=" + pingAfter + " muxteeAlive=" + muxteeAlive +
                           " muxteeExit=" + (muxteeAlive ? "-" : "0x" + exit.ToString("X8")) +
                           " killedByCtrlC=" + (!muxteeAlive && exit == STATUS_CONTROL_C_EXIT));

            // Re-attach to read the console roster now (muxtee may be gone; attach to a survivor if any).
            FreeConsole();
            string after = "n/a";
            if (muxteeAlive && AttachConsole((uint)pi.dwProcessId)) { after = ConsolePids(); FreeConsole(); }
            log.AppendLine("consolePids after=" + after);

            log.AppendLine(string.Format("RESULT mode={0} tag={1} muxteePid={2} pingBefore={3} pingAfter={4} muxteeAlive={5} exit={6}",
                mode, tag, pi.dwProcessId, pingBefore, pingAfter, muxteeAlive,
                muxteeAlive ? "-" : "0x" + exit.ToString("X8")));

            CloseHandle(pi.hProcess); CloseHandle(pi.hThread);
        }
        catch (Exception ex) { log.AppendLine("EXCEPTION " + ex.GetType().Name + ": " + ex.Message); }
        finally { try { File.WriteAllText(report, log.ToString()); } catch { } }
        return 0;
    }
}
