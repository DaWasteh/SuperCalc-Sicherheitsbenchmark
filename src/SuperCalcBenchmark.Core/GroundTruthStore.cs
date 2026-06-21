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

        return document;
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

            foreach (var evidence in vulnerability.RequiredEvidence.Where(e => !string.IsNullOrWhiteSpace(e)))
            {
                if (!source.Contains(evidence, StringComparison.Ordinal))
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Error",
                        Message = $"{vulnerability.Id}: required evidence not present in source: {evidence}"
                    });
                }
            }

            foreach (var location in vulnerability.Locations)
            {
                if (location.LineStart <= 0 || location.LineEnd < location.LineStart || location.LineEnd > sourceLines.Length)
                {
                    issues.Add(new ValidationIssue
                    {
                        Severity = "Error",
                        Message = $"{vulnerability.Id}: invalid line range {location.LineStart}-{location.LineEnd}."
                    });
                    continue;
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
                            Message = $"{vulnerability.Id}: symbol leaf '{symbolLeaf}' was not found near/in source."
                        });
                    }
                }
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

    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
