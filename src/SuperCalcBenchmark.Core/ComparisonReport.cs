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
    /// Builds a comparison. When <paramref name="aggregate"/> is Average, each model+quant
    /// contributes one series averaged over its runs; when Best, the highest-scoring run is used.
    /// Pass <paramref name="familyFilter"/> to compare only quants of a single model family.
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
            var record = SelectRecord(group, aggregate);
            var primary = record?.PrimaryRun;
            if (record is null || primary is null)
            {
                continue;
            }

            var perVuln = axis
                .Select(id => primary.VulnerabilityCredit.TryGetValue(id, out var credit) ? credit : 0.0)
                .ToList();

            series.Add(new ComparisonSeries
            {
                GroupKey = group.GroupKey,
                ModelFamily = group.ModelFamily,
                Quant = group.Quant,
                Label = $"{group.ModelFamily} · {group.Quant}",
                RunCount = group.RunCount,
                Aggregate = aggregate,
                ScorePercent = aggregate == ComparisonAggregate.Best ? group.BestScorePercent : group.AverageScorePercent,
                Precision = primary.Precision,
                Recall = primary.Recall,
                F1 = primary.F1,
                FullTruePositives = primary.FullTruePositives,
                PartialTruePositives = primary.PartialTruePositives,
                FalsePositives = primary.FalsePositives,
                Missed = primary.Missed,
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

    private static ArchiveRecord? SelectRecord(ArchiveGroup group, ComparisonAggregate aggregate)
    {
        if (group.Records.Count == 0)
        {
            return null;
        }

        // For Best we want the actual run that scored highest (so its per-vuln polygon matches
        // the headline number). For Average we surface the latest run as the representative
        // polygon while the headline number is the group mean.
        return aggregate == ComparisonAggregate.Best
            ? group.Records.OrderByDescending(r => r.PrimaryRun?.ScorePercent ?? 0).First()
            : group.Latest;
    }
}

public enum ComparisonAggregate
{
    /// <summary>Headline number is the mean primary-run score over all runs in the group.</summary>
    Average,

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
    public double ScorePercent { get; init; }
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
