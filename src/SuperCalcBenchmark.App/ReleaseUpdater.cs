using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuperCalcBenchmark.App;

/// <summary>
/// Update path for the standalone (self-contained) EXE distribution.
///
/// The git checkout keeps using "git pull --ff-only" (see MainWindow); this class
/// covers users who downloaded the release ZIP and have neither git nor the .NET SDK.
/// It looks up the newest app release on GitHub (tags like v0.6.5 with a
/// SuperCalcBenchmark-win-x64*.zip asset — the auto "SuperCalc Comparison vNNN"
/// releases from pages.yml carry neither and are skipped), downloads and extracts
/// the ZIP into %LOCALAPPDATA%\SuperCalcBenchmark\Updates, and swaps the files in
/// via a small cmd script after the app exits.  archive/, artifacts/ and results/
/// next to the EXE are never touched.
/// </summary>
internal static partial class ReleaseUpdater
{
    private const string Owner = "DaWasteh";
    private const string Repository = "SuperCalc-Sicherheitsbenchmark";
    private const string AssetPrefix = "SuperCalcBenchmark-win-x64";
    private const string AppExeName = "SuperCalcBenchmark.App.exe";

    internal static string ReleasesPageUrl => $"https://github.com/{Owner}/{Repository}/releases";

    [GeneratedRegex(@"^v(\d+\.\d+(\.\d+){0,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex AppReleaseTagRegex();

    internal sealed record ReleaseInfo(
        Version Version,
        string TagName,
        string Title,
        string AssetName,
        string AssetDownloadUrl,
        long AssetSize,
        string HtmlUrl);

    internal sealed record StagedUpdate(string PayloadDirectory, ReleaseInfo Release);

    /// <summary>
    /// True when the app runs out of a git checkout (repo root or bin/ below it);
    /// then updates keep going through git pull instead of release ZIPs.
    /// </summary>
    internal static bool IsRunningFromGitCheckout(params string?[] candidateRoots)
    {
        foreach (var candidate in candidateRoots.Append(AppContext.BaseDirectory))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            for (var directory = new DirectoryInfo(candidate); directory is not null; directory = directory.Parent)
            {
                var gitPath = Path.Combine(directory.FullName, ".git");
                if (Directory.Exists(gitPath) || File.Exists(gitPath))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static Version GetCurrentVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip a "+<commit>" source-revision suffix (SourceLink) before parsing.
            var plusIndex = informational.IndexOf('+');
            var candidate = plusIndex >= 0 ? informational[..plusIndex] : informational;
            if (Version.TryParse(candidate, out var parsed))
            {
                return Normalize(parsed);
            }
        }

        return Normalize(assembly.GetName().Version ?? new Version(0, 0, 0));
    }

    /// <summary>
    /// Returns the newest app release, or null when no release with a matching
    /// version tag and win-x64 ZIP asset exists yet.
    /// </summary>
    internal static async Task<ReleaseInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken)
    {
        using var client = CreateHttpClient(TimeSpan.FromSeconds(30));

        // App releases are marked "latest" (the comparison-page auto-releases are
        // not), so /releases/latest normally answers directly. The list is only a
        // fallback, because frequent comparison releases could push the app
        // release far down the paged list.
        var latest = await TryParseReleaseAsync(client, $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest", cancellationToken);
        if (latest is not null)
        {
            return latest;
        }

        var url = $"https://api.github.com/repos/{Owner}/{Repository}/releases?per_page=100";
        using var response = await client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        ReleaseInfo? best = null;
        foreach (var release in document.RootElement.EnumerateArray())
        {
            var candidate = ParseAppRelease(release);
            if (candidate is not null && (best is null || candidate.Version > best.Version))
            {
                best = candidate;
            }
        }

        return best;
    }

    private static async Task<ReleaseInfo?> TryParseReleaseAsync(HttpClient client, string url, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // 404 = no release marked "latest" yet; the caller falls back to the list.
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.ValueKind == JsonValueKind.Object ? ParseAppRelease(document.RootElement) : null;
    }

    /// <summary>
    /// Returns the release as an app release, or null when it is a draft/prerelease,
    /// its tag is no vX.Y.Z version tag, or it carries no win-x64 ZIP asset
    /// (comparison-page releases fail both of the latter checks).
    /// </summary>
    private static ReleaseInfo? ParseAppRelease(JsonElement release)
    {
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean())
        {
            return null;
        }

        if (release.TryGetProperty("prerelease", out var prerelease) && prerelease.GetBoolean())
        {
            return null;
        }

        var tagName = release.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null;
        if (string.IsNullOrWhiteSpace(tagName))
        {
            return null;
        }

        tagName = tagName.Trim();
        var match = AppReleaseTagRegex().Match(tagName);
        if (!match.Success || !Version.TryParse(match.Groups[1].Value, out var version))
        {
            return null;
        }

        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var asset in assets.EnumerateArray())
        {
            var assetName = asset.TryGetProperty("name", out var name) ? name.GetString() : null;
            var downloadUrl = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(assetName) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            if (!assetName.StartsWith(AssetPrefix, StringComparison.OrdinalIgnoreCase) ||
                !assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = release.TryGetProperty("name", out var releaseName) ? releaseName.GetString() : null;
            var htmlUrl = release.TryGetProperty("html_url", out var html) ? html.GetString() : null;
            var size = asset.TryGetProperty("size", out var sizeProperty) ? sizeProperty.GetInt64() : 0;
            return new ReleaseInfo(
                Normalize(version),
                tagName,
                string.IsNullOrWhiteSpace(title) ? tagName : title,
                assetName,
                downloadUrl,
                size,
                htmlUrl ?? ReleasesPageUrl);
        }

        return null;
    }

    /// <summary>
    /// Downloads the release ZIP and extracts it into a staging directory under
    /// %LOCALAPPDATA%\SuperCalcBenchmark\Updates. Nothing in the app directory
    /// is modified yet.
    /// </summary>
    internal static async Task<StagedUpdate> DownloadAndStageAsync(
        ReleaseInfo release,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var updateRoot = Path.Combine(GetUpdatesRoot(), release.TagName + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(updateRoot);
        var zipPath = Path.Combine(updateRoot, release.AssetName);

        progress?.Report($"Lade {release.AssetName} ({FormatSize(release.AssetSize)}) herunter...");

        using (var client = CreateHttpClient(Timeout.InfiniteTimeSpan))
        using (var response = await client.GetAsync(release.AssetDownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = File.Create(zipPath);

            var buffer = new byte[1 << 16];
            long totalRead = 0;
            long lastReported = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                totalRead += read;
                if (totalRead - lastReported >= 20 * 1024 * 1024)
                {
                    lastReported = totalRead;
                    progress?.Report($"Download: {FormatSize(totalRead)} von {FormatSize(release.AssetSize)}");
                }
            }
        }

        progress?.Report("Download abgeschlossen. Entpacke Update...");

        var payloadDirectory = Path.Combine(updateRoot, "payload");
        ZipFile.ExtractToDirectory(zipPath, payloadDirectory);
        File.Delete(zipPath);

        // Tolerate ZIPs that wrap everything in a single top-level folder.
        var effectivePayload = payloadDirectory;
        if (!File.Exists(Path.Combine(effectivePayload, AppExeName)))
        {
            var subdirectories = Directory.GetDirectories(effectivePayload);
            if (subdirectories.Length == 1 && File.Exists(Path.Combine(subdirectories[0], AppExeName)))
            {
                effectivePayload = subdirectories[0];
            }
        }

        if (!File.Exists(Path.Combine(effectivePayload, AppExeName)))
        {
            throw new InvalidOperationException(
                $"Das heruntergeladene Update enthält keine {AppExeName}. " +
                $"Bitte das Release manuell prüfen: {release.HtmlUrl}");
        }

        progress?.Report("Update vorbereitet: " + effectivePayload);
        return new StagedUpdate(effectivePayload, release);
    }

    /// <summary>
    /// Throws when the app directory is not writable (e.g. extracted into a
    /// protected folder) — better to fail here than in the updater script.
    /// </summary>
    internal static void EnsureApplicationDirectoryWritable()
    {
        var applicationDirectory = AppContext.BaseDirectory;
        var probe = Path.Combine(applicationDirectory, ".update-write-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(probe, "test");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Der App-Ordner ist nicht beschreibbar:\n" + applicationDirectory +
                "\n\nBitte die App in einen normalen Ordner (z. B. Dokumente oder Desktop) verschieben und erneut versuchen.",
                ex);
        }
    }

    /// <summary>
    /// Writes an updater cmd script that waits for this process to exit, copies the
    /// staged files over the app directory (excluding archive/, artifacts/, results/)
    /// and restarts the app. The caller must shut the application down afterwards.
    /// </summary>
    internal static void LaunchUpdaterAndPrepareShutdown(StagedUpdate update)
    {
        var applicationDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var scriptPath = Path.Combine(GetUpdatesRoot(), $"apply-{update.Release.TagName}-{DateTime.Now:yyyyMMdd-HHmmss}.cmd");
        var logPath = scriptPath + ".log";

        var script = new StringBuilder();
        script.AppendLine("@echo off");
        // The script is written as UTF-8 (no BOM); chcp 65001 makes cmd decode the
        // following lines correctly even when paths contain umlauts etc.
        script.AppendLine("chcp 65001 >nul");
        script.AppendLine("setlocal EnableExtensions EnableDelayedExpansion");
        script.AppendLine($"set \"SRC={update.PayloadDirectory}\"");
        script.AppendLine($"set \"DST={applicationDirectory}\"");
        script.AppendLine($"set \"EXE={AppExeName}\"");
        script.AppendLine($"set \"LOG={logPath}\"");
        script.AppendLine("set WAITED=0");
        script.AppendLine(":waitloop");
        script.AppendLine("timeout /t 1 /nobreak >nul");
        // The exe of a running process is write-locked; the append-probe fails until it exits.
        script.AppendLine("2>nul (>>\"%DST%\\%EXE%\" call ;) || (");
        script.AppendLine("  set /a WAITED+=1");
        script.AppendLine("  if !WAITED! GEQ 120 (");
        script.AppendLine("    echo App wurde nicht beendet - Update abgebrochen.>>\"%LOG%\"");
        script.AppendLine("    exit /b 1");
        script.AppendLine("  )");
        script.AppendLine("  goto waitloop");
        script.AppendLine(")");
        script.AppendLine("robocopy \"%SRC%\" \"%DST%\" /E /R:10 /W:1 /XD archive artifacts results >>\"%LOG%\" 2>&1");
        script.AppendLine("if %ERRORLEVEL% GEQ 8 (");
        script.AppendLine("  echo Robocopy meldete Fehler %ERRORLEVEL%.>>\"%LOG%\"");
        script.AppendLine("  exit /b 1");
        script.AppendLine(")");
        script.AppendLine("start \"\" \"%DST%\\%EXE%\"");
        script.AppendLine("(goto) 2>nul & del \"%~f0\"");

        File.WriteAllText(scriptPath, script.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            ArgumentList = { "/c", scriptPath },
            WorkingDirectory = GetUpdatesRoot(),
            UseShellExecute = false,
            CreateNoWindow = true
        };

        _ = System.Diagnostics.Process.Start(startInfo)
            ?? throw new InvalidOperationException("Der Updater-Prozess konnte nicht gestartet werden.");
    }

    internal static string FormatSize(long bytes)
    {
        if (bytes <= 0)
        {
            return "unbekannter Größe";
        }

        return bytes switch
        {
            >= 1 << 30 => $"{bytes / (double)(1 << 30):0.0} GB",
            >= 1 << 20 => $"{bytes / (double)(1 << 20):0.0} MB",
            >= 1 << 10 => $"{bytes / (double)(1 << 10):0.0} KB",
            _ => $"{bytes} B"
        };
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static HttpClient CreateHttpClient(TimeSpan timeout)
    {
        var client = new HttpClient { Timeout = timeout };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SuperCalcBenchmark", GetCurrentVersion().ToString()));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static string GetUpdatesRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(
            string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData,
            "SuperCalcBenchmark",
            "Updates");
        Directory.CreateDirectory(root);
        return root;
    }
}
