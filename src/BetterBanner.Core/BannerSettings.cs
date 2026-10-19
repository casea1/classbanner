using System.Text.Json.Serialization;

namespace BetterBanner.Core;

public enum BannerPosition
{
    Top = 1,
    Bottom = 2,
    Both = 3,
}

public enum WallpaperMode
{
    /// <summary>Leave the user's wallpaper alone.</summary>
    Off,

    /// <summary>Solid classification color with the marking in the middle.</summary>
    Generated,

    /// <summary>An administrator-supplied image.</summary>
    Image,
}

/// <summary>
/// Better Banner settings. Effective settings are the built-in defaults, overlaid by the machine settings file
/// (%ProgramFiles%\BetterBanner\settings.json), overlaid by Group Policy (HKLM\SOFTWARE\Policies\BetterBanner).
/// </summary>
public sealed class BannerSettings
{
    /// <summary>
    /// The classification. Null means not configured: the banner then shows <see cref="NotConfiguredCaption"/>, and
    /// nothing ever saves a level nobody chose (null is simply left out of settings.json).
    /// </summary>
    public ClassificationLevel? Level { get; set; }

    /// <summary>Replaces the level's default text, e.g. "SECRET//NOFORN". Null or empty uses the standard marking.</summary>
    public string? Caption { get; set; }

    /// <summary>Overrides the level's banner color (#RRGGBB).</summary>
    public string? BackColor { get; set; }

    /// <summary>Overrides the level's text color (#RRGGBB).</summary>
    public string? ForeColor { get; set; }

    public BannerPosition Position { get; set; } = BannerPosition.Both;

    /// <summary>Banner text size in points (8-36). Scaled for each monitor's DPI.</summary>
    public int FontSize { get; set; } = 10;

    /// <summary>Banner thickness in pixels at 100% scaling (12-120), scaled for each monitor. 0 = automatic (fits the text).</summary>
    public int BannerThickness { get; set; }

    /// <summary>How see-through the banner gets while the mouse is over it, in percent (0-90).</summary>
    public int HoverTransparency { get; set; } = 40;

    /// <summary>Keep the banner above full-screen apps (presentations, video) so the marking is never hidden.</summary>
    public bool StayAboveFullScreenApps { get; set; } = true;

    /// <summary>Show "Exit" on the banner menu.</summary>
    public bool AllowUserExit { get; set; }

    public bool ClockEnabled { get; set; }

    /// <summary>.NET custom date/time format. %TZI% becomes "Z" (Zulu) or "L" (local).</summary>
    public string ClockFormat { get; set; } = BannerText.DefaultClockFormat;

    public bool ClockUseZulu { get; set; }

    /// <summary>Upper-case the clock text (military style, e.g. 071830Z OCT 26).</summary>
    public bool ClockUppercase { get; set; }

    /// <summary>Color title bars, accent and selection highlight to match the classification.</summary>
    public bool EnforceColors { get; set; } = true;

    public WallpaperMode WallpaperMode { get; set; } = WallpaperMode.Off;

    public string? WallpaperImagePath { get; set; }

    /// <summary>Set the Better Banner classification screen saver for every user.</summary>
    public bool EnforceScreenSaver { get; set; }

    public int ScreenSaverTimeoutSeconds { get; set; } = 600;

    public bool ScreenSaverRequirePassword { get; set; } = true;

    /// <summary>
    /// Title of the Windows sign-in notice users must acknowledge before logging on. Applied by
    /// <c>install</c>/<c>config</c> (machine-wide). Empty title and text means no notice.
    /// </summary>
    public string? LogonNoticeTitle { get; set; }

    public string? LogonNoticeText { get; set; }

    public const string NotConfiguredCaption = "CLASSIFICATION NOT SET - CONTACT YOUR ADMINISTRATOR";
    public static readonly RgbColor NotConfiguredBackColor = RgbColor.Parse("#4D4D4D");

    /// <summary>No valid level from the settings file or Group Policy.</summary>
    [JsonIgnore]
    public bool LevelNotConfigured => Level is null;

    /// <summary>The level's standard marking and colors. Only meaningful when <see cref="Level"/> is set.</summary>
    [JsonIgnore]
    public ClassificationPreset Preset => ClassificationPresets.Get(Level ?? ClassificationLevel.Unclassified);

    [JsonIgnore]
    public string EffectiveCaption => LevelNotConfigured ? NotConfiguredCaption
        : string.IsNullOrWhiteSpace(Caption) ? Preset.Caption : Caption.Trim();

    [JsonIgnore]
    public RgbColor EffectiveBackColor => LevelNotConfigured ? NotConfiguredBackColor
        : RgbColor.TryParse(BackColor, out var c) ? c : Preset.Background;

    [JsonIgnore]
    public RgbColor EffectiveForeColor => LevelNotConfigured ? RgbColor.White
        : RgbColor.TryParse(ForeColor, out var c) ? c : Preset.Foreground;

    [JsonIgnore]
    public bool HasLogonNotice => !string.IsNullOrWhiteSpace(LogonNoticeTitle) || !string.IsNullOrWhiteSpace(LogonNoticeText);

    /// <summary>Clamps out-of-range values so bad policy or file data can never break the banner.</summary>
    public void Normalize()
    {
        if (Level is { } level && !Enum.IsDefined(level)) Level = null;
        if (!Enum.IsDefined(Position)) Position = BannerPosition.Both;
        if (!Enum.IsDefined(WallpaperMode)) WallpaperMode = WallpaperMode.Off;
        // Values are range-checked when applied; this is a last line of defence for code that sets them directly.
        FontSize = Math.Clamp(FontSize, 8, 36);
        if (BannerThickness != 0) BannerThickness = Math.Clamp(BannerThickness, BannerLayout.MinThickness, BannerLayout.MaxThickness);
        HoverTransparency = Math.Clamp(HoverTransparency, 0, 90);
        ScreenSaverTimeoutSeconds = Math.Clamp(ScreenSaverTimeoutSeconds, 60, 7200);
        if (!BannerText.IsValidClockFormat(ClockFormat)) ClockFormat = BannerText.DefaultClockFormat;
    }

    public BannerSettings Clone() => SettingsSerializer.Parse(SettingsSerializer.Serialize(this));
}
