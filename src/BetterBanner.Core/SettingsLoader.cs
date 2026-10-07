using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterBanner.Core;

/// <summary>A source of flat policy values (see <see cref="SettingKeys"/>), e.g. the Group Policy registry key.</summary>
public interface IPolicySource
{
    /// <summary>Returns configured values only. Missing values mean "not configured".</summary>
    IReadOnlyDictionary<string, string> ReadValues();
}

public sealed record LoadedSettings(BannerSettings Settings, IReadOnlyList<string> Warnings, bool PolicyApplied);

public static class SettingsLoader
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>%ProgramData%\BetterBanner\settings.json</summary>
    public static string DefaultSettingsPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BetterBanner", "settings.json");

    /// <summary>Defaults, then the machine settings file, then policy. A broken layer is reported and skipped.</summary>
    public static LoadedSettings Load(string? settingsPath, IPolicySource? policy)
    {
        var warnings = new List<string>();
        var settings = new BannerSettings();

        if (settingsPath is not null && File.Exists(settingsPath))
        {
            try
            {
                settings = Parse(File.ReadAllText(settingsPath));
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                warnings.Add($"Ignoring settings file '{settingsPath}': {ex.Message}");
                settings = new BannerSettings();
            }
        }

        var policyApplied = false;
        if (policy is not null)
        {
            try
            {
                var values = policy.ReadValues();
                if (values.Count > 0)
                {
                    policyApplied = true;
                    foreach (var name in SettingKeys.ApplyAll(settings, values!))
                        warnings.Add($"Ignoring invalid or unknown policy value '{name}'.");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add($"Could not read policy: {ex.Message}");
            }
        }

        settings.Normalize();
        return new LoadedSettings(settings, warnings, policyApplied);
    }

    public static BannerSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize<BannerSettings>(json, JsonOptions) ?? new BannerSettings();
        settings.Normalize();
        return settings;
    }

    public static string Serialize(BannerSettings settings) => JsonSerializer.Serialize(settings, JsonOptions);

    /// <summary>Writes atomically so a running banner never reads a half-written file.</summary>
    public static void Save(BannerSettings settings, string path)
    {
        settings.Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, Serialize(settings));
        File.Move(temp, path, overwrite: true);
    }
}
