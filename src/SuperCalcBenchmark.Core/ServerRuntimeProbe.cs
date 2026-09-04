using System.Net.Http.Headers;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Determines which inference engine and compute backend serve a given endpoint. Sources, in
/// precedence order: manual override, AutoTuner control-API status, loaded modules of the
/// local server process, the server binary path, and llama-server's <c>/props</c>. Every
/// source is optional and every failure is a note, never an exception: a benchmark run must
/// still work against a bare OpenAI-compatible endpoint.
/// </summary>
public sealed class ServerRuntimeProbe : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;

    public ServerRuntimeProbe(HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _ownsClient = handler is null;
        _http.Timeout = timeout ?? TimeSpan.FromSeconds(8);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "no-key");
    }

    public async Task<ServerRuntimeInfo> ProbeAsync(
        string serverUrl,
        RuntimeOverride? manual = null,
        ServerRuntimeInfo? autoTunerRuntime = null,
        bool inspectLocalProcess = true,
        CancellationToken cancellationToken = default)
    {
        var notes = new List<string>();
        var props = await TryGetJsonAsync(serverUrl, "/props", notes, cancellationToken).ConfigureAwait(false);
        var models = await TryGetJsonAsync(serverUrl, "/v1/models", notes, cancellationToken).ConfigureAwait(false);
        var health = await TryGetJsonAsync(serverUrl, "/health", notes, cancellationToken).ConfigureAwait(false);

        var observed = new ObservedFacts();
        ReadProps(props, observed);
        ReadModels(models, observed);
        ReadHealth(health, observed);

        if (observed.Engine is null)
        {
            var version = await TryGetJsonAsync(serverUrl, "/version", notes, cancellationToken).ConfigureAwait(false);
            if (version is not null && version.RootElement.ValueKind == JsonValueKind.Object && version.RootElement.TryGetProperty("version", out var vllmVersion))
            {
                observed.Engine = "vllm";
                observed.EngineVersion = ReadString(vllmVersion);
            }
            else
            {
                var ollama = await TryGetJsonAsync(serverUrl, "/api/version", notes, cancellationToken).ConfigureAwait(false);
                if (ollama is not null && ollama.RootElement.ValueKind == JsonValueKind.Object && ollama.RootElement.TryGetProperty("version", out var ollamaVersion))
                {
                    observed.Engine = "ollama";
                    observed.EngineVersion = ReadString(ollamaVersion);
                }
            }
        }

        LocalProcessInfo? process = null;
        if (inspectLocalProcess && LocalProcessInspector.IsLoopback(serverUrl))
        {
            process = await Task.Run(() => LocalProcessInspector.Inspect(serverUrl), cancellationToken).ConfigureAwait(false);
            if (process is null)
            {
                notes.Add("local process for the endpoint could not be identified");
            }
            else
            {
                notes.AddRange(process.Notes.Select(note => "process: " + note));
            }
        }

        return Compose(manual, autoTunerRuntime, observed, process, notes);
    }

    /// <summary>Pure composition step, separated so the precedence rules are unit-testable.</summary>
    public static ServerRuntimeInfo Compose(
        RuntimeOverride? manual,
        ServerRuntimeInfo? autoTunerRuntime,
        ObservedFacts observed,
        LocalProcessInfo? process,
        List<string>? notes = null)
    {
        notes ??= [];
        var launch = process is not null ? ParseLlamaServerArguments(LocalProcessInspector.Tokenize(process.CommandLine)) : new LlamaServerArguments();

        // ---- engine ----
        string engine;
        if (!string.IsNullOrWhiteSpace(manual?.Engine))
        {
            engine = RuntimeKeys.NormalizeEngine(manual.Engine);
        }
        else if (autoTunerRuntime is not null && !string.Equals(autoTunerRuntime.Engine, ServerRuntimeInfo.UnknownValue, StringComparison.OrdinalIgnoreCase))
        {
            engine = RuntimeKeys.NormalizeEngine(autoTunerRuntime.Engine);
        }
        else if (observed.Engine is not null)
        {
            engine = RuntimeKeys.NormalizeEngine(observed.Engine);
        }
        else if (process?.ExecutablePath is { } exe && EngineFromExecutable(exe) is { } fromExe)
        {
            engine = fromExe;
        }
        else
        {
            engine = observed.LooksOpenAiCompatible ? "openai-compatible" : ServerRuntimeInfo.UnknownValue;
        }

        var engineVersion = FirstNonEmpty(manual?.EngineVersion, autoTunerRuntime?.EngineVersion, observed.EngineVersion);

        // ---- backend ----
        string backend;
        string backendSource;
        string? backendDetail = null;
        var moduleBackend = process is null ? null : BackendFromModules(process.Modules, out backendDetail);
        var pathBackend = process?.ExecutablePath is { } path ? RuntimeKeys.BackendFromPath(path) : ServerRuntimeInfo.UnknownValue;
        if (pathBackend == ServerRuntimeInfo.UnknownValue && !string.IsNullOrWhiteSpace(autoTunerRuntime?.ServerBinary))
        {
            pathBackend = RuntimeKeys.BackendFromPath(autoTunerRuntime!.ServerBinary);
        }

        if (!string.IsNullOrWhiteSpace(manual?.Backend))
        {
            backend = RuntimeKeys.NormalizeBackend(manual.Backend);
            backendSource = "manual";
            backendDetail = manual.Backend;
        }
        else if (autoTunerRuntime is not null && autoTunerRuntime.HasKnownBackend)
        {
            backend = RuntimeKeys.NormalizeBackend(autoTunerRuntime.Backend);
            backendSource = "autotuner";
            backendDetail = autoTunerRuntime.BackendDetail ?? autoTunerRuntime.RuntimeLabel;
            if (moduleBackend is not null && moduleBackend != ServerRuntimeInfo.UnknownValue && moduleBackend != backend)
            {
                notes.Add($"AutoTuner reports backend '{backend}' but loaded modules indicate '{moduleBackend}'");
            }
        }
        else if (moduleBackend is not null && moduleBackend != ServerRuntimeInfo.UnknownValue)
        {
            backend = moduleBackend;
            backendSource = "process_modules";
        }
        else if (pathBackend != ServerRuntimeInfo.UnknownValue)
        {
            backend = pathBackend;
            backendSource = "process_path";
            backendDetail = process?.ExecutablePath ?? autoTunerRuntime?.ServerBinary;
        }
        else if (observed.SystemInfoBackend is not null)
        {
            backend = observed.SystemInfoBackend;
            backendSource = "server_props";
            backendDetail = "system_info";
        }
        else
        {
            backend = ServerRuntimeInfo.UnknownValue;
            backendSource = ServerRuntimeInfo.UnknownValue;
        }

        var devices = new List<string>();
        if (autoTunerRuntime?.Devices.Count > 0)
        {
            devices.AddRange(autoTunerRuntime.Devices);
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (autoTunerRuntime is not null)
        {
            foreach (var kvp in autoTunerRuntime.Environment)
            {
                environment[kvp.Key] = kvp.Value;
            }
        }

        var commandLine = FirstNonEmpty(
            autoTunerRuntime?.CommandLine,
            process?.CommandLine is null ? null : LocalProcessInspector.Redact(process.CommandLine));

        return new ServerRuntimeInfo
        {
            Engine = engine,
            EngineVersion = engineVersion,
            Backend = backend,
            BackendSource = backendSource,
            BackendDetail = backendDetail,
            Devices = devices,
            ServerBinary = FirstNonEmpty(autoTunerRuntime?.ServerBinary, process?.ExecutablePath),
            RuntimeLabel = FirstNonEmpty(manual?.RuntimeLabel, autoTunerRuntime?.RuntimeLabel),
            ProcessId = autoTunerRuntime?.ProcessId ?? process?.ProcessId,
            CommandLine = commandLine,
            ModelPath = FirstNonEmpty(autoTunerRuntime?.ModelPath, observed.ModelPath, launch.Model),
            ModelAlias = FirstNonEmpty(autoTunerRuntime?.ModelAlias, observed.ModelAlias, launch.Alias),
            ModelFtype = FirstNonEmpty(autoTunerRuntime?.ModelFtype, observed.ModelFtype),
            ContextSize = autoTunerRuntime?.ContextSize ?? observed.ContextSize ?? launch.ContextSize,
            GpuLayers = autoTunerRuntime?.GpuLayers ?? launch.GpuLayers,
            Threads = autoTunerRuntime?.Threads ?? launch.Threads,
            BatchThreads = autoTunerRuntime?.BatchThreads ?? launch.BatchThreads,
            BatchSize = autoTunerRuntime?.BatchSize ?? launch.BatchSize,
            UBatchSize = autoTunerRuntime?.UBatchSize ?? launch.UBatchSize,
            Parallel = autoTunerRuntime?.Parallel ?? launch.Parallel ?? observed.TotalSlots,
            KvTypeK = FirstNonEmpty(autoTunerRuntime?.KvTypeK, launch.KvTypeK),
            KvTypeV = FirstNonEmpty(autoTunerRuntime?.KvTypeV, launch.KvTypeV),
            FlashAttention = FirstNonEmpty(autoTunerRuntime?.FlashAttention, launch.FlashAttention),
            SpecType = FirstNonEmpty(autoTunerRuntime?.SpecType, launch.SpecType),
            DraftModel = FirstNonEmpty(autoTunerRuntime?.DraftModel, launch.DraftModel),
            MmProj = FirstNonEmpty(autoTunerRuntime?.MmProj, launch.MmProj),
            Environment = environment,
            AutoTunerVersion = autoTunerRuntime?.AutoTunerVersion,
            AutoTunerModelId = autoTunerRuntime?.AutoTunerModelId,
            AutoTunerRuntimeId = autoTunerRuntime?.AutoTunerRuntimeId,
            AutoTunerProfile = autoTunerRuntime?.AutoTunerProfile,
            AutoTunerPerformanceTarget = autoTunerRuntime?.AutoTunerPerformanceTarget,
            ProbedAt = DateTimeOffset.UtcNow,
            ProbeNotes = notes
        };
    }

    public static string? BackendFromModules(IReadOnlyList<string> modules, out string? detail)
    {
        detail = null;
        string? best = null;
        var bestPriority = 0;
        var evidence = new List<string>();
        foreach (var module in modules)
        {
            var backend = RuntimeKeys.BackendFromModuleName(module);
            if (backend == ServerRuntimeInfo.UnknownValue)
            {
                continue;
            }

            evidence.Add(Path.GetFileName(module));
            var priority = RuntimeKeys.BackendPriority(backend);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = backend;
            }
        }

        if (best is null)
        {
            return null;
        }

        detail = string.Join(", ", evidence.Distinct(StringComparer.OrdinalIgnoreCase).Take(6));
        return best;
    }

    private static string? EngineFromExecutable(string executablePath)
    {
        var name = Path.GetFileNameWithoutExtension(executablePath).ToLowerInvariant();
        if (name.Contains("llama-server") || name.Contains("llama_server") || name.StartsWith("llama", StringComparison.Ordinal))
        {
            return "llama.cpp";
        }

        if (name.Contains("koboldcpp"))
        {
            return "koboldcpp";
        }

        if (name.Contains("ollama"))
        {
            return "ollama";
        }

        if (name.Contains("lm studio") || name.Contains("lmstudio") || name.Contains("lms"))
        {
            return "lmstudio";
        }

        if (name.Contains("vllm"))
        {
            return "vllm";
        }

        if (name.Contains("sglang"))
        {
            return "sglang";
        }

        return null;
    }

    // ---- llama-server argument parsing ----------------------------------------------

    public sealed class LlamaServerArguments
    {
        public string? Model { get; set; }
        public string? Alias { get; set; }
        public int? ContextSize { get; set; }
        public int? GpuLayers { get; set; }
        public int? Threads { get; set; }
        public int? BatchThreads { get; set; }
        public int? BatchSize { get; set; }
        public int? UBatchSize { get; set; }
        public int? Parallel { get; set; }
        public string? KvTypeK { get; set; }
        public string? KvTypeV { get; set; }
        public string? FlashAttention { get; set; }
        public string? SpecType { get; set; }
        public string? DraftModel { get; set; }
        public string? MmProj { get; set; }
        public string? Device { get; set; }
    }

    public static LlamaServerArguments ParseLlamaServerArguments(IReadOnlyList<string> tokens)
    {
        var result = new LlamaServerArguments();
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            string key;
            string? value;
            var equals = token.IndexOf('=');
            if (token.StartsWith("--", StringComparison.Ordinal) && equals > 0)
            {
                key = token[..equals];
                value = token[(equals + 1)..];
            }
            else
            {
                key = token;
                value = i + 1 < tokens.Count ? tokens[i + 1] : null;
            }

            string? Take()
            {
                if (equals > 0)
                {
                    return value;
                }

                if (value is null || value.StartsWith('-') && !IsNumeric(value))
                {
                    return null;
                }

                i++;
                return value;
            }

            switch (key)
            {
                case "-m":
                case "--model":
                    result.Model = Take();
                    break;
                case "-a":
                case "--alias":
                    result.Alias = Take();
                    break;
                case "-c":
                case "--ctx-size":
                    result.ContextSize = ParseInt(Take());
                    break;
                case "-ngl":
                case "--n-gpu-layers":
                case "--gpu-layers":
                    result.GpuLayers = ParseInt(Take());
                    break;
                case "-t":
                case "--threads":
                    result.Threads = ParseInt(Take());
                    break;
                case "-tb":
                case "--threads-batch":
                    result.BatchThreads = ParseInt(Take());
                    break;
                case "-b":
                case "--batch-size":
                    result.BatchSize = ParseInt(Take());
                    break;
                case "-ub":
                case "--ubatch-size":
                    result.UBatchSize = ParseInt(Take());
                    break;
                case "-np":
                case "--parallel":
                    result.Parallel = ParseInt(Take());
                    break;
                case "-ctk":
                case "--cache-type-k":
                    result.KvTypeK = Take();
                    break;
                case "-ctv":
                case "--cache-type-v":
                    result.KvTypeV = Take();
                    break;
                case "-fa":
                case "--flash-attn":
                    result.FlashAttention = Take() ?? "on";
                    break;
                case "--spec-type":
                    result.SpecType = Take();
                    break;
                case "-md":
                case "--model-draft":
                    result.DraftModel = Take();
                    break;
                case "--mmproj":
                    result.MmProj = Take();
                    break;
                case "-dev":
                case "--device":
                    result.Device = Take();
                    break;
            }
        }

        return result;
    }

    private static bool IsNumeric(string value) => double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);

    private static int? ParseInt(string? value)
        => int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    // ---- endpoint readers ------------------------------------------------------------

    public sealed class ObservedFacts
    {
        public string? Engine { get; set; }
        public string? EngineVersion { get; set; }
        public string? SystemInfoBackend { get; set; }
        public string? ModelPath { get; set; }
        public string? ModelAlias { get; set; }
        public string? ModelFtype { get; set; }
        public int? ContextSize { get; set; }
        public int? TotalSlots { get; set; }
        public bool LooksOpenAiCompatible { get; set; }
    }

    public static void ReadProps(JsonDocument? props, ObservedFacts observed)
    {
        if (props is null || props.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var root = props.RootElement;
        observed.Engine ??= "llama.cpp";
        if (root.TryGetProperty("build_info", out var buildInfo))
        {
            observed.EngineVersion = ReadString(buildInfo);
        }

        if (root.TryGetProperty("system_info", out var systemInfo) && systemInfo.ValueKind == JsonValueKind.String)
        {
            observed.SystemInfoBackend = BackendFromSystemInfo(systemInfo.GetString());
        }

        if (root.TryGetProperty("model_path", out var modelPath))
        {
            observed.ModelPath = ReadString(modelPath);
        }

        if (root.TryGetProperty("model_alias", out var alias))
        {
            observed.ModelAlias = ReadString(alias);
        }

        if (root.TryGetProperty("model_ftype", out var ftype))
        {
            observed.ModelFtype = ReadString(ftype);
        }

        if (root.TryGetProperty("total_slots", out var slots) && slots.TryGetInt32(out var totalSlots))
        {
            observed.TotalSlots = totalSlots;
        }

        if (root.TryGetProperty("default_generation_settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("n_ctx", out var nCtx)
            && nCtx.TryGetInt32(out var contextSize))
        {
            observed.ContextSize = contextSize;
        }
    }

    public static void ReadModels(JsonDocument? models, ObservedFacts observed)
    {
        if (models is null || models.RootElement.ValueKind != JsonValueKind.Object || !models.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        observed.LooksOpenAiCompatible = true;
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (item.TryGetProperty("owned_by", out var ownedBy) && ownedBy.ValueKind == JsonValueKind.String)
            {
                var owner = ownedBy.GetString() ?? string.Empty;
                var engine = RuntimeKeys.NormalizeEngine(owner);
                if (engine is "llama.cpp" or "vllm" or "sglang" or "ollama" or "lmstudio" or "koboldcpp")
                {
                    observed.Engine ??= engine;
                }
            }

            if (item.TryGetProperty("meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
            {
                if (meta.TryGetProperty("n_ctx", out var nCtx) && nCtx.TryGetInt32(out var contextSize))
                {
                    observed.ContextSize ??= contextSize;
                }

                if (meta.TryGetProperty("ftype", out var ftype) && ftype.ValueKind == JsonValueKind.String)
                {
                    observed.ModelFtype ??= ftype.GetString();
                }
            }
        }
    }

    public static void ReadHealth(JsonDocument? health, ObservedFacts observed)
    {
        if (health is null || health.RootElement.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (health.RootElement.TryGetProperty("service", out var service) && service.ValueKind == JsonValueKind.String
            && (service.GetString() ?? string.Empty).Contains("autotuner", StringComparison.OrdinalIgnoreCase))
        {
            observed.Engine ??= "autotuner-gateway";
            if (health.RootElement.TryGetProperty("version", out var version))
            {
                observed.EngineVersion ??= ReadString(version);
            }
        }
    }

    /// <summary>
    /// llama.cpp's system_info string lists the registered backends first, e.g.
    /// "CUDA : ARCHS = 890 | ... | CPU : SSE3 = 1 ...". ROCm builds print "ROCm".
    /// </summary>
    public static string? BackendFromSystemInfo(string? systemInfo)
    {
        if (string.IsNullOrWhiteSpace(systemInfo))
        {
            return null;
        }

        string? best = null;
        var bestPriority = 0;
        foreach (var segment in systemInfo.Split('|'))
        {
            var colon = segment.IndexOf(':');
            var head = (colon > 0 ? segment[..colon] : segment).Trim();
            if (head.Length == 0 || head.Length > 12)
            {
                continue;
            }

            var backend = RuntimeKeys.NormalizeBackend(head);
            var priority = RuntimeKeys.BackendPriority(backend);
            if (priority > bestPriority)
            {
                bestPriority = priority;
                best = backend;
            }
        }

        return best;
    }

    private async Task<JsonDocument?> TryGetJsonAsync(string serverUrl, string endpoint, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            var baseUri = serverUrl.EndsWith('/') ? serverUrl : serverUrl + "/";
            using var response = await _http.GetAsync(new Uri(new Uri(baseUri), endpoint.TrimStart('/')), cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                notes.Add($"{endpoint}: HTTP {(int)response.StatusCode}");
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(body))
            {
                return null;
            }

            return JsonDocument.Parse(body);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or UriFormatException or InvalidOperationException)
        {
            notes.Add($"{endpoint}: {ex.GetType().Name}");
            return null;
        }
    }

    private static string? ReadString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null
    };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    public void Dispose()
    {
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }
}
