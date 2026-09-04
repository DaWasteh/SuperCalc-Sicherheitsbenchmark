using System.Net;
using System.Text;
using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

/// <summary>v0.7.6: parser-v3 lenient JSON repair, runtime/backend identity, AutoTuner client, campaigns.</summary>
internal static partial class TestRunner
{
    private static void LenientJsonRepairFixesCommonDefects()
    {
        var parser = new ResponseParser();

        // 1. leading zeros in line numbers (GLM/VibeCoder/Muse pattern) — the #1 cause of text_fallback.
        var leadingZeros = """
```json
{
  "summary": "two findings",
  "findings": [
    { "title": "Format string", "vulnerability_type": "CWE-134", "severity": "High", "confidence": 0.95, "file": "enhanced_calc.cpp", "line_start": 0218, "line_end": 0219, "evidence": "printf(fmt)" },
    { "title": "Command injection", "vulnerability_type": "CWE-78", "severity": "Critical", "confidence": 0.05, "file": "enhanced_calc.cpp", "line_start": 0602, "line_end": 0609, "evidence": "system(cmd.c_str())" }
  ]
}
```
""";
        var parsed = parser.Parse(leadingZeros);
        Assert(parsed.Findings.Count == 2, $"leading-zero JSON should yield 2 findings, got {parsed.Findings.Count} ({parsed.ParseMode})");
        Assert(parsed.ParseMode == "markdown_json", $"fenced leading-zero JSON should parse as markdown_json, got {parsed.ParseMode}");
        Assert(parsed.Findings[0].LineStart == 218 && parsed.Findings[0].LineEnd == 219, "leading zeros must be stripped from line numbers");
        Assert(Math.Abs(parsed.Findings[1].Confidence - 0.05) < 1e-9, "fractions such as 0.05 must never be treated as leading-zero integers");
        Assert(parsed.Repairs.Contains("leading_zero"), "repair list must name leading_zero");
        Assert(parsed.Warning?.Contains("Lenient JSON repair", StringComparison.Ordinal) == true, "warning must disclose that lenient repair was applied");

        // 2. invalid escapes, raw control characters and unescaped inner quotes.
        var messy = "{\n  \"findings\": [\n    { \"title\": \"Regex \\d+ path \\.\", \"vulnerability_type\": \"x\", \"severity\": \"Low\", \"confidence\": 0.6, \"file\": \"enhanced_calc.cpp\", \"line_start\": 10, \"line_end\": 12, \"evidence\": \"line1\nline2\" },\n    { \"title\": \"Quote\", \"vulnerability_type\": \"y\", \"severity\": \"Low\", \"confidence\": 0.6, \"file\": \"enhanced_calc.cpp\", \"line_start\": 20, \"line_end\": 21, \"evidence\": \"std::cout << \"Variable \" << var_name << \"\\n\"\" }\n  ]\n}";
        var repaired = parser.Parse(messy);
        Assert(repaired.Findings.Count == 2, $"invalid escape/control/quote JSON should yield 2 findings, got {repaired.Findings.Count} ({repaired.ParseMode}: {repaired.Warning})");
        Assert(repaired.Findings[0].Evidence.Contains("line1\nline2", StringComparison.Ordinal), "raw newline must be preserved as an escaped newline");
        Assert(repaired.Findings[0].Title.Contains("\\d+", StringComparison.Ordinal), "invalid escape \\d must survive as literal backslash-d");
        Assert(repaired.Findings[1].Evidence.Contains("\"Variable \"", StringComparison.Ordinal), "unescaped inner quotes must be kept as quotes inside the value");
        Assert(repaired.Repairs.Contains("invalid_escape") && repaired.Repairs.Contains("raw_control_char") && repaired.Repairs.Contains("unescaped_quote"), $"repairs should list every applied fix, got {string.Join(",", repaired.Repairs)}");

        // 3. missing commas between properties and between array objects.
        var missingCommas = "{ \"findings\": [ { \"title\": \"A\", \"vulnerability_type\": \"a\" \"severity\": \"Low\", \"confidence\": 0.7, \"file\": \"f\", \"line_start\": 1, \"line_end\": 1, \"evidence\": \"e\" } { \"title\": \"B\", \"vulnerability_type\": \"b\", \"severity\": \"Low\", \"confidence\": 0.7, \"file\": \"f\", \"line_start\": 2, \"line_end\": 2, \"evidence\": \"e\" } ] }";
        var commas = parser.Parse(missingCommas);
        Assert(commas.Findings.Count == 2, $"missing commas should be repaired, got {commas.Findings.Count} ({commas.Warning})");
        Assert(commas.Repairs.Contains("missing_comma"), "missing_comma must be reported");

        // 4. Direct repair API keeps exponents/fractions intact.
        var direct = LenientJsonRepair.Repair("{\"a\": 0.05, \"b\": 1e-3, \"c\": -007, \"d\": 0}");
        Assert(direct.Json == "{\"a\": 0.05, \"b\": 1e-3, \"c\": -7, \"d\": 0}", $"repair must only touch leading zeros: {direct.Json}");
        Assert(direct.Repairs.Count == 1 && direct.Repairs[0] == "leading_zero", "only leading_zero should be reported");
    }

    private static void ParserDoesNotRepairValidJson()
    {
        var parser = new ResponseParser();
        var valid = "{\"summary\":\"ok\",\"findings\":[{\"title\":\"A \\\"quoted\\\" title\",\"vulnerability_type\":\"t\",\"severity\":\"High\",\"confidence\":0.05,\"file\":\"enhanced_calc.cpp\",\"line_start\":10,\"line_end\":12,\"evidence\":\"x = \\\"y\\\";\\ttab\"}]}";
        var parsed = parser.Parse(valid);
        Assert(parsed.Findings.Count == 1 && parsed.ParseMode == "json", "valid JSON must parse directly");
        Assert(parsed.Repairs.Count == 0, "valid JSON must never be rewritten");
        Assert(parsed.Findings[0].Title == "A \"quoted\" title", "escaped quotes in valid JSON must round-trip");
        Assert(Math.Abs(parsed.Findings[0].Confidence - 0.05) < 1e-9, "valid fractions must be untouched");
        Assert(parsed.Warning is null, $"valid JSON must not carry a repair warning, got {parsed.Warning}");

        var untouched = LenientJsonRepair.Repair("{\"a\":\"b\",\"n\":[1,2,3],\"t\":true,\"z\":null}");
        Assert(!untouched.Changed, "well-formed JSON must not trigger any repair");
    }

    private static void ParserAcceptsFindingsEmbeddedInSchemaProperties()
    {
        var parser = new ResponseParser();
        var mixed = """
{"$schema":"https://json-schema.org/draft/2020-12/schema","title":"SuperCalc LLM Findings Response","type":"object","required":["findings"],
 "properties":{"summary":{"type":"string"},
   "findings":[
     {"title":"Command injection","vulnerability_type":"OS Command Injection","cwe":"CWE-78","severity":"Critical","confidence":0.9,"file":"enhanced_calc.cpp","line_start":605,"line_end":609,"evidence":"system(cmd.c_str())"},
     {"title":"Hard-coded secret","vulnerability_type":"Credentials","cwe":"CWE-798","severity":"High","confidence":0.9,"file":"enhanced_calc.cpp","line_start":73,"line_end":73,"evidence":"ADMIN_SECRET[]"}
   ]}}
""";
        var parsed = parser.Parse(mixed);
        Assert(parsed.Findings.Count == 2, $"findings placed under an echoed schema's properties must be read, got {parsed.Findings.Count} ({parsed.ParseMode}: {parsed.Warning})");
        Assert(parsed.ParsedJson && !parsed.UsedTextFallback, "embedded properties findings must count as JSON parse");
        Assert(parsed.Warning?.Contains("properties.findings", StringComparison.Ordinal) == true, "warning must disclose the embedded schema shape");

        var pureEcho = """
{"$schema":"https://json-schema.org/draft/2020-12/schema","title":"SuperCalc LLM Findings Response","type":"object",
 "properties":{"findings":{"type":"array","items":{"type":"object","properties":{"title":{"type":"string"},"severity":{"type":"string"}}}}}}
""";
        var echo = parser.Parse(pureEcho);
        Assert(echo.Findings.Count == 0, "a pure schema echo must still yield zero findings");
    }

    private static void RunnerTreatsStrayThinkCloseAsReasoning()
    {
        var prose = "Looking at this carefully, finding 1 is confirmed.\n\nAll 11 findings are supported.</think>{\"findings\":[{\"title\":\"A\",\"vulnerability_type\":\"a\",\"severity\":\"Low\",\"confidence\":0.7,\"file\":\"f\",\"line_start\":1,\"line_end\":1,\"evidence\":\"e\"}]}";
        var split = BenchmarkRunner.ExtractInlineThinkBlocks(prose);
        Assert(split.InlineReasoning.StartsWith("Looking at this carefully", StringComparison.Ordinal), "text before a stray </think> must become reasoning");
        Assert(split.OutputContent.TrimStart().StartsWith('{'), "the answer after the stray </think> must remain the output");
        Assert(new ResponseParser().Parse(split.OutputContent).Findings.Count == 1, "the remaining output must parse as findings");

        var loopTail = "```json\n{\"findings\":[{\"title\":\"A\",\"vulnerability_type\":\"a\",\"severity\":\"Low\",\"confidence\":0.7,\"file\":\"f\",\"line_start\":1,\"line_end\":1,\"evidence\":\"e\"}]}\n```\nrepeated garbage </think> more garbage";
        var unchanged = BenchmarkRunner.ExtractInlineThinkBlocks(loopTail);
        Assert(string.IsNullOrEmpty(unchanged.InlineReasoning) && unchanged.OutputContent == loopTail, "a </think> after the answer payload must not swallow the answer");

        var proper = "<think>hidden</think>{\"findings\":[]}";
        var properSplit = BenchmarkRunner.ExtractInlineThinkBlocks(proper);
        Assert(properSplit.InlineReasoning == "hidden" && properSplit.OutputContent == "{\"findings\":[]}", "regular <think> blocks keep working");
    }

    private static void RuntimeKeysClassifyBackends()
    {
        Assert(RuntimeKeys.BackendFromPath(@"L:\LAB\ai-local\b10786_vulkan_llama.cpp\build\bin\Release\llama-server.exe") == "vulkan", "vulkan build folder must classify as vulkan");
        Assert(RuntimeKeys.BackendFromPath(@"L:\LAB\ai-local\b10786_hip_llama.cpp\build\bin\llama-server.exe") == "hip", "hip build folder must classify as hip");
        Assert(RuntimeKeys.BackendFromPath("/opt/llama-cuda12/bin/llama-server") == "cuda", "cuda folder must classify as cuda");
        Assert(RuntimeKeys.BackendFromPath(@"C:\tools\llama-b1234-bin-win-sycl-x64\llama-server.exe") == "sycl", "sycl release folder must classify as sycl");
        Assert(RuntimeKeys.BackendFromPath(@"C:\tools\llama\llama-server.exe") == ServerRuntimeInfo.UnknownValue, "no backend token means unknown, never a guess");

        Assert(RuntimeKeys.BackendFromModuleName(@"C:\Windows\System32\vulkan-1.dll") == "vulkan", "vulkan loader module");
        Assert(RuntimeKeys.BackendFromModuleName(@"L:\LAB\amdhip64_7.dll") == "hip", "HIP runtime module");
        Assert(RuntimeKeys.BackendFromModuleName("ggml-cuda.dll") == "cuda" && RuntimeKeys.BackendFromModuleName("nvcuda.dll") == "cuda", "CUDA modules");
        Assert(RuntimeKeys.BackendFromModuleName("ggml-cpu-haswell.dll") == "cpu", "CPU backend module");
        Assert(RuntimeKeys.BackendFromModuleName("kernel32.dll") == ServerRuntimeInfo.UnknownValue, "unrelated modules are unknown");

        Assert(ServerRuntimeProbe.BackendFromModules(["kernel32.dll", "ggml-cpu.dll", "vulkan-1.dll"], out var detail) == "vulkan" && detail!.Contains("vulkan-1.dll", StringComparison.Ordinal), "GPU backend must outrank the always-present CPU backend");
        Assert(ServerRuntimeProbe.BackendFromModules(["kernel32.dll", "ggml-cpu.dll"], out _) == "cpu", "only CPU modules means CPU backend");
        Assert(ServerRuntimeProbe.BackendFromModules(["kernel32.dll"], out _) is null, "no ggml modules means no evidence");

        Assert(RuntimeKeys.NormalizeBackend("HIP") == "hip" && RuntimeKeys.NormalizeBackend("ROCm0") == "hip" && RuntimeKeys.NormalizeBackend("Vulkan") == "vulkan" && RuntimeKeys.NormalizeBackend("ggml-cuda") == "cuda", "backend aliases must normalize");
        Assert(RuntimeKeys.NormalizeEngine("llamacpp") == "llama.cpp" && RuntimeKeys.NormalizeEngine("vLLM") == "vllm", "engine aliases must normalize");
        Assert(RuntimeKeys.Build("llama.cpp", "Vulkan", "b10786-0f3a71be1") == "llama.cpp/vulkan/b10786-0f3a71be1", "runtime key format");
        Assert(RuntimeKeys.DisplayLabel("llama.cpp", "hip", "b10786-abc") == "llama.cpp b10786 · HIP/ROCm", $"display label, got {RuntimeKeys.DisplayLabel("llama.cpp", "hip", "b10786-abc")}");
        Assert(ServerRuntimeProbe.BackendFromSystemInfo("CUDA : ARCHS = 890 | USE_GRAPHS = 1 | CPU : SSE3 = 1 | AVX = 1") == "cuda", "system_info must yield the GPU backend");
        Assert(ServerRuntimeProbe.BackendFromSystemInfo("CPU : SSE3 = 1 | AVX2 = 1") == "cpu", "CPU-only system_info");
    }

    private static void RuntimeProbeComposesPrecedenceAndLaunchArguments()
    {
        var commandLine = @"L:\LAB\ai-local\b10760_vulkan_llama.cpp\build\bin\Release\llama-server.exe -m I:\models\Alibaba\Qwen3.8\Qwen3.8-27B-Ridge-3.7bpw.gguf -c 262144 -ngl 999 -t 12 -tb 16 -b 1024 -ub 1024 -ctk q4_0 -ctv q4_0 --host 127.0.0.1 --port 1234 --spec-type draft-dflash,ngram-map-k4v -md I:\models\draft.gguf -fa on --parallel 1 --mmproj ""I:\models\mm proj.gguf"" --api-key secret123 -a Qwen3.8-27B-Ridge-3.7bpw";
        var tokens = LocalProcessInspector.Tokenize(commandLine);
        var launch = ServerRuntimeProbe.ParseLlamaServerArguments(tokens);
        Assert(launch.ContextSize == 262144 && launch.GpuLayers == 999 && launch.Threads == 12 && launch.BatchThreads == 16 && launch.BatchSize == 1024 && launch.UBatchSize == 1024, "numeric llama-server arguments must parse");
        Assert(launch.KvTypeK == "q4_0" && launch.KvTypeV == "q4_0" && launch.FlashAttention == "on" && launch.SpecType == "draft-dflash,ngram-map-k4v" && launch.Parallel == 1, "string llama-server arguments must parse");
        Assert(launch.MmProj == @"I:\models\mm proj.gguf" && launch.Alias == "Qwen3.8-27B-Ridge-3.7bpw" && launch.DraftModel == @"I:\models\draft.gguf", "quoted paths and alias must parse");
        Assert(LocalProcessInspector.Redact(commandLine).Contains("--api-key <redacted>", StringComparison.Ordinal) && !LocalProcessInspector.Redact(commandLine).Contains("secret123", StringComparison.Ordinal), "API keys must be redacted before archiving");

        var process = new LocalProcessInfo
        {
            ProcessId = 4242,
            ExecutablePath = @"L:\LAB\ai-local\b10760_hip_llama.cpp\build\bin\llama-server.exe",
            CommandLine = commandLine,
            Modules = [@"C:\Windows\System32\kernel32.dll", @"L:\LAB\ai-local\b10760_hip_llama.cpp\build\bin\amdhip64_7.dll", @"L:\LAB\ai-local\b10760_hip_llama.cpp\build\bin\ggml-cpu.dll"]
        };
        var observed = new ServerRuntimeProbe.ObservedFacts { Engine = "llama.cpp", EngineVersion = "b10760-0f3a71be1", SystemInfoBackend = "cpu", ContextSize = 262144, ModelFtype = "IQ2_M - 2.7 bpw" };

        var fromModules = ServerRuntimeProbe.Compose(null, null, observed, process);
        Assert(fromModules.Backend == "hip" && fromModules.BackendSource == "process_modules", $"loaded modules must win over /props system_info, got {fromModules.Backend}/{fromModules.BackendSource}");
        Assert(fromModules.Engine == "llama.cpp" && fromModules.EngineVersion == "b10760-0f3a71be1", "engine identity from /props");
        Assert(fromModules.GpuLayers == 999 && fromModules.Threads == 12 && fromModules.SpecType == "draft-dflash,ngram-map-k4v", "launch parameters must come from the command line");
        Assert(fromModules.CommandLine!.Contains("<redacted>", StringComparison.Ordinal), "composed command line must be redacted");
        Assert(fromModules.RuntimeKey == "llama.cpp/hip/b10760-0f3a71be1", "runtime key must combine engine, backend and build");

        var pathOnly = ServerRuntimeProbe.Compose(null, null, observed, new LocalProcessInfo { ProcessId = 1, ExecutablePath = process.ExecutablePath, Modules = [] });
        Assert(pathOnly.Backend == "hip" && pathOnly.BackendSource == "process_path", "binary path is the fallback when modules are unreadable");

        var propsOnly = ServerRuntimeProbe.Compose(null, null, observed, null);
        Assert(propsOnly.Backend == "cpu" && propsOnly.BackendSource == "server_props", "system_info is the last automatic fallback");

        var autoTuner = new ServerRuntimeInfo { Engine = "llama.cpp", Backend = "vulkan", BackendSource = "autotuner", RuntimeLabel = "b10760 Vulkan", GpuLayers = 99, Devices = ["AMD Radeon AI PRO R9700"] };
        var withTuner = ServerRuntimeProbe.Compose(null, autoTuner, observed, process);
        Assert(withTuner.Backend == "vulkan" && withTuner.BackendSource == "autotuner" && withTuner.GpuLayers == 99 && withTuner.Devices.Count == 1, "AutoTuner identity outranks process inspection");
        Assert(withTuner.ProbeNotes.Any(n => n.Contains("loaded modules indicate 'hip'", StringComparison.Ordinal)), "a disagreement between AutoTuner and modules must be noted");

        var manual = ServerRuntimeProbe.Compose(new RuntimeOverride { Backend = "CUDA", RuntimeLabel = "manual" }, autoTuner, observed, process);
        Assert(manual.Backend == "cuda" && manual.BackendSource == "manual" && manual.RuntimeLabel == "manual", "manual override outranks everything");

        var nothing = ServerRuntimeProbe.Compose(null, null, new ServerRuntimeProbe.ObservedFacts(), null);
        Assert(nothing.Backend == ServerRuntimeInfo.UnknownValue && nothing.Engine == ServerRuntimeInfo.UnknownValue && !nothing.HasKnownBackend, "no evidence means unknown");
    }

    private sealed class AutoTunerFakeHandler : HttpMessageHandler
    {
        public int SwitchCalls { get; private set; }
        public List<string> Authorizations { get; } = [];
        public bool RuntimesAvailable { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Authorizations.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            var path = request.RequestUri!.AbsolutePath;
            string body;
            var status = HttpStatusCode.OK;
            switch (path)
            {
                case "/health":
                    body = "{\"status\":\"ok\",\"service\":\"autotuner-control-api\",\"version\":\"5.3.9\"}";
                    break;
                case "/api/v1/models":
                    body = "{\"models\":[{\"id\":\"qwen38-27b--abc\",\"name\":\"Qwen3.8-27B\",\"path\":\"I:\\\\models\\\\q.gguf\",\"context_window\":262144,\"max_tokens\":16384,\"reasoning\":true,\"runnable\":true,\"unavailable_reason\":\"\",\"default_runtime_id\":\"b10786-vulkan-llama-cpp\",\"quant\":\"IQ2_M - 2.7 bpw\",\"params_b\":27.3,\"size_bytes\":12599187008},{\"id\":\"draft--x\",\"name\":\"Draft\",\"path\":\"d\",\"context_window\":4096,\"runnable\":false,\"unavailable_reason\":\"Standalone draft models cannot serve requests by themselves.\"}],\"status\":\"idle\"}";
                    break;
                case "/api/v1/runtimes":
                    if (!RuntimesAvailable) { status = HttpStatusCode.NotFound; body = "{\"error\":{\"message\":\"Endpoint not found.\",\"type\":\"autotuner_control_error\",\"code\":\"not_found\"}}"; break; }
                    body = "{\"runtimes\":[{\"id\":\"b10786-vulkan-llama-cpp\",\"label\":\"b10786 Vulkan\",\"server_binary\":\"L:\\\\LAB\\\\ai-local\\\\b10786_vulkan_llama.cpp\\\\build\\\\bin\\\\Release\\\\llama-server.exe\",\"backend\":\"vulkan\",\"build\":\"b10786\",\"build_info\":\"b10786-abc123\",\"is_default\":true,\"available\":true,\"unavailable_reason\":\"\"},{\"id\":\"b10786-hip-llama-cpp\",\"label\":\"b10786 HIP\",\"server_binary\":\"L:\\\\LAB\\\\ai-local\\\\b10786_hip_llama.cpp\\\\build\\\\bin\\\\llama-server.exe\",\"backend\":\"hip\",\"build\":\"b10786\",\"build_info\":\"b10786-abc123\",\"is_default\":false,\"available\":true,\"unavailable_reason\":\"\"}],\"default_runtime_id\":\"b10786-vulkan-llama-cpp\"}";
                    break;
                case "/api/v1/switch":
                    SwitchCalls++;
                    if (SwitchCalls == 1)
                    {
                        status = HttpStatusCode.Conflict;
                        body = "{\"error\":{\"message\":\"AutoTuner is busy with an exclusive benchmark or OCR workflow.\",\"type\":\"autotuner_control_error\",\"code\":\"autotuner_busy\"}}";
                        break;
                    }

                    body = StatusJson();
                    break;
                case "/api/v1/status":
                    body = StatusJson();
                    break;
                case "/api/v1/stop":
                    body = "{\"status\":\"stopped\"}";
                    break;
                default:
                    status = HttpStatusCode.NotFound;
                    body = "{\"error\":{\"message\":\"Endpoint not found.\",\"type\":\"autotuner_control_error\",\"code\":\"not_found\"}}";
                    break;
            }

            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }

        private static string StatusJson() => "{\"status\":\"ready\",\"active_model\":\"qwen38-27b--abc\",\"loading_model\":null,\"active_since\":1.0,\"inflight_requests\":0,\"endpoint\":\"http://127.0.0.1:1233\",\"ready\":true,\"backend_url\":\"http://127.0.0.1:1234\",\"alias\":\"Qwen3.8-27B\",\"backend_api_key\":null,\"pid\":777,"
            + "\"runtime\":{\"id\":\"b10786-hip-llama-cpp\",\"label\":\"b10786 HIP\",\"server_binary\":\"L:\\\\LAB\\\\ai-local\\\\b10786_hip_llama.cpp\\\\build\\\\bin\\\\llama-server.exe\",\"backend\":\"hip\",\"build\":\"b10786\",\"build_info\":\"b10786-abc123\"},"
            + "\"model\":{\"id\":\"qwen38-27b--abc\",\"name\":\"Qwen3.8-27B\",\"path\":\"I:\\\\models\\\\q.gguf\",\"ftype\":\"IQ2_M - 2.7 bpw\",\"params_b\":27.3,\"size_bytes\":12599187008,\"draft_model_path\":\"I:\\\\models\\\\draft.gguf\",\"mmproj_path\":null},"
            + "\"launch\":{\"ctx_size\":262144,\"gpu_layers\":999,\"threads\":12,\"batch_threads\":16,\"batch\":1024,\"ubatch\":1024,\"kv_type_k\":\"q4_0\",\"kv_type_v\":\"q4_0\",\"flash_attention\":\"on\",\"spec_type\":\"draft-dflash\",\"draft_n_max\":7,\"main_gpu\":0,\"parallel\":1,\"thinking\":true,\"profile\":\"Expert\",\"performance_target\":\"safe\"},"
            + "\"devices\":[{\"index\":0,\"name\":\"AMD Radeon AI PRO R9700\",\"backend\":\"hip\",\"vram_mb\":32768}],\"env\":{\"HIP_VISIBLE_DEVICES\":\"0\"},\"command_line\":[\"llama-server.exe\",\"-m\",\"I:\\\\models\\\\q.gguf\",\"--api-key\",\"hidden\"]}";
    }

    private static void AutoTunerClientFollowsContract()
    {
        var handler = new AutoTunerFakeHandler();
        var connection = new AutoTunerConnection { BaseUrl = "http://127.0.0.1:1233", Token = "test-token-0123456789", Source = "manual" };
        using var client = new AutoTunerClient(connection, handler, TimeSpan.FromSeconds(30));

        var health = client.GetHealthAsync().GetAwaiter().GetResult();
        Assert(health.Version == "5.3.9" && health.Service == "autotuner-control-api", "health must expose service and version");
        Assert(handler.Authorizations.All(a => a == "Bearer test-token-0123456789"), "every request must carry the bearer token");

        var models = client.GetModelsAsync().GetAwaiter().GetResult();
        Assert(models.Count == 2 && models[0].Id == "qwen38-27b--abc" && models[0].DefaultRuntimeId == "b10786-vulkan-llama-cpp" && models[0].Quant == "IQ2_M - 2.7 bpw" && !models[1].Runnable, "models must map the extended catalogue fields");

        var runtimes = client.GetRuntimesAsync().GetAwaiter().GetResult();
        Assert(runtimes.Count == 2 && runtimes[1].Backend == "hip" && runtimes[0].IsDefault, "runtimes must map id/backend/default");

        var status = client.SwitchAsync("qwen38-27b--abc", "b10786-hip-llama-cpp", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Assert(handler.SwitchCalls == 2, $"a busy tuner must be retried, got {handler.SwitchCalls} switch calls");
        Assert(status.IsReady && status.BackendUrl == "http://127.0.0.1:1234" && status.Alias == "Qwen3.8-27B", "switch must return the direct llama-server URL and alias");

        var runtime = status.ToRuntimeInfo("5.3.9");
        Assert(runtime.Engine == "llama.cpp" && runtime.Backend == "hip" && runtime.BackendSource == "autotuner" && runtime.EngineVersion == "b10786-abc123", $"status must convert to runtime identity, got {runtime.Summary()}");
        Assert(runtime.GpuLayers == 999 && runtime.Threads == 12 && runtime.BatchSize == 1024 && runtime.UBatchSize == 1024 && runtime.KvTypeK == "q4_0" && runtime.SpecType == "draft-dflash" && runtime.ContextSize == 262144, "launch parameters must be copied");
        Assert(runtime.Devices.Count == 1 && runtime.Devices[0].Contains("R9700", StringComparison.Ordinal) && runtime.Environment["HIP_VISIBLE_DEVICES"] == "0", "devices and environment must be copied");
        Assert(runtime.AutoTunerRuntimeId == "b10786-hip-llama-cpp" && runtime.AutoTunerModelId == "qwen38-27b--abc" && runtime.AutoTunerProfile == "Expert" && runtime.AutoTunerVersion == "5.3.9", "AutoTuner identity fields must be copied");
        Assert(runtime.CommandLine!.Contains("<redacted>", StringComparison.Ordinal) && !runtime.CommandLine.Contains("hidden", StringComparison.Ordinal), "command line from the tuner must be redacted too");
        Assert(runtime.ModelPath == @"I:\models\q.gguf" && runtime.DraftModel == @"I:\models\draft.gguf" && runtime.ModelFtype == "IQ2_M - 2.7 bpw", "model paths must be copied");

        client.StopAsync().GetAwaiter().GetResult();

        var legacyHandler = new AutoTunerFakeHandler { RuntimesAvailable = false };
        using var legacyClient = new AutoTunerClient(connection, legacyHandler, TimeSpan.FromSeconds(30));
        Assert(legacyClient.GetRuntimesAsync().GetAwaiter().GetResult().Count == 0, "a tuner without /api/v1/runtimes must yield an empty build list, not an error");

        var error = new AutoTunerApiException("busy", 409, "model_busy");
        Assert(error.IsRetryable && !new AutoTunerApiException("gone", 404, "model_not_found").IsRetryable, "retryable classification");
    }

    private static void AutoTunerDiscoveryReadsSidecarAndEnvironment()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-autotuner-sidecar-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var sidecar = Path.Combine(tempRoot, AutoTunerDiscovery.SidecarFileName);
        var previousUrl = Environment.GetEnvironmentVariable(AutoTunerDiscovery.UrlEnvironmentVariable);
        var previousKey = Environment.GetEnvironmentVariable(AutoTunerDiscovery.KeyEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable(AutoTunerDiscovery.UrlEnvironmentVariable, null);
            Environment.SetEnvironmentVariable(AutoTunerDiscovery.KeyEnvironmentVariable, null);

            File.WriteAllText(sidecar, "{\"schema\":1,\"enabled\":true,\"base_url\":\"http://127.0.0.1:1233\",\"port\":1233,\"token\":\"sidecar-token-abcdef\",\"version\":\"5.3.9\",\"pid\":1,\"started_at\":\"2026-09-03T18:00:00Z\"}");
            var discovered = AutoTunerDiscovery.Discover(sidecarPath: sidecar);
            Assert(discovered is not null && discovered.BaseUrl == "http://127.0.0.1:1233" && discovered.Token == "sidecar-token-abcdef" && discovered.Source == "sidecar" && discovered.Version == "5.3.9", "enabled sidecar must provide url, token and version");

            File.WriteAllText(sidecar, "{\"schema\":1,\"enabled\":false,\"port\":1233,\"version\":\"5.3.9\"}");
            Assert(AutoTunerDiscovery.Discover(sidecarPath: sidecar) is null, "a disabled sidecar must not yield a connection");

            var manual = AutoTunerDiscovery.Discover("http://127.0.0.1:2000/", "manual-token-xyz", sidecar);
            Assert(manual is not null && manual.BaseUrl == "http://127.0.0.1:2000" && manual.Token == "manual-token-xyz" && manual.Source == "manual", "explicit values must win and trailing slashes must be trimmed");

            Environment.SetEnvironmentVariable(AutoTunerDiscovery.UrlEnvironmentVariable, "http://127.0.0.1:3000");
            Environment.SetEnvironmentVariable(AutoTunerDiscovery.KeyEnvironmentVariable, "env-token-abcdefgh");
            var fromEnv = AutoTunerDiscovery.Discover(sidecarPath: sidecar);
            Assert(fromEnv is not null && fromEnv.BaseUrl == "http://127.0.0.1:3000" && fromEnv.Token == "env-token-abcdefgh" && fromEnv.Source == "environment", "environment variables must outrank the sidecar");
        }
        finally
        {
            Environment.SetEnvironmentVariable(AutoTunerDiscovery.UrlEnvironmentVariable, previousUrl);
            Environment.SetEnvironmentVariable(AutoTunerDiscovery.KeyEnvironmentVariable, previousKey);
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveStoresRuntimeIdentityAndComparisonSplitsByBackend()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-runtime-archive-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var vulkan = FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 7, 0, 0, 3);
            vulkan.Runtime = new ServerRuntimeInfo { Engine = "llama.cpp", EngineVersion = "b10786-abc123", Backend = "vulkan", BackendSource = "process_modules", BackendDetail = "vulkan-1.dll", RuntimeLabel = "b10786 Vulkan", GpuLayers = 999, Threads = 12, BatchSize = 1024, UBatchSize = 512, ContextSize = 65536, Devices = ["AMD Radeon AI PRO R9700"], KvTypeK = "q8_0", SpecType = "draft-dflash", ServerBinary = @"L:\LAB\b10786_vulkan\llama-server.exe", CommandLine = "llama-server.exe -m x.gguf", AutoTunerRuntimeId = "b10786-vulkan-llama-cpp", AutoTunerVersion = "5.3.9", Environment = new Dictionary<string, string> { ["GGML_VK_VISIBLE_DEVICES"] = "1" } };
            var hip = FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 60, 6, 0, 0, 4);
            hip.Runtime = new ServerRuntimeInfo { Engine = "llama.cpp", EngineVersion = "b10786-abc123", Backend = "HIP", BackendSource = "autotuner", RuntimeLabel = "b10786 HIP" };
            var legacy = FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 50, 5, 0, 0, 5, parserVersion: ResponseParser.ParserV2Version);

            var vulkanPath = store.Save(vulkan);
            store.Save(hip);
            store.Save(legacy);

            var json = File.ReadAllText(vulkanPath);
            Assert(json.Contains("\"backend\": \"vulkan\"", StringComparison.Ordinal) && json.Contains("\"engine\": \"llama.cpp\"", StringComparison.Ordinal) && json.Contains("\"llamaBuild\": \"b10786-abc123\"", StringComparison.Ordinal), "scorecard must persist canonical backend, engine and build");
            Assert(json.Contains("\"backendSource\": \"process_modules\"", StringComparison.Ordinal) && json.Contains("\"ubatchSize\": 512", StringComparison.Ordinal) && json.Contains("\"autoTunerRuntimeId\": \"b10786-vulkan-llama-cpp\"", StringComparison.Ordinal) && json.Contains("GGML_VK_VISIBLE_DEVICES", StringComparison.Ordinal), "scorecard must persist the additive runtime fields");
            Assert(!json.Contains("\"schemaVersion\": 6", StringComparison.Ordinal), "runtime metadata is additive and must not bump the archive schema");

            var groups = store.LoadGroups();
            Assert(groups.Count == 1 && groups[0].Records.Count == 3, "all three runs share one model+quant group");
            var loaded = groups[0].Records.Single(r => r.ServerMetadata.Backend == "vulkan");
            Assert(loaded.ServerMetadata.RuntimeKey == "llama.cpp/vulkan/b10786-abc123" && loaded.ServerMetadata.GpuLayers == 999 && loaded.ServerMetadata.Devices!.Count == 1, "loaded scorecard must expose runtime key and launch parameters");
            var hipLoaded = groups[0].Records.Single(r => string.Equals(r.ServerMetadata.Backend, "hip", StringComparison.OrdinalIgnoreCase));
            Assert(hipLoaded.ServerMetadata.NormalizedBackend == "hip", "backend labels are normalized on read");

            var pooled = ComparisonReport.Build(groups, "supercalc-v3");
            Assert(pooled.Series.Count == 1 && pooled.Series[0].Backend == "mixed" && pooled.Series[0].BackendBreakdown["vulkan"] == 1 && pooled.Series[0].BackendBreakdown["hip"] == 1 && pooled.Series[0].BackendBreakdown[ServerRuntimeInfo.UnknownValue] == 1, "pooled grouping reports a mixed backend breakdown");
            Assert(pooled.AvailableScopes.Any(s => s.Kind == ComparisonScopeKind.ParserVersion && s.Value == ResponseParser.ParserV2Version && s.RunCount == 1), "available scopes must list parser-v2 with its run count");
            Assert(pooled.AvailableScopes.Any(s => s.Kind == ComparisonScopeKind.ToolVersion && s.Value == "test" && s.RunCount == 3), "available scopes must list the tool version");

            var byBackend = ComparisonReport.Build(groups, "supercalc-v3", grouping: ComparisonGrouping.ModelQuantBackend);
            Assert(byBackend.Series.Count == 3, $"backend grouping must split into vulkan/hip/unknown, got {byBackend.Series.Count}");
            var vulkanSeries = byBackend.Series.Single(s => s.Backend == "vulkan");
            Assert(Math.Abs(vulkanSeries.ScorePercent - 70) < 0.001 && vulkanSeries.Label.Contains("Vulkan", StringComparison.Ordinal) && vulkanSeries.Build == "b10786", "backend series must carry only its own runs and label the backend");
            Assert(byBackend.Series.Single(s => s.Backend == "hip").ScorePercent == 60, "hip series must carry the hip score");

            var byRuntime = ComparisonReport.Build(groups, "supercalc-v3", grouping: ComparisonGrouping.ModelQuantRuntime);
            Assert(byRuntime.Series.Count == 3 && byRuntime.Series.Any(s => s.RuntimeKey == "llama.cpp/vulkan/b10786-abc123"), "runtime grouping splits by engine/backend/build");

            var parserScope = ComparisonReport.Build(groups, "supercalc-v3", scope: new ComparisonScope(ComparisonScopeKind.ParserVersion, ResponseParser.ParserV2Version));
            Assert(parserScope.Series.Single().RunCount == 1 && Math.Abs(parserScope.Series.Single().ScorePercent - 50) < 0.001, "parser scope must restrict to the requested parser version");
            var currentScope = ComparisonReport.Build(groups, "supercalc-v3", scope: new ComparisonScope(ComparisonScopeKind.Current, ResponseParser.CurrentParserVersion));
            Assert(currentScope.Series.Single().RunCount == 2, "current scope must contain only current-parser runs");
            var toolScope = ComparisonReport.Build(groups, "supercalc-v3", scope: ComparisonScope.Parse("tool:test"));
            Assert(toolScope.Series.Single().RunCount == 3, "tool scope must match the tool version");
            Assert(ComparisonScope.Parse("parser-v2")!.Kind == ComparisonScopeKind.ParserVersion && ComparisonScope.Parse("v0.7.5")!.Value == "0.7.5" && ComparisonScope.Parse("all") is null && ComparisonScope.Parse("current")!.Kind == ComparisonScopeKind.Current, "scope parsing accepts the documented forms");

            var html = new ComparisonHtmlWriter { Groups = groups }.BuildHtml(pooled);
            Assert(html.Contains("\"backend\"", StringComparison.Ordinal) && html.Contains("Backend-Vergleich", StringComparison.Ordinal), "HTML must embed backend projections and the backend comparison tile");
            var payloadStart = html.IndexOf("<script id=\"data\" type=\"application/json\">", StringComparison.Ordinal) + "<script id=\"data\" type=\"application/json\">".Length;
            var payloadEnd = html.IndexOf("</script>", payloadStart, StringComparison.Ordinal);
            using var doc = JsonDocument.Parse(html[payloadStart..payloadEnd].Trim());
            var rows = ExpandTabularPayload(doc.RootElement, "seriesKeys", "seriesRows");
            Assert(rows.Any(r => r["scope"].GetString() == "all" && r["grouping"].GetString() == "backend" && r["backend"].GetString() == "vulkan"), "payload must contain the per-backend projection");
            Assert(rows.Any(r => r["scope"].GetString() == "parser:parser-v2" && r["grouping"].GetString() == "model"), "payload must contain the parser-version scope projection");
            Assert(html.Length < 400_000, $"a three-run archive page must stay compact, got {html.Length} bytes");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void CampaignRunnerRecordsFailuresAndHonorsStop()
    {
        var paths = BenchmarkPathResolver.Resolve();
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-campaign-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Port 9 (discard) is closed on every Windows/Linux dev box: connection refused is immediate.
            var baseOptions = new BenchmarkOptions
            {
                ServerUrl = "http://127.0.0.1:9",
                SourcePath = paths.SourcePath,
                GroundTruthPath = paths.GroundTruthPath,
                AnalysisPromptPath = paths.AnalysisPromptPath,
                SelfValidatePromptPath = paths.SelfValidatePromptPath,
                TruthAuditPromptPath = paths.TruthAuditPromptPath,
                SchemaPath = paths.FindingsSchemaPath,
                TruthAuditSchemaPath = paths.TruthAuditSchemaPath,
                OutputDirectory = Path.Combine(tempRoot, "runs"),
                Timeout = TimeSpan.FromSeconds(20),
                ProbeRuntime = false,
                ArchiveDirectory = null
            };
            var plan = new CampaignPlan
            {
                CampaignId = "test-campaign",
                Items =
                [
                    new CampaignItem { ModelId = "model-a", ModelName = "Model A", Repeats = 2 },
                    new CampaignItem { ModelId = "model-b", ModelName = "Model B", RuntimeId = "vulkan", RuntimeLabel = "b1 Vulkan" }
                ],
                BaseOptions = baseOptions,
                AutoTuner = null,
                StopOnError = false,
                StopServerAtEnd = false
            };

            var runner = new CampaignRunner();
            var changes = new List<string>();
            var summary = runner.RunAsync(
                plan,
                progress: message => { if (message.Contains("Run failed", StringComparison.Ordinal)) runner.RequestStop(CampaignStopMode.AfterCurrentRun); },
                onItemChanged: state => changes.Add($"{state.Item.Label}:{state.State}"),
                onRunStarting: (state, repeat) => changes.Add($"start:{state.Item.Label}:{repeat}")).GetAwaiter().GetResult();

            Assert(summary.CampaignId == "test-campaign" && summary.Items.Count == 2, "summary must list every planned item");
            Assert(summary.Items[0].State == "Failed" && summary.Items[0].RepeatsCompleted == 0, $"unreachable server must mark the item failed, got {summary.Items[0].State}");
            Assert(summary.Items[1].State == "Skipped" && summary.Items[1].Message.Contains("stop requested", StringComparison.Ordinal), "stop after current run must skip the remaining items");
            Assert(summary.StopMode == "AfterCurrentRun", "summary must record the stop mode");
            Assert(changes.Contains("start:Model A:1") && !changes.Contains("start:Model A:2"), "the stop request must end the item after the failed first repeat");
            Assert(summary.Items[1].Label == "Model B @ b1 Vulkan", "item labels combine model and build");
            var summaryPath = Path.Combine(BenchmarkPathResolver.ResolveDataRoot(), "Campaigns", "test-campaign.json");
            Assert(File.Exists(summaryPath), "campaign summary JSON must be written to the data pool");
            File.Delete(summaryPath);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }
}
