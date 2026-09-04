using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

/// <summary>Where the AutoTuner endpoint and token came from.</summary>
public sealed class AutoTunerConnection
{
    public string BaseUrl { get; init; } = "http://127.0.0.1:1233";
    public string Token { get; init; } = string.Empty;
    public string Source { get; init; } = "manual";
    public string? Version { get; init; }
    public string? SidecarPath { get; init; }

    public bool HasToken => !string.IsNullOrWhiteSpace(Token);
}

/// <summary>
/// Locates the AutoTuner control API: explicit values win, then the environment
/// (<c>AUTOTUNER_API_URL</c>/<c>AUTOTUNER_API_KEY</c>), then the sidecar file the AutoTuner
/// writes while its API is enabled (<c>&lt;home&gt;/.autotuner/control_api.json</c>, or
/// <c>AUTOTUNER_DATA_DIR/control_api.json</c>).
/// </summary>
public static class AutoTunerDiscovery
{
    public const string UrlEnvironmentVariable = "AUTOTUNER_API_URL";
    public const string KeyEnvironmentVariable = "AUTOTUNER_API_KEY";
    public const string DataDirEnvironmentVariable = "AUTOTUNER_DATA_DIR";
    public const string SidecarFileName = "control_api.json";

    public static string DefaultSidecarPath
    {
        get
        {
            var dataDir = Environment.GetEnvironmentVariable(DataDirEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(dataDir))
            {
                dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".autotuner");
            }

            return Path.Combine(dataDir, SidecarFileName);
        }
    }

    public static AutoTunerConnection? Discover(string? manualUrl = null, string? manualToken = null, string? sidecarPath = null)
    {
        var envUrl = Environment.GetEnvironmentVariable(UrlEnvironmentVariable);
        var envKey = Environment.GetEnvironmentVariable(KeyEnvironmentVariable);
        var sidecar = ReadSidecar(sidecarPath ?? DefaultSidecarPath);

        var url = FirstNonEmpty(manualUrl, envUrl, sidecar?.BaseUrl);
        var token = FirstNonEmpty(manualToken, envKey, sidecar?.Token);
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var source = !string.IsNullOrWhiteSpace(manualUrl) || !string.IsNullOrWhiteSpace(manualToken)
            ? "manual"
            : !string.IsNullOrWhiteSpace(envUrl) || !string.IsNullOrWhiteSpace(envKey)
                ? "environment"
                : "sidecar";

        return new AutoTunerConnection
        {
            BaseUrl = url.TrimEnd('/'),
            Token = token ?? string.Empty,
            Source = source,
            Version = sidecar?.Version,
            SidecarPath = sidecar?.Path
        };
    }

    public sealed class Sidecar
    {
        public string? BaseUrl { get; init; }
        public string? Token { get; init; }
        public string? Version { get; init; }
        public bool Enabled { get; init; }
        public string Path { get; init; } = string.Empty;
    }

    public static Sidecar? ReadSidecar(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return null;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var enabled = root.TryGetProperty("enabled", out var enabledElement) && enabledElement.ValueKind == JsonValueKind.True;
            var baseUrl = root.TryGetProperty("base_url", out var urlElement) && urlElement.ValueKind == JsonValueKind.String ? urlElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(baseUrl) && root.TryGetProperty("port", out var portElement) && portElement.TryGetInt32(out var port))
            {
                baseUrl = $"http://127.0.0.1:{port}";
            }

            return new Sidecar
            {
                Enabled = enabled,
                BaseUrl = enabled ? baseUrl : null,
                Token = enabled && root.TryGetProperty("token", out var tokenElement) && tokenElement.ValueKind == JsonValueKind.String ? tokenElement.GetString() : null,
                Version = root.TryGetProperty("version", out var versionElement) && versionElement.ValueKind == JsonValueKind.String ? versionElement.GetString() : null,
                Path = path
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}

public sealed class AutoTunerApiException : Exception
{
    public AutoTunerApiException(string message, int status, string code) : base(message)
    {
        Status = status;
        Code = code;
    }

    public int Status { get; }
    public string Code { get; }

    /// <summary>Transient states worth retrying: the tuner is busy or still detecting hardware.</summary>
    public bool IsRetryable => Code is "autotuner_busy" or "model_busy" or "hardware_pending" || Status == 503 && Code != "shutting_down";
}

public sealed class AutoTunerHealth
{
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("service")] public string Service { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
}

public sealed class AutoTunerModel
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("path")] public string Path { get; init; } = string.Empty;
    [JsonPropertyName("context_window")] public int ContextWindow { get; init; }
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
    [JsonPropertyName("reasoning")] public bool Reasoning { get; init; }
    [JsonPropertyName("runnable")] public bool Runnable { get; init; } = true;
    [JsonPropertyName("unavailable_reason")] public string UnavailableReason { get; init; } = string.Empty;
    [JsonPropertyName("default_runtime_id")] public string? DefaultRuntimeId { get; init; }
    [JsonPropertyName("quant")] public string? Quant { get; init; }
    [JsonPropertyName("params_b")] public double? ParamsB { get; init; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; init; }
    [JsonPropertyName("family")] public string? Family { get; init; }
    [JsonPropertyName("architecture")] public string? Architecture { get; init; }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Id : Name;
}

public sealed class AutoTunerRuntime
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("label")] public string Label { get; init; } = string.Empty;
    [JsonPropertyName("server_binary")] public string? ServerBinary { get; init; }
    [JsonPropertyName("backend")] public string Backend { get; init; } = ServerRuntimeInfo.UnknownValue;
    [JsonPropertyName("build")] public string? Build { get; init; }
    [JsonPropertyName("build_info")] public string? BuildInfo { get; init; }
    [JsonPropertyName("is_default")] public bool IsDefault { get; init; }
    [JsonPropertyName("available")] public bool Available { get; init; } = true;
    [JsonPropertyName("unavailable_reason")] public string UnavailableReason { get; init; } = string.Empty;

    [JsonIgnore]
    public string DisplayLabel => string.IsNullOrWhiteSpace(Label) ? Id : Label;
}

public sealed class AutoTunerStatusModel
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("path")] public string? Path { get; init; }
    [JsonPropertyName("ftype")] public string? Ftype { get; init; }
    [JsonPropertyName("params_b")] public double? ParamsB { get; init; }
    [JsonPropertyName("size_bytes")] public long? SizeBytes { get; init; }
    [JsonPropertyName("draft_model_path")] public string? DraftModelPath { get; init; }
    [JsonPropertyName("mmproj_path")] public string? MmProjPath { get; init; }
}

public sealed class AutoTunerLaunch
{
    [JsonPropertyName("ctx_size")] public int? CtxSize { get; init; }
    [JsonPropertyName("gpu_layers")] public int? GpuLayers { get; init; }
    [JsonPropertyName("threads")] public int? Threads { get; init; }
    [JsonPropertyName("batch_threads")] public int? BatchThreads { get; init; }
    [JsonPropertyName("batch")] public int? Batch { get; init; }
    [JsonPropertyName("ubatch")] public int? UBatch { get; init; }
    [JsonPropertyName("kv_type_k")] public string? KvTypeK { get; init; }
    [JsonPropertyName("kv_type_v")] public string? KvTypeV { get; init; }
    [JsonPropertyName("flash_attention")] public string? FlashAttention { get; init; }
    [JsonPropertyName("spec_type")] public string? SpecType { get; init; }
    [JsonPropertyName("draft_n_max")] public int? DraftNMax { get; init; }
    [JsonPropertyName("main_gpu")] public int? MainGpu { get; init; }
    [JsonPropertyName("parallel")] public int? Parallel { get; init; }
    [JsonPropertyName("thinking")] public bool? Thinking { get; init; }
    [JsonPropertyName("profile")] public string? Profile { get; init; }
    [JsonPropertyName("performance_target")] public string? PerformanceTarget { get; init; }
}

public sealed class AutoTunerDevice
{
    [JsonPropertyName("index")] public int? Index { get; init; }
    [JsonPropertyName("name")] public string? Name { get; init; }
    [JsonPropertyName("backend")] public string? Backend { get; init; }
    [JsonPropertyName("vram_mb")] public long? VramMb { get; init; }
}

public sealed class AutoTunerStatus
{
    [JsonPropertyName("status")] public string Status { get; init; } = "idle";
    [JsonPropertyName("active_model")] public string? ActiveModel { get; init; }
    [JsonPropertyName("loading_model")] public string? LoadingModel { get; init; }
    [JsonPropertyName("active_since")] public double? ActiveSince { get; init; }
    [JsonPropertyName("inflight_requests")] public int InflightRequests { get; init; }
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }
    [JsonPropertyName("ready")] public bool? Ready { get; init; }
    [JsonPropertyName("backend_url")] public string? BackendUrl { get; init; }
    [JsonPropertyName("alias")] public string? Alias { get; init; }
    [JsonPropertyName("backend_api_key")] public string? BackendApiKey { get; init; }
    [JsonPropertyName("pid")] public int? Pid { get; init; }
    [JsonPropertyName("active_runtime")] public string? ActiveRuntime { get; init; }
    [JsonPropertyName("log_path")] public string? LogPath { get; init; }
    [JsonPropertyName("runtime")] public AutoTunerRuntime? Runtime { get; init; }
    [JsonPropertyName("model")] public AutoTunerStatusModel? Model { get; init; }
    [JsonPropertyName("launch")] public AutoTunerLaunch? Launch { get; init; }
    [JsonPropertyName("devices")] public List<AutoTunerDevice> Devices { get; init; } = [];
    [JsonPropertyName("env")] public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("command_line")] public List<string> CommandLine { get; init; } = [];

    [JsonIgnore]
    public bool IsReady => Ready ?? string.Equals(Status, "ready", StringComparison.OrdinalIgnoreCase);

    /// <summary>Converts the AutoTuner view of the active server into the archive runtime identity.</summary>
    public ServerRuntimeInfo ToRuntimeInfo(string? autoTunerVersion)
    {
        var runtime = Runtime;
        var backend = RuntimeKeys.NormalizeBackend(runtime?.Backend);
        if (backend == ServerRuntimeInfo.UnknownValue && !string.IsNullOrWhiteSpace(runtime?.ServerBinary))
        {
            backend = RuntimeKeys.BackendFromPath(runtime.ServerBinary);
        }

        if (backend == ServerRuntimeInfo.UnknownValue && !string.IsNullOrWhiteSpace(runtime?.Label))
        {
            backend = RuntimeKeys.NormalizeBackend(runtime.Label);
        }

        return new ServerRuntimeInfo
        {
            Engine = "llama.cpp",
            EngineVersion = string.IsNullOrWhiteSpace(runtime?.BuildInfo) ? runtime?.Build : runtime.BuildInfo,
            Backend = backend,
            BackendSource = backend == ServerRuntimeInfo.UnknownValue ? ServerRuntimeInfo.UnknownValue : "autotuner",
            BackendDetail = runtime?.Label,
            Devices = Devices.Where(d => !string.IsNullOrWhiteSpace(d.Name)).Select(d => d.VramMb is > 0 ? $"{d.Name} ({d.VramMb} MB)" : d.Name!).ToList(),
            ServerBinary = runtime?.ServerBinary,
            RuntimeLabel = runtime?.Label,
            ProcessId = Pid,
            CommandLine = CommandLine.Count == 0 ? null : LocalProcessInspector.Redact(string.Join(' ', CommandLine.Select(a => a.Contains(' ') ? "\"" + a + "\"" : a))),
            ModelPath = Model?.Path,
            ModelAlias = Alias,
            ModelFtype = Model?.Ftype,
            ContextSize = Launch?.CtxSize,
            GpuLayers = Launch?.GpuLayers,
            Threads = Launch?.Threads,
            BatchThreads = Launch?.BatchThreads,
            BatchSize = Launch?.Batch,
            UBatchSize = Launch?.UBatch,
            Parallel = Launch?.Parallel,
            KvTypeK = Launch?.KvTypeK,
            KvTypeV = Launch?.KvTypeV,
            FlashAttention = Launch?.FlashAttention,
            SpecType = Launch?.SpecType,
            DraftModel = Model?.DraftModelPath,
            MmProj = Model?.MmProjPath,
            Environment = new Dictionary<string, string>(Environment, StringComparer.OrdinalIgnoreCase),
            AutoTunerVersion = autoTunerVersion,
            AutoTunerModelId = ActiveModel ?? Model?.Id,
            AutoTunerRuntimeId = runtime?.Id ?? ActiveRuntime,
            AutoTunerProfile = Launch?.Profile,
            AutoTunerPerformanceTarget = Launch?.PerformanceTarget,
            ProbedAt = DateTimeOffset.UtcNow,
            ProbeNotes = string.IsNullOrWhiteSpace(LogPath) ? [] : [$"autotuner server log: {LogPath}"]
        };
    }
}

/// <summary>
/// Thin client for the AutoTuner control API (docs/control-api.md in the AutoTuner repo).
/// Unknown JSON fields are ignored so the benchmark keeps working with older/newer tuners.
/// </summary>
public sealed class AutoTunerClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public AutoTunerClient(AutoTunerConnection connection, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        Connection = connection ?? throw new ArgumentNullException(nameof(connection));
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = handler is null;
        _http.Timeout = timeout ?? TimeSpan.FromMinutes(20);
        if (connection.HasToken)
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", connection.Token);
        }
    }

    public AutoTunerConnection Connection { get; }

    public string? Version { get; private set; }

    public async Task<AutoTunerHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var health = await GetAsync<AutoTunerHealth>("/health", cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(health.Version))
        {
            Version = health.Version;
        }

        return health;
    }

    public async Task<IReadOnlyList<AutoTunerModel>> GetModelsAsync(CancellationToken cancellationToken = default)
    {
        using var document = await GetDocumentAsync("/api/v1/models", cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var models = new List<AutoTunerModel>();
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("models", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                var model = item.Deserialize<AutoTunerModel>(JsonOptions);
                if (model is not null && !string.IsNullOrWhiteSpace(model.Id))
                {
                    models.Add(model);
                }
            }
        }

        return models;
    }

    /// <summary>Returns an empty list (not an error) when the tuner predates /api/v1/runtimes.</summary>
    public async Task<IReadOnlyList<AutoTunerRuntime>> GetRuntimesAsync(CancellationToken cancellationToken = default)
    {
        JsonDocument document;
        try
        {
            document = await GetDocumentAsync("/api/v1/runtimes", cancellationToken).ConfigureAwait(false);
        }
        catch (AutoTunerApiException ex) when (ex.Status == 404)
        {
            return [];
        }

        using (document)
        {
            var root = document.RootElement;
            var runtimes = new List<AutoTunerRuntime>();
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("runtimes", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in array.EnumerateArray())
                {
                    var runtime = item.Deserialize<AutoTunerRuntime>(JsonOptions);
                    if (runtime is not null && !string.IsNullOrWhiteSpace(runtime.Id))
                    {
                        runtimes.Add(runtime);
                    }
                }
            }

            return runtimes;
        }
    }

    public Task<AutoTunerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<AutoTunerStatus>("/api/v1/status", cancellationToken);

    /// <summary>
    /// Activates a model (optionally on a specific llama-server build) and waits until the
    /// tuner reports it ready. Transient busy states are retried with backoff until
    /// <paramref name="deadline"/>.
    /// </summary>
    public async Task<AutoTunerStatus> SwitchAsync(
        string modelId,
        string? runtimeId = null,
        TimeSpan? timeout = null,
        TimeSpan? deadline = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new ArgumentException("A model id is required.", nameof(modelId));
        }

        var switchTimeout = timeout ?? TimeSpan.FromMinutes(15);
        var stopAt = DateTimeOffset.UtcNow + (deadline ?? switchTimeout + TimeSpan.FromMinutes(10));
        var delay = TimeSpan.FromSeconds(5);
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model_id"] = modelId,
            ["timeout_s"] = Math.Ceiling(switchTimeout.TotalSeconds)
        };
        if (!string.IsNullOrWhiteSpace(runtimeId))
        {
            payload["runtime_id"] = runtimeId;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var status = await PostAsync<AutoTunerStatus>("/api/v1/switch", payload, cancellationToken).ConfigureAwait(false);
                if (!status.IsReady && string.IsNullOrWhiteSpace(status.BackendUrl))
                {
                    progress?.Invoke($"AutoTuner returned status '{status.Status}' without a ready backend; polling status...");
                    status = await WaitUntilReadyAsync(stopAt, progress, cancellationToken).ConfigureAwait(false);
                }

                return status;
            }
            catch (AutoTunerApiException ex) when (ex.IsRetryable && DateTimeOffset.UtcNow + delay < stopAt)
            {
                progress?.Invoke($"AutoTuner busy ({ex.Code}: {ex.Message}); retrying in {delay.TotalSeconds:0}s...");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 1.5));
            }
        }
    }

    private async Task<AutoTunerStatus> WaitUntilReadyAsync(DateTimeOffset stopAt, Action<string>? progress, CancellationToken cancellationToken)
    {
        while (DateTimeOffset.UtcNow < stopAt)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (status.IsReady && !string.IsNullOrWhiteSpace(status.BackendUrl))
            {
                return status;
            }

            progress?.Invoke($"AutoTuner status: {status.Status} (loading {status.LoadingModel ?? "-"})");
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }

        throw new AutoTunerApiException("AutoTuner did not report the model ready before the deadline.", 504, "switch_timeout");
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await PostAsync<JsonElement>("/api/v1/stop", new Dictionary<string, object?>(), cancellationToken).ConfigureAwait(false);
        }
        catch (AutoTunerApiException ex) when (ex.Code is "no_active_model" or "stop_unavailable")
        {
            // Nothing to stop is not an error for a campaign.
        }
    }

    /// <summary>Optional log tail; null when the tuner does not implement /api/v1/logs.</summary>
    public async Task<string?> TryGetLogsAsync(int lines = 200, CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetDocumentAsync($"/api/v1/logs?lines={lines}", cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("lines", out var array) && array.ValueKind == JsonValueKind.Array)
                {
                    return string.Join(Environment.NewLine, array.EnumerateArray().Select(e => e.ToString()));
                }

                if (root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                {
                    return text.GetString();
                }
            }

            return root.ToString();
        }
        catch (Exception ex) when (ex is AutoTunerApiException or HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    private async Task<T> GetAsync<T>(string endpoint, CancellationToken cancellationToken)
    {
        using var document = await GetDocumentAsync(endpoint, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new AutoTunerApiException($"Empty response from {endpoint}.", 500, "empty_response");
    }

    private async Task<JsonDocument> GetDocumentAsync(string endpoint, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(BuildUri(endpoint), cancellationToken).ConfigureAwait(false);
        return await ReadDocumentAsync(response, endpoint, cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> PostAsync<T>(string endpoint, Dictionary<string, object?> payload, CancellationToken cancellationToken)
    {
        using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync(BuildUri(endpoint), content, cancellationToken).ConfigureAwait(false);
        using var document = await ReadDocumentAsync(response, endpoint, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Deserialize<T>(JsonOptions) ?? throw new AutoTunerApiException($"Empty response from {endpoint}.", 500, "empty_response");
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, string endpoint, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw ParseError(response.StatusCode, body, endpoint);
        }

        try
        {
            return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
        }
        catch (JsonException ex)
        {
            throw new AutoTunerApiException($"Invalid JSON from {endpoint}: {ex.Message}", (int)response.StatusCode, "invalid_json");
        }
    }

    private static AutoTunerApiException ParseError(HttpStatusCode status, string body, string endpoint)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.Object)
            {
                var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
                var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
                return new AutoTunerApiException(message ?? $"{endpoint} failed with HTTP {(int)status}.", (int)status, code ?? "api_error");
            }
        }
        catch (JsonException)
        {
            // Fall through to the generic error.
        }

        var code2 = status == HttpStatusCode.Unauthorized ? "unauthorised" : status == HttpStatusCode.NotFound ? "not_found" : "http_error";
        var snippet = body.Length > 300 ? body[..300] + "..." : body;
        return new AutoTunerApiException($"{endpoint} failed with HTTP {(int)status}: {snippet}", (int)status, code2);
    }

    private Uri BuildUri(string endpoint)
        => new(new Uri(Connection.BaseUrl.TrimEnd('/') + "/"), endpoint.TrimStart('/'));

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
