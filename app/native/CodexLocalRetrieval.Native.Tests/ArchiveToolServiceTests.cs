using System.Text.Json;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

// The tool surface itself: stats accuracy and the input validation that keeps the model from writing
// blank project names / titles even after the user approves the action.
[TestClass]
public sealed class ArchiveToolServiceTests
{
    private static ArchiveService StoreWith(params ArchiveSession[] sessions)
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-tool-" + Guid.NewGuid().ToString("N") + ".json");
        var svc = new ArchiveService(storePath: store);
        foreach (var s in sessions) svc.Store.Sessions[s.Id] = s;
        return svc;
    }

    private static async Task<string> RunTool(ArchiveService svc, string name, string argsJson)
    {
        var tool = new ArchiveToolService(svc).Tools().First(t => t.Name == name);
        var args = JsonSerializer.Deserialize<JsonElement>(argsJson);
        return JsonSerializer.Serialize(await tool.Execute(args, default));
    }

    [TestMethod]
    public async Task ArchiveStats_CountsTotalsAndByTool()
    {
        var svc = StoreWith(
            new ArchiveSession { Id = "a", Tool = "codex", WorkspaceName = "cortex", Pinned = true },
            new ArchiveSession { Id = "b", Tool = "codex", WorkspaceName = "cortex" },
            new ArchiveSession { Id = "c", Tool = "claude", WorkspaceName = "venpod" },
            new ArchiveSession { Id = "d", Tool = "codex", Archived = true }); // archived is excluded

        var json = await RunTool(svc, "archive_stats", "{}");

        StringAssert.Contains(json, "\"totalChats\":3");
        StringAssert.Contains(json, "\"codex\":2");
        StringAssert.Contains(json, "\"claude\":1");
        StringAssert.Contains(json, "\"favorites\":1");
    }

    [TestMethod]
    public async Task AddToProject_BlankName_IsRefused()
    {
        var svc = StoreWith(new ArchiveSession { Id = "s1" });
        var json = await RunTool(svc, "add_to_project", "{\"id\":\"s1\",\"project\":\"   \"}");
        StringAssert.Contains(json, "\"ok\":false");
    }

    [TestMethod]
    public async Task RenameLocal_BlankTitle_IsRefused()
    {
        var svc = StoreWith(new ArchiveSession { Id = "s1" });
        var json = await RunTool(svc, "rename_local", "{\"id\":\"s1\",\"title\":\"\"}");
        StringAssert.Contains(json, "\"ok\":false");
        Assert.AreEqual("", svc.Store.Sessions["s1"].CustomTitle);
    }

    [TestMethod]
    public async Task ReadChat_UnknownId_ReturnsError_NotCrash()
    {
        var svc = StoreWith();
        var json = await RunTool(svc, "read_chat", "{\"id\":\"nope\"}");
        StringAssert.Contains(json, "error");
    }

    // A secret pasted into the first message becomes the auto-derived title, so the title field must
    // be redacted too — not just the message body.
    [TestMethod]
    public async Task ReadChat_RedactsSecretInTitle()
    {
        var fakeKey = "sk-" + "FAKEexampleKEYnotreal0000000"; // synthetic, not a real key
        var svc = StoreWith(new ArchiveSession { Id = "s1", Title = $"deploy key {fakeKey} notes" });
        var json = await RunTool(svc, "read_chat", "{\"id\":\"s1\"}");
        StringAssert.Contains(json, SecretRedactor.Mask);
        StringAssert.DoesNotMatch(json, new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(fakeKey)));
    }

    [TestMethod]
    public async Task ResumeChat_GatewaySession_DescribesAndReturnsGatewayLaunchMode()
    {
        var session = new ArchiveSession
        {
            Id = "gateway-chat",
            Tool = "claude",
            Title = "Gateway chat",
            LaunchMode = ArchiveService.GatewayLaunchMode
        };
        var svc = StoreWith(session);
        string? resumed = null;
        var tool = new ArchiveToolService(
            svc,
            resumeChat: id => resumed = id)
            .Tools()
            .First(t => t.Name == "resume_chat");

        StringAssert.Contains(tool.Spec.Description, "Gateway");
        StringAssert.Contains(tool.Spec.Description, "cc");

        var args = JsonSerializer.Deserialize<JsonElement>("{\"id\":\"gateway-chat\"}");
        var json = JsonSerializer.Serialize(await tool.Execute(args, default));

        Assert.AreEqual("gateway-chat", resumed);
        StringAssert.Contains(json, "\"launchMode\":\"gateway\"");
        StringAssert.Contains(json, "Gateway");
    }
}
