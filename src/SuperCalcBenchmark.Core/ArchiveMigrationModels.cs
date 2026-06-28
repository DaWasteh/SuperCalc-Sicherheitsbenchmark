namespace SuperCalcBenchmark.Core;

public sealed class ArchiveMigrationOptions
{
    public string AssumedProfile { get; init; } = ScoringProfiles.OfficialV1Name;
    public bool Write { get; init; }
    public string? BackupDirectory { get; init; }
    public string GroundTruthSha256 { get; init; } = string.Empty;
    public string SourceSha256 { get; init; } = string.Empty;
    public DateTimeOffset MigratedAt { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ArchiveMigrationResult
{
    public int FilesScanned { get; init; }
    public int FilesChanged { get; init; }
    public int FilesWritten { get; init; }
    public int RunsMigrated { get; init; }
    public int RunsAlreadyVersioned { get; init; }
    public string? BackupDirectory { get; init; }
    public List<ArchiveMigrationFileResult> Files { get; init; } = [];
}

public sealed class ArchiveMigrationFileResult
{
    public string Path { get; init; } = string.Empty;
    public bool Changed { get; init; }
    public bool Written { get; init; }
    public int MigratedRuns { get; init; }
    public int AlreadyVersionedRuns { get; init; }
    public string Profile { get; init; } = string.Empty;
    public string? BackupPath { get; init; }
    public string? Warning { get; init; }
}
