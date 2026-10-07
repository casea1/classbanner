using System.Globalization;

namespace BetterBanner.Core;

/// <summary>
/// Flat setting names shared by Group Policy (registry value names under HKLM\SOFTWARE\Policies\BetterBanner)
/// and the command line (<c>--set Name=Value</c>). Keep in sync with policy/BetterBanner.admx.
/// </summary>
public static class SettingKeys
{
    private sealed record Key(string Name, Func<BannerSettings, string, bool> Apply);

    private static readonly Key[] Keys =
    [
        new("Level", (s, v) => Set(ClassificationPresets.TryParseLevel(v, out var l), () => s.Level = l)),
        new("Caption", (s, v) => { s.Caption = NullIfEmpty(v); return true; }),
        new("BackColor", (s, v) => SetColor(v, c => s.BackColor = c)),
        new("ForeColor", (s, v) => SetColor(v, c => s.ForeColor = c)),
        new("Position", (s, v) => SetEnum<BannerPosition>(v, p => s.Position = p)),
        new("FontSize", (s, v) => SetInt(v, i => s.FontSize = i)),
        new("ExpansionDelimiter", (s, v) => { s.ExpansionDelimiter = v; return true; }),
        new("Transparency", (s, v) => SetInt(v, i => s.Transparency = i)),
        new("HoverTransparency", (s, v) => SetInt(v, i => s.HoverTransparency = i)),
        new("StayAboveFullScreenApps", (s, v) => SetBool(v, b => s.StayAboveFullScreenApps = b)),
        new("AllowUserExit", (s, v) => SetBool(v, b => s.AllowUserExit = b)),

        new("ClockEnabled", (s, v) => SetBool(v, b => s.Clock.Enabled = b)),
        new("ClockFormat", (s, v) => { if (string.IsNullOrWhiteSpace(v)) return false; s.Clock.Format = v; return true; }),
        new("ClockUseZulu", (s, v) => SetBool(v, b => s.Clock.UseZulu = b)),
        new("ClockLocalIndicator", (s, v) => { s.Clock.LocalIndicator = v; return true; }),
        new("ClockZuluIndicator", (s, v) => { s.Clock.ZuluIndicator = v; return true; }),
        new("ClockUppercase", (s, v) => SetBool(v, b => s.Clock.Uppercase = b)),

        new("CascadeRecoveredWindows", (s, v) => SetBool(v, b => s.WindowRecovery.CascadeRecoveredWindows = b)),
        new("IgnoredWindowClasses", (s, v) => { s.WindowRecovery.IgnoredWindowClasses = v; return true; }),
        new("DragCoveredWindowSeconds", (s, v) => SetInt(v, i => s.WindowRecovery.DragCoveredWindowSeconds = i)),

        new("EnforceColors", (s, v) => SetBool(v, b => s.Colors.Enforce = b)),

        new("WallpaperMode", (s, v) => SetEnum<WallpaperMode>(v, m => s.Wallpaper.Mode = m)),
        new("WallpaperImagePath", (s, v) => { s.Wallpaper.ImagePath = NullIfEmpty(v); return true; }),
        new("WallpaperStyle", (s, v) => SetEnum<WallpaperStyle>(v, w => s.Wallpaper.Style = w)),

        new("EnforceScreenSaver", (s, v) => SetBool(v, b => s.ScreenSaver.Enforce = b)),
        new("ScreenSaverTimeoutSeconds", (s, v) => SetInt(v, i => s.ScreenSaver.TimeoutSeconds = i)),
        new("ScreenSaverRequirePassword", (s, v) => SetBool(v, b => s.ScreenSaver.RequirePassword = b)),
        new("ScreenSaverSpeed", (s, v) => SetInt(v, i => s.ScreenSaver.Speed = i)),

        new("LoggingEnabled", (s, v) => SetBool(v, b => s.Logging.Enabled = b)),
        new("LogFilePath", (s, v) => { s.Logging.FilePath = NullIfEmpty(v); return true; }),
    ];

    private static readonly Dictionary<string, Key> ByName =
        Keys.ToDictionary(k => k.Name, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<string> Names { get; } = Keys.Select(k => k.Name).ToArray();

    public static bool IsKnown(string name) => ByName.ContainsKey(name);

    /// <summary>Applies one value. Returns false (leaving settings untouched) for unknown names or unparseable values.</summary>
    public static bool TryApply(BannerSettings settings, string name, string? value) =>
        value is not null && ByName.TryGetValue(name, out var key) && key.Apply(settings, value);

    /// <summary>Applies a set of values; returns the names that were rejected.</summary>
    public static IReadOnlyList<string> ApplyAll(BannerSettings settings, IEnumerable<KeyValuePair<string, string?>> values)
    {
        var rejected = new List<string>();
        foreach (var (name, value) in values)
        {
            if (!TryApply(settings, name, value)) rejected.Add(name);
        }
        settings.Normalize();
        return rejected;
    }

    internal static bool TryParseBool(string v, out bool result)
    {
        switch (v.Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "on": result = true; return true;
            case "0": case "false": case "no": case "off": result = false; return true;
            default: result = false; return false;
        }
    }

    private static bool Set(bool ok, Action apply)
    {
        if (ok) apply();
        return ok;
    }

    private static bool SetBool(string v, Action<bool> apply) => Set(TryParseBool(v, out var b), () => apply(b));

    private static bool SetInt(string v, Action<int> apply) =>
        Set(int.TryParse(v.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var i), () => apply(i));

    private static bool SetEnum<T>(string v, Action<T> apply) where T : struct, Enum
    {
        var t = v.Trim();
        // Numeric values must be defined members; Enum.TryParse would accept any number.
        var ok = Enum.TryParse<T>(t, ignoreCase: true, out var e) && Enum.IsDefined(e);
        return Set(ok, () => apply(e));
    }

    private static bool SetColor(string v, Action<string?> apply)
    {
        if (string.IsNullOrWhiteSpace(v))
        {
            apply(null);
            return true;
        }
        return Set(RgbColor.TryParse(v, out var c), () => apply(c.ToString()));
    }

    private static string? NullIfEmpty(string v) => string.IsNullOrWhiteSpace(v) ? null : v;
}
