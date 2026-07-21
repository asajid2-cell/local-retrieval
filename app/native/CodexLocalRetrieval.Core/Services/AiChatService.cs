using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using CodexLocalRetrieval.Core.Models;

namespace CodexLocalRetrieval.Core.Services;

public sealed class AiChatService
{
    private readonly HttpClient _http;
    private readonly JsonSerializerOptions _jsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AiChatService(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
    }

    public async Task<string> TestAsync(AiProviderSettings provider, string apiKey, CancellationToken cancellationToken = default)
    {
        return await CompleteAsync(
            provider,
            apiKey,
            "You are a connection test. Reply with exactly: OK",
            "Reply with OK.",
            32,
            cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        AiProviderSettings provider,
        string apiKey,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new InvalidOperationException("Provider base URL is required.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API key is required.");

        using var request = new HttpRequestMessage(HttpMethod.Get, BuildModelsEndpoint(provider.BaseUrl));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Model detection returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Model detection returned an unexpected response.");
        }

        return data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "")
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<string> AskArchiveAsync(
        AiProviderSettings provider,
        string apiKey,
        string question,
        IEnumerable<ArchiveSearchHit> hits,
        CancellationToken cancellationToken = default)
    {
        var context = BuildContext(hits);
        var system = """
            You answer questions about a local chat archive.
            Use only the provided archive excerpts.
            Cite source chat titles and source labels when relevant.
            If the excerpts do not contain enough evidence, say what is missing.
            Keep the answer concise and practical.
            """;
        var user = $"Question:\n{question}\n\nArchive excerpts:\n{context}";
        return await CompleteAsync(provider, apiKey, system, user, 900, cancellationToken);
    }

    private async Task<string> CompleteAsync(
        AiProviderSettings provider,
        string apiKey,
        string system,
        string user,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider.BaseUrl)) throw new InvalidOperationException("Provider base URL is required.");
        if (string.IsNullOrWhiteSpace(provider.Model)) throw new InvalidOperationException("Provider model is required.");
        if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("API key is required.");

        var endpoint = BuildChatCompletionsEndpoint(provider.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = provider.Model,
            messages = new[]
            {
                new { role = "system", content = system },
                new { role = "user", content = user }
            },
            stream = false,
            max_tokens = maxTokens
        }), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Provider returned {(int)response.StatusCode}: {TrimForError(body)}");
        }

        using var document = JsonDocument.Parse(body);
        var content = document.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        return string.IsNullOrWhiteSpace(content) ? "(empty response)" : content.Trim();
    }

    private static string BuildContext(IEnumerable<ArchiveSearchHit> hits)
    {
        var builder = new StringBuilder();
        foreach (var hit in hits.Take(10))
        {
            builder.AppendLine($"Source: {hit.Session.DisplayTitle} [{hit.SourceLabel}]");
            builder.AppendLine($"Path: {hit.Session.SourcePath}");
            builder.AppendLine(hit.Snippet);
            builder.AppendLine();
        }
        return builder.Length == 0 ? "No local matches were found." : builder.ToString();
    }

    private static Uri BuildChatCompletionsEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            return new Uri(trimmed);
        }
        return new Uri($"{trimmed}/chat/completions");
    }

    private static Uri BuildModelsEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/chat/completions".Length];
        }
        return new Uri($"{trimmed}/models");
    }

    private static string TrimForError(string value)
    {
        var clean = value.Replace("\r", " ").Replace("\n", " ").Trim();
        return clean[..Math.Min(500, clean.Length)];
    }
}
