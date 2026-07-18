using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public enum MetricEvidenceTier { FullArtifact, ScorecardDerived, Unavailable }
public enum TruthAuditValidityState { Valid, Partial, Invalid }
public enum TruthAuditGateFailure { MissingAuditRun, WrongRunKind, GroundTruthNotVisible, ManualAbort, LoopDetected, EmptyOutput, ParseFailed, MissingTargetRun, DegenerateTargetRun, NonComparableTarget, AuditedRunMismatch, ProfileMismatch, ScoreMismatch, GroundTruthHashMismatch, SourceHashMismatch, MissingExpectedId, DuplicateExpectedId, UnknownId, InvalidAssessment, RequiredFlagMissing }
public enum AuditActualStatus { FoundFull, FoundPartial, Missed }
public enum AuditAssessment { FoundFull, FoundPartial, UnclearOrOverclaimed, Missed, InvalidOrMissing }
public enum ParseQualityLevel { Unknown = -1, Unusable = 0, TextFallback = 1, PartialJson = 2, RecoveredJson = 3, DirectJson = 4 }
public enum ConfidenceOrigin { Reported, JsonDefault, TextFallbackDefault, LegacyUnknown }
public enum AuditCorrectionType { Severity, Cwe, Location, Evidence, Impact, Unsupported, Invalid }
public enum RevisionOutcome { Beneficial, Harmful, Mixed, Ineffective, Untouched }

public sealed record AuditConfusionCell(AuditActualStatus Actual, AuditAssessment Assessment, int Count);
public sealed record TriangulationCell(bool ReasoningFound, AuditActualStatus OutputStatus, AuditAssessment AuditAssessment, int Count);
public sealed record SeverityConfusionCell(string Actual, string Reported, int Count);
public sealed record RevisionDiagnosticRow(string Key, string Kind, RevisionOutcome Outcome, double CreditDelta, bool EvidenceImproved, bool EvidenceRegressed, bool LocationImproved, bool LocationRegressed);

public sealed class TruthAuditValidity
{
    public string MetricContractVersion { get; init; } = "diagnostics-v1";
    public MetricEvidenceTier EvidenceTier { get; init; }
    public TruthAuditValidityState State { get; init; }
    public bool MetricEligible { get; init; }
    public int ExpectedItemCount { get; init; }
    public int UniqueExpectedItemCount { get; init; }
    public int MissingItemCount { get; init; }
    public int DuplicateItemCount { get; init; }
    public int UnknownItemCount { get; init; }
    public int InvalidAssessmentCount { get; init; }
    public int RequiredFlagMissingCount { get; init; }
    public double Coverage { get; init; }
    public List<TruthAuditGateFailure> Failures { get; init; } = [];
}

public sealed class TruthAuditCorrectionResult
{
    public string PreviousClaim { get; init; } = "";
    public string CorrectedClaim { get; init; } = "";
    public string RawCorrectionType { get; init; } = "";
    public AuditCorrectionType Type { get; init; }
    public bool PreviousClaimQuoted { get; init; }
    public bool CorrectedClaimNonEmpty { get; init; }
    public bool MateriallyChanged { get; init; }
    public bool Duplicate { get; init; }
    public bool ProvenanceValid { get; init; }
    public string RejectionReason { get; init; } = "";
}

public sealed class CalibrationBin { public int Index { get; init; } public int Count { get; init; } public double SumConfidence { get; init; } public double SumOutcomeCredit { get; init; } }
public sealed class CalibrationMetricSet
{
    public int Count { get; init; }
    public double? MeanConfidence { get; init; }
    public double? MeanObservedCredit { get; init; }
    public double? SignedCalibrationBias { get; init; }
    public double? SoftBrier { get; init; }
    public double? BinaryBrier { get; init; }
    public double? Ece10 { get; init; }
    public double? Mce10 { get; init; }
    public List<CalibrationBin> Bins { get; init; } = [];
}
public sealed class ConfidenceCalibrationDiagnostics
{
    public int JoinableCount { get; init; }
    public int ReportedCount { get; init; }
    public double? ConfidenceCoverage { get; init; }
    public CalibrationMetricSet ReportedOnly { get; init; } = new();
    public CalibrationMetricSet AllIncludingImputed { get; init; } = new();
    // Compatibility aliases for the reported-only headline.
    public double? MeanReportedConfidence => ReportedOnly.MeanConfidence;
    public double? MeanObservedCredit => ReportedOnly.MeanObservedCredit;
    public double? SignedCalibrationBias => ReportedOnly.SignedCalibrationBias;
    public double? SoftBrier => ReportedOnly.SoftBrier;
    public double? BinaryBrier => ReportedOnly.BinaryBrier;
    public double? Ece10 => ReportedOnly.Ece10;
    public double? Mce10 => ReportedOnly.Mce10;
    public List<CalibrationBin> Bins => ReportedOnly.Bins;
}

public sealed class SeverityCweDiagnostics
{
    public int AssignedTruePositiveCount { get; init; }
    public int SeverityReportedCount { get; init; }
    public int SeverityExactCount { get; init; }
    public int SeverityOrdinalEligibleCount { get; init; }
    public int SeverityInflationCount { get; init; }
    public int SeverityUnderclaimCount { get; init; }
    public int SeverityAbsoluteError { get; init; }
    public double? SeverityCoverage { get; init; }
    public double? SeverityExactRate { get; init; }
    public double? SeverityInflationRate { get; init; }
    public double? SeverityUnderclaimRate { get; init; }
    public double? SeverityMae { get; init; }
    public double? NormalizedSeverityMae { get; init; }
    public List<SeverityConfusionCell> SeverityConfusion { get; init; } = [];
    public int CweEligibleCount { get; init; }
    public int CweReportedCount { get; init; }
    public int CweAnyHitCount { get; init; }
    public int CweExactSetCount { get; init; }
    public int CweIntersectionCount { get; init; }
    public int CweReportedIdCount { get; init; }
    public int CweActualIdCount { get; init; }
    public double? CweCoverage { get; init; }
    public double? CweAnyHitRate { get; init; }
    public double? CweExactSetRate { get; init; }
    public double? CweMicroPrecision { get; init; }
    public double? CweMicroRecall { get; init; }
    public Dictionary<string, int> UnsupportedSeverityClaims { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, int> UnsupportedCweClaims { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double? UnsupportedClaimRate { get; init; }
}

public sealed class TriangulationDiagnostics
{
    public bool ReasoningAvailable { get; init; }
    public List<TriangulationCell> Cells { get; init; } = [];
    // Quote-gated sufficient statistics. Aggregates must use these rather than
    // attempting to infer acknowledgment from the (non-provenance-bearing) cells.
    public int ReasoningEligibleCount { get; init; }
    public int ReasoningOutputCount { get; init; }
    public int OutputEligibleCount { get; init; }
    public int OutputAcknowledgedCount { get; init; }
    public int ReasoningAcknowledgedCount { get; init; }
    public int EndToEndCount { get; init; }
    public double? ReasoningToOutputRetention { get; init; }
    public double? OutputToAuditAcknowledgment { get; init; }
    public double? ReasoningToAuditClaimRate { get; init; }
    public double? EndToEndRetention { get; init; }
    public int ThoughtOnlyCount { get; init; }
    public int ThoughtOnlyHonestOmission { get; init; }
    public double? ThoughtOnlyHonestyRate { get; init; }
    public int? OutputOnlyCount { get; init; }
    public double? OutputOnlyAuditAckRate { get; init; }
}

public sealed class TruthMetricDiagnostics
{
    public TruthAuditValidity Validity { get; init; } = new();
    public List<AuditConfusionCell> Confusion { get; init; } = [];
    public int OrdinalEligibleCount { get; init; }
    public int InflationCount { get; init; }
    public int InflationMagnitude { get; init; }
    public double? InflationRate { get; init; }
    public double? NormalizedInflation { get; init; }
    public int UnderclaimCount { get; init; }
    public int UnderclaimMagnitude { get; init; }
    public double? UnderclaimRate { get; init; }
    public double? NormalizedUnderclaim { get; init; }
    public double? MeanSignedGap { get; init; }
    public double? NormalizedSignedGap { get; init; }
    public double? MeanAbsoluteGap { get; init; }
    public int FoundClaimCount { get; init; }
    public int StatusContradictionCount { get; init; }
    public double? StatusContradictionRate { get; init; }
    public double? LaunderingPrevalence { get; init; }
    public double? LaunderingOpportunityRate { get; init; }
    public int LegacyContradictionCount { get; init; }
    public double? ContradictionPrevalence { get; init; }
    public double? InvalidAssessmentRate { get; init; }
    public int ExplicitFlagPresentCount { get; init; }
    public int ExplicitFlagConsistentCount { get; init; }
    public double? ExplicitFlagConsistencyRate { get; init; }
    public List<TruthAuditCorrectionResult> Corrections { get; init; } = [];
    public int RawCorrectionCount { get; init; }
    public int ValidCorrectionCount { get; init; }
    public int RejectedCorrectionCount { get; init; }
    public Dictionary<string, int> CorrectionCountByType { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public double? CorrectionProvenanceRate { get; init; }
    public TriangulationDiagnostics? Triangulation { get; init; }
}

public sealed class ParseTransitionDiagnostics { public string Run1Mode { get; init; } = ""; public string Run2Mode { get; init; } = ""; public ParseQualityLevel Run1Level { get; init; } public ParseQualityLevel Run2Level { get; init; } public double? Delta { get; init; } public string Transition { get; init; } = "Unknown"; }
public sealed class RevisionSelectivityDiagnostics
{
    public List<RevisionDiagnosticRow> Items { get; init; } = [];
    public int Beneficial { get; init; } public int Harmful { get; init; } public int Mixed { get; init; } public int Ineffective { get; init; } public int Untouched { get; init; }
    public int Touched => Beneficial + Harmful + Mixed + Ineffective;
    public double? RevisionSelectivity => Touched == 0 ? null : (double)Beneficial / Touched;
    public double? RevisionHarmRate => Touched == 0 ? null : (double)Harmful / Touched;
    public double? RevisionMixedRate => Touched == 0 ? null : (double)Mixed / Touched;
    public double? RevisionNet => Touched == 0 ? null : (double)(Beneficial - Harmful) / Touched;
}
public sealed class DiagnosticsProvenance
{
    public string CalculatorVersion { get; init; } = "diagnostics-v1.1";
    public string ParserVersion { get; init; } = "truth-audit-v1";
    public string? RunJsonSha256 { get; init; }
    public string? FinalResponseSha256 { get; init; }
    public string? ArtifactInputSha256 { get; init; }
    public string? ArchiveInputSha256 { get; init; }
    public string? ResponseSource { get; init; }
    public string? AuditedRunName { get; init; }
}

public sealed class DiagnosticsComponentAvailability
{
    public string Status { get; init; } = "unavailable";
    public string? Reason { get; init; }
}

public sealed class BehavioralDiagnosticsEnvelope
{
    [JsonPropertyName("diagnosticsVersion")] public string DiagnosticsVersion { get; init; } = "diagnostics-v1";
    [JsonPropertyName("computedAt")] public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
    [JsonPropertyName("source")] public string Source { get; init; } = "native";
    [JsonPropertyName("provenance")] public DiagnosticsProvenance Provenance { get; init; } = new();
    [JsonPropertyName("componentAvailability")] public Dictionary<string, DiagnosticsComponentAvailability> ComponentAvailability { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonPropertyName("truthAudit")] public TruthMetricDiagnostics? TruthAudit { get; init; }
    [JsonPropertyName("run1Confidence")] public ConfidenceCalibrationDiagnostics? Run1Confidence { get; init; }
    [JsonPropertyName("run2Confidence")] public ConfidenceCalibrationDiagnostics? Run2Confidence { get; init; }
    [JsonPropertyName("run1Taxonomy")] public SeverityCweDiagnostics? Run1Taxonomy { get; init; }
    [JsonPropertyName("run2Taxonomy")] public SeverityCweDiagnostics? Run2Taxonomy { get; init; }
    [JsonPropertyName("parseTransition")] public ParseTransitionDiagnostics? ParseTransition { get; init; }
    [JsonPropertyName("revisionSelectivity")] public RevisionSelectivityDiagnostics? RevisionSelectivity { get; init; }
}
