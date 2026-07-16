using System.Text.Json;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class BranchSessionTests
{
    [TestMethod]
    public async Task BranchSessionAsync_Claude_ClonesTranscriptAndLinksParent()
    {
        using var fixture = new TemplateFixture("claude");
        var result = await fixture.Service.BranchSessionAsync(fixture.Source);

        Assert.IsTrue(result.Ok, result.Message);
        Assert.IsNotNull(result.Branch);
        Assert.AreEqual(fixture.Source.Id, result.Branch.BranchOfId);
        Assert.IsTrue(File.Exists(result.Branch.SourcePath));
        var text = await File.ReadAllTextAsync(result.Branch.SourcePath);
        StringAssert.Contains(text, result.Branch.Id);
        StringAssert.Contains(text, fixture.Source.Id);
        StringAssert.Contains(text, "snapshot-tip");
    }

    [TestMethod]
    public void RewriteCodexSessionMeta_RewritesIdAndStampsFork()
    {
        var line = "{\"timestamp\":\"2026-07-08T22:44:11Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"old-id\",\"id\":\"old-id\",\"cwd\":\"z:/proj\"}}";
        var output = ArchiveService.RewriteCodexSessionMeta(line, "old-id", "new-id");

        using var document = JsonDocument.Parse(output);
        var payload = document.RootElement.GetProperty("payload");
        Assert.AreEqual("new-id", payload.GetProperty("id").GetString());
        Assert.AreEqual("new-id", payload.GetProperty("session_id").GetString());
        Assert.AreEqual("old-id", payload.GetProperty("forked_from_id").GetString());
    }

    [TestMethod]
    public async Task Claude_TemplateSnapshot_DoesNotChangeWhenSourceAppends()
    {
        using var fixture = new TemplateFixture("claude");
        var created = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source);
        Assert.IsTrue(created.Ok, created.Message);
        var before = await File.ReadAllTextAsync(created.Snapshot!.SnapshotPath);

        await File.AppendAllTextAsync(fixture.Source.SourcePath, ClaudeLine(fixture.Source.Id, "u3", "later-source-tip"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(created.Snapshot.SnapshotPath));
        Assert.DoesNotContain("later-source-tip", before);
    }

    [TestMethod]
    public async Task Codex_TemplateSnapshot_DoesNotChangeWhenSourceAppends()
    {
        using var fixture = new TemplateFixture("codex");
        var created = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source);
        Assert.IsTrue(created.Ok, created.Message);
        var before = await File.ReadAllTextAsync(created.Snapshot!.SnapshotPath);

        await File.AppendAllTextAsync(fixture.Source.SourcePath, CodexMessage("later-source-tip"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(created.Snapshot.SnapshotPath));
        Assert.DoesNotContain("later-source-tip", before);
    }

    [TestMethod]
    public async Task SpawnedClaudeChat_UsesSnapshotTip_NotCurrentSourceTip()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        await File.AppendAllTextAsync(fixture.Source.SourcePath, ClaudeLine(fixture.Source.Id, "u3", "current-source-tip"));

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(spawned.Ok, spawned.Message);
        var text = await File.ReadAllTextAsync(spawned.Branch!.SourcePath);
        StringAssert.Contains(text, "snapshot-tip");
        Assert.DoesNotContain("current-source-tip", text);
    }

    [TestMethod]
    public async Task SpawnedCodexChat_UsesSnapshotTip_NotCurrentSourceTip()
    {
        using var fixture = new TemplateFixture("codex");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        await File.AppendAllTextAsync(fixture.Source.SourcePath, CodexMessage("current-source-tip"));

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(spawned.Ok, spawned.Message);
        var text = await File.ReadAllTextAsync(spawned.Branch!.SourcePath);
        StringAssert.Contains(text, "snapshot-tip");
        Assert.DoesNotContain("current-source-tip", text);
    }

    [TestMethod]
    public async Task AppendingToSpawnedClaudeChat_DoesNotModifySnapshot()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);
        var before = await File.ReadAllTextAsync(snapshot.SnapshotPath);

        await File.AppendAllTextAsync(spawned.Branch!.SourcePath, ClaudeLine(spawned.Branch.Id, "spawn-u3", "spawn-append"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(snapshot.SnapshotPath));
    }

    [TestMethod]
    public async Task AppendingToSpawnedCodexChat_DoesNotModifySnapshot()
    {
        using var fixture = new TemplateFixture("codex");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);
        var before = await File.ReadAllTextAsync(snapshot.SnapshotPath);

        await File.AppendAllTextAsync(spawned.Branch!.SourcePath, CodexMessage("spawn-append"));

        Assert.AreEqual(before, await File.ReadAllTextAsync(snapshot.SnapshotPath));
    }

    [TestMethod]
    public async Task TemplateSnapshot_IsAbsentFromSessionsSearchCollectionsProjectionAndAliasResolution()
    {
        using var fixture = new TemplateFixture("claude");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(
            fixture.Source,
            "unique-checkpoint-only-name")).Snapshot!;

        Assert.IsFalse(fixture.Service.Store.Sessions.ContainsKey(snapshot.Id));
        Assert.IsFalse(fixture.Service.Search("unique-checkpoint-only-name").Any());
        Assert.IsFalse(fixture.Service.Store.Collections.Values.Any(collection => collection.SessionIds.Contains(snapshot.Id)));
        Assert.DoesNotContain(snapshot.Id, fixture.Service.BuildProjectsProjectionJson());
        Assert.IsNull(fixture.Service.ResolveSessionByIdOrAlias(snapshot.Id));
    }

    [TestMethod]
    public async Task DeletingTemplate_CannotResurrectAfterDiskSync()
    {
        using var fixture = new TemplateFixture("claude");
        fixture.Service.Store.Settings.Sources =
        [
            new SessionSource { Tool = "claude", Root = fixture.NativeRoot }
        ];
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;

        Assert.IsTrue(await fixture.Service.DeleteTemplateSnapshotAsync(snapshot.Id));
        await fixture.Service.SyncFromDiskAsync();

        Assert.IsFalse(fixture.Service.Store.TemplateSnapshots.ContainsKey(snapshot.Id));
        Assert.IsFalse(File.Exists(snapshot.SnapshotPath));
    }

    [TestMethod]
    public async Task Codex_SnapshotCreation_DoesNotInsertThreadRow()
    {
        using var fixture = new TemplateFixture("codex");
        var before = fixture.ThreadCount();

        var created = await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source);

        Assert.IsTrue(created.Ok, created.Message);
        Assert.AreEqual(before, fixture.ThreadCount());
    }

    [TestMethod]
    public async Task Codex_Spawn_InsertsExactlyOneThreadWithCorrectRolloutPath()
    {
        using var fixture = new TemplateFixture("codex");
        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var before = fixture.ThreadCount();

        var spawned = await fixture.Service.SpawnTemplateAsync(snapshot);

        Assert.IsTrue(spawned.Ok, spawned.Message);
        Assert.AreEqual(before + 1, fixture.ThreadCount());
        Assert.AreEqual(spawned.Branch!.SourcePath, fixture.RolloutPath(spawned.Branch.Id));
    }

    [TestMethod]
    public async Task LegacyMigration_IsIdempotentAcrossRestartAndSaveConflict()
    {
        using var fixture = new TemplateFixture("claude", seedStore: false);
        fixture.Source.IsTemplate = true;
        fixture.Service.Store.Sessions[fixture.Source.Id] = fixture.Source;
        await fixture.Service.SaveAsync();

        var migrator = fixture.NewService();
        var competingWriter = fixture.NewService();
        await migrator.LoadStoreStateAsync();
        await competingWriter.LoadStoreStateAsync();
        competingWriter.Store.Settings.MultiplexApiPort = 8127;
        await competingWriter.SaveAsync();

        await migrator.MigrateLegacyTemplatesAsync();
        var restarted = fixture.NewService();
        await restarted.LoadAsync();

        Assert.AreEqual(1, restarted.TemplateSnapshotsForSource(fixture.Source.Id).Count);
        Assert.IsFalse(restarted.Store.Sessions[fixture.Source.Id].IsTemplate);
        Assert.AreEqual(8127, restarted.Store.Settings.MultiplexApiPort);
    }

    [TestMethod]
    public async Task LegacyMigration_MissingTranscript_DoesNotClearOrHideTemplate()
    {
        using var fixture = new TemplateFixture("claude", seedStore: false);
        fixture.Source.SourcePath = Path.Combine(fixture.Root, "missing.jsonl");
        fixture.Source.IsTemplate = true;
        fixture.Service.Store.Sessions[fixture.Source.Id] = fixture.Source;
        await fixture.Service.SaveAsync();

        var restarted = fixture.NewService();
        await restarted.LoadAsync();

        Assert.IsTrue(restarted.Store.Sessions[fixture.Source.Id].IsTemplate);
        Assert.AreEqual("!", restarted.Store.Sessions[fixture.Source.Id].ListMarks);
        Assert.AreEqual(0, restarted.TemplateSnapshotsForSource(fixture.Source.Id).Count);
    }

    [TestMethod]
    public async Task JsonlClone_DropsIncompleteTailAtCapturedBoundary()
    {
        using var fixture = new TemplateFixture("claude");
        await File.AppendAllTextAsync(fixture.Source.SourcePath, "{\"type\":\"user\",\"sessionId\":\"incomplete");

        var snapshot = (await fixture.Service.CreateTemplateSnapshotAsync(fixture.Source)).Snapshot!;
        var text = await File.ReadAllTextAsync(snapshot.SnapshotPath);

        StringAssert.Contains(text, "snapshot-tip");
        Assert.DoesNotContain("incomplete", text);
        Assert.IsTrue(text.EndsWith("\n", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task BranchSessionAsync_MissingTranscript_FailsCleanly()
    {
        var service = new ArchiveService(useBundledStore: true);
        var parent = new ArchiveSession { Id = "x", Tool = "claude", SourcePath = "z:/does/not/exist.jsonl" };
        var result = await service.BranchSessionAsync(parent);
        Assert.IsFalse(result.Ok);
        Assert.IsNull(result.Branch);
    }

    private static string ClaudeLine(string sessionId, string uuid, string text) =>
        $"{{\"type\":\"user\",\"sessionId\":\"{sessionId}\",\"uuid\":\"{uuid}\",\"message\":{{\"role\":\"user\",\"content\":\"{text}\"}}}}\n";

    private static string CodexMessage(string text) =>
        $"{{\"timestamp\":\"2026-07-16T12:00:00Z\",\"type\":\"event_msg\",\"payload\":{{\"type\":\"user_message\",\"message\":\"{text}\"}}}}\n";

    private sealed class TemplateFixture : IDisposable
    {
        public TemplateFixture(string tool, bool seedStore = true)
        {
            Tool = tool;
            Root = Path.Combine(Path.GetTempPath(), "clr-template-" + Guid.NewGuid().ToString("N"));
            NativeRoot = Path.Combine(Root, tool + "-native");
            TemplatesRoot = Path.Combine(Root, "templates");
            StorePath = Path.Combine(Root, "store.json");
            DbPath = Path.Combine(Root, "state.sqlite");
            Directory.CreateDirectory(NativeRoot);

            var id = tool + "-source-11111111";
            var sourcePath = tool == "claude"
                ? Path.Combine(NativeRoot, id + ".jsonl")
                : Path.Combine(NativeRoot, "2026", "07", "16", $"rollout-2026-07-16T12-00-00-{id}.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(sourcePath)!);
            File.WriteAllText(
                sourcePath,
                tool == "claude"
                    ? ClaudeLine(id, "u1", "first") + ClaudeLine(id, "u2", "snapshot-tip")
                    : $"{{\"timestamp\":\"2026-07-16T12:00:00Z\",\"type\":\"session_meta\",\"payload\":{{\"id\":\"{id}\",\"session_id\":\"{id}\",\"cwd\":\"{Escape(Root)}\"}}}}\n"
                      + CodexMessage("snapshot-tip"));

            Source = new ArchiveSession
            {
                Id = id,
                Tool = tool,
                Title = "Source chat",
                Workspace = Root,
                WorkspaceName = "fixture",
                SourcePath = sourcePath
            };
            if (tool == "codex") CreateCodexDatabase(id, sourcePath);
            Service = NewService();
            if (seedStore) Service.Store.Sessions[id] = Source;
        }

        public string Tool { get; }
        public string Root { get; }
        public string NativeRoot { get; }
        public string TemplatesRoot { get; }
        public string StorePath { get; }
        public string DbPath { get; }
        public ArchiveSession Source { get; }
        public ArchiveService Service { get; }

        public ArchiveService NewService() => new(
            storePath: StorePath,
            codexSessionsRoot: NativeRoot,
            claudeSessionsRoot: NativeRoot,
            codexStateDbPath: DbPath,
            templatesRoot: TemplatesRoot);

        public int ThreadCount()
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "select count(*) from threads";
            return Convert.ToInt32(command.ExecuteScalar());
        }

        public string? RolloutPath(string id)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = "select rollout_path from threads where id = $id";
            command.Parameters.AddWithValue("$id", id);
            return command.ExecuteScalar() as string;
        }

        private void CreateCodexDatabase(string id, string rolloutPath)
        {
            using var connection = OpenDatabase();
            using var command = connection.CreateCommand();
            command.CommandText = """
                create table threads (
                    id text primary key,
                    rollout_path text not null,
                    title text not null,
                    cwd text not null,
                    created_at integer not null,
                    updated_at integer not null,
                    archived integer not null default 0
                );
                insert into threads (id, rollout_path, title, cwd, created_at, updated_at, archived)
                values ($id, $path, 'Source chat', $cwd, 1, 1, 0);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$path", rolloutPath);
            command.Parameters.AddWithValue("$cwd", Root);
            command.ExecuteNonQuery();
        }

        private SqliteConnection OpenDatabase()
        {
            var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString());
            connection.Open();
            return connection;
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); } catch { }
        }

        private static string Escape(string value) => value.Replace("\\", "\\\\");
    }
}
