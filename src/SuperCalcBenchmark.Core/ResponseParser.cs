using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

public sealed partial class ResponseParser
{
    public ParseResult Parse(string assistantContent)
    {
        assistantContent ??= string.Empty;
        var trimmed = assistantContent.Trim().Trim('\uFEFF');

        if (TryParseJsonFindings(trimmed, out var directFindings, out var directWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = Reindex(directFindings),
                ParsedJson = true,
                Warning = directWarning
            };
        }

        if (TryParseJsonWithoutFindings(trimmed, out var directJsonWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = [],
                ParsedJson = true,
                Warning = directJsonWarning
            };
        }

        var fencedJson = ExtractFencedJson(trimmed);
        if (!string.IsNullOrWhiteSpace(fencedJson) && TryParseJsonFindings(fencedJson, out var fencedFindings, out var fencedWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = Reindex(fencedFindings),
                ParsedJson = true,
                UsedMarkdownJsonBlock = true,
                Warning = fencedWarning
            };
        }

        if (!string.IsNullOrWhiteSpace(fencedJson) && TryParseJsonWithoutFindings(fencedJson, out var fencedJsonWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = [],
                ParsedJson = true,
                UsedMarkdownJsonBlock = true,
                Warning = fencedJsonWarning
            };
        }

        var balanced = ExtractBalancedJson(trimmed);
        if (!string.IsNullOrWhiteSpace(balanced) && TryParseJsonFindings(balanced, out var balancedFindings, out var balancedWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = Reindex(balancedFindings),
                ParsedJson = true,
                UsedMarkdownJsonBlock = trimmed.Contains("```", StringComparison.Ordinal),
                Warning = balancedWarning
            };
        }

        if (!string.IsNullOrWhiteSpace(balanced) && TryParseJsonWithoutFindings(balanced, out var balancedJsonWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = [],
                ParsedJson = true,
                UsedMarkdownJsonBlock = trimmed.Contains("```", StringComparison.Ordinal),
                Warning = balancedJsonWarning
            };
        }

        if (TryParsePartialFindingsArray(trimmed, out var partialFindings, out var partialWarning))
        {
            return new ParseResult
            {
                AssistantContent = assistantContent,
                Findings = Reindex(partialFindings),
                ParsedJson = true,
                UsedMarkdownJsonBlock = trimmed.Contains("```", StringComparison.Ordinal),
                Warning = partialWarning
            };
        }

        var fallbackFindings = ParseTextFallback(trimmed);
        return new ParseResult
        {
            AssistantContent = assistantContent,
            Findings = Reindex(fallbackFindings),
            UsedTextFallback = true,
            Warning = fallbackFindings.Count == 0
                ? "Could not parse JSON and text fallback found no findings."
                : "Could not parse JSON; used heuristic text fallback. Scores should be treated as low parse-confidence."
        };
    }

    private static List<LlmFinding> Reindex(List<LlmFinding> findings)
    {
        for (var i = 0; i < findings.Count; i++)
        {
            findings[i].Index = i + 1;
        }

        return findings;
    }

    private static bool TryParseJsonFindings(string json, out List<LlmFinding> findings, out string? warning)
    {
        findings = [];
        warning = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        var cleaned = RemoveTrailingCommas(json.Trim());

        try
        {
            using var document = JsonDocument.Parse(cleaned, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            var root = document.RootElement;
            if (LooksLikeSchemaEcho(root))
            {
                return false;
            }

            var warnings = new List<string>();
            if (TryGetFindingsElement(root, out var findingsElement, warnings))
            {
                ParseFindingsElement(findingsElement, findings, warnings);
                warning = FormatWarnings(warnings);
                return true;
            }

            if (root.ValueKind == JsonValueKind.Object && LooksLikeFindingObject(root))
            {
                findings.Add(ReadFinding(root));
                warnings.Add("Parsed a single finding object without a top-level findings array.");
                warning = FormatWarnings(warnings);
                return true;
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
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

        return false;
    }

    private static void ParseFindingsElement(JsonElement findingsElement, List<LlmFinding> findings, List<string> warnings)
    {
        switch (findingsElement.ValueKind)
        {
            case JsonValueKind.Array:
                foreach (var item in findingsElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object && LooksLikeFindingObject(item))
                    {
                        findings.Add(ReadFinding(item));
                    }
                }

                break;

            case JsonValueKind.Object when LooksLikeFindingObject(findingsElement):
                findings.Add(ReadFinding(findingsElement));
                warnings.Add("Parsed object-valued 'findings' as a single finding.");
                break;

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
                }

                break;
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

    private static bool TryParsePartialFindingsArray(string text, out List<LlmFinding> findings, out string warning)
    {
        findings = [];
        warning = string.Empty;

        var findingsProperty = -1;
        foreach (var name in new[] { "findings", "vulnerabilities", "issues", "security_findings", "securityFindings", "results" })
        {
            findingsProperty = text.IndexOf($"\"{name}\"", StringComparison.OrdinalIgnoreCase);
            if (findingsProperty >= 0)
            {
                break;
            }
        }

        if (findingsProperty < 0)
        {
            return false;
        }

        var arrayStart = text.IndexOf('[', findingsProperty);
        if (arrayStart < 0)
        {
            return false;
        }

        var objectJson = ExtractCompleteObjectsFromArray(text, arrayStart);
        if (objectJson.Count == 0)
        {
            return false;
        }

        var salvagedJson = "{\"findings\":[" + string.Join(',', objectJson) + "]}";
        if (!TryParseJsonFindings(salvagedJson, out findings, out var parseWarning) || findings.Count == 0)
        {
            return false;
        }

        warning = $"Response JSON was incomplete or had trailing non-JSON text; salvaged {findings.Count} complete finding object(s) from the findings array.";
        if (!string.IsNullOrWhiteSpace(parseWarning))
        {
            warning += " " + parseWarning;
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

    private static bool TryParseJsonWithoutFindings(string json, out string warning)
    {
        warning = string.Empty;

        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(RemoveTrailingCommas(json.Trim()), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });

            if (document.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            {
                return false;
            }

            warning = LooksLikeSchemaEcho(document.RootElement)
                ? "Response appears to echo the JSON schema instead of returning findings."
                : "Valid JSON response did not contain a findings array.";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeSchemaEcho(JsonElement root)
    {
        return root.ValueKind == JsonValueKind.Object &&
               (TryGetProperty(root, "$schema", out _) || TryGetProperty(root, "properties", out _)) &&
               TryGetProperty(root, "title", out var title) &&
               title.ValueKind == JsonValueKind.String &&
               title.GetString()?.Contains("Findings Response", StringComparison.OrdinalIgnoreCase) == true;
    }

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

    private static double ReadDouble(JsonElement item, double defaultValue, params string[] names)
    {
        foreach (var name in names)
        {
            if (!TryGetProperty(item, name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
            {
                return TextUtil.Clamp01(number);
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                var text = (property.GetString() ?? string.Empty).Trim();
                var isPercent = text.EndsWith('%');
                if (isPercent)
                {
                    text = text.TrimEnd('%').Trim();
                }

                if (!text.Contains('.') && text.Count(c => c == ',') == 1)
                {
                    text = text.Replace(',', '.');
                }

                if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out number))
                {
                    return TextUtil.Clamp01(isPercent ? number / 100.0 : number);
                }
            }
        }

        return defaultValue;
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

    private static string? ExtractFencedJson(string text)
    {
        var match = FencedJsonRegex().Match(text);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        match = AnyFenceRegex().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static string? ExtractBalancedJson(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0)
        {
            start = text.IndexOf('[');
        }

        if (start < 0)
        {
            return null;
        }

        var opening = text[start];
        var closing = opening == '{' ? '}' : ']';
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
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

            if (c == opening)
            {
                depth++;
            }
            else if (c == closing)
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        return null;
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

    [GeneratedRegex("```(?:json|JSON)\\s*(.*?)```", RegexOptions.Singleline)]
    private static partial Regex FencedJsonRegex();

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
