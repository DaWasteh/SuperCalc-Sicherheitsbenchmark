using SuperCalcBenchmark.Core;

namespace SuperCalcBenchmark.Tests;

internal static partial class TestRunner
{
    private const string EmptyAuditArrays = "\"truth_items\":[],\"false_positive_admissions\":[],\"corrections\":[]";

    private static void TruthAuditParserSelectsFinalResponse()
    {
        var schema = "{\"type\":\"object\",\"properties\":{\"audited_run\":{\"type\":\"string\"},\"truth_items\":{\"type\":\"array\"},\"false_positive_admissions\":{\"type\":\"array\"},\"corrections\":{\"type\":\"array\"}}}";
        var final = "{\"audited_run\":\"Run 2\",\"summary\":\"final\"," + EmptyAuditArrays + "}";
        var parsed = new TruthAuditParser().Parse(schema + "\nHere is the final answer:\n" + final);
        Assert(parsed.ParseSucceeded && parsed.RequiredArraysPresent, "final response must parse with required arrays");
        Assert(parsed.AuditedRun == "Run 2" && parsed.Summary == "final", "final response must beat preceding schema echo");
    }

    private static void TruthAuditParserHandlesCandidateBoundaries()
    {
        var first = "{\"audited_run\":\"Run 1\"," + EmptyAuditArrays + "}";
        var second = "{\"audited_run\":\"Run 2\",\"summary\":\"brace { text } and escaped \\\"quote\\\"\"," + EmptyAuditArrays + "}";
        var parsed = new TruthAuditParser().Parse("```json\n" + first + "\n```\ntext\n```json\n" + second + "\n```");
        Assert(parsed.ParseSucceeded && parsed.AuditedRun == "Run 2", "later equally valid fenced response must be selected");
        Assert(parsed.Summary.Contains("{ text }") && parsed.Summary.Contains("\"quote\""), "braces and escaped quotes inside strings must not split candidates");
    }

    private static void TruthAuditParserRejectsSchemaOnlyAndNormalizesAlias()
    {
        var schema = "{\"type\":\"object\",\"properties\":{\"truth_items\":{\"type\":\"array\"}}}";
        var invalid = new TruthAuditParser().Parse(schema);
        Assert(!invalid.ParseSucceeded && !invalid.RequiredArraysPresent, "schema-only content must be invalid and ineligible");

        var alias = new TruthAuditParser().Parse("{\"audited_run\":\"run2\"," + EmptyAuditArrays + "}");
        Assert(alias.ParseSucceeded && alias.AuditedRun == "Run 2", "run2 alias must normalize to Run 2");
    }
}
