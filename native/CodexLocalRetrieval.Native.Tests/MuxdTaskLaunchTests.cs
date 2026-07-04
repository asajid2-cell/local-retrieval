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
}
