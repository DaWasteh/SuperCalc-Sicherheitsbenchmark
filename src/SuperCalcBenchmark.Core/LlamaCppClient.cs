using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class LlamaCppClient : IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public LlamaCppClient(TimeSpan? timeout = null, HttpMessageHandler? handler = null)
    {
        if (handler is null)
        {
            _httpClient = new HttpClient();
            _ownsClient = true;
        }
        else
        {
            _httpClient = new HttpClient(handler, disposeHandler: false);
        }

        _httpClient.Timeout = timeout ?? TimeSpan.FromMinutes(20);
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "no-key");
    }

    public async Task<IReadOnlyList<string>> GetModelsAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        var endpoints = new[] { "/v1/models", "/models" };
        List<Exception> errors = [];

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await _httpClient.GetAsync(BuildUri(serverUrl, endpoint), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
                {
                    using var retry = new HttpClient { Timeout = _httpClient.Timeout };
                    using var retryResponse = await retry.GetAsync(BuildUri(serverUrl, endpoint), cancellationToken).ConfigureAwait(false);
                    if (!retryResponse.IsSuccessStatusCode)
                    {
                        errors.Add(new HttpRequestException($"GET {endpoint} failed with {(int)retryResponse.StatusCode} {retryResponse.ReasonPhrase}."));
                        continue;
                    }

                    var retryBody = await retryResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    return ParseModels(retryBody);
                }

                if (!response.IsSuccessStatusCode)
                {
                    errors.Add(new HttpRequestException($"GET {endpoint} failed with {(int)response.StatusCode} {response.ReasonPhrase}."));
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                return ParseModels(body);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                errors.Add(ex);
            }
        }

        throw new InvalidOperationException(
            $"Could not fetch models from '{serverUrl}'. Is llama-server running on that URL? Errors: {string.Join(" | ", errors.Select(e => e.Message))}");
    }

    public async Task<ChatCompletionResult> CreateChatCompletionAsync(
        string serverUrl,
        string model,
        string systemPrompt,
        string userPrompt,
        BenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model id is required.", nameof(model));
        }

        if (!options.SkipResponseFormat)
        {
            var withFormat = BuildChatRequest(model, systemPrompt, userPrompt, options, includeResponseFormat: true);
            var first = await PostChatCompletionAsync(serverUrl, withFormat, cancellationToken).ConfigureAwait(false);
            if (first.Success)
            {
                return first.Result! with { UsedResponseFormat = true };
            }

            var withoutFormat = BuildChatRequest(model, systemPrompt, userPrompt, options, includeResponseFormat: false);
            var second = await PostChatCompletionAsync(serverUrl, withoutFormat, cancellationToken).ConfigureAwait(false);
            if (second.Success)
            {
                return second.Result! with { UsedResponseFormat = false, RetriedWithoutResponseFormat = true };
            }

            throw new InvalidOperationException($"Chat completion failed. With response_format: {first.Error}; without response_format: {second.Error}");
        }

        var request = BuildChatRequest(model, systemPrompt, userPrompt, options, includeResponseFormat: false);
        var completion = await PostChatCompletionAsync(serverUrl, request, cancellationToken).ConfigureAwait(false);
        if (completion.Success)
        {
            return completion.Result!;
        }

        throw new InvalidOperationException($"Chat completion failed: {completion.Error}");
    }

    private static object BuildChatRequest(string model, string systemPrompt, string userPrompt, BenchmarkOptions options, bool includeResponseFormat)
    {
        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        };

        if (includeResponseFormat)
        {
            return new
            {
                model,
                messages,
                temperature = options.Temperature,
                top_p = options.TopP,
                seed = options.Seed,
                max_tokens = options.MaxTokens,
                response_format = new { type = "json_object" }
            };
        }

        return new
        {
            model,
            messages,
            temperature = options.Temperature,
            top_p = options.TopP,
            seed = options.Seed,
            max_tokens = options.MaxTokens
        };
    }

    private async Task<(bool Success, ChatCompletionResult? Result, string Error)> PostChatCompletionAsync(
        string serverUrl,
        object request,
        CancellationToken cancellationToken)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(BuildUri(serverUrl, "/v1/chat/completions"), content, cancellationToken).ConfigureAwait(false);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return (false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {TrimForError(responseText)}");
        }

        try
        {
            var assistantContent = ExtractAssistantContent(responseText);
            return (true, new ChatCompletionResult
            {
                AssistantContent = assistantContent,
                RawResponse = responseText,
                RequestJson = requestJson,
                UsedResponseFormat = requestJson.Contains("response_format", StringComparison.Ordinal),
                RetriedWithoutResponseFormat = false
            }, string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (false, null, $"Could not parse chat response: {ex.Message}. Raw: {TrimForError(responseText)}");
        }
    }

    private static string ExtractAssistantContent(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            if (firstChoice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content))
            {
                return content.ValueKind == JsonValueKind.String ? content.GetString() ?? string.Empty : content.GetRawText();
            }

            if (firstChoice.TryGetProperty("text", out var text))
            {
                return text.GetString() ?? text.GetRawText();
            }
        }

        if (root.TryGetProperty("content", out var directContent))
        {
            return directContent.GetString() ?? directContent.GetRawText();
        }

        throw new InvalidOperationException("The response does not contain choices[0].message.content.");
    }

    private static IReadOnlyList<string> ParseModels(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var models = new List<string>();

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id))
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        models.Add(value);
                    }
                }
                else if (item.ValueKind == JsonValueKind.String)
                {
                    models.Add(item.GetString()!);
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    models.Add(item.GetString()!);
                }
                else if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("id", out var id))
                {
                    var value = id.GetString();
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        models.Add(value);
                    }
                }
            }
        }

        return models.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static Uri BuildUri(string serverUrl, string endpoint)
    {
        var baseUri = serverUrl.EndsWith('/') ? serverUrl : serverUrl + "/";
        return new Uri(new Uri(baseUri), endpoint.TrimStart('/'));
    }

    private static string TrimForError(string value)
    {
        const int maxLength = 1000;
        if (value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
