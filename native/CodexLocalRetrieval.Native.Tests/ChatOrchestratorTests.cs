using System.Net;
using System.Text.Json;
using CodexLocalRetrieval.Core.Agents;
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

    // Honors the cancellation token (throws if cancelled) so we can prove the orchestrator propagates it.
    private sealed class CancelAwareBackend : IChatBackend
    {
        public string Name => "cancel";
        public bool SupportsTools => true;
        public Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatToolSpec> t, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "ok" } });
        }
    }

    // Never returns a final answer: each call asks for a *distinct* search (so the repeat-guard never
    // trips first), which is how we isolate the tool-round limit.
    private sealed class LoopingBackend : IChatBackend
    {
        private int _n;
        public string Name => "loop";
        public bool SupportsTools => true;
        public int Calls => _n;
        public Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> m, IReadOnlyList<ChatToolSpec> t, CancellationToken ct)
        {
            var q = _n++;
            return Task.FromResult(new BackendReply
            {
                Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c" + q, "search_chats", "{\"query\":\"q" + q + "\"}") } }
            });
        }
    }

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

    [TestMethod]
    public async Task BoundedTextCapture_CapsMemoryButDrainsTheProducer()
    {
        var reader = new CaptureTrackingReader(new string('x', 128 * 1024));

        var result = await BoundedTextCapture.ReadToEndAsync(reader, 4096);

        Assert.AreEqual(4096, result.Text.Length);
        Assert.IsTrue(result.Truncated);
        Assert.IsTrue(reader.FullyDrained);
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

    // The repeat-guard stops the loop if the model keeps making the identical call (a stuck model
    // shouldn't burn the whole round budget on the same no-op).
    [TestMethod]
    public async Task Orchestrator_RepeatedIdenticalCall_StopsWithError()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        var same = Call("c", "search_chats", "{\"query\":\"x\"}");
        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { same } } },
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { same } } },
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { same } } }
        });
        var orchestrator = new ChatOrchestrator(backend, new ArchiveToolService(archive).Tools());

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("search x") });

        Assert.IsTrue(result.Stopped);
        StringAssert.Contains(result.Error!, "repeated");
    }

    // A side-effecting tool (resume_chat, limit 1/message) is rate-limited if the model calls it
    // twice in one assistant turn — only the first runs, the second is refused back to the model.
    [TestMethod]
    public async Task Orchestrator_SideEffectQuota_RateLimitsSecondResume()
    {
        var svc = new ArchiveService(useBundledStore: true);
        await svc.LoadAsync();
        var fixture = svc.Search("fixture-b").First();
        var resumes = 0;
        var tools = new ArchiveToolService(svc, resumeChat: _ => resumes++).Tools();
        var twoResumes = new ChatMessage
        {
            Role = "assistant",
            ToolCalls = new() { Call("c1", "resume_chat", $"{{\"id\":\"{fixture.Id}\"}}"), Call("c2", "resume_chat", $"{{\"id\":\"{fixture.Id}\"}}") }
        };
        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = twoResumes },
            new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "done" } }
        });
        var orchestrator = new ChatOrchestrator(backend, tools, confirm: (_, _) => Task.FromResult(true));

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("resume it twice") });

        Assert.AreEqual(1, resumes, "only the first resume actually ran");
        Assert.IsTrue(result.Activity.Any(a => a.Tool == "resume_chat" && !a.Ok), "the second was rate limited");
    }

    // Invalid JSON arguments are fed back to the model as a structured error (so it can retry), not thrown.
    [TestMethod]
    public async Task Orchestrator_InvalidJsonArgs_FedBackAsError()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        var backend = new FakeBackend(new[]
        {
            new BackendReply { Message = new ChatMessage { Role = "assistant", ToolCalls = new() { Call("c1", "search_chats", "{not valid json") } } },
            new BackendReply { Message = new ChatMessage { Role = "assistant", Content = "let me try again" } }
        });
        var orchestrator = new ChatOrchestrator(backend, new ArchiveToolService(archive).Tools());

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("search") });

        Assert.IsFalse(result.Activity[0].Ok);
        var toolMsg = backend.Seen[1].Messages.LastOrDefault(m => m.Role == "tool");
        StringAssert.Contains(toolMsg!.Content!, "valid JSON");
    }

    // If the model never stops calling tools, the loop bails out at the round limit instead of running forever.
    [TestMethod]
    public async Task Orchestrator_NeverStops_HitsRoundLimit()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        var backend = new LoopingBackend();
        var orchestrator = new ChatOrchestrator(backend, new ArchiveToolService(archive).Tools());

        var result = await orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("loop forever") });

        Assert.IsTrue(result.Stopped);
        StringAssert.Contains(result.Error!, "tool-round limit");
        Assert.AreEqual(ChatOrchestrator.MaxToolRounds, backend.Calls);
    }

    // When a guardrail stops mid-turn, EVERY tool_call in the assistant message must still get a
    // role:tool reply — otherwise the shared history has an unanswered tool_call_id and the provider
    // rejects the NEXT request, bricking the co-pilot until "New chat".
    [TestMethod]
    public async Task Orchestrator_GuardrailStop_LeavesNoUnansweredToolCall()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        // Four identical calls: the repeat-guard trips on the 3rd, leaving the 3rd and 4th unanswered
        // unless the orchestrator backfills replies for them.
        var fourCalls = new ChatMessage
        {
            Role = "assistant",
            ToolCalls = new()
            {
                Call("c0", "search_chats", "{\"query\":\"x\"}"),
                Call("c1", "search_chats", "{\"query\":\"x\"}"),
                Call("c2", "search_chats", "{\"query\":\"x\"}"),
                Call("c3", "search_chats", "{\"query\":\"x\"}")
            }
        };
        var backend = new FakeBackend(new[] { new BackendReply { Message = fourCalls } });
        var orchestrator = new ChatOrchestrator(backend, new ArchiveToolService(archive).Tools());
        var messages = new List<ChatMessage> { ChatMessage.User("x") };

        var result = await orchestrator.RunAsync(messages);

        Assert.IsTrue(result.Stopped);
        var demanded = messages.Where(m => m.Role == "assistant" && m.ToolCalls is { Count: > 0 })
            .SelectMany(m => m.ToolCalls!).Select(c => c.Id).ToHashSet();
        var answered = messages.Where(m => m.Role == "tool").Select(m => m.ToolCallId).ToHashSet();
        Assert.IsTrue(demanded.IsSubsetOf(answered), "every assistant tool_call must have a role:tool reply");
        Assert.AreEqual(4, demanded.Count);
    }

    // A user Stop (cancelled token) propagates as OperationCanceledException, not a swallowed
    // result.Error — so the UI can show "Stopped." rather than a model-error message.
    [TestMethod]
    public async Task Orchestrator_Cancellation_Propagates()
    {
        var archive = new ArchiveService(useBundledStore: true);
        await archive.LoadAsync();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var orchestrator = new ChatOrchestrator(new CancelAwareBackend(), new ArchiveToolService(archive).Tools());

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() =>
            orchestrator.RunAsync(new List<ChatMessage> { ChatMessage.User("hi") }, cts.Token));
    }

    private sealed class CaptureTrackingReader(string text) : TextReader
    {
        private int _offset;

        public bool FullyDrained { get; private set; }

        public override ValueTask<int> ReadAsync(
            Memory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_offset >= text.Length)
            {
                FullyDrained = true;
                return ValueTask.FromResult(0);
            }

            var count = Math.Min(buffer.Length, text.Length - _offset);
            text.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return ValueTask.FromResult(count);
        }
    }
}
