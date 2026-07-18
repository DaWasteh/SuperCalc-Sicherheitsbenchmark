using System.Text;
using System.Text.Json;
using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

internal static partial class TestRunner
{
    private static readonly JsonSerializerOptions ArtifactJson = new() { WriteIndented = true };

    private static void RunArtifactReaderBomAndPriority()
    {
        WithArtifact((dir, record, run) =>
        {
            foreach (var bom in new[] { false, true })
            {
                run.Run3 = new() { Response = "from-json" };
                WriteRun(dir, run, bom);
                File.WriteAllText(Path.Combine(dir, "run3_response.txt"), "from-text");
                File.WriteAllText(Path.Combine(dir, "run3_raw_response.json"), "{\"choices\":[{\"delta\":{\"content\":\"from-raw\"}}]}");
                var result = new RunArtifactReader().Read(record);
                Assert(result.IdentityMatches && result.FinalResponse == "from-json" && result.ResponseSource == "run.json", $"run.json must win (BOM={bom})");
            }
            run.Run3 = new(); WriteRun(dir, run, false);
            var text = new RunArtifactReader().Read(record);
            Assert(text.FinalResponse == "from-text" && text.ResponseSource == "run3_response.txt", "text must win when run.json response is empty");
        });
    }

    private static void RunArtifactReaderStreamingFormats()
    {
        var path = Path.Combine(Path.GetTempPath(), "supercalc-stream-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllLines(path, [
                "{\"choices\":[{\"delta\":{\"content\":\"jsonl \"}}]}",
                "data: {\"choices\":[{\"delta\":{\"content\":\"sse \"}}]}",
                "{not-json}",
                "{\"choices\":[{\"message\":{\"content\":\"nonstream\"}}]}",
                "data: [DONE]"
            ]);
            var value = RunArtifactReader.ReconstructRaw(path, out var corrupt);
            Assert(value == "jsonl sse nonstream", "JSONL, SSE and nonstream chunks must reconstruct in order");
            Assert(corrupt, "a corrupt chunk must be retained as partial-evidence metadata");
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static void RunArtifactReaderIdentityMismatches()
    {
        WithArtifact((dir, baseline, run) =>
        {
            WriteRun(dir, run, false);
            var variants = new (string expected, Action<ArchiveRecord> mutate)[]
            {
                ("benchmark", x => x.BenchmarkId="other"), ("source hash", x => x.SourceSha256="other"),
                ("model", x => x.RawModelId="other"), ("start time", x => x.StartedAt=x.StartedAt.AddMinutes(1)),
                ("seed", x => x.Seed=999), ("output directory", x => { var other=Path.Combine(dir,"other"); Directory.CreateDirectory(other); File.Copy(Path.Combine(dir,"run.json"),Path.Combine(other,"run.json"),true); x.RunDirectory=other; })
            };
            foreach (var (expected, mutate) in variants)
            {
                var copy = JsonSerializer.Deserialize<ArchiveRecord>(JsonSerializer.Serialize(baseline))!;
                mutate(copy);
                var result = new RunArtifactReader().Read(copy);
                Assert(!result.IdentityMatches && result.Error!.Contains(expected, StringComparison.OrdinalIgnoreCase), $"expected {expected} mismatch, got {result.Error}");
            }
        });
    }

    private static void ArchiveBackfillTransactionContract()
    {
        WithArchive((root, backup, scorePath, record, run) =>
        {
            var audit = "{\"audited_run\":\"Run 1\",\"truth_items\":[],\"false_positive_admissions\":[],\"corrections\":[]}";
            run.Run3 = new() { Response = audit }; WriteRun(record.RunDirectory, run, true);
            var before = File.ReadAllBytes(scorePath); var stamp = File.GetLastWriteTimeUtc(scorePath);
            var options = new ArchiveMetricsBackfillOptions { BackupDirectory=backup, ComputedAt=DateTimeOffset.Parse("2026-01-02T03:04:05Z") };
            var dry = new ArchiveMetricsBackfiller(root).Run(options);
            Assert(dry.WouldWrite == 1 && dry.Written == 0 && File.ReadAllBytes(scorePath).SequenceEqual(before) && File.GetLastWriteTimeUtc(scorePath)==stamp, "dry run must be byte/timestamp inert");
            var written = new ArchiveMetricsBackfiller(root).Run(new() { Write=true, BackupDirectory=backup, ComputedAt=options.ComputedAt });
            Assert(written.Written==1 && written.Backups==1, "write should update and back up exactly once");
            Assert(File.ReadAllBytes(Path.Combine(backup, Path.GetRelativePath(root, scorePath))).SequenceEqual(before), "backup must preserve exact original bytes including BOM");
            using var doc = JsonDocument.Parse(File.ReadAllBytes(scorePath));
            Assert(doc.RootElement.GetProperty("schemaVersion").GetInt32()==4 && doc.RootElement.TryGetProperty("behavioralDiagnostics", out _), "write must create schema 4 diagnostics");
            var stableStamp=File.GetLastWriteTimeUtc(scorePath); var stableBytes=File.ReadAllBytes(scorePath);
            var second = new ArchiveMetricsBackfiller(root).Run(new() { Write=true, BackupDirectory=backup, ComputedAt=options.ComputedAt.AddDays(1) });
            Assert(second.AlreadyCurrent==1 && second.Written==0 && File.GetLastWriteTimeUtc(scorePath)==stableStamp && File.ReadAllBytes(scorePath).SequenceEqual(stableBytes), "second write must be byte/timestamp stable");
            Assert(!Directory.EnumerateFiles(root,"*.tmp-*",SearchOption.AllDirectories).Any(), "no temporary files may remain");
        }, bomScorecard:true);
    }

    private static void ArchiveBackfillIsolationContract()
    {
        WithArchive((root, backup, scorePath, record, run) =>
        {
            File.WriteAllText(Path.Combine(root,"corrupt.json"), "null");
            Directory.CreateDirectory(Path.Combine(root,"_migration-backup"));
            File.WriteAllText(Path.Combine(root,"_migration-backup","ignored.json"), "bad");
            Directory.CreateDirectory(backup); File.WriteAllText(Path.Combine(backup,"configured.json"), "bad");
            var result = new ArchiveMetricsBackfiller(root).Run(new() { BackupDirectory=backup });
            Assert(result.Scanned==2 && result.Files.Count==2 && result.Files.Any(x=>x.Path.EndsWith("corrupt.json") && x.Warning is not null) && result.Files.Any(x=>x.Path.EndsWith("score.json")), "bad scorecard must warn while another scorecard continues; backup subtrees excluded");
            var rejected=false; try { new ArchiveMetricsBackfiller(root).Run(new(){Write=true,BackupDirectory=Path.GetDirectoryName(root)}); } catch(InvalidOperationException){rejected=true;}
            Assert(rejected, "backup root containing archive root must be rejected");
        });
    }

    private static void WithArtifact(Action<string,ArchiveRecord,BenchmarkRunResult> action)
    {
        var dir=Path.Combine(Path.GetTempPath(),"supercalc-artifact-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(dir);
        try { var (record,run)=Fixture(dir); action(dir,record,run); } finally { TryDeleteDirectory(dir); }
    }

    private static void WithArchive(Action<string,string,string,ArchiveRecord,BenchmarkRunResult> action, bool bomScorecard=false)
    {
        var parent=Path.Combine(Path.GetTempPath(),"supercalc-backfill-"+Guid.NewGuid().ToString("N")); var root=Path.Combine(parent,"archive"); var backup=Path.Combine(root,"configured-backups"); var artifacts=Path.Combine(parent,"artifacts");
        Directory.CreateDirectory(root); Directory.CreateDirectory(artifacts);
        try { var (record,run)=Fixture(artifacts); var score=Path.Combine(root,"score.json"); var bytes=JsonSerializer.SerializeToUtf8Bytes(record,ArtifactJson); File.WriteAllBytes(score,bomScorecard?[0xef,0xbb,0xbf,..bytes]:bytes); action(root,backup,score,record,run); } finally { TryDeleteDirectory(parent); }
    }

    private static (ArchiveRecord record,BenchmarkRunResult run) Fixture(string dir)
    {
        var at=DateTimeOffset.Parse("2026-01-01T00:00:00Z");
        var run=new BenchmarkRunResult { BenchmarkId="bench",SourceSha256="sha",Model="model",Seed=7,StartedAt=at,CompletedAt=at.AddSeconds(2),OutputDirectory=dir,Run1=new(){RunName="Run 1"},Run3=new() };
        var record=new ArchiveRecord { SchemaVersion=3,RecordId="id",BenchmarkId="bench",SourceSha256="sha",RawModelId="model",Seed=7,StartedAt=at,CompletedAt=at.AddSeconds(2),RunDirectory=dir,Runs=[new(){RunName="Run 1"}] };
        return(record,run);
    }

    private static void WriteRun(string dir,BenchmarkRunResult run,bool bom)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(run,ArtifactJson); File.WriteAllBytes(Path.Combine(dir,"run.json"),bom?[0xef,0xbb,0xbf,..bytes]:bytes);
    }
}
