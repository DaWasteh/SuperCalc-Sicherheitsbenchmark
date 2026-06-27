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
    private const int MinimumRunawayListItems = 36;
    private const int MinimumRunawayListNumber = 30;
    private const int MinimumRepeatedTopicOccurrences = 3;
    private const int MinimumRepeatedTopicHeadings = 10;

    private static readonly HashSet<string> TopicStopWords = new(StringComparer.Ordinal)
    {
        "about", "across", "again", "against", "also", "because", "before", "being", "between", "could", "from", "have", "into", "more", "over", "same", "should", "than", "then", "their", "there", "these", "this", "through", "under", "were", "when", "where", "which", "while", "with", "without", "would",
        "assessment", "challenge", "classification", "comprehensive", "concern", "confidence", "coverage", "critical", "detailed", "documentation", "enhanced", "enhancement", "evidence", "evolution", "finding", "findings", "framework", "function", "hardening", "immediate", "impact", "improvement", "issue", "location", "mechanism", "methodical", "mitigation", "neutralization", "optimization", "potential", "precision", "prevention", "prioritization", "recommendation", "refinement", "remediation", "resilience", "review", "risk", "security", "severity", "strategy", "system", "systematic", "systemic", "targeted", "technical", "transformation", "trigger", "vulnerability"
    };

    private static readonly HashSet<string> FindingMetadataHeadings = new(StringComparer.Ordinal)
    {
        "confidence", "constraint checklist", "constraint checklist & confidence score", "cwe", "description", "double check line numbers", "evidence", "file", "fix", "function", "impact", "json structure", "key areas to check", "line", "line end", "line start", "line-by-line analysis", "lines", "location", "mental checklist", "mental sandbox", "recommendation", "schema", "severity", "source", "title", "trigger", "type"
    };

    private sealed record ListHeading(int? Number, string Title);

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
        AddRepeatedEnumeratedTopics(text, candidates);

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

    private static void AddRepeatedEnumeratedTopics(string text, List<RepetitionCandidate> candidates)
    {
        var headings = ExtractListHeadings(text);
        if (headings.Count == 0)
        {
            return;
        }

        var maxNumber = headings
            .Where(heading => heading.Number.HasValue)
            .Select(heading => heading.Number!.Value)
            .DefaultIfEmpty(0)
            .Max();

        var eligibleHeadings = SelectRunawayEligibleHeadings(headings, maxNumber);
        if (eligibleHeadings.Count == 0)
        {
            return;
        }

        var headingTopics = eligibleHeadings
            .Select(heading => ExtractTopicKeys(heading.Title).ToHashSet(StringComparer.Ordinal))
            .Where(topics => topics.Count > 0)
            .ToList();

        if (headingTopics.Count == 0)
        {
            return;
        }

        var topicCounts = headingTopics
            .SelectMany(topics => topics)
            .GroupBy(topic => topic, StringComparer.Ordinal)
            .Select(group => new { Topic = group.Key, Count = group.Count() })
            .Where(topic => topic.Count >= MinimumRepeatedTopicOccurrences)
            .OrderByDescending(topic => topic.Count)
            .ThenBy(topic => topic.Topic, StringComparer.Ordinal)
            .ToList();

        if (topicCounts.Count == 0)
        {
            return;
        }

        var repeatedTopicSet = topicCounts.Select(topic => topic.Topic).ToHashSet(StringComparer.Ordinal);
        var repeatedTopicHeadingCount = headingTopics.Count(topics => topics.Overlaps(repeatedTopicSet));
        if (repeatedTopicHeadingCount < MinimumRepeatedTopicHeadings)
        {
            return;
        }

        var snippet = string.Join(", ", topicCounts.Take(8).Select(topic => topic.Topic));
        candidates.Add(new RepetitionCandidate("runaway enumerated topic cycle", snippet, repeatedTopicHeadingCount));
    }

    private static IReadOnlyList<ListHeading> SelectRunawayEligibleHeadings(IReadOnlyList<ListHeading> headings, int maxNumber)
    {
        // Reasoning models often build several bounded numbered lists (checklist,
        // candidate findings, final selection, mental sandbox) and naturally repeat
        // the same vulnerability topics. That is progress, not a runaway list. Only
        // treat numbered headings as a runaway cycle when the numbering itself grows
        // past the threshold. Bullet-only loops still use the aggregate item count.
        if (maxNumber >= MinimumRunawayListNumber)
        {
            return headings.Where(heading => heading.Number.HasValue).ToList();
        }

        var bulletHeadings = headings.Where(heading => !heading.Number.HasValue).ToList();
        return bulletHeadings.Count >= MinimumRunawayListItems
            ? bulletHeadings
            : [];
    }

    private static List<ListHeading> ExtractListHeadings(string text)
    {
        var headings = new List<ListHeading>();
        var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var numberedMatch = NumberedHeading().Match(line);
            int? number = null;
            string rest;
            if (numberedMatch.Success)
            {
                rest = numberedMatch.Groups["rest"].Value;
                if (int.TryParse(numberedMatch.Groups["number"].Value, out var parsedNumber))
                {
                    number = parsedNumber;
                }
            }
            else
            {
                var bulletMatch = BulletHeading().Match(line);
                if (!bulletMatch.Success)
                {
                    continue;
                }

                rest = bulletMatch.Groups["rest"].Value;
            }

            var title = ExtractHeadingTitle(rest);
            if (title.Length >= 3 && !LooksLikeJsonOrSchemaLine(title) && !LooksLikeFindingMetadataHeading(title))
            {
                headings.Add(new ListHeading(number, title));
            }
        }

        return headings;
    }

    private static string ExtractHeadingTitle(string rest)
    {
        var value = rest.Trim();
        var boldMatch = BoldTitle().Match(value);
        if (boldMatch.Success)
        {
            return NormalizeWhitespace(boldMatch.Groups["title"].Value.Trim());
        }

        value = value.Trim('*', '`', '_', ' ', '\t');
        var separator = FindTitleSeparator(value);
        if (separator > 0)
        {
            value = value[..separator];
        }

        value = NormalizeWhitespace(value);
        return value.Length <= MaxSnippetLength ? value : value[..MaxSnippetLength];
    }

    private static int FindTitleSeparator(string value)
    {
        var candidates = new[]
        {
            value.IndexOf(':', StringComparison.Ordinal),
            value.IndexOf(" — ", StringComparison.Ordinal),
            value.IndexOf(" – ", StringComparison.Ordinal),
            value.IndexOf(" - ", StringComparison.Ordinal)
        };

        return candidates
            .Where(index => index > 0 && index <= 160)
            .DefaultIfEmpty(-1)
            .Min();
    }

    private static bool LooksLikeJsonOrSchemaLine(string title)
    {
        var trimmed = title.TrimStart();
        return trimmed.StartsWith('{')
            || trimmed.StartsWith('}')
            || trimmed.StartsWith('[')
            || trimmed.StartsWith(']')
            || trimmed.StartsWith('"')
            || trimmed.Contains("\":", StringComparison.Ordinal);
    }

    private static bool LooksLikeFindingMetadataHeading(string title)
    {
        var normalized = NormalizeWhitespace(title)
            .Trim('`', '*', '_', ' ', '\t')
            .ToLowerInvariant();

        return FindingMetadataHeadings.Contains(normalized)
            || normalized.StartsWith("line ", StringComparison.Ordinal)
            || normalized.StartsWith("lines ", StringComparison.Ordinal)
            || normalized.StartsWith("cwe-", StringComparison.Ordinal)
            || normalized.StartsWith("cwe ", StringComparison.Ordinal);
    }

    private static IEnumerable<string> ExtractTopicKeys(string title)
    {
        foreach (Match match in TopicTokenizer().Matches(title))
        {
            var topic = NormalizeTopicToken(match.Value);
            if (topic.Length >= 5 && !TopicStopWords.Contains(topic))
            {
                yield return topic;
            }
        }
    }

    private static string NormalizeTopicToken(string value)
    {
        var token = value.Trim('_', '-', '.', ':', '/', '\\').ToLowerInvariant();
        if (token.Any(char.IsDigit))
        {
            return string.Empty;
        }

        if (token.EndsWith("ies", StringComparison.Ordinal) && token.Length > 5)
        {
            token = token[..^3] + "y";
        }
        else if (token.EndsWith("sses", StringComparison.Ordinal) && token.Length > 6)
        {
            token = token[..^2];
        }
        else if (token.EndsWith('s') && token.Length > 5)
        {
            token = token[..^1];
        }

        return token;
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
            "runaway enumerated topic cycle" => candidate.Occurrences >= MinimumRepeatedTopicHeadings,
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

    [GeneratedRegex(@"^\s*(?<number>\d{1,4})[.)]\s+(?<rest>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex NumberedHeading();

    [GeneratedRegex(@"^\s*[-*+]\s+(?<rest>.+?)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex BulletHeading();

    [GeneratedRegex(@"^\*{1,2}(?<title>[^*]{3,160})\*{1,2}", RegexOptions.CultureInvariant)]
    private static partial Regex BoldTitle();

    [GeneratedRegex(@"\p{L}[\p{L}\p{N}_-]*", RegexOptions.CultureInvariant)]
    private static partial Regex TopicTokenizer();
}
