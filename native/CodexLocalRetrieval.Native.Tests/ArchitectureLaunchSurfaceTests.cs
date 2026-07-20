namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchitectureLaunchSurfaceTests
{
    [TestMethod]
    public void ProductionCode_AcquiresLaunchClaimsOnlyThroughGovernor()
    {
        var root = FindRepoRoot();
        var allowed = Path.GetFullPath(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Remote",
            "SessionLaunchGovernor.cs"));
        var productionRoots = new[]
        {
            Path.Combine(root, "native", "CodexLocalRetrieval.Core"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Server"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Native"),
        };
        var banned = new[]
        {
            "SessionLaunchClaims.TryAcquire(",
            "SessionLaunchClaims.TryAcquireForResumeCommand(",
        };

        var violations = new List<string>();
        foreach (var dir in productionRoots)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;

                var full = Path.GetFullPath(file);
                if (string.Equals(full, allowed, StringComparison.OrdinalIgnoreCase))
                    continue;

                var text = File.ReadAllText(full);
                foreach (var marker in banned)
                {
                    if (text.Contains(marker, StringComparison.Ordinal))
                        violations.Add(Path.GetRelativePath(root, full) + " uses " + marker);
                }
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "Production writer launch reservations must go through SessionLaunchGovernor, not SessionLaunchClaims directly:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void ProductionAgentLaunchesHaveOneContainedBoundedProcessSurface()
    {
        var root = FindRepoRoot();
        Assert.IsFalse(File.Exists(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Agents",
            "CodexAgentSession.cs")));
        Assert.IsFalse(File.Exists(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Agents",
            "IAgentSession.cs")));

        var productionRoots = new[]
        {
            Path.Combine(root, "native", "CodexLocalRetrieval.Core"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Server"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Native"),
        };
        var violations = new List<string>();
        foreach (var dir in productionRoots)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = File.ReadAllText(file);
                if (text.Contains(".StandardOutput.ReadToEndAsync(", StringComparison.Ordinal)
                    || text.Contains(".StandardError.ReadToEndAsync(", StringComparison.Ordinal)
                    || text.Contains(".StandardOutput.ReadToEnd(", StringComparison.Ordinal)
                    || text.Contains(".StandardError.ReadToEnd(", StringComparison.Ordinal))
                {
                    violations.Add(Path.GetRelativePath(root, file) + " buffers child output without a capture bound");
                }
            }
        }

        Assert.IsEmpty(
            violations,
            "App-owned child output must use BoundedTextCapture/ContainedProcessRunner:" +
            Environment.NewLine + string.Join(Environment.NewLine, violations));

        var containment = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Agents",
            "WindowsProcessJob.cs"));
        var interfaceStart = containment.IndexOf("public interface IProcessContainment", StringComparison.Ordinal);
        var interfaceEnd = containment.IndexOf("// Owns server-controlled agent processes", interfaceStart, StringComparison.Ordinal);
        var contract = containment[interfaceStart..interfaceEnd];
        Assert.IsFalse(
            contract.Contains("AttachOrTerminate", StringComparison.Ordinal),
            "Production containment must be established before child code runs.");
        StringAssert.Contains(
            containment,
            "private static readonly object ProcessCreationGate",
            "Pipe creation through CreateProcessW must be serialized across job instances.");
        StringAssert.Contains(
            containment,
            "lock (ProcessCreationGate)",
            "The process-wide launch gate must cover the inheritable-handle window.");

        var runner = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Agents",
            "ContainedProcessRunner.cs"));
        Assert.DoesNotContain(
            ".StandardInput.Close(",
            runner,
            "Synchronous StreamWriter.Close can flush outside the operation timeout.");
        StringAssert.Contains(runner, "CloseStandardInputPipe()");

        var program = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Server",
            "Program.cs"));
        var shutdownStart = program.IndexOf("await agentHub.DisposeAsync()", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, shutdownStart);
        var shutdown = program[shutdownStart..];
        StringAssert.Contains(shutdown, "catch (Exception ex)");
        StringAssert.Contains(shutdown, "Console.Error.WriteLine");
    }

    [TestMethod]
    public void ArchiveTranscriptReadsAndSearchesAreStreamingAndBounded()
    {
        var root = FindRepoRoot();
        var archive = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Services",
            "ArchiveService.cs"));

        Assert.IsFalse(
            archive.Contains("while (await reader.ReadLineAsync()", StringComparison.Ordinal)
            || archive.Contains("while ((line = reader.ReadLine())", StringComparison.Ordinal),
            "Transcript readers must reject oversized records before allocating an unbounded line.");

        var phraseSearchStart = archive.IndexOf(
            "public async Task<IReadOnlyList<ArchiveSession>> SearchDiskPhraseAsync",
            StringComparison.Ordinal);
        var phraseSearchEnd = archive.IndexOf(
            "private static readonly HashSet<string> SearchStopwords",
            phraseSearchStart,
            StringComparison.Ordinal);
        var phraseSearch = archive[phraseSearchStart..phraseSearchEnd];
        Assert.DoesNotContain(
            "SafeReadAllText",
            phraseSearch,
            "Phrase search must stream transcripts instead of materializing whole files.");

        var deepSearchStart = archive.IndexOf(
            "public async Task<IReadOnlyList<ArchiveSearchHit>> DeepSearchContentAsync",
            StringComparison.Ordinal);
        var deepSearchEnd = archive.IndexOf(
            "private static List<string> DistinctiveTokens",
            deepSearchStart,
            StringComparison.Ordinal);
        var deepSearch = archive[deepSearchStart..deepSearchEnd];
        Assert.DoesNotContain(
            "SafeReadAllText",
            deepSearch,
            "Deep search must stream transcripts instead of materializing whole files.");
    }

    [TestMethod]
    public void RemoteProjection_DoesNotExposeExecutableMuxCommands()
    {
        var root = FindRepoRoot();
        var archiveService = Path.Combine(root, "native", "CodexLocalRetrieval.Core", "Services", "ArchiveService.cs");
        var text = File.ReadAllText(archiveService);
        var start = text.IndexOf("object? ChatProjection(ArchiveSession s)", StringComparison.Ordinal);
        var end = text.IndexOf("var collections = Store.Collections.Values", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start, "Could not locate the remote chat projection body.");
        var chatProjection = text[start..end];

        Assert.IsFalse(
            chatProjection.Contains("muxCommand,", StringComparison.Ordinal)
            || chatProjection.Contains("muxCommand =", StringComparison.Ordinal),
            "Remote chat projection must carry launch intent only; executable mux commands stay local.");
    }

    [TestMethod]
    public void RemoteBridge_DelegatesMuxLaunchReservationToMuxd()
    {
        var root = FindRepoRoot();
        var bridge = Path.Combine(root, "native", "CodexLocalRetrieval.Core", "Remote", "RemoteBridge.cs");
        var text = File.ReadAllText(bridge);

        Assert.IsFalse(
            text.Contains("SessionLaunchGovernor", StringComparison.Ordinal)
            || text.Contains("SessionLaunchClaims", StringComparison.Ordinal),
            "Muxd is the sole reservation authority for mux-hosted writers; a bridge-side claim deadlocks against muxd.");
    }

    [TestMethod]
    public void UserConfirmedMuxTakeover_ConsolidatesIdentityAndUsesFencedRelaunch()
    {
        var root = FindRepoRoot();
        var gui = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"));
        var headless = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Core", "Remote", "RemoteBridge.cs"));
        var transfer = File.ReadAllText(Path.Combine(root, "native", "CodexLocalRetrieval.Core", "Remote", "MuxIdentityTransfer.cs"));

        foreach (var (consumer, text) in new[] { ("GUI", gui), ("headless", headless) })
        {
            Assert.Contains("MuxIdentityTransfer.ExecuteAsync", text,
                consumer + " relaunch must use the shared canonical-identity transfer boundary.");
            Assert.Contains("relaunch", text,
                consumer + " takeover must reach muxd as a relaunch under the same durable command intent.");
            Assert.Contains("takeover", text,
                consumer + " command consumer must preserve the explicit ownership-transfer flag.");
        }
        Assert.Contains("TryMuxOwnedAgentPids", transfer,
            "Takeover must distinguish the requested mux owner's process from stray writers during replay.");
        Assert.Contains("SelectMany", transfer,
            "Takeover must stop every verified PID across the canonical identity and aliases.");
        Assert.Contains("RunningSessions.TryLiveSessionPids", transfer,
            "Shared transfer must fail closed and retain exact PID custody when local state is uncertain.");
    }

    [TestMethod]
    public void Tomux_DoesNotKillCallerBeforeSharedMuxCustodyClassification()
    {
        var root = FindRepoRoot();
        var gui = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Native",
            "MainPage.Remote.cs"));
        var start = gui.IndexOf("HandleToMuxAsync", StringComparison.Ordinal);
        var end = gui.IndexOf("AddSessionToCollectionAsync", start, StringComparison.Ordinal);
        var handler = gui.Substring(start, end - start);

        Assert.DoesNotContain("KillRunningSession(null, c.pid)", handler);
        Assert.Contains("MuxIdentityTransfer.ExecuteAsync", handler);
        Assert.Contains("Already running in multiplex", handler);
    }

    [TestMethod]
    public void LiveOwnerScan_DoesNotPermanentlyPoisonLaunchesAfterOneTimeout()
    {
        var root = FindRepoRoot();
        var running = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Remote",
            "RunningSessions.cs"));

        Assert.DoesNotContain("_openHandleScanDisabled", running);

        // The single-flight gate is GONE. It was the poison: one global scan, one pid-subset cache, and a 5s
        // give-up meant a single slow scan made concurrent callers with disjoint pid sets refuse each other,
        // and the abandoned task kept burning while holding the gate.
        Assert.DoesNotContain("_openHandleScan", running);
        Assert.DoesNotContain("OpenHandleScanTimeout", running);
        Assert.DoesNotContain("pids.IsSubsetOf(_openHandleCachePids)", running);
        Assert.DoesNotContain("pids.IsSubsetOf(_openHandleScanPids)", running);
        Assert.DoesNotContain("is still running; refusing to risk a second writer", running);
        Assert.DoesNotContain("busy verifying another process set", running);

        // What replaced it: per-(pid, start-time) entries that COMPOSE, so different pid sets never contend.
        Assert.Contains("_perPidTranscripts", running);
        Assert.Contains("PerPidTranscriptCacheLifetime", running);
        Assert.Contains("PerPidHandleTimeout", running);
        Assert.Contains("ProcessOpenFiles.TryGetAliveIdentity", running);
        // [F#8] only positive resolutions are cached — a failure must not fail-close every caller for a TTL.
        Assert.Contains("CachePositiveTranscripts", running);
        // The world scan survives one release behind a flag, and nothing else.
        Assert.Contains("CODEXLOCAL_LEGACY_HANDLE_SCAN", running);
    }

    [TestMethod]
    public void OpenHandleScan_DoesNotCopyTheGlobalHandleTableIntoManagedMemory()
    {
        var root = FindRepoRoot();
        var handles = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Core",
            "Remote",
            "OpenHandles.cs"));

        Assert.Contains("QueryAllHandles(e =>", handles);
        Assert.DoesNotContain("new List<SYSTEM_HANDLE_ENTRY>", handles);
        Assert.Contains("GetFileType(dup) != FILE_TYPE_DISK", handles);
        Assert.Contains("MaxHandleTableBytes", handles);

        var runningChats = File.ReadAllText(Path.Combine(
            root,
            "native",
            "CodexLocalRetrieval.Native",
            "MainPage.RunningChats.cs"));
        Assert.Contains("RunningSessions.TryOpenTranscriptSessionIds", runningChats);
        Assert.DoesNotContain("OpenHandles.OpenTranscriptSessionIds", runningChats);
    }

    [TestMethod]
    public void RemoteCommandConsumers_UseFencedLeasesAndPropagateIntentIds()
    {
        var root = FindRepoRoot();
        var files = new[]
        {
            Path.Combine(root, "native", "CodexLocalRetrieval.Core", "Remote", "RemoteBridge.cs"),
            Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"),
        };

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            Assert.IsTrue(
                text.Contains("/api/app-commands/lease", StringComparison.Ordinal),
                Path.GetFileName(file) + " must claim commands through the durable lease endpoint.");
            Assert.IsTrue(
                text.Contains("limit = 1", StringComparison.Ordinal),
                Path.GetFileName(file) + " executes commands serially and must lease only one command at a time.");
            Assert.IsFalse(
                text.Contains("limit = 8", StringComparison.Ordinal),
                Path.GetFileName(file) + " must not reserve a batch that can expire before serial execution reaches it.");
            Assert.IsTrue(
                text.Contains("leaseToken = c.leaseToken", StringComparison.Ordinal),
                Path.GetFileName(file) + " must fence acknowledgements with the lease token.");
            Assert.IsTrue(
                text.Contains("c.intentId", StringComparison.Ordinal),
                Path.GetFileName(file) + " must propagate the relay intent into muxd.");
            Assert.IsTrue(
                text.Contains("RemoteCommandProtocol.IsReplaySafe", StringComparison.Ordinal),
                Path.GetFileName(file) + " must refuse command types without an explicit replay policy.");
            Assert.IsTrue(
                text.Contains("AckCommandAsync", StringComparison.Ordinal),
                Path.GetFileName(file) + " must retry acknowledgement with the same fenced lease token.");
            Assert.IsFalse(
                text.Contains($"curl -s http://127.0.0.1:{{", StringComparison.Ordinal)
                && text.Contains("/api/app-commands\"", StringComparison.Ordinal),
                Path.GetFileName(file) + " must not use the legacy unleased command pull.");
        }
    }

    [TestMethod]
    public void CopilotConfirmation_DoesNotPreviewResumeCommands()
    {
        var root = FindRepoRoot();
        var copilot = Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Copilot.cs");
        var text = File.ReadAllText(copilot);

        Assert.IsFalse(
            text.Contains("BuildResumeLaunch(session)", StringComparison.Ordinal)
            || text.Contains("DisplayCommand", StringComparison.Ordinal),
            "Co-pilot resume confirmation must not expose raw launch commands before integrity-gated user action.");
    }

    // The guard's refusal reaches the ledger through these two call sites. An unverifiable scan must land
    // on `*.refused.unverified`; if it ever falls back into the `*.refused.running` branch again, the app is
    // writing "the session already had a live owner" about a scan that confirmed nothing.
    [TestMethod]
    public void LaunchGuardRefusals_RecordUnverifiedScansSeparatelyFromConfirmedLiveOwners()
    {
        var root = FindRepoRoot();
        var sites = new[]
        {
            ("resume", Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Sessions.cs"), "resume"),
            ("mux", Path.Combine(root, "native", "CodexLocalRetrieval.Native", "MainPage.Remote.cs"), "mux"),
        };

        foreach (var (label, file, kindPrefix) in sites)
        {
            var text = File.ReadAllText(file);

            Assert.Contains("RunGuardOutcome.Unverifiable", text,
                label + " must branch on the guard's outcome, not on a bare bool that hides why it refused.");
            Assert.Contains("\"" + kindPrefix + ".refused.unverified\"", text,
                label + " must record an unverified scan under its own event kind.");
            Assert.Contains("\"" + kindPrefix + ".refused.running\"", text,
                label + " must still record a CONFIRMED live owner as such.");

            // The two branches must be disjoint: whatever block records `refused.running` must not be the
            // block reached by Unverifiable. Slice the Unverifiable branch and assert the kind is absent.
            var start = text.IndexOf("RunGuardOutcome.Unverifiable", StringComparison.Ordinal);
            var end = text.IndexOf("\"" + kindPrefix + ".refused.running\"", start, StringComparison.Ordinal);
            Assert.IsTrue(end > start, label + " must handle Unverifiable BEFORE the live-owner branch.");
            var unverifiableBranch = text[start..end];

            Assert.DoesNotContain("refused.running", unverifiableBranch);
            Assert.DoesNotContain("already had a live owner", unverifiableBranch);
            Assert.DoesNotContain("Cancelled - already running.", unverifiableBranch);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "CodexLocalRetrieval.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not find CodexLocalRetrieval.sln from " + AppContext.BaseDirectory);
    }
}
