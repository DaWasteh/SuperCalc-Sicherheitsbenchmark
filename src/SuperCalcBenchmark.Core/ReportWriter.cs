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
        builder.AppendLine($"- Thinking disable requested: `{result.DisableThinking}`");
        builder.AppendLine($"- Streaming loop guard enabled: `{result.AbortOnLoop}`");
        builder.AppendLine($"- Started: `{result.StartedAt:O}`");
        builder.AppendLine($"- Completed: `{result.CompletedAt:O}`");
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
            builder.AppendLine($"- False positives Run 1 → Run 2: {result.Comparison.Run1FalsePositives} → {result.Comparison.Run2FalsePositives}");
            builder.AppendLine($"- False-positive reduction: {result.Comparison.FalsePositiveReduction}");
            builder.AppendLine($"- True-positive retention: {result.Comparison.TruePositiveRetention:P1}");
            builder.AppendLine();
        }

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
        builder.AppendLine();
    }

    private static void AppendCompletionDiagnostics(StringBuilder builder, BenchmarkRunArtifacts artifacts)
    {
        builder.AppendLine($"## {artifacts.RunName} Completion Diagnostics");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| ----- | ----- |");
        builder.AppendLine($"| Finish reason | `{EscapePipe(artifacts.FinishReason)}` |");
        builder.AppendLine($"| Loop aborted by client | {artifacts.LoopDetected} |");
        if (!string.IsNullOrWhiteSpace(artifacts.LoopDiagnosticsSummary))
        {
            builder.AppendLine($"| Loop abort reason | {EscapePipe(artifacts.LoopDiagnosticsSummary)} |");
        }
        builder.AppendLine($"| Assistant content chars | {artifacts.Response.Length} |");
        builder.AppendLine($"| Reasoning content chars | {artifacts.ReasoningContent.Length} |");
        builder.AppendLine($"| Used response_format | {artifacts.UsedResponseFormat} |");
        builder.AppendLine($"| Retried without response_format | {artifacts.RetriedWithoutResponseFormat} |");
        builder.AppendLine($"| Sent Qwen thinking disable hint | {artifacts.UsedThinkingControl} |");
        builder.AppendLine($"| Retried without thinking hint | {artifacts.RetriedWithoutThinkingControl} |");
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
        if (artifacts.ReasoningDisclosure.HasVisibleReasoning)
        {
            builder.AppendLine("> `Denken-vs-Sagen` is diagnostic only and is not included in the 100-point benchmark score. It uses the same hidden-ground-truth matcher on visible `reasoning_content` / inline `<think>` blocks, so unstructured thinking can be undercounted.");
            builder.AppendLine();
        }

        if (artifacts.LoopDetected)
        {
            builder.AppendLine("> Warning: the client closed the streaming request early because the live output matched the loop/repetition guard. The saved output is intentionally partial so the benchmark does not hang or exhaust memory.");
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
        builder.AppendLine("run,finding_index,classification,points,match_score,matched_id,title,reason");
        foreach (var finding in score.Findings.OrderBy(f => f.FindingIndex))
        {
            builder.AppendLine(string.Join(',',
                Csv(score.RunName),
                finding.FindingIndex,
                Csv(finding.Classification.ToString()),
                finding.Points.ToString(System.Globalization.CultureInfo.InvariantCulture),
                finding.MatchScore.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Csv(finding.MatchedVulnerabilityId ?? "UNMATCHED"),
                Csv(finding.FindingTitle),
                Csv(finding.Reason)));
        }

        return builder.ToString();
    }

    private static string FormatMaxTokens(int maxTokens) => maxTokens < 0 ? "-1 (server max/unbounded)" : maxTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatNullablePercent(double? value) => value is null ? "n/a" : value.Value.ToString("P1", System.Globalization.CultureInfo.InvariantCulture);

    private static string FormatList(IReadOnlyList<string> items) => items.Count == 0 ? "-" : string.Join(", ", items.Select(i => $"`{i}`"));

    private static string EscapePipe(string value) => value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);

    private static string Csv(string value)
    {
        var escaped = value.Replace("\"", "\"\"", StringComparison.Ordinal);
        return $"\"{escaped}\"";
    }
}
