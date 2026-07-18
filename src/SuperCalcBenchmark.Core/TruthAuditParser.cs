using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class TruthAuditParser
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public TruthAuditResponse Parse(string content)
    {
        content ??= string.Empty;
        Candidate? best = null;
        var ordinal = 0;
        foreach (var json in JsonCandidates(content.Trim()).Distinct(StringComparer.Ordinal))
        {
            try
            {
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;

                var arraysPresent = HasArray(document.RootElement, "truth_items")
                    && HasArray(document.RootElement, "false_positive_admissions")
                    && HasArray(document.RootElement, "corrections");
                var response = JsonSerializer.Deserialize<TruthAuditResponse>(json, Options);
                if (response is null) continue;
                var normalizedRun = AuditedRunNames.Normalize(response.AuditedRun);
                // A schema/example echo commonly deserializes because unknown schema keywords are
                // ignored. It is not a response unless it has response arrays or a real run value.
                if (!arraysPresent && normalizedRun is null) continue;

                response.ParseSucceeded = true;
                response.RequiredArraysPresent = arraysPresent;
                response.TruthItems ??= [];
                response.FalsePositiveAdmissions ??= [];
                response.Corrections ??= [];

                if (normalizedRun is not null) response.AuditedRun = normalizedRun;
                var score = arraysPresent ? (normalizedRun is null ? 2 : 3) : 1;
                var candidate = new Candidate(response, score, ordinal++);
                if (best is null || candidate.Score > best.Score ||
                    (candidate.Score == best.Score && candidate.Ordinal > best.Ordinal))
                    best = candidate;
            }
            catch (JsonException)
            {
                // Try every other balanced object/fenced/direct candidate.
            }
        }

        return best?.Response ?? new TruthAuditResponse
        {
            Summary = "Could not parse truth-audit JSON.",
            ParseSucceeded = false,
            RequiredArraysPresent = false
        };
    }

    private static bool HasArray(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return property.Value.ValueKind == JsonValueKind.Array;
        return false;
    }

    private static IEnumerable<string> JsonCandidates(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) yield break;

        // Retains tolerant direct JSON and markdown handling. The balanced scanner below also
        // finds sequential objects, objects inside fences, and valid answers after prose/schema.
        yield return content;
        foreach (var candidate in BalancedObjects(content)) yield return candidate;
    }

    private static IEnumerable<string> BalancedObjects(string content)
    {
        var starts = new Stack<int>();
        var inString = false;
        var escaped = false;
        for (var i = 0; i < content.Length; i++)
        {
            var ch = content[i];
            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') { inString = true; continue; }
            if (ch == '{') starts.Push(i);
            else if (ch == '}' && starts.Count > 0)
            {
                var start = starts.Pop();
                yield return content[start..(i + 1)];
            }
        }
    }

    private sealed record Candidate(TruthAuditResponse Response, int Score, int Ordinal);
}
