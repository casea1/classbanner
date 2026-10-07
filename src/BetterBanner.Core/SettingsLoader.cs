namespace BetterBanner.Core;

/// <summary>Configured policy values by setting name (see <see cref="SettingKeys"/>). Missing means not configured.</summary>
public interface IPolicySource
{
    IReadOnlyDictionary<string, string> ReadValues();
}

public sealed record LoadedSettings(BannerSettings Settings, IReadOnlyList<string> Warnings, bool FromPolicy);

public static class SettingsLoader
{
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterBanner");

    public static string DefaultSettingsPath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Defaults, then the settings file, then policy. A broken layer is reported and skipped, never fatal.</summary>
    public static LoadedSettings Load(string? settingsPath, IPolicySource? policy)
    {
        var warnings = new List<string>();
        var settings = LoadFile(settingsPath, warnings);

        var fromPolicy = false;
        if (policy is not null)
        {
            try
            {
                var values = policy.ReadValues();
                fromPolicy = values.Count > 0;
                foreach (var name in SettingKeys.ApplyAll(settings, values))
                    warnings.Add($"Ignoring invalid policy value '{name}'.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add($"Could not read Group Policy settings: {ex.Message}");
            }
        }

        settings.Normalize();
        return new LoadedSettings(settings, warnings, fromPolicy);
    }

    /// <summary>The settings file alone (what <c>config</c> edits), without policy.</summary>
    public static BannerSettings LoadFile(string? path, List<string>? warnings = null)
    {
        if (path is null || !File.Exists(path)) return new BannerSettings();
        try
        {
            return SettingsSerializer.Parse(File.ReadAllText(path));
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            warnings?.Add($"Ignoring settings file '{path}': {ex.Message}");
            return new BannerSettings();
        }
    }

    /// <summary>Writes atomically so a running banner never reads a half-written file.</summary>
    public static void Save(BannerSettings settings, string path)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, SettingsSerializer.Serialize(settings));
        File.Move(temp, path, overwrite: true);
    }
}
