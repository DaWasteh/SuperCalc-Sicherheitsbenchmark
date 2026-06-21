using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

// Tests for the run-archive and model-comparison feature. Kept in a separate partial-class
// file so the core TestRunner stays readable; registration happens in TestRunner.Main.
internal static partial class TestRunner
{
    private static readonly string[] FakeVulnIds = ["SC-V3-001", "SC-V3-002", "SC-V3-003", "SC-V3-004"];

    private static void ModelIdentityDetectsQuantAndFamily()
    {
        var a = ModelIdentity.Parse("Qwen3-Coder-30B-A3B-Instruct-Q4_K_M.gguf");
        Assert(a.Quant == "Q4_K_M", $"expected Q4_K_M, got {a.Quant}");
        Assert(a.QuantWasDetected, "quant should be auto-detected");
        Assert(a.Family == "qwen3-coder-30b-a3b-instruct", $"unexpected family: {a.Family}");

        var b = ModelIdentity.Parse("/models/Qwen3-Coder-30B-A3B-Instruct-IQ3_XXS.gguf");
        Assert(b.Quant == "IQ3_XXS", $"expected IQ3_XXS, got {b.Quant}");
        // Different quant of the same model must share a family (so they line up in a
        // comparison) but produce distinct group keys.
        Assert(b.Family == a.Family, "same model, different quant should share a family");
        Assert(a.GroupKey != b.GroupKey, "different quants must produce different group keys");

        var f16 = ModelIdentity.Parse("Phi-3-medium-128k-instruct.FP16.gguf");
        Assert(f16.Quant == "F16", $"FP16 should normalize to F16, got {f16.Quant}");

        var none = ModelIdentity.Parse("some-model-without-quant");
        Assert(!none.QuantWasDetected, "no quant token should be detected");
        Assert(none.Quant == ModelIdentity.UnknownQuant, "missing quant should fall back to unknown-quant");
    }

    private static void ModelIdentityHonorsQuantOverride()
    {
        // When the id encodes no quant, a manual override must win.
        var overridden = ModelIdentity.Parse("internal-alias-model", "Q5_K_M");
        Assert(overridden.Quant == "Q5_K_M", $"override should be used, got {overridden.Quant}");
        Assert(overridden.GroupKey.Contains("Q5_K_M", StringComparison.Ordinal), "group key should reflect the override");
    }

    private static void ArchiveRoundTripsAndGroups()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-archive-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);

            store.Save(FakeResult("Qwen3-Coder-30B-Instruct-Q4_K_M.gguf", 70, 7, 1, 1, 2));
            store.Save(FakeResult("Qwen3-Coder-30B-Instruct-Q4_K_M.gguf", 74, 8, 0, 0, 2));
            store.Save(FakeResult("Qwen3-Coder-30B-Instruct-IQ3_XXS.gguf", 61, 6, 1, 2, 3));
            store.Save(FakeResult("Meta-Llama-3.1-8B-Instruct-Q8_0.gguf", 52, 5, 0, 3, 5));

            var all = store.LoadAll();
            Assert(all.Count == 4, $"expected 4 archived records, got {all.Count}");

            var groups = store.LoadGroups();
            Assert(groups.Count == 3, $"expected 3 model/quant groups, got {groups.Count}");

            var q4 = groups.Single(g => g.Quant == "Q4_K_M" && g.ModelFamily.Contains("qwen3-coder-30b"));
            Assert(q4.RunCount == 2, $"Q4_K_M group should hold 2 runs, got {q4.RunCount}");
            Assert(Math.Abs(q4.AverageScorePercent - 72.0) < 0.001, $"avg should be 72, got {q4.AverageScorePercent}");
            Assert(Math.Abs(q4.BestScorePercent - 74.0) < 0.001, $"best should be 74, got {q4.BestScorePercent}");

            var families = store.LoadFamilies();
            Assert(families.Count == 2, $"expected 2 distinct families, got {families.Count}");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ComparisonAggregatesAndFiltersByFamily()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-compare-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 7, 0, 0, 3));
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 80, 8, 0, 0, 2));
            store.Save(FakeResult("Qwen3-Coder-30B-IQ3_XXS.gguf", 60, 6, 0, 1, 4));
            store.Save(FakeResult("Gemma-2-27B-Q5_K_M.gguf", 55, 5, 0, 2, 5));

            var groups = store.LoadGroups();

            var avg = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Average);
            Assert(avg.Series.Count == 3, $"expected 3 series, got {avg.Series.Count}");
            var q4avg = avg.Series.Single(s => s.Quant == "Q4_K_M");
            Assert(Math.Abs(q4avg.ScorePercent - 75.0) < 0.001, $"Q4 average should be 75, got {q4avg.ScorePercent}");
            Assert(avg.Series[0].ScorePercent >= avg.Series[^1].ScorePercent, "series should be sorted by score desc");

            var best = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Best);
            var q4best = best.Series.Single(s => s.Quant == "Q4_K_M");
            Assert(Math.Abs(q4best.ScorePercent - 80.0) < 0.001, $"Q4 best should be 80, got {q4best.ScorePercent}");

            var qwenOnly = ComparisonReport.Build(groups, "supercalc-v3", ComparisonAggregate.Average, "qwen3-coder-30b");
            Assert(qwenOnly.Series.Count == 2, $"qwen filter should yield 2 quants, got {qwenOnly.Series.Count}");
            Assert(qwenOnly.Series.All(s => s.ModelFamily == "qwen3-coder-30b"), "filtered series must all be qwen");

            Assert(avg.VulnerabilityAxis.Count == FakeVulnIds.Length, $"axis should cover {FakeVulnIds.Length} vulns, got {avg.VulnerabilityAxis.Count}");
            Assert(q4avg.PerVulnerabilityCredit.Count == avg.VulnerabilityAxis.Count, "per-vuln credit must align to the axis length");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    private static void ComparisonHtmlEmbedsParseablePayload()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "supercalc-html-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new ArchiveStore(tempRoot);
            store.Save(FakeResult("Qwen3-Coder-30B-Q4_K_M.gguf", 70, 7, 0, 0, 3));

            var report = ComparisonReport.Build(store.LoadGroups(), "supercalc-v3");
            var writer = new ComparisonHtmlWriter();
            var html = writer.BuildHtml(report);

            Assert(html.Contains("barChart", StringComparison.Ordinal), "html should contain the bar chart canvas");
            Assert(html.Contains("radarChart", StringComparison.Ordinal), "html should contain the radar chart canvas");

            const string open = "<script id=\"data\" type=\"application/json\">";
            var start = html.IndexOf(open, StringComparison.Ordinal);
            Assert(start >= 0, "html should contain the data island");
            start += open.Length;
            var end = html.IndexOf("</script>", start, StringComparison.Ordinal);
            var json = html[start..end].Trim();
            using var doc = JsonDocument.Parse(json);
            Assert(doc.RootElement.GetProperty("series").GetArrayLength() == 1, "payload should contain one series");

            var csv = writer.BuildCsv(report);
            Assert(csv.Contains("model_family", StringComparison.Ordinal), "csv should have a header row");
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    // Minimal but realistic BenchmarkRunResult with a single primary run whose per-vulnerability
    // credit is derived from the requested TP/partial/missed counts.
    private static BenchmarkRunResult FakeResult(string model, double scorePercent, int fullTp, int partialTp, int fp, int missed)
    {
        var vulnerabilities = new List<VulnerabilityScore>();
        for (var i = 0; i < FakeVulnIds.Length; i++)
        {
            bool found, partial;
            if (i < fullTp) { found = true; partial = false; }
            else if (i < fullTp + partialTp) { found = true; partial = true; }
            else { found = false; partial = false; }

            vulnerabilities.Add(new VulnerabilityScore
            {
                Id = FakeVulnIds[i],
                Title = "vuln " + FakeVulnIds[i],
                Severity = "High",
                Found = found,
                Partial = partial
            });
        }

        var score = new ScoringResult
        {
            RunName = "Run 1",
            ScoreableVulnerabilityCount = FakeVulnIds.Length,
            FindingCount = fullTp + partialTp + fp,
            FullTruePositives = fullTp,
            PartialTruePositives = partialTp,
            FalsePositives = fp,
            Missed = missed,
            RawPoints = fullTp * 5.0,
            ScorePercent = scorePercent,
            Precision = (fullTp + partialTp + fp) == 0 ? 0 : (double)fullTp / (fullTp + partialTp + fp),
            Recall = FakeVulnIds.Length == 0 ? 0 : (double)fullTp / FakeVulnIds.Length,
            F1 = 0.5,
            Vulnerabilities = vulnerabilities
        };

        var now = DateTimeOffset.Now;
        return new BenchmarkRunResult
        {
            BenchmarkId = "supercalc-v3",
            ToolVersion = "test",
            Model = model,
            StartedAt = now,
            CompletedAt = now,
            SourceSha256 = "deadbeef",
            SourceHashMatches = true,
            OutputDirectory = string.Empty,
            Run1 = new BenchmarkRunArtifacts { RunName = "Run 1", Score = score }
        };
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; a leftover temp dir must never fail the test run.
        }
    }
}
