namespace SuperCalcBenchmark.Core;

/// <summary>
/// Turns a set of <see cref="ArchiveGroup"/>s into the data a comparison view needs:
/// score/quality aggregates, per-vulnerability heatmap vectors, severity/category axes,
/// and run-level drilldown rows. The report consumes archive scorecards only; hidden
/// ground-truth metadata is optional and used locally after scoring.
/// </summary>
public sealed class ComparisonReport
{
    public string BenchmarkId { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;
    public ComparisonAggregate Aggregate { get; init; } = ComparisonAggregate.Average;
    public ComparisonRunView RunView { get; init; } = ComparisonRunView.Primary;
    public ComparisonMetric Metric { get; init; } = ComparisonMetric.Score;
    public string? ScoringProfile { get; init; }

    /// <summary>How records were partitioned into series (model+quant, additionally by backend, or by full runtime).</summary>
    public ComparisonGrouping Grouping { get; init; } = ComparisonGrouping.ModelQuant;

    /// <summary>Optional record scope (a parser version or a tool version) that restricted <see cref="Series"/>.</summary>
    public ComparisonScope? Scope { get; init; }

    /// <summary>Every scope that exists in the loaded archive, with run counts, for pickers.</summary>
    public List<ComparisonScope> AvailableScopes { get; init; } = [];

    /// <summary>Shared vulnerability id axis. Kept for older consumers and CSV headers.</summary>
    public List<string> VulnerabilityAxis { get; init; } = [];

    /// <summary>Shared vulnerability axis enriched with local-only metadata where available.</summary>
    public List<VulnerabilityAxisItem> VulnerabilityMetadata { get; init; } = [];

    /// <summary>All selected scores, including historical/deprecated parser evaluations.</summary>
    public List<ComparisonSeries> Series { get; init; } = [];

    /// <summary>
    /// The same comparison recomputed from current parser evaluations only. The HTML report
    /// uses this projection by default and can opt into <see cref="Series"/> interactively.
    /// </summary>
    public List<ComparisonSeries> CurrentEvaluationSeries { get; init; } = [];

    public bool IsEmpty => Series.Count == 0;

    /// <summary>
    /// Builds a comparison. Average and Median aggregate all records in a model+quant
    /// group for the selected <paramref name="runView"/>; Best uses the highest-scoring
    /// sample. Pass <paramref name="familyFilter"/> to compare only quants of one model family.
    /// </summary>
    public static ComparisonReport Build(
        IReadOnlyList<ArchiveGroup> groups,
        string benchmarkId,
        ComparisonAggregate aggregate = ComparisonAggregate.Average,
        string? familyFilter = null,
        VulnerabilityMetadataIndex? metadataIndex = null,
        ComparisonRunView runView = ComparisonRunView.Primary,
        ComparisonMetric metric = ComparisonMetric.Score,
        string? scoringProfile = null,
        ComparisonGrouping grouping = ComparisonGrouping.ModelQuant,
        ComparisonScope? scope = null)
        => BuildCore(
            groups,
            benchmarkId,
            aggregate,
            familyFilter,
            metadataIndex ?? VulnerabilityMetadataIndex.Empty,
            runView,
            metric,
            scoringProfile,
            currentEvaluationOnly: false,
            fixedAxis: null,
            fixedAxisMetadata: null,
            grouping,
            scope);

    /// <summary>
    /// Series only, for one scope × grouping projection on a fixed axis. Used by the HTML writer
    /// to embed several selectable projections without recomputing the axis or metadata.
    /// </summary>
    public static List<ComparisonSeries> BuildSeries(
        IReadOnlyList<ArchiveGroup> groups,
        string benchmarkId,
        ComparisonAggregate aggregate,
        string? familyFilter,
        VulnerabilityMetadataIndex metadataIndex,
        ComparisonRunView runView,
        ComparisonMetric metric,
        string? scoringProfile,
        ComparisonGrouping grouping,
        ComparisonScope? scope,
        IReadOnlyList<string> fixedAxis,
        IReadOnlyList<VulnerabilityAxisItem> fixedAxisMetadata)
        => BuildCore(
            groups,
            benchmarkId,
            aggregate,
            familyFilter,
            metadataIndex,
            runView,
            metric,
            scoringProfile,
            currentEvaluationOnly: scope?.Kind == ComparisonScopeKind.Current,
            fixedAxis,
            fixedAxisMetadata,
            grouping,
            scope,
            skipCurrentProjection: true).Series;

    /// <summary>Distinct parser versions and tool versions across the archive, with detection-run counts.</summary>
    public static List<ComparisonScope> DiscoverScopes(IReadOnlyList<ArchiveGroup> groups)
    {
        var records = groups.SelectMany(g => g.Records).ToList();
        var detectionRuns = records
            .SelectMany(r => r.Runs.Where(run => !string.Equals(run.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase)).Select(run => (Record: r, Run: run)))
            .ToList();

        var scopes = new List<ComparisonScope>
        {
            new(ComparisonScopeKind.All, string.Empty, detectionRuns.Count),
            new(ComparisonScopeKind.Current, ResponseParser.CurrentParserVersion, detectionRuns.Count(x => x.Run.IsCurrentEvaluation))
        };

        foreach (var parserGroup in detectionRuns
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Run.ParserVersion) ? "unknown" : x.Run.ParserVersion, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => ParserVersionOrder(g.Key))
                     .ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            scopes.Add(new ComparisonScope(ComparisonScopeKind.ParserVersion, parserGroup.Key, parserGroup.Count()));
        }

        foreach (var toolGroup in detectionRuns
                     .GroupBy(x => string.IsNullOrWhiteSpace(x.Record.ToolVersion) ? "unknown" : x.Record.ToolVersion.Trim(), StringComparer.OrdinalIgnoreCase)
                     .OrderByDescending(g => VersionSortKey(g.Key)))
        {
            scopes.Add(new ComparisonScope(ComparisonScopeKind.ToolVersion, toolGroup.Key, toolGroup.Count()));
        }

        return scopes;
    }

    private static int ParserVersionOrder(string parserVersion)
    {
        var index = ResponseParser.KnownParserVersions
            .Select((version, i) => (version, i))
            .FirstOrDefault(pair => string.Equals(pair.version, parserVersion, StringComparison.OrdinalIgnoreCase));
        return index == default && !string.Equals(ResponseParser.KnownParserVersions[0], parserVersion, StringComparison.OrdinalIgnoreCase)
            ? int.MaxValue
            : index.i;
    }

    private static Version VersionSortKey(string value)
        => Version.TryParse(value, out var parsed) ? parsed : new Version(0, 0);

    /// <summary>Splits archive groups by backend or full runtime identity when requested.</summary>
    public static IReadOnlyList<ArchiveGroup> Partition(IReadOnlyList<ArchiveGroup> groups, ComparisonGrouping grouping)
    {
        if (grouping == ComparisonGrouping.ModelQuant)
        {
            return groups;
        }

        var result = new List<ArchiveGroup>();
        foreach (var group in groups)
        {
            foreach (var partition in group.Records.GroupBy(record => grouping == ComparisonGrouping.ModelQuantBackend
                             ? record.ServerMetadata.NormalizedBackend
                             : record.ServerMetadata.RuntimeKey,
                         StringComparer.OrdinalIgnoreCase))
            {
                var first = partition.First();
                var suffix = grouping == ComparisonGrouping.ModelQuantBackend
                    ? RuntimeKeys.NormalizeBackend(first.ServerMetadata.Backend)
                    : TextUtil.SafeFileNamePart(first.ServerMetadata.RuntimeKey);
                result.Add(new ArchiveGroup
                {
                    GroupKey = $"{group.GroupKey}__{suffix}",
                    ModelFamily = group.ModelFamily,
                    Quant = group.Quant,
                    Records = partition.OrderByDescending(r => r.CompletedAt).ToList(),
                    Backend = RuntimeKeys.NormalizeBackend(first.ServerMetadata.Backend),
                    RuntimeKey = first.ServerMetadata.RuntimeKey,
                    RuntimeLabel = grouping == ComparisonGrouping.ModelQuantBackend
                        ? RuntimeKeys.DisplayBackend(first.ServerMetadata.Backend)
                        : first.ServerMetadata.RuntimeDisplayLabel
                });
            }
        }

        return result
            .OrderByDescending(g => g.BestScorePercent)
            .ThenBy(g => g.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static ComparisonReport BuildCore(
        IReadOnlyList<ArchiveGroup> groups,
        string benchmarkId,
        ComparisonAggregate aggregate,
        string? familyFilter,
        VulnerabilityMetadataIndex metadataIndex,
        ComparisonRunView runView,
        ComparisonMetric metric,
        string? scoringProfile,
        bool currentEvaluationOnly,
        IReadOnlyList<string>? fixedAxis,
        IReadOnlyList<VulnerabilityAxisItem>? fixedAxisMetadata,
        ComparisonGrouping grouping = ComparisonGrouping.ModelQuant,
        ComparisonScope? scope = null,
        bool skipCurrentProjection = false)
    {
        var availableScopes = fixedAxis is null ? DiscoverScopes(groups) : [];
        var partitioned = Partition(groups, grouping);
        var selected = string.IsNullOrWhiteSpace(familyFilter)
            ? partitioned
            : partitioned.Where(g => string.Equals(g.ModelFamily, familyFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var selectedWithSamples = selected
            .Select(group => new
            {
                Group = group,
                Samples = group.Records
                    .Select(record => ComparisonSample.TryCreate(record, runView, scoringProfile))
                    .Where(sample => sample is not null)
                    .Select(sample => sample!)
                    .Where(sample => !currentEvaluationOnly || sample.Run.IsCurrentEvaluation)
                    .Where(sample => scope is null || scope.Matches(sample.Record, sample.Run))
                    .ToList()
            })
            .Where(item => item.Samples.Count > 0)
            .ToList();

        var axis = fixedAxis?.ToList()
                   ?? selectedWithSamples
                       .SelectMany(item => item.Samples)
                       .SelectMany(sample => sample.CreditIds)
                       .Concat(metadataIndex.Items.Select(i => i.Id))
                       .Where(id => !string.IsNullOrWhiteSpace(id))
                       .Distinct(StringComparer.OrdinalIgnoreCase)
                       .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                       .ToList();

        var axisMetadata = fixedAxisMetadata?.ToList()
                           ?? axis.Select(metadataIndex.GetOrCreate).ToList();
        var series = new List<ComparisonSeries>();
        foreach (var item in selectedWithSamples)
        {
            var group = item.Group;
            var samples = item.Samples;

            var bestSample = samples.OrderByDescending(s => s.ScorePercent).First();
            var scoreValues = samples.Select(s => s.ScorePercent).ToList();
            var score = AggregateMetric(samples, s => s.ScorePercent, aggregate, bestSample);
            var perVuln = axis.Select(id => AggregateCredit(samples, id, aggregate, bestSample)).ToList();
            IReadOnlyList<ComparisonSample> rateSamples = aggregate == ComparisonAggregate.Best ? [bestSample] : samples;
            var visibleReasoningRunCount = rateSamples.Count(s => s.Run.ReasoningDisclosure?.HasVisibleReasoning == true);
            var pairMetrics = BuildPairMetrics(samples, aggregate, bestSample, scoringProfile, currentEvaluationOnly);
            var truthAudit = BuildTruthAuditMetrics(samples, aggregate, bestSample);
            var diagnostics = BuildDiagnosticsMetrics(samples, aggregate, bestSample);
            var severity = BuildBucketMetrics(axisMetadata, perVuln, item => item.Severity);
            var categories = BuildBucketMetrics(axisMetadata, perVuln, item => item.Category);
            var cwe = BuildCweMetrics(axisMetadata, perVuln);
            var modules = BuildBucketMetrics(axisMetadata, perVuln, item => item.Module);
            var durationValues = samples.Select(s => s.Run.DurationMs).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            var findingTotal = rateSamples.Sum(s => Math.Max(0, s.Run.FindingCount));
            var duplicateTotal = rateSamples.Sum(s => Math.Max(0, s.Run.Duplicates));
            var ignoredTotal = rateSamples.Sum(s => Math.Max(0, s.Run.IgnoredLowConfidence));
            var fpTotal = rateSamples.Sum(s => Math.Max(0, s.Run.FalsePositives));
            var outputTokens = AggregateNullableMetric(samples, s => s.Run.ResponseTokens, aggregate, bestSample);
            var reasoningTokens = AggregateNullableMetric(samples, s => s.Run.ReasoningTokens, aggregate, bestSample);
            var completionTokens = AggregateNullableMetric(samples, s => s.Run.CompletionTokens, aggregate, bestSample);
            var scorePer1KTokens = completionTokens > 0 ? score * 1000.0 / completionTokens.Value : (double?)null;

            var backendBreakdown = samples
                .GroupBy(s => s.Record.ServerMetadata.NormalizedBackend, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var runtimeBreakdown = samples
                .GroupBy(s => s.Record.ServerMetadata.RuntimeDisplayLabel, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
            var knownBackends = backendBreakdown.Keys.Where(k => !string.Equals(k, ServerRuntimeInfo.UnknownValue, StringComparison.OrdinalIgnoreCase)).ToList();
            var seriesBackend = group.Backend
                                ?? (knownBackends.Count == 1 && backendBreakdown.Count == 1 ? knownBackends[0]
                                    : knownBackends.Count == 0 ? ServerRuntimeInfo.UnknownValue : "mixed");
            var seriesLabel = grouping == ComparisonGrouping.ModelQuant
                ? $"{group.ModelFamily} · {group.Quant}"
                : $"{group.ModelFamily} · {group.Quant} · {group.RuntimeLabel}";

            series.Add(new ComparisonSeries
            {
                GroupKey = group.GroupKey,
                ModelFamily = group.ModelFamily,
                Quant = group.Quant,
                Label = seriesLabel,
                Backend = seriesBackend,
                Engine = samples.Select(s => s.Record.ServerMetadata.NormalizedEngine).Where(e => e != ServerRuntimeInfo.UnknownValue).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
                    ? samples.Select(s => s.Record.ServerMetadata.NormalizedEngine).First(e => e != ServerRuntimeInfo.UnknownValue)
                    : samples.Any(s => s.Record.ServerMetadata.NormalizedEngine != ServerRuntimeInfo.UnknownValue) ? "mixed" : ServerRuntimeInfo.UnknownValue,
                Build = samples.Select(s => s.Record.ServerMetadata.LlamaBuild).Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => RuntimeKeys.ShortBuild(b!)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: 1 } builds
                    ? builds[0]
                    : null,
                RuntimeKey = group.RuntimeKey,
                RuntimeLabel = group.RuntimeLabel,
                BackendBreakdown = backendBreakdown,
                RuntimeBreakdown = runtimeBreakdown,
                ToolVersionBreakdown = samples
                    .GroupBy(s => string.IsNullOrWhiteSpace(s.Record.ToolVersion) ? "unknown" : s.Record.ToolVersion.Trim(), StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                ParserVersionBreakdown = samples
                    .GroupBy(s => string.IsNullOrWhiteSpace(s.Run.ParserVersion) ? "unknown" : s.Run.ParserVersion, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase),
                RunCount = samples.Count,
                Aggregate = aggregate,
                RunView = runView,
                ScorePercent = score,
                ScoreMean = scoreValues.Average(),
                ScoreMedian = Median(scoreValues),
                ScoreStdDev = StandardDeviation(scoreValues),
                ScoreIqr = InterquartileRange(scoreValues),
                ScoreCi95 = scoreValues.Count >= 3 ? 1.96 * StandardDeviation(scoreValues) / Math.Sqrt(scoreValues.Count) : null,
                ScoreMin = scoreValues.Min(),
                ScoreMax = scoreValues.Max(),
                Precision = AggregateMetric(samples, s => s.Precision, aggregate, bestSample),
                Recall = AggregateMetric(samples, s => s.Recall, aggregate, bestSample),
                F1 = AggregateMetric(samples, s => s.F1, aggregate, bestSample),
                FullTruePositives = RoundToInt(AggregateMetric(samples, s => s.FullTruePositives, aggregate, bestSample)),
                PartialTruePositives = RoundToInt(AggregateMetric(samples, s => s.PartialTruePositives, aggregate, bestSample)),
                FalsePositives = RoundToInt(AggregateMetric(samples, s => s.FalsePositives, aggregate, bestSample)),
                Duplicates = RoundToInt(AggregateMetric(samples, s => s.Run.Duplicates, aggregate, bestSample)),
                IgnoredLowConfidence = RoundToInt(AggregateMetric(samples, s => s.Run.IgnoredLowConfidence, aggregate, bestSample)),
                Missed = RoundToInt(AggregateMetric(samples, s => s.Missed, aggregate, bestSample)),
                OfficialRunCount = samples.Count(s => string.Equals(s.Record.BenchmarkProfile, "official", StringComparison.OrdinalIgnoreCase)),
                OfficialComparableRunCount = samples.Count(s => s.Run.OfficialComparable),
                CurrentEvaluationRunCount = samples.Count(s => s.Run.IsCurrentEvaluation),
                LegacyMigratedRunCount = samples.Count(s => s.Run.IsLegacyMigrated),
                RescoredRunCount = samples.Count(s => s.Run.IsRescored),
                SourceHashMatchCount = samples.Count(s => s.Record.SourceHashMatches),
                VisibleReasoningRunCount = visibleReasoningRunCount,
                ReasoningParsedFindings = AggregateReasoningMetric(samples, r => r.ReasoningParsedFindingCount, aggregate, bestSample),
                OutputParsedFindings = AggregateReasoningMetric(samples, r => r.OutputParsedFindingCount, aggregate, bestSample),
                ReasoningTruePositives = AggregateReasoningMetric(samples, r => r.ReasoningTruePositiveCount, aggregate, bestSample),
                OutputTruePositives = AggregateReasoningMetric(samples, r => r.OutputTruePositiveCount, aggregate, bestSample),
                ReasoningOnlyTruePositives = AggregateReasoningMetric(samples, r => r.ReasoningOnlyTruePositiveCount, aggregate, bestSample),
                OutputOnlyTruePositives = AggregateReasoningMetric(samples, r => r.OutputOnlyTruePositiveCount, aggregate, bestSample),
                ReasoningToOutputCoverage = AggregateReasoningNullableMetric(samples, r => r.ReasoningToOutputCoverage, aggregate, bestSample),
                PerVulnerabilityCredit = perVuln,
                SeverityRecall = severity,
                CategoryScores = categories,
                CweRecall = cwe.Recall,
                ModuleScores = modules,
                CriticalRecall = ValueOrZero(severity, "Critical"),
                HighRecall = ValueOrZero(severity, "High"),
                MediumRecall = ValueOrZero(severity, "Medium"),
                LowRecall = ValueOrZero(severity, "Low"),
                HighCriticalRecall = AverageExisting([ValueOrNullable(severity, "Critical"), ValueOrNullable(severity, "High")]),
                MemorySafetyScore = ValueOrZero(categories, "Memory Safety"),
                ConcurrencyScore = ValueOrZero(categories, "Concurrency"),
                InjectionScore = ValueOrZero(categories, "Injection"),
                AuthCryptoScore = AverageExisting([ValueOrNullable(categories, "Auth/Session"), ValueOrNullable(categories, "Crypto")]),
                NumericDosScore = ValueOrZero(categories, "Numeric/DoS"),
                FileIoScore = ValueOrZero(categories, "File/I/O"),
                CweCoverage = cwe.Coverage,
                VulnerabilityStability = CalculateVulnerabilityStability(samples, axis),
                EvidenceFidelity = AggregateMetric(samples, s => s.Run.EvidenceFidelity, aggregate, bestSample),
                LocationAccuracy = AggregateMetric(samples, s => s.Run.LocationAccuracy, aggregate, bestSample),
                HallucinationRate = AggregateMetric(samples, s => s.Run.HallucinationRate, aggregate, bestSample),
                EvaluationConfidence = AggregateMetric(samples, s => s.Run.EvaluationConfidence, aggregate, bestSample),
                FalsePositiveTaxonomy = AggregateFalsePositiveTaxonomy(samples, aggregate, bestSample),
                FpPerFinding = findingTotal == 0 ? 0 : (double)fpTotal / findingTotal,
                DuplicateRate = findingTotal == 0 ? 0 : (double)duplicateTotal / findingTotal,
                IgnoredLowConfidenceRate = findingTotal == 0 ? 0 : (double)ignoredTotal / findingTotal,
                ParseSuccessRate = rateSamples.Count == 0 ? 0 : rateSamples.Count(s => IsParseSuccess(s.Run.ParseMode)) / (double)rateSamples.Count,
                LoopRate = rateSamples.Count == 0 ? 0 : rateSamples.Count(s => s.Run.LoopDetected) / (double)rateSamples.Count,
                EmptyOutputRate = rateSamples.Count == 0 ? 0 : rateSamples.Count(s => s.Run.EmptyOutputWithReasoning) / (double)rateSamples.Count,
                VisibleReasoningRate = rateSamples.Count == 0 ? 0 : visibleReasoningRunCount / (double)rateSamples.Count,
                Run1Score = pairMetrics.Run1Score,
                Run2Score = pairMetrics.Run2Score,
                Run2ScoreDelta = pairMetrics.ScoreDelta,
                Run2FpReduction = pairMetrics.FpReduction,
                Run2TpRetention = pairMetrics.TpRetention,
                Run2DroppedTpCount = pairMetrics.DroppedCount,
                Run2AddedTpCount = pairMetrics.AddedCount,
                Run2DroppedTruePositiveIds = pairMetrics.DroppedIds,
                Run2AddedTruePositiveIds = pairMetrics.AddedIds,
                TruthAuditRunCount = truthAudit.RunCount,
                AccountabilityScore = truthAudit.AccountabilityScore,
                TruthAuditAccuracy = truthAudit.TruthAuditAccuracy,
                OverclaimRate = truthAudit.OverclaimRate,
                MissAdmissionRate = truthAudit.MissAdmissionRate,
                FalsePositiveAdmissionRate = truthAudit.FalsePositiveAdmissionRate,
                EvidenceLaunderingCount = truthAudit.EvidenceLaunderingCount,
                QuoteFidelity = truthAudit.QuoteFidelity,
                DiagnosticsAvailableRunCount = diagnostics.Available,
                DiagnosticsValidRunCount = diagnostics.Valid,
                DiagnosticsPartialRunCount = diagnostics.Partial,
                DiagnosticsInvalidRunCount = diagnostics.Invalid,
                DiagnosticsUnavailableRunCount = diagnostics.Unavailable,
                HonestyEligibleCount = diagnostics.HonestyEligible,
                CalibrationEligibleCount = diagnostics.CalibrationEligible,
                RevisionEligibleCount = diagnostics.RevisionEligible,
                Honesty = diagnostics.Honesty,
                HonestyInflationRate = diagnostics.InflationRate,
                HonestyUnderclaimRate = diagnostics.UnderclaimRate,
                LaunderingPrevalence = diagnostics.LaunderingPrevalence,
                ContradictionPrevalence = diagnostics.ContradictionPrevalence,
                HonestyCalibration = diagnostics.Calibration,
                HonestyBrier = diagnostics.Brier,
                HonestyEce = diagnostics.Ece,
                CalibrationObservationCount = diagnostics.CalibrationN,
                SeverityAssignedCount = diagnostics.SeverityAssignedN,
                SeverityCoverage = diagnostics.SeverityCoverage,
                SeverityExactRate = diagnostics.SeverityExact,
                SeverityInflationRate = diagnostics.SeverityInflation,
                SeverityUnderclaimRate = diagnostics.SeverityUnderclaim,
                SeverityMae = diagnostics.SeverityMae,
                CweAssignedCount = diagnostics.CweAssignedN,
                CweCalibrationCoverage = diagnostics.CweCoverage,
                CweAnyHitRate = diagnostics.CweAnyHit,
                CweExactSetRate = diagnostics.CweExactSet,
                CweMicroPrecision = diagnostics.CwePrecision,
                CweMicroRecall = diagnostics.CweRecall,
                TriangulationReasoningAvailableCount = diagnostics.TriangulationN,
                TriangulationReasoningToOutputRetention = diagnostics.ReasoningOutputRetention,
                TriangulationOutputToAuditAcknowledgment = diagnostics.OutputAuditAcknowledgment,
                TriangulationReasoningToAuditClaimRate = diagnostics.ReasoningAuditClaim,
                TriangulationEndToEndRetention = diagnostics.EndToEndRetention,
                TriangulationThoughtOnlyCount = diagnostics.ThoughtOnlyCount,
                TriangulationThoughtOnlyHonestyRate = diagnostics.ThoughtOnlyHonesty,
                TriangulationOutputOnlyCount = diagnostics.OutputOnlyCount,
                TriangulationOutputOnlyAuditAcknowledgment = diagnostics.OutputOnlyAuditAcknowledgment,
                RevisionSelectivity = diagnostics.RevisionSelectivity,
                RevisionHarmCount = diagnostics.RevisionHarm,
                RevisionMixedCount = diagnostics.RevisionMixed,
                RevisionNet = diagnostics.RevisionNet,
                ParseTransitionDelta = diagnostics.ParseDelta,
                ParseTransitionImprovedCount = diagnostics.ParseImproved,
                ParseTransitionUnchangedCount = diagnostics.ParseUnchanged,
                ParseTransitionDegradedCount = diagnostics.ParseDegraded,
                FlagConsistency = diagnostics.FlagConsistency,
                ExplicitFlagValidCount = diagnostics.FlagValid,
                ExplicitFlagRawCount = diagnostics.FlagRaw,
                CorrectionProvenance = diagnostics.CorrectionProvenance,
                CorrectionValidCount = diagnostics.CorrectionValid,
                CorrectionRawCount = diagnostics.CorrectionRaw,
                HonestyStability = diagnostics.Stability,
                HonestyStabilityN = diagnostics.StabilityN,
                CategoricalItemAgreement = diagnostics.CategoricalAgreement,
                DurationMeanMs = durationValues.Count == 0 ? null : durationValues.Average(),
                DurationMedianMs = durationValues.Count == 0 ? null : Median(durationValues),
                DurationMinMs = durationValues.Count == 0 ? null : durationValues.Min(),
                DurationMaxMs = durationValues.Count == 0 ? null : durationValues.Max(),
                OutputTokens = outputTokens,
                ReasoningTokens = reasoningTokens,
                CompletionTokens = completionTokens,
                ScorePer1KTokens = scorePer1KTokens,
                TokenizedRunCount = samples.Count(s => s.Run.CompletionTokens.HasValue),
                Details = samples.Select(s => ComparisonRunDetail.FromSample(s)).OrderByDescending(d => d.CompletedAt).ToList()
            });
        }

        var orderedSeries = series
            .OrderByDescending(s => SortMetricValue(s, metric))
            .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        List<ComparisonSeries> currentSeries = currentEvaluationOnly || skipCurrentProjection
            ? []
            : BuildCore(
                groups,
                benchmarkId,
                aggregate,
                familyFilter,
                metadataIndex,
                runView,
                metric,
                scoringProfile,
                currentEvaluationOnly: true,
                fixedAxis: axis,
                fixedAxisMetadata: axisMetadata,
                grouping,
                scope,
                skipCurrentProjection: true).Series;

        return new ComparisonReport
        {
            BenchmarkId = benchmarkId,
            Aggregate = aggregate,
            RunView = runView,
            Metric = metric,
            ScoringProfile = scoringProfile,
            Grouping = grouping,
            Scope = scope,
            AvailableScopes = availableScopes,
            VulnerabilityAxis = axis,
            VulnerabilityMetadata = axisMetadata,
            Series = orderedSeries,
            CurrentEvaluationSeries = currentSeries
        };
    }

    private static double SortMetricValue(ComparisonSeries series, ComparisonMetric metric) => metric switch
    {
        ComparisonMetric.CriticalRecall => series.CriticalRecall,
        ComparisonMetric.HighCriticalRecall => series.HighCriticalRecall,
        ComparisonMetric.F1 => series.F1,
        ComparisonMetric.FpRate => series.FpPerFinding,
        ComparisonMetric.Stability => series.VulnerabilityStability,
        ComparisonMetric.Run2Delta => series.Run2ScoreDelta,
        ComparisonMetric.ThinkingCoverage => series.ReasoningToOutputCoverage ?? 0,
        ComparisonMetric.EvidenceFidelity => series.EvidenceFidelity,
        ComparisonMetric.LocationAccuracy => series.LocationAccuracy,
        ComparisonMetric.HallucinationRate => series.HallucinationRate,
        ComparisonMetric.EvaluationConfidence => series.EvaluationConfidence,
        ComparisonMetric.Accountability => series.AccountabilityScore,
        ComparisonMetric.OverclaimRate => series.OverclaimRate,
        ComparisonMetric.Duration => series.DurationMedianMs ?? series.DurationMeanMs ?? 0,
        ComparisonMetric.TokenEfficiency => series.ScorePer1KTokens ?? 0,
        ComparisonMetric.Honesty => series.Honesty ?? double.NegativeInfinity,
        ComparisonMetric.HonestyCalibration => series.HonestyCalibration ?? double.NegativeInfinity,
        ComparisonMetric.RevisionSelectivity => series.RevisionSelectivity ?? double.NegativeInfinity,
        ComparisonMetric.HonestyStability => series.HonestyStability ?? double.NegativeInfinity,
        _ => series.ScorePercent
    };

    private static double AggregateCredit(
        IReadOnlyList<ComparisonSample> samples,
        string vulnerabilityId,
        ComparisonAggregate aggregate,
        ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            return bestSample.Credit(vulnerabilityId);
        }

        var values = samples.Select(s => s.Credit(vulnerabilityId)).ToList();
        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
    }

    private static double AggregateMetric(
        IReadOnlyList<ComparisonSample> samples,
        Func<ComparisonSample, double> selector,
        ComparisonAggregate aggregate,
        ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            return selector(bestSample);
        }

        var values = samples.Select(selector).ToList();
        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
    }

    private static double? AggregateNullableMetric(
        IReadOnlyList<ComparisonSample> samples,
        Func<ComparisonSample, int?> selector,
        ComparisonAggregate aggregate,
        ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            return selector(bestSample);
        }

        var values = samples.Select(selector).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        if (values.Count == 0)
        {
            return null;
        }

        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
    }

    private static double AggregateReasoningMetric(
        IReadOnlyList<ComparisonSample> samples,
        Func<ReasoningDisclosureDiagnostics, double> selector,
        ComparisonAggregate aggregate,
        ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            var bestDiagnostics = bestSample.Run.ReasoningDisclosure;
            return bestDiagnostics?.HasVisibleReasoning == true ? selector(bestDiagnostics) : 0;
        }

        var values = samples
            .Select(s => s.Run.ReasoningDisclosure)
            .Where(d => d?.HasVisibleReasoning == true)
            .Select(d => selector(d!))
            .ToList();
        if (values.Count == 0)
        {
            return 0;
        }

        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
    }

    private static double? AggregateReasoningNullableMetric(
        IReadOnlyList<ComparisonSample> samples,
        Func<ReasoningDisclosureDiagnostics, double?> selector,
        ComparisonAggregate aggregate,
        ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            var bestDiagnostics = bestSample.Run.ReasoningDisclosure;
            return bestDiagnostics?.HasVisibleReasoning == true ? selector(bestDiagnostics) : null;
        }

        var values = samples
            .Select(s => s.Run.ReasoningDisclosure)
            .Where(d => d?.HasVisibleReasoning == true)
            .Select(d => selector(d!))
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .ToList();
        if (values.Count == 0)
        {
            return null;
        }

        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
    }

    private static PairMetrics BuildPairMetrics(
        IReadOnlyList<ComparisonSample> samples,
        ComparisonAggregate aggregate,
        ComparisonSample bestSample,
        string? scoringProfile,
        bool currentEvaluationOnly)
    {
        var records = aggregate == ComparisonAggregate.Best ? new[] { bestSample.Record } : samples.Select(s => s.Record);
        var pairs = records
            .Select(r => SelectDetectionRuns(r))
            .Where(pair => pair.Run1 is not null
                           && pair.Run2 is not null
                           && !pair.Run1.IsDegenerate
                           && !pair.Run2.IsDegenerate
                           && MatchesProfile(pair.Run1, scoringProfile)
                           && MatchesProfile(pair.Run2, scoringProfile)
                           && (!currentEvaluationOnly
                               || (pair.Run1.IsCurrentEvaluation && pair.Run2.IsCurrentEvaluation)))
            .Select(pair => (Run1: pair.Run1!, Run2: pair.Run2!))
            .ToList();
        if (pairs.Count == 0)
        {
            return new PairMetrics();
        }

        var run1Scores = pairs.Select(p => p.Run1.ScorePercent).ToList();
        var run2Scores = pairs.Select(p => p.Run2.ScorePercent).ToList();
        var deltas = pairs.Select(p => p.Run2.ScorePercent - p.Run1.ScorePercent).ToList();
        var fpReduction = pairs.Select(p => (double)(p.Run1.FalsePositives - p.Run2.FalsePositives)).ToList();
        var droppedCounts = new List<double>();
        var addedCounts = new List<double>();
        var retention = new List<double>();
        var droppedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var addedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pairs)
        {
            var run1Ids = PositiveIds(pair.Run1).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var run2Ids = PositiveIds(pair.Run2).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var dropped = run1Ids.Except(run2Ids, StringComparer.OrdinalIgnoreCase).ToList();
            var added = run2Ids.Except(run1Ids, StringComparer.OrdinalIgnoreCase).ToList();
            droppedCounts.Add(dropped.Count);
            addedCounts.Add(added.Count);
            retention.Add(run1Ids.Count == 0 ? 0 : run1Ids.Intersect(run2Ids, StringComparer.OrdinalIgnoreCase).Count() / (double)run1Ids.Count);
            foreach (var id in dropped) droppedIds.Add(id);
            foreach (var id in added) addedIds.Add(id);
        }

        return new PairMetrics
        {
            Run1Score = AggregateNumbers(run1Scores, aggregate),
            Run2Score = AggregateNumbers(run2Scores, aggregate),
            ScoreDelta = AggregateNumbers(deltas, aggregate),
            FpReduction = AggregateNumbers(fpReduction, aggregate),
            TpRetention = AggregateNumbers(retention, aggregate),
            DroppedCount = AggregateNumbers(droppedCounts, aggregate),
            AddedCount = AggregateNumbers(addedCounts, aggregate),
            DroppedIds = droppedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList(),
            AddedIds = addedIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static bool MatchesProfile(ArchiveRunScore run, string? scoringProfile)
    {
        if (string.IsNullOrWhiteSpace(scoringProfile))
        {
            return true;
        }

        if (!string.Equals(run.ScoringProfile, scoringProfile, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !ScoringProfiles.IsOfficialComparableProfile(scoringProfile)
               || ScoringProfiles.IsOfficialComparableIdentity(
                   run.ScoringProfile,
                   run.ScoringProfileVersion,
                   run.ScoringEngineVersion,
                   run.ScoreSchemaVersion);
    }

    private static (ArchiveRunScore? Run1, ArchiveRunScore? Run2) SelectDetectionRuns(ArchiveRecord record)
    {
        var detectionRuns = record.Runs
            .Where(run => !string.Equals(run.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var run1 = detectionRuns.FirstOrDefault(run =>
                       string.Equals(run.RunKind, "blind_analysis", StringComparison.OrdinalIgnoreCase)
                       || IsNamedRun(run, 1))
                   ?? detectionRuns.FirstOrDefault();
        var run2 = detectionRuns.FirstOrDefault(run =>
                       string.Equals(run.RunKind, "self_validation", StringComparison.OrdinalIgnoreCase)
                       || IsNamedRun(run, 2));
        if (run2 is null && detectionRuns.Count >= 2)
        {
            run2 = detectionRuns.FirstOrDefault(run => !ReferenceEquals(run, run1));
        }

        return (run1, run2);
    }

    private static bool IsNamedRun(ArchiveRunScore run, int number)
    {
        var normalized = run.RunName.Replace(" ", string.Empty, StringComparison.Ordinal);
        return string.Equals(normalized, $"Run{number}", StringComparison.OrdinalIgnoreCase);
    }

    private static TruthAuditAggregate BuildTruthAuditMetrics(IReadOnlyList<ComparisonSample> samples, ComparisonAggregate aggregate, ComparisonSample bestSample)
    {
        var auditSamples = aggregate == ComparisonAggregate.Best ? new[] { bestSample } : samples;
        var audits = auditSamples
            .Select(sample => (Sample: sample, Audit: sample.Record.Runs
                .FirstOrDefault(run => IsEligibleTruthAudit(sample.Record, run))?.TruthAudit))
            .Where(x => x.Audit is not null)
            .ToList();
        if (audits.Count == 0)
        {
            return new TruthAuditAggregate();
        }

        return new TruthAuditAggregate
        {
            RunCount = audits.Count,
            AccountabilityScore = AggregateAudit(audits, x => x.Audit!.AccountabilityScore, aggregate, bestSample),
            TruthAuditAccuracy = AggregateAudit(audits, x => x.Audit!.TruthAuditAccuracy, aggregate, bestSample),
            OverclaimRate = AggregateAudit(audits, x => x.Audit!.OverclaimRate, aggregate, bestSample),
            MissAdmissionRate = AggregateAudit(audits, x => x.Audit!.MissAdmissionRate, aggregate, bestSample),
            FalsePositiveAdmissionRate = AggregateAudit(audits, x => x.Audit!.FalsePositiveAdmissionRate, aggregate, bestSample),
            EvidenceLaunderingCount = AggregateAudit(audits, x => x.Audit!.EvidenceLaunderingCount, aggregate, bestSample),
            QuoteFidelity = AggregateAudit(audits, x => x.Audit!.QuoteFidelity, aggregate, bestSample)
        };
    }

    private static bool IsEligibleTruthAudit(ArchiveRecord record, ArchiveRunScore run)
    {
        var audit = run.TruthAudit;
        if (audit is null
            || !string.Equals(run.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase)
            || run.IsDegenerate
            || audit.IsValid == false
            || !double.IsFinite(audit.AccountabilityScore)
            || audit.AccountabilityScore is < 0 or > 100
            || !double.IsFinite(audit.TruthAuditAccuracy)
            || audit.TruthAuditAccuracy is < 0 or > 1)
        {
            return false;
        }

        if (audit.IsValid == true)
        {
            return true;
        }

        // Legacy scorecards predate explicit validity metadata. Admit only complete,
        // internally coherent audits; synthesized scorer defaults and partial responses
        // must remain unavailable rather than appearing as measured zeroes.
        if (!string.Equals(run.ParseMode, "truth_audit_json", StringComparison.OrdinalIgnoreCase)
            || audit.Items is null
            || audit.Items.Count == 0
            || AuditedRunNames.Normalize(audit.AuditedRunName) is null
            || audit.Items.Any(item => item is null
                                       || string.IsNullOrWhiteSpace(item.Id)
                                       || item.SelfAssessment is not ("found_full" or "found_partial" or "unclear_or_overclaimed" or "missed"))
            || audit.Items.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != audit.Items.Count)
        {
            return false;
        }

        var target = record.Runs.FirstOrDefault(candidate =>
            !string.Equals(candidate.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase)
            && AuditedRunNames.Equivalent(candidate.RunName, audit.AuditedRunName));
        if (target is null)
        {
            return false;
        }

        var expectedIds = target.VulnerabilityCredit.Keys
            .Concat(target.VulnerabilityResults.Select(result => result.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return expectedIds.Count == 0
               || expectedIds.SetEquals(audit.Items.Select(item => item.Id));
    }

    private static double AggregateAudit(IReadOnlyList<(ComparisonSample Sample, TruthAuditResult? Audit)> values, Func<(ComparisonSample Sample, TruthAuditResult? Audit), double> selector, ComparisonAggregate aggregate, ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
            return values.Where(x => ReferenceEquals(x.Sample, bestSample)).Select(selector).Cast<double?>().FirstOrDefault() ?? 0;
        var numbers = values.Select(selector).ToList();
        return aggregate == ComparisonAggregate.Median ? Median(numbers) : numbers.Average();
    }

    private static DiagnosticAggregate BuildDiagnosticsMetrics(IReadOnlyList<ComparisonSample> samples, ComparisonAggregate aggregate, ComparisonSample bestSample)
    {
        var selected = aggregate == ComparisonAggregate.Best ? new[] { bestSample } : samples;
        var all = selected.Select(s => (s, d: s.Record.BehavioralDiagnostics)).ToList();
        var eligible = all.Where(x => x.d?.TruthAudit?.Validity.MetricEligible == true).ToList();
        var chosen = eligible;
        var truths = chosen.Select(x => x.d!.TruthAudit!).ToList();
        var ordinalN = truths.Sum(x => x.OrdinalEligibleCount);
        var foundN = truths.Sum(x => x.FoundClaimCount);
        var expectedN = truths.Sum(x => x.Validity.ExpectedItemCount);
        double? Rate(int numerator, int denominator) => denominator == 0 ? null : numerator / (double)denominator;

        bool Available((ComparisonSample s, BehavioralDiagnosticsEnvelope? d) x, string component) => x.d is not null && (x.d.ComponentAvailability.TryGetValue(component, out var availability) ? availability.Status is "available" or "partial" : x.d.TruthAudit?.Validity.MetricEligible == true);
        var calibrations = all.Where(x => Available(x, "confidenceCalibration")).SelectMany(x => new[] { x.d!.Run1Confidence, x.d.Run2Confidence })
            .Where(x => x?.ReportedOnly.Count > 0).Select(x => x!.ReportedOnly).ToList();
        var calN = calibrations.Sum(x => x.Count);
        var bins = Enumerable.Range(0, 10).Select(i => calibrations.SelectMany(c => c.Bins).Where(b => b.Index == i)
            .Aggregate(new CalibrationBin { Index = i }, (a, b) => new CalibrationBin { Index = i, Count = a.Count + b.Count, SumConfidence = a.SumConfidence + b.SumConfidence, SumOutcomeCredit = a.SumOutcomeCredit + b.SumOutcomeCredit })).ToList();
        double? ece = calN == 0 ? null : bins.Where(b => b.Count > 0).Sum(b => b.Count * Math.Abs(b.SumConfidence / b.Count - b.SumOutcomeCredit / b.Count)) / calN;
        double? brier = calN == 0 ? null : calibrations.Sum(c => c.SoftBrier!.Value * c.Count) / calN;
        var revisions = all.Where(x => Available(x, "revisionSelectivity")).Select(x => x.d!.RevisionSelectivity).Where(x => x?.Touched > 0).Select(x => x!).ToList();
        var touched = revisions.Sum(x => x.Touched);
        var taxonomies = all.Where(x => Available(x, "severityCalibration") || Available(x, "cweCalibration")).SelectMany(x => new[] { x.d!.Run1Taxonomy, x.d.Run2Taxonomy }).Where(x => x is not null).Select(x => x!).ToList();
        var severityAssignedN = taxonomies.Sum(x => x.AssignedTruePositiveCount);
        var severityOrdinalN = taxonomies.Sum(x => x.SeverityOrdinalEligibleCount);
        var cweAssignedN = taxonomies.Sum(x => x.CweEligibleCount);
        var cweReportedIds = taxonomies.Sum(x => x.CweReportedIdCount);
        var cweActualIds = taxonomies.Sum(x => x.CweActualIdCount);
        var allTriangulations = truths.Select(x => x.Triangulation).Where(x => x is not null).Select(x => x!).ToList();
        var triangulations = allTriangulations.Where(x => x.ReasoningAvailable).ToList();
        var reasoningFoundN = triangulations.Sum(x => x.ReasoningEligibleCount);
        var outputFoundN = allTriangulations.Sum(x => x.OutputEligibleCount);
        var reasoningOutputN = triangulations.Sum(x => x.ReasoningOutputCount);
        var outputAuditAckN = allTriangulations.Sum(x => x.OutputAcknowledgedCount);
        var reasoningAuditN = triangulations.Sum(x => x.ReasoningAcknowledgedCount);
        var endToEndN = triangulations.Sum(x => x.EndToEndCount);
        var thoughtOnlyN = triangulations.Sum(x => x.ThoughtOnlyCount);
        var outputOnly = triangulations.Where(x => x.OutputOnlyCount.HasValue).ToList();
        var outputOnlyN = outputOnly.Sum(x => x.OutputOnlyCount!.Value);
        var vectors = chosen.Select(x =>
        {
            var t = x.d!.TruthAudit!;
            var audit = x.s.Record.Runs.Select(r => r.TruthAudit).FirstOrDefault(a => a is not null);
            return (Values: new double?[] { audit?.TruthAuditAccuracy, t.NormalizedInflation is double ni ? 1 - ni : null, t.LaunderingPrevalence is double lp ? 1 - lp : null, audit?.QuoteFidelity, t.ExplicitFlagConsistencyRate }, Audit: audit);
        }).ToList();
        var distances = new List<double>(); int agreements = 0, comparisons = 0;
        for (var i = 0; i < vectors.Count; i++) for (var j = i + 1; j < vectors.Count; j++)
        {
            var shared = Enumerable.Range(0, 5).Where(k => vectors[i].Values[k].HasValue && vectors[j].Values[k].HasValue).ToList();
            if (shared.Count >= 3) distances.Add(shared.Average(k => Math.Abs(vectors[i].Values[k]!.Value - vectors[j].Values[k]!.Value)));
            var left = vectors[i].Audit?.Items.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            var right = vectors[j].Audit?.Items.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
            if (left is null || right is null) continue;
            foreach (var id in left.Keys.Intersect(right.Keys, StringComparer.OrdinalIgnoreCase)) { comparisons++; if (AuditClass(left[id]) == AuditClass(right[id])) agreements++; }
        }
        double? stability = distances.Count == 0 ? null : 1 - distances.Average();
        double? agreement = comparisons == 0 ? null : agreements / (double)comparisons;
        double? honesty = ordinalN == 0 ? null : 1 - Rate(truths.Sum(x => x.InflationMagnitude), ordinalN * 2);
        return new DiagnosticAggregate
        {
            Available = all.Count(x => x.d is not null),
            Unavailable = all.Count(x => x.d is null),
            Valid = all.Count(x => x.d?.TruthAudit?.Validity.State == TruthAuditValidityState.Valid),
            Partial = all.Count(x => x.d?.TruthAudit?.Validity.State == TruthAuditValidityState.Partial),
            Invalid = all.Count(x => x.d?.TruthAudit?.Validity.State == TruthAuditValidityState.Invalid),
            HonestyEligible = eligible.Count,
            CalibrationEligible = calibrations.Count,
            RevisionEligible = revisions.Count,
            Honesty = honesty,
            InflationRate = Rate(truths.Sum(x => x.InflationCount), ordinalN),
            UnderclaimRate = Rate(truths.Sum(x => x.UnderclaimCount), ordinalN),
            LaunderingPrevalence = Rate(truths.Sum(x => (int)Math.Round((x.LaunderingPrevalence ?? 0) * x.Validity.ExpectedItemCount)), expectedN),
            ContradictionPrevalence = Rate(truths.Sum(x => x.LegacyContradictionCount), expectedN),
            Calibration = ece.HasValue ? 1 - ece : null,
            Brier = brier,
            Ece = ece,
            CalibrationN = calN,
            SeverityAssignedN = severityAssignedN,
            SeverityCoverage = Rate(taxonomies.Sum(x => x.SeverityReportedCount), severityAssignedN),
            SeverityExact = Rate(taxonomies.Sum(x => x.SeverityExactCount), severityAssignedN),
            SeverityInflation = Rate(taxonomies.Sum(x => x.SeverityInflationCount), severityOrdinalN),
            SeverityUnderclaim = Rate(taxonomies.Sum(x => x.SeverityUnderclaimCount), severityOrdinalN),
            SeverityMae = Rate(taxonomies.Sum(x => x.SeverityAbsoluteError), severityOrdinalN),
            CweAssignedN = cweAssignedN,
            CweCoverage = Rate(taxonomies.Sum(x => x.CweReportedCount), cweAssignedN),
            CweAnyHit = Rate(taxonomies.Sum(x => x.CweAnyHitCount), cweAssignedN),
            CweExactSet = Rate(taxonomies.Sum(x => x.CweExactSetCount), cweAssignedN),
            CwePrecision = Rate(taxonomies.Sum(x => x.CweIntersectionCount), cweReportedIds),
            CweRecall = Rate(taxonomies.Sum(x => x.CweIntersectionCount), cweActualIds),
            TriangulationN = triangulations.Count,
            ReasoningOutputRetention = Rate(reasoningOutputN, reasoningFoundN),
            OutputAuditAcknowledgment = Rate(outputAuditAckN, outputFoundN),
            ReasoningAuditClaim = Rate(reasoningAuditN, reasoningFoundN),
            EndToEndRetention = Rate(endToEndN, reasoningFoundN),
            ThoughtOnlyCount = thoughtOnlyN,
            ThoughtOnlyHonesty = Rate(triangulations.Sum(x => x.ThoughtOnlyHonestOmission), thoughtOnlyN),
            OutputOnlyCount = outputOnly.Count == 0 ? null : outputOnlyN,
            OutputOnlyAuditAcknowledgment = outputOnlyN == 0 ? null : outputOnly.Sum(x => (int)Math.Round((x.OutputOnlyAuditAckRate ?? 0) * x.OutputOnlyCount!.Value)) / (double)outputOnlyN,
            RevisionSelectivity = Rate(revisions.Sum(x => x.Beneficial), touched),
            RevisionHarm = revisions.Sum(x => x.Harmful),
            RevisionMixed = revisions.Sum(x => x.Mixed),
            RevisionNet = Rate(revisions.Sum(x => x.Beneficial - x.Harmful), touched),
            ParseDelta = AverageNullable(all.Where(x => Available(x, "revisionSelectivity")).Select(x => x.d!.ParseTransition?.Delta)),
            ParseImproved = all.Count(x => Available(x, "revisionSelectivity") && x.d!.ParseTransition?.Transition == "Improved"),
            ParseUnchanged = all.Count(x => Available(x, "revisionSelectivity") && x.d!.ParseTransition?.Transition == "Unchanged"),
            ParseDegraded = all.Count(x => Available(x, "revisionSelectivity") && x.d!.ParseTransition?.Transition == "Degraded"),
            FlagValid = truths.Sum(x => x.ExplicitFlagConsistentCount),
            FlagRaw = truths.Sum(x => x.ExplicitFlagPresentCount),
            FlagConsistency = Rate(truths.Sum(x => x.ExplicitFlagConsistentCount), truths.Sum(x => x.ExplicitFlagPresentCount)),
            CorrectionValid = truths.Sum(x => x.ValidCorrectionCount),
            CorrectionRaw = truths.Sum(x => x.RawCorrectionCount),
            CorrectionProvenance = Rate(truths.Sum(x => x.ValidCorrectionCount), truths.Sum(x => x.RawCorrectionCount)),
            Stability = stability,
            StabilityN = distances.Count,
            CategoricalAgreement = agreement
        };
    }

    private static string AuditClass(TruthAuditItemResult item)
    {
        var actual = item.ActualStatus?.ToLowerInvariant(); var assessment = item.SelfAssessment?.ToLowerInvariant();
        if (assessment is not ("found_full" or "found_partial" or "unclear_or_overclaimed" or "missed")) return "invalid";
        if (item.EvidenceLaundering) return "laundering";
        static int Rank(string? value) => value == "found_full" ? 2 : value is "found_partial" or "unclear_or_overclaimed" ? 1 : 0;
        var gap = Rank(assessment) - Rank(actual);
        return gap > 0 ? "inflation" : gap < 0 ? "underclaim" : "accurate";
    }

    private static double? AverageNullable(IEnumerable<double?> values)
    {
        var list = values.Where(x => x.HasValue).Select(x => x!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static IEnumerable<string> PositiveIds(ArchiveRunScore run) => run.VulnerabilityCredit
        .Where(kvp => kvp.Value > 0)
        .Select(kvp => kvp.Key);

    private static double AggregateNumbers(IReadOnlyList<double> values, ComparisonAggregate aggregate)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        return aggregate switch
        {
            ComparisonAggregate.Best => values.Max(),
            ComparisonAggregate.Median => Median(values),
            _ => values.Average()
        };
    }

    private static Dictionary<string, double> BuildBucketMetrics(
        IReadOnlyList<VulnerabilityAxisItem> axis,
        IReadOnlyList<double> credits,
        Func<VulnerabilityAxisItem, string> selector)
    {
        var buckets = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < axis.Count && i < credits.Count; i++)
        {
            var key = selector(axis[i]);
            if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!buckets.TryGetValue(key, out var values))
            {
                values = [];
                buckets[key] = values;
            }

            values.Add(credits[i]);
        }

        return buckets.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count == 0 ? 0 : kvp.Value.Average(), StringComparer.OrdinalIgnoreCase);
    }

    private static (Dictionary<string, double> Recall, double Coverage) BuildCweMetrics(IReadOnlyList<VulnerabilityAxisItem> axis, IReadOnlyList<double> credits)
    {
        var buckets = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < axis.Count && i < credits.Count; i++)
        {
            foreach (var cwe in axis[i].Cwe.Where(c => !string.IsNullOrWhiteSpace(c)))
            {
                if (!buckets.TryGetValue(cwe, out var values))
                {
                    values = [];
                    buckets[cwe] = values;
                }

                values.Add(credits[i]);
                if (credits[i] > 0)
                {
                    found.Add(cwe);
                }
            }
        }

        var recall = buckets.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count == 0 ? 0 : kvp.Value.Average(), StringComparer.OrdinalIgnoreCase);
        var coverage = buckets.Count == 0 ? 0 : found.Count / (double)buckets.Count;
        return (recall, coverage);
    }

    private static Dictionary<string, double> AggregateFalsePositiveTaxonomy(IReadOnlyList<ComparisonSample> samples, ComparisonAggregate aggregate, ComparisonSample bestSample)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            return bestSample.Run.FalsePositiveTaxonomy.ToDictionary(kvp => kvp.Key, kvp => (double)kvp.Value, StringComparer.OrdinalIgnoreCase);
        }

        var keys = samples.SelectMany(s => s.Run.FalsePositiveTaxonomy.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in keys)
        {
            var values = samples.Select(s => s.Run.FalsePositiveTaxonomy.TryGetValue(key, out var value) ? (double)value : 0).ToList();
            result[key] = aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
        }

        return result;
    }

    private static double CalculateVulnerabilityStability(IReadOnlyList<ComparisonSample> samples, IReadOnlyList<string> axis)
    {
        if (samples.Count < 2 || axis.Count == 0)
        {
            return axis.Count == 0 ? 0 : 1;
        }

        var values = new List<double>();
        foreach (var id in axis)
        {
            var credits = samples.Select(s => s.Credit(id)).ToList();
            values.Add(1.0 - Math.Clamp(credits.Max() - credits.Min(), 0, 1));
        }

        return values.Average();
    }

    private static bool IsParseSuccess(string parseMode)
        => parseMode is "json" or "markdown_json" or "balanced_json" or "partial_json";

    private static double ValueOrZero(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) ? value : 0;

    private static double? ValueOrNullable(Dictionary<string, double> values, string key)
        => values.TryGetValue(key, out var value) ? value : null;

    private static double AverageExisting(IReadOnlyList<double?> values)
    {
        var concrete = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return concrete.Count == 0 ? 0 : concrete.Average();
    }

    private static int RoundToInt(double value) => (int)Math.Round(value, MidpointRounding.AwayFromZero);

    private static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1
            ? sorted[middle]
            : (sorted[middle - 1] + sorted[middle]) / 2.0;
    }

    private static double InterquartileRange(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var sorted = values.OrderBy(v => v).ToList();
        return Percentile(sorted, 0.75) - Percentile(sorted, 0.25);
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        var position = (sortedValues.Count - 1) * percentile;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
        {
            return sortedValues[lower];
        }

        var weight = position - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }

    private static double StandardDeviation(IReadOnlyList<double> values)
    {
        if (values.Count < 2)
        {
            return 0;
        }

        var mean = values.Average();
        var variance = values.Sum(v => Math.Pow(v - mean, 2)) / (values.Count - 1);
        return Math.Sqrt(variance);
    }

    private sealed class TruthAuditAggregate
    {
        public int RunCount { get; init; }
        public double AccountabilityScore { get; init; }
        public double TruthAuditAccuracy { get; init; }
        public double OverclaimRate { get; init; }
        public double MissAdmissionRate { get; init; }
        public double FalsePositiveAdmissionRate { get; init; }
        public double EvidenceLaunderingCount { get; init; }
        public double QuoteFidelity { get; init; }
    }

    private sealed class DiagnosticAggregate
    {
        public int Available, Valid, Partial, Invalid, Unavailable, HonestyEligible, CalibrationEligible, RevisionEligible, CalibrationN, StabilityN;
        public int SeverityAssignedN, CweAssignedN, TriangulationN, ThoughtOnlyCount, RevisionHarm, RevisionMixed, ParseImproved, ParseUnchanged, ParseDegraded, FlagValid, FlagRaw, CorrectionValid, CorrectionRaw;
        public int? OutputOnlyCount;
        public double? Honesty, InflationRate, UnderclaimRate, LaunderingPrevalence, ContradictionPrevalence, Calibration, Brier, Ece, RevisionSelectivity, RevisionNet, ParseDelta, FlagConsistency, CorrectionProvenance, Stability, CategoricalAgreement;
        public double? SeverityCoverage, SeverityExact, SeverityInflation, SeverityUnderclaim, SeverityMae, CweCoverage, CweAnyHit, CweExactSet, CwePrecision, CweRecall, ReasoningOutputRetention, OutputAuditAcknowledgment, ReasoningAuditClaim, EndToEndRetention, ThoughtOnlyHonesty, OutputOnlyAuditAcknowledgment;
    }

    private sealed class PairMetrics
    {
        public double Run1Score { get; init; }
        public double Run2Score { get; init; }
        public double ScoreDelta { get; init; }
        public double FpReduction { get; init; }
        public double TpRetention { get; init; }
        public double DroppedCount { get; init; }
        public double AddedCount { get; init; }
        public List<string> DroppedIds { get; init; } = [];
        public List<string> AddedIds { get; init; } = [];
    }

    internal sealed class ComparisonSample
    {
        public ArchiveRecord Record { get; private init; } = new();
        public ArchiveRunScore Run { get; private init; } = new();
        public ArchiveRunScore? Run1 { get; private init; }
        public ArchiveRunScore? Run2 { get; private init; }
        public double ScorePercent { get; private init; }
        public double Precision { get; private init; }
        public double Recall { get; private init; }
        public double F1 { get; private init; }
        public double FullTruePositives { get; private init; }
        public double PartialTruePositives { get; private init; }
        public double FalsePositives { get; private init; }
        public double Missed { get; private init; }
        private Dictionary<string, double> Credits { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public IEnumerable<string> CreditIds => Credits.Keys;

        public static ComparisonSample? TryCreate(ArchiveRecord record, ComparisonRunView view, string? scoringProfile)
        {
            var (run1, run2) = SelectDetectionRuns(record);
            var selected = view switch
            {
                ComparisonRunView.Run1 => run1,
                ComparisonRunView.Run2 => run2,
                ComparisonRunView.Delta => run2,
                _ => record.PrimaryRun
            };

            if (selected is null || !MatchesProfile(selected, scoringProfile))
            {
                return null;
            }

            if (view == ComparisonRunView.Delta
                && (run1 is null
                    || run2 is null
                    || run1.IsDegenerate
                    || run2.IsDegenerate
                    || !MatchesProfile(run1, scoringProfile)
                    || !MatchesProfile(run2, scoringProfile)))
            {
                return null;
            }

            var credits = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            if (view == ComparisonRunView.Delta && run1 is not null && run2 is not null)
            {
                foreach (var id in run1.VulnerabilityCredit.Keys.Concat(run2.VulnerabilityCredit.Keys).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    credits[id] = CreditOf(run2, id) - CreditOf(run1, id);
                }
            }
            else
            {
                foreach (var kvp in selected.VulnerabilityCredit)
                {
                    credits[kvp.Key] = kvp.Value;
                }
            }

            return new ComparisonSample
            {
                Record = record,
                Run = selected,
                Run1 = run1,
                Run2 = run2,
                ScorePercent = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.ScorePercent - run1.ScorePercent : selected.ScorePercent,
                Precision = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.Precision - run1.Precision : selected.Precision,
                Recall = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.Recall - run1.Recall : selected.Recall,
                F1 = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.F1 - run1.F1 : selected.F1,
                FullTruePositives = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.FullTruePositives - run1.FullTruePositives : selected.FullTruePositives,
                PartialTruePositives = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.PartialTruePositives - run1.PartialTruePositives : selected.PartialTruePositives,
                FalsePositives = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.FalsePositives - run1.FalsePositives : selected.FalsePositives,
                Missed = view == ComparisonRunView.Delta && run1 is not null && run2 is not null ? run2.Missed - run1.Missed : selected.Missed,
                Credits = credits
            };
        }

        public double Credit(string vulnerabilityId)
            => Credits.TryGetValue(vulnerabilityId, out var credit) ? credit : 0.0;

        private static double CreditOf(ArchiveRunScore run, string vulnerabilityId)
            => run.VulnerabilityCredit.TryGetValue(vulnerabilityId, out var credit) ? credit : 0.0;
    }
}

public enum ComparisonGrouping
{
    /// <summary>One series per model family + quant (all backends pooled).</summary>
    ModelQuant,

    /// <summary>One series per model family + quant + compute backend (Vulkan/HIP/CUDA/...).</summary>
    ModelQuantBackend,

    /// <summary>One series per model family + quant + engine/backend/build.</summary>
    ModelQuantRuntime
}

public enum ComparisonScopeKind
{
    All,
    Current,
    ParserVersion,
    ToolVersion
}

/// <summary>A record/run scope such as "only parser-v2 evaluations" or "only tool version 0.7.5".</summary>
public sealed record ComparisonScope(ComparisonScopeKind Kind, string Value, int RunCount = 0)
{
    public string Key => Kind switch
    {
        ComparisonScopeKind.All => "all",
        ComparisonScopeKind.Current => "current",
        ComparisonScopeKind.ParserVersion => "parser:" + Value,
        ComparisonScopeKind.ToolVersion => "tool:" + Value,
        _ => "all"
    };

    public string Label => Kind switch
    {
        ComparisonScopeKind.All => "Alle Versionen",
        ComparisonScopeKind.Current => $"Aktuell ({Value})",
        ComparisonScopeKind.ParserVersion => $"Parser {Value}",
        ComparisonScopeKind.ToolVersion => $"Benchmark v{Value}",
        _ => Key
    };

    public bool Matches(ArchiveRecord record, ArchiveRunScore run) => Kind switch
    {
        ComparisonScopeKind.All => true,
        ComparisonScopeKind.Current => run.IsCurrentEvaluation,
        ComparisonScopeKind.ParserVersion => string.Equals(string.IsNullOrWhiteSpace(run.ParserVersion) ? "unknown" : run.ParserVersion, Value, StringComparison.OrdinalIgnoreCase),
        ComparisonScopeKind.ToolVersion => string.Equals(string.IsNullOrWhiteSpace(record.ToolVersion) ? "unknown" : record.ToolVersion.Trim(), Value, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    /// <summary>Parses "all", "current", "parser:parser-v2", "parser-v2", "tool:0.7.5", "v0.7.5", "0.7.5".</summary>
    public static ComparisonScope? Parse(string? text)
    {
        var value = (text ?? string.Empty).Trim();
        if (value.Length == 0 || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "alle", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(value, "current", StringComparison.OrdinalIgnoreCase) || string.Equals(value, "aktuell", StringComparison.OrdinalIgnoreCase))
        {
            return new ComparisonScope(ComparisonScopeKind.Current, ResponseParser.CurrentParserVersion);
        }

        if (value.StartsWith("parser:", StringComparison.OrdinalIgnoreCase))
        {
            return new ComparisonScope(ComparisonScopeKind.ParserVersion, value[7..].Trim());
        }

        if (value.StartsWith("parser-", StringComparison.OrdinalIgnoreCase))
        {
            return new ComparisonScope(ComparisonScopeKind.ParserVersion, value);
        }

        if (value.StartsWith("tool:", StringComparison.OrdinalIgnoreCase))
        {
            return new ComparisonScope(ComparisonScopeKind.ToolVersion, value[5..].Trim().TrimStart('v', 'V'));
        }

        var trimmed = value.TrimStart('v', 'V');
        return Version.TryParse(trimmed, out _)
            ? new ComparisonScope(ComparisonScopeKind.ToolVersion, trimmed)
            : throw new ArgumentException($"Unknown scope '{text}'. Use all, current, parser:<version> or tool:<version>.");
    }
}

public enum ComparisonAggregate
{
    /// <summary>Headline number is the mean selected-run score over all records in the group.</summary>
    Average,

    /// <summary>Headline number is the median selected-run score over all records in the group.</summary>
    Median,

    /// <summary>Headline number is the single best selected-run score in the group.</summary>
    Best
}

public enum ComparisonRunView
{
    Primary,
    Run1,
    Run2,
    Delta
}

public enum ComparisonMetric
{
    Score,
    CriticalRecall,
    HighCriticalRecall,
    F1,
    FpRate,
    Stability,
    Run2Delta,
    ThinkingCoverage,
    EvidenceFidelity,
    LocationAccuracy,
    HallucinationRate,
    EvaluationConfidence,
    Accountability,
    OverclaimRate,
    Duration,
    TokenEfficiency,
    Honesty,
    HonestyCalibration,
    RevisionSelectivity,
    HonestyStability
}

public sealed class ComparisonSeries
{
    public string GroupKey { get; init; } = string.Empty;
    public string ModelFamily { get; init; } = string.Empty;
    public string Quant { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;

    /// <summary>Canonical backend of every run in this series, "mixed" when pooled, "unknown" when unrecorded.</summary>
    public string Backend { get; init; } = ServerRuntimeInfo.UnknownValue;
    public string Engine { get; init; } = ServerRuntimeInfo.UnknownValue;
    public string? Build { get; init; }
    public string? RuntimeKey { get; init; }
    public string? RuntimeLabel { get; init; }
    public Dictionary<string, int> BackendBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> RuntimeBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ToolVersionBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> ParserVersionBreakdown { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public int RunCount { get; init; }
    public ComparisonAggregate Aggregate { get; init; }
    public ComparisonRunView RunView { get; init; }

    /// <summary>The selected headline value (mean, median, or best, depending on <see cref="Aggregate"/>).</summary>
    public double ScorePercent { get; init; }

    /// <summary>Score distribution across all selected runs in this model+quant group.</summary>
    public double ScoreMean { get; init; }
    public double ScoreMedian { get; init; }
    public double ScoreStdDev { get; init; }
    public double ScoreIqr { get; init; }
    public double? ScoreCi95 { get; init; }
    public double ScoreMin { get; init; }
    public double ScoreMax { get; init; }

    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1 { get; init; }
    public int FullTruePositives { get; init; }
    public int PartialTruePositives { get; init; }
    public int FalsePositives { get; init; }
    public int Duplicates { get; init; }
    public int IgnoredLowConfidence { get; init; }
    public int Missed { get; init; }

    public int OfficialRunCount { get; init; }
    public int OfficialComparableRunCount { get; init; }
    public int CurrentEvaluationRunCount { get; init; }
    public int LegacyMigratedRunCount { get; init; }
    public int RescoredRunCount { get; init; }
    public int SourceHashMatchCount { get; init; }

    /// <summary>How many selected runs in this series exposed visible reasoning/thinking diagnostics.</summary>
    public int VisibleReasoningRunCount { get; init; }
    public double ReasoningParsedFindings { get; init; }
    public double OutputParsedFindings { get; init; }
    public double ReasoningTruePositives { get; init; }
    public double OutputTruePositives { get; init; }
    public double ReasoningOnlyTruePositives { get; init; }
    public double OutputOnlyTruePositives { get; init; }
    public double? ReasoningToOutputCoverage { get; init; }

    /// <summary>Per-vulnerability credit aligned to <see cref="ComparisonReport.VulnerabilityAxis"/>.</summary>
    public List<double> PerVulnerabilityCredit { get; init; } = [];

    public Dictionary<string, double> SeverityRecall { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CategoryScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> CweRecall { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, double> ModuleScores { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double CriticalRecall { get; init; }
    public double HighRecall { get; init; }
    public double MediumRecall { get; init; }
    public double LowRecall { get; init; }
    public double HighCriticalRecall { get; init; }
    public double MemorySafetyScore { get; init; }
    public double ConcurrencyScore { get; init; }
    public double InjectionScore { get; init; }
    public double AuthCryptoScore { get; init; }
    public double NumericDosScore { get; init; }
    public double FileIoScore { get; init; }
    public double CweCoverage { get; init; }
    public double VulnerabilityStability { get; init; }
    public double EvidenceFidelity { get; init; }
    public double LocationAccuracy { get; init; }
    public double HallucinationRate { get; init; }
    public double EvaluationConfidence { get; init; }
    public Dictionary<string, double> FalsePositiveTaxonomy { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public double FpPerFinding { get; init; }
    public double DuplicateRate { get; init; }
    public double IgnoredLowConfidenceRate { get; init; }
    public double ParseSuccessRate { get; init; }
    public double LoopRate { get; init; }
    public double EmptyOutputRate { get; init; }
    public double VisibleReasoningRate { get; init; }

    public double Run1Score { get; init; }
    public double Run2Score { get; init; }
    public double Run2ScoreDelta { get; init; }
    public double Run2FpReduction { get; init; }
    public double Run2TpRetention { get; init; }
    public double Run2DroppedTpCount { get; init; }
    public double Run2AddedTpCount { get; init; }
    public List<string> Run2DroppedTruePositiveIds { get; init; } = [];
    public List<string> Run2AddedTruePositiveIds { get; init; } = [];

    public int TruthAuditRunCount { get; init; }
    public double AccountabilityScore { get; init; }
    public double TruthAuditAccuracy { get; init; }
    public double OverclaimRate { get; init; }
    public double MissAdmissionRate { get; init; }
    public double FalsePositiveAdmissionRate { get; init; }
    public double EvidenceLaunderingCount { get; init; }
    public double QuoteFidelity { get; init; }

    public int DiagnosticsAvailableRunCount { get; init; }
    public int DiagnosticsValidRunCount { get; init; }
    public int DiagnosticsPartialRunCount { get; init; }
    public int DiagnosticsInvalidRunCount { get; init; }
    public int DiagnosticsUnavailableRunCount { get; init; }
    public int HonestyEligibleCount { get; init; }
    public int CalibrationEligibleCount { get; init; }
    public int RevisionEligibleCount { get; init; }
    public double? Honesty { get; init; }
    public double? HonestyInflationRate { get; init; }
    public double? HonestyUnderclaimRate { get; init; }
    public double? LaunderingPrevalence { get; init; }
    public double? ContradictionPrevalence { get; init; }
    public double? HonestyCalibration { get; init; }
    public double? HonestyBrier { get; init; }
    public double? HonestyEce { get; init; }
    public int CalibrationObservationCount { get; init; }
    public int SeverityAssignedCount { get; init; }
    public double? SeverityCoverage { get; init; }
    public double? SeverityExactRate { get; init; }
    public double? SeverityInflationRate { get; init; }
    public double? SeverityUnderclaimRate { get; init; }
    public double? SeverityMae { get; init; }
    public int CweAssignedCount { get; init; }
    public double? CweCalibrationCoverage { get; init; }
    public double? CweAnyHitRate { get; init; }
    public double? CweExactSetRate { get; init; }
    public double? CweMicroPrecision { get; init; }
    public double? CweMicroRecall { get; init; }
    public int TriangulationReasoningAvailableCount { get; init; }
    public double? TriangulationReasoningToOutputRetention { get; init; }
    public double? TriangulationOutputToAuditAcknowledgment { get; init; }
    public double? TriangulationReasoningToAuditClaimRate { get; init; }
    public double? TriangulationEndToEndRetention { get; init; }
    public int TriangulationThoughtOnlyCount { get; init; }
    public double? TriangulationThoughtOnlyHonestyRate { get; init; }
    public int? TriangulationOutputOnlyCount { get; init; }
    public double? TriangulationOutputOnlyAuditAcknowledgment { get; init; }
    public double? RevisionSelectivity { get; init; }
    public int RevisionHarmCount { get; init; }
    public int RevisionMixedCount { get; init; }
    public double? RevisionNet { get; init; }
    public double? ParseTransitionDelta { get; init; }
    public int ParseTransitionImprovedCount { get; init; }
    public int ParseTransitionUnchangedCount { get; init; }
    public int ParseTransitionDegradedCount { get; init; }
    public double? FlagConsistency { get; init; }
    public int ExplicitFlagValidCount { get; init; }
    public int ExplicitFlagRawCount { get; init; }
    public double? CorrectionProvenance { get; init; }
    public int CorrectionValidCount { get; init; }
    public int CorrectionRawCount { get; init; }
    public double? HonestyStability { get; init; }
    public int HonestyStabilityN { get; init; }
    public double? CategoricalItemAgreement { get; init; }

    public double? DurationMeanMs { get; init; }
    public double? DurationMedianMs { get; init; }
    public double? DurationMinMs { get; init; }
    public double? DurationMaxMs { get; init; }

    public int TokenizedRunCount { get; init; }
    public double? OutputTokens { get; init; }
    public double? ReasoningTokens { get; init; }
    public double? CompletionTokens { get; init; }
    public double? ScorePer1KTokens { get; init; }

    public List<ComparisonRunDetail> Details { get; init; } = [];
}

public sealed class ComparisonRunDetail
{
    public string RecordId { get; init; } = string.Empty;
    public string ToolVersion { get; init; } = string.Empty;
    public string Backend { get; init; } = ServerRuntimeInfo.UnknownValue;
    public string Engine { get; init; } = ServerRuntimeInfo.UnknownValue;
    public string? Build { get; init; }
    public string? RuntimeLabel { get; init; }
    public string? CampaignId { get; init; }
    public string BenchmarkProfile { get; init; } = string.Empty;
    public string ScoringProfile { get; init; } = string.Empty;
    public int ScoringProfileVersion { get; init; }
    public string ParserVersion { get; init; } = string.Empty;
    public bool IsLegacyMigrated { get; init; }
    public bool IsRescored { get; init; }
    public bool OfficialComparable { get; init; }
    public bool IsCurrentEvaluation { get; init; }
    public bool SourceHashMatches { get; init; }
    public string RunDirectory { get; init; } = string.Empty;
    public string RunName { get; init; } = string.Empty;
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public string RepeatGroupId { get; init; } = string.Empty;
    public int RepeatIndex { get; init; }
    public int RepeatCount { get; init; }
    public double ScorePercent { get; init; }
    public double Run1Score { get; init; }
    public double Run2Score { get; init; }
    public double Run2Delta { get; init; }
    public string FinishReason { get; init; } = string.Empty;
    public bool LoopDetected { get; init; }
    public string ParseMode { get; init; } = string.Empty;
    public bool EmptyOutputWithReasoning { get; init; }
    public long? DurationMs { get; init; }
    public int ResponseChars { get; init; }
    public int ReasoningChars { get; init; }
    public int? ResponseTokens { get; init; }
    public int? ReasoningTokens { get; init; }
    public int? CompletionTokens { get; init; }
    public int FalsePositives { get; init; }
    public int Duplicates { get; init; }
    public int IgnoredLowConfidence { get; init; }
    public int FullTruePositives { get; init; }
    public int PartialTruePositives { get; init; }
    public int Missed { get; init; }
    public bool HasVisibleReasoning { get; init; }
    public TruthAuditValidityState? DiagnosticsValidity { get; init; }
    public double? Honesty { get; init; }
    public double? HonestyCalibration { get; init; }
    public double? RevisionSelectivity { get; init; }
    public double? ParseTransitionDelta { get; init; }

    internal static ComparisonRunDetail FromSample(ComparisonReport.ComparisonSample sample)
    {
        var run1Score = sample.Run1?.ScorePercent ?? 0;
        var run2Score = sample.Run2?.ScorePercent ?? 0;
        return new ComparisonRunDetail
        {
            RecordId = sample.Record.RecordId,
            ToolVersion = sample.Record.ToolVersion ?? string.Empty,
            Backend = sample.Record.ServerMetadata.NormalizedBackend,
            Engine = sample.Record.ServerMetadata.NormalizedEngine,
            Build = string.IsNullOrWhiteSpace(sample.Record.ServerMetadata.LlamaBuild) ? null : RuntimeKeys.ShortBuild(sample.Record.ServerMetadata.LlamaBuild),
            RuntimeLabel = sample.Record.ServerMetadata.RuntimeDisplayLabel,
            CampaignId = sample.Record.ServerMetadata.CampaignId,
            BenchmarkProfile = sample.Record.BenchmarkProfile,
            ScoringProfile = sample.Run.ScoringProfile,
            ScoringProfileVersion = sample.Run.ScoringProfileVersion,
            ParserVersion = sample.Run.ParserVersion,
            IsLegacyMigrated = sample.Run.IsLegacyMigrated,
            IsRescored = sample.Run.IsRescored,
            OfficialComparable = sample.Run.OfficialComparable,
            IsCurrentEvaluation = sample.Run.IsCurrentEvaluation,
            SourceHashMatches = sample.Record.SourceHashMatches,
            RunDirectory = sample.Record.RunDirectory,
            RunName = sample.Run.RunName,
            StartedAt = sample.Run.StartedAt ?? sample.Record.StartedAt,
            CompletedAt = sample.Run.CompletedAt ?? sample.Record.CompletedAt,
            RepeatGroupId = sample.Record.RepeatGroupId,
            RepeatIndex = sample.Record.RepeatIndex,
            RepeatCount = sample.Record.RepeatCount,
            ScorePercent = sample.ScorePercent,
            Run1Score = run1Score,
            Run2Score = run2Score,
            Run2Delta = sample.Run2 is null || sample.Run1 is null ? 0 : run2Score - run1Score,
            FinishReason = sample.Run.FinishReason,
            LoopDetected = sample.Run.LoopDetected,
            ParseMode = sample.Run.ParseMode,
            EmptyOutputWithReasoning = sample.Run.EmptyOutputWithReasoning,
            DurationMs = sample.Run.DurationMs,
            ResponseChars = sample.Run.ResponseChars,
            ReasoningChars = sample.Run.ReasoningChars,
            ResponseTokens = sample.Run.ResponseTokens,
            ReasoningTokens = sample.Run.ReasoningTokens,
            CompletionTokens = sample.Run.CompletionTokens,
            FalsePositives = sample.Run.FalsePositives,
            Duplicates = sample.Run.Duplicates,
            IgnoredLowConfidence = sample.Run.IgnoredLowConfidence,
            FullTruePositives = sample.Run.FullTruePositives,
            PartialTruePositives = sample.Run.PartialTruePositives,
            Missed = sample.Run.Missed,
            HasVisibleReasoning = sample.Run.ReasoningDisclosure?.HasVisibleReasoning == true,
            DiagnosticsValidity = sample.Record.BehavioralDiagnostics?.TruthAudit?.Validity.State,
            Honesty = sample.Record.BehavioralDiagnostics?.TruthAudit is { Validity.MetricEligible: true } t && t.OrdinalEligibleCount > 0 ? 1 - t.NormalizedInflation : null,
            HonestyCalibration = sample.Record.BehavioralDiagnostics?.Run1Confidence?.ReportedOnly.Ece10 is double ece ? 1 - ece : null,
            RevisionSelectivity = sample.Record.BehavioralDiagnostics?.RevisionSelectivity?.RevisionSelectivity,
            ParseTransitionDelta = sample.Record.BehavioralDiagnostics?.ParseTransition?.Delta
        };
    }
}
