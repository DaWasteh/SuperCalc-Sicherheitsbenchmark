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

    /// <summary>Matching rules; all off for the frozen official-v1/v2 profiles.</summary>
    [JsonPropertyName("matching")]
    public ScoringMatchingOptions Matching { get; init; } = new();

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

    /// <summary>
    /// Points for an unmatched finding reported with confidence below 0.35. Frozen profiles
    /// ignore such findings (0), which makes low-confidence guessing free; official-v3 charges
    /// half the false-positive penalty so honest hedging stays cheaper than a confident miss.
    /// </summary>
    [JsonPropertyName("lowConfidenceFalsePositive")]
    public double LowConfidenceFalsePositive { get; init; }
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

/// <summary>
/// Matching behaviour introduced after the official-v1/v2 freeze. Every option defaults to the
/// frozen behaviour, so only profiles that opt in (official-v3) change how findings are matched.
/// </summary>
public sealed class ScoringMatchingOptions
{
    /// <summary>
    /// Aliases, evidence anchors, CWEs and symbols must match whole words/identifiers (plural
    /// "s"/"es" tolerated). Frozen profiles match raw substrings, so "CWE-78" also hits "CWE-787",
    /// "parse" hits "parse_factor", "TEMP" hits "attempts" and "system" hits "system_clock".
    /// </summary>
    [JsonPropertyName("wordBoundaryTerms")]
    public bool WordBoundaryTerms { get; init; }

    /// <summary>
    /// Normalizes quoted evidence before it is looked up in the source: literal "\n"/"\t" escapes
    /// from double-escaped JSON become real line breaks/tabs, and the "0317: " line-number prefix
    /// the prompt shows before each source line is removed, so faithful quotes still match.
    /// </summary>
    [JsonPropertyName("normalizeQuotedEvidence")]
    public bool NormalizeQuotedEvidence { get; init; }

    /// <summary>
    /// Assigns findings to vulnerabilities with a globally optimal one-to-one matching (most
    /// matched vulnerabilities, then most full matches, then highest scores) instead of giving
    /// each finding only its single best vulnerability. A finding is a duplicate only when every
    /// vulnerability it matches is already credited to another finding.
    /// </summary>
    [JsonPropertyName("optimalAssignment")]
    public bool OptimalAssignment { get; init; }

    /// <summary>
    /// With <see cref="OptimalAssignment"/>: a finding may only be credited for a vulnerability
    /// whose match score is at most this far below the finding's own best match. This keeps a
    /// finding about one bug from being re-labelled as a weakly related other bug just to raise
    /// the number of credited vulnerabilities.
    /// </summary>
    [JsonPropertyName("assignmentScoreMargin")]
    public double AssignmentScoreMargin { get; init; }

    /// <summary>
    /// With <see cref="OptimalAssignment"/>: a finding may be credited for a vulnerability other
    /// than its best match only when it points more precisely at that vulnerability's code
    /// (location signal at least this value and higher than for its best match). A finding at
    /// <c>append_override_audit_event</c> may thus move from an already credited command
    /// injection to the insecure-temp-file bug located there, but not to a bug it merely names.
    /// 0 disables the rule.
    /// </summary>
    [JsonPropertyName("alternativeMinimumLocation")]
    public double AlternativeMinimumLocation { get; init; }

    /// <summary>
    /// When a finding states a location (line or symbol), that location must point at the
    /// vulnerability (location signal at least this value) for a true positive. 0 disables it.
    /// </summary>
    [JsonPropertyName("minimumReportedLocation")]
    public double MinimumReportedLocation { get; init; }

    /// <summary>
    /// A reported line range wider than this many lines (e.g. "1-908") counts only half as a
    /// line overlap: it overlaps every vulnerability without locating any. 0 disables it.
    /// </summary>
    [JsonPropertyName("maxPreciseLineSpan")]
    public int MaxPreciseLineSpan { get; init; }
}

public static class ScoringProfiles
{
    public const string OfficialV1Name = "official-v1";
    public const int OfficialV1Version = 1;
    public const string OfficialV1EngineVersion = "official-v1-freeze-2026-06-28";
    public const string OfficialV2Name = "official-v2";
    public const int OfficialV2Version = 1;
    public const string OfficialV2EngineVersion = "official-v2-gated-2026-06-28";
    public const string OfficialV3Name = "official-v3";
    public const int OfficialV3Version = 1;
    public const string OfficialV3EngineVersion = "official-v3-matching-2026-09-24";
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

    /// <summary>
    /// official-v1 weights, thresholds and points with the post-audit matching fixes: whole-word
    /// terms, line-prefix-tolerant evidence, optimal assignment and reported-location consistency.
    /// </summary>
    public static ScoringProfile OfficialV3 { get; } = new()
    {
        Name = OfficialV3Name,
        Version = OfficialV3Version,
        EngineVersion = OfficialV3EngineVersion,
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
        Points = new ScoringPointSchedule
        {
            FullTp = 5.0,
            PartialTp = 2.5,
            FalsePositive = -2.0,
            Duplicate = -1.0,
            SeverityMismatch = -1.0,
            LowConfidenceFalsePositive = -1.0
        },
        Matching = new ScoringMatchingOptions
        {
            WordBoundaryTerms = true,
            NormalizeQuotedEvidence = true,
            OptimalAssignment = true,
            AssignmentScoreMargin = 0.15,
            AlternativeMinimumLocation = 0.9,
            MinimumReportedLocation = 0.2,
            MaxPreciseLineSpan = 120
        }
    };

    public static IReadOnlyList<ScoringProfile> All { get; } = [OfficialV1, OfficialV2, OfficialV3];

    private static ScoringPointSchedule DefaultPoints => new()
    {
        FullTp = 5.0,
        PartialTp = 2.5,
        FalsePositive = -2.0,
        Duplicate = -1.0,
        SeverityMismatch = -1.0
    };

    /// <summary>
    /// The newest official profile. It is the default for new runs, fixture scoring and the
    /// comparison filter, so "the current evaluation" always means the latest scorer.
    /// </summary>
    public static ScoringProfile Latest => All[^1];

    public static string DefaultName => Latest.Name;

    /// <summary>Resolves a profile name; an empty name means <see cref="Latest"/>.</summary>
    public static bool TryGet(string? name, out ScoringProfile profile)
    {
        var normalized = (name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            profile = Latest;
            return true;
        }

        var known = All.FirstOrDefault(candidate => string.Equals(candidate.Name, normalized, StringComparison.OrdinalIgnoreCase));
        profile = known ?? Latest;
        return known is not null;
    }

    public static ScoringProfile Get(string? name)
        => TryGet(name, out var profile)
            ? profile
            : throw new ArgumentException($"Unknown scoring profile '{name}'. Currently supported: {string.Join(", ", All.Select(p => p.Name))}.");

    /// <summary>The newest known official profile among <paramref name="available"/>, else the first one.</summary>
    public static string? PreferredOf(IEnumerable<string?> available)
    {
        var names = available.Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList();
        for (var i = All.Count - 1; i >= 0; i--)
        {
            var match = names.FirstOrDefault(name => string.Equals(name, All[i].Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        return names.FirstOrDefault();
    }

    public static bool IsOfficialComparableProfile(string? profile)
        => All.Any(candidate => string.Equals(candidate.Name, profile, StringComparison.OrdinalIgnoreCase));

    public static bool IsOfficialComparableIdentity(
        string? profile,
        int profileVersion,
        string? engineVersion,
        int scoreSchemaVersion)
    {
        if (scoreSchemaVersion != ScoreSchemaVersion
            || !IsOfficialComparableProfile(profile)
            || !TryGet(profile, out var known))
        {
            return false;
        }

        return profileVersion == known.Version
               && string.Equals(engineVersion, known.EngineVersion, StringComparison.Ordinal);
    }
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
    public const string TruthAuditV2 = "truth_audit_v2";
    public const string CurrentTruthAudit = TruthAuditV2;
    public const string Fixture = "fixture";
    public const string ReasoningDisclosure = "reasoning_disclosure";
    public const string Unknown = "unknown";

    public const string CustomPrefix = "custom:";

    /// <summary>
    /// The prompt version of a detection prompt file: the bundled version when the file content
    /// equals the bundled asset, otherwise "custom:&lt;file name&gt;".
    /// </summary>
    public static string ForPromptFile(string promptPath, string bundledPath, string bundledVersion)
        => FilesIdentical(promptPath, bundledPath) ? bundledVersion : CustomPrefix + Path.GetFileName(promptPath);

    public static bool FilesIdentical(string left, string right)
    {
        try
        {
            if (string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return File.Exists(left) && File.Exists(right)
                   && File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool IsTruthAudit(string? promptVersion)
    {
        return string.Equals(promptVersion, TruthAuditV1, StringComparison.OrdinalIgnoreCase)
               || string.Equals(promptVersion, TruthAuditV2, StringComparison.OrdinalIgnoreCase);
    }

    public static string ResolveTruthAudit(
        string? explicitVersion,
        string? promptPath,
        string? schemaPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitVersion))
        {
            return explicitVersion.Trim();
        }

        var promptFile = Path.GetFileName(promptPath ?? string.Empty);
        var schemaFile = Path.GetFileName(schemaPath ?? string.Empty);
        if (string.Equals(promptFile, "truth_audit_v2.md", StringComparison.OrdinalIgnoreCase)
            && string.Equals(schemaFile, "truth_audit_v2.schema.json", StringComparison.OrdinalIgnoreCase))
        {
            return TruthAuditV2;
        }

        if (string.Equals(promptFile, "truth_audit_v1.md", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(schemaFile, "truth_audit.schema.json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(schemaFile, "truth_audit_v1.schema.json", StringComparison.OrdinalIgnoreCase)))
        {
            return TruthAuditV1;
        }

        return Unknown;
    }

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
