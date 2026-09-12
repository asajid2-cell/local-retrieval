using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Memory;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;
using CodexLocalRetrieval.Server;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ContainedProcessLifecycleTests
{
    [TestMethod]
    public async Task CreationTimeContainment_RetainsExitCodeAfterFastChildExits()
    {
        using var job = WindowsProcessJob.CreateKillOnClose();
        using var process = job.StartContained(new ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Arguments = "/d /c exit 37",
        });
        // Pipe EOF observes child termination without opening Process's cached OS handle.
        Assert.AreEqual("", await process.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.AreEqual("", await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(37, process.ExitCode);
    }

    [TestMethod]
    public async Task ClaudeContainmentFailure_KillsProcessAndReleasesWriterLease()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        var pids = new List<int>();
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-claude-containment-tests", Guid.NewGuid().ToString("N"));
        using var job = WindowsProcessJob.CreateKillOnClose();
        job.Dispose();
        var containment = new AttachAfterStartContainment(job, _ => StartSleeper(pids));
        var driver = new ClaudeLiveDriver(
            "ignored.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(RootDirectory: claimRoot),
            processContainment: containment);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = Assert.ThrowsExactly<ProcessContainmentException>(() => driver.StartTurn(
                "claude-containment-session",
                Path.GetTempPath(),
                "hello",
                _ => Task.CompletedTask,
                CancellationToken.None));
            Assert.IsTrue(error.TerminationConfirmed);
        }

        Assert.HasCount(2, pids, "A leaked writer lease would refuse the second launch before process start.");
        foreach (var pid in pids)
            await AssertExitedAsync(pid);
    }

    [TestMethod]
    public async Task CodexContainmentFailure_KillsProcessAndAllowsRetry()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows Job Objects are Windows-only.");
            return;
        }

        var pids = new List<int>();
        using var job = WindowsProcessJob.CreateKillOnClose();
        job.Dispose();
        var containment = new AttachAfterStartContainment(job, _ => StartSleeper(pids));
        await using var hub = new CodexAgentHub(
            "ignored.exe",
            processContainment: containment);

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var error = await Assert.ThrowsExactlyAsync<ProcessContainmentException>(
                () => hub.ListSessionsAsync(null, 1, CancellationToken.None));
            Assert.IsTrue(error.TerminationConfirmed);
        }

        Assert.HasCount(2, pids, "A failed app-server start must leave the hub retryable.");
        foreach (var pid in pids)
            await AssertExitedAsync(pid);
    }

    [TestMethod]
    public void ClaudeUnconfirmedContainmentFailure_RetainsWriterClaim()
    {
        var pids = new List<int>();
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-claude-unconfirmed-tests", Guid.NewGuid().ToString("N"));
        var containment = new UnconfirmedContainment(_ => StartSleeper(pids));
        var driver = new ClaudeLiveDriver(
            "ignored.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(
                RootDirectory: claimRoot,
                StaleAfter: TimeSpan.FromMinutes(5)),
            processContainment: containment);

        try
        {
            var first = Assert.ThrowsExactly<ProcessContainmentException>(() => driver.StartTurn(
                "claude-unconfirmed-session",
                Path.GetTempPath(),
                "hello",
                _ => Task.CompletedTask,
                CancellationToken.None));
            Assert.IsFalse(first.TerminationConfirmed);

            var second = Assert.ThrowsExactly<InvalidOperationException>(() => driver.StartTurn(
                "claude-unconfirmed-session",
                Path.GetTempPath(),
                "hello again",
                _ => Task.CompletedTask,
                CancellationToken.None));

            StringAssert.Contains(second.Message, "launch already pending");
            Assert.HasCount(1, pids, "The retained claim must block a second process start.");
        }
        finally
        {
            foreach (var pid in pids) TryKill(pid);
            try { Directory.Delete(claimRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task CodexInitializationFailure_DisposesStartedProcess()
    {
        var pid = 0;
        await using var hub = new CodexAgentHub(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                $null = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"error":{"message":"injected initialization failure"}}')
                [Console]::Out.Flush()
                Start-Sleep -Seconds 120
                """,
                started => pid = started.Id));

        await Assert.ThrowsExactlyAsync<CodexAppServerResponseException>(
            () => hub.ListSessionsAsync(null, 1, CancellationToken.None));

        Assert.IsGreaterThan(0, pid);
        await AssertExitedAsync(pid);
    }

    [TestMethod]
    public async Task CodexAppServer_DrainsStderrWithoutBlockingProtocol()
    {
        await using var server = CodexAppServer.Start(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                $null = [Console]::In.ReadLine()
                [Console]::Error.Write(('x' * 262144))
                [Console]::Error.Flush()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}')
                [Console]::Out.Flush()
                Start-Sleep -Seconds 120
                """));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var result = await server.InitializeAsync(timeout.Token);

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
    }

    [TestMethod]
    public async Task CodexAppServer_CancelledRequestIsRemovedFromPendingMap()
    {
        await using var server = CodexAppServer.Start(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                $null = [Console]::In.ReadLine()
                Start-Sleep -Seconds 120
                """));
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            () => server.InitializeAsync(cancellation.Token));

        var pendingField = typeof(CodexAppServer).GetField(
            "_pending",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(CodexAppServer).FullName, "_pending");
        var pending = (ICollection)(pendingField.GetValue(server)
            ?? throw new InvalidOperationException("pending request map was null"));
        Assert.IsEmpty(pending);
    }

    [TestMethod]
    public async Task CodexAppServer_CancelledLargeWriteDoesNotPoisonNextFrame()
    {
        await using var server = CodexAppServer.Start(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                $null = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}')
                [Console]::Out.Flush()

                $length = 0
                while (($ch = [Console]::In.Read()) -ge 0) {
                    if ($ch -eq 10) { break }
                    $length++
                    if (($length % 2048) -eq 0) { Start-Sleep -Milliseconds 5 }
                }
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":2,"result":{"accepted":true}}')
                [Console]::Out.Flush()

                $null = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":3,"result":{"healthy":true}}')
                [Console]::Out.Flush()
                Start-Sleep -Seconds 120
                """));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await server.InitializeAsync(timeout.Token);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try
        {
            await server.RequestAsync(
                "test/large",
                new { text = new string('x', 512 * 1024) },
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }

        var next = await server.RequestAsync(
            "test/next",
            ct: CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsTrue(next.GetProperty("healthy").GetBoolean());
    }

    [TestMethod]
    public async Task CodexAppServer_DisposeCompletesWhenNotificationChannelIsFull()
    {
        var server = CodexAppServer.Start(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                1..5000 | ForEach-Object {
                    [Console]::Out.WriteLine('{"jsonrpc":"2.0","method":"item/test","params":{"threadId":"blocked"}}')
                }
                [Console]::Out.Flush()
                Start-Sleep -Seconds 120
                """));

        await Task.Delay(500);

        await server.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
    }

    [TestMethod]
    public async Task CodexAgentHub_DisposeCancelsHungInitialization()
    {
        var pid = 0;
        var hub = new CodexAgentHub(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                $null = [Console]::In.ReadLine()
                Start-Sleep -Seconds 120
                """,
                started => pid = started.Id));
        var initialization = hub.ListSessionsAsync(null, 1, CancellationToken.None);

        await WaitUntilAsync(() => pid > 0, TimeSpan.FromSeconds(5));
        await Task.Delay(200);

        Exception? shutdownError = null;
        try
        {
            await hub.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception ex)
        {
            shutdownError = ex;
        }
        finally
        {
            if (pid > 0) TryKill(pid);
            try { await initialization; } catch { }
        }

        Assert.IsNull(shutdownError, shutdownError?.ToString());
        await AssertExitedAsync(pid);
    }

    [TestMethod]
    public async Task CodexAgentHub_TimedOutShutdownDefersCleanupUntilInitializationReleases()
    {
        var enteredStart = new ManualResetEventSlim();
        var releaseStart = new ManualResetEventSlim();
        var pid = 0;
        var hub = new CodexAgentHub(
            "ignored.exe",
            processStarter: _ =>
            {
                enteredStart.Set();
                releaseStart.Wait();
                return StartProtocolProcess(
                    "Start-Sleep -Seconds 120",
                    started => pid = started.Id);
            },
            shutdownTimeout: TimeSpan.FromMilliseconds(150));
        var initialization = Task.Run(
            () => hub.ListSessionsAsync(null, 1, CancellationToken.None));

        Assert.IsTrue(enteredStart.Wait(TimeSpan.FromSeconds(5)));
        var error = await Assert.ThrowsExactlyAsync<TimeoutException>(
            () => hub.DisposeAsync().AsTask());
        StringAssert.Contains(error.Message, "initialization");

        releaseStart.Set();
        try { await initialization; } catch { }

        Assert.IsGreaterThan(0, pid);
        await AssertExitedAsync(pid);

        var lifetimeField = typeof(CodexAgentHub).GetField(
            "_lifetime",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(CodexAgentHub).FullName, "_lifetime");
        var lifetime = (CancellationTokenSource)(lifetimeField.GetValue(hub)
            ?? throw new InvalidOperationException("hub lifetime was null"));
        await WaitUntilAsync(
            () =>
            {
                try
                {
                    _ = lifetime.Token;
                    return false;
                }
                catch (ObjectDisposedException)
                {
                    return true;
                }
            },
            TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task CodexAppServer_RequestAfterTransportCloseDoesNotPublishPendingWork()
    {
        var server = CodexAppServer.Start(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(""));

        await server.Notifications.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            () => server.InitializeAsync(CancellationToken.None));

        var pendingField = typeof(CodexAppServer).GetField(
            "_pending",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(CodexAppServer).FullName, "_pending");
        var pending = (ICollection)(pendingField.GetValue(server)
            ?? throw new InvalidOperationException("pending request map was null"));
        Assert.IsEmpty(pending);
        await server.DisposeAsync();
    }

    [TestMethod]
    public async Task BoundedTextLineReader_RejectsOversizedLine()
    {
        var reader = new BoundedTextLineReader(new StringReader(new string('x', 1025)), 1024);

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => reader.ReadLineAsync().AsTask());

        StringAssert.Contains(error.Message, "1024-character limit");
    }

    [TestMethod]
    public void BoundedTextLineReader_SynchronousReadRejectsOversizedLine()
    {
        var reader = new BoundedTextLineReader(new StringReader(new string('x', 1025)), 1024);

        var error = Assert.ThrowsExactly<InvalidDataException>(() => reader.ReadLine());

        StringAssert.Contains(error.Message, "1024-character limit");
    }

    [TestMethod]
    public async Task ContainedProcessRunner_BoundsAndDrainsBothOutputStreams()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            """
            [Console]::Out.Write(('o' * 131072))
            [Console]::Error.Write(('e' * 131072))
            [Console]::Out.Flush()
            [Console]::Error.Flush()
            """));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
        };

        var result = await ContainedProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(10),
            maxStdoutChars: 4096,
            maxStderrChars: 2048);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(4096, result.Stdout.Length);
        Assert.AreEqual(2048, result.Stderr.Length);
        Assert.IsTrue(result.StdoutTruncated);
        Assert.IsTrue(result.StderrTruncated);
        Assert.IsFalse(result.TimedOut);
    }

    [TestMethod]
    public async Task ContainedProcessRunner_NormalizesAndClosesUnusedStdin()
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(
            """
            $input = [Console]::In.ReadToEnd()
            [Console]::Out.Write($input.Length)
            """));
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
        };

        var result = await ContainedProcessRunner.RunAsync(
            startInfo,
            TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("0", result.Stdout);
        Assert.IsTrue(startInfo.RedirectStandardInput);
    }

    [TestMethod]
    public async Task GitHistory_RunCompletesOnNonPumpingSynchronizationContext()
    {
        var run = typeof(GitHistory).GetMethod(
            "Run",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(typeof(GitHistory).FullName, "Run");
        var completion = new TaskCompletionSource<GitHistory.GitResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                completion.TrySetResult((GitHistory.GitResult)(run.Invoke(
                    null,
                    new object[] { Environment.CurrentDirectory, "--version" })
                    ?? throw new InvalidOperationException("GitHistory.Run returned null")));
            }
            catch (Exception ex)
            {
                completion.TrySetException(ex);
            }
        })
        {
            IsBackground = true,
        };

        thread.Start();
        var result = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(result.Ok, result.StdErr);
    }

    [TestMethod]
    public async Task ClaudeOutputPump_DetachesPermanentlyBlockedConsumerAndKeepsDraining()
    {
        var stdout = new StringReader(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-1\"}\n"
            + "{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\"}\n");
        var stderr = new TrackingTextReader(new string('x', 32 * 1024));
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = 0;

        await ClaudeLiveDriver.PumpOutputAsync(
            stdout,
            stderr,
            _ =>
            {
                callbacks++;
                return neverCompletes.Task;
            },
            eventDeliveryTimeout: TimeSpan.FromMilliseconds(100))
            .WaitAsync(TimeSpan.FromSeconds(2));

        Assert.AreEqual(1, callbacks, "A timed-out consumer must be detached instead of accumulating blocked callbacks.");
        Assert.IsTrue(stderr.FullyDrained);
    }

    [TestMethod]
    public async Task CodexAppServer_OversizedProtocolLineFailsClosed()
    {
        var server = CodexAppServer.Start(
            "ignored.exe",
            processStarter: _ => StartProtocolProcess(
                """
                [Console]::Out.Write(('x' * 2097153))
                [Console]::Out.Flush()
                Start-Sleep -Seconds 120
                """));

        var error = await Assert.ThrowsExactlyAsync<InvalidDataException>(
            () => server.Notifications.Completion.WaitAsync(TimeSpan.FromSeconds(10)));
        StringAssert.Contains(error.Message, "2097152-character limit");
        await WaitUntilAsync(() => server.HasExited, TimeSpan.FromSeconds(10));
        await server.DisposeAsync();
    }

    [TestMethod]
    public void CreationTimeContainment_RejectsAttachAfterStartTestInjection()
    {
        using var job = WindowsProcessJob.CreateKillOnClose();
        var starterCalled = false;

        Assert.ThrowsExactly<ArgumentException>(() => CodexAppServer.Start(
            "ignored.exe",
            processContainment: job,
            processStarter: _ =>
            {
                starterCalled = true;
                return null;
            }));

        Assert.IsFalse(starterCalled);
    }

    [TestMethod]
    public void CodexAppServer_RequiresCreationTimeContainmentOutsideTestInjection()
    {
        var error = Assert.ThrowsExactly<InvalidOperationException>(
            () => CodexAppServer.Start("codex-do-not-launch.exe"));

        StringAssert.Contains(error.Message, "containment");
    }

    [TestMethod]
    public void ClaudeLiveDriver_RequiresCreationTimeContainmentOutsideTestInjection()
    {
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-claude-required-containment", Guid.NewGuid().ToString("N"));
        var driver = new ClaudeLiveDriver(
            "claude-do-not-launch.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(RootDirectory: claimRoot));

        try
        {
            var error = Assert.ThrowsExactly<InvalidOperationException>(() => driver.StartTurn(
                "claude-required-containment",
                Path.GetTempPath(),
                "hello",
                _ => Task.CompletedTask,
                CancellationToken.None));

            StringAssert.Contains(error.Message, "containment");
        }
        finally
        {
            try { Directory.Delete(claimRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void ClaudeLiveDriver_GatewayMode_UsesCcWrapperWithLiveTurnArguments()
    {
        ProcessStartInfo? captured = null;
        var driver = new ClaudeLiveDriver(
            "ignored-claude.exe",
            isSessionLive: _ => false,
            processStarter: psi =>
            {
                captured = psi;
                throw new InvalidOperationException("captured");
            },
            gatewayCliScript: @"C:\trusted\cc.cmd",
            cmdExe: @"C:\Windows\System32\cmd.exe");

        var error = Assert.ThrowsExactly<InvalidOperationException>(() => driver.StartTurn(
            "gateway-live-session",
            Path.GetTempPath(),
            "hello",
            _ => Task.CompletedTask,
            CancellationToken.None,
            launchMode: ArchiveService.GatewayLaunchMode));

        Assert.AreEqual("captured", error.Message);
        Assert.IsNotNull(captured);
        Assert.AreEqual(@"C:\Windows\System32\cmd.exe", captured!.FileName);
        CollectionAssert.AreEqual(
            new[]
            {
                "/c",
                @"C:\trusted\cc.cmd",
                "-p",
                "hello",
                "--output-format",
                "stream-json",
                "--verbose",
                "--permission-mode",
                "acceptEdits",
                "--resume",
                "gateway-live-session",
            },
            captured.ArgumentList.ToArray());
    }

    [TestMethod]
    public void ClaudeLiveDriver_GatewayAvailability_UsesCcInsteadOfNativeClaude()
    {
        var gateway = Path.Combine(Path.GetTempPath(), "clr-gateway-availability", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(gateway);
        try
        {
            var cc = Path.Combine(gateway, "cc.cmd");
            File.WriteAllText(cc, "@echo off");
            var driver = new ClaudeLiveDriver(
                Path.Combine(gateway, "missing-claude.exe"),
                gatewayCliScript: cc,
                cmdExe: "cmd.exe");

            Assert.IsFalse(driver.Available);
            Assert.IsTrue(driver.AvailableFor(ArchiveService.GatewayLaunchMode));
        }
        finally
        {
            try { Directory.Delete(gateway, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task CodexHub_ReleasesClaimWhenTerminationIsConfirmedButTransportCleanupTimesOut()
    {
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-codex-confirmed-timeout", Guid.NewGuid().ToString("N"));
        var containment = new ScriptThenBlockContainment();
        var hub = new CodexAgentHub(
            "ignored.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(
                RootDirectory: claimRoot,
                StaleAfter: TimeSpan.FromMinutes(5)),
            processContainment: containment);

        try
        {
            await hub.StartTurnAsync("codex-confirmed-timeout", "hello", CancellationToken.None);
            Assert.IsTrue(hub.IsTurnActive("codex-confirmed-timeout"));

            var error = await Assert.ThrowsExactlyAsync<TimeoutException>(
                () => hub.DisposeAsync().AsTask());
            StringAssert.Contains(error.Message, "transport");

            Assert.IsTrue(SessionLaunchClaims.TryAcquire(
                "codex-confirmed-timeout",
                null,
                "verification",
                out var retryClaim,
                out var detail,
                _ => false,
                new SessionLaunchClaims.Options(
                    RootDirectory: claimRoot,
                    StaleAfter: TimeSpan.FromMinutes(5))),
                detail);
            retryClaim?.Dispose();
        }
        finally
        {
            try { await hub.DisposeAsync(); } catch { }
            containment.Dispose();
            try { Directory.Delete(claimRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task CodexTurnStart_LostAcknowledgementRetainsWriterClaim()
    {
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-codex-lost-turn-ack", Guid.NewGuid().ToString("N"));
        var hub = new CodexAgentHub(
            "ignored.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(
                RootDirectory: claimRoot,
                StaleAfter: TimeSpan.FromMinutes(5)),
            processStarter: _ => StartProtocolProcess(
                """
                $initialize = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}')
                [Console]::Out.Flush()
                $turn = [Console]::In.ReadLine()
                Start-Sleep -Seconds 120
                """));

        try
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsExactlyAsync<TaskCanceledException>(
                () => hub.StartTurnAsync(
                    "codex-lost-turn-ack",
                    "hello",
                    cancellation.Token));

            var acquired = SessionLaunchClaims.TryAcquire(
                "codex-lost-turn-ack",
                null,
                "duplicate verification",
                out var duplicateClaim,
                out var detail,
                _ => false,
                new SessionLaunchClaims.Options(
                    RootDirectory: claimRoot,
                    StaleAfter: TimeSpan.FromMinutes(5)));
            duplicateClaim?.Dispose();

            Assert.IsFalse(
                acquired,
                "A turn/start request may have been accepted before its response was lost; retry must remain fenced.");
            StringAssert.Contains(detail, "launch already pending");
        }
        finally
        {
            try { await hub.DisposeAsync(); } catch { }
            try { Directory.Delete(claimRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task CodexTurnStart_ConfirmedTerminationWithoutResponseStillRetainsWriterClaim()
    {
        // A confirmed process exit proves the transport is gone; it does not prove that
        // turn/start was rejected before Codex accepted it. The writer claim must stay
        // fenced until expiry so a retry can never double-start an accepted turn.
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-codex-dead-no-response", Guid.NewGuid().ToString("N"));
        var hub = new CodexAgentHub(
            "ignored.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(
                RootDirectory: claimRoot,
                StaleAfter: TimeSpan.FromMinutes(5)),
            processStarter: _ => StartProtocolProcess(
                """
                $initialize = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}')
                [Console]::Out.Flush()
                $null = [Console]::In.ReadLine()
                exit 0
                """));

        try
        {
            var startTask = hub.StartTurnAsync(
                "codex-died-before-response",
                "hello",
                CancellationToken.None);
            Exception ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => startTask);
            Assert.IsNotInstanceOfType(ex, typeof(CodexAppServerResponseException),
                "the fake server never sent a response, so this must not look like an RPC rejection");

            var acquired = SessionLaunchClaims.TryAcquire(
                "codex-died-before-response",
                null,
                "fence verification",
                out var fencedClaim,
                out var detail,
                _ => false,
                new SessionLaunchClaims.Options(
                    RootDirectory: claimRoot,
                    StaleAfter: TimeSpan.FromMinutes(5)));

            Assert.IsFalse(acquired, detail == null
                ? "claim should still be held"
                : detail + " — a confirmed transport death must not unfence a turn whose acceptance is unknown.");
            fencedClaim?.Dispose();
        }
        finally
        {
            try { await hub.DisposeAsync(); } catch { }
            try { Directory.Delete(claimRoot, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public async Task CodexTurnStart_DefiniteRpcRejectionReleasesWriterClaim()
    {
        var claimRoot = Path.Combine(Path.GetTempPath(), "clr-codex-rejected-turn", Guid.NewGuid().ToString("N"));
        var hub = new CodexAgentHub(
            "ignored.exe",
            isSessionLive: _ => false,
            claimOptions: new SessionLaunchClaims.Options(
                RootDirectory: claimRoot,
                StaleAfter: TimeSpan.FromMinutes(5)),
            processStarter: _ => StartProtocolProcess(
                """
                $initialize = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}')
                [Console]::Out.Flush()
                $turn = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":2,"error":{"message":"turn rejected"}}')
                [Console]::Out.Flush()
                Start-Sleep -Seconds 120
                """));

        try
        {
            await Assert.ThrowsExactlyAsync<CodexAppServerResponseException>(
                () => hub.StartTurnAsync(
                    "codex-rejected-turn",
                    "hello",
                    CancellationToken.None));

            var acquired = SessionLaunchClaims.TryAcquire(
                "codex-rejected-turn",
                null,
                "retry verification",
                out var retryClaim,
                out var detail,
                _ => false,
                new SessionLaunchClaims.Options(
                    RootDirectory: claimRoot,
                    StaleAfter: TimeSpan.FromMinutes(5)));

            Assert.IsTrue(acquired, detail);
            retryClaim?.Dispose();
        }
        finally
        {
            try { await hub.DisposeAsync(); } catch { }
            try { Directory.Delete(claimRoot, recursive: true); } catch { }
        }
    }

    private static Process StartSleeper(List<int> pids)
    {
        var process = StartProtocolProcess("Start-Sleep -Seconds 120");
        pids.Add(process.Id);
        return process;
    }

    private static Process StartProtocolProcess(string script, Action<Process>? started = null)
    {
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-EncodedCommand", encoded },
        }) ?? throw new InvalidOperationException("could not start lifecycle test process");
        started?.Invoke(process);
        return process;
    }

    private static async Task AssertExitedAsync(int pid)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsAlive(pid)) return;
            await Task.Delay(50);
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
        }
        Assert.Fail($"process {pid} survived a failed or disposed containment path");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail("condition was not reached before timeout");
    }

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

    private sealed class UnconfirmedContainment : IProcessContainment
    {
        private readonly Func<ProcessStartInfo, Process?> _starter;

        public UnconfirmedContainment(Func<ProcessStartInfo, Process?> starter) =>
            _starter = starter;

        public ContainedProcess StartContained(ProcessStartInfo startInfo)
        {
            using var process = ContainedProcess.Start(startInfo, _starter);
            throw new ProcessContainmentException(
                "injected unconfirmed containment failure",
                terminationConfirmed: false);
        }

    }

    private sealed class AttachAfterStartContainment : IProcessContainment
    {
        private readonly WindowsProcessJob _inner;
        private readonly Func<ProcessStartInfo, Process?> _starter;

        public AttachAfterStartContainment(
            WindowsProcessJob inner,
            Func<ProcessStartInfo, Process?> starter)
        {
            _inner = inner;
            _starter = starter;
        }

        public ContainedProcess StartContained(ProcessStartInfo startInfo)
        {
            var process = ContainedProcess.Start(startInfo, _starter);
            try
            {
                _inner.AttachOrTerminate(process.Process);
                return process;
            }
            catch
            {
                process.Dispose();
                throw;
            }
        }

    }

    private sealed class ScriptThenBlockContainment : IProcessContainment, IDisposable
    {
        private Process? _process;
        private int _processId;
        private BlockAfterEofStreamReader? _reader;

        public ContainedProcess StartContained(ProcessStartInfo startInfo)
        {
            _process = StartProtocolProcess(
                """
                $null = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":1,"result":{"ok":true}}')
                [Console]::Out.Flush()
                $null = [Console]::In.ReadLine()
                [Console]::Out.WriteLine('{"jsonrpc":"2.0","id":2,"result":{"ok":true}}')
                [Console]::Out.Flush()
                """);
            _processId = _process.Id;
            _reader = new BlockAfterEofStreamReader(_process.StandardOutput);
            return new ContainedProcess(
                _process,
                _process.StandardInput,
                _reader,
                _process.StandardError,
                ownsSeparateStreams: false);
        }

        public void Dispose()
        {
            _reader?.Unblock();
            if (_processId > 0) TryKill(_processId);
            _process?.Dispose();
        }
    }

    private sealed class BlockAfterEofStreamReader : StreamReader
    {
        private readonly StreamReader _inner;
        private readonly TaskCompletionSource<int> _blocked =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _newlineCount;

        public BlockAfterEofStreamReader(StreamReader inner)
            : base(Stream.Null) =>
            _inner = inner;

        public override async ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_newlineCount >= 2)
                return await _blocked.Task;

            var count = await _inner.ReadAsync(buffer, cancellationToken);
            if (count > 0)
            {
                _newlineCount += CountNewlines(buffer, count);
                return count;
            }
            return await _blocked.Task;
        }

        public void Unblock() => _blocked.TrySetResult(0);

        private static int CountNewlines(Memory<char> buffer, int count)
        {
            var total = 0;
            for (var i = 0; i < count; i++)
                if (buffer.Span[i] == '\n') total++;
            return total;
        }
    }

    private sealed class TrackingTextReader(string text) : TextReader
    {
        private int _offset;

        public bool FullyDrained { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= text.Length)
            {
                FullyDrained = true;
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(buffer.Length, text.Length - _offset);
            text.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
    }
}
