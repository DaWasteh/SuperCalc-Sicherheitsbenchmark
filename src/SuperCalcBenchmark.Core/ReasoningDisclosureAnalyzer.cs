namespace SuperCalcBenchmark.Core;

public static class ReasoningDisclosureAnalyzer
{
    public static ReasoningDisclosureDiagnostics Analyze(
        string? reasoningContent,
        ParseResult reasoningParse,
        ScoringResult reasoningScore,
        ScoringResult outputScore)
    {
        var hasVisibleReasoning = !string.IsNullOrWhiteSpace(reasoningContent);
        var reasoningIds = TruePositiveIds(reasoningScore);
        var outputIds = TruePositiveIds(outputScore);
        List<string> kept;
        List<string> reasoningOnly;
        List<string> outputOnly;
        double? coverage;

        if (!hasVisibleReasoning)
        {
            kept = [];
            reasoningOnly = [];
            outputOnly = [];
            coverage = null;
        }
        else
        {
            kept = reasoningIds.Intersect(outputIds, StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            reasoningOnly = reasoningIds.Except(outputIds, StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            outputOnly = outputIds.Except(reasoningIds, StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
            coverage = reasoningIds.Count == 0
                ? (double?)null
                : Math.Round((double)kept.Count / reasoningIds.Count, 4);
        }

        return new ReasoningDisclosureDiagnostics
        {
            HasVisibleReasoning = hasVisibleReasoning,
            Summary = BuildSummary(hasVisibleReasoning, reasoningIds.Count, outputIds.Count, reasoningOnly.Count, outputOnly.Count, kept.Count, coverage),
            ReasoningParsedFindingCount = reasoningScore.FindingCount,
            OutputParsedFindingCount = outputScore.FindingCount,
            ReasoningTruePositiveCount = reasoningIds.Count,
            OutputTruePositiveCount = outputIds.Count,
            ReasoningOnlyTruePositiveCount = reasoningOnly.Count,
            OutputOnlyTruePositiveCount = outputOnly.Count,
            ReasoningToOutputCoverage = coverage,
            ReasoningFalsePositives = reasoningScore.FalsePositives,
            OutputFalsePositives = outputScore.FalsePositives,
            ReasoningParsedJson = reasoningParse.ParsedJson,
            ReasoningUsedTextFallback = reasoningParse.UsedTextFallback,
            ReasoningParseWarning = reasoningParse.Warning,
            ReasoningTruePositiveIds = reasoningIds,
            OutputTruePositiveIds = outputIds,
            ReasoningOnlyTruePositiveIds = reasoningOnly,
            OutputOnlyTruePositiveIds = outputOnly
        };
    }

    private static List<string> TruePositiveIds(ScoringResult result)
    {
        return result.Findings
            .Where(f => f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive)
            .Select(f => f.MatchedVulnerabilityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string BuildSummary(
        bool hasVisibleReasoning,
        int reasoningTp,
        int outputTp,
        int reasoningOnly,
        int outputOnly,
        int kept,
        double? coverage)
    {
        if (!hasVisibleReasoning)
        {
            return "No visible thinking/reasoning content; thinking-vs-output diagnostic unavailable.";
        }

        if (reasoningTp == 0)
        {
            return $"Thinking parsed 0 scoreable true positives; final output reported {outputTp}. Coverage unavailable.";
        }

        return $"{FormatPercent(coverage)} of thinking true positives were reported in final output ({kept}/{reasoningTp}); thinking-only {reasoningOnly}; output-only {outputOnly}.";
    }

    private static string FormatPercent(double? value)
    {
        return value is null
            ? "n/a"
            : value.Value.ToString("P1", System.Globalization.CultureInfo.InvariantCulture);
    }
}
