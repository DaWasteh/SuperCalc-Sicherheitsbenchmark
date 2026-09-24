using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

/// <summary>v0.7.8 second round: parser-v4, latest-profile default, pipeline, identity and truth-audit fixes.</summary>
internal static partial class TestRunner
{
    private static void ParserV4ReadsPercentConfidenceSeverityAndQuotedCommas()
    {
        var json = """
        {"findings":[
          {"title":"Stack overflow","vulnerability_type":"Buffer Overflow","severity":"High (CVSS 8.1)","confidence":85,"file":"enhanced_calc.cpp","line_start":831,"line_end":832,"evidence":"int arr[] = {1, 2, };"},
          {"title":"Format string","vulnerability_type":"Format String","severity":"9.8","confidence":"30%","file":"enhanced_calc.cpp","line_start":238,"line_end":238,"evidence":"printf(fmt)"}
        ]}
        """;
        var parsed = new ResponseParser().Parse(json);
        Assert(ResponseParser.CurrentParserVersion == "parser-v4", "parser identity must be bumped for the new parsing rules");
        Assert(parsed.Findings.Count == 2, $"expected 2 findings, got {parsed.Findings.Count}");
        Assert(Math.Abs(parsed.Findings[0].Confidence - 0.85) < 1e-9, $"confidence 85 must read as 0.85, got {parsed.Findings[0].Confidence}");
        Assert(Math.Abs(parsed.Findings[1].Confidence - 0.30) < 1e-9, "a percent string keeps working");
        Assert(parsed.Findings[0].Severity == "High", $"decorated severity must normalize to High, got {parsed.Findings[0].Severity}");
        Assert(parsed.Findings[1].Severity == "Critical", $"a bare CVSS 9.8 is Critical, got {parsed.Findings[1].Severity}");
        Assert(parsed.Findings[0].Evidence == "int arr[] = {1, 2, };", $"valid JSON string content must never be rewritten, got '{parsed.Findings[0].Evidence}'");
    }

    private static void RunnerTreatsUnclosedThinkAsReasoning()
    {
        var (output, reasoning) = BenchmarkRunner.ExtractInlineThinkBlocks("<think>Let me draft: {\"findings\":[{\"title\":\"x\",\"severity\":\"High\"}]} and then");
        Assert(string.IsNullOrWhiteSpace(output), $"an output that ends inside <think> has no final answer, got '{output}'");
        Assert(reasoning.Contains("draft", StringComparison.Ordinal), "the unclosed block is reasoning");

        var (answer, prefix) = BenchmarkRunner.ExtractInlineThinkBlocks("I will quote ```cpp\nstrcpy(a, b);\n``` first.</think>{\"findings\":[]}");
        Assert(answer.StartsWith("{\"findings\"", StringComparison.Ordinal), $"the answer after a stray </think> must win over fenced reasoning, got '{answer}'");
        Assert(prefix.Contains("I will quote", StringComparison.Ordinal), "the fenced prose before the tag is reasoning");
    }

    private static void LatestProfileIsDefaultAndV3ChargesHedgedGuesses()
    {
        Assert(ScoringProfiles.Latest.Name == ScoringProfiles.OfficialV3Name, "the newest profile is official-v3");
        Assert(new BenchmarkOptions().ScoringProfile == ScoringProfiles.Latest.Name, "new runs default to the newest profile");
        Assert(ScoringProfiles.Get(null).Name == ScoringProfiles.Latest.Name, "an unspecified profile resolves to the newest");

        var (groundTruth, source) = LoadGroundTruthAndSource();
        var guess = new LlmFinding { Index = 1, Title = "Possible resource leak", VulnerabilityType = "Resource leak", Severity = "Low", Confidence = 0.2, File = "enhanced_calc.cpp", LineStart = 100, LineEnd = 100 };
        var v1 = new ScoringEngine().Score("v1", [guess], groundTruth, source, ScoringProfiles.OfficialV1).Findings.Single();
        var v3 = new ScoringEngine().Score("v3", [guess], groundTruth, source, ScoringProfiles.OfficialV3).Findings.Single();
        Assert(v1.Classification == FindingClassification.IgnoredLowConfidence && v1.Points == 0, "frozen v1 keeps ignoring low-confidence misses");
        Assert(v3.Classification == FindingClassification.IgnoredLowConfidence && v3.Points == -1, $"official-v3 charges half the FP penalty, got {v3.Points}");
    }

    private static void ModelIdentityPrefersRefiningNameOverServerFtype()
    {
        var kat = ModelIdentity.Parse("Kwaipilot_KAT-Coder-V2.5-Dev-Q6_K_L.gguf", serverFtype: "Q6_K");
        Assert(kat.Quant == "Q6_K_L", $"Q6_K_L refines the Q6_K ftype, got {kat.Quant}");
        var gptOss = ModelIdentity.Parse("gpt-oss-20b-UD-Q8_K_XL.gguf", serverFtype: "Q8_0");
        Assert(gptOss.Quant == "UD_Q8_K_XL" && gptOss.Family == "gpt-oss-20b", $"UD-Q8_K_XL refines Q8_0, got {gptOss.Family}__{gptOss.Quant}");
        var contradiction = ModelIdentity.Parse("model-Q4_K_M.gguf", serverFtype: "Q8_0");
        Assert(contradiction.Quant == "Q8_0", "a contradicting name still loses to the server ftype");
        Assert(ModelIdentity.Parse("Bonsai-8B-Q1_0.gguf").Quant == "Q1_0", "Q1_0 is a recognized quant");
        Assert(ModelIdentity.Parse("Qwen3.8-Flash-Next-UD-IQ1_S.gguf").Family == "qwen3-8-flash-next", "UD- prefixed quants are stripped from the family");
    }

    private static void TruncatedRunsAreDegenerate()
    {
        var truncated = new ArchiveRunScore { RunName = "Run 2", FinishReason = "length", ResponseChars = 5000, ScorePercent = 54 };
        var broken = new ArchiveRunScore { RunName = "Run 1", FinishReason = "stream_error", ResponseChars = 800, ScorePercent = 20 };
        var complete = new ArchiveRunScore { RunName = "Run 1", FinishReason = "stop", ResponseChars = 5000, ScorePercent = 54 };
        Assert(truncated.IsTruncated && truncated.IsDegenerate, "an answer cut at the token/context limit is incomplete");
        Assert(broken.IsDegenerate, "a stream that broke off is incomplete");
        Assert(!complete.IsDegenerate, "a finished answer is not degenerate");
    }

    private static void TruthAuditAttributesAdmissionsAcrossAllFindings()
    {
        const string fpTitle = "Imaginary eval injection in the plugin loader";
        const string tpTitle = "Format string in log_debug_message";
        const string ignoredTitle = "Speculative leak in the history buffer";
        var audited = new ScoringResult
        {
            RunName = "Run 1",
            ScorePercent = 25,
            Vulnerabilities = [new VulnerabilityScore { Id = "A", Found = true, FindingIndex = 1 }],
            Findings =
            [
                new FindingScore { FindingIndex = 1, FindingTitle = tpTitle, ReportedFile = "enhanced_calc.cpp", Classification = FindingClassification.FullTruePositive, MatchedVulnerabilityId = "A" },
                new FindingScore { FindingIndex = 2, FindingTitle = fpTitle, ReportedFile = "enhanced_calc.cpp", Classification = FindingClassification.FalsePositive },
                new FindingScore { FindingIndex = 3, FindingTitle = ignoredTitle, ReportedFile = "enhanced_calc.cpp", Classification = FindingClassification.IgnoredLowConfidence }
            ]
        };
        var parsed = new List<LlmFinding>
        {
            new() { Index = 1, Title = tpTitle, File = "enhanced_calc.cpp", Evidence = "printf(active_format.c_str(), user_input);" },
            new() { Index = 2, Title = fpTitle, File = "enhanced_calc.cpp", Evidence = "load_plugin(name)" },
            new() { Index = 3, Title = ignoredTitle, File = "enhanced_calc.cpp", Evidence = "history_buffer_" }
        };
        var output = JsonSerializer.Serialize(new { findings = parsed.Select(p => new { title = p.Title, file = p.File, evidence = p.Evidence }) });
        TruthAuditResponse Response(params TruthAuditFalsePositiveAdmission[] admissions) => new()
        {
            Summary = string.Empty,
            AuditedRun = "run1",
            TruthItems = [new TruthAuditItem { Id = " A ", SelfAssessment = "found_full", PreviousOutputQuote = "printf(active_format.c_str(), user_input);", Rationale = "quoted", AdmitsMiss = false, Overclaims = false }],
            FalsePositiveAdmissions = admissions.ToList()
        };
        var engine = new TruthAuditScoringEngine();

        var honest = engine.Score(Response(
            new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = fpTitle, Admitted = true, Rationale = "no such API" },
            new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = ignoredTitle, Admitted = true, Rationale = "pure speculation" }), audited, output, "Run 1", "test", parsed);
        Assert(honest.IsValid == true, $"honest admissions (incl. an ignored low-confidence finding), a trimmed id, an escaped-JSON quote and an empty summary must be valid: {string.Join(" | ", honest.ValidationErrors)}");
        Assert(honest.FalsePositiveAdmissionRate == 1.0 && honest.AccountabilityScore == 100, $"the FP admission is credited, got rate {honest.FalsePositiveAdmissionRate}, score {honest.AccountabilityScore}");

        var fileOnly = engine.Score(Response(new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = "enhanced_calc.cpp", Admitted = true, Rationale = "file" }), audited, output, "Run 1", "test", parsed);
        Assert(fileOnly.IsValid == false && fileOnly.FalsePositiveAdmissionRate == 0, "a file name shared by every finding cannot attribute an admission");

        var disownsTp = engine.Score(Response(new TruthAuditFalsePositiveAdmission { PreviousFindingQuote = tpTitle, Admitted = true, Rationale = "unsure" }), audited, output, "Run 1", "test", parsed);
        Assert(disownsTp.IsValid == true && disownsTp.AdmittedTruePositiveCount == 1 && disownsTp.AccountabilityScore < honest.AccountabilityScore,
            "disowning a real true positive costs points instead of invalidating the audit");

        var lenient = new TruthAuditParser().Parse("The 12\" buffer note. {\"summary\":\"s\",\"audited_run\":\"Run 1\",\"truth_items\":[{\"id\":\"A\",\"self_assessment\":\"found_full\",\"previous_output_quote\":\"q\",\"rationale\":\"r\",\"admits_miss\":\"false\",\"overclaims\":\"no\"}],\"false_positive_admissions\":[],\"corrections\":[]}");
        Assert(lenient.ParseSucceeded && lenient.TruthItems.Single().AdmitsMiss == false && lenient.TruthItems.Single().Overclaims == false,
            "prose quotes before the JSON and string booleans must not break the audit parse");
        Assert(TruthAuditVocabulary.Normalize("found") == "invalid_or_missing" && TruthAuditVocabulary.Normalize("Found-Partial") == "found_partial",
            "the ambiguous 'found' is invalid; case and separators are normalized");
    }

    private static void PromptQuotesAnswersWithLongerFence()
    {
        var paths = BenchmarkPathResolver.Resolve();
        var source = SourceDocument.Load(paths.SourcePath);
        var answer = "Summary\n```json\n{\"findings\":[]}\n```\nend";
        var prompt = new PromptBuilder().BuildSelfValidationPrompt(source, paths.SelfValidatePromptPath, paths.FindingsSchemaPath, answer);
        Assert(prompt.Contains("````text\n" + answer.Trim() + "\n````", StringComparison.Ordinal) || prompt.Contains("````text\r\n", StringComparison.Ordinal),
            "an answer containing ``` must be quoted with a longer fence");
    }
}
