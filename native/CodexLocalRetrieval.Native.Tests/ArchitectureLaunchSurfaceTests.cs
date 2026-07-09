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
