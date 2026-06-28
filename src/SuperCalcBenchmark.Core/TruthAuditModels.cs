using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public sealed class TruthAuditResponse
{
    [JsonPropertyName("summary")]
    public string Summary { get; set; } = string.Empty;

    [JsonPropertyName("audited_run")]
    public string AuditedRun { get; set; } = string.Empty;

    [JsonPropertyName("truth_items")]
    public List<TruthAuditItem> TruthItems { get; set; } = [];

    [JsonPropertyName("false_positive_admissions")]
    public List<TruthAuditFalsePositiveAdmission> FalsePositiveAdmissions { get; set; } = [];

    [JsonPropertyName("corrections")]
    public List<TruthAuditCorrection> Corrections { get; set; } = [];
}

public sealed class TruthAuditItem
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("self_assessment")]
    public string SelfAssessment { get; set; } = string.Empty;

    [JsonPropertyName("previous_output_quote")]
    public string PreviousOutputQuote { get; set; } = string.Empty;

    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;

    [JsonPropertyName("admits_miss")]
    public bool AdmitsMiss { get; set; }

    [JsonPropertyName("overclaims")]
    public bool Overclaims { get; set; }
}

public sealed class TruthAuditFalsePositiveAdmission
{
    [JsonPropertyName("previous_finding_quote")]
    public string PreviousFindingQuote { get; set; } = string.Empty;

    [JsonPropertyName("admitted")]
    public bool Admitted { get; set; }

    [JsonPropertyName("rationale")]
    public string Rationale { get; set; } = string.Empty;
}

public sealed class TruthAuditCorrection
{
    [JsonPropertyName("previous_claim")]
    public string PreviousClaim { get; set; } = string.Empty;

    [JsonPropertyName("corrected_claim")]
    public string CorrectedClaim { get; set; } = string.Empty;

    [JsonPropertyName("correction_type")]
    public string CorrectionType { get; set; } = string.Empty;
}

public sealed class TruthAuditResult
{
    public string Summary { get; init; } = string.Empty;
    public string AuditedRunName { get; init; } = string.Empty;
    public string AuditedRunScoreProfile { get; init; } = string.Empty;
    public double AuditedRunScorePercent { get; init; }
    public string SelectionReason { get; init; } = string.Empty;
    public double TruthAuditAccuracy { get; init; }
    public double MissAdmissionRate { get; init; }
    public double OverclaimRate { get; init; }
    public double FalsePositiveAdmissionRate { get; init; }
    public int EvidenceLaunderingCount { get; init; }
    public double QuoteFidelity { get; init; }
    public int ContradictionCount { get; init; }
    public double AccountabilityScore { get; init; }
    public int ActualMissedCount { get; init; }
    public int ActualFalsePositiveCount { get; init; }
    public List<TruthAuditItemResult> Items { get; init; } = [];
}

public sealed class TruthAuditItemResult
{
    public string Id { get; init; } = string.Empty;
    public string ActualStatus { get; init; } = "missed";
    public string SelfAssessment { get; init; } = string.Empty;
    public bool Correct { get; init; }
    public bool QuoteValid { get; init; }
    public bool Overclaim { get; init; }
    public bool EvidenceLaundering { get; init; }
    public string PreviousOutputQuote { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}
