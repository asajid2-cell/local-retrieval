using System.Diagnostics;
using System.Text;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

// The per-pid transcript-owner enum and its DEAD-vs-DENIED safety contract. The old world scan conflated the
// two: any candidate pid it couldn't open refused the WHOLE scan, so ordinary swarm churn (a pid that exited
// between the process scan and the handle scan) vetoed every launch. These tests pin the split — a dead pid
// drops out silently, an alive-but-uninspectable pid is reported as unverifiable and never as "not an owner".
//
// Tests 1, 2 and 4 use REAL child processes holding REAL handles; only the access-denied case (which needs an
// elevated process we can't create in-test) is simulated, and even there the ALIVENESS probe runs for real.
[TestClass]
public class ProcessOpenFilesTests
{
    private readonly List<Process> _children = new();
    private readonly List<string> _projectFolders = new();

    [TestCleanup]
    public void Cleanup()
    {
        ProcessOpenFiles.DupQueryOpenOverride = null;
        RunningSessions.ResetPerPidTranscriptCache();
        foreach (var p in _children)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { p.Dispose(); } catch { }
        }
        foreach (var folder in _projectFolders)
            try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { }
    }

    [TestMethod]
    public void PerPidEnum_FindsTheTranscriptAChildProcessHoldsOpen()
    {
        RequireWindows();
        var (sid, child) = StartChildHoldingTranscript();

        var result = ProcessOpenFiles.Scan(new[] { child.Id });

        Assert.IsTrue(result.AllVerifiable, "a child we own must be inspectable: " + result.UnverifiableDetail());
        Assert.IsTrue(result.Found.TryGetValue(sid, out var owner), $"session {sid} was not attributed to any pid");
        Assert.AreEqual(child.Id, owner, "the transcript must be attributed to the process actually holding it");
    }

    // C2 isolation: churn kills a candidate mid-set. The exited pid classifies DEAD and the OTHER candidate
    // still resolves — the whole point of the rewrite. Under the old scan this refused everything.
    [TestMethod]
    public void ExitedCandidate_IsDeadAndDoesNotVetoTheRestOfTheSet()
    {
        RequireWindows();
        var (survivorSid, survivor) = StartChildHoldingTranscript();
        var (doomedSid, doomed) = StartChildHoldingTranscript();

        doomed.Kill(entireProcessTree: true);
        Assert.IsTrue(doomed.WaitForExit(10_000), "the doomed child did not exit");

        var result = ProcessOpenFiles.Scan(new[] { survivor.Id, doomed.Id });

        Assert.IsTrue(result.DeadPids.Contains(doomed.Id), "an exited candidate must classify dead");
        Assert.IsFalse(result.UnverifiablePids.Contains(doomed.Id), "an exited candidate is not uncertainty");
        Assert.IsTrue(result.AllVerifiable, "one dead pid must not make the set unverifiable");
        Assert.IsTrue(result.Found.TryGetValue(survivorSid, out var owner), "the surviving owner was lost with the dead one");
        Assert.AreEqual(survivor.Id, owner);
        Assert.IsFalse(result.Found.ContainsKey(doomedSid), "a dead process cannot still own a transcript");
    }

    // [F#6] "Alive" is exit-code defined, never open-success defined. OpenProcess SUCCEEDS on a zombie (an
    // exited process whose handle someone still holds — e.g. the cmd wrapper mid-wait); treating that as alive
    // would resurrect dead owners and block launches forever.
    [TestMethod]
    public void ZombiePid_WithHandleStillHeld_ClassifiesDeadByExitCode()
    {
        RequireWindows();
        // The Process object keeps the kernel handle open after exit, so the pid stays openable but exited.
        var zombie = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "exit", "0" },
        }) ?? throw new InvalidOperationException("could not start the zombie probe");
        _children.Add(zombie);
        Assert.IsTrue(zombie.WaitForExit(10_000), "the zombie probe did not exit");

        if (!ProcessOpenFiles.CanOpenForDupQuery(zombie.Id))
            Assert.Inconclusive("the pid was invalidated before the zombie precondition could be established");

        Assert.IsFalse(ProcessOpenFiles.IsAlive(zombie.Id), "an exited process is not alive, however openable it is");

        var inspection = ProcessOpenFiles.Inspect(zombie.Id);
        Assert.AreEqual(ProcessOpenFiles.PidOutcome.Dead, inspection.Outcome,
            "a successful open on an exited process must classify DEAD, not alive");
    }

    // C2 policy: alive + uninspectable (the elevated-agent case) is UNVERIFIABLE — reported honestly — and the
    // bool wrapper Start-class callers use fails CLOSED with wording distinct from "a live owner was found".
    [TestMethod]
    public void AccessDeniedOnALiveProcess_IsUnverifiable_AndTheBoolWrapperFailsClosed()
    {
        RequireWindows();
        var (_, child) = StartChildHoldingTranscript();
        var denied = child.Id;
        // Only the DUP|QUERY open is forced to deny; the aliveness probe still measures the REAL live child.
        ProcessOpenFiles.DupQueryOpenOverride = pid =>
            pid == denied ? (IntPtr.Zero, ERROR_ACCESS_DENIED) : null;
        RunningSessions.ResetPerPidTranscriptCache();

        var result = ProcessOpenFiles.Scan(new[] { denied });

        Assert.IsFalse(result.AllVerifiable);
        Assert.IsTrue(result.UnverifiablePids.Contains(denied), "an alive process we cannot open is uncertainty, not absence");
        Assert.IsFalse(result.DeadPids.Contains(denied));

        var ok = RunningSessions.TryOpenTranscriptSessionIds(
            new[] { denied }, out var ids, out var unverifiable, out var detail);

        Assert.IsFalse(ok, "Start-class callers must fail closed on an unverifiable pid");
        Assert.IsTrue(unverifiable.Contains(denied), "the wrapper must propagate WHICH pid was unverifiable");
        Assert.IsEmpty(ids);
        StringAssert.Contains(detail, "unverified", "the refusal must read as unverified, not as a confirmed live owner");
        Assert.IsFalse(detail.Contains("live owner found", StringComparison.OrdinalIgnoreCase));
    }

    // [F#8] A failed/unverifiable per-pid result is never cached — otherwise one transient hiccup fail-closes
    // every caller for a whole TTL.
    [TestMethod]
    public void UnverifiableResults_AreNotCached()
    {
        RequireWindows();
        var (sid, child) = StartChildHoldingTranscript();
        var pid = child.Id;

        ProcessOpenFiles.DupQueryOpenOverride = p => p == pid ? (IntPtr.Zero, ERROR_ACCESS_DENIED) : null;
        RunningSessions.ResetPerPidTranscriptCache();
        Assert.IsFalse(RunningSessions.TryOpenTranscriptSessionIds(new[] { pid }, out _, out _, out _));

        // Same pid, same TTL window — the denial must NOT have been cached, so recovery is immediate.
        ProcessOpenFiles.DupQueryOpenOverride = null;
        var ok = RunningSessions.TryOpenTranscriptSessionIds(new[] { pid }, out var ids, out _, out var detail);

        Assert.IsTrue(ok, "a cached failure would keep failing closed after the cause cleared: " + detail);
        Assert.IsTrue(ids.ContainsKey(sid), "the recovered scan must find the transcript");
    }

    // Concurrent callers with DIFFERENT pid sets used to refuse each other ("busy verifying another process
    // set"). Per-pid entries compose, so both callers now get a real answer.
    [TestMethod]
    public void ConcurrentCallersWithDisjointPidSets_DoNotRefuseEachOther()
    {
        RequireWindows();
        var (sidA, childA) = StartChildHoldingTranscript();
        var (sidB, childB) = StartChildHoldingTranscript();
        RunningSessions.ResetPerPidTranscriptCache();

        var a = Task.Run(() => RunningSessions.TryOpenTranscriptSessionIds(
            new[] { childA.Id }, out var ids, out _, out var d) ? (true, ids, d) : (false, ids, d));
        var b = Task.Run(() => RunningSessions.TryOpenTranscriptSessionIds(
            new[] { childB.Id }, out var ids, out _, out var d) ? (true, ids, d) : (false, ids, d));
        Task.WaitAll(new Task[] { a, b }, 30_000);

        Assert.IsTrue(a.Result.Item1, "caller A was refused: " + a.Result.d);
        Assert.IsTrue(b.Result.Item1, "caller B was refused: " + b.Result.d);
        Assert.IsTrue(a.Result.ids.ContainsKey(sidA));
        Assert.IsTrue(b.Result.ids.ContainsKey(sidB));
    }

    // ---- M6: tolerant-with-retry claude registry ----------------------------------------------------

    // Claude rewrites ~/.claude/sessions/<pid>.json live, so an unreadable moment is usually a transient write
    // race. Retrying rides it out instead of failing the entire liveness check on one file.
    [TestMethod]
    public void RegistryFileLockedThenReleased_IsReadOnRetry()
    {
        RequireWindows();
        var dir = TempRegistryDir();
        var sid = Guid.NewGuid().ToString();
        var file = Path.Combine(dir, "4242.json");
        File.WriteAllText(file, $"{{\"pid\":4242,\"sessionId\":\"{sid}\"}}");

        // Lock BEFORE the read starts (so attempt 1 is guaranteed to fail), release at 200ms — i.e. after
        // attempt 2 (150ms) and before attempt 3 (300ms).
        var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var releaser = Task.Run(() => { Thread.Sleep(200); exclusive.Dispose(); });

        var ok = RunningSessions.TryReadClaudeRegistryFiles(
            new[] { file }, null, out var map, out var unverifiable, out var detail);
        releaser.Wait(10_000);

        Assert.IsTrue(ok, "a transient lock must be ridden out by the retry, not fail the whole check: " + detail);
        Assert.IsEmpty(unverifiable);
        Assert.IsTrue(map.TryGetValue(sid, out var pid), "the retried read must still yield the session");
        Assert.AreEqual(4242, pid);
    }

    // Persistently unreadable + the file's pid is ALIVE: it may be hiding an idle claude that is visible
    // NOWHERE else, so this is uncertainty and Start-class fails closed.
    [TestMethod]
    public void RegistryFilePersistentlyLocked_WithLivePid_IsUnverifiable()
    {
        RequireWindows();
        var dir = TempRegistryDir();
        var live = StartSleeper();
        var file = Path.Combine(dir, live.Id + ".json");
        File.WriteAllText(file, $"{{\"pid\":{live.Id},\"sessionId\":\"{Guid.NewGuid()}\"}}");

        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var ok = RunningSessions.TryReadClaudeRegistryFiles(
            new[] { file }, null, out _, out var unverifiable, out var detail);

        Assert.IsFalse(ok, "an unreadable registry file for a LIVE pid must fail closed");
        Assert.IsTrue(unverifiable.Contains(live.Id), "the live pid must be named as the unverifiable one");
        StringAssert.Contains(detail, "unverified");
    }

    // The same file for a DEAD pid is a stale leftover: skipped, not a blocker. One unreadable file no longer
    // fails the whole check unconditionally.
    [TestMethod]
    public void RegistryFilePersistentlyLocked_WithDeadPid_IsSkipped()
    {
        RequireWindows();
        var dir = TempRegistryDir();
        var dead = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList = { "/c", "exit", "0" },
        }) ?? throw new InvalidOperationException("could not start the stale-registry probe");
        _children.Add(dead);
        Assert.IsTrue(dead.WaitForExit(10_000));

        var file = Path.Combine(dir, dead.Id + ".json");
        File.WriteAllText(file, $"{{\"pid\":{dead.Id},\"sessionId\":\"{Guid.NewGuid()}\"}}");

        using var exclusive = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
        var ok = RunningSessions.TryReadClaudeRegistryFiles(
            new[] { file }, null, out _, out var unverifiable, out var detail);

        Assert.IsTrue(ok, "a stale registry file for a dead pid must not block anything: " + detail);
        Assert.IsEmpty(unverifiable);
    }

    // ---- harness ------------------------------------------------------------------------------------

    private const int ERROR_ACCESS_DENIED = 5;

    private static void RequireWindows()
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("per-pid handle enumeration is Windows-only.");
    }

    // A transcript under the REAL ~/.claude/projects (in a unique per-test folder, deleted in cleanup) — the
    // same fake-root approach the archive tests use, because the transcript matcher keys off that path shape.
    private (string SessionId, Process Child) StartChildHoldingTranscript()
    {
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude",
            "projects",
            "clr-pof-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(folder);
        _projectFolders.Add(folder);

        var sid = Guid.NewGuid().ToString();
        var path = Path.Combine(folder, sid + ".jsonl");
        File.WriteAllText(path, "{\"type\":\"user\"}\n");

        // Hold the transcript open exactly as a live agent does, then park.
        var script = $"""
            $fs = [System.IO.File]::Open('{path}', 'Open', 'Read', 'ReadWrite')
            [Console]::Out.WriteLine('ready')
            [Console]::Out.Flush()
            Start-Sleep -Seconds 120
            """;
        var child = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) },
        }) ?? throw new InvalidOperationException("could not start the transcript-holding child");
        _children.Add(child);

        var ready = child.StandardOutput.ReadLineAsync().Wait(20_000)
            ? "ready"
            : throw new TimeoutException("the child never reported holding the transcript open");
        Assert.AreEqual("ready", ready);
        return (sid, child);
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
                Convert.ToBase64String(Encoding.Unicode.GetBytes("Start-Sleep -Seconds 120")),
            },
        }) ?? throw new InvalidOperationException("could not start the liveness probe child");
        _children.Add(child);
        return child;
    }

    private string TempRegistryDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-registry-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        _projectFolders.Add(dir);
        return dir;
    }
}
