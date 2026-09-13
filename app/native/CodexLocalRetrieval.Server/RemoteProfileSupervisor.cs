using System.Diagnostics;
using CodexLocalRetrieval.Core.Agents;
using CodexLocalRetrieval.Core.Remote;

namespace CodexLocalRetrieval.Server;

internal static class RemoteProfileSupervisor
{
    public static int Run(string tunnelScript)
    {
        var profile = RemoteRuntimeProfile.Load(RemoteRuntimeProfile.ProductionRelayPort);
        if (profile.Name != "test") throw new InvalidOperationException("Supervisor requires the isolated test profile.");
        var root = Environment.GetEnvironmentVariable("MUXD_RUNTIME_ROOT") ?? "";
        var python = Environment.GetEnvironmentVariable("MUXD_PYTHON") ?? "";
        foreach (var path in new[] { python, Path.Combine(root, "muxd.py"), tunnelScript })
            if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
                throw new InvalidOperationException("Supervisor requires explicit existing runtime files.");
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Supervisor executable unavailable.");
        if (!string.Equals(Path.GetFileNameWithoutExtension(executable), "CodexLocalRetrieval.Server", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Run the supervisor through the server apphost executable.");
        Directory.CreateDirectory(profile.StateRoot);
        using var ownership = new FileStream(Path.Combine(profile.StateRoot, "supervisor.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var shell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        var specifications = new[]
        {
            StartInfo(python, root, Path.Combine(root, "muxd.py"), "--profile", "test"),
            StartInfo(executable, AppContext.BaseDirectory),
            StartInfo(shell, Path.GetDirectoryName(tunnelScript)!, "-NoProfile", "-NonInteractive", "-File", tunnelScript)
        };
        var children = new ContainedProcess?[specifications.Length];
        var jobs = new WindowsProcessJob?[specifications.Length];
        var drains = new Task?[specifications.Length];
        using var stop = new ManualResetEventSlim();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stop.Set(); };
        Console.CancelKeyPress += cancel;
        try
        {
            while (!stop.IsSet)
            {
                for (var i = 0; i < children.Length; i++)
                {
                    if (children[i] is { HasExited: false }) continue;
                    // Close the old service's job before replacement: its descendants may outlive its root.
                    jobs[i]?.Dispose();
                    jobs[i] = null;
                    children[i]?.Dispose();
                    children[i] = null;
                    if (drains[i] is not null) drains[i]!.GetAwaiter().GetResult();
                    jobs[i] = WindowsProcessJob.CreateKillOnClose();
                    children[i] = jobs[i]!.StartContained(specifications[i]);
                    children[i]!.StandardInput.Close();
                    drains[i] = Task.WhenAll(
                        children[i]!.StandardOutput.BaseStream.CopyToAsync(Stream.Null),
                        children[i]!.StandardError.BaseStream.CopyToAsync(Stream.Null));
                    Console.WriteLine($"supervisor service {i} started pid={children[i]!.Id}");
                }
                stop.Wait(TimeSpan.FromSeconds(5));
            }
            return 0;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
            for (var i = 0; i < jobs.Length; i++)
            {
                try { jobs[i]?.Dispose(); }
                catch (Exception ex) { Console.Error.WriteLine("Supervisor job cleanup failed: " + ex.Message); }
                try { children[i]?.Dispose(); }
                catch (Exception ex) { Console.Error.WriteLine("Supervisor handle cleanup failed: " + ex.Message); }
            }
        }
    }

    private static ProcessStartInfo StartInfo(string executable, string directory, params string[] arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["MUXCTL_AUTOSTART"] = "0";
        return info;
    }
}
