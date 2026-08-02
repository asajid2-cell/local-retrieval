using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class WindowsProcessJobTests
{
    [TestMethod]
    public async Task Dispose_TerminatesAttachedProcessAndItsDescendant()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        var workingDirectory = CreateFixtureWorkingDirectory();
        var childPid = 0;
        try
        {
            using var parent = StartParentWaitingForSignal(workingDirectory);
            using var job = WindowsProcessJob.CreateKillOnClose();
            try
            {
                job.AttachOrTerminate(parent);
                await parent.StandardInput.WriteLineAsync("spawn");
                await parent.StandardInput.FlushAsync();

                var childLine = await parent.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(int.TryParse(childLine, out childPid), "parent did not report its child pid");
                Assert.IsFalse(parent.HasExited, "parent exited before the containment assertion");
                Assert.IsTrue(IsAlive(childPid), "descendant exited before job close");

                job.Dispose();

                await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await WaitUntilExitedAsync(childPid, TimeSpan.FromSeconds(10));
                Assert.IsTrue(parent.HasExited);
                AssertProcessExited(childPid);
            }
            finally
            {
                TryKill(parent);
                if (childPid > 0) TryKill(childPid);
            }
        }
        finally
        {
            TryDeleteFixtureWorkingDirectory(workingDirectory);
        }
    }

    [TestMethod]
    public async Task AttachAfterDispose_TerminatesTheUncontainedProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        var workingDirectory = CreateFixtureWorkingDirectory();
        try
        {
            using var process = StartSleeper(workingDirectory);
            var job = WindowsProcessJob.CreateKillOnClose();
            job.Dispose();

            var error = Assert.ThrowsExactly<ProcessContainmentException>(
                () => job.AttachOrTerminate(process));

            Assert.IsTrue(error.TerminationConfirmed);
            Assert.IsInstanceOfType<ObjectDisposedException>(error.InnerException);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(process.HasExited, "failed containment must not leak the child");
        }
        finally
        {
            TryDeleteFixtureWorkingDirectory(workingDirectory);
        }
    }

    [TestMethod]
    public async Task StartContained_AssignsBeforeImmediateDescendantSpawn()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        var workingDirectory = CreateFixtureWorkingDirectory();
        var childPid = 0;
        try
        {
            using var job = WindowsProcessJob.CreateKillOnClose();
            using var process = job.StartContained(ImmediateForkStartInfo(workingDirectory));
            try
            {
                var childLine = await process.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(10));
                Assert.IsTrue(int.TryParse(childLine, out childPid), "contained parent did not report its child pid");
                Assert.IsFalse(process.HasExited);
                Assert.IsTrue(IsAlive(childPid));

                job.Dispose();

                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
                await WaitUntilExitedAsync(childPid, TimeSpan.FromSeconds(10));
                AssertProcessExited(childPid);
            }
            finally
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                if (childPid > 0) TryKill(childPid);
            }
        }
        finally
        {
            TryDeleteFixtureWorkingDirectory(workingDirectory);
        }
    }

    [TestMethod]
    public void AttachCurrentProcess_IsRefusedWithoutKillingOwner()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        using var job = WindowsProcessJob.CreateKillOnClose();
        using var current = Process.GetCurrentProcess();

        Assert.ThrowsExactly<InvalidOperationException>(() => job.AttachOrTerminate(current));
        Assert.IsFalse(current.HasExited);
    }

    [TestMethod]
    public async Task AbruptOwnerTermination_ClosesJobAndKillsContainedTree()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        var workingDirectory = CreateFixtureWorkingDirectory();
        var parentPid = 0;
        var childPid = 0;
        try
        {
            using var owner = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = workingDirectory, // A mapped network drive is not a legal working directory for a spawned process here.
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                ArgumentList = { FindProcessJobProbe(), "owner-crash" },
            }) ?? throw new InvalidOperationException("could not start process-job owner probe");
            try
            {
                var line = await owner.StandardOutput.ReadLineAsync()
                    .WaitAsync(TimeSpan.FromSeconds(15));
                var parts = (line ?? "").Split(':');
                Assert.HasCount(2, parts);
                Assert.IsTrue(int.TryParse(parts[0], out parentPid));
                Assert.IsTrue(int.TryParse(parts[1], out childPid));
                Assert.IsTrue(IsAlive(parentPid));
                Assert.IsTrue(IsAlive(childPid));

                owner.Kill(entireProcessTree: false);
                await owner.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));

                await WaitUntilExitedAsync(parentPid, TimeSpan.FromSeconds(10));
                await WaitUntilExitedAsync(childPid, TimeSpan.FromSeconds(10));
                AssertProcessExited(parentPid);
                AssertProcessExited(childPid);
            }
            finally
            {
                TryKill(owner);
                if (parentPid > 0) TryKill(parentPid);
                if (childPid > 0) TryKill(childPid);
            }
        }
        finally
        {
            TryDeleteFixtureWorkingDirectory(workingDirectory);
        }
    }

    [TestMethod]
    public void NativeLaunchContract_UsesProcessWideGateAndUnicodeJobApi()
    {
        var gate = typeof(WindowsProcessJob).GetField(
            "ProcessCreationGate",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.IsNotNull(gate);
        Assert.IsTrue(gate.IsStatic);

        var createJob = typeof(WindowsProcessJob).GetMethod(
            "CreateJobObjectW",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(WindowsProcessJob).FullName, "CreateJobObjectW");
        var import = createJob.GetCustomAttribute<DllImportAttribute>()
            ?? throw new InvalidOperationException("CreateJobObjectW is missing DllImportAttribute");
        Assert.AreEqual(CharSet.Unicode, import.CharSet);
    }

    private static Process StartParentWaitingForSignal(string workingDirectory)
    {
        const string script = """
            $null = [Console]::In.ReadLine()
            $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -WindowStyle Hidden -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -PassThru
            [Console]::Out.WriteLine($child.Id)
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes(script.Replace("\r\n", "\n")));
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory, // A mapped network drive is not a legal working directory for a spawned process here.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
        }) ?? throw new InvalidOperationException("could not start containment test parent");
    }

    private static Process StartSleeper(string workingDirectory)
    {
        var encoded = Convert.ToBase64String(
            Encoding.Unicode.GetBytes("Start-Sleep -Seconds 120"));
        return Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory, // A mapped network drive is not a legal working directory for a spawned process here.
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
        }) ?? throw new InvalidOperationException("could not start containment test sleeper");
    }

    private static ProcessStartInfo ImmediateForkStartInfo(string workingDirectory)
    {
        const string script = """
            $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -WindowStyle Hidden -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 120') -PassThru
            [Console]::Out.WriteLine($child.Id)
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return new ProcessStartInfo
        {
            FileName = "powershell.exe",
            WorkingDirectory = workingDirectory, // A mapped network drive is not a legal working directory for a spawned process here.
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
        };
    }

    private static string FindProcessJobProbe()
    {
        var directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CodexLocalRetrieval.sln")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate the repository root for the process-job probe.");
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var path = Path.Combine(
            directory.FullName,
            "native",
            "CodexLocalRetrieval.ProcessJobProbe",
            "bin",
            configuration,
            "net8.0",
            "CodexLocalRetrieval.ProcessJobProbe.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException("Process-job probe was not built.", path);
        return path;
    }

    private static string CreateFixtureWorkingDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"CodexLocalRetrieval.Native.Tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteFixtureWorkingDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static async Task WaitUntilExitedAsync(int pid, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid)) return;
            await Task.Delay(50);
        }
    }

    private static void AssertProcessExited(int pid) =>
        Assert.IsFalse(IsAlive(pid), $"process {pid} survived job close");

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }

    private static void TryKill(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
    }
}
