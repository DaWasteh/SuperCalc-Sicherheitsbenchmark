namespace SuperCalcBenchmark.Core;

public sealed class ScoringEngine
{
    private readonly ScoringProfile _defaultProfile;

    public ScoringEngine(ScoringProfile? defaultProfile = null)
    {
        _defaultProfile = defaultProfile ?? ScoringProfiles.OfficialV1;
    }

    public ScoringResult Score(
        string runName,
        IReadOnlyList<LlmFinding> findings,
        GroundTruthDocument groundTruth,
        SourceDocument source,
        ScoringProfile? profile = null,
        ScoreComputationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(groundTruth);
        ArgumentNullException.ThrowIfNull(source);
        profile ??= _defaultProfile;
        context ??= new ScoreComputationContext();
        ValidateProfile(profile);

        if (groundTruth.Vulnerabilities is null
            || groundTruth.Vulnerabilities.Any(vulnerability => vulnerability is null))
        {
            throw new ArgumentException("Ground truth contains a null vulnerabilities collection or entry.", nameof(groundTruth));
        }

        if (findings.Any(finding => finding is null || !double.IsFinite(finding.Confidence)))
        {
            throw new ArgumentException("Findings must not contain null entries or non-finite confidence values.", nameof(findings));
        }

        var scoreable = groundTruth.Vulnerabilities.Where(v => v.StrictScoreable).ToList();
        var candidateSets = findings
            .Select(f => scoreable.Select(v => ScoreCandidate(f, v, source, profile)).ToList())
            .ToList();
        var candidates = findings.Select((f, i) => PickBestCandidate(f, candidateSets[i])).ToList();
        var assigned = profile.Matching.OptimalAssignment
            ? AssignOptimally(candidateSets, candidates, profile)
            : AssignCandidates(candidates, profile);
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
                EvidenceFidelity = candidate.EvidenceFidelity,
                LocationAccuracy = candidate.LocationAccuracy,
                EvidenceExactMatch = candidate.EvidenceExactMatch,
                EvidenceNormalizedMatch = candidate.EvidenceNormalizedMatch,
                ReportedFile = finding.File,
                ReportedLineStart = finding.LineStart,
                ReportedLineEnd = finding.LineEnd,
                ReportedSymbol = finding.FunctionOrSymbol,
                ReportedSeverity = finding.Severity,
                ReportedEvidence = finding.Evidence,
                AcceptedEvidenceAnchors = candidate.AcceptedEvidenceAnchors,
                MissingMustAnchors = candidate.MissingMustAnchors,
                RejectedBecause = candidate.RejectedBecause,
                MatchedVulnerabilityId = candidate.Vulnerability?.Id,
                MatchedVulnerabilityTitle = candidate.Vulnerability?.Title
            };

            if (candidate.Vulnerability is not null && candidate.Score >= profile.PartialThreshold)
            {
                var assignedFinding = assigned.TryGetValue(candidate.Vulnerability.Id, out var assignedCandidate) && ReferenceEquals(assignedCandidate, candidate);
                if (assignedFinding)
                {
                    score.Classification = candidate.Score >= profile.FullThreshold
                        ? FindingClassification.FullTruePositive
                        : FindingClassification.PartialTruePositive;
                    score.SeverityMismatch = IsSeverityMismatch(finding.Severity, candidate.Vulnerability.Severity);
                    score.Points = score.Classification == FindingClassification.FullTruePositive ? profile.Points.FullTp : profile.Points.PartialTp;
                    if (score.SeverityMismatch)
                    {
                        score.Points += profile.Points.SeverityMismatch;
                    }

                    score.Reason = score.SeverityMismatch
                        ? $"Matched {candidate.Vulnerability.Id}, but severity differs ({finding.Severity} vs {candidate.Vulnerability.Severity})."
                        : $"Matched {candidate.Vulnerability.Id}.";
                }
                else
                {
                    score.Classification = FindingClassification.Duplicate;
                    score.Duplicate = true;
                    score.Points = profile.Points.Duplicate;
                    score.Reason = $"Duplicate report for {candidate.Vulnerability.Id}; another finding had the stronger match.";
                }
            }
            else if (finding.Confidence < 0.35 && candidate.Score < profile.PartialThreshold)
            {
                score.Classification = FindingClassification.IgnoredLowConfidence;
                score.Points = profile.Points.LowConfidenceFalsePositive;
                score.MatchedVulnerabilityId = null;
                score.MatchedVulnerabilityTitle = null;
                score.Reason = profile.Points.LowConfidenceFalsePositive == 0
                    ? "Ignored because confidence is low and no ground-truth item matched."
                    : $"Low-confidence unmatched finding: {profile.Points.LowConfidenceFalsePositive:0.##} (half the false-positive penalty).";
            }
            else
            {
                score.Classification = FindingClassification.FalsePositive;
                score.Points = profile.Points.FalsePositive;
                score.FalsePositiveCategory = ClassifyFalsePositive(finding, candidate, source);
                score.MatchedVulnerabilityId = null;
                score.MatchedVulnerabilityTitle = null;
                score.Reason = $"No sufficient match in hidden ground truth. FP category: {score.FalsePositiveCategory}.";
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
                Cwe = v.Cwe.ToList(),
                Found = matchingScore is not null,
                Partial = matchingScore?.Classification == FindingClassification.PartialTruePositive,
                FindingIndex = matchingScore?.FindingIndex,
                MatchScore = matchingScore?.MatchScore ?? 0,
                EvidenceFidelity = matchingScore?.EvidenceFidelity ?? 0,
                LocationAccuracy = matchingScore?.LocationAccuracy ?? 0,
                Category = v.Category,
                Module = v.Module,
                Exploitability = v.Exploitability,
                Difficulty = v.Difficulty
            };
        }).ToList();

        var fullTp = findingScores.Count(f => f.Classification == FindingClassification.FullTruePositive);
        var partialTp = findingScores.Count(f => f.Classification == FindingClassification.PartialTruePositive);
        var falsePositives = findingScores.Count(f => f.Classification == FindingClassification.FalsePositive);
        var duplicates = findingScores.Count(f => f.Classification == FindingClassification.Duplicate);
        var ignored = findingScores.Count(f => f.Classification == FindingClassification.IgnoredLowConfidence);
        var missed = vulnerabilityScores.Count(v => !v.Found);
        var rawPoints = findingScores.Sum(f => f.Points);
        var maxPoints = scoreable.Count * profile.Points.FullTp;
        var scorePercent = maxPoints == 0 ? 0 : TextUtil.Clamp(rawPoints / maxPoints * 100.0, 0, 100);
        var weightedTp = fullTp + partialTp * 0.5;
        var precisionDenominator = fullTp + partialTp + falsePositives;
        var precision = precisionDenominator == 0 ? 0 : weightedTp / precisionDenominator;
        var recall = scoreable.Count == 0 ? 0 : weightedTp / scoreable.Count;
        var f1 = precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
        var assignedPositiveFindings = findingScores
            .Where(f => f.Classification is FindingClassification.FullTruePositive or FindingClassification.PartialTruePositive)
            .ToList();
        var evidenceFidelity = assignedPositiveFindings.Count == 0 ? 0 : assignedPositiveFindings.Average(f => f.EvidenceFidelity);
        var locationAccuracy = assignedPositiveFindings.Count == 0 ? 0 : assignedPositiveFindings.Average(f => f.LocationAccuracy);
        var reportedFindingDenominator = Math.Max(1, findingScores.Count(f => f.Classification != FindingClassification.IgnoredLowConfidence));
        var hallucinationRate = falsePositives / (double)reportedFindingDenominator;
        var duplicateRate = findings.Count == 0 ? 0 : duplicates / (double)findings.Count;
        var evaluationConfidence = CalculateEvaluationConfidence(findingScores, profile);
        var falsePositiveTaxonomy = findingScores
            .Where(f => f.Classification == FindingClassification.FalsePositive)
            .GroupBy(f => string.IsNullOrWhiteSpace(f.FalsePositiveCategory) ? "unsupported_by_code" : f.FalsePositiveCategory, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        return new ScoringResult
        {
            RunName = runName,
            ScoreSchemaVersion = ScoringProfiles.ScoreSchemaVersion,
            ScoringProfile = profile.Name,
            ScoringProfileVersion = profile.Version,
            ScoringEngineVersion = profile.EngineVersion,
            ParserVersion = context.ParserVersion,
            GroundTruthSha256 = context.GroundTruthSha256,
            SourceSha256 = context.SourceSha256,
            PromptVersion = context.PromptVersion,
            ComputedAt = context.ComputedAt,
            IsLegacyMigrated = context.IsLegacyMigrated,
            IsRescored = context.IsRescored,
            MaxPoints = Math.Round(maxPoints, 2),
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
            EvidenceFidelity = Math.Round(evidenceFidelity, 4),
            LocationAccuracy = Math.Round(locationAccuracy, 4),
            HallucinationRate = Math.Round(hallucinationRate, 4),
            DuplicateRate = Math.Round(duplicateRate, 4),
            EvaluationConfidence = Math.Round(evaluationConfidence, 4),
            FalsePositiveTaxonomy = falsePositiveTaxonomy,
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
        var falsePositiveMatches = MatchFalsePositives(run1, run2);
        var vulnerabilityChanges = BuildVulnerabilityChanges(run1, run2);
        var severityCorrected = vulnerabilityChanges.Count(c => c.Notes.Contains("severity corrected", StringComparison.OrdinalIgnoreCase));
        var evidenceImproved = vulnerabilityChanges.Count(c => c.Change == "evidence_improved" || c.Notes.Contains("evidence improved", StringComparison.OrdinalIgnoreCase));
        var evidenceDegraded = vulnerabilityChanges.Count(c => c.Change == "evidence_degraded" || c.Notes.Contains("evidence degraded", StringComparison.OrdinalIgnoreCase));
        var evidenceDelta = run2.EvidenceFidelity - run1.EvidenceFidelity;

        return new RunComparison
        {
            KeptTruePositiveIds = kept,
            DroppedTruePositiveIds = dropped,
            AddedTruePositiveIds = added,
            KeptFalsePositiveKeys = falsePositiveMatches.KeptKeys,
            DroppedFalsePositiveKeys = falsePositiveMatches.DroppedKeys,
            AddedFalsePositiveKeys = falsePositiveMatches.AddedKeys,
            Run1TruePositives = run1Ids.Count,
            Run2TruePositives = run2Ids.Count,
            Run1FalsePositives = run1.FalsePositives,
            Run2FalsePositives = run2.FalsePositives,
            KeptFalsePositives = falsePositiveMatches.KeptKeys.Count,
            DroppedFalsePositives = falsePositiveMatches.DroppedKeys.Count,
            AddedFalsePositives = falsePositiveMatches.AddedKeys.Count,
            FalsePositiveReduction = run1.FalsePositives - run2.FalsePositives,
            // Undefined rates stay null: "0 of 0" is neither 100 % reduction nor 0 % retention.
            FalsePositiveReductionRate = run1.FalsePositives == 0 ? null : Math.Round((run1.FalsePositives - run2.FalsePositives) / (double)run1.FalsePositives, 4),
            TruePositiveRetention = run1Ids.Count == 0 ? null : Math.Round((double)kept.Count / run1Ids.Count, 4),
            OverPruningRate = run1Ids.Count == 0 ? null : Math.Round((double)dropped.Count / run1Ids.Count, 4),
            EvidenceImprovementDelta = Math.Round(evidenceDelta, 4),
            ParseQualityDelta = null,
            SeverityCorrectedCount = severityCorrected,
            EvidenceImprovedCount = evidenceImproved,
            EvidenceDegradedCount = evidenceDegraded,
            VulnerabilityChanges = vulnerabilityChanges,
            FindingChanges = falsePositiveMatches.FindingChanges
        };
    }

    private static void ValidateProfile(ScoringProfile profile)
    {
        if (!double.IsFinite(profile.PartialThreshold)
            || !double.IsFinite(profile.FullThreshold)
            || profile.PartialThreshold is < 0 or > 1
            || profile.FullThreshold is < 0 or > 1
            || profile.PartialThreshold > profile.FullThreshold)
        {
            throw new ArgumentException("Scoring profile thresholds must be finite, ordered, and within 0..1.", nameof(profile));
        }

        if (profile.Weights is null
            || profile.Weights.Values.Any(weight => !double.IsFinite(weight) || weight < 0))
        {
            throw new ArgumentException("Scoring profile weights must be finite and non-negative.", nameof(profile));
        }

        var gates = profile.Gates
            ?? throw new ArgumentException("Scoring profile gates are required.", nameof(profile));
        if (!double.IsFinite(gates.AliasEvidenceMinimum)
            || !double.IsFinite(gates.MinimumAliasForTp)
            || !double.IsFinite(gates.GenericAliasOnlyCap)
            || gates.AliasEvidenceMinimum is < 0 or > 1
            || gates.MinimumAliasForTp is < 0 or > 1
            || gates.GenericAliasOnlyCap is < 0 or > 1)
        {
            throw new ArgumentException("Scoring profile gate thresholds must be finite and within 0..1.", nameof(profile));
        }

        var matching = profile.Matching
            ?? throw new ArgumentException("Scoring profile matching options are required.", nameof(profile));
        if (!double.IsFinite(matching.MinimumReportedLocation) || matching.MinimumReportedLocation is < 0 or > 1
            || !double.IsFinite(matching.AssignmentScoreMargin) || matching.AssignmentScoreMargin is < 0 or > 1
            || !double.IsFinite(matching.AlternativeMinimumLocation) || matching.AlternativeMinimumLocation is < 0 or > 1)
        {
            throw new ArgumentException("Scoring profile matching thresholds must be finite and within 0..1.", nameof(profile));
        }

        var points = profile.Points
            ?? throw new ArgumentException("Scoring profile point schedule is required.", nameof(profile));
        if (!double.IsFinite(points.FullTp) || points.FullTp <= 0
            || !double.IsFinite(points.PartialTp)
            || !double.IsFinite(points.FalsePositive)
            || !double.IsFinite(points.Duplicate)
            || !double.IsFinite(points.SeverityMismatch)
            || !double.IsFinite(points.LowConfidenceFalsePositive))
        {
            throw new ArgumentException("Scoring profile point values must be finite and full-TP points must be positive.", nameof(profile));
        }
    }

    private static double CalculateEvaluationConfidence(IReadOnlyList<FindingScore> findings, ScoringProfile profile)
    {
        if (findings.Count == 0)
        {
            return 1;
        }

        var clear = findings.Count(f =>
            Math.Abs(f.MatchScore - profile.PartialThreshold) > 0.05
            && Math.Abs(f.MatchScore - profile.FullThreshold) > 0.05);
        return clear / (double)findings.Count;
    }

    private static List<SelfValidationVulnerabilityChange> BuildVulnerabilityChanges(ScoringResult run1, ScoringResult run2)
    {
        var run1ById = run1.Vulnerabilities.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var run2ById = run2.Vulnerabilities.ToDictionary(v => v.Id, StringComparer.OrdinalIgnoreCase);
        var ids = run1ById.Keys.Concat(run2ById.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(id => id, StringComparer.OrdinalIgnoreCase);
        var changes = new List<SelfValidationVulnerabilityChange>();

        foreach (var id in ids)
        {
            run1ById.TryGetValue(id, out var before);
            run2ById.TryGetValue(id, out var after);
            var beforeStatus = VulnerabilityStatus(before);
            var afterStatus = VulnerabilityStatus(after);
            var evidenceDelta = (after?.EvidenceFidelity ?? 0) - (before?.EvidenceFidelity ?? 0);
            var locationDelta = (after?.LocationAccuracy ?? 0) - (before?.LocationAccuracy ?? 0);
            var notes = new List<string>();
            // Status changes outrank evidence changes: an upgrade usually comes with better evidence.
            var change = (beforeStatus, afterStatus) switch
            {
                ("partial", "full") => "upgraded_tp",
                ("full", "partial") => "downgraded_tp",
                ("full" or "partial", "missed") => "dropped_tp",
                ("missed", "full" or "partial") => "added_tp",
                ("full" or "partial", "full" or "partial") when evidenceDelta > 0.05 => "evidence_improved",
                ("full" or "partial", "full" or "partial") when evidenceDelta < -0.05 => "evidence_degraded",
                _ when string.Equals(beforeStatus, afterStatus, StringComparison.OrdinalIgnoreCase) => "unchanged",
                _ => "changed"
            };

            var beforeFinding = before?.FindingIndex is int beforeIndex
                ? run1.Findings.FirstOrDefault(f => f.FindingIndex == beforeIndex)
                : null;
            var afterFinding = after?.FindingIndex is int afterIndex
                ? run2.Findings.FirstOrDefault(f => f.FindingIndex == afterIndex)
                : null;
            // Dropping the severity is not a correction; the revised finding must state the right one.
            if (beforeFinding?.SeverityMismatch == true
                && afterFinding is { SeverityMismatch: false }
                && !string.IsNullOrWhiteSpace(afterFinding.ReportedSeverity)
                && !string.Equals(afterFinding.ReportedSeverity, "Unknown", StringComparison.OrdinalIgnoreCase))
            {
                notes.Add("severity corrected");
            }

            // Evidence/location of a finding only compare when both runs found the vulnerability;
            // an added or dropped TP is not an evidence improvement or degradation.
            var foundInBoth = beforeStatus != "missed" && afterStatus != "missed";
            if (foundInBoth && evidenceDelta > 0.05)
            {
                notes.Add("evidence improved");
            }
            else if (foundInBoth && evidenceDelta < -0.05)
            {
                notes.Add("evidence degraded");
            }

            if (foundInBoth && locationDelta > 0.05)
            {
                notes.Add("location improved");
            }
            else if (foundInBoth && locationDelta < -0.05)
            {
                notes.Add("location degraded");
            }

            changes.Add(new SelfValidationVulnerabilityChange
            {
                GroundTruthId = id,
                Run1Status = beforeStatus,
                Run2Status = afterStatus,
                Change = change,
                EvidenceDelta = Math.Round(evidenceDelta, 4),
                LocationDelta = Math.Round(locationDelta, 4),
                Notes = string.Join("; ", notes)
            });
        }

        return changes;
    }

    private static string VulnerabilityStatus(VulnerabilityScore? score)
    {
        if (score?.Found != true)
        {
            return "missed";
        }

        return score.Partial ? "partial" : "full";
    }

    private static FalsePositiveMatchResult MatchFalsePositives(ScoringResult run1, ScoringResult run2)
    {
        var before = run1.Findings
            .Where(f => f.Classification == FindingClassification.FalsePositive)
            .OrderBy(f => f.FindingIndex)
            .ToList();
        var after = run2.Findings
            .Where(f => f.Classification == FindingClassification.FalsePositive)
            .OrderBy(f => f.FindingIndex)
            .ToList();
        var usedAfter = new HashSet<int>();
        var kept = new List<string>();
        var dropped = new List<string>();
        var added = new List<string>();
        var changes = new List<SelfValidationFindingChange>();

        // Optimal one-to-one pairing (a greedy pass in index order can pair a Run-1 FP with the
        // only good partner of a later one and under-count kept FPs).
        var similarity = new double[before.Count, after.Count];
        for (var i = 0; i < before.Count; i++)
        {
            for (var j = 0; j < after.Count; j++)
            {
                var value = FindingSimilarity(before[i], after[j]);
                similarity[i, j] = value >= 0.45 ? value : 0;
            }
        }

        var pairing = before.Count == 0 || after.Count == 0
            ? Enumerable.Repeat(-1, before.Count).ToArray()
            : HungarianMaximize(similarity, before.Count, after.Count);

        for (var leftIndex = 0; leftIndex < before.Count; leftIndex++)
        {
            var left = before[leftIndex];
            var partner = pairing[leftIndex];
            var best = partner >= 0 && similarity[leftIndex, partner] > 0
                ? new { Finding = after[partner], Score = similarity[leftIndex, partner] }
                : null;

            if (best is not null && best.Score >= 0.45)
            {
                usedAfter.Add(best.Finding.FindingIndex);
                var key = FindingKey(best.Finding);
                kept.Add(key);
                changes.Add(new SelfValidationFindingChange
                {
                    FindingKey = key,
                    Run1FindingIndex = left.FindingIndex,
                    Run2FindingIndex = best.Finding.FindingIndex,
                    Run1Classification = left.Classification.ToString(),
                    Run2Classification = best.Finding.Classification.ToString(),
                    Change = "kept_fp",
                    FalsePositiveCategory = string.IsNullOrWhiteSpace(best.Finding.FalsePositiveCategory) ? left.FalsePositiveCategory : best.Finding.FalsePositiveCategory,
                    EvidenceDelta = Math.Round(best.Finding.EvidenceFidelity - left.EvidenceFidelity, 4),
                    Notes = $"similarity={best.Score:0.00}"
                });
            }
            else
            {
                var key = FindingKey(left);
                dropped.Add(key);
                changes.Add(new SelfValidationFindingChange
                {
                    FindingKey = key,
                    Run1FindingIndex = left.FindingIndex,
                    Run1Classification = left.Classification.ToString(),
                    Change = "dropped_fp",
                    FalsePositiveCategory = left.FalsePositiveCategory,
                    EvidenceDelta = Math.Round(-left.EvidenceFidelity, 4),
                    Notes = "Run 2 removed this unsupported finding."
                });
            }
        }

        foreach (var right in after.Where(f => !usedAfter.Contains(f.FindingIndex)))
        {
            var key = FindingKey(right);
            added.Add(key);
            changes.Add(new SelfValidationFindingChange
            {
                FindingKey = key,
                Run2FindingIndex = right.FindingIndex,
                Run2Classification = right.Classification.ToString(),
                Change = "added_fp",
                FalsePositiveCategory = right.FalsePositiveCategory,
                EvidenceDelta = Math.Round(right.EvidenceFidelity, 4),
                Notes = "Run 2 introduced this unsupported finding."
            });
        }

        return new FalsePositiveMatchResult(kept, dropped, added, changes);
    }

    private static double FindingSimilarity(FindingScore left, FindingScore right)
    {
        var title = TextUtil.TokenOverlap(left.FindingTitle, right.FindingTitle);
        var category = !string.IsNullOrWhiteSpace(left.FalsePositiveCategory)
                       && string.Equals(left.FalsePositiveCategory, right.FalsePositiveCategory, StringComparison.OrdinalIgnoreCase)
            ? 0.25
            : 0;
        var symbol = !string.IsNullOrWhiteSpace(left.ReportedSymbol)
                     && TextUtil.ContainsNormalized(right.ReportedSymbol + " " + right.FindingTitle, TextUtil.SymbolLeaf(left.ReportedSymbol))
            ? 0.25
            : 0;
        var file = !string.IsNullOrWhiteSpace(left.ReportedFile)
                   && !string.IsNullOrWhiteSpace(right.ReportedFile)
                   && string.Equals(Path.GetFileName(left.ReportedFile), Path.GetFileName(right.ReportedFile), StringComparison.OrdinalIgnoreCase)
            ? 0.15
            : 0;
        return TextUtil.Clamp01(title * 0.60 + category + symbol + file);
    }

    private static string FindingKey(FindingScore finding)
    {
        var category = string.IsNullOrWhiteSpace(finding.FalsePositiveCategory) ? "fp" : finding.FalsePositiveCategory;
        var title = TextUtil.Normalize(finding.FindingTitle);
        if (title.Length > 52)
        {
            title = title[..52];
        }

        return string.IsNullOrWhiteSpace(title) ? $"{category}#{finding.FindingIndex}" : $"{category}:{title}";
    }

    private static string ClassifyFalsePositive(LlmFinding finding, MatchCandidate candidate, SourceDocument source)
    {
        var combined = CombinedFindingText(finding);
        var normalized = TextUtil.Normalize(combined);
        var aliasScore = SignalValue(candidate, "type_alias");
        var locationScore = candidate.LocationAccuracy;
        var evidenceScore = candidate.EvidenceFidelity;

        if (ContainsAny(normalized, ["style", "readability", "refactor", "maintainability", "performance only", "code quality", "naming"]))
        {
            return "non_security";
        }

        if (HasHallucinatedApi(finding, source))
        {
            return "hallucinated_api";
        }

        if (candidate.Vulnerability is not null && candidate.Score >= 0.35 && aliasScore >= 0.45 && evidenceScore >= 0.20)
        {
            return "duplicate_variant";
        }

        if (aliasScore >= 0.45 && locationScore < 0.25 && (!string.IsNullOrWhiteSpace(finding.File) || finding.LineStart > 0 || !string.IsNullOrWhiteSpace(finding.FunctionOrSymbol)))
        {
            return "wrong_location";
        }

        if (evidenceScore <= 0 && locationScore <= 0.15 && ContainsAny(normalized, ["unsafe", "input validation", "could", "might", "potential", "generic", "all user input"]))
        {
            return "overgeneralized";
        }

        return evidenceScore <= 0 ? "unsupported_by_code" : "overgeneralized";
    }

    private static double SignalValue(MatchCandidate candidate, string signalName) => candidate.Signals
        .FirstOrDefault(s => string.Equals(s.Name, signalName, StringComparison.OrdinalIgnoreCase))?.Value ?? 0;

    private static bool ContainsAny(string normalizedText, IReadOnlyList<string> needles)
        => needles.Any(needle => normalizedText.Contains(TextUtil.Normalize(needle), StringComparison.Ordinal));

    private static bool HasHallucinatedApi(LlmFinding finding, SourceDocument source)
    {
        var reportedSymbol = TextUtil.SymbolLeaf(finding.FunctionOrSymbol);
        if (!string.IsNullOrWhiteSpace(reportedSymbol)
            && reportedSymbol.Length >= 4
            && !SourceContainsIdentifier(source.Text, reportedSymbol))
        {
            return true;
        }

        var text = string.Join(' ', finding.Title, finding.VulnerabilityType, finding.Evidence, finding.Impact, finding.Trigger, finding.Fix);
        foreach (var identifier in ExtractInterestingIdentifiers(text))
        {
            if (!SourceContainsIdentifier(source.Text, identifier))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ExtractInterestingIdentifiers(string text)
    {
        foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(text ?? string.Empty, @"\b[A-Za-z_][A-Za-z0-9_:]{3,}\s*\("))
        {
            var identifier = match.Value.Trim().TrimEnd('(').Trim();
            identifier = TextUtil.SymbolLeaf(identifier);
            if (identifier.Length >= 4 && !CommonFunctionWords.Contains(identifier))
            {
                yield return identifier;
            }
        }
    }

    private static bool SourceContainsIdentifier(string sourceText, string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return true;
        }

        return System.Text.RegularExpressions.Regex.IsMatch(sourceText ?? string.Empty, $@"\b{System.Text.RegularExpressions.Regex.Escape(identifier)}\b", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }

    private static readonly HashSet<string> CommonFunctionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "input", "output", "string", "buffer", "memory", "printf", "scanf", "could", "would", "should", "function"
    };

    private sealed record FalsePositiveMatchResult(
        List<string> KeptKeys,
        List<string> DroppedKeys,
        List<string> AddedKeys,
        List<SelfValidationFindingChange> FindingChanges);

    private static Dictionary<string, MatchCandidate> AssignCandidates(List<MatchCandidate> candidates, ScoringProfile profile)
    {
        var assigned = new Dictionary<string, MatchCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates
                     .Where(c => c.Vulnerability is not null && c.Score >= profile.PartialThreshold)
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

    private static MatchCandidate PickBestCandidate(LlmFinding finding, IReadOnlyList<MatchCandidate> candidates)
    {
        MatchCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (best is null || candidate.Score > best.Score)
            {
                best = candidate;
            }
        }

        return best ?? new MatchCandidate(finding, null, 0, [], 0, 0, false, false, [], [], []);
    }

    /// <summary>
    /// Globally optimal one-to-one assignment (Hungarian method). Lexicographic objective:
    /// most credited vulnerabilities, then most full matches, then the highest match scores,
    /// then reported confidence. Assigned findings take the candidate of their assigned
    /// vulnerability, so a finding whose best vulnerability is already credited can still be
    /// credited for another vulnerability it matches instead of becoming a duplicate.
    /// </summary>
    private static Dictionary<string, MatchCandidate> AssignOptimally(
        IReadOnlyList<List<MatchCandidate>> candidateSets,
        List<MatchCandidate> bestCandidates,
        ScoringProfile profile)
    {
        var assigned = new Dictionary<string, MatchCandidate>(StringComparer.OrdinalIgnoreCase);
        var rows = candidateSets.Count;
        var columns = rows == 0 ? 0 : candidateSets.Max(set => set.Count);
        if (rows == 0 || columns == 0)
        {
            return assigned;
        }

        var weights = new double[rows, columns];
        for (var i = 0; i < rows; i++)
        {
            var best = bestCandidates[i];
            for (var j = 0; j < candidateSets[i].Count; j++)
            {
                var candidate = candidateSets[i][j];
                if (candidate.Vulnerability is null
                    || candidate.Score < profile.PartialThreshold
                    || !IsAllowedAlternative(candidate, best, profile.Matching))
                {
                    continue;
                }

                weights[i, j] = 10.0
                                + (candidate.Score >= profile.FullThreshold ? 5.0 : 0.0)
                                + candidate.Score
                                + TextUtil.Clamp01(candidate.Finding.Confidence) * 1e-6;
            }
        }

        var assignment = HungarianMaximize(weights, rows, columns);
        for (var i = 0; i < rows; i++)
        {
            var j = assignment[i];
            if (j < 0 || j >= candidateSets[i].Count || weights[i, j] <= 0)
            {
                continue;
            }

            var candidate = candidateSets[i][j];
            bestCandidates[i] = candidate;
            assigned[candidate.Vulnerability!.Id] = candidate;
        }

        return assigned;
    }

    private static bool IsAllowedAlternative(MatchCandidate candidate, MatchCandidate best, ScoringMatchingOptions matching)
    {
        if (ReferenceEquals(candidate, best) || ReferenceEquals(candidate.Vulnerability, best.Vulnerability))
        {
            return true;
        }

        if (matching.AssignmentScoreMargin > 0 && candidate.Score < best.Score - matching.AssignmentScoreMargin)
        {
            return false;
        }

        return matching.AlternativeMinimumLocation <= 0
               || (candidate.LocationAccuracy >= matching.AlternativeMinimumLocation
                   && candidate.LocationAccuracy > best.LocationAccuracy);
    }

    /// <summary>Maximum-weight assignment of rows to columns; -1 for rows left unassigned.</summary>
    private static int[] HungarianMaximize(double[,] weights, int rows, int columns)
    {
        var n = Math.Max(rows, columns);
        var max = 0.0;
        foreach (var weight in weights)
        {
            max = Math.Max(max, weight);
        }

        // Square cost matrix (1-based, classic O(n^3) potentials formulation).
        var cost = new double[n + 1, n + 1];
        for (var i = 1; i <= n; i++)
        {
            for (var j = 1; j <= n; j++)
            {
                var weight = i <= rows && j <= columns ? weights[i - 1, j - 1] : 0.0;
                cost[i, j] = max - weight;
            }
        }

        var u = new double[n + 1];
        var v = new double[n + 1];
        var p = new int[n + 1];
        var way = new int[n + 1];
        for (var i = 1; i <= n; i++)
        {
            p[0] = i;
            var j0 = 0;
            var minv = Enumerable.Repeat(double.PositiveInfinity, n + 1).ToArray();
            var used = new bool[n + 1];
            do
            {
                used[j0] = true;
                var i0 = p[j0];
                var delta = double.PositiveInfinity;
                var j1 = 0;
                for (var j = 1; j <= n; j++)
                {
                    if (used[j])
                    {
                        continue;
                    }

                    var current = cost[i0, j] - u[i0] - v[j];
                    if (current < minv[j])
                    {
                        minv[j] = current;
                        way[j] = j0;
                    }

                    if (minv[j] < delta)
                    {
                        delta = minv[j];
                        j1 = j;
                    }
                }

                for (var j = 0; j <= n; j++)
                {
                    if (used[j])
                    {
                        u[p[j]] += delta;
                        v[j] -= delta;
                    }
                    else
                    {
                        minv[j] -= delta;
                    }
                }

                j0 = j1;
            }
            while (p[j0] != 0);

            do
            {
                var j1 = way[j0];
                p[j0] = p[j1];
                j0 = j1;
            }
            while (j0 != 0);
        }

        var result = Enumerable.Repeat(-1, rows).ToArray();
        for (var j = 1; j <= n; j++)
        {
            var row = p[j] - 1;
            var column = j - 1;
            if (row >= 0 && row < rows && column < columns)
            {
                result[row] = column;
            }
        }

        return result;
    }

    private static MatchCandidate ScoreCandidate(LlmFinding finding, VulnerabilityDefinition vulnerability, SourceDocument source, ScoringProfile profile)
    {
        var matching = profile.Matching;
        var alias = ScoreAlias(finding, vulnerability, matching);
        var location = ScoreLocation(finding, vulnerability, matching);
        var evidence = ScoreEvidence(finding, vulnerability, source, matching);
        var cweSeverity = ScoreCweSeverity(finding, vulnerability, matching);
        var impact = ScoreImpact(finding, vulnerability, matching);

        var signals = new List<SignalScore>
        {
            new() { Name = "type_alias", Weight = profile.Weight("type_alias"), Value = alias.Score, Detail = alias.Detail },
            new() { Name = "location", Weight = profile.Weight("location"), Value = location.Score, Detail = location.Detail },
            new() { Name = "evidence", Weight = profile.Weight("evidence"), Value = evidence.Score, Detail = evidence.Detail },
            new() { Name = "cwe_severity", Weight = profile.Weight("cwe_severity"), Value = cweSeverity.Score, Detail = cweSeverity.Detail },
            new() { Name = "impact_trigger", Weight = profile.Weight("impact_trigger"), Value = impact.Score, Detail = impact.Detail }
        };

        var score = signals.Sum(s => s.Weighted);
        var gateDetail = ApplyProfileGates(profile, alias.Score, location.Score, evidence.Score, ref score);
        if (matching.MinimumReportedLocation > 0
            && ReportsLocation(finding)
            && location.Score < matching.MinimumReportedLocation)
        {
            score = Math.Min(score, profile.PartialThreshold - 0.0001);
            gateDetail = string.Join("; ", new[] { gateDetail, $"blocked TP: reported location does not point at this vulnerability (location {location.Score:0.00})" }.Where(d => !string.IsNullOrWhiteSpace(d)));
        }
        if (!string.IsNullOrWhiteSpace(gateDetail))
        {
            signals.Add(new SignalScore { Name = "gate", Weight = 0, Value = score, Detail = gateDetail });
        }

        return new MatchCandidate(
            finding,
            vulnerability,
            Math.Round(score, 4),
            signals,
            Math.Round(evidence.Score, 4),
            Math.Round(location.Score, 4),
            evidence.ExactMatch,
            evidence.NormalizedMatch,
            evidence.AcceptedAnchors,
            evidence.MissingMustAnchors,
            evidence.RejectedBecause);
    }

    private static bool ReportsLocation(LlmFinding finding)
        => finding.LineStart > 0 || !string.IsNullOrWhiteSpace(finding.FunctionOrSymbol);

    /// <summary>Frozen profiles match raw normalized substrings; opted-in profiles match whole terms.</summary>
    private static bool Contains(ScoringMatchingOptions matching, string? haystack, string? needle)
        => matching.WordBoundaryTerms
            ? TextUtil.ContainsTerm(haystack, needle)
            : TextUtil.ContainsNormalized(haystack, needle);

    private static string ApplyProfileGates(ScoringProfile profile, double aliasScore, double locationScore, double evidenceScore, ref double score)
    {
        var details = new List<string>();
        if (profile.Gates.RequireEvidenceOrLocation && locationScore <= 0 && evidenceScore <= 0)
        {
            score = Math.Min(score, profile.PartialThreshold - 0.0001);
            details.Add("blocked TP: no location or evidence signal");
        }

        if (profile.Gates.CapGenericAliasOnly && aliasScore < profile.Gates.MinimumAliasForTp && evidenceScore < profile.Gates.AliasEvidenceMinimum)
        {
            score = Math.Min(score, profile.PartialThreshold - 0.0001);
            details.Add($"blocked TP: alias {aliasScore:0.00} and evidence {evidenceScore:0.00} below v2 minima");
        }

        if (profile.Gates.CapGenericAliasOnly && aliasScore > 0 && locationScore <= 0.15 && evidenceScore <= 0)
        {
            score = Math.Min(score, profile.Gates.GenericAliasOnlyCap);
            details.Add("capped generic alias-only match without accepted evidence");
        }

        return string.Join("; ", details);
    }

    private static (double Score, string Detail) ScoreAlias(LlmFinding finding, VulnerabilityDefinition vulnerability, ScoringMatchingOptions matching)
    {
        var combined = CombinedFindingText(finding);
        var exactAliases = vulnerability.Aliases
            .Where(alias => Contains(matching, combined, alias))
            .ToList();

        if (exactAliases.Count > 0)
        {
            return (1.0, $"Alias matched: {string.Join(", ", exactAliases.Take(3))}");
        }

        var cweAliases = vulnerability.Cwe.Where(cwe => Contains(matching, combined, cwe)).ToList();
        if (cweAliases.Count > 0)
        {
            return (0.85, $"CWE alias matched: {string.Join(", ", cweAliases)}");
        }

        var overlap = Math.Max(
            TextUtil.TokenOverlap(finding.Title, vulnerability.Title),
            TextUtil.TokenOverlap(finding.VulnerabilityType, vulnerability.Title));

        return (overlap >= 0.5 ? 0.65 : overlap * 0.8, overlap > 0 ? $"Title/type token overlap {overlap:0.00}." : "No type/alias overlap.");
    }

    private static (double Score, string Detail) ScoreLocation(LlmFinding finding, VulnerabilityDefinition vulnerability, ScoringMatchingOptions matching)
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

            var symbolScore = SymbolScore(finding.FunctionOrSymbol, location.Symbol, finding.Title + " " + finding.Evidence, matching);
            score += 0.35 * symbolScore;
            if (symbolScore > 0)
            {
                details.Add($"symbol {location.Symbol}={symbolScore:0.00}");
            }

            var lineScore = LineOverlapScore(finding.LineStart, finding.LineEnd, location.LineStart, location.LineEnd);
            if (matching.MaxPreciseLineSpan > 0 && ReportedSpan(finding) > matching.MaxPreciseLineSpan)
            {
                lineScore *= 0.5;
            }
            score += 0.50 * lineScore;
            if (lineScore > 0)
            {
                details.Add($"lines {finding.LineStart}-{finding.LineEnd} overlap {location.LineStart}-{location.LineEnd}={lineScore:0.00}");
            }

            best = Math.Max(best, TextUtil.Clamp01(score));
        }

        return (best, details.Count > 0 ? string.Join("; ", details.Take(4)) : "No location signal.");
    }

    private static double SymbolScore(string reportedSymbol, string groundTruthSymbol, string fallbackText, ScoringMatchingOptions matching)
    {
        if (string.IsNullOrWhiteSpace(groundTruthSymbol))
        {
            return 0;
        }

        var symbolLeaf = TextUtil.SymbolLeaf(groundTruthSymbol);
        if (Contains(matching, reportedSymbol, groundTruthSymbol) || Contains(matching, reportedSymbol, symbolLeaf))
        {
            return 1.0;
        }

        if (Contains(matching, fallbackText, symbolLeaf))
        {
            return 0.7;
        }

        var overlap = TextUtil.TokenOverlap(reportedSymbol, groundTruthSymbol);
        return overlap >= 0.5 ? 0.6 : 0;
    }

    private static int ReportedSpan(LlmFinding finding)
        => finding.LineStart <= 0 ? 0 : Math.Abs((finding.LineEnd <= 0 ? finding.LineStart : finding.LineEnd) - finding.LineStart) + 1;

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

    private static EvidenceScoreDetail ScoreEvidence(LlmFinding finding, VulnerabilityDefinition vulnerability, SourceDocument source, ScoringMatchingOptions matching)
    {
        var combined = CombinedFindingText(finding);
        var anchors = vulnerability.EvidenceAnchors.HasAny
            ? vulnerability.EvidenceAnchors
            : new EvidenceAnchorSet { Must = vulnerability.RequiredEvidence.ToList() };
        var positiveAnchors = anchors.Positive;
        var acceptedAnchors = positiveAnchors
            .Where(evidence => Contains(matching, combined, evidence))
            .ToList();
        var missingMustAnchors = anchors.Must
            .Where(evidence => !string.IsNullOrWhiteSpace(evidence) && !Contains(matching, combined, evidence))
            .ToList();
        var negativeHits = anchors.Negative
            .Where(evidence => !string.IsNullOrWhiteSpace(evidence) && Contains(matching, combined, evidence))
            .ToList();

        var quotedEvidence = matching.NormalizeQuotedEvidence ? NormalizeQuotedEvidence(finding.Evidence) : finding.Evidence;
        var sourceEvidence = EvidenceAppearsInSource(quotedEvidence, source.Text);
        var sourceEvidenceScore = sourceEvidence.AnyMatch ? 0.35 : 0;
        var requiredScore = positiveAnchors.Count == 0
            ? 0
            : Math.Min(0.65, 0.65 * acceptedAnchors.Count / Math.Min(3, positiveAnchors.Count));

        var score = TextUtil.Clamp01(sourceEvidenceScore + requiredScore);
        var details = new List<string>();
        if (sourceEvidence.AnyMatch)
        {
            details.Add(sourceEvidence.ExactMatch ? "quoted evidence appears exactly in source" : "quoted evidence appears normalized in source");
        }

        if (acceptedAnchors.Count > 0)
        {
            details.Add($"accepted anchors: {string.Join(", ", acceptedAnchors.Take(3))}");
        }

        if (missingMustAnchors.Count > 0)
        {
            details.Add($"missing must anchors: {string.Join(", ", missingMustAnchors.Take(3))}");
        }

        if (negativeHits.Count > 0)
        {
            score = Math.Min(score, 0.50);
            details.Add($"negative anchors: {string.Join(", ", negativeHits.Take(3))}");
        }

        return new EvidenceScoreDetail(
            score,
            details.Count > 0 ? string.Join("; ", details) : "No accepted evidence anchor.",
            acceptedAnchors,
            missingMustAnchors,
            negativeHits.Select(hit => $"negative_anchor:{hit}").ToList(),
            sourceEvidence.ExactMatch,
            sourceEvidence.NormalizedMatch);
    }

    /// <summary>
    /// Double-escaped JSON leaves a literal "\n" in quoted code, and the prompt shows the source
    /// as "0317: code", a prefix models copy although it is not part of the file. Both are undone
    /// before the quote is looked up in the source.
    /// </summary>
    internal static string NormalizeQuotedEvidence(string? evidence)
    {
        if (string.IsNullOrEmpty(evidence))
        {
            return evidence ?? string.Empty;
        }

        var unescaped = evidence
            .Replace(@"\r\n", "\n", StringComparison.Ordinal)
            .Replace(@"\n", "\n", StringComparison.Ordinal)
            .Replace(@"\r", "\n", StringComparison.Ordinal)
            .Replace(@"\t", "\t", StringComparison.Ordinal);
        return LineNumberPrefixRegex.Replace(unescaped, string.Empty);
    }

    private static readonly System.Text.RegularExpressions.Regex LineNumberPrefixRegex = new(
        @"(?im)^[ \t]*(?:line[ \t]*|l)?\d{1,5}[ \t]*[:|][ \t]?",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static EvidenceSourceMatch EvidenceAppearsInSource(string evidence, string sourceText)
    {
        if (string.IsNullOrWhiteSpace(evidence))
        {
            return new EvidenceSourceMatch(false, false);
        }

        var snippets = evidence.Split(['\n', '\r', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.Trim('`', ' ', '\t'))
            .Where(s => s.Length >= 8)
            .Take(10)
            .ToList();

        var exact = snippets.Any(snippet => sourceText.Contains(snippet, StringComparison.Ordinal));
        if (exact)
        {
            return new EvidenceSourceMatch(true, false);
        }

        var normalizedSource = TextUtil.Normalize(sourceText);
        var normalized = snippets.Any(snippet => TextUtil.Normalize(snippet).Length >= 8 && normalizedSource.Contains(TextUtil.Normalize(snippet), StringComparison.Ordinal));
        return new EvidenceSourceMatch(false, normalized);
    }

    private static (double Score, string Detail) ScoreCweSeverity(LlmFinding finding, VulnerabilityDefinition vulnerability, ScoringMatchingOptions matching)
    {
        var cweMatches = vulnerability.Cwe.Where(cwe => Contains(matching, finding.Cwe + " " + CombinedFindingText(finding), cwe)).ToList();
        var cweScore = cweMatches.Count > 0 ? 0.65 : 0;
        var severityScore = IsSeverityMismatch(finding.Severity, vulnerability.Severity) ? 0 : 0.35;

        if (string.IsNullOrWhiteSpace(finding.Severity) || string.Equals(finding.Severity, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            severityScore = 0.1;
        }

        var detail = $"CWE {(cweMatches.Count > 0 ? string.Join(", ", cweMatches) : "not matched")}; severity {finding.Severity}/{vulnerability.Severity}.";
        return (TextUtil.Clamp01(cweScore + severityScore), detail);
    }

    private static (double Score, string Detail) ScoreImpact(LlmFinding finding, VulnerabilityDefinition vulnerability, ScoringMatchingOptions matching)
    {
        var impactText = string.Join(' ', finding.Impact, finding.Trigger, finding.Title, finding.VulnerabilityType);
        if (!string.IsNullOrWhiteSpace(vulnerability.Trigger) && Contains(matching, impactText, vulnerability.Trigger))
        {
            return (1.0, "Trigger matched.");
        }

        var aliasMatches = vulnerability.Aliases.Count(alias => Contains(matching, impactText, alias));
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

    internal static bool IsSeverityMismatch(string reported, string expected)
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

    private sealed record EvidenceSourceMatch(bool ExactMatch, bool NormalizedMatch)
    {
        public bool AnyMatch => ExactMatch || NormalizedMatch;
    }

    private sealed record EvidenceScoreDetail(
        double Score,
        string Detail,
        List<string> AcceptedAnchors,
        List<string> MissingMustAnchors,
        List<string> RejectedBecause,
        bool ExactMatch,
        bool NormalizedMatch);

    private sealed record MatchCandidate(
        LlmFinding Finding,
        VulnerabilityDefinition? Vulnerability,
        double Score,
        List<SignalScore> Signals,
        double EvidenceFidelity,
        double LocationAccuracy,
        bool EvidenceExactMatch,
        bool EvidenceNormalizedMatch,
        List<string> AcceptedEvidenceAnchors,
        List<string> MissingMustAnchors,
        List<string> RejectedBecause);
}
