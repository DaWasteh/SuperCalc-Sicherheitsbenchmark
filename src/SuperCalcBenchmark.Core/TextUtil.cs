using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

internal static partial class TextUtil
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "or", "in", "via", "with", "for", "of", "to", "a", "an", "by", "on", "from",
        "cwe", "security", "vulnerability", "vulnerabilities", "issue", "bug", "flaw", "weakness",
        "critical", "high", "medium", "low", "informational", "unknown"
    };

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var formD = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    public static string CompactNormalize(string? value)
    {
        var normalized = Normalize(value);
        return normalized.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    public static IReadOnlySet<string> Tokens(string? value)
    {
        var tokens = Normalize(value)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 3 && !StopWords.Contains(t))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return tokens;
    }

    public static double TokenOverlap(string? left, string? right)
    {
        var leftTokens = Tokens(left);
        var rightTokens = Tokens(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0;
        }

        var intersection = leftTokens.Count(t => rightTokens.Contains(t));
        var denominator = Math.Min(leftTokens.Count, rightTokens.Count);
        return denominator == 0 ? 0 : Clamp01((double)intersection / denominator);
    }

    public static bool ContainsNormalized(string? haystack, string? needle)
    {
        var normalizedNeedle = Normalize(needle);
        if (normalizedNeedle.Length == 0)
        {
            return false;
        }

        return Normalize(haystack).Contains(normalizedNeedle, StringComparison.Ordinal);
    }

    public static string SymbolLeaf(string? symbol)
    {
        if (string.IsNullOrWhiteSpace(symbol))
        {
            return string.Empty;
        }

        var trimmed = symbol.Trim();
        var parts = trimmed.Split("::", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length == 0 ? trimmed : parts[^1];
    }

    public static string SafeFileNamePart(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(invalid.Contains(c) || char.IsWhiteSpace(c) ? '_' : c);
        }

        var result = builder.ToString().Trim('_');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    public static double Clamp01(double value) => Math.Min(1, Math.Max(0, value));

    public static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}
