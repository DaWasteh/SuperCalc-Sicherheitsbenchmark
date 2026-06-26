using System.Net;
using System.Text;
using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

internal static partial class TestRunner
{
    private static int _failures;

    private static int Main()
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        Run("ground truth validates", GroundTruthValidates);
        Run("parser handles valid JSON", ParserHandlesValidJson);
        Run("parser handles markdown JSON fence", ParserHandlesMarkdownJsonFence);
        Run("parser treats schema echo as no findings", ParserTreatsSchemaEchoAsNoFindings);
        Run("parser accepts schema metadata with findings", ParserAcceptsSchemaMetadataWithFindings);
        Run("parser salvages truncated findings JSON", ParserSalvagesTruncatedFindingsJson);
        Run("parser handles lenient finding shapes", ParserHandlesLenientFindingShapes);
        Run("llama client leaves thinking enabled by default", LlamaClientLeavesThinkingEnabledByDefault);
        Run("llama client disables Qwen thinking when requested", LlamaClientDisablesQwenThinkingWhenRequested);
        Run("loop detector flags repeated reasoning", LoopDetectorFlagsRepeatedReasoning);
        Run("loop detector ignores normal output", LoopDetectorIgnoresNormalOutput);
        Run("loop detector ignores perfect fixture repetition", LoopDetectorIgnoresPerfectFixtureRepetition);
        Run("llama streaming loop guard aborts repeated reasoning", LlamaStreamingLoopGuardAbortsRepeatedReasoning);
        Run("perfect synthetic fixture scores 100", PerfectSyntheticFixtureScoresHigh);
        Run("duplicate finding is penalized", DuplicateFindingIsPenalized);
        Run("reasoning disclosure compares thinking and output true positives", ReasoningDisclosureComparesThinkingAndOutputTruePositives);
        Run("fixture scoring separates inline think block", FixtureScoringSeparatesInlineThinkBlock);
        Run("prompts do not contain hidden answer files", PromptsDoNotLeakHiddenGroundTruth);
        Run("model identity detects quant and family", ModelIdentityDetectsQuantAndFamily);
        Run("model identity honors manual quant override", ModelIdentityHonorsQuantOverride);
        Run("archive store updates editable identity fields", ArchiveStoreUpdatesEditableIdentityFields);
        Run("archive manual quant edit rebuilds group key", ArchiveManualQuantEditRebuildsGroupKey);
        Run("archive duplicate run names do not clobber", ArchiveDuplicateRunNamesDoNotClobber);
        Run("archive manual model rename merges groups", ArchiveManualModelRenameMergesGroups);
        Run("archive round-trips and groups by model and quant", ArchiveRoundTripsAndGroups);
        Run("comparison aggregates and filters by family", ComparisonAggregatesAndFiltersByFamily);
        Run("comparison html embeds parseable payload", ComparisonHtmlEmbedsParseablePayload);

        if (_failures == 0)
        {
            Console.WriteLine("All tests passed.");
            return 0;
        }

        Console.Error.WriteLine($"{_failures} test(s) failed.");
        return 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"PASS {name}");
        }
        catch (Exception ex)
        {
            _failures++;
            Console.Error.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    private static void GroundTruthValidates()
    {
        var store = new GroundTruthStore();
        var result = store.Validate(GroundTruthPath, SourcePath);
        Assert(result.ActualSourceSha256 == result.ExpectedSourceSha256, "source hash must match ground truth");
        Assert(result.VulnerabilityCount == 20, "supercalc-v3 should contain 20 vulnerabilities");
        Assert(result.Issues.All(i => !string.Equals(i.Severity, "Error", StringComparison.OrdinalIgnoreCase)),
            string.Join(Environment.NewLine, result.Issues.Select(i => $"[{i.Severity}] {i.Message}")));
    }

    private static void ParserHandlesValidJson()
    {
        var parser = new ResponseParser();
        var result = parser.Parse("""
        {
          "summary": "one finding",
          "findings": [
            {
              "title": "Format string",
              "vulnerability_type": "format string",
              "cwe": "CWE-134",
              "severity": "Critical",
              "confidence": 0.92,
              "file": "enhanced_calc.cpp",
              "line_start": 237,
              "line_end": 238,
              "function_or_symbol": "string_utils::log_debug_message",
              "evidence": "printf(active_format.c_str(), user_input)",
              "impact": "attacker controlled format string",
              "fix": "Use printf with fixed format"
            }
          ]
        }
        """);

        Assert(result.ParsedJson, "JSON should parse");
        Assert(result.Findings.Count == 1, "one finding expected");
        Assert(result.Findings[0].Index == 1, "finding should be indexed");
        Assert(result.Findings[0].Cwe == "CWE-134", "CWE should parse");
    }

    private static void ParserHandlesMarkdownJsonFence()
    {
        var parser = new ResponseParser();
        var result = parser.Parse("""
        Here is JSON:
        ```json
        { "findings": [ { "title": "Hardcoded credential", "vulnerability_type": "hardcoded secret", "cwe": ["CWE-798"], "severity": "High", "confidence": 0.8, "file": "enhanced_calc.cpp", "line_start": 73, "line_end": 73, "evidence": "ADMIN_SECRET" } ] }
        ```
        """);

        Assert(result.ParsedJson, "fenced JSON should parse");
        Assert(result.UsedMarkdownJsonBlock, "fenced JSON should be marked");
        Assert(result.Findings.Count == 1, "one finding expected");
        Assert(result.Findings[0].Cwe == "CWE-798", "CWE array should normalize");
    }

    private static void ParserTreatsSchemaEchoAsNoFindings()
    {
        var parser = new ResponseParser();
        var result = parser.Parse("""
        ```json
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "SuperCalc LLM Findings Response",
          "type": "object",
          "properties": {
            "findings": { "type": "array" }
          }
        }
        ```
        """);

        Assert(result.ParsedJson, "schema echo should be treated as JSON");
        Assert(result.UsedMarkdownJsonBlock, "schema echo came from a fenced JSON block");
        Assert(result.Findings.Count == 0, "schema echo should not become a false finding");
        Assert(result.Warning?.Contains("echo", StringComparison.OrdinalIgnoreCase) == true, "schema echo should carry a diagnostic warning");
    }

    private static void ParserAcceptsSchemaMetadataWithFindings()
    {
        var parser = new ResponseParser();
        var result = parser.Parse("""
        {
          "$schema": "https://json-schema.org/draft/2020-12/schema",
          "title": "SuperCalc LLM Findings Response",
          "type": "object",
          "findings": [
            {
              "title": "Hardcoded Administrative Secret",
              "vulnerability_type": "Hardcoded Credentials",
              "cwe": "CWE-798",
              "severity": "High",
              "confidence": 0.95,
              "file": "enhanced_calc.cpp",
              "line_start": 73,
              "line_end": 73,
              "function_or_symbol": "config::security::ADMIN_SECRET",
              "evidence": "constexpr char ADMIN_SECRET[] = \"SC_ENT_2025_AUTH\""
            }
          ]
        }
        """);

        Assert(result.ParsedJson, "schema metadata plus real findings should parse as JSON");
        Assert(result.Findings.Count == 1, $"expected one finding, got {result.Findings.Count}");
        Assert(result.Findings[0].Cwe == "CWE-798", $"CWE should parse, got {result.Findings[0].Cwe}");
        Assert(result.Warning?.Contains("schema metadata", StringComparison.OrdinalIgnoreCase) == true, "schema metadata should be diagnosed without suppressing findings");
    }

    private static void ParserSalvagesTruncatedFindingsJson()
    {
        var parser = new ResponseParser();
        var result = parser.Parse("""
        {
          "summary": "truncated",
          "findings": [
            {
              "title": "Format string",
              "vulnerability_type": "format string",
              "cwe": "CWE-134",
              "severity": "Critical",
              "confidence": 0.92,
              "file": "enhanced_calc.cpp",
              "line_start": 237,
              "line_end": 238,
              "function_or_symbol": "string_utils::log_debug_message",
              "evidence": "printf(active_format.c_str(), user_input)",
              "impact": "attacker controlled format string",
              "fix": "Use printf with fixed format"
            },
            {
              "title": "unfinished"
        Then the model started writing prose instead of closing JSON.
        """);

        Assert(result.ParsedJson, "truncated JSON should still be parsed via salvage mode");
        Assert(result.Findings.Count == 1, $"expected one salvaged complete finding, got {result.Findings.Count}");
        Assert(result.Findings[0].Title == "Format string", "complete finding title should parse");
        Assert(result.Warning?.Contains("salvaged", StringComparison.OrdinalIgnoreCase) == true, "salvage warning should be present");
    }

    private static void ParserHandlesLenientFindingShapes()
    {
        var parser = new ResponseParser();

        var singleton = parser.Parse("""
        {
          "findings": {
            "title": "Format string",
            "type": "format string",
            "cwe_id": "CWE-134",
            "risk_rating": "Critical",
            "confidence": "92%",
            "path": "enhanced_calc.cpp",
            "lines": "237-238",
            "functionName": "string_utils::log_debug_message",
            "codeSnippet": "printf(active_format.c_str(), user_input)"
          }
        }
        """);

        Assert(singleton.ParsedJson, "object-valued findings should parse as JSON");
        Assert(singleton.Findings.Count == 1, $"expected one singleton finding, got {singleton.Findings.Count}");
        Assert(singleton.Findings[0].LineStart == 237, $"range start should parse, got {singleton.Findings[0].LineStart}");
        Assert(singleton.Findings[0].LineEnd == 238, $"range end should parse, got {singleton.Findings[0].LineEnd}");
        Assert(Math.Abs(singleton.Findings[0].Confidence - 0.92) < 0.001, $"percent confidence should parse, got {singleton.Findings[0].Confidence}");
        Assert(singleton.Warning?.Contains("single", StringComparison.OrdinalIgnoreCase) == true, "singleton parsing should carry a warning");

        var mapped = parser.Parse("""
        {
          "issues": {
            "one": {
              "name": "Command injection",
              "vulnerability": "command injection",
              "cwe": { "id": "CWE-78" },
              "severity": "High",
              "file": "enhanced_calc.cpp",
              "line": "line 563",
              "evidence": "system(command.c_str())"
            }
          }
        }
        """);

        Assert(mapped.ParsedJson, "issues map should parse as JSON");
        Assert(mapped.Findings.Count == 1, $"expected one mapped finding, got {mapped.Findings.Count}");
        Assert(mapped.Findings[0].Cwe == "CWE-78", $"CWE object id should parse, got {mapped.Findings[0].Cwe}");
        Assert(mapped.Findings[0].LineStart == 563, $"line text should parse, got {mapped.Findings[0].LineStart}");
    }

    private static void LlamaClientLeavesThinkingEnabledByDefault()
    {
        var handler = new CapturingHandler();
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "Qwen3.5-4B",
            "system",
            "user",
            new BenchmarkOptions { Model = "Qwen3.5-4B", MaxTokens = 16 },
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

        Assert(!handler.RequestBody.Contains("\"chat_template_kwargs\"", StringComparison.Ordinal), "request should leave thinking enabled by default");
        Assert(!result.UsedThinkingControl, "result should record no thinking-control usage by default");
        Assert(result.AssistantContent == "{\"ok\":true}", "assistant content should be extracted");
        Assert(result.ReasoningContent == "not used", "reasoning_content should be captured for diagnostics");
    }

    private static void LlamaClientDisablesQwenThinkingWhenRequested()
    {
        var handler = new CapturingHandler();
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "Qwen3.5-4B",
            "system",
            "user",
            new BenchmarkOptions { Model = "Qwen3.5-4B", MaxTokens = 16, DisableThinking = true },
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

        Assert(handler.RequestBody.Contains("\"chat_template_kwargs\"", StringComparison.Ordinal), "request should include chat_template_kwargs when disable-thinking is requested");
        Assert(handler.RequestBody.Contains("\"enable_thinking\": false", StringComparison.Ordinal), "request should disable Qwen thinking when requested");
        Assert(result.UsedThinkingControl, "result should record thinking-control usage");
        Assert(result.AssistantContent == "{\"ok\":true}", "assistant content should be extracted");
        Assert(result.ReasoningContent == "not used", "reasoning_content should be captured for diagnostics");
    }

    private static void LoopDetectorFlagsRepeatedReasoning()
    {
        var repeated = string.Join("\n", Enumerable.Repeat(
            "I will inspect validate_password, then I will inspect validate_password again, then I will inspect validate_password again because the same branch may be important.",
            8));

        var diagnostics = OutputLoopDetector.Analyze(repeated);
        Assert(diagnostics.HasSuspectedLoop, "repeated reasoning should be flagged as a likely loop");
        Assert(diagnostics.Repetitions.Count > 0, "loop diagnostics should include repeated segments");
    }

    private static void LoopDetectorIgnoresNormalOutput()
    {
        var diagnostics = OutputLoopDetector.Analyze("""
        {
          "summary": "normal compact answer",
          "findings": [
            { "title": "Format string", "severity": "Critical" },
            { "title": "Hardcoded secret", "severity": "High" }
          ]
        }
        """);

        Assert(!diagnostics.HasSuspectedLoop, "short normal output should not be flagged as a loop");
    }

    private static void LoopDetectorIgnoresPerfectFixtureRepetition()
    {
        var fixture = File.ReadAllText(Path.Combine("tools", "response-fixtures", "perfect.json"), Encoding.UTF8);
        var diagnostics = OutputLoopDetector.Analyze(fixture);
        Assert(!diagnostics.HasSuspectedLoop, "normal structured fixture repetition should not be flagged as a loop");
    }

    private static void LlamaStreamingLoopGuardAbortsRepeatedReasoning()
    {
        var handler = new LoopingStreamHandler();
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "loop-model",
            "system",
            "user",
            new BenchmarkOptions { Model = "loop-model" },
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

        Assert(handler.RequestBody.Contains("\"stream\": true", StringComparison.Ordinal), "loop guard should force streaming so it can interrupt a model loop");
        Assert(result.LoopDetected, "streaming loop guard should mark the result as loop-detected");
        Assert(result.FinishReason == "loop_detected", $"finish reason should be loop_detected, got {result.FinishReason}");
        Assert(result.LoopDiagnosticsSummary.Contains("reasoning content", StringComparison.Ordinal), "diagnostics should identify the looping channel");
        Assert(result.ReasoningContent.Length < handler.FullReasoningLength, "guard should stop reading before the full synthetic loop is consumed");
    }

    private static void PerfectSyntheticFixtureScoresHigh()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var findings = groundTruth.Vulnerabilities.Select(v => SyntheticFinding(v)).ToList();
        var json = JsonSerializer.Serialize(new { findings }, JsonOptions);
        var parsed = new ResponseParser().Parse(json);
        var score = new ScoringEngine().Score("perfect", parsed.Findings, groundTruth, source);

        Assert(score.FullTruePositives == 20, $"expected 20 full TPs, got {score.FullTruePositives}");
        Assert(score.FalsePositives == 0, "perfect fixture should have no false positives");
        Assert(score.Duplicates == 0, "perfect fixture should have no duplicates");
        Assert(score.ScorePercent >= 99.0, $"perfect fixture should score near 100, got {score.ScorePercent}");
    }

    private static void DuplicateFindingIsPenalized()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var finding = SyntheticFinding(groundTruth.Vulnerabilities[0]);
        var json = JsonSerializer.Serialize(new { findings = new[] { finding, finding } }, JsonOptions);
        var parsed = new ResponseParser().Parse(json);
        var score = new ScoringEngine().Score("duplicate", parsed.Findings, groundTruth, source);

        Assert(score.FullTruePositives == 1, $"expected one assigned TP, got {score.FullTruePositives}");
        Assert(score.Duplicates == 1, $"expected one duplicate, got {score.Duplicates}");
        Assert(score.RawPoints == 4.0, $"expected 5 - 1 = 4 points, got {score.RawPoints}");
    }

    private static void ReasoningDisclosureComparesThinkingAndOutputTruePositives()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var parser = new ResponseParser();
        var scoring = new ScoringEngine();

        var reasoningJson = JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                SyntheticFinding(groundTruth.Vulnerabilities[0]),
                SyntheticFinding(groundTruth.Vulnerabilities[1])
            }
        }, JsonOptions);

        var outputJson = JsonSerializer.Serialize(new
        {
            findings = new[]
            {
                SyntheticFinding(groundTruth.Vulnerabilities[0]),
                SyntheticFinding(groundTruth.Vulnerabilities[2])
            }
        }, JsonOptions);

        var reasoningParse = parser.Parse(reasoningJson);
        var outputParse = parser.Parse(outputJson);
        var reasoningScore = scoring.Score("thinking", reasoningParse.Findings, groundTruth, source);
        var outputScore = scoring.Score("output", outputParse.Findings, groundTruth, source);
        var diagnostics = ReasoningDisclosureAnalyzer.Analyze(reasoningJson, reasoningParse, reasoningScore, outputScore);

        Assert(diagnostics.HasVisibleReasoning, "reasoning should be marked visible");
        Assert(diagnostics.ReasoningTruePositiveCount == 2, $"expected two thinking TPs, got {diagnostics.ReasoningTruePositiveCount}");
        Assert(diagnostics.OutputTruePositiveCount == 2, $"expected two output TPs, got {diagnostics.OutputTruePositiveCount}");
        Assert(diagnostics.ReasoningOnlyTruePositiveCount == 1, $"expected one thinking-only TP, got {diagnostics.ReasoningOnlyTruePositiveCount}");
        Assert(diagnostics.OutputOnlyTruePositiveCount == 1, $"expected one output-only TP, got {diagnostics.OutputOnlyTruePositiveCount}");
        Assert(Math.Abs((diagnostics.ReasoningToOutputCoverage ?? -1) - 0.5) < 0.0001, $"expected 50% coverage, got {diagnostics.ReasoningToOutputCoverage}");
        Assert(diagnostics.ReasoningOnlyTruePositiveIds.Contains(groundTruth.Vulnerabilities[1].Id), "second vulnerability should be thinking-only");
        Assert(diagnostics.OutputOnlyTruePositiveIds.Contains(groundTruth.Vulnerabilities[2].Id), "third vulnerability should be output-only");
    }

    private static void FixtureScoringSeparatesInlineThinkBlock()
    {
        var (groundTruth, _) = LoadGroundTruthAndSource();
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-inline-think-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var responsePath = Path.Combine(tempRoot, "response.txt");
            var outputDirectory = Path.Combine(tempRoot, "out");

            var reasoningJson = JsonSerializer.Serialize(new
            {
                findings = new[] { SyntheticFinding(groundTruth.Vulnerabilities[1]) }
            }, JsonOptions);
            var outputJson = JsonSerializer.Serialize(new
            {
                findings = new[] { SyntheticFinding(groundTruth.Vulnerabilities[0]) }
            }, JsonOptions);

            File.WriteAllText(responsePath, $"<think>{reasoningJson}</think>{Environment.NewLine}{outputJson}", Encoding.UTF8);

            var result = new BenchmarkRunner().ScoreFixture(
                new BenchmarkOptions
                {
                    Model = "inline-think-fixture",
                    OutputDirectory = outputDirectory
                },
                responsePath);

            var disclosure = result.Run1.ReasoningDisclosure;
            Assert(!result.Run1.Response.Contains("<think", StringComparison.OrdinalIgnoreCase), "final output should not include inline think blocks");
            Assert(result.Run1.ReasoningContent.Contains(groundTruth.Vulnerabilities[1].Title, StringComparison.OrdinalIgnoreCase), "inline thinking should be moved to reasoning content");
            Assert(result.Run1.Score.FullTruePositives == 1, $"only final output should affect score; got {result.Run1.Score.FullTruePositives} full TPs");
            Assert(disclosure.HasVisibleReasoning, "inline thinking should make disclosure diagnostics available");
            Assert(disclosure.ReasoningOnlyTruePositiveIds.Contains(groundTruth.Vulnerabilities[1].Id), "thinking-only TP should come from inline think block");
            Assert(disclosure.OutputOnlyTruePositiveIds.Contains(groundTruth.Vulnerabilities[0].Id), "output-only TP should come from final output");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void PromptsDoNotLeakHiddenGroundTruth()
    {
        var source = SourceDocument.Load(SourcePath);
        var builder = new PromptBuilder();
        var run1 = builder.BuildAnalysisPrompt(source, AnalysisPromptPath, SchemaPath);
        var run2 = builder.BuildSelfValidationPrompt(source, SelfPromptPath, SchemaPath, "{\"findings\":[]}");

        foreach (var prompt in new[] { run1, run2 })
        {
            Assert(!prompt.Contains("enhanced_exploits.md", StringComparison.OrdinalIgnoreCase), "prompt must not name exploit MD");
            Assert(!prompt.Contains("ground_truth.json", StringComparison.OrdinalIgnoreCase), "prompt must not name ground truth JSON");
            Assert(!prompt.Contains("SC-V3-001", StringComparison.OrdinalIgnoreCase), "prompt must not contain ground-truth IDs");
        }
    }

    private static (GroundTruthDocument GroundTruth, SourceDocument Source) LoadGroundTruthAndSource()
    {
        return (new GroundTruthStore().Load(GroundTruthPath), SourceDocument.Load(SourcePath));
    }

    private static object SyntheticFinding(VulnerabilityDefinition vulnerability)
    {
        var location = vulnerability.Locations[0];
        return new
        {
            title = vulnerability.Title,
            vulnerability_type = vulnerability.Aliases.FirstOrDefault() ?? vulnerability.Title,
            cwe = vulnerability.Cwe.FirstOrDefault() ?? string.Empty,
            severity = vulnerability.Severity,
            confidence = 0.98,
            file = location.File,
            line_start = location.LineStart,
            line_end = location.LineEnd,
            function_or_symbol = location.Symbol,
            evidence = string.Join("; ", vulnerability.RequiredEvidence),
            impact = string.Join("; ", vulnerability.Aliases.Take(2)),
            trigger = vulnerability.Trigger ?? string.Empty,
            fix = "Validate input and use safe APIs."
        };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            const string responseJson = """
            {
              "choices": [
                {
                  "finish_reason": "stop",
                  "index": 0,
                  "message": {
                    "role": "assistant",
                    "content": "{\"ok\":true}",
                    "reasoning_content": "not used"
                  }
                }
              ]
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    private sealed class LoopingStreamHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;
        public int FullReasoningLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            const string repeatedLine = "I will inspect validate_password, then I will inspect validate_password again, then I will inspect validate_password again because the same branch may be important.\n";
            var sse = new StringBuilder();
            for (var i = 0; i < 120; i++)
            {
                FullReasoningLength += repeatedLine.Length;
                var payload = JsonSerializer.Serialize(new Dictionary<string, object?>
                {
                    ["choices"] = new object[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["finish_reason"] = null,
                            ["delta"] = new Dictionary<string, object?>
                            {
                                ["reasoning_content"] = repeatedLine
                            }
                        }
                    }
                });
                sse.Append("data: ").Append(payload).Append("\n\n");
            }

            sse.Append("data: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
            };
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private const string SourcePath = "enhanced_calc.cpp";
    private static readonly string GroundTruthPath = Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json");
    private static readonly string AnalysisPromptPath = Path.Combine("benchmarks", "supercalc-v3", "prompts", "analysis_v1.md");
    private static readonly string SelfPromptPath = Path.Combine("benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md");
    private static readonly string SchemaPath = Path.Combine("benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}
