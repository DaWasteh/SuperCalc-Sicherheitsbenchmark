using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public sealed class ScoringProfile
{
    [JsonPropertyName("scoringProfile")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("scoringProfileVersion")]
    public int Version { get; init; }

    [JsonPropertyName("scoringEngineVersion")]
    public string EngineVersion { get; init; } = string.Empty;

    [JsonPropertyName("fullThreshold")]
    public double FullThreshold { get; init; }

    [JsonPropertyName("partialThreshold")]
    public double PartialThreshold { get; init; }

    [JsonPropertyName("weights")]
    public Dictionary<string, double> Weights { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("points")]
    public ScoringPointSchedule Points { get; init; } = new();

    [JsonPropertyName("gates")]
    public ScoringGateOptions Gates { get; init; } = new();

    public double Weight(string signalName)
        => Weights.TryGetValue(signalName, out var weight) ? weight : 0;
}

public sealed class ScoringPointSchedule
{
    [JsonPropertyName("fullTp")]
    public double FullTp { get; init; }

    [JsonPropertyName("partialTp")]
    public double PartialTp { get; init; }

    [JsonPropertyName("falsePositive")]
    public double FalsePositive { get; init; }

    [JsonPropertyName("duplicate")]
    public double Duplicate { get; init; }

    [JsonPropertyName("severityMismatch")]
    public double SeverityMismatch { get; init; }
}

public sealed class ScoringGateOptions
{
    [JsonPropertyName("requireEvidenceOrLocation")]
    public bool RequireEvidenceOrLocation { get; init; }

    [JsonPropertyName("capGenericAliasOnly")]
    public bool CapGenericAliasOnly { get; init; }

    [JsonPropertyName("aliasEvidenceMinimum")]
    public double AliasEvidenceMinimum { get; init; }

    [JsonPropertyName("minimumAliasForTp")]
    public double MinimumAliasForTp { get; init; }

    [JsonPropertyName("genericAliasOnlyCap")]
    public double GenericAliasOnlyCap { get; init; }
}

public static class ScoringProfiles
{
    public const string OfficialV1Name = "official-v1";
    public const int OfficialV1Version = 1;
    public const string OfficialV1EngineVersion = "official-v1-freeze-2026-06-28";
    public const string OfficialV2Name = "official-v2";
    public const int OfficialV2Version = 1;
    public const string OfficialV2EngineVersion = "official-v2-gated-2026-06-28";
    public const int ScoreSchemaVersion = 1;

    public static ScoringProfile OfficialV1 { get; } = new()
    {
        Name = OfficialV1Name,
        Version = OfficialV1Version,
        EngineVersion = OfficialV1EngineVersion,
        FullThreshold = 0.75,
        PartialThreshold = 0.55,
        Weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["type_alias"] = 0.25,
            ["location"] = 0.30,
            ["evidence"] = 0.25,
            ["cwe_severity"] = 0.10,
            ["impact_trigger"] = 0.10
        },
        Points = DefaultPoints
    };

    public static ScoringProfile OfficialV2 { get; } = new()
    {
        Name = OfficialV2Name,
        Version = OfficialV2Version,
        EngineVersion = OfficialV2EngineVersion,
        FullThreshold = 0.78,
        PartialThreshold = 0.58,
        Weights = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
        {
            ["type_alias"] = 0.22,
            ["location"] = 0.25,
            ["evidence"] = 0.30,
            ["cwe_severity"] = 0.10,
            ["impact_trigger"] = 0.13
        },
        Points = DefaultPoints,
        Gates = new ScoringGateOptions
        {
            RequireEvidenceOrLocation = true,
            CapGenericAliasOnly = true,
            AliasEvidenceMinimum = 0.50,
            MinimumAliasForTp = 0.40,
            GenericAliasOnlyCap = 0.50
        }
    };

    private static ScoringPointSchedule DefaultPoints => new()
    {
        FullTp = 5.0,
        PartialTp = 2.5,
        FalsePositive = -2.0,
        Duplicate = -1.0,
        SeverityMismatch = -1.0
    };

    public static bool TryGet(string? name, out ScoringProfile profile)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized) || string.Equals(normalized, OfficialV1Name, StringComparison.OrdinalIgnoreCase))
        {
            profile = OfficialV1;
            return true;
        }

        if (string.Equals(normalized, OfficialV2Name, StringComparison.OrdinalIgnoreCase))
        {
            profile = OfficialV2;
            return true;
        }

        profile = OfficialV1;
        return false;
    }

    public static ScoringProfile Get(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name, OfficialV1Name, StringComparison.OrdinalIgnoreCase))
        {
            return OfficialV1;
        }

        if (string.Equals(name, OfficialV2Name, StringComparison.OrdinalIgnoreCase))
        {
            return OfficialV2;
        }

        throw new ArgumentException($"Unknown scoring profile '{name}'. Currently supported: {OfficialV1Name}, {OfficialV2Name}.");
    }

    public static bool IsOfficialComparableProfile(string? profile)
        => !string.IsNullOrWhiteSpace(profile)
           && profile.StartsWith("official-", StringComparison.OrdinalIgnoreCase)
           && !string.Equals(profile, "legacy-unknown", StringComparison.OrdinalIgnoreCase);
}

public sealed class ScoreComputationContext
{
    public string ParserVersion { get; init; } = ResponseParser.CurrentParserVersion;
    public string GroundTruthSha256 { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string PromptVersion { get; init; } = PromptVersions.Unknown;
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool IsLegacyMigrated { get; init; }
    public bool IsRescored { get; init; }
}

public static class PromptVersions
{
    public const string AnalysisV1 = "analysis_v1";
    public const string SelfValidateV1 = "self_validate_v1";
    public const string TruthAuditV1 = "truth_audit_v1";
    public const string Fixture = "fixture";
    public const string ReasoningDisclosure = "reasoning_disclosure";
    public const string Unknown = "unknown";

    public static string ForRunName(string? runName)
    {
        var normalized = (runName ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized.Contains("run 1", StringComparison.Ordinal) || normalized.Contains("run1", StringComparison.Ordinal))
        {
            return AnalysisV1;
        }

        if (normalized.Contains("run 2", StringComparison.Ordinal) || normalized.Contains("run2", StringComparison.Ordinal))
        {
            return SelfValidateV1;
        }

        if (normalized.Contains("truth", StringComparison.Ordinal) || normalized.Contains("audit", StringComparison.Ordinal) || normalized.Contains("run 3", StringComparison.Ordinal) || normalized.Contains("run3", StringComparison.Ordinal))
        {
            return TruthAuditV1;
        }

        if (normalized.Contains("fixture", StringComparison.Ordinal))
        {
            return Fixture;
        }

        if (normalized.Contains("thinking", StringComparison.Ordinal) || normalized.Contains("reasoning", StringComparison.Ordinal))
        {
            return ReasoningDisclosure;
        }

        return Unknown;
    }
}
