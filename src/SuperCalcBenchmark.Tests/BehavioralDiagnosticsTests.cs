using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

internal static partial class TestRunner
{
    private static void DiagnosticsParseTransitionContract()
    {
        var improved = BehavioralDiagnosticsCalculator.ParseTransition("none", "json");
        Assert(improved.Transition == "Improved" && improved.Delta == 1, "none→json must improve by one normalized unit");
        Assert(BehavioralDiagnosticsCalculator.ParseTransition("markdown_json", "balanced_json").Transition == "Unchanged", "recovered modes have equal quality");
        Assert(BehavioralDiagnosticsCalculator.ParseTransition("future", "json").Delta is null, "unknown modes must have null delta");
        Assert(BehavioralDiagnosticsCalculator.ParseTransition("json", "text_fallback").Transition == "Degraded", "json→fallback must degrade");
    }

    private static void DiagnosticsCorrectionsContract()
    {
        const string output = "The exact previous security claim appears here.";
        var rows = BehavioralDiagnosticsCalculator.EvaluateCorrections([
            new() { PreviousClaim = "exact previous security claim", CorrectedClaim = "corrected security claim", CorrectionType = "evidence" },
            new() { PreviousClaim = "exact previous security claim", CorrectedClaim = "corrected security claim", CorrectionType = "evidence" },
            new() { PreviousClaim = "short", CorrectedClaim = "different", CorrectionType = "severity" },
            new() { PreviousClaim = "exact previous security claim", CorrectedClaim = "different", CorrectionType = "invented" },
            new() { PreviousClaim = "exact previous security claim", CorrectedClaim = "different type", CorrectionType = "vulnerability_type" }
        ], output);
        Assert(rows[0].ProvenanceValid, "exact, material, typed correction should have valid provenance");
        Assert(rows[1].Duplicate && !rows[1].ProvenanceValid, "duplicate tuple must be rejected");
        Assert(!rows[2].ProvenanceValid && !rows[3].ProvenanceValid, "short quotes and unknown types must be rejected");
        Assert(!rows[4].ProvenanceValid && rows[4].Type == AuditCorrectionType.Invalid, "vulnerability_type is not a schema correction type and must not map to CWE");
        Assert(rows[0].PreviousClaim == "exact previous security claim", "raw value must be preserved except trimming");
    }

    private static void DiagnosticsRevisionSelectivityContract()
    {
        static BenchmarkRunArtifacts Art(params (LlmFinding raw, FindingScore score)[] items) => new()
        {
            Parse = new ParseResult { Findings = items.Select(x => x.raw).ToList() },
            Score = new ScoringResult
            {
                Findings = items.Select(x => x.score).ToList(),
                Vulnerabilities = [new() { Id = "GT-1", Cwe = ["CWE-79"], Found = items.Any(x => x.score.MatchedVulnerabilityId == "GT-1"), Partial = items.Any(x => x.score.MatchedVulnerabilityId == "GT-1" && x.score.Classification == FindingClassification.PartialTruePositive) }]
            }
        };
        static LlmFinding Raw(string title, string evidence, double confidence = .5, int index = 0) => new() { Index = index, Title = title, Evidence = evidence, Severity = "High", Cwe = "CWE-79", Confidence = confidence, File = "a.cpp", FunctionOrSymbol = "parse" };
        static FindingScore Sc(string title, FindingClassification classification, string? id = null, double evidence = 0, double location = 0, int index = 0) => new() { FindingIndex = index, FindingTitle = title, Classification = classification, MatchedVulnerabilityId = id, EvidenceFidelity = evidence, LocationAccuracy = location, ReportedFile = "a.cpp", ReportedSymbol = "parse", FalsePositiveCategory = "unsupported_by_code" };

        var first = Art((Raw("real overflow", "old", .5, 7), Sc("real overflow", FindingClassification.PartialTruePositive, "GT-1", .4, .8, 7)), (Raw("imaginary race", "same", .8, 7), Sc("imaginary race", FindingClassification.FalsePositive, index: 7)), (Raw("duplicate report", "same"), Sc("duplicate report", FindingClassification.Duplicate)));
        var second = Art((Raw("real overflow", "better", 1, 7), Sc("real overflow", FindingClassification.FullTruePositive, "GT-1", .8, .8, 7)), (Raw("imaginary race edited", "changed", .2, 7), Sc("imaginary race edited", FindingClassification.FalsePositive, index: 7)), (Raw("new unsupported", "new"), Sc("new unsupported", FindingClassification.FalsePositive)));
        var result = BehavioralDiagnosticsCalculator.RevisionSelectivity(first, second);
        Assert(result.Items.Count == 4, "each TP, retained FP, dropped duplicate, and added FP must be counted once despite duplicate indices");
        Assert(result.Items.Any(x => x.Kind == "vulnerability" && x.Outcome == RevisionOutcome.Beneficial), "TP credit/evidence improvement must be beneficial");
        Assert(result.Items.Any(x => x.Kind.Contains("falsepositive_to_falsepositive") && x.Outcome != RevisionOutcome.Untouched), "retained edited FP must be paired and classified");
        Assert(result.Items.Any(x => x.Kind.Contains("duplicate_to_dropped") && x.Outcome == RevisionOutcome.Beneficial), "dropped duplicate must be beneficial");
        Assert(result.Items.Any(x => x.Kind.Contains("added_to_falsepositive") && x.Outcome == RevisionOutcome.Harmful), "added FP must be harmful");

        var unchanged = BehavioralDiagnosticsCalculator.RevisionSelectivity(first, first);
        Assert(unchanged.Items.All(x => x.Outcome == RevisionOutcome.Untouched), "identical full fingerprints must be untouched");
        var ineffective = BehavioralDiagnosticsCalculator.RevisionSelectivity(Art((Raw("imaginary race", "a"), Sc("imaginary race", FindingClassification.FalsePositive))), Art((Raw("imaginary race", "b"), Sc("imaginary race", FindingClassification.FalsePositive))));
        Assert(ineffective.Ineffective == 1, "equal-quality fingerprint edits must be ineffective");
    }

    private static void DiagnosticsConfidenceContract()
    {
        var parse = new ParseResult { ParseMode = "json", Findings = [
            new() { Index = 0, Confidence = 1, ConfidenceOrigin = ConfidenceOrigin.Reported, RawText = "{\"confidence\":1}" },
            new() { Index = 1, Confidence = .75, ConfidenceOrigin = ConfidenceOrigin.JsonDefault, RawText = "{\"title\":\"confidence in evidence\",\"evidence\":\"confidence\"}" }
        ] };
        var score = new ScoringResult { Findings = [
            new() { FindingIndex = 0, Classification = FindingClassification.PartialTruePositive },
            new() { FindingIndex = 1, Classification = FindingClassification.Duplicate }
        ] };
        var result = BehavioralDiagnosticsCalculator.Calibrate(parse, score);
        Assert(result.JoinableCount == 2 && result.ReportedCount == 1 && result.ConfidenceCoverage == .5, "reported origin coverage mismatch");
        Assert(result.ReportedOnly.Count == 1 && result.AllIncludingImputed.Count == 2, "headline and sensitivity populations must differ");
        Assert(result.ReportedOnly.Bins[9].Count == 1, "p=1 belongs in final bin");
        Assert(Math.Abs(result.ReportedOnly.MeanObservedCredit!.Value - .5) < 1e-12, "partial TP outcome credit must be .5");
        Assert(BehavioralDiagnosticsCalculator.Calibrate(new(), new()).ReportedOnly.SoftBrier is null, "empty calibration metrics must be null");
    }

    private static void DiagnosticsCweContract()
    {
        var values = BehavioralDiagnosticsCalculator.Cwes("CWE_079, cwe:79 and CWE-89");
        Assert(values.SetEquals(["CWE-79", "CWE-89"]), "CWE IDs must normalize integers and deduplicate");
    }
}
