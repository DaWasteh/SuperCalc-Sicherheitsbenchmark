using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class GroundTruthStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public GroundTruthDocument Load(string path)
    {
        using var stream = File.OpenRead(path);
        var document = JsonSerializer.Deserialize<GroundTruthDocument>(stream, JsonOptions)
            ?? throw new InvalidOperationException($"Ground truth file '{path}' is empty or invalid.");

        if (document.Vulnerabilities.Count == 0)
        {
            throw new InvalidOperationException($"Ground truth file '{path}' contains no vulnerabilities.");
        }

        Normalize(document);
        return document;
    }

    private static void Normalize(GroundTruthDocument document)
    {
        if (document.GroundTruthSchemaVersion <= 0)
        {
            document.GroundTruthSchemaVersion = 1;
        }

        foreach (var vulnerability in document.Vulnerabilities)
        {
            vulnerability.Locations ??= [];
            vulnerability.Aliases ??= [];
            vulnerability.RequiredEvidence ??= [];
            vulnerability.EvidenceAnchors ??= new EvidenceAnchorSet();

            if (!vulnerability.EvidenceAnchors.HasAny && vulnerability.RequiredEvidence.Count > 0)
            {
                vulnerability.EvidenceAnchors.Must = vulnerability.RequiredEvidence
                    .Where(anchor => !string.IsNullOrWhiteSpace(anchor))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            else if (vulnerability.RequiredEvidence.Count == 0 && vulnerability.EvidenceAnchors.HasAny)
            {
                vulnerability.RequiredEvidence = vulnerability.EvidenceAnchors.Positive.ToList();
            }

            if (vulnerability.PrimaryLocation is null && vulnerability.Locations.Count > 0)
            {
                vulnerability.PrimaryLocation = vulnerability.Locations[0];
            }
        }
    }

    public GroundTruthValidationResult Validate(string groundTruthPath, string sourcePath)
    {
        var issues = new List<ValidationIssue>();
        var document = Load(groundTruthPath);
        var actualHash = ComputeSha256(sourcePath);

        if (!string.Equals(actualHash, document.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new ValidationIssue
            {
                Severity = "Error",
                Message = $"Source hash mismatch. Expected {document.SourceSha256}, actual {actualHash}."
            });
        }

        if (document.Vulnerabilities.Count != 20)
        {
            issues.Add(new ValidationIssue
            {
                Severity = "Warning",
                Message = $"Expected 20 vulnerabilities for supercalc-v3, found {document.Vulnerabilities.Count}."
            });
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var source = File.ReadAllText(sourcePath, Encoding.UTF8);
        var sourceLines = File.ReadAllLines(sourcePath, Encoding.UTF8);

        foreach (var vulnerability in document.Vulnerabilities)
        {
            if (string.IsNullOrWhiteSpace(vulnerability.Id))
            {
                issues.Add(new ValidationIssue { Severity = "Error", Message = "A vulnerability has an empty id." });
            }
            else if (!ids.Add(vulnerability.Id))
            {
                issues.Add(new ValidationIssue { Severity = "Error", Message = $"Duplicate vulnerability id '{vulnerability.Id}'." });
            }

            if (!vulnerability.StrictScoreable)
            {
                issues.Add(new ValidationIssue { Severity = "Warning", Message = $"{vulnerability.Id} is not strict-scoreable." });
            }

            var anchorsToValidate = vulnerability.EvidenceAnchors.HasAny
                ? vulnerability.EvidenceAnchors.Must.Concat(vulnerability.EvidenceAnchors.Should)
                : vulnerability.RequiredEvidence;
            foreach (var evidence in anchorsToValidate.Where(e => !string.IsNullOrWhiteSpace(e)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!source.Contains(evidence, StringComparison.Ordinal))
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Error",
                        Message = $"{vulnerability.Id}: required evidence anchor not present in source: {evidence}"
                    });
                }
            }

            ValidateVocabulary(issues, vulnerability.Id, "category", vulnerability.Category, AllowedCategories);
            ValidateVocabulary(issues, vulnerability.Id, "exploitability", vulnerability.Exploitability, AllowedExploitability);
            ValidateVocabulary(issues, vulnerability.Id, "reachability", vulnerability.Reachability, AllowedReachability);
            ValidateVocabulary(issues, vulnerability.Id, "difficulty", vulnerability.Difficulty, AllowedDifficulty);

            foreach (var location in vulnerability.Locations)
            {
                ValidateLocation(issues, vulnerability.Id, location, source, sourceLines);
            }

            if (vulnerability.PrimaryLocation is not null && !vulnerability.Locations.Contains(vulnerability.PrimaryLocation))
            {
                ValidateLocation(issues, vulnerability.Id, vulnerability.PrimaryLocation, source, sourceLines);
            }
        }

        return new GroundTruthValidationResult
        {
            ActualSourceSha256 = actualHash,
            ExpectedSourceSha256 = document.SourceSha256,
            VulnerabilityCount = document.Vulnerabilities.Count,
            Issues = issues
        };
    }

    private static void ValidateLocation(List<ValidationIssue> issues, string vulnerabilityId, CodeLocation location, string source, string[] sourceLines)
    {
        if (location.LineStart <= 0 || location.LineEnd < location.LineStart || location.LineEnd > sourceLines.Length)
        {
            issues.Add(new ValidationIssue
            {
                Severity = "Error",
                Message = $"{vulnerabilityId}: invalid line range {location.LineStart}-{location.LineEnd}."
            });
            return;
        }

        if (!string.IsNullOrWhiteSpace(location.Symbol))
        {
            var rangeText = string.Join('\n', sourceLines.Skip(location.LineStart - 1).Take(location.LineEnd - location.LineStart + 1));
            var symbolLeaf = TextUtil.SymbolLeaf(location.Symbol);
            if (!rangeText.Contains(symbolLeaf, StringComparison.OrdinalIgnoreCase) && !source.Contains(symbolLeaf, StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new ValidationIssue
                {
                    Severity = "Warning",
                    Message = $"{vulnerabilityId}: symbol leaf '{symbolLeaf}' was not found near/in source."
                });
            }
        }
    }

    private static void ValidateVocabulary(List<ValidationIssue> issues, string vulnerabilityId, string fieldName, string value, HashSet<string> allowed)
    {
        if (!string.IsNullOrWhiteSpace(value) && !allowed.Contains(value))
        {
            issues.Add(new ValidationIssue
            {
                Severity = "Error",
                Message = $"{vulnerabilityId}: invalid {fieldName} '{value}'."
            });
        }
    }

    private static readonly HashSet<string> AllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "Memory Safety", "Injection", "Auth", "Auth/Session", "Crypto", "Concurrency", "Numeric/DoS", "File I/O", "File/I/O", "Parser", "Logging", "Configuration", "Other"
    };

    private static readonly HashSet<string> AllowedExploitability = new(StringComparer.OrdinalIgnoreCase) { "High", "Medium", "Low" };

    private static readonly HashSet<string> AllowedReachability = new(StringComparer.OrdinalIgnoreCase)
    {
        "Direct", "Indirect", "Config-dependent", "Debug/Admin-only"
    };

    private static readonly HashSet<string> AllowedDifficulty = new(StringComparer.OrdinalIgnoreCase) { "Easy", "Medium", "Hard" };

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
