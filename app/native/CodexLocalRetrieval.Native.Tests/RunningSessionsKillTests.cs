using System.Diagnostics;
using System.Text;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// Kill's target resolution, its identity-checked verification, and the job-object tied kill.
//
// The failure these pin down: Kill used to resolve targets from the WMI command-line scan and NOTHING else, so
// an owner visible only through Claude's registry or through an open transcript handle could not be killed at
// all - and with an empty scan a sessionId-only kill gave up outright. Verification then watched bare pids, so
// one recycled pid in the tree turned a successful kill into a permanent "did not exit".
//
// The WMI scan and the Claude registry are machine-global, so they are injected (the KillSignals seam); the
// owner records, the per-pid handle probe, the process trees, the job objects and every identity check run for
// real against real child processes.
[TestClass]
public class RunningSessionsKillTests
{
    private readonly List<Process> _children = new();
    private readonly List<string> _folders = new();

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var p in _children)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        foreach (var folder in _folders)
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }

    // ---- 1. target resolution unions the signals ----------------------------------------------------

    // Claude's own registry is the ONLY place an idle session appears (no --resume on the command line, no
    // open transcript handle). The old Kill could not see it and refused with "process scan returned nothing".
    [TestMethod]
    public void RegistryOnlyOwner_ResolvesATargetAndIsKilled()
    {
        RequireWindows();
        var sid = Guid.NewGuid().ToString();
        var owner = StartSleeper();

        var (ok, detail) = RunningSessions.Kill(
            new[] { sid },
            pid: 0,
            expectedStartedUtc: null,
            signals: Signals(registry: (sid, owner.Id), recordRoot: TempRecordRoot()));

        Assert.IsTrue(ok, detail);
        StringAssert.Contains(detail, "live-session registry");
        Assert.IsTrue(owner.WaitForExit(15_000), "the registry-visible owner was not killed: " + detail);
    }

    // The per-pid transcript probe catches an owner running under a DIFFERENT session id that nonetheless holds
    // OUR transcript open - an alias/fork case no id comparison can find.
    [TestMethod]
    public void TranscriptHandleOwner_UnderADifferentSessionId_ResolvesATarget()
    {
        RequireWindows();
        var (sid, holder) = StartChildHoldingTranscript();
        // The scan sees the process but attributes it to an unrelated session; only its OPEN FILES tie it to us.
        var unrelated = new ArchiveService.RunningSessionInfo(
            holder.Id, "claude", Guid.NewGuid().ToString(), "Terminal", "", "");

        var (ok, detail) = RunningSessions.Kill(
            new[] { sid },
            pid: 0,
            expectedStartedUtc: null,
            signals: Signals(scan: new List<ArchiveService.RunningSessionInfo> { unrelated }, recordRoot: TempRecordRoot()));

        Assert.IsTrue(ok, detail);
        StringAssert.Contains(detail, "holds this transcript open");
        Assert.IsTrue(holder.WaitForExit(15_000), "the transcript-holding owner was not killed: " + detail);
    }

    // The owner record is the last resort: nothing else can see this launch, but we wrote down which process we
    // started for it. It is only usable because the record carries a start time to prove the pid is still ours.
    [TestMethod]
    public void RecordedWrapperOnly_ResolvesATargetAfterTheIdentityCheck()
    {
        RequireWindows();
        var sid = Guid.NewGuid().ToString();
        var wrapper = StartSleeper();
        var root = TempRecordRoot();
        WriteRecord(root, sid, wrapper.Id, wrapper.StartTime.ToUniversalTime());

        var (ok, detail) = RunningSessions.Kill(
            new[] { sid }, pid: 0, expectedStartedUtc: null, signals: Signals(recordRoot: root));

        Assert.IsTrue(ok, detail);
        StringAssert.Contains(detail, "owner record");
        Assert.IsTrue(wrapper.WaitForExit(15_000), "the recorded wrapper was not killed: " + detail);
    }

    // THE reason the record needs a start time. Windows reissues pids within seconds under swarm load; a record
    // pointing at a recycled pid must resolve NO target, or take-control becomes a random-process killer.
    [TestMethod]
    public void RecordedWrapperWithAReusedPid_ResolvesNoTarget_AndLeavesTheProcessAlone()
    {
        RequireWindows();
        var sid = Guid.NewGuid().ToString();
        var innocent = StartSleeper();
        var root = TempRecordRoot();
        // Same pid, a start time from a launch that is long gone: this pid is NOT the process we recorded.
        WriteRecord(root, sid, innocent.Id, innocent.StartTime.ToUniversalTime().AddMinutes(-30));

        var (ok, detail) = RunningSessions.Kill(
            new[] { sid }, pid: 0, expectedStartedUtc: null, signals: Signals(recordRoot: root));

        Assert.IsTrue(ok, detail);
        StringAssert.Contains(detail, "already gone");
        Assert.IsFalse(innocent.HasExited, "a reused pid must never be killed on the strength of a stale record");
    }

    // ---- 2. a live pid tied by no signal is still refused --------------------------------------------

    [TestMethod]
    public void PidTiedByNoSignal_IsRefused_NotKilled()
    {
        RequireWindows();
        var innocent = StartSleeper();

        var (ok, detail) = RunningSessions.Kill(
            new[] { Guid.NewGuid().ToString() },
            pid: innocent.Id,
            expectedStartedUtc: null,
            signals: Signals(recordRoot: TempRecordRoot()));

        Assert.IsFalse(ok);
        StringAssert.Contains(detail, "not a tracked claude/codex agent");
        Assert.IsFalse(innocent.HasExited, "Kill is not a general-purpose process killer");
    }

    // ---- 3. answered-and-empty vs. could-not-answer ---------------------------------------------------

    [TestMethod]
    public void EverySignalAnsweredEmpty_ReportsAlreadyGone()
    {
        RequireWindows();

        var (ok, detail) = RunningSessions.Kill(
            new[] { Guid.NewGuid().ToString() },
            pid: 0,
            expectedStartedUtc: null,
            signals: Signals(recordRoot: TempRecordRoot()));

        Assert.IsTrue(ok);
        StringAssert.Contains(detail, "already gone");
    }

    // The distinction the old code collapsed: an empty scan was treated as proof of absence in one branch and
    // as failure in another. A signal that could not ANSWER can never produce "already gone".
    [TestMethod]
    public void ASignalThatCouldNotAnswer_FailsHonestly_InsteadOfClaimingAlreadyGone()
    {
        RequireWindows();

        var (ok, detail) = RunningSessions.Kill(
            new[] { Guid.NewGuid().ToString() },
            pid: 0,
            expectedStartedUtc: null,
            signals: new RunningSessions.KillSignals(
                Scan: () => (false, new List<ArchiveService.RunningSessionInfo>(), "WMI timed out"),
                ClaudeRegistry: () => (false, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase), new HashSet<int>(), "registry unreadable"),
                OwnerRecords: new SessionOwnerRecords.Options(TempRecordRoot())));

        Assert.IsFalse(ok, "an unanswerable signal set must not report success");
        StringAssert.Contains(detail, "couldn't verify");
        StringAssert.Contains(detail, "WMI timed out");
        StringAssert.Contains(detail, "registry unreadable");
        Assert.IsFalse(detail.Contains("already gone", StringComparison.OrdinalIgnoreCase));
    }

    // ---- 4. real tree, real record, no pid supplied ---------------------------------------------------

    // End to end on a real `cmd.exe` wrapper with a real child holding a real transcript: resolution comes from
    // the owner record alone, and the whole TREE must die - verified by identity, not by bare pid.
    [TestMethod]
    public void RecordedWrapperTree_IsKilledWholesale_WithNoPidSupplied()
    {
        RequireWindows();
        var (sid, wrapper, child) = StartWrapperTreeHoldingTranscript();
        var wrapperIdentity = Identity(wrapper.Id);
        var childIdentity = Identity(child);
        var root = TempRecordRoot();
        WriteRecord(root, sid, wrapper.Id, wrapper.StartTime.ToUniversalTime());

        var (ok, detail) = RunningSessions.Kill(
            new[] { sid }, pid: 0, expectedStartedUtc: null, signals: Signals(recordRoot: root));

        Assert.IsTrue(ok, detail);
        StringAssert.Contains(detail, "killed");
        Assert.IsTrue(RunningSessions.HasExitedByIdentity(wrapper.Id, wrapperIdentity),
            "the wrapper is still the process we snapshotted: " + detail);
        Assert.IsTrue(RunningSessions.HasExitedByIdentity(child, childIdentity),
            "the transcript-holding child outlived the tree kill: " + detail);
    }

    // ---- 5. job objects [F#7] -------------------------------------------------------------------------

    // TerminateJobObject has no enumeration window, so a child forked while a tree snapshot is being taken
    // cannot escape it. Assignment happens BEFORE the grandchild is spawned, which is the whole point:
    // membership is inherited at creation.
    [TestMethod]
    public void JobObject_TerminatesTheWrapperAndItsGrandchild_AndIsIdempotent()
    {
        RequireWindows();
        var (wrapper, grandchild) = StartWrapperInJobWithGrandchild(out var jobName);
        Assert.IsNotNull(jobName, "the wrapper could not be assigned to a job");
        var wrapperIdentity = Identity(wrapper.Id);
        var grandchildIdentity = Identity(grandchild);

        Assert.IsTrue(OwnerJobObjects.TryTerminate(jobName, out var first), first);
        Assert.IsTrue(wrapper.WaitForExit(15_000), "the wrapper survived TerminateJobObject");
        Assert.IsTrue(WaitForExitByIdentity(grandchild, grandchildIdentity, 15_000),
            "the grandchild survived TerminateJobObject - job membership must be inherited");
        Assert.IsTrue(RunningSessions.HasExitedByIdentity(wrapper.Id, wrapperIdentity));

        // Terminating a job whose members are all gone is the already-reached end state, not an error.
        Assert.IsTrue(OwnerJobObjects.TryTerminate(jobName, out var second), second);
        StringAssert.Contains(second, "already gone");

        OwnerJobObjects.ReleaseHandle(jobName);
        // With our handle dropped and no members left the name no longer resolves - still "already gone".
        Assert.IsTrue(OwnerJobObjects.TryTerminate(jobName, out var third), third);
        StringAssert.Contains(third, "no longer exists");
    }

    [TestMethod]
    public void TryAssign_ReturnsNullForAWrapperThatAlreadyExited()
    {
        RequireWindows();
        var exited = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "exit", "0" },
        })!;
        _children.Add(exited);
        Assert.IsTrue(exited.WaitForExit(10_000));

        // Best-effort means best-effort: a dead wrapper yields no job and never fails the launch.
        Assert.IsNull(OwnerJobObjects.TryAssign(exited));
    }

    // ---- 6. the reused-pid verification rule ----------------------------------------------------------

    // A pid alone cannot answer "did the thing I killed exit?". Under the old bare-pid check a recycled pid
    // read as "still alive" forever and failed an otherwise perfect kill.
    [TestMethod]
    public void HasExitedByIdentity_TreatsAChangedStartTimeAsExited()
    {
        RequireWindows();
        var live = StartSleeper();
        var identity = Identity(live.Id);

        Assert.IsFalse(RunningSessions.HasExitedByIdentity(live.Id, identity),
            "the same process at the same pid has plainly not exited");
        // Same pid, different start time = the pid was reused, so the process we snapshotted is dead.
        Assert.IsTrue(RunningSessions.HasExitedByIdentity(live.Id, identity!.Value.AddMinutes(-10)),
            "a reused pid must count as exited, not as a survivor");

        live.Kill(entireProcessTree: true);
        Assert.IsTrue(live.WaitForExit(10_000));
        Assert.IsTrue(RunningSessions.HasExitedByIdentity(live.Id, identity));
        // No snapshot identity available: fall back to the bare-pid check, as before.
        Assert.IsTrue(RunningSessions.HasExitedByIdentity(live.Id, null));
    }

    // ---- harness --------------------------------------------------------------------------------------

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Kill's process signals are Windows-only.");
    }

    // Empty-but-ANSWERED scan and registry by default: the point of most tests is that exactly one signal has
    // something to say.
    private static RunningSessions.KillSignals Signals(
        List<ArchiveService.RunningSessionInfo>? scan = null,
        (string Sid, int Pid)? registry = null,
        string? recordRoot = null)
    {
        var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (registry is not null) map[registry.Value.Sid] = registry.Value.Pid;
        return new RunningSessions.KillSignals(
            Scan: () => (true, scan ?? new List<ArchiveService.RunningSessionInfo>(), ""),
            ClaudeRegistry: () => (true, map, new HashSet<int>(), ""),
            OwnerRecords: recordRoot is null ? null : new SessionOwnerRecords.Options(recordRoot));
    }

    private static void WriteRecord(string root, string sid, int wrapperPid, DateTimeOffset? startedUtc, string? jobName = null)
        => Assert.IsTrue(SessionOwnerRecords.TryWrite(
            sid, null, wrapperPid, startedUtc, "terminal", out var detail,
            options: new SessionOwnerRecords.Options(root), jobName: jobName), detail);

    private static DateTime? Identity(int pid)
        => ProcessOpenFiles.TryGetAliveIdentity(pid, out var started) ? started : null;

    private static bool WaitForExitByIdentity(int pid, DateTime? identity, int milliseconds)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < deadline)
        {
            if (RunningSessions.HasExitedByIdentity(pid, identity)) return true;
            Thread.Sleep(50);
        }
        return false;
    }

    private string TempRecordRoot()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-kill-owners-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        _folders.Add(dir);
        return dir;
    }

    // A transcript under the REAL ~/.claude/projects, in a unique per-test folder deleted in cleanup - the
    // transcript matcher keys off that exact path shape, so a wholly synthetic root would not be recognised.
    private string NewTranscript(out string sessionId)
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects",
            "clr-kill-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        _folders.Add(folder);

        sessionId = Guid.NewGuid().ToString();
        var path = Path.Combine(folder, sessionId + ".jsonl");
        File.WriteAllText(path, "{\"type\":\"user\"}\n");
        return path;
    }

    private Process StartSleeper()
    {
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile",
                "-EncodedCommand",
                Encode("Start-Sleep -Seconds 120"),
            },
        }) ?? throw new InvalidOperationException("could not start the probe child");
        _children.Add(child);
        return child;
    }

    private (string SessionId, Process Child) StartChildHoldingTranscript()
    {
        var path = NewTranscript(out var sid);
        var child = StartReadyChild($"""
            $fs = [System.IO.File]::Open('{path}', 'Open', 'Read', 'ReadWrite')
            [Console]::Out.WriteLine('ready')
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """);
        return (sid, child);
    }

    // `cmd.exe /c powershell ...` - the same shape the app launches (a shell wrapper above the real agent),
    // so the tree kill has an actual tree to walk.
    private (string SessionId, Process Wrapper, int ChildPid) StartWrapperTreeHoldingTranscript()
    {
        var path = NewTranscript(out var sid);
        var script = $"""
            $fs = [System.IO.File]::Open('{path}', 'Open', 'Read', 'ReadWrite')
            [Console]::Out.WriteLine("CLRPID=$PID")
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        var wrapper = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/c", "powershell.exe", "-NoProfile", "-EncodedCommand", Encode(script) },
        }) ?? throw new InvalidOperationException("could not start the wrapper");
        _children.Add(wrapper);
        return (sid, wrapper, ReadChildPid(wrapper));
    }

    // Assignment must happen while the wrapper has no children yet, so the wrapper is started as an idle
    // `cmd /K` reading stdin, assigned, and only THEN told to spawn the grandchild.
    private (Process Wrapper, int Grandchild) StartWrapperInJobWithGrandchild(out string? jobName)
    {
        var wrapper = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "/Q", "/K" },
        }) ?? throw new InvalidOperationException("could not start the job wrapper");
        _children.Add(wrapper);

        jobName = OwnerJobObjects.TryAssign(wrapper);

        var script = """
            [Console]::Out.WriteLine("CLRPID=$PID")
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        wrapper.StandardInput.WriteLine($"powershell.exe -NoProfile -EncodedCommand {Encode(script)}");
        wrapper.StandardInput.Flush();
        return (wrapper, ReadChildPid(wrapper));
    }

    // cmd emits its own noise around the child's output - and in /K mode its PROMPT has no trailing newline,
    // so it lands glued to the front of the child's first line. Hence a marker rather than a bare number.
    private static int ReadChildPid(Process wrapper)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var read = wrapper.StandardOutput.ReadLineAsync();
            if (!read.Wait(TimeSpan.FromSeconds(30))) break;
            var line = read.Result;
            if (line is null) break;
            var marker = line.IndexOf("CLRPID=", StringComparison.Ordinal);
            if (marker < 0) continue;
            var digits = new string(line[(marker + 7)..].TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var pid) && pid > 0) return pid;
        }
        throw new TimeoutException("the child never reported its pid");
    }

    private Process StartReadyChild(string script)
    {
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", Encode(script) },
        }) ?? throw new InvalidOperationException("could not start the probe child");
        _children.Add(child);

        var ready = child.StandardOutput.ReadLineAsync();
        if (!ready.Wait(TimeSpan.FromSeconds(20)))
            throw new TimeoutException("the child never signalled readiness");
        return child;
    }

    private static string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
}
