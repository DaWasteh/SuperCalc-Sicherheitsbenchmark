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

    /// <summary>
    /// Whole-term containment used by matching profiles that opt into word boundaries.
    /// A single-word needle (letters/digits only, e.g. "system", "fact", "TEMP") must equal a
    /// whole identifier of the haystack, where '_' belongs to the identifier: "system_clock",
    /// "parse_factor" and "login_attempts_" therefore do not contain "system", "fact" or "temp".
    /// A needle with separators ("CWE-78", "format string", "result_cache_") must occur as a
    /// whole phrase of the normalized haystack: "CWE-787" does not contain "CWE-78".
    /// A trailing plural "s"/"es" on the haystack side is tolerated in both cases.
    /// </summary>
    public static bool ContainsTerm(string? haystack, string? needle)
    {
        var trimmedNeedle = (needle ?? string.Empty).Trim();
        if (trimmedNeedle.Length == 0 || string.IsNullOrWhiteSpace(haystack))
        {
            return false;
        }

        // Models that double-escape JSON leave a literal "\n" in quoted code; "{\nstrcpy(" must
        // still contain the identifier "strcpy" rather than "nstrcpy".
        haystack = LiteralEscapeRegex().Replace(haystack, " ");

        var singleWord = trimmedNeedle.All(char.IsLetterOrDigit);
        if (singleWord)
        {
            var identifierNeedle = IdentifierNormalize(trimmedNeedle);
            return identifierNeedle.Length > 0
                   && TermRegex(identifierNeedle, identifierChars: true).IsMatch(IdentifierNormalize(haystack));
        }

        var phrase = Normalize(trimmedNeedle);
        return phrase.Length > 0 && TermRegex(phrase, identifierChars: false).IsMatch(Normalize(haystack));
    }

    /// <summary>Like <see cref="Normalize"/>, but keeps '_' as part of identifiers.</summary>
    private static string IdentifierNormalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var formD = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? char.ToLowerInvariant(c) : ' ');
        }

        return WhitespaceRegex().Replace(builder.ToString(), " ").Trim();
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string Needle, bool IdentifierChars), Regex> TermRegexCache = new();

    private static Regex TermRegex(string normalizedNeedle, bool identifierChars)
        => TermRegexCache.GetOrAdd((normalizedNeedle, identifierChars), key =>
        {
            var wordClass = key.IdentifierChars ? "[\\p{L}\\p{Nd}_]" : "[\\p{L}\\p{Nd}]";
            return new Regex(
                $"(?<!{wordClass}){Regex.Escape(key.Needle)}(?:e?s)?(?!{wordClass})",
                RegexOptions.CultureInvariant);
        });

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

    public static double Clamp01(double value)
    {
        if (double.IsNaN(value) || double.IsNegativeInfinity(value))
        {
            return 0;
        }

        return double.IsPositiveInfinity(value) ? 1 : Math.Min(1, Math.Max(0, value));
    }

    public static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\\[nrt]")]
    private static partial Regex LiteralEscapeRegex();
}
