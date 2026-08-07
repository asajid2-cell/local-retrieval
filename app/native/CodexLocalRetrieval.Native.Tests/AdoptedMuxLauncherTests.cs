using System.Diagnostics;
using CodexLocalRetrieval.Core.Remote;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class AdoptedMuxLauncherTests
{
    private const string TaskXml = """
        <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Actions>
            <Exec>
              <Command>C:\Python311\pythonw.exe</Command>
              <Arguments>"C:\Users\Ahmed\muxd\muxd.py"</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    [TestMethod]
    public async Task MirrorAsync_VerifiesExactPidAndStartsTrustedSidecar()
    {
        ProcessStartInfo? started = null;
        var sidecarStarted = false;
        var request = Request();
        var deps = Dependencies(
            sessions: [Running(request)],
            start: psi =>
            {
                started = psi;
                sidecarStarted = true;
                return true;
            });

        Task<string> Muxd(object _)
            => Task.FromResult(sidecarStarted ? AdoptedRow(request) : """{"t":"ls","list":[]}""");

        var result = await AdoptedMuxLauncher.MirrorAsync(request, Muxd, deps);

        Assert.IsTrue(result.Ok, result.Detail);
        Assert.IsNotNull(started);
        Assert.AreEqual(@"C:\Python311\pythonw.exe", started.FileName);
        CollectionAssert.Contains(started.ArgumentList.ToArray(), @"C:\Users\Ahmed\muxd\muxrun.py");
        CollectionAssert.Contains(started.ArgumentList.ToArray(), "--attach-pid");
        CollectionAssert.Contains(started.ArgumentList.ToArray(), request.Pid.ToString());
        CollectionAssert.Contains(started.ArgumentList.ToArray(), "--cmd-b64");
        Assert.DoesNotContain(
            request.Command,
            started.ArgumentList,
            "trusted command must not ride as plaintext argv");
    }

    [TestMethod]
    public async Task MirrorAsync_RefusesPidThatDoesNotOwnRequestedSession()
    {
        var request = Request();
        var wrong = Running(request) with { SessionId = "different-session" };
        var starts = 0;
        var deps = Dependencies(
            sessions: [wrong],
            start: _ => { starts++; return true; });

        var result = await AdoptedMuxLauncher.MirrorAsync(
            request,
            _ => Task.FromResult("""{"t":"ls","list":[]}"""),
            deps);

        Assert.IsFalse(result.Ok);
        StringAssert.Contains(result.Detail, "does not own session");
        Assert.AreEqual(0, starts);
    }

    [TestMethod]
    public async Task MirrorAsync_IsIdempotentWhenExactAdoptedRowAlreadyExists()
    {
        var request = Request();
        var starts = 0;
        var deps = Dependencies(
            sessions: [Running(request)],
            start: _ => { starts++; return true; });

        var result = await AdoptedMuxLauncher.MirrorAsync(
            request,
            _ => Task.FromResult(AdoptedRow(request)),
            deps);

        Assert.IsTrue(result.Ok, result.Detail);
        Assert.AreEqual(0, starts);
    }

    private static AdoptedMuxLauncher.Request Request()
        => new(
            "mirror-test",
            4242,
            "session-1",
            ["session-alias"],
            "codex",
            "codex resume session-1",
            @"C:\work");

    private static ArchiveService.RunningSessionInfo Running(AdoptedMuxLauncher.Request request)
        => new(request.Pid, request.Tool, request.SessionId, "Windows Terminal", "2026-08-05T00:00:00Z", request.Workspace);

    private static AdoptedMuxLauncher.Dependencies Dependencies(
        IReadOnlyList<ArchiveService.RunningSessionInfo> sessions,
        Func<ProcessStartInfo, bool> start)
        => new(
            () => (true, sessions.ToList(), ""),
            () => Task.FromResult((true, TaskXml, "")),
            start,
            _ => Task.CompletedTask,
            path => path is @"C:\Python311\pythonw.exe" or @"C:\Users\Ahmed\muxd\muxrun.py",
            _ => null,
            _ => true);

    private static string AdoptedRow(AdoptedMuxLauncher.Request request)
        => $$"""
            {"t":"ls","list":[{
              "name":"{{request.MuxName}}",
              "alive":true,
              "adopted":true,
              "externalOwner":true,
              "childPid":{{request.Pid}},
              "sessionId":"{{request.SessionId}}",
              "aliases":["session-alias"]
            }]}
            """;
}
