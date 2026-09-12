using System.Text.Json;
using CodexLocalRetrieval_Native;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
[DoNotParallelize]
public class GuiVerificationFixtureTests
{
    private string _root = "";
    private readonly Dictionary<string, string?> _environment = new();

    private void Set(string name, string? value)
    {
        _environment.TryAdd(name, Environment.GetEnvironmentVariable(name));
        Environment.SetEnvironmentVariable(name, value);
    }

    [TestInitialize]
    public void Init()
    {
        _root = Path.Combine(Path.GetTempPath(), "mux-gui-isolation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "gui-fixture.marker"), "fixture");
        File.WriteAllText(Path.Combine(_root, "app-store.json"), "{\"sessions\":{}}");
        Set("CLR_GUI_TEST_PROFILE", "1");
        Set("CLR_GUI_TEST_ROOT", _root);
        Set("CLR_REMOTE_TEST_RELAY_PORT", "32145");
        Set("CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN", Guid.NewGuid().ToString("N"));
        Set("CLR_CAPTURE", "1");
        Set("CLR_CAP_DIR", Path.Combine(_root, "capture"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        foreach (var (name, value) in _environment) Environment.SetEnvironmentVariable(name, value);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [TestMethod]
    public void ValidMarkedFixture_IsAccepted() => GuiVerificationFixture.Validate();

    [TestMethod]
    public void RelativeRoot_IsRefusedBeforeNormalization()
    {
        var cwd = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = _root;
            Set("CLR_GUI_TEST_ROOT", ".");
            Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
        }
        finally { Environment.CurrentDirectory = cwd; }
    }

    [TestMethod]
    public void MissingMarker_IsRefused()
    {
        File.Delete(Path.Combine(_root, "gui-fixture.marker"));
        Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
    }

    [TestMethod]
    [DataRow("7699")]
    [DataRow("0")]
    [DataRow("65536")]
    public void ProductionOrInvalidPort_IsRefused(string port)
    {
        Set("CLR_REMOTE_TEST_RELAY_PORT", port);
        Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
    }

    [TestMethod]
    public void SourceOutsideFixture_IsRefused()
    {
        File.WriteAllText(Path.Combine(_root, "app-store.json"), JsonSerializer.Serialize(new
        {
            sessions = new { outside = new { sourcePath = typeof(GuiVerificationFixtureTests).Assembly.Location } }
        }));
        Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
    }

    [TestMethod]
    public void ServerPresence_RequiresExactFixtureMutexAndProfile()
    {
        Set("CLR_REMOTE_TEST_PROFILE", "1");
        var store = Path.Combine(_root, "app-store.json");
        Assert.IsFalse(CodexLocalRetrieval.Server.LoopbackRelayTransport.IsGuiFixtureRunning(store));
        using (var unrelated = new Mutex(false, GuiVerificationFixture.MutexName + ".other"))
            Assert.IsFalse(CodexLocalRetrieval.Server.LoopbackRelayTransport.IsGuiFixtureRunning(store));
        using (var exact = new Mutex(false, GuiVerificationFixture.MutexName))
        {
            Assert.IsTrue(CodexLocalRetrieval.Server.LoopbackRelayTransport.IsGuiFixtureRunning(store));
            Set("CLR_REMOTE_TEST_PROFILE", null);
            Assert.IsFalse(CodexLocalRetrieval.Server.LoopbackRelayTransport.IsGuiFixtureRunning(store));
        }
        Set("CLR_REMOTE_TEST_PROFILE", "1");
        Assert.IsFalse(CodexLocalRetrieval.Server.LoopbackRelayTransport.IsGuiFixtureRunning(store));
    }

    [TestMethod]
    public void SnapshotStore_IsRefused()
    {
        File.WriteAllText(Path.Combine(_root, "app-store.json"), "{\"sessions\":{},\"templateSnapshots\":{\"outside\":{\"snapshotPath\":\"C:/outside\"}}}");
        Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
    }

    [TestMethod]
    public void MissingBridgeToken_IsRefused()
    {
        Set("CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN", null);
        Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
    }

    [TestMethod]
    public void CaptureOutsideFixture_IsRefused()
    {
        Set("CLR_CAP_DIR", Path.GetTempPath());
        Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
    }

    [TestMethod]
    public async Task ReparseCaptureDirectory_IsRefused()
    {
        var target = Path.Combine(_root, "target");
        var link = Path.Combine(_root, "capture");
        Directory.CreateDirectory(target);
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\""
        };
        try
        {
            var result = await CodexLocalRetrieval.Core.Agents.ContainedProcessRunner.RunAsync(start, TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, result.ExitCode, "disposable junction setup must succeed");
            Assert.IsTrue((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            Assert.ThrowsExactly<InvalidOperationException>(GuiVerificationFixture.Validate);
        }
        finally { if (Directory.Exists(link)) Directory.Delete(link); }
    }

    [TestMethod]
    public void DisabledProfile_DoesNotValidateOrCreateFixture()
    {
        Set("CLR_GUI_TEST_PROFILE", null);
        Set("CLR_GUI_TEST_ROOT", null);
        Assert.IsFalse(GuiVerificationFixture.Enabled);
        GuiVerificationFixture.Validate();
    }
}
