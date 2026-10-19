namespace ClassBanner.Core;

/// <summary>Configured policy values by setting name (see <see cref="SettingKeys"/>). Missing means not configured.</summary>
public interface IPolicySource
{
    IReadOnlyDictionary<string, string> ReadValues();
}

public sealed record LoadedSettings(BannerSettings Settings, IReadOnlyList<string> Warnings, bool FromPolicy);

/// <summary>The settings file on its own. <see cref="Unreadable"/> means it exists but isn't usable JSON at all.</summary>
public sealed record SettingsFile(BannerSettings Settings, IReadOnlyList<string> Warnings, bool Exists, bool Unreadable);

public static class SettingsLoader
{
    /// <summary>
    /// %ProgramFiles%\ClassBanner, next to the program. Standard users can't create or change anything under
    /// Program Files, so they can't plant or edit the marking (unlike ProgramData, where any user can create folders).
    /// </summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ClassBanner");

    public static string DefaultSettingsPath => Path.Combine(DefaultDirectory, "settings.json");

    /// <summary>Defaults, then the settings file, then policy. A broken layer is reported and skipped, never fatal.</summary>
    public static LoadedSettings Load(string? settingsPath, IPolicySource? policy)
    {
        var file = LoadFile(settingsPath);
        var warnings = new List<string>(file.Warnings);
        var settings = file.Settings;

        var fromPolicy = false;
        IReadOnlyDictionary<string, string> values = new Dictionary<string, string>();
        if (policy is not null)
        {
            try
            {
                values = policy.ReadValues();
                fromPolicy = values.Count > 0;
                foreach (var name in SettingKeys.ApplyAll(settings, values))
                {
                    if (name.Equals(nameof(BannerSettings.Level), StringComparison.OrdinalIgnoreCase))
                    {
                        // Group Policy means to set the level; a broken GPO must not be hidden behind the local level.
                        settings.Level = null;
                        warnings.Add($"Group Policy sets an invalid Level '{values[name]}'; the banner shows CLASSIFICATION NOT SET until the policy is fixed.");
                    }
                    else
                    {
                        warnings.Add($"Ignoring invalid policy value '{name}'.");
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                warnings.Add($"Could not read Group Policy settings: {ex.Message}");
            }
        }

        // Never fall back to a marking nobody chose: an unknown level shows "CLASSIFICATION NOT SET" instead.
        if (settings.LevelNotConfigured) warnings.Add("No valid classification level is configured; the banner shows CLASSIFICATION NOT SET.");
        CheckMarking(settings, values, warnings);

        settings.Normalize();
        return new LoadedSettings(settings, warnings, fromPolicy);
    }

    /// <summary>
    /// A caption that contradicts the level is dropped (the standard marking is shown instead), wherever it came from.
    /// Custom colors that differ from the level's standard colors are kept but reported.
    /// </summary>
    private static void CheckMarking(BannerSettings settings, IReadOnlyDictionary<string, string> policy, List<string> warnings)
    {
        if (settings.Level is not { } level) return;
        string Source(string name) => policy.ContainsKey(name) ? "Group Policy" : "the settings file";

        if (ClassificationPresets.CaptionProblem(level, settings.Caption) is { } problem)
        {
            warnings.Add($"Ignoring Caption from {Source(nameof(BannerSettings.Caption))}. {problem} The banner shows {settings.Preset.Caption}.");
            settings.Caption = null;
        }
        if (RgbColor.TryParse(settings.BackColor, out var back) && back != settings.Preset.Background)
            warnings.Add($"BackColor {back} (from {Source(nameof(BannerSettings.BackColor))}) is not the standard {settings.Preset.Caption} color {settings.Preset.Background}.");
        if (RgbColor.TryParse(settings.ForeColor, out var fore) && fore != settings.Preset.Foreground)
            warnings.Add($"ForeColor {fore} (from {Source(nameof(BannerSettings.ForeColor))}) is not the standard {settings.Preset.Caption} text color {settings.Preset.Foreground}.");
    }

    /// <summary>The settings file alone (what <c>config</c> edits), without policy. Bad values are skipped and reported.</summary>
    public static SettingsFile LoadFile(string? path)
    {
        if (path is null || !File.Exists(path)) return new SettingsFile(new BannerSettings(), [], Exists: false, Unreadable: false);
        try
        {
            var parsed = SettingsSerializer.ParseLenient(File.ReadAllText(path));
            var warnings = parsed.Rejected.Select(r => $"Ignoring invalid setting in '{path}': {r}").ToList();
            return new SettingsFile(parsed.Settings, warnings, Exists: true, Unreadable: false);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or UnauthorizedAccessException)
        {
            return new SettingsFile(new BannerSettings(), [$"Could not read settings file '{path}': {ex.Message}"], Exists: true, Unreadable: true);
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
