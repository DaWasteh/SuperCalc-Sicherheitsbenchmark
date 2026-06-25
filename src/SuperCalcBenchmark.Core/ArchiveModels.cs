using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// A compact, self-contained scorecard for a single benchmark run, persisted under
/// <c>archive/&lt;benchmark&gt;/&lt;family&gt;__&lt;quant&gt;/&lt;timestamp&gt;.json</c>.
/// Deliberately smaller than the full run.json so the comparison view can load hundreds
/// of historical runs quickly without re-reading prompts and raw API payloads.
/// </summary>
public sealed class ArchiveRecord
{
    public const int CurrentSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("recordId")]
    public string RecordId { get; set; } = string.Empty;

    [JsonPropertyName("benchmarkId")]
    public string BenchmarkId { get; set; } = string.Empty;

    [JsonPropertyName("toolVersion")]
    public string ToolVersion { get; set; } = string.Empty;

    [JsonPropertyName("rawModelId")]
    public string RawModelId { get; set; } = string.Empty;

    [JsonPropertyName("modelFamily")]
    public string ModelFamily { get; set; } = string.Empty;

    [JsonPropertyName("quant")]
    public string Quant { get; set; } = string.Empty;

    [JsonPropertyName("quantWasDetected")]
    public bool QuantWasDetected { get; set; }

    /// <summary>
    /// Derived from <see cref="ModelFamily"/> + <see cref="Quant"/>. On load this is
    /// recomputed, so manual JSON edits only need to change modelFamily/quant.
    /// </summary>
    [JsonPropertyName("groupKey")]
    public string GroupKey { get; set; } = string.Empty;

    [JsonPropertyName("serverContextSize")]
    public int? ServerContextSize { get; set; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("disableThinking")]
    public bool DisableThinking { get; set; }

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = string.Empty;

    [JsonPropertyName("sourceHashMatches")]
    public bool SourceHashMatches { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("runDirectory")]
    public string RunDirectory { get; set; } = string.Empty;

    /// <summary>
    /// Physical JSON path this record was loaded from. Not serialized; used by the app/CLI
    /// to persist manual identity edits back to the scorecard.
    /// </summary>
    [JsonIgnore]
    public string ArchivePath { get; set; } = string.Empty;

    /// <summary>Per-run scorecards. Index 0 is Run 1, index 1 (if present) is Run 2.</summary>
    [JsonPropertyName("runs")]
    public List<ArchiveRunScore> Runs { get; set; } = [];

    /// <summary>
    /// The run used as the headline result in comparisons. Run 2 (self-validation) when it
    /// exists, otherwise Run 1.
    /// </summary>
    [JsonIgnore]
    public ArchiveRunScore? PrimaryRun => Runs.Count == 0 ? null : Runs[^1];
}

public sealed class ArchiveRunScore
{
    [JsonPropertyName("runName")]
    public string RunName { get; set; } = string.Empty;

    [JsonPropertyName("scorePercent")]
    public double ScorePercent { get; set; }

    [JsonPropertyName("rawPoints")]
    public double RawPoints { get; set; }

    [JsonPropertyName("fullTruePositives")]
    public int FullTruePositives { get; set; }

    [JsonPropertyName("partialTruePositives")]
    public int PartialTruePositives { get; set; }

    [JsonPropertyName("falsePositives")]
    public int FalsePositives { get; set; }

    [JsonPropertyName("missed")]
    public int Missed { get; set; }

    [JsonPropertyName("precision")]
    public double Precision { get; set; }

    [JsonPropertyName("recall")]
    public double Recall { get; set; }

    [JsonPropertyName("f1")]
    public double F1 { get; set; }

    /// <summary>
    /// Per-vulnerability credit in [0,1] keyed by ground-truth id: 1.0 full, 0.5 partial,
    /// 0.0 missed. This is the series the radar/net chart plots.
    /// </summary>
    [JsonPropertyName("vulnerabilityCredit")]
    public Dictionary<string, double> VulnerabilityCredit { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public static ArchiveRunScore FromScore(ScoringResult score)
    {
        var credit = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var vulnerability in score.Vulnerabilities)
        {
            credit[vulnerability.Id] = vulnerability.Found ? (vulnerability.Partial ? 0.5 : 1.0) : 0.0;
        }

        return new ArchiveRunScore
        {
            RunName = score.RunName,
            ScorePercent = score.ScorePercent,
            RawPoints = score.RawPoints,
            FullTruePositives = score.FullTruePositives,
            PartialTruePositives = score.PartialTruePositives,
            FalsePositives = score.FalsePositives,
            Missed = score.Missed,
            Precision = score.Precision,
            Recall = score.Recall,
            F1 = score.F1,
            VulnerabilityCredit = credit
        };
    }
}

/// <summary>One model family + quant, with every archived run for it.</summary>
public sealed class ArchiveGroup
{
    public string GroupKey { get; init; } = string.Empty;
    public string ModelFamily { get; init; } = string.Empty;
    public string Quant { get; init; } = string.Empty;
    public List<ArchiveRecord> Records { get; init; } = [];

    public int RunCount => Records.Count;

    public ArchiveRecord? Latest => Records
        .OrderByDescending(r => r.CompletedAt)
        .FirstOrDefault();

    public IReadOnlyList<double> PrimaryScores => Records
        .Select(r => r.PrimaryRun?.ScorePercent ?? 0)
        .ToList();

    /// <summary>Mean primary-run score across every archived run in this group.</summary>
    public double AverageScorePercent => PrimaryScores.Count == 0 ? 0 : PrimaryScores.Average();

    /// <summary>Median primary-run score across every archived run in this group.</summary>
    public double MedianScorePercent => Median(PrimaryScores);

    public double BestScorePercent => PrimaryScores.Count == 0 ? 0 : PrimaryScores.Max();

    public double MinScorePercent => PrimaryScores.Count == 0 ? 0 : PrimaryScores.Min();

    public double MaxScorePercent => PrimaryScores.Count == 0 ? 0 : PrimaryScores.Max();

    /// <summary>Sample standard deviation of primary-run scores. Zero when fewer than two runs exist.</summary>
    public double ScoreStdDev => StandardDeviation(PrimaryScores);

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
