using System.Net;
using System.Text.Json;
using CodexLocalRetrieval.Core.Chat;
using CodexLocalRetrieval.Core.Models;
using CodexLocalRetrieval.Core.Services;

namespace CodexLocalRetrieval.Native.Tests;

[TestClass]
public sealed class ChatOrchestratorTests
{
    // Stub transport: captures each request body and returns canned responses in order.
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;
        public StubHandler(params string[] responses) => _responses = new Queue<string>(responses);
        public List<string> CapturedBodies { get; } = new();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CapturedBodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(_responses.Dequeue()) };
        }
    }

    private static string ToolCallsResponse(string name, string argsJson) =>
        "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":null," +
        "\"tool_calls\":[{\"id\":\"call_1\",\"type\":\"function\",\"function\":{\"name\":\"" + name + "\"," +
        "\"arguments\":" + System.Text.Json.JsonSerializer.Serialize(argsJson) + "}}]}}]}";

    private static string FinalResponse(string content) =>
        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":" +
        System.Text.Json.JsonSerializer.Serialize(content) + "}}]}";

    // The DeepSeek backend sends tools + tool_choice and parses a tool_calls response back.
    [TestMethod]
    public async Task DeepSeekBackend_SendsToolsAndParsesToolCall()
    {
        var stub = new StubHandler(ToolCallsResponse("search_chats", "{\"query\":\"venpod\"}"));
        var backend = new DeepSeekBackend("https://api.deepseek.com", "deepseek-v4-flash", "sk-test", new HttpClient(stub));
        var tools = new[] { new ChatToolSpec("search_chats", "search", new { type = "object" }) };

        var reply = await backend.CompleteAsync(new[] { ChatMessage.User("find venpod") }, tools, default);

        var sent = stub.CapturedBodies[0];
        StringAssert.Contains(sent, "\"tools\"");
        StringAssert.Contains(sent, "search_chats");
        StringAssert.Contains(sent, "\"tool_choice\":\"auto\"");
        StringAssert.Contains(sent, "deepseek-v4-flash");
        Assert.IsNotNull(reply.Message.ToolCalls);
        Assert.AreEqual("search_chats", reply.Message.ToolCalls![0].Function.Name);
        StringAssert.Contains(reply.Message.ToolCalls[0].Function.Arguments, "venpod");
        Assert.AreEqual("tool_calls", reply.FinishReason);
    }

    // The orchestrator drives the REAL DeepSeekBackend through a full loop (stubbed transport):
    // tool_calls response -> search runs -> tool result fed back -> final answer.
    [TestMethod]
    public async Task Orchestrator_WithDeepSeekBackend_FullLoop()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        var stub = new StubHandler(
            ToolCallsResponse("search_chats", "{\"query\":\"fixture\"}"),
            FinalResponse("Here is what I found."));
        var backend = new DeepSeekBackend("https://api.deepseek.com", "deepseek-v4-flash", "sk-test", new HttpClient(stub));
        var orchestrator = new ChatOrchestrator(backend, new ArchiveToolService(archive).Tools());

        var result = await orchestrator.RunAsync(new List<ChatMessage>
        {
            ChatMessage.System(ArchiveToolService.SystemPrompt),
            ChatMessage.User("find the fixture chats")
        });

        Assert.AreEqual("Here is what I found.", result.Answer);
        Assert.AreEqual("search_chats", result.Activity[0].Tool);
        Assert.IsTrue(result.Activity[0].Ok);
        // the second request carried the tool result back to the model
        StringAssert.Contains(stub.CapturedBodies[1], "\"role\":\"tool\"");
        StringAssert.Contains(stub.CapturedBodies[1], "fixture");
    }

    // A scripted backend so the loop is provable without a network/API key.
    private sealed class FakeBackend : IChatBackend
    {
        private readonly Queue<BackendReply> _replies;
        public FakeBackend(IEnumerable<BackendReply> replies) => _replies = new Queue<BackendReply>(replies);
        public string Name => "fake";
        public bool SupportsTools => true;
        public List<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ChatToolSpec> Tools)> Seen { get; } = new();
        public Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatToolSpec> tools, CancellationToken ct)
        {
            Seen.Add((messages.ToList(), tools));
            return Task.FromResult(_replies.Dequeue());
        }
    }

    private static ToolCall Call(string id, string name, string args) =>
        new() { Id = id, Function = new ToolCallFunction { Name = name, Arguments = args } };

    // The whole point: ask -> model calls search_chats -> tool runs against the real archive ->
    // result is fed back as a role:tool message -> model gives a final answer.
    [TestMethod]
    public async Task Orchestrator_RunsToolCall_AgainstArchive_AndReturnsAnswer()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        var tools = new ArchiveToolService(archive).Tools();

        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "search_chats", "{\"query\":\"fixture\"}") } }, FinishReason = "tool_calls" },
            new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "Found a matching chat." }, FinishReason = "stop" }
        });
        var orchestrator = new ChatOrchestrator(backend, tools);

        var messages = new List<ChatMessage> { ChatMessage.System(ArchiveToolService.SystemPrompt), ChatMessage.User("find the fixture chats") };
        var result = await orchestrator.RunAsync(messages);

        Assert.AreEqual("Found a matching chat.", result.Answer);
        Assert.IsNull(result.Error);
        Assert.AreEqual(1, result.Activity.Count);
        Assert.AreEqual("search_chats", result.Activity[0].Tool);
        Assert.IsTrue(result.Activity[0].Ok);

        // the tool schema was advertised to the backend
        Assert.IsTrue(backend.Seen[0].Tools.Any(t => t.Name == "search_chats"));
        // the second backend call saw the tool result (real archive hit) fed back
        var toolMsg = backend.Seen[1].Messages.LastOrDefault(m => m.Role == "tool");
        Assert.IsNotNull(toolMsg);
        StringAssert.Contains(toolMsg!.Content!, "results");
        StringAssert.Contains(toolMsg.Content!, "fixture");
    }

    // An unknown tool is fed back as a structured error (once) so the model can recover, not a crash.
    [TestMethod]
    public async Task Orchestrator_UnknownTool_FeedsBackErrorAndContinues()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        var tools = new ArchiveToolService(archive).Tools();

        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "delete_everything", "{}") } } },
            new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "Sorry, I can't do that." } }
        });
        var orchestrator = new ChatOrchestrator(backend, tools);

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("delete it all") });

        Assert.AreEqual("Sorry, I can't do that.", result.Answer);
        Assert.IsFalse(result.Activity[0].Ok);
        var toolMsg = backend.Seen[1].Messages.LastOrDefault(m => m.Role == "tool");
        StringAssert.Contains(toolMsg!.Content!, "Unknown tool");
    }

    // A confirmed write actually mutates: set_favorite through an approving confirm gate pins the chat.
    [TestMethod]
    public async Task Orchestrator_ConfirmedWrite_FavoritesSession()
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-chat-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new ArchiveService(storePath: store);
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1", Title = "t" };
            var tools = new ArchiveToolService(svc).Tools();
            var backend = new FakeBackend(new[]
            {
                new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "set_favorite", "{\"id\":\"s1\",\"favorite\":true}") } } },
                new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "Favorited it." } }
            });
            var confirmed = false;
            var orchestrator = new ChatOrchestrator(backend, tools, confirm: (_, _) => { confirmed = true; return Task.FromResult(true); });

            var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("favorite s1") });

            Assert.IsTrue(confirmed, "the confirm gate was invoked for the mutation");
            Assert.IsTrue(svc.Store.Sessions["s1"].Pinned, "the chat is favorited after approval");
            Assert.IsTrue(result.Activity[0].Ok);
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // A declined write does not mutate.
    [TestMethod]
    public async Task Orchestrator_DeclinedWrite_DoesNotMutate()
    {
        var store = Path.Combine(Path.GetTempPath(), "clr-chat-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var svc = new ArchiveService(storePath: store);
            svc.Store.Sessions["s1"] = new ArchiveSession { Id = "s1" };
            var tools = new ArchiveToolService(svc).Tools();
            var backend = new FakeBackend(new[]
            {
                new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "rename_local", "{\"id\":\"s1\",\"title\":\"Nope\"}") } } },
                new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "Okay, left it." } }
            });
            var orchestrator = new ChatOrchestrator(backend, tools, confirm: (_, _) => Task.FromResult(false));

            await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("rename s1") });

            Assert.AreEqual("", svc.Store.Sessions["s1"].CustomTitle, "declined rename did not apply");
        }
        finally { if (File.Exists(store)) File.Delete(store); }
    }

    // resume_chat is a confirmed tool that hands the session id to the resume-in-terminal callback.
    [TestMethod]
    public async Task ResumeChat_ConfirmedTool_InvokesResumeCallback()
    {
        var svc = new ArchiveService(useBundledStore: true);
        await svc.LoadAsync();
        var fixture = svc.Search("fixture-b").First();
        string? resumed = null;
        var tools = new ArchiveToolService(svc, resumeChat: id => resumed = id).Tools();
        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "resume_chat", $"{{\"id\":\"{fixture.Id}\"}}") } } },
            new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "Resuming it." } }
        });
        var orchestrator = new ChatOrchestrator(backend, tools, confirm: (_, _) => Task.FromResult(true));

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("resume fixture-b") });

        Assert.AreEqual(fixture.Id, resumed, "the resume callback ran with the session id after confirmation");
        Assert.IsTrue(result.Activity.Any(a => a.Tool == "resume_chat" && a.Ok));
    }

    // The Claude CLI fallback is text-only (no tool-calling) and flattens the conversation cleanly.
    [TestMethod]
    public void ClaudexBackend_IsTextOnly_AndFlattensPrompt()
    {
        var backend = new ClaudexBackend("claude.exe");
        Assert.IsFalse(backend.SupportsTools, "the claude CLI fallback is text-only");

        var prompt = ClaudexBackend.FlattenPrompt(new[]
        {
            ChatMessage.System("You are a co-pilot."),
            ChatMessage.User("hi"),
            new ChatMessage { Role = "assistant", Content = "hello" },
            ChatMessage.User("summarize")
        });

        StringAssert.Contains(prompt, "You are a co-pilot.");
        StringAssert.Contains(prompt, "User: hi");
        StringAssert.Contains(prompt, "Assistant: hello");
        StringAssert.Contains(prompt, "User: summarize");
        Assert.IsTrue(prompt.TrimEnd().EndsWith("Assistant:"));
    }

    // read_chat returns a summary + paged messages (ids-first, capped), never the whole conversation raw.
    [TestMethod]
    public async Task ReadChat_ReturnsSummaryAndPagedMessages()
    {
        var svc = new ArchiveService(useBundledStore: true);
        await svc.LoadAsync();
        var fixture = svc.Search("fixture-b").First();
        var readChat = new ArchiveToolService(svc).Tools().First(t => t.Name == "read_chat");

        var args = JsonSerializer.Deserialize<JsonElement>($"{{\"id\":\"{fixture.Id}\"}}");
        var output = await readChat.Execute(args, default);
        var json = JsonSerializer.Serialize(output);

        StringAssert.Contains(json, "totalMessages");
        StringAssert.Contains(json, "untrusted_text");
        StringAssert.Contains(json, fixture.Id);
    }

    // A mutation tool with no confirmation gate is refused (write tools require explicit approval).
    [TestMethod]
    public async Task Orchestrator_MutationWithoutConfirm_IsRefused()
    {
        var ran = false;
        var mutationTool = new ChatTool
        {
            Spec = new ChatToolSpec("set_favorite", "fav", new { type = "object" }),
            IsMutation = true,
            Execute = (_, _) => { ran = true; return Task.FromResult<object>(new { ok = true }); }
        };
        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "set_favorite", "{\"id\":\"x\"}") } } },
            new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "ok" } }
        });
        var orchestrator = new ChatOrchestrator(backend, new[] { mutationTool }); // no confirm callback

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("favorite x") });

        Assert.IsFalse(ran, "a mutation must not run without a confirmation gate");
        Assert.IsFalse(result.Activity[0].Ok);
    }
}
