using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Persists a compact <see cref="ArchiveRecord"/> for every run and reads them back grouped
/// by model family + quant. Layout inside the repository:
///
///   archive/
///     &lt;benchmarkId&gt;/
///       &lt;family&gt;__&lt;quant&gt;/
///         20260621-143012_qwen3-coder-30b.json
///
/// Grouping by the <c>family__quant</c> folder is what lets a comparison line up, say, every
/// Q4_K_M run of qwen3-coder-30b, while still keeping each quant in its own bucket.
/// </summary>
public sealed class ArchiveStore
{
    public const string DefaultArchiveFolderName = "archive";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly string _archiveRoot;

    /// <param name="archiveRoot">
    /// Absolute path to the archive folder (typically &lt;repoRoot&gt;/archive). Created on demand.
    /// </param>
    public ArchiveStore(string archiveRoot)
    {
        if (string.IsNullOrWhiteSpace(archiveRoot))
        {
            throw new ArgumentException("Archive root path is required.", nameof(archiveRoot));
        }

        _archiveRoot = Path.GetFullPath(archiveRoot);
    }

    public string ArchiveRoot => _archiveRoot;

    /// <summary>
    /// Convenience factory: archive folder lives directly under the repository root.
    /// </summary>
    public static ArchiveStore ForRepository(string repositoryRoot)
        => new(Path.Combine(repositoryRoot, DefaultArchiveFolderName));

    /// <summary>
    /// Builds an <see cref="ArchiveRecord"/> from a finished run and writes it to the archive.
    /// Returns the path of the JSON file that was written.
    /// </summary>
    public string Save(BenchmarkRunResult result, string? quantOverride = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var record = BuildRecord(result, quantOverride);
        var directory = Path.Combine(_archiveRoot, TextUtil.SafeFileNamePart(record.BenchmarkId), record.GroupKey);
        Directory.CreateDirectory(directory);

        var stamp = result.StartedAt.ToLocalTime().ToString("yyyyMMdd-HHmmss");
        var fileName = $"{stamp}_{TextUtil.SafeFileNamePart(record.ModelFamily)}.json";
        var path = Path.Combine(directory, fileName);

        // Guard against two runs in the same second clobbering each other.
        path = EnsureUniquePath(path);

        File.WriteAllText(path, JsonSerializer.Serialize(record, WriteOptions), Encoding.UTF8);
        return path;
    }

    public static ArchiveRecord BuildRecord(BenchmarkRunResult result, string? quantOverride = null)
    {
        var identity = ModelIdentity.Parse(result.Model, quantOverride);

        var runs = new List<ArchiveRunScore>();
        if (result.Run1?.Score is not null)
        {
            runs.Add(ArchiveRunScore.FromArtifacts(result.Run1));
        }

        if (result.Run2?.Score is not null)
        {
            runs.Add(ArchiveRunScore.FromArtifacts(result.Run2));
        }

        if (result.Run3?.TruthAudit is not null)
        {
            runs.Add(ArchiveRunScore.FromArtifacts(result.Run3));
        }

        foreach (var run in runs)
        {
            run.SourceSha256 = string.IsNullOrWhiteSpace(run.SourceSha256) ? result.SourceSha256 : run.SourceSha256;
            run.OfficialComparable = run.OfficialComparable
                                     && result.SourceHashMatches
                                     && string.Equals(result.BenchmarkProfile, "official", StringComparison.OrdinalIgnoreCase)
                                     && !run.ManuallyStopped
                                     && !run.LoopDetected
                                     && !run.GroundTruthVisibleToModel
                                     && !string.Equals(run.RunKind, "truth_audit", StringComparison.OrdinalIgnoreCase);
        }

        if (result.Comparison is not null)
        {
            var run2 = runs.FirstOrDefault(run => string.Equals(run.RunName, "Run 2", StringComparison.OrdinalIgnoreCase));
            if (run2 is not null)
            {
                run2.SelfValidation = result.Comparison;
            }
        }

        var scoreVersions = runs.Select(run => ArchiveScoreVersion.FromRun(run, "native")).ToList();
        var availableProfiles = runs
            .Select(run => run.ScoringProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (availableProfiles.Count == 0)
        {
            availableProfiles.Add(ScoringProfiles.OfficialV1Name);
        }

        return new ArchiveRecord
        {
            SchemaVersion = ArchiveRecord.CurrentSchemaVersion,
            RecordId = Guid.NewGuid().ToString("N"),
            BenchmarkId = string.IsNullOrWhiteSpace(result.BenchmarkId) ? "unknown-benchmark" : result.BenchmarkId,
            BenchmarkProfile = string.IsNullOrWhiteSpace(result.BenchmarkProfile) ? "official" : result.BenchmarkProfile,
            ToolVersion = result.ToolVersion,
            RawModelId = identity.RawModelId,
            ModelFamily = identity.Family,
            Quant = identity.Quant,
            QuantWasDetected = identity.QuantWasDetected,
            GroupKey = identity.GroupKey,
            ServerUrlHash = HashServerUrl(result.ServerUrl),
            ServerLabel = string.IsNullOrWhiteSpace(result.ServerUrl) ? string.Empty : "server-" + HashServerUrl(result.ServerUrl)[..8],
            ServerContextSize = result.ServerContextSize,
            MaxTokens = result.MaxTokens,
            TimeoutSeconds = result.TimeoutSeconds,
            Seed = result.Seed,
            RepeatGroupId = result.RepeatGroupId,
            RepeatIndex = Math.Max(1, result.RepeatIndex),
            RepeatCount = Math.Max(1, result.RepeatCount),
            SkipResponseFormat = result.SkipResponseFormat,
            DisableThinking = result.DisableThinking,
            AbortOnLoop = result.AbortOnLoop,
            SourceFile = result.SourceFile,
            SourceSha256 = result.SourceSha256,
            ExpectedSourceSha256 = result.ExpectedSourceSha256,
            SourceHashMatches = result.SourceHashMatches,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            DurationMs = result.DurationMs,
            ModelMetadata = new ArchiveModelMetadata
            {
                Family = identity.Family,
                Quant = identity.Quant,
                QuantBits = EstimateQuantBits(identity.Quant)
            },
            ServerMetadata = new ArchiveServerMetadata
            {
                ServerContextSize = result.ServerContextSize
            },
            RunDirectory = result.OutputDirectory,
            Runs = runs,
            ScoreVersions = scoreVersions,
            DefaultDetectionProfile = availableProfiles.Contains(ScoringProfiles.OfficialV1Name, StringComparer.OrdinalIgnoreCase)
                ? ScoringProfiles.OfficialV1Name
                : availableProfiles[0],
            AvailableDetectionProfiles = availableProfiles
        };
    }

    /// <summary>
    /// Loads every archived record, optionally restricted to a single benchmark id.
    /// Unreadable or malformed files are skipped rather than aborting the whole load.
    /// </summary>
    public IReadOnlyList<ArchiveRecord> LoadAll(string? benchmarkId = null)
    {
        if (!Directory.Exists(_archiveRoot))
        {
            return [];
        }

        var records = new List<ArchiveRecord>();
        foreach (var path in Directory.EnumerateFiles(_archiveRoot, "*.json", SearchOption.AllDirectories)
                     .Where(path => !IsMigrationBackupPath(path)))
        {
            var record = TryLoad(path);
            if (record is null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(benchmarkId)
                && !string.Equals(record.BenchmarkId, benchmarkId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            records.Add(record);
        }

        return records;
    }

    /// <summary>
    /// Groups archived records by model family + quant, sorted by best primary-run score.
    /// </summary>
    public IReadOnlyList<ArchiveGroup> LoadGroups(string? benchmarkId = null)
    {
        return LoadAll(benchmarkId)
            .GroupBy(r => r.GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => new ArchiveGroup
            {
                GroupKey = g.Key,
                ModelFamily = g.First().ModelFamily,
                Quant = g.First().Quant,
                Records = g.OrderByDescending(r => r.CompletedAt).ToList()
            })
            .OrderByDescending(g => g.BestScorePercent)
            .ThenBy(g => g.GroupKey, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Distinct model families present in the archive, for picker UIs. A family containing
    /// several quants surfaces once.
    /// </summary>
    public IReadOnlyList<string> LoadFamilies(string? benchmarkId = null)
    {
        return LoadAll(benchmarkId)
            .Select(r => r.ModelFamily)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Updates only the manually editable archive identity fields for already-loaded records
    /// and writes the scorecards back to disk. Files are moved into the matching
    /// archive/&lt;benchmark&gt;/&lt;family&gt;__&lt;quant&gt;/ folder when the group changes.
    /// </summary>
    public IReadOnlyList<string> UpdateIdentity(IEnumerable<ArchiveRecord> records, string modelFamily, string quant)
    {
        ArgumentNullException.ThrowIfNull(records);

        var normalizedFamily = (modelFamily ?? string.Empty).Trim();
        var normalizedQuant = (quant ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedFamily))
        {
            throw new ArgumentException("Model family must not be empty.", nameof(modelFamily));
        }

        if (string.IsNullOrWhiteSpace(normalizedQuant))
        {
            throw new ArgumentException("Quant must not be empty.", nameof(quant));
        }

        var updatedPaths = new List<string>();
        foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.ArchivePath))
            {
                throw new InvalidOperationException("Archive record has no source path and cannot be updated in place.");
            }

            var oldPath = Path.GetFullPath(record.ArchivePath);
            if (!File.Exists(oldPath))
            {
                throw new FileNotFoundException("Archive record JSON was not found.", oldPath);
            }

            record.ModelFamily = normalizedFamily;
            record.Quant = normalizedQuant;
            record.QuantWasDetected = false;
            record.GroupKey = ModelIdentity.GroupKey(record.ModelFamily, record.Quant);
            record.ModelMetadata ??= new ArchiveModelMetadata();
            record.ModelMetadata.Family = record.ModelFamily;
            record.ModelMetadata.Quant = record.Quant;
            record.ModelMetadata.QuantBits ??= EstimateQuantBits(record.Quant);

            var targetDirectory = Path.Combine(_archiveRoot, TextUtil.SafeFileNamePart(record.BenchmarkId), record.GroupKey);
            Directory.CreateDirectory(targetDirectory);

            var newPath = Path.Combine(targetDirectory, Path.GetFileName(oldPath));
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
            {
                newPath = EnsureUniquePath(newPath);
            }

            File.WriteAllText(newPath, JsonSerializer.Serialize(record, WriteOptions), Encoding.UTF8);
            if (!string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(oldPath);
                TryDeleteEmptyDirectory(Path.GetDirectoryName(oldPath));
            }

            record.ArchivePath = newPath;
            updatedPaths.Add(newPath);
        }

        return updatedPaths;
    }

    public ArchiveMigrationResult MigrateScores(ArchiveMigrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!Directory.Exists(_archiveRoot))
        {
            return new ArchiveMigrationResult { BackupDirectory = options.BackupDirectory };
        }

        var files = new List<ArchiveMigrationFileResult>();
        var scanned = 0;
        var changedFiles = 0;
        var writtenFiles = 0;
        var migratedRuns = 0;
        var alreadyVersionedRuns = 0;

        foreach (var path in Directory.EnumerateFiles(_archiveRoot, "*.json", SearchOption.AllDirectories)
                     .Where(path => !IsMigrationBackupPath(path)))
        {
            scanned++;
            var json = File.ReadAllText(path, Encoding.UTF8);
            var runProfileStates = ReadRunProfileStates(json, out var hasScoreVersions);
            ArchiveRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ArchiveRecord>(json, ReadOptions);
            }
            catch (JsonException ex)
            {
                files.Add(new ArchiveMigrationFileResult { Path = path, Warning = ex.Message });
                continue;
            }

            if (record is null || record.Runs.Count == 0)
            {
                files.Add(new ArchiveMigrationFileResult { Path = path, Warning = "No archive runs found." });
                continue;
            }

            NormalizeLoadedRecord(record);
            record.ArchivePath = Path.GetFullPath(path);

            var fileMigratedRuns = 0;
            var fileAlreadyVersionedRuns = 0;
            for (var i = 0; i < record.Runs.Count; i++)
            {
                var run = record.Runs[i];
                var missingProfile = i >= runProfileStates.Count || runProfileStates[i];
                if (missingProfile)
                {
                    ApplyLegacyMigrationMetadata(record, run, options);
                    fileMigratedRuns++;
                    continue;
                }

                fileAlreadyVersionedRuns++;
            }

            RefreshRecordScoreProfiles(record);
            var changed = fileMigratedRuns > 0 || !hasScoreVersions;
            string? backupPath = null;
            if (changed)
            {
                changedFiles++;
                if (options.Write)
                {
                    if (!string.IsNullOrWhiteSpace(options.BackupDirectory))
                    {
                        backupPath = BackupArchiveFile(path, options.BackupDirectory);
                    }

                    File.WriteAllText(path, JsonSerializer.Serialize(record, WriteOptions), Encoding.UTF8);
                    writtenFiles++;
                }
            }

            migratedRuns += fileMigratedRuns;
            alreadyVersionedRuns += fileAlreadyVersionedRuns;
            files.Add(new ArchiveMigrationFileResult
            {
                Path = path,
                Changed = changed,
                Written = changed && options.Write,
                MigratedRuns = fileMigratedRuns,
                AlreadyVersionedRuns = fileAlreadyVersionedRuns,
                Profile = options.AssumedProfile,
                BackupPath = backupPath
            });
        }

        return new ArchiveMigrationResult
        {
            FilesScanned = scanned,
            FilesChanged = changedFiles,
            FilesWritten = writtenFiles,
            RunsMigrated = migratedRuns,
            RunsAlreadyVersioned = alreadyVersionedRuns,
            BackupDirectory = options.BackupDirectory,
            Files = files
        };
    }

    private static ArchiveRecord? TryLoad(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var record = JsonSerializer.Deserialize<ArchiveRecord>(stream, ReadOptions);
            if (record is null || record.Runs.Count == 0)
            {
                return null;
            }

            NormalizeLoadedRecord(record);
            record.ArchivePath = Path.GetFullPath(path);
            return record;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void NormalizeLoadedRecord(ArchiveRecord record)
    {
        // Treat modelFamily + quant as the manually editable source of truth. groupKey is
        // only a derived/cache field written for readability, so a user can correct just
        // "quant" in an archived scorecard without also moving folders or editing groupKey.
        var identity = ModelIdentity.Parse(record.RawModelId);

        if (record.SchemaVersion <= 0)
        {
            record.SchemaVersion = 1;
        }

        record.BenchmarkId = string.IsNullOrWhiteSpace(record.BenchmarkId)
            ? "unknown-benchmark"
            : record.BenchmarkId.Trim();
        record.BenchmarkProfile = string.IsNullOrWhiteSpace(record.BenchmarkProfile)
            ? "official"
            : record.BenchmarkProfile.Trim();
        record.ModelFamily = string.IsNullOrWhiteSpace(record.ModelFamily)
            ? identity.Family
            : record.ModelFamily.Trim();
        record.Quant = string.IsNullOrWhiteSpace(record.Quant)
            ? identity.Quant
            : record.Quant.Trim();
        record.GroupKey = ModelIdentity.GroupKey(record.ModelFamily, record.Quant);

        if (string.IsNullOrWhiteSpace(record.ExpectedSourceSha256))
        {
            record.ExpectedSourceSha256 = record.SourceSha256;
        }

        if (record.DurationMs <= 0 && record.StartedAt != default && record.CompletedAt != default)
        {
            record.DurationMs = Math.Max(0, (long)(record.CompletedAt - record.StartedAt).TotalMilliseconds);
        }

        record.ModelMetadata ??= new ArchiveModelMetadata();
        if (string.IsNullOrWhiteSpace(record.ModelMetadata.Family))
        {
            record.ModelMetadata.Family = record.ModelFamily;
        }

        if (string.IsNullOrWhiteSpace(record.ModelMetadata.Quant))
        {
            record.ModelMetadata.Quant = record.Quant;
        }

        record.ModelMetadata.QuantBits ??= EstimateQuantBits(record.Quant);
        record.ServerMetadata ??= new ArchiveServerMetadata();
        record.ServerMetadata.ServerContextSize ??= record.ServerContextSize;
        record.ServerContextSize ??= record.ServerMetadata.ServerContextSize;

        foreach (var run in record.Runs)
        {
            run.NormalizeAfterLoad(record);
        }

        record.ScoreVersions ??= [];
        if (record.ScoreVersions.Count == 0)
        {
            record.ScoreVersions = record.Runs
                .Select(run => ArchiveScoreVersion.FromRun(run, run.IsLegacyMigrated ? "legacy_migrated" : "native"))
                .ToList();
        }

        record.AvailableDetectionProfiles = record.Runs
            .Select(run => run.ScoringProfile)
            .Concat(record.ScoreVersions.Select(version => version.Profile))
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (record.AvailableDetectionProfiles.Count == 0)
        {
            record.AvailableDetectionProfiles.Add("legacy-unknown");
        }

        if (string.IsNullOrWhiteSpace(record.DefaultDetectionProfile)
            || !record.AvailableDetectionProfiles.Contains(record.DefaultDetectionProfile, StringComparer.OrdinalIgnoreCase))
        {
            record.DefaultDetectionProfile = record.AvailableDetectionProfiles.Contains(ScoringProfiles.OfficialV1Name, StringComparer.OrdinalIgnoreCase)
                ? ScoringProfiles.OfficialV1Name
                : record.AvailableDetectionProfiles[0];
        }
    }

    private static List<bool> ReadRunProfileStates(string json, out bool hasScoreVersions)
    {
        hasScoreVersions = false;
        var states = new List<bool>();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true
            });
            var root = document.RootElement;
            hasScoreVersions = root.TryGetProperty("scoreVersions", out var versions)
                               && versions.ValueKind == JsonValueKind.Array;
            if (!root.TryGetProperty("runs", out var runs) || runs.ValueKind != JsonValueKind.Array)
            {
                return states;
            }

            foreach (var run in runs.EnumerateArray())
            {
                states.Add(!run.TryGetProperty("scoringProfile", out var profile)
                           || profile.ValueKind == JsonValueKind.Null
                           || string.IsNullOrWhiteSpace(profile.GetString()));
            }
        }
        catch (JsonException)
        {
            // The caller will report the deserialize failure; return an empty state list here.
        }

        return states;
    }

    private static void ApplyLegacyMigrationMetadata(ArchiveRecord record, ArchiveRunScore run, ArchiveMigrationOptions options)
    {
        var profileName = string.IsNullOrWhiteSpace(options.AssumedProfile)
            ? "legacy-unknown"
            : options.AssumedProfile.Trim();
        var isOfficialV1 = string.Equals(profileName, ScoringProfiles.OfficialV1Name, StringComparison.OrdinalIgnoreCase);

        run.ScoreSchemaVersion = ScoringProfiles.ScoreSchemaVersion;
        run.ScoringProfile = isOfficialV1 ? ScoringProfiles.OfficialV1Name : profileName;
        run.ScoringProfileVersion = isOfficialV1 ? ScoringProfiles.OfficialV1Version : 0;
        run.ScoringEngineVersion = isOfficialV1 ? ScoringProfiles.OfficialV1EngineVersion : "unknown";
        run.ParserVersion = ResponseParser.CurrentParserVersion;
        run.GroundTruthSha256 = string.IsNullOrWhiteSpace(run.GroundTruthSha256) ? options.GroundTruthSha256 : run.GroundTruthSha256;
        run.SourceSha256 = FirstNonEmpty(run.SourceSha256, record.SourceSha256, options.SourceSha256);
        run.PromptVersion = string.IsNullOrWhiteSpace(run.PromptVersion) || string.Equals(run.PromptVersion, PromptVersions.Unknown, StringComparison.OrdinalIgnoreCase)
            ? PromptVersions.ForRunName(run.RunName)
            : run.PromptVersion;
        var completedAt = run.CompletedAt ?? record.CompletedAt;
        run.ComputedAt = completedAt == default ? options.MigratedAt : completedAt;
        run.IsLegacyMigrated = true;
        run.IsRescored = false;
        run.OfficialComparable = ScoringProfiles.IsOfficialComparableProfile(run.ScoringProfile) && record.SourceHashMatches;

        record.LegacyMigration = new ArchiveLegacyMigration
        {
            IsLegacyMigrated = true,
            MigratedAt = options.MigratedAt,
            AssumedProfile = run.ScoringProfile
        };
    }

    private static void RefreshRecordScoreProfiles(ArchiveRecord record)
    {
        record.ScoreVersions = record.Runs
            .Select(run => ArchiveScoreVersion.FromRun(run, run.IsLegacyMigrated ? "legacy_migrated" : (run.IsRescored ? "recomputed_from_run_json" : "native")))
            .ToList();

        record.AvailableDetectionProfiles = record.Runs
            .Select(run => run.ScoringProfile)
            .Where(profile => !string.IsNullOrWhiteSpace(profile))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(profile => profile, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (record.AvailableDetectionProfiles.Count == 0)
        {
            record.AvailableDetectionProfiles.Add("legacy-unknown");
        }

        record.DefaultDetectionProfile = record.AvailableDetectionProfiles.Contains(ScoringProfiles.OfficialV1Name, StringComparer.OrdinalIgnoreCase)
            ? ScoringProfiles.OfficialV1Name
            : record.AvailableDetectionProfiles[0];
    }

    private string BackupArchiveFile(string path, string backupDirectory)
    {
        var fullBackupRoot = Path.GetFullPath(backupDirectory);
        var relative = Path.GetRelativePath(_archiveRoot, path);
        var backupPath = Path.Combine(fullBackupRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        File.Copy(path, backupPath, overwrite: true);
        return backupPath;
    }

    private static string FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

    private static bool IsMigrationBackupPath(string path)
        => path.Contains($"{Path.DirectorySeparatorChar}_migration-backup{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
           || path.Contains($"{Path.AltDirectorySeparatorChar}_migration-backup{Path.AltDirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);

    private static string HashServerUrl(string serverUrl)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
        {
            return string.Empty;
        }

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(serverUrl.Trim()));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static double? EstimateQuantBits(string quant)
    {
        if (string.IsNullOrWhiteSpace(quant))
        {
            return null;
        }

        var upper = quant.ToUpperInvariant();
        if (upper.Contains("IQ1", StringComparison.Ordinal) || upper.Contains("Q1", StringComparison.Ordinal)) return 1;
        if (upper.Contains("IQ2", StringComparison.Ordinal) || upper.Contains("Q2", StringComparison.Ordinal)) return 2;
        if (upper.Contains("IQ3", StringComparison.Ordinal) || upper.Contains("Q3", StringComparison.Ordinal)) return 3;
        if (upper.Contains("IQ4", StringComparison.Ordinal) || upper.Contains("Q4", StringComparison.Ordinal)) return 4;
        if (upper.Contains("IQ5", StringComparison.Ordinal) || upper.Contains("Q5", StringComparison.Ordinal)) return 5;
        if (upper.Contains("IQ6", StringComparison.Ordinal) || upper.Contains("Q6", StringComparison.Ordinal)) return 6;
        if (upper.Contains("Q8", StringComparison.Ordinal)) return 8;
        if (upper.Contains("F16", StringComparison.Ordinal) || upper.Contains("FP16", StringComparison.Ordinal)) return 16;
        return null;
    }

    private static void TryDeleteEmptyDirectory(string? directory)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(directory)
                && Directory.Exists(directory)
                && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch
        {
            // Best-effort cleanup only; stale empty folders must never break an edit.
        }
    }

    private static string EnsureUniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var directory = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var candidate = Path.Combine(directory, $"{stem}-{i}{extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Extremely unlikely; fall back to a guid suffix.
        return Path.Combine(directory, $"{stem}-{Guid.NewGuid():N}{extension}");
    }
}
