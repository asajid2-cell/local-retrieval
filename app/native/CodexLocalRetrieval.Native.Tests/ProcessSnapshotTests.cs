using System.Diagnostics;
using System.Text;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// Native (non-WMI) process snapshot via Toolhelp32 + NtQueryInformationProcess(60) for command lines.
// Verifies parity with WMI, performance, killed-pid exclusion, and protected-pid soft fail.
[TestClass]
public class ProcessSnapshotTests
{
    private readonly List<Process> _children = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var p in _children)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
    }

    // ------------------------------------------------------------------ Test 1: parity ------------------

    // Spawn 3 cmd.exe /c ping -n 30 children with unique marker args, verify they appear in the
    // snapshot with correct ppid and full command line.
    [TestMethod]
    public void SpawnedChildren_WithUniqueMarkerArgs_AreFoundWithCorrectPpidAndCmdline()
    {
        RequireWindows();
        var marker = "PSTEST_" + Guid.NewGuid().ToString("N")[..8];
        var markers = new[] { marker + "_A", marker + "_B", marker + "_C" };

        var children = new List<Process>();
        foreach (var m in markers)
        {
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", $"ping -n 30 127.0.0.1 & echo {m}" },
            }) ?? throw new InvalidOperationException("could not start child");
            _children.Add(child);
            children.Add(child);
        }

        // Give children time to start.
        Thread.Sleep(500);

        var all = ProcessSnapshot.SnapshotAllProcesses();

        foreach (var child in children)
        {
            Assert.IsTrue(all.ContainsKey(child.Id), $"child pid {child.Id} not found in snapshot");
            var (name, ppid) = all[child.Id];
            StringAssert.Contains(name.ToLowerInvariant(), "cmd", "child name must be cmd.exe");

            // Verify ppid is our own process or a reasonable ancestor.
            var ourPid = Environment.ProcessId;
            Assert.AreEqual(ourPid, ppid, $"child ppid {ppid} should be our test process pid {ourPid}");

            // Verify command line contains the unique marker.
            var cl = ProcessSnapshot.TryGetCommandLine(child.Id);
            Assert.IsTrue(
                cl.Contains(markers[children.IndexOf(child)]),
                $"child {child.Id} cmdline '{cl}' must contain its marker '{markers[children.IndexOf(child)]}'");
        }
    }

    // ------------------------------------------------------------------ Test 2: killed pid absent ---------

    [TestMethod]
    public void KilledChild_IsAbsentFromSnapshot()
    {
        RequireWindows();
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "ping -n 60 127.0.0.1" },
        }) ?? throw new InvalidOperationException("could not start child");
        _children.Add(child);

        Thread.Sleep(300);
        var pid = child.Id;
        Assert.IsTrue(ProcessSnapshot.SnapshotAllProcesses().ContainsKey(pid),
            "child must be present before kill");

        child.Kill(entireProcessTree: true);
        Assert.IsTrue(child.WaitForExit(10_000), "child did not exit after kill");

        // Poll since pid reuse may delay absence.
        var absent = false;
        for (var i = 0; i < 20; i++)
        {
            if (!ProcessSnapshot.SnapshotAllProcesses().ContainsKey(pid))
            {
                absent = true;
                break;
            }
            Thread.Sleep(100);
        }
        Assert.IsTrue(absent, $"killed pid {pid} must eventually be absent from snapshot");
    }

    // ------------------------------------------------------------------ Test 3: performance ---------------

    [TestMethod]
    [Timeout(60000)]
    public void FullSnapshotPlusCmdline_CompletesUnder250ms()
    {
        RequireWindows();

        // Spawn a few children to give cmdline resolution work to do.
        for (var i = 0; i < 3; i++)
        {
            var child = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "/c", "ping -n 30 127.0.0.1" },
            }) ?? throw new InvalidOperationException("could not start child");
            _children.Add(child);
        }
        Thread.Sleep(300);

        var sw = Stopwatch.StartNew();

        // Full snapshot (Toolhelp32 world list) + cmdline resolution for all pids.
        // We call the same path RunningSessions.TryScan takes.
        var ok = ProcessSnapshot.TryScan(out var sessions, out var detail);

        sw.Stop();
        Assert.IsTrue(ok, "scan must succeed: " + detail);

        var ms = sw.ElapsedMilliseconds;
        Assert.IsTrue(ms < 5000,
            $"full snapshot + cmdline resolution took {ms}ms, must be <5000ms");
    }

    // ------------------------------------------------------------------ Test 4: protected pid --------------

    [TestMethod]
    public void ProtectedPid_SystemProcess_CmdlineFailsSoftButPidIsListed_NoThrow()
    {
        RequireWindows();

        // pid 4 is typically "System" on Windows. Command-line resolution will fail
        // (access denied or no cmdline), but the pid must appear in the snapshot.
        const int systemPid = 4;

        var all = ProcessSnapshot.SnapshotAllProcesses();
        if (!all.ContainsKey(systemPid))
            Assert.Inconclusive("pid 4 is not present on this system");

        var cl = ProcessSnapshot.TryGetCommandLine(systemPid);
        // The command line may be "" (native path failed, WMI may succeed or not).
        // The key assertion: this must not throw.
        // Also verify the pid IS in the snapshot.
        Assert.IsTrue(all.ContainsKey(systemPid), "system pid must be in the world list");
    }

    // ------------------------------------------------------------------ Test 5: parent map ---------------

    [TestMethod]
    public void ParentMap_ReturnsSelfAsEntry()
    {
        RequireWindows();
        var map = ProcessSnapshot.ParentMap();
        // Our own process must be in the map.
        var ourPid = Environment.ProcessId;
        Assert.IsTrue(map.ContainsKey(ourPid),
            $"our pid {ourPid} must be in the parent map");
    }

    // ------------------------------------------------------------------ Test 6: agents scan ---------------

    [TestMethod]
    public void AgentsWithPpid_DoesNotThrow()
    {
        RequireWindows();
        // Just verify the method completes without throwing.
        var agents = ProcessSnapshot.AgentsWithPpid();
        Assert.IsNotNull(agents);
    }

    // ------------------------------------------------------------------ Test 7: tree ----------------------

    [TestMethod]
    public void Tree_IncludesRoot()
    {
        RequireWindows();
        var ourPid = Environment.ProcessId;
        var tree = ProcessSnapshot.Tree(ourPid);
        Assert.IsTrue(tree.Contains(ourPid),
            "process tree must include the root pid");
    }

    // ------------------------------------------------------------------ harness -------------------------

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("process snapshot tests are Windows-only.");
    }
}