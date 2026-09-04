using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

public sealed partial class ResponseParser
{
    public const string LegacyParserVersion = "parser-v1";
    public const string ParserV2Version = "parser-v2";

    /// <summary>
    /// parser-v3 adds a lenient JSON repair pass (leading zeros, invalid escapes, raw control
    /// characters, unescaped inner quotes, missing commas) that only runs after strict parsing
    /// failed, accepts findings embedded in an echoed schema's <c>properties</c> object, and
    /// treats text before a stray <c>&lt;/think&gt;</c> as reasoning. Matching and scoring are
    /// unchanged; parser identity is versioned so older scorecards stay comparable history.
    /// </summary>
    public const string CurrentParserVersion = "parser-v3";

    /// <summary>Every parser identity that has ever been written into an archive, oldest first.</summary>
    public static readonly IReadOnlyList<string> KnownParserVersions = [LegacyParserVersion, ParserV2Version, CurrentParserVersion];

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public ParseResult Parse(string assistantContent)
    {
        assistantContent ??= string.Empty;
        var trimmed = assistantContent.Trim().Trim('\uFEFF');

        var direct = EvaluateJsonCandidate(trimmed, start: 0, fromFence: false, parseMode: "json");
        if (direct is not null)
        {
            return ToParseResult(assistantContent, direct);
        }

        var fencedCandidates = ExtractFencedJsonCandidates(trimmed);
        var completeCandidates = new List<JsonCandidate>(fencedCandidates);
        completeCandidates.AddRange(ExtractBalancedJsonCandidates(trimmed, fencedCandidates));
        var bestComplete = SelectBestCandidate(completeCandidates);
        if (bestComplete is { Quality: JsonCandidateQuality.Findings })
        {
            return ToParseResult(assistantContent, bestComplete);
        }

        // An incidental empty [] in surrounding prose is weaker evidence than complete
        // finding objects recoverable from an otherwise truncated/extra-braced response.
        if (TryParsePartialFindingsArray(trimmed, out var partialFindings, out var partialWarning, out var partialRepairs))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = Reindex(partialFindings),
                ParsedJson = true,
                UsedMarkdownJsonBlock = trimmed.Contains("```", StringComparison.Ordinal),
                ParseMode = "partial_json",
                Warning = partialWarning,
                Repairs = partialRepairs.ToList()
            };
        }

        if (bestComplete is not null)
        {
            return ToParseResult(assistantContent, bestComplete);
        }

        var fallbackFindings = ParseTextFallback(trimmed);
        return new ParseResult
        {
            AssistantContent = assistantContent,
            Findings = Reindex(fallbackFindings),
            UsedTextFallback = true,
            ParseMode = fallbackFindings.Count == 0 ? "none" : "text_fallback",
            Warning = fallbackFindings.Count == 0
                ? "Could not parse JSON and text fallback found no findings."
                : "Could not parse JSON; used heuristic text fallback. Scores should be treated as low parse-confidence."
        };
    }

    private static ParsedJsonCandidate? EvaluateJsonCandidate(string json, int start, bool fromFence, string parseMode)
    {
        if (TryParseJsonFindings(json, out var findings, out var warning, out var validPayload, out var repairs))
        {
            var quality = validPayload
                ? findings.Count > 0 ? JsonCandidateQuality.Findings : JsonCandidateQuality.ValidFindingsPayload
                : JsonCandidateQuality.JsonWithoutFindings;
            return new ParsedJsonCandidate(start, fromFence, parseMode, findings, warning, quality, repairs);
        }

        return TryParseJsonWithoutFindings(json, out var jsonWarning, out var jsonRepairs)
            ? new ParsedJsonCandidate(start, fromFence, parseMode, [], jsonWarning, JsonCandidateQuality.JsonWithoutFindings, jsonRepairs)
            : null;
    }

    private static ParsedJsonCandidate? SelectBestCandidate(IEnumerable<JsonCandidate> candidates)
    {
        ParsedJsonCandidate? best = null;
        foreach (var candidate in candidates.OrderBy(candidate => candidate.Start))
        {
            var evaluated = EvaluateJsonCandidate(
                candidate.Json,
                candidate.Start,
                candidate.FromFence,
                candidate.FromFence ? "markdown_json" : "balanced_json");
            if (evaluated is null)
            {
                continue;
            }

            if (best is null
                || evaluated.Quality > best.Quality
                || evaluated.Quality == best.Quality && evaluated.Start >= best.Start)
            {
                best = evaluated;
            }
        }

        return best;
    }

    private static ParseResult ToParseResult(string assistantContent, ParsedJsonCandidate candidate) => new()
    {
        AssistantContent = assistantContent,
        Findings = Reindex(candidate.Findings),
        ParsedJson = true,
        UsedMarkdownJsonBlock = candidate.FromFence,
        ParseMode = candidate.ParseMode,
        Warning = candidate.Warning,
        Repairs = candidate.Repairs.ToList()
    };

    private enum JsonCandidateQuality
    {
        JsonWithoutFindings = 1,
        ValidFindingsPayload = 2,
        Findings = 3
    }

    private sealed record JsonCandidate(string Json, int Start, int End, bool FromFence);

    private sealed record ParsedJsonCandidate(
        int Start,
        bool FromFence,
        string ParseMode,
        List<LlmFinding> Findings,
        string? Warning,
        JsonCandidateQuality Quality,
        IReadOnlyList<string> Repairs);

    /// <summary>
    /// Parses strictly first. Only when strict parsing fails is the lenient repair pass applied,
    /// so valid JSON is never rewritten. Returns null when neither form parses.
    /// </summary>
    private static JsonDocument? TryParseDocument(string json, out IReadOnlyList<string> repairs)
    {
        repairs = [];
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var cleaned = RemoveTrailingCommas(json.Trim());
        try
        {
            return JsonDocument.Parse(cleaned, DocumentOptions);
        }
        catch (JsonException)
        {
            // Fall through to the repair pass below.
        }

        var repaired = LenientJsonRepair.Repair(cleaned);
        if (!repaired.Changed)
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(RemoveTrailingCommas(repaired.Json), DocumentOptions);
            repairs = repaired.Repairs;
            return document;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string DescribeRepairs(IReadOnlyList<string> repairs)
        => repairs.Count == 0 ? string.Empty : $"Lenient JSON repair applied ({string.Join(", ", repairs)}); the model emitted invalid JSON.";

    private static List<LlmFinding> Reindex(List<LlmFinding> findings)
    {
        for (var i = 0; i < findings.Count; i++)
        {
            findings[i].Index = i + 1;
        }

        return findings;
    }

    private static bool TryParseJsonFindings(
        string json,
        out List<LlmFinding> findings,
        out string? warning,
        out bool validPayload)
        => TryParseJsonFindings(json, out findings, out warning, out validPayload, out _);

    private static bool TryParseJsonFindings(
        string json,
        out List<LlmFinding> findings,
        out string? warning,
        out bool validPayload,
        out IReadOnlyList<string> repairs)
    {
        findings = [];
        warning = null;
        validPayload = false;
        repairs = [];

        using var document = TryParseDocument(json, out repairs);
        if (document is null)
        {
            repairs = [];
            return false;
        }

        var root = document.RootElement;
        if (LooksLikeSchemaEcho(root))
        {
            repairs = [];
            return false;
        }

        var warnings = new List<string>();
        if (repairs.Count > 0)
        {
            warnings.Add(DescribeRepairs(repairs));
        }

        if (HasSchemaMetadata(root))
        {
            warnings.Add("Response included JSON schema metadata alongside findings.");
        }

        if (TryGetFindingsElement(root, out var findingsElement, warnings))
        {
            validPayload = ParseFindingsElement(findingsElement, findings, warnings);
            if (!validPayload)
            {
                warnings.Add("The findings payload had an invalid shape and was ignored; expected an array or finding object.");
            }

            warning = FormatWarnings(warnings);
            return true;
        }

        if (root.ValueKind == JsonValueKind.Object && LooksLikeFindingObject(root))
        {
            findings.Add(ReadFinding(root));
            validPayload = true;
            warnings.Add("Parsed a single finding object without a top-level findings array.");
            warning = FormatWarnings(warnings);
            return true;
        }

        repairs = [];
        return false;
    }

    private static bool TryGetFindingsElement(JsonElement root, out JsonElement findingsElement, List<string> warnings)
    {
        findingsElement = default;

        if (root.ValueKind == JsonValueKind.Array)
        {
            findingsElement = root;
            warnings.Add("Parsed a top-level JSON array as findings.");
            return true;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "findings", "vulnerabilities", "issues", "security_findings", "securityFindings", "results" })
        {
            if (!TryGetProperty(root, name, out var candidate))
            {
                continue;
            }

            findingsElement = candidate;
            if (!string.Equals(name, "findings", StringComparison.Ordinal))
            {
                warnings.Add($"Parsed '{name}' as findings.");
            }

            return true;
        }

        // Some models wrap the actual answer in one extra object, e.g. {"result":{"findings":[...]}}.
        // Do not search arbitrary descendants; that would turn JSON schemas (properties.findings)
        // into fake findings. Only unwrap common response containers.
        foreach (var wrapper in new[] { "response", "result", "analysis", "answer", "output", "data" })
        {
            if (!TryGetProperty(root, wrapper, out var wrapped))
            {
                continue;
            }

            if (wrapped.ValueKind == JsonValueKind.Array)
            {
                findingsElement = wrapped;
                warnings.Add($"Unwrapped '{wrapper}' array before parsing findings.");
                return true;
            }

            if (wrapped.ValueKind == JsonValueKind.Object
                && !LooksLikeSchemaEcho(wrapped)
                && TryGetFindingsElement(wrapped, out findingsElement, warnings))
            {
                warnings.Add($"Unwrapped '{wrapper}' object before parsing findings.");
                return true;
            }
        }

        // Some models echo the schema skeleton and then put their real answer where the
        // schema declared it: {"$schema":..., "properties": {"findings": [ {...}, ... ]}}.
        // A pure schema echo keeps properties.findings as an object ({"type":"array"}), so
        // only a real array of finding objects is accepted here.
        if (TryGetEmbeddedPropertiesFindings(root, out findingsElement, out var embeddedName))
        {
            warnings.Add($"Parsed findings embedded in the echoed schema 'properties.{embeddedName}' array.");
            return true;
        }

        return false;
    }

    private static bool TryGetEmbeddedPropertiesFindings(JsonElement root, out JsonElement findingsElement, out string name)
    {
        findingsElement = default;
        name = string.Empty;
        if (root.ValueKind != JsonValueKind.Object
            || !TryGetProperty(root, "properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var candidateName in new[] { "findings", "vulnerabilities", "issues", "security_findings", "securityFindings", "results" })
        {
            if (TryGetProperty(properties, candidateName, out var candidate)
                && candidate.ValueKind == JsonValueKind.Array
                && candidate.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.Object && LooksLikeFindingObject(item)))
            {
                findingsElement = candidate;
                name = candidateName;
                return true;
            }
        }

        return false;
    }

    private static bool ParseFindingsElement(JsonElement findingsElement, List<LlmFinding> findings, List<string> warnings)
    {
        switch (findingsElement.ValueKind)
        {
            case JsonValueKind.Array:
                var rejected = 0;
                foreach (var item in findingsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && LooksLikeFindingObject(item))
                    {
                        findings.Add(ReadFinding(item));
                    }
                    else
                    {
                        rejected++;
                    }
                }

                if (rejected > 0)
                {
                    warnings.Add($"Ignored {rejected} malformed finding array element(s).");
                }

                return true;

            case JsonValueKind.Object when LooksLikeFindingObject(findingsElement):
                findings.Add(ReadFinding(findingsElement));
                warnings.Add("Parsed object-valued 'findings' as a single finding.");
                return true;

            case JsonValueKind.Object:
                var before = findings.Count;
                foreach (var property in findingsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Object && LooksLikeFindingObject(property.Value))
                    {
                        findings.Add(ReadFinding(property.Value));
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Array)
                    {
                        ParseFindingsElement(property.Value, findings, warnings);
                    }
                }

                if (findings.Count > before)
                {
                    warnings.Add("Parsed object-valued 'findings' map as multiple findings.");
                    return true;
                }

                return false;

            default:
                return false;
        }
    }

    private static LlmFinding ReadFinding(JsonElement item)
    {
        var (lineStart, lineEnd) = ReadLineRange(item);
        var finding = new LlmFinding
        {
            Title = ReadString(item, "title", "name", "finding", "issue", "description") ?? string.Empty,
            VulnerabilityType = ReadString(item, "vulnerability_type", "type", "category", "vulnerabilityType", "vulnerability", "weakness") ?? string.Empty,
            Cwe = ReadCwe(item),
            Severity = NormalizeSeverity(ReadString(item, "severity", "risk", "risk_rating", "riskRating") ?? "Unknown"),
            Confidence = ReadDouble(item, 0.75, "confidence", "probability", "likelihood"),
            ConfidenceOrigin = HasValidNumericProperty(item, "confidence", "probability", "likelihood") ? ConfidenceOrigin.Reported : ConfidenceOrigin.JsonDefault,
            File = ReadString(item, "file", "filename", "source_file", "sourceFile", "path") ?? string.Empty,
            LineStart = lineStart,
            LineEnd = lineEnd,
            FunctionOrSymbol = ReadString(item, "function_or_symbol", "functionOrSymbol", "function", "function_name", "functionName", "method", "symbol", "symbol_name", "location") ?? string.Empty,
            Evidence = ReadString(item, "evidence", "code", "snippet", "code_snippet", "codeSnippet", "quote", "proof", "details") ?? string.Empty,
            Impact = ReadString(item, "impact", "consequence", "consequences") ?? string.Empty,
            Trigger = ReadString(item, "trigger", "exploit", "attack", "attack_vector", "attackVector", "scenario") ?? string.Empty,
            Fix = ReadString(item, "fix", "recommendation", "mitigation", "remediation", "solution") ?? string.Empty,
            RawText = item.GetRawText()
        };

        if (finding.LineEnd == 0)
        {
            finding.LineEnd = finding.LineStart;
        }

        if (string.IsNullOrWhiteSpace(finding.VulnerabilityType))
        {
            finding.VulnerabilityType = finding.Title;
        }

        if (string.IsNullOrWhiteSpace(finding.Title))
        {
            finding.Title = finding.VulnerabilityType;
        }

        return finding;
    }

    private static bool LooksLikeFindingObject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || TryGetProperty(item, "properties", out _))
        {
            return false;
        }

        var hasIdentity = HasAnyProperty(item, "title", "name", "finding", "issue", "description", "vulnerability_type", "type", "category", "vulnerabilityType", "vulnerability", "weakness");
        var hasDetails = HasAnyProperty(item, "evidence", "code", "snippet", "quote", "file", "filename", "path", "line_start", "lineStart", "line", "lines", "location", "severity", "risk", "cwe");
        return hasIdentity && hasDetails;
    }

    private static bool HasAnyProperty(JsonElement item, params string[] names)
    {
        return names.Any(name => TryGetProperty(item, name, out _));
    }

    private static (int Start, int End) ReadLineRange(JsonElement item)
    {
        var start = ReadInt(item, 0, "line_start", "lineStart", "start_line", "startLine", "start_line_number", "startLineNumber", "line", "line_number", "lineNumber", "lineno");
        var end = ReadInt(item, 0, "line_end", "lineEnd", "end_line", "endLine", "end_line_number", "endLineNumber");

        foreach (var name in new[] { "line_range", "lineRange", "lines", "line", "line_number", "lineNumber", "location", "loc" })
        {
            var value = ReadString(item, name);
            if (TryParseLineRange(value, out var rangeStart, out var rangeEnd))
            {
                if (start == 0)
                {
                    start = rangeStart;
                }

                if (end == 0)
                {
                    end = rangeEnd;
                }

                break;
            }
        }

        return (start, end);
    }

    private static bool TryParseLineRange(string? value, out int start, out int end)
    {
        start = 0;
        end = 0;

        var match = LineRangeValueRegex().Match(value ?? string.Empty);
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out start))
        {
            return false;
        }

        if (!int.TryParse(match.Groups[2].Success ? match.Groups[2].Value : match.Groups[1].Value, out end))
        {
            end = start;
        }

        return true;
    }

    private static string? FormatWarnings(List<string> warnings)
    {
        var distinct = warnings.Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal).ToList();
        return distinct.Count == 0 ? null : string.Join(" ", distinct);
    }

    private static bool TryParsePartialFindingsArray(string text, out List<LlmFinding> findings, out string warning, out IReadOnlyList<string> repairs)
    {
        findings = [];
        warning = string.Empty;
        repairs = [];
        string? bestParseWarning = null;
        IReadOnlyList<string> bestRepairs = [];
        var bestPropertyIndex = -1;

        var propertyOccurrences = new List<int>();
        foreach (var name in new[] { "findings", "vulnerabilities", "issues", "security_findings", "securityFindings", "results" })
        {
            var needle = $"\"{name}\"";
            for (var searchStart = 0; searchStart < text.Length;)
            {
                var index = text.IndexOf(needle, searchStart, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                propertyOccurrences.Add(index);
                searchStart = index + needle.Length;
            }
        }

        foreach (var propertyIndex in propertyOccurrences.Distinct().OrderBy(index => index))
        {
            var closingQuote = text.IndexOf('"', propertyIndex + 1);
            if (closingQuote < 0)
            {
                continue;
            }

            var colon = closingQuote + 1;
            while (colon < text.Length && char.IsWhiteSpace(text[colon]))
            {
                colon++;
            }

            if (colon >= text.Length || text[colon] != ':')
            {
                continue;
            }

            var arrayStart = colon + 1;
            while (arrayStart < text.Length && char.IsWhiteSpace(text[arrayStart]))
            {
                arrayStart++;
            }

            // Do not jump from a schema property whose value is an object to an unrelated
            // later array. The '[' must be the direct value of this findings property.
            if (arrayStart >= text.Length || text[arrayStart] != '[')
            {
                continue;
            }

            var objectJson = ExtractCompleteObjectsFromArray(text, arrayStart);
            if (objectJson.Count == 0)
            {
                continue;
            }

            var salvagedJson = "{\"findings\":[" + string.Join(',', objectJson) + "]}";
            if (!TryParseJsonFindings(salvagedJson, out var candidate, out var parseWarning, out var validPayload, out var candidateRepairs)
                || !validPayload
                || candidate.Count == 0)
            {
                continue;
            }

            if (candidate.Count > findings.Count
                || candidate.Count == findings.Count && propertyIndex >= bestPropertyIndex)
            {
                findings = candidate;
                bestParseWarning = parseWarning;
                bestRepairs = candidateRepairs;
                bestPropertyIndex = propertyIndex;
            }
        }

        if (findings.Count == 0)
        {
            return false;
        }

        repairs = bestRepairs;
        warning = $"Response JSON was incomplete or had trailing non-JSON text; salvaged {findings.Count} complete finding object(s) from the findings array.";
        if (!string.IsNullOrWhiteSpace(bestParseWarning))
        {
            warning += " " + bestParseWarning;
        }

        return true;
    }

    private static List<string> ExtractCompleteObjectsFromArray(string text, int arrayStart)
    {
        var objects = new List<string>();
        var inString = false;
        var escaped = false;
        var objectDepth = 0;
        var objectStart = -1;

        for (var i = arrayStart + 1; i < text.Length; i++)
        {
            var c = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '{')
            {
                if (objectDepth == 0)
                {
                    objectStart = i;
                }

                objectDepth++;
                continue;
            }

            if (c == '}')
            {
                if (objectDepth <= 0)
                {
                    continue;
                }

                objectDepth--;
                if (objectDepth == 0 && objectStart >= 0)
                {
                    objects.Add(text[objectStart..(i + 1)]);
                    objectStart = -1;
                }

                continue;
            }

            if (c == ']' && objectDepth == 0)
            {
                break;
            }
        }

        return objects;
    }

    private static bool TryParseJsonWithoutFindings(string json, out string warning, out IReadOnlyList<string> repairs)
    {
        warning = string.Empty;
        repairs = [];

        using var document = TryParseDocument(json, out repairs);
        if (document is null || document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
        {
            repairs = [];
            return false;
        }

        warning = LooksLikeSchemaEcho(document.RootElement)
            ? "Response appears to echo the JSON schema instead of returning findings."
            : "Valid JSON response did not contain a findings array.";
        if (repairs.Count > 0)
        {
            warning = DescribeRepairs(repairs) + " " + warning;
        }

        return true;
    }

    private static bool LooksLikeSchemaEcho(JsonElement root)
    {
        return HasSchemaMetadata(root) && !HasTopLevelFindingsPayload(root);
    }

    private static bool HasSchemaMetadata(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object &&
               (TryGetProperty(root, "$schema", out _) || TryGetProperty(root, "properties", out _)) &&
               TryGetProperty(root, "title", out var title) &&
               title.ValueKind == JsonValueKind.String &&
               title.GetString()?.Contains("Findings Response", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static bool HasTopLevelFindingsPayload(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        foreach (var name in new[] { "findings", "vulnerabilities", "issues", "security_findings", "securityFindings", "results" })
        {
            if (TryGetProperty(root, name, out var candidate))
            {
                return candidate.ValueKind == JsonValueKind.Array || ContainsFindingObject(candidate);
            }
        }

        // A schema echo whose properties.findings is a real array of findings is an answer,
        // not an echo (see TryGetEmbeddedPropertiesFindings).
        return TryGetEmbeddedPropertiesFindings(root, out _, out _);
    }

    private static bool ContainsFindingObject(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Object when LooksLikeFindingObject(element) => true,
            JsonValueKind.Object => element.EnumerateObject().Any(property => ContainsFindingObject(property.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(ContainsFindingObject),
            _ => false
        };
    }

    private static bool HasProperty(JsonElement item, params string[] names) =>
        names.Any(name => TryGetProperty(item, name, out var value) && value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined);

    private static string? ReadString(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(item, name, out var property))
            {
                continue;
            }

            return property.ValueKind switch
            {
                JsonValueKind.String => property.GetString(),
                JsonValueKind.Number => property.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                JsonValueKind.Array => string.Join(", ", property.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())),
                JsonValueKind.Object => property.GetRawText(),
                _ => null
            };
        }

        return null;
    }

    private static string ReadCwe(JsonElement item)
    {
        JsonElement cwe = default;
        var found = false;
        foreach (var name in new[] { "cwe", "cwe_id", "cweId", "cwes" })
        {
            if (TryGetProperty(item, name, out cwe))
            {
                found = true;
                break;
            }
        }

        if (!found)
        {
            return string.Empty;
        }

        if (cwe.ValueKind == JsonValueKind.String)
        {
            return cwe.GetString() ?? string.Empty;
        }

        if (cwe.ValueKind == JsonValueKind.Array)
        {
            return string.Join(", ", cwe.EnumerateArray().Select(element => element.ValueKind == JsonValueKind.String ? element.GetString() : element.GetRawText()));
        }

        if (cwe.ValueKind == JsonValueKind.Object)
        {
            return ReadString(cwe, "id", "cwe", "name") ?? cwe.GetRawText();
        }

        return cwe.GetRawText();
    }

    private static int ReadInt(JsonElement item, int defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(item, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
            {
                return number;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var value = property.GetString();
                if (int.TryParse(value, out number))
                {
                    return number;
                }

                var match = LineNumberRegex().Match(value ?? string.Empty);
                if (match.Success && int.TryParse(match.Groups[1].Value, out number))
                {
                    return number;
                }
            }
        }

        return defaultValue;
    }

    private static bool HasValidNumericProperty(JsonElement item, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(item, name, out var property) && TryReadFiniteDouble(property, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static double ReadDouble(JsonElement item, double defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (TryGetProperty(item, name, out var property) && TryReadFiniteDouble(property, out var number))
            {
                return TextUtil.Clamp01(number);
            }
        }

        return defaultValue;
    }

    private static bool TryReadFiniteDouble(JsonElement property, out double number)
    {
        number = 0;
        if (property.ValueKind == JsonValueKind.Number)
        {
            return property.TryGetDouble(out number) && double.IsFinite(number);
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = (property.GetString() ?? string.Empty).Trim();
        var isPercent = text.EndsWith('%');
        if (isPercent)
        {
            text = text.TrimEnd('%').Trim();
        }

        if (!text.Contains('.') && text.Count(character => character == ',') == 1)
        {
            text = text.Replace(',', '.');
        }

        if (!double.TryParse(
                text,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out number))
        {
            return false;
        }

        if (isPercent)
        {
            number /= 100.0;
        }

        return double.IsFinite(number);
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement property)
    {
        if (element.TryGetProperty(name, out property))
        {
            return true;
        }

        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static string NormalizeSeverity(string severity)
    {
        var normalized = severity.Trim().ToLowerInvariant();
        return normalized switch
        {
            "critical" or "crit" => "Critical",
            "high" => "High",
            "medium" or "med" or "moderate" => "Medium",
            "low" => "Low",
            "info" or "informational" => "Informational",
            _ => string.IsNullOrWhiteSpace(severity) ? "Unknown" : severity.Trim()
        };
    }

    private static List<JsonCandidate> ExtractFencedJsonCandidates(string text)
    {
        return AnyFenceRegex().Matches(text)
            .Select(match => new JsonCandidate(
                match.Groups[1].Value,
                match.Index,
                match.Index + match.Length,
                FromFence: true))
            .ToList();
    }

    private static List<JsonCandidate> ExtractBalancedJsonCandidates(
        string text,
        IReadOnlyList<JsonCandidate> fencedCandidates)
    {
        var candidates = new List<JsonCandidate>();
        var cursor = 0;
        while (cursor < text.Length)
        {
            var objectStart = text.IndexOf('{', cursor);
            var arrayStart = text.IndexOf('[', cursor);
            var start = objectStart < 0
                ? arrayStart
                : arrayStart < 0 ? objectStart : Math.Min(objectStart, arrayStart);
            if (start < 0)
            {
                break;
            }

            if (!TryExtractBalancedJson(text, start, out var end))
            {
                // Recover nested/later self-contained answers in one linear pass. This
                // avoids both abandoning the response and O(n²) retries on brace floods.
                candidates.AddRange(ExtractRecoveryCandidates(text, start + 1, fencedCandidates));
                break;
            }

            var json = text[start..end];
            var evaluated = EvaluateJsonCandidate(json, start, fromFence: false, parseMode: "balanced_json");
            var insideFence = fencedCandidates.Any(fence => start >= fence.Start && start < fence.End);
            if (!insideFence)
            {
                candidates.Add(new JsonCandidate(json, start, end, FromFence: false));
            }

            if (evaluated is null)
            {
                // Balanced prose can surround later valid JSON. Its nested recovery is
                // filtered to real findings containers, never single finding fragments.
                candidates.AddRange(ExtractRecoveryCandidates(text, start + 1, fencedCandidates));
                break;
            }

            cursor = end;
        }

        return candidates;
    }

    private static IEnumerable<JsonCandidate> ExtractRecoveryCandidates(
        string text,
        int start,
        IReadOnlyList<JsonCandidate> fencedCandidates)
    {
        var openings = new Stack<(char Closing, int Start)>();
        var inString = false;
        var escaped = false;
        for (var index = Math.Max(0, start); index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character is '{' or '[')
            {
                openings.Push((character == '{' ? '}' : ']', index));
                continue;
            }

            if (character is not ('}' or ']') || openings.Count == 0)
            {
                continue;
            }

            var opening = openings.Pop();
            if (opening.Closing != character)
            {
                openings.Clear();
                continue;
            }

            var end = index + 1;
            if (!CouldBeRecoveryFindingsPayload(text, opening.Start, end))
            {
                continue;
            }

            var json = text[opening.Start..end];
            var evaluated = EvaluateJsonCandidate(json, opening.Start, fromFence: false, parseMode: "balanced_json");
            var isEligibleContainer = evaluated is { Quality: JsonCandidateQuality.Findings }
                                      && !IsSingleFindingObjectCandidate(json)
                                      || IsExplicitFindingsWrapperCandidate(json);
            var insideFence = fencedCandidates.Any(fence => opening.Start >= fence.Start && opening.Start < fence.End);
            if (isEligibleContainer && !insideFence)
            {
                yield return new JsonCandidate(json, opening.Start, end, FromFence: false);
            }
        }
    }

    private static bool CouldBeRecoveryFindingsPayload(string text, int start, int end)
    {
        var contentStart = start + 1;
        while (contentStart < end && char.IsWhiteSpace(text[contentStart]))
        {
            contentStart++;
        }

        var length = Math.Min(2048, Math.Max(0, end - contentStart));
        if (length == 0)
        {
            return false;
        }

        var prefix = text.AsSpan(contentStart, length);
        var firstQuote = prefix.IndexOf('"');
        if (firstQuote < 0)
        {
            return false;
        }

        prefix = prefix[firstQuote..];
        return prefix.Contains("\"findings\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"vulnerabilities\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"issues\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"results\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"title\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"vulnerability_type\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"vulnerabilityType\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"name\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"summary\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"description\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"severity\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"cwe\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"evidence\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"impact\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"file\"", StringComparison.OrdinalIgnoreCase)
               || prefix.Contains("\"symbol\"", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSingleFindingObjectCandidate(string json)
    {
        using var document = TryParseDocument(json, out _);
        return document is not null
               && document.RootElement.ValueKind == JsonValueKind.Object
               && LooksLikeFindingObject(document.RootElement)
               && !TryGetFindingsElement(document.RootElement, out _, []);
    }

    private static bool IsExplicitFindingsWrapperCandidate(string json)
    {
        using var document = TryParseDocument(json, out _);
        return document is not null
               && document.RootElement.ValueKind == JsonValueKind.Object
               && TryGetFindingsElement(document.RootElement, out _, []);
    }

    private static bool TryExtractBalancedJson(string text, int start, out int end)
    {
        end = start;
        if (start < 0 || start >= text.Length || text[start] is not ('{' or '['))
        {
            return false;
        }

        var expectedClosings = new Stack<char>();
        var inString = false;
        var escaped = false;
        for (var index = start; index < text.Length; index++)
        {
            var character = text[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (character == '"')
            {
                inString = true;
                continue;
            }

            if (character is '{' or '[')
            {
                expectedClosings.Push(character == '{' ? '}' : ']');
                continue;
            }

            if (character is not ('}' or ']'))
            {
                continue;
            }

            if (expectedClosings.Count == 0 || expectedClosings.Pop() != character)
            {
                return false;
            }

            if (expectedClosings.Count == 0)
            {
                end = index + 1;
                return true;
            }
        }

        return false;
    }

    private static List<LlmFinding> ParseTextFallback(string text)
    {
        var findings = new List<LlmFinding>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return findings;
        }

        var sections = SplitIntoFindingSections(text);
        foreach (var section in sections)
        {
            var title = FirstNonEmptyLine(section);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var severity = SeverityRegex().Match(section);
            var cwe = CweRegex().Match(section);
            var lines = LineRangeRegex().Match(section);
            var lineStart = 0;
            var lineEnd = 0;
            if (lines.Success)
            {
                int.TryParse(lines.Groups[1].Value, out lineStart);
                if (!int.TryParse(lines.Groups[2].Success ? lines.Groups[2].Value : lines.Groups[1].Value, out lineEnd))
                {
                    lineEnd = lineStart;
                }
            }

            var evidence = ExtractEvidence(section);
            findings.Add(new LlmFinding
            {
                Title = CleanupHeading(title),
                VulnerabilityType = CleanupHeading(title),
                Cwe = cwe.Success ? cwe.Value.ToUpperInvariant() : string.Empty,
                Severity = severity.Success ? NormalizeSeverity(severity.Groups[1].Value) : "Unknown",
                Confidence = 0.55,
                ConfidenceOrigin = ConfidenceOrigin.TextFallbackDefault,
                File = section.Contains("enhanced_calc.cpp", StringComparison.OrdinalIgnoreCase) ? "enhanced_calc.cpp" : string.Empty,
                LineStart = lineStart,
                LineEnd = lineEnd,
                FunctionOrSymbol = ExtractFunction(section),
                Evidence = evidence,
                Impact = ExtractField(section, "impact") ?? string.Empty,
                Trigger = ExtractField(section, "trigger") ?? string.Empty,
                Fix = ExtractField(section, "fix") ?? ExtractField(section, "mitigation") ?? string.Empty,
                RawText = section.Trim()
            });
        }

        return findings;
    }

    private static List<string> SplitIntoFindingSections(string text)
    {
        var matches = SectionStartRegex().Matches(text);
        if (matches.Count == 0)
        {
            return text.Contains("CWE-", StringComparison.OrdinalIgnoreCase) || text.Contains("vulnerab", StringComparison.OrdinalIgnoreCase)
                ? [text]
                : [];
        }

        var sections = new List<string>();
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index;
            var end = i + 1 < matches.Count ? matches[i + 1].Index : text.Length;
            sections.Add(text[start..end]);
        }

        return sections;
    }

    private static string FirstNonEmptyLine(string text)
    {
        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;
    }

    private static string CleanupHeading(string value)
    {
        return HeadingPrefixRegex().Replace(value.Trim(), string.Empty).Trim(' ', ':', '-', '#', '*');
    }

    private static string ExtractEvidence(string section)
    {
        var backticks = BacktickRegex().Matches(section).Select(m => m.Groups[1].Value).Where(s => s.Length > 0).Take(5).ToList();
        if (backticks.Count > 0)
        {
            return string.Join(" | ", backticks);
        }

        return ExtractField(section, "evidence") ?? ExtractField(section, "code") ?? string.Empty;
    }

    private static string ExtractFunction(string section)
    {
        var field = ExtractField(section, "function") ?? ExtractField(section, "symbol") ?? ExtractField(section, "location");
        if (!string.IsNullOrWhiteSpace(field))
        {
            return field;
        }

        var match = FunctionLikeRegex().Match(section);
        return match.Success ? match.Value : string.Empty;
    }

    private static string? ExtractField(string section, string field)
    {
        var regex = new Regex($"(?im)^\\s*[-*]?\\s*{Regex.Escape(field)}\\s*[:=-]\\s*(.+)$");
        var match = regex.Match(section);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string RemoveTrailingCommas(string json)
    {
        return TrailingCommaRegex().Replace(json, "$1");
    }

    [GeneratedRegex("```\\w*\\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex AnyFenceRegex();

    [GeneratedRegex("(\\d+)")]
    private static partial Regex LineNumberRegex();

    [GeneratedRegex("(?i)\\b(Critical|High|Medium|Moderate|Low|Informational|Info)\\b")]
    private static partial Regex SeverityRegex();

    [GeneratedRegex("(?i)CWE-\\d+")]
    private static partial Regex CweRegex();

    [GeneratedRegex("(?i)(?:line|lines|linenumber|line_start|at)\\D+(\\d+)(?:\\D+(\\d+))?")]
    private static partial Regex LineRangeRegex();

    [GeneratedRegex(@"(?<!\d)(\d+)(?:\s*(?:-|–|—|to|through|bis|\.\.)\s*(\d+))?")]
    private static partial Regex LineRangeValueRegex();

    [GeneratedRegex("(?m)^(?:\\s{0,3}#{1,4}\\s+|\\s*\\d+[.)]\\s+|\\s*[-*]\\s+(?:Finding|Vulnerability)\\b)")]
    private static partial Regex SectionStartRegex();

    [GeneratedRegex("^(?:#{1,6}\\s*|\\d+[.)]\\s*|[-*]\\s*)")]
    private static partial Regex HeadingPrefixRegex();

    [GeneratedRegex("`([^`]+)`")]
    private static partial Regex BacktickRegex();

    [GeneratedRegex("[A-Za-z_][A-Za-z0-9_:]*\\s*\\(")]
    private static partial Regex FunctionLikeRegex();

    [GeneratedRegex(",\\s*([}\\]])")]
    private static partial Regex TrailingCommaRegex();
}
