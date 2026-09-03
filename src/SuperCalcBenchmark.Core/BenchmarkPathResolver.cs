namespace SuperCalcBenchmark.Core;

public sealed class BenchmarkPathResolutionOptions
{
    public string? CurrentDirectory { get; init; }
    public string? BaseDirectory { get; init; }
    public string? ExplicitAssetRoot { get; init; }
    public string? ExplicitDataRoot { get; init; }
}

public sealed class BenchmarkPathSet
{
    public required string AssetRoot { get; init; }
    public required string DataRoot { get; init; }
    public required string RunsRoot { get; init; }
    public required string ArchiveRoot { get; init; }
    public required string SettingsFile { get; init; }

    public string SourcePath => Path.Combine(AssetRoot, "enhanced_calc.cpp");
    public string GroundTruthPath => Path.Combine(AssetRoot, "benchmarks", "supercalc-v3", "ground_truth.json");
    public string AnalysisPromptPath => Path.Combine(AssetRoot, "benchmarks", "supercalc-v3", "prompts", "analysis_v1.md");
    public string SelfValidatePromptPath => Path.Combine(AssetRoot, "benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md");
    public string TruthAuditPromptPath => Path.Combine(AssetRoot, "benchmarks", "supercalc-v3", "prompts", "truth_audit_v2.md");
    public string FindingsSchemaPath => Path.Combine(AssetRoot, "benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json");
    public string TruthAuditSchemaPath => Path.Combine(AssetRoot, "benchmarks", "supercalc-v3", "schemas", "truth_audit_v2.schema.json");
    public string LegacyArchiveRoot => Path.Combine(AssetRoot, ArchiveStore.DefaultArchiveFolderName);

    /// <summary>
    /// Tracked archive in a Git checkout. Null for standalone/portable asset folders, where
    /// benchmark results must remain exclusively in the shared per-user data pool.
    /// </summary>
    public string? RepositoryArchiveRoot => Directory.Exists(Path.Combine(AssetRoot, ".git"))
                                            || File.Exists(Path.Combine(AssetRoot, ".git"))
        ? LegacyArchiveRoot
        : null;
}

/// <summary>
/// Resolves immutable benchmark assets separately from mutable per-user data. This
/// makes source, CLI, and portable EXE starts share one runs/archive pool while each
/// binary can still load the assets shipped beside it.
/// </summary>
public static class BenchmarkPathResolver
{
    public const string AssetRootEnvironmentVariable = "SUPERCALC_ASSET_ROOT";
    public const string LegacyAssetRootEnvironmentVariable = "SUPERCALC_REPOSITORY_ROOT";
    public const string DataRootEnvironmentVariable = "SUPERCALC_DATA_ROOT";

    private static readonly string[] RequiredAssetPaths =
    [
        "enhanced_calc.cpp",
        Path.Combine("benchmarks", "supercalc-v3", "ground_truth.json"),
        Path.Combine("benchmarks", "supercalc-v3", "prompts", "analysis_v1.md"),
        Path.Combine("benchmarks", "supercalc-v3", "prompts", "self_validate_v1.md"),
        Path.Combine("benchmarks", "supercalc-v3", "prompts", "truth_audit_v2.md"),
        Path.Combine("benchmarks", "supercalc-v3", "schemas", "llm_findings.schema.json"),
        Path.Combine("benchmarks", "supercalc-v3", "schemas", "truth_audit_v2.schema.json")
    ];

    public static BenchmarkPathSet Resolve(BenchmarkPathResolutionOptions? options = null)
    {
        options ??= new BenchmarkPathResolutionOptions();
        var currentDirectory = NormalizeDirectory(options.CurrentDirectory, Environment.CurrentDirectory);
        var baseDirectory = NormalizeDirectory(options.BaseDirectory, AppContext.BaseDirectory);
        var explicitAssetRoot = FirstNonEmpty(
            options.ExplicitAssetRoot,
            Environment.GetEnvironmentVariable(AssetRootEnvironmentVariable),
            Environment.GetEnvironmentVariable(LegacyAssetRootEnvironmentVariable));
        var explicitDataRoot = FirstNonEmpty(
            options.ExplicitDataRoot,
            Environment.GetEnvironmentVariable(DataRootEnvironmentVariable));

        var assetRoot = ResolveAssetRoot(explicitAssetRoot, currentDirectory, baseDirectory);
        var dataRoot = ResolveDataRoot(explicitDataRoot, currentDirectory);
        return new BenchmarkPathSet
        {
            AssetRoot = assetRoot,
            DataRoot = dataRoot,
            RunsRoot = Path.Combine(dataRoot, "Runs"),
            ArchiveRoot = Path.Combine(dataRoot, ArchiveStore.DefaultArchiveFolderName),
            SettingsFile = Path.Combine(dataRoot, "app-settings.json")
        };
    }

    public static string ResolveDataRoot(string? explicitDataRoot = null, string? currentDirectory = null)
    {
        currentDirectory = NormalizeDirectory(currentDirectory, Environment.CurrentDirectory);
        explicitDataRoot = FirstNonEmpty(
            explicitDataRoot,
            Environment.GetEnvironmentVariable(DataRootEnvironmentVariable));
        if (!string.IsNullOrWhiteSpace(explicitDataRoot))
        {
            return NormalizePath(explicitDataRoot, currentDirectory);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            localAppData = string.IsNullOrWhiteSpace(home) ? Path.GetTempPath() : Path.Combine(home, ".local", "share");
        }

        return Path.GetFullPath(Path.Combine(localAppData, "SuperCalcBenchmark"));
    }

    public static bool IsAssetRoot(string path)
    {
        try
        {
            var root = Path.GetFullPath(path);
            return RequiredAssetPaths.All(relative => File.Exists(Path.Combine(root, relative)));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryCreateDataLocator(string absolutePath, string dataRoot, out string locator)
    {
        locator = string.Empty;
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
            var path = Path.GetFullPath(absolutePath);
            var relative = Path.GetRelativePath(root, path);
            if (relative == "."
                || Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
            {
                return false;
            }

            locator = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryResolveDataLocator(string locator, string dataRoot, out string absolutePath)
    {
        absolutePath = string.Empty;
        if (string.IsNullOrWhiteSpace(locator) || Path.IsPathRooted(locator))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot));
            var normalizedLocator = locator.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(root, normalizedLocator));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            var rootWithSeparator = root + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(rootWithSeparator, comparison))
            {
                return false;
            }

            absolutePath = candidate;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool SamePath(string left, string right)
    {
        try
        {
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                comparison);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static string ResolveAssetRoot(string? explicitRoot, string currentDirectory, string baseDirectory)
    {
        if (!string.IsNullOrWhiteSpace(explicitRoot))
        {
            var root = NormalizePath(explicitRoot, currentDirectory);
            if (!IsAssetRoot(root))
            {
                var missing = RequiredAssetPaths.Where(relative => !File.Exists(Path.Combine(root, relative)));
                throw new DirectoryNotFoundException(
                    $"Explicit SuperCalc asset root '{root}' is incomplete. Missing: {string.Join(", ", missing)}");
            }

            return root;
        }

        var comparison = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var launchCandidates = new List<string>();
        AddAncestors(launchCandidates, currentDirectory);
        AddAncestors(launchCandidates, baseDirectory);
        var launchRoot = SelectAssetRoot(launchCandidates, comparison);
        if (!string.IsNullOrWhiteSpace(launchRoot))
        {
            return launchRoot;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate SuperCalc benchmark assets from current directory '{currentDirectory}' or application directory '{baseDirectory}'. "
            + $"Set {AssetRootEnvironmentVariable} to a folder containing enhanced_calc.cpp and benchmarks/supercalc-v3.");
    }

    private static string? SelectAssetRoot(IEnumerable<string> candidates, StringComparer comparer)
    {
        var valid = candidates.Distinct(comparer).Where(IsAssetRoot).ToList();
        return valid.FirstOrDefault(path => Directory.Exists(Path.Combine(path, ".git"))
                                            || File.Exists(Path.Combine(path, "SuperCalcBenchmark.slnx")))
               ?? valid.FirstOrDefault();
    }

    private static void AddAncestors(List<string> candidates, string path)
    {
        var directory = new DirectoryInfo(path);
        while (directory is not null)
        {
            candidates.Add(directory.FullName);
            directory = directory.Parent;
        }
    }

    private static string NormalizeDirectory(string? value, string fallback)
    {
        var selected = string.IsNullOrWhiteSpace(value) ? fallback : value;
        return Path.GetFullPath(selected);
    }

    private static string NormalizePath(string value, string baseDirectory)
        => Path.IsPathRooted(value) ? Path.GetFullPath(value) : Path.GetFullPath(value, baseDirectory);

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
