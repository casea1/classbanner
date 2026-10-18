namespace BetterBanner.Core;

/// <summary>
/// A per-user registry view (HKCU, or a user's hive under HKEY_USERS). Values are int (DWORD), string or string[].
/// Kept abstract so the backup and restore rules are unit tested without a registry.
/// </summary>
public interface IUserValueStore
{
    bool TryGet(string key, string name, out object? value);
    void Set(string key, string name, object value);
    void Delete(string key, string name);
    IEnumerable<string> ValueNames(string key);
    IEnumerable<string> SubKeys(string key);
    bool KeyExists(string key);
    void DeleteTree(string key);
}

public enum RestoreOutcome
{
    /// <summary>Nothing of ours found in this profile.</summary>
    Untouched,

    /// <summary>The values saved before Better Banner first changed them were put back.</summary>
    Restored,

    /// <summary>Changed by a version that saved nothing (1.1.0 and earlier): our changes were switched off.</summary>
    ResetLegacy,
}

/// <summary>
/// Issue #19 item 6: before the banner first changes a user's colors, wallpaper or screen saver, it saves that
/// profile's own values under HKCU\Software\BetterBanner\Saved; uninstall puts them back.
/// </summary>
public static class UserSettingsBackup
{
    public const string ProductKey = @"Software\BetterBanner";
    public const string SavedKey = ProductKey + @"\Saved";
    private const string AbsentValue = "~absent";

    public const string DwmKey = @"Software\Microsoft\Windows\DWM";
    public const string AccentKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent";
    public const string DesktopKey = @"Control Panel\Desktop";

    public static readonly (string Key, string[] Names)[] Colors =
    [
        (DwmKey, ["ColorPrevalence", "AccentColor", "ColorizationColor"]),
        (AccentKey, ["AccentColorMenu"]),
    ];

    public static readonly (string Key, string[] Names)[] Wallpaper =
        [(DesktopKey, ["Wallpaper", "WallpaperStyle", "TileWallpaper"])];

    public static readonly (string Key, string[] Names)[] ScreenSaver =
        [(DesktopKey, ["SCRNSAVE.EXE", "ScreenSaveActive", "ScreenSaveTimeOut", "ScreenSaverIsSecure"])];

    /// <summary>Saves each value's current state the first time only, so later runs never overwrite the user's original.</summary>
    public static void Remember(IUserValueStore store, IEnumerable<(string Key, string[] Names)> group)
    {
        foreach (var (key, names) in group)
        {
            var saved = SavedFor(key);
            var absent = Absent(store, saved);
            var changed = false;
            foreach (var name in names)
            {
                if (absent.Contains(name, StringComparer.OrdinalIgnoreCase) || store.TryGet(saved, name, out _)) continue;
                if (store.TryGet(key, name, out var value) && value is not null) store.Set(saved, name, value);
                else
                {
                    absent.Add(name);
                    changed = true;
                }
            }
            if (changed) store.Set(saved, AbsentValue, absent.ToArray());
        }
    }

    /// <summary>
    /// Puts back what <see cref="Remember"/> saved and removes the saved copy. A profile with nothing saved but our
    /// fingerprints (an accent in a banner color, our wallpaper or our screen saver) has those switched off instead.
    /// </summary>
    /// <param name="bannerAccentColors">DWM AccentColor values the banner may have written (see <see cref="AccentValue"/>).</param>
    public static RestoreOutcome Restore(IUserValueStore store, IReadOnlySet<int> bannerAccentColors)
    {
        if (store.KeyExists(SavedKey))
        {
            foreach (var savedKey in store.SubKeys(SavedKey).ToList())
            {
                var key = savedKey.Replace('/', '\\');
                var saved = SavedFor(key);
                foreach (var name in store.ValueNames(saved).Where(n => n != AbsentValue))
                {
                    if (store.TryGet(saved, name, out var value) && value is not null) store.Set(key, name, value);
                }
                foreach (var name in Absent(store, saved)) store.Delete(key, name);
            }
            store.DeleteTree(SavedKey);
            DeleteProductKeyIfEmpty(store);
            return RestoreOutcome.Restored;
        }

        var reset = false;
        if (store.TryGet(DwmKey, "AccentColor", out var accent) && accent is int color && bannerAccentColors.Contains(color) &&
            store.TryGet(DwmKey, "ColorPrevalence", out var prevalence) && prevalence is 1)
        {
            store.Set(DwmKey, "ColorPrevalence", 0);
            reset = true;
        }
        if (store.TryGet(DesktopKey, "Wallpaper", out var wallpaper) && wallpaper is string path &&
            path.EndsWith(@"\BetterBanner\wallpaper.bmp", StringComparison.OrdinalIgnoreCase))
        {
            store.Set(DesktopKey, "Wallpaper", "");
            reset = true;
        }
        if (store.TryGet(DesktopKey, "SCRNSAVE.EXE", out var scr) && scr is string scrPath &&
            scrPath.EndsWith(@"\BetterBanner.scr", StringComparison.OrdinalIgnoreCase))
        {
            store.Delete(DesktopKey, "SCRNSAVE.EXE");
            store.Set(DesktopKey, "ScreenSaveActive", "0");
            reset = true;
        }
        return reset ? RestoreOutcome.ResetLegacy : RestoreOutcome.Untouched;
    }

    /// <summary>The DWM AccentColor value the banner writes for a banner color (0xFFBBGGRR).</summary>
    public static int AccentValue(RgbColor color) => unchecked((int)(0xFF000000 | (uint)color.ToColorRef()));

    /// <summary>Every standard banner color, plus any custom one, as DWM AccentColor values.</summary>
    public static HashSet<int> BannerAccentColors(IEnumerable<RgbColor> extra) =>
        ClassificationPresets.All.Select(p => p.Background).Concat(extra).Append(BannerSettings.NotConfiguredBackColor)
            .Select(AccentValue).ToHashSet();

    private static string SavedFor(string key) => SavedKey + @"\" + key.Replace('\\', '/');

    private static List<string> Absent(IUserValueStore store, string saved) =>
        store.TryGet(saved, AbsentValue, out var value) && value is string[] names ? [.. names] : [];

    private static void DeleteProductKeyIfEmpty(IUserValueStore store)
    {
        if (store.KeyExists(ProductKey) && !store.SubKeys(ProductKey).Any() && !store.ValueNames(ProductKey).Any())
            store.DeleteTree(ProductKey);
    }
}
