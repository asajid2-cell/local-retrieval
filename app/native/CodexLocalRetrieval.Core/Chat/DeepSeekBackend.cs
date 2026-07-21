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
    private readonly int _maxAttempts;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public DeepSeekBackend(string baseUrl, string model, string apiKey, HttpClient? http = null, int maxTokens = 2000,
        int maxAttempts = 3, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(90) };
        _baseUrl = baseUrl;
        _model = model;
        _apiKey = apiKey;
        _maxTokens = maxTokens;
        _maxAttempts = Math.Max(1, maxAttempts);
        _delay = delay ?? ((d, ct) => Task.Delay(d, ct)); // injectable so tests don't actually sleep
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

        // Serialize once; each attempt needs a fresh request/content (a sent one can't be reused).
        var json = JsonSerializer.Serialize(payload, Wire);
        InvalidOperationException? lastTransient = null;

        for (var attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_baseUrl));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _http.SendAsync(request, cancellationToken);
            }
            catch (HttpRequestException ex) // connection refused / DNS / TLS — transient, worth a retry
            {
                lastTransient = new InvalidOperationException("Network error talking to DeepSeek: " + ex.Message, ex);
                if (attempt < _maxAttempts) { await Backoff(attempt, null, cancellationToken); continue; }
                throw lastTransient;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) // HttpClient timeout, not a user cancel
            {
                lastTransient = new InvalidOperationException("DeepSeek request timed out.");
                if (attempt < _maxAttempts) { await Backoff(attempt, null, cancellationToken); continue; }
                throw lastTransient;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode) return Parse(body);

                // Retry rate-limit / transient server errors; fail fast on other 4xx (bad key, bad request).
                if (IsTransient(response.StatusCode) && attempt < _maxAttempts)
                {
                    lastTransient = new InvalidOperationException($"DeepSeek returned {(int)response.StatusCode}: {Trim(body)}");
                    await Backoff(attempt, response.Headers.RetryAfter, cancellationToken);
                    continue;
                }
                throw new InvalidOperationException($"DeepSeek returned {(int)response.StatusCode}: {Trim(body)}");
            }
        }

        throw lastTransient ?? new InvalidOperationException("DeepSeek request failed.");
    }

    private static bool IsTransient(System.Net.HttpStatusCode code) => (int)code switch
    {
        408 or 429 or 500 or 502 or 503 or 504 => true,
        _ => false
    };

    // Exponential backoff, but honor a server Retry-After when present. Bounded so a hostile header
    // can't park the UI on "Thinking..." indefinitely.
    private async Task Backoff(int attempt, System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, CancellationToken ct)
    {
        TimeSpan delay;
        if (retryAfter?.Delta is TimeSpan d) delay = d;
        else if (retryAfter?.Date is DateTimeOffset when) delay = when - DateTimeOffset.UtcNow;
        else delay = TimeSpan.FromMilliseconds(400 * Math.Pow(2, attempt - 1));
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        if (delay > TimeSpan.FromSeconds(20)) delay = TimeSpan.FromSeconds(20);
        await _delay(delay, ct);
    }

    public static BackendReply Parse(string body) // public so it can be unit-tested directly
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
