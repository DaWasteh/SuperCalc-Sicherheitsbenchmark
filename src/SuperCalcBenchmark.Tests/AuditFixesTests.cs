using System.Net;
using System.Text;
using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

/// <summary>v0.7.8: fixes from the scoring/evaluation audit (campaign identity, run robustness, comparison, official-v3, accountability-v2).</summary>
internal static partial class TestRunner
{
    private static void CampaignItemsDoNotInheritQuantOverride()
    {
        var baseOptions = new BenchmarkOptions { Model = "base", QuantOverride = "UD_Q2_K_XL" };

        var campaignItem = baseOptions.With(model: "Ornith-1.5-9B", quantOverride: null, clearQuantOverride: true);
        Assert(campaignItem.QuantOverride is null, $"a campaign item without its own quant must not inherit the base override, got {campaignItem.QuantOverride}");

        var labelledItem = baseOptions.With(model: "gemma", quantOverride: "Q4_0", clearQuantOverride: true);
        Assert(labelledItem.QuantOverride == "Q4_0", "a campaign item keeps its explicit per-item quant");

        var repeat = baseOptions.With(seed: 7);
        Assert(repeat.QuantOverride == "UD_Q2_K_XL", "single-model repeats keep the user's manual quant");
    }

    private static void RunnerKeepsRun1WhenRun2Fails()
    {
        var paths = BenchmarkPathResolver.Resolve();
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-run2-failure-" + Guid.NewGuid().ToString("N"));
        try
        {
            var (groundTruth, _) = LoadGroundTruthAndSource();
            var findingsJson = JsonSerializer.Serialize(new { findings = new[] { SyntheticFinding(groundTruth.Vulnerabilities[0]) } }, JsonOptions);
            var handler = new ScriptedChatHandler(findingsJson, failFromCall: 2);
            var runner = new BenchmarkRunner(clientFactory: options => new LlamaCppClient(options.Timeout, handler));
            var options = FakeServerOptions(paths, tempRoot);

            var result = runner.RunAsync(options).GetAwaiter().GetResult();

            Assert(result.Run1.Score.FullTruePositives == 1, $"Run 1 must still be scored, got {result.Run1.Score.FullTruePositives} TP");
            Assert(result.Run2 is null && result.Comparison is null, "a failed Run 2 must not leave a half-built Run 2 or comparison");
            Assert(result.RunNotes.Any(note => note.StartsWith("Run 2 failed", StringComparison.Ordinal)), "the Run 2 failure must be recorded as a run note");
            var report = File.ReadAllText(Path.Combine(result.OutputDirectory, "report.md"));
            Assert(report.Contains("## Run Notes", StringComparison.Ordinal) && report.Contains("Run 2 failed", StringComparison.Ordinal), "report.md must be written and show the run note");
            var record = new ArchiveStore(options.ArchiveDirectory!).LoadAll().Single();
            Assert(record.PrimaryRun?.RunName == "Run 1", "the completed Run 1 must be archived as headline");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void RunnerSkipsRun2WithoutRun1Answer()
    {
        var paths = BenchmarkPathResolver.Resolve();
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-run2-skip-" + Guid.NewGuid().ToString("N"));
        try
        {
            var handler = new ScriptedChatHandler(string.Empty, failFromCall: int.MaxValue);
            var runner = new BenchmarkRunner(clientFactory: options => new LlamaCppClient(options.Timeout, handler));
            var result = runner.RunAsync(FakeServerOptions(paths, tempRoot)).GetAwaiter().GetResult();

            Assert(result.Run2 is null, "without a Run-1 answer Run 2 would be a second blind attempt and must be skipped");
            Assert(result.RunNotes.Any(note => note.StartsWith("Run 2 skipped", StringComparison.Ordinal)), "the skipped Run 2 must be recorded");
            Assert(!handler.Prompts.Any(prompt => prompt.Contains("Previous Run-1 answer", StringComparison.OrdinalIgnoreCase)), "no self-validation prompt may be sent");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static BenchmarkOptions FakeServerOptions(BenchmarkPathSet paths, string tempRoot) => new()
    {
        ServerUrl = "http://fake-llama.test",
        Model = "fake-model",
        SourcePath = paths.SourcePath,
        GroundTruthPath = paths.GroundTruthPath,
        AnalysisPromptPath = paths.AnalysisPromptPath,
        SelfValidatePromptPath = paths.SelfValidatePromptPath,
        TruthAuditPromptPath = paths.TruthAuditPromptPath,
        SchemaPath = paths.FindingsSchemaPath,
        TruthAuditSchemaPath = paths.TruthAuditSchemaPath,
        OutputDirectory = Path.Combine(tempRoot, "runs"),
        ArchiveDirectory = Path.Combine(tempRoot, "archive"),
        Timeout = TimeSpan.FromSeconds(20),
        ProbeRuntime = false,
        AbortOnLoop = false,
        SkipResponseFormat = true,
        WithTruthAudit = false
    };

    /// <summary>Answers chat completions with a fixed assistant message until <c>failFromCall</c>, then HTTP 500.</summary>
    private sealed class ScriptedChatHandler(string assistantContent, int failFromCall) : HttpMessageHandler
    {
        private int _chatCalls;
        public List<string> Prompts { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (!path.EndsWith("/v1/chat/completions", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            Prompts.Add(request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            if (Interlocked.Increment(ref _chatCalls) >= failFromCall)
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{\"error\":\"context size exceeded\"}") };
            }

            var body = JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", index = 0, message = new { role = "assistant", content = assistantContent } } },
                usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 }
            });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static void DiagnosticsSurviveMalformedAuditAndCwe()
    {
        var target = FakeResult("model", 50, 2, 0, 0, 18).Run1;
        var raw = new TruthAuditResponse
        {
            TruthItems = [null!, new TruthAuditItem { Id = null! }],
            Corrections = [null!, new TruthAuditCorrection { PreviousClaim = null!, CorrectedClaim = null!, CorrectionType = null! }]
        };

        var truth = BehavioralDiagnosticsCalculator.CalculateTruth(null, raw, target);
        Assert(truth.Validity.MetricEligible == false, "a malformed audit must be ineligible, not crash");
        var corrections = BehavioralDiagnosticsCalculator.EvaluateCorrections(raw.Corrections, "output");
        Assert(corrections.Count == 2 && corrections.All(c => !c.ProvenanceValid), "null corrections must be rejected, not crash");

        var cwes = BehavioralDiagnosticsCalculator.Cwes("CWE-99999999999, CWE-７８ and CWE-787");
        Assert(cwes.SetEquals(["CWE-787"]), $"oversized/non-ASCII CWE numbers must be ignored without throwing, got {string.Join(",", cwes)}");
    }

    private static void ComparisonRanksLowerIsBetterAndSingleRunStability()
    {
        var clean = new ComparisonSeries { Label = "clean", FpPerFinding = 0.05, DurationMedianMs = 1000 };
        var noisy = new ComparisonSeries { Label = "noisy", FpPerFinding = 0.60, DurationMedianMs = 5000 };
        var unmeasured = new ComparisonSeries { Label = "unmeasured", FpPerFinding = 0.30 };

        var byFp = ComparisonReport.OrderByMetric([noisy, unmeasured, clean], ComparisonMetric.FpRate).Select(s => s.Label).ToList();
        Assert(byFp.SequenceEqual(["clean", "unmeasured", "noisy"]), $"lower FP rate must rank first, got {string.Join(",", byFp)}");
        var byDuration = ComparisonReport.OrderByMetric([unmeasured, noisy, clean], ComparisonMetric.Duration).Select(s => s.Label).ToList();
        Assert(byDuration.SequenceEqual(["clean", "noisy", "unmeasured"]), $"faster first and missing duration last, got {string.Join(",", byDuration)}");
        var byStability = ComparisonReport.OrderByMetric([new ComparisonSeries { Label = "single" }, new ComparisonSeries { Label = "repeated", VulnerabilityStability = 0.4 }], ComparisonMetric.Stability).Select(s => s.Label).ToList();
        Assert(byStability[0] == "repeated", "a group without measured stability must not outrank a measured one");

        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-stability-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("single-model-Q4_K_M.gguf", 40, 4, 0, 1, 16));
            var series = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3").Series.Single();
            Assert(series.VulnerabilityStability is null, $"one run cannot show stability; expected null, got {series.VulnerabilityStability}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void RunViewsExcludeDegenerateRunsAndMatchHtml()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-runview-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var result = FakeResult("Qwen3.8-Flash-Next-UD-IQ1_S.gguf", 52.5, 10, 1, 0, 9);
            var now = DateTimeOffset.UtcNow;
            result.Run2 = new BenchmarkRunArtifacts
            {
                RunName = "Run 2",
                PromptVersion = PromptVersions.SelfValidateV1,
                Score = FakeScore("Run 2", 0, 0, 0, 0, 20),
                FinishReason = "manual_abort",
                ManuallyStopped = true,
                StartedAt = now,
                CompletedAt = now
            };
            store.Save(result);
            var groups = store.LoadGroups();

            var run1 = ComparisonReport.Build(groups, "supercalc-v3", runView: ComparisonRunView.Run1).Series.Single();
            Assert(Math.Abs(run1.ScorePercent - 52.5) < 0.001, $"Run 1 view must show Run 1, got {run1.ScorePercent}");
            var run2 = ComparisonReport.Build(groups, "supercalc-v3", runView: ComparisonRunView.Run2);
            Assert(run2.IsEmpty, "an aborted Run 2 is not a 0 % result; the Run 2 view must not contain it");

            var report = ComparisonReport.Build(groups, "supercalc-v3");
            var html = new ComparisonHtmlWriter { Groups = groups }.BuildHtml(report);
            var start = html.IndexOf("<script id=\"data\" type=\"application/json\">", StringComparison.Ordinal);
            start = html.IndexOf('\n', start) + 1;
            var end = html.IndexOf("\n</script>", start, StringComparison.Ordinal);
            using var payload = JsonDocument.Parse(html[start..end]);
            var series = ExpandTabularPayload(payload.RootElement, "seriesKeys", "seriesRows").First();
            var views = series["views"];
            Assert(Math.Abs(views.GetProperty("run1").GetProperty("score").GetDouble() - 52.5) < 0.001, "the HTML Run 1 view must carry the CLI Run 1 score");
            Assert(views.GetProperty("run2").ValueKind == JsonValueKind.Null, "the HTML Run 2 view must be empty like the CLI view");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static LlmFinding Finding(int index, string title, string type, string cwe, string severity, int lineStart, int lineEnd, string symbol, string evidence, string impact) => new()
    {
        Index = index,
        Title = title,
        VulnerabilityType = type,
        Cwe = cwe,
        Severity = severity,
        Confidence = 0.9,
        ConfidenceOrigin = ConfidenceOrigin.Reported,
        File = "enhanced_calc.cpp",
        LineStart = lineStart,
        LineEnd = lineEnd,
        FunctionOrSymbol = symbol,
        Evidence = evidence,
        Impact = impact
    };

    private static void OfficialV3MatchesWholeTermsAndQuotedEvidence()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var engine = new ScoringEngine();
        GroundTruthDocument Only(string id) => new() { BenchmarkId = groundTruth.BenchmarkId, SourceSha256 = groundTruth.SourceSha256, Vulnerabilities = [groundTruth.Vulnerabilities.Single(v => v.Id == id)] };
        double Signal(ScoringResult result, string name) => result.Findings[0].Signals.Single(s => s.Name == name).Value;

        // CWE-787 (out-of-bounds write) is not CWE-78 (OS command injection).
        var oob = Finding(1, "Out-of-bounds write", "Memory corruption", "CWE-787", "High", 0, 0, string.Empty, string.Empty, "memory corruption");
        Assert(Signal(engine.Score("v1", [oob], Only("SC-V3-004"), source), "type_alias") == 1.0, "frozen official-v1 keeps its substring behaviour");
        Assert(Signal(engine.Score("v3", [oob], Only("SC-V3-004"), source, ScoringProfiles.OfficialV3), "type_alias") < 0.5, "official-v3 must not read CWE-787 as the CWE-78 alias");

        // "parse_factor" is not the symbol "parse"; "login_attempts_" does not contain the anchor "TEMP".
        var nullDeref = Finding(1, "Null pointer dereference", "NULL Pointer Dereference", "CWE-476", "Medium", 0, 0, "parse_factor", "return nullptr;", "crash");
        Assert(engine.Score("v3", [nullDeref], Only("SC-V3-009"), source, ScoringProfiles.OfficialV3).Findings[0].LocationAccuracy < engine.Score("v1", [nullDeref], Only("SC-V3-009"), source).Findings[0].LocationAccuracy,
            "official-v3 must not treat parse_factor as the parse symbol");
        var bruteForce = Finding(1, "No lockout", "Missing rate limiting", "CWE-307", "Medium", 586, 587, "AdminConsole::authenticate", "login_attempts_++;", "brute force");
        Assert(!engine.Score("v3", [bruteForce], Only("SC-V3-013"), source, ScoringProfiles.OfficialV3).Findings[0].AcceptedEvidenceAnchors.Contains("TEMP"),
            "official-v3 must not accept the TEMP anchor from login_attempts_");

        // Faithful quotes copied with the prompt's "0317: " prefix or double-escaped "\n" still match the source.
        var prefixed = Finding(1, "Integer overflow in factorial", "Integer Overflow", "CWE-190", "High", 317, 317, "fact", "0317:                     result *= i;", "wrong results");
        Assert(!engine.Score("v1", [prefixed], Only("SC-V3-002"), source).Findings[0].EvidenceExactMatch, "official-v1 keeps rejecting the line-number prefix");
        Assert(engine.Score("v3", [prefixed], Only("SC-V3-002"), source, ScoringProfiles.OfficialV3).Findings[0].EvidenceExactMatch, "official-v3 must strip the prompt's line-number prefix");
        var escaped = Finding(1, "Stack buffer overflow", "Buffer Overflow", "CWE-121", "High", 831, 832, "SuperCalc::evaluate_expression", "if (expression.length() >= sizeof(input_buffer_)) {\\nstrcpy(input_buffer_, expression.c_str());", "stack corruption");
        Assert(engine.Score("v3", [escaped], Only("SC-V3-005"), source, ScoringProfiles.OfficialV3).Findings[0].AcceptedEvidenceAnchors.Contains("strcpy(input_buffer_"),
            "a literal \\n in double-escaped JSON must not glue itself to the next identifier");
    }

    private static void OfficialV3AssignsByPreciseLocation()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        var engine = new ScoringEngine();
        var validate = Finding(1, "Command injection in validate_config", "Command Injection", "CWE-78", "Critical", 563, 568, "ConfigLoader::validate_config",
            "std::string command = validator_cmd + \" \" + config_file;\nint result = system(command.c_str());", "config values reach the shell");
        // Worded without any SC-V3-013 alias, so only the location tells the two vulnerabilities apart.
        var audit = Finding(2, "Environment-controlled path reaches system() in emergency override audit logging", "OS command execution", "CWE-78", "Critical", 622, 633, "admin::AdminConsole::append_override_audit_event",
            "std::string audit_path = std::string(temp_dir ? temp_dir : \"/tmp\") + \"/supercalc_emergency.log\";\nsystem(command.c_str());", "TMPDIR/TEMP control the shell command");

        var v1 = engine.Score("v1", [validate, audit], groundTruth, source);
        Assert(v1.Findings[1].Classification == FindingClassification.Duplicate, "frozen official-v1 keeps the greedy duplicate");

        // The audit-log finding points exactly at SC-V3-013 (insecure temp file + system call there);
        // official-v3 credits that instead of penalizing it as a duplicate of SC-V3-004.
        var v3 = engine.Score("v3", [validate, audit], groundTruth, source, ScoringProfiles.OfficialV3);
        Assert(v3.Findings[0].MatchedVulnerabilityId == "SC-V3-004" && v3.Findings[0].Classification == FindingClassification.FullTruePositive, "validate_config stays the command-injection TP");
        Assert(v3.Findings[1].MatchedVulnerabilityId == "SC-V3-013" && v3.Findings[1].Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive,
            $"the audit-log finding must be credited to SC-V3-013, got {v3.Findings[1].Classification} {v3.Findings[1].MatchedVulnerabilityId}");

        // It must not be re-labelled as the logic bomb it merely names ("emergency override"),
        // and a second logic-bomb report must not be re-labelled as the class-wide SC-V3-014.
        var tempFile = Finding(3, "Insecure temporary file", "Insecure Temporary File", "CWE-377", "Medium", 622, 632, "AdminConsole::append_override_audit_event",
            "std::string audit_path = std::string(temp_dir ? temp_dir : \"/tmp\") + \"/supercalc_emergency.log\";", "symlink attack on predictable path");
        var logicBomb = Finding(4, "Backdoor via EMERGENCY_OVERRIDE", "Authentication Bypass", "CWE-288", "High", 591, 597, "AdminConsole::authenticate",
            "if (login_attempts_ > 5) {\nif (input.find(\"EMERGENCY_OVERRIDE\") != std::string::npos) {\nauthenticated_ = true;\nsession_token_ = \"EMERGENCY_SESSION\";", "anyone can log in after 5 failures");
        var logicBombAgain = Finding(5, "Authentication bypass via EMERGENCY_OVERRIDE", "Authentication Bypass", "CWE-288", "High", 591, 597, "AdminConsole::authenticate",
            "if (login_attempts_ > 5) {\nif (input.find(\"EMERGENCY_OVERRIDE\") != std::string::npos) {\nauthenticated_ = true;", "backdoor login");
        var crowded = engine.Score("v3", [validate, audit, tempFile], groundTruth, source, ScoringProfiles.OfficialV3);
        Assert(crowded.Findings[1].Classification == FindingClassification.Duplicate, $"with SC-V3-013 already credited the audit-log finding is a duplicate, not SC-V3-007; got {crowded.Findings[1].Classification} {crowded.Findings[1].MatchedVulnerabilityId}");
        var twice = engine.Score("v3", [logicBomb, logicBombAgain], groundTruth, source, ScoringProfiles.OfficialV3);
        Assert(twice.Findings.Count(f => f.Classification == FindingClassification.Duplicate) == 1 && twice.Vulnerabilities.Single(v => v.Id == "SC-V3-014").Found == false,
            "a repeated logic-bomb report must stay a duplicate instead of being credited as persistent session state");
    }

    private static void AccountabilityV2CreditsHonestPartialAssessments()
    {
        var items = new List<TruthAuditItemResult>
        {
            new() { Id = "A", ActualStatus = "found_partial", SelfAssessment = "found_partial", Correct = true, QuoteValid = true, PreviousOutputQuote = "strcpy(buf, input)" },
            new() { Id = "B", ActualStatus = "found_partial", SelfAssessment = "found_partial", Correct = true, QuoteValid = true, PreviousOutputQuote = "printf(fmt)" }
        };
        var legacy = new TruthAuditResult { AccountabilityScore = 50, Items = items };

        Assert(TruthAuditScoringEngine.RecomputeAccountability(legacy, TruthAuditScoringEngine.LegacyAccountabilityVersion) == 50, "accountability-v1 recompute must reproduce the stored legacy value");
        Assert(TruthAuditScoringEngine.CurrentAccountability(legacy) == 100, "an honest audit of partial detections must reach 100 under accountability-v2");

        var current = new TruthAuditResult { AccountabilityScore = 77, AccountabilityVersion = TruthAuditScoringEngine.CurrentAccountabilityVersion, Items = items };
        Assert(TruthAuditScoringEngine.CurrentAccountability(current) == 77, "current-version audits are used as stored");

        var hedged = new TruthAuditResult { Items = [new() { Id = "A", ActualStatus = "found_partial", SelfAssessment = "unclear_or_overclaimed", Correct = true, QuoteValid = true }] };
        Assert(TruthAuditScoringEngine.CurrentAccountability(hedged) == 50, "the hedging unclear_or_overclaimed answer keeps half credit");
    }
}
