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

    /// <summary>Shared vulnerability id axis. Kept for older consumers and CSV headers.</summary>
    public List<string> VulnerabilityAxis { get; init; } = [];

    /// <summary>Shared vulnerability axis enriched with local-only metadata where available.</summary>
    public List<VulnerabilityAxisItem> VulnerabilityMetadata { get; init; } = [];

    public List<ComparisonSeries> Series { get; init; } = [];

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
        string? scoringProfile = null)
    {
        metadataIndex ??= VulnerabilityMetadataIndex.Empty;
        var selected = string.IsNullOrWhiteSpace(familyFilter)
            ? groups
            : groups.Where(g => string.Equals(g.ModelFamily, familyFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        var selectedWithSamples = selected
            .Select(group => new
            {
                Group = group,
                Samples = group.Records
                    .Select(record => ComparisonSample.TryCreate(record, runView, scoringProfile))
                    .Where(sample => sample is not null)
                    .Select(sample => sample!)
                    .ToList()
            })
            .Where(item => item.Samples.Count > 0)
            .ToList();

        var axis = selectedWithSamples
            .SelectMany(item => item.Samples)
            .SelectMany(sample => sample.CreditIds)
            .Concat(metadataIndex.Items.Select(i => i.Id))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var axisMetadata = axis.Select(metadataIndex.GetOrCreate).ToList();
        var series = new List<ComparisonSeries>();
        foreach (var item in selectedWithSamples)
        {
            var group = item.Group;
            var samples = item.Samples;

            var bestSample = samples.OrderByDescending(s => s.ScorePercent).First();
            var scoreValues = samples.Select(s => s.ScorePercent).ToList();
            var score = AggregateMetric(samples, s => s.ScorePercent, aggregate, bestSample);
            var perVuln = axis.Select(id => AggregateCredit(samples, id, aggregate, bestSample)).ToList();
            var visibleReasoningRunCount = samples.Count(s => s.Run.ReasoningDisclosure?.HasVisibleReasoning == true);
            var pairMetrics = BuildPairMetrics(group.Records, aggregate, scoringProfile);
            var truthAudit = BuildTruthAuditMetrics(group.Records, aggregate);
            var severity = BuildBucketMetrics(axisMetadata, perVuln, item => item.Severity);
            var categories = BuildBucketMetrics(axisMetadata, perVuln, item => item.Category);
            var cwe = BuildCweMetrics(axisMetadata, perVuln);
            var modules = BuildBucketMetrics(axisMetadata, perVuln, item => item.Module);
            var durationValues = samples.Select(s => s.Run.DurationMs).Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
            var findingTotal = samples.Sum(s => Math.Max(0, s.Run.FindingCount));
            var duplicateTotal = samples.Sum(s => Math.Max(0, s.Run.Duplicates));
            var ignoredTotal = samples.Sum(s => Math.Max(0, s.Run.IgnoredLowConfidence));
            var fpTotal = samples.Sum(s => Math.Max(0, s.Run.FalsePositives));

            series.Add(new ComparisonSeries
            {
                GroupKey = group.GroupKey,
                ModelFamily = group.ModelFamily,
                Quant = group.Quant,
                Label = $"{group.ModelFamily} · {group.Quant}",
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
                ParseSuccessRate = samples.Count == 0 ? 0 : samples.Count(s => IsParseSuccess(s.Run.ParseMode)) / (double)samples.Count,
                LoopRate = samples.Count == 0 ? 0 : samples.Count(s => s.Run.LoopDetected) / (double)samples.Count,
                EmptyOutputRate = samples.Count == 0 ? 0 : samples.Count(s => s.Run.EmptyOutputWithReasoning) / (double)samples.Count,
                VisibleReasoningRate = samples.Count == 0 ? 0 : visibleReasoningRunCount / (double)samples.Count,
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
                DurationMeanMs = durationValues.Count == 0 ? null : durationValues.Average(),
                DurationMedianMs = durationValues.Count == 0 ? null : Median(durationValues),
                DurationMinMs = durationValues.Count == 0 ? null : durationValues.Min(),
                DurationMaxMs = durationValues.Count == 0 ? null : durationValues.Max(),
                Details = samples.Select(s => ComparisonRunDetail.FromSample(s)).OrderByDescending(d => d.CompletedAt).ToList()
            });
        }

        return new ComparisonReport
        {
            BenchmarkId = benchmarkId,
            Aggregate = aggregate,
            RunView = runView,
            Metric = metric,
            ScoringProfile = scoringProfile,
            VulnerabilityAxis = axis,
            VulnerabilityMetadata = axisMetadata,
            Series = series
                .OrderByDescending(s => SortMetricValue(s, metric))
                .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()
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

    private static PairMetrics BuildPairMetrics(IReadOnlyList<ArchiveRecord> records, ComparisonAggregate aggregate, string? scoringProfile)
    {
        var pairs = records
            .Select(r => SelectDetectionRuns(r))
            .Where(pair => pair.Run1 is not null
                           && pair.Run2 is not null
                           && !pair.Run1.IsDegenerate
                           && !pair.Run2.IsDegenerate
                           && MatchesProfile(pair.Run1, scoringProfile)
                           && MatchesProfile(pair.Run2, scoringProfile))
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
        => string.IsNullOrWhiteSpace(scoringProfile)
           || string.Equals(run.ScoringProfile, scoringProfile, StringComparison.OrdinalIgnoreCase);

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

    private static TruthAuditAggregate BuildTruthAuditMetrics(IReadOnlyList<ArchiveRecord> records, ComparisonAggregate aggregate)
    {
        var audits = records
            .SelectMany(record => record.Runs)
            .Select(run => run.TruthAudit)
            .Where(audit => audit is not null)
            .Select(audit => audit!)
            .ToList();
        if (audits.Count == 0)
        {
            return new TruthAuditAggregate();
        }

        return new TruthAuditAggregate
        {
            RunCount = audits.Count,
            AccountabilityScore = AggregateAuditNumbers(audits.Select(a => a.AccountabilityScore).ToList(), aggregate),
            TruthAuditAccuracy = AggregateAuditNumbers(audits.Select(a => a.TruthAuditAccuracy).ToList(), aggregate),
            OverclaimRate = AggregateAuditNumbers(audits.Select(a => a.OverclaimRate).ToList(), aggregate),
            MissAdmissionRate = AggregateAuditNumbers(audits.Select(a => a.MissAdmissionRate).ToList(), aggregate),
            FalsePositiveAdmissionRate = AggregateAuditNumbers(audits.Select(a => a.FalsePositiveAdmissionRate).ToList(), aggregate),
            EvidenceLaunderingCount = AggregateAuditNumbers(audits.Select(a => (double)a.EvidenceLaunderingCount).ToList(), aggregate),
            QuoteFidelity = AggregateAuditNumbers(audits.Select(a => a.QuoteFidelity).ToList(), aggregate)
        };
    }

    private static double AggregateAuditNumbers(IReadOnlyList<double> values, ComparisonAggregate aggregate)
        => AggregateNumbers(values, aggregate);

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
    Duration
}

public sealed class ComparisonSeries
{
    public string GroupKey { get; init; } = string.Empty;
    public string ModelFamily { get; init; } = string.Empty;
    public string Quant { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
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

    public double? DurationMeanMs { get; init; }
    public double? DurationMedianMs { get; init; }
    public double? DurationMinMs { get; init; }
    public double? DurationMaxMs { get; init; }

    public List<ComparisonRunDetail> Details { get; init; } = [];
}

public sealed class ComparisonRunDetail
{
    public string RecordId { get; init; } = string.Empty;
    public string BenchmarkProfile { get; init; } = string.Empty;
    public string ScoringProfile { get; init; } = string.Empty;
    public int ScoringProfileVersion { get; init; }
    public bool IsLegacyMigrated { get; init; }
    public bool IsRescored { get; init; }
    public bool OfficialComparable { get; init; }
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
    public int FalsePositives { get; init; }
    public int Duplicates { get; init; }
    public int IgnoredLowConfidence { get; init; }
    public int FullTruePositives { get; init; }
    public int PartialTruePositives { get; init; }
    public int Missed { get; init; }
    public bool HasVisibleReasoning { get; init; }

    internal static ComparisonRunDetail FromSample(ComparisonReport.ComparisonSample sample)
    {
        var run1Score = sample.Run1?.ScorePercent ?? 0;
        var run2Score = sample.Run2?.ScorePercent ?? 0;
        return new ComparisonRunDetail
        {
            RecordId = sample.Record.RecordId,
            BenchmarkProfile = sample.Record.BenchmarkProfile,
            ScoringProfile = sample.Run.ScoringProfile,
            ScoringProfileVersion = sample.Run.ScoringProfileVersion,
            IsLegacyMigrated = sample.Run.IsLegacyMigrated,
            IsRescored = sample.Run.IsRescored,
            OfficialComparable = sample.Run.OfficialComparable,
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
            FalsePositives = sample.Run.FalsePositives,
            Duplicates = sample.Run.Duplicates,
            IgnoredLowConfidence = sample.Run.IgnoredLowConfidence,
            FullTruePositives = sample.Run.FullTruePositives,
            PartialTruePositives = sample.Run.PartialTruePositives,
            Missed = sample.Run.Missed,
            HasVisibleReasoning = sample.Run.ReasoningDisclosure?.HasVisibleReasoning == true
        };
    }
}
