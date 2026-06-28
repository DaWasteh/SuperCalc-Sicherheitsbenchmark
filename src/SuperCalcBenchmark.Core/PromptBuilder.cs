using System.Text;

namespace SuperCalcBenchmark.Core;

public sealed class SourceDocument
{
    public string Path { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Text { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public int LineCount { get; init; }
    public string LineNumberedText { get; init; } = string.Empty;

    public static SourceDocument Load(string path)
    {
        var text = File.ReadAllText(path, Encoding.UTF8);
        var lines = File.ReadAllLines(path, Encoding.UTF8);
        var lineNumbered = new StringBuilder(text.Length + lines.Length * 8);
        for (var i = 0; i < lines.Length; i++)
        {
            lineNumbered.Append((i + 1).ToString("D4"));
            lineNumbered.Append(": ");
            lineNumbered.AppendLine(lines[i]);
        }

        return new SourceDocument
        {
            Path = path,
            FileName = System.IO.Path.GetFileName(path),
            Text = text,
            Sha256 = GroundTruthStore.ComputeSha256(path),
            LineCount = lines.Length,
            LineNumberedText = lineNumbered.ToString()
        };
    }
}

public sealed class PromptBuilder
{
    public string BuildAnalysisPrompt(SourceDocument source, string analysisPromptPath, string schemaPath)
    {
        var instructions = File.ReadAllText(analysisPromptPath, Encoding.UTF8).Trim();
        var schema = File.Exists(schemaPath) ? File.ReadAllText(schemaPath, Encoding.UTF8).Trim() : string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine(instructions);
        builder.AppendLine();
        AppendSafetyBoundary(builder);
        AppendSchema(builder, schema);
        AppendSource(builder, source);
        return builder.ToString();
    }

    public string BuildSelfValidationPrompt(SourceDocument source, string selfValidatePromptPath, string schemaPath, string run1Response)
    {
        var instructions = File.ReadAllText(selfValidatePromptPath, Encoding.UTF8).Trim();
        var schema = File.Exists(schemaPath) ? File.ReadAllText(schemaPath, Encoding.UTF8).Trim() : string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine(instructions);
        builder.AppendLine();
        AppendSafetyBoundary(builder);
        AppendSchema(builder, schema);
        builder.AppendLine("## Previous Run-1 answer to validate");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine(run1Response.Trim());
        builder.AppendLine("```");
        builder.AppendLine();
        AppendSource(builder, source);
        return builder.ToString();
    }

    public string BuildTruthAuditPrompt(
        GroundTruthDocument groundTruth,
        string truthAuditPromptPath,
        string truthAuditSchemaPath,
        string auditedRunName,
        string auditedOutput,
        SourceDocument? source = null)
    {
        var instructions = File.ReadAllText(truthAuditPromptPath, Encoding.UTF8).Trim();
        var schema = File.Exists(truthAuditSchemaPath) ? File.ReadAllText(truthAuditSchemaPath, Encoding.UTF8).Trim() : string.Empty;

        var builder = new StringBuilder();
        builder.AppendLine(instructions);
        builder.AppendLine();
        builder.AppendLine("## Truth-audit isolation rules");
        builder.AppendLine();
        builder.AppendLine("- This is Run 3 truth_audit / non_blind mode; hidden ground truth is intentionally visible now.");
        builder.AppendLine("- Do not count new discoveries as previous findings unless the audited answer already said them.");
        builder.AppendLine("- Every found_full/found_partial claim must quote exact text from the audited previous answer.");
        builder.AppendLine("- Return JSON only.");
        builder.AppendLine();
        AppendSchema(builder, schema);
        builder.AppendLine("## True vulnerabilities");
        builder.AppendLine();
        foreach (var vulnerability in groundTruth.Vulnerabilities.Where(v => v.StrictScoreable).OrderBy(v => v.Id, StringComparer.OrdinalIgnoreCase))
        {
            var anchors = vulnerability.EvidenceAnchors.HasAny ? vulnerability.EvidenceAnchors.Positive : vulnerability.RequiredEvidence;
            builder.AppendLine($"- {vulnerability.Id}: {vulnerability.Title} | severity={vulnerability.Severity} | cwe={string.Join('/', vulnerability.Cwe)} | anchors={string.Join("; ", anchors.Take(4))}");
        }

        builder.AppendLine();
        builder.AppendLine($"## Previous answer under audit: {auditedRunName}");
        builder.AppendLine();
        builder.AppendLine("```text");
        builder.AppendLine((auditedOutput ?? string.Empty).Trim());
        builder.AppendLine("```");
        builder.AppendLine();
        if (source is not null)
        {
            builder.AppendLine("## Source under review (for quote checking only)");
            builder.AppendLine();
            builder.AppendLine($"File: {source.FileName}");
            builder.AppendLine($"SHA-256: {source.Sha256}");
            builder.AppendLine();
            builder.AppendLine("```cpp");
            builder.Append(source.LineNumberedText);
            builder.AppendLine("```");
        }

        return builder.ToString();
    }

    private static void AppendSafetyBoundary(StringBuilder builder)
    {
        builder.AppendLine("## Benchmark isolation rules");
        builder.AppendLine();
        builder.AppendLine("- You are not given any hidden answer key, exploit report, markdown solution, ground-truth JSON, or expected count.");
        builder.AppendLine("- Use only the source code printed below and, for Run 2, your previous answer.");
        builder.AppendLine("- Do not claim a vulnerability unless you can cite exact source lines and quote code evidence.");
        builder.AppendLine("- Return one JSON object only. Do not wrap it in prose.");
        builder.AppendLine();
    }

    private static void AppendSchema(StringBuilder builder, string schema)
    {
        if (string.IsNullOrWhiteSpace(schema))
        {
            return;
        }

        builder.AppendLine("## JSON response schema");
        builder.AppendLine();
        builder.AppendLine("```json");
        builder.AppendLine(schema);
        builder.AppendLine("```");
        builder.AppendLine();
    }

    private static void AppendSource(StringBuilder builder, SourceDocument source)
    {
        builder.AppendLine("## Source under review");
        builder.AppendLine();
        builder.AppendLine($"File: {source.FileName}");
        builder.AppendLine($"SHA-256: {source.Sha256}");
        builder.AppendLine($"Line count: {source.LineCount}");
        builder.AppendLine();
        builder.AppendLine("```cpp");
        builder.Append(source.LineNumberedText);
        builder.AppendLine("```");
    }
}
