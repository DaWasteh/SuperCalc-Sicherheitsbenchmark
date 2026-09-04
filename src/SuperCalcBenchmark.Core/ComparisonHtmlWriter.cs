using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Renders a <see cref="ComparisonReport"/> to a single self-contained HTML file with
/// offline tables plus optional Chart.js visualizations. Hidden prompt/raw-response data is
/// never embedded; only compact archive metrics and local comparison metadata are included.
///
/// The page embeds several precomputed projections (version scope × grouping) so the viewer
/// can switch between "current parser only", a specific parser version, a specific benchmark
/// version, or everything, and between pooled model+quant series and per-backend series,
/// without recomputing aggregates client-side. Per-run detail rows are stored once and
/// referenced by key from every projection.
/// </summary>
public sealed class ComparisonHtmlWriter
{
    private static readonly JsonSerializerOptions PayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    private const string ChartJsCdn = "https://cdn.jsdelivr.net/npm/chart.js@4.4.3/dist/chart.umd.min.js";

    /// <summary>Optional archive groups; when set, additional scope/grouping projections are embedded.</summary>
    public IReadOnlyList<ArchiveGroup>? Groups { get; init; }

    /// <summary>Family filter and metadata used to build the extra projections (mirrors the report's inputs).</summary>
    public string? FamilyFilter { get; init; }
    public VulnerabilityMetadataIndex? MetadataIndex { get; init; }

    /// <summary>Writes comparison.html (+ comparison.csv) into <paramref name="outputDirectory"/>.</summary>
    public string Write(ComparisonReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);

        var htmlPath = Path.Combine(outputDirectory, "comparison.html");
        File.WriteAllText(htmlPath, BuildHtml(report), Encoding.UTF8);

        var csvPath = Path.Combine(outputDirectory, "comparison.csv");
        File.WriteAllText(csvPath, BuildCsv(report), Encoding.UTF8);

        return htmlPath;
    }

    private sealed record Projection(string ScopeKey, string GroupingKey, IReadOnlyList<ComparisonSeries> Series);

    public string BuildHtml(ComparisonReport report)
    {
        var projections = BuildProjections(report);
        var scopes = (report.AvailableScopes.Count > 0 ? report.AvailableScopes : ComparisonReport.DiscoverScopes(Groups ?? []))
            .Select(scope => new { key = scope.Key, label = scope.Label, kind = scope.Kind.ToString(), runCount = scope.RunCount })
            .ToList();
        if (scopes.Count == 0)
        {
            scopes.Add(new { key = "all", label = "Alle Versionen", kind = "All", runCount = report.Series.Sum(s => s.RunCount) });
            scopes.Add(new { key = "current", label = $"Aktuell ({ResponseParser.CurrentParserVersion})", kind = "Current", runCount = report.CurrentEvaluationSeries.Sum(s => s.RunCount) });
        }

        var colors = BuildColorMap(projections.SelectMany(p => p.Series));

        // Tabular encoding: field names are emitted once (seriesKeys/runKeys) and every series
        // or run is a plain value row. With ~10 version scopes × ~90 groups this keeps the page
        // around 1 MB instead of ~5 MB of repeated camelCase keys. Split projections (backend /
        // runtime grouping) only carry the groups that actually split; the viewer merges the
        // remaining groups from the pooled projection.
        var runIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var runRows = new List<List<object?>>();
        List<string>? runKeys = null;
        var seriesRows = new List<List<object?>>();
        List<string>? seriesKeys = null;
        foreach (var projection in projections)
        {
            foreach (var series in projection.Series)
            {
                if (projection.GroupingKey != "model"
                    && !series.BackendBreakdown.Keys.Any(k => !string.Equals(k, ServerRuntimeInfo.UnknownValue, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var detailIndexes = new List<int>();
                foreach (var detail in series.Details)
                {
                    var key = detail.RecordId + "|" + detail.RunName;
                    if (!runIndex.TryGetValue(key, out var index))
                    {
                        var row = DetailPayload(detail);
                        runKeys ??= row.Keys.ToList();
                        index = runRows.Count;
                        runIndex[key] = index;
                        runRows.Add(runKeys.Select(k => row[k]).ToList());
                    }

                    detailIndexes.Add(index);
                }

                var seriesRow = SeriesPayload(series, projection.ScopeKey, projection.GroupingKey, colors[series.GroupKey], detailIndexes);
                seriesKeys ??= seriesRow.Keys.ToList();
                seriesRows.Add(seriesKeys.Select(k => seriesRow[k]).ToList());
            }
        }

        var payload = new
        {
            benchmarkId = report.BenchmarkId,
            generatedAt = report.GeneratedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            aggregate = report.Aggregate.ToString(),
            runView = report.RunView.ToString(),
            metric = MetricValue(report.Metric),
            scoringProfile = report.ScoringProfile,
            parserVersion = ResponseParser.CurrentParserVersion,
            knownParserVersions = ResponseParser.KnownParserVersions,
            defaultScope = report.Scope?.Key ?? "current",
            defaultGrouping = GroupingKey(report.Grouping),
            scopes,
            groupings = new[]
            {
                new { key = "model", label = "Modell · Quant (Backends zusammen)" },
                new { key = "backend", label = "Modell · Quant · Backend (getrennt)" },
                new { key = "runtime", label = "Modell · Quant · Engine/Build" }
            },
            axis = report.VulnerabilityMetadata.Select(a => new
            {
                id = a.Id,
                title = a.Title,
                severity = a.Severity,
                cwe = a.Cwe,
                category = a.Category,
                module = a.Module
            }),
            runKeys = runKeys ?? [],
            runRows,
            seriesKeys = seriesKeys ?? [],
            seriesRows
        };

        var json = JsonSerializer.Serialize(payload, PayloadOptions);
        var builder = new StringBuilder();
        builder.Append(HtmlHead);
        builder.Append("<script src=\"");
        builder.Append(HtmlEscape(ChartJsCdn));
        builder.Append("\"></script>\n<script id=\"data\" type=\"application/json\">\n");
        builder.Append(json);
        builder.Append("\n</script>\n<script>\n");
        builder.Append(HtmlScript);
        builder.Append("\n</script>\n</body>\n</html>\n");
        return builder.ToString();
    }

    private List<Projection> BuildProjections(ComparisonReport report)
    {
        var projections = new List<Projection>
        {
            new(report.Scope?.Key ?? "all", GroupingKey(report.Grouping), report.Series)
        };

        if (report.Scope is null && report.CurrentEvaluationSeries.Count > 0 || report.Scope is null && report.Series.Count > 0)
        {
            projections.Add(new Projection("current", GroupingKey(report.Grouping), report.CurrentEvaluationSeries));
        }

        if (Groups is null || Groups.Count == 0)
        {
            return projections;
        }

        var metadata = MetadataIndex ?? VulnerabilityMetadataIndex.Empty;
        var hasBackendInfo = Groups.SelectMany(g => g.Records).Any(r => r.ServerMetadata.NormalizedBackend != ServerRuntimeInfo.UnknownValue);
        var groupings = hasBackendInfo
            ? new[] { ComparisonGrouping.ModelQuant, ComparisonGrouping.ModelQuantBackend, ComparisonGrouping.ModelQuantRuntime }
            : new[] { ComparisonGrouping.ModelQuant };
        var scopes = report.AvailableScopes.Count > 0 ? report.AvailableScopes : ComparisonReport.DiscoverScopes(Groups);
        var seen = new HashSet<string>(projections.Select(p => p.ScopeKey + "|" + p.GroupingKey), StringComparer.Ordinal);

        foreach (var scope in scopes)
        {
            foreach (var grouping in groupings)
            {
                var key = scope.Key + "|" + GroupingKey(grouping);
                if (!seen.Add(key))
                {
                    continue;
                }

                var series = ComparisonReport.BuildSeries(
                    Groups,
                    report.BenchmarkId,
                    report.Aggregate,
                    FamilyFilter,
                    metadata,
                    report.RunView,
                    report.Metric,
                    report.ScoringProfile,
                    grouping,
                    scope.Kind == ComparisonScopeKind.All ? null : scope,
                    report.VulnerabilityAxis,
                    report.VulnerabilityMetadata);
                projections.Add(new Projection(scope.Key, GroupingKey(grouping), series));
            }
        }

        return projections;
    }

    private static string GroupingKey(ComparisonGrouping grouping) => grouping switch
    {
        ComparisonGrouping.ModelQuantBackend => "backend",
        ComparisonGrouping.ModelQuantRuntime => "runtime",
        _ => "model"
    };

    /// <summary>Stable colors: one hue per model family+quant, lightness variants per backend/runtime split.</summary>
    private static Dictionary<string, string> BuildColorMap(IEnumerable<ComparisonSeries> allSeries)
    {
        var baseKeys = allSeries
            .Select(s => ModelIdentity.GroupKey(s.ModelFamily, s.Quant))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var hues = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < baseKeys.Count; i++)
        {
            // Golden-angle spacing keeps neighbouring models visually distinct even for 60+ groups.
            hues[baseKeys[i]] = (i * 137.508) % 360;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var variantIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var series in allSeries)
        {
            if (result.ContainsKey(series.GroupKey))
            {
                continue;
            }

            var baseKey = ModelIdentity.GroupKey(series.ModelFamily, series.Quant);
            var hue = hues.TryGetValue(baseKey, out var h) ? h : 210;
            var isVariant = !string.Equals(series.GroupKey, baseKey, StringComparison.OrdinalIgnoreCase);
            var lightness = 46.0;
            if (isVariant)
            {
                var index = variantIndex.TryGetValue(baseKey, out var v) ? v : 0;
                variantIndex[baseKey] = index + 1;
                lightness = index switch { 0 => 40, 1 => 56, 2 => 32, _ => 64 };
            }

            result[series.GroupKey] = HslToHex(hue, 62, lightness);
        }

        return result;
    }

    private static Dictionary<string, object?> SeriesPayload(ComparisonSeries s, string scope, string grouping, string color, List<int> detailIndexes)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["scope"] = scope,
            ["grouping"] = grouping,
            ["groupKey"] = s.GroupKey,
            ["label"] = s.Label,
            ["family"] = s.ModelFamily,
            ["quant"] = s.Quant,
            ["backend"] = s.Backend,
            ["engine"] = s.Engine,
            ["build"] = s.Build,
            ["runtimeLabel"] = s.RuntimeLabel,
            ["backendBreakdown"] = s.BackendBreakdown,
            ["runtimeBreakdown"] = s.RuntimeBreakdown,
            ["toolVersions"] = s.ToolVersionBreakdown,
            ["parserVersions"] = s.ParserVersionBreakdown,
            ["runCount"] = s.RunCount,
            ["officialRunCount"] = s.OfficialRunCount,
            ["officialComparableRunCount"] = s.OfficialComparableRunCount,
            ["currentEvaluationRunCount"] = s.CurrentEvaluationRunCount,
            ["legacyMigratedRunCount"] = s.LegacyMigratedRunCount,
            ["rescoredRunCount"] = s.RescoredRunCount,
            ["sourceHashMatchCount"] = s.SourceHashMatchCount,
            ["aggregate"] = s.Aggregate.ToString(),
            ["runView"] = s.RunView.ToString(),
            ["score"] = Math.Round(s.ScorePercent, 2),
            ["scoreMean"] = Math.Round(s.ScoreMean, 2),
            ["scoreMedian"] = Math.Round(s.ScoreMedian, 2),
            ["scoreStdDev"] = Math.Round(s.ScoreStdDev, 2),
            ["scoreIqr"] = Math.Round(s.ScoreIqr, 2),
            ["scoreCi95"] = s.ScoreCi95.HasValue ? Math.Round(s.ScoreCi95.Value, 2) : null,
            ["scoreMin"] = Math.Round(s.ScoreMin, 2),
            ["scoreMax"] = Math.Round(s.ScoreMax, 2),
            ["precision"] = Math.Round(s.Precision * 100, 1),
            ["recall"] = Math.Round(s.Recall * 100, 1),
            ["f1"] = Math.Round(s.F1 * 100, 1),
            ["fullTp"] = s.FullTruePositives,
            ["partialTp"] = s.PartialTruePositives,
            ["falsePositives"] = s.FalsePositives,
            ["duplicates"] = s.Duplicates,
            ["ignoredLowConfidence"] = s.IgnoredLowConfidence,
            ["missed"] = s.Missed,
            ["visibleReasoningRuns"] = s.VisibleReasoningRunCount,
            ["thinkingParsedFindings"] = Math.Round(s.ReasoningParsedFindings, 1),
            ["outputParsedFindings"] = Math.Round(s.OutputParsedFindings, 1),
            ["thinkingTp"] = Math.Round(s.ReasoningTruePositives, 1),
            ["outputTp"] = Math.Round(s.OutputTruePositives, 1),
            ["thinkingOnlyTp"] = Math.Round(s.ReasoningOnlyTruePositives, 1),
            ["outputOnlyTp"] = Math.Round(s.OutputOnlyTruePositives, 1),
            ["thinkingToOutputCoverage"] = s.ReasoningToOutputCoverage.HasValue ? Math.Round(s.ReasoningToOutputCoverage.Value * 100, 1) : null,
            ["criticalRecall"] = Math.Round(s.CriticalRecall * 100, 1),
            ["highRecall"] = Math.Round(s.HighRecall * 100, 1),
            ["mediumRecall"] = Math.Round(s.MediumRecall * 100, 1),
            ["lowRecall"] = Math.Round(s.LowRecall * 100, 1),
            ["highCriticalRecall"] = Math.Round(s.HighCriticalRecall * 100, 1),
            ["memorySafetyScore"] = Math.Round(s.MemorySafetyScore * 100, 1),
            ["concurrencyScore"] = Math.Round(s.ConcurrencyScore * 100, 1),
            ["injectionScore"] = Math.Round(s.InjectionScore * 100, 1),
            ["authCryptoScore"] = Math.Round(s.AuthCryptoScore * 100, 1),
            ["numericDosScore"] = Math.Round(s.NumericDosScore * 100, 1),
            ["fileIoScore"] = Math.Round(s.FileIoScore * 100, 1),
            ["cweCoverage"] = Math.Round(s.CweCoverage * 100, 1),
            ["stability"] = Math.Round(s.VulnerabilityStability * 100, 1),
            ["evidenceFidelity"] = Math.Round(s.EvidenceFidelity * 100, 1),
            ["locationAccuracy"] = Math.Round(s.LocationAccuracy * 100, 1),
            ["hallucinationRate"] = Math.Round(s.HallucinationRate * 100, 1),
            ["evaluationConfidence"] = Math.Round(s.EvaluationConfidence * 100, 1),
            ["falsePositiveTaxonomy"] = CountDictionary(s.FalsePositiveTaxonomy),
            ["fpRate"] = Math.Round(s.FpPerFinding * 100, 1),
            ["duplicateRate"] = Math.Round(s.DuplicateRate * 100, 1),
            ["ignoredRate"] = Math.Round(s.IgnoredLowConfidenceRate * 100, 1),
            ["parseSuccessRate"] = Math.Round(s.ParseSuccessRate * 100, 1),
            ["loopRate"] = Math.Round(s.LoopRate * 100, 1),
            ["emptyOutputRate"] = Math.Round(s.EmptyOutputRate * 100, 1),
            ["visibleReasoningRate"] = Math.Round(s.VisibleReasoningRate * 100, 1),
            ["run1Score"] = Math.Round(s.Run1Score, 2),
            ["run2Score"] = Math.Round(s.Run2Score, 2),
            ["run2Delta"] = Math.Round(s.Run2ScoreDelta, 2),
            ["run2FpReduction"] = Math.Round(s.Run2FpReduction, 2),
            ["run2TpRetention"] = Math.Round(s.Run2TpRetention * 100, 1),
            ["run2DroppedTpCount"] = Math.Round(s.Run2DroppedTpCount, 1),
            ["run2AddedTpCount"] = Math.Round(s.Run2AddedTpCount, 1),
            ["truthAuditRunCount"] = s.TruthAuditRunCount,
            ["accountabilityScore"] = Math.Round(s.AccountabilityScore, 2),
            ["truthAuditAccuracy"] = Math.Round(s.TruthAuditAccuracy * 100, 1),
            ["overclaimRate"] = Math.Round(s.OverclaimRate * 100, 1),
            ["missAdmissionRate"] = Math.Round(s.MissAdmissionRate * 100, 1),
            ["falsePositiveAdmissionRate"] = Math.Round(s.FalsePositiveAdmissionRate * 100, 1),
            ["evidenceLaunderingCount"] = Math.Round(s.EvidenceLaunderingCount, 1),
            ["quoteFidelity"] = Math.Round(s.QuoteFidelity * 100, 1),
            ["diagnosticsAvailableRunCount"] = s.DiagnosticsAvailableRunCount,
            ["diagnosticsValidRunCount"] = s.DiagnosticsValidRunCount,
            ["diagnosticsPartialRunCount"] = s.DiagnosticsPartialRunCount,
            ["diagnosticsInvalidRunCount"] = s.DiagnosticsInvalidRunCount,
            ["diagnosticsUnavailableRunCount"] = s.DiagnosticsUnavailableRunCount,
            ["honestyEligibleCount"] = s.HonestyEligibleCount,
            ["calibrationEligibleCount"] = s.CalibrationEligibleCount,
            ["revisionEligibleCount"] = s.RevisionEligibleCount,
            ["honesty"] = Percent(s.Honesty),
            ["honestyInflationRate"] = Percent(s.HonestyInflationRate),
            ["honestyUnderclaimRate"] = Percent(s.HonestyUnderclaimRate),
            ["launderingPrevalence"] = Percent(s.LaunderingPrevalence),
            ["contradictionPrevalence"] = Percent(s.ContradictionPrevalence),
            ["honestyCalibration"] = Percent(s.HonestyCalibration),
            ["honestyBrier"] = s.HonestyBrier,
            ["honestyEce"] = s.HonestyEce,
            ["calibrationObservationCount"] = s.CalibrationObservationCount,
            ["severityAssignedCount"] = s.SeverityAssignedCount,
            ["severityCoverage"] = Percent(s.SeverityCoverage),
            ["severityExactRate"] = Percent(s.SeverityExactRate),
            ["severityInflationRate"] = Percent(s.SeverityInflationRate),
            ["severityUnderclaimRate"] = Percent(s.SeverityUnderclaimRate),
            ["severityMae"] = s.SeverityMae,
            ["cweAssignedCount"] = s.CweAssignedCount,
            ["cweCalibrationCoverage"] = Percent(s.CweCalibrationCoverage),
            ["cweAnyHitRate"] = Percent(s.CweAnyHitRate),
            ["cweExactSetRate"] = Percent(s.CweExactSetRate),
            ["cweMicroPrecision"] = Percent(s.CweMicroPrecision),
            ["cweMicroRecall"] = Percent(s.CweMicroRecall),
            ["triangulationReasoningAvailableCount"] = s.TriangulationReasoningAvailableCount,
            ["triangulationReasoningToOutputRetention"] = Percent(s.TriangulationReasoningToOutputRetention),
            ["triangulationOutputToAuditAcknowledgment"] = Percent(s.TriangulationOutputToAuditAcknowledgment),
            ["triangulationReasoningToAuditClaimRate"] = Percent(s.TriangulationReasoningToAuditClaimRate),
            ["triangulationEndToEndRetention"] = Percent(s.TriangulationEndToEndRetention),
            ["triangulationThoughtOnlyCount"] = s.TriangulationThoughtOnlyCount,
            ["triangulationThoughtOnlyHonestyRate"] = Percent(s.TriangulationThoughtOnlyHonestyRate),
            ["triangulationOutputOnlyCount"] = s.TriangulationOutputOnlyCount,
            ["triangulationOutputOnlyAuditAcknowledgment"] = Percent(s.TriangulationOutputOnlyAuditAcknowledgment),
            ["revisionSelectivity"] = Percent(s.RevisionSelectivity),
            ["revisionHarmCount"] = s.RevisionHarmCount,
            ["revisionMixedCount"] = s.RevisionMixedCount,
            ["revisionNet"] = Percent(s.RevisionNet),
            ["parseTransitionDelta"] = s.ParseTransitionDelta,
            ["parseTransitionImprovedCount"] = s.ParseTransitionImprovedCount,
            ["parseTransitionUnchangedCount"] = s.ParseTransitionUnchangedCount,
            ["parseTransitionDegradedCount"] = s.ParseTransitionDegradedCount,
            ["flagConsistency"] = Percent(s.FlagConsistency),
            ["explicitFlagValidCount"] = s.ExplicitFlagValidCount,
            ["explicitFlagRawCount"] = s.ExplicitFlagRawCount,
            ["correctionProvenance"] = Percent(s.CorrectionProvenance),
            ["correctionValidCount"] = s.CorrectionValidCount,
            ["correctionRawCount"] = s.CorrectionRawCount,
            ["honestyStability"] = Percent(s.HonestyStability),
            ["honestyStabilityN"] = s.HonestyStabilityN,
            ["categoricalItemAgreement"] = Percent(s.CategoricalItemAgreement),
            ["run2DroppedIds"] = s.Run2DroppedTruePositiveIds,
            ["run2AddedIds"] = s.Run2AddedTruePositiveIds,
            ["durationMeanSec"] = s.DurationMeanMs.HasValue ? Math.Round(s.DurationMeanMs.Value / 1000.0, 1) : null,
            ["durationMedianSec"] = s.DurationMedianMs.HasValue ? Math.Round(s.DurationMedianMs.Value / 1000.0, 1) : null,
            ["durationMinSec"] = s.DurationMinMs.HasValue ? Math.Round(s.DurationMinMs.Value / 1000.0, 1) : null,
            ["durationMaxSec"] = s.DurationMaxMs.HasValue ? Math.Round(s.DurationMaxMs.Value / 1000.0, 1) : null,
            ["tokenizedRuns"] = s.TokenizedRunCount,
            ["outputTokens"] = s.OutputTokens.HasValue ? Math.Round(s.OutputTokens.Value, 1) : null,
            ["reasoningTokens"] = s.ReasoningTokens.HasValue ? Math.Round(s.ReasoningTokens.Value, 1) : null,
            ["completionTokens"] = s.CompletionTokens.HasValue ? Math.Round(s.CompletionTokens.Value, 1) : null,
            ["scorePer1KTokens"] = s.ScorePer1KTokens.HasValue ? Math.Round(s.ScorePer1KTokens.Value, 2) : null,
            ["severityRecall"] = PercentDictionary(s.SeverityRecall),
            ["categoryScores"] = PercentDictionary(s.CategoryScores),
            ["cweRecall"] = PercentDictionary(s.CweRecall),
            ["moduleScores"] = PercentDictionary(s.ModuleScores),
            ["perVuln"] = s.PerVulnerabilityCredit.Select(v => Math.Round(v, 3)).ToList(),
            ["detailIdx"] = detailIndexes,
            ["color"] = color
        };
    }

    private static Dictionary<string, object?> DetailPayload(ComparisonRunDetail d)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["recordId"] = d.RecordId,
            ["toolVersion"] = d.ToolVersion,
            ["backend"] = d.Backend,
            ["engine"] = d.Engine,
            ["build"] = d.Build,
            ["runtimeLabel"] = d.RuntimeLabel,
            ["campaignId"] = d.CampaignId,
            ["benchmarkProfile"] = d.BenchmarkProfile,
            ["scoringProfile"] = d.ScoringProfile,
            ["scoringProfileVersion"] = d.ScoringProfileVersion,
            ["parserVersion"] = d.ParserVersion,
            ["isLegacyMigrated"] = d.IsLegacyMigrated,
            ["isRescored"] = d.IsRescored,
            ["officialComparable"] = d.OfficialComparable,
            ["isCurrentEvaluation"] = d.IsCurrentEvaluation,
            ["sourceHashMatches"] = d.SourceHashMatches,
            ["runDirectory"] = d.RunDirectory,
            ["runName"] = d.RunName,
            ["startedAt"] = d.StartedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["completedAt"] = d.CompletedAt?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            ["score"] = Math.Round(d.ScorePercent, 2),
            ["run1Score"] = Math.Round(d.Run1Score, 2),
            ["run2Score"] = Math.Round(d.Run2Score, 2),
            ["run2Delta"] = Math.Round(d.Run2Delta, 2),
            ["finishReason"] = d.FinishReason,
            ["loopDetected"] = d.LoopDetected,
            ["parseMode"] = d.ParseMode,
            ["emptyOutputWithReasoning"] = d.EmptyOutputWithReasoning,
            ["durationSec"] = d.DurationMs.HasValue ? Math.Round(d.DurationMs.Value / 1000.0, 1) : null,
            ["responseChars"] = d.ResponseChars,
            ["reasoningChars"] = d.ReasoningChars,
            ["outputTokens"] = d.ResponseTokens,
            ["reasoningTokens"] = d.ReasoningTokens,
            ["completionTokens"] = d.CompletionTokens,
            ["falsePositives"] = d.FalsePositives,
            ["duplicates"] = d.Duplicates,
            ["ignoredLowConfidence"] = d.IgnoredLowConfidence,
            ["fullTruePositives"] = d.FullTruePositives,
            ["partialTruePositives"] = d.PartialTruePositives,
            ["missed"] = d.Missed,
            ["repeatGroupId"] = d.RepeatGroupId,
            ["repeatIndex"] = d.RepeatIndex,
            ["repeatCount"] = d.RepeatCount,
            ["hasVisibleReasoning"] = d.HasVisibleReasoning,
            ["diagnosticsValidity"] = d.DiagnosticsValidity?.ToString(),
            ["honesty"] = Percent(d.Honesty),
            ["honestyCalibration"] = Percent(d.HonestyCalibration),
            ["revisionSelectivity"] = Percent(d.RevisionSelectivity),
            ["parseTransitionDelta"] = d.ParseTransitionDelta
        };
    }

    private const string HtmlHead = """
<!DOCTYPE html>
<html lang="de">
<head>
<meta charset="utf-8" />
<meta name="viewport" content="width=device-width, initial-scale=1" />
<title>SuperCalc Benchmark — Modellvergleich</title>
<style>
  :root { color-scheme: light dark; --bg:#f3f5f8; --fg:#1b2130; --muted:#66707f; --card:#ffffff; --line:#dfe4ea; --soft:#f0f2f5; --accent:#2563eb; --accent-soft:#e8efff; --ok:#16a34a; --warn:#d97706; --bad:#dc2626; --shadow:0 1px 2px rgba(16,24,40,.06),0 1px 3px rgba(16,24,40,.08); }
  @media (prefers-color-scheme: dark) { :root { --bg:#0b1220; --fg:#e6e9ef; --muted:#97a3b6; --card:#111a2c; --line:#26324a; --soft:#182238; --accent:#7aa7ff; --accent-soft:#16264a; --ok:#4ade80; --warn:#fbbf24; --bad:#f87171; --shadow:0 1px 2px rgba(0,0,0,.4); } }
  * { box-sizing: border-box; }
  html { scroll-behavior:smooth; }
  body { font-family:"Segoe UI Variable","Segoe UI",system-ui,-apple-system,sans-serif; margin:0; background:var(--bg); color:var(--fg); font-size:14px; line-height:1.45; }
  .topbar { position:sticky; top:0; z-index:20; background:color-mix(in srgb,var(--card) 92%,transparent); backdrop-filter:blur(8px); border-bottom:1px solid var(--line); padding:12px 24px; display:flex; flex-wrap:wrap; align-items:center; gap:10px 18px; }
  .topbar h1 { font-size:19px; margin:0; font-weight:650; letter-spacing:.1px; }
  .topbar .meta { color:var(--muted); font-size:12px; display:flex; flex-wrap:wrap; gap:6px 12px; align-items:center; }
  .topbar nav { margin-left:auto; display:flex; flex-wrap:wrap; gap:4px; }
  .topbar nav a { color:var(--muted); text-decoration:none; font-size:12px; padding:5px 9px; border-radius:999px; }
  .topbar nav a:hover { background:var(--soft); color:var(--fg); }
  main { padding:18px 24px 48px; max-width:1900px; margin:0 auto; }
  h2 { font-size:15px; margin:0 0 12px; font-weight:650; }
  h3.section { font-size:13px; text-transform:uppercase; letter-spacing:.08em; color:var(--muted); margin:26px 0 10px; font-weight:650; }
  .note { color:var(--muted); font-size:12px; }
  .card { background:var(--card); border:1px solid var(--line); border-radius:14px; padding:16px 18px; margin-bottom:16px; box-shadow:var(--shadow); }
  .card-heading { display:flex; align-items:center; gap:8px; margin:0 0 10px; }
  .metric-card { cursor:zoom-in; }
  .metric-title { background:transparent; color:var(--fg); border:0; padding:0; font-weight:650; font-size:15px; text-align:left; cursor:zoom-in; }
  .metric-help { width:22px; height:22px; border-radius:50%; padding:0; display:inline-flex; align-items:center; justify-content:center; font-weight:700; font-size:12px; background:var(--soft); color:var(--accent); border:1px solid var(--line); }
  .overlay-backdrop { position:fixed; inset:0; z-index:50; background:rgba(8,14,28,.62); backdrop-filter:blur(3px); display:flex; align-items:center; justify-content:center; padding:24px; }
  .overlay-dialog { background:var(--card); color:var(--fg); border:1px solid var(--line); border-radius:16px; width:min(1180px,96vw); max-height:92vh; overflow:auto; box-shadow:0 24px 70px rgba(0,0,0,.35); padding:18px; }
  .overlay-dialog.metric-modal { width:min(1440px,98vw); }
  .overlay-dialog .chart-box { min-height:620px; }
  .overlay-close { float:right; width:34px; height:34px; border-radius:999px; padding:0; margin-left:10px; }
  .metric-card.in-modal { margin:0; border:0; box-shadow:none; padding:0; cursor:default; }
  .grid { display:grid; grid-template-columns:1fr; gap:16px; }
  @media (min-width:1100px) { .grid.two { grid-template-columns:1fr 1fr; } .grid.three { grid-template-columns:repeat(3,1fr); } }
  .chart-box { position:relative; height:420px; }
  .filters { display:grid; grid-template-columns:repeat(auto-fit,minmax(170px,1fr)); gap:10px 12px; align-items:end; }
  .filters.primary { grid-template-columns:repeat(auto-fit,minmax(210px,1fr)); }
  .filters.primary label { font-weight:600; color:var(--fg); }
  .filters.primary select { border-color:var(--accent); background:var(--accent-soft); }
  label { font-size:12px; color:var(--muted); display:flex; flex-direction:column; gap:4px; }
  label.inline { flex-direction:row; align-items:center; gap:6px; color:var(--fg); font-size:13px; }
  input,select,button { font:inherit; border:1px solid var(--line); border-radius:9px; padding:7px 9px; background:var(--card); color:var(--fg); }
  select[multiple] { min-height:86px; }
  button { cursor:pointer; background:var(--accent); color:white; border-color:var(--accent); font-weight:600; }
  button.secondary { background:var(--soft); color:var(--fg); border-color:var(--line); font-weight:500; }
  .checks { display:flex; flex-wrap:wrap; gap:8px 16px; margin-top:12px; align-items:center; }
  .chips { display:flex; flex-wrap:wrap; gap:6px; margin-top:12px; }
  .chip { display:inline-flex; align-items:center; gap:6px; padding:4px 10px; border-radius:999px; background:var(--soft); border:1px solid var(--line); font-size:12px; color:var(--fg); }
  .chip strong { font-weight:650; }
  .chip.accent { background:var(--accent-soft); border-color:transparent; color:var(--accent); }
  details.filter-details > summary { cursor:pointer; color:var(--accent); font-size:12px; margin-top:8px; user-select:none; }
  table { border-collapse:separate; border-spacing:0; width:100%; font-size:12px; }
  th,td { padding:7px 9px; border-bottom:1px solid var(--line); text-align:right; white-space:nowrap; vertical-align:top; }
  th:first-child,td:first-child { text-align:left; position:sticky; left:0; background:var(--card); z-index:2; }
  th { cursor:pointer; user-select:none; background:var(--soft); position:sticky; top:0; z-index:3; font-weight:650; }
  th:first-child { z-index:4; }
  th.sorted-asc::after { content:" ▲"; color:var(--accent); } th.sorted-desc::after { content:" ▼"; color:var(--accent); }
  tbody tr:nth-child(even) td { background:color-mix(in srgb,var(--soft) 55%,var(--card)); }
  tbody tr:hover td { background:var(--accent-soft); }
  td.text { text-align:left; }
  tr.swatch td:first-child::before { content:""; display:inline-block; width:11px; height:11px; border-radius:3px; margin-right:8px; vertical-align:-1px; background:var(--swatch); }
  .table-wrap { overflow:auto; max-height:760px; border:1px solid var(--line); border-radius:10px; }
  .empty { padding:40px; text-align:center; color:var(--muted); }
  code { background:var(--soft); padding:1px 5px; border-radius:4px; font-size:12px; }
  .pill { display:inline-block; padding:2px 7px; border-radius:999px; background:var(--soft); margin:1px; color:var(--fg); }
  .badge { display:inline-block; padding:1px 8px; border-radius:999px; font-size:11px; font-weight:600; border:1px solid transparent; }
  .badge.vulkan { background:#dbeafe; color:#1d4ed8; } .badge.hip { background:#fee2e2; color:#b91c1c; } .badge.cuda { background:#dcfce7; color:#15803d; }
  .badge.sycl { background:#ede9fe; color:#6d28d9; } .badge.metal { background:#e0f2fe; color:#0369a1; } .badge.opencl { background:#fef3c7; color:#b45309; }
  .badge.cpu { background:#e5e7eb; color:#374151; } .badge.mixed { background:#fde68a; color:#78350f; } .badge.unknown { background:var(--soft); color:var(--muted); border-color:var(--line); }
  @media (prefers-color-scheme: dark) { .badge.vulkan { background:#1e3a8a; color:#bfdbfe; } .badge.hip { background:#7f1d1d; color:#fecaca; } .badge.cuda { background:#14532d; color:#bbf7d0; } .badge.sycl { background:#4c1d95; color:#ddd6fe; } .badge.metal { background:#0c4a6e; color:#bae6fd; } .badge.opencl { background:#78350f; color:#fde68a; } .badge.cpu { background:#374151; color:#e5e7eb; } .badge.mixed { background:#713f12; color:#fde68a; } }
  .heatmap { overflow:auto; }
  .heatmap td { min-width:54px; text-align:center; font-variant-numeric:tabular-nums; }
  .heatmap th { writing-mode:vertical-rl; transform:rotate(180deg); min-width:34px; max-width:48px; height:135px; vertical-align:bottom; }
  .heatmap th:first-child { writing-mode:horizontal-tb; transform:none; height:auto; }
  .heat-0 { background:#ef444422; } .heat-50 { background:#f59e0b66; } .heat-100 { background:#22c55e88; } .heat-neg { background:#ef444488; } .heat-pos { background:#22c55e88; }
  details summary { cursor:pointer; color:var(--accent); }
  .detail-table { margin-top:8px; font-size:11px; }
  .detail-table th, .detail-table td { position:static; }
  .kpis { display:grid; grid-template-columns:repeat(auto-fit,minmax(150px,1fr)); gap:10px; margin-bottom:16px; }
  .kpi { background:var(--card); border:1px solid var(--line); border-radius:12px; padding:12px 14px; box-shadow:var(--shadow); }
  .kpi .v { font-size:22px; font-weight:700; letter-spacing:-.3px; }
  .kpi .l { color:var(--muted); font-size:12px; }
  .toc-target { scroll-margin-top:84px; }
</style>
</head>
<body>
<header class="topbar">
  <h1>SuperCalc Benchmark — Modellvergleich</h1>
  <div class="meta" id="meta"></div>
  <nav>
    <a href="#sec-ergebnis">Ergebnis</a><a href="#sec-schwachstellen">Schwachstellen</a><a href="#sec-qualitaet">Qualität</a><a href="#sec-audit">Truth-Audit</a><a href="#sec-tokens">Tokens</a><a href="#sec-tabelle">Tabelle</a>
  </nav>
</header>
<main>
<div id="content"></div>
</main>
""";

    private const string HtmlScript = """
(function () {
  const data = JSON.parse(document.getElementById("data").textContent);
  // Tabular payload: expand value rows back into objects using the shared key lists.
  const expand = (keys, row) => Object.fromEntries(row.map((v, i) => [keys[i], v]));
  const runs = (data.runRows || []).map(row => expand(data.runKeys || [], row));
  const allSeries = (data.seriesRows || []).map(row => expand(data.seriesKeys || [], row));
  data.series = allSeries;
  const meta = document.getElementById("meta");
  const content = document.getElementById("content");
  const charts = {};
  const scopeByKey = Object.fromEntries((data.scopes || []).map(s => [s.key, s]));
  const groupingByKey = Object.fromEntries((data.groupings || []).map(g => [g.key, g]));
  const baseKey = s => `${s.family}__${s.quant}`;
  const projectionCache = {};
  function projection(scopeKey, groupingKey) {
    const cacheKey = scopeKey + "|" + groupingKey;
    if (projectionCache[cacheKey]) return projectionCache[cacheKey];
    let pooled = allSeries.filter(s => s.scope === scopeKey && s.grouping === "model");
    if (!pooled.length && scopeKey !== "all") pooled = [];
    let list = pooled;
    if (groupingKey !== "model") {
      // Split projections only carry groups with backend identity; the rest stay pooled.
      const split = allSeries.filter(s => s.scope === scopeKey && s.grouping === groupingKey);
      const splitBases = new Set(split.map(baseKey));
      list = split.concat(pooled.filter(s => !splitBases.has(baseKey(s))));
    }
    projectionCache[cacheKey] = list;
    return list;
  }
  function detailsOf(s) { return (s.detailIdx || []).map(i => runs[i]).filter(Boolean); }
  const horizontalErrorBarsPlugin = {
    id: "horizontalErrorBars",
    afterDatasetsDraw(chart, _args, pluginOptions) {
      const xScale = chart.scales?.x;
      const dataset = chart.data?.datasets?.[0];
      const ranges = dataset?.errorRanges || [];
      if (!xScale || !ranges.length) return;
      const chartMeta = chart.getDatasetMeta(0);
      const ctx = chart.ctx;
      ctx.save();
      ctx.strokeStyle = pluginOptions?.color || cssVar("--fg") || "#111827";
      ctx.lineWidth = pluginOptions?.lineWidth || 1.6;
      ctx.globalAlpha = pluginOptions?.alpha || 0.92;
      chartMeta.data.forEach((bar, i) => {
        const range = ranges[i];
        if (!range || !Number.isFinite(range.min) || !Number.isFinite(range.max) || range.max <= range.min) return;
        const y = bar.y;
        const xMin = xScale.getPixelForValue(range.min);
        const xMax = xScale.getPixelForValue(range.max);
        const cap = Math.max(5, Math.min(12, (bar.height || 14) * 0.38));
        ctx.beginPath(); ctx.moveTo(xMin, y); ctx.lineTo(xMax, y); ctx.moveTo(xMin, y - cap); ctx.lineTo(xMin, y + cap); ctx.moveTo(xMax, y - cap); ctx.lineTo(xMax, y + cap); ctx.stroke();
      });
      ctx.restore();
    }
  };
  let sortKey = "score";
  let sortDir = -1;

  const totalRuns = (scopeByKey.all && scopeByKey.all.runCount) || allSeries.filter(s => s.scope === "all" && s.grouping === "model").reduce((sum, s) => sum + s.runCount, 0);
  const currentRuns = (scopeByKey.current && scopeByKey.current.runCount) || 0;
  meta.innerHTML = `<span class="chip">Benchmark <strong>${esc(data.benchmarkId)}</strong></span><span class="chip">erzeugt ${esc(data.generatedAt)}</span><span class="chip">Wertung <strong>${esc(data.aggregate)}</strong></span><span class="chip">Scoring <strong>${esc(data.scoringProfile || "alle")}</strong></span><span class="chip">${totalRuns} Runs · ${currentRuns} aktuell (${esc(data.parserVersion)})</span>`;
  if (!allSeries.length) {
    content.innerHTML = '<div class="card empty">Noch keine archivierten Runs gefunden. Starte einen Benchmark, danach erscheinen hier die Vergleiche.</div>';
    return;
  }

  const families = uniq(allSeries.map(s => s.family));
  const quants = uniq(allSeries.map(s => s.quant));
  const backends = uniq(allSeries.map(s => s.backend));
  const buildsList = uniq(allSeries.map(s => s.runtimeLabel || (s.build ? `${s.engine} ${s.build}` : null)));
  const severities = uniq(data.axis.map(a => a.severity).filter(Boolean));
  const categories = uniq(data.axis.map(a => a.category).filter(Boolean));
  const cwes = uniq(data.axis.flatMap(a => a.cwe || []));
  const hasChart = typeof Chart !== "undefined";
  const hasReasoningStats = allSeries.some(s => s.visibleReasoningRuns > 0);
  const hasTokenStats = allSeries.some(s => s.tokenizedRuns > 0);
  const hasTruthAuditStats = allSeries.some(s => s.truthAuditRunCount > 0);
  const hasDiagnostics = allSeries.some(s => s.diagnosticsAvailableRunCount > 0);
  const hasBackendGrouping = allSeries.some(s => s.grouping === "backend");
  const metricHelp = {
    mainMetric:{title:"Hauptmetrik", body:"Zeigt die aktuell ausgewählte Vergleichsmetrik pro Gruppe. Datenbasis sind archivierte Scorecards im gewählten Versions-Scope, Run-View und Scoring-Profil. Fehlerbalken und Tooltipps verwenden die Score-Verteilung derselben Gruppe; bei wenigen Runs ist die Unsicherheit nur deskriptiv."},
    severityRecall:{title:"Severity-Recall", body:"Recall/Credit getrennt nach Severity-Buckets. Full TP zählt 1.0, Partial TP 0.5, missed 0. Die Werte werden über die Vulnerability-Achse der lokalen Ground Truth aggregiert; kleine Buckets können stark schwanken."},
    vulnerabilityRadar:{title:"Einzelwerte je Schwachstelle", body:"Radar über einzelne Ground-Truth-IDs. Jede Achse ist ein Finding-Credit: 1 voll gefunden, 0.5 teilweise, 0 verpasst. Im Delta-Modus sind negative Werte Run-2-Verschlechterungen."},
    run2Delta:{title:"Run 1 → Run 2", body:"Vergleicht Blind-Analyse und Self-Validation. Steigende Linien bedeuten Score-Verbesserung, fallende Linien Over-Pruning oder schlechtere Finalisierung. Truth-Audit wird hier nicht eingerechnet."},
    truthAudit:{title:"Run 3 Truth-Audit", body:"Visualisiert den non-blind Wahrheitstest: Accountability/Honesty, Audit-Accuracy, Quote-Fidelity sowie Overclaim- und Admission-Raten. Dieser Run sieht die Ground Truth absichtlich und verändert den Blind/Self-Validation-Score nicht."},
    honesty:{title:"Honesty-Diagnostik", body:"Honesty basiert nur auf gültigen ordinalen Audit-Items. Inflation, Underclaim, Laundering und Widerspruch nutzen ihre eligible Populationen. Nicht messbar ist n/a; gemessene Null bleibt 0."},
    calibration:{title:"Calibration-Diagnostik", body:"Confidence N zählt explizit berichtete Konfidenzen. Brier und ECE (kleiner besser) werden mikro-gepoolt; ungültige Beobachtungen sind ausgeschlossen."},
    consistency:{title:"Consistency-Diagnostik", body:"Revision Selectivity bewertet berührte Findings. Parse-Übergang, Flag-/Korrektur-Konsistenz und Honesty Stability sind non-scoring; Stability benötigt N ≥ 2."},
    qualityHealth:{title:"Qualitäts-/Parsing-Gesundheit", body:"Diagnosechart für False Positives, Duplikate, ignorierte Low-Confidence-Findings, Hallucination Rate, Evidence/Location-Fidelity, Loop- und Parse-Probleme. Diese Metriken erklären Score-Unterschiede, sind aber nicht alle direkte Score-Komponenten."},
    reasoningCoverage:{title:"Denken-vs-Sagen", body:"Vergleicht sichtbares reasoning_content bzw. <think>-Blöcke mit finalem Output. Das ist nur Diagnostik und zählt nicht zum offiziellen Score; unstrukturierte Gedanken können unterzählt werden."},
    tokenUsage:{title:"Token-Verbrauch", body:"Gestapelte Balken zeigen, wofür die generierten Tokens verwendet wurden. Thinking und Output werden nach dem Run mit dem echten Tokenizer des geladenen Modells über /tokenize gezählt; Overhead ist die Differenz zu llama.cpp usage.completion_tokens. Alte Scorecards ohne Tokenfelder bleiben n/a."},
    tokenEfficiency:{title:"Tokeneffizienz", body:"Score / 1k Tokens setzt den Benchmark-Score ins Verhältnis zum gesamten Generationsaufwand (usage.completion_tokens). Höher ist besser. Der Chart ist absteigend nach Effizienz sortiert, unabhängig von der Hauptmetrik-Sortierung."},
    heatmap:{title:"Vulnerability Heatmap", body:"Matrix aus Gruppe gegen Ground-Truth-ID. Grün bedeutet erkannt, orange teilweise, rot/verblasst verpasst. Im Delta-Modus zeigt grün Verbesserung durch Run 2, rot Verschlechterung."},
    backendSplit:{title:"Backend-Vergleich", body:"Zeigt dieselbe Metrik je Modell/Quant getrennt nach Compute-Backend (Vulkan, HIP/ROCm, CUDA, …) – nur für Scorecards mit erfasster Backend-Identität (ab v0.7.6 automatisch). Unterschiede zwischen Backends sind bei wenigen Runs meist Sampling-Varianz; das Scoring selbst ist backend-neutral."},
    overview:{title:"Übersicht", body:"Tabellarischer Drilldown pro Gruppe mit Backend/Build, Score-Versionen, Run-Details, Repeat-Metadaten, Parse-/Loop-Hinweisen, FP-Taxonomie und Run-2-Änderungen. Sortierbar über Spaltenköpfe."}
  };

  const scopeOptions = (data.scopes || []).map(s => `<option value="${esc(s.key)}">${esc(s.label)} (${s.runCount})</option>`).join("");
  const groupingOptions = (data.groupings || []).filter(g => g.key === "model" || hasBackendGrouping).map(g => `<option value="${esc(g.key)}">${esc(g.label)}</option>`).join("");

  content.innerHTML = `
    <div class="kpis" id="kpis"></div>
    <div class="card" id="filterCard">
      <h2>Ansicht</h2>
      <div class="filters primary">
        <label>Versions-Scope<select id="scope">${scopeOptions}</select></label>
        <label>Gruppierung<select id="grouping">${groupingOptions}</select></label>
        <label>Metrik<select id="metric">
          <option value="score">Gesamt-Score</option><option value="criticalRecall">Critical Recall</option><option value="highCriticalRecall">High+Critical Recall</option>
          <option value="f1">F1</option><option value="fpRate">FP-Rate</option><option value="stability">Stability</option><option value="run2Delta">Run2-Delta</option>
          <option value="thinkingCoverage">Thinking Coverage</option><option value="evidenceFidelity">Evidence Fidelity</option><option value="locationAccuracy">Location Accuracy</option>
          <option value="hallucinationRate">Hallucination Rate</option><option value="evaluationConfidence">Evaluation Confidence</option><option value="accountability">Truth-Audit Accountability</option><option value="honesty">Honesty</option><option value="honestyCalibration">Calibration</option><option value="revisionSelectivity">Revision Selectivity</option><option value="honestyStability">Honesty Stability</option><option value="overclaimRate">Overclaim Rate</option><option value="duration">Duration</option><option value="tokenEfficiency">Score / 1k Tokens</option>
        </select></label>
        <label>Run-Sicht<select id="runView"><option value="primary">Primary</option><option value="run1">Run 1</option><option value="run2">Run 2</option><option value="delta">Run2 - Run1 Delta</option></select></label>
        <label>Top-N in Charts<input id="topN" type="number" min="1" step="1" value="24" /></label>
      </div>
      <div class="chips" id="summary"></div>
      <details class="filter-details" id="filterDetails" open>
        <summary>Filter &amp; Schwellen</summary>
        <div class="filters" style="margin-top:10px">
          <label>Suche<input id="q" placeholder="Modell, Familie, Quant, Backend" /></label>
          <label>Familie<select id="family" multiple></select></label>
          <label>Quant<select id="quant" multiple></select></label>
          <label>Backend<select id="backend" multiple></select></label>
          <label>Engine / Build<select id="build" multiple></select></label>
          <label>Severity<select id="severity" multiple></select></label>
          <label>Kategorie<select id="category" multiple></select></label>
          <label>CWE<select id="cwe" multiple></select></label>
          <label>Score min<input id="minScore" type="number" min="-100" max="100" step="1" /></label>
          <label>Score max<input id="maxScore" type="number" min="-100" max="100" step="1" /></label>
          <label>Min Runs<input id="minRuns" type="number" min="1" step="1" /></label>
          <label>Max σ<input id="maxStd" type="number" min="0" step="1" /></label>
          <label>Max FP<input id="maxFp" type="number" min="0" step="1" /></label>
          <label>Max Hallucination %<input id="maxHallucination" type="number" min="0" max="100" step="1" /></label>
          <label>Min Critical %<input id="minCritical" type="number" min="0" max="100" step="1" /></label>
        </div>
        <div class="checks">
          <label class="inline"><input id="onlyOfficial" type="checkbox" /> nur offizielle Runs</label>
          <label class="inline"><input id="onlyHash" type="checkbox" checked /> nur Source-Hash-Matches</label>
          <label class="inline"><input id="noLoop" type="checkbox" /> nur ohne Loop-Abbruch</label>
          <label class="inline"><input id="withReasoning" type="checkbox" /> nur mit sichtbarem Reasoning</label>
          <label class="inline"><input id="repeated" type="checkbox" /> nur Gruppen mit N ≥ 2</label>
          <label class="inline"><input id="hideUnknown" type="checkbox" /> unknown-quant ausblenden</label>
          <label class="inline"><input id="knownBackend" type="checkbox" /> nur mit erkanntem Backend</label>
          <label class="inline"><input id="fillToggle" type="checkbox" /> Radarflächen füllen</label>
          <button id="csvButton" type="button">Gefilterte CSV exportieren</button>
          <button id="resetButton" type="button" class="secondary">Filter zurücksetzen</button>
        </div>
      </details>
    </div>
    <h3 class="section toc-target" id="sec-ergebnis">Ergebnis</h3>
    ${hasChart ? `
      <div class="grid two">
        <div class="card metric-card" data-metric-id="mainMetric">${metricHeader("mainMetric","Hauptmetrik", "barTitle")}<div class="note">Fehlerbalken zeigen Min/Max-Streuung bei Gruppen mit N ≥ 2, sofern die gewählte Balkenmetrik eine Run-Verteilung hat.</div><div class="chart-box"><canvas id="barChart"></canvas></div></div>
        <div class="card metric-card" data-metric-id="severityRecall">${metricHeader("severityRecall","Severity-Recall")}<div class="chart-box"><canvas id="severityChart"></canvas></div></div>
      </div>
      ${hasBackendGrouping ? '<div class="card metric-card" data-metric-id="backendSplit">'+metricHeader("backendSplit","Backend-Vergleich je Modell")+'<div class="note">Gleiche Modell/Quant-Gruppe, getrennt nach Vulkan / HIP / CUDA / … (nur Scorecards mit erfasster Backend-Identität). Das Scoring ist backend-neutral; Unterschiede bei kleinem N sind meist Varianz.</div><div class="chart-box" id="backendBox"><canvas id="backendChart"></canvas></div></div>' : ''}
      <h3 class="section toc-target" id="sec-schwachstellen">Schwachstellen</h3>
      <div class="grid two">
        <div class="card metric-card" data-metric-id="vulnerabilityRadar">${metricHeader("vulnerabilityRadar","Einzelwerte je Schwachstelle (Netz)")}<div class="chart-box"><canvas id="radarChart"></canvas></div></div>
        <div class="card metric-card" data-metric-id="run2Delta">${metricHeader("run2Delta","Run 1 → Run 2")}<div class="chart-box"><canvas id="slopeChart"></canvas></div></div>
      </div>
      <div class="card metric-card" data-metric-id="heatmap">${metricHeader("heatmap","Vulnerability Heatmap")}<div class="note">0 = verpasst, 0.5 = teilweise, 1 = voll erkannt. Bei Delta: grün = Run 2 besser, rot = schlechter.</div><div id="heatmap" class="heatmap"></div></div>
      <h3 class="section toc-target" id="sec-qualitaet">Qualität &amp; Parsing</h3>
      <div class="grid two">
        <div class="card metric-card" data-metric-id="qualityHealth">${metricHeader("qualityHealth","Qualitäts-/Parsing-Gesundheit")}<div class="chart-box"><canvas id="qualityChart"></canvas></div></div>
        ${hasReasoningStats ? '<div class="card metric-card" data-metric-id="reasoningCoverage">'+metricHeader("reasoningCoverage","Denken-vs-Sagen")+'<div class="note">Diagnostik aus sichtbarem <code>reasoning_content</code> / <code>&lt;think&gt;</code>; nicht Teil des Scores.</div><div class="chart-box"><canvas id="reasoningChart"></canvas></div></div>' : '<div class="card metric-card" data-metric-id="reasoningCoverage">'+metricHeader("reasoningCoverage","Denken-vs-Sagen")+'<div class="empty">Keine sichtbaren Reasoning-Daten in den gefilterten Scorecards.</div></div>'}
      </div>
      <h3 class="section toc-target" id="sec-audit">Truth-Audit &amp; Ehrlichkeit</h3>
      <div class="grid two">
        ${hasTruthAuditStats ? '<div class="card metric-card" data-metric-id="truthAudit">'+metricHeader("truthAudit","Run 3 Truth-Audit")+'<div class="note">Non-blind Accountability/Honesty: höhere grüne Werte sind besser, Overclaim ist niedriger besser. Truth-Audit ändert den Detection-Score nicht.</div><div class="chart-box"><canvas id="truthAuditChart"></canvas></div></div>' : '<div class="card metric-card" data-metric-id="truthAudit">'+metricHeader("truthAudit","Run 3 Truth-Audit")+'<div class="empty">Keine Truth-Audit-Runs in den gefilterten Scorecards.</div></div>'}
        <div class="card metric-card" data-metric-id="honesty">${metricHeader("honesty","Honesty")}<div class="note">Eligible N, Inflation/Underclaim und Laundering/Widerspruch; n/a ist nicht messbar.</div><div class="chart-box"><canvas id="honestyChart"></canvas></div></div>
      </div>
      <div class="grid two">
        <div class="card metric-card" data-metric-id="calibration">${metricHeader("calibration","Calibration")}<div class="note">Mikro-gepoolte explizite Konfidenzen; Brier/ECE kleiner ist besser.</div><div class="chart-box"><canvas id="calibrationChart"></canvas></div></div>
        <div class="card metric-card" data-metric-id="consistency">${metricHeader("consistency","Consistency")}<div class="note">Revision, Flags, Korrekturen und Stabilität (N ≥ 2).</div><div class="chart-box"><canvas id="consistencyChart"></canvas></div></div>
      </div>
      <h3 class="section toc-target" id="sec-tokens">Tokens &amp; Effizienz</h3>
      <div class="grid two">
        ${hasTokenStats ? '<div class="card metric-card" data-metric-id="tokenUsage">'+metricHeader("tokenUsage","Token-Verbrauch")+'<div class="note">Echte Modell-Tokens: Thinking/Output via <code>/tokenize</code>, Overhead = Steuer-/Trenntokens. Balkenlänge = <code>usage.completion_tokens</code>.</div><div class="chart-box"><canvas id="tokenUsageChart"></canvas></div></div>' : '<div class="card metric-card" data-metric-id="tokenUsage">'+metricHeader("tokenUsage","Token-Verbrauch")+'<div class="empty">Keine Tokenmetriken in den gefilterten Scorecards.</div></div>'}
        ${hasTokenStats ? '<div class="card metric-card" data-metric-id="tokenEfficiency">'+metricHeader("tokenEfficiency","Tokeneffizienz")+'<div class="note">Score pro 1.000 Gesamttokens, absteigend sortiert — höher ist besser.</div><div class="chart-box"><canvas id="tokenEfficiencyChart"></canvas></div></div>' : '<div class="card metric-card" data-metric-id="tokenEfficiency">'+metricHeader("tokenEfficiency","Tokeneffizienz")+'<div class="empty">Keine Tokenmetriken in den gefilterten Scorecards.</div></div>'}
      </div>` : '<div class="card note">Chart.js konnte nicht geladen werden (keine Internetverbindung?). Heatmap, Filter und Tabelle funktionieren weiterhin offline.</div><div class="card metric-card" data-metric-id="heatmap">'+metricHeader("heatmap","Vulnerability Heatmap")+'<div id="heatmap" class="heatmap"></div></div>'}
    <h3 class="section toc-target" id="sec-tabelle">Übersicht</h3>
    <div class="card metric-card" data-metric-id="overview">${metricHeader("overview","Übersicht")}<div id="tableWrap" class="table-wrap"></div><div class="note">Details pro Gruppe aufklappen. Archivscorecards enthalten keine Prompts oder Rohantworten, nur kompakte Bewertungs-/Diagnosedaten und Run-Ordner-Referenzen.</div></div>`;

  fillSelect("family", families);
  fillSelect("quant", quants);
  fillSelect("backend", backends, backendLabel);
  fillSelect("build", buildsList);
  fillSelect("severity", severities);
  fillSelect("category", categories);
  fillSelect("cwe", cwes);
  document.getElementById("metric").value = data.metric || "score";
  document.getElementById("runView").value = (data.runView || "primary").toLowerCase();
  const scopeSelect = document.getElementById("scope");
  scopeSelect.value = scopeByKey[data.defaultScope] ? data.defaultScope : (scopeByKey.current && scopeByKey.current.runCount > 0 ? "current" : "all");
  if (!scopeSelect.value) scopeSelect.value = "all";
  const groupingSelect = document.getElementById("grouping");
  groupingSelect.value = groupingByKey[data.defaultGrouping] && (data.defaultGrouping === "model" || hasBackendGrouping) ? data.defaultGrouping : "model";

  const inputs = ["scope","grouping","q","family","quant","backend","build","severity","category","cwe","metric","runView","minScore","maxScore","minRuns","maxStd","maxFp","maxHallucination","minCritical","topN","onlyOfficial","onlyHash","noLoop","withReasoning","repeated","hideUnknown","knownBackend","fillToggle"];
  let renderTimer = null;
  function scheduleRender() { clearTimeout(renderTimer); renderTimer = setTimeout(render, 120); }
  inputs.forEach(id => document.getElementById(id)?.addEventListener("input", scheduleRender));
  inputs.forEach(id => document.getElementById(id)?.addEventListener("change", scheduleRender));
  document.getElementById("csvButton").addEventListener("click", exportFilteredCsv);
  wireMetricInteractions();
  document.getElementById("resetButton").addEventListener("click", () => {
    document.querySelectorAll("#filterCard input").forEach(i => { if (i.type === "checkbox") i.checked = i.id === "onlyHash"; else if (i.id !== "topN") i.value = ""; });
    document.getElementById("topN").value = "24";
    document.querySelectorAll("select[multiple]").forEach(s => [...s.options].forEach(o => o.selected = false));
    document.getElementById("metric").value = "score";
    document.getElementById("runView").value = (data.runView || "primary").toLowerCase();
    scopeSelect.value = scopeByKey.current && scopeByKey.current.runCount > 0 ? "current" : "all";
    groupingSelect.value = "model";
    render();
  });

  function render() {
    const state = readState();
    const axisIdx = filteredAxisIndices(state);
    const availableSeries = projection(state.scope, state.grouping);
    const rows = availableSeries.filter(s => includeSeries(s, state)).sort((a,b) => metricValue(b,state) - metricValue(a,state) || a.label.localeCompare(b.label));
    const expandedMetricId = activeMovedCard?.getAttribute("data-metric-id") || null;
    const chartRows = expandedMetricId ? rows : rows.slice(0, Math.max(1, state.topN || 24));
    const scope = scopeByKey[state.scope] || {label: state.scope, runCount: 0};
    const grouping = groupingByKey[state.grouping] || {label: state.grouping};
    const visibleRuns = rows.reduce((sum, s) => sum + s.runCount, 0);
    const backendCounts = {};
    rows.forEach(s => Object.entries(s.backendBreakdown || {}).forEach(([k,v]) => { backendCounts[k] = (backendCounts[k]||0) + v; }));
    document.getElementById("summary").innerHTML =
      `<span class="chip accent"><strong>${rows.length}</strong> von ${availableSeries.length} Gruppen</span>` +
      `<span class="chip"><strong>${visibleRuns}</strong> Runs im Scope „${esc(scope.label)}“</span>` +
      `<span class="chip">Gruppierung: ${esc(grouping.label)}</span>` +
      `<span class="chip">${axisIdx.length}/${data.axis.length} Schwachstellenachsen</span>` +
      Object.entries(backendCounts).sort((a,b)=>b[1]-a[1]).map(([k,v]) => `<span class="chip">${backendBadge(k)} ${v}</span>`).join("");
    renderKpis(rows, state, scope);
    renderTable(rows, state);
    renderHeatmap(rows, axisIdx, state);
    if (activeMovedCard) {
      const expandedChartBox = activeMovedCard.querySelector(".chart-box");
      if (expandedChartBox) expandedChartBox.style.height = `${Math.max(620, rows.length * 34 + 180)}px`;
    }
    if (hasChart) renderCharts(chartRows, axisIdx, state, expandedMetricId);
  }

  function renderKpis(rows, st, scope) {
    const el = document.getElementById("kpis");
    if (!rows.length) { el.innerHTML = ""; return; }
    const best = rows[0];
    const scores = rows.map(s => scoreForRunView(s, st.runView)).filter(Number.isFinite);
    const median = scores.length ? scores.slice().sort((a,b)=>a-b)[Math.floor(scores.length/2)] : null;
    const audited = rows.filter(s => s.truthAuditRunCount > 0);
    const kpi = (v, l) => `<div class="kpi"><div class="v">${v}</div><div class="l">${l}</div></div>`;
    el.innerHTML =
      kpi(esc(metricLabel(st)), `Metrik · Scope ${esc(scope.label)}`) +
      kpi(fmt(metricValue(best, st)), `Bester: ${esc(best.label)}`) +
      kpi(median === null ? "n/a" : fmt(median), "Median-Score der sichtbaren Gruppen") +
      kpi(rows.reduce((s,r)=>s+r.runCount,0), "Runs in Auswahl") +
      kpi(audited.length ? fmt(audited.reduce((s,r)=>s+r.accountabilityScore,0)/audited.length) : "n/a", "Ø Accountability (Truth-Audit)") +
      kpi(rows.filter(s => s.backend !== "unknown").length, "Gruppen mit erkanntem Backend");
  }

  function readState() {
    return {
      scope: document.getElementById("scope").value, grouping: document.getElementById("grouping").value,
      q: document.getElementById("q").value.trim().toLowerCase(),
      families: selected("family"), quants: selected("quant"), backends: selected("backend"), builds: selected("build"), severities: selected("severity"), categories: selected("category"), cwes: selected("cwe"),
      metric: document.getElementById("metric").value, runView: document.getElementById("runView").value,
      minScore: num("minScore"), maxScore: num("maxScore"), minRuns: num("minRuns"), maxStd: num("maxStd"), maxFp: num("maxFp"), maxHallucination: num("maxHallucination"), minCritical: num("minCritical"), topN: num("topN"),
      onlyOfficial: checked("onlyOfficial"), onlyHash: checked("onlyHash"), noLoop: checked("noLoop"), withReasoning: checked("withReasoning"), repeated: checked("repeated"), hideUnknown: checked("hideUnknown"), knownBackend: checked("knownBackend"), fill: checked("fillToggle")
    };
  }

  function includeSeries(s, st) {
    const hay = `${s.label} ${s.family} ${s.quant} ${s.backend} ${s.engine} ${s.build || ""} ${s.runtimeLabel || ""}`.toLowerCase();
    if (st.q && !hay.includes(st.q)) return false;
    if (st.families.length && !st.families.includes(s.family)) return false;
    if (st.quants.length && !st.quants.includes(s.quant)) return false;
    if (st.backends.length && !st.backends.includes(s.backend)) return false;
    if (st.builds.length && !st.builds.includes(s.runtimeLabel || (s.build ? `${s.engine} ${s.build}` : ""))) return false;
    const score = scoreForRunView(s, st.runView);
    if (st.minScore !== null && score < st.minScore) return false;
    if (st.maxScore !== null && score > st.maxScore) return false;
    if (st.minRuns !== null && s.runCount < st.minRuns) return false;
    if (st.maxStd !== null && s.scoreStdDev > st.maxStd) return false;
    if (st.maxFp !== null && s.falsePositives > st.maxFp) return false;
    if (st.maxHallucination !== null && s.hallucinationRate > st.maxHallucination) return false;
    if (st.minCritical !== null && s.criticalRecall < st.minCritical) return false;
    if (st.onlyOfficial && s.officialRunCount !== s.runCount) return false;
    if (st.onlyHash && s.sourceHashMatchCount !== s.runCount) return false;
    if (st.noLoop && s.loopRate > 0) return false;
    if (st.withReasoning && s.visibleReasoningRuns === 0) return false;
    if (st.repeated && s.runCount < 2) return false;
    if (st.hideUnknown && String(s.quant).toLowerCase() === "unknown-quant") return false;
    if (st.knownBackend && (s.backend === "unknown" || !s.backend)) return false;
    return true;
  }

  function filteredAxisIndices(st) {
    const idx = [];
    data.axis.forEach((a,i) => {
      if (st.severities.length && !st.severities.includes(a.severity)) return;
      if (st.categories.length && !st.categories.includes(a.category)) return;
      if (st.cwes.length && !(a.cwe || []).some(c => st.cwes.includes(c))) return;
      idx.push(i);
    });
    return idx;
  }

  function metricValue(s, st) {
    switch (st.metric) {
      case "criticalRecall": return s.criticalRecall ?? 0;
      case "highCriticalRecall": return s.highCriticalRecall ?? 0;
      case "f1": return s.f1 ?? 0;
      case "fpRate": return s.fpRate ?? 0;
      case "stability": return s.stability ?? 0;
      case "run2Delta": return s.run2Delta ?? 0;
      case "thinkingCoverage": return s.thinkingToOutputCoverage ?? 0;
      case "evidenceFidelity": return s.evidenceFidelity ?? 0;
      case "locationAccuracy": return s.locationAccuracy ?? 0;
      case "hallucinationRate": return s.hallucinationRate ?? 0;
      case "evaluationConfidence": return s.evaluationConfidence ?? 0;
      case "accountability": return s.accountabilityScore ?? 0;
      case "honesty": return s.honesty ?? -Infinity;
      case "honestyCalibration": return s.honestyCalibration ?? -Infinity;
      case "revisionSelectivity": return s.revisionSelectivity ?? -Infinity;
      case "honestyStability": return s.honestyStability ?? -Infinity;
      case "overclaimRate": return s.overclaimRate ?? 0;
      case "duration": return s.durationMedianSec ?? s.durationMeanSec ?? 0;
      case "tokenEfficiency": return s.scorePer1KTokens ?? 0;
      default: return scoreForRunView(s, st.runView);
    }
  }
  function scoreForRunView(s, runView) { if (runView === "run1") return s.run1Score ?? s.score; if (runView === "run2") return s.run2Score ?? s.score; if (runView === "delta") return s.run2Delta ?? 0; return s.score; }
  function metricLabel(st) { return ({score:"Gesamt-Score",criticalRecall:"Critical Recall %",highCriticalRecall:"High+Critical Recall %",f1:"F1 %",fpRate:"FP-Rate %",stability:"Stability %",run2Delta:"Run2-Delta",thinkingCoverage:"Thinking Coverage %",evidenceFidelity:"Evidence Fidelity %",locationAccuracy:"Location Accuracy %",hallucinationRate:"Hallucination Rate %",evaluationConfidence:"Evaluation Confidence %",accountability:"Truth-Audit Accountability",honesty:"Honesty %",honestyCalibration:"Calibration %",revisionSelectivity:"Revision Selectivity %",honestyStability:"Honesty Stability %",overclaimRate:"Overclaim Rate %",duration:"Duration sec",tokenEfficiency:"Score / 1k Tokens"})[st.metric] || "Metrik"; }
  function metricErrorRange(s, st) {
    if ((s.runCount || 0) < 2) return null;
    const values = metricDistributionValues(s, st).filter(Number.isFinite);
    let min = values.length >= 2 ? Math.min(...values) : null;
    let max = values.length >= 2 ? Math.max(...values) : null;
    if ((min === null || max === null) && st.metric === "score" && Number.isFinite(s.scoreMin) && Number.isFinite(s.scoreMax)) { min = s.scoreMin; max = s.scoreMax; }
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) return null;
    return {min, max, label:metricLabel(st)};
  }
  function metricDistributionValues(s, st) {
    const details = detailsOf(s);
    if (st.metric === "score") {
      if (st.runView === "run1") return details.map(d => d.run1Score);
      if (st.runView === "run2") return details.map(d => d.run2Score);
      if (st.runView === "delta") return details.map(d => d.run2Delta);
      return details.map(d => d.score);
    }
    if (st.metric === "run2Delta") return details.map(d => d.run2Delta);
    if (st.metric === "duration") return details.map(d => d.durationSec);
    if (st.metric === "tokenEfficiency") return details.filter(d => d.completionTokens > 0).map(d => d.score * 1000 / d.completionTokens);
    return [];
  }
  function errorScaleBounds(ranges) {
    const valid = ranges.filter(r => r && Number.isFinite(r.min) && Number.isFinite(r.max));
    if (!valid.length) return {};
    const min = Math.min(...valid.map(r => r.min));
    const max = Math.max(...valid.map(r => r.max));
    if (!Number.isFinite(min) || !Number.isFinite(max) || max <= min) return {};
    const pad = Math.max(1, (max - min) * 0.05);
    return {suggestedMin:min - pad, suggestedMax:max + pad};
  }

  function renderCharts(rows, axisIdx, st, expandedMetricId) {
    const metricName = metricLabel(st);
    const barErrorRanges = rows.map(s => metricErrorRange(s, st));
    const xScaleBounds = errorScaleBounds(barErrorRanges);
    document.getElementById("barTitle").textContent = metricName;
    updateChart("barChart", {
      type:"bar",
      data:{ labels:rows.map(s=>s.label), datasets:[{ label:metricName, data:rows.map(s=>metricValue(s,st)), errorRanges:barErrorRanges, backgroundColor:rows.map(s=>s.color+"cc"), borderColor:rows.map(s=>s.color), borderWidth:1 }]},
      options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{ x:{ beginAtZero: st.runView !== "delta", title:{display:true,text:metricName}, ...xScaleBounds }}, plugins:{ horizontalErrorBars:{color:cssVar("--fg"),lineWidth:1.6}, legend:{display:false}, tooltip:{callbacks:{afterBody:items=>{ const s=rows[items[0].dataIndex]; const range=barErrorRanges[items[0].dataIndex]; const lines=[`Runs: ${s.runCount} · Backend: ${backendLabel(s.backend)}${s.build ? " · " + s.engine + " " + s.build : ""}`,`Score Median/Mittel: ${fmt(s.scoreMedian)} / ${fmt(s.scoreMean)} · σ ${fmt(s.scoreStdDev)} · IQR ${fmt(s.scoreIqr)}`]; if (range) lines.push(`Fehlerbalken: ${fmt(range.min)}–${fmt(range.max)} (${range.label})`); else if (s.runCount >= 2) lines.push("Fehlerbalken: für diese Metrik nicht verfügbar"); lines.push(`Run1→Run2: ${fmt(s.run1Score)} → ${fmt(s.run2Score)} (${fmtSigned(s.run2Delta)})`); return lines; }}}}},
      plugins:[horizontalErrorBarsPlugin]
    });
    updateChart("severityChart", { type:"bar", data:{ labels:rows.map(s=>s.label), datasets:[
      {label:"Critical",data:rows.map(s=>s.criticalRecall),backgroundColor:"#dc2626cc"},{label:"High",data:rows.map(s=>s.highRecall),backgroundColor:"#f97316cc"},{label:"Medium",data:rows.map(s=>s.mediumRecall),backgroundColor:"#eab308cc"},{label:"Low",data:rows.map(s=>s.lowRecall),backgroundColor:"#22c55ecc"}]},
      options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{min:0,max:100,title:{display:true,text:"Recall/Credit %"}}} }});
    if (hasBackendGrouping && document.getElementById("backendChart")) renderBackendChart(st, expandedMetricId);
    const radarRows = expandedMetricId === "vulnerabilityRadar" ? rows : rows.slice(0, Math.min(10, rows.length));
    const labels = axisIdx.map(i => data.axis[i].id);
    updateChart("radarChart", { type:"radar", data:{ labels, datasets:radarRows.map(s=>({ label:s.label, data:axisIdx.map(i=>s.perVuln[i] ?? 0), borderColor:s.color, backgroundColor:s.color+"33", fill:st.fill, pointRadius:2, borderWidth:2 }))}, options:{ responsive:true, maintainAspectRatio:false, scales:{ r:{ min: st.runView === "delta" ? -1 : 0, max:1, ticks:{stepSize:0.5,showLabelBackdrop:false}, pointLabels:{font:{size:10}}}}, plugins:{legend:{position:"bottom",labels:{boxWidth:12,font:{size:11}}}} }});
    const slopeCandidates = rows.filter(s => s.run2Score || s.run1Score);
    const slopeRows = expandedMetricId === "run2Delta" ? slopeCandidates : slopeCandidates.slice(0, 12);
    updateChart("slopeChart", { type:"line", data:{ labels:["Run 1","Run 2"], datasets:slopeRows.map(s=>({ label:s.label, data:[s.run1Score,s.run2Score], borderColor:s.color, backgroundColor:s.color, tension:0.15 }))}, options:{ responsive:true, maintainAspectRatio:false, scales:{ y:{ beginAtZero:true, max:100, title:{display:true,text:"Score"}}}, plugins:{legend:{position:"bottom",labels:{boxWidth:12,font:{size:11}}}} }});
    updateChart("qualityChart", { type:"bar", data:{ labels:rows.map(s=>s.label), datasets:[
      {label:"FP",data:rows.map(s=>s.falsePositives),backgroundColor:"#ef4444cc"},{label:"Duplicates",data:rows.map(s=>s.duplicates),backgroundColor:"#f59e0bcc"},{label:"Ignored",data:rows.map(s=>s.ignoredLowConfidence),backgroundColor:"#64748bcc"},{label:"Hallucination %",data:rows.map(s=>s.hallucinationRate),backgroundColor:"#fb7185aa"},{label:"Evidence %",data:rows.map(s=>s.evidenceFidelity),backgroundColor:"#22c55e99"},{label:"Location %",data:rows.map(s=>s.locationAccuracy),backgroundColor:"#14b8a699"},{label:"Loop %",data:rows.map(s=>s.loopRate),backgroundColor:"#a855f7aa"},{label:"Parse fail %",data:rows.map(s=>100-(s.parseSuccessRate||0)),backgroundColor:"#0ea5e9aa"}]}, options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{beginAtZero:true}}, plugins:{legend:{position:"bottom"}} }});
    const auditRows = rows.filter(s => s.truthAuditRunCount > 0);
    if (hasTruthAuditStats && document.getElementById("truthAuditChart")) updateChart("truthAuditChart", { type:"bar", data:{ labels:auditRows.map(s=>s.label), datasets:[
      {label:"Accountability",data:auditRows.map(s=>s.accountabilityScore),backgroundColor:"#2563ebcc"},{label:"Audit Accuracy %",data:auditRows.map(s=>s.truthAuditAccuracy),backgroundColor:"#22c55ecc"},{label:"Quote Fidelity %",data:auditRows.map(s=>s.quoteFidelity),backgroundColor:"#14b8a6cc"},{label:"Overclaim %",data:auditRows.map(s=>s.overclaimRate),backgroundColor:"#ef4444aa"},{label:"Miss Admit %",data:auditRows.map(s=>s.missAdmissionRate),backgroundColor:"#a855f7aa"},{label:"FP Admit %",data:auditRows.map(s=>s.falsePositiveAdmissionRate),backgroundColor:"#f59e0baa"}]}, options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{min:0,max:100,title:{display:true,text:"Run 3 Audit % / Score"}}}, plugins:{legend:{position:"bottom"}, tooltip:{callbacks:{afterBody:items=>{ const s=auditRows[items[0].dataIndex]; return [`Audit Runs: ${s.truthAuditRunCount}`,`Evidence Laundering: ${fmt(s.evidenceLaunderingCount)}`]; }}}} }});
    const diagnosticRows = rows.filter(s => s.diagnosticsAvailableRunCount > 0);
    if (hasDiagnostics) {
      updateChart("honestyChart", {type:"bar",data:{labels:diagnosticRows.map(s=>s.label),datasets:[{label:"Honesty %",data:diagnosticRows.map(s=>s.honesty),backgroundColor:"#22c55ecc"},{label:"Inflation %",data:diagnosticRows.map(s=>s.honestyInflationRate),backgroundColor:"#ef4444aa"},{label:"Underclaim %",data:diagnosticRows.map(s=>s.honestyUnderclaimRate),backgroundColor:"#f59e0baa"},{label:"Laundering %",data:diagnosticRows.map(s=>s.launderingPrevalence),backgroundColor:"#a855f7aa"},{label:"Contradiction %",data:diagnosticRows.map(s=>s.contradictionPrevalence),backgroundColor:"#64748baa"}]},options:{indexAxis:"y",responsive:true,maintainAspectRatio:false,scales:{x:{min:0,max:100}},plugins:{legend:{position:"bottom"}}}});
      updateChart("calibrationChart", {type:"bar",data:{labels:diagnosticRows.map(s=>s.label),datasets:[{label:"Calibration %",data:diagnosticRows.map(s=>s.honestyCalibration),backgroundColor:"#2563ebcc"},{label:"Brier ×100",data:diagnosticRows.map(s=>s.honestyBrier == null ? null : s.honestyBrier*100),backgroundColor:"#f97316aa"},{label:"ECE ×100",data:diagnosticRows.map(s=>s.honestyEce == null ? null : s.honestyEce*100),backgroundColor:"#dc2626aa"}]},options:{indexAxis:"y",responsive:true,maintainAspectRatio:false,plugins:{legend:{position:"bottom"}}}});
      updateChart("consistencyChart", {type:"bar",data:{labels:diagnosticRows.map(s=>s.label),datasets:[{label:"Revision %",data:diagnosticRows.map(s=>s.revisionSelectivity),backgroundColor:"#14b8a6cc"},{label:"Flag %",data:diagnosticRows.map(s=>s.flagConsistency),backgroundColor:"#6366f1aa"},{label:"Correction %",data:diagnosticRows.map(s=>s.correctionProvenance),backgroundColor:"#0ea5e9aa"},{label:"Stability %",data:diagnosticRows.map(s=>s.honestyStability),backgroundColor:"#22c55eaa"}]},options:{indexAxis:"y",responsive:true,maintainAspectRatio:false,scales:{x:{min:0,max:100}},plugins:{legend:{position:"bottom"}}}});
    } else {
      ["honestyChart","calibrationChart","consistencyChart"].forEach(id => { const el = document.getElementById(id); if (el && el.parentElement) el.parentElement.innerHTML = '<div class="empty">Keine Diagnostik-Daten in den gefilterten Scorecards.</div>'; });
    }
    if (hasReasoningStats && document.getElementById("reasoningChart")) updateChart("reasoningChart", { type:"bar", data:{ labels:rows.map(s=>s.label), datasets:[{label:"Gedacht TP",data:rows.map(s=>s.thinkingTp),backgroundColor:"#6366f1cc"},{label:"Gesagt TP",data:rows.map(s=>s.outputTp),backgroundColor:"#10b981cc"},{label:"Nur gedacht",data:rows.map(s=>s.thinkingOnlyTp),backgroundColor:"#f59e0bcc"}]}, options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{beginAtZero:true}}, plugins:{legend:{position:"bottom"}} }});
    const tokenRows = rows.filter(s => s.tokenizedRuns > 0);
    if (hasTokenStats && document.getElementById("tokenUsageChart")) updateChart("tokenUsageChart", { type:"bar", data:{ labels:tokenRows.map(s=>s.label), datasets:[
      {label:"Thinking Tokens",data:tokenRows.map(s=>s.reasoningTokens),backgroundColor:"#6366f1cc",stack:"tokens"},{label:"Output Tokens",data:tokenRows.map(s=>s.outputTokens),backgroundColor:"#10b981cc",stack:"tokens"},{label:"Overhead (Steuer-/Trenntokens)",data:tokenRows.map(s=>Math.max(0,(s.completionTokens||0)-(s.reasoningTokens||0)-(s.outputTokens||0))),backgroundColor:"#94a3b8b3",stack:"tokens"}]},
      options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{beginAtZero:true,stacked:true,title:{display:true,text:"Generierte Tokens (Balkenlänge = Gesamttokens)"}},y:{stacked:true}}, plugins:{legend:{position:"bottom",labels:{boxWidth:12,font:{size:11}}},tooltip:{callbacks:{afterBody:items=>{const s=tokenRows[items[0].dataIndex]; return [`Tokenisierte Runs: ${s.tokenizedRuns}/${s.runCount}`,`Gesamt: ${fmt(s.completionTokens)} · Score/1k: ${fmt(s.scorePer1KTokens)}`];}}}} }});
    if (hasTokenStats && document.getElementById("tokenEfficiencyChart")) {
      const effRows = tokenRows.filter(s => (s.completionTokens||0) > 0).slice().sort((a,b)=>(b.scorePer1KTokens??-Infinity)-(a.scorePer1KTokens??-Infinity));
      updateChart("tokenEfficiencyChart", { type:"bar", data:{ labels:effRows.map(s=>s.label), datasets:[{label:"Score / 1k Tokens",data:effRows.map(s=>s.scorePer1KTokens),backgroundColor:effRows.map(s=>s.color+"cc"),borderColor:effRows.map(s=>s.color),borderWidth:1}]},
        options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{x:{beginAtZero:true,title:{display:true,text:"Score / 1.000 Gesamttokens"}}}, plugins:{legend:{display:false},tooltip:{callbacks:{afterBody:items=>{const s=effRows[items[0].dataIndex]; return [`Score: ${fmt(scoreForRunView(s, st.runView))} · Gesamt: ${fmt(s.completionTokens)} Tokens`,`Tokenisierte Runs: ${s.tokenizedRuns}/${s.runCount}`];}}}} }});
    }
  }

  function renderBackendChart(st, expandedMetricId) {
    const perBackend = projection(st.scope, "backend").filter(s => s.backend !== "unknown" && includeSeries(s, {...st, backends: st.backends, knownBackend: false}));
    const box = document.getElementById("backendBox");
    if (!perBackend.length) { if (charts.backendChart) { charts.backendChart.destroy(); delete charts.backendChart; } box.innerHTML = '<div class="empty">Keine Scorecards mit erkanntem Backend im gewählten Scope.</div>'; return; }
    if (!document.getElementById("backendChart")) box.innerHTML = '<canvas id="backendChart"></canvas>';
    const modelKeys = uniq(perBackend.map(s => `${s.family} · ${s.quant}`));
    const modelRows = expandedMetricId === "backendSplit" ? modelKeys : modelKeys.slice(0, Math.max(1, st.topN || 24));
    const backendKeys = uniq(perBackend.map(s => s.backend));
    const palette = {vulkan:"#3b82f6",hip:"#ef4444",cuda:"#22c55e",sycl:"#8b5cf6",metal:"#0ea5e9",opencl:"#f59e0b",cpu:"#6b7280"};
    const datasets = backendKeys.map(b => ({ label: backendLabel(b), backgroundColor: (palette[b] || "#94a3b8") + "cc", data: modelRows.map(m => { const s = perBackend.find(x => `${x.family} · ${x.quant}` === m && x.backend === b); return s ? metricValue(s, st) : null; }) }));
    updateChart("backendChart", { type:"bar", data:{ labels: modelRows, datasets }, options:{ indexAxis:"y", responsive:true, maintainAspectRatio:false, scales:{ x:{ beginAtZero: st.runView !== "delta", title:{display:true,text:metricLabel(st)} } }, plugins:{ legend:{position:"bottom"}, tooltip:{callbacks:{afterBody:items=>{ const m = modelRows[items[0].dataIndex]; const b = backendKeys[items[0].datasetIndex]; const s = perBackend.find(x => `${x.family} · ${x.quant}` === m && x.backend === b); return s ? [`Runs: ${s.runCount} · ${s.runtimeLabel || ""}`, `Median ${fmt(s.scoreMedian)} · σ ${fmt(s.scoreStdDev)} · Range ${fmt(s.scoreMin)}–${fmt(s.scoreMax)}`] : []; }}} } } });
  }

  function renderHeatmap(rows, axisIdx, st) {
    if (!axisIdx.length) { document.getElementById("heatmap").innerHTML = '<div class="empty">Keine Schwachstellenachsen im Filter.</div>'; return; }
    let html = '<table><thead><tr><th>Gruppe</th>' + axisIdx.map(i => `<th title="${esc(axisTitle(data.axis[i]))}">${esc(data.axis[i].id)}</th>`).join("") + '</tr></thead><tbody>';
    rows.forEach(s => { html += `<tr class="swatch" style="--swatch:${s.color}"><td class="text">${esc(s.label)}</td>`; axisIdx.forEach(i => { const v = s.perVuln[i] ?? 0; html += `<td class="${heatClass(v, st.runView)}" title="${esc(s.label)} · ${esc(axisTitle(data.axis[i]))}: ${fmt(v)}">${fmt(v)}</td>`; }); html += '</tr>'; });
    html += '</tbody></table>'; document.getElementById("heatmap").innerHTML = html;
  }

  const cols = [
    {key:"label",title:"Gruppe",kind:"detail"},{key:"backend",title:"Backend",kind:"backend"},{key:"build",title:"Build",kind:"text"},{key:"runCount",title:"Runs",kind:"num"},{key:"score",title:"Score",kind:"num"},{key:"criticalRecall",title:"Critical %",kind:"num"},{key:"highCriticalRecall",title:"High+Crit %",kind:"num"},{key:"evidenceFidelity",title:"Evidence %",kind:"num"},{key:"locationAccuracy",title:"Location %",kind:"num"},{key:"hallucinationRate",title:"Hallucination %",kind:"num"},{key:"stability",title:"Stability %",kind:"num"},{key:"run2Delta",title:"Run2 Δ",kind:"num"},{key:"truthAuditRunCount",title:"Audit Runs",kind:"num"},{key:"accountabilityScore",title:"Audit",kind:"num"},{key:"truthAuditAccuracy",title:"Audit Acc %",kind:"num"},{key:"overclaimRate",title:"Overclaim %",kind:"num"},{key:"missAdmissionRate",title:"Miss Admit %",kind:"num"},{key:"falsePositiveAdmissionRate",title:"FP Admit %",kind:"num"},{key:"quoteFidelity",title:"Quote %",kind:"num"},{key:"evidenceLaunderingCount",title:"Launder",kind:"num"},{key:"honesty",title:"Honesty %",kind:"num"},{key:"honestyEligibleCount",title:"Honesty N",kind:"num"},{key:"honestyCalibration",title:"Calibration %",kind:"num"},{key:"calibrationObservationCount",title:"Conf N",kind:"num"},{key:"honestyBrier",title:"Brier",kind:"num"},{key:"honestyEce",title:"ECE",kind:"num"},{key:"revisionSelectivity",title:"Revision %",kind:"num"},{key:"flagConsistency",title:"Flag %",kind:"num"},{key:"correctionProvenance",title:"Correction %",kind:"num"},{key:"honestyStability",title:"Honesty Stability %",kind:"num"},{key:"honestyStabilityN",title:"Stability N",kind:"num"},{key:"scoreMedian",title:"Median",kind:"num"},{key:"scoreStdDev",title:"±σ",kind:"num"},{key:"scoreIqr",title:"IQR",kind:"num"},{key:"precision",title:"Precision %",kind:"num"},{key:"recall",title:"Recall %",kind:"num"},{key:"f1",title:"F1 %",kind:"num"},{key:"fullTp",title:"Full TP",kind:"num"},{key:"partialTp",title:"Partial",kind:"num"},{key:"falsePositives",title:"FP",kind:"num"},{key:"duplicates",title:"Dup",kind:"num"},{key:"missed",title:"Missed",kind:"num"},{key:"parseSuccessRate",title:"Parse %",kind:"num"},{key:"loopRate",title:"Loop %",kind:"num"},{key:"durationMedianSec",title:"Dur s",kind:"num"},{key:"reasoningTokens",title:"Think Tok",kind:"num"},{key:"outputTokens",title:"Out Tok",kind:"num"},{key:"completionTokens",title:"Gesamt Tok",kind:"num"},{key:"scorePer1KTokens",title:"Score/1k Tok",kind:"num"},{key:"thinkingToOutputCoverage",title:"Think→Out %",kind:"num"}
  ];
  function renderTable(rows, st) {
    rows = rows.slice().sort((a,b) => { const av = valueForSort(a,sortKey,st), bv = valueForSort(b,sortKey,st); if (typeof av === "number" || typeof bv === "number") return ((av ?? -Infinity) - (bv ?? -Infinity))*sortDir; return String(av??"").localeCompare(String(bv??""))*sortDir; });
    let html = '<table><thead><tr>' + cols.map(c => `<th class="${c.key===sortKey?(sortDir===1?'sorted-asc':'sorted-desc'):''}" data-key="${c.key}">${c.title}</th>`).join("") + '</tr></thead><tbody>';
    rows.forEach(s => { html += `<tr class="swatch" style="--swatch:${s.color}">`; cols.forEach(c => { html += `<td class="${c.kind === "num" ? "" : "text"}">${cell(s,c,st)}</td>`; }); html += '</tr>'; });
    html += '</tbody></table>'; document.getElementById("tableWrap").innerHTML = html;
    document.querySelectorAll("th[data-key]").forEach(th => th.addEventListener("click", () => { const k = th.getAttribute("data-key"); if (k === sortKey) sortDir = -sortDir; else { sortKey = k; sortDir = (k === "label" || k === "backend" || k === "build") ? 1 : -1; } render(); }));
  }
  const auditMetricKeys = new Set(["accountabilityScore","truthAuditAccuracy","overclaimRate","missAdmissionRate","falsePositiveAdmissionRate","quoteFidelity","evidenceLaunderingCount"]);
  function cell(s,c,st) {
    if (c.kind === "detail") return detailCell(s);
    if (c.kind === "backend") return backendCell(s);
    if (c.kind === "text") return esc(s[c.key] ?? "—");
    if (auditMetricKeys.has(c.key) && !(s.truthAuditRunCount > 0)) return "n/a";
    const v = valueForSort(s,c.key,st); return typeof v === "number" && Number.isFinite(v) ? (Number.isInteger(v) ? v : v.toFixed(1)) : "n/a";
  }
  function backendCell(s) {
    if (s.backend === "mixed") { return Object.entries(s.backendBreakdown||{}).sort((a,b)=>b[1]-a[1]).map(([k,v]) => `${backendBadge(k)} ${v}`).join(" "); }
    return backendBadge(s.backend);
  }
  function backendLabel(b) { return ({vulkan:"Vulkan",hip:"HIP/ROCm",cuda:"CUDA",sycl:"SYCL",metal:"Metal",opencl:"OpenCL",cpu:"CPU",mixed:"gemischt",unknown:"unbekannt"})[b] || b; }
  function backendBadge(b) { const key = String(b || "unknown").toLowerCase(); return `<span class="badge ${esc(key)}">${esc(backendLabel(key))}</span>`; }
  function detailCell(s) {
    const details = detailsOf(s);
    const rows = details.map(d => { const version = `${esc(d.scoringProfile||"legacy-unknown")} - ${esc(d.parserVersion||"parser-unbekannt")}${d.isLegacyMigrated ? " · legacy-migriert" : ""}${d.isRescored ? " · rescored" : ""}`; return `<tr><td>${esc(d.completedAt||"")}</td><td>${esc(d.runName)}</td><td>v${esc(d.toolVersion||"?")}</td><td>${backendBadge(d.backend)} ${esc(d.build||"")}</td><td>${version}</td><td>${d.officialComparable?'ja':'nein'}</td><td>${d.isCurrentEvaluation?'aktuell':'veraltet'}</td><td>${fmt(d.score)}</td><td>${fmtSigned(d.run2Delta)}</td><td>${esc(d.finishReason||"")}</td><td>${esc(d.parseMode||"")}</td><td>${d.loopDetected?'ja':'nein'}</td><td>${fmt(d.durationSec)}</td><td>${fmt(d.outputTokens)}/${fmt(d.reasoningTokens)}/${fmt(d.completionTokens)}</td><td>${esc(d.repeatGroupId||d.campaignId||'—')} ${d.repeatCount>1 ? '('+d.repeatIndex+'/'+d.repeatCount+')' : ''}</td></tr>`; }).join("");
    const fpTax = Object.entries(s.falsePositiveTaxonomy||{}).sort((a,b)=>b[1]-a[1]).map(([k,v])=>`${esc(k)}=${fmt(v)}`).join(', ') || '—';
    const versions = Object.entries(s.parserVersions||{}).map(([k,v])=>`${esc(k)}: ${v}`).join(', ') || '—';
    const tools = Object.entries(s.toolVersions||{}).sort((a,b)=>b[0].localeCompare(a[0])).map(([k,v])=>`v${esc(k)}: ${v}`).join(', ') || '—';
    return `<details><summary>${esc(s.label)}</summary><div class="note">Backends: ${Object.entries(s.backendBreakdown||{}).map(([k,v])=>`${backendBadge(k)} ${v}`).join(' ') || '—'} · Builds: ${Object.entries(s.runtimeBreakdown||{}).map(([k,v])=>`${esc(k)} (${v})`).join(', ') || '—'} · Parser: ${versions} · Benchmark: ${tools}<br>Profil: ${s.officialRunCount}/${s.runCount} offiziell · official comparable: ${s.officialComparableRunCount}/${s.runCount} · aktuell (${esc(data.parserVersion)}): ${s.currentEvaluationRunCount}/${s.runCount} · legacy-migriert: ${s.legacyMigratedRunCount}/${s.runCount} · rescored: ${s.rescoredRunCount}/${s.runCount} · Source-Hash: ${s.sourceHashMatchCount}/${s.runCount} · FP-Taxonomie: ${fpTax} · Run2 dropped: ${(s.run2DroppedIds||[]).join(', ')||'—'} · added: ${(s.run2AddedIds||[]).join(', ')||'—'}</div><table class="detail-table"><thead><tr><th>Datum</th><th>Run</th><th>Tool</th><th>Backend/Build</th><th>Score-Version</th><th>Official</th><th>Aktualität</th><th>Score</th><th>Run2 Δ</th><th>Finish</th><th>Parse</th><th>Loop</th><th>s</th><th>Out/Think/Gesamt Tokens</th><th>Repeat/Kampagne</th></tr></thead><tbody>${rows}</tbody></table></details>`;
  }
  function valueForSort(s,key,st) { if (key === "score") return scoreForRunView(s, st.runView); if (key === "backend") return backendLabel(s.backend); return s[key]; }

  function metricHeader(metricId, title, titleId) {
    const idAttr = titleId ? ` id="${esc(titleId)}"` : "";
    return `<div class="card-heading"><button${idAttr} type="button" class="metric-title" data-modal-metric="${esc(metricId)}" aria-label="${esc(title)} maximieren">${esc(title)}</button><button type="button" class="metric-help" data-help-metric="${esc(metricId)}" aria-label="Hilfe zu ${esc(title)}">?</button></div>`;
  }

  function wireMetricInteractions() {
    document.querySelectorAll(".metric-card").forEach(card => card.addEventListener("click", ev => {
      if (card.classList.contains("in-modal")) return;
      const target = ev.target;
      if (target?.closest?.("button,a,input,select,textarea,label,summary,details,th,[data-no-modal]")) return;
      openMetricModal(card.getAttribute("data-metric-id"));
    }));
    document.querySelectorAll("[data-modal-metric]").forEach(btn => btn.addEventListener("click", ev => { ev.stopPropagation(); openMetricModal(btn.getAttribute("data-modal-metric")); }));
    document.querySelectorAll("[data-help-metric]").forEach(btn => btn.addEventListener("click", ev => { ev.stopPropagation(); openHelpPopover(btn.getAttribute("data-help-metric")); }));
  }

  let activeOverlay = null;
  let activePlaceholder = null;
  let activeMovedCard = null;
  let lastFocusedElement = null;
  function openMetricModal(metricId) {
    if (activeOverlay) closeOverlay();
    const card = [...document.querySelectorAll(".metric-card")].find(el => el.getAttribute("data-metric-id") === metricId);
    if (!card) return;
    lastFocusedElement = document.activeElement;
    activePlaceholder = document.createComment(`metric-${metricId}-placeholder`);
    card.parentNode.insertBefore(activePlaceholder, card);
    activeMovedCard = card;
    const overlay = createOverlay("metric-modal");
    const close = overlay.querySelector(".overlay-close");
    const body = overlay.querySelector(".overlay-body");
    body.appendChild(card);
    card.classList.add("in-modal");
    document.body.appendChild(overlay);
    activeOverlay = overlay;
    close.focus();
    render();
    resizeChartsSoon();
  }

  function openHelpPopover(metricId) {
    if (activeOverlay) closeOverlay();
    const help = metricHelp[metricId] || {title:"Metrik", body:"Keine Detailbeschreibung verfügbar."};
    lastFocusedElement = document.activeElement;
    const overlay = createOverlay("help-popover");
    overlay.querySelector(".overlay-body").innerHTML = `<h2>${esc(help.title)}</h2><p>${esc(help.body)}</p><div class="note">Schließen mit Esc, Klick außerhalb oder X. Alle Werte stammen aus lokalen Archiv-Scorecards; Ground Truth wird nur offline zur Aggregation verwendet.</div>`;
    document.body.appendChild(overlay);
    activeOverlay = overlay;
    overlay.querySelector(".overlay-close").focus();
  }

  function createOverlay(extraClass) {
    const overlay = document.createElement("div");
    overlay.className = "overlay-backdrop";
    overlay.innerHTML = `<div class="overlay-dialog ${extraClass}" role="dialog" aria-modal="true" tabindex="-1"><button type="button" class="overlay-close" aria-label="Overlay schließen">×</button><div class="overlay-body"></div></div>`;
    overlay.addEventListener("click", ev => { if (ev.target === overlay) closeOverlay(); });
    overlay.querySelector(".overlay-close").addEventListener("click", closeOverlay);
    return overlay;
  }

  function closeOverlay() {
    if (!activeOverlay) return;
    if (activeMovedCard && activePlaceholder) {
      activeMovedCard.classList.remove("in-modal");
      const expandedChartBox = activeMovedCard.querySelector(".chart-box");
      if (expandedChartBox) expandedChartBox.style.removeProperty("height");
      activePlaceholder.parentNode.insertBefore(activeMovedCard, activePlaceholder);
      activePlaceholder.remove();
    }
    activeOverlay.remove();
    activeOverlay = null; activePlaceholder = null; activeMovedCard = null;
    if (lastFocusedElement && typeof lastFocusedElement.focus === "function") lastFocusedElement.focus();
    render();
    resizeChartsSoon();
  }

  document.addEventListener("keydown", ev => { if (ev.key === "Escape" && activeOverlay) closeOverlay(); });
  function resizeChartsSoon() { setTimeout(() => Object.values(charts).forEach(chart => chart && chart.resize && chart.resize()), 40); }

  function exportFilteredCsv() {
    const st = readState(); const availableSeries = projection(st.scope, st.grouping); const rows = availableSeries.filter(s => includeSeries(s, st));
    const headers = ["scope","grouping","model_family","quant","backend","engine","build","runs","score","critical_recall_pct","high_critical_recall_pct","evidence_fidelity_pct","location_accuracy_pct","hallucination_rate_pct","f1_pct","stability_pct","run2_delta","fp","duplicates","missed","parse_success_pct","loop_pct","diagnostics_available_runs","diagnostics_valid_runs","diagnostics_partial_runs","diagnostics_invalid_runs","diagnostics_unavailable_runs","honesty_eligible_n","calibration_eligible_n","revision_eligible_n","honesty_pct","inflation_pct","underclaim_pct","laundering_pct","contradiction_pct","confidence_n","confidence_brier","confidence_ece","severity_assigned_n","severity_coverage_pct","severity_exact_pct","severity_inflation_pct","severity_underclaim_pct","severity_mae","cwe_assigned_n","cwe_coverage_pct","cwe_any_hit_pct","cwe_exact_set_pct","cwe_micro_precision_pct","cwe_micro_recall_pct","triangulation_reasoning_available_n","reasoning_output_retention_pct","output_audit_ack_pct","reasoning_audit_claim_pct","end_to_end_retention_pct","thought_only_count","thought_only_honesty_pct","output_only_count","output_only_audit_ack_pct","revision_selectivity_pct","revision_harm_count","revision_mixed_count","revision_net_pct","parse_transition_delta","parse_improved_count","parse_unchanged_count","parse_degraded_count","correction_consistency_pct","correction_valid_count","correction_raw_count","flag_consistency_pct","flag_valid_count","flag_raw_count","honesty_stability_n","honesty_stability_pct","duration_median_sec","thinking_tokens","output_tokens","completion_tokens","score_per_1k_tokens"];
    const lines = [headers.join(",")]; rows.forEach(s => lines.push([st.scope,st.grouping,s.family,s.quant,s.backend,s.engine,s.build??"",s.runCount,scoreForRunView(s,st.runView),s.criticalRecall,s.highCriticalRecall,s.evidenceFidelity,s.locationAccuracy,s.hallucinationRate,s.f1,s.stability,s.run2Delta,s.falsePositives,s.duplicates,s.missed,s.parseSuccessRate,s.loopRate,s.diagnosticsAvailableRunCount,s.diagnosticsValidRunCount,s.diagnosticsPartialRunCount,s.diagnosticsInvalidRunCount,s.diagnosticsUnavailableRunCount,s.honestyEligibleCount,s.calibrationEligibleCount,s.revisionEligibleCount,s.honesty??"",s.honestyInflationRate??"",s.honestyUnderclaimRate??"",s.launderingPrevalence??"",s.contradictionPrevalence??"",s.calibrationObservationCount||"",s.honestyBrier??"",s.honestyEce??"",s.severityAssignedCount||"",s.severityCoverage??"",s.severityExactRate??"",s.severityInflationRate??"",s.severityUnderclaimRate??"",s.severityMae??"",s.cweAssignedCount||"",s.cweCalibrationCoverage??"",s.cweAnyHitRate??"",s.cweExactSetRate??"",s.cweMicroPrecision??"",s.cweMicroRecall??"",s.triangulationReasoningAvailableCount||"",s.triangulationReasoningToOutputRetention??"",s.triangulationOutputToAuditAcknowledgment??"",s.triangulationReasoningToAuditClaimRate??"",s.triangulationEndToEndRetention??"",s.triangulationReasoningAvailableCount?s.triangulationThoughtOnlyCount:"",s.triangulationThoughtOnlyHonestyRate??"",s.triangulationOutputOnlyCount??"",s.triangulationOutputOnlyAuditAcknowledgment??"",s.revisionSelectivity??"",s.revisionEligibleCount?s.revisionHarmCount:"",s.revisionEligibleCount?s.revisionMixedCount:"",s.revisionNet??"",s.parseTransitionDelta??"",s.parseTransitionImprovedCount+s.parseTransitionUnchangedCount+s.parseTransitionDegradedCount?s.parseTransitionImprovedCount:"",s.parseTransitionImprovedCount+s.parseTransitionUnchangedCount+s.parseTransitionDegradedCount?s.parseTransitionUnchangedCount:"",s.parseTransitionImprovedCount+s.parseTransitionUnchangedCount+s.parseTransitionDegradedCount?s.parseTransitionDegradedCount:"",s.correctionProvenance??"",s.correctionRawCount?s.correctionValidCount:"",s.correctionRawCount||"",s.flagConsistency??"",s.explicitFlagRawCount?s.explicitFlagValidCount:"",s.explicitFlagRawCount||"",s.honestyStabilityN>=2?s.honestyStabilityN:"",s.honestyStability??"",s.durationMedianSec??"",s.reasoningTokens??"",s.outputTokens??"",s.completionTokens??"",s.scorePer1KTokens??""].map(csv).join(",")));
    const blob = new Blob([lines.join("\n")], {type:"text/csv;charset=utf-8"}); const a = document.createElement("a"); a.href = URL.createObjectURL(blob); a.download = `supercalc-comparison-${st.scope.replace(/[^a-z0-9.-]+/gi,"_")}-${st.grouping}-${Date.now()}.csv`; a.click(); URL.revokeObjectURL(a.href);
  }

  function updateChart(id, cfg) { const el = document.getElementById(id); if (!el) return; if (charts[id]) charts[id].destroy(); charts[id] = new Chart(el, cfg); }
  function fillSelect(id, values, labelFn) { const el = document.getElementById(id); el.innerHTML = values.map(v => `<option value="${esc(v)}">${esc(labelFn ? labelFn(v) : v)}</option>`).join(""); }
  function selected(id) { return [...document.getElementById(id).selectedOptions].map(o => o.value); }
  function num(id) { const v = document.getElementById(id).value; return v === "" ? null : Number(v); }
  function checked(id) { return !!document.getElementById(id)?.checked; }
  function uniq(values) { return [...new Set(values.filter(v => v !== null && v !== undefined && v !== ""))].sort((a,b)=>String(a).localeCompare(String(b))); }
  function fmt(v) { return typeof v === "number" && Number.isFinite(v) ? v.toFixed(1) : "n/a"; }
  function fmtSigned(v) { return typeof v === "number" && Number.isFinite(v) ? (v>0?"+":"") + v.toFixed(1) : "n/a"; }
  function cssVar(name) { return getComputedStyle(document.documentElement).getPropertyValue(name).trim(); }
  function esc(v) { return String(v ?? "").replace(/[&<>"']/g, ch => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[ch])); }
  function csv(v) { v = String(v ?? ""); return /[",\n]/.test(v) ? '"' + v.replace(/"/g,'""') + '"' : v; }
  function axisTitle(a) { return `${a.id}${a.title?' — '+a.title:''} · ${a.severity||''} · ${a.category||''} · ${(a.cwe||[]).join('/')}`; }
  function heatClass(v, runView) { if (runView === "delta") return v < 0 ? "heat-neg" : (v > 0 ? "heat-pos" : "heat-0"); if (v >= .99) return "heat-100"; if (v > 0) return "heat-50"; return "heat-0"; }
  render();
})();
""";

    public string BuildCsv(ComparisonReport report)
    {
        var builder = new StringBuilder();
        var header = new List<string>
        {
            "model_family", "quant", "backend", "engine", "build", "runtime_label", "run_count", "aggregate", "run_view", "scoring_profile", "scope", "grouping", "official_comparable_runs", "current_evaluation_runs", "legacy_migrated_runs", "rescored_runs", "score_percent",
            "score_mean", "score_median", "score_stddev", "score_iqr", "score_ci95", "score_min", "score_max",
            "precision_percent", "recall_percent", "f1_percent",
            "critical_recall_percent", "high_recall_percent", "medium_recall_percent", "low_recall_percent", "high_critical_recall_percent",
            "memory_safety_percent", "concurrency_percent", "injection_percent", "auth_crypto_percent", "numeric_dos_percent", "file_io_percent", "cwe_coverage_percent", "stability_percent",
            "evidence_fidelity_percent", "location_accuracy_percent", "hallucination_rate_percent", "evaluation_confidence_percent", "fp_taxonomy",
            "full_tp", "partial_tp", "false_positives", "duplicates", "ignored_low_confidence", "missed",
            "parse_success_percent", "loop_rate_percent", "empty_output_rate_percent", "visible_reasoning_rate_percent",
            "run1_score", "run2_score", "run2_delta", "run2_fp_reduction", "run2_tp_retention_percent", "run2_dropped_tp", "run2_added_tp",
            "truth_audit_runs", "accountability_score", "truth_audit_accuracy_percent", "overclaim_rate_percent", "miss_admission_rate_percent", "fp_admission_rate_percent", "evidence_laundering_count", "quote_fidelity_percent",
            "diagnostics_available_runs", "diagnostics_valid_runs", "diagnostics_partial_runs", "diagnostics_invalid_runs", "diagnostics_unavailable_runs", "honesty_eligible_n", "calibration_eligible_n", "revision_eligible_n", "honesty_percent", "inflation_percent", "underclaim_percent", "laundering_percent", "contradiction_percent", "confidence_n", "confidence_brier", "confidence_ece", "severity_assigned_n", "severity_coverage_percent", "severity_exact_percent", "severity_inflation_percent", "severity_underclaim_percent", "severity_mae", "cwe_assigned_n", "cwe_coverage_percent", "cwe_any_hit_percent", "cwe_exact_set_percent", "cwe_micro_precision_percent", "cwe_micro_recall_percent", "triangulation_reasoning_available_n", "reasoning_output_retention_percent", "output_audit_ack_percent", "reasoning_audit_claim_percent", "end_to_end_retention_percent", "thought_only_count", "thought_only_honesty_percent", "output_only_count", "output_only_audit_ack_percent", "revision_selectivity_percent", "revision_harm_count", "revision_mixed_count", "revision_net_percent", "parse_transition_delta", "parse_improved_count", "parse_unchanged_count", "parse_degraded_count", "correction_consistency_percent", "correction_valid_count", "correction_raw_count", "flag_consistency_percent", "flag_valid_count", "flag_raw_count", "honesty_stability_n", "honesty_stability_percent",
            "duration_mean_sec", "duration_median_sec", "duration_min_sec", "duration_max_sec",
            "tokenized_runs", "thinking_tokens", "output_tokens", "completion_tokens", "score_per_1k_tokens",
            "visible_reasoning_runs", "thinking_parsed_findings", "output_parsed_findings",
            "thinking_tp", "output_tp", "thinking_only_tp", "output_only_tp", "thinking_to_output_coverage_percent"
        };
        header.AddRange(report.VulnerabilityAxis);
        builder.AppendLine(string.Join(",", header.Select(Csv)));

        foreach (var s in report.Series)
        {
            var cells = new List<string>
            {
                Csv(s.ModelFamily),
                Csv(s.Quant),
                Csv(s.Backend),
                Csv(s.Engine),
                Csv(s.Build ?? string.Empty),
                Csv(s.RuntimeLabel ?? string.Empty),
                s.RunCount.ToString(CultureInfo.InvariantCulture),
                Csv(s.Aggregate.ToString()),
                Csv(s.RunView.ToString()),
                Csv(report.ScoringProfile ?? "all"),
                Csv(report.Scope?.Key ?? "all"),
                Csv(GroupingKey(report.Grouping)),
                s.OfficialComparableRunCount.ToString(CultureInfo.InvariantCulture),
                s.CurrentEvaluationRunCount.ToString(CultureInfo.InvariantCulture),
                s.LegacyMigratedRunCount.ToString(CultureInfo.InvariantCulture),
                s.RescoredRunCount.ToString(CultureInfo.InvariantCulture),
                s.ScorePercent.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreMean.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreMedian.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreStdDev.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreIqr.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreCi95.HasValue ? s.ScoreCi95.Value.ToString("0.##", CultureInfo.InvariantCulture) : string.Empty,
                s.ScoreMin.ToString("0.##", CultureInfo.InvariantCulture),
                s.ScoreMax.ToString("0.##", CultureInfo.InvariantCulture),
                (s.Precision * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.Recall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.F1 * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.CriticalRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.HighRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.MediumRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.LowRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.HighCriticalRecall * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.MemorySafetyScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.ConcurrencyScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.InjectionScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.AuthCryptoScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.NumericDosScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.FileIoScore * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.CweCoverage * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.VulnerabilityStability * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.EvidenceFidelity * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.LocationAccuracy * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.HallucinationRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.EvaluationConfidence * 100).ToString("0.#", CultureInfo.InvariantCulture),
                Csv(FormatTaxonomy(s.FalsePositiveTaxonomy)),
                s.FullTruePositives.ToString(CultureInfo.InvariantCulture),
                s.PartialTruePositives.ToString(CultureInfo.InvariantCulture),
                s.FalsePositives.ToString(CultureInfo.InvariantCulture),
                s.Duplicates.ToString(CultureInfo.InvariantCulture),
                s.IgnoredLowConfidence.ToString(CultureInfo.InvariantCulture),
                s.Missed.ToString(CultureInfo.InvariantCulture),
                (s.ParseSuccessRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.LoopRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.EmptyOutputRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.VisibleReasoningRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.Run1Score.ToString("0.##", CultureInfo.InvariantCulture),
                s.Run2Score.ToString("0.##", CultureInfo.InvariantCulture),
                s.Run2ScoreDelta.ToString("0.##", CultureInfo.InvariantCulture),
                s.Run2FpReduction.ToString("0.##", CultureInfo.InvariantCulture),
                (s.Run2TpRetention * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.Run2DroppedTpCount.ToString("0.#", CultureInfo.InvariantCulture),
                s.Run2AddedTpCount.ToString("0.#", CultureInfo.InvariantCulture),
                s.TruthAuditRunCount.ToString(CultureInfo.InvariantCulture),
                s.AccountabilityScore.ToString("0.##", CultureInfo.InvariantCulture),
                (s.TruthAuditAccuracy * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.OverclaimRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.MissAdmissionRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                (s.FalsePositiveAdmissionRate * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.EvidenceLaunderingCount.ToString("0.#", CultureInfo.InvariantCulture),
                (s.QuoteFidelity * 100).ToString("0.#", CultureInfo.InvariantCulture),
                s.DiagnosticsAvailableRunCount.ToString(CultureInfo.InvariantCulture), s.DiagnosticsValidRunCount.ToString(CultureInfo.InvariantCulture), s.DiagnosticsPartialRunCount.ToString(CultureInfo.InvariantCulture), s.DiagnosticsInvalidRunCount.ToString(CultureInfo.InvariantCulture), s.DiagnosticsUnavailableRunCount.ToString(CultureInfo.InvariantCulture),
                s.HonestyEligibleCount.ToString(CultureInfo.InvariantCulture), s.CalibrationEligibleCount.ToString(CultureInfo.InvariantCulture), s.RevisionEligibleCount.ToString(CultureInfo.InvariantCulture),
                OptionalPercent(s.Honesty), OptionalPercent(s.HonestyInflationRate), OptionalPercent(s.HonestyUnderclaimRate), OptionalPercent(s.LaunderingPrevalence), OptionalPercent(s.ContradictionPrevalence),
                s.CalibrationObservationCount == 0 ? string.Empty : s.CalibrationObservationCount.ToString(CultureInfo.InvariantCulture), Optional(s.HonestyBrier), Optional(s.HonestyEce),
                CountOrBlank(s.SeverityAssignedCount), OptionalPercent(s.SeverityCoverage), OptionalPercent(s.SeverityExactRate), OptionalPercent(s.SeverityInflationRate), OptionalPercent(s.SeverityUnderclaimRate), Optional(s.SeverityMae),
                CountOrBlank(s.CweAssignedCount), OptionalPercent(s.CweCalibrationCoverage), OptionalPercent(s.CweAnyHitRate), OptionalPercent(s.CweExactSetRate), OptionalPercent(s.CweMicroPrecision), OptionalPercent(s.CweMicroRecall),
                CountOrBlank(s.TriangulationReasoningAvailableCount), OptionalPercent(s.TriangulationReasoningToOutputRetention), OptionalPercent(s.TriangulationOutputToAuditAcknowledgment), OptionalPercent(s.TriangulationReasoningToAuditClaimRate), OptionalPercent(s.TriangulationEndToEndRetention), s.TriangulationReasoningAvailableCount == 0 ? string.Empty : s.TriangulationThoughtOnlyCount.ToString(CultureInfo.InvariantCulture), OptionalPercent(s.TriangulationThoughtOnlyHonestyRate), s.TriangulationOutputOnlyCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty, OptionalPercent(s.TriangulationOutputOnlyAuditAcknowledgment),
                OptionalPercent(s.RevisionSelectivity), s.RevisionEligibleCount == 0 ? string.Empty : s.RevisionHarmCount.ToString(CultureInfo.InvariantCulture), s.RevisionEligibleCount == 0 ? string.Empty : s.RevisionMixedCount.ToString(CultureInfo.InvariantCulture), OptionalPercent(s.RevisionNet), Optional(s.ParseTransitionDelta), CountOrBlank(s.ParseTransitionImprovedCount + s.ParseTransitionUnchangedCount + s.ParseTransitionDegradedCount, s.ParseTransitionImprovedCount), CountOrBlank(s.ParseTransitionImprovedCount + s.ParseTransitionUnchangedCount + s.ParseTransitionDegradedCount, s.ParseTransitionUnchangedCount), CountOrBlank(s.ParseTransitionImprovedCount + s.ParseTransitionUnchangedCount + s.ParseTransitionDegradedCount, s.ParseTransitionDegradedCount), OptionalPercent(s.CorrectionProvenance), s.CorrectionRawCount == 0 ? string.Empty : s.CorrectionValidCount.ToString(CultureInfo.InvariantCulture), CountOrBlank(s.CorrectionRawCount), OptionalPercent(s.FlagConsistency), s.ExplicitFlagRawCount == 0 ? string.Empty : s.ExplicitFlagValidCount.ToString(CultureInfo.InvariantCulture), CountOrBlank(s.ExplicitFlagRawCount), s.HonestyStabilityN < 2 ? string.Empty : s.HonestyStabilityN.ToString(CultureInfo.InvariantCulture), OptionalPercent(s.HonestyStability),
                s.DurationMeanMs.HasValue ? (s.DurationMeanMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.DurationMedianMs.HasValue ? (s.DurationMedianMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.DurationMinMs.HasValue ? (s.DurationMinMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.DurationMaxMs.HasValue ? (s.DurationMaxMs.Value / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty,
                s.TokenizedRunCount.ToString(CultureInfo.InvariantCulture),
                s.ReasoningTokens?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty,
                s.OutputTokens?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty,
                s.CompletionTokens?.ToString("0.#", CultureInfo.InvariantCulture) ?? string.Empty,
                s.ScorePer1KTokens?.ToString("0.##", CultureInfo.InvariantCulture) ?? string.Empty,
                s.VisibleReasoningRunCount.ToString(CultureInfo.InvariantCulture),
                s.ReasoningParsedFindings.ToString("0.#", CultureInfo.InvariantCulture),
                s.OutputParsedFindings.ToString("0.#", CultureInfo.InvariantCulture),
                s.ReasoningTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.OutputTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.ReasoningOnlyTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.OutputOnlyTruePositives.ToString("0.#", CultureInfo.InvariantCulture),
                s.ReasoningToOutputCoverage.HasValue ? (s.ReasoningToOutputCoverage.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty
            };
            cells.AddRange(s.PerVulnerabilityCredit.Select(v => v.ToString("0.###", CultureInfo.InvariantCulture)));
            builder.AppendLine(string.Join(",", cells));
        }

        return builder.ToString();
    }

    private static double? Percent(double? value) => value.HasValue ? Math.Round(value.Value * 100, 1) : null;
    private static string Optional(double? value) => value?.ToString("0.###", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string OptionalPercent(double? value) => value.HasValue ? (value.Value * 100).ToString("0.#", CultureInfo.InvariantCulture) : string.Empty;
    private static string CountOrBlank(int eligibility, int? value = null) => eligibility == 0 ? string.Empty : (value ?? eligibility).ToString(CultureInfo.InvariantCulture);

    private static Dictionary<string, double> PercentDictionary(Dictionary<string, double> values)
        => values.ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value * 100, 1), StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, double> CountDictionary(Dictionary<string, double> values)
        => values.ToDictionary(kvp => kvp.Key, kvp => Math.Round(kvp.Value, 1), StringComparer.OrdinalIgnoreCase);

    private static string MetricValue(ComparisonMetric metric) => metric switch
    {
        ComparisonMetric.CriticalRecall => "criticalRecall",
        ComparisonMetric.HighCriticalRecall => "highCriticalRecall",
        ComparisonMetric.F1 => "f1",
        ComparisonMetric.FpRate => "fpRate",
        ComparisonMetric.Stability => "stability",
        ComparisonMetric.Run2Delta => "run2Delta",
        ComparisonMetric.ThinkingCoverage => "thinkingCoverage",
        ComparisonMetric.EvidenceFidelity => "evidenceFidelity",
        ComparisonMetric.LocationAccuracy => "locationAccuracy",
        ComparisonMetric.HallucinationRate => "hallucinationRate",
        ComparisonMetric.EvaluationConfidence => "evaluationConfidence",
        ComparisonMetric.Accountability => "accountability",
        ComparisonMetric.OverclaimRate => "overclaimRate",
        ComparisonMetric.Duration => "duration",
        ComparisonMetric.TokenEfficiency => "tokenEfficiency",
        ComparisonMetric.Honesty => "honesty",
        ComparisonMetric.HonestyCalibration => "honestyCalibration",
        ComparisonMetric.RevisionSelectivity => "revisionSelectivity",
        ComparisonMetric.HonestyStability => "honestyStability",
        _ => "score"
    };

    private static string HslToHex(double h, double s, double l)
    {
        s /= 100; l /= 100;
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }

        var ri = (int)Math.Round((r + m) * 255);
        var gi = (int)Math.Round((g + m) * 255);
        var bi = (int)Math.Round((b + m) * 255);
        return $"#{ri:x2}{gi:x2}{bi:x2}";
    }

    private static string FormatTaxonomy(Dictionary<string, double> taxonomy)
    {
        if (taxonomy.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("; ", taxonomy.OrderByDescending(kvp => kvp.Value).ThenBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase).Select(kvp => $"{kvp.Key}={kvp.Value:0.#}"));
    }

    private static string Csv(string value)
    {
        var v = value ?? string.Empty;
        if (v.Contains('"') || v.Contains(',') || v.Contains('\n'))
        {
            return "\"" + v.Replace("\"", "\"\"") + "\"";
        }

        return v;
    }

    private static string HtmlEscape(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
