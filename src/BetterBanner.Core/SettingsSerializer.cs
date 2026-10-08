using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BetterBanner.Core;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(BannerSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>The result of reading settings JSON: every value that could be applied, plus what was skipped.</summary>
public sealed record ParsedSettings(BannerSettings Settings, IReadOnlyList<string> Rejected, bool LevelSet);

/// <summary>Reads and writes settings.json. Writing uses source generation so it works in the native (AOT) build.</summary>
public static class SettingsSerializer
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Reads settings value by value through <see cref="SettingKeys"/>, the same rules Group Policy and the command line
    /// use, so "Level": "S" works and one bad value never discards the rest of the file.
    /// Throws <see cref="JsonException"/> only when the text is not a JSON object at all.
    /// </summary>
    public static ParsedSettings ParseLenient(string json)
    {
        using var document = JsonDocument.Parse(json, DocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Settings must be a JSON object, e.g. { \"Level\": \"Secret\" }.");

        var settings = new BannerSettings();
        var rejected = new List<string>();
        var levelSet = false;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            var value = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString(),
                JsonValueKind.Number => property.Value.GetRawText(),
                JsonValueKind.True => "1",
                JsonValueKind.False => "0",
                JsonValueKind.Null => "",
                _ => null,
            };
            if (value is not null && SettingKeys.TryApply(settings, property.Name, value))
                levelSet |= string.Equals(property.Name, nameof(BannerSettings.Level), StringComparison.OrdinalIgnoreCase);
            else
                rejected.Add($"{property.Name} = {Describe(property.Value)}");
        }

        settings.Normalize();
        return new ParsedSettings(settings, rejected, levelSet);
    }

    /// <summary>Lenient parse that ignores rejected values (see <see cref="ParseLenient"/>).</summary>
    public static BannerSettings Parse(string json) => ParseLenient(json).Settings;

    public static string Serialize(BannerSettings settings) =>
        JsonSerializer.Serialize(settings, SettingsJsonContext.Default.BannerSettings);

    private static string Describe(JsonElement value)
    {
        var text = value.GetRawText();
        return text.Length <= 60 ? text : string.Concat(text.AsSpan(0, 57), "...");
    }
}
