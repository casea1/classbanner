namespace ClassBanner.Core;

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

    /// <summary>The values saved before ClassBanner first changed them were put back.</summary>
    Restored,

    /// <summary>Changed by a version that saved nothing (1.1.0 and earlier): our changes were switched off.</summary>
    ResetLegacy,
}

/// <summary>
/// Issue #19 item 6: before the banner first changes a user's colors, wallpaper or screen saver, it saves that
/// profile's own values under HKCU\Software\ClassBanner\Saved; uninstall puts them back.
/// </summary>
public static class UserSettingsBackup
{
    public const string ProductKey = @"Software\ClassBanner";
    public const string SavedKey = ProductKey + @"\Saved";

    /// <summary>Where Better Banner 1.2 (the name before ClassBanner) saved the same values.</summary>
    public const string OldProductKey = @"Software\BetterBanner";
    public const string OldSavedKey = OldProductKey + @"\Saved";
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

    private static readonly Dictionary<string, HashSet<string>> KnownValues =
        Colors.Concat(Wallpaper).Concat(ScreenSaver)
            .GroupBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.SelectMany(x => x.Names).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);

    /// <summary>Saves each value's current state the first time only, so later runs never overwrite the user's original.</summary>
    /// <param name="bannerAccentColors">
    /// Accent values the banner writes. A value that is already the banner's own (left by 1.1.0, or by an earlier
    /// uninstall) is not the user's original and is saved as the Windows default instead (issue #25).
    /// </param>
    public static void Remember(IUserValueStore store, IEnumerable<(string Key, string[] Names)> group, IReadOnlySet<int>? bannerAccentColors = null)
    {
        AdoptOldSaved(store);
        var colors = bannerAccentColors ?? BannerAccentColors([]);
        var bannerColored = IsBannerColored(store, colors);
        foreach (var (key, names) in group)
        {
            var saved = SavedFor(key);
            var absent = Absent(store, saved);
            var changed = false;
            foreach (var name in names)
            {
                if (absent.Contains(name, StringComparer.OrdinalIgnoreCase) || store.TryGet(saved, name, out _)) continue;
                if (store.TryGet(key, name, out var value) && value is not null &&
                    Original(key, name, value, bannerColored, colors) is { } original)
                {
                    store.Set(saved, name, original);
                }
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
        AdoptOldSaved(store);
        if (store.KeyExists(SavedKey))
        {
            foreach (var savedKey in store.SubKeys(SavedKey).ToList())
            {
                var key = savedKey.Replace('/', '\\');
                var saved = SavedFor(key);
                // Uninstall runs as administrator and the saved copy is in the user's own hive, which the user can
                // edit: only ever write back the values the banner itself saves.
                if (!KnownValues.TryGetValue(key, out var allowed))
                {
                    continue;
                }
                foreach (var name in store.ValueNames(saved).Where(n => n != AbsentValue && allowed.Contains(n)))
                {
                    if (store.TryGet(saved, name, out var value) && value is not null) store.Set(key, name, value);
                }
                foreach (var name in Absent(store, saved).Where(allowed.Contains)) store.Delete(key, name);
            }
            store.DeleteTree(SavedKey);
            DeleteProductKeyIfEmpty(store);
            // 1.2.0 could save the banner's own colors as the "original" (issue #25); never put those back.
            ResetFingerprints(store, bannerAccentColors);
            return RestoreOutcome.Restored;
        }

        return ResetFingerprints(store, bannerAccentColors) ? RestoreOutcome.ResetLegacy : RestoreOutcome.Untouched;
    }

    private static bool IsBannerColored(IUserValueStore store, IReadOnlySet<int> colors) =>
        store.TryGet(DwmKey, "AccentColor", out var accent) && accent is int color && colors.Contains(color) &&
        store.TryGet(DwmKey, "ColorPrevalence", out var prevalence) && prevalence is 1;

    private static bool IsOurWallpaper(object? value) => value is string path &&
        (path.EndsWith(@"\ClassBanner\wallpaper.bmp", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(@"\BetterBanner\wallpaper.bmp", StringComparison.OrdinalIgnoreCase));

    private static bool IsOurScreenSaver(object? value) => value is string path &&
        (path.EndsWith(@"\ClassBanner.scr", StringComparison.OrdinalIgnoreCase) ||
         path.EndsWith(@"\BetterBanner.scr", StringComparison.OrdinalIgnoreCase));

    /// <summary>The value to save as the user's original, or null to save it as "didn't exist" (the Windows default).</summary>
    private static object? Original(string key, string name, object value, bool bannerColored, IReadOnlySet<int> colors)
    {
        if (key == DwmKey && bannerColored) return name == "ColorPrevalence" ? 0 : null;
        if (key == AccentKey && name == "AccentColorMenu" && value is int menu && colors.Contains(menu)) return null;
        if (key == DesktopKey && name == "Wallpaper" && IsOurWallpaper(value)) return "";
        if (key == DesktopKey && name == "SCRNSAVE.EXE" && IsOurScreenSaver(value)) return null;
        return value;
    }

    /// <summary>Switches off whatever of ours is still in the profile. Returns true if anything was.</summary>
    private static bool ResetFingerprints(IUserValueStore store, IReadOnlySet<int> bannerAccentColors)
    {
        var reset = false;
        if (IsBannerColored(store, bannerAccentColors))
        {
            store.Set(DwmKey, "ColorPrevalence", 0);
            reset = true;
        }
        if (store.TryGet(DesktopKey, "Wallpaper", out var wallpaper) && IsOurWallpaper(wallpaper))
        {
            store.Set(DesktopKey, "Wallpaper", "");
            reset = true;
        }
        if (store.TryGet(DesktopKey, "SCRNSAVE.EXE", out var scr) && IsOurScreenSaver(scr))
        {
            store.Delete(DesktopKey, "SCRNSAVE.EXE");
            store.Set(DesktopKey, "ScreenSaveActive", "0");
            reset = true;
        }
        return reset;
    }

    /// <summary>The DWM AccentColor value the banner writes for a banner color (0xFFBBGGRR).</summary>
    public static int AccentValue(RgbColor color) => unchecked((int)(0xFF000000 | (uint)color.ToColorRef()));

    /// <summary>Every standard banner color, plus any custom one, as DWM AccentColor values.</summary>
    public static HashSet<int> BannerAccentColors(IEnumerable<RgbColor> extra) =>
        ClassificationPresets.All.Select(p => p.Background).Concat(extra).Append(BannerSettings.NotConfiguredBackColor)
            .Select(AccentValue).ToHashSet();

    /// <summary>
    /// Values Better Banner 1.2 saved are this user's real originals (by now the live values are the banner's), so
    /// they move to ClassBanner's key instead of being saved again.
    /// </summary>
    private static void AdoptOldSaved(IUserValueStore store)
    {
        if (!store.KeyExists(OldSavedKey)) return;
        if (!store.KeyExists(SavedKey))
        {
            foreach (var sub in store.SubKeys(OldSavedKey).ToList())
            {
                var from = OldSavedKey + @"\" + sub;
                var to = SavedKey + @"\" + sub;
                foreach (var name in store.ValueNames(from).ToList())
                {
                    if (store.TryGet(from, name, out var value) && value is not null) store.Set(to, name, value);
                }
            }
        }
        store.DeleteTree(OldSavedKey);
        if (store.KeyExists(OldProductKey) && !store.SubKeys(OldProductKey).Any() && !store.ValueNames(OldProductKey).Any())
            store.DeleteTree(OldProductKey);
    }

    private static string SavedFor(string key) => SavedKey + @"\" + key.Replace('\\', '/');

    private static List<string> Absent(IUserValueStore store, string saved) =>
        store.TryGet(saved, AbsentValue, out var value) && value is string[] names ? [.. names] : [];

    private static void DeleteProductKeyIfEmpty(IUserValueStore store)
    {
        if (store.KeyExists(ProductKey) && !store.SubKeys(ProductKey).Any() && !store.ValueNames(ProductKey).Any())
            store.DeleteTree(ProductKey);
    }
}
