using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.Core;

/// <summary>
/// Splits a raw llama.cpp model id (which is usually a GGUF file name or a llama-server
/// alias) into a stable model family and an explicit quantization label, so the archive can
/// group every quant of the same model together (e.g. all qwen3-coder-30b runs) while still
/// telling Q4_K_M apart from IQ3_XXS in a comparison.
///
/// llama.cpp / GGUF quant tokens we recognise:
///   Q2_K, Q3_K_S/M/L, Q4_0, Q4_1, Q4_K_S/M, Q5_K_M, Q6_K, Q8_0, ...
///   IQ1_S, IQ2_XXS/XS/S/M, IQ3_XXS/XS/S/M, IQ4_NL, IQ4_XS, ...
///   F16, FP16, BF16, F32, FP32
/// </summary>
public static partial class ModelIdentity
{
    public const string UnknownQuant = "unknown-quant";

    public static ModelIdentityInfo Parse(string? rawModelId, string? quantOverride = null)
    {
        var raw = (rawModelId ?? string.Empty).Trim();
        var stem = StripPathAndExtension(raw);

        var detectedQuant = DetectQuant(stem);
        var quant = !string.IsNullOrWhiteSpace(quantOverride)
            ? quantOverride.Trim()
            : detectedQuant ?? UnknownQuant;

        var family = DeriveFamily(stem, detectedQuant);

        return new ModelIdentityInfo
        {
            RawModelId = raw,
            Family = family,
            Quant = quant,
            QuantWasDetected = detectedQuant is not null
        };
    }

    /// <summary>
    /// Stable folder/group key combining family and quant, e.g. "qwen3-coder-30b__Q4_K_M".
    /// Safe to use as a directory name on every platform.
    /// </summary>
    public static string GroupKey(string family, string quant)
    {
        var familyPart = TextUtil.SafeFileNamePart(family);
        var quantPart = TextUtil.SafeFileNamePart(quant);
        return $"{familyPart}__{quantPart}";
    }

    private static string StripPathAndExtension(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        // Model ids frequently arrive as a full GGUF path or file name. Take the leaf and
        // drop a trailing .gguf so the quant/family tokens are clean.
        var leaf = raw.Replace('\\', '/');
        var slash = leaf.LastIndexOf('/');
        if (slash >= 0 && slash < leaf.Length - 1)
        {
            leaf = leaf[(slash + 1)..];
        }

        if (leaf.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
        {
            leaf = leaf[..^5];
        }

        // Multi-part GGUF shards like model-00001-of-00003 -> strip the shard suffix.
        leaf = ShardSuffixRegex().Replace(leaf, string.Empty);

        return leaf.Trim();
    }

    private static string? DetectQuant(string stem)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return null;
        }

        var match = QuantRegex().Match(stem);
        if (!match.Success)
        {
            return null;
        }

        // Normalise common float aliases to a single label.
        var value = match.Value.ToUpperInvariant();
        return value switch
        {
            "FP16" => "F16",
            "FP32" => "F32",
            _ => value
        };
    }

    private static string DeriveFamily(string stem, string? detectedQuant)
    {
        if (string.IsNullOrWhiteSpace(stem))
        {
            return "unknown-model";
        }

        var family = stem;

        // Remove the detected quant token (and an adjacent separator) from the family name.
        if (!string.IsNullOrWhiteSpace(detectedQuant))
        {
            family = QuantStripRegex().Replace(family, string.Empty);
        }

        // Drop common packaging noise so different uploads of the same model collapse together.
        family = NoiseRegex().Replace(family, string.Empty);

        // Collapse leftover separators.
        family = SeparatorRegex().Replace(family, "-").Trim('-', '.', '_', ' ');

        return string.IsNullOrWhiteSpace(family) ? "unknown-model" : family.ToLowerInvariant();
    }

    // IQ quants first (longer tokens), then K-quants, then legacy Qn_n, then floats.
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[2-8]_K(?:_[SML])?|Q[2-8]_[01]|Q[2-8]_K|BF16|FP16|FP32|F16|F32)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuantRegex();

    [GeneratedRegex(
        @"[-_.]?(IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[2-8]_K(?:_[SML])?|Q[2-8]_[01]|Q[2-8]_K|BF16|FP16|FP32|F16|F32)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex QuantStripRegex();

    [GeneratedRegex(@"-\d{5}-of-\d{5}$", RegexOptions.IgnoreCase)]
    private static partial Regex ShardSuffixRegex();

    [GeneratedRegex(
        @"[-_.]?(gguf|ggml|imatrix|imat|i1|gptq|awq|exl2|safetensors)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"[-_.\s]+")]
    private static partial Regex SeparatorRegex();
}

public sealed class ModelIdentityInfo
{
    public string RawModelId { get; init; } = string.Empty;
    public string Family { get; init; } = "unknown-model";
    public string Quant { get; init; } = ModelIdentity.UnknownQuant;
    public bool QuantWasDetected { get; init; }

    public string GroupKey => ModelIdentity.GroupKey(Family, Quant);

    /// <summary>Human-friendly label such as "qwen3-coder-30b (Q4_K_M)".</summary>
    public string DisplayLabel => $"{Family} ({Quant})";
}
