using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Canonical inference-runtime identity of the server a benchmark run talked to: which engine
/// (llama.cpp, vLLM, SGLang, Ollama, ...) and which compute backend (Vulkan, HIP/ROCm, CUDA,
/// SYCL, Metal, OpenCL, CPU) actually generated the answer, plus the launch parameters when
/// they could be observed. Recorded per scorecard so backend cohorts can be compared
/// separately or together; never used by the scorer.
/// </summary>
public sealed class ServerRuntimeInfo
{
    public const string UnknownValue = "unknown";

    /// <summary>llama.cpp | vllm | sglang | ollama | lmstudio | koboldcpp | autotuner-gateway | openai-compatible | unknown</summary>
    [JsonPropertyName("engine")]
    public string Engine { get; init; } = UnknownValue;

    /// <summary>Engine build/version string, e.g. llama.cpp <c>b10786-0f3a71be1</c> or vLLM <c>0.11.0</c>.</summary>
    [JsonPropertyName("engineVersion")]
    public string? EngineVersion { get; init; }

    /// <summary>Canonical lowercase backend: vulkan | hip | cuda | sycl | metal | opencl | cpu | unknown.</summary>
    [JsonPropertyName("backend")]
    public string Backend { get; init; } = UnknownValue;

    /// <summary>manual | autotuner | process_modules | process_path | server_props | unknown</summary>
    [JsonPropertyName("backendSource")]
    public string BackendSource { get; init; } = UnknownValue;

    /// <summary>Evidence behind the backend decision, e.g. the loaded module name.</summary>
    [JsonPropertyName("backendDetail")]
    public string? BackendDetail { get; init; }

    [JsonPropertyName("devices")]
    public List<string> Devices { get; init; } = [];

    [JsonPropertyName("serverBinary")]
    public string? ServerBinary { get; init; }

    /// <summary>Human label of the build, e.g. "b10786 Vulkan" (AutoTuner runtime label or derived).</summary>
    [JsonPropertyName("runtimeLabel")]
    public string? RuntimeLabel { get; init; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    /// <summary>Observed launch command line with API keys redacted.</summary>
    [JsonPropertyName("commandLine")]
    public string? CommandLine { get; init; }

    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; init; }

    [JsonPropertyName("modelAlias")]
    public string? ModelAlias { get; init; }

    [JsonPropertyName("modelFtype")]
    public string? ModelFtype { get; init; }

    [JsonPropertyName("contextSize")]
    public int? ContextSize { get; init; }

    [JsonPropertyName("gpuLayers")]
    public int? GpuLayers { get; init; }

    [JsonPropertyName("threads")]
    public int? Threads { get; init; }

    [JsonPropertyName("batchThreads")]
    public int? BatchThreads { get; init; }

    [JsonPropertyName("batchSize")]
    public int? BatchSize { get; init; }

    [JsonPropertyName("ubatchSize")]
    public int? UBatchSize { get; init; }

    [JsonPropertyName("parallel")]
    public int? Parallel { get; init; }

    [JsonPropertyName("kvTypeK")]
    public string? KvTypeK { get; init; }

    [JsonPropertyName("kvTypeV")]
    public string? KvTypeV { get; init; }

    [JsonPropertyName("flashAttention")]
    public string? FlashAttention { get; init; }

    [JsonPropertyName("specType")]
    public string? SpecType { get; init; }

    [JsonPropertyName("draftModel")]
    public string? DraftModel { get; init; }

    [JsonPropertyName("mmproj")]
    public string? MmProj { get; init; }

    [JsonPropertyName("environment")]
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("autoTunerVersion")]
    public string? AutoTunerVersion { get; init; }

    [JsonPropertyName("autoTunerModelId")]
    public string? AutoTunerModelId { get; init; }

    [JsonPropertyName("autoTunerRuntimeId")]
    public string? AutoTunerRuntimeId { get; init; }

    [JsonPropertyName("autoTunerProfile")]
    public string? AutoTunerProfile { get; init; }

    [JsonPropertyName("autoTunerPerformanceTarget")]
    public string? AutoTunerPerformanceTarget { get; init; }

    [JsonPropertyName("probedAt")]
    public DateTimeOffset? ProbedAt { get; init; }

    /// <summary>Non-fatal probe notes (endpoints that failed, heuristics used).</summary>
    [JsonPropertyName("probeNotes")]
    public List<string> ProbeNotes { get; init; } = [];

    [JsonIgnore]
    public bool HasKnownBackend => !string.Equals(Backend, UnknownValue, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Backend);

    /// <summary>Stable key for grouping scorecards by runtime: engine + backend + build.</summary>
    [JsonIgnore]
    public string RuntimeKey => RuntimeKeys.Build(Engine, Backend, EngineVersion);

    [JsonIgnore]
    public string DisplayLabel => RuntimeKeys.DisplayLabel(Engine, Backend, EngineVersion, RuntimeLabel);

    public string Summary()
    {
        var parts = new List<string> { DisplayLabel, $"backend source {BackendSource}" };
        if (Devices.Count > 0) parts.Add("devices " + string.Join(" | ", Devices));
        if (!string.IsNullOrWhiteSpace(ServerBinary)) parts.Add("binary " + ServerBinary);
        if (ContextSize.HasValue) parts.Add($"ctx {ContextSize}");
        if (GpuLayers.HasValue) parts.Add($"ngl {GpuLayers}");
        if (Threads.HasValue) parts.Add($"threads {Threads}");
        if (BatchSize.HasValue) parts.Add($"batch {BatchSize}/{UBatchSize?.ToString() ?? "?"}");
        if (!string.IsNullOrWhiteSpace(SpecType)) parts.Add("spec " + SpecType);
        return string.Join(" · ", parts);
    }
}

/// <summary>Manual runtime identity entered by the user (CLI --backend/--engine, GUI fields).</summary>
public sealed class RuntimeOverride
{
    public string? Engine { get; init; }
    public string? EngineVersion { get; init; }
    public string? Backend { get; init; }
    public string? RuntimeLabel { get; init; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Engine) && string.IsNullOrWhiteSpace(EngineVersion)
                           && string.IsNullOrWhiteSpace(Backend) && string.IsNullOrWhiteSpace(RuntimeLabel);
}

/// <summary>Canonical backend/engine vocabulary shared by the probe, the archive, and the comparison.</summary>
public static partial class RuntimeKeys
{
    public static readonly IReadOnlyList<string> KnownBackends = ["vulkan", "hip", "cuda", "sycl", "metal", "opencl", "cpu"];

    /// <summary>Maps free-form backend names (ROCm, Vulkan0, ggml-cuda, "HIP") to the canonical token.</summary>
    public static string NormalizeBackend(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return ServerRuntimeInfo.UnknownValue;
        }

        if (text.Contains("vulkan") || text == "vk" || text.StartsWith("vk", StringComparison.Ordinal) && text.Length <= 4)
        {
            return "vulkan";
        }

        if (text.Contains("hip") || text.Contains("rocm") || text.Contains("amdgpu"))
        {
            return "hip";
        }

        if (text.Contains("cuda") || text.Contains("nvidia") || text.Contains("cublas"))
        {
            return "cuda";
        }

        if (text.Contains("sycl") || text.Contains("oneapi") || text.Contains("level-zero") || text.Contains("levelzero"))
        {
            return "sycl";
        }

        if (text.Contains("metal"))
        {
            return "metal";
        }

        if (text.Contains("opencl") || text.Contains("clblast"))
        {
            return "opencl";
        }

        if (text == "cpu" || text.Contains("cpu") || text.Contains("blas") || text.Contains("avx"))
        {
            return "cpu";
        }

        return ServerRuntimeInfo.UnknownValue;
    }

    public static string NormalizeEngine(string? value)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0)
        {
            return ServerRuntimeInfo.UnknownValue;
        }

        if (text.Contains("llama") || text == "llamacpp" || text.Contains("ggml"))
        {
            return "llama.cpp";
        }

        if (text.Contains("vllm"))
        {
            return "vllm";
        }

        if (text.Contains("sglang"))
        {
            return "sglang";
        }

        if (text.Contains("ollama"))
        {
            return "ollama";
        }

        if (text.Contains("lmstudio") || text.Contains("lm studio") || text.Contains("lm-studio"))
        {
            return "lmstudio";
        }

        if (text.Contains("kobold"))
        {
            return "koboldcpp";
        }

        if (text.Contains("autotuner"))
        {
            return "autotuner-gateway";
        }

        return text;
    }

    public static string DisplayBackend(string? backend) => NormalizeBackend(backend) switch
    {
        "vulkan" => "Vulkan",
        "hip" => "HIP/ROCm",
        "cuda" => "CUDA",
        "sycl" => "SYCL",
        "metal" => "Metal",
        "opencl" => "OpenCL",
        "cpu" => "CPU",
        _ => "unbekannt"
    };

    public static string Build(string? engine, string? backend, string? engineVersion)
    {
        var e = NormalizeEngine(engine);
        var b = NormalizeBackend(backend);
        var v = string.IsNullOrWhiteSpace(engineVersion) ? ServerRuntimeInfo.UnknownValue : engineVersion.Trim();
        return $"{e}/{b}/{v}";
    }

    public static string DisplayLabel(string? engine, string? backend, string? engineVersion, string? runtimeLabel = null)
    {
        if (!string.IsNullOrWhiteSpace(runtimeLabel))
        {
            return runtimeLabel.Trim();
        }

        var e = NormalizeEngine(engine);
        var b = DisplayBackend(backend);
        var v = string.IsNullOrWhiteSpace(engineVersion) ? string.Empty : " " + ShortBuild(engineVersion);
        return e == ServerRuntimeInfo.UnknownValue && b == "unbekannt" ? "Runtime unbekannt" : $"{e}{v} · {b}";
    }

    /// <summary>"b10786-0f3a71be1" → "b10786"; other version strings are returned unchanged.</summary>
    public static string ShortBuild(string engineVersion)
    {
        var match = BuildNumberRegex().Match(engineVersion ?? string.Empty);
        return match.Success ? match.Value : (engineVersion ?? string.Empty).Trim();
    }

    /// <summary>Guesses the backend from a llama-server binary path such as <c>...\b10786_vulkan_llama.cpp\...</c>.</summary>
    public static string BackendFromPath(string? path)
    {
        var text = (path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
        if (text.Length == 0)
        {
            return ServerRuntimeInfo.UnknownValue;
        }

        // Look at directory segments only; the file name (llama-server.exe) carries no backend.
        var segments = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments.Reverse())
        {
            if (segment.Contains("vulkan") || Regex.IsMatch(segment, @"(^|[_\-.])vk([_\-.]|$)"))
            {
                return "vulkan";
            }

            if (Regex.IsMatch(segment, @"(^|[_\-.])(hip|rocm)([_\-.]|$)") || segment.Contains("hipblas"))
            {
                return "hip";
            }

            if (segment.Contains("cuda") || Regex.IsMatch(segment, @"(^|[_\-.])cu\d{2,3}([_\-.]|$)"))
            {
                return "cuda";
            }

            if (segment.Contains("sycl") || segment.Contains("oneapi"))
            {
                return "sycl";
            }

            if (segment.Contains("metal"))
            {
                return "metal";
            }

            if (segment.Contains("opencl"))
            {
                return "opencl";
            }

            if (Regex.IsMatch(segment, @"(^|[_\-.])(cpu|avx2|avx512|openblas)([_\-.]|$)"))
            {
                return "cpu";
            }
        }

        return ServerRuntimeInfo.UnknownValue;
    }

    /// <summary>
    /// Classifies a loaded module (DLL / shared object) name. GPU backends outrank the CPU
    /// backend because every llama.cpp process also loads ggml-cpu.
    /// </summary>
    public static string BackendFromModuleName(string? moduleName)
    {
        var name = Path.GetFileName((moduleName ?? string.Empty).Replace('\\', '/')).ToLowerInvariant();
        if (name.Length == 0)
        {
            return ServerRuntimeInfo.UnknownValue;
        }

        if (name.StartsWith("ggml-vulkan", StringComparison.Ordinal) || name.StartsWith("vulkan-1", StringComparison.Ordinal) || name.StartsWith("libvulkan", StringComparison.Ordinal))
        {
            return "vulkan";
        }

        if (name.StartsWith("ggml-hip", StringComparison.Ordinal) || name.StartsWith("amdhip64", StringComparison.Ordinal) || name.StartsWith("libamdhip64", StringComparison.Ordinal)
            || name.StartsWith("rocblas", StringComparison.Ordinal) || name.StartsWith("hipblas", StringComparison.Ordinal) || name.StartsWith("librocblas", StringComparison.Ordinal))
        {
            return "hip";
        }

        if (name.StartsWith("ggml-cuda", StringComparison.Ordinal) || name.StartsWith("nvcuda", StringComparison.Ordinal) || name.StartsWith("libcuda", StringComparison.Ordinal)
            || name.StartsWith("cudart", StringComparison.Ordinal) || name.StartsWith("libcudart", StringComparison.Ordinal) || name.StartsWith("cublas", StringComparison.Ordinal))
        {
            return "cuda";
        }

        if (name.StartsWith("ggml-sycl", StringComparison.Ordinal) || name.StartsWith("sycl", StringComparison.Ordinal) || name.StartsWith("libsycl", StringComparison.Ordinal)
            || name.StartsWith("ze_loader", StringComparison.Ordinal) || name.StartsWith("libze_loader", StringComparison.Ordinal))
        {
            return "sycl";
        }

        if (name.StartsWith("ggml-metal", StringComparison.Ordinal))
        {
            return "metal";
        }

        if (name.StartsWith("ggml-opencl", StringComparison.Ordinal) || name == "opencl.dll" || name.StartsWith("libopencl", StringComparison.Ordinal))
        {
            return "opencl";
        }

        if (name.StartsWith("ggml-cpu", StringComparison.Ordinal) || name.StartsWith("ggml-blas", StringComparison.Ordinal))
        {
            return "cpu";
        }

        return ServerRuntimeInfo.UnknownValue;
    }

    public static int BackendPriority(string backend) => NormalizeBackend(backend) switch
    {
        "cuda" => 6,
        "hip" => 5,
        "vulkan" => 4,
        "sycl" => 3,
        "metal" => 3,
        "opencl" => 2,
        "cpu" => 1,
        _ => 0
    };

    [GeneratedRegex(@"\bb\d{3,6}\b", RegexOptions.IgnoreCase)]
    private static partial Regex BuildNumberRegex();
}
