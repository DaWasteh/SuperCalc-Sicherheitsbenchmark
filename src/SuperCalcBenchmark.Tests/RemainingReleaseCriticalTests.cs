using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

internal static partial class TestRunner
{
    private static void BackfillBackupSafetyVariants()
    {
        WithArchive((root, backup, score, record, run) =>
        {
            run.Run3 = new() { Response = "{\"audited_run\":\"Run 1\",\"truth_items\":[],\"false_positive_admissions\":[],\"corrections\":[]}" };
            WriteRun(record.RunDirectory, run, false);
            foreach (var unsafePath in new[] { root, Path.GetDirectoryName(root)! })
            {
                var rejected = false;
                try { new ArchiveMetricsBackfiller(root).Run(new() { Write = true, BackupDirectory = unsafePath }); }
                catch (InvalidOperationException) { rejected = true; }
                Assert(rejected, "backup equal to or containing archive must be rejected");
            }

            var original = File.ReadAllBytes(score);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(backup, Path.GetFileName(score)))!);
            File.WriteAllBytes(Path.Combine(backup, Path.GetFileName(score)), original);
            var reused = new ArchiveMetricsBackfiller(root).Run(new() { Write = true, BackupDirectory = backup });
            Assert(reused.Written == 1 && reused.Backups == 0, "an exact existing backup must be safely reused");
            Assert(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories).Any(), "successful write must leave no temp files");

            // Restore an old scorecard and force a collision. The source must remain byte-identical.
            File.WriteAllBytes(score, original);
            File.WriteAllText(Path.Combine(backup, Path.GetFileName(score)), "different");
            var collision = new ArchiveMetricsBackfiller(root).Run(new() { Write = true, BackupDirectory = backup, ComputedAt = DateTimeOffset.UtcNow.AddMinutes(1) });
            Assert(collision.Written == 0 && collision.Files.Single(x => x.Path == score).Warning?.Contains("collision") == true, "different backup bytes must warn and skip");
            Assert(File.ReadAllBytes(score).SequenceEqual(original), "backup collision must not mutate scorecard");
            Assert(!Directory.EnumerateFiles(root, "*.tmp-*", SearchOption.AllDirectories).Any(), "failed write must leave no temp files");

            var nested = Path.Combine(root, "custom", "backup");
            Directory.CreateDirectory(nested);
            File.WriteAllText(Path.Combine(nested, "decoy.json"), "bad");
            var scan = new ArchiveMetricsBackfiller(root).Run(new() { BackupDirectory = nested });
            Assert(scan.Files.All(x => !x.Path.Contains("decoy.json", StringComparison.Ordinal)), "configured backup subtree inside archive must be excluded from scanning");
        });
    }

    private static void ArchiveOnlyEligibilityVariants()
    {
        WithArchive((root, backup, score, record, run) =>
        {
            TryDeleteDirectory(record.RunDirectory);
            TruthAuditResult Audit(string target = "Run 1", Func<int, TruthAuditItemResult>? item = null) => new()
            {
                AuditedRunName = target,
                Items = Enumerable.Range(1, 20).Select(i => item?.Invoke(i) ?? new TruthAuditItemResult { Id = $"SC-V3-{i:000}", ActualStatus = "missed", SelfAssessment = "missed", Correct = true }).ToList()
            };
            void Save(TruthAuditResult? audit) { record.BehavioralDiagnostics = null; record.Runs[0].TruthAudit = audit; File.WriteAllBytes(score, JsonSerializer.SerializeToUtf8Bytes(record, ArtifactJson)); }
            Save(Audit());
            var valid = new ArchiveMetricsBackfiller(root).Run(new());
            Assert(valid.Partial == 1 && valid.Files.Single().WouldWrite, "valid 20-item archive-only audit must be partial and backfillable");

            var variants = new TruthAuditResult?[]
            {
                null,
                new() { AuditedRunName="Run 1", Items=Audit().Items.Take(19).ToList() },
                new() { AuditedRunName="Run 1", Items=Audit().Items.Select((x,i)=> i==19 ? new TruthAuditItemResult { Id="SC-V3-001", ActualStatus="missed", SelfAssessment="missed" } : x).ToList() },
                Audit(item:i=>new(){Id=i==20?"UNKNOWN":$"SC-V3-{i:000}",ActualStatus="missed",SelfAssessment="missed"}),
                Audit(item:i=>new(){Id=$"SC-V3-{i:000}",ActualStatus=i==1?"invalid":"missed",SelfAssessment="missed"}),
                Audit("Run 3")
            };
            foreach (var malformed in variants) { Save(malformed); var result = new ArchiveMetricsBackfiller(root).Run(new()); Assert(result.Unavailable == 1 && !result.Files.Single().WouldWrite, "malformed archive-only audit must be unavailable and inert"); }
        });
    }

    private static void ConfidenceProvenanceParserContract()
    {
        var parser = new ResponseParser();
        var parsed = parser.Parse("{\"findings\":[{\"title\":\"confidence probability words\",\"vulnerability_type\":\"overflow\",\"evidence\":\"confidence\"},{\"title\":\"explicit\",\"vulnerability_type\":\"overflow\",\"evidence\":\"x\",\"probability\":0.2}]}");
        Assert(parsed.Findings[0].ConfidenceOrigin == ConfidenceOrigin.JsonDefault && parsed.Findings[1].ConfidenceOrigin == ConfidenceOrigin.Reported, "origin must depend on finding properties, not descriptive text");
        var fallback = parser.Parse("1. Integer overflow\nCWE-190\nSeverity: High\nEvidence: arithmetic wraps in enhanced_calc.cpp line 10");
        Assert(fallback.Findings.Count > 0 && fallback.Findings[0].ConfidenceOrigin == ConfidenceOrigin.TextFallbackDefault, "text fallback must retain fallback provenance");
    }

    private static void ReleasePresentationExportContracts()
    {
        var html = File.ReadAllText(Path.Combine("src", "SuperCalcBenchmark.Core", "ComparisonHtmlWriter.cs"));
        Assert(html.Contains("data-metric-id=\"honesty\"") && html.Contains("data-metric-id=\"calibration\"") && html.Contains("data-metric-id=\"consistency\""), "HTML must retain all three diagnostics cards");
        Assert(html.Contains("Nicht messbar ist n/a; gemessene Null bleibt 0.") && html.Contains("String(v ?? \"\")"), "HTML help/export must distinguish null from measured zero using nullish logic");
        Assert(!html.Contains("String(v || \"\")", StringComparison.Ordinal), "browser CSV must not erase zero with truthy fallback");
        var markdown = File.ReadAllText(Path.Combine("src", "SuperCalcBenchmark.Core", "ReportWriter.cs"));
        Assert(markdown.Contains("n/a", StringComparison.OrdinalIgnoreCase), "Markdown must expose unavailable audit metrics as n/a");
        var wpf = File.ReadAllText(Path.Combine("src", "SuperCalcBenchmark.App", "MainWindow.xaml.cs"));
        Assert(wpf.Contains("audit.AuditedRunName") && wpf.Contains("Auditiert:") && wpf.Contains("\"Run 2\""), "WPF diagnostics must disclose the audited target and support Run 2 selection");
    }

    private static void FrozenDetectionInvariantContract()
    {
        var (groundTruth, source) = LoadGroundTruthAndSource();
        foreach (var profile in new[] { ScoringProfiles.OfficialV1, ScoringProfiles.OfficialV2 })
        {
            var json = JsonSerializer.Serialize(new { findings = groundTruth.Vulnerabilities.Take(2).Select(SyntheticFinding).ToList() });
            var parsed = new ResponseParser().Parse(json);
            var score = new ScoringEngine().Score("frozen", parsed.Findings, groundTruth, source, profile);
            var before = JsonSerializer.Serialize(score);
            _ = BehavioralDiagnosticsCalculator.Calibrate(parsed, score);
            Assert(JsonSerializer.Serialize(score) == before, $"diagnostics must not mutate {profile} detection score, credits, findings, classifications, points, or engine metadata");
        }
    }
}
