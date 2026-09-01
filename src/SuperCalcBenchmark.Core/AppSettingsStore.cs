using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuperCalcBenchmark.Core;

public enum ThemePreference
{
    System,
    Light,
    Dark
}

public sealed class AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public ThemePreference Theme { get; init; } = ThemePreference.System;
}

/// <summary>
/// Loads and atomically persists small per-user UI preferences. Invalid or future
/// settings never block startup; callers receive safe defaults instead.
/// </summary>
public sealed class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly string _path;

    public AppSettingsStore(string? path = null)
    {
        _path = Path.GetFullPath(string.IsNullOrWhiteSpace(path) ? DefaultFilePath : path);
    }

    public string FilePath => _path;

    public static string DefaultFilePath =>
        Path.Combine(BenchmarkPathResolver.ResolveDataRoot(), "app-settings.json");

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path), JsonOptions);
            if (settings is null
                || settings.SchemaVersion <= 0
                || settings.SchemaVersion > AppSettings.CurrentSchemaVersion
                || !Enum.IsDefined(settings.Theme))
            {
                return new AppSettings();
            }

            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
        catch (IOException)
        {
            return new AppSettings();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppSettings();
        }
    }

    public bool TrySave(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = Path.GetDirectoryName(_path);
        var temporaryPath = _path + $".tmp-{Guid.NewGuid():N}";
        try
        {
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(temporaryPath, _path, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
                // Best-effort cleanup only. A stale temp file must not block shutdown.
            }
        }
    }
}
