namespace SuperCalcBenchmark.Core;

public sealed class ScoringEngine
{
    private const double FullThreshold = 0.75;
    private const double PartialThreshold = 0.55;
    private const double FullTpPoints = 5.0;
    private const double PartialTpPoints = 2.5;
    private const double FalsePositivePenalty = -2.0;
    private const double DuplicatePenalty = -1.0;
    private const double SeverityMismatchPenalty = -1.0;

    public ScoringResult Score(string runName, IReadOnlyList<LlmFinding> findings, GroundTruthDocument groundTruth, SourceDocument source)
    {
        var scoreable = groundTruth.Vulnerabilities.Where(v => v.StrictScoreable).ToList();
        var candidates = findings.Select(f => BuildBestCandidate(f, scoreable, source)).ToList();
        var assigned = AssignCandidates(candidates);
        var findingScores = new List<FindingScore>();

        foreach (var candidate in candidates)
        {
            var finding = candidate.Finding;
            var score = new FindingScore
            {
                FindingIndex = finding.Index,
                FindingTitle = string.IsNullOrWhiteSpace(finding.Title) ? finding.VulnerabilityType : finding.Title,
                MatchScore = candidate.Score,
                Signals = candidate.Signals,
                MatchedVulnerabilityId = candidate.Vulnerability?.Id,
                MatchedVulnerabilityTitle = candidate.Vulnerability?.Title
            };

            if (candidate.Vulnerability is not null && candidate.Score >= PartialThreshold)
            {
                var assignedFinding = assigned.TryGetValue(candidate.Vulnerability.Id, out var assignedCandidate) && ReferenceEquals(assignedCandidate, candidate);
                if (assignedFinding)
                {
                    score.Classification = candidate.Score >= FullThreshold
                        ? FindingClassification.FullTruePositive
                        : FindingClassification.PartialTruePositive;
                    score.SeverityMismatch = IsSeverityMismatch(finding.Severity, candidate.Vulnerability.Severity);
                    score.Points = score.Classification == FindingClassification.FullTruePositive ? FullTpPoints : PartialTpPoints;
                    if (score.SeverityMismatch)
                    {
                        score.Points += SeverityMismatchPenalty;
                    }

                    score.Reason = score.SeverityMismatch
                        ? $"Matched {candidate.Vulnerability.Id}, but severity differs ({finding.Severity} vs {candidate.Vulnerability.Severity})."
                        : $"Matched {candidate.Vulnerability.Id}.";
                }
                else
                {
                    score.Classification = FindingClassification.Duplicate;
                    score.Duplicate = true;
                    score.Points = DuplicatePenalty;
                    score.Reason = $"Duplicate report for {candidate.Vulnerability.Id}; another finding had the stronger match.";
                }
            }
            else if (finding.Confidence < 0.35 && candidate.Score < PartialThreshold)
            {
                score.Classification = FindingClassification.IgnoredLowConfidence;
                score.Points = 0;
                score.MatchedVulnerabilityId = null;
                score.MatchedVulnerabilityTitle = null;
                score.Reason = "Ignored because confidence is low and no ground-truth item matched.";
            }
            else
            {
                score.Classification = FindingClassification.FalsePositive;
                score.Points = FalsePositivePenalty;
                score.MatchedVulnerabilityId = null;
                score.MatchedVulnerabilityTitle = null;
                score.Reason = "No sufficient match in hidden ground truth.";
            }

            findingScores.Add(score);
        }

        var vulnerabilityScores = scoreable.Select(v =>
        {
            var matchingScore = findingScores
                .Where(f => string.Equals(f.MatchedVulnerabilityId, v.Id, StringComparison.OrdinalIgnoreCase)
                            && f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive)
                .OrderByDescending(f => f.MatchScore)
                .FirstOrDefault();

            return new VulnerabilityScore
            {
                Id = v.Id,
                Title = v.Title,
                Severity = v.Severity,
                Found = matchingScore is not null,
                Partial = matchingScore?.Classification == FindingClassification.PartialTruePositive,
                FindingIndex = matchingScore?.FindingIndex,
                MatchScore = matchingScore?.MatchScore ?? 0
            };
        }).ToList();

        var fullTp = findingScores.Count(f => f.Classification == FindingClassification.FullTruePositive);
        var partialTp = findingScores.Count(f => f.Classification == FindingClassification.PartialTruePositive);
        var falsePositives = findingScores.Count(f => f.Classification == FindingClassification.FalsePositive);
        var duplicates = findingScores.Count(f => f.Classification == FindingClassification.Duplicate);
        var ignored = findingScores.Count(f => f.Classification == FindingClassification.IgnoredLowConfidence);
        var missed = vulnerabilityScores.Count(v => !v.Found);
        var rawPoints = findingScores.Sum(f => f.Points);
        var maxPoints = scoreable.Count * FullTpPoints;
        var scorePercent = maxPoints == 0 ? 0 : TextUtil.Clamp(rawPoints / maxPoints * 100.0, 0, 100);
        var weightedTp = fullTp + partialTp * 0.5;
        var precisionDenominator = fullTp + partialTp + falsePositives;
        var precision = precisionDenominator == 0 ? 0 : weightedTp / precisionDenominator;
        var recall = scoreable.Count == 0 ? 0 : weightedTp / scoreable.Count;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);

        return new ScoringResult
        {
            RunName = runName,
            ScoreableVulnerabilityCount = scoreable.Count,
            FindingCount = findings.Count,
            FullTruePositives = fullTp,
            PartialTruePositives = partialTp,
            FalsePositives = falsePositives,
            Duplicates = duplicates,
            IgnoredLowConfidence = ignored,
            Missed = missed,
            RawPoints = Math.Round(rawPoints, 2),
            ScorePercent = Math.Round(scorePercent, 2),
            Precision = Math.Round(precision, 4),
            Recall = Math.Round(recall, 4),
            F1 = Math.Round(f1, 4),
            Findings = findingScores,
            Vulnerabilities = vulnerabilityScores
        };
    }

    public RunComparison Compare(ScoringResult run1, ScoringResult run2)
    {
        static HashSet<string> TruePositiveIds(ScoringResult result) => result.Findings
            .Where(f => f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive)
            .Select(f => f.MatchedVulnerabilityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var run1Ids = TruePositiveIds(run1);
        var run2Ids = TruePositiveIds(run2);
        var kept = run1Ids.Intersect(run2Ids, StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        var dropped = run1Ids.Except(run2Ids, StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();
        var added = run2Ids.Except(run1Ids, StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToList();

        return new RunComparison
        {
            KeptTruePositiveIds = kept,
            DroppedTruePositiveIds = dropped,
            AddedTruePositiveIds = added,
            Run1FalsePositives = run1.FalsePositives,
            Run2FalsePositives = run2.FalsePositives,
            FalsePositiveReduction = run1.FalsePositives - run2.FalsePositives,
            TruePositiveRetention = run1Ids.Count == 0 ? 0 : Math.Round((double)kept.Count / run1Ids.Count, 4)
        };
    }

    private static Dictionary<string, MatchCandidate> AssignCandidates(List<MatchCandidate> candidates)
    {
        var assigned = new Dictionary<string, MatchCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates
                     .Where(c => c.Vulnerability is not null && c.Score >= PartialThreshold)
                     .OrderByDescending(c => c.Score)
                     .ThenByDescending(c => c.Finding.Confidence))
        {
            if (!assigned.ContainsKey(candidate.Vulnerability!.Id))
            {
                assigned[candidate.Vulnerability.Id] = candidate;
            }
        }

        return assigned;
    }

    private static MatchCandidate BuildBestCandidate(LlmFinding finding, IReadOnlyList<VulnerabilityDefinition> vulnerabilities, SourceDocument source)
    {
        MatchCandidate? best = null;
        foreach (var vulnerability in vulnerabilities)
        {
            var candidate = ScoreCandidate(finding, vulnerability, source);
            if (best is null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best ?? new MatchCandidate(finding, null, 0, []);
    }

    private static MatchCandidate ScoreCandidate(LlmFinding finding, VulnerabilityDefinition vulnerability, SourceDocument source)
    {
        var alias = ScoreAlias(finding, vulnerability);
        var location = ScoreLocation(finding, vulnerability);
        var evidence = ScoreEvidence(finding, vulnerability, source);
        var cweSeverity = ScoreCweSeverity(finding, vulnerability);
        var impact = ScoreImpact(finding, vulnerability);

        var signals = new List<SignalScore>
        {
            new() { Name = "type_alias", Weight = 0.25, Value = alias.Score, Detail = alias.Detail },
            new() { Name = "location", Weight = 0.30, Value = location.Score, Detail = location.Detail },
            new() { Name = "evidence", Weight = 0.25, Value = evidence.Score, Detail = evidence.Detail },
            new() { Name = "cwe_severity", Weight = 0.10, Value = cweSeverity.Score, Detail = cweSeverity.Detail },
            new() { Name = "impact_trigger", Weight = 0.10, Value = impact.Score, Detail = impact.Detail }
        };

        var score = signals.Sum(s => s.Weighted);
        return new MatchCandidate(finding, vulnerability, Math.Round(score, 4), signals);
    }

    private static (double Score, string Detail) ScoreAlias(LlmFinding finding, VulnerabilityDefinition vulnerability)
    {
        var combined = CombinedFindingText(finding);
        var exactAliases = vulnerability.Aliases
            .Where(alias => TextUtil.ContainsNormalized(combined, alias))
            .ToList();

        if (exactAliases.Count > 0)
        {
            return (1.0, $"Alias matched: {string.Join(", ", exactAliases.Take(3))}");
        }

        var cweAliases = vulnerability.Cwe.Where(cwe => TextUtil.ContainsNormalized(combined, cwe)).ToList();
        if (cweAliases.Count > 0)
        {
            return (0.85, $"CWE alias matched: {string.Join(", ", cweAliases)}");
        }

        var overlap = Math.Max(
            TextUtil.TokenOverlap(finding.Title, vulnerability.Title),
            TextUtil.TokenOverlap(finding.VulnerabilityType, vulnerability.Title));

        return (overlap >= 0.5 ? 0.65 : overlap * 0.8, overlap > 0 ? $"Title/type token overlap {overlap:0.00}." : "No type/alias overlap.");
    }

    private static (double Score, string Detail) ScoreLocation(LlmFinding finding, VulnerabilityDefinition vulnerability)
    {
        var details = new List<string>();
        var best = 0.0;
        foreach (var location in vulnerability.Locations)
        {
            var score = 0.0;
            var fileMatches = string.IsNullOrWhiteSpace(finding.File)
                              || string.Equals(Path.GetFileName(finding.File), Path.GetFileName(location.File), StringComparison.OrdinalIgnoreCase)
                              || finding.File.Contains(Path.GetFileName(location.File), StringComparison.OrdinalIgnoreCase);
            if (fileMatches)
            {
                score += 0.15;
            }

            var symbolScore = SymbolScore(finding.FunctionOrSymbol, location.Symbol, finding.Title + " " + finding.Evidence);
            score += 0.35 * symbolScore;
            if (symbolScore > 0)
            {
                details.Add($"symbol {location.Symbol}={symbolScore:0.00}");
            }

            var lineScore = LineOverlapScore(finding.LineStart, finding.LineEnd, location.LineStart, location.LineEnd);
            score += 0.50 * lineScore;
            if (lineScore > 0)
            {
                details.Add($"lines {finding.LineStart}-{finding.LineEnd} overlap {location.LineStart}-{location.LineEnd}={lineScore:0.00}");
            }

            best = Math.Max(best, TextUtil.Clamp01(score));
        }

        return (best, details.Count > 0 ? string.Join("; ", details.Take(4)) : "No location signal.");
    }

    private static double SymbolScore(string reportedSymbol, string groundTruthSymbol, string fallbackText)
    {
        if (string.IsNullOrWhiteSpace(groundTruthSymbol))
        {
            return 0;
        }

        var symbolLeaf = TextUtil.SymbolLeaf(groundTruthSymbol);
        if (TextUtil.ContainsNormalized(reportedSymbol, groundTruthSymbol) || TextUtil.ContainsNormalized(reportedSymbol, symbolLeaf))
        {
            return 1.0;
        }

        if (TextUtil.ContainsNormalized(fallbackText, symbolLeaf))
        {
            return 0.7;
        }

        var overlap = TextUtil.TokenOverlap(reportedSymbol, groundTruthSymbol);
        return overlap >= 0.5 ? 0.6 : 0;
    }

    private static double LineOverlapScore(int findingStart, int findingEnd, int truthStart, int truthEnd)
    {
        if (findingStart <= 0)
        {
            return 0;
        }

        if (findingEnd <= 0)
        {
            findingEnd = findingStart;
        }

        if (findingEnd < findingStart)
        {
            (findingStart, findingEnd) = (findingEnd, findingStart);
        }

        var overlapStart = Math.Max(findingStart, truthStart);
        var overlapEnd = Math.Min(findingEnd, truthEnd);
        if (overlapStart <= overlapEnd)
        {
            return 1.0;
        }

        var distance = findingEnd < truthStart ? truthStart - findingEnd : findingStart - truthEnd;
        return distance switch
        {
            <= 3 => 0.75,
            <= 10 => 0.45,
            <= 25 => 0.2,
            _ => 0
        };
    }

    private static (double Score, string Detail) ScoreEvidence(LlmFinding finding, VulnerabilityDefinition vulnerability, SourceDocument source)
    {
        var combined = CombinedFindingText(finding);
        var requiredMatches = vulnerability.RequiredEvidence
            .Where(evidence => TextUtil.ContainsNormalized(combined, evidence))
            .ToList();

        var sourceEvidenceScore = EvidenceAppearsInSource(finding.Evidence, source.Text) ? 0.35 : 0;
        var requiredScore = vulnerability.RequiredEvidence.Count == 0
            ? 0
            : Math.Min(0.65, 0.65 * requiredMatches.Count / Math.Min(3, vulnerability.RequiredEvidence.Count));

        var score = TextUtil.Clamp01(sourceEvidenceScore + requiredScore);
        var details = new List<string>();
        if (sourceEvidenceScore > 0)
        {
            details.Add("quoted evidence appears in source");
        }

        if (requiredMatches.Count > 0)
        {
            details.Add($"required anchors: {string.Join(", ", requiredMatches.Take(3))}");
        }

        return (score, details.Count > 0 ? string.Join("; ", details) : "No accepted evidence anchor.");
    }

    private static bool EvidenceAppearsInSource(string evidence, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return false;
        }

        var snippets = evidence.Split(['\n', '\r', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('`', ' ', '\t'))
            .Where(s => s.Length >= 8)
            .Take(10)
            .ToList();

        if (snippets.Any(snippet => sourceText.Contains(snippet, StringComparison.Ordinal)))
        {
            return true;
        }

        var normalizedSource = TextUtil.Normalize(sourceText);
        return snippets.Any(snippet => TextUtil.Normalize(snippet).Length >= 8 && normalizedSource.Contains(TextUtil.Normalize(snippet), StringComparison.Ordinal));
    }

    private static (double Score, string Detail) ScoreCweSeverity(LlmFinding finding, VulnerabilityDefinition vulnerability)
    {
        var cweMatches = vulnerability.Cwe.Where(cwe => TextUtil.ContainsNormalized(finding.Cwe + " " + CombinedFindingText(finding), cwe)).ToList();
        var cweScore = cweMatches.Count > 0 ? 0.65 : 0;
        var severityScore = IsSeverityMismatch(finding.Severity, vulnerability.Severity) ? 0 : 0.35;

        if (string.IsNullOrWhiteSpace(finding.Severity) || string.Equals(finding.Severity, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            severityScore = 0.1;
        }

        var detail = $"CWE {(cweMatches.Count > 0 ? string.Join(", ", cweMatches) : "not matched")}; severity {finding.Severity}/{vulnerability.Severity}.";
        return (TextUtil.Clamp01(cweScore + severityScore), detail);
    }

    private static (double Score, string Detail) ScoreImpact(LlmFinding finding, VulnerabilityDefinition vulnerability)
    {
        var impactText = string.Join(' ', finding.Impact, finding.Trigger, finding.Title, finding.VulnerabilityType);
        if (!string.IsNullOrWhiteSpace(vulnerability.Trigger) && TextUtil.ContainsNormalized(impactText, vulnerability.Trigger))
        {
            return (1.0, "Trigger matched.");
        }

        var aliasMatches = vulnerability.Aliases.Count(alias => TextUtil.ContainsNormalized(impactText, alias));
        if (aliasMatches > 0)
        {
            return (0.65, "Impact/trigger text contains vulnerability alias.");
        }

        if (!string.IsNullOrWhiteSpace(finding.Impact) || !string.IsNullOrWhiteSpace(finding.Trigger))
        {
            var overlap = TextUtil.TokenOverlap(impactText, vulnerability.Title + " " + string.Join(' ', vulnerability.RequiredEvidence));
            return (Math.Min(0.55, overlap), overlap > 0 ? $"Impact token overlap {overlap:0.00}." : "Impact present but not aligned.");
        }

        return (0, "No impact/trigger signal.");
    }

    private static bool IsSeverityMismatch(string reported, string expected)
    {
        if (string.IsNullOrWhiteSpace(reported) || string.Equals(reported, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !string.Equals(reported.Trim(), expected.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static string CombinedFindingText(LlmFinding finding)
    {
        return string.Join(' ', finding.Title, finding.VulnerabilityType, finding.Cwe, finding.File, finding.FunctionOrSymbol, finding.Evidence, finding.Impact, finding.Trigger, finding.Fix);
    }

    private sealed record MatchCandidate(
        LlmFinding Finding,
        VulnerabilityDefinition? Vulnerability,
        double Score,
        List<SignalScore> Signals);
}
