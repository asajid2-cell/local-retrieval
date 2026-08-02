using CodexLocalRetrieval_Native;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class DiagTests
{
    [TestMethod]
    public void Write_RotatesAndRetainsOnlyTheConfiguredFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-diag-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, "clr-startup.log");
            for (var i = 0; i < 30; i++)
                Diag.Write(path, $"line {i} {new string('x', 180)}", 1024, 2, DateTime.Parse("2026-08-02T12:00:00"));

            Assert.IsTrue(File.Exists(path));
            Assert.IsTrue(File.Exists(path + ".1"));
            Assert.IsTrue(File.Exists(path + ".2"));
            Assert.IsFalse(File.Exists(path + ".3"));
            Assert.IsTrue(new FileInfo(path).Length <= 1024);
            Assert.IsTrue(new FileInfo(path + ".1").Length <= 1024);
            Assert.IsTrue(new FileInfo(path + ".2").Length <= 1024);
            StringAssert.Contains(File.ReadAllText(path), "2026-08-02 12:00:00.000");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }
}
