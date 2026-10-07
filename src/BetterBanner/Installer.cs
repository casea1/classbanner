using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using BetterBanner.Core;
using Microsoft.Win32;
using static BetterBanner.Native;

namespace BetterBanner;

/// <summary>install / uninstall / config / show. The exe installs itself; no MSI needed.</summary>
internal static class Installer
{
    private const string AppName = "BetterBanner";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterBanner";
    private const string StateKey = @"SOFTWARE\BetterBanner";
    private const string LogonNoticeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    private static string InstalledExe => Path.Combine(InstallDir, "BetterBanner.exe");
    private static string InstalledScr => Path.Combine(InstallDir, "BetterBanner.scr");

    public static int Install(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;

        var settings = UpdateSettingsFile(cmd);
        StopRunningBanners();

        Directory.CreateDirectory(InstallDir);
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");
        if (!string.Equals(Path.GetFullPath(self), InstalledExe, StringComparison.OrdinalIgnoreCase))
            CopyWithRetry(self, InstalledExe);
        CopyWithRetry(self, InstalledScr);

        using (var run = Registry.LocalMachine.CreateSubKey(RunKey))
            run.SetValue(AppName, $"\"{InstalledExe}\"");

        using (var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            uninstall.SetValue("DisplayName", "Better Banner");
            uninstall.SetValue("DisplayVersion", AppInfo.Version);
            uninstall.SetValue("Publisher", "Better Banner");
            uninstall.SetValue("InstallLocation", InstallDir);
            uninstall.SetValue("DisplayIcon", InstalledExe);
            uninstall.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall");
            uninstall.SetValue("QuietUninstallString", $"\"{InstalledExe}\" uninstall --quiet");
            uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            uninstall.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }

        ApplyLogonNotice(settings, output);

        if (!cmd.NoStart) StartForCurrentUser();

        output.Line($"Better Banner {AppInfo.Version} installed to {InstallDir}.");
        output.Line($"Marking: {settings.EffectiveCaption}. It starts automatically at every sign-in.");
        return ExitCodes.Ok;
    }

    public static int Config(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;
        var settings = UpdateSettingsFile(cmd);
        ApplyLogonNotice(settings, output);
        // Running banners in this session reload immediately; others within 30 seconds.
        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "BetterBanner", SMTO_ABORTIFHUNG, 1000, out _);
        output.Line($"Settings saved to {SettingsLoader.DefaultSettingsPath}.");
        if (new RegistryPolicySource().ReadValues().Count > 0)
            output.Line("Note: Group Policy settings are present and override the settings file where both are set.");
        return ExitCodes.Ok;
    }

    public static int Uninstall(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;

        StopRunningBanners();
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(AppName, throwOnMissingValue: false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        RemoveLogonNoticeIfOurs(output);
        Registry.LocalMachine.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false);

        if (!cmd.KeepSettings && Directory.Exists(SettingsLoader.DefaultDirectory))
            Directory.Delete(SettingsLoader.DefaultDirectory, recursive: true);

        if (Directory.Exists(InstallDir))
        {
            var runningFromInstallDir = (Environment.ProcessPath ?? "").StartsWith(InstallDir, StringComparison.OrdinalIgnoreCase);
            if (runningFromInstallDir)
            {
                // Can't delete our own exe while running: let a short-lived cmd do it after we exit.
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{InstallDir}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            else
            {
                Directory.Delete(InstallDir, recursive: true);
            }
        }

        output.Line("Better Banner uninstalled. Colors, wallpaper and screen saver already applied to user profiles are left as they are.");
        return ExitCodes.Ok;
    }

    public static int Show(Output output)
    {
        var loaded = RegistryPolicySource.LoadEffective();
        output.Line($"Better Banner {AppInfo.Version}");
        output.Line($"Installed: {(File.Exists(InstalledExe) ? InstalledExe : "no")}");
        output.Line($"Settings file: {SettingsLoader.DefaultSettingsPath}{(File.Exists(SettingsLoader.DefaultSettingsPath) ? "" : " (not present)")}");
        output.Line($"Group Policy: {(loaded.FromPolicy ? "applied" : "not configured")}");
        foreach (var warning in loaded.Warnings) output.Line($"Warning: {warning}");
        output.Line("");
        output.Line(SettingsSerializer.Serialize(loaded.Settings));
        return ExitCodes.Ok;
    }

    private static BannerSettings UpdateSettingsFile(ParsedCommand cmd)
    {
        var path = SettingsLoader.DefaultSettingsPath;
        var settings = SettingsLoader.LoadFile(path);
        SettingKeys.ApplyAll(settings, cmd.Settings);
        // Lock the folder first so the new file inherits admin-only write access.
        Directory.CreateDirectory(SettingsLoader.DefaultDirectory);
        LockDownSettingsDirectory(SettingsLoader.DefaultDirectory);
        SettingsLoader.Save(settings, path);
        return settings;
    }

    /// <summary>Only administrators and SYSTEM may change settings; users can read them.</summary>
    private static void LockDownSettingsDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    /// <summary>
    /// The Windows sign-in notice ("legal notice"): a small window users must click OK on before they can log on.
    /// Only removed later if we were the ones who set it.
    /// </summary>
    private static void ApplyLogonNotice(BannerSettings settings, Output output)
    {
        if (!settings.HasLogonNotice)
        {
            RemoveLogonNoticeIfOurs(output);
            return;
        }

        using (var key = Registry.LocalMachine.CreateSubKey(LogonNoticeKey))
        {
            key.SetValue("legalnoticecaption", settings.LogonNoticeTitle ?? "");
            key.SetValue("legalnoticetext", settings.LogonNoticeText ?? "");
        }
        using (var state = Registry.LocalMachine.CreateSubKey(StateKey))
            state.SetValue("ManagesLogonNotice", 1, RegistryValueKind.DWord);
        output.Line("Sign-in notice set; users will acknowledge it before logging on.");
    }

    private static void RemoveLogonNoticeIfOurs(Output output)
    {
        using var state = Registry.LocalMachine.OpenSubKey(StateKey, writable: true);
        if (state?.GetValue("ManagesLogonNotice") is not int managed || managed != 1) return;

        using (var key = Registry.LocalMachine.OpenSubKey(LogonNoticeKey, writable: true))
        {
            key?.SetValue("legalnoticecaption", "");
            key?.SetValue("legalnoticetext", "");
        }
        state.DeleteValue("ManagesLogonNotice", throwOnMissingValue: false);
        output.Line("Sign-in notice removed.");
    }

    private static bool RequireAdmin(Output output)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return true;
        output.Error("This command must be run as administrator (right-click > Run as administrator, or an elevated prompt).");
        return false;
    }

    /// <summary>Stops banners in every session so files can be replaced; they restart at next sign-in (or below).</summary>
    private static void StopRunningBanners()
    {
        foreach (var process in Process.GetProcessesByName(AppName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    process.Kill();
                    process.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    Log.Warn($"Could not stop banner process {process.Id}: {ex.Message}");
                }
            }
        }
    }

    private static void CopyWithRetry(string source, string destination)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(source, destination, overwrite: true);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(500 * attempt);
            }
        }
    }

    /// <summary>Starts the banner un-elevated in the current session (Explorer launches it as the signed-in user).</summary>
    private static void StartForCurrentUser()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{InstalledExe}\"") { UseShellExecute = false });
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not start the banner after install: {ex.Message}");
        }
    }
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int BadArguments = 2;
    public const int NotAdmin = 5;
}
