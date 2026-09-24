using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public sealed class TruthAuditResponse
{
    [JsonIgnore] public bool ParseSucceeded { get; set; } = true;
    [JsonIgnore] public bool RequiredArraysPresent { get; set; } = true;

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
    [JsonConverter(typeof(LenientNullableBooleanConverter))]
    public bool? AdmitsMiss { get; set; }

    [JsonPropertyName("overclaims")]
    [JsonConverter(typeof(LenientNullableBooleanConverter))]
    public bool? Overclaims { get; set; }
}

public sealed class TruthAuditFalsePositiveAdmission
{
    [JsonPropertyName("previous_finding_quote")]
    public string PreviousFindingQuote { get; set; } = string.Empty;

    [JsonPropertyName("admitted")]
    [JsonConverter(typeof(LenientBooleanConverter))]
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
    /// <summary>
    /// Null identifies legacy scorecards written before explicit validation metadata.
    /// New audits are eligible for headline metrics only when this value is true.
    /// </summary>
    public bool? IsValid { get; init; }
    public List<string> ValidationErrors { get; init; } = [];
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

    /// <summary>Point scheme behind <see cref="AccountabilityScore"/>; empty means legacy accountability-v1.</summary>
    public string AccountabilityVersion { get; init; } = string.Empty;
    public int ActualMissedCount { get; init; }
    public int ActualFalsePositiveCount { get; init; }

    /// <summary>Admissions whose quote identified a real true positive (inaccurate self-assessment, -1 each).</summary>
    public int AdmittedTruePositiveCount { get; init; }
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
    public bool? ReportedAdmitsMiss { get; init; }
    public bool? ReportedOverclaims { get; init; }
    public bool ExpectedAdmitsMiss { get; init; }
    public bool ExpectedOverclaims { get; init; }
    public bool? AdmitsMissConsistent { get; init; }
    public bool? OverclaimsConsistent { get; init; }
    public string PreviousOutputQuote { get; init; } = string.Empty;
    public string Notes { get; init; } = string.Empty;
}

/// <summary>
/// The four self-assessment values of the truth-audit contract. Case, spaces and hyphens are
/// normalized and unambiguous short forms accepted; the ambiguous "found" (full or partial?)
/// is invalid. Scoring and diagnostics share this so both judge the same audits valid.
/// </summary>
public static class TruthAuditVocabulary
{
    public static string Normalize(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');
        return normalized switch
        {
            "found_full" or "full" => "found_full",
            "found_partial" or "partial" => "found_partial",
            "unclear_or_overclaimed" or "unclear" or "overclaimed" => "unclear_or_overclaimed",
            "missed" or "miss" => "missed",
            _ => "invalid_or_missing"
        };
    }
}

/// <summary>Reads JSON booleans also when a model writes them as strings ("true", "yes", "false", "no").</summary>
public sealed class LenientNullableBooleanConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Null => null,
            JsonTokenType.String => (reader.GetString() ?? string.Empty).Trim().ToLowerInvariant() switch
            {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" => false,
                _ => null
            },
            JsonTokenType.Number => reader.TryGetInt32(out var number) ? number != 0 : null,
            _ => throw new JsonException($"Unexpected token {reader.TokenType} for a boolean.")
        };

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            writer.WriteBooleanValue(value.Value);
        }
    }
}

public sealed class LenientBooleanConverter : JsonConverter<bool>
{
    private static readonly LenientNullableBooleanConverter Inner = new();

    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => Inner.Read(ref reader, typeToConvert, options) ?? false;

    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options)
        => writer.WriteBooleanValue(value);
}
