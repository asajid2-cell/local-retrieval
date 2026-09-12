using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CodexLocalRetrieval.Core.Agents;

public sealed class ProcessContainmentException : Exception
{
    public ProcessContainmentException(
        string message,
        bool terminationConfirmed,
        Exception? innerException = null)
        : base(message, innerException) =>
        TerminationConfirmed = terminationConfirmed;

    public bool TerminationConfirmed { get; }
}

public sealed class ContainedProcess : IDisposable
{
    private readonly bool _ownsSeparateStreams;
    private int _disposed;

    internal ContainedProcess(
        Process process,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError,
        bool ownsSeparateStreams)
    {
        Process = process;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _ownsSeparateStreams = ownsSeparateStreams;
    }

    public Process Process { get; }
    public StreamWriter StandardInput { get; }
    public StreamReader StandardOutput { get; }
    public StreamReader StandardError { get; }
    public int Id => Process.Id;
    public bool HasExited => Process.HasExited;
    public int ExitCode => Process.ExitCode;

    public void Kill(bool entireProcessTree) => Process.Kill(entireProcessTree);
    public Task WaitForExitAsync(CancellationToken cancellationToken) =>
        Process.WaitForExitAsync(cancellationToken);

    public static ContainedProcess Start(
        ProcessStartInfo startInfo,
        Func<ProcessStartInfo, Process?>? processStarter = null)
    {
        var process = (processStarter is null ? Process.Start(startInfo) : processStarter(startInfo))
            ?? throw new InvalidOperationException("process start returned no process");
        try
        {
            return FromStartedProcess(process);
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
            process.Dispose();
            throw;
        }
    }

    internal static ContainedProcess FromStartedProcess(Process process) =>
        new(
            process,
            process.StandardInput,
            process.StandardOutput,
            process.StandardError,
            ownsSeparateStreams: false);

    internal void CloseStandardInputPipe() => StandardInput.BaseStream.Dispose();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_ownsSeparateStreams)
        {
            try { StandardInput.BaseStream.Dispose(); } catch { }
            try { StandardInput.Dispose(); } catch { }
            try { StandardOutput.Dispose(); } catch { }
            try { StandardError.Dispose(); } catch { }
        }
        Process.Dispose();
    }
}

public interface IProcessContainment
{
    ContainedProcess StartContained(ProcessStartInfo startInfo);
}

// Owns server-controlled agent processes in a Windows Job Object. Production launches are created
// suspended, assigned before any child code runs, then resumed. Closing the final job handle kills
// every attached process and descendant, including after an abrupt owner crash.
public sealed class WindowsProcessJob : IProcessContainment, IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private const uint CreateSuspended = 0x00000004;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint HandleFlagInherit = 0x00000001;
    private const nuint ProcThreadAttributeHandleList = 0x00020002;
    private const uint WaitObject0 = 0;
    private const uint TerminationWaitMilliseconds = 5000;

    // Inheritable pipe handles exist briefly before CreateProcessW consumes the explicit handle
    // list. Serialize that window across every job instance so another app-owned launch cannot
    // inherit a different launch's pipe before its parent endpoint is made non-inheritable.
    private static readonly object ProcessCreationGate = new();
    private readonly object _gate = new();
    private readonly SafeFileHandle _job;
    private bool _disposed;

    private WindowsProcessJob(SafeFileHandle job) => _job = job;

    public static WindowsProcessJob CreateKillOnClose()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Windows Job Objects require Windows.");

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not create the child-process job.");
        }

        var limits = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose,
            },
        };
        if (!SetInformationJobObject(
                job,
                JobObjectInfoClass.ExtendedLimitInformation,
                ref limits,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            job.Dispose();
            throw new Win32Exception(error, "Could not configure kill-on-close process containment.");
        }

        return new WindowsProcessJob(job);
    }

    public ContainedProcess StartContained(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            lock (ProcessCreationGate)
                return StartSuspendedContained(startInfo);
        }
    }

    public void AttachOrTerminate(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (process.Id == Environment.ProcessId)
            throw new InvalidOperationException("The owner process cannot be attached to its child-process job.");

        try
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (process.HasExited)
                    throw new InvalidOperationException("The child process exited before containment was established.");
                if (!AssignProcessToJobObject(_job, process.SafeHandle))
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        $"Could not attach child process {process.Id} to the owner job.");
            }
        }
        catch (Exception ex) when (ex is not ProcessContainmentException)
        {
            var terminated = TryTerminate(process);
            throw new ProcessContainmentException(
                $"Could not establish containment for child process {SafeProcessId(process)}.",
                terminated,
                ex);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _job.Dispose();
        }
    }

    private ContainedProcess StartSuspendedContained(ProcessStartInfo startInfo)
    {
        ValidateStartInfo(startInfo);

        SafeFileHandle? childStdin = null;
        SafeFileHandle? parentStdin = null;
        SafeFileHandle? parentStdout = null;
        SafeFileHandle? childStdout = null;
        SafeFileHandle? parentStderr = null;
        SafeFileHandle? childStderr = null;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleList = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        ProcessInformation processInfo = default;
        ContainedProcess? contained = null;
        FileStream? inputStream = null;
        FileStream? outputStream = null;
        FileStream? errorStream = null;
        StreamWriter? inputWriter = null;
        StreamReader? outputReader = null;
        StreamReader? errorReader = null;
        var processCreated = false;
        var attributeListInitialized = false;

        try
        {
            CreateRedirectedPipe(out childStdin, out parentStdin, childReads: true);
            CreateRedirectedPipe(out parentStdout, out childStdout, childReads: false);
            CreateRedirectedPipe(out parentStderr, out childStderr, childReads: false);

            nuint attributeBytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
            attributeList = Marshal.AllocHGlobal(checked((int)attributeBytes));
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not initialize process attribute list.");
            attributeListInitialized = true;

            handleList = Marshal.AllocHGlobal(IntPtr.Size * 3);
            Marshal.WriteIntPtr(handleList, 0, childStdin.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size, childStdout.DangerousGetHandle());
            Marshal.WriteIntPtr(handleList, IntPtr.Size * 2, childStderr.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    ProcThreadAttributeHandleList,
                    handleList,
                    (nuint)(IntPtr.Size * 3),
                    IntPtr.Zero,
                    IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not restrict inherited process handles.");
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    Cb = Marshal.SizeOf<StartupInfoEx>(),
                    Flags = StartfUseStdHandles,
                    StandardInput = childStdin.DangerousGetHandle(),
                    StandardOutput = childStdout.DangerousGetHandle(),
                    StandardError = childStderr.DangerousGetHandle(),
                },
                AttributeList = attributeList,
            };
            environment = BuildEnvironmentBlock(startInfo);
            var commandLine = new StringBuilder(BuildCommandLine(startInfo));
            var creationFlags = CreateSuspended | CreateUnicodeEnvironment | ExtendedStartupInfoPresent;
            if (startInfo.CreateNoWindow) creationFlags |= CreateNoWindow;

            if (!CreateProcessW(
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    inheritHandles: true,
                    creationFlags,
                    environment,
                    string.IsNullOrWhiteSpace(startInfo.WorkingDirectory) ? null : startInfo.WorkingDirectory,
                    ref startup,
                    out processInfo))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Could not start contained process {startInfo.FileName}.");
            }
            processCreated = true;

            childStdin.Dispose();
            childStdin = null;
            childStdout.Dispose();
            childStdout = null;
            childStderr.Dispose();
            childStderr = null;

            var process = Process.GetProcessById(processInfo.ProcessId);
            inputStream = new FileStream(parentStdin, FileAccess.Write, 4096, isAsync: false);
            parentStdin = null;
            outputStream = new FileStream(parentStdout, FileAccess.Read, 4096, isAsync: false);
            parentStdout = null;
            errorStream = new FileStream(parentStderr, FileAccess.Read, 4096, isAsync: false);
            parentStderr = null;
            inputWriter = new StreamWriter(
                inputStream,
                startInfo.StandardInputEncoding ?? new UTF8Encoding(false));
            inputStream = null;
            outputReader = new StreamReader(
                outputStream,
                startInfo.StandardOutputEncoding ?? Encoding.UTF8);
            outputStream = null;
            errorReader = new StreamReader(
                errorStream,
                startInfo.StandardErrorEncoding ?? Encoding.UTF8);
            errorStream = null;
            contained = new ContainedProcess(
                process,
                inputWriter,
                outputReader,
                errorReader,
                ownsSeparateStreams: true);
            inputWriter = null;
            outputReader = null;
            errorReader = null;

            if (!AssignProcessToJobObject(_job, processInfo.Process))
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not attach suspended child process {processInfo.ProcessId} to the owner job.");
            // Retain exit-query access before the child can finish; a PID-only Process cannot
            // recover an exited process's handle after the creation handle is closed below.
            _ = process.SafeHandle;
            if (ResumeThread(processInfo.Thread) == uint.MaxValue)
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    $"Could not resume contained child process {processInfo.ProcessId}.");

            return contained;
        }
        catch (Exception ex)
        {
            var terminated = !processCreated || TerminateAndConfirm(processInfo.Process);
            contained?.Dispose();
            if (!processCreated) throw;
            throw new ProcessContainmentException(
                $"Could not establish creation-time containment for child process {processInfo.ProcessId}.",
                terminated,
                ex);
        }
        finally
        {
            childStdin?.Dispose();
            parentStdin?.Dispose();
            parentStdout?.Dispose();
            childStdout?.Dispose();
            parentStderr?.Dispose();
            childStderr?.Dispose();
            inputWriter?.Dispose();
            outputReader?.Dispose();
            errorReader?.Dispose();
            inputStream?.Dispose();
            outputStream?.Dispose();
            errorStream?.Dispose();
            if (processInfo.Thread != IntPtr.Zero) CloseHandle(processInfo.Thread);
            if (processInfo.Process != IntPtr.Zero) CloseHandle(processInfo.Process);
            if (attributeListInitialized) DeleteProcThreadAttributeList(attributeList);
            if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
            if (handleList != IntPtr.Zero) Marshal.FreeHGlobal(handleList);
            if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
        }
    }

    private static void ValidateStartInfo(ProcessStartInfo startInfo)
    {
        if (startInfo.UseShellExecute)
            throw new NotSupportedException("Contained process launch requires UseShellExecute=false.");
        if (!startInfo.RedirectStandardInput
            || !startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new NotSupportedException("Contained process launch requires all standard streams to be redirected.");
        }
        if (string.IsNullOrWhiteSpace(startInfo.FileName))
            throw new ArgumentException("Contained process launch requires an executable.", nameof(startInfo));
    }

    private static void CreateRedirectedPipe(
        out SafeFileHandle first,
        out SafeFileHandle second,
        bool childReads)
    {
        var attributes = new SecurityAttributes
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = 1,
        };
        if (!CreatePipe(out var read, out var write, ref attributes, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not create redirected process pipe.");

        first = read;
        second = write;
        var parentHandle = childReads ? write : read;
        if (!SetHandleInformation(parentHandle, HandleFlagInherit, 0))
        {
            var error = Marshal.GetLastWin32Error();
            read.Dispose();
            write.Dispose();
            throw new Win32Exception(error, "Could not make the parent pipe handle non-inheritable.");
        }
    }

    private static IntPtr BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var block = string.Join(
                '\0',
                startInfo.Environment
                    .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                    .Select(pair => pair.Key + "=" + pair.Value))
            + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var parts = new List<string> { QuoteWindowsArgument(startInfo.FileName) };
        if (startInfo.ArgumentList.Count > 0)
            parts.AddRange(startInfo.ArgumentList.Select(QuoteWindowsArgument));
        else if (!string.IsNullOrWhiteSpace(startInfo.Arguments))
            parts.Add(startInfo.Arguments);
        return string.Join(" ", parts);
    }

    private static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0
            && !argument.Any(ch => char.IsWhiteSpace(ch) || ch == '"'))
            return argument;

        var result = new StringBuilder(argument.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var ch in argument)
        {
            if (ch == '\\')
            {
                backslashes++;
                continue;
            }
            if (ch == '"')
            {
                result.Append('\\', backslashes * 2 + 1).Append('"');
                backslashes = 0;
                continue;
            }
            result.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }
        result.Append('\\', backslashes * 2).Append('"');
        return result.ToString();
    }

    private static bool TryTerminate(Process process)
    {
        try
        {
            if (process.HasExited) return true;
            process.Kill(entireProcessTree: true);
            return process.WaitForExit((int)TerminationWaitMilliseconds);
        }
        catch
        {
            try { return process.HasExited; } catch { return false; }
        }
    }

    private static bool TerminateAndConfirm(IntPtr process)
    {
        if (process == IntPtr.Zero) return true;
        if (!TerminateProcess(process, 1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 5) return false;
        }
        var wait = WaitForSingleObject(process, TerminationWaitMilliseconds);
        return wait == WaitObject0;
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; } catch { return 0; }
    }

    private enum JobObjectInfoClass
    {
        ExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public int InheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public uint Flags;
        public short ShowWindow;
        public short Reserved2Bytes;
        public IntPtr Reserved2;
        public IntPtr StandardInput;
        public IntPtr StandardOutput;
        public IntPtr StandardError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr jobAttributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInfoClass infoClass,
        ref JobObjectExtendedLimitInformation info,
        uint infoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        SafeFileHandle job,
        IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(
        out SafeFileHandle readPipe,
        out SafeFileHandle writePipe,
        ref SecurityAttributes pipeAttributes,
        uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        SafeFileHandle handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref nuint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        nuint attribute,
        IntPtr value,
        nuint size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
