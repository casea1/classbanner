using ClassBanner.Core;

namespace ClassBanner.Core.Tests;

/// <summary>Issue #19 item 6: uninstall gives each profile its own colors, wallpaper and screen saver back.</summary>
public class UserSettingsBackupTests
{
    private sealed class MemoryStore : IUserValueStore
    {
        public readonly Dictionary<string, Dictionary<string, object>> Keys = new(StringComparer.OrdinalIgnoreCase);

        public bool TryGet(string key, string name, out object? value)
        {
            value = null;
            return Keys.TryGetValue(key, out var values) && values.TryGetValue(name, out value);
        }

        public void Set(string key, string name, object value)
        {
            if (!Keys.TryGetValue(key, out var values)) Keys[key] = values = new(StringComparer.OrdinalIgnoreCase);
            values[name] = value;
        }

        public void Delete(string key, string name)
        {
            if (Keys.TryGetValue(key, out var values)) values.Remove(name);
        }

        public IEnumerable<string> ValueNames(string key) => Keys.TryGetValue(key, out var v) ? v.Keys.ToList() : [];

        public IEnumerable<string> SubKeys(string key) =>
            Keys.Keys.Where(k => k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase))
                .Select(k => k[(key.Length + 1)..].Split('\\')[0]).Distinct().ToList();

        public bool KeyExists(string key) => Keys.Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase));

        public void DeleteTree(string key)
        {
            foreach (var k in Keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + @"\", StringComparison.OrdinalIgnoreCase)).ToList())
                Keys.Remove(k);
        }

        public object? Get(string key, string name) => TryGet(key, name, out var v) ? v : null;
    }

    private static readonly int SecretAccent = UserSettingsBackup.AccentValue(RgbColor.Parse("#C8102E"));
    private static readonly HashSet<int> BannerColors = UserSettingsBackup.BannerAccentColors([]);

    /// <summary>What the banner writes when it enforces SECRET colors, wallpaper and screen saver.</summary>
    private static void ApplyBanner(MemoryStore store)
    {
        UserSettingsBackup.Remember(store, UserSettingsBackup.Colors);
        UserSettingsBackup.Remember(store, UserSettingsBackup.Wallpaper);
        UserSettingsBackup.Remember(store, UserSettingsBackup.ScreenSaver);
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", SecretAccent);
        store.Set(UserSettingsBackup.DwmKey, "ColorizationColor", 123);
        store.Set(UserSettingsBackup.AccentKey, "AccentColorMenu", SecretAccent);
        store.Set(UserSettingsBackup.DesktopKey, "Wallpaper", @"C:\Users\jo\AppData\Local\ClassBanner\wallpaper.bmp");
        store.Set(UserSettingsBackup.DesktopKey, "WallpaperStyle", "10");
        store.Set(UserSettingsBackup.DesktopKey, "SCRNSAVE.EXE", @"C:\Program Files\ClassBanner\ClassBanner.scr");
        store.Set(UserSettingsBackup.DesktopKey, "ScreenSaveActive", "1");
    }

    [Fact]
    public void Restores_the_users_own_values_and_removes_values_that_did_not_exist()
    {
        var store = new MemoryStore();
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 0);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", 42);
        store.Set(UserSettingsBackup.DesktopKey, "Wallpaper", @"C:\Users\jo\Pictures\cat.jpg");
        store.Set(UserSettingsBackup.DesktopKey, "WallpaperStyle", "2");

        ApplyBanner(store);
        Assert.Equal(RestoreOutcome.Restored, UserSettingsBackup.Restore(store, BannerColors));

        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
        Assert.Equal(42, store.Get(UserSettingsBackup.DwmKey, "AccentColor"));
        Assert.Null(store.Get(UserSettingsBackup.DwmKey, "ColorizationColor"));
        Assert.Null(store.Get(UserSettingsBackup.AccentKey, "AccentColorMenu"));
        Assert.Equal(@"C:\Users\jo\Pictures\cat.jpg", store.Get(UserSettingsBackup.DesktopKey, "Wallpaper"));
        Assert.Equal("2", store.Get(UserSettingsBackup.DesktopKey, "WallpaperStyle"));
        Assert.Null(store.Get(UserSettingsBackup.DesktopKey, "SCRNSAVE.EXE"));
        Assert.False(store.KeyExists(UserSettingsBackup.ProductKey));
    }

    [Fact]
    public void Later_runs_never_overwrite_the_saved_originals()
    {
        var store = new MemoryStore();
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 0);
        ApplyBanner(store);
        ApplyBanner(store); // the banner starts again at every sign-in

        UserSettingsBackup.Restore(store, BannerColors);
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
        Assert.Null(store.Get(UserSettingsBackup.DwmKey, "AccentColor"));
    }

    [Fact]
    public void Values_saved_by_Better_Banner_are_taken_over_not_saved_again()
    {
        var store = new MemoryStore();
        // Better Banner 1.2 saved the user's original (0), then colored the title bars (1).
        store.Set(UserSettingsBackup.OldSavedKey + "\\" + UserSettingsBackup.DwmKey.Replace('\\', '/'), "ColorPrevalence", 0);
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);

        ApplyBanner(store); // ClassBanner's first run must not save the banner's 1 as the original
        Assert.False(store.KeyExists(UserSettingsBackup.OldProductKey));

        UserSettingsBackup.Restore(store, BannerColors);
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
    }

    [Fact]
    public void Better_Banner_saved_values_are_restored_by_uninstall_even_if_ClassBanner_never_ran()
    {
        var store = new MemoryStore();
        store.Set(UserSettingsBackup.OldSavedKey + "\\" + UserSettingsBackup.DwmKey.Replace('\\', '/'), "ColorPrevalence", 0);
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        Assert.Equal(RestoreOutcome.Restored, UserSettingsBackup.Restore(store, BannerColors));
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
    }

    [Fact]
    public void Profile_changed_by_an_older_version_is_reset()
    {
        var store = new MemoryStore();
        // 1.1.0 saved nothing.
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", SecretAccent);
        store.Set(UserSettingsBackup.DesktopKey, "Wallpaper", @"C:\Users\jo\AppData\Local\ClassBanner\wallpaper.bmp");
        store.Set(UserSettingsBackup.DesktopKey, "SCRNSAVE.EXE", @"C:\Program Files\ClassBanner\ClassBanner.scr");
        store.Set(UserSettingsBackup.DesktopKey, "ScreenSaveActive", "1");

        Assert.Equal(RestoreOutcome.ResetLegacy, UserSettingsBackup.Restore(store, BannerColors));
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
        Assert.Equal("", store.Get(UserSettingsBackup.DesktopKey, "Wallpaper"));
        Assert.Null(store.Get(UserSettingsBackup.DesktopKey, "SCRNSAVE.EXE"));
        Assert.Equal("0", store.Get(UserSettingsBackup.DesktopKey, "ScreenSaveActive"));
    }

    [Fact]
    public void A_profile_we_never_touched_is_left_alone()
    {
        var store = new MemoryStore();
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", 42); // the user's own accent on title bars
        store.Set(UserSettingsBackup.DesktopKey, "Wallpaper", @"C:\Windows\Web\Wallpaper\Windows\img0.jpg");

        Assert.Equal(RestoreOutcome.Untouched, UserSettingsBackup.Restore(store, BannerColors));
        Assert.Equal(1, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
        Assert.Equal(@"C:\Windows\Web\Wallpaper\Windows\img0.jpg", store.Get(UserSettingsBackup.DesktopKey, "Wallpaper"));
    }

    [Fact]
    public void Custom_banner_colors_count_as_ours()
    {
        var custom = RgbColor.Parse("#123456");
        var store = new MemoryStore();
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", UserSettingsBackup.AccentValue(custom));
        Assert.Equal(RestoreOutcome.Untouched, UserSettingsBackup.Restore(store, BannerColors));
        Assert.Equal(RestoreOutcome.ResetLegacy, UserSettingsBackup.Restore(store, UserSettingsBackup.BannerAccentColors([custom])));
    }
}

/// <summary>Issue #24: the struct passed to AdjustTokenPrivileges must match the native layout.</summary>
public class NativeLayoutTests
{
    [Fact]
    public void Token_privileges_matches_the_native_layout()
    {
        Assert.Equal(16, System.Runtime.InteropServices.Marshal.SizeOf<TokenPrivilegesOne>());
        Assert.Equal(4, (int)System.Runtime.InteropServices.Marshal.OffsetOf<TokenPrivilegesOne>(nameof(TokenPrivilegesOne.LuidLowPart)));
        Assert.Equal(12, (int)System.Runtime.InteropServices.Marshal.OffsetOf<TokenPrivilegesOne>(nameof(TokenPrivilegesOne.Attributes)));
    }

    [Fact]
    public void Luid_is_split_into_its_two_halves()
    {
        var tp = TokenPrivilegesOne.Enable(0x0000_0001_0000_0011);
        Assert.Equal(0x11u, tp.LuidLowPart);
        Assert.Equal(1, tp.LuidHighPart);
    }
}

/// <summary>Issue #25: the banner's own colors are never saved, or restored, as the user's originals.</summary>
public class ContaminatedOriginalsTests
{
    private static readonly int Secret = UserSettingsBackup.AccentValue(RgbColor.Parse("#C8102E"));
    private static readonly HashSet<int> Colors = UserSettingsBackup.BannerAccentColors([]);

    private sealed class Store : IUserValueStore
    {
        private readonly Dictionary<string, Dictionary<string, object>> _keys = new(StringComparer.OrdinalIgnoreCase);
        public bool TryGet(string key, string name, out object? value) { value = null; return _keys.TryGetValue(key, out var v) && v.TryGetValue(name, out value); }
        public void Set(string key, string name, object value) { if (!_keys.TryGetValue(key, out var v)) _keys[key] = v = new(StringComparer.OrdinalIgnoreCase); v[name] = value; }
        public void Delete(string key, string name) { if (_keys.TryGetValue(key, out var v)) v.Remove(name); }
        public IEnumerable<string> ValueNames(string key) => _keys.TryGetValue(key, out var v) ? v.Keys.ToList() : [];
        public IEnumerable<string> SubKeys(string key) => _keys.Keys.Where(k => k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).Select(k => k[(key.Length + 1)..].Split('\\')[0]).Distinct().ToList();
        public bool KeyExists(string key) => _keys.Keys.Any(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase));
        public void DeleteTree(string key) { foreach (var k in _keys.Keys.Where(k => k.Equals(key, StringComparison.OrdinalIgnoreCase) || k.StartsWith(key + "\\", StringComparison.OrdinalIgnoreCase)).ToList()) _keys.Remove(k); }
        public object? Get(string key, string name) => TryGet(key, name, out var v) ? v : null;
    }

    [Fact]
    public void Upgrade_from_1_1_0_then_uninstall_turns_the_classification_accent_off()
    {
        var store = new Store();
        // 1.1.0's banner colored the title bars and saved nothing.
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", Secret);
        store.Set(UserSettingsBackup.AccentKey, "AccentColorMenu", Secret);
        store.Set(UserSettingsBackup.DesktopKey, "Wallpaper", @"C:\Users\jo\AppData\Local\BetterBanner\wallpaper.bmp");

        // The new banner runs right after the upgrade.
        UserSettingsBackup.Remember(store, UserSettingsBackup.Colors, Colors);
        UserSettingsBackup.Remember(store, UserSettingsBackup.Wallpaper, Colors);

        UserSettingsBackup.Restore(store, Colors);
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
        Assert.Null(store.Get(UserSettingsBackup.DwmKey, "AccentColor"));
        Assert.Null(store.Get(UserSettingsBackup.AccentKey, "AccentColorMenu"));
        Assert.Equal("", store.Get(UserSettingsBackup.DesktopKey, "Wallpaper"));
    }

    [Fact]
    public void Uninstall_writes_back_only_the_values_the_banner_saves()
    {
        var store = new Store();
        // A user edits their own saved copy to plant something for the elevated uninstall to write.
        store.Set(UserSettingsBackup.SavedKey + "\\Software/Microsoft/Windows/CurrentVersion/Run", "evil", @"C:\evil.exe");
        store.Set(UserSettingsBackup.SavedKey + "\\" + UserSettingsBackup.DwmKey.Replace('\\', '/'), "Evil", 1);
        store.Set(UserSettingsBackup.SavedKey + "\\" + UserSettingsBackup.DwmKey.Replace('\\', '/'), "ColorPrevalence", 0);

        UserSettingsBackup.Restore(store, Colors);
        Assert.Null(store.Get(@"Software\Microsoft\Windows\CurrentVersion\Run", "evil"));
        Assert.Null(store.Get(UserSettingsBackup.DwmKey, "Evil"));
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
    }

    [Fact]
    public void Contaminated_save_from_1_2_0_is_not_restored()
    {
        var store = new Store();
        // 1.2.0 saved the banner's red as the "original".
        var saved = UserSettingsBackup.OldSavedKey + "\\" + UserSettingsBackup.DwmKey.Replace('\\', '/');
        store.Set(saved, "ColorPrevalence", 1);
        store.Set(saved, "AccentColor", Secret);
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", Secret);

        Assert.Equal(RestoreOutcome.Restored, UserSettingsBackup.Restore(store, Colors));
        Assert.Equal(0, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
    }

    [Fact]
    public void A_users_own_accent_is_still_saved_and_restored()
    {
        var store = new Store();
        store.Set(UserSettingsBackup.DwmKey, "ColorPrevalence", 1);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", 42);
        UserSettingsBackup.Remember(store, UserSettingsBackup.Colors, Colors);
        store.Set(UserSettingsBackup.DwmKey, "AccentColor", Secret);

        UserSettingsBackup.Restore(store, Colors);
        Assert.Equal(1, store.Get(UserSettingsBackup.DwmKey, "ColorPrevalence"));
        Assert.Equal(42, store.Get(UserSettingsBackup.DwmKey, "AccentColor"));
    }
}
