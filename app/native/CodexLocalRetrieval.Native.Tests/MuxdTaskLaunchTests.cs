using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public class MuxdTaskLaunchTests
{
    [TestMethod]
    public void TryGetHiddenPythonAction_ConvertsMuxdPythonTaskToPythonw()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Python311\python.exe</Command>
                  <Arguments>C:\Users\Ahmed\muxd\muxd.py</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var ok = MuxdTaskLaunch.TryGetHiddenPythonAction(xml, p => p == @"C:\Python311\pythonw.exe", out var exe, out var args);

        Assert.IsTrue(ok);
        Assert.AreEqual(@"C:\Python311\pythonw.exe", exe);
        Assert.AreEqual(@"C:\Users\Ahmed\muxd\muxd.py", args);
    }

    [TestMethod]
    public void TryGetHiddenPythonAction_LeavesAlreadyHiddenTaskAlone()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Python311\pythonw.exe</Command>
                  <Arguments>C:\Users\Ahmed\muxd\muxd.py</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var ok = MuxdTaskLaunch.TryGetHiddenPythonAction(xml, _ => true, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryGetHiddenPythonAction_IgnoresNonMuxdPythonTasks()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Python311\python.exe</Command>
                  <Arguments>C:\Users\Ahmed\other.py</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var ok = MuxdTaskLaunch.TryGetHiddenPythonAction(xml, _ => true, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryGetHiddenPythonAction_RequiresPythonwToExist()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Python311\python.exe</Command>
                  <Arguments>C:\Users\Ahmed\muxd\muxd.py</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var ok = MuxdTaskLaunch.TryGetHiddenPythonAction(xml, _ => false, out _, out _);

        Assert.IsFalse(ok);
    }

    [TestMethod]
    public void TryGetAdopterAction_ResolvesPythonwAndMuxrunBesideMuxd()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Python311\python.exe</Command>
                  <Arguments>-u "C:\Users\Ahmed\muxd\muxd.py"</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        var ok = MuxdTaskLaunch.TryGetAdopterAction(
            xml,
            path => path is @"C:\Python311\pythonw.exe" or @"C:\Users\Ahmed\muxd\muxrun.py",
            out var action);

        Assert.IsTrue(ok);
        Assert.IsNotNull(action);
        Assert.AreEqual(@"C:\Python311\pythonw.exe", action.PythonwExe);
        Assert.AreEqual(@"C:\Users\Ahmed\muxd\muxrun.py", action.MuxrunPath);
    }

    [TestMethod]
    public void TryGetAdopterAction_RejectsMissingMuxrun()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Python311\pythonw.exe</Command>
                  <Arguments>C:\Users\Ahmed\muxd\muxd.py</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        Assert.IsFalse(MuxdTaskLaunch.TryGetAdopterAction(
            xml,
            path => path == @"C:\Python311\pythonw.exe",
            out _));
    }

    [TestMethod]
    public void TryGetAdopterAction_ResolvesInstalledWscriptWrapperChain()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Windows\System32\wscript.exe</Command>
                  <Arguments>//B //NoLogo "C:\Users\Ahmed\muxd\ops\launch_muxd.vbs"</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        const string vbs = """"
            command = """C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe"" -File ""C:\Users\Ahmed\muxd\ops\launch_muxd.ps1"""
            """";
        const string ps1 = """
            $child = "Start-Process -Wait -FilePath 'C:\Python311\pythonw.exe' -ArgumentList 'C:\Users\Ahmed\muxd\muxd.py'"
            """;

        var ok = MuxdTaskLaunch.TryGetAdopterAction(
            xml,
            path => path is
                @"C:\Users\Ahmed\muxd\ops\launch_muxd.vbs" or
                @"C:\Users\Ahmed\muxd\ops\launch_muxd.ps1" or
                @"C:\Python311\pythonw.exe" or
                @"C:\Users\Ahmed\muxd\muxrun.py",
            path => path.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase) ? vbs : ps1,
            out var action);

        Assert.IsTrue(ok);
        Assert.IsNotNull(action);
        Assert.AreEqual(@"C:\Python311\pythonw.exe", action.PythonwExe);
        Assert.AreEqual(@"C:\Users\Ahmed\muxd\muxrun.py", action.MuxrunPath);
    }

    [TestMethod]
    public void TryGetAdopterAction_RejectsWrapperThatPointsOutsideRuntimeRoot()
    {
        const string xml = """
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <Actions>
                <Exec>
                  <Command>C:\Windows\System32\wscript.exe</Command>
                  <Arguments>"C:\Users\Ahmed\muxd\ops\launch_muxd.vbs"</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;

        Assert.IsFalse(MuxdTaskLaunch.TryGetAdopterAction(
            xml,
            _ => true,
            path => path.EndsWith(".vbs", StringComparison.OrdinalIgnoreCase)
                ? "launch_muxd.ps1"
                : "$child = \"Start-Process -Wait -FilePath 'C:\\Python311\\pythonw.exe' -ArgumentList 'C:\\Other\\muxd.py'\"",
            out _));
    }
}
