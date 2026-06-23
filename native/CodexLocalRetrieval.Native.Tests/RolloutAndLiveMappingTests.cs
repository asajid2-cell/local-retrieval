using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Native.Tests;

// Locks in the two paths that make a stored session and a live one render identically:
// RolloutToEvents (the on-disk .jsonl -> AgentEvent) and CodexItemMapper.MapNotification (live app-server events).
[TestClass]
public sealed class RolloutAndLiveMappingTests
{
    private static string WriteRollout(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), "clr-rollout-" + Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    [TestMethod]
    public void Rollout_ParsesTurnsCommandsAndMessages_SkipsEncryptedReasoning()
    {
        var path = WriteRollout(
            "{\"timestamp\":\"t\",\"type\":\"event_msg\",\"payload\":{\"type\":\"user_message\",\"message\":\"build the thing\"}}",
            "{\"timestamp\":\"t\",\"type\":\"response_item\",\"payload\":{\"type\":\"reasoning\",\"summary\":[],\"encrypted_content\":\"gAAAA-opaque\"}}",
            "{\"timestamp\":\"t\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"call_id\":\"c1\",\"name\":\"shell_command\",\"arguments\":\"{\\\"cmd\\\":\\\"echo hi\\\"}\"}}",
            "{\"timestamp\":\"t\",\"type\":\"response_item\",\"payload\":{\"type\":\"function_call_output\",\"call_id\":\"c1\",\"output\":\"hi\\n\"}}",
            "{\"timestamp\":\"t\",\"type\":\"event_msg\",\"payload\":{\"type\":\"agent_message\",\"message\":\"it printed hi\"}}");
        try
        {
            var ev = RolloutToEvents.Parse(path);
            CollectionAssert.AreEqual(
                new[] { AgentEventKind.UserMessage, AgentEventKind.ToolCall, AgentEventKind.ToolOutput, AgentEventKind.AssistantText },
                ev.Select(e => e.Kind).ToArray());
            Assert.AreEqual("build the thing", ev[0].Text);
            Assert.AreEqual("echo hi", ev[1].ToolInput);   // extracted from {"cmd":...}, not raw JSON
            Assert.AreEqual("c1", ev[1].ItemId);
            Assert.AreEqual("c1", ev[2].ItemId);           // output correlates to its call by id
            StringAssert.Contains(ev[2].Output!, "hi");
            Assert.AreEqual("it printed hi", ev[3].Text);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Rollout_ExtractsArgvCommand()
    {
        var path = WriteRollout(
            "{\"type\":\"response_item\",\"payload\":{\"type\":\"function_call\",\"call_id\":\"x\",\"arguments\":\"{\\\"command\\\":[\\\"bash\\\",\\\"-lc\\\",\\\"ls\\\"]}\"}}");
        try
        {
            var ev = RolloutToEvents.Parse(path);
            Assert.AreEqual(1, ev.Count);
            Assert.AreEqual("bash -lc ls", ev[0].ToolInput);
        }
        finally { File.Delete(path); }
    }

    [TestMethod]
    public void Claude_Title_CustomBeatsAi_BeatsFirstPrompt_AndRenamePersists()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-rename-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "z--proj");
        Directory.CreateDirectory(proj);
        var id = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var path = Path.Combine(proj, id + ".jsonl");
        File.WriteAllLines(path, new[]
        {
            "{\"type\":\"user\",\"cwd\":\"z:\\\\proj\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"the original first task\"}]}}"
        });
        try
        {
            // 1) no title record -> first prompt is the title
            Assert.AreEqual("the original first task", new ClaudeSessionStore(root).List()[0].Title);

            // 2) an ai-title record wins over the first prompt
            File.AppendAllText(path, "{\"type\":\"ai-title\",\"sessionId\":\"" + id + "\",\"aiTitle\":\"Claude generated title\"}\n");
            Assert.AreEqual("Claude generated title", new ClaudeSessionStore(root).List()[0].Title);

            // 3) rename (custom-title) wins over the ai-title, persists, and is shared via the .jsonl
            var store = new ClaudeSessionStore(root);
            store.List(); // cache the path
            Assert.IsTrue(store.RenameSession(id, "claude-remote maker"));
            Assert.AreEqual("claude-remote maker", new ClaudeSessionStore(root).List()[0].Title);
            var disk = File.ReadAllText(path);
            StringAssert.Contains(disk, "\"type\":\"custom-title\"");
            StringAssert.Contains(disk, "\"customTitle\":\"claude-remote maker\"");
        }
        finally { Directory.Delete(root, true); }
    }

    private static IEnumerable<AgentEvent> Note(string method, string paramsJson)
    {
        using var d = JsonDocument.Parse(paramsJson);
        return CodexItemMapper.MapNotification(method, d.RootElement).ToList();
    }

    [TestMethod]
    public void Live_AgentMessageDelta_IsAppendableAssistantText()
    {
        var e = Note("item/agentMessage/delta", "{\"threadId\":\"th\",\"itemId\":\"a1\",\"delta\":\"Hel\"}").Single();
        Assert.AreEqual(AgentEventKind.AssistantText, e.Kind);
        Assert.IsTrue(e.Delta);
        Assert.AreEqual("a1", e.ItemId);
        Assert.AreEqual("Hel", e.Text);
    }

    [TestMethod]
    public void Live_TurnCompleted_EndsTurnAndGoesIdle()
    {
        var es = Note("turn/completed", "{\"threadId\":\"th\",\"usage\":{\"inputTokens\":1}}").ToList();
        Assert.AreEqual(AgentEventKind.TurnResult, es[0].Kind);
        Assert.AreEqual(AgentEventKind.Status, es[1].Kind);
        Assert.AreEqual("idle", es[1].Text);
    }

    [TestMethod]
    public void Live_CommandOutputDelta_IsAppendableToolOutput()
    {
        var e = Note("item/commandExecution/outputDelta", "{\"threadId\":\"th\",\"itemId\":\"c2\",\"delta\":\"line\\n\"}").Single();
        Assert.AreEqual(AgentEventKind.ToolOutput, e.Kind);
        Assert.IsTrue(e.Delta);
        Assert.AreEqual("c2", e.ItemId);
        StringAssert.Contains(e.Output!, "line");
    }

    [TestMethod]
    public void Claude_ParsesMessagesThinkingAndTools_SkipsInjectedContext()
    {
        var root = Path.Combine(Path.GetTempPath(), "clr-claude-" + Guid.NewGuid().ToString("N"));
        var proj = Path.Combine(root, "z--proj");
        Directory.CreateDirectory(proj);
        var id = "11111111-2222-3333-4444-555555555555";
        File.WriteAllLines(Path.Combine(proj, id + ".jsonl"), new[]
        {
            "{\"type\":\"user\",\"cwd\":\"z:\\\\proj\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"<system-reminder>noise</system-reminder>\"}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"fix the bug\"}]}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"text\",\"text\":\"on it\"},{\"type\":\"tool_use\",\"id\":\"t1\",\"name\":\"Bash\",\"input\":{\"command\":\"ls\"}}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t1\",\"content\":\"a.txt\"}]}}"
        });
        try
        {
            var store = new ClaudeSessionStore(root);
            var list = store.List();
            Assert.AreEqual(1, list.Count);
            Assert.AreEqual("fix the bug", list[0].Title);        // injected <system-reminder> skipped for the title
            Assert.AreEqual("z:\\proj", list[0].Cwd);

            var ev = store.ReadHistory(id);
            CollectionAssert.AreEqual(
                new[] { AgentEventKind.UserMessage, AgentEventKind.Thinking, AgentEventKind.AssistantText, AgentEventKind.ToolCall, AgentEventKind.ToolOutput },
                ev.Select(e => e.Kind).ToArray());
            Assert.AreEqual("fix the bug", ev[0].Text);            // boilerplate user text dropped
            Assert.AreEqual("Bash", ev[3].ToolName);
            StringAssert.Contains(ev[3].ToolInput!, "\"command\":\"ls\"");  // full raw input JSON (client renders it)
            Assert.AreEqual("t1", ev[4].ItemId);
            StringAssert.Contains(ev[4].Output!, "a.txt");
        }
        finally { Directory.Delete(root, true); }
    }

    [TestMethod]
    public void ClaudeStream_MapsInitMessagesToolsAndResult()
    {
        Assert.AreEqual(AgentEventKind.SessionStarted,
            ClaudeStreamMapper.Map("{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"sid-1\"}").Single().Kind);

        var asst = ClaudeStreamMapper.Map("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"hmm\"},{\"type\":\"text\",\"text\":\"hi\"},{\"type\":\"tool_use\",\"id\":\"t9\",\"name\":\"Bash\",\"input\":{\"command\":\"echo x\"}}]}}").ToList();
        CollectionAssert.AreEqual(new[] { AgentEventKind.Thinking, AgentEventKind.AssistantText, AgentEventKind.ToolCall }, asst.Select(e => e.Kind).ToArray());
        StringAssert.Contains(asst[2].ToolInput!, "echo x");  // full raw input JSON

        var tr = ClaudeStreamMapper.Map("{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"t9\",\"content\":\"x\"}]}}").Single();
        Assert.AreEqual(AgentEventKind.ToolOutput, tr.Kind);
        Assert.AreEqual("t9", tr.ItemId);

        var res = ClaudeStreamMapper.Map("{\"type\":\"result\",\"subtype\":\"success\",\"result\":\"done\"}").ToList();
        Assert.AreEqual(AgentEventKind.TurnResult, res[0].Kind);
        Assert.AreEqual("idle", res[1].Text);
    }
}
