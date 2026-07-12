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
    public const int CurrentSchemaVersion = 3;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("recordId")]
    public string RecordId { get; set; } = string.Empty;

    [JsonPropertyName("benchmarkId")]
    public string BenchmarkId { get; set; } = string.Empty;

    /// <summary>official/debug/fixture. Used only for post-run filtering; never sent to a model.</summary>
    [JsonPropertyName("benchmarkProfile")]
    public string BenchmarkProfile { get; set; } = "official";

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

    [JsonPropertyName("serverUrlHash")]
    public string ServerUrlHash { get; set; } = string.Empty;

    [JsonPropertyName("serverLabel")]
    public string ServerLabel { get; set; } = string.Empty;

    [JsonPropertyName("serverContextSize")]
    public int? ServerContextSize { get; set; }

    [JsonPropertyName("maxTokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("timeoutSeconds")]
    public int TimeoutSeconds { get; set; }

    [JsonPropertyName("seed")]
    public int? Seed { get; set; }

    [JsonPropertyName("repeatGroupId")]
    public string RepeatGroupId { get; set; } = string.Empty;

    [JsonPropertyName("repeatIndex")]
    public int RepeatIndex { get; set; } = 1;

    [JsonPropertyName("repeatCount")]
    public int RepeatCount { get; set; } = 1;

    [JsonPropertyName("skipResponseFormat")]
    public bool SkipResponseFormat { get; set; }

    [JsonPropertyName("disableThinking")]
    public bool DisableThinking { get; set; }

    [JsonPropertyName("abortOnLoop")]
    public bool AbortOnLoop { get; set; }

    [JsonPropertyName("sourceFile")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = string.Empty;

    [JsonPropertyName("expectedSourceSha256")]
    public string ExpectedSourceSha256 { get; set; } = string.Empty;

    [JsonPropertyName("sourceHashMatches")]
    public bool SourceHashMatches { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset CompletedAt { get; set; }

    [JsonPropertyName("durationMs")]
    public long DurationMs { get; set; }

    [JsonPropertyName("modelMetadata")]
    public ArchiveModelMetadata ModelMetadata { get; set; } = new();

    [JsonPropertyName("serverMetadata")]
    public ArchiveServerMetadata ServerMetadata { get; set; } = new();

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

    /// <summary>Parallel score-version index used by migration/rescoring workflows.</summary>
    [JsonPropertyName("scoreVersions")]
    public List<ArchiveScoreVersion> ScoreVersions { get; set; } = [];

    [JsonPropertyName("defaultDetectionProfile")]
    public string DefaultDetectionProfile { get; set; } = ScoringProfiles.OfficialV1Name;

    [JsonPropertyName("availableDetectionProfiles")]
    public List<string> AvailableDetectionProfiles { get; set; } = [ScoringProfiles.OfficialV1Name];

    [JsonPropertyName("legacyMigration")]
    public ArchiveLegacyMigration? LegacyMigration { get; set; }

    /// <summary>
    /// The run used as the headline result in comparisons. Prefers Run 2 (self-validation) when
    /// it produced a usable result; otherwise falls back to Run 1 (blind analysis). Runs flagged
    /// <see cref="ArchiveRunScore.IsDegenerate"/> (manually aborted, looped, or with no final
    /// assistant output) are skipped so an aborted run is never reported as a 0% detection score.
    /// Truth-audit runs (Run 3) never headline because they are non-blind and must not affect
    /// detection scores. If every detection run is degenerate the most recent detection run is
    /// still returned rather than silently dropping the record.
    /// </summary>
    [JsonIgnore]
    public ArchiveRunScore? PrimaryRun =>
        Runs.LastOrDefault(IsHeadlineEligible)
        ?? Runs.LastOrDefault(IsDetectionRun)
        ?? Runs.LastOrDefault();

    private static bool IsDetectionRun(ArchiveRunScore run) =>
        !string.Equals(run.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase);

    private static bool IsHeadlineEligible(ArchiveRunScore run) =>
        IsDetectionRun(run) && !run.IsDegenerate;
}

public sealed class ArchiveModelMetadata
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("quant")]
    public string Quant { get; set; } = string.Empty;

    [JsonPropertyName("paramsB")]
    public double? ParamsB { get; set; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; set; }

    [JsonPropertyName("ggufFile")]
    public string? GgufFile { get; set; }

    [JsonPropertyName("ggufSizeBytes")]
    public long? GgufSizeBytes { get; set; }

    [JsonPropertyName("quantBits")]
    public double? QuantBits { get; set; }
}

public sealed class ArchiveServerMetadata
{
    [JsonPropertyName("serverContextSize")]
    public int? ServerContextSize { get; set; }

    [JsonPropertyName("llamaBuild")]
    public string? LlamaBuild { get; set; }

    [JsonPropertyName("backend")]
    public string? Backend { get; set; }

    [JsonPropertyName("gpuLayers")]
    public int? GpuLayers { get; set; }

    [JsonPropertyName("threads")]
    public int? Threads { get; set; }

    [JsonPropertyName("batchSize")]
    public int? BatchSize { get; set; }
}

public sealed class ArchiveLegacyMigration
{
    [JsonPropertyName("isLegacyMigrated")]
    public bool IsLegacyMigrated { get; set; }

    [JsonPropertyName("migratedAt")]
    public DateTimeOffset MigratedAt { get; set; }

    [JsonPropertyName("assumedProfile")]
    public string AssumedProfile { get; set; } = string.Empty;
}

public sealed class ArchiveScoreVersion
{
    [JsonPropertyName("profile")]
    public string Profile { get; set; } = string.Empty;

    [JsonPropertyName("profileVersion")]
    public int ProfileVersion { get; set; }

    [JsonPropertyName("runName")]
    public string RunName { get; set; } = string.Empty;

    [JsonPropertyName("scorePercent")]
    public double ScorePercent { get; set; }

    [JsonPropertyName("rawPoints")]
    public double RawPoints { get; set; }

    [JsonPropertyName("source")]
    public string Source { get; set; } = "native";

    [JsonPropertyName("scoreSchemaVersion")]
    public int ScoreSchemaVersion { get; set; }

    [JsonPropertyName("scoringEngineVersion")]
    public string ScoringEngineVersion { get; set; } = string.Empty;

    [JsonPropertyName("parserVersion")]
    public string ParserVersion { get; set; } = string.Empty;

    [JsonPropertyName("groundTruthSha256")]
    public string GroundTruthSha256 { get; set; } = string.Empty;

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = string.Empty;

    [JsonPropertyName("promptVersion")]
    public string PromptVersion { get; set; } = string.Empty;

    [JsonPropertyName("computedAt")]
    public DateTimeOffset? ComputedAt { get; set; }

    [JsonPropertyName("isLegacyMigrated")]
    public bool IsLegacyMigrated { get; set; }

    [JsonPropertyName("isRescored")]
    public bool IsRescored { get; set; }

    public static ArchiveScoreVersion FromRun(ArchiveRunScore run, string source) => new()
    {
        Profile = run.ScoringProfile,
        ProfileVersion = run.ScoringProfileVersion,
        RunName = run.RunName,
        ScorePercent = run.ScorePercent,
        RawPoints = run.RawPoints,
        Source = source,
        ScoreSchemaVersion = run.ScoreSchemaVersion,
        ScoringEngineVersion = run.ScoringEngineVersion,
        ParserVersion = run.ParserVersion,
        GroundTruthSha256 = run.GroundTruthSha256,
        SourceSha256 = run.SourceSha256,
        PromptVersion = run.PromptVersion,
        ComputedAt = run.ComputedAt == default ? null : run.ComputedAt,
        IsLegacyMigrated = run.IsLegacyMigrated,
        IsRescored = run.IsRescored
    };
}

public sealed class ArchiveRunScore
{
    [JsonPropertyName("runName")]
    public string RunName { get; set; } = string.Empty;

    [JsonPropertyName("runKind")]
    public string RunKind { get; set; } = "blind_analysis";

    [JsonPropertyName("groundTruthVisibleToModel")]
    public bool GroundTruthVisibleToModel { get; set; }

    [JsonPropertyName("scoreSchemaVersion")]
    public int ScoreSchemaVersion { get; set; }

    [JsonPropertyName("scoringProfile")]
    public string ScoringProfile { get; set; } = string.Empty;

    [JsonPropertyName("scoringProfileVersion")]
    public int ScoringProfileVersion { get; set; }

    [JsonPropertyName("scoringEngineVersion")]
    public string ScoringEngineVersion { get; set; } = string.Empty;

    [JsonPropertyName("parserVersion")]
    public string ParserVersion { get; set; } = string.Empty;

    [JsonPropertyName("groundTruthSha256")]
    public string GroundTruthSha256 { get; set; } = string.Empty;

    [JsonPropertyName("sourceSha256")]
    public string SourceSha256 { get; set; } = string.Empty;

    [JsonPropertyName("promptVersion")]
    public string PromptVersion { get; set; } = string.Empty;

    [JsonPropertyName("computedAt")]
    public DateTimeOffset ComputedAt { get; set; }

    [JsonPropertyName("isLegacyMigrated")]
    public bool IsLegacyMigrated { get; set; }

    [JsonPropertyName("isRescored")]
    public bool IsRescored { get; set; }

    [JsonPropertyName("isAdjudicated")]
    public bool IsAdjudicated { get; set; }

    [JsonPropertyName("adjudicationLabel")]
    public string AdjudicationLabel { get; set; } = string.Empty;

    [JsonPropertyName("officialComparable")]
    public bool OfficialComparable { get; set; }

    [JsonPropertyName("scorePercent")]
    public double ScorePercent { get; set; }

    [JsonPropertyName("rawPoints")]
    public double RawPoints { get; set; }

    [JsonPropertyName("maxPoints")]
    public double MaxPoints { get; set; }

    [JsonPropertyName("scoreableVulnerabilityCount")]
    public int ScoreableVulnerabilityCount { get; set; }

    [JsonPropertyName("findingCount")]
    public int FindingCount { get; set; }

    [JsonPropertyName("fullTruePositives")]
    public int FullTruePositives { get; set; }

    [JsonPropertyName("partialTruePositives")]
    public int PartialTruePositives { get; set; }

    [JsonPropertyName("falsePositives")]
    public int FalsePositives { get; set; }

    [JsonPropertyName("duplicates")]
    public int Duplicates { get; set; }

    [JsonPropertyName("ignoredLowConfidence")]
    public int IgnoredLowConfidence { get; set; }

    [JsonPropertyName("missed")]
    public int Missed { get; set; }

    [JsonPropertyName("precision")]
    public double Precision { get; set; }

    [JsonPropertyName("recall")]
    public double Recall { get; set; }

    [JsonPropertyName("f1")]
    public double F1 { get; set; }

    [JsonPropertyName("finishReason")]
    public string FinishReason { get; set; } = string.Empty;

    [JsonPropertyName("loopDetected")]
    public bool LoopDetected { get; set; }

    [JsonPropertyName("loopDiagnosticsSummary")]
    public string LoopDiagnosticsSummary { get; set; } = string.Empty;

    [JsonPropertyName("manuallyStopped")]
    public bool ManuallyStopped { get; set; }

    [JsonPropertyName("promptTokens")]
    public int? PromptTokens { get; set; }

    [JsonPropertyName("responseTokens")]
    public int? ResponseTokens { get; set; }

    [JsonPropertyName("reasoningTokens")]
    public int? ReasoningTokens { get; set; }

    [JsonPropertyName("completionTokens")]
    public int? CompletionTokens { get; set; }

    [JsonPropertyName("responseChars")]
    public int ResponseChars { get; set; }

    [JsonPropertyName("reasoningChars")]
    public int ReasoningChars { get; set; }

    [JsonPropertyName("rawResponseChars")]
    public int RawResponseChars { get; set; }

    [JsonPropertyName("requestChars")]
    public int RequestChars { get; set; }

    [JsonPropertyName("promptChars")]
    public int PromptChars { get; set; }

    [JsonPropertyName("startedAt")]
    public DateTimeOffset? StartedAt { get; set; }

    [JsonPropertyName("completedAt")]
    public DateTimeOffset? CompletedAt { get; set; }

    [JsonPropertyName("durationMs")]
    public long? DurationMs { get; set; }

    [JsonPropertyName("usedResponseFormat")]
    public bool UsedResponseFormat { get; set; }

    [JsonPropertyName("retriedWithoutResponseFormat")]
    public bool RetriedWithoutResponseFormat { get; set; }

    [JsonPropertyName("usedThinkingControl")]
    public bool UsedThinkingControl { get; set; }

    [JsonPropertyName("retriedWithoutThinkingControl")]
    public bool RetriedWithoutThinkingControl { get; set; }

    [JsonPropertyName("parseMode")]
    public string ParseMode { get; set; } = string.Empty;

    [JsonPropertyName("parseWarning")]
    public string? ParseWarning { get; set; }

    [JsonPropertyName("emptyOutputWithReasoning")]
    public bool EmptyOutputWithReasoning { get; set; }

    /// <summary>
    /// Optional, non-scoring diagnostic comparing true positives visible in reasoning_content
    /// or inline &lt;think&gt; blocks against true positives reported in the final assistant output.
    /// </summary>
    [JsonPropertyName("reasoningDisclosure")]
    public ReasoningDisclosureDiagnostics? ReasoningDisclosure { get; set; }

    [JsonPropertyName("truthAudit")]
    public TruthAuditResult? TruthAudit { get; set; }

    [JsonPropertyName("selfValidation")]
    public RunComparison? SelfValidation { get; set; }

    [JsonPropertyName("evidenceFidelity")]
    public double EvidenceFidelity { get; set; }

    [JsonPropertyName("locationAccuracy")]
    public double LocationAccuracy { get; set; }

    [JsonPropertyName("hallucinationRate")]
    public double HallucinationRate { get; set; }

    [JsonPropertyName("duplicateRate")]
    public double DuplicateRate { get; set; }

    [JsonPropertyName("evaluationConfidence")]
    public double EvaluationConfidence { get; set; }

    [JsonPropertyName("falsePositiveTaxonomy")]
    public Dictionary<string, int> FalsePositiveTaxonomy { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Per-vulnerability credit in [0,1] keyed by ground-truth id: 1.0 full, 0.5 partial,
    /// 0.0 missed. Kept for v1 compatibility and small consumers.
    /// </summary>
    [JsonPropertyName("vulnerabilityCredit")]
    public Dictionary<string, double> VulnerabilityCredit { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Rich v2 per-vulnerability status. Metadata fields may be filled from local ground truth
    /// while comparing; archived v2 scorecards contain only compact scoring status by default.
    /// </summary>
    [JsonPropertyName("vulnerabilityResults")]
    public List<ArchiveVulnerabilityResult> VulnerabilityResults { get; set; } = [];

    /// <summary>
    /// True when this run did not produce a usable, model-authored detection result: it was
    /// manually aborted, hit an output loop, or never emitted a final assistant answer. Such
    /// runs stay in the archive for transparency/diagnostics but are skipped by
    /// <see cref="ArchiveRecord.PrimaryRun"/> so an aborted run cannot be reported as a 0% result.
    /// A genuinely poor but complete run (e.g. the model returned valid JSON yet found nothing)
    /// is <em>not</em> degenerate because it still produced a real model-authored answer.
    /// </summary>
    [JsonIgnore]
    public bool IsDegenerate =>
        ManuallyStopped
        || LoopDetected
        || string.Equals(FinishReason, "manual_abort", StringComparison.OrdinalIgnoreCase)
        || (ResponseChars <= 0
            && ScorePercent <= 0
            && !string.Equals(RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase));

    public static ArchiveRunScore FromArtifacts(BenchmarkRunArtifacts artifacts)
    {
        var run = FromScore(
            artifacts.Score,
            string.IsNullOrWhiteSpace(artifacts.ReasoningDisclosure.Summary) ? null : artifacts.ReasoningDisclosure);

        run.RunName = string.IsNullOrWhiteSpace(artifacts.RunName) ? run.RunName : artifacts.RunName;
        run.RunKind = string.IsNullOrWhiteSpace(artifacts.RunKind) ? run.RunKind : artifacts.RunKind;
        run.GroundTruthVisibleToModel = artifacts.GroundTruthVisibleToModel;
        run.TruthAudit = artifacts.TruthAudit;
        run.PromptVersion = string.IsNullOrWhiteSpace(artifacts.PromptVersion) || string.Equals(artifacts.PromptVersion, PromptVersions.Unknown, StringComparison.OrdinalIgnoreCase)
            ? run.PromptVersion
            : artifacts.PromptVersion;
        run.FinishReason = artifacts.FinishReason ?? string.Empty;
        run.LoopDetected = artifacts.LoopDetected;
        run.LoopDiagnosticsSummary = artifacts.LoopDiagnosticsSummary ?? string.Empty;
        run.ManuallyStopped = artifacts.ManuallyStopped;
        run.PromptTokens = artifacts.PromptTokens;
        run.ResponseTokens = artifacts.ResponseTokens;
        run.ReasoningTokens = artifacts.ReasoningTokens;
        run.CompletionTokens = artifacts.CompletionTokens;
        run.ResponseChars = artifacts.Response?.Length ?? 0;
        run.ReasoningChars = artifacts.ReasoningContent?.Length ?? 0;
        run.RawResponseChars = artifacts.RawResponse?.Length ?? 0;
        run.RequestChars = artifacts.RequestJson?.Length ?? 0;
        run.PromptChars = artifacts.Prompt?.Length ?? 0;
        run.StartedAt = artifacts.StartedAt == default ? null : artifacts.StartedAt;
        run.CompletedAt = artifacts.CompletedAt == default ? null : artifacts.CompletedAt;
        run.DurationMs = artifacts.DurationMs > 0
            ? artifacts.DurationMs
            : (run.StartedAt.HasValue && run.CompletedAt.HasValue
                ? Math.Max(0, (long)(run.CompletedAt.Value - run.StartedAt.Value).TotalMilliseconds)
                : null);
        run.UsedResponseFormat = artifacts.UsedResponseFormat;
        run.RetriedWithoutResponseFormat = artifacts.RetriedWithoutResponseFormat;
        run.UsedThinkingControl = artifacts.UsedThinkingControl;
        run.RetriedWithoutThinkingControl = artifacts.RetriedWithoutThinkingControl;
        run.ParseMode = string.IsNullOrWhiteSpace(artifacts.Parse.ParseMode) ? DeriveParseMode(artifacts.Parse) : artifacts.Parse.ParseMode;
        run.ParseWarning = artifacts.Parse.Warning;
        run.EmptyOutputWithReasoning = string.IsNullOrWhiteSpace(artifacts.Response) && !string.IsNullOrWhiteSpace(artifacts.ReasoningContent);
        return run;
    }

    public static ArchiveRunScore FromScore(ScoringResult score, ReasoningDisclosureDiagnostics? reasoningDisclosure = null)
    {
        var credit = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ArchiveVulnerabilityResult>();
        foreach (var vulnerability in score.Vulnerabilities)
        {
            var value = vulnerability.Found ? (vulnerability.Partial ? 0.5 : 1.0) : 0.0;
            credit[vulnerability.Id] = value;
            results.Add(new ArchiveVulnerabilityResult
            {
                Id = vulnerability.Id,
                Title = vulnerability.Title,
                Severity = vulnerability.Severity,
                Credit = value,
                Status = vulnerability.Found ? (vulnerability.Partial ? "partial" : "full") : "missed",
                MatchScore = vulnerability.MatchScore,
                FindingIndex = vulnerability.FindingIndex,
                Category = vulnerability.Category,
                Module = vulnerability.Module,
                Exploitability = vulnerability.Exploitability,
                Difficulty = vulnerability.Difficulty,
                EvidenceFidelity = vulnerability.EvidenceFidelity,
                LocationAccuracy = vulnerability.LocationAccuracy
            });
        }

        return new ArchiveRunScore
        {
            RunName = score.RunName,
            RunKind = PromptVersions.ForRunName(score.RunName) switch
            {
                PromptVersions.SelfValidateV1 => "self_validation",
                PromptVersions.TruthAuditV1 => "truth_audit",
                _ => "blind_analysis"
            },
            GroundTruthVisibleToModel = string.Equals(score.PromptVersion, PromptVersions.TruthAuditV1, StringComparison.OrdinalIgnoreCase),
            ScoreSchemaVersion = score.ScoreSchemaVersion <= 0 ? ScoringProfiles.ScoreSchemaVersion : score.ScoreSchemaVersion,
            ScoringProfile = string.IsNullOrWhiteSpace(score.ScoringProfile) ? ScoringProfiles.OfficialV1Name : score.ScoringProfile,
            ScoringProfileVersion = score.ScoringProfileVersion <= 0 ? ScoringProfiles.OfficialV1Version : score.ScoringProfileVersion,
            ScoringEngineVersion = string.IsNullOrWhiteSpace(score.ScoringEngineVersion) ? ScoringProfiles.OfficialV1EngineVersion : score.ScoringEngineVersion,
            ParserVersion = string.IsNullOrWhiteSpace(score.ParserVersion) ? ResponseParser.CurrentParserVersion : score.ParserVersion,
            GroundTruthSha256 = score.GroundTruthSha256,
            SourceSha256 = score.SourceSha256,
            PromptVersion = string.IsNullOrWhiteSpace(score.PromptVersion) || string.Equals(score.PromptVersion, PromptVersions.Unknown, StringComparison.OrdinalIgnoreCase)
                ? PromptVersions.ForRunName(score.RunName)
                : score.PromptVersion,
            ComputedAt = score.ComputedAt == default ? DateTimeOffset.UtcNow : score.ComputedAt,
            IsLegacyMigrated = score.IsLegacyMigrated,
            IsRescored = score.IsRescored,
            IsAdjudicated = score.IsAdjudicated,
            AdjudicationLabel = score.AdjudicationLabel,
            OfficialComparable = ScoringProfiles.IsOfficialComparableProfile(score.ScoringProfile) && !score.IsAdjudicated,
            ScorePercent = score.ScorePercent,
            RawPoints = score.RawPoints,
            MaxPoints = score.MaxPoints > 0 ? score.MaxPoints : score.ScoreableVulnerabilityCount * ScoringProfiles.OfficialV1.Points.FullTp,
            ScoreableVulnerabilityCount = score.ScoreableVulnerabilityCount,
            FindingCount = score.FindingCount,
            FullTruePositives = score.FullTruePositives,
            PartialTruePositives = score.PartialTruePositives,
            FalsePositives = score.FalsePositives,
            Duplicates = score.Duplicates,
            IgnoredLowConfidence = score.IgnoredLowConfidence,
            Missed = score.Missed,
            Precision = score.Precision,
            Recall = score.Recall,
            F1 = score.F1,
            EvidenceFidelity = score.EvidenceFidelity,
            LocationAccuracy = score.LocationAccuracy,
            HallucinationRate = score.HallucinationRate,
            DuplicateRate = score.DuplicateRate,
            EvaluationConfidence = score.EvaluationConfidence,
            FalsePositiveTaxonomy = new Dictionary<string, int>(score.FalsePositiveTaxonomy, StringComparer.OrdinalIgnoreCase),
            ReasoningDisclosure = reasoningDisclosure,
            VulnerabilityCredit = credit,
            VulnerabilityResults = results
        };
    }

    public void NormalizeAfterLoad(ArchiveRecord? record = null)
    {
        var existingCredit = VulnerabilityCredit ?? new Dictionary<string, double>();
        VulnerabilityCredit = new Dictionary<string, double>(existingCredit, StringComparer.OrdinalIgnoreCase);
        VulnerabilityResults ??= [];
        FalsePositiveTaxonomy = new Dictionary<string, int>(FalsePositiveTaxonomy ?? new Dictionary<string, int>(), StringComparer.OrdinalIgnoreCase);

        if (HallucinationRate <= 0 && FindingCount > 0 && FalsePositives > 0)
        {
            HallucinationRate = FalsePositives / (double)Math.Max(1, FindingCount - IgnoredLowConfidence);
        }

        if (DuplicateRate <= 0 && FindingCount > 0 && Duplicates > 0)
        {
            DuplicateRate = Duplicates / (double)FindingCount;
        }

        if (FalsePositiveTaxonomy.Count == 0 && FalsePositives > 0)
        {
            FalsePositiveTaxonomy["unsupported_by_code"] = FalsePositives;
        }

        if (VulnerabilityResults.Count == 0 && VulnerabilityCredit.Count > 0)
        {
            VulnerabilityResults = VulnerabilityCredit
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => new ArchiveVulnerabilityResult
                {
                    Id = kvp.Key,
                    Credit = kvp.Value,
                    Status = CreditToStatus(kvp.Value)
                })
                .ToList();
        }

        if (VulnerabilityCredit.Count == 0 && VulnerabilityResults.Count > 0)
        {
            foreach (var result in VulnerabilityResults.Where(r => !string.IsNullOrWhiteSpace(r.Id)))
            {
                VulnerabilityCredit[result.Id] = result.Credit;
            }
        }

        foreach (var result in VulnerabilityResults)
        {
            if (string.IsNullOrWhiteSpace(result.Status))
            {
                result.Status = CreditToStatus(result.Credit);
            }
        }

        if (string.IsNullOrWhiteSpace(ParseMode))
        {
            ParseMode = "unknown";
        }

        if (string.IsNullOrWhiteSpace(RunKind))
        {
            RunKind = PromptVersion switch
            {
                PromptVersions.SelfValidateV1 => "self_validation",
                PromptVersions.TruthAuditV1 => "truth_audit",
                _ => string.Equals(RunName, "Run 2", StringComparison.OrdinalIgnoreCase) ? "self_validation" : "blind_analysis"
            };
        }

        GroundTruthVisibleToModel = GroundTruthVisibleToModel || string.Equals(RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(ScoringProfile))
        {
            ScoringProfile = "legacy-unknown";
        }

        if (ScoreSchemaVersion <= 0 && !string.Equals(ScoringProfile, "legacy-unknown", StringComparison.OrdinalIgnoreCase))
        {
            ScoreSchemaVersion = ScoringProfiles.ScoreSchemaVersion;
        }

        if (ScoringProfileVersion <= 0 && string.Equals(ScoringProfile, ScoringProfiles.OfficialV1Name, StringComparison.OrdinalIgnoreCase))
        {
            ScoringProfileVersion = ScoringProfiles.OfficialV1Version;
        }

        if (string.IsNullOrWhiteSpace(ScoringEngineVersion))
        {
            ScoringEngineVersion = string.Equals(ScoringProfile, ScoringProfiles.OfficialV1Name, StringComparison.OrdinalIgnoreCase)
                ? ScoringProfiles.OfficialV1EngineVersion
                : "unknown";
        }

        if (string.IsNullOrWhiteSpace(ParserVersion))
        {
            ParserVersion = string.Equals(ScoringProfile, "legacy-unknown", StringComparison.OrdinalIgnoreCase)
                ? "unknown"
                : ResponseParser.CurrentParserVersion;
        }

        if (string.IsNullOrWhiteSpace(SourceSha256))
        {
            SourceSha256 = record?.SourceSha256 ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(PromptVersion))
        {
            PromptVersion = PromptVersions.ForRunName(RunName);
        }

        if (ComputedAt == default)
        {
            ComputedAt = CompletedAt ?? record?.CompletedAt ?? DateTimeOffset.MinValue;
        }

        var eligibleForOfficialComparison = !IsAdjudicated
                                            && ScoringProfiles.IsOfficialComparableProfile(ScoringProfile)
                                            && (record?.SourceHashMatches ?? true)
                                            && string.Equals(record?.BenchmarkProfile ?? "official", "official", StringComparison.OrdinalIgnoreCase)
                                            && !IsDegenerate
                                            && !GroundTruthVisibleToModel
                                            && !string.Equals(RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase);
        OfficialComparable = eligibleForOfficialComparison;
    }

    private static string CreditToStatus(double credit) => credit >= 0.99 ? "full" : credit > 0 ? "partial" : "missed";

    private static string DeriveParseMode(ParseResult parse)
    {
        if (parse.UsedTextFallback)
        {
            return parse.Findings.Count == 0 ? "none" : "text_fallback";
        }

        if (!parse.ParsedJson)
        {
            return "none";
        }

        if (parse.UsedMarkdownJsonBlock)
        {
            return "markdown_json";
        }

        return "json";
    }
}

public sealed class ArchiveVulnerabilityResult
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("credit")]
    public double Credit { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "missed";

    [JsonPropertyName("matchScore")]
    public double MatchScore { get; set; }

    [JsonPropertyName("findingIndex")]
    public int? FindingIndex { get; set; }

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("cwe")]
    public List<string> Cwe { get; set; } = [];

    [JsonPropertyName("category")]
    public string Category { get; set; } = string.Empty;

    [JsonPropertyName("module")]
    public string Module { get; set; } = string.Empty;

    [JsonPropertyName("exploitability")]
    public string Exploitability { get; set; } = string.Empty;

    [JsonPropertyName("difficulty")]
    public string Difficulty { get; set; } = string.Empty;

    [JsonPropertyName("evidenceFidelity")]
    public double EvidenceFidelity { get; set; }

    [JsonPropertyName("locationAccuracy")]
    public double LocationAccuracy { get; set; }
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
