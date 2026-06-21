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
            runs.Add(ArchiveRunScore.FromScore(result.Run1.Score));
        }

        if (result.Run2?.Score is not null)
        {
            runs.Add(ArchiveRunScore.FromScore(result.Run2.Score));
        }

        return new ArchiveRecord
        {
            SchemaVersion = ArchiveRecord.CurrentSchemaVersion,
            RecordId = Guid.NewGuid().ToString("N"),
            BenchmarkId = string.IsNullOrWhiteSpace(result.BenchmarkId) ? "unknown-benchmark" : result.BenchmarkId,
            ToolVersion = result.ToolVersion,
            RawModelId = identity.RawModelId,
            ModelFamily = identity.Family,
            Quant = identity.Quant,
            QuantWasDetected = identity.QuantWasDetected,
            GroupKey = identity.GroupKey,
            ServerContextSize = result.ServerContextSize,
            MaxTokens = result.MaxTokens,
            DisableThinking = result.DisableThinking,
            SourceSha256 = result.SourceSha256,
            SourceHashMatches = result.SourceHashMatches,
            StartedAt = result.StartedAt,
            CompletedAt = result.CompletedAt,
            RunDirectory = result.OutputDirectory,
            Runs = runs
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
        foreach (var path in Directory.EnumerateFiles(_archiveRoot, "*.json", SearchOption.AllDirectories))
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

        record.BenchmarkId = string.IsNullOrWhiteSpace(record.BenchmarkId)
            ? "unknown-benchmark"
            : record.BenchmarkId.Trim();
        record.ModelFamily = string.IsNullOrWhiteSpace(record.ModelFamily)
            ? identity.Family
            : record.ModelFamily.Trim();
        record.Quant = string.IsNullOrWhiteSpace(record.Quant)
            ? identity.Quant
            : record.Quant.Trim();
        record.GroupKey = ModelIdentity.GroupKey(record.ModelFamily, record.Quant);
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
