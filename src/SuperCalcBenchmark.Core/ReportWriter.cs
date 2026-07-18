using System.Text;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public string CreateRunDirectory(BenchmarkOptions options, DateTimeOffset startedAt)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
            return Path.GetFullPath(options.OutputDirectory);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Path.Combine(Environment.CurrentDirectory, "results");
        }

        var modelPart = TextUtil.SafeFileNamePart(options.Model);
        var stamp = startedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var directory = Path.Combine(localAppData, "SuperCalcBenchmark", "Runs", $"{stamp}_{modelPart}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    public void Write(BenchmarkRunResult result)
    {
        if (string.IsNullOrWhiteSpace(result.OutputDirectory))
        {
            throw new InvalidOperationException("OutputDirectory is required before writing a benchmark result.");
        }

        Directory.CreateDirectory(result.OutputDirectory);

        File.WriteAllText(Path.Combine(result.OutputDirectory, "run.json"), JsonSerializer.Serialize(result, JsonOptions), Encoding.UTF8);
        File.WriteAllText(Path.Combine(result.OutputDirectory, "run1_prompt.txt"), result.Run1.Prompt, Encoding.UTF8);
        File.WriteAllText(Path.Combine(result.OutputDirectory, "run1_response.txt"), result.Run1.Response, Encoding.UTF8);
        File.WriteAllText(Path.Combine(result.OutputDirectory, "run1_reasoning.txt"), result.Run1.ReasoningContent, Encoding.UTF8);
        File.WriteAllText(Path.Combine(result.OutputDirectory, "run1_raw_response.json"), result.Run1.RawResponse, Encoding.UTF8);
        File.WriteAllText(Path.Combine(result.OutputDirectory, "run1_request.json"), result.Run1.RequestJson, Encoding.UTF8);

        if (result.Run2 is not null)
        {
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run2_prompt.txt"), result.Run2.Prompt, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run2_response.txt"), result.Run2.Response, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run2_reasoning.txt"), result.Run2.ReasoningContent, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run2_raw_response.json"), result.Run2.RawResponse, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run2_request.json"), result.Run2.RequestJson, Encoding.UTF8);
        }

        if (result.Run3 is not null)
        {
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run3_prompt.txt"), result.Run3.Prompt, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run3_response.txt"), result.Run3.Response, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run3_reasoning.txt"), result.Run3.ReasoningContent, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run3_raw_response.json"), result.Run3.RawResponse, Encoding.UTF8);
            File.WriteAllText(Path.Combine(result.OutputDirectory, "run3_request.json"), result.Run3.RequestJson, Encoding.UTF8);
        }

        File.WriteAllText(Path.Combine(result.OutputDirectory, "report.md"), BuildMarkdownReport(result), Encoding.UTF8);
        File.WriteAllText(Path.Combine(result.OutputDirectory, "findings-run1.csv"), BuildFindingsCsv(result.Run1.Score), Encoding.UTF8);
        if (result.Run2 is not null)
        {
            File.WriteAllText(Path.Combine(result.OutputDirectory, "findings-run2.csv"), BuildFindingsCsv(result.Run2.Score), Encoding.UTF8);
        }
    }

    public string BuildMarkdownReport(BenchmarkRunResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("# SuperCalc Benchmark Report");
        builder.AppendLine();
        builder.AppendLine($"- Benchmark: `{result.BenchmarkId}`");
        builder.AppendLine($"- Tool version: `{result.ToolVersion}`");
        builder.AppendLine($"- Model: `{result.Model}`");
        builder.AppendLine($"- Server: `{result.ServerUrl}`");
        builder.AppendLine($"- Server context window: `{(result.ServerContextSize?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown")}`");
        builder.AppendLine($"- Requested max completion tokens: `{FormatMaxTokens(result.MaxTokens)}`");
        if (!string.IsNullOrWhiteSpace(result.RepeatGroupId) || result.RepeatCount > 1)
        {
            builder.AppendLine($"- Repeat group: `{(string.IsNullOrWhiteSpace(result.RepeatGroupId) ? "n/a" : result.RepeatGroupId)}` ({result.RepeatIndex}/{result.RepeatCount}, seed `{result.Seed}`)");
        }
        builder.AppendLine($"- HTTP request timeout: `{FormatTimeout(result.TimeoutSeconds)}`");
        builder.AppendLine($"- Thinking disable requested: `{result.DisableThinking}`");
        builder.AppendLine($"- Streaming loop guard enabled: `{result.AbortOnLoop}`");
        builder.AppendLine($"- Started: `{result.StartedAt:O}`");
        builder.AppendLine($"- Completed: `{result.CompletedAt:O}`");
        builder.AppendLine($"- Total duration: `{FormatDuration(result.DurationMs)}`");
        builder.AppendLine($"- Source: `{result.SourceFile}`");
        builder.AppendLine($"- Source SHA-256: `{result.SourceSha256}`");
        builder.AppendLine($"- Expected SHA-256: `{result.ExpectedSourceSha256}`");
        builder.AppendLine($"- Source hash matches: **{result.SourceHashMatches}**");
        builder.AppendLine();
        builder.AppendLine("> The evaluated model received only the source file and, for Run 2, its own Run-1 response. Hidden ground truth and `enhanced_exploits.md` were not included in prompts.");
        builder.AppendLine();

        AppendScoreSummary(builder, result.Run1.Score);
        AppendCompletionDiagnostics(builder, result.Run1);
        if (result.Run2 is not null)
        {
            AppendScoreSummary(builder, result.Run2.Score);
            AppendCompletionDiagnostics(builder, result.Run2);
        }

        if (result.Comparison is not null)
        {
            builder.AppendLine("## Run 1 vs Run 2 Self-Validation");
            builder.AppendLine();
            builder.AppendLine($"- Kept true positives: {FormatList(result.Comparison.KeptTruePositiveIds)}");
            builder.AppendLine($"- Dropped true positives: {FormatList(result.Comparison.DroppedTruePositiveIds)}");
            builder.AppendLine($"- Added true positives: {FormatList(result.Comparison.AddedTruePositiveIds)}");
            builder.AppendLine($"- Kept false positives: {result.Comparison.KeptFalsePositives} ({FormatList(result.Comparison.KeptFalsePositiveKeys)})");
            builder.AppendLine($"- Dropped false positives: {result.Comparison.DroppedFalsePositives} ({FormatList(result.Comparison.DroppedFalsePositiveKeys)})");
            builder.AppendLine($"- Added false positives: {result.Comparison.AddedFalsePositives} ({FormatList(result.Comparison.AddedFalsePositiveKeys)})");
            builder.AppendLine($"- False positives Run 1 → Run 2: {result.Comparison.Run1FalsePositives} → {result.Comparison.Run2FalsePositives}");
            builder.AppendLine($"- False-positive reduction: {result.Comparison.FalsePositiveReduction} ({result.Comparison.FalsePositiveReductionRate:P1})");
            builder.AppendLine($"- True-positive retention: {result.Comparison.TruePositiveRetention:P1}");
            builder.AppendLine($"- Over-pruning rate: {result.Comparison.OverPruningRate:P1}");
            builder.AppendLine($"- Evidence improvement delta: {result.Comparison.EvidenceImprovementDelta:+0.00;-0.00;0.00}");
            builder.AppendLine($"- Severity corrected: {result.Comparison.SeverityCorrectedCount}");
            builder.AppendLine();
            AppendSelfValidationTables(builder, result.Comparison);
            builder.AppendLine();
        }

        if (result.Run3?.TruthAudit is not null)
        {
            AppendTruthAudit(builder, result.Run3.TruthAudit);
        }
        AppendBehavioralDiagnostics(builder, result.BehavioralDiagnostics);

        AppendFindingLedger(builder, result.Run1.Score);
        if (result.Run2 is not null)
        {
            AppendFindingLedger(builder, result.Run2.Score);
        }

        AppendVulnerabilityMatrix(builder, result.Run1.Score);
        if (result.Run2 is not null)
        {
            AppendVulnerabilityMatrix(builder, result.Run2.Score);
        }

        return builder.ToString();
    }

    private static void AppendScoreSummary(StringBuilder builder, ScoringResult score)
    {
        builder.AppendLine($"## {score.RunName} Summary");
        builder.AppendLine();
        builder.AppendLine("| Metric | Value |");
        builder.AppendLine("| ------ | ----: |");
        builder.AppendLine($"| Score | {score.ScorePercent:0.##}/100 |");
        builder.AppendLine($"| Raw points | {score.RawPoints:0.##} |");
        builder.AppendLine($"| Score schema version | {score.ScoreSchemaVersion} |");
        builder.AppendLine($"| Scoring profile | `{EscapePipe(score.ScoringProfile)}` v{score.ScoringProfileVersion} |");
        builder.AppendLine($"| Adjudicated | {score.IsAdjudicated} |");
        if (!string.IsNullOrWhiteSpace(score.AdjudicationLabel))
        {
            builder.AppendLine($"| Adjudication label | `{EscapePipe(score.AdjudicationLabel)}` |");
        }
        builder.AppendLine($"| Scoring engine | `{EscapePipe(score.ScoringEngineVersion)}` |");
        builder.AppendLine($"| Parser version | `{EscapePipe(score.ParserVersion)}` |");
        builder.AppendLine($"| Prompt version | `{EscapePipe(score.PromptVersion)}` |");
        builder.AppendLine($"| Ground-truth SHA-256 | `{EscapePipe(score.GroundTruthSha256)}` |");
        builder.AppendLine($"| Score computed at | `{score.ComputedAt:O}` |");
        builder.AppendLine($"| Findings parsed | {score.FindingCount} |");
        builder.AppendLine($"| Full TP | {score.FullTruePositives} |");
        builder.AppendLine($"| Partial TP | {score.PartialTruePositives} |");
        builder.AppendLine($"| False positives | {score.FalsePositives} |");
        builder.AppendLine($"| Duplicates | {score.Duplicates} |");
        builder.AppendLine($"| Ignored low confidence | {score.IgnoredLowConfidence} |");
        builder.AppendLine($"| Missed | {score.Missed} |");
        builder.AppendLine($"| Precision | {score.Precision:P1} |");
        builder.AppendLine($"| Recall | {score.Recall:P1} |");
        builder.AppendLine($"| F1 | {score.F1:P1} |");
        builder.AppendLine($"| Evidence fidelity | {score.EvidenceFidelity:P1} |");
        builder.AppendLine($"| Location accuracy | {score.LocationAccuracy:P1} |");
        builder.AppendLine($"| Hallucination rate | {score.HallucinationRate:P1} |");
        builder.AppendLine($"| Duplicate rate | {score.DuplicateRate:P1} |");
        builder.AppendLine($"| Evaluation confidence | {score.EvaluationConfidence:P1} |");
        builder.AppendLine($"| FP taxonomy | {FormatTaxonomy(score.FalsePositiveTaxonomy)} |");
        builder.AppendLine();
    }

    private static void AppendCompletionDiagnostics(StringBuilder builder, BenchmarkRunArtifacts artifacts)
    {
        builder.AppendLine($"## {artifacts.RunName} Completion Diagnostics");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| ----- | ----- |");
        builder.AppendLine($"| Finish reason | `{EscapePipe(artifacts.FinishReason)}` |");
        builder.AppendLine($"| Manually stopped by user | {artifacts.ManuallyStopped} |");
        builder.AppendLine($"| Loop aborted by client | {artifacts.LoopDetected} |");
        if (!string.IsNullOrWhiteSpace(artifacts.LoopDiagnosticsSummary))
        {
            builder.AppendLine($"| Loop abort reason | {EscapePipe(artifacts.LoopDiagnosticsSummary)} |");
        }
        builder.AppendLine($"| Duration | {FormatDuration(artifacts.DurationMs)} |");
        builder.AppendLine($"| User prompt chars | {artifacts.Prompt.Length:N0} |");
        builder.AppendLine($"| Input prompt tokens | {FormatTokenCount(artifacts.PromptTokens)} |");
        builder.AppendLine($"| Assistant output tokens | {FormatTokenCount(artifacts.ResponseTokens)} |");
        builder.AppendLine($"| Thinking/reasoning tokens | {FormatTokenCount(artifacts.ReasoningTokens)} |");
        builder.AppendLine($"| Total generated tokens | {FormatTokenCount(artifacts.CompletionTokens)} |");
        builder.AppendLine($"| Used response_format | {artifacts.UsedResponseFormat} |");
        builder.AppendLine($"| Retried without response_format | {artifacts.RetriedWithoutResponseFormat} |");
        builder.AppendLine($"| Sent Qwen thinking disable hint | {artifacts.UsedThinkingControl} |");
        builder.AppendLine($"| Retried without thinking hint | {artifacts.RetriedWithoutThinkingControl} |");
        builder.AppendLine($"| Parse mode | `{EscapePipe(string.IsNullOrWhiteSpace(artifacts.Parse.ParseMode) ? "unknown" : artifacts.Parse.ParseMode)}` |");
        builder.AppendLine($"| Parsed JSON | {artifacts.Parse.ParsedJson} |");
        builder.AppendLine($"| Used text fallback | {artifacts.Parse.UsedTextFallback} |");
        AppendReasoningDisclosureRows(builder, artifacts.ReasoningDisclosure);
        var responseLoop = OutputLoopDetector.Analyze(artifacts.Response);
        var reasoningLoop = OutputLoopDetector.Analyze(artifacts.ReasoningContent);
        builder.AppendLine($"| Assistant loop check | {EscapePipe(responseLoop.Summary)} |");
        builder.AppendLine($"| Thinking loop check | {EscapePipe(reasoningLoop.Summary)} |");
        if (!string.IsNullOrWhiteSpace(artifacts.Parse.Warning))
        {
            builder.AppendLine($"| Parse warning | {EscapePipe(artifacts.Parse.Warning)} |");
        }

        builder.AppendLine();
        if (artifacts.ResponseTokens.HasValue || artifacts.ReasoningTokens.HasValue || artifacts.CompletionTokens.HasValue)
        {
            builder.AppendLine("> Token counts are exact model-tokenizer values, not character estimates. `Total generated tokens` comes from llama.cpp `usage.completion_tokens` and can exceed Thinking + Output because generated control, separator, or EOS tokens are not visible in either text channel.");
            builder.AppendLine();
        }
        if (artifacts.ReasoningDisclosure.HasVisibleReasoning)
        {
            builder.AppendLine("> `Denken-vs-Sagen` is diagnostic only and is not included in the 100-point benchmark score. It uses the same hidden-ground-truth matcher on visible `reasoning_content` / inline `<think>` blocks, so unstructured thinking can be undercounted.");
            builder.AppendLine();
        }

        if (artifacts.LoopDetected)
        {
            builder.AppendLine("> Warning: the client closed the streaming request early because final assistant content matched the loop/repetition guard. Visible reasoning_content is not live-aborted; the saved final output is intentionally partial so the benchmark does not hang or exhaust memory.");
            builder.AppendLine();
        }

        if (artifacts.ManuallyStopped)
        {
            builder.AppendLine("> Note: this run was manually stopped from the Raw Outputs tab. The partial assistant content and visible reasoning up to that point were still parsed, scored and saved.");
            builder.AppendLine();
        }

        if (string.IsNullOrWhiteSpace(artifacts.Response) && !string.IsNullOrWhiteSpace(artifacts.ReasoningContent))
        {
            builder.AppendLine($"> Warning: the server returned `reasoning_content` but an empty final `message.content`. This usually means Qwen thinking was not disabled or the model exhausted `max_tokens` before producing a final answer.");
            builder.AppendLine();
        }

        AppendLoopDetails(builder, "assistant content", responseLoop);
        AppendLoopDetails(builder, "thinking/reasoning content", reasoningLoop);
    }

    private static void AppendReasoningDisclosureRows(StringBuilder builder, ReasoningDisclosureDiagnostics diagnostics)
    {
        builder.AppendLine($"| Denken-vs-Sagen | {EscapePipe(diagnostics.Summary)} |");
        builder.AppendLine($"| Thinking parsed findings | {diagnostics.ReasoningParsedFindingCount} |");
        builder.AppendLine($"| Output parsed findings | {diagnostics.OutputParsedFindingCount} |");
        builder.AppendLine($"| Thinking TPs vs output TPs | {diagnostics.ReasoningTruePositiveCount} → {diagnostics.OutputTruePositiveCount} |");
        builder.AppendLine($"| Thinking-only TPs | {FormatList(diagnostics.ReasoningOnlyTruePositiveIds)} |");
        builder.AppendLine($"| Output-only TPs | {FormatList(diagnostics.OutputOnlyTruePositiveIds)} |");
        builder.AppendLine($"| Thinking→Output coverage | {FormatNullablePercent(diagnostics.ReasoningToOutputCoverage)} |");
        builder.AppendLine($"| Thinking FPs vs output FPs | {diagnostics.ReasoningFalsePositives} → {diagnostics.OutputFalsePositives} |");
        builder.AppendLine($"| Thinking parse mode | JSON={diagnostics.ReasoningParsedJson}, textFallback={diagnostics.ReasoningUsedTextFallback} |");
        if (!string.IsNullOrWhiteSpace(diagnostics.ReasoningParseWarning))
        {
            builder.AppendLine($"| Thinking parse warning | {EscapePipe(diagnostics.ReasoningParseWarning)} |");
        }
    }

    private static void AppendLoopDetails(StringBuilder builder, string label, OutputLoopDiagnostics diagnostics)
    {
        if (!diagnostics.HasSuspectedLoop)
        {
            return;
        }

        builder.AppendLine($"> Warning: possible loop detected in {label}. Top repeated segments:");
        foreach (var repetition in diagnostics.Repetitions.Take(3))
        {
            builder.AppendLine($"> - {repetition.Kind} x{repetition.Occurrences}: `{EscapePipe(repetition.Snippet)}`");
        }

        builder.AppendLine();
    }

    private static void AppendSelfValidationTables(StringBuilder builder, RunComparison comparison)
    {
        var interestingVulnerabilities = comparison.VulnerabilityChanges
            .Where(change => !string.Equals(change.Change, "unchanged", StringComparison.OrdinalIgnoreCase))
            .OrderBy(change => change.GroundTruthId, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (interestingVulnerabilities.Count > 0)
        {
            builder.AppendLine("### Self-Validation Vulnerability Changes");
            builder.AppendLine();
            builder.AppendLine("| GroundTruthId | Run1 status | Run2 status | Change | Evidence Δ | Location Δ | Notes |");
            builder.AppendLine("| ------------- | ----------- | ----------- | ------ | ---------: | ---------: | ----- |");
            foreach (var change in interestingVulnerabilities)
            {
                builder.AppendLine($"| {EscapePipe(change.GroundTruthId)} | {EscapePipe(change.Run1Status)} | {EscapePipe(change.Run2Status)} | {EscapePipe(change.Change)} | {change.EvidenceDelta:+0.00;-0.00;0.00} | {change.LocationDelta:+0.00;-0.00;0.00} | {EscapePipe(change.Notes)} |");
            }

            builder.AppendLine();
        }

        if (comparison.FindingChanges.Count > 0)
        {
            builder.AppendLine("### Self-Validation Finding Changes");
            builder.AppendLine();
            builder.AppendLine("| Finding | Run1 # | Run2 # | Change | FP Category | Evidence Δ | Notes |");
            builder.AppendLine("| ------- | -----: | -----: | ------ | ----------- | ---------: | ----- |");
            foreach (var change in comparison.FindingChanges.OrderBy(change => change.Change, StringComparer.OrdinalIgnoreCase).ThenBy(change => change.FindingKey, StringComparer.OrdinalIgnoreCase))
            {
                builder.AppendLine($"| {EscapePipe(change.FindingKey)} | {(change.Run1FindingIndex?.ToString() ?? "-")} | {(change.Run2FindingIndex?.ToString() ?? "-")} | {EscapePipe(change.Change)} | {EscapePipe(change.FalsePositiveCategory)} | {change.EvidenceDelta:+0.00;-0.00;0.00} | {EscapePipe(change.Notes)} |");
            }

            builder.AppendLine();
        }
    }

    private static void AppendBehavioralDiagnostics(StringBuilder builder, BehavioralDiagnosticsEnvelope? envelope)
    {
        builder.AppendLine("## Behavioral Diagnostics (non-scoring)");
        builder.AppendLine();
        builder.AppendLine("These post-hoc diagnostics are non-blind and non-scoring; they never alter official-v1/v2 results.");
        if (envelope is null) { builder.AppendLine(); builder.AppendLine("- Availability: n/a"); builder.AppendLine(); return; }
        var t = envelope.TruthAudit;
        static string P(double? v) => v.HasValue ? v.Value.ToString("P1") : "n/a";
        builder.AppendLine($"- Validity / coverage: `{t?.Validity.State.ToString() ?? "unavailable"}`; eligible `{t?.Validity.MetricEligible.ToString() ?? "n/a"}`; coverage {P(t?.Validity.Coverage)}; audited source `{envelope.Provenance.AuditedRunName ?? "n/a"}`");
        var truthEligible = t?.Validity.MetricEligible == true;
        var truthGate = t is null ? "unavailable" : $"state={t.Validity.State}; failures={string.Join(',', t.Validity.Failures)}; tier={t.Validity.EvidenceTier}";
        builder.AppendLine($"- Honesty confusion / ordinal: {(truthEligible ? $"N={t!.OrdinalEligibleCount}; inflation {P(t.InflationRate)}; underclaim {P(t.UnderclaimRate)}" : "n/a (" + truthGate + ")")}");
        builder.AppendLine($"- Normalized laundering / contradiction: {(truthEligible ? P(t?.LaunderingPrevalence) + " / " + P(t?.ContradictionPrevalence) : "n/a (" + truthGate + ")")}");
        builder.AppendLine($"- Confidence calibration (Run 1 / Run 2): Brier {P(envelope.Run1Confidence?.ReportedOnly.SoftBrier)} / {P(envelope.Run2Confidence?.ReportedOnly.SoftBrier)}; ECE {P(envelope.Run1Confidence?.ReportedOnly.Ece10)} / {P(envelope.Run2Confidence?.ReportedOnly.Ece10)}; N={envelope.Run1Confidence?.ReportedOnly.Count ?? 0}/{envelope.Run2Confidence?.ReportedOnly.Count ?? 0}");
        builder.AppendLine($"- Severity / CWE (Run 1 / Run 2): exact {P(envelope.Run1Taxonomy?.SeverityExactRate)} / {P(envelope.Run2Taxonomy?.SeverityExactRate)}; CWE hit {P(envelope.Run1Taxonomy?.CweAnyHitRate)} / {P(envelope.Run2Taxonomy?.CweAnyHitRate)}");
        builder.AppendLine($"- Triangulation: {(truthEligible ? $"reasoning→output {P(t?.Triangulation?.ReasoningToOutputRetention)}; output→audit {P(t?.Triangulation?.OutputToAuditAcknowledgment)}; end-to-end {P(t?.Triangulation?.EndToEndRetention)}" : "n/a (" + truthGate + ")")}");
        builder.AppendLine($"- Revision / parse transition: selectivity {P(envelope.RevisionSelectivity?.RevisionSelectivity)}; `{envelope.ParseTransition?.Transition ?? "n/a"}` ({envelope.ParseTransition?.Delta?.ToString("+0.##;-0.##;0") ?? "n/a"})");
        builder.AppendLine($"- Flags / corrections: {(truthEligible ? $"consistency {P(t!.ExplicitFlagConsistencyRate)}; provenance {P(t.CorrectionProvenanceRate)} (valid {t.ValidCorrectionCount}/{t.RawCorrectionCount})" : "n/a (" + truthGate + ")")}");
        builder.AppendLine();
    }

    private static void AppendTruthAudit(StringBuilder builder, TruthAuditResult audit)
    {
        builder.AppendLine("## Run 3 — Truth Audit / Honesty");
        builder.AppendLine();
        builder.AppendLine($"- Audited previous run: `{audit.AuditedRunName}`");
        builder.AppendLine($"- Detection score of audited run: `{audit.AuditedRunScorePercent:0.##}` `{audit.AuditedRunScoreProfile}`");
        builder.AppendLine($"- Selection reason: `{audit.SelectionReason}`");
        builder.AppendLine($"- Accountability score: {audit.AccountabilityScore:0.##}/100");
        builder.AppendLine($"- Truth-audit accuracy: {audit.TruthAuditAccuracy:P1}");
        builder.AppendLine($"- Overclaim rate: {audit.OverclaimRate:P1}");
        builder.AppendLine($"- Miss admission rate: {audit.MissAdmissionRate:P1}");
        builder.AppendLine($"- FP admission rate: {audit.FalsePositiveAdmissionRate:P1}");
        builder.AppendLine($"- Evidence laundering: {audit.EvidenceLaunderingCount} case(s)");
        builder.AppendLine($"- Quote fidelity: {audit.QuoteFidelity:P1}");
        builder.AppendLine();
        builder.AppendLine("| ID | Actual audited status | Model self-assessment | Correct? | Quote valid? | Notes |");
        builder.AppendLine("| -- | --------------------- | --------------------- | -------- | ------------ | ----- |");
        foreach (var item in audit.Items.OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine($"| {EscapePipe(item.Id)} | {EscapePipe(item.ActualStatus)} | {EscapePipe(item.SelfAssessment)} | {item.Correct} | {item.QuoteValid} | {EscapePipe(item.Notes)} |");
        }

        builder.AppendLine();
        builder.AppendLine("> Run 3 is non-blind: ground truth is intentionally visible and the result does not improve the Blind/Self-Validation detection score.");
        builder.AppendLine();
    }

    private static void AppendFindingLedger(StringBuilder builder, ScoringResult score)
    {
        builder.AppendLine($"## {score.RunName} Finding Ledger");
        builder.AppendLine();
        builder.AppendLine("| # | Classification | Points | Match | Finding | Reason |");
        builder.AppendLine("| -: | -------------- | -----: | ----: | ------- | ------ |");
        foreach (var finding in score.Findings.OrderBy(f => f.FindingIndex))
        {
            builder.AppendLine($"| {finding.FindingIndex} | {finding.Classification} | {finding.Points:0.##} | {finding.MatchScore:0.00} {EscapePipe(finding.MatchedVulnerabilityId ?? "UNMATCHED")} | {EscapePipe(finding.FindingTitle)} | {EscapePipe(finding.Reason)} |");
        }

        builder.AppendLine();

        foreach (var finding in score.Findings.OrderBy(f => f.FindingIndex))
        {
            builder.AppendLine($"### {score.RunName} Finding {finding.FindingIndex}: {finding.FindingTitle}");
            builder.AppendLine();
            builder.AppendLine($"- Classification: `{finding.Classification}`");
            builder.AppendLine($"- Matched ID: `{finding.MatchedVulnerabilityId ?? "UNMATCHED"}`");
            builder.AppendLine($"- Match score: `{finding.MatchScore:0.000}`");
            builder.AppendLine($"- Points: `{finding.Points:0.##}`");
            builder.AppendLine($"- Duplicate: `{finding.Duplicate}`");
            builder.AppendLine($"- Severity mismatch: `{finding.SeverityMismatch}`");
            if (!string.IsNullOrWhiteSpace(finding.FalsePositiveCategory))
            {
                builder.AppendLine($"- False-positive category: `{finding.FalsePositiveCategory}`");
            }
            if (!string.IsNullOrWhiteSpace(finding.AdjudicationReason))
            {
                builder.AppendLine($"- Adjudication: `{EscapePipe(finding.AdjudicationReason)}`");
            }
            builder.AppendLine($"- Evidence fidelity: `{finding.EvidenceFidelity:0.00}` (exact={finding.EvidenceExactMatch}, normalized={finding.EvidenceNormalizedMatch})");
            builder.AppendLine($"- Location accuracy: `{finding.LocationAccuracy:0.00}`");
            builder.AppendLine($"- Accepted evidence anchors: {FormatList(finding.AcceptedEvidenceAnchors)}");
            builder.AppendLine($"- Missing must anchors: {FormatList(finding.MissingMustAnchors)}");
            builder.AppendLine($"- Rejections/caps: {FormatList(finding.RejectedBecause)}");
            builder.AppendLine();
            builder.AppendLine("| Signal | Weight | Value | Detail |");
            builder.AppendLine("| ------ | -----: | ----: | ------ |");
            foreach (var signal in finding.Signals)
            {
                builder.AppendLine($"| {signal.Name} | {signal.Weight:0.00} | {signal.Value:0.00} | {EscapePipe(signal.Detail)} |");
            }

            builder.AppendLine();
        }
    }

    private static void AppendVulnerabilityMatrix(StringBuilder builder, ScoringResult score)
    {
        builder.AppendLine($"## {score.RunName} Vulnerability Matrix");
        builder.AppendLine();
        builder.AppendLine("| Ground-truth ID | Status | Finding | Match | Title |");
        builder.AppendLine("| --------------- | ------ | ------: | ----: | ----- |");
        foreach (var vulnerability in score.Vulnerabilities.OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase))
        {
            var status = vulnerability.Found ? (vulnerability.Partial ? "Partial" : "Found") : "Missed";
            builder.AppendLine($"| {vulnerability.Id} | {status} | {(vulnerability.FindingIndex?.ToString() ?? "-")} | {vulnerability.MatchScore:0.00} | {EscapePipe(vulnerability.Title)} |");
        }

        builder.AppendLine();
    }

    private static string BuildFindingsCsv(ScoringResult score)
    {
        var builder = new StringBuilder();
        builder.AppendLine("run,finding_index,classification,fp_category,points,match_score,matched_id,title,reason");
        foreach (var finding in score.Findings.OrderBy(f => f.FindingIndex))
        {
            builder.AppendLine(string.Join(',',
                Csv(score.RunName),
                finding.FindingIndex,
                Csv(finding.Classification.ToString()),
                Csv(finding.FalsePositiveCategory),
                finding.Points.ToString(System.Globalization.CultureInfo.InvariantCulture),
                finding.MatchScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(finding.MatchedVulnerabilityId ?? "UNMATCHED"),
                Csv(finding.FindingTitle),
                Csv(finding.Reason)));
        }

        return builder.ToString();
    }

    private static string FormatTokenCount(int? value) => value.HasValue ? value.Value.ToString("N0") : "n/a";

    private static string FormatMaxTokens(int maxTokens) => maxTokens < 0 ? "-1 (server max/unbounded)" : maxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatTimeout(int seconds) => seconds <= 0 ? "unknown" : FormatDuration(seconds * 1000L);

    private static string FormatDuration(long durationMs)
    {
        if (durationMs <= 0)
        {
            return "n/a";
        }

        var span = TimeSpan.FromMilliseconds(durationMs);
        if (span.TotalHours >= 1)
        {
            return $"{span.TotalHours:0.0} h";
        }

        return span.TotalMinutes >= 1
            ? $"{span.TotalMinutes:0.0} min"
            : $"{span.TotalSeconds:0.0} s";
    }

    private static string FormatNullablePercent(double? value) => value is null ? "n/a" : value.Value.ToString("P1", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatList(IReadOnlyList<string> items) => items.Count == 0 ? "-" : string.Join(", ", items.Select(i => $"`{i}`"));

    private static string FormatTaxonomy(IReadOnlyDictionary<string, int> taxonomy) => taxonomy.Count == 0
        ? "-"
        : string.Join(", ", taxonomy.OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"`{kvp.Key}`={kvp.Value}"));

    private static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
