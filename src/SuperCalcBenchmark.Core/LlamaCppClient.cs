using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class LlamaCppClient : IDisposable
{
    private const int LoopCheckMinimumChars = 1_500;
    private const int LoopCheckIntervalChars = 750;
    private const int LoopConfirmationChecksRequired = 4;
    private const string LoopDetectedFinishReason = "loop_detected";
    private const string ManualAbortFinishReason = "manual_abort";

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

        _httpClient.Timeout = timeout ?? BenchmarkDefaults.OfficialRequestTimeout;
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

    public async Task<int?> CountTokensAsync(string serverUrl, string model, string text, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var requestJson = JsonSerializer.Serialize(new
        {
            model,
            content = text,
            add_special = false,
            parse_special = true
        }, JsonOptions);
        using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync(BuildUri(serverUrl, "/tokenize"), content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(responseText);
            return document.RootElement.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Array
                ? tokens.GetArrayLength()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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

    /// <summary>
    /// Best-effort lookup of the loaded model's file-type (quantization) name as exposed by
    /// llama-server via GET /v1/models data[].meta.ftype (llama.cpp PR #25134, build b9860+).
    /// Returns values such as "Q8_0", "Q4_K - Medium", "(guessed) Q8_0" or
    /// "IQ3_S mix - 3.66 bpw". Returns null when the server is unreachable, the endpoint or
    /// the meta.ftype field is absent (older builds, OpenAI-compatible gateways without meta),
    /// or the model id cannot be matched — callers then fall back to name-based detection.
    /// Never throws.
    /// </summary>
    public async Task<string?> GetModelFtypeAsync(string serverUrl, string? modelId, CancellationToken cancellationToken = default)
    {
        // /v1/models is the canonical OpenAI-compatible endpoint; /models is the legacy
        // llama.cpp alias. Try both so the lookup works across builds.
        var endpoints = new[] { "/v1/models", "/models" };

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
                var ftype = TryExtractModelFtype(body, modelId);
                if (!string.IsNullOrWhiteSpace(ftype))
                {
                    return ftype;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // Best-effort diagnostics only; quant detection must still work without the endpoint.
            }
        }

        return null;
    }

    /// <summary>
    /// Extracts meta.ftype for the requested model id from a /v1/models response body.
    /// Falls back to the single available entry when the id is blank or cannot be matched
    /// (typical for single-model llama-server instances that report only one model).
    /// </summary>
    private static string? TryExtractModelFtype(string body, string? requestedModelId)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var requested = (requestedModelId ?? string.Empty).Trim();

        JsonElement? fallback = null; // first entry carrying an ftype, used when no id matches

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var ftype = ReadMetaFtype(item);
            if (string.IsNullOrWhiteSpace(ftype))
            {
                continue;
            }

            fallback ??= item;

            if (item.TryGetProperty("id", out var idElement) &&
                idElement.ValueKind == JsonValueKind.String &&
                string.Equals(idElement.GetString(), requested, StringComparison.OrdinalIgnoreCase))
            {
                return ftype;
            }
        }

        // No exact id match (e.g. model id is a llama-server alias). If the server reports
        // exactly one model, or only one carries an ftype, that is unambiguously ours.
        if (fallback is JsonElement fb &&
            fb.ValueKind == JsonValueKind.Object)
        {
            return ReadMetaFtype(fb);
        }

        return null;
    }

    private static string? ReadMetaFtype(JsonElement modelObject)
    {
        // meta.ftype is the field PR #25134 adds; older builds and non-llama.cpp servers omit
        // the whole meta object, in which case there is nothing authoritative to report.
        if (!modelObject.TryGetProperty("meta", out var meta) ||
            meta.ValueKind != JsonValueKind.Object ||
            !meta.TryGetProperty("ftype", out var ftype))
        {
            return null;
        }

        return ftype.ValueKind == JsonValueKind.String ? ftype.GetString() : null;
    }

    public static string BuildChatRequestJsonForDiagnostics(
        string model,
        string systemPrompt,
        string userPrompt,
        BenchmarkOptions options)
    {
        var firstAttempt = BuildAttempts(options)[0];
        var request = BuildChatRequest(model, systemPrompt, userPrompt, options, firstAttempt.IncludeResponseFormat, firstAttempt.IncludeThinkingControl, stream: options.AbortOnLoop);
        return JsonSerializer.Serialize(request, JsonOptions);
    }

    public async Task<ChatCompletionResult> CreateChatCompletionAsync(
        string serverUrl,
        string model,
        string systemPrompt,
        string userPrompt,
        BenchmarkOptions options,
        IProgress<ChatStreamDelta>? streamProgress = null,
        CancellationToken cancellationToken = default,
        CancellationToken manualAbortToken = default)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model id is required.", nameof(model));
        }

        var errors = new List<string>();
        ChatCompletionResult? firstSuccessful = null;
        var attempts = BuildAttempts(options);

        for (var i = 0; i < attempts.Count; i++)
        {
            var attempt = attempts[i];

            // Tell the UI a fresh attempt is starting so it can reset the live buffers
            // (otherwise tokens from a retried attempt would append to the failed one).
            streamProgress?.Report(ChatStreamDelta.AttemptStart(attempt.Label, i, attempts.Count));

            var useStreaming = streamProgress is not null || options.AbortOnLoop;
            var request = BuildChatRequest(model, systemPrompt, userPrompt, options, attempt.IncludeResponseFormat, attempt.IncludeThinkingControl, stream: useStreaming);

            var completion = useStreaming
                ? await PostChatCompletionStreamingAsync(serverUrl, request, streamProgress, options.AbortOnLoop, cancellationToken, manualAbortToken).ConfigureAwait(false)
                : await PostChatCompletionAsync(serverUrl, request, cancellationToken, manualAbortToken).ConfigureAwait(false);

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
            if (result.LoopDetected || result.ManuallyStopped)
            {
                return result;
            }

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

            // "Retried without X" is only meaningful if an earlier attempt actually used X.
            // Since the unconstrained attempt now runs first, base the flag on history,
            // not on the option alone.
            var anyEarlierUsedResponseFormat = attempts.Any(a => a.IncludeResponseFormat);
            var anyEarlierUsedThinkingControl = attempts.Any(a => a.IncludeThinkingControl);

            attempts.Add(new ChatRequestAttempt(
                includeResponseFormat,
                includeThinkingControl,
                RetriedWithoutResponseFormat: !includeResponseFormat && anyEarlierUsedResponseFormat,
                RetriedWithoutThinkingControl: !includeThinkingControl && anyEarlierUsedThinkingControl));
        }

        if (!options.SkipResponseFormat)
        {
            // Try WITHOUT response_format first. Forcing a json_object grammar on a
            // reasoning model frequently collides with its thinking phase (the model
            // either has its reasoning suppressed/garbled or exhausts the budget before
            // it can emit the constrained JSON). The parser already extracts JSON from
            // free-form output (fenced, balanced, partial), so the unconstrained attempt
            // is both more compatible and usually sufficient. json_object stays as a fallback.
            Add(includeResponseFormat: false, includeThinkingControl: options.DisableThinking);
            Add(includeResponseFormat: true, includeThinkingControl: options.DisableThinking);
            if (options.DisableThinking)
            {
                Add(includeResponseFormat: false, includeThinkingControl: false);
                Add(includeResponseFormat: true, includeThinkingControl: false);
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
        bool includeThinkingControl,
        bool stream = false)
    {
        var messages = new[]
        {
            new { role = "system", content = systemPrompt },
            new { role = "user", content = userPrompt }
        };

        // Intentionally do NOT send sampler settings (temperature, top_p, top_k,
        // min_p, repeat_penalty, ...). Those are configured per model on the
        // llama-server side (via the autotuner). Sending them here would override
        // the server defaults for every request - e.g. forcing temperature=0
        // collapses any model to greedy decoding and produces endless repetition
        // loops. The benchmark only asks the question and evaluates the answer;
        // it must not change how a model is tuned.
        //
        // seed is kept on purpose: it is a run-level reproducibility control
        // (exposed in the UI), not a per-model tuning parameter, and it does not
        // cause loops.
        var request = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["messages"] = messages,
            ["seed"] = options.Seed
        };

        if (stream)
        {
            request["stream"] = true;
            // llama.cpp emits exact usage in the final SSE chunk when requested.
            request["stream_options"] = new { include_usage = true };
        }

        // Only send max_tokens when it is a real positive budget. A value of -1
        // (our "no cap" default) must NOT be forwarded: llama-server treats a
        // literal "max_tokens": -1 inconsistently across builds, and a too-small
        // cap is the main reason reasoning models burn the whole budget inside
        // reasoning_content and return empty message.content. Omitting the field
        // lets the server generate until EOS / context / timeout, exactly like
        // the web UI.
        if (options.MaxTokens > 0)
        {
            request["max_tokens"] = options.MaxTokens;
        }

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
        CancellationToken cancellationToken,
        CancellationToken manualAbortToken)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken, manualAbortToken);
        var operationToken = linkedCancellation?.Token ?? cancellationToken;

        try
        {
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var response = await _httpClient.PostAsync(BuildUri(serverUrl, "/v1/chat/completions"), content, operationToken).ConfigureAwait(false);
            var responseText = await response.Content.ReadAsStringAsync(operationToken).ConfigureAwait(false);

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
                    PromptTokens = extracted.PromptTokens,
                    CompletionTokens = extracted.CompletionTokens,
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
        catch (OperationCanceledException) when (IsManualAbort(manualAbortToken, cancellationToken))
        {
            return (true, CreateManualAbortResult(requestJson, assistantContent: string.Empty, reasoningContent: string.Empty, rawResponse: string.Empty), string.Empty);
        }
    }

    private async Task<(bool Success, ChatCompletionResult? Result, string Error)> PostChatCompletionStreamingAsync(
        string serverUrl,
        object request,
        IProgress<ChatStreamDelta>? streamProgress,
        bool abortOnLoop,
        CancellationToken cancellationToken,
        CancellationToken manualAbortToken)
    {
        var requestJson = JsonSerializer.Serialize(request, JsonOptions);
        var contentBuilder = new StringBuilder();
        var reasoningBuilder = new StringBuilder();
        var rawBuilder = new StringBuilder();
        var nonSseBuilder = new StringBuilder();
        var finishReason = string.Empty;
        int? promptTokens = null;
        int? completionTokens = null;
        var loopDetected = false;
        var loopDiagnosticsSummary = string.Empty;
        var loopState = new StreamLoopState();
        using var linkedCancellation = CreateLinkedCancellation(cancellationToken, manualAbortToken);
        var operationToken = linkedCancellation?.Token ?? cancellationToken;

        try
        {
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
            using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildUri(serverUrl, "/v1/chat/completions"))
            {
                Content = content
            };
            httpRequest.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

            using var response = await _httpClient
                .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, operationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(operationToken).ConfigureAwait(false);
                return (false, null, $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {TrimForError(errorBody)}");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(operationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? line;
            while ((line = await reader.ReadLineAsync(operationToken).ConfigureAwait(false)) is not null)
            {
                operationToken.ThrowIfCancellationRequested();

                if (line.Length == 0)
                {
                    continue;
                }

                // SSE comment / keep-alive lines start with ':' — ignore.
                if (line[0] == ':')
                {
                    continue;
                }

                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    // Some OpenAI-compatible test doubles or gateways ignore stream=true
                    // and return a normal JSON body. Keep those lines so we can parse the
                    // response after the read completes instead of treating it as empty.
                    if (rawBuilder.Length == 0 && contentBuilder.Length == 0 && reasoningBuilder.Length == 0)
                    {
                        nonSseBuilder.AppendLine(line);
                    }
                    continue;
                }

                var payload = line.Substring("data:".Length).Trim();
                if (payload.Length == 0)
                {
                    continue;
                }

                if (string.Equals(payload, "[DONE]", StringComparison.Ordinal))
                {
                    break;
                }

                rawBuilder.AppendLine(payload);

                finishReason = ConsumeStreamChunk(payload, contentBuilder, reasoningBuilder, finishReason, streamProgress, ref promptTokens, ref completionTokens);
                if (abortOnLoop && loopState.TryDetect(contentBuilder, out var diagnosticsSummary))
                {
                    loopDetected = true;
                    loopDiagnosticsSummary = diagnosticsSummary;
                    finishReason = LoopDetectedFinishReason;
                    streamProgress?.Report(ChatStreamDelta.LoopDetected(diagnosticsSummary));
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (IsManualAbort(manualAbortToken, cancellationToken))
        {
            return (true, BuildStreamingResult(
                requestJson,
                contentBuilder,
                reasoningBuilder,
                rawBuilder,
                finishReason,
                loopDetected,
                loopDiagnosticsSummary,
                manuallyStopped: true,
                promptTokens: promptTokens,
                completionTokens: completionTokens), string.Empty);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or JsonException)
        {
            // If we already accumulated something, fall through and return it as a partial
            // success so the run still has data; otherwise report the error.
            if (contentBuilder.Length == 0 && reasoningBuilder.Length == 0)
            {
                return (false, null, $"Streaming read failed: {ex.Message}");
            }
        }

        if (rawBuilder.Length == 0 && nonSseBuilder.Length > 0 && contentBuilder.Length == 0 && reasoningBuilder.Length == 0)
        {
            try
            {
                var fallbackText = nonSseBuilder.ToString();
                var extracted = ExtractChatMessageContent(fallbackText);
                return (true, new ChatCompletionResult
                {
                    AssistantContent = extracted.AssistantContent,
                    ReasoningContent = extracted.ReasoningContent,
                    FinishReason = extracted.FinishReason,
                    PromptTokens = extracted.PromptTokens,
                    CompletionTokens = extracted.CompletionTokens,
                    RawResponse = fallbackText.TrimEnd(),
                    RequestJson = requestJson,
                    UsedResponseFormat = requestJson.Contains("response_format", StringComparison.Ordinal),
                    UsedThinkingControl = requestJson.Contains("chat_template_kwargs", StringComparison.Ordinal),
                    RetriedWithoutResponseFormat = false,
                    RetriedWithoutThinkingControl = false
                }, string.Empty);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                return (false, null, $"Could not parse non-SSE chat response: {ex.Message}. Raw: {TrimForError(nonSseBuilder.ToString())}");
            }
        }

        return (true, BuildStreamingResult(
            requestJson,
            contentBuilder,
            reasoningBuilder,
            rawBuilder,
            finishReason,
            loopDetected,
            loopDiagnosticsSummary,
            manuallyStopped: false,
            promptTokens: promptTokens,
            completionTokens: completionTokens), string.Empty);
    }

    private static ChatCompletionResult BuildStreamingResult(
        string requestJson,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        StringBuilder rawBuilder,
        string finishReason,
        bool loopDetected,
        string loopDiagnosticsSummary,
        bool manuallyStopped,
        int? promptTokens,
        int? completionTokens)
    {
        return new ChatCompletionResult
        {
            AssistantContent = contentBuilder.ToString(),
            ReasoningContent = reasoningBuilder.ToString(),
            FinishReason = manuallyStopped ? ManualAbortFinishReason : finishReason,
            PromptTokens = promptTokens,
            CompletionTokens = completionTokens,
            LoopDetected = loopDetected,
            LoopDiagnosticsSummary = loopDiagnosticsSummary,
            ManuallyStopped = manuallyStopped,
            RawResponse = rawBuilder.ToString().TrimEnd(),
            RequestJson = requestJson,
            UsedResponseFormat = requestJson.Contains("response_format", StringComparison.Ordinal),
            UsedThinkingControl = requestJson.Contains("chat_template_kwargs", StringComparison.Ordinal),
            RetriedWithoutResponseFormat = false,
            RetriedWithoutThinkingControl = false
        };
    }

    private static ChatCompletionResult CreateManualAbortResult(
        string requestJson,
        string assistantContent,
        string reasoningContent,
        string rawResponse)
    {
        return new ChatCompletionResult
        {
            AssistantContent = assistantContent,
            ReasoningContent = reasoningContent,
            FinishReason = ManualAbortFinishReason,
            ManuallyStopped = true,
            RawResponse = rawResponse,
            RequestJson = requestJson,
            UsedResponseFormat = requestJson.Contains("response_format", StringComparison.Ordinal),
            UsedThinkingControl = requestJson.Contains("chat_template_kwargs", StringComparison.Ordinal),
            RetriedWithoutResponseFormat = false,
            RetriedWithoutThinkingControl = false
        };
    }

    private static CancellationTokenSource? CreateLinkedCancellation(CancellationToken cancellationToken, CancellationToken manualAbortToken)
    {
        return manualAbortToken.CanBeCanceled
            ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, manualAbortToken)
            : null;
    }

    private static bool IsManualAbort(CancellationToken manualAbortToken, CancellationToken globalCancellationToken)
    {
        return manualAbortToken.IsCancellationRequested && !globalCancellationToken.IsCancellationRequested;
    }

    private static string ConsumeStreamChunk(
        string payloadJson,
        StringBuilder contentBuilder,
        StringBuilder reasoningBuilder,
        string finishReason,
        IProgress<ChatStreamDelta>? streamProgress,
        ref int? promptTokens,
        ref int? completionTokens)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payloadJson);
        }
        catch (JsonException)
        {
            // A single malformed chunk should not abort the whole stream.
            return finishReason;
        }

        using (document)
        {
            var root = document.RootElement;
            ReadUsage(root, ref promptTokens, ref completionTokens);
            if (!root.TryGetProperty("choices", out var choices) ||
                choices.ValueKind != JsonValueKind.Array ||
                choices.GetArrayLength() == 0)
            {
                return finishReason;
            }

            var firstChoice = choices[0];

            if (firstChoice.TryGetProperty("finish_reason", out var finishReasonElement) &&
                finishReasonElement.ValueKind == JsonValueKind.String)
            {
                var value = finishReasonElement.GetString();
                if (!string.IsNullOrEmpty(value))
                {
                    finishReason = value;
                }
            }

            // Streaming responses put incremental text under choices[0].delta.
            // (We intentionally do NOT also read choices[0].message here: in streaming
            // mode llama.cpp emits delta chunks only, and treating a final "message"
            // object as a delta would re-append the entire accumulated text.)
            if (!firstChoice.TryGetProperty("delta", out var deltaContainer))
            {
                return finishReason;
            }

            var contentPiece = deltaContainer.TryGetProperty("content", out var contentElement)
                ? ReadElementAsString(contentElement)
                : string.Empty;
            var reasoningPiece = deltaContainer.TryGetProperty("reasoning_content", out var reasoningElement)
                ? ReadElementAsString(reasoningElement)
                : string.Empty;

            if (!string.IsNullOrEmpty(reasoningPiece))
            {
                reasoningBuilder.Append(reasoningPiece);
                streamProgress?.Report(ChatStreamDelta.Reasoning(reasoningPiece));
            }

            if (!string.IsNullOrEmpty(contentPiece))
            {
                contentBuilder.Append(contentPiece);
                streamProgress?.Report(ChatStreamDelta.Content(contentPiece));
            }
        }

        return finishReason;
    }

    private static (string AssistantContent, string ReasoningContent, string FinishReason, int? PromptTokens, int? CompletionTokens) ExtractChatMessageContent(string responseText)
    {
        using var document = JsonDocument.Parse(responseText);
        var root = document.RootElement;
        int? promptTokens = null;
        int? completionTokens = null;
        ReadUsage(root, ref promptTokens, ref completionTokens);

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
                    return (assistantContent, reasoningContent, finishReason, promptTokens, completionTokens);
                }
            }

            if (firstChoice.TryGetProperty("text", out var text))
            {
                return (ReadElementAsString(text), string.Empty, finishReason, promptTokens, completionTokens);
            }
        }

        if (root.TryGetProperty("content", out var directContent))
        {
            return (ReadElementAsString(directContent), string.Empty, string.Empty, promptTokens, completionTokens);
        }

        throw new InvalidOperationException("The response does not contain choices[0].message.content or reasoning_content.");
    }

    private static void ReadUsage(JsonElement root, ref int? promptTokens, ref int? completionTokens)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var parsedPrompt))
        {
            promptTokens = parsedPrompt;
        }

        if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var parsedCompletion))
        {
            completionTokens = parsedCompletion;
        }
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

    private sealed class StreamLoopState
    {
        private readonly ChannelLoopTracker _content = new();

        public bool TryDetect(StringBuilder contentBuilder, out string diagnosticsSummary)
        {
            // Do not abort visible reasoning_content. Qwen-style reasoning models often
            // repeat bounded checklists while still making progress toward the final JSON;
            // cutting that channel short harms official scores. We still stream it for the
            // UI/report and run post-hoc diagnostics, but live interruption is limited to
            // final assistant content loops.
            if (_content.TryDetect(contentBuilder, "assistant content", out diagnosticsSummary))
            {
                return true;
            }

            diagnosticsSummary = string.Empty;
            return false;
        }

        private sealed class ChannelLoopTracker
        {
            private int _nextCheckAt = LoopCheckMinimumChars;
            private int _consecutiveSuspicions;
            private string _lastSignature = string.Empty;

            public bool TryDetect(StringBuilder builder, string label, out string diagnosticsSummary)
            {
                if (builder.Length < _nextCheckAt)
                {
                    diagnosticsSummary = string.Empty;
                    return false;
                }

                while (_nextCheckAt <= builder.Length)
                {
                    _nextCheckAt += LoopCheckIntervalChars;
                }

                var diagnostics = OutputLoopDetector.Analyze(builder.ToString());
                if (!diagnostics.HasSuspectedLoop)
                {
                    _consecutiveSuspicions = 0;
                    _lastSignature = string.Empty;
                    diagnosticsSummary = string.Empty;
                    return false;
                }

                var signature = BuildSignature(diagnostics);
                _consecutiveSuspicions = string.Equals(signature, _lastSignature, StringComparison.Ordinal)
                    ? _consecutiveSuspicions + 1
                    : 1;
                _lastSignature = signature;

                const int requiredChecks = LoopConfirmationChecksRequired;
                if (_consecutiveSuspicions < requiredChecks)
                {
                    diagnosticsSummary = string.Empty;
                    return false;
                }

                var confirmation = requiredChecks == 1
                    ? "matched conservative runaway-list threshold"
                    : $"confirmed over {_consecutiveSuspicions} checks";
                diagnosticsSummary = $"{label}: {diagnostics.Summary} ({confirmation})";
                return true;
            }

            private static string BuildSignature(OutputLoopDiagnostics diagnostics)
            {
                var top = diagnostics.Repetitions.FirstOrDefault();
                return top is null
                    ? diagnostics.Summary
                    : $"{top.Kind}\n{top.Snippet}";
            }
        }
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
