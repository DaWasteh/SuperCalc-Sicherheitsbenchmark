namespace SuperCalcBenchmark.Core;

public sealed class ArchiveMetricsBackfillOptions
{
    public bool Write { get; init; }
    public string? BackupDirectory { get; init; }
    public DateTimeOffset ComputedAt { get; init; } = DateTimeOffset.UtcNow;
}
public sealed class ArchiveMetricsBackfillFileResult
{
    public string Path { get; init; } = "";
    public string Status { get; init; } = "unavailable";
    public bool WouldWrite { get; init; }
    public bool Written { get; init; }
    public string? BackupPath { get; init; }
    public string? Warning { get; init; }
}
public sealed class ArchiveMetricsBackfillResult
{
    public int Scanned { get; init; }
    public int Complete { get; init; }
    public int Partial { get; init; }
    public int Unavailable { get; init; }
    public int AlreadyCurrent { get; init; }
    public int WouldWrite { get; init; }
    public int Written { get; init; }
    public int Backups { get; init; }
    public int InvariantFailures { get; init; }
    public string? BackupDirectory { get; init; }
    public List<ArchiveMetricsBackfillFileResult> Files { get; init; } = [];
    public bool HasErrors => Files.Any(x => x.Warning is not null) || InvariantFailures > 0;
}
