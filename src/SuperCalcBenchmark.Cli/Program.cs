using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = System.Globalization.CultureInfo.InvariantCulture;
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var parsed = ParsedArgs.Parse(args);
        if (parsed.HelpRequested)
        {
            PrintUsage();
            return 0;
        }

        var command = parsed.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = parsed.Has("--model") ? "run" : "help";
        }

        try
        {
            return command.ToLowerInvariant() switch
            {
                "models" => await ListModelsAsync(parsed),
                "validate" => Validate(parsed),
                "score-fixture" or "fixture" => ScoreFixture(parsed),
                "run" or "benchmark" => await RunBenchmarkAsync(parsed),
                "archive-list" or "archive" => ArchiveList(parsed),
                "migrate-archive-scores" => MigrateArchiveScores(parsed),
                "backfill-archive-metrics" => BackfillArchiveMetrics(parsed),
                "compare" => Compare(parsed),
                "help" => PrintUsageAndReturn(),
                _ => UnknownCommand(command)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Aborted.");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            if (parsed.Has("--verbose"))
            {
                Console.Error.WriteLine(ex);
            }

            return 1;
        }
    }

    private static async Task<int> ListModelsAsync(ParsedArgs args)
    {
        var server = args.Get("--server", "http://127.0.0.1:1234");
        using var client = new LlamaCppClient(TimeSpan.FromSeconds(args.GetInt("--timeout-seconds", BenchmarkDefaults.ModelListTimeoutSeconds)));
        var models = await client.GetModelsAsync(server);
        if (models.Count == 0)
        {
            Console.WriteLine("No models returned by server.");
            return 2;
        }

        foreach (var model in models)
        {
            Console.WriteLine(model);
        }

        return 0;
    }

    private static int Validate(ParsedArgs args)
    {
        var options = BuildOptions(args, requireModel: false);
        var store = new GroundTruthStore();
        var result = store.Validate(options.GroundTruthPath, options.SourcePath);

        Console.WriteLine($"Ground truth: {options.GroundTruthPath}");
        Console.WriteLine($"Source:       {options.SourcePath}");
        Console.WriteLine($"Expected SHA: {result.ExpectedSourceSha256}");
        Console.WriteLine($"Actual SHA:   {result.ActualSourceSha256}");
        Console.WriteLine($"Vulns:        {result.VulnerabilityCount}");
        Console.WriteLine($"Valid:        {result.IsValid}");

        foreach (var issue in result.Issues)
        {
            Console.WriteLine($"[{issue.Severity}] {issue.Message}");
        }

        return result.IsValid ? 0 : 1;
    }

    private static int ScoreFixture(ParsedArgs args)
    {
        var response = args.Get("--response", args.Get("--fixture", string.Empty));
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new ArgumentException("score-fixture requires --response <file>.");
        }

        var options = BuildOptions(args, requireModel: false);
        var runner = new BenchmarkRunner();
        var result = runner.ScoreFixture(options, response, args.Get("--run-name", "Fixture"));
        PrintScore(result.Run1.Score);
        Console.WriteLine($"Report: {Path.Combine(result.OutputDirectory, "report.md")}");
        return 0;
    }

    private static async Task<int> RunBenchmarkAsync(ParsedArgs args)
    {
        var options = BuildOptions(args, requireModel: true);
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cts.Cancel();
        };

        var runner = new BenchmarkRunner();
        var repeats = Math.Max(1, options.Repeats);
        if (repeats == 1)
        {
            var singleOptions = CloneOptions(options, seed: options.SeedStart ?? options.Seed, repeatGroupId: options.RepeatGroupId, repeatIndex: 1, repeatCount: 1, withTruthAudit: options.WithTruthAudit);
            var result = await runner.RunAsync(singleOptions, message => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"), cts.Token);
            PrintCompletedRun(result);
            return 0;
        }

        var repeatGroupId = string.IsNullOrWhiteSpace(options.RepeatGroupId)
            ? "repeat-" + DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..8]
            : options.RepeatGroupId;
        var seedStart = options.SeedStart ?? options.Seed;
        var results = new List<BenchmarkRunResult>();
        var auditMode = NormalizeTruthAuditRepeatMode(options.TruthAuditRepeatMode, options.WithTruthAudit);
        var archiveAfterRepeats = string.Equals(auditMode, "only-best-repeat", StringComparison.OrdinalIgnoreCase)
                                  && !string.IsNullOrWhiteSpace(options.ArchiveDirectory);

        Console.Error.WriteLine($"Running {repeats} repeat(s), group {repeatGroupId}, seeds {seedStart}..{seedStart + repeats - 1}, truth-audit={auditMode}.");
        for (var i = 0; i < repeats; i++)
        {
            var repeatIndex = i + 1;
            var repeatOut = string.IsNullOrWhiteSpace(options.OutputDirectory)
                ? null
                : Path.Combine(options.OutputDirectory, $"repeat-{repeatIndex:D3}");
            var repeatOptions = CloneOptions(
                options,
                seed: seedStart + i,
                repeatGroupId: repeatGroupId,
                repeatIndex: repeatIndex,
                repeatCount: repeats,
                withTruthAudit: string.Equals(auditMode, "always", StringComparison.OrdinalIgnoreCase),
                outputDirectory: repeatOut,
                archiveDirectory: archiveAfterRepeats ? string.Empty : options.ArchiveDirectory);

            Console.Error.WriteLine($"=== Repeat {repeatIndex}/{repeats} (seed {repeatOptions.Seed}) ===");
            var result = await runner.RunAsync(repeatOptions, message => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"), cts.Token);
            results.Add(result);
            PrintCompletedRun(result);
        }

        if (string.Equals(auditMode, "only-best-repeat", StringComparison.OrdinalIgnoreCase) && options.WithTruthAudit)
        {
            var best = results
                .OrderByDescending(r => r.Run2?.Score.ScorePercent ?? r.Run1.Score.ScorePercent)
                .ThenByDescending(r => r.RepeatIndex)
                .First();
            Console.Error.WriteLine($"Running truth-audit only for best repeat {best.RepeatIndex}/{best.RepeatCount}.");
            var auditOptions = CloneOptions(
                options,
                seed: best.Seed,
                repeatGroupId: repeatGroupId,
                repeatIndex: best.RepeatIndex,
                repeatCount: repeats,
                withTruthAudit: true,
                outputDirectory: best.OutputDirectory,
                archiveDirectory: string.Empty);
            await runner.AddTruthAuditAsync(best, auditOptions, message => Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}"), cts.Token);
        }

        if (archiveAfterRepeats && !string.IsNullOrWhiteSpace(options.ArchiveDirectory))
        {
            var store = new ArchiveStore(options.ArchiveDirectory);
            foreach (var result in results)
            {
                result.ArchivedRecordPath = store.Save(result, options.QuantOverride);
                Console.WriteLine($"Archived: {result.ArchivedRecordPath}");
            }
        }

        var mean = results.Average(r => r.Run2?.Score.ScorePercent ?? r.Run1.Score.ScorePercent);
        var bestScore = results.Max(r => r.Run2?.Score.ScorePercent ?? r.Run1.Score.ScorePercent);
        Console.WriteLine($"Repeat summary: n={results.Count}, mean={mean:0.##}, best={bestScore:0.##}, group={repeatGroupId}");
        return 0;
    }

    private static int ArchiveList(ParsedArgs args)
    {
        var archiveDir = Path.GetFullPath(args.Get("--archive", ArchiveStore.DefaultArchiveFolderName));
        var benchmark = args.GetNullable("--benchmark");
        var store = new ArchiveStore(archiveDir);
        var groups = store.LoadGroups(benchmark);

        Console.WriteLine($"Archive: {archiveDir}");
        if (groups.Count == 0)
        {
            Console.WriteLine("No archived runs found. Run a benchmark first (archiving is on by default).");
            return 0;
        }

        Console.WriteLine($"{groups.Count} model/quant group(s):");
        Console.WriteLine();
        foreach (var group in groups)
        {
            Console.WriteLine($"  {group.ModelFamily} · {group.Quant}");
            Console.WriteLine($"    runs={group.RunCount}  best={group.BestScorePercent:0.##}  median={group.MedianScorePercent:0.##}  avg={group.AverageScorePercent:0.##}  σ={group.ScoreStdDev:0.##}  range={group.MinScorePercent:0.##}-{group.MaxScorePercent:0.##}");
        }

        if (groups.Any(g => string.Equals(g.Quant, ModelIdentity.UnknownQuant, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            Console.WriteLine("Hinweis: unknown-quant kann in den archive/*.json Scorecards manuell korrigiert werden.");
            Console.WriteLine("         modelFamily/quant bearbeiten, speichern, dann archive-list/compare bzw. Archiv neu laden.");
        }

        return 0;
    }

    private static int MigrateArchiveScores(ParsedArgs args)
    {
        var archiveDir = Path.GetFullPath(args.Get("--archive", ArchiveStore.DefaultArchiveFolderName));
        var assumeProfile = args.Get("--assume-profile", ScoringProfiles.OfficialV1Name);
        var write = args.Has("--write") && !args.Has("--dry-run");
        var groundTruthPath = args.Get("--ground-truth", Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json"));
        var sourcePath = args.Get("--source", "enhanced_calc.cpp");
        var backupDir = args.GetNullable("--backup");
        if (write && string.IsNullOrWhiteSpace(backupDir))
        {
            backupDir = Path.Combine(archiveDir, "_migration-backup", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        var options = new ArchiveMigrationOptions
        {
            AssumedProfile = assumeProfile,
            Write = write,
            BackupDirectory = backupDir,
            GroundTruthSha256 = File.Exists(groundTruthPath) ? GroundTruthStore.ComputeSha256(groundTruthPath) : string.Empty,
            SourceSha256 = File.Exists(sourcePath) ? GroundTruthStore.ComputeSha256(sourcePath) : string.Empty
        };

        var result = new ArchiveStore(archiveDir).MigrateScores(options);
        Console.WriteLine($"Archive: {archiveDir}");
        Console.WriteLine($"Mode: {(write ? "write" : "dry-run")}");
        Console.WriteLine($"Assumed profile: {assumeProfile}");
        if (!string.IsNullOrWhiteSpace(result.BackupDirectory))
        {
            Console.WriteLine($"Backup: {result.BackupDirectory}");
        }

        Console.WriteLine($"Files scanned: {result.FilesScanned}");
        Console.WriteLine($"Files needing changes: {result.FilesChanged}");
        Console.WriteLine($"Files written: {result.FilesWritten}");
        Console.WriteLine($"Runs migrated: {result.RunsMigrated}");
        Console.WriteLine($"Runs already versioned: {result.RunsAlreadyVersioned}");

        foreach (var file in result.Files.Where(f => f.Changed || !string.IsNullOrWhiteSpace(f.Warning)))
        {
            var marker = file.Warning is not null ? "WARN" : (file.Written ? "WRITE" : "DRY");
            Console.WriteLine($"  [{marker}] {file.Path} migratedRuns={file.MigratedRuns} alreadyVersioned={file.AlreadyVersionedRuns}{(string.IsNullOrWhiteSpace(file.Warning) ? string.Empty : " warning=" + file.Warning)}");
        }

        if (!write && result.FilesChanged > 0)
        {
            Console.WriteLine("Dry-run only. Re-run with --write to update scorecards without changing point values.");
        }

        return 0;
    }

    private static int BackfillArchiveMetrics(ParsedArgs args)
    {
        var archiveDir = Path.GetFullPath(args.Get("--archive", ArchiveStore.DefaultArchiveFolderName));
        var write = args.Has("--write") && !args.Has("--dry-run");
        var backup = args.GetNullable("--backup");
        if (write && string.IsNullOrWhiteSpace(backup))
            backup = Path.Combine(archiveDir, "_migration-backup", "v0.7.2-behavioral-metrics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var result = new ArchiveMetricsBackfiller(archiveDir).Run(new() { Write=write, BackupDirectory=backup });
        Console.WriteLine($"Archive: {archiveDir}"); Console.WriteLine($"Mode: {(write ? "write" : "dry-run")}");
        if (backup is not null) Console.WriteLine($"Backup: {backup}");
        Console.WriteLine($"Scanned: {result.Scanned}  complete: {result.Complete}  partial: {result.Partial}  unavailable: {result.Unavailable}");
        Console.WriteLine($"Already current: {result.AlreadyCurrent}  would write: {result.WouldWrite}  written: {result.Written}  backups: {result.Backups}  invariant failures: {result.InvariantFailures}");
        foreach (var file in result.Files.Where(x => x.WouldWrite || x.Warning is not null)) Console.WriteLine($"  [{(file.Warning is not null ? "WARN" : file.Written ? "WRITE" : "DRY")}] {file.Path}{(file.Warning is null ? "" : " warning="+file.Warning)}");
        return result.HasErrors ? 1 : 0;
    }

    private static int Compare(ParsedArgs args)
    {
        var archiveDir = Path.GetFullPath(args.Get("--archive", ArchiveStore.DefaultArchiveFolderName));
        var benchmark = args.GetNullable("--benchmark");
        var family = args.GetNullable("--family");
        var aggregate = ParseAggregate(args.Get("--aggregate", "average"));
        var runView = ParseRunView(args.Get("--run-view", "primary"));
        var metric = ParseMetric(args.Get("--metric", "score"));
        var scoringProfile = args.GetNullable("--scoring-profile");
        var publicLabels = args.Has("--public-labels");
        var groundTruthPath = args.Get("--ground-truth", Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json"));
        var metadata = VulnerabilityMetadataIndex.Load(groundTruthPath, publicLabels);

        var store = new ArchiveStore(archiveDir);
        var groups = store.LoadGroups(benchmark);
        if (groups.Count == 0)
        {
            Console.WriteLine($"No archived runs found in {archiveDir}.");
            return 0;
        }

        var report = ComparisonReport.Build(groups, benchmark ?? groups[0].Records[0].BenchmarkId, aggregate, family, metadata, runView, metric, scoringProfile);
        if (report.IsEmpty)
        {
            Console.WriteLine(family is null
                ? "Nothing to compare."
                : $"No archived runs for model family '{family}'. Use archive-list to see available families.");
            return 0;
        }

        var outputDir = args.GetNullable("--out") ?? Path.Combine(archiveDir, "_reports");
        var htmlPath = new ComparisonHtmlWriter().Write(report, Path.GetFullPath(outputDir));

        Console.WriteLine($"Comparison ({aggregate}, {runView}, {metric}, profile={report.ScoringProfile ?? "all"}, {report.Series.Count} series, {report.VulnerabilityAxis.Count} vulns):");
        foreach (var series in report.Series)
        {
            var selectedMetric = DisplayMetricValue(series, metric);
            Console.WriteLine($"  {selectedMetric,6:0.##}  {series.Label}  (metric={metric}, score={series.ScorePercent:0.##}, runs={series.RunCount}, median={series.ScoreMedian:0.##}, avg={series.ScoreMean:0.##}, σ={series.ScoreStdDev:0.##}, range={series.ScoreMin:0.##}-{series.ScoreMax:0.##})");
        }

        Console.WriteLine();
        Console.WriteLine($"HTML report: {htmlPath}");
        return 0;
    }

    private static void PrintCompletedRun(BenchmarkRunResult result)
    {
        PrintScore(result.Run1.Score);
        if (result.Run2 is not null)
        {
            PrintScore(result.Run2.Score);
        }

        if (result.Run3?.TruthAudit is not null)
        {
            Console.WriteLine($"Run 3 truth-audit: accountability {result.Run3.TruthAudit.AccountabilityScore:0.##}/100 | overclaim {result.Run3.TruthAudit.OverclaimRate:P1}");
        }

        Console.WriteLine($"Report: {Path.Combine(result.OutputDirectory, "report.md")}");
        if (!string.IsNullOrWhiteSpace(result.ArchivedRecordPath))
        {
            Console.WriteLine($"Archived: {result.ArchivedRecordPath}");
        }
    }

    private static string NormalizeTruthAuditRepeatMode(string configuredMode, bool withTruthAudit)
    {
        if (!withTruthAudit)
        {
            return "never";
        }

        var normalized = (configuredMode ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "" or "true" or "yes" or "always" => "always",
            "never" or "false" or "no" => "never",
            "only-best-repeat" or "best" or "best-repeat" => "only-best-repeat",
            _ => throw new ArgumentException("--with-truth-audit accepts optional value always|never|only-best-repeat.")
        };
    }

    private static BenchmarkOptions CloneOptions(
        BenchmarkOptions original,
        int seed,
        string repeatGroupId,
        int repeatIndex,
        int repeatCount,
        bool withTruthAudit,
        string? outputDirectory = null,
        string? archiveDirectory = null)
    {
        return new BenchmarkOptions
        {
            ServerUrl = original.ServerUrl,
            Model = original.Model,
            SourcePath = original.SourcePath,
            GroundTruthPath = original.GroundTruthPath,
            AnalysisPromptPath = original.AnalysisPromptPath,
            SelfValidatePromptPath = original.SelfValidatePromptPath,
            TruthAuditPromptPath = original.TruthAuditPromptPath,
            SchemaPath = original.SchemaPath,
            TruthAuditSchemaPath = original.TruthAuditSchemaPath,
            OutputDirectory = outputDirectory ?? original.OutputDirectory,
            Temperature = original.Temperature,
            TopP = original.TopP,
            MaxTokens = original.MaxTokens,
            Seed = seed,
            Repeats = original.Repeats,
            SeedStart = original.SeedStart,
            RepeatGroupId = repeatGroupId,
            RepeatIndex = repeatIndex,
            RepeatCount = repeatCount,
            TruthAuditRepeatMode = original.TruthAuditRepeatMode,
            Timeout = original.Timeout,
            AllowHashMismatch = original.AllowHashMismatch,
            SkipResponseFormat = original.SkipResponseFormat,
            DisableThinking = original.DisableThinking,
            BenchmarkProfile = original.BenchmarkProfile,
            ScoringProfile = original.ScoringProfile,
            WithTruthAudit = withTruthAudit,
            TruthAuditSource = original.TruthAuditSource,
            AbortOnLoop = original.AbortOnLoop,
            ArchiveDirectory = archiveDirectory is null ? original.ArchiveDirectory : (archiveDirectory.Length == 0 ? null : archiveDirectory),
            QuantOverride = original.QuantOverride,
            AdjudicationPath = original.AdjudicationPath
        };
    }

    private static BenchmarkOptions BuildOptions(ParsedArgs args, bool requireModel)
    {
        var model = args.Get("--model", string.Empty);
        if (requireModel && string.IsNullOrWhiteSpace(model))
        {
            throw new ArgumentException("A model id is required. Use --model <MODEL> or run 'models' first.");
        }

        var truthAuditRaw = args.GetNullable("--with-truth-audit");
        var truthAuditMode = args.Has("--with-truth-audit")
            ? (string.IsNullOrWhiteSpace(truthAuditRaw) ? "always" : truthAuditRaw!.Trim().ToLowerInvariant())
            : "never";
        var withTruthAudit = args.Has("--with-truth-audit") && truthAuditMode is not ("never" or "false" or "no");
        var repeats = Math.Max(1, args.GetInt("--repeats", 1));
        var seedStart = args.GetNullable("--seed-start") is string seedStartText && int.TryParse(seedStartText, out var parsedSeedStart)
            ? parsedSeedStart
            : (int?)null;

        return new BenchmarkOptions
        {
            ServerUrl = args.Get("--server", "http://127.0.0.1:1234"),
            Model = model,
            SourcePath = args.Get("--source", "enhanced_calc.cpp"),
            GroundTruthPath = args.Get("--ground-truth", Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json")),
            AnalysisPromptPath = args.Get("--analysis-prompt", Path.Combine("benchmarks", "supercalc-v3", "prompts", "analysis_v1.md")),
            SelfValidatePromptPath = args.Get("--self-prompt", Path.Combine("benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md")),
            TruthAuditPromptPath = args.Get("--truth-audit-prompt", Path.Combine("benchmarks", "supercalc-v3", "prompts", "truth_audit_v1.md")),
            SchemaPath = args.Get("--schema", Path.Combine("benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json")),
            TruthAuditSchemaPath = args.Get("--truth-audit-schema", Path.Combine("benchmarks", "supercalc-v3", "schemas", "truth_audit.schema.json")),
            OutputDirectory = args.GetNullable("--out"),
            MaxTokens = args.GetInt("--max-tokens", -1),
            Seed = args.GetInt("--seed", seedStart ?? 12345),
            Repeats = repeats,
            SeedStart = seedStart,
            RepeatIndex = 1,
            RepeatCount = repeats,
            TruthAuditRepeatMode = truthAuditMode,
            Timeout = TimeSpan.FromSeconds(args.GetInt("--timeout-seconds", BenchmarkDefaults.OfficialRequestTimeoutSeconds)),
            AllowHashMismatch = args.Has("--allow-hash-mismatch"),
            SkipResponseFormat = args.Has("--skip-response-format"),
            DisableThinking = args.Has("--disable-thinking"),
            BenchmarkProfile = args.Get("--profile", args.Get("--benchmark-profile", "official")),
            ScoringProfile = args.Get("--scoring-profile", ScoringProfiles.OfficialV1Name),
            WithTruthAudit = withTruthAudit,
            TruthAuditSource = args.Get("--truth-audit-source", "best"),
            AbortOnLoop = !args.Has("--no-loop-abort"),
            ArchiveDirectory = ResolveArchiveDirectory(args),
            QuantOverride = args.GetNullable("--quant"),
            AdjudicationPath = args.GetNullable("--adjudication")
        };
    }

    // Archiving is on by default (folder: ./archive) so every run becomes comparable.
    // --no-archive disables it; --archive <dir> picks a custom location.
    private static string? ResolveArchiveDirectory(ParsedArgs args)
    {
        if (args.Has("--no-archive"))
        {
            return null;
        }

        var explicitPath = args.GetNullable("--archive");
        return Path.GetFullPath(string.IsNullOrWhiteSpace(explicitPath)
            ? ArchiveStore.DefaultArchiveFolderName
            : explicitPath);
    }

    private static ComparisonAggregate ParseAggregate(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "best" or "bester" => ComparisonAggregate.Best,
            "median" => ComparisonAggregate.Median,
            "average" or "avg" or "mean" or "durchschnitt" => ComparisonAggregate.Average,
            _ => throw new ArgumentException("--aggregate must be one of: average, median, best.")
        };
    }

    private static ComparisonRunView ParseRunView(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "run1" or "run-1" or "1" => ComparisonRunView.Run1,
            "run2" or "run-2" or "2" => ComparisonRunView.Run2,
            "delta" or "run2-run1" or "run2-delta" => ComparisonRunView.Delta,
            "primary" or "haupt" => ComparisonRunView.Primary,
            _ => throw new ArgumentException("--run-view must be one of: primary, run1, run2, delta.")
        };
    }

    private static double DisplayMetricValue(ComparisonSeries series, ComparisonMetric metric) => metric switch
    {
        ComparisonMetric.CriticalRecall => series.CriticalRecall * 100,
        ComparisonMetric.HighCriticalRecall => series.HighCriticalRecall * 100,
        ComparisonMetric.F1 => series.F1 * 100,
        ComparisonMetric.FpRate => series.FpPerFinding * 100,
        ComparisonMetric.Stability => series.VulnerabilityStability * 100,
        ComparisonMetric.Run2Delta => series.Run2ScoreDelta,
        ComparisonMetric.ThinkingCoverage => (series.ReasoningToOutputCoverage ?? 0) * 100,
        ComparisonMetric.EvidenceFidelity => series.EvidenceFidelity * 100,
        ComparisonMetric.LocationAccuracy => series.LocationAccuracy * 100,
        ComparisonMetric.HallucinationRate => series.HallucinationRate * 100,
        ComparisonMetric.EvaluationConfidence => series.EvaluationConfidence * 100,
        ComparisonMetric.Accountability => series.AccountabilityScore,
        ComparisonMetric.OverclaimRate => series.OverclaimRate * 100,
        ComparisonMetric.Duration => (series.DurationMedianMs ?? series.DurationMeanMs ?? 0) / 1000.0,
        ComparisonMetric.TokenEfficiency => series.ScorePer1KTokens ?? 0,
        ComparisonMetric.Honesty => (series.Honesty ?? 0) * 100,
        ComparisonMetric.HonestyCalibration => (series.HonestyCalibration ?? 0) * 100,
        ComparisonMetric.RevisionSelectivity => (series.RevisionSelectivity ?? 0) * 100,
        ComparisonMetric.HonestyStability => (series.HonestyStability ?? 0) * 100,
        _ => series.ScorePercent
    };

    private static ComparisonMetric ParseMetric(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "critical-recall" or "critical" => ComparisonMetric.CriticalRecall,
            "high-critical-recall" or "high+critical" or "high-critical" => ComparisonMetric.HighCriticalRecall,
            "f1" => ComparisonMetric.F1,
            "fp-rate" or "fpr" => ComparisonMetric.FpRate,
            "stability" or "stable" => ComparisonMetric.Stability,
            "run2-delta" or "delta" => ComparisonMetric.Run2Delta,
            "thinking-coverage" or "thinking" => ComparisonMetric.ThinkingCoverage,
            "evidence-fidelity" or "evidence" => ComparisonMetric.EvidenceFidelity,
            "location-accuracy" or "location" => ComparisonMetric.LocationAccuracy,
            "hallucination-rate" or "hallucination" => ComparisonMetric.HallucinationRate,
            "evaluation-confidence" or "confidence" => ComparisonMetric.EvaluationConfidence,
            "accountability" or "truth-audit" => ComparisonMetric.Accountability,
            "overclaim-rate" or "overclaim" => ComparisonMetric.OverclaimRate,
            "duration" or "time" => ComparisonMetric.Duration,
            "token-efficiency" or "tokens" or "score-per-1k-tokens" => ComparisonMetric.TokenEfficiency,
            "honesty" => ComparisonMetric.Honesty,
            "honesty-calibration" or "calibration" => ComparisonMetric.HonestyCalibration,
            "revision-selectivity" or "revision" => ComparisonMetric.RevisionSelectivity,
            "honesty-stability" => ComparisonMetric.HonestyStability,
            "score" or "overall" => ComparisonMetric.Score,
            _ => throw new ArgumentException("--metric must be one of: score, critical-recall, high-critical-recall, f1, fp-rate, stability, run2-delta, thinking-coverage, evidence-fidelity, location-accuracy, hallucination-rate, accountability, overclaim-rate, duration, token-efficiency, honesty, honesty-calibration, revision-selectivity, honesty-stability.")
        };
    }

    private static void PrintScore(ScoringResult score)
    {
        Console.WriteLine($"{score.RunName}: {score.ScorePercent:0.##}/100 | TP {score.FullTruePositives} full + {score.PartialTruePositives} partial | FP {score.FalsePositives} | FN {score.Missed} | Precision {score.Precision:P1} | Recall {score.Recall:P1}");
    }

    private static int PrintUsageAndReturn()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("SuperCalcBenchmark.Cli (.NET 10)");
        Console.WriteLine();
        Console.WriteLine("Commands:");
        Console.WriteLine("  models           List model ids from llama-server /v1/models");
        Console.WriteLine("  validate         Validate ground_truth.json against enhanced_calc.cpp");
        Console.WriteLine("  run              Execute Run 1 + Run 2 against llama-server and score offline");
        Console.WriteLine("  score-fixture    Score a saved model response without contacting llama-server");
        Console.WriteLine("  archive-list     List archived runs grouped by model family + quant");
        Console.WriteLine("  migrate-archive-scores  Version legacy archive scorecards without changing points");
        Console.WriteLine("  backfill-archive-metrics  Explicitly backfill non-scoring schema-v4 diagnostics (dry-run by default)");
        Console.WriteLine("  compare          Build an HTML comparison (bar + radar) from archived runs");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- models --server http://127.0.0.1:1234");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- validate");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- run --model MODEL_ID");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- run --model gpt --quant Q5_K_M   # manual quant");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- archive-list");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- migrate-archive-scores --assume-profile official-v1 --dry-run");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- backfill-archive-metrics --archive ./archive  # dry-run; partial/ineligible records are retained explicitly");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- backfill-archive-metrics --archive ./archive --write --backup ./artifacts/v0.7.2-archive-backup");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- compare                          # all models");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- compare --family qwen3-coder-30b # quants of one model");
        Console.WriteLine("  dotnet run --project src/SuperCalcBenchmark.Cli -- score-fixture --response tools/response-fixtures/perfect.json --out results/perfect");
        Console.WriteLine();
        Console.WriteLine("Common options:");
        Console.WriteLine("  --server <url>             Default: http://127.0.0.1:1234");
        Console.WriteLine("  --model <id>               Required for run");
        Console.WriteLine("  --source <file>            Default: enhanced_calc.cpp");
        Console.WriteLine("  --ground-truth <file>      Default: benchmarks/supercalc-v3/ground_truth.json");
        Console.WriteLine("  --out <dir>                Default: %LOCALAPPDATA%/SuperCalcBenchmark/Runs/<timestamp_model>");
        Console.WriteLine("  --temperature <number>     Default: 0.0");
        Console.WriteLine("  --top-p <number>           Default: 1.0");
        Console.WriteLine("  --seed <int>               Default: 12345");
        Console.WriteLine("  --repeats <int>            Run N independent repeats as one repeatGroupId. Default: 1");
        Console.WriteLine("  --seed-start <int>         First seed for --repeats; seeds increment by 1");
        Console.WriteLine("  --max-tokens <int>         Default: -1 (llama.cpp max/unbounded; server ctx/timeout still apply)");
        Console.WriteLine($"  --timeout-seconds <int>    Run default: {BenchmarkDefaults.OfficialRequestTimeoutSeconds} (4h/request); models default: {BenchmarkDefaults.ModelListTimeoutSeconds}");
        Console.WriteLine("  --skip-response-format     Do not send llama.cpp response_format");
        Console.WriteLine("  --disable-thinking         Send chat_template_kwargs.enable_thinking=false for Qwen/debug runs");
        Console.WriteLine("  --profile <official|debug|fixture>  Label archived run context. Default: official");
        Console.WriteLine("  --scoring-profile <official-v1|official-v2>  Scoring profile for run/fixture/compare. Default: official-v1");
        Console.WriteLine("  --with-truth-audit [always|never|only-best-repeat]  Run non-blind Run 3 honesty/accountability audit after Run 1+2");
        Console.WriteLine("  --truth-audit-source <best|run1|run2>  Previous answer audited by Run 3. Default: best");
        Console.WriteLine("  --no-loop-abort            Disable final-output repetition guard (not recommended)");
        Console.WriteLine("  --allow-hash-mismatch      Development escape hatch; do not use for official scoring");
        Console.WriteLine();
        Console.WriteLine("Archive / comparison options:");
        Console.WriteLine("  --archive <dir>            Archive folder. Default: ./archive");
        Console.WriteLine("  --no-archive               Do not archive this run");
        Console.WriteLine("  --quant <label>            Manual quant label when the model id has none (e.g. Q4_K_M)");
        Console.WriteLine("  --adjudication <file>      Apply local reviewer decisions after automatic scoring (score label +adjudicated)");
        Console.WriteLine("  --benchmark <id>           Restrict archive-list/compare to one benchmark id");
        Console.WriteLine("  --family <name>            compare: only quants of this model family");
        Console.WriteLine("  --aggregate <average|median|best> compare: headline score per group. Default: average");
        Console.WriteLine("  --run-view <primary|run1|run2|delta> compare: selected run perspective. Default: primary");
        Console.WriteLine("  --metric <score|critical-recall|f1|fp-rate|stability|run2-delta|thinking-coverage|evidence-fidelity|location-accuracy|hallucination-rate|accountability|duration|token-efficiency|honesty|honesty-calibration|revision-selectivity|honesty-stability>");
        Console.WriteLine("  --scoring-profile <name>   compare: include only runs scored with this profile (e.g. official-v1)");
        Console.WriteLine("  --assume-profile <name>    migrate-archive-scores: mark legacy scores with this profile");
        Console.WriteLine("  --write / --dry-run        archive migration/backfill: dry-run is default; partial/ineligible diagnostics remain explicit");
        Console.WriteLine("  --backup <dir>             archive migration/backfill: byte-exact backup destination before writes");
        Console.WriteLine("  --public-labels            compare: hide vulnerability titles/CWEs/modules in the HTML payload");
    }

    private sealed class ParsedArgs
    {
        private readonly Dictionary<string, string?> _options = new(StringComparer.OrdinalIgnoreCase);

        public string Command { get; private set; } = string.Empty;
        public bool HelpRequested => Has("--help") || Has("-h") || string.Equals(Command, "help", StringComparison.OrdinalIgnoreCase);

        public static ParsedArgs Parse(string[] args)
        {
            var parsed = new ParsedArgs();
            var index = 0;
            if (args.Length > 0 && !args[0].StartsWith("-", StringComparison.Ordinal))
            {
                parsed.Command = args[0];
                index = 1;
            }

            while (index < args.Length)
            {
                var current = args[index];
                if (!current.StartsWith("-", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                if (index + 1 < args.Length && IsValueToken(args[index + 1]))
                {
                    parsed._options[current] = args[index + 1];
                    index += 2;
                }
                else
                {
                    parsed._options[current] = null;
                    index++;
                }
            }

            return parsed;
        }

        private static bool IsValueToken(string value)
        {
            if (!value.StartsWith("-", StringComparison.Ordinal))
            {
                return true;
            }

            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _);
        }

        public bool Has(string name) => _options.ContainsKey(name);

        public string? GetNullable(string name) => _options.TryGetValue(name, out var value) ? value : null;

        public string Get(string name, string defaultValue) => _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value! : defaultValue;

        public int GetInt(string name, int defaultValue)
        {
            var value = GetNullable(name);
            return int.TryParse(value, out var parsed) ? parsed : defaultValue;
        }

        public double GetDouble(string name, double defaultValue)
        {
            var value = GetNullable(name);
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : defaultValue;
        }
    }
}
