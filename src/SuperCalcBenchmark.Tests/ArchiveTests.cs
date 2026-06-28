using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

// Tests for the run-archive and model-comparison feature. Kept in a separate partial-class
// file so the core TestRunner stays readable; registration happens in TestRunner.Main.
internal static partial class TestRunner
{
    private static readonly string[] FakeVulnIds = ["SC-V3-001", "SC-V3-002", "SC-V3-003", "SC-V3-004"];

    private static void ModelIdentityDetectsQuantAndFamily()
    {
        var a = ModelIdentity.Parse("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf");
        Assert(a.Quant == "Q4_K_M", $"expected Q4_K_M, got {a.Quant}");
        Assert(a.QuantWasDetected, "quant should be auto-detected");
        Assert(a.Family == "qwen3-coder-30b-a3b-instruct", $"unexpected family: {a.Family}");

        var b = ModelIdentity.Parse("/models/Qwen3-Coder-30B-A3B-Instruct-IQ3_XXS.gguf");
        Assert(b.Quant == "IQ3_XXS", $"expected IQ3_XXS, got {b.Quant}");
        // Different quant of the same model must share a family (so they line up in a
        // comparison) but produce distinct group keys.
        Assert(b.Family == a.Family, "same model, different quant should share a family");
        Assert(a.GroupKey != b.GroupKey, "different quants must produce different group keys");

        var f16 = ModelIdentity.Parse("Phi-3-medium-128k-instruct.FP16.gguf");
        Assert(f16.Quant == "F16", $"FP16 should normalize to F16, got {f16.Quant}");

        var none = ModelIdentity.Parse("some-model-without-quant");
        Assert(!none.QuantWasDetected, "no quant token should be detected");
        Assert(none.Quant == ModelIdentity.UnknownQuant, "missing quant should fall back to unknown-quant");
    }

    private static void ModelIdentityHonorsQuantOverride()
    {
        // When the id encodes no quant, a manual override must win.
        var overridden = ModelIdentity.Parse("internal-alias-model", "Q5_K_M");
        Assert(overridden.Quant == "Q5_K_M", $"override should be used, got {overridden.Quant}");
        Assert(overridden.GroupKey.Contains("Q5_K_M", StringComparison.Ordinal), "group key should reflect the override");
    }

    private static void ArchiveStoreUpdatesEditableIdentityFields()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-archive-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var originalPath = store.Save(FakeResult("local-server-alias", 42, 4, 0, 1, 0));
            var group = store.LoadGroups().Single();

            var updatedPaths = store.UpdateIdentity(group.Records, "qwen3-coder-30b", "Q4_K_M");
            Assert(updatedPaths.Count == 1, $"expected one updated scorecard, got {updatedPaths.Count}");
            Assert(!File.Exists(originalPath), "scorecard should be moved out of the stale unknown-quant folder");
            Assert(File.Exists(updatedPaths[0]), "updated scorecard should exist at the new group path");
            Assert(updatedPaths[0].Contains("qwen3-coder-30b__Q4_K_M", StringComparison.Ordinal), "updated path should contain the new group key");

            var updatedGroup = store.LoadGroups().Single();
            Assert(updatedGroup.ModelFamily == "qwen3-coder-30b", $"model family should update, got {updatedGroup.ModelFamily}");
            Assert(updatedGroup.Quant == "Q4_K_M", $"quant should update, got {updatedGroup.Quant}");
            Assert(updatedGroup.GroupKey == "qwen3-coder-30b__Q4_K_M", $"group key should update, got {updatedGroup.GroupKey}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveManualQuantEditRebuildsGroupKey()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-archive-edit-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var path = store.Save(FakeResult("local-server-alias", 42, 4, 0, 1, 0));
            var json = File.ReadAllText(path);
            Assert(json.Contains("\"quant\": \"unknown-quant\"", StringComparison.Ordinal), "fixture should start as unknown quant");
            Assert(json.Contains("\"groupKey\": \"local-server-alias__unknown-quant\"", StringComparison.Ordinal), "fixture should start with stale unknown group key");

            // Simulate a user fixing only the visible quant field in the archived scorecard.
            // They should not also need to update groupKey or move the JSON to another folder.
            json = json.Replace("\"quant\": \"unknown-quant\"", "\"quant\": \"Q5_K_M\"", StringComparison.Ordinal);
            File.WriteAllText(path, json);

            var record = store.LoadAll().Single();
            Assert(record.Quant == "Q5_K_M", $"manual quant should load, got {record.Quant}");
            Assert(record.GroupKey == "local-server-alias__Q5_K_M", $"group key should be rebuilt, got {record.GroupKey}");

            var group = store.LoadGroups().Single();
            Assert(group.Quant == "Q5_K_M", $"comparison group should use manual quant, got {group.Quant}");
            Assert(group.GroupKey == "local-server-alias__Q5_K_M", $"comparison group key should use manual quant, got {group.GroupKey}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveDuplicateRunNamesDoNotClobber()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-archive-duplicate-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var result = FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 7, 0, 0, 3);

            var firstPath = store.Save(result);
            var secondPath = store.Save(result);

            Assert(!string.Equals(firstPath, secondPath, StringComparison.OrdinalIgnoreCase), "same-timestamp scorecards must get unique paths");
            Assert(File.Exists(firstPath), "first scorecard should still exist");
            Assert(File.Exists(secondPath), "second scorecard should exist");

            var group = store.LoadGroups().Single();
            Assert(group.RunCount == 2, $"duplicate-name runs should stack in one group, got {group.RunCount}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveManualModelRenameMergesGroups()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-archive-rename-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var aliasPath = store.Save(FakeResult("local-qwen-alias", 66, 6, 0, 1, 4));
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 78, 8, 0, 0, 2));

            var json = File.ReadAllText(aliasPath);
            json = json.Replace("\"modelFamily\": \"local-qwen-alias\"", "\"modelFamily\": \"qwen3-coder-30b\"", StringComparison.Ordinal);
            json = json.Replace("\"quant\": \"unknown-quant\"", "\"quant\": \"Q4_K_M\"", StringComparison.Ordinal);
            File.WriteAllText(aliasPath, json);

            var groups = store.LoadGroups();
            var merged = groups.SingleOrDefault(g => g.ModelFamily == "qwen3-coder-30b" && g.Quant == "Q4_K_M");
            Assert(merged is not null, "manual model/quant rename should create the qwen Q4 group");
            Assert(merged!.RunCount == 2, $"renamed alias should merge with existing qwen Q4 runs, got {merged.RunCount}");
            Assert(merged.GroupKey == "qwen3-coder-30b__Q4_K_M", $"group key should be rebuilt from edited metadata, got {merged.GroupKey}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveRoundTripsAndGroups()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-archive-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);

            store.Save(FakeResult("Qwen3-Coder-30B-Instruct-Q4_K_M.gguf", 70, 7, 1, 1, 2));
            store.Save(FakeResult("Qwen3-Coder-30B-Instruct-Q4_K_M.gguf", 74, 8, 0, 0, 2));
            store.Save(FakeResult("Qwen3-Coder-30B-Instruct-IQ3_XXS.gguf", 61, 6, 1, 2, 3));
            store.Save(FakeResult("Meta-Llama-3.1-8B-Instruct-Q8_0.gguf", 52, 5, 0, 3, 5));

            var all = store.LoadAll();
            Assert(all.Count == 4, $"expected 4 archived records, got {all.Count}");

            var groups = store.LoadGroups();
            Assert(groups.Count == 3, $"expected 3 model/quant groups, got {groups.Count}");

            var q4 = groups.Single(g => g.Quant == "Q4_K_M" && g.ModelFamily.Contains("qwen3-coder-30b"));
            Assert(q4.RunCount == 2, $"Q4_K_M group should hold 2 runs, got {q4.RunCount}");
            Assert(Math.Abs(q4.AverageScorePercent - 72.0) < 0.001, $"avg should be 72, got {q4.AverageScorePercent}");
            Assert(Math.Abs(q4.MedianScorePercent - 72.0) < 0.001, $"median should be 72, got {q4.MedianScorePercent}");
            Assert(Math.Abs(q4.BestScorePercent - 74.0) < 0.001, $"best should be 74, got {q4.BestScorePercent}");
            Assert(Math.Abs(q4.MinScorePercent - 70.0) < 0.001, $"min should be 70, got {q4.MinScorePercent}");
            Assert(Math.Abs(q4.MaxScorePercent - 74.0) < 0.001, $"max should be 74, got {q4.MaxScorePercent}");
            Assert(q4.ScoreStdDev > 0, "stddev should be positive for two different runs");

            var families = store.LoadFamilies();
            Assert(families.Count == 2, $"expected 2 distinct families, got {families.Count}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ComparisonAggregatesAndFiltersByFamily()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-compare-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 7, 0, 0, 3));
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 80, 8, 0, 0, 2));
            store.Save(FakeResult("Qwen3-Coder-30B-IQ3_XXS.gguf", 60, 6, 0, 1, 4));
            store.Save(FakeResult("Gemma-2-27B-Q5_K_M.gguf", 55, 5, 0, 2, 5));

            var groups = store.LoadGroups();

            var avg = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Average);
            Assert(avg.Series.Count == 3, $"expected 3 series, got {avg.Series.Count}");
            var q4avg = avg.Series.Single(s => s.Quant == "Q4_K_M");
            Assert(Math.Abs(q4avg.ScorePercent - 75.0) < 0.001, $"Q4 average should be 75, got {q4avg.ScorePercent}");
            Assert(Math.Abs(q4avg.ScoreMedian - 75.0) < 0.001, $"Q4 median distribution should be 75, got {q4avg.ScoreMedian}");
            Assert(Math.Abs(q4avg.ScoreMin - 70.0) < 0.001, $"Q4 min should be 70, got {q4avg.ScoreMin}");
            Assert(Math.Abs(q4avg.ScoreMax - 80.0) < 0.001, $"Q4 max should be 80, got {q4avg.ScoreMax}");
            Assert(q4avg.ScoreStdDev > 0, "Q4 stddev should be positive");
            Assert(avg.Series[0].ScorePercent >= avg.Series[^1].ScorePercent, "series should be sorted by score desc");

            var median = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Median);
            var q4median = median.Series.Single(s => s.Quant == "Q4_K_M");
            Assert(Math.Abs(q4median.ScorePercent - 75.0) < 0.001, $"Q4 median should be 75, got {q4median.ScorePercent}");

            var best = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Best);
            var q4best = best.Series.Single(s => s.Quant == "Q4_K_M");
            Assert(Math.Abs(q4best.ScorePercent - 80.0) < 0.001, $"Q4 best should be 80, got {q4best.ScorePercent}");

            var qwenOnly = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Average, "qwen3-coder-30b");
            Assert(qwenOnly.Series.Count == 2, $"qwen filter should yield 2 quants, got {qwenOnly.Series.Count}");
            Assert(qwenOnly.Series.All(s => s.ModelFamily == "qwen3-coder-30b"), "filtered series must all be qwen");

            Assert(avg.VulnerabilityAxis.Count == FakeVulnIds.Length, $"axis should cover {FakeVulnIds.Length} vulns, got {avg.VulnerabilityAxis.Count}");
            Assert(q4avg.PerVulnerabilityCredit.Count == avg.VulnerabilityAxis.Count, "per-vuln credit must align to the axis length");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ComparisonHtmlEmbedsParseablePayload()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-html-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 7, 0, 0, 3, includeReasoning: true));

            var report = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3");
            var writer = new ComparisonHtmlWriter();
            var html = writer.BuildHtml(report);

            Assert(html.Contains("barChart", StringComparison.Ordinal), "html should contain the bar chart canvas");
            Assert(html.Contains("radarChart", StringComparison.Ordinal), "html should contain the radar chart canvas");
            Assert(html.Contains("reasoningChart", StringComparison.Ordinal), "html should include the Denken-vs-Sagen chart when diagnostics exist");
            Assert(html.Contains("heatmap", StringComparison.Ordinal), "html should include the vulnerability heatmap");
            Assert(html.Contains("openMetricModal", StringComparison.Ordinal), "html should include maximizable metric-card modal code");
            Assert(html.Contains("openHelpPopover", StringComparison.Ordinal), "html should include metric help popover code");
            Assert(html.Contains("aria-modal=\"true\"", StringComparison.Ordinal), "metric overlays should expose ARIA modal attributes");
            Assert(html.Contains("data-help-metric", StringComparison.Ordinal), "metric headings should include help buttons");

            const string open = "<script id=\"data\" type=\"application/json\">";
            var start = html.IndexOf(open, StringComparison.Ordinal);
            Assert(start >= 0, "html should contain the data island");
            start += open.Length;
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            var json = html[start..end].Trim();
            using var doc = JsonDocument.Parse(json);
            Assert(doc.RootElement.GetProperty("series").GetArrayLength() == 1, "payload should contain one series");
            Assert(doc.RootElement.TryGetProperty("axis", out _), "payload should expose vulnerability metadata axis");
            var series = doc.RootElement.GetProperty("series")[0];
            Assert(series.TryGetProperty("scoreMedian", out _), "payload should expose scoreMedian for uncertainty tables");
            Assert(series.TryGetProperty("scoreMin", out _), "payload should expose scoreMin for uncertainty bars");
            Assert(series.TryGetProperty("scoreMax", out _), "payload should expose scoreMax for uncertainty bars");
            Assert(series.TryGetProperty("criticalRecall", out _), "payload should expose severity metrics");
            Assert(series.TryGetProperty("parseSuccessRate", out _), "payload should expose parse/completion health metrics");
            Assert(series.TryGetProperty("evidenceFidelity", out _), "payload should expose evidence fidelity drilldown metrics");
            Assert(series.TryGetProperty("hallucinationRate", out _), "payload should expose hallucination drilldown metrics");
            Assert(series.TryGetProperty("falsePositiveTaxonomy", out _), "payload should expose FP taxonomy drilldown data");
            Assert(series.TryGetProperty("thinkingTp", out _), "payload should expose Denken/Gedacht TP statistics");
            Assert(series.TryGetProperty("outputTp", out _), "payload should expose Sagen/Gesagt TP statistics");
            Assert(series.GetProperty("visibleReasoningRuns").GetInt32() == 1, "payload should count visible reasoning runs");

            var csv = writer.BuildCsv(report);
            Assert(csv.Contains("model_family", StringComparison.Ordinal), "csv should have a header row");
            Assert(csv.Contains("critical_recall_percent", StringComparison.Ordinal), "csv should include severity metric columns");
            Assert(csv.Contains("thinking_tp", StringComparison.Ordinal), "csv should include Denken-vs-Sagen columns");
            Assert(csv.Contains("hallucination_rate_percent", StringComparison.Ordinal), "csv should include hallucination metric columns");
            Assert(csv.Contains("fp_taxonomy", StringComparison.Ordinal), "csv should include FP taxonomy columns");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveAndComparisonExposeTruthAuditMetrics()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-truth-audit-archive-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var result = FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 2, 0, 1, 2);
            var now = DateTimeOffset.UtcNow;
            result.Run3 = new BenchmarkRunArtifacts
            {
                RunName = "Run 3",
                RunKind = "truth_audit",
                GroundTruthVisibleToModel = true,
                PromptVersion = PromptVersions.TruthAuditV1,
                StartedAt = now,
                CompletedAt = now,
                Response = "{\"summary\":\"ok\"}",
                TruthAudit = new TruthAuditResult
                {
                    AuditedRunName = "Run 1",
                    AuditedRunScoreProfile = ScoringProfiles.OfficialV1Name,
                    AuditedRunScorePercent = 70,
                    AccountabilityScore = 88,
                    TruthAuditAccuracy = 0.9,
                    OverclaimRate = 0.1,
                    MissAdmissionRate = 0.8,
                    FalsePositiveAdmissionRate = 1.0,
                    EvidenceLaunderingCount = 1,
                    QuoteFidelity = 0.95
                },
                Score = new ScoringResult { RunName = "Run 3", PromptVersion = PromptVersions.TruthAuditV1, ScorePercent = 88, RawPoints = 88 }
            };

            store.Save(result);
            var record = store.LoadAll().Single();
            Assert(record.Runs.Count == 2, "archive should store primary run and truth-audit run");
            Assert(record.PrimaryRun?.RunName == "Run 1", "truth audit must not become the primary detection run");
            var auditRun = record.Runs.Single(r => r.RunKind == "truth_audit");
            Assert(auditRun.GroundTruthVisibleToModel, "truth-audit archive run must mark ground truth as visible");
            Assert(auditRun.TruthAudit?.AccountabilityScore == 88, "truth-audit metrics should be archived");

            var report = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3");
            var series = report.Series.Single();
            Assert(series.TruthAuditRunCount == 1, "comparison should aggregate truth-audit run count");
            Assert(Math.Abs(series.AccountabilityScore - 88) < 0.001, $"accountability should aggregate, got {series.AccountabilityScore}");
            var html = new ComparisonHtmlWriter().BuildHtml(report);
            Assert(html.Contains("accountabilityScore", StringComparison.Ordinal), "HTML payload should expose accountability score");
            Assert(new ComparisonHtmlWriter().BuildCsv(report).Contains("accountability_score", StringComparison.Ordinal), "CSV should expose accountability score");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveLoadsV1ScorecardsWithV2Fallbacks()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-v1-load-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(tempRoot, "supercalc-v3", "legacy-model__Q4_K_M");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "legacy.json"), """
{
  "schemaVersion": 1,
  "recordId": "legacy",
  "benchmarkId": "supercalc-v3",
  "toolVersion": "test",
  "rawModelId": "legacy-model-Q4_K_M.gguf",
  "modelFamily": "legacy-model",
  "quant": "Q4_K_M",
  "groupKey": "legacy-model__Q4_K_M",
  "sourceSha256": "deadbeef",
  "sourceHashMatches": true,
  "startedAt": "2026-01-01T00:00:00Z",
  "completedAt": "2026-01-01T00:00:01Z",
  "runs": [
    {
      "runName": "Run 1",
      "scorePercent": 50,
      "rawPoints": 10,
      "fullTruePositives": 1,
      "partialTruePositives": 1,
      "falsePositives": 0,
      "missed": 2,
      "precision": 1,
      "recall": 0.5,
      "f1": 0.66,
      "vulnerabilityCredit": { "SC-V3-001": 1.0, "SC-V3-002": 0.5, "SC-V3-003": 0.0 }
    }
  ]
}
""", System.Text.Encoding.UTF8);

            var record = new ArchiveStore(tempRoot).LoadAll().Single();
            Assert(record.SchemaVersion == 1, "legacy schema version should be preserved on load");
            Assert(record.GroupKey == "legacy-model__Q4_K_M", "group key should be rebuilt from editable identity fields");
            var run = record.Runs.Single();
            Assert(run.VulnerabilityResults.Count == 3, "v1 vulnerabilityCredit should synthesize v2 vulnerabilityResults");
            Assert(run.VulnerabilityResults.Single(v => v.Id == "SC-V3-002").Status == "partial", "partial credit should synthesize partial status");
            Assert(run.ParseMode == "unknown", "missing v1 parse mode should normalize to unknown");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveV2StoresCompletionAndParseDiagnostics()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-v2-diagnostics-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var result = FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 2, 0, 1, 2, includeReasoning: true);
            var runStarted = DateTimeOffset.UtcNow;
            var runCompleted = runStarted.AddSeconds(2);
            result.Run1 = new BenchmarkRunArtifacts
            {
                RunName = "Run 1",
                StartedAt = runStarted,
                CompletedAt = runCompleted,
                Prompt = "prompt",
                Response = string.Empty,
                ReasoningContent = "visible thought",
                RawResponse = "{\"raw\":true}",
                RequestJson = "{\"request\":true}",
                FinishReason = "length",
                LoopDetected = true,
                LoopDiagnosticsSummary = "repeated reasoning",
                UsedResponseFormat = true,
                RetriedWithoutResponseFormat = true,
                UsedThinkingControl = true,
                RetriedWithoutThinkingControl = false,
                Parse = new ParseResult { ParsedJson = true, UsedMarkdownJsonBlock = true, ParseMode = "markdown_json" },
                Score = result.Run1.Score,
                ReasoningDisclosure = result.Run1.ReasoningDisclosure
            };

            var path = store.Save(result);
            var record = store.LoadAll().Single();
            Assert(record.SchemaVersion == ArchiveRecord.CurrentSchemaVersion, "new archives should use current schema version");
            Assert(record.TimeoutSeconds == BenchmarkDefaults.OfficialRequestTimeoutSeconds, "request timeout should be archived for slow-model diagnostics");
            Assert(File.ReadAllText(path).Contains("\"schemaVersion\": 3", StringComparison.Ordinal), "saved JSON should declare schema v3");
            var run = record.Runs.Single();
            Assert(run.FinishReason == "length", "finish reason should be archived");
            Assert(run.LoopDetected, "loop flag should be archived");
            Assert(run.EmptyOutputWithReasoning, "empty output with reasoning should be diagnosed");
            Assert(run.ParseMode == "markdown_json", "parse mode should be archived");
            Assert(run.DurationMs >= 2000, "per-run duration should be archived");
            Assert(run.PromptChars == "prompt".Length, "prompt character count should be archived without storing the prompt");
            Assert(run.VulnerabilityResults.Count == FakeVulnIds.Length, "v2 scorecards should include rich vulnerability results");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveStoresOfficialV1ScoreMetadata()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-score-version-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            var path = store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 2, 0, 1, 2));
            var record = store.LoadAll().Single();
            var run = record.Runs.Single();

            Assert(record.SchemaVersion == ArchiveRecord.CurrentSchemaVersion, "new scorecards should use the current archive schema");
            Assert(run.ScoreSchemaVersion == ScoringProfiles.ScoreSchemaVersion, "run should include score schema version");
            Assert(run.ScoringProfile == ScoringProfiles.OfficialV1Name, $"new run should use official-v1, got {run.ScoringProfile}");
            Assert(run.ScoringProfileVersion == ScoringProfiles.OfficialV1Version, "official-v1 profile version should be archived");
            Assert(run.ScoringEngineVersion == ScoringProfiles.OfficialV1EngineVersion, "engine freeze id should be archived");
            Assert(run.ParserVersion == ResponseParser.CurrentParserVersion, "parser version should be archived");
            Assert(run.PromptVersion == PromptVersions.AnalysisV1, "Run 1 prompt version should be archived");
            Assert(run.SourceSha256 == "deadbeef", "source hash should be copied to the run score");
            Assert(run.OfficialComparable, "normal official fake run should be comparable");
            Assert(record.ScoreVersions.Count == 1, "scoreVersions should index the archived run score");
            Assert(record.ScoreVersions[0].Profile == ScoringProfiles.OfficialV1Name, "scoreVersions should carry the profile");
            Assert(File.ReadAllText(path).Contains("\"scoreVersions\"", StringComparison.Ordinal), "scoreVersions should be serialized");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveStoresRepeatGroupMetadata()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-repeat-metadata-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 2, 0, 1, 2, repeatGroupId: "repeat-test", repeatIndex: 2, repeatCount: 5));
            var record = store.LoadAll().Single();
            Assert(record.RepeatGroupId == "repeat-test", "repeatGroupId should be archived");
            Assert(record.RepeatIndex == 2, $"repeatIndex should be archived, got {record.RepeatIndex}");
            Assert(record.RepeatCount == 5, $"repeatCount should be archived, got {record.RepeatCount}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ArchiveMigrationVersionsLegacyScores()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-score-migration-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var directory = Path.Combine(tempRoot, "supercalc-v3", "legacy-model__Q4_K_M");
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "legacy.json");
            File.WriteAllText(path, """
{
  "schemaVersion": 2,
  "recordId": "legacy",
  "benchmarkId": "supercalc-v3",
  "benchmarkProfile": "official",
  "toolVersion": "test",
  "rawModelId": "legacy-model-Q4_K_M.gguf",
  "modelFamily": "legacy-model",
  "quant": "Q4_K_M",
  "groupKey": "legacy-model__Q4_K_M",
  "sourceSha256": "deadbeef",
  "sourceHashMatches": true,
  "startedAt": "2026-01-01T00:00:00Z",
  "completedAt": "2026-01-01T00:00:01Z",
  "runs": [
    {
      "runName": "Run 1",
      "scorePercent": 50,
      "rawPoints": 10,
      "fullTruePositives": 1,
      "partialTruePositives": 1,
      "falsePositives": 0,
      "missed": 2,
      "precision": 1,
      "recall": 0.5,
      "f1": 0.66,
      "vulnerabilityCredit": { "SC-V3-001": 1.0, "SC-V3-002": 0.5, "SC-V3-003": 0.0 }
    }
  ]
}
""", System.Text.Encoding.UTF8);

            var dryRun = new ArchiveStore(tempRoot).MigrateScores(new ArchiveMigrationOptions
            {
                AssumedProfile = ScoringProfiles.OfficialV1Name,
                Write = false,
                GroundTruthSha256 = "gt-hash",
                SourceSha256 = "deadbeef"
            });
            Assert(dryRun.FilesChanged == 1, "dry-run should detect the legacy scorecard");
            Assert(!File.ReadAllText(path).Contains("scoringProfile", StringComparison.Ordinal), "dry-run must not write changes");

            var backup = Path.Combine(tempRoot, "_migration-backup", "test");
            var written = new ArchiveStore(tempRoot).MigrateScores(new ArchiveMigrationOptions
            {
                AssumedProfile = ScoringProfiles.OfficialV1Name,
                Write = true,
                BackupDirectory = backup,
                GroundTruthSha256 = "gt-hash",
                SourceSha256 = "deadbeef"
            });
            Assert(written.FilesWritten == 1, "write mode should update one scorecard");
            Assert(written.RunsMigrated == 1, "one run should be legacy-migrated");
            Assert(Directory.EnumerateFiles(backup, "*.json", SearchOption.AllDirectories).Any(), "migration should create a backup copy");

            var record = new ArchiveStore(tempRoot).LoadAll().Single();
            var run = record.Runs.Single();
            Assert(run.ScorePercent == 50 && run.RawPoints == 10, "migration must not change point values");
            Assert(run.ScoringProfile == ScoringProfiles.OfficialV1Name, "legacy score should be marked official-v1");
            Assert(run.IsLegacyMigrated, "run should be marked legacy-migrated");
            Assert(run.GroundTruthSha256 == "gt-hash", "ground-truth hash should be filled when supplied");
            Assert(record.ScoreVersions.Single().Source == "legacy_migrated", "scoreVersions should mark legacy migration source");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ComparisonFiltersByScoringProfile()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-profile-filter-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 80, 2, 0, 0, 2));

            var legacyDirectory = Path.Combine(tempRoot, "supercalc-v3", "legacy__Q5_K_M");
            Directory.CreateDirectory(legacyDirectory);
            File.WriteAllText(Path.Combine(legacyDirectory, "legacy.json"), """
{
  "schemaVersion": 2,
  "recordId": "legacy-filter",
  "benchmarkId": "supercalc-v3",
  "rawModelId": "legacy-Q5_K_M.gguf",
  "modelFamily": "legacy",
  "quant": "Q5_K_M",
  "sourceHashMatches": true,
  "startedAt": "2026-01-01T00:00:00Z",
  "completedAt": "2026-01-01T00:00:01Z",
  "runs": [
    { "runName": "Run 1", "scorePercent": 40, "rawPoints": 8, "vulnerabilityCredit": { "SC-V3-001": 1.0 } }
  ]
}
""", System.Text.Encoding.UTF8);

            var all = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3");
            Assert(all.Series.Count == 2, $"unfiltered comparison should include both groups, got {all.Series.Count}");

            var officialV1 = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3", scoringProfile: ScoringProfiles.OfficialV1Name);
            Assert(officialV1.Series.Count == 1, $"official-v1 filter should include only native official-v1 runs, got {officialV1.Series.Count}");
            Assert(officialV1.Series.Single().Quant == "Q4_K_M", "profile filter should exclude legacy-unknown scorecards");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ComparisonMetadataDeltaAndStabilityMetricsWork()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-metadata-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var metadataPath = WriteFakeMetadata(tempRoot);
            var metadata = VulnerabilityMetadataIndex.Load(metadataPath);
            var store = new ArchiveStore(Path.Combine(tempRoot, "archive"));
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 25, 1, 0, 0, 3));
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 50, 2, 0, 0, 2));

            var report = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3", ComparisonAggregate.Average, metadataIndex: metadata);
            var series = report.Series.Single();
            Assert(Math.Abs(series.CriticalRecall - 0.5) < 0.001, $"critical recall should average critical credits, got {series.CriticalRecall}");
            Assert(Math.Abs(series.HighRecall - 0.5) < 0.001, $"high recall should be 0.5, got {series.HighRecall}");
            Assert(Math.Abs(series.VulnerabilityStability - 0.75) < 0.001, $"stability should reflect per-vuln volatility, got {series.VulnerabilityStability}");

            var withRun2 = FakeResult("Delta-Model-Q5_K_M.gguf", 25, 1, 0, 2, 3);
            withRun2.Run2 = new BenchmarkRunArtifacts
            {
                RunName = "Run 2",
                Score = FakeScore("Run 2", 50, 2, 0, 1, 2)
            };
            store.Save(withRun2);

            var delta = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3", ComparisonAggregate.Average, metadataIndex: metadata, runView: ComparisonRunView.Delta);
            var deltaSeries = delta.Series.Single(s => s.Quant == "Q5_K_M");
            Assert(Math.Abs(deltaSeries.ScorePercent - 25) < 0.001, $"delta score should be 25, got {deltaSeries.ScorePercent}");
            Assert(Math.Abs(deltaSeries.Run2ScoreDelta - 25) < 0.001, $"pair metric delta should be 25, got {deltaSeries.Run2ScoreDelta}");
            Assert(Math.Abs(deltaSeries.Run2FpReduction - 1) < 0.001, $"FP reduction should be 1, got {deltaSeries.Run2FpReduction}");
            Assert(deltaSeries.Run2AddedTruePositiveIds.Contains("SC-V3-002", StringComparer.OrdinalIgnoreCase), "Run 2 should add SC-V3-002");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static string WriteFakeMetadata(string root)
    {
        var path = Path.Combine(root, "ground_truth.json");
        Directory.CreateDirectory(root);
        File.WriteAllText(path, """
{
  "benchmark_id": "supercalc-v3",
  "source_file": "enhanced_calc.cpp",
  "source_sha256": "deadbeef",
  "policy": { "hidden_from_model": true },
  "vulnerabilities": [
    { "id": "SC-V3-001", "title": "critical injection", "severity": "Critical", "cwe": ["CWE-134"], "strict_scoreable": true, "locations": [{ "file": "enhanced_calc.cpp", "symbol": "string_utils::log_debug_message", "line_start": 1, "line_end": 1 }] },
    { "id": "SC-V3-002", "title": "high numeric", "severity": "High", "cwe": ["CWE-190"], "strict_scoreable": true, "locations": [{ "file": "enhanced_calc.cpp", "symbol": "math_engine::FunctionRegistry::fact", "line_start": 1, "line_end": 1 }] },
    { "id": "SC-V3-003", "title": "critical memory", "severity": "Critical", "cwe": ["CWE-416"], "strict_scoreable": true, "locations": [{ "file": "enhanced_calc.cpp", "symbol": "memory::MemoryPool::cleanup", "line_start": 1, "line_end": 1 }] },
    { "id": "SC-V3-004", "title": "low division", "severity": "Low", "cwe": ["CWE-369"], "strict_scoreable": true, "locations": [{ "file": "enhanced_calc.cpp", "symbol": "calculator::SuperCalc::run", "line_start": 1, "line_end": 1 }] }
  ]
}
""", System.Text.Encoding.UTF8);
        return path;
    }

    // Minimal but realistic BenchmarkRunResult with a single primary run whose per-vulnerability
    // credit is derived from the requested TP/partial/missed counts.
    private static BenchmarkRunResult FakeResult(string model, double scorePercent, int fullTp, int partialTp, int fp, int missed, bool includeReasoning = false, string repeatGroupId = "", int repeatIndex = 1, int repeatCount = 1)
    {
        var score = FakeScore("Run 1", scorePercent, fullTp, partialTp, fp, missed);
        var reasoningDisclosure = FakeReasoningDisclosure(fullTp, partialTp, fp, includeReasoning);
        var now = DateTimeOffset.Now;
        return new BenchmarkRunResult
        {
            BenchmarkId = "supercalc-v3",
            BenchmarkProfile = "official",
            ToolVersion = "test",
            Model = model,
            StartedAt = now,
            CompletedAt = now,
            MaxTokens = -1,
            TimeoutSeconds = BenchmarkDefaults.OfficialRequestTimeoutSeconds,
            Seed = 12345,
            RepeatGroupId = repeatGroupId,
            RepeatIndex = repeatIndex,
            RepeatCount = repeatCount,
            SourceFile = "enhanced_calc.cpp",
            SourceSha256 = "deadbeef",
            ExpectedSourceSha256 = "deadbeef",
            SourceHashMatches = true,
            OutputDirectory = string.Empty,
            Run1 = new BenchmarkRunArtifacts { RunName = "Run 1", StartedAt = now, CompletedAt = now, Score = score, ReasoningDisclosure = reasoningDisclosure }
        };
    }

    private static ScoringResult FakeScore(string runName, double scorePercent, int fullTp, int partialTp, int fp, int missed)
    {
        var vulnerabilities = new List<VulnerabilityScore>();
        for (var i = 0; i < FakeVulnIds.Length; i++)
        {
            bool found, partial;
            if (i < fullTp) { found = true; partial = false; }
            else if (i < fullTp + partialTp) { found = true; partial = true; }
            else { found = false; partial = false; }

            vulnerabilities.Add(new VulnerabilityScore
            {
                Id = FakeVulnIds[i],
                Title = "vuln " + FakeVulnIds[i],
                Severity = i == 0 || i == 2 ? "Critical" : "High",
                Found = found,
                Partial = partial,
                FindingIndex = found ? i + 1 : null,
                MatchScore = found ? 0.9 : 0
            });
        }

        return new ScoringResult
        {
            RunName = runName,
            ScoreableVulnerabilityCount = FakeVulnIds.Length,
            FindingCount = fullTp + partialTp + fp,
            FullTruePositives = fullTp,
            PartialTruePositives = partialTp,
            FalsePositives = fp,
            Duplicates = 1,
            IgnoredLowConfidence = 1,
            Missed = missed,
            RawPoints = fullTp * 5.0 + partialTp * 2.5,
            ScorePercent = scorePercent,
            Precision = (fullTp + partialTp + fp) == 0 ? 0 : (fullTp + partialTp * 0.5) / (fullTp + partialTp + fp),
            Recall = FakeVulnIds.Length == 0 ? 0 : (fullTp + partialTp * 0.5) / FakeVulnIds.Length,
            F1 = 0.5,
            Vulnerabilities = vulnerabilities
        };
    }

    private static ReasoningDisclosureDiagnostics FakeReasoningDisclosure(int fullTp, int partialTp, int fp, bool includeReasoning)
        => includeReasoning
            ? new ReasoningDisclosureDiagnostics
            {
                HasVisibleReasoning = true,
                Summary = "test reasoning disclosure",
                ReasoningParsedFindingCount = fullTp + partialTp + 1,
                OutputParsedFindingCount = fullTp + partialTp + fp,
                ReasoningTruePositiveCount = fullTp + partialTp + 1,
                OutputTruePositiveCount = fullTp + partialTp,
                ReasoningOnlyTruePositiveCount = 1,
                OutputOnlyTruePositiveCount = 0,
                ReasoningToOutputCoverage = fullTp + partialTp + 1 == 0 ? 0 : (double)(fullTp + partialTp) / (fullTp + partialTp + 1),
                ReasoningFalsePositives = 0,
                OutputFalsePositives = fp
            }
            : new ReasoningDisclosureDiagnostics();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp dir must never fail the test run.
        }
    }
}
