using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using ClassBanner.Core;
using Microsoft.Win32;
using static ClassBanner.Native;

namespace ClassBanner;

/// <summary>install / uninstall / config / show. The exe installs itself; no MSI needed.</summary>
internal static class Installer
{
    private const string AppName = "ClassBanner";
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ClassBanner";
    public const string StateKey = @"SOFTWARE\ClassBanner";
    /// <summary>Changes on every install; running banners watch it and restart into the new version.</summary>
    public const string InstallIdValue = "InstallId";
    private const string LogonNoticeKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    private const string Publisher = "Austin Case";

    // Control Panel item (an exe-based "command object" item).
    private const string ControlPanelClsid = "{7B3E2C5A-9D41-4F6B-8E2A-5C1D0B9F4A21}";
    private const string ClsidKey = @"SOFTWARE\Classes\CLSID\" + ControlPanelClsid;
    private const string ControlPanelNamespaceKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\ControlPanel\NameSpace\" + ControlPanelClsid;

    // Up to 1.2 the product was "Better Banner": %ProgramFiles%\BetterBanner, process BetterBanner.exe, service
    // "BetterBanner" (1.2) or the Run key (earlier), its own Apps & features entry, state key and Start menu entry.
    // Installing ClassBanner takes over its settings and sign-in notice and removes the rest.
    private const string OldName = "BetterBanner";
    private const string OldUninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BetterBanner";
    private const string OldStateKey = @"SOFTWARE\BetterBanner";
    private static string OldInstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), OldName);
    private static string OldStartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Better Banner.lnk");

    private static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), AppName);
    public static string InstalledExe => Path.Combine(InstallDir, "ClassBanner.exe");
    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "ClassBanner.lnk");

    public static bool IsInstalled => File.Exists(InstalledExe);

    /// <summary>True when this process is the installed copy (which shows the banner) rather than a downloaded one.</summary>
    public static bool IsInstalledCopy =>
        string.Equals(Path.GetFullPath(Environment.ProcessPath ?? ""), InstalledExe, StringComparison.OrdinalIgnoreCase);
    private static string InstalledScr => Path.Combine(InstallDir, "ClassBanner.scr");

    public static int Install(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;

        var settings = PrepareSettings(cmd, output, out var failure);
        if (settings is null) return failure;

        // Stopping the service ends the banners it started; banners started any other way (1.1.0 and earlier used the
        // Run key) are stopped too. The service starts the new version in every signed-in session afterwards. A banner
        // that can't be stopped keeps running from its renamed exe and is replaced when the service starts.
        ServiceControl.Stop(BannerService.Name);
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(AppName, throwOnMissingValue: false);
        StopRunningBanners(AppName, InstallDir);

        // Better Banner (the name up to 1.2): stop its service and banners; ClassBanner's service replaces them.
        var fromBetterBanner = Directory.Exists(OldInstallDir) || Directory.Exists(LegacyDirectory) || ServiceControl.Exists(OldName);
        if (fromBetterBanner)
        {
            ServiceControl.Stop(OldName);
            ServiceControl.Delete(OldName);
            StopRunningBanners(OldName, null);
            MigrateOldState();
        }

        Directory.CreateDirectory(InstallDir);
        DeleteLeftoverBinaries();
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine the program path.");
        if (!string.Equals(Path.GetFullPath(self), InstalledExe, StringComparison.OrdinalIgnoreCase))
            ReplaceFile(self, InstalledExe);
        ReplaceFile(self, InstalledScr);

        SaveSettings(settings, output);

        RegisterEventSource();

        using (var uninstall = Registry.LocalMachine.CreateSubKey(UninstallKey))
        {
            uninstall.SetValue("DisplayName", "ClassBanner");
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
        CreateStartMenuShortcut();
        if (ChangesLogonNotice(cmd)) ApplyLogonNotice(settings, output);
        using (var state = Registry.LocalMachine.CreateSubKey(StateKey))
            state.SetValue(InstallIdValue, Guid.NewGuid().ToString());

        // The service starts the banner in every signed-in session and restarts it if it stops (issue #17).
        ServiceControl.Install(BannerService.Name, BannerService.DisplayName, BannerService.Description, $"\"{InstalledExe}\" service");
        if (fromBetterBanner) RemoveBetterBanner(output);
        if (!cmd.NoStart) ServiceControl.Start(BannerService.Name);

        output.Line($"ClassBanner {AppInfo.Version} installed to {InstallDir}.");
        output.Line($"Marking: {settings.EffectiveCaption}. It starts automatically at every sign-in.");
        return ExitCodes.Ok;
    }

    public static int Config(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;
        var settings = PrepareSettings(cmd, output, out var failure);
        if (settings is null) return failure;
        SaveSettings(settings, output);
        if (ChangesLogonNotice(cmd)) ApplyLogonNotice(settings, output);
        // Running banners in this session reload immediately; others within 30 seconds.
        SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ClassBanner", SMTO_ABORTIFHUNG, 1000, out _);
        output.Line($"Settings saved to {SettingsLoader.DefaultSettingsPath}.");
        if (new RegistryPolicySource().ReadValues().Count > 0)
            output.Line("Note: Group Policy settings are present and override the settings file where both are set.");
        return ExitCodes.Ok;
    }

    public static int Uninstall(ParsedCommand cmd, Output output)
    {
        if (!RequireAdmin(output)) return ExitCodes.NotAdmin;

        ServiceControl.Stop(BannerService.Name);
        ServiceControl.Delete(BannerService.Name);
        StopRunningBanners(AppName, null);
        var userSettings = cmd.KeepUserSettings ? null : RestoreUserSettings();
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(AppName, throwOnMissingValue: false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(ControlPanelNamespaceKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(ClsidKey, throwOnMissingSubKey: false);
        if (File.Exists(StartMenuShortcut)) File.Delete(StartMenuShortcut);
        RemoveLogonNoticeIfOurs(output);
        Registry.LocalMachine.DeleteSubKeyTree(StateKey, throwOnMissingSubKey: false);
        if (!cmd.KeepSettings) DeleteLegacyDirectory(output);

        if (Directory.Exists(InstallDir))
        {
            var runningFromInstallDir = (Environment.ProcessPath ?? "").StartsWith(InstallDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (runningFromInstallDir)
            {
                // Can't delete our own exe while running. Remove everything else now; leave the rest to a short-lived
                // cmd started outside the caller's job (a deployment agent or SSH session closing its job would kill
                // it, issue #26), and to Windows at the next restart if even that is stopped.
                foreach (var path in Directory.EnumerateFiles(InstallDir))
                {
                    if (cmd.KeepSettings && string.Equals(path, SettingsLoader.DefaultSettingsPath, StringComparison.OrdinalIgnoreCase)) continue;
                    try { File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { DeleteAtRestart(path); }
                }
                if (!cmd.KeepSettings) DeleteAtRestart(InstallDir);
                var delete = cmd.KeepSettings
                    ? $"del /f /q \"{InstalledExe}\" \"{InstalledScr}\" \"{Path.Combine(InstallDir, "*.old-*")}\""
                    : $"rmdir /s /q \"{InstallDir}\"";
                StartDetached($"\"{SystemTool("cmd.exe")}\" /c ping 127.0.0.1 -n 3 > nul & {delete}");
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

        output.Line("ClassBanner uninstalled.");
        if (userSettings is null)
        {
            output.Line("Colors, wallpaper and screen saver applied to user profiles were left as they are (--keep-user-settings).");
        }
        else
        {
            var failed = userSettings.Failed.Count;
            output.Line($"User colors, wallpaper and screen saver: restored in {userSettings.Restored} profile(s), reset in {userSettings.Reset} " +
                        "profile(s) changed by an earlier version" +
                        (failed > 0 ? $", NOT restored in {failed} profile(s) (listed below; they keep the classification colors)." : ".") +
                        " Users signed in now see all of it at their next sign-in.");
            foreach (var failure in userSettings.Failed) output.Line($"Warning: could not restore {failure}");
        }
        return ExitCodes.Ok;
    }

    /// <summary>Puts back each profile's own colors, wallpaper and screen saver (issue #19). Never fails the uninstall.</summary>
    private static UserProfiles.Summary RestoreUserSettings()
    {
        try
        {
            var settings = RegistryPolicySource.LoadEffective().Settings;
            var custom = RgbColor.TryParse(settings.BackColor, out var back) ? new[] { back } : [];
            var summary = UserProfiles.RestoreAll(custom);
            // This session's title bars follow straight away; other sessions at their next sign-in.
            SendMessageTimeoutW(HWND_BROADCAST, WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SMTO_ABORTIFHUNG, 1000, out _);
            return summary;
        }
        catch (Exception ex)
        {
            Log.Error("Could not restore user settings", ex);
            return new UserProfiles.Summary(0, 0, [ex.Message]);
        }
    }

    public static int Show(Output output)
    {
        var loaded = RegistryPolicySource.LoadEffective();
        output.Line($"ClassBanner {AppInfo.Version}");
        output.Line($"Installed: {(File.Exists(InstalledExe) ? InstalledExe : "no")}");
        output.Line($"Settings file: {SettingsLoader.DefaultSettingsPath}{(File.Exists(SettingsLoader.DefaultSettingsPath) ? "" : " (not present)")}");
        output.Line($"Group Policy: {(loaded.FromPolicy ? "applied" : "not configured")}");
        foreach (var warning in loaded.Warnings) output.Line($"Warning: {warning}");
        output.Line("");
        output.Line(SettingsSerializer.Serialize(loaded.Settings));
        return ExitCodes.Ok;
    }

    /// <summary>Prints an install command that sets up another computer like this one, and copies it to the clipboard.</summary>
    public static int Export(Output output)
    {
        var loaded = RegistryPolicySource.LoadEffective();
        if (ExportCommand.Build(loaded.Settings) is not { } exported)
        {
            output.Error("No classification level is configured on this computer, so there is nothing to export.");
            return ExitCodes.Error;
        }

        var copied = Clipboard.SetText(exported.Command);
        if (!output.IsConsole) output.Line(copied ? "Copied to the clipboard:" : "Could not copy to the clipboard; copy it from here (Ctrl+C):");
        output.Line("");
        output.Line(exported.Command);
        if (loaded.FromPolicy) exported = exported with { Notes = [.. exported.Notes, "Includes values set by Group Policy on this computer."] };
        if (exported.Notes.Count > 0) output.Line("");
        foreach (var note in exported.Notes) output.Line("Note: " + note);
        if (output.IsConsole && copied) output.Line("(Also copied to the clipboard.)");
        return ExitCodes.Ok;
    }

    /// <summary>Start menu entry for all users that opens the settings window (created with the Windows Script Host).</summary>
    private static void CreateStartMenuShortcut()
    {
        static string Quote(string s) => "'" + s.Replace("'", "''") + "'";
        var script =
            $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut({Quote(StartMenuShortcut)}); " +
            $"$s.TargetPath = {Quote(InstalledExe)}; $s.Arguments = 'settings'; " +
            $"$s.IconLocation = {Quote(InstalledExe + ",0")}; $s.Description = 'ClassBanner settings'; $s.Save()";
        try
        {
            using var process = Process.Start(new ProcessStartInfo(SystemTool(@"WindowsPowerShell\v1.0\powershell.exe"), $"-NoProfile -NonInteractive -Command \"{script}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            })!;
            if (!process.WaitForExit(30_000) || process.ExitCode != 0) Log.Warn("Could not create the Start menu shortcut.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not create the Start menu shortcut: {ex.Message}");
        }
    }

    /// <summary>Adds "ClassBanner" to Control Panel (Appearance and Personalization; System and Security).</summary>
    private static void RegisterControlPanelItem()
    {
        using (var clsid = Registry.LocalMachine.CreateSubKey(ClsidKey))
        {
            clsid.SetValue("", "ClassBanner");
            clsid.SetValue("LocalizedString", "ClassBanner");
            clsid.SetValue("InfoTip", "Classification banner, sign-in notice and related settings for this computer.");
            clsid.SetValue("System.ApplicationName", "AustinCase.ClassBanner");
            clsid.SetValue("System.ControlPanel.Category", "1,5");
            using (var icon = clsid.CreateSubKey("DefaultIcon"))
                icon.SetValue("", $"\"{InstalledExe}\",0");
            using (var command = clsid.CreateSubKey(@"Shell\Open\Command"))
                command.SetValue("", $"\"{InstalledExe}\" settings", RegistryValueKind.ExpandString);
        }
        using (var ns = Registry.LocalMachine.CreateSubKey(ControlPanelNamespaceKey))
            ns.SetValue("", "ClassBanner");
    }

    /// <summary>Refuses to touch a settings file that exists but can't be read, rather than replacing it with defaults.</summary>
    private static bool CheckSettingsFile(SettingsFile file, Output output)
    {
        if (!file.Unreadable) return true;
        foreach (var warning in file.Warnings) output.Error(warning);
        output.Error($"Nothing was changed. Fix or delete {SettingsLoader.DefaultSettingsPath}, then run the command again.");
        return false;
    }

    /// <summary>beta.1 kept settings here; any user could create this folder, so it is only trusted if admin-owned.</summary>
    private static string LegacyDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), OldName);

    /// <summary>
    /// The settings file (or beta.1's, migrated) with the command's changes applied. Returns null, with the exit code,
    /// when the file can't be read or no classification level would be set: nothing ever saves a level nobody chose.
    /// </summary>
    private static BannerSettings? PrepareSettings(ParsedCommand cmd, Output output, out int failure)
    {
        failure = ExitCodes.Ok;
        BannerSettings settings;
        if (cmd.Reset)
        {
            // --reset: the command describes the whole configuration, so what was here before doesn't matter.
            settings = new BannerSettings();
        }
        else
        {
            var file = SettingsLoader.LoadFile(SettingsLoader.DefaultSettingsPath);
            if (!file.Exists && TryLoadBetterBannerSettings(output) is { } old) file = old;
            if (!file.Exists && TryLoadLegacySettings(output) is { } legacy) file = legacy;
            if (!CheckSettingsFile(file, output))
            {
                failure = ExitCodes.Error;
                return null;
            }

            foreach (var warning in file.Warnings) output.Line($"Warning: {warning} (removed when saving)");
            settings = file.Settings;
        }
        SettingKeys.ApplyAll(settings, cmd.Settings);
        var policy = new RegistryPolicySource().ReadValues();
        var policyLevel = policy.TryGetValue(nameof(BannerSettings.Level), out var text) && ClassificationPresets.TryParseLevel(text, out var parsed)
            ? parsed : (ClassificationLevel?)null;
        if (settings.Level is null && policyLevel is null)
        {
            output.Error("No classification level is configured for this computer. Add --level, e.g. --level SECRET (U, CUI, C, S, TS or TS/SCI). Nothing was changed.");
            failure = ExitCodes.BadArguments;
            return null;
        }

        // Never save banner text that contradicts the level (issue #18). Group Policy's level wins, so check against it.
        var level = policyLevel ?? settings.Level!.Value;
        if (!policy.ContainsKey(nameof(BannerSettings.Caption)) && ClassificationPresets.CaptionProblem(level, settings.Caption) is { } problem)
        {
            output.Error($"{problem} Nothing was changed.");
            failure = ExitCodes.BadArguments;
            return null;
        }
        if (CaptionDependsOnPolicy(settings, policyLevel) is { } note) output.Line($"Warning: {note}");
        return settings;
    }

    /// <summary>
    /// Banner text that fits Group Policy's level but not this computer's own level is shown only while the policy
    /// applies (issue #26). Returns a note saying so, or null.
    /// </summary>
    public static string? CaptionDependsOnPolicy()
    {
        var policy = new RegistryPolicySource().ReadValues();
        var policyLevel = policy.TryGetValue(nameof(BannerSettings.Level), out var text) && ClassificationPresets.TryParseLevel(text, out var parsed)
            ? parsed : (ClassificationLevel?)null;
        return CaptionDependsOnPolicy(SettingsLoader.LoadFile(SettingsLoader.DefaultSettingsPath).Settings, policyLevel);
    }

    private static string? CaptionDependsOnPolicy(BannerSettings local, ClassificationLevel? policyLevel)
    {
        if (policyLevel is null || local.Level is not { } own || own == policyLevel) return null;
        if (ClassificationPresets.CaptionProblem(own, local.Caption) is null) return null;
        return $"The banner text \"{local.Caption?.Trim()}\" fits Group Policy's level {ClassificationPresets.Get(policyLevel.Value).Caption} " +
               $"but not this computer's own level {ClassificationPresets.Get(own).Caption}. If the policy is removed, the banner " +
               $"shows the standard {ClassificationPresets.Get(own).Caption} marking.";
    }

    private static void SaveSettings(BannerSettings settings, Output output)
    {
        // Lives under Program Files, which only administrators can write; no custom permissions needed.
        SettingsLoader.Save(settings, SettingsLoader.DefaultSettingsPath);
        DeleteLegacyDirectory(output);
    }

    /// <summary>Better Banner 1.x settings, under Program Files (only administrators can write there).</summary>
    private static SettingsFile? TryLoadBetterBannerSettings(Output output)
    {
        var path = Path.Combine(OldInstallDir, "settings.json");
        if (!File.Exists(path)) return null;
        var old = SettingsLoader.LoadFile(path);
        if (old.Unreadable)
        {
            output.Line($"Warning: could not import {path}; it can't be read.");
            return null;
        }
        output.Line($"Imported settings from Better Banner ({path}).");
        return old;
    }

    /// <summary>Carries over whether Better Banner set the sign-in notice, so uninstalling ClassBanner still clears it.</summary>
    private static void MigrateOldState()
    {
        using var old = Registry.LocalMachine.OpenSubKey(OldStateKey);
        if (old?.GetValue("ManagesLogonNotice") is not int managed || managed != 1) return;
        using var state = Registry.LocalMachine.CreateSubKey(StateKey);
        state.SetValue("ManagesLogonNotice", 1, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Removes the Better Banner install ClassBanner replaces. The Control Panel item has the same ID and is simply
    /// re-registered; Better Banner's event log source stays so its entries remain readable.
    /// </summary>
    private static void RemoveBetterBanner(Output output)
    {
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true)) run?.DeleteValue(OldName, throwOnMissingValue: false);
        Registry.LocalMachine.DeleteSubKeyTree(OldUninstallKey, throwOnMissingSubKey: false);
        Registry.LocalMachine.DeleteSubKeyTree(OldStateKey, throwOnMissingSubKey: false);
        try
        {
            if (File.Exists(OldStartMenuShortcut)) File.Delete(OldStartMenuShortcut);
            if (Directory.Exists(OldInstallDir)) Directory.Delete(OldInstallDir, recursive: true);
            output.Line("Replaced Better Banner (its settings were kept).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // e.g. its screen saver is running; harmless, nothing starts it any more.
            output.Line($"Warning: could not remove {OldInstallDir}: {ex.Message}");
        }
    }

    private static SettingsFile? TryLoadLegacySettings(Output output)
    {
        var path = Path.Combine(LegacyDirectory, "settings.json");
        if (!File.Exists(path)) return null;
        if (!IsAdminOwned(new DirectoryInfo(LegacyDirectory).GetAccessControl()) || !IsAdminOwned(new FileInfo(path).GetAccessControl()))
        {
            output.Line($"Warning: not importing {path}: it isn't owned by Administrators, so it may have been changed by a standard user.");
            return null;
        }

        var legacy = SettingsLoader.LoadFile(path);
        if (legacy.Unreadable)
        {
            output.Line($"Warning: could not import {path}; it can't be read.");
            return null;
        }
        output.Line($"Imported settings from Better Banner beta.1 ({path}).");
        return legacy;
    }

    /// <summary>Removes beta.1's settings folder once its settings live in the new place (admin-owned folders only).</summary>
    private static void DeleteLegacyDirectory(Output output)
    {
        if (!Directory.Exists(LegacyDirectory)) return;
        try
        {
            if (!IsAdminOwned(new DirectoryInfo(LegacyDirectory).GetAccessControl()))
            {
                output.Line($"Warning: left {LegacyDirectory} in place because it isn't owned by Administrators; check and delete it.");
                return;
            }
            Directory.Delete(LegacyDirectory, recursive: true);
            output.Line($"Removed the old settings folder {LegacyDirectory}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            output.Line($"Warning: could not remove {LegacyDirectory}: {ex.Message}");
        }
    }

    private static bool IsAdminOwned(FileSystemSecurity security) =>
        security.GetOwner(typeof(SecurityIdentifier)) is SecurityIdentifier owner &&
        (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));

    /// <summary>
    /// The sign-in notice is machine state outside settings.json, so only touch it when the command sets or clears it
    /// explicitly; a settings file without notice fields never removes a notice.
    /// </summary>
    private static bool ChangesLogonNotice(ParsedCommand cmd) => cmd.Settings.Any(s =>
        s.Key.Equals(nameof(BannerSettings.LogonNoticeTitle), StringComparison.OrdinalIgnoreCase) ||
        s.Key.Equals(nameof(BannerSettings.LogonNoticeText), StringComparison.OrdinalIgnoreCase));

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

    /// <summary>
    /// Stops banners in every session (and open settings windows). When installing, only copies of the installed exe:
    /// the setup window that started this install runs from the download folder and must stay open.
    /// </summary>
    private static void StopRunningBanners(string processName, string? onlyUnder)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                try
                {
                    if (onlyUnder is not null && !IsUnder(process, onlyUnder)) continue;
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

    /// <summary>
    /// Full path of a Windows tool. install and uninstall run elevated, often from the download folder, and a bare
    /// "cmd.exe" is looked up in the program's own folder first, so a file planted there would run as administrator.
    /// </summary>
    private static string SystemTool(string name) => Path.Combine(Environment.SystemDirectory, name);

    /// <summary>Marks a file (or empty folder) for deletion at the next restart (MoveFileEx DELAY_UNTIL_REBOOT).</summary>
    private static void DeleteAtRestart(string path)
    {
        if (!MoveFileExW(path, null, MOVEFILE_DELAY_UNTIL_REBOOT))
            Log.Warn($"Could not schedule {path} for deletion at restart (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}).");
    }

    /// <summary>Starts a hidden process outside this process's job when the job allows it, otherwise inside it.</summary>
    private static unsafe void StartDetached(string commandLine)
    {
        foreach (var flags in new[] { CREATE_NO_WINDOW | CREATE_BREAKAWAY_FROM_JOB, CREATE_NO_WINDOW })
        {
            var buffer = (commandLine + "\0").ToCharArray();
            fixed (char* cmd = buffer)
            {
                var startup = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW) };
                if (CreateProcessW(null, cmd, 0, 0, false, flags, 0, Path.GetTempPath(), ref startup, out var info))
                {
                    CloseHandle(info.hThread);
                    CloseHandle(info.hProcess);
                    return;
                }
            }
        }
        Log.Warn($"Could not start the clean-up command (error {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}); files are removed at the next restart.");
    }

    private static bool IsUnder(Process process, string directory)
    {
        var path = process.MainModule?.FileName ?? "";
        return path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private const string EventSourceKey = @"SYSTEM\CurrentControlSet\Services\EventLog\Application\" + EventLog.Source;

    /// <summary>
    /// Registers "ClassBanner" in the Application log, using the .NET Framework's generic message file (part of
    /// Windows) so Event Viewer shows the text. Kept after uninstall so earlier entries stay readable.
    /// </summary>
    private static void RegisterEventSource()
    {
        using var key = Registry.LocalMachine.CreateSubKey(EventSourceKey);
        key.SetValue("EventMessageFile", @"%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\EventLogMessages.dll", RegistryValueKind.ExpandString);
        key.SetValue("TypesSupported", 7, RegistryValueKind.DWord);
    }
}

internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int BadArguments = 2;
    public const int NotAdmin = 5;
}
