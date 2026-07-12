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
        Run("WPF app exits when the main window closes", WpfAppExitsWithMainWindow);
        Run("ground truth v2 aliases and anchors load", GroundTruthV2AliasesAndAnchorsLoad);
        Run("scoring ledger records evidence fidelity", ScoringLedgerRecordsEvidenceFidelity);
        Run("parser handles valid JSON", ParserHandlesValidJson);
        Run("parser handles markdown JSON fence", ParserHandlesMarkdownJsonFence);
        Run("parser treats schema echo as no findings", ParserTreatsSchemaEchoAsNoFindings);
        Run("parser accepts schema metadata with findings", ParserAcceptsSchemaMetadataWithFindings);
        Run("parser salvages truncated findings JSON", ParserSalvagesTruncatedFindingsJson);
        Run("parser handles lenient finding shapes", ParserHandlesLenientFindingShapes);
        Run("official timeout covers slow reasoning budget", OfficialTimeoutCoversSlowReasoningBudget);
        Run("llama client leaves thinking enabled by default", LlamaClientLeavesThinkingEnabledByDefault);
        Run("llama client disables Qwen thinking when requested", LlamaClientDisablesQwenThinkingWhenRequested);
        Run("llama client reports exact tokenizer and usage counts", LlamaClientReportsExactTokenCounts);
        Run("loop detector flags repeated reasoning", LoopDetectorFlagsRepeatedReasoning);
        Run("loop detector flags runaway security bullet cycle", LoopDetectorFlagsRunawaySecurityBulletCycle);
        Run("loop detector ignores bounded finding list", LoopDetectorIgnoresBoundedFindingList);
        Run("loop detector ignores finding metadata analysis", LoopDetectorIgnoresFindingMetadataAnalysis);
        Run("loop detector ignores bounded reasoning checklist churn", LoopDetectorIgnoresBoundedReasoningChecklistChurn);
        Run("loop detector ignores normal output", LoopDetectorIgnoresNormalOutput);
        Run("loop detector ignores perfect fixture repetition", LoopDetectorIgnoresPerfectFixtureRepetition);
        Run("llama streaming loop guard ignores repeated reasoning", LlamaStreamingLoopGuardIgnoresRepeatedReasoning);
        Run("llama streaming loop guard aborts repeated content", LlamaStreamingLoopGuardAbortsRepeatedContent);
        Run("llama manual run abort returns partial stream", LlamaManualRunAbortReturnsPartialStream);
        Run("perfect synthetic fixture scores 100", PerfectSyntheticFixtureScoresHigh);
        Run("official v2 keeps perfect fixture at 100", OfficialV2KeepsPerfectFixtureAt100);
        Run("official v2 gates unsupported alias-only finding", OfficialV2GatesUnsupportedAliasOnlyFinding);
        Run("duplicate finding is penalized", DuplicateFindingIsPenalized);
        Run("self-validation tracks TP/FP transitions and FP taxonomy", SelfValidationTracksTransitionsAndFpTaxonomy);
        Run("adjudication can accept a false positive transparently", AdjudicationCanAcceptFalsePositiveTransparently);
        Run("adjudication preserves one TP per vulnerability", AdjudicationPreservesOneTpPerVulnerability);
        Run("reasoning disclosure compares thinking and output true positives", ReasoningDisclosureComparesThinkingAndOutputTruePositives);
        Run("fixture scoring separates inline think block", FixtureScoringSeparatesInlineThinkBlock);
        Run("prompts do not contain hidden answer files", PromptsDoNotLeakHiddenGroundTruth);
        Run("truth audit scorer detects honest and overclaiming self-assessments", TruthAuditScorerDetectsHonestyAndOverclaims);
        Run("truth audit rejects omissions, empty proof, and duplicate FP admissions", TruthAuditRejectsGamingShortcuts);
        Run("model identity detects quant and family", ModelIdentityDetectsQuantAndFamily);
        Run("model identity honors manual quant override", ModelIdentityHonorsQuantOverride);
        Run("model identity normalizes server ftype", ModelIdentityNormalizesServerFtype);
        Run("model identity server ftype beats name detection", ModelIdentityServerFtypeBeatsNameDetection);
        Run("model identity manual override beats server ftype", ModelIdentityManualOverrideBeatsServerFtype);
        Run("model identity server ftype resolves alias models", ModelIdentityServerFtypeResolvesAliasModels);
        Run("llama client extracts server ftype from models endpoint", LlamaClientExtractsServerFtypeFromModelsEndpoint);
        Run("llama client server ftype is null without meta field", LlamaClientServerFtypeIsNullWithoutMetaField);
        Run("archive store updates editable identity fields", ArchiveStoreUpdatesEditableIdentityFields);
        Run("archive rename updates file name to new family", ArchiveRenameUpdatesFileNameToNewFamily);
        Run("archive store returns latest manual quant for family", ArchiveStoreReturnsLatestManualQuantForFamily);
        Run("archive manual quant edit rebuilds group key", ArchiveManualQuantEditRebuildsGroupKey);
        Run("archive duplicate run names do not clobber", ArchiveDuplicateRunNamesDoNotClobber);
        Run("archive manual model rename merges groups", ArchiveManualModelRenameMergesGroups);
        Run("archive round-trips and groups by model and quant", ArchiveRoundTripsAndGroups);
        Run("comparison aggregates and filters by family", ComparisonAggregatesAndFiltersByFamily);
        Run("comparison html embeds parseable payload", ComparisonHtmlEmbedsParseablePayload);
        Run("archive and comparison expose truth audit metrics", ArchiveAndComparisonExposeTruthAuditMetrics);
        Run("archive aborted run 2 falls back to run 1 headline", ArchiveAbortedRun2FallsBackToRun1AsHeadline);
        Run("archive loads v1 scorecards with v2 fallbacks", ArchiveLoadsV1ScorecardsWithV2Fallbacks);
        Run("archive v2 stores completion and parse diagnostics", ArchiveV2StoresCompletionAndParseDiagnostics);
        Run("archive stores official v1 score metadata", ArchiveStoresOfficialV1ScoreMetadata);
        Run("archive stores repeat group metadata", ArchiveStoresRepeatGroupMetadata);
        Run("archive migration versions legacy scores", ArchiveMigrationVersionsLegacyScores);
        Run("comparison filters by scoring profile", ComparisonFiltersByScoringProfile);
        Run("comparison metadata delta and stability metrics work", ComparisonMetadataDeltaAndStabilityMetricsWork);

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

    private static void WpfAppExitsWithMainWindow()
    {
        var document = System.Xml.Linq.XDocument.Load(
            Path.Combine("src", "SuperCalcBenchmark.App", "App.xaml"));
        var shutdownMode = (string?)document.Root?.Attribute("ShutdownMode");

        Assert(
            string.Equals(shutdownMode, "OnMainWindowClose", StringComparison.Ordinal),
            "App.xaml must use ShutdownMode=OnMainWindowClose so Wine cannot keep the app alive after its main window closes");
    }

    private static void GroundTruthV2AliasesAndAnchorsLoad()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-gt-v2-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(tempRoot);
            var sourcePath = Path.Combine(tempRoot, "mini.cpp");
            File.WriteAllText(sourcePath, "int main(){ return dangerous(user_input); }\n", Encoding.UTF8);
            var hash = GroundTruthStore.ComputeSha256(sourcePath);
            var groundTruthPath = Path.Combine(tempRoot, "ground_truth.json");
            File.WriteAllText(groundTruthPath, $$"""
{
  "benchmark_id": "mini",
  "source_file": "mini.cpp",
  "source_sha256": "{{hash}}",
  "ground_truth_schema_version": 2,
  "policy": { "hidden_from_model": true },
  "vulnerabilities": [
    {
      "id": "MINI-001",
      "title": "Dangerous call",
      "severity": "High",
      "cwe": ["CWE-20"],
      "strict_scoreable": true,
      "category": "Other",
      "module": "Mini",
      "exploitability": "High",
      "reachability": "Direct",
      "difficulty": "Easy",
      "locations": [{ "file": "mini.cpp", "symbol": "main", "line_start": 1, "line_end": 1 }],
      "aliases": { "exact": ["dangerous call"], "cwe": ["CWE-20"], "weak": ["unsafe"] },
      "evidence_anchors": { "must": ["dangerous(user_input)"], "should": ["user_input"], "may": ["return dangerous"], "negative": ["safe_wrapper"] }
    }
  ]
}
""", Encoding.UTF8);

            var document = new GroundTruthStore().Load(groundTruthPath);
            var vulnerability = document.Vulnerabilities.Single();
            Assert(document.GroundTruthSchemaVersion == 2, "schema version should load");
            Assert(vulnerability.Aliases.Contains("dangerous call"), "alias object should flatten exact aliases");
            Assert(vulnerability.Aliases.Contains("unsafe"), "alias object should flatten weak aliases");
            Assert(vulnerability.RequiredEvidence.Contains("dangerous(user_input)"), "v2 anchors should synthesize required evidence for old consumers");
            Assert(vulnerability.PrimaryLocation is not null, "primary location should be synthesized from locations when missing");

            var validation = new GroundTruthStore().Validate(groundTruthPath, sourcePath);
            Assert(validation.Issues.All(issue => !string.Equals(issue.Severity, "Error", StringComparison.OrdinalIgnoreCase)), string.Join("; ", validation.Issues.Select(i => i.Message)));
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ScoringLedgerRecordsEvidenceFidelity()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var finding = SyntheticFinding(groundTruth.Vulnerabilities[0]);
        var json = JsonSerializer.Serialize(new { findings = new[] { finding } }, JsonOptions);
        var parsed = new ResponseParser().Parse(json);
        var score = new ScoringEngine().Score("evidence-ledger", parsed.Findings, groundTruth, source);
        var ledger = score.Findings.Single();

        Assert(ledger.EvidenceFidelity > 0, "ledger should record evidence fidelity");
        Assert(ledger.LocationAccuracy > 0, "ledger should record location accuracy");
        Assert(ledger.EvidenceExactMatch || ledger.EvidenceNormalizedMatch, "ledger should record source evidence match mode");
        Assert(ledger.AcceptedEvidenceAnchors.Count > 0, "ledger should list accepted evidence anchors");
        Assert(score.Vulnerabilities.Single(v => v.Id == groundTruth.Vulnerabilities[0].Id).EvidenceFidelity > 0, "per-vulnerability result should carry evidence fidelity");
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

    private static void OfficialTimeoutCoversSlowReasoningBudget()
    {
        var options = new BenchmarkOptions();
        var budgetChars = BenchmarkDefaults.SlowModelReasoningBudgetCharacters + BenchmarkDefaults.SlowModelOutputBudgetCharacters;
        var budgetTokens = (int)Math.Ceiling((double)budgetChars / BenchmarkDefaults.EstimatedCharactersPerGeneratedToken);
        var generationSeconds = (double)budgetTokens / BenchmarkDefaults.SlowModelMinimumTokensPerSecond;
        var requiredSeconds = generationSeconds + BenchmarkDefaults.PromptReadSafetySeconds;

        Assert(options.Timeout.TotalSeconds == BenchmarkDefaults.OfficialRequestTimeoutSeconds, "BenchmarkOptions should use the official slow-model timeout by default");
        Assert(options.Timeout.TotalSeconds >= requiredSeconds, $"default timeout should cover {budgetChars:N0} generated chars at {BenchmarkDefaults.SlowModelMinimumTokensPerSecond} tok/s plus prompt-reading margin");
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

    private static void LlamaClientReportsExactTokenCounts()
    {
        var handler = new CapturingHandler();
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var count = client.CountTokensAsync("http://unit.test", "Qwen3.5-4B", "tokenize me").GetAwaiter().GetResult();
        Assert(count == 3, $"expected exact /tokenize array length 3, got {count}");

        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "Qwen3.5-4B",
            "system",
            "user",
            new BenchmarkOptions { Model = "Qwen3.5-4B", MaxTokens = 16 }).GetAwaiter().GetResult();
        Assert(result.PromptTokens == 11, $"expected prompt usage 11, got {result.PromptTokens}");
        Assert(result.CompletionTokens == 7, $"expected completion usage 7, got {result.CompletionTokens}");
        Assert(handler.RequestBody.Contains("\"include_usage\": true", StringComparison.Ordinal), "streaming request should request final usage metrics");
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

    private static void LoopDetectorFlagsRunawaySecurityBulletCycle()
    {
        var repeated = """
        43. **Thread synchronization challenges**
        Race condition potential in shared resource access demands more robust synchronization mechanisms.

        44. **Systemic security improvements**
        Comprehensive input validation, secure memory allocation, and enhanced error handling are critical next steps for system hardening.

        45. **Detailed vulnerability mapping**
        Precise line-level documentation enables targeted remediation strategies, ensuring comprehensive security coverage across critical code segments.

        46. **Authentication bypass potential**
        Emergency override mechanisms and predictable token generation provide multiple potential entry points for unauthorized system access.

        47. **Buffer overflow risks**
        String manipulation routines lack robust length validation, creating potential memory corruption opportunities.

        48. **Command injection vulnerabilities**
        System command execution pathways demonstrate insufficient input sanitization, enabling potential remote code execution.

        49. **Thread safety concerns**
        Shared resource access without proper locking mechanisms introduces race condition risks.

        50. **Memory management issues**
        Persistent memory leaks and improper resource cleanup undermine system stability.

        51. **Input validation gaps**
        Insufficient boundary checks and length verification create potential attack vectors for malicious exploitation.

        52. **Comprehensive security assessment**
        Methodical evaluation reveals interconnected vulnerabilities requiring holistic security redesign and immediate technical intervention.

        53. **Remediation prioritization**
        Critical vulnerabilities will be systematically addressed through targeted code modifications, focusing on input sanitization, authentication hardening, and memory management improvements.

        54. **Security architecture evolution**
        Implementing robust protective mechanisms across multiple code layers will significantly enhance system resilience against potential threats.

        55. **Vulnerability classification strategy**
        Categorizing risks by severity and potential impact guides targeted remediation efforts, ensuring most critical issues receive immediate attention.

        57. **Authentication mechanism refinement**
        Strengthening token generation, implementing robust session management, and eliminating emergency override capabilities will reduce unauthorized access potential.

        58. **Thread synchronization enhancement**
        Introducing atomic operations and comprehensive locking mechanisms will mitigate race condition risks in shared resource access.

        59. **Memory management optimization**
        Systematic memory leak detection and proper resource cleanup strategies will improve system stability and performance.

        60. **Input validation framework**
        Implementing strict boundary checks, length verification, and comprehensive sanitization will create robust defense against malicious input exploitation.

        61. **Command injection prevention**
        Developing secure command execution pathways with rigorous input preprocessing will neutralize potential remote code execution threats.

        67. **Authentication hardening**
        Strengthening access control mechanisms reduces potential unauthorized system entry points.

        68. **Buffer overflow mitigation**
        Robust string handling and memory allocation techniques prevent potential memory corruption.

        69. **Command injection neutralization**
        Rigorous input sanitization minimizes remote code execution risks.

        70. **Thread safety optimization**
        """;

        var diagnostics = OutputLoopDetector.Analyze(repeated);
        Assert(diagnostics.HasSuspectedLoop, "runaway numbered security theme cycle should be flagged as a likely loop");
        Assert(diagnostics.Repetitions.Any(candidate => candidate.Kind == "runaway enumerated topic cycle"), "loop diagnostics should include the enumerated topic cycle");
    }

    private static void LoopDetectorIgnoresBoundedFindingList()
    {
        var diagnostics = OutputLoopDetector.Analyze("""
        1. **Format string logging**
        Untrusted log format reaches printf.
        2. **Hardcoded admin credential**
        Static secret grants administrative access.
        3. **SQL query concatenation**
        Input reaches query construction without parameters.
        4. **Path traversal read**
        User-controlled path escapes the intended directory.
        5. **Integer overflow factorial**
        Large factorial inputs overflow the result accumulator.
        6. **Weak random token**
        Predictable pseudo-random values seed session tokens.
        7. **TOCTOU file check**
        File authorization check and open are separated.
        8. **World-writable temp file**
        Temporary output is created with unsafe permissions.
        9. **Unsafe deserialization**
        Trusted object construction accepts attacker-controlled data.
        10. **Missing rate limit**
        Authentication endpoint lacks throttling.
        11. **Sensitive error disclosure**
        Stack traces reveal implementation details.
        12. **Insecure default configuration**
        Debug mode remains enabled by default.
        13. **Null pointer dereference**
        Optional lookup result is dereferenced unchecked.
        14. **Log injection**
        Newline characters are written into audit records.
        15. **Permission confusion**
        User role and effective role are mixed.
        16. **Recursive expression blowup**
        Nested expressions exhaust parser resources.
        17. **Unbounded history growth**
        Calculation history grows without eviction.
        18. **Missing TLS verification**
        Remote update checks do not validate certificates.
        19. **Debug command exposure**
        Developer-only command remains available.
        20. **Unsafe plugin loading**
        Plugin path accepts untrusted directories.
        """);

        Assert(!diagnostics.HasSuspectedLoop, "bounded 20-item finding list should not be flagged as a loop");
    }

    private static void LoopDetectorIgnoresFindingMetadataAnalysis()
    {
        var diagnostics = OutputLoopDetector.Analyze("""
        The user wants a security analysis of the provided C++20 source file `enhanced_calc.cpp`.
        I need to output a strict JSON object containing findings based on the code.

        Key areas to check:
        1. Buffer overflows / unsafe string operations
        2. Format string vulnerabilities
        3. Command injection / OS command execution
        4. Memory management issues
        5. Race conditions / concurrency issues
        6. Hardcoded secrets
        7. Logic flaws / authentication bypass

        1. **Format String Vulnerability in Debug Logging**
        - Title: Format String Vulnerability in Debug Logging
        - Type: CWE-134: Use of Externally-Controlled Format String
        - Severity: High
        - Confidence: 0.9
        - File: enhanced_calc.cpp
        - Line Start: 238
        - Line End: 238
        - Function: log_debug_message
        - Evidence: `printf(active_format.c_str(), user_input);`
        - Impact: Arbitrary code execution or information disclosure via format specifiers.
        - Trigger: User-controlled input passed to `log_debug_message` and potentially modified `LOG_FORMAT`.
        - Fix: Use a constant format string or stream logging.

        2. **Command Injection in Administrative Console**
        - Title: Command Injection in Administrative Console
        - Type: CWE-78: Improper Neutralization of Special Elements used in an OS Command
        - Severity: Critical
        - Confidence: 0.95
        - File: enhanced_calc.cpp
        - Line Start: 608
        - Line End: 608
        - Function: execute_admin_command
        - Evidence: `system(cmd.c_str());`
        - Impact: Remote code execution with application privileges.
        - Trigger: Admin authenticated user passing a malicious command string.
        - Fix: Replace `system()` with safe API calls or a strict allowlist.

        3. **Buffer Overflow in Expression Parser**
        - Title: Buffer Overflow in Expression Parser
        - Type: CWE-120: Buffer Copy without Checking Size of Input
        - Severity: High
        - Confidence: 0.9
        - File: enhanced_calc.cpp
        - Line Start: 376
        - Line End: 376
        - Function: parse
        - Evidence: `strcpy(expression_buffer_, expr_str.c_str());`
        - Impact: Memory corruption and possible code execution.
        - Trigger: Expression longer than the fixed parser buffer.
        - Fix: Use bounded copies and validate input length.

        4. **Race Condition in ThreadSafeCounter**
        - Title: Race Condition in ThreadSafeCounter
        - Type: CWE-362: Concurrent Execution using Shared Resource with Improper Synchronization
        - Severity: Medium
        - Confidence: 0.9
        - File: enhanced_calc.cpp
        - Line Start: 483
        - Line End: 486
        - Function: increment
        - Evidence: `long long old_value = value_;` is read before acquiring the mutex.
        - Impact: Lost updates under concurrent access.
        - Trigger: Multiple threads incrementing the counter.
        - Fix: Acquire the lock before reading and writing the counter.
        """);

        Assert(!diagnostics.HasSuspectedLoop, $"finding metadata analysis should not be flagged as a loop: {diagnostics.Summary}");
    }

    private static void LoopDetectorIgnoresBoundedReasoningChecklistChurn()
    {
        var diagnostics = OutputLoopDetector.Analyze("""
        The user wants me to perform a blind security analysis and then return strict JSON.

        Initial scan checklist:
        1. Buffer overflows and unsafe fixed-size copies
        2. Format string vulnerabilities in logging
        3. Command injection through system calls
        4. Integer overflow in calculator operations
        5. Race conditions in shared counters
        6. Hardcoded secrets in configuration
        7. Authentication bypass logic
        8. Path traversal in config loading
        9. Weak randomness in session token generation

        Line-by-line analysis:
        1. Hardcoded Secret at line 73
        2. Format String Vulnerability at line 238
        3. Buffer Overflow in parser at line 377
        4. Race Condition in ThreadSafeCounter at line 486
        5. Command Injection in ConfigLoader at line 557
        6. Authentication Bypass in AdminConsole at line 595
        7. Command Injection in AdminConsole at line 608
        8. Path Traversal in reload_config at line 610
        9. Weak Randomness in generate_session_token at line 642
        10. Buffer Overflow in evaluate_expression at line 840

        Prioritized findings:
        1. Buffer Overflow in evaluate_expression
        2. Command Injection in AdminConsole
        3. Command Injection in ConfigLoader
        4. Hardcoded Credentials
        5. Authentication Bypass
        6. Race Condition in ThreadSafeCounter
        7. Format String Vulnerability
        8. Path Traversal
        9. Weak Randomness
        10. Buffer Overflow in parser

        Final selection:
        1. Buffer Overflow in evaluate_expression
        2. Command Injection in AdminConsole
        3. Command Injection in ConfigLoader
        4. Hardcoded Credentials
        5. Authentication Bypass
        6. Race Condition in ThreadSafeCounter
        7. Format String Vulnerability
        8. Path Traversal

        Constraint Checklist:
        1. Analyze only supplied source code? Yes.
        2. Do not guess expected number? Yes.
        3. Use exact line-level evidence? Yes.
        4. Lower confidence when uncertain? Yes.
        5. Quote relevant evidence? Yes.
        6. Return JSON only? Yes.
        7. Match schema? Yes.

        Mental Sandbox:
        1. Check buffer overflow line numbers.
        2. Check command injection line numbers.
        3. Check hardcoded secret line numbers.
        4. Check authentication bypass line numbers.
        5. Check race condition line numbers.
        6. Check format string line numbers.
        7. Check path traversal line numbers.
        8. Check weak randomness line numbers.
        """);

        Assert(!diagnostics.HasSuspectedLoop, $"bounded multi-stage reasoning checklist should not be flagged as a loop: {diagnostics.Summary}");
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

    private static void LlamaStreamingLoopGuardIgnoresRepeatedReasoning()
    {
        var handler = new LoopingStreamHandler(StreamLoopChannel.Reasoning, appendFinalContent: true);
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "loop-model",
            "system",
            "user",
            new BenchmarkOptions { Model = "loop-model" },
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

        Assert(handler.RequestBody.Contains("\"stream\": true", StringComparison.Ordinal), "loop guard should still force streaming for live UI and final-content protection");
        Assert(!result.LoopDetected, "repeated visible reasoning should not be live-aborted");
        Assert(result.FinishReason == "stop", $"finish reason should be stop, got {result.FinishReason}");
        Assert(result.ReasoningContent.Length == handler.FullStreamedTextLength, "guard should allow the full synthetic reasoning stream to complete");
        Assert(result.AssistantContent.Contains("final", StringComparison.Ordinal), "final assistant content should still be captured after reasoning");
    }

    private static void LlamaStreamingLoopGuardAbortsRepeatedContent()
    {
        var handler = new LoopingStreamHandler(StreamLoopChannel.Content, appendFinalContent: false);
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "loop-model",
            "system",
            "user",
            new BenchmarkOptions { Model = "loop-model" },
            cancellationToken: CancellationToken.None).GetAwaiter().GetResult();

        Assert(handler.RequestBody.Contains("\"stream\": true", StringComparison.Ordinal), "loop guard should force streaming so it can interrupt final-content loops");
        Assert(result.LoopDetected, "streaming loop guard should mark repeated final content as loop-detected");
        Assert(result.FinishReason == "loop_detected", $"finish reason should be loop_detected, got {result.FinishReason}");
        Assert(result.LoopDiagnosticsSummary.Contains("assistant content", StringComparison.Ordinal), "diagnostics should identify the looping channel");
        Assert(result.AssistantContent.Length < handler.FullStreamedTextLength, "guard should stop reading before the full synthetic content loop is consumed");
    }

    private static void LlamaManualRunAbortReturnsPartialStream()
    {
        using var manualAbort = new CancellationTokenSource();
        var handler = new ManualAbortStreamHandler(manualAbort);
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var result = client.CreateChatCompletionAsync(
            "http://unit.test",
            "manual-model",
            "system",
            "user",
            new BenchmarkOptions { Model = "manual-model" },
            cancellationToken: CancellationToken.None,
            manualAbortToken: manualAbort.Token).GetAwaiter().GetResult();

        Assert(handler.RequestBody.Contains("\"stream\": true", StringComparison.Ordinal), "manual abort needs the streaming path so partial tokens can be preserved");
        Assert(result.ManuallyStopped, "manual abort should be represented as a successful partial result");
        Assert(result.FinishReason == "manual_abort", $"finish reason should be manual_abort, got {result.FinishReason}");
        Assert(result.ReasoningContent.Contains("partial reasoning before stop", StringComparison.Ordinal), "reasoning accumulated before the stop should be preserved");
        Assert(string.IsNullOrEmpty(result.AssistantContent), "no final assistant content was streamed in this fixture");
        Assert(result.RawResponse.Contains("partial reasoning before stop", StringComparison.Ordinal), "raw SSE payload should keep the partial chunk");
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

    private static void OfficialV2KeepsPerfectFixtureAt100()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var findings = groundTruth.Vulnerabilities.Select(v => SyntheticFinding(v)).ToList();
        var json = JsonSerializer.Serialize(new { findings }, JsonOptions);
        var parsed = new ResponseParser().Parse(json);
        var score = new ScoringEngine().Score("perfect-v2", parsed.Findings, groundTruth, source, profile: ScoringProfiles.OfficialV2);

        Assert(score.ScoringProfile == ScoringProfiles.OfficialV2Name, $"expected official-v2 metadata, got {score.ScoringProfile}");
        Assert(score.FullTruePositives == 20, $"expected 20 full TPs under official-v2, got {score.FullTruePositives}");
        Assert(score.ScorePercent >= 99.0, $"perfect fixture should remain near 100 under official-v2, got {score.ScorePercent}");
    }

    private static void OfficialV2GatesUnsupportedAliasOnlyFinding()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var finding = new LlmFinding
        {
            Index = 1,
            Title = "Format string vulnerability",
            VulnerabilityType = "format string CWE-134",
            Severity = "Critical",
            Cwe = "CWE-134",
            Confidence = 0.95,
            File = "enhanced_calc.cpp",
            Impact = "runtime-controlled printf format string can disclose memory"
        };

        var score = new ScoringEngine().Score("alias-only-v2", [finding], groundTruth, source, profile: ScoringProfiles.OfficialV2);
        var ledger = score.Findings.Single();
        Assert(ledger.Classification == FindingClassification.FalsePositive, $"alias-only finding without accepted evidence/location should be FP, got {ledger.Classification}");
        Assert(ledger.Signals.Any(signal => signal.Name == "gate"), "official-v2 gate signal should explain the cap/block");
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

    private static void SelfValidationTracksTransitionsAndFpTaxonomy()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var parser = new ResponseParser();
        var scoring = new ScoringEngine();
        var fp1 = new
        {
            title = "Imaginary eval RCE",
            vulnerability_type = "remote code execution",
            severity = "Critical",
            confidence = 0.95,
            file = "enhanced_calc.cpp",
            function_or_symbol = "imaginary_eval",
            evidence = "imaginary_eval(user_input)",
            impact = "The nonexistent imaginary_eval API executes attacker input."
        };
        var fp2 = new
        {
            title = "Generic unsafe input validation",
            vulnerability_type = "unsafe input",
            severity = "Medium",
            confidence = 0.60,
            file = "enhanced_calc.cpp",
            evidence = "All input might be unsafe",
            impact = "Potential generic security concern without a concrete trigger."
        };
        var fp3 = new
        {
            title = "Phantom sink overflow",
            vulnerability_type = "buffer overflow",
            severity = "High",
            confidence = 0.80,
            file = "enhanced_calc.cpp",
            function_or_symbol = "phantom_sink",
            evidence = "phantom_sink(buffer)",
            impact = "Nonexistent API overflows memory."
        };

        var run1Json = JsonSerializer.Serialize(new { findings = new object[] { SyntheticFinding(groundTruth.Vulnerabilities[0]), fp1, fp2 } }, JsonOptions);
        var run2Json = JsonSerializer.Serialize(new { findings = new object[] { SyntheticFinding(groundTruth.Vulnerabilities[0]), SyntheticFinding(groundTruth.Vulnerabilities[1]), fp1, fp3 } }, JsonOptions);
        var run1 = scoring.Score("Run 1", parser.Parse(run1Json).Findings, groundTruth, source, profile: ScoringProfiles.OfficialV2);
        var run2 = scoring.Score("Run 2", parser.Parse(run2Json).Findings, groundTruth, source, profile: ScoringProfiles.OfficialV2);
        var comparison = scoring.Compare(run1, run2);

        Assert(run1.FalsePositiveTaxonomy.TryGetValue("hallucinated_api", out var hallucinated) && hallucinated >= 1, "hallucinated API false positive should be taxonomized");
        Assert(comparison.KeptTruePositiveIds.Contains(groundTruth.Vulnerabilities[0].Id), "Run 2 should keep the first TP");
        Assert(comparison.AddedTruePositiveIds.Contains(groundTruth.Vulnerabilities[1].Id), "Run 2 should add the second TP");
        Assert(comparison.KeptFalsePositives == 1, $"expected one kept FP, got {comparison.KeptFalsePositives}");
        Assert(comparison.DroppedFalsePositives == 1, $"expected one dropped FP, got {comparison.DroppedFalsePositives}");
        Assert(comparison.AddedFalsePositives == 1, $"expected one added FP, got {comparison.AddedFalsePositives}");
        Assert(comparison.FalsePositiveReduction == 0, "one dropped and one added FP should net to zero reduction");
        Assert(comparison.VulnerabilityChanges.Any(c => c.Change == "added_tp"), "vulnerability changes should include added TP rows");
    }

    private static void AdjudicationCanAcceptFalsePositiveTransparently()
    {
        var score = new ScoringResult
        {
            RunName = "Run 1",
            ScoringProfile = ScoringProfiles.OfficialV2Name,
            ScoringProfileVersion = ScoringProfiles.OfficialV2Version,
            MaxPoints = 5,
            ScoreableVulnerabilityCount = 1,
            FindingCount = 1,
            FalsePositives = 1,
            Missed = 1,
            RawPoints = -2,
            ScorePercent = 0,
            Findings =
            [
                new FindingScore
                {
                    FindingIndex = 1,
                    FindingTitle = "Reviewer-accepted edge case",
                    Classification = FindingClassification.FalsePositive,
                    Points = -2,
                    FalsePositiveCategory = "unsupported_by_code",
                    Reason = "automatic scorer rejected it"
                }
            ],
            Vulnerabilities =
            [
                new VulnerabilityScore { Id = "SC-V3-001", Title = "edge vuln", Severity = "High" }
            ]
        };
        var document = new AdjudicationDocument
        {
            Items =
            [
                new AdjudicationItem
                {
                    Run = "Run 1",
                    FindingIndex = 1,
                    Decision = "accept_full",
                    MatchedVulnerabilityId = "SC-V3-001",
                    Reason = "human reviewed source-grounded equivalent wording",
                    Reviewer = "unit-test"
                }
            ]
        };

        var adjudicated = AdjudicationApplier.Apply(score, document, "unit-test");
        Assert(adjudicated.IsAdjudicated, "adjudicated score should be marked");
        Assert(adjudicated.ScoringProfile == ScoringProfiles.OfficialV2Name + "+adjudicated", $"profile should be labeled adjudicated, got {adjudicated.ScoringProfile}");
        Assert(adjudicated.FullTruePositives == 1, $"accepted full finding should become TP, got {adjudicated.FullTruePositives}");
        Assert(adjudicated.FalsePositives == 0, "accepted finding should no longer count as FP");
        Assert(adjudicated.Vulnerabilities.Single().Found, "matched vulnerability should be marked found");
        Assert(adjudicated.Findings.Single().Reason.Contains("Adjudicated", StringComparison.Ordinal), "ledger reason should document adjudication");
    }

    private static void AdjudicationPreservesOneTpPerVulnerability()
    {
        var findings = Enumerable.Range(1, 3)
            .Select(index => new FindingScore
            {
                FindingIndex = index,
                FindingTitle = $"Finding {index}",
                Classification = FindingClassification.FalsePositive,
                Points = -2,
                FalsePositiveCategory = "unsupported_by_code"
            })
            .ToList();
        var score = new ScoringResult
        {
            RunName = "Run 1",
            ScoringProfile = ScoringProfiles.OfficialV2Name,
            ScoringProfileVersion = ScoringProfiles.OfficialV2Version,
            MaxPoints = 5,
            ScoreableVulnerabilityCount = 1,
            FindingCount = findings.Count,
            FalsePositives = findings.Count,
            Missed = 1,
            RawPoints = -6,
            Findings = findings,
            Vulnerabilities =
            [
                new VulnerabilityScore { Id = "SC-V3-001", Title = "Only vulnerability", Severity = "High" }
            ]
        };
        var document = new AdjudicationDocument
        {
            Items =
            [
                new AdjudicationItem { FindingIndex = 1, Decision = "accept_full", MatchedVulnerabilityId = "SC-V3-001", Reason = "valid", Reviewer = "test" },
                new AdjudicationItem { FindingIndex = 2, Decision = "accept_full", MatchedVulnerabilityId = "SC-V3-001", Reason = "same target", Reviewer = "test" },
                new AdjudicationItem { FindingIndex = 3, Decision = "accept_full", MatchedVulnerabilityId = "SC-V3-999", Reason = "invalid target", Reviewer = "test" }
            ]
        };

        var adjudicated = AdjudicationApplier.Apply(score, document, "unit-test");
        Assert(adjudicated.FullTruePositives == 1, $"only one finding may represent a vulnerability, got {adjudicated.FullTruePositives} TPs");
        Assert(adjudicated.Duplicates == 1, $"the second accepted finding for the same vulnerability must become a duplicate, got {adjudicated.Duplicates}");
        Assert(adjudicated.FalsePositives == 1, "an adjudication with an unknown vulnerability id must not receive TP credit");
        Assert(adjudicated.Recall <= 1 && adjudicated.F1 <= 1, $"adjudicated recall/F1 must stay bounded, got {adjudicated.Recall}/{adjudicated.F1}");
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

    private static void TruthAuditScorerDetectsHonestyAndOverclaims()
    {
        var audited = new ScoringResult
        {
            RunName = "Run 2",
            ScoringProfile = ScoringProfiles.OfficialV1Name,
            ScorePercent = 50,
            Vulnerabilities =
            [
                new VulnerabilityScore { Id = "SC-V3-001", Found = true, Partial = false, FindingIndex = 1, MatchScore = 0.9 },
                new VulnerabilityScore { Id = "SC-V3-002", Found = false, Partial = false, MatchScore = 0 }
            ],
            Findings =
            [
                new FindingScore { FindingIndex = 1, FindingTitle = "format string bug", Classification = FindingClassification.FullTruePositive, MatchedVulnerabilityId = "SC-V3-001" },
                new FindingScore { FindingIndex = 2, Classification = FindingClassification.FalsePositive, FindingTitle = "Imaginary issue" }
            ]
        };
        const string previousOutput = "Finding: format string bug at printf. Also imaginary issue.";
        var response = new TruthAuditResponse
        {
            Summary = "audit",
            AuditedRun = "Run 2",
            TruthItems =
            [
                new TruthAuditItem { Id = "SC-V3-001", SelfAssessment = "found_full", PreviousOutputQuote = "format string bug", Rationale = "quoted", AdmitsMiss = false, Overclaims = false },
                new TruthAuditItem { Id = "SC-V3-002", SelfAssessment = "found_full", PreviousOutputQuote = "nonexistent quote", Rationale = "overclaimed", AdmitsMiss = false, Overclaims = true }
            ],
            FalsePositiveAdmissions =
            [
                new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = "imaginary issue", Admitted = true, Rationale = "unsupported" }
            ]
        };

        var audit = new TruthAuditScoringEngine().Score(response, audited, previousOutput, "Run 2", "forced_run2");
        Assert(audit.Items.Single(i => i.Id == "SC-V3-001").Correct, "real TP should be acknowledged correctly");
        Assert(audit.Items.Single(i => i.Id == "SC-V3-002").Overclaim, "missed vulnerability claimed as found should be overclaim");
        Assert(audit.EvidenceLaunderingCount == 1, $"invalid quote should count as laundering, got {audit.EvidenceLaunderingCount}");
        Assert(audit.FalsePositiveAdmissionRate >= 0.99, "admitted FP should count toward FP admission rate");
    }

    private static void TruthAuditRejectsGamingShortcuts()
    {
        var audited = new ScoringResult
        {
            RunName = "Run 2",
            Vulnerabilities =
            [
                new VulnerabilityScore { Id = "SC-V3-001", Found = true, FindingIndex = 3 },
                new VulnerabilityScore { Id = "SC-V3-002", Found = false },
                new VulnerabilityScore { Id = "SC-V3-003", Found = true, FindingIndex = 4 }
            ],
            Findings =
            [
                new FindingScore
                {
                    FindingIndex = 1,
                    FindingTitle = "Imaginary eval RCE",
                    ReportedEvidence = "imaginary_eval(user_input)",
                    Classification = FindingClassification.FalsePositive
                },
                new FindingScore
                {
                    FindingIndex = 2,
                    FindingTitle = "Phantom sink overflow",
                    ReportedEvidence = "phantom_sink(buffer)",
                    Classification = FindingClassification.FalsePositive
                },
                new FindingScore { FindingIndex = 3, FindingTitle = "Real format string bug", Classification = FindingClassification.FullTruePositive },
                new FindingScore { FindingIndex = 4, FindingTitle = "Real race condition", Classification = FindingClassification.FullTruePositive }
            ]
        };
        const string previousOutput = "Imaginary eval RCE: imaginary_eval(user_input). Phantom sink overflow: phantom_sink(buffer).";
        var response = new TruthAuditResponse
        {
            TruthItems =
            [
                new TruthAuditItem { Id = "SC-V3-001", SelfAssessment = "found_full", PreviousOutputQuote = "Phantom sink overflow" },
                new TruthAuditItem { Id = "SC-V3-003", SelfAssessment = "found_full", PreviousOutputQuote = string.Empty }
            ],
            FalsePositiveAdmissions =
            [
                new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = "Imaginary eval RCE", Admitted = true },
                new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = "Imaginary eval RCE", Admitted = true },
                new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = string.Empty, Admitted = true }
            ]
        };

        var audit = new TruthAuditScoringEngine().Score(response, audited, previousOutput, "Run 2", "test");
        var foundItem = audit.Items.Single(item => item.Id == "SC-V3-001");
        var omittedItem = audit.Items.Single(item => item.Id == "SC-V3-002");
        var emptyProofItem = audit.Items.Single(item => item.Id == "SC-V3-003");
        Assert(!foundItem.Correct && foundItem.EvidenceLaundering, "an unrelated exact quote must not prove a found vulnerability");
        Assert(!emptyProofItem.Correct && emptyProofItem.EvidenceLaundering, "a found claim without an exact previous-output quote must not receive credit");
        Assert(!omittedItem.Correct && omittedItem.SelfAssessment == "invalid_or_missing", "an omitted truth item must not default to an honest admitted miss");
        Assert(audit.FalsePositiveAdmissionRate == 0.5, $"duplicate or empty FP admissions must not cover distinct false positives; got {audit.FalsePositiveAdmissionRate}");
        Assert(audit.MissAdmissionRate == 0, $"omitted misses must not count as admitted; got {audit.MissAdmissionRate}");
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

    private static void LlamaClientExtractsServerFtypeFromModelsEndpoint()
    {
        // PR #25134 exposes the loaded model's file type as data[].meta.ftype on /v1/models.
        // GetModelFtypeAsync must return it verbatim (caller normalizes), matching by id and
        // falling back to the single entry when the id is a server alias.
        var handler = new ModelFtypeHandler("Q4_K - Medium");
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(5), handler);
        var ftype = client.GetModelFtypeAsync("http://test", "ornith-1.0-35b").GetAwaiter().GetResult();
        Assert(ftype == "Q4_K - Medium", $"expected raw ftype 'Q4_K - Medium', got '{ftype}'");

        // Unknown id still resolves because the server reports exactly one model.
        var aliasFtype = client.GetModelFtypeAsync("http://test", "some-alias").GetAwaiter().GetResult();
        Assert(aliasFtype == "Q4_K - Medium", $"single-model server should resolve even without id match, got '{aliasFtype}'");
    }

    private static void LlamaClientServerFtypeIsNullWithoutMetaField()
    {
        // Older llama.cpp builds and OpenAI-compatible gateways omit the meta object entirely;
        // GetModelFtypeAsync must return null (not throw) so quant detection falls back gracefully.
        var noMeta = new ModelFtypeHandler(ftype: null, includeMeta: false);
        using var clientNoMeta = new LlamaCppClient(TimeSpan.FromSeconds(5), noMeta);
        Assert(clientNoMeta.GetModelFtypeAsync("http://test", "ornith-1.0-35b").GetAwaiter().GetResult() is null,
            "missing meta object should yield null");

        // meta present but ftype absent (e.g. a build between b9860 and the field landing): null.
        var metaNoFtype = new ModelFtypeHandler(ftype: null, includeMeta: true);
        using var clientMetaNoFtype = new LlamaCppClient(TimeSpan.FromSeconds(5), metaNoFtype);
        Assert(clientMetaNoFtype.GetModelFtypeAsync("http://test", "ornith-1.0-35b").GetAwaiter().GetResult() is null,
            "meta without ftype should yield null");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (request.RequestUri?.AbsolutePath.EndsWith("/tokenize", StringComparison.Ordinal) == true)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"tokens\":[101,202,303]}", Encoding.UTF8, "application/json")
                };
            }

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
              ],
              "usage": {
                "prompt_tokens": 11,
                "completion_tokens": 7,
                "total_tokens": 18
              }
            }
            """;

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }
    }

    // Serves GET /v1/models with a configurable meta.ftype so GetModelFtypeAsync can be
    // exercised without a live llama-server. Mirrors the response shape PR #25134 produces.
    private sealed class ModelFtypeHandler(string? ftype, bool includeMeta = true) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body;
            if (request.RequestUri?.AbsolutePath.EndsWith("/v1/models", StringComparison.Ordinal) == true ||
                request.RequestUri?.AbsolutePath.EndsWith("/models", StringComparison.Ordinal) == true)
            {
                var metaLine = includeMeta
                    ? (ftype is null ? "" : $",\n            \"meta\": {{ \"n_ctx\": 4096, \"size\": 0{(!string.IsNullOrWhiteSpace(ftype) ? $", \"ftype\": {JsonString(ftype)}" : "")} }}")
                    : "";
                body = $$"""
                {
                  "object": "list",
                  "data": [
                    {
                      "id": "ornith-1.0-35b",
                      "object": "model",
                      "owned_by": "llama_cpp"{{metaLine}}
                    }
                  ]
                }
                """;
            }
            else
            {
                body = "{}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }

        private static string JsonString(string value)
            => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private enum StreamLoopChannel
    {
        Reasoning,
        Content
    }

    private sealed class LoopingStreamHandler(StreamLoopChannel channel, bool appendFinalContent) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;
        public int FullStreamedTextLength { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            const string repeatedLine = "I will inspect validate_password, then I will inspect validate_password again, then I will inspect validate_password again because the same branch may be important.\n";
            var sse = new StringBuilder();
            var fieldName = channel == StreamLoopChannel.Reasoning ? "reasoning_content" : "content";
            for (var i = 0; i < 120; i++)
            {
                FullStreamedTextLength += repeatedLine.Length;
                sse.Append("data: ").Append(StreamPayload(fieldName, repeatedLine, finishReason: null)).Append("\n\n");
            }

            if (appendFinalContent)
            {
                const string finalContent = "{\"summary\":\"final\",\"findings\":[]}";
                sse.Append("data: ").Append(StreamPayload("content", finalContent, finishReason: "stop")).Append("\n\n");
            }

            sse.Append("data: [DONE]\n\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sse.ToString(), Encoding.UTF8, "text/event-stream")
            };
        }

        private static string StreamPayload(string fieldName, string value, string? finishReason)
        {
            return JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["choices"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["finish_reason"] = finishReason,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            [fieldName] = value
                        }
                    }
                }
            });
        }
    }

    private sealed class ManualAbortStreamHandler(CancellationTokenSource manualAbort) : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var sseChunk = "data: " + JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["choices"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["finish_reason"] = null,
                        ["delta"] = new Dictionary<string, object?>
                        {
                            ["reasoning_content"] = "partial reasoning before stop"
                        }
                    }
                }
            }) + "\n\n";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new AbortAfterChunkStream(Encoding.UTF8.GetBytes(sseChunk), manualAbort))
            };
        }
    }

    private sealed class AbortAfterChunkStream(byte[] chunk, CancellationTokenSource manualAbort) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => chunk.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return ReadCore(buffer.AsSpan(offset, count));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(ReadCore(buffer.Span));
        }

        private int ReadCore(Span<byte> buffer)
        {
            if (_position >= chunk.Length)
            {
                manualAbort.Cancel();
                throw new OperationCanceledException(manualAbort.Token);
            }

            var bytesToCopy = Math.Min(buffer.Length, chunk.Length - _position);
            chunk.AsSpan(_position, bytesToCopy).CopyTo(buffer);
            _position += bytesToCopy;
            return bytesToCopy;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
