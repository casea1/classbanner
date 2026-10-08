using System.Diagnostics;
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
    public const string StateKey = @"SOFTWARE\BetterBanner";
    /// <summary>Changes on every install; running banners watch it and restart into the new version.</summary>
    public const string InstallIdValue = "InstallId";
    private const string LogonNoticeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string Publisher = "Austin Case";

    // Control Panel item (an exe-based "command object" item).
    private const string ControlPanelClsid = "{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}";
    private const string ClsidKey = @"SOFTWARE\Classes\CLSID\" + ControlPanelClsid;
    private const string ControlPanelNamespaceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\" + ControlPanelClsid;

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    private static string InstalledExe => Path.Combine(InstallDir, "BetterBanner.exe");
    private static string InstalledScr => Path.Combine(InstallDir, "BetterBanner.scr");

    public static int Install(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;

        var file = SettingsLoader.LoadFile(SettingsLoader.DefaultSettingsPath);
        if (!CheckSettingsFile(file, output)) return ExitCodes.Error;
        if (!file.LevelSet && !cmd.Settings.Any(s => s.Key.Equals(nameof(BannerSettings.Level), StringComparison.OrdinalIgnoreCase)))
        {
            output.Error("Choose the classification for this computer, e.g. install --level SECRET (U, CUI, C, S, TS or TS/SCI).");
            return ExitCodes.BadArguments;
        }

        // Banners in other users' sessions keep running: the running exe is renamed (Windows allows that), the new one
        // is copied in, and each banner restarts itself into the new version when it sees the new install id.
        Directory.CreateDirectory(InstallDir);
        DeleteLeftoverBinaries();
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");
        if (!string.Equals(Path.GetFullPath(self), InstalledExe, StringComparison.OrdinalIgnoreCase))
            ReplaceFile(self, InstalledExe);
        ReplaceFile(self, InstalledScr);

        var settings = UpdateSettingsFile(file, cmd, output);

        using (var run = Registry.LocalMachine.CreateSubKey(RunKey))
            run.SetValue(AppName, $"\"{InstalledExe}\"");

        using (var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            uninstall.SetValue("DisplayName", "Better Banner");
            uninstall.SetValue("DisplayVersion", AppInfo.Version);
            uninstall.SetValue("Publisher", Publisher);
            uninstall.SetValue("InstallLocation", InstallDir);
            uninstall.SetValue("DisplayIcon", InstalledExe);
            uninstall.SetValue("UninstallString", $"\"{InstalledExe}\" uninstall");
            uninstall.SetValue("QuietUninstallString", $"\"{InstalledExe}\" uninstall --quiet");
            uninstall.SetValue("ModifyPath", $"\"{InstalledExe}\" settings");
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            uninstall.SetValue("EstimatedSize", (int)Math.Max(1, new FileInfo(InstalledExe).Length / 1024), RegistryValueKind.DWord);
        }

        RegisterControlPanelItem();
        ApplyLogonNotice(settings, output);
        using (var state = Registry.LocalMachine.CreateSubKey(StateKey))
            state.SetValue(InstallIdValue, Guid.NewGuid().ToString());

        if (!cmd.NoStart) StartForCurrentUser();

        output.Line($"Better Banner {AppInfo.Version} installed to {InstallDir}.");
        output.Line($"Marking: {settings.EffectiveCaption}. It starts automatically at every sign-in.");
        return ExitCodes.Ok;
    }

    public static int Config(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;
        var file = SettingsLoader.LoadFile(SettingsLoader.DefaultSettingsPath);
        if (!CheckSettingsFile(file, output)) return ExitCodes.Error;
        var settings = UpdateSettingsFile(file, cmd, output);
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
        Registry.LocalMachine.DeleteSubKeyTree(ControlPanelNamespaceKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
        RemoveLogonNoticeIfOurs(output);
        Registry.LocalMachine.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false);

        if (Directory.Exists(InstallDir))
        {
            var runningFromInstallDir = (Environment.ProcessPath ?? "").StartsWith(InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (runningFromInstallDir)
            {
                // Can't delete our own exe while running: let a short-lived cmd finish after we exit.
                var delete = cmd.KeepSettings
                    ? $"del /f /q \"{InstalledExe}\" \"{InstalledScr}\" \"{Path.Combine(InstallDir, "*.old-*")}\""
                    : $"rmdir /s /q \"{InstallDir}\"";
                Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & {delete}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = Path.GetTempPath(),
                });
            }
            else if (cmd.KeepSettings)
            {
                foreach (var path in Directory.EnumerateFiles(InstallDir))
                {
                    if (!string.Equals(path, SettingsLoader.DefaultSettingsPath, StringComparison.OrdinalIgnoreCase)) File.Delete(path);
                }
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

    /// <summary>Adds "Better Banner" to Control Panel (Appearance and Personalization; System and Security).</summary>
    private static void RegisterControlPanelItem()
    {
        using (var clsid = Registry.LocalMachine.CreateSubKey(ClsidKey))
        {
            clsid.SetValue("", "Better Banner");
            clsid.SetValue("LocalizedString", "Better Banner");
            clsid.SetValue("InfoTip", "Classification banner, sign-in notice and related settings for this computer.");
            clsid.SetValue("System.ApplicationName", "AustinCase.BetterBanner");
            clsid.SetValue("System.ControlPanel.Category", "1,5");
            using (var icon = clsid.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{InstalledExe}\",0");
            using (var command = clsid.CreateSubKey(@"Shell\Open\Command"))
                command.SetValue("", $"\"{InstalledExe}\" settings", RegistryValueKind.ExpandString);
        }
        using (var ns = Registry.LocalMachine.CreateSubKey(ControlPanelNamespaceKey))
            ns.SetValue("", "Better Banner");
    }

    /// <summary>Refuses to touch a settings file that exists but can't be read, rather than replacing it with defaults.</summary>
    private static bool CheckSettingsFile(SettingsFile file, Output output)
    {
        if (!file.Unreadable) return true;
        foreach (var warning in file.Warnings) output.Error(warning);
        output.Error($"Nothing was changed. Fix or delete {SettingsLoader.DefaultSettingsPath}, then run the command again.");
        return false;
    }

    private static BannerSettings UpdateSettingsFile(SettingsFile file, ParsedCommand cmd, Output output)
    {
        foreach (var warning in file.Warnings) output.Line($"Warning: {warning} (removed when saving)");
        var settings = file.Settings;
        SettingKeys.ApplyAll(settings, cmd.Settings);
        // Lives under Program Files, which only administrators can write; no custom permissions needed.
        SettingsLoader.Save(settings, SettingsLoader.DefaultSettingsPath);
        return settings;
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

    /// <summary>Stops banners in every session (uninstall only).</summary>
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

    /// <summary>
    /// Puts <paramref name="source"/> at <paramref name="destination"/>. If the destination is running (a banner or the
    /// screen saver), it is renamed out of the way first; Windows allows renaming a running program but not deleting it.
    /// </summary>
    private static void ReplaceFile(string source, string destination)
    {
        if (File.Exists(destination))
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                File.Move(destination, $"{destination}.old-{Guid.NewGuid():N}");
            }
        }
        File.Copy(source, destination);
    }

    /// <summary>Removes renamed binaries from earlier upgrades once nothing is running them.</summary>
    private static void DeleteLeftoverBinaries()
    {
        foreach (var old in Directory.EnumerateFiles(InstallDir, "*.old-*"))
        {
            try
            {
                File.Delete(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use by a banner that hasn't restarted yet; the next install cleans it up.
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
