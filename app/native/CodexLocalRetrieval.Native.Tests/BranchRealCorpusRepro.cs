using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// Branching passes on the synthetic fixtures in BranchSessionTests but fails often in the field, so
// this drives the real code path over REAL transcripts copied out of the live stores. It touches no
// live state: every transcript is copied into a temp tree first, and the store/templates/codex-db
// paths are all temp. It self-skips when the machine has no corpus (CI), so it is a reproduction
// harness that is safe to keep rather than a fixture test.
[TestClass]
public sealed class BranchRealCorpusRepro
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static List<string> RealClaudeTranscripts(int take)
    {
        var root = Path.Combine(Home, ".claude", "projects");
        if (!Directory.Exists(root)) return new();
        return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
            .Where(p => !Path.GetFileName(p).StartsWith("agent-", StringComparison.OrdinalIgnoreCase))
            .Select(p => new FileInfo(p))
            .Where(f => f.Length > 0)
            .OrderByDescending(f => f.Length)          // biggest first: size is the untested axis
            .Take(take)
            .Select(f => f.FullName)
            .ToList();
    }

    [TestMethod]
    public async Task BranchingRealClaudeTranscripts_Succeeds()
    {
        var sources = RealClaudeTranscripts(6);
        if (sources.Count == 0) Assert.Inconclusive("no local Claude corpus on this machine");

        var dir = Path.Combine(Path.GetTempPath(), "branch-repro-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var projects = Path.Combine(dir, "projects", "workspace");
            Directory.CreateDirectory(projects);
            var service = new ArchiveService(
                storePath: Path.Combine(dir, "store.json"),
                claudeSessionsRoot: Path.Combine(dir, "projects"),
                templatesRoot: Path.Combine(dir, "templates"));

            var failures = new List<string>();
            foreach (var source in sources)
            {
                // Copy out of the live store so a running agent's file is never the branch target.
                var id = Path.GetFileNameWithoutExtension(source);
                var local = Path.Combine(projects, id + ".jsonl");
                File.Copy(source, local, overwrite: true);

                var session = new ArchiveSession
                {
                    Id = id,
                    Tool = "claude",
                    Title = "real corpus " + id,
                    SourcePath = local,
                    Workspace = projects,
                };
                service.Store.Sessions[session.Id] = session;

                var sizeMb = new FileInfo(local).Length / (1024.0 * 1024.0);
                BranchResultLike result;
                try
                {
                    var branch = await service.BranchSessionAsync(session);
                    result = new BranchResultLike(branch.Ok, branch.Message);
                }
                catch (Exception ex)
                {
                    result = new BranchResultLike(false, ex.GetType().Name + ": " + ex.Message);
                }

                Console.WriteLine($"[{sizeMb,7:F1} MB] {id} -> ok={result.Ok} :: {result.Message}");
                if (!result.Ok) failures.Add($"{id} ({sizeMb:F1} MB): {result.Message}");
            }

            Assert.IsTrue(
                failures.Count == 0,
                "branching real transcripts failed:\n  " + string.Join("\n  ", failures));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    // THE field failure: the desktop app and the always-on server share one app-store, so the save
    // that RECORDS a branch could lose the generation race. The transcript was already on disk, but
    // the branch was reported as failed and the clone deleted. Intermittent, and therefore "a lot of
    // times" rather than always.
    [TestMethod]
    public async Task BranchSurvivesAnotherProcessWritingTheStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "branch-race-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = Path.Combine(dir, "store.json");
            var projects = Path.Combine(dir, "projects", "workspace");
            Directory.CreateDirectory(projects);
            var transcript = Path.Combine(projects, "parent.jsonl");
            await File.WriteAllTextAsync(transcript,
                "{\"type\":\"user\",\"uuid\":\"u1\",\"sessionId\":\"parent\",\"cwd\":\"" + projects.Replace("\\", "\\\\") + "\"}\n" +
                "{\"type\":\"assistant\",\"uuid\":\"u2\",\"sessionId\":\"parent\"}\n");

            var desktop = new ArchiveService(storePath: store, claudeSessionsRoot: Path.Combine(dir, "projects"), templatesRoot: Path.Combine(dir, "templates"));
            var parent = new ArchiveSession { Id = "parent", Tool = "claude", Title = "parent", SourcePath = transcript, Workspace = projects };
            desktop.Store.Sessions[parent.Id] = parent;
            await desktop.SaveAsync();
            await desktop.LoadAsync();

            // The server commits first, so the desktop's loaded generation is now stale — exactly the
            // state the app is in when the 30s projection push lands mid-branch.
            var server = new ArchiveService(storePath: store);
            await server.LoadAsync();
            server.Store.Sessions["server-change"] = new ArchiveSession { Id = "server-change", Tool = "codex", Title = "server-change" };
            await server.SaveAsync();

            var result = await desktop.BranchSessionAsync(parent);

            Assert.IsTrue(result.Ok, "branch reported failure during a store race: " + result.Message);
            Assert.IsNotNull(result.Branch);
            Assert.IsTrue(File.Exists(result.Branch!.SourcePath), "the cloned transcript was deleted by the lost race");

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();
            Assert.IsTrue(reader.Store.Sessions.ContainsKey(result.Branch.Id), "the branch never reached the store");
            Assert.IsTrue(reader.Store.Sessions.ContainsKey("server-change"), "the other writer's commit was clobbered");
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    private readonly record struct BranchResultLike(bool Ok, string Message);
}
