using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public sealed class AdjudicationDocument
{
    [JsonPropertyName("adjudication_schema")]
    public int AdjudicationSchema { get; set; } = 1;

    [JsonPropertyName("items")]
    public List<AdjudicationItem> Items { get; set; } = [];
}

public sealed class AdjudicationItem
{
    [JsonPropertyName("run")]
    public string Run { get; set; } = string.Empty;

    [JsonPropertyName("findingIndex")]
    public int FindingIndex { get; set; }

    [JsonPropertyName("decision")]
    public string Decision { get; set; } = string.Empty;

    [JsonPropertyName("matchedVulnerabilityId")]
    public string MatchedVulnerabilityId { get; set; } = string.Empty;

    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;

    [JsonPropertyName("reviewer")]
    public string Reviewer { get; set; } = "local";
}

public static class AdjudicationApplier
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public static ScoringResult ApplyFromFile(ScoringResult score, string adjudicationPath)
    {
        if (string.IsNullOrWhiteSpace(adjudicationPath) || !File.Exists(adjudicationPath))
        {
            return score;
        }

        var json = File.ReadAllText(adjudicationPath, System.Text.Encoding.UTF8);
        var document = JsonSerializer.Deserialize<AdjudicationDocument>(json, ReadOptions) ?? new AdjudicationDocument();
        return Apply(score, document, adjudicationPath);
    }

    public static ScoringResult Apply(ScoringResult score, AdjudicationDocument document, string label = "local")
    {
        var applicable = document.Items
            .Where(item => item.FindingIndex > 0)
            .Where(item => string.IsNullOrWhiteSpace(item.Run) || string.Equals(item.Run, score.RunName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (applicable.Count == 0)
        {
            return score;
        }

        var baseProfileName = score.ScoringProfile.Replace("+adjudicated", string.Empty, StringComparison.OrdinalIgnoreCase);
        var profile = ScoringProfiles.TryGet(baseProfileName, out var resolvedProfile)
            ? resolvedProfile
            : ScoringProfiles.OfficialV1;
        var findings = score.Findings.Select(CloneFinding).ToList();

        foreach (var item in applicable)
        {
            var finding = findings.FirstOrDefault(f => f.FindingIndex == item.FindingIndex);
            if (finding is null)
            {
                continue;
            }

            var decision = (item.Decision ?? string.Empty).Trim().ToLowerInvariant();
            switch (decision)
            {
                case "accept_full":
                case "accept_partial":
                    var target = score.Vulnerabilities.FirstOrDefault(v =>
                        string.Equals(v.Id, item.MatchedVulnerabilityId, StringComparison.OrdinalIgnoreCase));
                    if (target is null)
                    {
                        break;
                    }

                    finding.Classification = decision == "accept_full"
                        ? FindingClassification.FullTruePositive
                        : FindingClassification.PartialTruePositive;
                    finding.MatchedVulnerabilityId = target.Id;
                    finding.MatchedVulnerabilityTitle = target.Title;
                    finding.Points = decision == "accept_full" ? profile.Points.FullTp : profile.Points.PartialTp;
                    finding.Duplicate = false;
                    finding.FalsePositiveCategory = string.Empty;
                    finding.Reason = AppendAdjudication(finding.Reason, item);
                    finding.AdjudicationReason = item.Reason;
                    break;
                case "ignore":
                    finding.Classification = FindingClassification.IgnoredLowConfidence;
                    finding.MatchedVulnerabilityId = null;
                    finding.MatchedVulnerabilityTitle = null;
                    finding.Points = 0;
                    finding.FalsePositiveCategory = string.Empty;
                    finding.Reason = AppendAdjudication(finding.Reason, item);
                    finding.AdjudicationReason = item.Reason;
                    break;
                case "keep_fp":
                    finding.Classification = FindingClassification.FalsePositive;
                    finding.MatchedVulnerabilityId = null;
                    finding.MatchedVulnerabilityTitle = null;
                    finding.Points = profile.Points.FalsePositive;
                    finding.FalsePositiveCategory = string.IsNullOrWhiteSpace(finding.FalsePositiveCategory) ? "adjudicated_keep_fp" : finding.FalsePositiveCategory;
                    finding.Reason = AppendAdjudication(finding.Reason, item);
                    finding.AdjudicationReason = item.Reason;
                    break;
            }
        }

        return Recompute(score, findings, label, profile);
    }

    private static ScoringResult Recompute(ScoringResult original, List<FindingScore> findings, string label, ScoringProfile profile)
    {
        var vulnerabilities = original.Vulnerabilities.Select(v => new VulnerabilityScore
        {
            Id = v.Id,
            Title = v.Title,
            Severity = v.Severity,
            Category = v.Category,
            Module = v.Module,
            Exploitability = v.Exploitability,
            Difficulty = v.Difficulty
        }).ToList();

        foreach (var group in findings
                     .Where(f => f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive)
                     .Where(f => !string.IsNullOrWhiteSpace(f.MatchedVulnerabilityId))
                     .GroupBy(f => f.MatchedVulnerabilityId!, StringComparer.OrdinalIgnoreCase))
        {
            var winner = group
                .OrderByDescending(f => f.Classification == FindingClassification.FullTruePositive)
                .ThenByDescending(f => f.MatchScore)
                .ThenBy(f => f.FindingIndex)
                .First();
            foreach (var duplicate in group.Where(f => !ReferenceEquals(f, winner)))
            {
                duplicate.Classification = FindingClassification.Duplicate;
                duplicate.Points = profile.Points.Duplicate;
                duplicate.Duplicate = true;
                duplicate.FalsePositiveCategory = string.Empty;
                duplicate.Reason = (duplicate.Reason ?? string.Empty).TrimEnd()
                                   + $" Duplicate after adjudication; finding {winner.FindingIndex} already represents {group.Key}.";
            }
        }

        foreach (var vulnerability in vulnerabilities)
        {
            var match = findings
                .Where(f => string.Equals(f.MatchedVulnerabilityId, vulnerability.Id, StringComparison.OrdinalIgnoreCase)
                            && f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive)
                .OrderByDescending(f => f.Classification == FindingClassification.FullTruePositive)
                .ThenByDescending(f => f.MatchScore)
                .FirstOrDefault();
            vulnerability.Found = match is not null;
            vulnerability.Partial = match?.Classification == FindingClassification.PartialTruePositive;
            vulnerability.FindingIndex = match?.FindingIndex;
            vulnerability.MatchScore = match?.MatchScore ?? 0;
            vulnerability.EvidenceFidelity = match?.EvidenceFidelity ?? 0;
            vulnerability.LocationAccuracy = match?.LocationAccuracy ?? 0;
        }

        var fullTp = findings.Count(f => f.Classification == FindingClassification.FullTruePositive);
        var partialTp = findings.Count(f => f.Classification == FindingClassification.PartialTruePositive);
        var fp = findings.Count(f => f.Classification == FindingClassification.FalsePositive);
        var duplicates = findings.Count(f => f.Classification == FindingClassification.Duplicate);
        var ignored = findings.Count(f => f.Classification == FindingClassification.IgnoredLowConfidence);
        var missed = vulnerabilities.Count(v => !v.Found);
        var raw = findings.Sum(f => f.Points);
        var max = original.MaxPoints > 0 ? original.MaxPoints : original.ScoreableVulnerabilityCount * profile.Points.FullTp;
        var percent = max == 0 ? 0 : TextUtil.Clamp(raw / max * 100, 0, 100);
        var weightedTp = fullTp + partialTp * 0.5;
        var precisionDenominator = fullTp + partialTp + fp;
        var precision = precisionDenominator == 0 ? 0 : weightedTp / precisionDenominator;
        var recall = original.ScoreableVulnerabilityCount == 0 ? 0 : weightedTp / original.ScoreableVulnerabilityCount;
        var positives = findings.Where(f => f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive).ToList();
        var reported = Math.Max(1, findings.Count(f => f.Classification != FindingClassification.IgnoredLowConfidence));
        var taxonomy = findings
            .Where(f => f.Classification == FindingClassification.FalsePositive)
            .GroupBy(f => string.IsNullOrWhiteSpace(f.FalsePositiveCategory) ? "unsupported_by_code" : f.FalsePositiveCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new ScoringResult
        {
            RunName = original.RunName,
            ScoreSchemaVersion = original.ScoreSchemaVersion,
            ScoringProfile = original.ScoringProfile.EndsWith("+adjudicated", StringComparison.OrdinalIgnoreCase) ? original.ScoringProfile : original.ScoringProfile + "+adjudicated",
            ScoringProfileVersion = original.ScoringProfileVersion,
            ScoringEngineVersion = original.ScoringEngineVersion,
            ParserVersion = original.ParserVersion,
            GroundTruthSha256 = original.GroundTruthSha256,
            SourceSha256 = original.SourceSha256,
            PromptVersion = original.PromptVersion,
            ComputedAt = DateTimeOffset.UtcNow,
            IsLegacyMigrated = original.IsLegacyMigrated,
            IsRescored = true,
            IsAdjudicated = true,
            AdjudicationLabel = label,
            MaxPoints = Math.Round(max, 2),
            ScoreableVulnerabilityCount = original.ScoreableVulnerabilityCount,
            FindingCount = original.FindingCount,
            FullTruePositives = fullTp,
            PartialTruePositives = partialTp,
            FalsePositives = fp,
            Duplicates = duplicates,
            IgnoredLowConfidence = ignored,
            Missed = missed,
            RawPoints = Math.Round(raw, 2),
            ScorePercent = Math.Round(percent, 2),
            Precision = Math.Round(precision, 4),
            Recall = Math.Round(recall, 4),
            F1 = precision + recall == 0 ? 0 : Math.Round(2 * precision * recall / (precision + recall), 4),
            EvidenceFidelity = positives.Count == 0 ? 0 : Math.Round(positives.Average(f => f.EvidenceFidelity), 4),
            LocationAccuracy = positives.Count == 0 ? 0 : Math.Round(positives.Average(f => f.LocationAccuracy), 4),
            HallucinationRate = Math.Round(fp / (double)reported, 4),
            DuplicateRate = findings.Count == 0 ? 0 : Math.Round(duplicates / (double)findings.Count, 4),
            EvaluationConfidence = original.EvaluationConfidence,
            FalsePositiveTaxonomy = taxonomy,
            Findings = findings,
            Vulnerabilities = vulnerabilities
        };
    }

    private static string AppendAdjudication(string reason, AdjudicationItem item)
        => (reason ?? string.Empty).TrimEnd() + $" Adjudicated by {item.Reviewer}: {item.Decision} ({item.Reason})";

    private static FindingScore CloneFinding(FindingScore finding) => new()
    {
        FindingIndex = finding.FindingIndex,
        FindingTitle = finding.FindingTitle,
        MatchedVulnerabilityId = finding.MatchedVulnerabilityId,
        MatchedVulnerabilityTitle = finding.MatchedVulnerabilityTitle,
        MatchScore = finding.MatchScore,
        Classification = finding.Classification,
        Points = finding.Points,
        Duplicate = finding.Duplicate,
        SeverityMismatch = finding.SeverityMismatch,
        EvidenceFidelity = finding.EvidenceFidelity,
        LocationAccuracy = finding.LocationAccuracy,
        EvidenceExactMatch = finding.EvidenceExactMatch,
        EvidenceNormalizedMatch = finding.EvidenceNormalizedMatch,
        FalsePositiveCategory = finding.FalsePositiveCategory,
        ReportedFile = finding.ReportedFile,
        ReportedLineStart = finding.ReportedLineStart,
        ReportedLineEnd = finding.ReportedLineEnd,
        ReportedSymbol = finding.ReportedSymbol,
        ReportedEvidence = finding.ReportedEvidence,
        AcceptedEvidenceAnchors = finding.AcceptedEvidenceAnchors.ToList(),
        MissingMustAnchors = finding.MissingMustAnchors.ToList(),
        RejectedBecause = finding.RejectedBecause.ToList(),
        Signals = finding.Signals.ToList(),
        AdjudicationReason = finding.AdjudicationReason,
        Reason = finding.Reason
    };
}
