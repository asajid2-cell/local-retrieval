using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval_Native;

internal static class GuiVerificationFixture
{
    internal static bool Enabled => Environment.GetEnvironmentVariable("CLR_GUI_TEST_PROFILE") == "1";
    internal static string Root
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("CLR_GUI_TEST_ROOT");
            if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
                throw new InvalidOperationException("Absolute GUI fixture root required");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
    }
    internal static string MutexName => @"Local\MUX.GuiFixture." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())));
    internal static int Port => int.Parse(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_RELAY_PORT") ?? "0");

    internal static void Validate()
    {
        if (!Enabled) return;
        var root = Root;
        var relative = Path.GetRelativePath(Path.GetTempPath(), root);
        if (relative == "." || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar) || Path.IsPathRooted(relative)
            || !Directory.Exists(root) || !File.Exists(Path.Combine(root, "gui-fixture.marker")))
            throw new InvalidOperationException("GUI verification requires a marked temporary directory");
        for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("GUI fixture refuses reparse paths");
        var pending = new Stack<string>();
        pending.Push(root);
        var entries = 0;
        while (pending.TryPop(out var directory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > 10000) throw new InvalidOperationException("GUI fixture tree exceeds verification limit");
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("GUI fixture refuses reparse entries");
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
            }
        }
        if (Port is < 1024 or > 65535 or 7699 || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN")))
            throw new InvalidOperationException("GUI verification requires isolated relay port and bridge token");
        if (Environment.GetEnvironmentVariable("CLR_CAPTURE") != "1"
            || Path.GetFullPath(Environment.GetEnvironmentVariable("CLR_CAP_DIR") ?? "") != Path.Combine(root, "capture"))
            throw new InvalidOperationException("GUI verification requires fixture-contained capture directory");
        using var store = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "app-store.json")));
        if (store.RootElement.TryGetProperty("templateSnapshots", out var snapshots) && snapshots.EnumerateObject().Any())
            throw new InvalidOperationException("GUI metadata fixture does not load snapshots");
        foreach (var session in store.RootElement.GetProperty("sessions").EnumerateObject())
        {
            if (!session.Value.TryGetProperty("sourcePath", out var source) || string.IsNullOrEmpty(source.GetString())) continue;
            var path = Path.GetFullPath(source.GetString()!);
            var rel = Path.GetRelativePath(root, path);
            if (Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + Path.DirectorySeparatorChar)
                || !File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("GUI fixture source escapes temporary directory");
            for (var dir = Directory.GetParent(path); dir is not null && dir.FullName != root; dir = dir.Parent)
                if ((dir.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("GUI fixture source uses reparse directory");
        }
    }

    internal static ArchiveService CreateArchive()
    {
        if (!Enabled) return new ArchiveService();
        Validate();
        return new ArchiveService(storePath: Path.Combine(Root, "app-store.json"),
            codexSessionsRoot: Path.Combine(Root, "codex"), claudeSessionsRoot: Path.Combine(Root, "claude"),
            codexStateDbPath: Path.Combine(Root, "codex.sqlite"), templatesRoot: Path.Combine(Root, "templates"),
            transcriptSearchIndexPath: Path.Combine(Root, "search.sqlite"), enableTranscriptSearchIndex: false,
            sourceOverride: Array.Empty<SessionSource>());
    }

    internal static async Task<(int code, string outText)> SendAsync(string path, string body, TimeSpan? timeout = null)
    {
        if (!Enabled || !(path == "/api/projects" || path == "/api/app-commands/lease"
            || (path.StartsWith("/api/app-commands/", StringComparison.Ordinal) && path.EndsWith("/ack", StringComparison.Ordinal))))
            throw new InvalidOperationException("GUI fixture transport refused");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{Port}{path}");
        request.Headers.Add("X-Mux-Command-Bridge", Environment.GetEnvironmentVariable("CLR_REMOTE_TEST_COMMAND_BRIDGE_TOKEN"));
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        return (response.IsSuccessStatusCode ? 0 : (int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }
}
