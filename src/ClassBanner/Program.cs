using System.Text;
using ClassBanner.Core;
using static ClassBanner.Native;

// Load the DLLs we call (wtsapi32, userenv, dwmapi, ...) from System32 only, never from the folder the exe was started
// from: setup runs elevated from the download folder, where a planted DLL would otherwise be loaded.
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]

namespace ClassBanner;

internal static class Program
{
    private const string SingleInstanceMutex = @"Local\ClassBanner.Banner";

    private static int Main(string[] args)
    {
        var cmd = CommandLine.Parse(args);
        try
        {
            return cmd.Kind switch
            {
                // Double-clicking the downloaded exe opens setup; the installed copy (started at sign-in) is the banner.
                CommandKind.Run when args.Length == 0 && !Installer.IsInstalledCopy => SettingsWindow.Run(setup: true),
                CommandKind.Run => RunBanner(cmd.Managed),
                CommandKind.Service => BannerService.Run(),
                CommandKind.ScreenSaverShow => ScreenSaver.Run(),
                CommandKind.ScreenSaverPreview => ExitCodes.Ok, // no preview in the Control Panel thumbnail
                CommandKind.ScreenSaverConfigure => ShowScreenSaverInfo(),
                CommandKind.Settings => SettingsWindow.Run(),
                _ => RunCommand(cmd, args),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"'{cmd.Kind}' failed", ex);
            if (cmd.Kind == CommandKind.Run) return ExitCodes.Error;
            using var output = new Output(cmd.Quiet);
            output.Error(ex.Message);
            return ExitCodes.Error;
        }
    }

    /// <param name="managed">Started by the banner service, which restarts it; the exit code tells the service why it stopped.</param>
    private static int RunBanner(bool managed)
    {
        // One banner process per user session. A second launch just exits.
        var mutex = CreateMutexW(0, true, SingleInstanceMutex);
        // A banner started by the service owns a mutex this user can't open (its objects are locked down, issue #17),
        // which reads as "access denied" rather than "already exists": either way, one is running.
        // A banner started by the service never gives way: the mutex name is in the user's own namespace, so the user
        // could create it first and keep the marking off the screen. The service already keeps it to one per session.
        if (!managed && (mutex == 0 || System.Runtime.InteropServices.Marshal.GetLastPInvokeError() == ERROR_ALREADY_EXISTS))
        {
            CloseHandle(mutex);
            return BannerExitCodes.AlreadyRunning;
        }

        // Captured now: after an upgrade this path holds the new exe (the old one was renamed while running).
        var exePath = Environment.ProcessPath;
        Log.Info($"ClassBanner {AppInfo.Version} starting.");
        _ = new BannerHost();
        var code = Win32Window.RunMessageLoop();

        Log.Info($"ClassBanner exiting{(BannerHost.UserChoseExit ? " (the user chose Exit)" : "")}.");
        if (BannerHost.UserChoseExit) return BannerExitCodes.UserExit;
        if (BannerHost.RestartRequested)
        {
            // The service starts the new version itself; a banner started some other way restarts on its own.
            if (managed) return BannerExitCodes.Upgrade;
            CloseHandle(mutex);
            if (exePath is not null)
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(exePath, "banner") { UseShellExecute = false });
        }
        return code;
    }

    private static int RunCommand(ParsedCommand cmd, string[] args)
    {
        // Apps & features runs the uninstall command without elevation; ask for it (UAC) instead of failing.
        if (cmd.Kind is (CommandKind.Install or CommandKind.Uninstall or CommandKind.Config) && !IsAdministrator())
            return RunElevated(args, cmd.Quiet);

        using var output = new Output(cmd.Quiet);
        switch (cmd.Kind)
        {
            case CommandKind.Install: return Installer.Install(cmd, output);
            case CommandKind.Uninstall: return Installer.Uninstall(cmd, output);
            case CommandKind.Config: return Installer.Config(cmd, output);
            case CommandKind.Show: return Installer.Show(output);
            case CommandKind.Version:
                output.Line(AppInfo.Version);
                return ExitCodes.Ok;
            default:
                // A mistake gets the error and a pointer to the help, not 25 lines of usage with the error last (#19).
                if (cmd.Error is not null)
                {
                    output.Error(cmd.Error);
                    output.Line("Run 'ClassBanner.exe help' for usage.");
                    return ExitCodes.BadArguments;
                }
                output.Line(output.IsConsole ? CommandLine.Usage : CommandLine.DialogUsage);
                return ExitCodes.Ok;
        }
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    /// <summary>Re-runs this command as administrator and returns its exit code.</summary>
    private static int RunElevated(string[] args, bool quiet)
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, CommandLine.Join(args))
            {
                UseShellExecute = true,
                Verb = "runas",
            })!;
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            using var output = new Output(quiet);
            output.Error("Administrator approval was cancelled; nothing was changed.");
            return ExitCodes.NotAdmin;
        }
    }

    private static int ShowScreenSaverInfo()
    {
        MessageBoxW(0, "The ClassBanner screen saver is configured by your administrator.", "ClassBanner", MB_OK | MB_ICONINFORMATION);
        return ExitCodes.Ok;
    }
}

/// <summary>
/// Command output. The exe is a GUI program (so the banner never flashes a console), so it attaches to the console
/// it was started from; when there is none (e.g. double-clicked) the output is shown in a message box instead.
/// Tip: in a script use "start /wait ClassBanner.exe install ..." to wait for the exit code.
/// </summary>
internal sealed class Output : IDisposable
{
    private readonly bool _quiet;
    private readonly bool _console;
    private readonly StringBuilder _buffer = new();
    private bool _hadError;

    public Output(bool quiet)
    {
        _quiet = quiet;
        _console = AttachConsole(ATTACH_PARENT_PROCESS);
        if (_console) Console.WriteLine();
    }

    public bool IsConsole => _console;

    public void Line(string text)
    {
        if (_quiet) return;
        if (_console) Console.WriteLine(text);
        else _buffer.AppendLine(text);
    }

    public void Error(string text)
    {
        _hadError = true;
        if (_console) Console.Error.WriteLine("Error: " + text);
        else _buffer.AppendLine("Error: " + text);
    }

    public void Dispose()
    {
        if (_console || _buffer.Length == 0) return;
        if (_quiet && !_hadError) return;
        MessageBoxW(0, _buffer.ToString().TrimEnd(), "ClassBanner", MB_OK | (_hadError ? MB_ICONERROR : MB_ICONINFORMATION) | MB_SETFOREGROUND);
    }
}
