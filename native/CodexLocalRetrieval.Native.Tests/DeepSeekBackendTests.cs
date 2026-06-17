using System.Net;
using CodexLocalRetrieval.Core.Chat;

namespace CodexLocalRetrieval.Native.Tests;

// The HTTP backend in isolation: transient-error retry/backoff and the defensive Parse paths that
// keep a weird provider response from crashing the co-pilot.
[TestClass]
public sealed class DeepSeekBackendTests
{
    // Returns queued (status, body) pairs and counts requests, so retry behavior is observable.
    private sealed class StatusStub : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Code, string Body)> _responses;
        public StatusStub(params (HttpStatusCode, string)[] responses) => _responses = new(responses);
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var (code, body) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.InternalServerError, "{}");
            return Task.FromResult(new HttpResponseMessage(code) { Content = new StringContent(body) });
        }
    }

    private static string Final(string content) =>
        "{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"role\":\"assistant\",\"content\":\"" + content + "\"}}]}";

    // No-op delay so the backoff never actually sleeps in tests.
    private static DeepSeekBackend Backend(StatusStub stub, int maxAttempts = 3) =>
        new("https://api.deepseek.com", "deepseek-v4-flash", "sk-test", new HttpClient(stub),
            maxAttempts: maxAttempts, delay: (_, _) => Task.CompletedTask);

    [TestMethod]
    public async Task Retries_TransientError_ThenSucceeds()
    {
        var stub = new StatusStub((HttpStatusCode.ServiceUnavailable, "busy"), (HttpStatusCode.OK, Final("hi")));
        var reply = await Backend(stub).CompleteAsync(new[] { ChatMessage.User("hi") }, Array.Empty<ChatToolSpec>(), default);

        Assert.AreEqual("hi", reply.Message.Content);
        Assert.AreEqual(2, stub.Calls, "it retried once after the 503");
    }

    [TestMethod]
    public async Task Retries_429RateLimit()
    {
        var stub = new StatusStub((HttpStatusCode.TooManyRequests, "slow down"), (HttpStatusCode.OK, Final("ok")));
        var reply = await Backend(stub).CompleteAsync(new[] { ChatMessage.User("hi") }, Array.Empty<ChatToolSpec>(), default);
        Assert.AreEqual("ok", reply.Message.Content);
        Assert.AreEqual(2, stub.Calls);
    }

    [TestMethod]
    public async Task Exhausts_Attempts_ThenThrows()
    {
        var stub = new StatusStub(
            (HttpStatusCode.ServiceUnavailable, "down"),
            (HttpStatusCode.ServiceUnavailable, "down"),
            (HttpStatusCode.ServiceUnavailable, "down"));
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Backend(stub, maxAttempts: 3).CompleteAsync(new[] { ChatMessage.User("hi") }, Array.Empty<ChatToolSpec>(), default));

        StringAssert.Contains(ex.Message, "503");
        Assert.AreEqual(3, stub.Calls, "it tried exactly maxAttempts times");
    }

    [TestMethod]
    public async Task FailsFast_OnClientError()
    {
        var stub = new StatusStub((HttpStatusCode.BadRequest, "bad key"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            Backend(stub).CompleteAsync(new[] { ChatMessage.User("hi") }, Array.Empty<ChatToolSpec>(), default));

        Assert.AreEqual(1, stub.Calls, "a 400 is not retried");
    }

    // ---- defensive Parse ----

    [TestMethod]
    public void Parse_ErrorShaped200_Throws()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            DeepSeekBackend.Parse("{\"error\":{\"message\":\"invalid api key\"}}"));
        StringAssert.Contains(ex.Message, "invalid api key");
    }

    [TestMethod]
    public void Parse_MissingChoices_Throws()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => DeepSeekBackend.Parse("{\"id\":\"x\"}"));
    }

    [TestMethod]
    public void Parse_LengthFinish_AppendsCutoffNote()
    {
        var reply = DeepSeekBackend.Parse(
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"role\":\"assistant\",\"content\":\"half a thoug\"}}]}");
        StringAssert.Contains(reply.Message.Content!, "half a thoug");
        StringAssert.Contains(reply.Message.Content!, "cut off");
        Assert.AreEqual("length", reply.FinishReason);
    }

    [TestMethod]
    public void Parse_MalformedToolCall_IsSkipped()
    {
        // one tool call has no function object; the parser drops it rather than crashing
        var reply = DeepSeekBackend.Parse(
            "{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"role\":\"assistant\",\"content\":null," +
            "\"tool_calls\":[{\"id\":\"bad\"},{\"id\":\"good\",\"function\":{\"name\":\"search_chats\",\"arguments\":\"{}\"}}]}}]}");
        Assert.IsNotNull(reply.Message.ToolCalls);
        Assert.AreEqual(1, reply.Message.ToolCalls!.Count);
        Assert.AreEqual("search_chats", reply.Message.ToolCalls[0].Function.Name);
    }
}
