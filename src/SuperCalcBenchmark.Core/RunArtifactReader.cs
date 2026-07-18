using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuperCalcBenchmark.Core;

public sealed class RunArtifactReadResult
{
    public BenchmarkRunResult? Run { get; init; }
    public string? FinalResponse { get; init; }
    public string? ResponseSource { get; init; }
    public string? RunJsonSha256 { get; init; }
    public string? FinalResponseSha256 { get; init; }
    public string? Error { get; init; }
    public bool CorruptRawChunksSkipped { get; init; }
    public bool IdentityMatches => Error is null;
}

public sealed class RunArtifactReader
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public RunArtifactReadResult Read(ArchiveRecord record)
    {
        var directory = record.RunDirectory;
        var runPath = Path.Combine(directory, "run.json");
        if (!File.Exists(runPath)) return new() { Error = "run directory or run.json is missing" };
        try
        {
            var bytes = File.ReadAllBytes(runPath);
            var jsonBytes = StripUtf8Bom(bytes);
            var run = JsonSerializer.Deserialize<BenchmarkRunResult>(jsonBytes, Options);
            if (run is null) return new() { Error = "run.json is empty" };
            var mismatch = Verify(record, run);
            if (mismatch is not null) return new() { Error = mismatch, RunJsonSha256 = Hash(bytes) };
            var (response, source, skipped) = ReadResponse(run, directory);
            return new() { Run = run, FinalResponse = response, ResponseSource = source, CorruptRawChunksSkipped = skipped, RunJsonSha256 = Hash(bytes), FinalResponseSha256 = response is null ? null : Hash(Encoding.UTF8.GetBytes(response)) }; 
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { return new() { Error = $"corrupt/unreadable artifact: {ex.Message}" }; }
    }

    private static string? Verify(ArchiveRecord a, BenchmarkRunResult r)
    {
        if (!string.Equals(a.BenchmarkId, r.BenchmarkId, StringComparison.OrdinalIgnoreCase)) return "identity mismatch: benchmark";
        if (!string.IsNullOrWhiteSpace(a.SourceSha256) && !string.Equals(a.SourceSha256, r.SourceSha256, StringComparison.OrdinalIgnoreCase)) return "identity mismatch: source hash";
        if (a.StartedAt != default && r.StartedAt != default && Math.Abs((a.StartedAt-r.StartedAt).TotalSeconds) > 1) return "identity mismatch: start time";
        if (!string.IsNullOrWhiteSpace(a.RawModelId) && !string.Equals(a.RawModelId, r.Model, StringComparison.OrdinalIgnoreCase)) return "identity mismatch: model";
        if (!string.IsNullOrWhiteSpace(a.RepeatGroupId) && !string.Equals(a.RepeatGroupId, r.RepeatGroupId, StringComparison.Ordinal)) return "identity mismatch: repeat group";
        if (a.RepeatIndex > 0 && r.RepeatIndex > 0 && a.RepeatIndex != r.RepeatIndex) return "identity mismatch: repeat index";
        if (a.Seed.HasValue && a.Seed.Value != r.Seed) return "identity mismatch: seed";
        if (a.CompletedAt != default && r.CompletedAt != default && Math.Abs((a.CompletedAt-r.CompletedAt).TotalSeconds) > 1) return "identity mismatch: completion time";
        if (!string.IsNullOrWhiteSpace(r.OutputDirectory) && !SameDirectory(a.RunDirectory, r.OutputDirectory)) return "identity mismatch: output directory";
        return null;
    }

    private static (string?, string?, bool) ReadResponse(BenchmarkRunResult run, string directory)
    {
        if (!string.IsNullOrWhiteSpace(run.Run3?.Response)) return (run.Run3.Response, "run.json", false);
        var text = Path.Combine(directory, "run3_response.txt");
        if (File.Exists(text)) { var value = File.ReadAllText(text); if (!string.IsNullOrWhiteSpace(value)) return (value, "run3_response.txt", false); }
        var raw = Path.Combine(directory, "run3_raw_response.json");
        if (!File.Exists(raw)) return (null, null, false);
        var reconstructed = ReconstructRaw(raw, out var skipped);
        return (reconstructed, "run3_raw_response.json", skipped);
    }

    public static string? ReconstructRaw(string path) => ReconstructRaw(path, out _);
    public static string? ReconstructRaw(string path, out bool corruptChunksSkipped)
    {
        corruptChunksSkipped = false;
        var output = new StringBuilder();
        foreach (var original in File.ReadLines(path))
        {
            var line = original.Trim(); if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) line = line[5..].Trim();
            if (line.Length == 0 || line == "[DONE]") continue;
            try
            {
                using var doc = JsonDocument.Parse(line); var root = doc.RootElement;
                if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String) output.Append(content.GetString());
                else if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out content) && content.ValueKind == JsonValueKind.String) output.Append(content.GetString());
            }
            catch (JsonException) { corruptChunksSkipped = true; }
        }
        return output.Length == 0 ? null : output.ToString();
    }

    internal static ReadOnlySpan<byte> StripUtf8Bom(byte[] bytes) =>
        bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? bytes.AsSpan(3) : bytes;

    private static bool SameDirectory(string a, string b)
    {
        try { return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); }
        catch (Exception) when (a is not null && b is not null) { return false; }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
