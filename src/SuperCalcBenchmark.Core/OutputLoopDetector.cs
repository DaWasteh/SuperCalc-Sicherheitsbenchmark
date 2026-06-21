using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

public sealed record RepetitionCandidate(string Kind, string Snippet, int Occurrences);

public sealed record OutputLoopDiagnostics(
    bool HasSuspectedLoop,
    string Summary,
    IReadOnlyList<RepetitionCandidate> Repetitions);

public static partial class OutputLoopDetector
{
    private const int MaxSnippetLength = 260;
    private const int MinimumRepeatedLineLength = 120;
    private const int MinimumRepeatedParagraphLength = 100;
    private const int ShingleSize = 24;
    private const int ShingleStride = 8;

    public static OutputLoopDiagnostics Analyze(string? text, int maxCandidates = 5)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new OutputLoopDiagnostics(false, "empty output", Array.Empty<RepetitionCandidate>());
        }

        var candidates = new List<RepetitionCandidate>();
        AddRepeatedLines(text, candidates);
        AddRepeatedParagraphs(text, candidates);
        AddRepeatedWordShingles(text, candidates);

        var ordered = candidates
            .GroupBy(candidate => $"{candidate.Kind}\n{candidate.Snippet}", StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(candidate => candidate.Occurrences).First())
            .OrderByDescending(candidate => CandidateWeight(candidate))
            .ThenBy(candidate => candidate.Kind, StringComparer.Ordinal)
            .Take(Math.Max(1, maxCandidates))
            .ToList();

        var suspected = ordered.Any(IsStrongCandidate)
            || (text.Length >= 20_000 && ordered.Any(candidate => candidate.Occurrences >= 3 && candidate.Snippet.Length >= 100));

        var summary = BuildSummary(text, ordered, suspected);
        return new OutputLoopDiagnostics(suspected, summary, ordered);
    }

    private static void AddRepeatedLines(string text, List<RepetitionCandidate> candidates)
    {
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(NormalizeWhitespace)
            .Where(line => line.Length >= MinimumRepeatedLineLength);

        AddGroupedCandidates(lines, "repeated line", minimumOccurrences: 4, candidates);
    }

    private static void AddRepeatedParagraphs(string text, List<RepetitionCandidate> candidates)
    {
        var paragraphs = ParagraphSplitter().Split(text)
            .Select(NormalizeWhitespace)
            .Where(paragraph => paragraph.Length >= MinimumRepeatedParagraphLength);

        AddGroupedCandidates(paragraphs, "repeated paragraph", minimumOccurrences: 3, candidates);
    }

    private static void AddRepeatedWordShingles(string text, List<RepetitionCandidate> candidates)
    {
        var tokens = Tokenizer().Matches(text)
            .Select(match => match.Value.ToLowerInvariant())
            .ToList();

        if (tokens.Count < ShingleSize * 2)
        {
            return;
        }

        var shingles = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index <= tokens.Count - ShingleSize; index += ShingleStride)
        {
            var shingle = string.Join(' ', tokens.Skip(index).Take(ShingleSize));
            shingles[shingle] = shingles.TryGetValue(shingle, out var count) ? count + 1 : 1;
        }

        foreach (var group in shingles.Where(pair => pair.Value >= 6))
        {
            candidates.Add(new RepetitionCandidate("repeated 24-token span", Truncate(group.Key), group.Value));
        }
    }

    private static void AddGroupedCandidates(
        IEnumerable<string> snippets,
        string kind,
        int minimumOccurrences,
        List<RepetitionCandidate> candidates)
    {
        foreach (var group in snippets.GroupBy(snippet => snippet, StringComparer.Ordinal).Where(group => group.Count() >= minimumOccurrences))
        {
            candidates.Add(new RepetitionCandidate(kind, Truncate(group.Key), group.Count()));
        }
    }

    private static string BuildSummary(string text, IReadOnlyList<RepetitionCandidate> ordered, bool suspected)
    {
        var textInfo = $"{text.Length:N0} chars";
        if (ordered.Count == 0)
        {
            return $"No repeated loop-sized segments found ({textInfo}).";
        }

        var top = ordered[0];
        var prefix = suspected ? "Suspected loop" : "Repetition observed, below loop threshold";
        return $"{prefix}: {top.Kind} x{top.Occurrences} ({textInfo}).";
    }

    private static bool IsStrongCandidate(RepetitionCandidate candidate)
    {
        return candidate.Kind switch
        {
            "repeated line" => candidate.Occurrences >= 4 && candidate.Snippet.Length >= 120,
            "repeated paragraph" => candidate.Occurrences >= 3 && candidate.Snippet.Length >= 140,
            "repeated 24-token span" => candidate.Occurrences >= 6 && candidate.Snippet.Length >= 140,
            _ => candidate.Occurrences >= 6 && candidate.Snippet.Length >= 140
        };
    }

    private static int CandidateWeight(RepetitionCandidate candidate)
    {
        return candidate.Occurrences * Math.Min(candidate.Snippet.Length, MaxSnippetLength);
    }

    private static string NormalizeWhitespace(string value)
    {
        return WhitespaceNormalizer().Replace(value.Trim(), " ");
    }

    private static string Truncate(string value)
    {
        var normalized = NormalizeWhitespace(value);
        return normalized.Length <= MaxSnippetLength
            ? normalized
            : normalized[..MaxSnippetLength] + "…";
    }

    [GeneratedRegex(@"(?:\r?\n\s*){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex ParagraphSplitter();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceNormalizer();

    [GeneratedRegex(@"[\p{L}\p{N}_#.:/\\-]+|[{}\[\](),;:=+*<>-]", RegexOptions.CultureInvariant)]
    private static partial Regex Tokenizer();
}
