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

    /// <summary>Generate a solid wallpaper in the classification color with the marking in the middle.</summary>
    Generated,

    /// <summary>Use an administrator-supplied image.</summary>
    Image,
}

public enum WallpaperStyle
{
    Center,
    Tile,
    Stretch,
    Fit,
    Fill,
}

/// <summary>
/// All Better Banner settings. Loaded from built-in defaults, then the machine settings file
/// (%ProgramData%\BetterBanner\settings.json), then Group Policy (HKLM\SOFTWARE\Policies\BetterBanner), last one wins.
/// </summary>
public sealed class BannerSettings
{
    public ClassificationLevel Level { get; set; } = ClassificationLevel.Unclassified;

    /// <summary>Overrides the level's default caption, e.g. "SECRET//NOFORN". Null uses the preset.</summary>
    public string? Caption { get; set; }

    /// <summary>Overrides the level's banner color (#RRGGBB). Null uses the preset.</summary>
    public string? BackColor { get; set; }

    /// <summary>Overrides the level's text color (#RRGGBB). Null uses the preset.</summary>
    public string? ForeColor { get; set; }

    public BannerPosition Position { get; set; } = BannerPosition.Both;

    /// <summary>Caption font size in points (8-36). 0 means follow the user's title bar font.</summary>
    public int FontSize { get; set; }

    /// <summary>Inserted between caption characters, e.g. " " turns SECRET into "S E C R E T". Empty disables.</summary>
    public string ExpansionDelimiter { get; set; } = "";

    /// <summary>Constant banner transparency in percent (0-50).</summary>
    public int Transparency { get; set; }

    /// <summary>Transparency in percent (0-90) while the mouse is over the banner, so content underneath can be read.</summary>
    public int HoverTransparency { get; set; } = 40;

    /// <summary>Keep the banner above full-screen applications (presentations, video) so the marking is never hidden.</summary>
    public bool StayAboveFullScreenApps { get; set; } = true;

    /// <summary>Allow users to close the banner from its menu. Off by default.</summary>
    public bool AllowUserExit { get; set; }

    public ClockSettings Clock { get; set; } = new();

    public WindowRecoverySettings WindowRecovery { get; set; } = new();

    public ColorPolicySettings Colors { get; set; } = new();

    public WallpaperSettings Wallpaper { get; set; } = new();

    public ScreenSaverSettings ScreenSaver { get; set; } = new();

    public LoggingSettings Logging { get; set; } = new();

    [JsonIgnore]
    public ClassificationPreset Preset => ClassificationPresets.Get(Level);

    [JsonIgnore]
    public string EffectiveCaption => string.IsNullOrWhiteSpace(Caption) ? Preset.Caption : Caption.Trim();

    [JsonIgnore]
    public RgbColor EffectiveBackColor => RgbColor.TryParse(BackColor, out var c) ? c : Preset.Background;

    [JsonIgnore]
    public RgbColor EffectiveForeColor => RgbColor.TryParse(ForeColor, out var c) ? c : Preset.Foreground;

    /// <summary>Clamps out-of-range values so bad policy data can never break the banner.</summary>
    public void Normalize()
    {
        if (!Enum.IsDefined(Level)) Level = ClassificationLevel.Unclassified;
        if (!Enum.IsDefined(Position)) Position = BannerPosition.Both;
        if (FontSize != 0) FontSize = Math.Clamp(FontSize, 8, 36);
        Transparency = Math.Clamp(Transparency, 0, 50);
        HoverTransparency = Math.Clamp(HoverTransparency, 0, 90);
        ExpansionDelimiter ??= "";
        Clock ??= new();
        WindowRecovery ??= new();
        Colors ??= new();
        Wallpaper ??= new();
        ScreenSaver ??= new();
        Logging ??= new();
        WindowRecovery.DragCoveredWindowSeconds = Math.Clamp(WindowRecovery.DragCoveredWindowSeconds, 3, 60);
        ScreenSaver.TimeoutSeconds = Math.Clamp(ScreenSaver.TimeoutSeconds, 60, 7200);
        ScreenSaver.Speed = Math.Clamp(ScreenSaver.Speed, 1, 30);
        if (!Enum.IsDefined(Wallpaper.Mode)) Wallpaper.Mode = WallpaperMode.Off;
        if (!Enum.IsDefined(Wallpaper.Style)) Wallpaper.Style = WallpaperStyle.Fill;
    }
}

public sealed class ClockSettings
{
    public bool Enabled { get; set; }

    /// <summary>.NET date/time format. %TZI% is replaced by the local or Zulu indicator.</summary>
    public string Format { get; set; } = "ddd, dd-MMM-yy HH:mm %TZI%";

    /// <summary>Show UTC (Zulu) time instead of local time.</summary>
    public bool UseZulu { get; set; }

    public string LocalIndicator { get; set; } = "L";

    public string ZuluIndicator { get; set; } = "Z";

    /// <summary>Upper-case the result (military style: 071830Z OCT 26).</summary>
    public bool Uppercase { get; set; }
}

public sealed class WindowRecoverySettings
{
    /// <summary>Cascade windows pulled out from under the banner instead of stacking them.</summary>
    public bool CascadeRecoveredWindows { get; set; } = true;

    /// <summary>Extra window classes (semicolon separated) that recovery must never move.</summary>
    public string IgnoredWindowClasses { get; set; } = "";

    /// <summary>How long "Drag Covered Window" lets clicks pass through the banner.</summary>
    public int DragCoveredWindowSeconds { get; set; } = 8;
}

public sealed class ColorPolicySettings
{
    /// <summary>Color title bars, accent and selection highlight to match the classification at every logon.</summary>
    public bool Enforce { get; set; } = true;
}

public sealed class WallpaperSettings
{
    public WallpaperMode Mode { get; set; } = WallpaperMode.Off;

    /// <summary>Image file used when Mode is Image.</summary>
    public string? ImagePath { get; set; }

    public WallpaperStyle Style { get; set; } = WallpaperStyle.Fill;
}

public sealed class ScreenSaverSettings
{
    /// <summary>Set the Better Banner classification screen saver for every user at logon.</summary>
    public bool Enforce { get; set; }

    public int TimeoutSeconds { get; set; } = 600;

    /// <summary>Require a password when the screen saver ends.</summary>
    public bool RequirePassword { get; set; } = true;

    /// <summary>Marquee scroll speed, 1 (slow) to 30 (fast).</summary>
    public int Speed { get; set; } = 9;
}

public sealed class LoggingSettings
{
    /// <summary>Log recovered windows and policy actions. Handy for finding window classes to ignore.</summary>
    public bool Enabled { get; set; }

    /// <summary>Log file. Empty uses %LOCALAPPDATA%\BetterBanner\BetterBanner.log.</summary>
    public string? FilePath { get; set; }
}
