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

    public async Task<int?> GetServerContextSizeAsync(string serverUrl, CancellationToken cancellationToken = default)
    {
        var endpoints = new[] { "/props", "/v1/models", "/slots" };

        foreach (var endpoint in endpoints)
        {
            try
            {
                using var response = await _httpClient.GetAsync(BuildUri(serverUrl, endpoint), cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var nCtx = ParseContextSize(body);
                if (nCtx is > 0)
                {
                    return nCtx;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Best-effort diagnostics only; benchmark runs must still work on OpenAI-compatible servers without /props.
            }
        }

        return null;
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

        var errors = new List<string>();
        ChatCompletionResult? firstSuccessful = null;

        foreach (var attempt in BuildAttempts(options))
        {
            var request = BuildChatRequest(model, systemPrompt, userPrompt, options, attempt.IncludeResponseFormat, attempt.IncludeThinkingControl);
            var completion = await PostChatCompletionAsync(serverUrl, request, cancellationToken).ConfigureAwait(false);
            if (!completion.Success)
            {
                errors.Add($"{attempt.Label}: {completion.Error}");
                continue;
            }

            var result = completion.Result! with
            {
                UsedResponseFormat = attempt.IncludeResponseFormat,
                RetriedWithoutResponseFormat = attempt.RetriedWithoutResponseFormat,
                UsedThinkingControl = attempt.IncludeThinkingControl,
                RetriedWithoutThinkingControl = attempt.RetriedWithoutThinkingControl
            };

            firstSuccessful ??= result;
            if (!ReturnedOnlyReasoning(result))
            {
                return result;
            }

            errors.Add($"{attempt.Label}: server returned empty assistant content with {result.ReasoningContent.Length} chars reasoning_content (finish_reason='{result.FinishReason}').");
        }

        if (firstSuccessful is not null)
        {
            return firstSuccessful;
        }

        throw new InvalidOperationException($"Chat completion failed. Attempts: {string.Join(" | ", errors)}");
    }

    private static IReadOnlyList<ChatRequestAttempt> BuildAttempts(BenchmarkOptions options)
    {
        var attempts = new List<ChatRequestAttempt>();

        void Add(bool includeResponseFormat, bool includeThinkingControl)
        {
            if (attempts.Any(a => a.IncludeResponseFormat == includeResponseFormat && a.IncludeThinkingControl == includeThinkingControl))
            {
                return;
            }

            attempts.Add(new ChatRequestAttempt(
                includeResponseFormat,
                includeThinkingControl,
                RetriedWithoutResponseFormat: !includeResponseFormat && !options.SkipResponseFormat,
                RetriedWithoutThinkingControl: !includeThinkingControl && options.DisableThinking));
        }

        if (!options.SkipResponseFormat)
        {
            Add(includeResponseFormat: true, includeThinkingControl: options.DisableThinking);
            Add(includeResponseFormat: false, includeThinkingControl: options.DisableThinking);
            if (options.DisableThinking)
            {
                Add(includeResponseFormat: true, includeThinkingControl: false);
                Add(includeResponseFormat: false, includeThinkingControl: false);
            }
        }
        else
        {
            Add(includeResponseFormat: false, includeThinkingControl: options.DisableThinking);
            if (options.DisableThinking)
            {
                Add(includeResponseFormat: false, includeThinkingControl: false);
            }
        }

        return attempts;
    }

    private static object BuildChatRequest(
        string model,
        string systemPrompt,
        string userPrompt,
        BenchmarkOptions options,
        bool includeResponseFormat,
        bool includeThinkingControl)
    {
        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        };

        var request = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = options.Temperature,
            ["top_p"] = options.TopP,
            ["seed"] = options.Seed,
            ["max_tokens"] = options.MaxTokens
        };

        if (includeResponseFormat)
        {
            request["response_format"] = new { type = "json_object" };
        }

        if (includeThinkingControl)
        {
            request["chat_template_kwargs"] = new { enable_thinking = false };
        }

        return request;
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
            var extracted = ExtractChatMessageContent(responseText);
            return (true, new ChatCompletionResult
            {
                AssistantContent = extracted.AssistantContent,
                ReasoningContent = extracted.ReasoningContent,
                FinishReason = extracted.FinishReason,
                RawResponse = responseText,
                RequestJson = requestJson,
                UsedResponseFormat = requestJson.Contains("response_format", StringComparison.Ordinal),
                UsedThinkingControl = requestJson.Contains("chat_template_kwargs", StringComparison.Ordinal),
                RetriedWithoutResponseFormat = false,
                RetriedWithoutThinkingControl = false
            }, string.Empty);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return (false, null, $"Could not parse chat response: {ex.Message}. Raw: {TrimForError(responseText)}");
        }
    }

    private static (string AssistantContent, string ReasoningContent, string FinishReason) ExtractChatMessageContent(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array && choices.GetArrayLength() > 0)
        {
            var firstChoice = choices[0];
            var finishReason = firstChoice.TryGetProperty("finish_reason", out var finishReasonElement)
                ? ReadElementAsString(finishReasonElement)
                : string.Empty;

            if (firstChoice.TryGetProperty("message", out var message))
            {
                var assistantContent = message.TryGetProperty("content", out var content)
                    ? ReadElementAsString(content)
                    : string.Empty;
                var reasoningContent = message.TryGetProperty("reasoning_content", out var reasoning)
                    ? ReadElementAsString(reasoning)
                    : string.Empty;

                if (!string.IsNullOrEmpty(assistantContent) || !string.IsNullOrEmpty(reasoningContent) || message.TryGetProperty("content", out _))
                {
                    return (assistantContent, reasoningContent, finishReason);
                }
            }

            if (firstChoice.TryGetProperty("text", out var text))
            {
                return (ReadElementAsString(text), string.Empty, finishReason);
            }
        }

        if (root.TryGetProperty("content", out var directContent))
        {
            return (ReadElementAsString(directContent), string.Empty, string.Empty);
        }

        throw new InvalidOperationException("The response does not contain choices[0].message.content or reasoning_content.");
    }

    private static string ReadElementAsString(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
            JsonValueKind.String => element.GetString() ?? string.Empty,
            _ => element.GetRawText()
        };
    }

    private static bool ReturnedOnlyReasoning(ChatCompletionResult result)
    {
        return string.IsNullOrWhiteSpace(result.AssistantContent) && !string.IsNullOrWhiteSpace(result.ReasoningContent);
    }

    private sealed record ChatRequestAttempt(
        bool IncludeResponseFormat,
        bool IncludeThinkingControl,
        bool RetriedWithoutResponseFormat,
        bool RetriedWithoutThinkingControl)
    {
        public string Label
        {
            get
            {
                var responseFormat = IncludeResponseFormat ? "with response_format" : "without response_format";
                var thinking = IncludeThinkingControl ? "with thinking disabled" : "without thinking control";
                return $"{responseFormat}, {thinking}";
            }
        }
    }

    private static int? ParseContextSize(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Object)
        {
            if (root.TryGetProperty("default_generation_settings", out var settings) &&
                settings.TryGetProperty("n_ctx", out var settingsContext) &&
                settingsContext.TryGetInt32(out var nCtx))
            {
                return nCtx;
            }

            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("meta", out var meta) &&
                        meta.TryGetProperty("n_ctx", out var metaContext) &&
                        metaContext.TryGetInt32(out nCtx))
                    {
                        return nCtx;
                    }
                }
            }
        }

        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object &&
                    item.TryGetProperty("n_ctx", out var slotContext) &&
                    slotContext.TryGetInt32(out var nCtx))
                {
                    return nCtx;
                }
            }
        }

        return null;
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
