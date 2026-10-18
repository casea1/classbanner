using System.ComponentModel;
using System.Runtime.InteropServices;
using BetterBanner.Core;
using Microsoft.Win32;

namespace BetterBanner;

/// <summary><see cref="IUserValueStore"/> over a user's registry root (HKCU, or HKEY_USERS\&lt;SID&gt;).</summary>
internal sealed class RegistryUserStore(RegistryKey root) : IUserValueStore
{
    public bool TryGet(string key, string name, out object? value)
    {
        using var k = root.OpenSubKey(key);
        value = k?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        return value is not null;
    }

    public void Set(string key, string name, object value)
    {
        using var k = root.CreateSubKey(key);
        switch (value)
        {
            case int i: k.SetValue(name, i, RegistryValueKind.DWord); break;
            case string[] m: k.SetValue(name, m, RegistryValueKind.MultiString); break;
            default: k.SetValue(name, value.ToString() ?? "", RegistryValueKind.String); break;
        }
    }

    public void Delete(string key, string name)
    {
        using var k = root.OpenSubKey(key, writable: true);
        k?.DeleteValue(name, throwOnMissingValue: false);
    }

    public IEnumerable<string> ValueNames(string key)
    {
        using var k = root.OpenSubKey(key);
        return k?.GetValueNames() ?? [];
    }

    public IEnumerable<string> SubKeys(string key)
    {
        using var k = root.OpenSubKey(key);
        return k?.GetSubKeyNames() ?? [];
    }

    public bool KeyExists(string key)
    {
        using var k = root.OpenSubKey(key);
        return k is not null;
    }

    public void DeleteTree(string key) => root.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
}

/// <summary>Uninstall: gives every profile on the computer its own colors, wallpaper and screen saver back (issue #19).</summary>
internal static unsafe partial class UserProfiles
{
    private const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    public sealed record Summary(int Restored, int Reset, List<string> Failed);

    public static Summary RestoreAll(IEnumerable<RgbColor> customColors)
    {
        var colors = UserSettingsBackup.BannerAccentColors(customColors);
        int restored = 0, reset = 0;
        var failed = new List<string>();
        var privileges = EnablePrivileges();

        using var list = Registry.LocalMachine.OpenSubKey(ProfileList);
        foreach (var sid in list?.GetSubKeyNames() ?? [])
        {
            // Real user accounts only (local/domain S-1-5-21, Entra ID S-1-12-1); not SYSTEM or service profiles.
            if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) continue;
            try
            {
                var outcome = WithUserHive(sid, list!, root => UserSettingsBackup.Restore(new RegistryUserStore(root), colors), privileges);
                if (outcome == RestoreOutcome.Restored) restored++;
                else if (outcome == RestoreOutcome.ResetLegacy) reset++;
            }
            catch (Exception ex)
            {
                failed.Add($"{sid}: {ex.Message}");
                Log.Warn($"Could not restore user settings for {sid}: {ex.Message}");
            }
        }
        return new Summary(restored, reset, failed);
    }

    /// <summary>A signed-in user's hive is already under HKEY_USERS; anyone else's NTUSER.DAT is loaded briefly.</summary>
    private static RestoreOutcome? WithUserHive(string sid, RegistryKey list, Func<RegistryKey, RestoreOutcome> action, bool privileges)
    {
        using (var loaded = Registry.Users.OpenSubKey(sid, writable: true))
        {
            if (loaded is not null) return action(loaded);
        }

        using var profile = list.OpenSubKey(sid);
        var dir = Environment.ExpandEnvironmentVariables(profile?.GetValue("ProfileImagePath") as string ?? "");
        var hive = Path.Combine(dir, "NTUSER.DAT");
        if (dir.Length == 0 || !File.Exists(hive)) return null;
        if (!privileges) throw new InvalidOperationException("can't load the profile (backup/restore privileges unavailable)");

        var mount = "BetterBanner_" + sid;
        var error = RegLoadKeyW(HKEY_USERS, mount, hive);
        if (error != 0) throw new Win32Exception(error, $"could not load {hive}");
        try
        {
            using var root = Registry.Users.OpenSubKey(mount, writable: true) ?? throw new InvalidOperationException("loaded hive not found");
            return action(root);
        }
        finally
        {
            var unload = RegUnLoadKeyW(HKEY_USERS, mount);
            if (unload != 0) Log.Warn($"Could not unload {mount} (error {unload}).");
        }
    }

    // ---- interop: loading other users' hives needs SeBackupPrivilege and SeRestorePrivilege enabled ----
    private static readonly nint HKEY_USERS = unchecked((nint)(int)0x80000003);
    private const uint TOKEN_ADJUST_PRIVILEGES = 0x20;
    private const uint TOKEN_QUERY = 0x8;
    private const uint SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES
    {
        public uint PrivilegeCount;
        public long Luid;
        public uint Attributes;
    }

    private static bool EnablePrivileges()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token)) return false;
        try
        {
            var ok = true;
            foreach (var name in new[] { "SeBackupPrivilege", "SeRestorePrivilege" })
            {
                if (!LookupPrivilegeValueW(null, name, out var luid)) { ok = false; continue; }
                var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
                if (!AdjustTokenPrivileges(token, false, &tp, 0, 0, 0) || Marshal.GetLastPInvokeError() != 0) ok = false;
            }
            return ok;
        }
        finally
        {
            Native.CloseHandle(token);
        }
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll,
        TOKEN_PRIVILEGES* newState, uint length, nint previous, nint returnLength);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegLoadKeyW(nint key, string subKey, string file);

    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RegUnLoadKeyW(nint key, string subKey);
}
