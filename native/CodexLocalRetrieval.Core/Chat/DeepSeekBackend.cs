using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CodexLocalRetrieval.Core.Chat;

// OpenAI-compatible tool-calling backend (DeepSeek primary; also fits OpenAI). Non-streaming,
// tool_choice=auto when tools are present (per DeepSeek's current tool-calls API). Owns only the HTTP
// round-trip; the ChatOrchestrator owns the loop. Arguments produced by the model are NOT trusted
// here — the orchestrator/tools validate them.
public sealed class DeepSeekBackend : IChatBackend
{
    private static readonly JsonSerializerOptions Wire = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _model;
    private readonly string _apiKey;
    private readonly int _maxTokens;

    public DeepSeekBackend(string baseUrl, string model, string apiKey, HttpClient? http = null, int maxTokens = 2000)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _baseUrl = baseUrl;
        _model = model;
        _apiKey = apiKey;
        _maxTokens = maxTokens;
    }

    public string Name => "deepseek";
    public bool SupportsTools => true;

    public async Task<BackendReply> CompleteAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ChatToolSpec> tools, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey)) throw new InvalidOperationException("No API key. Set DEEPSEEK_API_KEY.");

        var payload = new Dictionary<string, object?>
        {
            ["model"] = _model,
            ["messages"] = messages,
            ["stream"] = false,
            ["max_tokens"] = _maxTokens
        };
        if (tools.Count > 0)
        {
            payload["tools"] = tools.Select(t => new
            {
                type = "function",
                function = new { name = t.Name, description = t.Description, parameters = t.ParametersSchema }
            }).ToList();
            payload["tool_choice"] = "auto";
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_baseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload, Wire), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"DeepSeek returned {(int)response.StatusCode}: {Trim(body)}");

        return Parse(body);
    }

    internal static BackendReply Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        // Some OpenAI-compatible providers return an error-shaped body with HTTP 200.
        if (root.TryGetProperty("error", out var error))
            throw new InvalidOperationException("Provider error: " +
                (error.TryGetProperty("message", out var em) ? em.GetString() : error.ToString()));

        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Provider returned no choices.");
        var choice = choices[0];
        if (!choice.TryGetProperty("message", out var message))
            throw new InvalidOperationException("Provider returned no message.");

        string? content = message.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

        List<ToolCall>? toolCalls = null;
        if (message.TryGetProperty("tool_calls", out var tc) && tc.ValueKind == JsonValueKind.Array && tc.GetArrayLength() > 0)
        {
            toolCalls = new List<ToolCall>();
            foreach (var t in tc.EnumerateArray())
            {
                if (!t.TryGetProperty("function", out var fn)) continue; // skip malformed tool calls
                toolCalls.Add(new ToolCall
                {
                    Id = t.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "",
                    Function = new ToolCallFunction
                    {
                        Name = fn.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                        Arguments = fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() ?? "{}" : "{}"
                    }
                });
            }
            if (toolCalls.Count == 0) toolCalls = null;
        }

        var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() : null;
        // A truncated final answer (no tool calls) should not silently read as complete.
        if (finish == "length" && toolCalls is null && !string.IsNullOrEmpty(content))
            content += "\n\n_[Response was cut off — ask me to continue.]_";

        return new BackendReply
        {
            Message = new ChatMessage { Role = "assistant", Content = content, ToolCalls = toolCalls },
            FinishReason = finish
        };
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return new Uri(trimmed);
        return new Uri($"{trimmed}/chat/completions");
    }

    private static string Trim(string s)
    {
        var clean = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean[..Math.Min(400, clean.Length)];
    }
}
