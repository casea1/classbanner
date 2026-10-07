using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterBanner.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BannerSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>Reads and writes settings.json. Uses source generation so it works in the native (AOT) build.</summary>
public static class SettingsSerializer
{
    public static BannerSettings Parse(string json)
    {
        var settings = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.BannerSettings) ?? new BannerSettings();
        settings.Normalize();
        return settings;
    }

    public static string Serialize(BannerSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.BannerSettings);
}
