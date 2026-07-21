using CodexLocalRetrieval.Core.Agents;

namespace CodexLocalRetrieval.Native.Tests;

// Maps real `codex exec --json` output (captured from codex 0.130) to the unified AgentEvent model.
[TestClass]
public sealed class CodexEventMapperTests
{
    private static AgentEvent One(string line) => CodexEventMapper.Map(line).Single();

    [TestMethod]
    public void ThreadStarted_BecomesSessionStarted()
    {
        var e = One("{\"type\":\"thread.started\",\"thread_id\":\"019ef2d1-1790-7bc1-950b-da2c8883a689\"}");
        Assert.AreEqual(AgentEventKind.SessionStarted, e.Kind);
        Assert.AreEqual("019ef2d1-1790-7bc1-950b-da2c8883a689", e.SessionId);
    }

    [TestMethod]
    public void TurnStarted_IsStatus()
        => Assert.AreEqual(AgentEventKind.Status, One("{\"type\":\"turn.started\"}").Kind);

    [TestMethod]
    public void CommandStarted_IsToolCall_WithCommand()
    {
        var e = One("{\"type\":\"item.started\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\",\"command\":\"echo hello-from-codex\",\"aggregated_output\":\"\",\"exit_code\":null,\"status\":\"in_progress\"}}");
        Assert.AreEqual(AgentEventKind.ToolCall, e.Kind);
        Assert.AreEqual("command", e.ToolName);
        Assert.AreEqual("item_0", e.ItemId);
        StringAssert.Contains(e.ToolInput!, "echo hello-from-codex");
        Assert.AreEqual("in_progress", e.State);
    }

    [TestMethod]
    public void CommandCompleted_IsToolOutput_WithExitCodeAndOutput()
    {
        var e = One("{\"type\":\"item.completed\",\"item\":{\"id\":\"item_0\",\"type\":\"command_execution\",\"command\":\"echo x\",\"aggregated_output\":\"hello-from-codex\\r\\n\",\"exit_code\":0,\"status\":\"completed\"}}");
        Assert.AreEqual(AgentEventKind.ToolOutput, e.Kind);
        Assert.AreEqual("item_0", e.ItemId);
        StringAssert.Contains(e.Output!, "hello-from-codex");
        Assert.AreEqual(0, e.ExitCode);
        Assert.AreEqual("completed", e.State);
    }

    [TestMethod]
    public void AgentMessage_IsAssistantText()
    {
        var e = One("{\"type\":\"item.completed\",\"item\":{\"id\":\"item_1\",\"type\":\"agent_message\",\"text\":\"It printed: hello-from-codex\"}}");
        Assert.AreEqual(AgentEventKind.AssistantText, e.Kind);
        StringAssert.Contains(e.Text!, "It printed");
    }

    [TestMethod]
    public void Reasoning_IsThinking()
    {
        var e = One("{\"type\":\"item.completed\",\"item\":{\"id\":\"r1\",\"type\":\"reasoning\",\"text\":\"I should run echo.\"}}");
        Assert.AreEqual(AgentEventKind.Thinking, e.Kind);
        StringAssert.Contains(e.Text!, "run echo");
    }

    [TestMethod]
    public void TurnCompleted_EmitsTurnResultThenIdle()
    {
        var evs = CodexEventMapper.Map("{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":35483,\"output_tokens\":68}}");
        Assert.AreEqual(AgentEventKind.TurnResult, evs[0].Kind);
        Assert.IsNotNull(evs[0].Usage);
        Assert.AreEqual(AgentEventKind.Status, evs[1].Kind);
        Assert.AreEqual("idle", evs[1].Text);
    }

    [TestMethod]
    public void Error_And_TurnFailed_BecomeError()
    {
        Assert.AreEqual(AgentEventKind.Error, One("{\"type\":\"error\",\"message\":\"boom\"}").Kind);
        var tf = One("{\"type\":\"turn.failed\",\"error\":{\"message\":\"Unsupported service_tier: flex\"}}");
        Assert.AreEqual(AgentEventKind.Error, tf.Kind);
        StringAssert.Contains(tf.Text!, "service_tier");
    }

    [TestMethod]
    public void GarbageOrUnknown_DoesNotThrow()
    {
        Assert.AreEqual(0, CodexEventMapper.Map("not json at all").Count);
        Assert.AreEqual(0, CodexEventMapper.Map("").Count);
        // unknown item type still surfaces (not silently dropped) on completion
        var e = One("{\"type\":\"item.completed\",\"item\":{\"id\":\"z\",\"type\":\"web_search\",\"status\":\"completed\"}}");
        Assert.AreEqual(AgentEventKind.ToolCall, e.Kind);
        Assert.AreEqual("web_search", e.ToolName);
    }
}
