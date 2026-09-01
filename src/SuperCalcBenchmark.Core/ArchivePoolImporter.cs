using System.Security.Cryptography;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class ArchiveImportResult
{
    public int Scanned { get; init; }
    public int Imported { get; init; }
    public int AlreadyPresent { get; init; }
    public int Failed { get; init; }
    public List<string> Warnings { get; init; } = [];
}

/// <summary>
/// Non-destructively imports legacy archive scorecards (for example checkout/archive
/// or archive beside an older portable EXE) into the shared per-user archive pool.
/// Record ids and byte hashes make repeated starts idempotent.
/// </summary>
public static class ArchivePoolImporter
{
    public static ArchiveImportResult ImportLegacyArchive(string sourceRoot, string targetRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || string.IsNullOrWhiteSpace(targetRoot))
        {
            throw new ArgumentException("Source and target archive roots are required.");
        }

        sourceRoot = Path.GetFullPath(sourceRoot);
        targetRoot = Path.GetFullPath(targetRoot);
        if (BenchmarkPathResolver.SamePath(sourceRoot, targetRoot) || !Directory.Exists(sourceRoot))
        {
            return new ArchiveImportResult();
        }

        Directory.CreateDirectory(targetRoot);
        using var importLock = TryAcquireImportLock(targetRoot, TimeSpan.FromSeconds(30));
        if (importLock is null)
        {
            return new ArchiveImportResult
            {
                Failed = 1,
                Warnings = ["Could not acquire the shared archive import lock within 30 seconds."]
            };
        }

        var targetFiles = EnumerateScorecards(targetRoot).ToList();
        var targetIds = targetFiles
            .Select(TryReadRecordId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var targetHashes = targetFiles
            .Where(path => string.IsNullOrWhiteSpace(TryReadRecordId(path)))
            .Select(TryHash)
            .Where(hash => !string.IsNullOrWhiteSpace(hash))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sourceFiles = EnumerateScorecards(sourceRoot)
            .Where(path => !IsInside(targetRoot, path))
            .ToList();
        var scanned = 0;
        var imported = 0;
        var alreadyPresent = 0;
        var failed = 0;
        var warnings = new List<string>();
        foreach (var sourcePath in sourceFiles)
        {
            scanned++;
            try
            {
                var recordId = TryReadRecordId(sourcePath);
                if (!string.IsNullOrWhiteSpace(recordId) && targetIds.Contains(recordId))
                {
                    alreadyPresent++;
                    continue;
                }

                var hash = TryHash(sourcePath);
                if (!string.IsNullOrWhiteSpace(hash) && targetHashes.Contains(hash))
                {
                    alreadyPresent++;
                    continue;
                }

                var relative = Path.GetRelativePath(sourceRoot, sourcePath);
                var preferredTarget = Path.GetFullPath(Path.Combine(targetRoot, relative));
                if (!IsInside(targetRoot, preferredTarget))
                {
                    failed++;
                    warnings.Add($"Skipped archive path outside target root: {relative}");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(preferredTarget)!);
                if (!PublishCopyAtomically(sourcePath, preferredTarget, hash))
                {
                    alreadyPresent++;
                    continue;
                }

                imported++;
                if (!string.IsNullOrWhiteSpace(recordId))
                {
                    targetIds.Add(recordId);
                }

                if (!string.IsNullOrWhiteSpace(hash))
                {
                    targetHashes.Add(hash);
                }
            }
            catch (Exception exception) when (exception is IOException
                                              or UnauthorizedAccessException
                                              or JsonException
                                              or ArgumentException
                                              or NotSupportedException)
            {
                failed++;
                warnings.Add($"Could not import '{sourcePath}': {exception.Message}");
            }
        }

        return new ArchiveImportResult
        {
            Scanned = scanned,
            Imported = imported,
            AlreadyPresent = alreadyPresent,
            Failed = failed,
            Warnings = warnings
        };
    }

    private static FileStream? TryAcquireImportLock(string targetRoot, TimeSpan timeout)
    {
        var lockPath = Path.Combine(targetRoot, ".archive-import.lock");
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    private static IReadOnlyList<string> EnumerateScorecards(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.json", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint
                })
                .Where(path =>
                {
                    var relative = Path.GetRelativePath(root, path);
                    var segments = relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
                    return !segments.Any(segment => string.Equals(segment, "_reports", StringComparison.OrdinalIgnoreCase)
                                                   || string.Equals(segment, "_migration-backup", StringComparison.OrdinalIgnoreCase));
                })
                .ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string? TryReadRecordId(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Name.Equals("recordId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return property.Value.GetString();
                }
            }
        }
        catch (Exception exception) when (exception is JsonException
                                          or IOException
                                          or UnauthorizedAccessException)
        {
            // Malformed legacy files can still be imported/deduplicated by byte hash.
        }

        return null;
    }

    private static string? TryHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static bool PublishCopyAtomically(string sourcePath, string preferredTarget, string? sourceHash)
    {
        var directory = Path.GetDirectoryName(preferredTarget)!;
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(preferredTarget)}.import-{Guid.NewGuid():N}.tmp");
        File.Copy(sourcePath, temporaryPath, overwrite: false);
        try
        {
            var stem = Path.GetFileNameWithoutExtension(preferredTarget);
            var extension = Path.GetExtension(preferredTarget);
            for (var attempt = 1; attempt < 1000; attempt++)
            {
                var candidate = attempt == 1
                    ? preferredTarget
                    : Path.Combine(directory, $"{stem}-import-{attempt}{extension}");
                if (File.Exists(candidate))
                {
                    if (!string.IsNullOrWhiteSpace(sourceHash)
                        && string.Equals(sourceHash, TryHash(candidate), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    continue;
                }

                try
                {
                    File.Move(temporaryPath, candidate);
                    return true;
                }
                catch (IOException) when (File.Exists(candidate))
                {
                    // A parallel importer claimed this candidate between the existence
                    // check and move. Identical content means that import already won.
                    if (!string.IsNullOrWhiteSpace(sourceHash)
                        && string.Equals(sourceHash, TryHash(candidate), StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }
                }
            }

            throw new IOException($"Could not reserve a unique import target for '{preferredTarget}'.");
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Import temp files do not match *.json and are safe to ignore.
            }
        }
    }

    private static bool IsInside(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        return candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }
}
