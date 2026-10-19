using System.Globalization;

namespace BetterBanner.Core;

/// <summary>
/// Setting names shared by Group Policy (value names under HKLM\SOFTWARE\Policies\BetterBanner) and the command line
/// (<c>--set Name=Value</c>). Names match <see cref="BannerSettings"/> properties. Keep in sync with policy/BetterBanner.admx.
/// </summary>
public static class SettingKeys
{
    private static readonly Dictionary<string, Func<BannerSettings, string, bool>> Appliers =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [nameof(BannerSettings.Level)] = (s, v) => ClassificationPresets.TryParseLevel(v, out var l) && Set(() => s.Level = l),
            [nameof(BannerSettings.Caption)] = (s, v) => Set(() => s.Caption = NullIfBlank(v)),
            [nameof(BannerSettings.BackColor)] = (s, v) => TryColor(v, out var c) && Set(() => s.BackColor = c),
            [nameof(BannerSettings.ForeColor)] = (s, v) => TryColor(v, out var c) && Set(() => s.ForeColor = c),
            [nameof(BannerSettings.Position)] = (s, v) => TryEnum<BannerPosition>(v, out var p) && Set(() => s.Position = p),
            [nameof(BannerSettings.FontSize)] = (s, v) => TryInt(v, 8, 36, out var i) && Set(() => s.FontSize = i),
            [nameof(BannerSettings.BannerThickness)] = (s, v) =>
                TryInt(v, 0, BannerLayout.MaxThickness, out var i) && (i == 0 || i >= BannerLayout.MinThickness) && Set(() => s.BannerThickness = i),
            [nameof(BannerSettings.HoverTransparency)] = (s, v) => TryInt(v, 0, 90, out var i) && Set(() => s.HoverTransparency = i),
            [nameof(BannerSettings.StayAboveFullScreenApps)] = (s, v) => TryBool(v, out var b) && Set(() => s.StayAboveFullScreenApps = b),
            [nameof(BannerSettings.AllowUserExit)] = (s, v) => TryBool(v, out var b) && Set(() => s.AllowUserExit = b),
            [nameof(BannerSettings.ClockEnabled)] = (s, v) => TryBool(v, out var b) && Set(() => s.ClockEnabled = b),
            [nameof(BannerSettings.ClockFormat)] = (s, v) => BannerText.IsValidClockFormat(v) && Set(() => s.ClockFormat = v),
            [nameof(BannerSettings.ClockUseZulu)] = (s, v) => TryBool(v, out var b) && Set(() => s.ClockUseZulu = b),
            [nameof(BannerSettings.ClockUppercase)] = (s, v) => TryBool(v, out var b) && Set(() => s.ClockUppercase = b),
            [nameof(BannerSettings.EnforceColors)] = (s, v) => TryBool(v, out var b) && Set(() => s.EnforceColors = b),
            [nameof(BannerSettings.WallpaperMode)] = (s, v) => TryEnum<WallpaperMode>(v, out var m) && Set(() => s.WallpaperMode = m),
            [nameof(BannerSettings.WallpaperImagePath)] = (s, v) => Set(() => s.WallpaperImagePath = NullIfBlank(v)),
            [nameof(BannerSettings.EnforceScreenSaver)] = (s, v) => TryBool(v, out var b) && Set(() => s.EnforceScreenSaver = b),
            [nameof(BannerSettings.ScreenSaverTimeoutSeconds)] = (s, v) => TryInt(v, 60, 7200, out var i) && Set(() => s.ScreenSaverTimeoutSeconds = i),
            [nameof(BannerSettings.ScreenSaverRequirePassword)] = (s, v) => TryBool(v, out var b) && Set(() => s.ScreenSaverRequirePassword = b),
            [nameof(BannerSettings.LogonNoticeTitle)] = (s, v) => Set(() => s.LogonNoticeTitle = NullIfBlank(v)),
            [nameof(BannerSettings.LogonNoticeText)] = (s, v) => Set(() => s.LogonNoticeText = NullIfBlank(v)),
        };

    public static IReadOnlyCollection<string> Names => Appliers.Keys;

    /// <summary>Applies one value. Returns false, leaving settings untouched, for unknown names or invalid values.</summary>
    public static bool TryApply(BannerSettings settings, string name, string value) =>
        Appliers.TryGetValue(name, out var apply) && apply(settings, value);

    /// <summary>Applies values in order, then normalizes. Returns the names that were rejected.</summary>
    public static IReadOnlyList<string> ApplyAll(BannerSettings settings, IEnumerable<KeyValuePair<string, string>> values)
    {
        var rejected = new List<string>();
        foreach (var (name, value) in values)
        {
            if (!TryApply(settings, name, value)) rejected.Add(name);
        }
        settings.Normalize();
        return rejected;
    }

    public static bool TryBool(string v, out bool result)
    {
        switch (v.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on": result = true; return true;
            case "0": case "false": case "no": case "off": result = false; return true;
            default: result = false; return false;
        }
    }

    private static bool Set(Action apply)
    {
        apply();
        return true;
    }

    /// <summary>Out-of-range numbers are rejected (and reported) rather than silently clamped.</summary>
    private static bool TryInt(string v, int min, int max, out int i) =>
        int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out i) && i >= min && i <= max;

    private static bool TryEnum<T>(string v, out T value) where T : struct, Enum =>
        Enum.TryParse(v.Trim(), ignoreCase: true, out value) && Enum.IsDefined(value);

    /// <summary>Blank clears the override (back to the level's standard color).</summary>
    private static bool TryColor(string v, out string? color)
    {
        color = null;
        if (string.IsNullOrWhiteSpace(v)) return true;
        if (!RgbColor.TryParse(v, out var c)) return false;
        color = c.ToString();
        return true;
    }

    private static string? NullIfBlank(string v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
