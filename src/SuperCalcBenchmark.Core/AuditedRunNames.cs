namespace SuperCalcBenchmark.Core;

/// <summary>Canonical names accepted by the truth-audit schema.</summary>
public static class AuditedRunNames
{
    public static string? Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "run1" or "run 1" or "run-1" or "1" => "Run 1",
        "run2" or "run 2" or "run-2" or "2" => "Run 2",
        _ => null
    };

    public static bool Equivalent(string? left, string? right)
    {
        var a = Normalize(left);
        return a is not null && a == Normalize(right);
    }
}
