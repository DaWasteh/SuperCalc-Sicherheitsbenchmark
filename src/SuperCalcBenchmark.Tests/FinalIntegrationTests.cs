using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

internal static partial class TestRunner
{
    private static ArchiveRunScore FinalRun(string name, double score, params string[] ids) => new()
    {
        RunName = name, RunKind = name == "Run 2" ? "self_validation" : "blind_analysis",
        ScorePercent = score, OfficialComparable = true, ParseMode = "json", ResponseChars = 10,
        VulnerabilityCredit = ids.ToDictionary(x => x, _ => 1d, StringComparer.OrdinalIgnoreCase)
    };

    private static BehavioralDiagnosticsEnvelope FinalDiagnostics(int ordinal, int inflation, double? normalized, int confusion,
        int calibrationCount = 0, double confidence = 0, double outcome = 0,
        TruthAuditValidityState state = TruthAuditValidityState.Valid, bool eligible = true) => new()
    {
        TruthAudit = new TruthMetricDiagnostics
        {
            Validity = new TruthAuditValidity { MetricEligible = eligible, State = state, ExpectedItemCount = ordinal },
            OrdinalEligibleCount = ordinal, InflationCount = inflation, InflationMagnitude = inflation,
            NormalizedInflation = normalized, LaunderingPrevalence = 0, ExplicitFlagConsistencyRate = 1,
            Confusion = [new(AuditActualStatus.FoundFull, AuditAssessment.FoundFull, confusion)]
        },
        Run1Confidence = new ConfidenceCalibrationDiagnostics
        {
            ReportedOnly = new CalibrationMetricSet
            {
                Count = calibrationCount,
                SoftBrier = calibrationCount == 0 ? null : Math.Pow(confidence - outcome, 2),
                Bins = calibrationCount == 0 ? [] : [new CalibrationBin { Index = 5, Count = calibrationCount, SumConfidence = confidence * calibrationCount, SumOutcomeCredit = outcome * calibrationCount }]
            }
        }
    };

    private static ArchiveRecord FinalRecord(string id, double score, BehavioralDiagnosticsEnvelope? diagnostics, bool audit = false,
        string[]? run1Ids = null, string[]? run2Ids = null)
    {
        var record = new ArchiveRecord { RecordId = id, ModelFamily = "fixture", Quant = "Q", GroupKey = "fixture__Q", BehavioralDiagnostics = diagnostics };
        record.Runs.Add(FinalRun("Run 1", score - 1, run1Ids ?? []));
        record.Runs.Add(FinalRun("Run 2", score, run2Ids ?? []));
        if (audit) record.Runs.Add(new ArchiveRunScore { RunName = "Run 3", RunKind = "truth_audit", ResponseChars = 10, ParseMode = "json", TruthAudit = new TruthAuditResult { AccountabilityScore = score, TruthAuditAccuracy = score / 100 } });
        return record;
    }

    private static ArchiveGroup FinalGroup(params ArchiveRecord[] records) => new() { GroupKey = "fixture__Q", ModelFamily = "fixture", Quant = "Q", Records = records.ToList() };

    private static void ComparisonDiagnosticsMicroPoolsAndStability()
    {
        var a = FinalRecord("a", 60, FinalDiagnostics(1, 1, 1, 1, 1, .9, 1));
        var b = FinalRecord("b", 61, FinalDiagnostics(9, 0, 0, 9, 9, .1, 0));
        var invalid = FinalRecord("bad", 62, FinalDiagnostics(100, 100, 1, 100, 100, 1, 0, TruthAuditValidityState.Partial, false));
        var s = ComparisonReport.Build([FinalGroup(a, b, invalid)], "fixture").Series.Single();
        Assert(Math.Abs(s.Honesty!.Value - .95) < 1e-12, "honesty must pool magnitudes/ordinal denominators and exclude ineligible partial records");
        Assert(s.CalibrationObservationCount == 10 && Math.Abs(s.HonestyEce!.Value - .08) < 1e-12, "calibration bins must be micro-pooled, not averaged by record");
        Assert(s.HonestyStabilityN == 1 && s.HonestyStability.HasValue, "two eligible records must yield one usable pair");
        Assert(s.CategoricalItemAgreement is null, "categorical agreement requires shared audited vulnerability IDs");
        var one = ComparisonReport.Build([FinalGroup(a)], "fixture").Series.Single();
        Assert(one.HonestyStabilityN == 0 && one.HonestyStability is null, "one eligible record must have no usable pair and null stability");
        var zero = ComparisonReport.Build([FinalGroup(FinalRecord("z", 1, FinalDiagnostics(1, 1, 1, 1)))], "fixture").Series.Single();
        Assert(zero.Honesty == .5 && zero.Honesty is not null, "a measured value must remain distinct from null");
    }

    private static void ComparisonBestUsesOneDetectionRecord()
    {
        var low = FinalRecord("low", 40, FinalDiagnostics(2, 0, 0, 2), true, ["A", "DROP"], ["A"]);
        var best = FinalRecord("best", 90, FinalDiagnostics(2, 1, .5, 2), false, ["B"], ["B", "ADD"]);
        var s = ComparisonReport.Build([FinalGroup(low, best)], "fixture", ComparisonAggregate.Best).Series.Single();
        Assert(s.Run1Score == 89 && s.Run2Score == 90 && s.Run2DroppedTruePositiveIds.Count == 0 && s.Run2AddedTruePositiveIds.SequenceEqual(["ADD"]), "Best pair metrics and IDs must come only from the detection-best record");
        Assert(s.TruthAuditRunCount == 0 && s.AccountabilityScore == 0, "an unaudited best record must not inherit another record's truth audit");
        Assert(s.DiagnosticsAvailableRunCount == 1 && s.HonestyEligibleCount == 1 && s.Honesty == .75 && s.HonestyStabilityN == 0, "Best diagnostic counts and values must consistently describe the best record");
    }

    private static void LegacyEmptyActualCweIsUnavailable()
    {
        var parse = new ParseResult { Findings = [new LlmFinding { Index = 1, Cwe = "", Severity = "High" }] };
        var score = new ScoringResult
        {
            Findings = [new FindingScore { FindingIndex = 1, Classification = FindingClassification.FullTruePositive, MatchedVulnerabilityId = "V" }],
            Vulnerabilities = [new VulnerabilityScore { Id = "V", Severity = "High", Cwe = [] }]
        };
        var taxonomy = BehavioralDiagnosticsCalculator.Taxonomy(parse, score);
        Assert(taxonomy.CweExactSetRate is null && taxonomy.CweCoverage is null && taxonomy.CweMicroRecall is null, "legacy empty actual CWE must not claim exact-set, coverage, or recall availability");
    }

    private static void GeneratedPresentationAndScriptContracts()
    {
        var measured = FinalRecord("zero", 0, FinalDiagnostics(1, 1, 1, 1));
        var unavailable = FinalRecord("null", -1, null);
        var report = ComparisonReport.Build([FinalGroup(measured, unavailable)], "fixture");
        var writer = new ComparisonHtmlWriter();
        var html = writer.BuildHtml(report);
        var payloadStart = html.IndexOf("<script id=\"data\" type=\"application/json\">", StringComparison.Ordinal);
        Assert(payloadStart >= 0 && html.Contains("Nicht messbar ist n/a; gemessene Null bleibt 0.", StringComparison.Ordinal), "HTML must render measured-zero and unavailable semantics");
        foreach (Match match in Regex.Matches(html, "<script(?<attrs>[^>]*)>(?<code>[\\s\\S]*?)</script>", RegexOptions.IgnoreCase))
        {
            if (match.Groups["attrs"].Value.Contains("application/json", StringComparison.OrdinalIgnoreCase)) continue;
            var psi = new ProcessStartInfo("node", "--check -") { RedirectStandardInput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(psi)!;
            process.StandardInput.Write(match.Groups["code"].Value); process.StandardInput.Close(); process.WaitForExit();
            Assert(process.ExitCode == 0, "generated inline script failed Node syntax check: " + process.StandardError.ReadToEnd());
        }
        var csv = writer.BuildCsv(report);
        Assert(csv.Contains(",0", StringComparison.Ordinal) && csv.Contains(",,", StringComparison.Ordinal), "static CSV must preserve zero while leaving unavailable values blank");
        Assert(html.Contains("String(v ?? \"\")", StringComparison.Ordinal) && !html.Contains("String(v || \"\")", StringComparison.Ordinal), "browser CSV must preserve zero with nullish fallback");
        var markdown = new ReportWriter().BuildMarkdownReport(FakeResult("fixture-Q.gguf", 0, 0, 0, 0, 20));
        Assert(markdown.Contains("n/a", StringComparison.OrdinalIgnoreCase), "ineligible Markdown diagnostics must render n/a rather than a headline value");
        var wpf = File.ReadAllText(Path.Combine("src", "SuperCalcBenchmark.App", "MainWindow.xaml.cs"));
        Assert(wpf.Contains("audit.AuditedRunName", StringComparison.Ordinal) && wpf.Contains("Run 2", StringComparison.Ordinal) && wpf.Contains("CWE", StringComparison.Ordinal), "WPF must show Run 2 audited-target taxonomy text");
    }
}
