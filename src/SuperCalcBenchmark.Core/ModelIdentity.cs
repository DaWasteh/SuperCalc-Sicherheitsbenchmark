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

    /// <summary>
    /// Resolves family + quant for a model id. Quant precedence (highest first):
    ///   1. <paramref name="quantOverride"/> — explicit manual input (UI field / --quant);
    ///      the archive must stay manually editable, so a hand-entered value always wins.
    ///   2. <paramref name="serverFtype"/> — authoritative file type reported by llama-server
    ///      via GET /v1/models data[].meta.ftype (llama.cpp PR #25134, build b9860+), e.g.
    ///      "Q4_K - Medium" or "(guessed) Q8_0". Beats name-based guessing because it reads
    ///      the GGUF ftype header the model was actually quantized with.
    ///   3. name-based <see cref="DetectQuant"/> regex fallback (legacy path).
    ///   4. <see cref="UnknownQuant"/>.
    /// </summary>
    public static ModelIdentityInfo Parse(string? rawModelId, string? quantOverride = null, string? serverFtype = null)
    {
        var raw = (rawModelId ?? string.Empty).Trim();
        var stem = StripPathAndExtension(raw);

        var nameDetectedQuant = DetectQuant(stem);
        // MXFP4 exists only as the MoE file type; the archive uses one label for every spelling
        // ("mxfp4_moe", "moe-mxfp4", server "MXFP4 MoE") so uploads of one model share a group.
        var serverQuant = NormalizeServerFtype(serverFtype) is { } ftypeQuant && ftypeQuant.StartsWith("MXFP4", StringComparison.OrdinalIgnoreCase)
            ? "MXFP4"
            : NormalizeServerFtype(serverFtype);

        // The server ftype (dominant tensor type from the GGUF header) outranks a contradicting
        // name-based guess. A name that only refines it (Q6_K_L, UD_Q8_K_XL, NVFP4-Q4_K_M: same
        // bit-width base, mixed-quant recipe) is more specific and wins, otherwise every such
        // upload would be merged into the plain ftype group. The manual override outranks all.
        var nameRefinesServer = serverQuant is not null
                                && nameDetectedQuant is not null
                                && !string.Equals(serverQuant, nameDetectedQuant, StringComparison.OrdinalIgnoreCase)
                                && QuantBase(serverQuant) is { } serverBase
                                && string.Equals(serverBase, QuantBase(nameDetectedQuant), StringComparison.OrdinalIgnoreCase);
        var authoritativeQuant = nameRefinesServer ? nameDetectedQuant : serverQuant ?? nameDetectedQuant;
        var quant = !string.IsNullOrWhiteSpace(quantOverride)
            ? quantOverride.Trim()
            : authoritativeQuant ?? UnknownQuant;

        // Family derivation strips whichever quant we believe in, so the server-reported
        // token (e.g. "Q4_K_M") is removed from the family just like a name-encoded one.
        var family = DeriveFamily(stem, authoritativeQuant);

        return new ModelIdentityInfo
        {
            RawModelId = raw,
            Family = family,
            Quant = quant,
            QuantWasDetected = authoritativeQuant is not null,
            QuantSource = string.IsNullOrWhiteSpace(quantOverride)
                ? (serverQuant is not null && !nameRefinesServer ? QuantSource.Server : (nameDetectedQuant is not null ? QuantSource.Name : QuantSource.None))
                : QuantSource.Manual
        };
    }

    /// <summary>
    /// Normalizes the file-type string exposed by llama-server (llama_ftype_name, llama.cpp
    /// PR #25134) into the canonical quant token used by the archive, e.g.
    ///   "Q8_0"                          -> "Q8_0"
    ///   "Q4_K - Medium"                 -> "Q4_K_M"
    ///   "(guessed) Q8_0"                -> "Q8_0"
    ///   "IQ3_S mix - 3.66 bpw"          -> "IQ3_M"   (LLAMA_FTYPE_MOSTLY_IQ3_M)
    ///   "IQ3_XXS - 3.0625 bpw"          -> "IQ3_XXS"
    ///   "TQ2_0 - 2.06 bpw ternary"      -> "TQ2_0"
    ///   "MXFP4 MoE"                     -> "MXFP4_MoE"
    ///   "all F32"                       -> "F32"
    ///   "unknown, may not work"         -> null
    /// Returns null for null/blank/unrecognizable input so callers fall back to name detection.
    /// The mapping mirrors the switch table in src/llama-model-loader.cpp verbatim.
    /// </summary>
    public static string? NormalizeServerFtype(string? serverFtype)
    {
        if (string.IsNullOrWhiteSpace(serverFtype))
        {
            return null;
        }

        var value = serverFtype.Trim();

        // PR #25134 prepends "(guessed) " when the ftype was inferred rather than read from
        // the GGUF header. The underlying token is still valid, just strip the marker.
        const string GuessedPrefix = "(guessed) ";
        if (value.StartsWith(GuessedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            value = value[GuessedPrefix.Length..].Trim();
        }

        if (ServerFtypeMap.TryGetValue(value, out var canonical))
        {
            return canonical;
        }

        // Unknown / unmapped ftype ("unknown, may not work" or a future quant we have not
        // taught the map). Do not fabricate a label — let the name-based detector try next.
        return null;
    }

    // Verbatim mirror of llama_ftype_name()'s switch in src/llama-model-loader.cpp (PR #25134).
    // Keys are the exact C++ string literals (case-sensitive as emitted); values are the
    // canonical archive tokens that line up with DetectQuant's output and the group-key format.
    private static readonly Dictionary<string, string> ServerFtypeMap = new(StringComparer.Ordinal)
    {
        ["all F32"] = "F32",
        ["F16"] = "F16",
        ["BF16"] = "BF16",
        ["Q1_0"] = "Q1_0",
        ["Q4_0"] = "Q4_0",
        ["Q4_1"] = "Q4_1",
        ["Q5_0"] = "Q5_0",
        ["Q5_1"] = "Q5_1",
        ["Q8_0"] = "Q8_0",
        ["MXFP4 MoE"] = "MXFP4_MoE",
        ["NVFP4"] = "NVFP4",
        ["Q2_K - Medium"] = "Q2_K",
        ["Q2_K - Small"] = "Q2_K_S",
        ["Q3_K - Small"] = "Q3_K_S",
        ["Q3_K - Medium"] = "Q3_K_M",
        ["Q3_K - Large"] = "Q3_K_L",
        ["Q4_K - Small"] = "Q4_K_S",
        ["Q4_K - Medium"] = "Q4_K_M",
        ["Q5_K - Small"] = "Q5_K_S",
        ["Q5_K - Medium"] = "Q5_K_M",
        ["Q6_K"] = "Q6_K",
        ["TQ1_0 - 1.69 bpw ternary"] = "TQ1_0",
        ["TQ2_0 - 2.06 bpw ternary"] = "TQ2_0",
        ["IQ2_XXS - 2.0625 bpw"] = "IQ2_XXS",
        ["IQ2_XS - 2.3125 bpw"] = "IQ2_XS",
        ["IQ2_S - 2.5 bpw"] = "IQ2_S",
        ["IQ2_M - 2.7 bpw"] = "IQ2_M",
        ["IQ3_XS - 3.3 bpw"] = "IQ3_XS",
        ["IQ3_XXS - 3.0625 bpw"] = "IQ3_XXS",
        ["IQ1_S - 1.5625 bpw"] = "IQ1_S",
        ["IQ1_M - 1.75 bpw"] = "IQ1_M",
        ["IQ4_NL - 4.5 bpw"] = "IQ4_NL",
        ["IQ4_XS - 4.25 bpw"] = "IQ4_XS",
        ["IQ3_S - 3.4375 bpw"] = "IQ3_S",
        // LLAMA_FTYPE_MOSTLY_IQ3_M is reported as "IQ3_S mix" — the only ftype whose label is
        // not a clean token prefix, so it needs an explicit entry rather than suffix stripping.
        ["IQ3_S mix - 3.66 bpw"] = "IQ3_M"
    };

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

    /// <summary>Bit-width base of a quant token ("Q6", "IQ4", "BF16"), used to tell a refinement from a contradiction.</summary>
    private static string? QuantBase(string quant)
    {
        var match = QuantBaseRegex().Match(quant);
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
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

        // Normalise aliases to one label: FP16/FP32 floats, "UD-" prefix, every MXFP4(/MoE) spelling.
        var value = match.Value.ToUpperInvariant();
        if (value.Contains("MXFP4", StringComparison.Ordinal))
        {
            return "MXFP4";
        }

        if (value.StartsWith("UD-", StringComparison.Ordinal))
        {
            value = "UD_" + value[3..];
        }

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

    // Compound/prefixed tokens first (NVFP4-Q4_K_M, UD-Q4_K_XL), then IQ, K-quants with
    // _XL/_L/_M/_S recipes, legacy Qn_n (incl. Q1_0 and Q4_0_4_4), ternary TQ, FP4 formats, floats.
    private const string QuantPattern =
        @"NVFP4[-_]Q[1-8]_K(?:_(?:XL|L|M|S))?|(?:UD[-_])?(?:IQ[1-4]_(?:XXS|XS|S|M|NL)|Q[1-8]_K(?:_(?:XXL|XL|L|M|S))?|Q[1-8]_[01](?:_[48]_[48])?|TQ[12]_0)|(?:MOE[-_])?MXFP4(?:[-_]MOE)?|NVFP4|BF16|FP16|FP32|F16|F32";

    [GeneratedRegex(@"(?<![A-Za-z0-9])(" + QuantPattern + @")(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantRegex();

    [GeneratedRegex(@"[-_.]?(" + QuantPattern + @")(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex QuantStripRegex();

    [GeneratedRegex(@"(?:UD[-_]|NVFP4[-_])?(I?Q\d|TQ\d|BF16|F16|F32|MXFP4|NVFP4)", RegexOptions.IgnoreCase)]
    private static partial Regex QuantBaseRegex();

    [GeneratedRegex(@"-\d{5}-of-\d{5}$", RegexOptions.IgnoreCase)]
    private static partial Regex ShardSuffixRegex();

    [GeneratedRegex(
        @"[-_.]?(gguf|ggml|imatrix|imat|i1|gptq|awq|exl2|safetensors)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase)]
    private static partial Regex NoiseRegex();

    [GeneratedRegex(@"[-_.\s]+")]
    private static partial Regex SeparatorRegex();
}

public enum QuantSource
{
    None,
    Name,
    Server,
    Manual
}

public sealed class ModelIdentityInfo
{
    public string RawModelId { get; init; } = string.Empty;
    public string Family { get; init; } = "unknown-model";
    public string Quant { get; init; } = ModelIdentity.UnknownQuant;
    public bool QuantWasDetected { get; init; }

    /// <summary>Which precedence layer supplied <see cref="Quant"/> (manual / server / name / none).</summary>
    public QuantSource QuantSource { get; init; } = QuantSource.None;

    public string GroupKey => ModelIdentity.GroupKey(Family, Quant);

    /// <summary>Human-friendly label such as "qwen3-coder-30b (Q4_K_M)".</summary>
    public string DisplayLabel => $"{Family} ({Quant})";
}
