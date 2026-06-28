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
        var trimmed = content.Trim();
        foreach (var candidate in JsonCandidates(trimmed))
        {
            try
            {
                var response = JsonSerializer.Deserialize<TruthAuditResponse>(candidate, Options);
                if (response is not null)
                {
                    response.TruthItems ??= [];
                    response.FalsePositiveAdmissions ??= [];
                    response.Corrections ??= [];
                    return response;
                }
            }
            catch (JsonException)
            {
                // Try next candidate.
            }
        }

        return new TruthAuditResponse { Summary = "Could not parse truth-audit JSON." };
    }

    private static IEnumerable<string> JsonCandidates(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            yield break;
        }

        yield return content;

        var fenceStart = content.IndexOf("```", StringComparison.Ordinal);
        while (fenceStart >= 0)
        {
            var lineEnd = content.IndexOf('\n', fenceStart);
            if (lineEnd < 0) yield break;
            var fenceEnd = content.IndexOf("```", lineEnd + 1, StringComparison.Ordinal);
            if (fenceEnd < 0) yield break;
            yield return content[(lineEnd + 1)..fenceEnd].Trim();
            fenceStart = content.IndexOf("```", fenceEnd + 3, StringComparison.Ordinal);
        }

        var objectStart = content.IndexOf('{');
        var objectEnd = content.LastIndexOf('}');
        if (objectStart >= 0 && objectEnd > objectStart)
        {
            yield return content[objectStart..(objectEnd + 1)];
        }
    }
}
