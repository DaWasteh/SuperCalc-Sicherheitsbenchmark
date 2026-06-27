using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public sealed class GroundTruthDocument
{
    [JsonPropertyName("benchmark_id")]
    public string BenchmarkId { get; set; } = string.Empty;

    [JsonPropertyName("source_file")]
    public string SourceFile { get; set; } = string.Empty;

    [JsonPropertyName("source_sha256")]
    public string SourceSha256 { get; set; } = string.Empty;

    [JsonPropertyName("policy")]
    public GroundTruthPolicy? Policy { get; set; }

    [JsonPropertyName("vulnerabilities")]
    public List<VulnerabilityDefinition> Vulnerabilities { get; set; } = [];
}

public sealed class GroundTruthPolicy
{
    [JsonPropertyName("hidden_from_model")]
    public bool HiddenFromModel { get; set; }

    [JsonPropertyName("runs")]
    public List<string> Runs { get; set; } = [];

    [JsonPropertyName("scoring_document")]
    public string? ScoringDocument { get; set; }
}

public sealed class VulnerabilityDefinition
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; set; } = string.Empty;

    [JsonPropertyName("cwe")]
    public List<string> Cwe { get; set; } = [];

    [JsonPropertyName("strict_scoreable")]
    public bool StrictScoreable { get; set; } = true;

    [JsonPropertyName("locations")]
    public List<CodeLocation> Locations { get; set; } = [];

    [JsonPropertyName("aliases")]
    public List<string> Aliases { get; set; } = [];

    [JsonPropertyName("required_evidence")]
    public List<string> RequiredEvidence { get; set; } = [];

    [JsonPropertyName("trigger")]
    public string? Trigger { get; set; }
}

public sealed class CodeLocation
{
    [JsonPropertyName("file")]
    public string File { get; set; } = string.Empty;

    [JsonPropertyName("symbol")]
    public string Symbol { get; set; } = string.Empty;

    [JsonPropertyName("line_start")]
    public int LineStart { get; set; }

    [JsonPropertyName("line_end")]
    public int LineEnd { get; set; }
}

public sealed class LlmFinding
{
    public int Index { get; set; }
    public string Title { get; set; } = string.Empty;
    public string VulnerabilityType { get; set; } = string.Empty;
    public string Cwe { get; set; } = string.Empty;
    public string Severity { get; set; } = "Unknown";
    public double Confidence { get; set; } = 0.75;
    public string File { get; set; } = string.Empty;
    public int LineStart { get; set; }
    public int LineEnd { get; set; }
    public string FunctionOrSymbol { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public string Impact { get; set; } = string.Empty;
    public string Trigger { get; set; } = string.Empty;
    public string Fix { get; set; } = string.Empty;
    public string RawText { get; set; } = string.Empty;
}

public sealed class ParseResult
{
    public List<LlmFinding> Findings { get; init; } = [];
    public string AssistantContent { get; init; } = string.Empty;
    public bool ParsedJson { get; init; }
    public bool UsedMarkdownJsonBlock { get; init; }
    public bool UsedTextFallback { get; init; }
    public string ParseMode { get; init; } = string.Empty;
    public string? Warning { get; init; }
}

public sealed class ValidationIssue
{
    public string Severity { get; init; } = "Error";
    public string Message { get; init; } = string.Empty;
}

public sealed class GroundTruthValidationResult
{
    public bool IsValid => Issues.All(i => !string.Equals(i.Severity, "Error", StringComparison.OrdinalIgnoreCase));
    public string ActualSourceSha256 { get; init; } = string.Empty;
    public string ExpectedSourceSha256 { get; init; } = string.Empty;
    public int VulnerabilityCount { get; init; }
    public List<ValidationIssue> Issues { get; init; } = [];
}

public enum FindingClassification
{
    FullTruePositive,
    PartialTruePositive,
    FalsePositive,
    Duplicate,
    IgnoredLowConfidence
}

public sealed class SignalScore
{
    public string Name { get; init; } = string.Empty;
    public double Weight { get; init; }
    public double Value { get; init; }
    public double Weighted => Weight * Value;
    public string Detail { get; init; } = string.Empty;
}

public sealed class FindingScore
{
    public int FindingIndex { get; init; }
    public string FindingTitle { get; init; } = string.Empty;
    public string? MatchedVulnerabilityId { get; set; }
    public string? MatchedVulnerabilityTitle { get; set; }
    public double MatchScore { get; set; }
    public FindingClassification Classification { get; set; }
    public double Points { get; set; }
    public bool Duplicate { get; set; }
    public bool SeverityMismatch { get; set; }
    public List<SignalScore> Signals { get; set; } = [];
    public string Reason { get; set; } = string.Empty;
}

public sealed class VulnerabilityScore
{
    public string Id { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public bool Found { get; set; }
    public bool Partial { get; set; }
    public int? FindingIndex { get; set; }
    public double MatchScore { get; set; }
}

public sealed class ScoringResult
{
    public string RunName { get; init; } = string.Empty;
    public int ScoreableVulnerabilityCount { get; init; }
    public int FindingCount { get; init; }
    public int FullTruePositives { get; init; }
    public int PartialTruePositives { get; init; }
    public int FalsePositives { get; init; }
    public int Duplicates { get; init; }
    public int IgnoredLowConfidence { get; init; }
    public int Missed { get; init; }
    public double RawPoints { get; init; }
    public double ScorePercent { get; init; }
    public double Precision { get; init; }
    public double Recall { get; init; }
    public double F1 { get; init; }
    public List<FindingScore> Findings { get; init; } = [];
    public List<VulnerabilityScore> Vulnerabilities { get; init; } = [];
}

public sealed class RunComparison
{
    public List<string> KeptTruePositiveIds { get; init; } = [];
    public List<string> DroppedTruePositiveIds { get; init; } = [];
    public List<string> AddedTruePositiveIds { get; init; } = [];
    public int Run1FalsePositives { get; init; }
    public int Run2FalsePositives { get; init; }
    public int FalsePositiveReduction { get; init; }
    public double TruePositiveRetention { get; init; }
}

public sealed class ReasoningDisclosureDiagnostics
{
    public bool HasVisibleReasoning { get; init; }
    public string Summary { get; init; } = string.Empty;
    public int ReasoningParsedFindingCount { get; init; }
    public int OutputParsedFindingCount { get; init; }
    public int ReasoningTruePositiveCount { get; init; }
    public int OutputTruePositiveCount { get; init; }
    public int ReasoningOnlyTruePositiveCount { get; init; }
    public int OutputOnlyTruePositiveCount { get; init; }
    public double? ReasoningToOutputCoverage { get; init; }
    public int ReasoningFalsePositives { get; init; }
    public int OutputFalsePositives { get; init; }
    public bool ReasoningParsedJson { get; init; }
    public bool ReasoningUsedTextFallback { get; init; }
    public string? ReasoningParseWarning { get; init; }
    public List<string> ReasoningTruePositiveIds { get; init; } = [];
    public List<string> OutputTruePositiveIds { get; init; } = [];
    public List<string> ReasoningOnlyTruePositiveIds { get; init; } = [];
    public List<string> OutputOnlyTruePositiveIds { get; init; } = [];
}

public sealed record ChatCompletionResult
{
    public string AssistantContent { get; init; } = string.Empty;
    public string ReasoningContent { get; init; } = string.Empty;
    public string RawResponse { get; init; } = string.Empty;
    public string RequestJson { get; init; } = string.Empty;
    public string FinishReason { get; init; } = string.Empty;
    public bool LoopDetected { get; init; }
    public string LoopDiagnosticsSummary { get; init; } = string.Empty;
    public bool UsedResponseFormat { get; init; }
    public bool RetriedWithoutResponseFormat { get; init; }
    public bool UsedThinkingControl { get; init; }
    public bool RetriedWithoutThinkingControl { get; init; }
}

public enum ChatStreamDeltaKind
{
    AttemptStart,
    Reasoning,
    Content,
    LoopDetected
}

/// <summary>
/// A single incremental update emitted while streaming a chat completion.
/// AttemptStart signals that a (possibly retried) request attempt has begun and the
/// live buffers should be cleared; Reasoning/Content carry appended token text.
/// </summary>
public sealed record ChatStreamDelta
{
    public ChatStreamDeltaKind Kind { get; init; }
    public string Text { get; init; } = string.Empty;
    public string AttemptLabel { get; init; } = string.Empty;
    public int AttemptIndex { get; init; }
    public int AttemptCount { get; init; }

    public static ChatStreamDelta AttemptStart(string label, int index, int count) => new()
    {
        Kind = ChatStreamDeltaKind.AttemptStart,
        AttemptLabel = label,
        AttemptIndex = index,
        AttemptCount = count
    };

    public static ChatStreamDelta Reasoning(string text) => new()
    {
        Kind = ChatStreamDeltaKind.Reasoning,
        Text = text
    };

    public static ChatStreamDelta Content(string text) => new()
    {
        Kind = ChatStreamDeltaKind.Content,
        Text = text
    };

    public static ChatStreamDelta LoopDetected(string text) => new()
    {
        Kind = ChatStreamDeltaKind.LoopDetected,
        Text = text
    };
}

public sealed class BenchmarkRunArtifacts
{
    public string RunName { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public long DurationMs => StartedAt == default || CompletedAt == default
        ? 0
        : Math.Max(0, (long)(CompletedAt - StartedAt).TotalMilliseconds);
    public string Prompt { get; init; } = string.Empty;
    public string Response { get; init; } = string.Empty;
    public string ReasoningContent { get; init; } = string.Empty;
    public string RawResponse { get; init; } = string.Empty;
    public string RequestJson { get; init; } = string.Empty;
    public string FinishReason { get; init; } = string.Empty;
    public bool LoopDetected { get; init; }
    public string LoopDiagnosticsSummary { get; init; } = string.Empty;
    public bool UsedResponseFormat { get; init; }
    public bool RetriedWithoutResponseFormat { get; init; }
    public bool UsedThinkingControl { get; init; }
    public bool RetriedWithoutThinkingControl { get; init; }
    public ParseResult Parse { get; init; } = new();
    public ScoringResult Score { get; init; } = new();
    public ReasoningDisclosureDiagnostics ReasoningDisclosure { get; init; } = new();
}

public sealed class BenchmarkRunResult
{
    public string ToolVersion { get; init; } = "0.1.0";
    public string BenchmarkId { get; init; } = string.Empty;
    public string BenchmarkProfile { get; init; } = "official";
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; set; }
    public long DurationMs => StartedAt == default || CompletedAt == default
        ? 0
        : Math.Max(0, (long)(CompletedAt - StartedAt).TotalMilliseconds);
    public string ServerUrl { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int MaxTokens { get; init; }
    public int TimeoutSeconds { get; init; }
    public int Seed { get; init; }
    public bool SkipResponseFormat { get; init; }
    public bool DisableThinking { get; init; }
    public bool AbortOnLoop { get; init; }
    public int? ServerContextSize { get; init; }
    public string SourceFile { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public string ExpectedSourceSha256 { get; init; } = string.Empty;
    public bool SourceHashMatches { get; init; }
    public BenchmarkRunArtifacts Run1 { get; set; } = new();
    public BenchmarkRunArtifacts? Run2 { get; set; }
    public RunComparison? Comparison { get; set; }
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>Path of the archive scorecard written for this run, if archiving was enabled.</summary>
    public string? ArchivedRecordPath { get; set; }
}

public sealed class BenchmarkOptions
{
    public string ServerUrl { get; init; } = "http://127.0.0.1:1234";
    public string Model { get; init; } = string.Empty;
    public string SourcePath { get; init; } = "enhanced_calc.cpp";
    public string GroundTruthPath { get; init; } = Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json");
    public string AnalysisPromptPath { get; init; } = Path.Combine("benchmarks", "supercalc-v3", "prompts", "analysis_v1.md");
    public string SelfValidatePromptPath { get; init; } = Path.Combine("benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md");
    public string SchemaPath { get; init; } = Path.Combine("benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json");
    public string? OutputDirectory { get; init; }
    public double Temperature { get; init; } = 0.0;
    public double TopP { get; init; } = 1.0;
    public int MaxTokens { get; init; } = -1;
    public int Seed { get; init; } = 12345;
    public TimeSpan Timeout { get; init; } = BenchmarkDefaults.OfficialRequestTimeout;
    public bool AllowHashMismatch { get; init; }
    public bool SkipResponseFormat { get; init; }
    public bool DisableThinking { get; init; }
    public string BenchmarkProfile { get; init; } = "official";

    /// <summary>
    /// Stream completions through a repetition guard and close the request early when
    /// final assistant content gets stuck in a likely model loop. Visible reasoning_content
    /// is not live-aborted; it is kept for diagnostics so reasoning models can finish.
    /// </summary>
    public bool AbortOnLoop { get; init; } = true;

    /// <summary>
    /// When set, each completed run is archived as a compact scorecard under this folder
    /// (grouped by model family + quant) for later comparison. Null disables archiving.
    /// </summary>
    public string? ArchiveDirectory { get; init; }

    /// <summary>
    /// Optional manual quant label (e.g. "Q4_K_M") used when the model id does not encode the
    /// quantization. Ignored when the quant can be detected from the model id.
    /// </summary>
    public string? QuantOverride { get; init; }
}
