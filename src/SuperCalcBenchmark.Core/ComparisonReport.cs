namespace SuperCalcBenchmark.Core;

/// <summary>
/// Turns a set of <see cref="ArchiveGroup"/>s into the data a comparison view needs:
/// one bar per model+quant (total score) and one radar polygon per model+quant
/// (per-vulnerability credit), sharing a common vulnerability axis.
/// </summary>
public sealed class ComparisonReport
{
    public string BenchmarkId { get; init; } = string.Empty;
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.Now;

    /// <summary>Shared radar axis: every ground-truth id seen across the included runs.</summary>
    public List<string> VulnerabilityAxis { get; init; } = [];

    public List<ComparisonSeries> Series { get; init; } = [];

    public bool IsEmpty => Series.Count == 0;

    /// <summary>
    /// Builds a comparison. Average and Median aggregate all primary runs in a model+quant
    /// group; Best uses the single highest-scoring primary run. Pass <paramref name="familyFilter"/>
    /// to compare only quants of a single model family.
    /// </summary>
    public static ComparisonReport Build(
        IReadOnlyList<ArchiveGroup> groups,
        string benchmarkId,
        ComparisonAggregate aggregate = ComparisonAggregate.Average,
        string? familyFilter = null)
    {
        var selected = string.IsNullOrWhiteSpace(familyFilter)
            ? groups
            : groups.Where(g => string.Equals(g.ModelFamily, familyFilter, StringComparison.OrdinalIgnoreCase)).ToList();

        // Union of every vulnerability id across the chosen records, in a stable order.
        var axis = selected
            .SelectMany(g => g.Records)
            .SelectMany(r => r.PrimaryRun?.VulnerabilityCredit.Keys ?? Enumerable.Empty<string>())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var series = new List<ComparisonSeries>();
        foreach (var group in selected)
        {
            var runs = group.Records
                .Select(r => r.PrimaryRun)
                .Where(r => r is not null)
                .Cast<ArchiveRunScore>()
                .ToList();

            if (runs.Count == 0)
            {
                continue;
            }

            var bestRun = runs.OrderByDescending(r => r.ScorePercent).First();
            var scoreValues = runs.Select(r => r.ScorePercent).ToList();
            var score = aggregate switch
            {
                ComparisonAggregate.Best => bestRun.ScorePercent,
                ComparisonAggregate.Median => Median(scoreValues),
                _ => scoreValues.Average()
            };

            var perVuln = axis
                .Select(id => AggregateCredit(runs, id, aggregate, bestRun))
                .ToList();

            series.Add(new ComparisonSeries
            {
                GroupKey = group.GroupKey,
                ModelFamily = group.ModelFamily,
                Quant = group.Quant,
                Label = $"{group.ModelFamily} · {group.Quant}",
                RunCount = runs.Count,
                Aggregate = aggregate,
                ScorePercent = score,
                ScoreMean = scoreValues.Average(),
                ScoreMedian = Median(scoreValues),
                ScoreStdDev = StandardDeviation(scoreValues),
                ScoreMin = scoreValues.Min(),
                ScoreMax = scoreValues.Max(),
                Precision = AggregateMetric(runs, r => r.Precision, aggregate, bestRun),
                Recall = AggregateMetric(runs, r => r.Recall, aggregate, bestRun),
                F1 = AggregateMetric(runs, r => r.F1, aggregate, bestRun),
                FullTruePositives = RoundToInt(AggregateMetric(runs, r => r.FullTruePositives, aggregate, bestRun)),
                PartialTruePositives = RoundToInt(AggregateMetric(runs, r => r.PartialTruePositives, aggregate, bestRun)),
                FalsePositives = RoundToInt(AggregateMetric(runs, r => r.FalsePositives, aggregate, bestRun)),
                Missed = RoundToInt(AggregateMetric(runs, r => r.Missed, aggregate, bestRun)),
                PerVulnerabilityCredit = perVuln
            });
        }

        return new ComparisonReport
        {
            BenchmarkId = benchmarkId,
            VulnerabilityAxis = axis,
            Series = series
                .OrderByDescending(s => s.ScorePercent)
                .ThenBy(s => s.Label, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private static double AggregateCredit(
        IReadOnlyList<ArchiveRunScore> runs,
        string vulnerabilityId,
        ComparisonAggregate aggregate,
        ArchiveRunScore bestRun)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            return bestRun.VulnerabilityCredit.TryGetValue(vulnerabilityId, out var bestCredit) ? bestCredit : 0.0;
        }

        var values = runs
            .Select(r => r.VulnerabilityCredit.TryGetValue(vulnerabilityId, out var credit) ? credit : 0.0)
            .ToList();

        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
    }

    private static double AggregateMetric(
        IReadOnlyList<ArchiveRunScore> runs,
        Func<ArchiveRunScore, double> selector,
        ComparisonAggregate aggregate,
        ArchiveRunScore bestRun)
    {
        if (aggregate == ComparisonAggregate.Best)
        {
            return selector(bestRun);
        }

        var values = runs.Select(selector).ToList();
        return aggregate == ComparisonAggregate.Median ? Median(values) : values.Average();
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
}

public enum ComparisonAggregate
{
    /// <summary>Headline number is the mean primary-run score over all runs in the group.</summary>
    Average,

    /// <summary>Headline number is the median primary-run score over all runs in the group.</summary>
    Median,

    /// <summary>Headline number is the single best primary-run score in the group.</summary>
    Best
}

public sealed class ComparisonSeries
{
    public string GroupKey { get; init; } = string.Empty;
    public string ModelFamily { get; init; } = string.Empty;
    public string Quant { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public int RunCount { get; init; }
    public ComparisonAggregate Aggregate { get; init; }

    /// <summary>The selected headline value (mean, median, or best, depending on <see cref="Aggregate"/>).</summary>
    public double ScorePercent { get; init; }

    /// <summary>Score distribution across all primary runs in this model+quant group.</summary>
    public double ScoreMean { get; init; }
    public double ScoreMedian { get; init; }
    public double ScoreStdDev { get; init; }
    public double ScoreMin { get; init; }
    public double ScoreMax { get; init; }

    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1 { get; init; }
    public int FullTruePositives { get; init; }
    public int PartialTruePositives { get; init; }
    public int FalsePositives { get; init; }
    public int Missed { get; init; }

    /// <summary>Per-vulnerability credit aligned to <see cref="ComparisonReport.VulnerabilityAxis"/>.</summary>
    public List<double> PerVulnerabilityCredit { get; init; } = [];
}
