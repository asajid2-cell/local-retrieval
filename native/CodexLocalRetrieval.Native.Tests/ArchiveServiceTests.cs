using System.Globalization;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;
using Microsoft.Data.Sqlite;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ArchiveServiceTests
{
    private static string WriteRollout(string dir, string fileName, string id, string isoTimestamp, string userText)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        File.WriteAllLines(path, new[]
        {
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"session_meta\",\"payload\":{\"id\":\"" + id + "\",\"type\":\"session_meta\",\"cwd\":\"z:/proj\"}}",
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"" + userText + "\"}}",
            "{\"timestamp\":\"" + isoTimestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"on it\"}}",
        });
        return path;
    }

    // A Codex session must be keyed by its THREAD id (session_meta), not a later rs_... response id.
    // This mis-keying is why CODEX_THREAD_ID self-add never matched the indexed chat.
    [TestMethod]
    public async Task Codex_SessionKeyedByThreadId_NotLaterResponseId()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-codexid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string threadId = "019f0d3e-aaaa-bbbb-cccc-deadbeef0001";
        File.WriteAllLines(Path.Combine(dir, "rollout-2026-06-28T02-00-00-" + threadId + ".jsonl"), new[]
        {
            "{\"timestamp\":\"2026-06-28T02:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + threadId + "\",\"id\":\"" + threadId + "\",\"cwd\":\"z:/proj\"}}",
            "{\"timestamp\":\"2026-06-28T02:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"hi\"}}",
            "{\"timestamp\":\"2026-06-28T02:00:02Z\",\"type\":\"response_item\",\"payload\":{\"id\":\"rs_deadbeefdeadbeef\",\"type\":\"message\",\"role\":\"assistant\",\"content\":\"done\"}}",
        });
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);
            Assert.IsTrue(svc.Store.Sessions.ContainsKey(threadId), "keyed by the session_meta thread id");
            Assert.IsFalse(svc.Store.Sessions.ContainsKey("rs_deadbeefdeadbeef"), "NOT keyed by a later response id");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    [TestMethod]
    public async Task Codex_SessionMetaForkedFromId_IsStoredAsAlias()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-codexalias-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string runtimeId = "019f0d62-b2e5-70a1-8e81-4ff2722ca865";
        const string forkedFrom = "019f0d3e-1d12-7cc3-b892-94652a2f95ee";
        File.WriteAllLines(Path.Combine(dir, "rollout-2026-06-28T02-00-00-" + runtimeId + ".jsonl"), new[]
        {
            "{\"timestamp\":\"2026-06-28T02:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + runtimeId + "\",\"id\":\"" + runtimeId + "\",\"forked_from_id\":\"" + forkedFrom + "\",\"cwd\":\"z:/proj\"}}",
            "{\"timestamp\":\"2026-06-28T02:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"hi\"}}"
        });
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);

            Assert.IsTrue(svc.Store.Sessions.ContainsKey(runtimeId));
            Assert.IsTrue(svc.Store.Sessions[runtimeId].Aliases.Contains(forkedFrom), "forked_from_id should resolve as a strong alias");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    [TestMethod]
    public async Task Codex_EventMessageArray_DoesNotAbortContentLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-codexarray-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        const string threadId = "019f0d62-array-message-test";
        var path = Path.Combine(dir, "rollout-2026-06-28T02-00-00-" + threadId + ".jsonl");
        File.WriteAllLines(path, new[]
        {
            "{\"timestamp\":\"2026-06-28T02:00:00Z\",\"type\":\"session_meta\",\"payload\":{\"session_id\":\"" + threadId + "\",\"id\":\"" + threadId + "\",\"cwd\":\"z:/proj\"}}",
            "{\"timestamp\":\"2026-06-28T02:00:01Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":[{\"type\":\"text\",\"text\":\"array-backed user message\"}]}}",
            "{\"timestamp\":\"2026-06-28T02:00:02Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"array handled\"}}",
        });
        var svc = TempService(out var store);
        try
        {
            await svc.IndexRootAsync(dir);

            var session = svc.Store.Sessions[threadId];
            session.ContentLoaded = false;
            session.Messages.Clear();
            await svc.EnsureContentAsync(session);

            Assert.IsTrue(session.ContentLoaded);
            Assert.IsTrue(session.Messages.Any(m => m.Text.Contains("array-backed user message")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    // L1: indexing the rollout store resurfaces every session on disk, old and new alike.
    [TestMethod]
    public async Task IndexRoot_ResurfacesOldAndNewRollouts()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-idx-" + Guid.NewGuid().ToString("N"));
        WriteRollout(root, "rollout-old.jsonl", "old-1", "2026-01-02T00:00:00Z", "old work from january");
        WriteRollout(root, "rollout-new.jsonl", "new-1", "2026-06-14T00:00:00Z", "recent work");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var indexed = await service.IndexRootAsync(root);

            Assert.AreEqual(2, indexed);
            Assert.IsTrue(service.Store.Sessions.ContainsKey("old-1"), "the months-old january session must resurface");
            Assert.IsTrue(service.Store.Sessions.ContainsKey("new-1"));
        }
        finally { Directory.Delete(root, true); }
    }

    // L1 (machine proof): the real ~/.codex store resurfaces, including chats older than 30 days.
    [TestMethod]
    [TestCategory("RealStore")]
    public async Task SyncFromDisk_ResurfacesRealStoreIncludingMonthsOld()
    {
        if (!Directory.Exists(ArchiveService.DefaultCodexSessionsRoot))
        {
            Assert.Inconclusive("No ~/.codex/sessions on this machine.");
            return;
        }
        var store = Path.Combine(Path.GetTempPath(), "clr-sync-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var service = new ArchiveService(storePath: store);
            var indexed = await service.SyncFromDiskAsync();

            Assert.IsTrue(indexed >= 400, $"expected >=400 on-disk sessions resurfaced, got {indexed}");

            // "Months ago" proof, robust to date drift: the OLDEST codex rollout file on disk must
            // resurface as a session. (We assert on the file, not UpdatedAt, because
            // EnrichTitlesFromLocalState legitimately overrides UpdatedAt with Codex's own index dates.)
            var oldestFile = Directory.EnumerateFiles(ArchiveService.DefaultCodexSessionsRoot, "*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTimeUtc)
                .First();
            var oldestAgeDays = (DateTime.UtcNow - oldestFile.LastWriteTimeUtc).TotalDays;
            Assert.IsTrue(oldestAgeDays >= 30, $"sanity: oldest on-disk chat should be months old (got {oldestAgeDays:F0}d)");
            Assert.IsTrue(
                service.Store.Sessions.Values.Any(s => string.Equals(s.SourcePath, oldestFile.FullName, StringComparison.OrdinalIgnoreCase)),
                $"the oldest on-disk chat ({oldestAgeDays:F0} days old) should resurface");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // L2: a chat resumes via `codex resume <id> --include-non-interactive` at its workspace cwd.
    [TestMethod]
    public void BuildResumeLaunch_TargetsCodexResumeAtWorkspace()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "abc-123", Workspace = cwd, SourcePath = Path.Combine(cwd, "x.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\codex.exe");

        Assert.AreEqual("C:\\codex.exe", launch.Exe);
        // Options before the positional session id so codex's [SESSION_ID] [PROMPT] never mis-parses.
        Assert.AreEqual("resume --include-non-interactive abc-123", launch.Arguments);
        Assert.AreEqual(cwd, launch.WorkingDirectory);
        Assert.AreEqual("\"C:\\codex.exe\" resume --include-non-interactive abc-123", launch.DisplayCommand);
    }

    // Review #4: a crafted/unsafe session id (e.g. a malicious filename) is refused, never injected.
    [TestMethod]
    public void BuildResumeLaunch_RefusesUnsafeSessionId()
    {
        var service = new ArchiveService(useBundledStore: true);
        var bad = service.BuildResumeLaunch(new ArchiveSession { Id = "x & calc", Tool = "codex", Workspace = Path.GetTempPath() });

        Assert.AreEqual("", bad.Exe, "unsafe id must be refused (empty Exe)");
        Assert.IsFalse(ArchiveService.IsResumableId("x & calc.jsonl"));
        Assert.IsFalse(ArchiveService.IsResumableId("a\" & start calc \""));
        Assert.IsTrue(ArchiveService.IsResumableId("019eb8f6-0f43-7f31-a284-f9da9cd5b3fa"));
        Assert.IsTrue(ArchiveService.IsResumableId("rollout-2026-06-15T22-39-19-019eceba"));
    }

    // Review #2: a parser-version migration prunes an orphan whose file now parses to a new id.
    [TestMethod]
    public async Task FullRescan_PrunesOrphanFromOldIdScheme()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-prune-" + Guid.NewGuid().ToString("N"));
        var path = WriteRollout(dir, "rollout-x.jsonl", "newid", "2026-06-14T00:00:00Z", "a chat");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "codex", Root = dir });
            // seed an orphan: same file, but an old-scheme id that the new parse no longer produces
            service.Store.Sessions["oldid"] = new ArchiveSession { Id = "oldid", SourcePath = path, Tool = "codex" };
            service.Store.Settings.IndexVersion = 0; // force a full rescan

            await service.SyncFromDiskAsync(refreshList: false);

            Assert.IsTrue(service.Store.Sessions.ContainsKey("newid"), "the file's current id is present");
            Assert.IsFalse(service.Store.Sessions.ContainsKey("oldid"), "the old-scheme orphan is pruned");
        }
        finally { Directory.Delete(dir, true); }
    }

    // N1: a Claude chat resumes with `claude --resume <id>`, not the codex form.
    [TestMethod]
    public void BuildResumeLaunch_RoutesClaudeSessionsToClaudeCli()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cl-9", Tool = "claude", Workspace = cwd, SourcePath = Path.Combine(cwd, "cl-9.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\claude.exe");

        Assert.AreEqual("C:\\claude.exe", launch.Exe);
        Assert.AreEqual("--resume cl-9", launch.Arguments);
        Assert.AreEqual(cwd, launch.WorkingDirectory);
    }

    // Optional per-tool launch args are inserted in the PREFIX position (after the exe, before the
    // subcommand) so global flags like Codex's --profile apply (codex --profile X resume ...). This is
    // how the app lets you force the stable HTTP/SSE transport without rebuilding the default command.
    [TestMethod]
    public void BuildResumeLaunch_Codex_PrefixesConfiguredLaunchArgs()
    {
        var service = new ArchiveService(useBundledStore: true);
        service.Store.Settings.CodexLaunchArgs = "--profile http_sse";
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cx-7", Tool = "codex", Workspace = cwd, SourcePath = Path.Combine(cwd, "cx-7.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\codex.exe");

        Assert.AreEqual("--profile http_sse resume --include-non-interactive cx-7", launch.Arguments);
        StringAssert.Contains(launch.DisplayCommand, "--profile http_sse resume");
    }

    // Default path must be untouched when no launch args are set.
    [TestMethod]
    public void BuildResumeLaunch_EmptyLaunchArgs_LeavesDefaultPathUntouched()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cx-8", Tool = "codex", Workspace = cwd, SourcePath = Path.Combine(cwd, "cx-8.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\codex.exe");

        Assert.AreEqual("resume --include-non-interactive cx-8", launch.Arguments);
    }

    [TestMethod]
    public void BuildResumeLaunch_Claude_PrefixesClaudeLaunchArgs()
    {
        var service = new ArchiveService(useBundledStore: true);
        service.Store.Settings.ClaudeLaunchArgs = "--mcp-debug";
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cl-7", Tool = "claude", Workspace = cwd, SourcePath = Path.Combine(cwd, "cl-7.jsonl") };

        var launch = service.BuildResumeLaunch(session, exeOverride: "C:\\claude.exe");

        Assert.AreEqual("--mcp-debug --resume cl-7", launch.Arguments);
    }

    // The multiplex resume command runs over SSH-into-Windows where `codex` is the shim that already
    // adds --profile http_sse, so the command must use the BARE tool name and must NOT re-add the
    // local launch-args (a doubled --profile would break codex's arg parser). The cd uses forward
    // slashes so the Windows path survives JSON+shell nesting on the way to the API.
    [TestMethod]
    public void BuildMultiplexCommand_Codex_BareToolForwardSlashCd_NoDoubledProfile()
    {
        var service = new ArchiveService(useBundledStore: true);
        service.Store.Settings.CodexLaunchArgs = "--profile http_sse";
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cx-9", Tool = "codex", Workspace = cwd, SourcePath = Path.Combine(cwd, "cx-9.jsonl") };

        var cmd = service.BuildMultiplexCommand(session);

        Assert.AreEqual($"cd '{cwd.Replace('\\', '/')}'; codex resume --include-non-interactive cx-9", cmd);
        Assert.IsFalse(cmd.Contains("--profile"), "shim adds the profile; the command must not double it");
        Assert.IsFalse(cmd.Contains("\\"), "the cd path must use forward slashes");
    }

    [TestMethod]
    public void BuildMultiplexCommand_EscapesSingleQuoteInCwd()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.Combine(Path.GetTempPath(), "mux quote's cwd " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        try
        {
            var session = new ArchiveSession { Id = "cx-quote", Tool = "codex", Workspace = cwd, SourcePath = Path.Combine(cwd, "cx-quote.jsonl") };

            var cmd = service.BuildMultiplexCommand(session);

            Assert.AreEqual($"cd '{cwd.Replace('\\', '/').Replace("'", "''")}'; codex resume --include-non-interactive cx-quote", cmd);
        }
        finally
        {
            try { Directory.Delete(cwd, recursive: true); } catch { }
        }
    }

    [TestMethod]
    public void BuildMultiplexCommand_Claude_UsesClaudeResume()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cwd = Path.GetTempPath().TrimEnd('\\', '/');
        var session = new ArchiveSession { Id = "cl-9", Tool = "claude", Workspace = cwd, SourcePath = Path.Combine(cwd, "cl-9.jsonl") };

        var cmd = service.BuildMultiplexCommand(session);

        Assert.AreEqual($"cd '{cwd.Replace('\\', '/')}'; claude --resume cl-9", cmd);
    }

    [TestMethod]
    public void BuildMultiplexCommand_RefusesUnsafeSessionId()
    {
        var service = new ArchiveService(useBundledStore: true);
        var cmd = service.BuildMultiplexCommand(new ArchiveSession { Id = "x & calc", Tool = "codex", Workspace = Path.GetTempPath() });
        Assert.AreEqual("", cmd);
    }

    // The tmux/tab name is a legible slug of the title plus a short id tail, so two same-titled chats
    // still get distinct sessions and the phone tab is readable.
    [TestMethod]
    public void MultiplexSessionName_SlugifiesTitleWithIdTail()
    {
        var session = new ArchiveSession { Id = "3b7b7fbc-c196", Tool = "claude", Title = "Cortex Engine AAA Push" };
        var name = ArchiveService.MultiplexSessionName(session);
        Assert.AreEqual("cortex-engine-aaa-push-3b7bc196", name);
    }

    [TestMethod]
    public void MultiplexSessionName_FallsBackToToolAndIdWhenNoTitle()
    {
        var session = new ArchiveSession { Id = "abcd1234", Tool = "codex", Title = "" };
        var name = ArchiveService.MultiplexSessionName(session);
        Assert.AreEqual("cx-abcd1234", name);
    }

    [TestMethod]
    public void MultiplexSessionName_UsesIdSuffixToAvoidUuidV7PrefixCollisions()
    {
        var a = new ArchiveSession
        {
            Id = "019f26ab-7133-7553-8e76-c09eb8ebfa6c",
            Tool = "codex",
            Title = "Continue this archived work from local context"
        };
        var b = new ArchiveSession
        {
            Id = "019f26b2-f740-7aa2-b38f-2a15a659607f",
            Tool = "codex",
            Title = "Continue this archived work from local context"
        };

        Assert.AreEqual("continue-this-archived-work-019ffa6c", ArchiveService.MultiplexSessionName(a));
        Assert.AreEqual("continue-this-archived-work-019f607f", ArchiveService.MultiplexSessionName(b));
        Assert.AreNotEqual(ArchiveService.MultiplexSessionName(a), ArchiveService.MultiplexSessionName(b));
    }

    // The projection the app pushes to the VPS so the web can list projects + resume chats remotely:
    // one entry per collection, each chat carrying a mux session name + a resumable command.
    [TestMethod]
    public void BuildProjectsProjectionJson_EmitsCollectionsWithResumableChats()
    {
        var svc = new ArchiveService(useBundledStore: true);
        var cwd = System.IO.Path.GetTempPath().TrimEnd('\\', '/');
        var s1 = new ArchiveSession { Id = "cl1", Tool = "claude", Title = "Cortex Push", Workspace = cwd, SourcePath = System.IO.Path.Combine(cwd, "cl1.jsonl") };
        var s2 = new ArchiveSession { Id = "cx2", Tool = "codex", Title = "GPU gen", Workspace = cwd, SourcePath = System.IO.Path.Combine(cwd, "cx2.jsonl") };
        svc.Store.Sessions[s1.Id] = s1; svc.Store.Sessions[s2.Id] = s2;
        svc.Store.Collections["col1"] = new ArchiveCollection { Id = "col1", Name = "Cortex Engine", SessionIds = new() { s1.Id, s2.Id } };

        using var doc = System.Text.Json.JsonDocument.Parse(svc.BuildProjectsProjectionJson());
        var root = doc.RootElement;
        Assert.IsTrue(root.GetProperty("host").GetString()!.Length > 0);
        var decks = root.GetProperty("decks");
        Assert.AreEqual(1, decks.GetArrayLength());
        Assert.AreEqual("main", decks[0].GetProperty("id").GetString());
        var cols = root.GetProperty("collections");
        Assert.AreEqual(1, cols.GetArrayLength());
        Assert.AreEqual("Cortex Engine", cols[0].GetProperty("name").GetString());
        Assert.AreEqual("main", cols[0].GetProperty("deckId").GetString());
        Assert.AreEqual("Main", cols[0].GetProperty("deckName").GetString());
        var chats = cols[0].GetProperty("chats");
        Assert.AreEqual(2, chats.GetArrayLength());
        foreach (var ch in chats.EnumerateArray())
        {
            Assert.IsTrue(ch.GetProperty("muxName").GetString()!.Length > 0);
            var cmd = ch.GetProperty("muxCommand").GetString()!;
            StringAssert.StartsWith(cmd, "cd '");
            StringAssert.Contains(cmd, "resume");
            Assert.IsFalse(ch.GetProperty("running").GetBoolean());   // no running set passed
        }
    }

    // runningSessions in the projection: every live agent appears (for the web's "Running on PC" view),
    // enriched with the matched chat's title + collection when its session id OR an alias is known; an
    // uncollected/unknown session still appears with null title/collection. Ordered newest-first.
    [TestMethod]
    public void BuildProjectsProjectionJson_EnrichesRunningSessions()
    {
        var svc = new ArchiveService(useBundledStore: true);
        var cwd = System.IO.Path.GetTempPath().TrimEnd('\\', '/');
        var s1 = new ArchiveSession { Id = "cl1", Tool = "claude", Title = "Cortex Push", Workspace = cwd, SourcePath = System.IO.Path.Combine(cwd, "cl1.jsonl") };
        s1.Aliases.Add("alias-1");
        svc.Store.Sessions[s1.Id] = s1;
        svc.Store.Collections["col1"] = new ArchiveCollection { Id = "col1", Name = "Cortex Engine", SessionIds = new() { s1.Id } };

        var sessions = new[]
        {
            new ArchiveService.RunningSessionInfo(100, "claude", "cl1", "VS Code", "2026-06-29T01:00:00Z", ""),         // matched by id
            new ArchiveService.RunningSessionInfo(200, "claude", "alias-1", "VS Code", "2026-06-29T03:00:00Z", ""),     // matched by alias
            new ArchiveService.RunningSessionInfo(300, "codex", "unknown-xyz", "Terminal", "2026-06-29T02:00:00Z", ""), // uncollected
        };
        using var doc = System.Text.Json.JsonDocument.Parse(svc.BuildProjectsProjectionJson(null, sessions));
        var rs = doc.RootElement.GetProperty("runningSessions");
        Assert.AreEqual(3, rs.GetArrayLength());
        // newest-first by startedAt: alias-1 (03:00) > unknown (02:00) > cl1 (01:00)
        Assert.AreEqual(200, rs[0].GetProperty("pid").GetInt32());
        Assert.AreEqual("Cortex Push", rs[0].GetProperty("title").GetString());
        Assert.AreEqual("Cortex Engine", rs[0].GetProperty("collection").GetString());
        Assert.AreEqual("main", rs[0].GetProperty("collectionDeckId").GetString());
        Assert.AreEqual("Main", rs[0].GetProperty("collectionDeck").GetString());
        Assert.AreEqual(300, rs[1].GetProperty("pid").GetInt32());
        Assert.AreEqual(System.Text.Json.JsonValueKind.Null, rs[1].GetProperty("title").ValueKind);   // uncollected -> null title
        Assert.AreEqual("Terminal", rs[1].GetProperty("parent").GetString());
        Assert.AreEqual(100, rs[2].GetProperty("pid").GetInt32());
        Assert.AreEqual("Cortex Push", rs[2].GetProperty("title").GetString());                       // matched by id
    }

    // The "is this chat already running?" guard reads the resumed session id off a claude/codex process
    // command line (local OR multiplex). Must match BuildMultiplexCommand's shapes and ignore non-resumes.
    [TestMethod]
    public void ParseResumedSessionId_ExtractsClaudeAndCodexIds()
    {
        Assert.AreEqual("3b7b7fbc", ArchiveService.ParseResumedSessionId(@"C:\x\claude.exe --resume 3b7b7fbc"));
        Assert.AreEqual("9f2a", ArchiveService.ParseResumedSessionId(@"codex.exe --profile http_sse resume --include-non-interactive 9f2a"));
        Assert.AreEqual("ab12", ArchiveService.ParseResumedSessionId("codex resume ab12"));
        Assert.AreEqual("", ArchiveService.ParseResumedSessionId(@"C:\x\claude.exe"));   // fresh, not a resume
        Assert.AreEqual("", ArchiveService.ParseResumedSessionId("codex resume"));        // picker, no id
        Assert.AreEqual("", ArchiveService.ParseResumedSessionId(""));
    }

    [TestMethod]
    public void RunningSessions_IgnoresCodexAppServerHelper()
    {
        Assert.IsFalse(CodexLocalRetrieval.Core.Remote.RunningSessions.IsLiveAgentProcess(
            "codex.exe",
            @"""C:\Users\Ahmed\AppData\Local\OpenAI\Codex\bin\msix-26.623.9142.0\codex.exe"" app-server --listen stdio://"));

        Assert.IsTrue(CodexLocalRetrieval.Core.Remote.RunningSessions.IsLiveAgentProcess(
            "codex.exe",
            @"""C:\Users\Ahmed\AppData\Local\OpenAI\Codex\bin\codex.exe"" resume --include-non-interactive 019f26ab-7133-7553-8e76-c09eb8ebfa6c"));

        Assert.IsTrue(CodexLocalRetrieval.Core.Remote.RunningSessions.IsLiveAgentProcess(
            "claude.exe",
            @"""C:\Users\Ahmed\.local\bin\claude.exe"" --resume f4413437-fb58-4002-8643-506f3b30cfde"));
    }

    // REGRESSION (real bug, session 3b7b7fbc "Cortex Engine AAA Push"): Claude files a transcript
    // under the DASH-ENCODED launch dir. This session was launched in …\301 (project folder
    // z--…-301) but its recorded workspace was the subdir …\301\graphics. Resuming from the workspace
    // made `claude --resume` look in z--…-301-graphics and fail "No conversation found with session
    // ID". The launch dir must be recovered from the transcript's project folder, not the workspace.
    [TestMethod]
    public void BuildResumeLaunch_Claude_RecoversLaunchDirFromProjectFolder_NotSubdirWorkspace()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-resume-" + Guid.NewGuid().ToString("N"));
        var launchDir = Path.Combine(root, "301");
        var subDir = Path.Combine(launchDir, "graphics");   // the recorded (wrong-for-resume) workspace
        Directory.CreateDirectory(subDir);

        static string Encode(string p) =>
            p.TrimEnd('\\', '/').Replace('\\', '-').Replace('/', '-').Replace(':', '-').Replace('.', '-').Replace(' ', '-');
        var projDir = Path.Combine(root, "projects", Encode(launchDir));   // mirrors ~/.claude/projects/<encoded-launch-dir>
        Directory.CreateDirectory(projDir);
        var sourcePath = Path.Combine(projDir, "3b7b7fbc-c196-4129-a86a-8d68460b8ca5.jsonl");
        File.WriteAllText(sourcePath, "{}");

        var session = new ArchiveSession
        {
            Id = "3b7b7fbc-c196-4129-a86a-8d68460b8ca5",
            Tool = "claude",
            Workspace = subDir,
            SourcePath = sourcePath,
        };
        var svc = TempService(out var store);
        try
        {
            var launch = svc.BuildResumeLaunch(session, exeOverride: "claude");
            Assert.AreEqual(launchDir, launch.WorkingDirectory,
                "resume must run from the launch dir (whose encoding == project folder), not the subdir workspace");
            Assert.AreEqual("--resume 3b7b7fbc-c196-4129-a86a-8d68460b8ca5", launch.Arguments);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    private static string WriteClaudeSession(string dir, string fileName, string sessionId, string cwd, string isoTs, string userText)
    {
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, fileName);
        var esc = cwd.Replace("\\", "\\\\");
        File.WriteAllLines(path, new[]
        {
            "{\"type\":\"user\",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"" + esc + "\",\"timestamp\":\"" + isoTs + "\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"" + userText + "\"}]}}",
            "{\"type\":\"assistant\",\"sessionId\":\"" + sessionId + "\",\"cwd\":\"" + esc + "\",\"timestamp\":\"" + isoTs + "\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"on it, here is the plan\"}]}}",
        });
        return path;
    }

    // N1/N2: a Claude source is parsed with the Claude shape and tagged tool=claude.
    [TestMethod]
    public async Task Sync_ParsesClaudeSource_AndTagsTool()
    {
        var claudeDir = Path.Combine(Path.GetTempPath(), "clr-claude-" + Guid.NewGuid().ToString("N"));
        WriteClaudeSession(claudeDir, "cl-1.jsonl", "cl-1", "z:\\proj", "2026-06-10T00:00:00Z", "help me ship the claude feature");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(claudeDir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "claude", Root = claudeDir });

            await service.SyncFromDiskAsync(refreshList: false);

            var s = service.Store.Sessions["cl-1"];
            Assert.AreEqual("claude", s.Tool);
            Assert.AreEqual("z:\\proj", s.Workspace);
            Assert.IsTrue(s.Messages.Count >= 2, "both claude messages should parse");
            StringAssert.Contains(s.Text, "ship the claude feature");
        }
        finally { Directory.Delete(claudeDir, true); }
    }

    [TestMethod]
    public async Task Sync_ParsesLargeClaudeSource_FromTailForDisplay()
    {
        var claudeDir = Path.Combine(Path.GetTempPath(), "clr-claude-tail-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(claudeDir);
        var path = Path.Combine(claudeDir, "cl-tail.jsonl");
        using (var writer = new StreamWriter(path))
        {
            writer.WriteLine("{\"type\":\"user\",\"sessionId\":\"cl-tail\",\"cwd\":\"z:\\\\proj\",\"timestamp\":\"2026-06-16T00:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"old stale front content\"}]}}");
            for (var i = 0; i < 18020; i++)
            {
                writer.WriteLine("{\"type\":\"assistant\",\"sessionId\":\"cl-tail\",\"cwd\":\"z:\\\\proj\",\"timestamp\":\"2026-06-16T00:00:01Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"filler " + i + "\"}]}}");
            }
            writer.WriteLine("{\"type\":\"user\",\"sessionId\":\"cl-tail\",\"cwd\":\"z:\\\\proj\",\"timestamp\":\"2026-06-28T10:00:00Z\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"fresh donor-binding tail question\"}]}}");
            writer.WriteLine("{\"type\":\"assistant\",\"sessionId\":\"cl-tail\",\"cwd\":\"z:\\\\proj\",\"timestamp\":\"2026-06-28T10:01:00Z\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"fresh donor-binding tail answer\"}]}}");
        }

        try
        {
            var service = new ArchiveService(storePath: Path.Combine(claudeDir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "claude", Root = claudeDir });

            await service.SyncFromDiskAsync(refreshList: false);

            var s = service.Store.Sessions["cl-tail"];
            StringAssert.Contains(s.Text, "fresh donor-binding tail answer");
            Assert.IsFalse(s.Text.Contains("old stale front content"), "display/search text must come from the live tail, not the stale front cap");
            Assert.AreEqual("2026-06-28T10:01:00Z", s.UpdatedAt);
        }
        finally { Directory.Delete(claudeDir, true); }
    }

    // N3: a second scan with no file changes parses nothing (incremental skip).
    [TestMethod]
    public async Task Scan_IsIncremental_SkipsUnchangedFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-incr-" + Guid.NewGuid().ToString("N"));
        WriteRollout(dir, "rollout-a.jsonl", "a-1", "2026-06-14T00:00:00Z", "first chat");
        WriteRollout(dir, "rollout-b.jsonl", "b-1", "2026-06-14T00:00:01Z", "second chat");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(dir, "store.json"));
            service.Store.Settings.BundledHistoryAbsorbed = true;
            service.Store.Settings.Sources.Add(new SessionSource { Tool = "codex", Root = dir });

            var first = await service.ScanDiskAsync();
            Assert.AreEqual(2, first.Disk.Count, "first scan parses both files");
            await service.MergeScanAsync(first, refreshList: false);

            var second = await service.ScanDiskAsync();
            Assert.AreEqual(0, second.Disk.Count, "second scan must skip unchanged files");
        }
        finally { Directory.Delete(dir, true); }
    }

    // L1 correctness (tandem review HIGH): a re-sync refreshes disk content but must NOT wipe the
    // user's organization (pin / archive / rename / review / tags).
    [TestMethod]
    public async Task MergeScan_RefreshesContentButPreservesOrganization()
    {
        var service = new ArchiveService(storePath: Path.Combine(Path.GetTempPath(), "clr-merge-" + Guid.NewGuid().ToString("N") + ".json"));
        var existing = new ArchiveSession { Id = "s1", Title = "old title", Pinned = true, Archived = true, Reviewed = true, CustomTitle = "My renamed chat" };
        existing.Tags.Add("squeezebox");
        service.Store.Sessions["s1"] = existing;

        var fromDisk = new ArchiveSession { Id = "s1", Title = "fresh title from disk" };
        await service.MergeScanAsync(new DiskScan(new List<ArchiveSession> { fromDisk }, new List<ArchiveSession>()), refreshList: false);

        var merged = service.Store.Sessions["s1"];
        Assert.AreEqual("fresh title from disk", merged.Title, "disk content should refresh");
        Assert.IsTrue(merged.Pinned, "pin must survive a re-sync");
        Assert.IsTrue(merged.Archived, "archive must survive a re-sync");
        Assert.IsTrue(merged.Reviewed, "review must survive a re-sync");
        Assert.AreEqual("My renamed chat", merged.CustomTitle, "rename must survive a re-sync");
        Assert.IsTrue(merged.Tags.Contains("squeezebox"), "user tags must survive a re-sync");
    }

    // L1 correctness (tandem review #5): a live rollout with a partial trailing line still indexes,
    // including the good lines above the partial one.
    [TestMethod]
    public async Task IndexRoot_ToleratesPartialTrailingLine()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-partial-" + Guid.NewGuid().ToString("N"));
        var path = WriteRollout(root, "rollout-live.jsonl", "live-1", "2026-06-15T00:00:00Z", "active session");
        File.AppendAllText(path, "\n{\"timestamp\":\"2026-06-15T00:00:05Z\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_m");
        try
        {
            var service = new ArchiveService(storePath: Path.Combine(root, "store.json"));
            var indexed = await service.IndexRootAsync(root);

            Assert.AreEqual(1, indexed, "the file must still index despite the partial tail line");
            Assert.IsTrue(service.Store.Sessions.ContainsKey("live-1"));
            Assert.IsTrue(service.Store.Sessions["live-1"].Messages.Count >= 1, "good lines above the partial must survive");
        }
        finally { Directory.Delete(root, true); }
    }

    // L5: reading-mode cleanup strips machine-context noise and code, keeps the human text.
    [TestMethod]
    public void ForReading_StripsNoiseKeepsConversation()
    {
        var raw = "# Context from my IDE setup: stuff\n"
                + "<environment_context>os=win</environment_context>\n"
                + "<goal_context>do the thing</goal_context>\n"
                + "Please fix the parser bug.\n\n```cs\nvar x = 1;\n```";

        var cleaned = ArchiveService.ForReading(raw);

        Assert.AreEqual("Please fix the parser bug.", cleaned);
        Assert.IsFalse(cleaned.Contains("environment_context"));
        Assert.IsFalse(cleaned.Contains("var x = 1"));
        Assert.AreEqual("", ArchiveService.ForReading("<environment_context>only noise</environment_context>"));
        // Claude IDE-context tags are stripped too, so titles read like real prompts.
        Assert.AreEqual("Fix the null check in foo.",
            ArchiveService.ForReading("<ide_opened_file>opened foo.cs</ide_opened_file>\nFix the null check in foo."));
    }

    // L3: adding a chat to a project (collection) survives an app restart.
    [TestMethod]
    public async Task AddToCollection_PersistsAcrossReload()
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-coll-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var session = new ArchiveSession { Id = "chat-42", Title = "Resume me later" };
            var writer = new ArchiveService(storePath: store);
            writer.Store.Sessions[session.Id] = session;
            await writer.AddToCollectionAsync(session, "Squeezebox");

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();

            Assert.IsTrue(
                reader.Store.Collections.Values.Any(c => c.Name == "Squeezebox" && c.SessionIds.Contains("chat-42")),
                "collection membership must survive a reload");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Bump (Codex): floats a chat to the top of `codex resume` by writing threads.updated_at_ms = now
    // in state_5.sqlite - no message sent. The picker sorts by updated_at_ms, so this is the lever.
    [TestMethod]
    public void Bump_CodexThread_SetsUpdatedAtMsToNow()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "clr-bumpdb-" + Guid.NewGuid().ToString("N") + ".sqlite");
        const string id = "019f0d3e-aaaa-bbbb-cccc-deadbeef0001";
        const string other = "019f0000-0000-0000-0000-000000000099";
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                conn.Open();
                using var create = conn.CreateCommand();
                create.CommandText = "create table threads (id text primary key, updated_at_ms integer, updated_at integer);" +
                                     "insert into threads values ('" + id + "', 1000, 1);" +
                                     "insert into threads values ('" + other + "', 2000, 2);";
                create.ExecuteNonQuery();
            }

            const long nowMs = 1782639989248;
            var changed = ArchiveService.BumpCodexThreadUpdatedAt(dbPath, new[] { id }, nowMs);
            Assert.AreEqual(1, changed, "exactly the one matching thread is bumped");

            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                conn.Open();
                using var read = conn.CreateCommand();
                read.CommandText = "select updated_at_ms, updated_at from threads where id = $id";
                read.Parameters.AddWithValue("$id", id);
                using var r = read.ExecuteReader();
                Assert.IsTrue(r.Read());
                Assert.AreEqual(nowMs, r.GetInt64(0), "updated_at_ms is set to now (millis)");
                Assert.AreEqual(nowMs / 1000, r.GetInt64(1), "updated_at is set to now (seconds)");

                using var read2 = conn.CreateCommand();
                read2.CommandText = "select updated_at_ms from threads where id = '" + other + "'";
                Assert.AreEqual(2000L, (long)read2.ExecuteScalar()!, "the sibling thread is untouched");
            }
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    // Bump (Codex): an unknown id changes nothing - never bumps a different chat.
    [TestMethod]
    public void Bump_CodexThread_UnknownId_ChangesNothing()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "clr-bumpdb-" + Guid.NewGuid().ToString("N") + ".sqlite");
        try
        {
            using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = dbPath }.ToString()))
            {
                conn.Open();
                using var create = conn.CreateCommand();
                create.CommandText = "create table threads (id text primary key, updated_at_ms integer, updated_at integer);" +
                                     "insert into threads values ('real', 1000, 1);";
                create.ExecuteNonQuery();
            }
            var changed = ArchiveService.BumpCodexThreadUpdatedAt(dbPath, new[] { "does-not-exist" }, 9999);
            Assert.AreEqual(0, changed, "no thread matched, nothing bumped");
        }
        finally { SqliteConnection.ClearAllPools(); if (File.Exists(dbPath)) File.Delete(dbPath); }
    }

    // Bump (Claude): floats a chat in Claude's recent list by touching the transcript file mtime
    // (the picker sorts transcripts by mtime), and floats it in this app's list too.
    [TestMethod]
    public async Task Bump_ClaudeSession_TouchesTranscriptMtimeAndOurOrder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-bumpclaude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, "session.jsonl");
        File.WriteAllText(transcript, "{}");
        var oldStamp = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(transcript, oldStamp);

        var svc = TempService(out var store);
        try
        {
            var session = new ArchiveSession { Id = "claude-1", Tool = "claude", SourcePath = transcript, UpdatedAt = "2020-01-01T00:00:00.0000000Z" };
            svc.Store.Sessions[session.Id] = session;

            var (native, recovered) = await svc.BumpSessionAsync(session);

            Assert.IsTrue(native, "the transcript file existed, so the native mtime was touched");
            Assert.IsFalse(recovered, "a plain transcript has no SDK entrypoint, so nothing was recovered");
            Assert.IsTrue(File.GetLastWriteTimeUtc(transcript) > oldStamp.AddHours(1), "transcript mtime moved to ~now");
            Assert.IsTrue(DateTime.Parse(session.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) > DateTime.UtcNow.AddMinutes(-5), "our list order is bumped too");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    // ★ Recover (Claude): a chat created by the Agent SDK carries entrypoint "sdk-cli", which claude.exe
    // filters OUT of `claude --resume` (function BTs). Bumping mtime alone is invisible for such a chat.
    // The bump must REWRITE the entrypoint to "cli" so the fallen-away chat re-appears. Regression guard.
    [TestMethod]
    public async Task Bump_ClaudeSdkSession_RewritesEntrypointSoItResurfaces()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-recover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, "sdk-session.jsonl");
        // Line 1 is a summary/meta line with NO entrypoint (as real continued sessions start), so the
        // detector must scan past it into the head — not just check line 1. Lines 2-3 carry sdk-cli.
        File.WriteAllText(transcript,
            "{\"type\":\"summary\",\"summary\":\"prior\",\"leafUuid\":\"x\"}\n" +
            "{\"type\":\"user\",\"entrypoint\":\"sdk-cli\",\"message\":{\"content\":\"hi\"}}\n" +
            "{\"type\":\"assistant\",\"entrypoint\":\"sdk-cli\",\"aiTitle\":\"My chat\"}\n");

        var svc = TempService(out var store);
        try
        {
            Assert.IsTrue(ArchiveService.IsHiddenClaudeEntrypoint(transcript), "an sdk-cli transcript is hidden from the picker");

            var session = new ArchiveSession { Id = "claude-sdk-1", Tool = "claude", SourcePath = transcript, UpdatedAt = "2020-01-01T00:00:00.0000000Z" };
            svc.Store.Sessions[session.Id] = session;

            var (native, recovered) = await svc.BumpSessionAsync(session);

            Assert.IsTrue(native, "transcript existed → mtime bumped");
            Assert.IsTrue(recovered, "the sdk-cli entrypoint was rewritten so the chat resurfaces");
            var text = File.ReadAllText(transcript);
            Assert.IsFalse(text.Contains("sdk-cli"), "no sdk-cli entrypoint remains");
            Assert.IsTrue(text.Contains("\"entrypoint\":\"cli\""), "entrypoint rewritten to the interactive value");
            Assert.IsTrue(text.Contains("\"aiTitle\":\"My chat\""), "the rest of the transcript is preserved");
            Assert.IsFalse(ArchiveService.IsHiddenClaudeEntrypoint(transcript), "after recovery the chat is no longer hidden");

            // Idempotent: a second bump of an already-visible chat doesn't report another recovery.
            var (_, recovered2) = await svc.BumpSessionAsync(session);
            Assert.IsFalse(recovered2, "already-visible chat needs no further recovery");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    // ★ Codex bump: `codex resume` orders the rollout FILES by mtime (NOT threads.updated_at_ms — verified
    // by driving the real picker), so the bump MUST touch the rollout file. Regression guard for the fix.
    [TestMethod]
    public async Task Bump_CodexSession_TouchesRolloutFileMtime()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-bumpcodex-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var rollout = Path.Combine(dir, "rollout-2026-01-01T00-00-00-codex-1.jsonl");
        File.WriteAllText(rollout, "{}");
        var oldStamp = DateTime.UtcNow.AddHours(-5);
        File.SetLastWriteTimeUtc(rollout, oldStamp);

        var svc = TempService(out var store);
        try
        {
            var session = new ArchiveSession { Id = "codex-1", Tool = "codex", SourcePath = rollout, UpdatedAt = "2020-01-01T00:00:00.0000000Z" };
            svc.Store.Sessions[session.Id] = session;

            var (native, _) = await svc.BumpSessionAsync(session);

            Assert.IsTrue(native, "the rollout file existed, so its mtime was touched");
            Assert.IsTrue(File.GetLastWriteTimeUtc(rollout) > oldStamp.AddHours(1), "rollout mtime moved to ~now — that's what codex resume actually sorts by");
            Assert.IsTrue(DateTime.Parse(session.UpdatedAt, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal) > DateTime.UtcNow.AddMinutes(-5), "our list order bumped too");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            if (File.Exists(store)) File.Delete(store);
        }
    }

    // Native rename (Claude): writes a `custom-title` record to the transcript (the first title Claude's
    // resume picker reads) and updates the in-app native Title. App-only CustomTitle is untouched.
    [TestMethod]
    public async Task RenameNative_ClaudeSession_AppendsCustomTitleAndUpdatesTitle()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-rn-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, "session.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\",\"entrypoint\":\"cli\",\"message\":{\"content\":\"hi\"}}\n");
        var svc = TempService(out var store);
        try
        {
            var session = new ArchiveSession { Id = "claude-rn-1", Tool = "claude", SourcePath = transcript, Title = "old native", CustomTitle = "app name" };
            svc.Store.Sessions[session.Id] = session;

            var status = await svc.RenameNativeAsync(session, "Renamed In Claude");

            var text = File.ReadAllText(transcript);
            Assert.IsTrue(text.Contains("\"type\":\"custom-title\""), "a custom-title record was appended");
            Assert.IsTrue(text.Contains("\"customTitle\":\"Renamed In Claude\""), "the new title is in the record");
            Assert.AreEqual("Renamed In Claude", session.Title, "the in-app native Title is updated");
            Assert.AreEqual("app name", session.CustomTitle, "the app-only CustomTitle is NOT touched by a native rename");
            Assert.IsTrue((status ?? "").Contains("Claude", StringComparison.OrdinalIgnoreCase), "status mentions Claude");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); if (File.Exists(store)) File.Delete(store); }
    }

    // RenameNativeById (the remote/web path): a running session not in the archive still renames by id —
    // here, an indexed session is found and renamed in place.
    [TestMethod]
    public async Task RenameNativeById_IndexedClaude_RenamesInPlace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-rnid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var transcript = Path.Combine(dir, "x.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}\n");
        var svc = TempService(out var store);
        try
        {
            var session = new ArchiveSession { Id = "rnid-1", Tool = "claude", SourcePath = transcript, Title = "before" };
            svc.Store.Sessions[session.Id] = session;

            var status = await svc.RenameNativeByIdAsync("claude", "rnid-1", "After Remote");

            Assert.AreEqual("After Remote", session.Title);
            Assert.IsTrue(File.ReadAllText(transcript).Contains("\"customTitle\":\"After Remote\""));
            Assert.IsFalse((status ?? "failed").Contains("failed", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); if (File.Exists(store)) File.Delete(store); }
    }

    [TestMethod]
    public async Task RenameNativeById_MissingArgs_ReturnsNeedsHint()
    {
        var svc = TempService(out var store);
        try
        {
            var s1 = await svc.RenameNativeByIdAsync("claude", "", "title");
            var s2 = await svc.RenameNativeByIdAsync("claude", "id", "");
            Assert.IsTrue((s1 ?? "").Contains("needs", StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue((s2 ?? "").Contains("needs", StringComparison.OrdinalIgnoreCase));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // ★ CLAUDE (the real path): the new chat is identified by the transcript that APPEARS in the cwd's
    // project folder after launch (folder-diff vs the KnownIds snapshot) — robust even when the cwd is the
    // shared 301 root and regardless of how Workspace parses. Uses the real ~/.claude/projects with a
    // unique temp cwd (no collision) and cleans up.
    [TestMethod]
    public async Task PendingNewChat_Claude_FilesNewTranscriptByFolderDiff()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "clrpend" + Guid.NewGuid().ToString("N").Substring(0, 12));
        var encoded = System.Text.RegularExpressions.Regex.Replace(cwd.TrimEnd('\\', '/'), @"[\\/:.\s]", "-");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", encoded);
        var svc = TempService(out var store);
        try
        {
            Directory.CreateDirectory(folder);   // empty at queue time
            var col = await svc.CreateCollectionAsync("FolderDiff Proj");
            await svc.QueuePendingNewChatAsync("claude", cwd, col.Id);
            Assert.AreEqual(0, svc.Store.PendingNewChats[0].KnownIds.Count, "no transcripts existed at launch");

            var newId = Guid.NewGuid().ToString();   // a new chat appears in the folder
            File.WriteAllText(Path.Combine(folder, newId + ".jsonl"), "{\"type\":\"user\",\"message\":{\"content\":\"hi\"}}\n");

            var filed = await svc.ReconcilePendingNewChatsAsync();
            Assert.IsTrue(filed, "reconcile filed the new transcript");
            Assert.IsTrue(svc.Store.Collections[col.Id].SessionIds.Contains(newId), "the NEW transcript id was filed into the collection");
            Assert.AreEqual(0, svc.Store.PendingNewChats.Count, "pending consumed");
        }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } if (File.Exists(store)) File.Delete(store); }
    }

    // A transcript that ALREADY existed at launch (in the snapshot) is NOT filed — only a genuinely new one.
    [TestMethod]
    public async Task PendingNewChat_Claude_IgnoresPreExistingTranscripts()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "clrpend" + Guid.NewGuid().ToString("N").Substring(0, 12));
        var encoded = System.Text.RegularExpressions.Regex.Replace(cwd.TrimEnd('\\', '/'), @"[\\/:.\s]", "-");
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects", encoded);
        var svc = TempService(out var store);
        try
        {
            Directory.CreateDirectory(folder);
            var oldId = Guid.NewGuid().ToString();   // a transcript that exists BEFORE the start
            File.WriteAllText(Path.Combine(folder, oldId + ".jsonl"), "{}\n");
            var col = await svc.CreateCollectionAsync("Proj");
            await svc.QueuePendingNewChatAsync("claude", cwd, col.Id);
            Assert.AreEqual(1, svc.Store.PendingNewChats[0].KnownIds.Count, "the pre-existing transcript is in the snapshot");

            var filed = await svc.ReconcilePendingNewChatsAsync();
            Assert.IsFalse(filed, "nothing new appeared -> nothing filed");
            Assert.IsFalse(svc.Store.Collections[col.Id].SessionIds.Contains(oldId), "the pre-existing chat is NOT filed");
            Assert.AreEqual(1, svc.Store.PendingNewChats.Count, "pending keeps waiting");
        }
        finally { try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch { } if (File.Exists(store)) File.Delete(store); }
    }

    // CODEX path (no per-cwd folder): matched by tool + normalized workspace, created after launch.
    [TestMethod]
    public async Task PendingNewChat_Codex_FilesByCwdOnIndex()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "clr-cwd-" + Guid.NewGuid().ToString("N"));
        var svc = TempService(out var store);
        try
        {
            var col = await svc.CreateCollectionAsync("Codex Proj");
            await svc.QueuePendingNewChatAsync("codex", cwd, col.Id);
            var fresh = new ArchiveSession { Id = "cx-1", Tool = "codex", Workspace = cwd, CreatedAt = DateTime.UtcNow.ToString("O") };
            await svc.MergeScanAsync(new DiskScan(new List<ArchiveSession> { fresh }, new List<ArchiveSession>()), refreshList: false);

            Assert.IsTrue(svc.Store.Collections[col.Id].SessionIds.Contains("cx-1"), "codex new chat filed by cwd");
            Assert.AreEqual(0, svc.Store.PendingNewChats.Count, "pending consumed");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A codex pending must NOT grab a session in a different folder.
    [TestMethod]
    public async Task PendingNewChat_Codex_DoesNotFileOtherFolder()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "clr-cwd-" + Guid.NewGuid().ToString("N"));
        var other = Path.Combine(Path.GetTempPath(), "clr-other-" + Guid.NewGuid().ToString("N"));
        var svc = TempService(out var store);
        try
        {
            var col = await svc.CreateCollectionAsync("Proj");
            await svc.QueuePendingNewChatAsync("codex", cwd, col.Id);
            var elsewhere = new ArchiveSession { Id = "else-1", Tool = "codex", Workspace = other, CreatedAt = DateTime.UtcNow.ToString("O") };
            await svc.MergeScanAsync(new DiskScan(new List<ArchiveSession> { elsewhere }, new List<ArchiveSession>()), refreshList: false);

            Assert.IsFalse(svc.Store.Collections[col.Id].SessionIds.Contains("else-1"), "a session in another folder is not filed");
            Assert.AreEqual(1, svc.Store.PendingNewChats.Count, "the pending intent keeps waiting");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    [TestMethod]
    public async Task PendingNewChat_SaveReload_PreservesIntent()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "clr-pending-" + Guid.NewGuid().ToString("N"));
        var svc = TempService(out var store);
        try
        {
            var col = await svc.CreateCollectionAsync("Persist Me");
            await svc.QueuePendingNewChatAsync("codex", cwd, col.Id);

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();

            Assert.AreEqual(1, reader.Store.PendingNewChats.Count, "pending start-chat intents must survive SaveAsync");
            Assert.AreEqual("codex", reader.Store.PendingNewChats[0].Tool);
            Assert.AreEqual(cwd, reader.Store.PendingNewChats[0].Cwd);
            Assert.AreEqual(col.Id, reader.Store.PendingNewChats[0].CollectionId);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    [TestMethod]
    public void BuildStartLaunch_Shell_OpensPlainTerminalInCwd()
    {
        var svc = TempService(out var store);
        try
        {
            var cwd = Path.GetTempPath();
            var launch = svc.BuildStartLaunch("shell", cwd);
            Assert.AreEqual("cmd.exe", launch.Exe);
            Assert.AreEqual("", launch.DisplayCommand, "a shell has no agent command");
            Assert.AreEqual(cwd.TrimEnd('\\'), launch.WorkingDirectory.TrimEnd('\\'));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    private static ArchiveService TempService(out string store)
    {
        store = Path.Combine(Path.GetTempPath(), "clr-agent-" + Guid.NewGuid().ToString("N") + ".json");
        return new ArchiveService(storePath: store);
    }

    // The control panel can create an empty collection (no chat needed); creating the same name
    // twice (by slug) returns the existing one so a later agent self-file lands in the same place.
    [TestMethod]
    public async Task CreateCollection_MakesEmptyAndDedupesBySlug()
    {
        var svc = TempService(out var store);
        try
        {
            var a = await svc.CreateCollectionAsync("Renderer Work");
            Assert.IsTrue(svc.Store.Collections.ContainsKey(a.Id), "the empty collection exists");
            Assert.AreEqual(0, a.SessionIds.Count, "it starts with no chats");

            // Same display name (same slug) -> same collection, not a duplicate.
            var b = await svc.CreateCollectionAsync("renderer work");
            Assert.AreEqual(a.Id, b.Id, "same slug returns the same collection");
            Assert.AreEqual(1, svc.Store.Collections.Count, "no duplicate collection is created");

            // An agent self-filing into the same name lands in that collection.
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Renderer Work");
            Assert.AreEqual(1, svc.Store.Collections.Count, "still one collection");
            Assert.IsTrue(svc.Store.Collections[a.Id].SessionIds.Contains("s1"), "the chat joins the existing collection");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // "self" must resolve to the chat that's LIVE (its transcript was just written), not the chat
    // with the newest in-transcript timestamp - that mismatch filed a random sibling before.
    [TestMethod]
    public void ResolveSelf_PicksMostRecentlyWrittenTranscript_NotNewestTimestamp()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var live = Path.Combine(root, "live.jsonl");
        var old = Path.Combine(root, "old.jsonl");
        File.WriteAllText(live, "{}");
        File.WriteAllText(old, "{}");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));   // sibling's file is old on disk...
        File.SetLastWriteTimeUtc(live, DateTime.UtcNow);              // ...the live chat is being written now

        var svc = TempService(out var store);
        try
        {
            const string ws = @"C:\proj\app";
            // The live chat has a STALE parsed timestamp; the sibling has a NEWER one. Old code picked the sibling.
            svc.Store.Sessions["live"] = new ArchiveSession { Id = "live", Workspace = ws, SourcePath = live, Tool = "claude", UpdatedAt = "2026-01-01T00:00:00Z" };
            svc.Store.Sessions["old"] = new ArchiveSession { Id = "old", Workspace = ws, SourcePath = old, Tool = "claude", UpdatedAt = "2026-06-27T00:00:00Z" };

            var target = svc.ResolveTargetSession(new AgentCommand { op = "addToCollection", project = "X", target = "self", tool = "claude", cwd = ws });

            Assert.IsNotNull(target);
            Assert.AreEqual("live", target!.Id, "resolves to the freshly-written transcript, not the newest timestamp");
        }
        finally
        {
            if (File.Exists(store)) File.Delete(store);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // Deleting a project removes only the grouping; the chats stay in the archive.
    [TestMethod]
    public async Task RemoveCollection_DropsGroupingKeepsChats()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Temp");
            var id = svc.Store.Collections.Keys.First();

            await svc.RemoveCollectionAsync(id);

            Assert.IsFalse(svc.Store.Collections.ContainsKey(id), "the collection is removed");
            Assert.IsTrue(svc.Store.Sessions.ContainsKey("s1"), "the chat itself is kept");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A deleted collection lands in Recently Deleted and can be restored exactly as it was.
    [TestMethod]
    public async Task DeleteCollection_GoesToRecentlyDeleted_AndRestores()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Renderer");
            var id = svc.Store.Collections.Keys.First();

            await svc.RemoveCollectionAsync(id);
            Assert.IsFalse(svc.Store.Collections.ContainsKey(id), "removed from active");
            Assert.AreEqual(1, svc.Store.DeletedCollections.Count, "moved to recently deleted");

            await svc.RestoreDeletedCollectionAsync(id);
            Assert.IsTrue(svc.Store.Collections.ContainsKey(id), "restored to active");
            Assert.AreEqual(0, svc.Store.DeletedCollections.Count, "no longer in recently deleted");
            Assert.IsTrue(svc.Store.Collections[id].SessionIds.Contains("s1"), "its chats came back");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Export then import rebuilds collections from a lightweight backup; re-import is non-destructive.
    [TestMethod]
    public async Task ExportImport_RoundTripsCollections()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            svc.Store.Sessions["s2"] = new ArchiveSession { Id = "s2" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Alpha");
            await svc.AddToCollectionAsync(svc.Store.Sessions["s2"], "Beta");
            var json = svc.ExportCollectionsJson();

            svc.Store.Collections.Clear();   // simulate accidental loss / a fresh machine

            var added = await svc.ImportCollectionsJsonAsync(json);
            Assert.AreEqual(2, added, "both collections restored from backup");
            Assert.IsTrue(svc.Store.Collections.Values.Any(c => c.Name == "Alpha" && c.SessionIds.Contains("s1")));
            Assert.IsTrue(svc.Store.Collections.Values.Any(c => c.Name == "Beta" && c.SessionIds.Contains("s2")));

            var again = await svc.ImportCollectionsJsonAsync(json);
            Assert.AreEqual(0, again, "re-importing an existing backup adds nothing");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A malformed backup file is rejected, never partially applied.
    [TestMethod]
    public async Task ImportCollections_RejectsInvalidJson()
    {
        var svc = TempService(out var store);
        try
        {
            Assert.AreEqual(-1, await svc.ImportCollectionsJsonAsync("this is not json"));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // ---- Tagging & filtering -----------------------------------------------------------------

    // Chat tags add/remove, normalize (trim + collapse whitespace), dedupe case-insensitively, and
    // refuse the reserved auto-tags so user tags stay clean.
    [TestMethod]
    public async Task ChatTags_AddRemoveNormalizeDedupeAndReserved()
    {
        var svc = TempService(out var store);
        try
        {
            var s = new ArchiveSession { Id = "s" };
            svc.Store.Sessions["s"] = s;

            Assert.IsTrue(await svc.AddChatTagAsync(s, "  Job   Search "), "normalized + added");
            Assert.IsFalse(await svc.AddChatTagAsync(s, "job search"), "case/space-insensitive duplicate rejected");
            Assert.IsFalse(await svc.AddChatTagAsync(s, "archive"), "reserved tag rejected");
            Assert.IsFalse(await svc.AddChatTagAsync(s, "   "), "blank rejected");

            CollectionAssert.AreEqual(new[] { "Job Search" }, ArchiveService.UserTags(s).ToArray());

            Assert.IsTrue(await svc.RemoveChatTagAsync(s, "JOB SEARCH"), "removed case-insensitively");
            Assert.AreEqual(0, ArchiveService.UserTags(s).Count);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // UserTags hides the reserved auto-tags (archive/code) that the indexer attaches.
    [TestMethod]
    public void UserTags_HidesReservedAutoTags()
    {
        var s = new ArchiveSession { Id = "s" };
        s.Tags.Add("archive"); s.Tags.Add("code"); s.Tags.Add("renderer");
        CollectionAssert.AreEqual(new[] { "renderer" }, ArchiveService.UserTags(s).ToArray());
    }

    // Filtering by tag returns only visible chats carrying a selected tag (ANY-match), archived excluded.
    [TestMethod]
    public void Filter_ByTag_ReturnsOnlyMatchingVisibleChats()
    {
        var svc = TempService(out var store);
        try
        {
            var a = new ArchiveSession { Id = "a", UpdatedAt = "2026-06-01T00:00:00Z" };
            a.Tags.Add("bug");
            var b = new ArchiveSession { Id = "b", UpdatedAt = "2026-06-02T00:00:00Z" };
            b.Tags.Add("idea");
            var c = new ArchiveSession { Id = "c", UpdatedAt = "2026-06-03T00:00:00Z", Archived = true };
            c.Tags.Add("bug");
            svc.Store.Sessions["a"] = a; svc.Store.Sessions["b"] = b; svc.Store.Sessions["c"] = c;

            var bugs = svc.Filter("", new[] { "bug" });
            Assert.AreEqual(1, bugs.Count, "only the visible bug chat (archived excluded)");
            Assert.AreEqual("a", bugs[0].Id);

            var either = svc.Filter("", new[] { "bug", "idea" });
            Assert.AreEqual(2, either.Count, "ANY-match returns both visible tagged chats");

            Assert.AreEqual(2, svc.Filter("", System.Array.Empty<string>()).Count, "no tags -> all visible");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // The compound filter: include + exclude + match-all. The user's case: "active chats that aren't cpp".
    [TestMethod]
    public void FilterChats_IncludeExcludeAndMatchAll()
    {
        var svc = TempService(out var store);
        try
        {
            ArchiveSession Mk(string id, params string[] tags) { var s = new ArchiveSession { Id = id, UpdatedAt = "2026-06-01T00:00:00Z" }; foreach (var t in tags) s.Tags.Add(t); svc.Store.Sessions[id] = s; return s; }
            Mk("x", "active", "cpp");           // active but cpp -> excluded
            Mk("y", "active", "rust");          // active, not cpp -> kept
            Mk("z", "idea");                    // not active
            Mk("w", "active", "rust", "urgent");// active, not cpp, also urgent

            // "active chats that aren't cpp"
            var r = svc.FilterChats(new ChatFilter { IncludeTags = { "active" }, ExcludeTags = { "cpp" } });
            CollectionAssert.AreEquivalent(new[] { "y", "w" }, r.Select(s => s.Id).ToArray());

            // match-ANY include: active OR idea
            var any = svc.FilterChats(new ChatFilter { IncludeTags = { "active", "idea" } });
            Assert.AreEqual(4, any.Count);

            // match-ALL include: active AND urgent
            var all = svc.FilterChats(new ChatFilter { IncludeTags = { "active", "urgent" }, MatchAllIncludes = true });
            CollectionAssert.AreEquivalent(new[] { "w" }, all.Select(s => s.Id).ToArray());

            // exclude-only
            var notCpp = svc.FilterChats(new ChatFilter { ExcludeTags = { "cpp" } });
            CollectionAssert.AreEquivalent(new[] { "y", "z", "w" }, notCpp.Select(s => s.Id).ToArray());
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Collections filter by their own tags, compound: "active + graphics, not web-dev".
    [TestMethod]
    public async Task FilterCollections_IncludeExcludeMatchAll()
    {
        var svc = TempService(out var store);
        try
        {
            async Task<ArchiveCollection> Mk(string name, params string[] tags)
            {
                var s = new ArchiveSession { Id = name + "-s" }; svc.Store.Sessions[s.Id] = s;
                await svc.AddToCollectionAsync(s, name);
                var c = svc.Store.Collections.Values.First(x => x.Name == name);
                foreach (var t in tags) await svc.AddCollectionTagAsync(c.Id, t);
                return c;
            }
            await Mk("Cortex", "active", "graphics");
            await Mk("WebApp", "active", "graphics", "web-dev");
            await Mk("Idle", "graphics");

            // active AND graphics, NOT web-dev
            var r = svc.FilterCollections(new[] { "active", "graphics" }, new[] { "web-dev" }, matchAll: true);
            CollectionAssert.AreEquivalent(new[] { "Cortex" }, r.Select(c => c.Name).ToArray());

            // ANY active or graphics, minus web-dev
            var any = svc.FilterCollections(new[] { "active", "graphics" }, new[] { "web-dev" }, matchAll: false);
            CollectionAssert.AreEquivalent(new[] { "Cortex", "Idle" }, any.Select(c => c.Name).ToArray());
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Decks: a Main deck is ensured; create scopes collections per deck; delete reparents to Main.
    [TestMethod]
    public async Task Decks_CreateScopeAndDelete()
    {
        var svc = TempService(out var store);
        try
        {
            svc.EnsureDecks();
            Assert.IsTrue(svc.Decks.Any(d => d.Id == "main"), "a Main deck is ensured");
            Assert.AreEqual("main", svc.ActiveDeckId);

            var ctx = await svc.CreateDeckAsync("Context Archive");
            Assert.AreNotEqual("main", ctx.Id);

            // Same collection NAME on two different decks = two distinct collections.
            var s1 = new ArchiveSession { Id = "s1" }; var s2 = new ArchiveSession { Id = "s2" };
            svc.Store.Sessions["s1"] = s1; svc.Store.Sessions["s2"] = s2;
            await svc.AddToCollectionAsync(s1, "Active", "main");
            await svc.AddToCollectionAsync(s2, "Active", ctx.Id);
            Assert.AreEqual(2, svc.Store.Collections.Values.Count(c => c.Name == "Active"), "same name on two decks => two collections");
            Assert.AreEqual(1, svc.CollectionsInDeck("main").Count(c => c.Name == "Active"));
            Assert.AreEqual(1, svc.CollectionsInDeck(ctx.Id).Count(c => c.Name == "Active"));

            // Delete the deck -> its collections move to Main; Main can't be deleted.
            await svc.DeleteDeckAsync(ctx.Id);
            Assert.IsFalse(svc.Decks.Any(d => d.Id == ctx.Id), "deck removed");
            Assert.AreEqual(2, svc.CollectionsInDeck("main").Count(c => c.Name == "Active"), "orphaned collection reparented to Main");
            await svc.DeleteDeckAsync("main");
            Assert.IsTrue(svc.Decks.Any(d => d.Id == "main"), "Main is never deleted");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // The agent files itself into a project on the deck named in the command; persists across reload.
    [TestMethod]
    public async Task Decks_AgentFilesIntoNamedDeck()
    {
        var svc = TempService(out var store);
        try
        {
            svc.EnsureDecks();
            var ctx = await svc.CreateDeckAsync("Context");
            svc.Store.Sessions["mine"] = new ArchiveSession { Id = "mine", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSelfToProject", project = "Archive", deck = "Context", id = "mine", tool = "codex" });
            Assert.IsTrue(r.Ok, r.Message);
            var col = svc.CollectionsInDeck(ctx.Id).FirstOrDefault(c => c.Name == "Archive");
            Assert.IsNotNull(col, "project created on the named deck");
            Assert.IsTrue(col!.SessionIds.Contains("mine"));
            Assert.IsFalse(svc.CollectionsInDeck("main").Any(c => c.Name == "Archive"), "not on Main");

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();
            Assert.IsTrue(reader.CollectionsInDeck(ctx.Id).Any(c => c.Name == "Archive"), "deck membership persists");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Layer rules group a collection's chats (active up, context down) over the manual order; persists.
    [TestMethod]
    public async Task TagLayers_GroupChatsAndPersist()
    {
        var svc = TempService(out var store);
        try
        {
            var a = new ArchiveSession { Id = "a", UpdatedAt = "2026-06-01T00:00:00Z" }; a.Tags.Add("active");
            var b = new ArchiveSession { Id = "b", UpdatedAt = "2026-06-05T00:00:00Z" }; b.Tags.Add("context"); // newest but context
            var c = new ArchiveSession { Id = "c", UpdatedAt = "2026-06-03T00:00:00Z" };                       // plain
            svc.Store.Sessions["a"] = a; svc.Store.Sessions["b"] = b; svc.Store.Sessions["c"] = c;
            await svc.AddToCollectionAsync(a, "P"); await svc.AddToCollectionAsync(b, "P"); await svc.AddToCollectionAsync(c, "P");
            var col = svc.Store.Collections.Values.First(x => x.Name == "P");

            // No layers: manual order (a, b, c, the add order).
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, svc.OrderCollectionChats(col, new[] { a, b, c }).Select(s => s.Id).ToArray());

            await svc.SetTagLayerAsync("active", 1);     // active -> top
            await svc.SetTagLayerAsync("context", 900);  // context -> bottom
            Assert.AreEqual(1, svc.TagLayer("ACTIVE"), "layer lookup is case-insensitive");
            Assert.AreEqual(900, svc.SessionLayer(b), "a context chat sinks even though context's layer is above default");
            // active(a) floats up, context(b) sinks, c default in the middle: a, c, b.
            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, svc.OrderCollectionChats(col, new[] { a, b, c }).Select(s => s.Id).ToArray());

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();
            Assert.AreEqual(900, reader.TagLayer("context"), "layers persist across reload");

            await svc.SetTagLayerAsync("active", null);
            Assert.AreEqual(ArchiveService.DefaultLayer, svc.TagLayer("active"), "clearing returns to default");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Manual reorder moves a chat within a collection's order and persists.
    [TestMethod]
    public async Task ReorderInCollection_MovesAndPersists()
    {
        var svc = TempService(out var store);
        try
        {
            var a = new ArchiveSession { Id = "a" }; var b = new ArchiveSession { Id = "b" }; var c = new ArchiveSession { Id = "c" };
            svc.Store.Sessions["a"] = a; svc.Store.Sessions["b"] = b; svc.Store.Sessions["c"] = c;
            await svc.AddToCollectionAsync(a, "P"); await svc.AddToCollectionAsync(b, "P"); await svc.AddToCollectionAsync(c, "P");
            var col = svc.Store.Collections.Values.First(x => x.Name == "P");
            CollectionAssert.AreEqual(new[] { "a", "b", "c" }, col.SessionIds.ToArray());

            await svc.ReorderInCollectionAsync(col.Id, "c", -1);          // c up one -> a, c, b
            CollectionAssert.AreEqual(new[] { "a", "c", "b" }, col.SessionIds.ToArray());

            await svc.ReorderInCollectionAsync(col.Id, "a", +1, toEnd: true); // a to bottom -> c, b, a
            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, col.SessionIds.ToArray());

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();
            CollectionAssert.AreEqual(new[] { "c", "b", "a" }, reader.Store.Collections[col.Id].SessionIds.ToArray());
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // The compound filter can also restrict to a collection's members (filter-by-project).
    [TestMethod]
    public async Task FilterChats_RestrictsToCollection()
    {
        var svc = TempService(out var store);
        try
        {
            var a = new ArchiveSession { Id = "a" }; a.Tags.Add("active");
            var b = new ArchiveSession { Id = "b" }; b.Tags.Add("active");
            svc.Store.Sessions["a"] = a; svc.Store.Sessions["b"] = b;
            await svc.AddToCollectionAsync(a, "Renderer");
            var col = svc.Store.Collections.Values.First(c => c.Name == "Renderer");

            var r = svc.FilterChats(new ChatFilter { IncludeTags = { "active" }, CollectionId = col.Id });
            CollectionAssert.AreEquivalent(new[] { "a" }, r.Select(s => s.Id).ToArray());
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Collection tags add/remove and ride along in the export/import backup.
    [TestMethod]
    public async Task CollectionTags_AddRemoveAndSurviveBackup()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            await svc.AddToCollectionAsync(svc.Store.Sessions["s1"], "Renderer");
            var col = svc.Store.Collections.Values.First(c => c.Name == "Renderer");

            Assert.IsTrue(await svc.AddCollectionTagAsync(col.Id, "graphics"));
            Assert.IsFalse(await svc.AddCollectionTagAsync(col.Id, "GRAPHICS"), "dupe rejected");
            CollectionAssert.Contains(col.Tags, "graphics");

            var json = svc.ExportCollectionsJson();
            svc.Store.Collections.Clear();
            await svc.ImportCollectionsJsonAsync(json);

            var restored = svc.Store.Collections.Values.First(c => c.Name == "Renderer");
            CollectionAssert.Contains(restored.Tags, "graphics", "collection tags survive a backup round-trip");

            Assert.IsTrue(await svc.RemoveCollectionTagAsync(restored.Id, "graphics"));
            Assert.AreEqual(0, restored.Tags.Count);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // An agent can tag itself by runtime id (app-only), and untag removes.
    [TestMethod]
    public async Task AgentCommand_Tag_AddsAndUntagRemoves_AppLocal()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["mine"] = new ArchiveSession { Id = "mine", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "tag", tags = new List<string> { "bug", "urgent" }, id = "mine", tool = "codex" });
            Assert.IsTrue(r.Ok, r.Message);
            var tags = ArchiveService.UserTags(svc.Store.Sessions["mine"]);
            CollectionAssert.Contains(tags.ToArray(), "bug");
            CollectionAssert.Contains(tags.ToArray(), "urgent");

            var u = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "untag", tags = new List<string> { "urgent" }, id = "mine", tool = "codex" });
            Assert.IsTrue(u.Ok, u.Message);
            CollectionAssert.DoesNotContain(ArchiveService.UserTags(svc.Store.Sessions["mine"]).ToArray(), "urgent");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Tag colors: auto-color is deterministic (same name -> same palette color, case-insensitive) and
    // a user override wins until cleared, persisting in the store.
    [TestMethod]
    public async Task TagColors_AutoDeterministicAndOverridable()
    {
        var svc = TempService(out var store);
        try
        {
            var auto1 = ArchiveService.AutoTagColor("bug");
            var auto2 = ArchiveService.AutoTagColor("BUG");
            Assert.AreEqual(auto1, auto2, "auto-color is case-insensitive + deterministic");
            CollectionAssert.Contains(ArchiveService.TagPalette.ToArray(), auto1, "auto-color comes from the palette");
            Assert.AreEqual(auto1, svc.TagColor("bug"), "no override -> auto-color");
            Assert.IsFalse(svc.HasCustomTagColor("bug"));

            await svc.SetTagColorAsync("bug", "#123456");
            Assert.AreEqual("#123456", svc.TagColor("BUG"), "override wins, case-insensitive");
            Assert.IsTrue(svc.HasCustomTagColor("bug"));

            var reader = new ArchiveService(storePath: store);
            await reader.LoadAsync();
            Assert.AreEqual("#123456", reader.TagColor("bug"), "override persists across reload");

            await svc.SetTagColorAsync("bug", null);
            Assert.AreEqual(auto1, svc.TagColor("bug"), "clearing returns to auto");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // The agent resume prompt must point a fresh agent at the transcript as the source of truth and
    // frame the snippets as a preview only - so it reconstructs the real task instead of acting on an excerpt.
    [TestMethod]
    public void ResumePrompt_TreatsTranscriptAsAuthoritative()
    {
        var svc = TempService(out var store);
        try
        {
            var s = new ArchiveSession
            {
                Id = "x",
                Title = "Check current date",
                SourcePath = @"C:\Users\Ahmed\.claude\projects\p\abc.jsonl",
                Workspace = @"z:\proj",
                ContentLoaded = true   // skip the file load; we set the preview messages directly
            };
            s.Messages.Add(new ArchiveMessage { Role = "user", Text = "do the actual thing" });
            s.Messages.Add(new ArchiveMessage { Role = "assistant", Text = "finished step one" });
            svc.Store.Sessions["x"] = s;

            var prompt = svc.CopyPayload(s, "resume");

            Assert.IsTrue(prompt.Contains(s.SourcePath), "includes the source transcript path");
            Assert.IsTrue(prompt.Contains("Read the archived transcript"), "tells the agent to read the file first");
            Assert.IsTrue(prompt.Contains("authoritative"), "marks the transcript as authoritative");
            Assert.IsTrue(prompt.Contains("preview"), "labels the snippets as only a preview");
            Assert.IsTrue(prompt.Contains("do the actual thing"), "includes a recent message preview");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4: an agent favorites "self" — resolved as the newest session in its workspace.
    [TestMethod]
    public async Task AgentCommand_FavoriteSelf_ResolvesNewestByCwd()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["old"] = new ArchiveSession { Id = "old", Workspace = "z:\\proj", UpdatedAt = "2026-06-01T00:00:00Z", Tool = "claude" };
            svc.Store.Sessions["new"] = new ArchiveSession { Id = "new", Workspace = "z:\\proj", UpdatedAt = "2026-06-15T00:00:00Z", Tool = "claude" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "favorite", target = "self", tool = "claude", cwd = "z:/proj/" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.Store.Sessions["new"].Pinned, "newest session in the cwd is 'self'");
            Assert.IsFalse(svc.Store.Sessions["old"].Pinned);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4: an agent files itself into a project by cwd.
    [TestMethod]
    public async Task AgentCommand_AddToProject_FilesSelfByCwd()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s"] = new ArchiveSession { Id = "s", Workspace = "z:\\proj", UpdatedAt = "2026-06-15T00:00:00Z", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToProject", project = "VENPOD", target = "self", tool = "codex", cwd = "z:\\proj" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.Store.Collections.Values.Any(c => c.Name == "VENPOD" && c.SessionIds.Contains("s")));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4/N2: an agent registers a non-default source root; defaults are kept.
    [TestMethod]
    public async Task AgentCommand_AddSource_RegistersCustomRootKeepsDefaults()
    {
        var svc = TempService(out var store);
        try
        {
            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSource", tool = "claude", root = "d:\\custom\\claude" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.EffectiveSources().Any(s => s.Tool == "claude" && s.Root == "d:\\custom\\claude"));
            Assert.IsTrue(svc.EffectiveSources().Any(s => s.Tool == "codex"), "default codex source kept");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // N4/N7: an agent renames itself (app title), canonical write-back is attempted.
    [TestMethod]
    public async Task AgentCommand_Rename_SetsLocalTitle()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["s"] = new ArchiveSession { Id = "s", Title = "orig", Workspace = "z:\\proj", UpdatedAt = "2026-06-15T00:00:00Z", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "rename", target = "self", tool = "codex", cwd = "z:\\proj", localName = "My cool chat" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual("My cool chat", svc.Store.Sessions["s"].DisplayTitle);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // An exact id (from CODEX_THREAD_ID / CLAUDE_CODE_SESSION_ID) files THAT chat, ignoring a
    // newer-looking decoy in the same folder.
    [TestMethod]
    public async Task AgentCommand_AddToProject_ByExactId_AddsThatChat()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["decoy"] = new ArchiveSession { Id = "decoy", Tool = "codex", Workspace = "z:\\proj", UpdatedAt = "2026-06-28T00:00:00Z" };
            svc.Store.Sessions["mine"]  = new ArchiveSession { Id = "mine",  Tool = "codex", Workspace = "z:\\proj", UpdatedAt = "2026-01-01T00:00:00Z" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToProject", project = "Venpod", id = "mine", tool = "codex" });

            Assert.IsTrue(r.Ok, r.Message);
            var col = svc.Store.Collections.Values.First(c => c.Name == "Venpod");
            Assert.IsTrue(col.SessionIds.Contains("mine"), "the exact id was filed");
            Assert.IsFalse(col.SessionIds.Contains("decoy"), "the decoy was NOT filed");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Filing into a project can carry an optional in-app name in the same command - the chat lands
    // in the project AND gets that app-only title (the agent's global title is untouched).
    [TestMethod]
    public async Task AgentCommand_AddSelfToProject_WithName_FilesAndNamesAppLocal()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["mine"] = new ArchiveSession { Id = "mine", Title = "raw auto title", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand
            {
                op = "addSelfToProject", project = "Local Retrieval", name = "codex-claude-local", id = "mine", tool = "codex"
            });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual("codex-claude-local", svc.Store.Sessions["mine"].DisplayTitle, "the in-app name is set");
            Assert.AreEqual("raw auto title", svc.Store.Sessions["mine"].Title, "the raw/global title is untouched");
            var col = svc.Store.Collections.Values.First(c => c.Name == "Local Retrieval");
            Assert.IsTrue(col.SessionIds.Contains("mine"), "and it's filed into the project");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Omitting name keeps the chat's existing (auto) title - name is strictly optional.
    [TestMethod]
    public async Task AgentCommand_AddSelfToProject_WithoutName_KeepsAutoTitle()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["mine"] = new ArchiveSession { Id = "mine", Title = "auto title", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSelfToProject", project = "P", id = "mine", tool = "codex" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual("auto title", svc.Store.Sessions["mine"].DisplayTitle, "no name given -> auto title kept");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // setName (alias of rename) sets the app-only name with no project, reading the friendly 'name' field.
    [TestMethod]
    public async Task AgentCommand_SetName_RenamesAppLocalOnly()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["mine"] = new ArchiveSession { Id = "mine", Title = "auto", Tool = "codex" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "setName", name = "codex-claude-local", id = "mine", tool = "codex" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual("codex-claude-local", svc.Store.Sessions["mine"].DisplayTitle);
            Assert.AreEqual(0, svc.Store.Collections.Count, "setName files into no project");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // Codex resume/fork sessions can expose CODEX_THREAD_ID as the rollout filename while the app's
    // stored key is a canonical parent id. The agent should still pass the runtime id, and the app
    // should resolve it to the stored key without falling back to cwd/latest.
    [TestMethod]
    public async Task AgentCommand_AddSelfToProject_RuntimeIdInSourcePath_ResolvesStoredKey()
    {
        var svc = TempService(out var store);
        try
        {
            const string runtimeId = "019f0d62-b2e5-70a1-8e81-4ff2722ca865";
            const string storedId = "019f0d3e-1d12-7cc3-b892-94652a2f95ee";
            svc.Store.Sessions[storedId] = new ArchiveSession
            {
                Id = storedId,
                Title = "Review VENPOD history",
                Tool = "codex",
                Workspace = "z:\\proj",
                SourcePath = "C:\\Users\\Ahmed\\.codex\\sessions\\2026\\06\\28\\rollout-2026-06-28T02-39-59-" + runtimeId + ".jsonl",
                UpdatedAt = "2026-06-28T00:00:00Z"
            };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand
            {
                op = "addSelfToProject",
                project = "Venpod",
                id = runtimeId,
                tool = "codex",
                requestId = "req-1"
            });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual(runtimeId, r.InputId);
            Assert.AreEqual(storedId, r.ResolvedSessionId);
            Assert.AreEqual(true, r.Persisted);
            Assert.IsTrue(svc.Store.Collections.Values.First(c => c.Name == "Venpod").SessionIds.Contains(storedId));
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    [TestMethod]
    public async Task AgentCommand_AddSelfToProject_ForkAlias_ResolvesWithoutGuessing()
    {
        var svc = TempService(out var store);
        try
        {
            const string runtimeId = "runtime-session";
            const string forkedFrom = "parent-session";
            svc.Store.Sessions[runtimeId] = new ArchiveSession
            {
                Id = runtimeId,
                Tool = "codex",
                Workspace = "z:\\proj",
                UpdatedAt = "2026-06-28T00:00:00Z",
                Aliases = new System.Collections.ObjectModel.ObservableCollection<string> { forkedFrom }
            };
            svc.Store.Sessions["decoy"] = new ArchiveSession { Id = "decoy", Tool = "codex", Workspace = "z:\\proj", UpdatedAt = "2026-06-29T00:00:00Z" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSelfToProject", project = "Venpod", id = forkedFrom, tool = "codex" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.AreEqual(runtimeId, r.ResolvedSessionId);
            var col = svc.Store.Collections.Values.First(c => c.Name == "Venpod");
            Assert.IsTrue(col.SessionIds.Contains(runtimeId));
            Assert.IsFalse(col.SessionIds.Contains("decoy"), "alias resolution must not choose latest/cwd decoys");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // The bug that started this: self + a cwd that matches nothing must FAIL, never grab another chat.
    [TestMethod]
    public async Task AgentCommand_SelfWithUnmatchedCwd_FailsClosed()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["unrelated"] = new ArchiveSession { Id = "unrelated", Tool = "claude", Workspace = "z:\\elsewhere", UpdatedAt = "2026-06-28T00:00:00Z" };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToCollection", project = "Venpod", target = "self", tool = "codex", cwd = "z:/nowhere/that/matches" });

            Assert.IsFalse(r.Ok, "must not add a chat when nothing matches");
            Assert.AreEqual(0, svc.Store.Collections.Count, "nothing was filed");
            Assert.IsFalse(svc.Store.Sessions["unrelated"].Pinned);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // self without a tool can't disambiguate codex vs claude -> fail closed.
    [TestMethod]
    public async Task AgentCommand_SelfWithoutTool_FailsClosed()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["a"] = new ArchiveSession { Id = "a", Tool = "codex", Workspace = "z:\\proj" };
            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToCollection", project = "P", target = "self", cwd = "z:\\proj" });
            Assert.IsFalse(r.Ok, "self requires a tool");
            Assert.AreEqual(0, svc.Store.Collections.Count);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // An unknown exact id fails closed - it never falls through to some other indexed chat.
    [TestMethod]
    public async Task AgentCommand_UnknownId_FailsClosed_AddsNoOtherChat()
    {
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions["other"] = new ArchiveSession { Id = "other", Tool = "codex", Workspace = "z:\\proj", UpdatedAt = "2026-06-28T00:00:00Z" };
            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToCollection", project = "P", id = "does-not-exist-anywhere", tool = "codex" });
            Assert.IsFalse(r.Ok, "unknown id must error");
            Assert.AreEqual(0, svc.Store.Collections.Count, "no other chat was filed");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A brand-new session that isn't indexed yet is found + indexed on demand by its exact id, then filed.
    [TestMethod]
    public async Task AgentCommand_AddToProject_IndexesFreshSessionByExactId()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-fresh-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = "fresh" + Guid.NewGuid().ToString("N").Substring(0, 8);
        File.WriteAllText(Path.Combine(root, id + ".jsonl"),
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"a brand new chat\"}]},\"timestamp\":\"2026-06-28T02:00:00Z\"}\n");
        var svc = TempService(out var store);
        try
        {
            await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSource", tool = "claude", root = root });
            Assert.IsFalse(svc.Store.Sessions.ContainsKey(id), "not indexed before the command");

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addToProject", project = "Fresh", id = id, tool = "claude" });

            Assert.IsTrue(r.Ok, r.Message);
            Assert.IsTrue(svc.Store.Sessions.ContainsKey(id), "indexed on demand from disk");
            Assert.IsTrue(svc.Store.Collections.Values.First(c => c.Name == "Fresh").SessionIds.Contains(id));
        }
        finally
        {
            if (File.Exists(store)) File.Delete(store);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public async Task AgentCommand_AddToProject_RefreshesExistingStaleClaudeSession()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-refresh-existing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var id = "claude" + Guid.NewGuid().ToString("N").Substring(0, 8);
        var path = WriteClaudeSession(root, id + ".jsonl", id, "z:\\proj", "2026-06-28T10:13:30Z", "fresh donor-binding status answer");
        var svc = TempService(out var store);
        try
        {
            svc.Store.Sessions[id] = new ArchiveSession
            {
                Id = id,
                Tool = "claude",
                SourcePath = path,
                Workspace = "z:\\proj",
                UpdatedAt = "2026-06-16T00:00:00Z",
                Text = "old stale front content",
                CustomTitle = "kept local title"
            };

            var r = await svc.ApplyAgentCommandAsync(new AgentCommand { op = "addSelfToProject", project = "T6 Modding", id = id, tool = "claude" });

            Assert.IsTrue(r.Ok, r.Message);
            var refreshed = svc.Store.Sessions[id];
            StringAssert.Contains(refreshed.Text, "fresh donor-binding status answer");
            Assert.IsFalse(refreshed.Text.Contains("old stale front content"), "exact-id filing must refresh stale display/search text first");
            Assert.AreEqual("2026-06-28T10:13:30Z", refreshed.UpdatedAt);
            Assert.AreEqual("kept local title", refreshed.CustomTitle, "app-local naming is still user metadata");
            Assert.IsTrue(svc.Store.Collections.Values.First(c => c.Name == "T6 Modding").SessionIds.Contains(id));
            Assert.IsTrue(svc.Store.FileStamps.ContainsKey(path), "the refreshed file stamp is recorded");
        }
        finally
        {
            if (File.Exists(store)) File.Delete(store);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    // L4: the Source inspector reads the real rollout event timeline, not a placeholder.
    [TestMethod]
    public void ReadEvents_ReturnsRealTimelineFromRollout()
    {
        var dir = Path.Combine(Path.GetTempPath(), "clr-evt-" + Guid.NewGuid().ToString("N"));
        var file = WriteRollout(dir, "rollout-evt.jsonl", "sess-1", "2026-03-01T00:00:00Z", "hello old chat");
        try
        {
            var service = new ArchiveService(useBundledStore: true);
            var events = service.ReadEvents(new ArchiveSession { Id = "sess-1", SourcePath = file });

            Assert.IsTrue(events.Count >= 3, $"expected >=3 events, got {events.Count}");
            Assert.IsTrue(events.Any(e => e.Kind == "user_message" && e.Preview.Contains("hello old chat")));
            Assert.IsTrue(events.Any(e => e.Kind == "agent_message"));
        }
        finally { Directory.Delete(dir, true); }
    }

    [TestMethod]
    public async Task LoadAsync_UsesRoseAsNativeDefault()
    {
        var service = new ArchiveService(useBundledStore: true);

        await service.LoadAsync();

        Assert.AreNotEqual("codex-blue", service.Store.Settings.Accent);
        Assert.StartsWith("#", service.Store.Settings.AccentHex);
        Assert.AreEqual("compact", service.Store.Settings.Radius);
        Assert.IsTrue(service.Store.Settings.ReadOnlySourceMode);
        Assert.AreNotEqual(0, service.Sessions.Count);
    }

    [TestMethod]
    public async Task Search_FindsFixtureBySessionId()
    {
        var service = new ArchiveService(useBundledStore: true);
        await service.LoadAsync();

        var results = service.Search("fixture-b");

        Assert.IsTrue(results.Any(session => session.Id == "fixture-b"));
    }

    [TestMethod]
    public async Task CopyPayload_BuildsRestorePacketAndCodePayload()
    {
        var service = new ArchiveService(useBundledStore: true);
        await service.LoadAsync();
        var fixture = service.Search("fixture-b").First();

        var restore = service.CopyPayload(fixture, "restore");
        var code = service.CopyPayload(fixture, "code");

        StringAssert.Contains(restore, "Restore Packet");
        StringAssert.Contains(code, "export function score");
    }

    [TestMethod]
    public async Task DeepSearch_ReturnsContentSnippetsAndPathPayload()
    {
        var service = new ArchiveService(useBundledStore: true);
        await service.LoadAsync();

        // DeepSearch now matches the capped, in-memory Text (full transcripts lazy-load on open), so
        // search terms that live in the stored text.
        var hits = service.DeepSearch("restore packet");
        var fixture = service.Search("fixture-b").First();
        var path = service.CopyPayload(fixture, "path");

        Assert.IsTrue(hits.Any(hit => hit.Session.Id == "fixture-b" && hit.Snippet.Contains("restore", StringComparison.OrdinalIgnoreCase)));
        Assert.AreEqual(fixture.SourcePath, path);
    }
}
